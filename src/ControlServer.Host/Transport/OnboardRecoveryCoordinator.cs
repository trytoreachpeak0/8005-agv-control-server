using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ControlServer.Host.Transport;

public sealed class OnboardRecoveryCoordinator(
    ControlServerDbContext dbContext,
    WireToGateStore store,
    OnboardJourneyPublisher publisher,
    SlotConfigurationActivationDispatcher activationDispatcher,
    TimeProvider timeProvider,
    IConfiguration configuration,
    ILogger<OnboardRecoveryCoordinator>? logger = null,
    PlanRevisionRoutingSource? planRevisionRouting = null)
{
    private static readonly Action<ILogger, string, string, string, string?, Exception?> LogCancellationFoundStopDecided =
        LoggerMessage.Define<string, string, string, string?>(
            LogLevel.Warning,
            new EventId(2120, nameof(LogCancellationFoundStopDecided)),
            "Load cancellation {CancellationId} for demand {DemandId} reported ALL_EMPTY after its stop had " +
            "already moved on (stage {Stage}, reason {BlockReasonCode}); the result is recorded and the stop is " +
            "left as it was decided.");
    private static readonly Action<ILogger, string, string, string, string, string?, string?, Exception?>
        LogSessionClosedNotReconciled =
            LoggerMessage.Define<string, string, string, string, string?, string?>(
                LogLevel.Warning,
                new EventId(2121, nameof(LogSessionClosedNotReconciled)),
                "Exception recovery session {SessionId} closed under {CloseReasonCode}: {WorkflowType} " +
                "{WorkflowId} reported {Outcome}, which does not reconcile. Demand {DemandId} stays blocked; a new " +
                "session is needed to recover it.");
    private static readonly Action<ILogger, string, string, string, string?, string?, Exception?>
        LogLateResultForClosedSession =
            LoggerMessage.Define<string, string, string, string?, string?>(
                LogLevel.Warning,
                new EventId(2122, nameof(LogLateResultForClosedSession)),
                "Exception recovery session {SessionId} had already closed when {WorkflowType} {WorkflowId} reported " +
                "{Outcome}. The result is recorded as evidence only: demand {DemandId}, its journey, lease and " +
                "vehicle are left as the session handling them now has them; reconcile by hand if they disagree.");

    /// <summary>
    /// Why a session closed on a result that did not reconcile (control-server#169). Not a wire code: the
    /// snapshot has no field for it and its blocking facts are empty once the session is CLOSED, so it is
    /// written to the log, and the store keeps what it is derived from -- the closed session's workflow in
    /// <see cref="RecoveryWorkflowState.RecoveryRequired"/> with the result's outcome, and the journey
    /// blocked under <c>&lt;messageType&gt;_NOT_RECONCILED</c>.
    /// </summary>
    internal const string SessionClosedResultNotReconciled = "RECOVERY_ACTION_RESULT_NOT_RECONCILED";

    /// <summary>
    /// The outcome a resume is judged on when the vehicle refused its command (control-server#187). A resume's
    /// outcome is otherwise empty -- its account is the replacement OperationResult -- so this marks, in the store,
    /// that its session closed because the command was refused rather than because a result did not reconcile. The
    /// refusal itself, the vehicle's reason code included, is the inbound message the workflow's ResultMessageId names.
    /// </summary>
    internal const string ResumeCommandRejectedOutcome = "COMMAND_REJECTED";

    /// <summary>Why a fault's cargo binding ended when its cargo was handed off in an exception recovery session.</summary>
    public const string HandedOffInExceptionSessionReason = "HANDED_OFF_IN_EXCEPTION_SESSION";

    private static readonly string[] RecoveryRequestTypes =
    [
        "ExceptionRecoverySessionRequested",
        "RecoveryActionSubmitted",
        "HardwareRecoveryRecordSubmitted",
        "LoadCancellationStartRequested",
        "LoadCompensationRequested",
        "LoadCorrectionRequested"
    ];
    private static readonly string[] RecoveryResultTypes =
    [
        "FaultCargoRecoveryResult",
        "ForcedMechanicalRecoveryResult",
        "LoadCancellationResult",
        "LoadCompensationResult",
        "LoadCorrectionResult"
    ];

    public static bool IsRecoveryRequest(string messageType) => RecoveryRequestTypes.Contains(messageType);

    public static bool IsRecoveryResult(string messageType) => RecoveryResultTypes.Contains(messageType);

    public async Task<string> ProcessRequestAsync(
        JsonElement root,
        string contentHash,
        CancellationToken cancellationToken)
    {
        string messageType = RequiredString(root, "messageType");
        return messageType switch
        {
            "ExceptionRecoverySessionRequested" => await OpenSessionAsync(root, contentHash, cancellationToken)
                .ConfigureAwait(false),
            "RecoveryActionSubmitted" => await SubmitActionAsync(root, contentHash, cancellationToken)
                .ConfigureAwait(false),
            "HardwareRecoveryRecordSubmitted" => await RecordHardwareRecoveryAsync(
                root, contentHash, cancellationToken).ConfigureAwait(false),
            "LoadCancellationStartRequested" => await AuthorizeLoadCancellationAsync(
                root, contentHash, cancellationToken).ConfigureAwait(false),
            "LoadCompensationRequested" => await AuthorizeLoadCompensationAsync(
                root, cancellationToken).ConfigureAwait(false),
            "LoadCorrectionRequested" => await AuthorizeLoadCorrectionAsync(
                root, contentHash, cancellationToken).ConfigureAwait(false),
            _ => throw new InvalidDataException($"Recovery request '{messageType}' is not supported.")
        };
    }

    public async Task<string> ProcessResultAsync(
        JsonElement root,
        string contentHash,
        CancellationToken cancellationToken)
    {
        string messageType = RequiredString(root, "messageType");
        string messageId = RequiredString(root, "messageId");
        string agvId = RequiredString(root, "agvId");
        long sessionGeneration = root.GetProperty("sessionGeneration").GetInt64();
        JsonElement payload = root.GetProperty("payload");
        string workflowId = messageType switch
        {
            "LoadCancellationResult" => RequiredString(payload, "cancellationId"),
            "LoadCorrectionResult" => RequiredString(payload, "correctionId"),
            _ => RequiredString(payload, "recoveryActionId")
        };
        RecoveryWorkflowRow workflow = await dbContext.RecoveryWorkflows.SingleAsync(
            row => row.WorkflowId == workflowId && row.AgvId == agvId,
            cancellationToken).ConfigureAwait(false);
        ValidateResultIdentity(messageType, payload, workflow);

        // DEFENSIVE RESIDUE, not a live path. Since 8005-agv-control-server#77 every inbound line reaches
        // this method through OnboardMessageProcessor, and the inbox there answers a second arrival of the
        // same messageId before this method is called at all: byte-identical replays return the first
        // response, resends that differ only in sessionGeneration are answered by RebindDurableAckAsync,
        // and anything else is already a content conflict. So no production caller can get here with a
        // row on file. It stays because this method is public and its contract -- one durable result per
        // messageId -- must hold for any caller, and because a future entry point that skips the inbox
        // would otherwise write a second evidence row in silence.
        //
        // What it must NOT do is judge equivalence a second time. That is decided once, by the inbox,
        // which ignores the sessionGeneration a resend rebinds and nothing else (#30). This compared the
        // whole line's hash, which a resend changes by definition, so whichever path reached it second
        // turned an accepted resend into a dropped connection. Identity is what is left: the same
        // messageId must still name the same workflow and the same kind of record.
        RecoveryResultEvidenceRow? existing = await dbContext.RecoveryResultEvidence
            .SingleOrDefaultAsync(row => row.MessageId == messageId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            if (existing.WorkflowId != workflowId || existing.MessageType != messageType)
            {
                throw new ProtocolContentConflictException(
                    "Recovery result MessageId was replayed with a different identity.");
            }
            return DurableAck(messageType, messageId, agvId, sessionGeneration, contentHash);
        }
        if (workflow.ResultMessageId is not null && workflow.ResultMessageId != messageId)
        {
            throw new ProtocolContentConflictException(
                "Recovery workflow already has a different first durable result.");
        }

        long currentGeneration = await CurrentForcedGenerationAsync(agvId, cancellationToken).ConfigureAwait(false);
        long resultGeneration = messageType == "ForcedMechanicalRecoveryResult"
            ? payload.GetProperty("forcedRecoveryGeneration").GetInt64()
            : workflow.ForcedRecoveryGeneration;
        bool historicalOnly = resultGeneration < currentGeneration;
        string outcome = ResultOutcome(messageType, payload);
        DateTimeOffset observedAt = payload.GetProperty("observedAt").GetDateTimeOffset();
        dbContext.RecoveryResultEvidence.Add(new RecoveryResultEvidenceRow
        {
            MessageId = messageId,
            WorkflowId = workflowId,
            MessageType = messageType,
            ForcedRecoveryGeneration = resultGeneration,
            ContentHash = contentHash,
            Outcome = outcome,
            HistoricalOnly = historicalOnly,
            ObservedAt = observedAt,
            ReceivedAt = timeProvider.GetUtcNow()
        });
        workflow.ResultMessageId = messageId;
        workflow.ResultContentHash = contentHash;
        workflow.Outcome = outcome;
        workflow.UpdatedAt = timeProvider.GetUtcNow();
        if (historicalOnly)
        {
            workflow.State = RecoveryWorkflowState.HistoricalOnly;
        }
        else
        {
            await ApplyCurrentResultAsync(messageType, payload, workflow, cancellationToken)
                .ConfigureAwait(false);
            await AdvanceSessionAfterResultAsync(
                workflow,
                sessionGeneration,
                cancellationToken).ConfigureAwait(false);
        }
        await SettleAnsweredCommandAsync(workflow, cancellationToken).ConfigureAwait(false);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return DurableAck(messageType, messageId, agvId, sessionGeneration, contentHash);
    }

    public async Task ObserveOperationResultAsync(
        string slotOperationAttemptId,
        OperationResultDisposition disposition,
        CancellationToken cancellationToken)
    {
        // Only the resume still waiting for its result. One already judged RecoveryRequired closed its session
        // (control-server#169), and the next session may resume the same attempt again: judging the new result
        // against both would fail, and judging it against the old one would rewrite why that session closed.
        RecoveryWorkflowRow? workflow = await dbContext.RecoveryWorkflows.SingleOrDefaultAsync(
            row => row.WorkflowType == "RESUME_AFTER_REPAIR" &&
                   row.SlotOperationAttemptId == slotOperationAttemptId &&
                   (row.State == RecoveryWorkflowState.CommandPending ||
                    row.State == RecoveryWorkflowState.AwaitingResult),
            cancellationToken).ConfigureAwait(false);
        if (workflow is null || disposition is OperationResultDisposition.Replay or OperationResultDisposition.HistoricalOnly)
            return;

        // A determinate failure closes the recovery too: what the administrator was asked for is a trustworthy
        // account of the slots, and one that says nobody handed the cargo over is exactly that
        // (ADR-cross-0058 decision 2). The runtime then ends the demand from AwaitingLoadResult.
        bool reconciled = disposition is OperationResultDisposition.Accepted
            or OperationResultDisposition.DeterminateFailure;
        workflow.State = reconciled
            ? RecoveryWorkflowState.Reconciled
            : RecoveryWorkflowState.RecoveryRequired;
        workflow.UpdatedAt = timeProvider.GetUtcNow();
        JourneyRuntimeRow? runtime = workflow.DemandId is null
            ? null
            : await DemandJourneyLookup.JourneyOf(dbContext, workflow.DemandId)
                .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (runtime is not null && reconciled)
        {
            StationOperationRow operation = await dbContext.StationOperations.SingleAsync(
                row => row.SlotOperationAttemptId == slotOperationAttemptId,
                cancellationToken).ConfigureAwait(false);
            runtime.Stage = operation.OperationType == SlotOperationType.Load
                ? JourneyRuntimeStage.AwaitingLoadResult
                : JourneyRuntimeStage.AwaitingUnloadResult;
            runtime.SetBlockReason(null, timeProvider.GetUtcNow());
            runtime.UpdatedAt = timeProvider.GetUtcNow();
        }
        if (workflow.ExceptionRecoverySessionId is not null)
        {
            long sessionGeneration = await dbContext.SessionRecoveries.Where(row => row.AgvId == workflow.AgvId)
                .Select(row => row.SessionGeneration).SingleAsync(cancellationToken).ConfigureAwait(false);
            await AdvanceSessionAfterResultAsync(workflow, sessionGeneration, cancellationToken).ConfigureAwait(false);
        }
        await SettleAnsweredCommandAsync(workflow, cancellationToken).ConfigureAwait(false);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The vehicle refused a command with <c>SlotOperationCommandRejected</c>. When the refused command is a resume
    /// still waiting for its replacement result, no result will come: the resume is judged RecoveryRequired and its
    /// session closes, the way a result that does not reconcile closes it (control-server#169, #187). The demand, the
    /// journey, the lease and the vehicle are not touched -- they stay where the failed load left them, for the next
    /// session. Any other refusal -- of the original SlotOperationCommand, or matching no waiting resume -- is only
    /// acknowledged, as before.
    /// </summary>
    /// <remarks>
    /// The refused command is known by the envelope's correlationId, which the protocol requires to be the refused
    /// command's messageId (REQUIRED_ORIGINAL_MESSAGE_ID) and the onboard fills so: the attempt alone cannot tell,
    /// because a refusal of the original SlotOperationCommand names the same attempt. The attempt must agree as well.
    /// A resend is answered by the inbox before this is called; a second refusal of the same command under a new
    /// messageId finds the resume already judged and changes nothing.
    /// </remarks>
    public async Task ObserveCommandRejectedAsync(JsonElement root, CancellationToken cancellationToken)
    {
        if (!root.TryGetProperty("correlationId", out JsonElement correlation) ||
            correlation.ValueKind != JsonValueKind.String)
            return;
        string refusedCommandId = correlation.GetString()!;
        string agvId = RequiredString(root, "agvId");
        string attemptId = RequiredString(root.GetProperty("payload"), "slotOperationAttemptId");
        RecoveryWorkflowRow? workflow = await dbContext.RecoveryWorkflows.SingleOrDefaultAsync(
            row => row.WorkflowType == "RESUME_AFTER_REPAIR" &&
                   row.AgvId == agvId &&
                   row.CommandMessageId == refusedCommandId &&
                   row.SlotOperationAttemptId == attemptId &&
                   (row.State == RecoveryWorkflowState.CommandPending ||
                    row.State == RecoveryWorkflowState.AwaitingResult),
            cancellationToken).ConfigureAwait(false);
        if (workflow is null) return;

        workflow.State = RecoveryWorkflowState.RecoveryRequired;
        workflow.Outcome = ResumeCommandRejectedOutcome;
        workflow.ResultMessageId = RequiredString(root, "messageId");
        workflow.UpdatedAt = timeProvider.GetUtcNow();
        await AdvanceSessionAfterResultAsync(
            workflow, root.GetProperty("sessionGeneration").GetInt64(), cancellationToken).ConfigureAwait(false);
        await SettleAnsweredCommandAsync(workflow, cancellationToken).ConfigureAwait(false);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SendTriggeredCommandAsync(JsonElement root, CancellationToken cancellationToken)
    {
        string messageType = RequiredString(root, "messageType");
        JsonElement payload = root.GetProperty("payload");
        string? workflowId = messageType switch
        {
            "RecoveryActionSubmitted" => RequiredString(payload, "recoveryActionId"),
            "LoadCompensationRequested" => RequiredString(payload, "recoveryActionId"),
            "LoadCorrectionRequested" => RequiredString(payload, "correctionId"),
            _ => null
        };
        if (workflowId is not null)
        {
            RecoveryWorkflowRow? workflow = await dbContext.RecoveryWorkflows.AsNoTracking()
                .SingleOrDefaultAsync(row => row.WorkflowId == workflowId, cancellationToken).ConfigureAwait(false);
            if (workflow?.CommandMessageId is not null)
                await publisher.SendPersistedAsync(workflow.CommandMessageId, cancellationToken).ConfigureAwait(false);
        }
        await SendPendingSessionSnapshotsAsync(
            RequiredString(root, "agvId"), cancellationToken).ConfigureAwait(false);
        // 这条结果若结束了旅程里最后一条需求，收尾快照已随它那次保存落库，在答复之后发（control-server#323）。
        await JourneyClosure.SendAsync(publisher, dbContext, RequiredString(root, "agvId"), cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task ReplayPendingCommandsAsync(
        string agvId,
        long sessionGeneration,
        CancellationToken cancellationToken)
    {
        string[] commandIds = await dbContext.RecoveryWorkflows.AsNoTracking()
            .Where(row => row.AgvId == agvId &&
                          row.CommandMessageId != null &&
                          row.State != RecoveryWorkflowState.Reconciled &&
                          row.State != RecoveryWorkflowState.HistoricalOnly)
            .Select(row => row.CommandMessageId!)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        string[] snapshotIds = await PendingSessionSnapshotIdsAsync(agvId, cancellationToken).ConfigureAwait(false);
        // 恢复角色 SLOT_CONFIGURATION：还没拿到结果的那几次激活，命令要跟着这一轮补发。判据是激活
        // 本身还在待补报态，不是发件箱那一行没被 ack——车 ack 了命令然后在报结果之前掉线，正是必须
        // 补发的那种情况。
        IReadOnlyList<string> activationIds = await activationDispatcher
            .PendingCommandMessageIdsAsync(agvId, cancellationToken).ConfigureAwait(false);
        // 车把旅程快照写进本地日志库、重连前恢复，所以收尾那一刻没送到的收尾快照要在这里补（control-server#323）；
        // 这辆车一旦有了下一趟旅程，它们就不在这里面了。
        IReadOnlyList<string> closureIds = await JourneyClosure
            .ReplayIdsAsync(dbContext, agvId, cancellationToken).ConfigureAwait(false);
        string[] pendingIds = [.. commandIds, .. snapshotIds, .. activationIds, .. closureIds];
        if (pendingIds.Length > 0)
            await publisher.ReplayPendingForSessionAsync(
                agvId, sessionGeneration, pendingIds.ToHashSet(StringComparer.Ordinal), cancellationToken)
                .ConfigureAwait(false);
    }

    private async Task<string> OpenSessionAsync(
        JsonElement root,
        string contentHash,
        CancellationToken cancellationToken)
    {
        JsonElement payload = root.GetProperty("payload");
        string messageId = RequiredString(root, "messageId");
        string agvId = RequiredString(root, "agvId");
        long generation = root.GetProperty("sessionGeneration").GetInt64();
        string requestId = RequiredUuid(payload, "requestId");
        string eventId = RequiredUuid(payload, "eventId");
        string? demandId = OptionalUuid(payload, "demandId");
        int[] slots = RequiredSlots(payload, "slots");
        JsonElement administrator = payload.GetProperty("administrator");
        string administratorId = RequiredString(administrator, "operatorId");
        string administratorRole = RequiredString(payload, "administratorRole");
        string reason = RequiredString(payload, "reason");
        string businessHash = PayloadHash(payload);
        if (administratorRole is not ("MAINTENANCE_ADMINISTRATOR" or "SYSTEM_ADMINISTRATOR") ||
            !RecoveryProofAccepted(RequiredString(payload, "authenticationProof")))
        {
            return Response(root, "ExceptionRecoverySessionRejected", new
            {
                requestId,
                problem = Problem(ServerReasonCodes.RecoveryAuthenticationFailed, "payload.authenticationProof",
                    "The recovery administrator proof was not accepted.")
            });
        }

        ExceptionRecoverySessionRow? replay = await dbContext.ExceptionRecoverySessions
            .SingleOrDefaultAsync(row => row.RequestId == requestId, cancellationToken).ConfigureAwait(false);
        if (replay is not null)
        {
            if (replay.RequestContentHash != businessHash || replay.AgvId != agvId)
                throw new ProtocolContentConflictException("Recovery requestId was replayed with different content.");
            StationOperationRow? replayOperation = await SessionOperationAsync(replay, cancellationToken)
                .ConfigureAwait(false);
            return OpenedResponse(root, replay, replayOperation?.SlotOperationAttemptId);
        }
        ExceptionRecoverySessionRow? active = await dbContext.ExceptionRecoverySessions
            .SingleOrDefaultAsync(row => row.AgvId == agvId && row.State != "CLOSED", cancellationToken)
            .ConfigureAwait(false);
        if (active is not null)
        {
            return Response(root, "ExceptionRecoverySessionRejected", new
            {
                requestId,
                problem = Problem(ServerReasonCodes.ActionNotAllowedInState, "payload.requestId",
                    "An exception recovery session is already open for this vehicle.")
            });
        }
        SessionRecoveryRow session = await dbContext.SessionRecoveries.SingleAsync(
            row => row.AgvId == agvId && row.SessionGeneration == generation,
            cancellationToken).ConfigureAwait(false);
        string? validation = await ValidateSessionScopeAsync(agvId, demandId, slots, cancellationToken)
            .ConfigureAwait(false);
        if (validation is not null)
        {
            return Response(root, "ExceptionRecoverySessionRejected", new
            {
                requestId,
                problem = Problem(validation, "payload", "Recovery scope does not match persisted journey facts.")
            });
        }
        DateTimeOffset now = timeProvider.GetUtcNow();
        ExceptionRecoverySessionRow row = new()
        {
            ExceptionRecoverySessionId = StableGuid(requestId, "exception-recovery-session"),
            RequestId = requestId,
            RequestContentHash = businessHash,
            AgvId = agvId,
            EventId = eventId,
            DemandId = demandId,
            SlotsJson = JsonSerializer.Serialize(slots),
            AdministratorId = administratorId,
            AdministratorRole = administratorRole,
            Reason = reason,
            State = "OPEN",
            Revision = 1,
            ForcedRecoveryGeneration = session.ForcedRecoveryGeneration,
            OpenedAt = now,
            UpdatedAt = now
        };
        dbContext.ExceptionRecoverySessions.Add(row);
        await QueueSessionSnapshotAsync(row, generation, cancellationToken).ConfigureAwait(false);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        _ = contentHash;
        _ = messageId;
        StationOperationRow? operation = await SessionOperationAsync(row, cancellationToken).ConfigureAwait(false);
        return OpenedResponse(root, row, operation?.SlotOperationAttemptId);
    }

    private async Task<string> SubmitActionAsync(
        JsonElement root,
        string contentHash,
        CancellationToken cancellationToken)
    {
        JsonElement payload = root.GetProperty("payload");
        string actionId = RequiredUuid(payload, "recoveryActionId");
        string recoverySessionId = RequiredUuid(payload, "exceptionRecoverySessionId");
        string action = RequiredString(payload, "action");
        ExceptionRecoverySessionRow session = await dbContext.ExceptionRecoverySessions.SingleAsync(
            row => row.ExceptionRecoverySessionId == recoverySessionId,
            cancellationToken).ConfigureAwait(false);
        string? rejection = ValidateActionScope(payload, session);
        if (rejection is not null)
            return RejectedAction(root, actionId, recoverySessionId, session.Revision, rejection);

        string businessHash = PayloadHash(payload);
        RecoveryWorkflowRow? replay = await dbContext.RecoveryWorkflows
            .SingleOrDefaultAsync(row => row.WorkflowId == actionId, cancellationToken).ConfigureAwait(false);
        if (replay is not null)
        {
            if (replay.RequestContentHash != businessHash || replay.WorkflowType != action)
                throw new ProtocolContentConflictException("RecoveryActionId was replayed with different content.");
            // What the workflow recorded, not a fresh lookup (8005-agv-program#95).
            return AcceptedAction(root, session, actionId, action, replay.SlotOperationAttemptId);
        }

        // The workflow's attempt is what RecoveryActionAccepted names and what compensation is later
        // authorized against, and the session already named one in ExceptionRecoverySessionOpened and
        // every snapshot. Those must be one value (8005-agv-program#95), so the action is judged on the
        // same operation those messages name, from the one lookup that names it.
        StationOperationRow? operation = await SessionOperationAsync(session, cancellationToken).ConfigureAwait(false);
        // A session is a verdict on the demand as it stood when the session opened. Should the demand
        // have gained a slot operation since, that verdict is stale, and an action is refused whole
        // rather than accepted against a load the world has moved past -- FORCED_MECHANICAL_RECOVERY
        // and FAULT_CARGO_HANDOFF look at no operation state of their own to catch it. Defensive: slot
        // operations are created only by the runtime's load and unload stages, a session opens only on a
        // Blocked journey, and the runtime does nothing for a Blocked one (8005-agv-control-server#78).
        if (session.DemandId is not null &&
            (await FindLatestOperationAsync(session.DemandId, cancellationToken).ConfigureAwait(false))
                ?.SlotOperationAttemptId != operation?.SlotOperationAttemptId)
            return RejectedAction(
                root, actionId, recoverySessionId, session.Revision, ServerReasonCodes.RecoveryScopeMismatch);
        SessionRecoveryRow connection = await dbContext.SessionRecoveries.SingleAsync(
            row => row.AgvId == session.AgvId, cancellationToken).ConfigureAwait(false);
        string? actionProblem = ValidateActionPreconditions(action, session, connection, operation);
        if (actionProblem is not null)
            return RejectedAction(root, actionId, recoverySessionId, session.Revision, actionProblem);
        // The one check against taking an action again while the same action still awaits its outcome, for all four
        // actions. Their preconditions alone cannot tell: the first submission leaves them true until its outcome
        // arrives. A resend of an accepted action never reaches this point: the replay above answers it.
        //
        // A resume is judged by attempt, in this session or any other (control-server#180): one resume
        // authorization admits one replacement result, and the result finds its resume by attempt alone
        // (WireToGateStore.RequireResumeAuthorizationAsync, ObserveOperationResultAsync), so a second one waiting
        // would leave that result two to settle and it would never be acknowledged.
        //
        // The other three are judged within the session (control-server#187, decided by the user on 2026-09-19):
        // each would send the vehicle a second command for the same physical action, and it would carry it out
        // twice. A second forced recovery would also advance the forced generation, which fences only a first
        // command not yet delivered and makes its result historical while the vehicle may be executing it. A
        // compensation awaiting authorization has sent nothing yet and is not counted: refusing past it would
        // leave a session whose compensation is never authorized with no action left to take.
        bool byAttempt = action == "RESUME_AFTER_REPAIR";
        string? attemptId = operation?.SlotOperationAttemptId;
        if (await dbContext.RecoveryWorkflows.AnyAsync(
                row => row.WorkflowType == action &&
                       (row.State == RecoveryWorkflowState.CommandPending ||
                        row.State == RecoveryWorkflowState.AwaitingResult) &&
                       ((byAttempt && row.SlotOperationAttemptId == attemptId) ||
                        (!byAttempt && row.ExceptionRecoverySessionId == recoverySessionId)),
                cancellationToken).ConfigureAwait(false))
            return RejectedAction(
                root, actionId, recoverySessionId, session.Revision, ServerReasonCodes.ActionNotAllowedInState);

        DateTimeOffset now = timeProvider.GetUtcNow();
        long forcedGeneration = connection.ForcedRecoveryGeneration;
        if (action == "FORCED_MECHANICAL_RECOVERY")
        {
            forcedGeneration = checked(forcedGeneration + 1);
            await store.AdvanceForcedRecoveryGenerationAsync(
                session.AgvId, forcedGeneration, now, cancellationToken).ConfigureAwait(false);
            session.ForcedRecoveryGeneration = forcedGeneration;
        }
        RecoveryWorkflowRow workflow = new()
        {
            WorkflowId = actionId,
            WorkflowType = action,
            ExceptionRecoverySessionId = recoverySessionId,
            AgvId = session.AgvId,
            DemandId = session.DemandId,
            SlotOperationAttemptId = operation?.SlotOperationAttemptId,
            SlotsJson = session.SlotsJson,
            ForcedRecoveryGeneration = forcedGeneration,
            State = action == "COMPENSATE_LOAD_ALL_EMPTY"
                ? RecoveryWorkflowState.AwaitingAuthorization
                : RecoveryWorkflowState.CommandPending,
            RequestMessageId = RequiredString(root, "messageId"),
            RequestContentHash = businessHash,
            CreatedAt = now,
            UpdatedAt = now
        };
        dbContext.RecoveryWorkflows.Add(workflow);
        session.SelectedAction = action;
        session.State = action == "COMPENSATE_LOAD_ALL_EMPTY" ? "ACTION_SELECTED" : "EXECUTING";
        session.Revision++;
        session.UpdatedAt = now;
        if (action != "COMPENSATE_LOAD_ALL_EMPTY")
            await QueueActionCommandAsync(root, session, workflow, operation, cancellationToken).ConfigureAwait(false);
        await QueueSessionSnapshotAsync(
            session, root.GetProperty("sessionGeneration").GetInt64(), cancellationToken).ConfigureAwait(false);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        _ = contentHash;
        return AcceptedAction(root, session, actionId, action, workflow.SlotOperationAttemptId);
    }

    private async Task<string> RecordHardwareRecoveryAsync(
        JsonElement root,
        string contentHash,
        CancellationToken cancellationToken)
    {
        JsonElement payload = root.GetProperty("payload");
        string recordId = RequiredUuid(payload, "recordId");
        string sessionId = RequiredUuid(payload, "exceptionRecoverySessionId");
        string actionId = RequiredUuid(payload, "recoveryActionId");
        ExceptionRecoverySessionRow? session = await dbContext.ExceptionRecoverySessions.SingleOrDefaultAsync(
            row => row.ExceptionRecoverySessionId == sessionId, cancellationToken).ConfigureAwait(false);
        RecoveryWorkflowRow? workflow = await dbContext.RecoveryWorkflows.SingleOrDefaultAsync(
            row => row.WorkflowId == actionId && row.ExceptionRecoverySessionId == sessionId,
            cancellationToken).ConfigureAwait(false);
        string role = RequiredString(payload, "administratorRole");
        int[] slots = RequiredSlots(payload, "slots");
        object? problem = null;
        string outcome = "RECORDED";
        // A record lifts the forced recovery's hardware hold (WireToGateStore.DecideReadinessAsync), so for a
        // forced workflow it must attest to the hardware after the forcing: taken while the command is still
        // pending it proves nothing, yet the result arriving later would let it lift the hold unexamined. Any
        // result on file will do, FAILED included -- the doors were forced either way. It must also be this
        // vehicle's workflow: a record sent over one vehicle's connection says nothing about another's slots.
        if (session is null || workflow is null ||
            workflow.AgvId != RequiredString(root, "agvId") ||
            (workflow.WorkflowType == "FORCED_MECHANICAL_RECOVERY" && workflow.ResultMessageId is null) ||
            role is not ("MAINTENANCE_ADMINISTRATOR" or "SYSTEM_ADMINISTRATOR") ||
            !slots.SequenceEqual(ParseSlots(session?.SlotsJson ?? "[]")))
        {
            outcome = "REJECTED";
            problem = Problem(ServerReasonCodes.RecoveryScopeMismatch, "payload",
            "Hardware recovery record scope is not current.");
        }
        else
        {
            HardwareRecoveryRecordRow? replay = await dbContext.HardwareRecoveryRecords.SingleOrDefaultAsync(
                row => row.RecordId == recordId, cancellationToken).ConfigureAwait(false);
            if (replay is not null && replay.ContentHash != contentHash)
                throw new ProtocolContentConflictException("Hardware recovery record was replayed with different content.");
            if (replay is null)
            {
                dbContext.HardwareRecoveryRecords.Add(new HardwareRecoveryRecordRow
                {
                    RecordId = recordId,
                    ExceptionRecoverySessionId = sessionId,
                    RecoveryActionId = actionId,
                    ContentHash = contentHash,
                    OperatorId = RequiredString(payload.GetProperty("operator"), "operatorId"),
                    AdministratorRole = role,
                    SlotsJson = JsonSerializer.Serialize(slots),
                    ChecksJson = payload.GetProperty("checksPerformed").GetRawText(),
                    ActionsJson = payload.GetProperty("actionsPerformed").GetRawText(),
                    ObservationsJson = payload.GetProperty("observations").GetRawText(),
                    ObservedAt = payload.GetProperty("observedAt").GetDateTimeOffset(),
                    RecordedAt = timeProvider.GetUtcNow()
                });
                session!.Revision++;
                session.UpdatedAt = timeProvider.GetUtcNow();
                await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        return Response(root, "HardwareRecoveryRecordResult", new
        {
            recordId,
            outcome,
            problem,
            recoverySessionRevision = session?.Revision ?? 0
        });
    }

    private async Task<string> AuthorizeLoadCancellationAsync(
        JsonElement root,
        string contentHash,
        CancellationToken cancellationToken)
    {
        JsonElement payload = root.GetProperty("payload");
        string cancellationId = RequiredUuid(payload, "cancellationId");
        string demandId = RequiredUuid(payload, "demandId");
        string? attemptId = OptionalUuid(payload, "slotOperationAttemptId");
        if (attemptId is null)
        {
            return await AuthorizeLoadCancellationBeforeSublotAsync(
                root, contentHash, cancellationId, demandId, cancellationToken).ConfigureAwait(false);
        }
        AcceptedDemandRow? demand = await dbContext.AcceptedDemands.SingleOrDefaultAsync(
            row => row.DemandId == demandId, cancellationToken).ConfigureAwait(false);
        bool sameVehicle = await DemandIsOnVehicleAsync(demandId, RequiredString(root, "agvId"), cancellationToken)
            .ConfigureAwait(false);
        StationOperationRow? operation = attemptId is null ? null : await dbContext.StationOperations
            .SingleOrDefaultAsync(row => row.SlotOperationAttemptId == attemptId, cancellationToken)
            .ConfigureAwait(false);
        bool authorized = demand is not null && demand.Status == DemandExecutionStatus.Accepted && sameVehicle &&
                          (operation is null || operation.DemandId == demandId &&
                           operation.OperationType == SlotOperationType.Load &&
                           operation.Status != StationOperationStatus.RecoveryRequired);
        int[] slots = operation is null ? [] : ParseSlots(operation.TargetSlotsJson);
        if (authorized)
        {
            await UpsertSimpleWorkflowAsync(
                cancellationId, "LOAD_CANCELLATION", root, contentHash, demandId, attemptId, slots,
                cancellationToken).ConfigureAwait(false);
        }
        return Response(root, "LoadCancellationAuthorization", new
        {
            cancellationId,
            decision = authorized ? "AUTHORIZED" : "REJECTED",
            demandId,
            slotOperationAttemptId = attemptId,
            slots,
            problem = authorized ? null : Problem(
                ServerReasonCodes.ActionNotAllowedInState, "payload.demandId",
                "Load cancellation is not safe in the current state.")
        });
    }

    /// <summary>
    /// ADR-cross-0046, first case: the operator cancels at the pickup before any sublot is entered, so no
    /// slot operation was commanded and there is no slot to prove empty. Authorized with an empty slot set,
    /// and the demand is ended only by the vehicle's ALL_EMPTY result that follows -- never by the
    /// authorization itself, which is how MVP did it (ADR-cross-0057 Consequences).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only while the journey waits in <see cref="JourneyRuntimeStage.AwaitingSublot"/>, before any load
    /// command, with no entry for the stop durable and no cancellation already open. Once an entry is
    /// durable the runtime acts on it, and the cancellation, persisted second, loses
    /// (<see cref="LoadCancellationBeforeSublot"/>). Until control-server#83 this path authorized at any
    /// stage and then waited for a result that protocol 1.0.0 could not carry, so the stop stayed held.
    /// </para>
    /// <para>
    /// A request naming a cancellation already on file, from the vehicle that raised it, is answered from
    /// the record rather than judged afresh: judged afresh, the open cancellation it created -- or the stop
    /// it has since ended -- would refuse it. <see cref="UpsertSimpleWorkflowAsync"/> compares the payload
    /// hash, so a resend has to repeat the first request's content exactly, as the onboard does.
    /// </para>
    /// <para>
    /// <b>Decided and recorded under one write lock.</b> This runs inside the inbox's write transaction,
    /// which on this store is BEGIN IMMEDIATE: every fact read here is current, and the station deadline,
    /// which re-reads the stop under its own write transaction before ending it, cannot end the same stop in
    /// between. Whichever commits first stands.
    /// </para>
    /// </remarks>
    private async Task<string> AuthorizeLoadCancellationBeforeSublotAsync(
        JsonElement root,
        string contentHash,
        string cancellationId,
        string demandId,
        CancellationToken cancellationToken)
    {
        string agvId = RequiredString(root, "agvId");
        RecoveryWorkflowRow? recorded = await dbContext.RecoveryWorkflows.AsNoTracking()
            .SingleOrDefaultAsync(row => row.WorkflowId == cancellationId, cancellationToken).ConfigureAwait(false);
        bool authorized = recorded is not null
            ? recorded.AgvId == agvId
            : await CancellationBeforeSublotAllowedAsync(demandId, agvId, cancellationToken).ConfigureAwait(false);
        if (authorized)
        {
            await UpsertSimpleWorkflowAsync(
                cancellationId, LoadCancellationBeforeSublot.WorkflowType, root, contentHash, demandId,
                attemptId: null, slots: [], cancellationToken).ConfigureAwait(false);
        }
        return Response(root, "LoadCancellationAuthorization", new
        {
            cancellationId,
            decision = authorized ? "AUTHORIZED" : "REJECTED",
            demandId,
            slotOperationAttemptId = (string?)null,
            slots = Array.Empty<int>(),
            problem = authorized ? null : Problem(
                ServerReasonCodes.ActionNotAllowedInState, "payload.slotOperationAttemptId",
                "Load cancellation before a sublot entry is not allowed in the current state.")
        });
    }

    /// <summary>
    /// 操作员指名的那一条需求，此刻能不能在扫码之前取消（票面第 9 条，批次7-06，control-server#211）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>判的是那一条需求，不是整趟旅程。</b>一站几条需求逐条串行，第一条正在装的时候，第二条仍然是「扫码之前」的，
    /// 操作员在清单上选中它就该能取消它。所以「旅程停在等录入」这个旅程级的条件换成了三条针对这条需求的：它挂在车
    /// 此刻所在的那个停靠上、那是它的取货停靠、它自己还没被录入也没发过装货命令。
    /// </para>
    /// <para>
    /// <b>单需求旅程的应答逐字不变。</b>那时停靠上只有这一条需求：旅程停在等录入 ⇔ 它的归属行是待装；旅程走到等装货
    /// 结果 ⇔ 它已经是 <c>LOADING</c>，第三条挡下，与原先 <c>Stage != AwaitingSublot</c> 挡下的是同一批请求。
    /// 车还没到站（<c>AwaitingPickupArrival</c>）也仍然拒绝——那时车不在这个停靠上，「扫码之前」无从谈起。
    /// </para>
    /// </remarks>
    private async Task<bool> CancellationBeforeSublotAllowedAsync(
        string demandId,
        string agvId,
        CancellationToken cancellationToken)
    {
        AcceptedDemandRow? demand = await dbContext.AcceptedDemands.AsNoTracking()
            .SingleOrDefaultAsync(row => row.DemandId == demandId, cancellationToken).ConfigureAwait(false);
        JourneyRuntimeRow? runtime = await DemandJourneyLookup.JourneyOf(dbContext, demandId).AsNoTracking()
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (demand?.Status != DemandExecutionStatus.Accepted ||
            runtime is null ||
            runtime.AgvId != agvId ||
            // 车已经到了这个停靠、还在装：只有这两个阶段有「扫码之前」可言。
            runtime.Stage is not (JourneyRuntimeStage.AwaitingSublot or JourneyRuntimeStage.AwaitingLoadResult))
        {
            return false;
        }

        JourneyStopCursor stops = await JourneyStopCursor.LoadAsync(dbContext, runtime, cancellationToken)
            .ConfigureAwait(false);
        if (stops.Current.StopRole != JourneyStopRoles.Pickup ||
            stops.OutstandingAtCurrentStop.SingleOrDefault(
                item => item.Demand.DemandId == demandId) is not { } member ||
            member.Membership.Status != JourneyDemandStatuses.PendingLoad)
        {
            return false;
        }
        if (await LoadCommandedAsync(member, cancellationToken).ConfigureAwait(false) ||
            await LoadCancellationBeforeSublot.HasOpenCancellationAsync(dbContext, demandId, cancellationToken)
                .ConfigureAwait(false))
        {
            return false;
        }
        // Narrowed in the store the way the runtime's own read is, and for the same reason: this runs on
        // every cancellation request and the inbox keeps every submission ever made. The operation
        // session is written into the submission's own JSON, so the substring is a filter the database
        // can apply; which entries are the stop's is still decided by the parse below.
        string operationSessionId = stops.Current.OperationSessionId;
        StopEntryAddress address = stops.EntryAddressOfCurrentStop(runtime.WorklistRevision);
        ProtocolInboxRow[] entries = await dbContext.ProtocolInbox.AsNoTracking()
            .Where(row => row.MessageType == "SublotSubmitted" && row.RequestJson.Contains(operationSessionId))
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        List<ProtocolInboxRow> forThisStop = [];
        foreach (ProtocolInboxRow entry in entries)
        {
            using JsonDocument document = JsonDocument.Parse(entry.RequestJson);
            if (LoadCancellationBeforeSublot.IsEntryForStop(document.RootElement, address, demand.Sublot))
            {
                forThisStop.Add(entry);
            }
        }

        // An entry the server already refused is not an entry for this stop (control-server#82): the
        // operator has been shown why their scan did not stand, and cancelling the stop before any sublot
        // is what they are most likely to want next. Read from the store, so a refusal that is not
        // durable yet has not been decided and this stays refused -- the conservative side of the race.
        HashSet<string> refused = await LoadCancellationBeforeSublot
            .RefusedSubmissionIdsAsync(dbContext, [.. forThisStop.Select(entry => entry.MessageId)], cancellationToken)
            .ConfigureAwait(false);
        foreach (ProtocolInboxRow entry in forThisStop.Where(entry => !refused.Contains(entry.MessageId)))
        {
            // An entry the runtime refuses for the station's task types starts no load, so it does not hold
            // the stop against the operator either. That carve-out is kept as it was: it names a station
            // that cannot do the work at all, and it is not a SublotRejected.
            // Judged at the AREA machine station the way the runtime judges the entry (control-server#163): for
            // STAGING_TO_WIRE that is the drop-off, not the staging station the entry was made at.
            return !await store.IsTaskTypeAllowedAtAreaEndAsync(runtime, demand.WorkType, cancellationToken)
                .ConfigureAwait(false);
        }
        return true;
    }

    /// <summary>
    /// Whether this demand's load was commanded: its SlotOperationCommand is queued, or a slot operation
    /// exists for it. The command's id is assigned when the journey is created, so the id alone says nothing.
    /// </summary>
    /// <remarks>
    /// Asked about the demand named in the request rather than the journey's anchor (control-server#211): a stop
    /// carrying several demands has a command per demand, and the anchor's says nothing about the one the operator
    /// is cancelling. With one demand per journey the two are the same row.
    /// </remarks>
    private async Task<bool> LoadCommandedAsync(JourneyStopDemand member, CancellationToken cancellationToken)
    {
        string commandMessageId = member.Membership.LoadCommandMessageId;
        string demandId = member.Demand.DemandId;
        return await dbContext.ProtocolOutbox.AsNoTracking()
                   .AnyAsync(row => row.MessageId == commandMessageId, cancellationToken).ConfigureAwait(false) ||
               await dbContext.StationOperations.AsNoTracking()
                   .AnyAsync(row => row.DemandId == demandId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Whether the demand's journey runs on the vehicle a request came from. A demand with no journey is
    /// on no vehicle.
    /// </summary>
    private Task<bool> DemandIsOnVehicleAsync(string demandId, string agvId, CancellationToken cancellationToken) =>
        DemandJourneyLookup.JourneyOf(dbContext, demandId).AsNoTracking()
            .AnyAsync(row => row.AgvId == agvId, cancellationToken);

    private async Task<string> AuthorizeLoadCompensationAsync(
        JsonElement root,
        CancellationToken cancellationToken)
    {
        JsonElement payload = root.GetProperty("payload");
        string actionId = RequiredUuid(payload, "recoveryActionId");
        RecoveryWorkflowRow? workflow = await dbContext.RecoveryWorkflows.SingleOrDefaultAsync(
            row => row.WorkflowId == actionId && row.WorkflowType == "COMPENSATE_LOAD_ALL_EMPTY",
            cancellationToken).ConfigureAwait(false);
        if (workflow is null || workflow.ExceptionRecoverySessionId != RequiredUuid(payload, "exceptionRecoverySessionId") ||
            workflow.DemandId != RequiredUuid(payload, "demandId") ||
            workflow.SlotOperationAttemptId != RequiredUuid(payload, "slotOperationAttemptId") ||
            workflow.State is not (RecoveryWorkflowState.AwaitingAuthorization or RecoveryWorkflowState.CommandPending))
        {
            return Response(root, "LoadCompensationRejected", new
            {
                recoveryActionId = actionId,
                problem = Problem(ServerReasonCodes.ActionNotAllowedInState, "payload",
                    "Load compensation is not authorized.")
            });
        }
        if (workflow.CommandMessageId is null)
        {
            ExceptionRecoverySessionRow session = await dbContext.ExceptionRecoverySessions.SingleAsync(
                row => row.ExceptionRecoverySessionId == workflow.ExceptionRecoverySessionId,
                cancellationToken).ConfigureAwait(false);
            // The authorization is a second message and can arrive after the session closed on another result of
            // the same action (control-server#169). Authorizing then would set a CLOSED session back to EXECUTING
            // and send the command, and the compensation's result would find an open session and settle the
            // demand the next session is now handling (control-server#175). A CLOSED session is never reopened.
            if (session.State == "CLOSED")
            {
                return Response(root, "LoadCompensationRejected", new
                {
                    recoveryActionId = actionId,
                    problem = Problem(ServerReasonCodes.RecoverySessionNotOpen, "payload",
                        "The recovery session this compensation belongs to has already closed.")
                });
            }
            // Only one compensation of a session goes to the vehicle at a time (control-server#187). A second one may
            // be submitted while the first still awaits its authorization -- refusing that at submission could leave a
            // session whose one compensation is never authorized with no action left -- so the line is drawn here,
            // where the command is sent: while another compensation of the session has its command out and awaits the
            // outcome, this one is not authorized, and the vehicle does not compensate twice.
            if (await dbContext.RecoveryWorkflows.AnyAsync(
                    row => row.WorkflowType == "COMPENSATE_LOAD_ALL_EMPTY" &&
                           row.WorkflowId != actionId &&
                           row.ExceptionRecoverySessionId == workflow.ExceptionRecoverySessionId &&
                           (row.State == RecoveryWorkflowState.CommandPending ||
                            row.State == RecoveryWorkflowState.AwaitingResult),
                    cancellationToken).ConfigureAwait(false))
            {
                return Response(root, "LoadCompensationRejected", new
                {
                    recoveryActionId = actionId,
                    problem = Problem(ServerReasonCodes.ActionNotAllowedInState, "payload",
                        "Another compensation of this recovery session is already awaiting its outcome.")
                });
            }
            string commandId = StableGuid(actionId, "load-compensation-command");
            string hash = RecoveryCommandHash.Compute(
                actionId, workflow.DemandId!, workflow.SlotOperationAttemptId!, workflow.SlotsJson);
            await publisher.QueueLoadCompensationCommandAsync(
                commandId,
                workflow.AgvId,
                root.GetProperty("sessionGeneration").GetInt64(),
                new LoadCompensationAuthorizationCommand(
                    actionId,
                    workflow.ExceptionRecoverySessionId!,
                    workflow.DemandId!,
                    workflow.SlotOperationAttemptId!,
                    ParseSlots(workflow.SlotsJson),
                    hash),
                cancellationToken).ConfigureAwait(false);
            BindCommand(workflow, commandId, "LoadCompensationCommand", hash);
            session.State = "EXECUTING";
            session.Revision++;
            session.UpdatedAt = timeProvider.GetUtcNow();
            await QueueSessionSnapshotAsync(
                session, root.GetProperty("sessionGeneration").GetInt64(), cancellationToken).ConfigureAwait(false);
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        return string.Empty;
    }

    private async Task<string> AuthorizeLoadCorrectionAsync(
        JsonElement root,
        string contentHash,
        CancellationToken cancellationToken)
    {
        JsonElement payload = root.GetProperty("payload");
        string correctionId = RequiredUuid(payload, "correctionId");
        string demandId = RequiredUuid(payload, "demandId");
        string attemptId = RequiredUuid(payload, "slotOperationAttemptId");
        int[] slots = RequiredSlots(payload, "slots");
        StationOperationRow? operation = await dbContext.StationOperations.SingleOrDefaultAsync(
            row => row.SlotOperationAttemptId == attemptId && row.DemandId == demandId,
            cancellationToken).ConfigureAwait(false);
        JourneyRuntimeRow? journey = await DemandJourneyLookup.JourneyOf(dbContext, demandId)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        // REQ-0237: an ordinary mis-placement is corrected only before the vehicle leaves the pickup.
        // A committed load alone is not enough -- until 2026-09-13 this authorized corrections for a
        // vehicle already sent to the gate, whose onboard could only refuse to open the doors.
        if (operation is null || operation.OperationType != SlotOperationType.Load ||
            operation.Status != StationOperationStatus.Committed ||
            !slots.All(ParseSlots(operation.TargetSlotsJson).Contains) ||
            journey?.Stage != JourneyRuntimeStage.AwaitingStationDeparture)
        {
            return Response(root, "LoadCorrectionRejected", new
            {
                correctionId,
                problem = Problem(ServerReasonCodes.ActionNotAllowedInState, "payload",
                    "Load correction is not authorized.")
            });
        }
        RecoveryWorkflowRow workflow = await UpsertSimpleWorkflowAsync(
            correctionId, "LOAD_CORRECTION", root, contentHash, demandId, attemptId, slots,
            cancellationToken).ConfigureAwait(false);
        if (workflow.CommandMessageId is null)
        {
            string commandId = StableGuid(correctionId, "load-correction-command");
            string hash = RecoveryCommandHash.Compute(
                correctionId, demandId, attemptId, JsonSerializer.Serialize(slots));
            await publisher.QueueLoadCorrectionCommandAsync(
                commandId,
                workflow.AgvId,
                root.GetProperty("sessionGeneration").GetInt64(),
                new LoadCorrectionAuthorizationCommand(correctionId, demandId, attemptId, slots, hash),
                cancellationToken).ConfigureAwait(false);
            BindCommand(workflow, commandId, "LoadCorrectionCommand", hash);
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        return string.Empty;
    }

    private async Task QueueActionCommandAsync(
        JsonElement root,
        ExceptionRecoverySessionRow session,
        RecoveryWorkflowRow workflow,
        StationOperationRow? operation,
        CancellationToken cancellationToken)
    {
        string commandId = StableGuid(workflow.WorkflowId, "recovery-command");
        int[] slots = ParseSlots(workflow.SlotsJson);
        long sessionGeneration = root.GetProperty("sessionGeneration").GetInt64();
        string hash = RecoveryCommandHash.ForRecoveryAction(
            workflow.WorkflowId,
            workflow.DemandId ?? string.Empty,
            operation?.SlotOperationAttemptId ?? string.Empty,
            workflow.SlotsJson,
            workflow.ForcedRecoveryGeneration);
        switch (workflow.WorkflowType)
        {
            case "RESUME_AFTER_REPAIR":
                if (operation is null) throw new InvalidDataException("Resume requires a persisted slot operation.");
                SessionRecoveryRow recovery = await dbContext.SessionRecoveries.SingleAsync(
                    row => row.AgvId == session.AgvId, cancellationToken).ConfigureAwait(false);
                // A resume carries the content hash of the very SlotOperationCommand it resumes, not a
                // hash of the recovery action: the vehicle resumes only the command it journaled and
                // refuses a resume naming any other. The action-scoped hash the other recovery commands
                // use was sent here until 2026-09-14, and the real onboard refused every resume with
                // RECOVERY_STATE_MISMATCH (G3 FP-IS-07 resume-004). The replacement result is checked
                // against the authorized scope directly (WireToGateStore.RequireResumeAuthorizationAsync).
                await publisher.QueueSlotOperationResumeCommandAsync(
                    commandId, session.AgvId, sessionGeneration,
                    new SlotOperationResumeAuthorization(
                        session.ExceptionRecoverySessionId,
                        workflow.WorkflowId,
                        workflow.DemandId!,
                        operation.SlotOperationAttemptId,
                        recovery.ProvenRecoveryCheckpoint!,
                        slots,
                        operation.ContentHash), cancellationToken).ConfigureAwait(false);
                BindCommand(workflow, commandId, "SlotOperationResumeCommand", operation.ContentHash);
                break;
            case "FAULT_CARGO_HANDOFF":
                if (operation is null) throw new InvalidDataException("Cargo handoff requires a persisted slot operation.");
                string handoffId = StableGuid(workflow.WorkflowId, "fault-cargo-handoff");
                await publisher.QueueFaultCargoRecoveryCommandAsync(
                    commandId, session.AgvId, sessionGeneration,
                    new FaultCargoRecoveryAuthorizationCommand(
                        session.ExceptionRecoverySessionId,
                        workflow.WorkflowId,
                        workflow.DemandId!,
                        slots,
                        handoffId,
                        hash), cancellationToken).ConfigureAwait(false);
                workflow.HandoffId = handoffId;
                BindCommand(workflow, commandId, "FaultCargoRecoveryCommand", hash);
                break;
            case "FORCED_MECHANICAL_RECOVERY":
                await publisher.QueueForcedMechanicalRecoveryCommandAsync(
                    commandId, session.AgvId, sessionGeneration,
                    new ForcedMechanicalRecoveryAuthorizationCommand(
                        session.ExceptionRecoverySessionId,
                        workflow.WorkflowId,
                        workflow.DemandId,
                        workflow.ForcedRecoveryGeneration,
                        slots,
                        hash), cancellationToken).ConfigureAwait(false);
                BindCommand(workflow, commandId, "ForcedMechanicalRecoveryCommand", hash);
                break;
            default:
                throw new InvalidDataException($"Recovery action '{workflow.WorkflowType}' has no command mapping.");
        }
    }

    /// <summary>
    /// Where a session goes once its action's result has been judged -- the one place that decides it, for the
    /// recovery results and for a resume's replacement OperationResult alike.
    /// </summary>
    /// <remarks>
    /// The session closes either way (control-server#169, decided by the user on 2026-09-19). A result that
    /// reconciles has ended what the session was opened for. One that does not -- FAILED, UNKNOWN, or a
    /// conclusion its slot results do not bear out -- has still arrived, so nothing is left to execute; until
    /// #169 it left the session EXECUTING, which refused a second action, refused a new session on the vehicle,
    /// and told the vehicle it was still waiting for a result. Closing it is not "handled": the business stays
    /// where the result left it (demand RecoveryRequired, journey Blocked, lease and vehicle held), and the
    /// administrator opens a new session, whose actions are worked out afresh from the vehicle's facts then.
    /// A result judged HistoricalOnly never reaches here (<see cref="ProcessResultAsync"/>): the only thing
    /// that makes a result historical is a later forced generation, which only a forced action submitted in
    /// the same open session creates, and that later action's own result closes the session.
    /// </remarks>
    private async Task AdvanceSessionAfterResultAsync(
        RecoveryWorkflowRow workflow,
        long sessionGeneration,
        CancellationToken cancellationToken)
    {
        if (workflow.ExceptionRecoverySessionId is null) return;
        ExceptionRecoverySessionRow session = await dbContext.ExceptionRecoverySessions.SingleAsync(
            row => row.ExceptionRecoverySessionId == workflow.ExceptionRecoverySessionId,
            cancellationToken).ConfigureAwait(false);
        // The first result to arrive closed it, and that closing stands. A later one -- the same action submitted
        // again under another recoveryActionId while the session was still executing -- is recorded against its
        // own workflow and changes nothing here: no new revision, no second closing snapshot.
        if (session.State == "CLOSED") return;
        session.State = "CLOSED";
        session.Revision++;
        session.UpdatedAt = timeProvider.GetUtcNow();
        if (workflow.State != RecoveryWorkflowState.Reconciled)
        {
            // Written before the caller's save: should that transaction roll back, this line names a closing that
            // did not happen. The store, not the log, is the record.
            LogSessionClosedNotReconciled(
                logger ?? (ILogger)NullLogger.Instance,
                session.ExceptionRecoverySessionId, SessionClosedResultNotReconciled, workflow.WorkflowType,
                workflow.WorkflowId, workflow.Outcome, workflow.DemandId, null);
        }
        await QueueSessionSnapshotAsync(session, sessionGeneration, cancellationToken).ConfigureAwait(false);
    }

    private async Task QueueSessionSnapshotAsync(
        ExceptionRecoverySessionRow session,
        long sessionGeneration,
        CancellationToken cancellationToken)
    {
        ProtocolOutboxRow[] previous = await dbContext.ProtocolOutbox
            .Where(row => row.MessageType == "ExceptionRecoverySessionSnapshot" &&
                          row.AcknowledgedAt == null && row.FencedAt == null)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        foreach (ProtocolOutboxRow row in previous)
        {
            using JsonDocument document = JsonDocument.Parse(row.PayloadJson);
            if (RequiredString(document.RootElement.GetProperty("payload"), "exceptionRecoverySessionId") ==
                session.ExceptionRecoverySessionId)
                row.FencedAt = timeProvider.GetUtcNow();
        }
        StationOperationRow? operation = await SessionOperationAsync(session, cancellationToken).ConfigureAwait(false);
        string[] allowedActions = session.State == "OPEN"
            ? AllowedActions(session, operation)
            : [];
        VehicleBusinessBlockingFact[] blockingFacts = session.State == "CLOSED"
            ? []
            : [new VehicleBusinessBlockingFact(
                session.State == "OPEN"
                    ? ServerReasonCodes.RecoveryActionRequired
                    : ServerReasonCodes.RecoveryResultRequired,
                "EXCEPTION_RECOVERY_SESSION",
                session.ExceptionRecoverySessionId)];
        string messageId = StableGuid(
            session.ExceptionRecoverySessionId,
            $"recovery-session-snapshot-{session.Revision}");
        await publisher.QueueExceptionRecoverySessionSnapshotAsync(
            messageId,
            session.AgvId,
            sessionGeneration,
            new ExceptionRecoverySessionProjection(
                session.ExceptionRecoverySessionId,
                session.Revision,
                session.State,
                session.AdministratorId,
                session.AdministratorRole,
                session.EventId,
                session.DemandId,
                operation?.SlotOperationAttemptId,
                ParseSlots(session.SlotsJson),
                session.SelectedAction,
                allowedActions,
                blockingFacts),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task SendPendingSessionSnapshotsAsync(
        string agvId,
        CancellationToken cancellationToken)
    {
        string[] ids = await PendingSessionSnapshotIdsAsync(agvId, cancellationToken).ConfigureAwait(false);
        foreach (string id in ids)
            await publisher.SendPersistedAsync(id, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string[]> PendingSessionSnapshotIdsAsync(
        string agvId,
        CancellationToken cancellationToken)
    {
        ProtocolOutboxRow[] rows = await dbContext.ProtocolOutbox.AsNoTracking()
            .Where(row => row.MessageType == "ExceptionRecoverySessionSnapshot" &&
                          row.AcknowledgedAt == null && row.FencedAt == null)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        return rows.Where(row =>
        {
            using JsonDocument document = JsonDocument.Parse(row.PayloadJson);
            return RequiredString(document.RootElement, "agvId") == agvId;
        }).OrderBy(row => row.CreatedAt).Select(row => row.MessageId).ToArray();
    }

    private static string[] AllowedActions(
        ExceptionRecoverySessionRow session,
        StationOperationRow? operation)
    {
        if (session.DemandId is null) return ["FORCED_MECHANICAL_RECOVERY"];
        if (operation?.OperationType == SlotOperationType.Load &&
            operation.Status == StationOperationStatus.RecoveryRequired)
            return
            [
                "RESUME_AFTER_REPAIR",
                "COMPENSATE_LOAD_ALL_EMPTY",
                "FAULT_CARGO_HANDOFF",
                "FORCED_MECHANICAL_RECOVERY"
            ];
        return ["RESUME_AFTER_REPAIR", "FAULT_CARGO_HANDOFF", "FORCED_MECHANICAL_RECOVERY"];
    }

    private async Task ApplyCurrentResultAsync(
        string messageType,
        JsonElement payload,
        RecoveryWorkflowRow workflow,
        CancellationToken cancellationToken)
    {
        // A session already CLOSED was closed by an earlier result, and the demand and journey have been in the
        // hands of the next session since (control-server#169): it may be executing, or may have settled them, and
        // this late result is a record of its own attempt, not a verdict on theirs -- whichever way it concluded
        // (control-server#175, decided by the user on 2026-09-19). So it settles nothing: no demand, journey,
        // operation, lease or vehicle write, and no termination, which a demand the next session delivered would
        // refuse outright. Decided here, before either branch below writes anything, once for both directions.
        // The workflow is RecoveryRequired, never HistoricalOnly, so a forced recovery keeps holding the vehicle for
        // its hardware record; and since the session closed on its first judged result, this one reads from the
        // store as arriving after the closing without changing why the session closed. The content checks below --
        // exact slot results, proofs -- are what a settlement rests on, so a result that settles nothing skips them.
        bool sessionClosed = workflow.ExceptionRecoverySessionId is not null &&
                             await dbContext.ExceptionRecoverySessions.AnyAsync(
                                 row => row.ExceptionRecoverySessionId == workflow.ExceptionRecoverySessionId &&
                                        row.State == "CLOSED",
                                 cancellationToken).ConfigureAwait(false);
        if (sessionClosed)
        {
            workflow.State = RecoveryWorkflowState.RecoveryRequired;
            LogLateResultForClosedSession(
                logger ?? (ILogger)NullLogger.Instance,
                workflow.ExceptionRecoverySessionId!, workflow.WorkflowType, workflow.WorkflowId, workflow.Outcome,
                workflow.DemandId, null);
            return;
        }
        bool safeEmpty = messageType is "LoadCancellationResult" or "LoadCompensationResult" or "FaultCargoRecoveryResult"
            ? HasExactSafeSlotResult(payload, ParseSlots(workflow.SlotsJson), "EMPTY")
            : false;
        bool success = messageType switch
        {
            "LoadCancellationResult" or "LoadCompensationResult" =>
                RequiredString(payload, "overallOutcome") == "ALL_EMPTY" && safeEmpty,
            "FaultCargoRecoveryResult" =>
                RequiredString(payload, "overallOutcome") == "HANDED_OFF" && safeEmpty,
            "LoadCorrectionResult" =>
                RequiredString(payload, "overallOutcome") == "COMPLETED" &&
                HasExactSafeSlotResult(payload, ParseSlots(workflow.SlotsJson), expectedState: null),
            "ForcedMechanicalRecoveryResult" =>
                RequiredString(payload, "outcome") == "MECHANICALLY_ISOLATED" &&
                !payload.GetProperty("electronicEmptyProven").GetBoolean() &&
                !payload.GetProperty("vehicleReadyProven").GetBoolean(),
            _ => false
        };
        if (!success)
        {
            workflow.State = RecoveryWorkflowState.RecoveryRequired;
            await KeepDemandAndJourneyBlockedAsync(
                workflow.DemandId, messageType + "_NOT_RECONCILED", cancellationToken).ConfigureAwait(false);
            return;
        }
        // A forced mechanical recovery closes the cargo's business, and only that (REQ-0242,
        // control-server#137). The session named the demand, so the product's identity is the demand's own
        // bound cargo, and the result's verified operator is the named person who took it: that is the
        // ForcedCargoHandoffRecord, and it ends the demand exactly as a fault cargo handoff does (CONTEXT.md,
        // FaultCargoRecoveryRecord). Protocol 2.0.0 carries no field for an unknown identity, so the "pending
        // inventory" branch of REQ-0242 cannot be reached from here. What the result does not prove -- empty
        // slots, safe doors, a recovered vehicle -- stays unproven: readiness is held separately until a
        // HardwareRecoveryRecord for this workflow arrives (WireToGateStore.DecideReadinessAsync). Until #137
        // this kept the demand blocked and the session EXECUTING for good, and nothing ever settled either.
        workflow.State = RecoveryWorkflowState.Reconciled;
        if (messageType == "LoadCorrectionResult") return;
        if (workflow.DemandId is null) return;
        if (messageType == "LoadCancellationResult" && workflow.SlotOperationAttemptId is null)
        {
            // Cancelled before any sublot entry: nothing was commanded, so the stop ends the way the station
            // deadline ends it -- demand, lease, vehicle occupancy, the unanswered entry request and the
            // journey in one staged change -- and differs only in why (control-server#83). Stamped with the
            // server's receipt time, like the deadline: every fact it writes is the server's own.
            JourneyRuntimeRow stop = await DemandJourneyLookup.JourneyOf(dbContext, workflow.DemandId)
                .SingleAsync(cancellationToken).ConfigureAwait(false);
            // Checked again here, inside the inbox's write transaction, because authorization and settlement
            // are two messages and the stop may have moved on between them. A stop already ended -- by its
            // station deadline, say -- keeps the reason it ended with: the vehicle has only proved what that
            // ending already assumed. A load commanded since is a stop this result cannot end at all, so it
            // is held for recovery like any result that does not reconcile.
            if (stop.Stage == JourneyRuntimeStage.Completed)
            {
                LogCancellationFoundStopDecided(
                    logger ?? (ILogger)NullLogger.Instance,
                    workflow.WorkflowId, stop.DemandId, stop.Stage.ToString(), stop.BlockReasonCode, null);
                return;
            }
            // 这条需求在这趟旅程里的归属：命令发没发、要结算哪一版录入请求，都挂在它身上（批次7-06，control-server#211）。
            JourneyStopCursor stopCursor = await JourneyStopCursor
                .LoadAsync(dbContext, stop, cancellationToken).ConfigureAwait(false);
            JourneyStopDemand? cancelled = stopCursor.AllDemands
                .SingleOrDefault(item => item.Demand.DemandId == workflow.DemandId);
            // 阶段判的仍是「车还在取货停靠上装货」，而不再是「旅程恰好停在等录入」：一站几条需求逐条串行，第一条正在装
            // 的时候第二条的取消结果照样该被受理。单需求下两者是同一批请求。
            if (stop.Stage is not (JourneyRuntimeStage.AwaitingSublot or JourneyRuntimeStage.AwaitingLoadResult) ||
                cancelled is null ||
                await LoadCommandedAsync(cancelled, cancellationToken).ConfigureAwait(false))
            {
                workflow.State = RecoveryWorkflowState.RecoveryRequired;
                await KeepDemandAndJourneyBlockedAsync(
                    workflow.DemandId, messageType + "_NOT_RECONCILED", cancellationToken).ConfigureAwait(false);
                return;
            }
            // 给了路网的终结在删掉空停靠之后还换序（批次7-10，control-server#215，调度决策 6）；没给就只删不换。
            await new PickupStopTermination(dbContext, await ReadPlanRevisionRoutingAsync(cancellationToken).ConfigureAwait(false))
                .StageAsync(
                    stop,
                    stopCursor.CurrentSublotRequestMessageId(stop.WorklistRevision),
                    workflow.DemandId,
                    "CANCELLED_BY_OPERATOR",
                    timeProvider.GetUtcNow(),
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }
        // A commanded slot operation proven empty -- an in-flight cancellation, a compensation, a fault cargo
        // handoff -- or, for a forced mechanical recovery, its cargo handed off by hand. The settlement of the
        // operation itself is this coordinator's: PickupStopTermination knows nothing about commanded
        // operations, so the operation is cancelled here. Everything else -- demand,
        // lease, vehicle occupancy, journey -- is the same tail the uncommanded endings use, staged into the
        // same unsaved change so ProcessResultAsync commits it with the result in one save. Until
        // control-server#131 this wrote those facts by hand minus the vehicle occupancy, and the pickup
        // order held the vehicle against every later claim. Stamped with the server's receipt time like the
        // other endings, not the vehicle's observedAt: the release has to sort after the server's own claim.
        JourneyRuntimeRow runtime = await DemandJourneyLookup.JourneyOf(dbContext, workflow.DemandId)
            .SingleAsync(cancellationToken).ConfigureAwait(false);
        JourneyStopCursor commandedStops = await JourneyStopCursor
            .LoadAsync(dbContext, runtime, cancellationToken).ConfigureAwait(false);
        if (workflow.SlotOperationAttemptId is not null)
        {
            StationOperationRow? operation = await dbContext.StationOperations.SingleOrDefaultAsync(
                row => row.SlotOperationAttemptId == workflow.SlotOperationAttemptId,
                cancellationToken).ConfigureAwait(false);
            if (operation is not null) operation.Status = StationOperationStatus.Cancelled;
        }
        await new PickupStopTermination(dbContext, await ReadPlanRevisionRoutingAsync(cancellationToken).ConfigureAwait(false))
            .StageAsync(
                runtime,
                // OrNone, unlike the cancellation above: that one runs only while the vehicle is loading at a
                // pickup stop (its stage guard says so), while a handoff reaches here wherever the vehicle is
                // standing -- at the gate as well as at the pickup, as PickupStopTermination's own remarks say.
                // An unload stop has no entry request and therefore none to settle. Batch 7-06 (#211) moved
                // this read from the journey row, whose id acceptance always writes, onto the current stop's,
                // which only a pickup stop carries; without OrNone a handoff at the gate throws instead of
                // ending the demand, and the manual recovery path it belongs to wedges.
                commandedStops.CurrentSublotRequestMessageIdOrNone(runtime.WorklistRevision),
                workflow.DemandId,
                messageType switch
                {
                    "FaultCargoRecoveryResult" or "ForcedMechanicalRecoveryResult" => "TERMINATED_BY_FAULT_CARGO_HANDOFF",
                    "LoadCompensationResult" => "CANCELLED_BY_LOAD_COMPENSATION",
                    _ => "CANCELLED_BY_OPERATOR"
                },
                timeProvider.GetUtcNow(),
                cancellationToken)
            .ConfigureAwait(false);
        if (messageType is "FaultCargoRecoveryResult" or "ForcedMechanicalRecoveryResult")
        {
            await SettleHandedOffCargoAsync(runtime, workflow.DemandId, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The cargo of <paramref name="demandId"/> left the vehicle by hand: the fault's cargo binding for it has done its work, and
    /// a stopped rebuild handed to this session is over once the journey has closed (control-server#345). Staged with the
    /// settlement; the caller saves.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The binding was never released here before</b>, a defect older than #345: a FAILED order with cargo on board binds it
    /// (REQ-0238), and only a confirmed resumption or a rebuild confirmed on the same vehicle released it. Handed off in a session,
    /// it stayed live, and the fault coordinator binds once per vehicle -- a live binding is taken as the cargo of whatever fault
    /// comes next -- so the vehicle's next FAILED order was held, and cleared, as a vehicle with cargo on board.
    /// </para>
    /// <para>
    /// Only the binding of the demand handed off: a binding names one demand, and another demand's cargo is still on board until
    /// its own session hands it off.
    /// </para>
    /// </remarks>
    private async Task SettleHandedOffCargoAsync(
        JourneyRuntimeRow runtime,
        string demandId,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        foreach (FaultedVehicleCargoRow cargo in await dbContext.FaultedVehicleCargo
                     .Where(row => row.AgvId == runtime.AgvId && row.DemandId == demandId && row.ReleasedAt == null)
                     .ToArrayAsync(cancellationToken).ConfigureAwait(false))
        {
            cargo.ReleasedAt = now;
            cargo.ReleasedReason = HandedOffInExceptionSessionReason;
        }

        if (runtime.Stage != JourneyRuntimeStage.Completed)
        {
            return;
        }

        foreach (OwnOrderRebuildRow handedOver in await dbContext.OwnOrderRebuilds
                     .Where(row => row.JourneyId == runtime.JourneyId && row.State == OwnOrderRebuildStates.AwaitingCargoHandoff)
                     .ToArrayAsync(cancellationToken).ConfigureAwait(false))
        {
            handedOver.State = OwnOrderRebuildStates.Ended;
        }
    }

    private async Task KeepDemandAndJourneyBlockedAsync(
        string? demandId,
        string reason,
        CancellationToken cancellationToken)
    {
        if (demandId is null) return;
        AcceptedDemandRow? demand = await dbContext.AcceptedDemands.SingleOrDefaultAsync(
            row => row.DemandId == demandId, cancellationToken).ConfigureAwait(false);
        if (demand is not null && demand.Status != DemandExecutionStatus.Succeeded &&
            demand.Status != DemandExecutionStatus.Cancelled)
            demand.Status = DemandExecutionStatus.RecoveryRequired;
        JourneyRuntimeRow? runtime = await DemandJourneyLookup.JourneyOf(dbContext, demandId)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (runtime is not null)
        {
            runtime.Stage = JourneyRuntimeStage.Blocked;
            runtime.SetBlockReason(reason, timeProvider.GetUtcNow());
            runtime.UpdatedAt = timeProvider.GetUtcNow();
        }
    }

    private async Task<RecoveryWorkflowRow> UpsertSimpleWorkflowAsync(
        string workflowId,
        string type,
        JsonElement root,
        string contentHash,
        string demandId,
        string? attemptId,
        int[] slots,
        CancellationToken cancellationToken)
    {
        RecoveryWorkflowRow? workflow = await dbContext.RecoveryWorkflows.SingleOrDefaultAsync(
            row => row.WorkflowId == workflowId, cancellationToken).ConfigureAwait(false);
        if (workflow is not null)
        {
            if (workflow.WorkflowType != type || workflow.RequestContentHash != PayloadHash(root.GetProperty("payload")) ||
                workflow.DemandId != demandId || workflow.SlotOperationAttemptId != attemptId)
                throw new ProtocolContentConflictException("Recovery workflow id was replayed with different content.");
            return workflow;
        }
        string agvId = RequiredString(root, "agvId");
        long forcedGeneration = await CurrentForcedGenerationAsync(agvId, cancellationToken).ConfigureAwait(false);
        DateTimeOffset now = timeProvider.GetUtcNow();
        workflow = new RecoveryWorkflowRow
        {
            WorkflowId = workflowId,
            WorkflowType = type,
            AgvId = agvId,
            DemandId = demandId,
            SlotOperationAttemptId = attemptId,
            SlotsJson = JsonSerializer.Serialize(slots),
            ForcedRecoveryGeneration = forcedGeneration,
            State = type == "LOAD_CANCELLATION"
                ? RecoveryWorkflowState.AwaitingResult
                : RecoveryWorkflowState.CommandPending,
            RequestMessageId = RequiredString(root, "messageId"),
            RequestContentHash = PayloadHash(root.GetProperty("payload")),
            CreatedAt = now,
            UpdatedAt = now
        };
        dbContext.RecoveryWorkflows.Add(workflow);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        _ = contentHash;
        return workflow;
    }

    private async Task<string?> ValidateSessionScopeAsync(
        string agvId,
        string? demandId,
        int[] slots,
        CancellationToken cancellationToken)
    {
        if (demandId is null) return null;
        JourneyRuntimeRow? runtime = await DemandJourneyLookup.JourneyOf(dbContext, demandId).SingleOrDefaultAsync(
            row => row.AgvId == agvId && row.Stage == JourneyRuntimeStage.Blocked,
            cancellationToken).ConfigureAwait(false);
        if (runtime is null) return ServerReasonCodes.RecoveryDemandNotBlocked;
        StationOperationRow? operation = await FindLatestOperationAsync(demandId, cancellationToken).ConfigureAwait(false);
        return operation is null || !slots.SequenceEqual(ParseSlots(operation.TargetSlotsJson))
            ? ServerReasonCodes.RecoveryScopeMismatch
            : null;
    }

    private static string? ValidateActionScope(JsonElement payload, ExceptionRecoverySessionRow session)
    {
        int[] slots = RequiredSlots(payload, "slots");
        string? demandId = OptionalUuid(payload, "demandId");
        string operatorId = RequiredString(payload.GetProperty("operator"), "operatorId");
        return session.State == "CLOSED" ? ServerReasonCodes.RecoverySessionNotOpen :
            RequiredUuid(payload, "eventId") != session.EventId ? ServerReasonCodes.RecoveryEventMismatch :
            demandId != session.DemandId ? ServerReasonCodes.RecoveryDemandMismatch :
            !slots.SequenceEqual(ParseSlots(session.SlotsJson)) ? ServerReasonCodes.RecoveryScopeMismatch :
            operatorId != session.AdministratorId ? ServerReasonCodes.RecoveryOperatorMismatch :
            session.SelectedAction is not null && session.SelectedAction != RequiredString(payload, "action")
                ? ServerReasonCodes.RecoveryActionAlreadySelected : null;
    }

    private static string? ValidateActionPreconditions(
        string action,
        ExceptionRecoverySessionRow session,
        SessionRecoveryRow connection,
        StationOperationRow? operation)
    {
        if (connection.ReportedForcedRecoveryGeneration != connection.ForcedRecoveryGeneration)
            return ServerReasonCodes.ForcedRecoveryGenerationStale;
        if (session.DemandId is not null && operation is null)
            return ServerReasonCodes.RecoveryOperationNotFound;
        return action switch
        {
            "RESUME_AFTER_REPAIR" when operation?.Status != StationOperationStatus.RecoveryRequired =>
                ServerReasonCodes.ActionNotAllowedInState,
            "RESUME_AFTER_REPAIR" when connection.UnsettledSlotOperationAttemptId != operation.SlotOperationAttemptId ||
                                       connection.ProvenRecoveryCheckpoint is not
                                           ("PREPARED" or "ACTIVE_UNLOCK_SET" or "SAFE_FINISH_REACHED") =>
                ServerReasonCodes.ProvenRecoveryCheckpointRequired,
            "COMPENSATE_LOAD_ALL_EMPTY" when operation?.OperationType != SlotOperationType.Load ||
                                              operation.Status != StationOperationStatus.RecoveryRequired =>
                ServerReasonCodes.ActionNotAllowedInState,
            "FAULT_CARGO_HANDOFF" when session.DemandId is null =>
                ServerReasonCodes.ActionNotAllowedInState,
            "FORCED_MECHANICAL_RECOVERY" => null,
            "RESUME_AFTER_REPAIR" or "COMPENSATE_LOAD_ALL_EMPTY" or "FAULT_CARGO_HANDOFF" => null,
            _ => ServerReasonCodes.ActionNotAllowedInState
        };
    }

    /// <summary>
    /// The slot operation a recovery session is about: the one whose attempt the three recovery messages
    /// to the vehicle name -- <c>ExceptionRecoverySessionOpened</c>, every
    /// <c>ExceptionRecoverySessionSnapshot</c> and <c>RecoveryActionAccepted</c> -- and the one an action
    /// on the session is judged on and its workflow recorded against.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Protocol <c>2.0.0</c> item 7 (<c>8005-agv-program#95</c>, rules in commit <c>6ed3564c</c>). A
    /// session on a demand for which a slot operation had been commanded names that demand's latest
    /// attempt; a session with no demand, or opened before any slot operation -- before loading began
    /// -- names null. Never an attempt invented to be non-null, never one of another demand. This is
    /// the gap MVP <c>8005-agv-control-server#5</c> closed: compensation demands the attempt, and
    /// without this the vehicle, whose own record is cleared by then, has nowhere to read it from.
    /// </para>
    /// <para>
    /// <b>Fixed at the session's first value.</b> Only operations created no later than the session
    /// opened are considered, and an operation row is only ever created together with its command, so
    /// every later call answers what the first one did -- a replayed request, each snapshot revision,
    /// the accepted action. No column stores the value; it is derived from those persisted facts.
    /// </para>
    /// <para>
    /// <b>The only lookup of an existing session's operation.</b> The action and the snapshot's allowed
    /// actions once read the demand's latest operation, with no cut-off at the opening, while the
    /// messages read this; one source is what keeps the attempt a workflow records equal to the attempt
    /// the vehicle was told (8005-agv-control-server#78). <see cref="FindLatestOperationAsync"/> answers
    /// a different question -- what the demand's latest operation is now -- and is asked in two places:
    /// validating a session's scope before the session exists, where the two coincide, and refusing an
    /// action once the demand has moved past its session.
    /// </para>
    /// </remarks>
    private async Task<StationOperationRow?> SessionOperationAsync(
        ExceptionRecoverySessionRow session,
        CancellationToken cancellationToken)
    {
        if (session.DemandId is null) return null;
        StationOperationRow[] operations = await dbContext.StationOperations.AsNoTracking()
            .Where(row => row.DemandId == session.DemandId)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        return operations
            .Where(row => row.CreatedAt <= session.OpenedAt)
            .OrderByDescending(row => row.CreatedAt)
            .FirstOrDefault();
    }

    private async Task<StationOperationRow?> FindLatestOperationAsync(
        string demandId,
        CancellationToken cancellationToken)
    {
        StationOperationRow[] operations = await dbContext.StationOperations
            .Where(row => row.DemandId == demandId)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        return operations.OrderByDescending(row => row.CreatedAt).FirstOrDefault();
    }

    private static void ValidateResultIdentity(
        string messageType,
        JsonElement payload,
        RecoveryWorkflowRow workflow)
    {
        if (workflow.DemandId is not null && payload.TryGetProperty("demandId", out JsonElement demand) &&
            OptionalString(demand) != workflow.DemandId)
            throw new BusinessIdentityConflictException("Recovery result demandId does not match its workflow.");
        if (workflow.SlotOperationAttemptId is not null &&
            payload.TryGetProperty("slotOperationAttemptId", out JsonElement attempt) &&
            OptionalString(attempt) != workflow.SlotOperationAttemptId)
            throw new BusinessIdentityConflictException("Recovery result operation identity does not match its workflow.");
        if (workflow.ExceptionRecoverySessionId is not null &&
            payload.TryGetProperty("exceptionRecoverySessionId", out JsonElement session) &&
            OptionalString(session) != workflow.ExceptionRecoverySessionId)
            throw new BusinessIdentityConflictException("Recovery result session does not match its workflow.");
        if (messageType == "FaultCargoRecoveryResult" &&
            RequiredString(payload, "handoffId") != workflow.HandoffId)
            throw new BusinessIdentityConflictException("Fault cargo handoff result does not match the authorized handoff.");
    }

    private static bool HasExactSafeSlotResult(JsonElement payload, int[] expectedSlots, string? expectedState)
    {
        JsonElement[] results = payload.GetProperty("slotResults").EnumerateArray().ToArray();
        int[] actual = results.Select(item => item.GetProperty("slotNo").GetInt32()).Order().ToArray();
        return actual.SequenceEqual(expectedSlots) && results.Length == expectedSlots.Length &&
               results.All(item => RequiredString(item, "outcome") == "COMPLETED" &&
                                   (expectedState is null || RequiredString(item, "finalPhysicalState") == expectedState) &&
                                   RequiredString(item, "finalPhysicalState") != "UNKNOWN" &&
                                   RequiredString(item, "lockState") == "LOCKED" &&
                                   RequiredString(item, "unlockOutputState") == "RESET");
    }

    private bool RecoveryProofAccepted(string supplied)
    {
        string variable = configuration["Recovery:AuthenticationProofEnvironmentVariable"]
            ?? "CONTROL_SERVER_RECOVERY_AUTHENTICATION_PROOF";
        string? expected = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrEmpty(expected)) return false;
        byte[] expectedBytes = Encoding.UTF8.GetBytes(expected);
        byte[] suppliedBytes = Encoding.UTF8.GetBytes(supplied);
        return expectedBytes.Length == suppliedBytes.Length &&
               CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes);
    }

    private async Task<long> CurrentForcedGenerationAsync(string agvId, CancellationToken cancellationToken) =>
        await dbContext.VehicleRecoveryGenerations.Where(row => row.AgvId == agvId)
            .Select(row => (long?)row.ForcedRecoveryGeneration)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false) ??
        await dbContext.SessionRecoveries.Where(row => row.AgvId == agvId)
            .Select(row => (long?)row.ForcedRecoveryGeneration)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false) ?? 0;

    private string OpenedResponse(
        JsonElement request,
        ExceptionRecoverySessionRow session,
        string? slotOperationAttemptId) =>
        Response(request, "ExceptionRecoverySessionOpened", new
        {
            requestId = session.RequestId,
            exceptionRecoverySessionId = session.ExceptionRecoverySessionId,
            openedAt = session.OpenedAt,
            eventId = session.EventId,
            demandId = session.DemandId,
            slotOperationAttemptId,
            slots = ParseSlots(session.SlotsJson),
            recoverySessionRevision = session.Revision
        });

    private string AcceptedAction(
        JsonElement request,
        ExceptionRecoverySessionRow session,
        string actionId,
        string action,
        string? slotOperationAttemptId) =>
        Response(request, "RecoveryActionAccepted", new
        {
            recoveryActionId = actionId,
            exceptionRecoverySessionId = session.ExceptionRecoverySessionId,
            slotOperationAttemptId,
            acceptedAction = action,
            recoverySessionRevision = session.Revision,
            acceptedAt = session.UpdatedAt
        });

    private string RejectedAction(
        JsonElement request,
        string actionId,
        string sessionId,
        long revision,
        string reasonCode) =>
        Response(request, "RecoveryActionRejected", new
        {
            recoveryActionId = actionId,
            exceptionRecoverySessionId = sessionId,
            problem = Problem(reasonCode, "payload", "Recovery action is not allowed by current facts."),
            recoverySessionRevision = revision
        });

    private string Response(JsonElement request, string messageType, object payload) =>
        ProtocolEnvelope.Serialize(
            messageType,
            Guid.NewGuid().ToString("D"),
            RequiredString(request, "messageId"),
            RequiredString(request, "agvId"),
            request.GetProperty("sessionGeneration").GetInt64(),
            timeProvider.GetUtcNow(),
            payload);

    private string DurableAck(
        string acceptedMessageType,
        string acceptedMessageId,
        string agvId,
        long generation,
        string contentHash) =>
        // Two clock reads, and the order they happen in is observable on the wire: sentAt first, the
        // payload's durablyAcceptedAt second. Argument evaluation is left to right, so it still is.
        ProtocolEnvelope.Serialize(
            "DurableAck",
            Guid.NewGuid().ToString("D"),
            acceptedMessageId,
            agvId,
            generation,
            timeProvider.GetUtcNow(),
            new
            {
                acceptedMessageId,
                acceptedMessageType,
                acceptedContentSha256 = contentHash,
                durablyAcceptedAt = timeProvider.GetUtcNow()
            });

    private static object Problem(string reasonCode, string fieldPath, string displayMessage) => new
    {
        reasonCode,
        fieldPath,
        displayMessage
    };

    private void BindCommand(
        RecoveryWorkflowRow workflow,
        string commandId,
        string commandType,
        string contentHash)
    {
        workflow.CommandMessageId = commandId;
        workflow.CommandMessageType = commandType;
        workflow.CommandContentHash = contentHash;
        workflow.State = RecoveryWorkflowState.AwaitingResult;
        workflow.UpdatedAt = timeProvider.GetUtcNow();
    }

    /// <summary>
    /// Settles the command a workflow's first result answers, whatever that result concluded.
    /// </summary>
    /// <remarks>
    /// A recovery command has no ack of its own (<c>LoadCompensationCommandAck</c> is on the profile
    /// denylist); its answer is the result -- one of the five recovery results, or for a resume the
    /// replacement OperationResult. A workflow holding its first result takes no other, so a command
    /// left pending could only be replayed into a later session to draw a duplicate. Until
    /// 8005-agv-control-server#78 nothing settled it: a failed workflow, still in RecoveryRequired,
    /// had its command replayed into every session that followed, and a reconciled one sat unsettled
    /// in the outbox for the life of the database (8005-agv-program#61, MVP <c>219b033f</c>).
    /// </remarks>
    private async Task SettleAnsweredCommandAsync(
        RecoveryWorkflowRow workflow,
        CancellationToken cancellationToken)
    {
        if (workflow.CommandMessageId is null) return;
        await store.SettleAnsweredCommandAsync(
            workflow.CommandMessageId, timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
    }

    private static string ResultOutcome(string messageType, JsonElement payload) =>
        messageType == "ForcedMechanicalRecoveryResult"
            ? RequiredString(payload, "outcome")
            : RequiredString(payload, "overallOutcome");

    private static string PayloadHash(JsonElement payload) => WireContentHash.Sha256(payload.GetRawText());

    private static string StableGuid(string identity, string purpose)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{identity}|{purpose}"));
        Span<byte> guidBytes = bytes.AsSpan(0, 16);
        guidBytes[6] = (byte)((guidBytes[6] & 0x0f) | 0x50);
        guidBytes[8] = (byte)((guidBytes[8] & 0x3f) | 0x80);
        return new Guid(guidBytes).ToString("D");
    }

    private static int[] RequiredSlots(JsonElement payload, string propertyName)
    {
        int[] slots = payload.GetProperty(propertyName).EnumerateArray().Select(item => item.GetInt32()).ToArray();
        if (slots.Length is < 1 or > 8 || slots.Any(slot => slot is < 1 or > 8) ||
            slots.Distinct().Count() != slots.Length || !slots.SequenceEqual(slots.Order()))
            throw new InvalidDataException($"{propertyName} must be a unique ascending subset of 1..8.");
        return slots;
    }

    private static int[] ParseSlots(string json) => JsonSerializer.Deserialize<int[]>(json) ?? [];

    private static string RequiredUuid(JsonElement element, string propertyName)
    {
        string value = RequiredString(element, propertyName);
        return Guid.TryParseExact(value, "D", out _)
            ? value
            : throw new InvalidDataException($"Protocol field '{propertyName}' must be a UUID.");
    }

    private static string? OptionalUuid(JsonElement element, string propertyName)
    {
        JsonElement value = element.GetProperty(propertyName);
        if (value.ValueKind == JsonValueKind.Null) return null;
        string? text = value.GetString();
        return Guid.TryParseExact(text, "D", out _)
            ? text
            : throw new InvalidDataException($"Protocol field '{propertyName}' must be null or a UUID.");
    }

    private static string? OptionalString(JsonElement value) =>
        value.ValueKind == JsonValueKind.Null ? null : value.GetString();

    private static string RequiredString(JsonElement element, string propertyName)
    {
        string? value = element.GetProperty(propertyName).GetString();
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidDataException($"Protocol field '{propertyName}' is required.")
            : value;
    }

    /// <summary>
    /// 终结之后换序要用的路网与每区参数（批次7-10，control-server#215）；没注册或路网不可用时为空，终结于是只删不换。
    /// </summary>
    private async Task<PlanRevisionRouting?> ReadPlanRevisionRoutingAsync(CancellationToken cancellationToken) =>
        planRevisionRouting is null
            ? null
            : await planRevisionRouting.ReadAsync(cancellationToken).ConfigureAwait(false);
}
