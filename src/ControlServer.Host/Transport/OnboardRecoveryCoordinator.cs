using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Recovery;
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
    private static readonly Action<ILogger, string, string, string, string, Exception?> LogCancellationFoundDemandEnded =
        LoggerMessage.Define<string, string, string, string>(
            LogLevel.Warning,
            new EventId(2137, nameof(LogCancellationFoundDemandEnded)),
            "Load cancellation {CancellationId} for demand {DemandId} reported ALL_EMPTY after the demand had already " +
            "ended ({DemandStatus}; journey stage {Stage}); nothing was commanded for it, so the result is recorded and " +
            "the journey is left as it was (control-server#505).");
    private static readonly Action<ILogger, string, string, string, string, string, Exception?> LogResultOnEndedDemandBlocked =
        LoggerMessage.Define<string, string, string, string, string>(
            LogLevel.Error,
            new EventId(2136, nameof(LogResultOnEndedDemandBlocked)),
            "{MessageType} for workflow {WorkflowId} did not reconcile, and its demand {DemandId} has already ended " +
            "({DemandStatus}), so it cannot be marked RecoveryRequired; the journey is blocked under {BlockReasonCode}, " +
            "which no release path lifts: it needs a person (control-server#505).");
    private static readonly Action<ILogger, string, string, string, Exception?> LogResultForDeliveredDemand =
        LoggerMessage.Define<string, string, string>(
            LogLevel.Warning,
            new EventId(2133, nameof(LogResultForDeliveredDemand)),
            "{MessageType} for workflow {WorkflowId} would end demand {DemandId}, which was already delivered; " +
            "acknowledged as a result that does not reconcile, and the demand is left as delivered.");
    private static readonly Action<ILogger, string, string, string, string, string, string, Exception?>
        LogUnknownWorkflowResult =
            LoggerMessage.Define<string, string, string, string, string, string>(
                LogLevel.Warning,
                new EventId(2132, nameof(LogUnknownWorkflowResult)),
                "Vehicle {AgvId} reported {MessageType} for workflow {WorkflowId}, which this server never opened " +
                "(message {MessageId}, outcome {Outcome}, payload sha256 {PayloadSha256}); acknowledged and kept as " +
                "historical evidence, nothing settled.");
    private static readonly Action<ILogger, string, string, string, string, string, Exception?>
        LogCompensationRequestedAgain =
            LoggerMessage.Define<string, string, string, string, string>(
                LogLevel.Information,
                new EventId(2129, nameof(LogCompensationRequestedAgain)),
                "Compensation {WorkflowId} of session {SessionId} was requested again by operator {OperatorId} " +
                "(verified {VerifiedAt}) in message {MessageId}. It was already authorized: nothing is authorized " +
                "again, and the command it earned is re-sent unchanged.");

    private static readonly Action<ILogger, string, string, string, string, string, string, Exception?>
        LogBlockedJourneyReleased =
            LoggerMessage.Define<string, string, string, string, string, string>(
                LogLevel.Information,
                new EventId(2135, nameof(LogBlockedJourneyReleased)),
                "Vehicle {AgvId}: {MessageType} for workflow {WorkflowId} ended demand {DemandId}, whose slot operation " +
                "the journey was blocked on ({BlockReasonCode}); the journey carries other demands and goes back to " +
                "{Stage} (control-server#499).");

    private static readonly Action<ILogger, string, Exception?> LogClosingSnapshotNotSent =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(2133, nameof(LogClosingSnapshotNotSent)),
            "An exception recovery session of vehicle {AgvId} was closed by an administrator, and sending its CLOSED snapshot " +
            "failed; the closing stands and the snapshot stays in the outbox for the reconnect replay (control-server#483).");

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
        RecoveryWorkflowRow? workflow = await dbContext.RecoveryWorkflows.SingleOrDefaultAsync(
            row => row.WorkflowId == workflowId,
            cancellationToken).ConfigureAwait(false);
        // A workflow this server knows, opened for another vehicle, is a known business id with other content -- refused,
        // never taken as unknown: the acknowledgement below is only for an id this server has no record of (review of
        // #489, S2). Until #481 this read filtered on the vehicle too, and such a result ended the connection.
        if (workflow is not null && workflow.AgvId != agvId)
        {
            throw new InboundMessageRejectedException(ServerReasonCodes.BusinessIdContentConflict,
                "Recovery result names a workflow of another vehicle.");
        }
        if (workflow is null)
        {
            return await RecordUnknownWorkflowResultAsync(
                messageType, messageId, agvId, sessionGeneration, workflowId, payload, contentHash, cancellationToken)
                .ConfigureAwait(false);
        }
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
                throw new InboundMessageRejectedException(ServerReasonCodes.MessageIdContentConflict,
                    "Recovery result MessageId was replayed with a different identity.");
            }
            return DurableAck(messageType, messageId, agvId, sessionGeneration, contentHash);
        }
        if (workflow.ResultMessageId is not null && workflow.ResultMessageId != messageId)
        {
            throw new InboundMessageRejectedException(ServerReasonCodes.BusinessIdContentConflict,
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

    /// <summary>
    /// A recovery result naming a workflow this server has no record of (control-server#481): a cancellation, correction or
    /// recovery action the vehicle took from a server whose database has since been replaced, or from another server
    /// instance. Kept as historical evidence and acknowledged, changing nothing. Until #481 the lookup threw, the connection
    /// ended, and the onboard replays an unacknowledged result in every handshake; a refusal would only move that loop to
    /// the onboard, which gives a row up on four row-content codes alone (8005-agv-onboard-hmi#254).
    /// </summary>
    private async Task<string> RecordUnknownWorkflowResultAsync(
        string messageType,
        string messageId,
        string agvId,
        long sessionGeneration,
        string workflowId,
        JsonElement payload,
        string contentHash,
        CancellationToken cancellationToken)
    {
        string outcome = ResultOutcome(messageType, payload);
        if (!await dbContext.RecoveryResultEvidence.AnyAsync(row => row.MessageId == messageId, cancellationToken)
                .ConfigureAwait(false))
        {
            dbContext.RecoveryResultEvidence.Add(new RecoveryResultEvidenceRow
            {
                MessageId = messageId,
                WorkflowId = workflowId,
                MessageType = messageType,
                ForcedRecoveryGeneration = messageType == "ForcedMechanicalRecoveryResult"
                    ? payload.GetProperty("forcedRecoveryGeneration").GetInt64()
                    : await CurrentForcedGenerationAsync(agvId, cancellationToken).ConfigureAwait(false),
                ContentHash = contentHash,
                Outcome = outcome,
                HistoricalOnly = true,
                ObservedAt = payload.GetProperty("observedAt").GetDateTimeOffset(),
                ReceivedAt = timeProvider.GetUtcNow()
            });
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        LogUnknownWorkflowResult(
            logger ?? (ILogger)NullLogger.Instance,
            agvId, messageType, workflowId, messageId, outcome, WireContentHash.Sha256(payload.GetRawText()), null);
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
        // Released only when nothing else of the journey is unresolved -- another demand awaiting recovery, another
        // operation not converged, an unreleasable *_NOT_RECONCILED_ON_ENDED_DEMAND block (control-server#506, #505; the same predicate as the
        // ending path, BlockedJourneyRelease.NothingElseUnresolvedAsync, which carries the reasons and the cost). Held,
        // the journey stays Blocked with its code; the workflow and the session settle as before.
        StationOperationRow? operation = reconciled
            ? await dbContext.StationOperations.SingleAsync(
                row => row.SlotOperationAttemptId == slotOperationAttemptId,
                cancellationToken).ConfigureAwait(false)
            : null;
        // A resumed load that committed leaves its demand where a load that committed the first time leaves it: Accepted.
        // The store commits the operation and touches no demand on a load (ApplyOperationResultAsync), so the
        // RecoveryRequired the failed result wrote stayed for the rest of the journey, and the release predicate above read
        // it as another demand still awaiting recovery: a second recovery in the same journey -- a resume or a handoff of
        // another demand -- held the journey Blocked for good (control-server#506 review M-1). Staged into this save, before
        // the predicate reads the demands. A determinate failure is not touched: the runtime ends that demand. An unload
        // that committed has already made its demand Succeeded.
        if (disposition is OperationResultDisposition.Accepted &&
            operation is { OperationType: SlotOperationType.Load, Status: StationOperationStatus.Committed })
        {
            AcceptedDemandRow? resumedDemand = await dbContext.AcceptedDemands.SingleOrDefaultAsync(
                row => row.DemandId == operation.DemandId, cancellationToken).ConfigureAwait(false);
            if (resumedDemand?.Status == DemandExecutionStatus.RecoveryRequired)
            {
                resumedDemand.Status = DemandExecutionStatus.Accepted;
            }
        }
        JourneyStopCursor? stops = runtime is not null && operation is not null
            ? await JourneyStopCursor.LoadIncludingUnsavedChangesAsync(dbContext, runtime, cancellationToken)
                .ConfigureAwait(false)
            : null;
        if (runtime is not null && operation is not null && stops is not null &&
            await BlockedJourneyRelease.NothingElseUnresolvedAsync(dbContext, runtime, stops, operation, cancellationToken)
                .ConfigureAwait(false))
        {
            // The same stage the ending path goes back to (control-server#505): the resumed operation is the current stop's,
            // so this is what its type used to give.
            runtime.Stage = BlockedJourneyRelease.ReleaseStage(stops);
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

    /// <summary>
    /// The outcome a workflow is judged on when an administrator closed its session because its result will not come
    /// (control-server#483). Like <see cref="ResumeCommandRejectedOutcome"/> it marks, in the store, why the session closed;
    /// it is not a wire code. A result arriving afterwards is refused, and the refusal names it.
    /// </summary>
    internal const string AdministratorClosedOutcome = RecoveryWorkflowOutcomes.AdministratorClosed;

    /// <summary>
    /// Which selected actions an administrator may close a session on while their outcome is awaited, and the workflow
    /// states that count as awaiting it. A resume since control-server#483; the other three since #484 (the coordinator's
    /// decision of 2026-10-06), a compensation also while it still awaits its authorization -- its session is ACTION_SELECTED
    /// then and nothing has gone to the vehicle. Closing a forced recovery leaves its hardware hold and its generation where
    /// they were; the way out of the fence that leaves is <see cref="ForcedFenceLiftedOverAdministratorClosingsAsync"/>.
    /// </summary>
    internal static IReadOnlyDictionary<string, IReadOnlySet<RecoveryWorkflowState>> AdministratorClosableActions { get; } =
        new Dictionary<string, IReadOnlySet<RecoveryWorkflowState>>(StringComparer.Ordinal)
        {
            ["RESUME_AFTER_REPAIR"] = AwaitingCommandOutcome(),
            ["FAULT_CARGO_HANDOFF"] = AwaitingCommandOutcome(),
            ["FORCED_MECHANICAL_RECOVERY"] = AwaitingCommandOutcome(),
            ["COMPENSATE_LOAD_ALL_EMPTY"] = new HashSet<RecoveryWorkflowState>
            {
                RecoveryWorkflowState.AwaitingAuthorization,
                RecoveryWorkflowState.CommandPending,
                RecoveryWorkflowState.AwaitingResult
            }
        };

    private static HashSet<RecoveryWorkflowState> AwaitingCommandOutcome() =>
        [RecoveryWorkflowState.CommandPending, RecoveryWorkflowState.AwaitingResult];

    /// <summary>
    /// Closes the vehicle's one open session whose action's result will never come, on an administrator's word
    /// (control-server#483): the result was refused and abandoned, or the vehicle went away for good. The workflow is judged
    /// <see cref="RecoveryWorkflowState.RecoveryRequired"/> under <see cref="AdministratorClosedOutcome"/>, its command is
    /// settled so it is no longer replayed, and the session closes the way a result that does not reconcile closes it
    /// (<see cref="AdvanceSessionAfterResultAsync"/>): CLOSED, a new revision, a CLOSED snapshot queued. The demand, the
    /// journey, the operation, the lease and the vehicle are not touched -- exactly as control-server#187 leaves them -- for
    /// the next session to take up. A replacement result arriving afterwards finds no resume awaiting it and is refused
    /// whole (<see cref="WireToGateStore"/>, BUSINESS_ID_CONTENT_CONFLICT naming <see cref="AdministratorClosedOutcome"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Only under the caller's write transaction</b>, and refused without one: everything is read and decided inside it,
    /// so an inbound result that commits first is seen here and this refuses, and one that commits after finds the session
    /// closed. A refusal writes nothing.
    /// </para>
    /// <para>
    /// <b>Not while the vehicle may still deliver the result</b> (review of control-server#483, S1). While the vehicle is
    /// connected (<paramref name="connectedSessionGeneration"/> not null) and its latest RecoveryStateReport still names the
    /// resume's attempt -- as a pending attempt or as its unsettled one -- or names any pending result at all, the result
    /// may be on its way, and closing would refuse it when it lands and the vehicle would drop a real result. A pending
    /// result is named by its messageId alone, which does not say which attempt it settles, so any one counts. A vehicle
    /// that is not connected is closed regardless: waiting for one that never returns is the very thing this exit ends.
    /// </para>
    /// <para>
    /// <b>Nor while the vehicle is in its handshake</b> (incremental review of control-server#483). A SessionHello clears the
    /// reported pending facts, and the vehicle replays its unacknowledged results before its recovery report is answered,
    /// which is before its connection becomes routable: in that window the report on file says nothing and the vehicle
    /// would read as gone. So a vehicle whose hello has arrived and whose connection is not routable yet
    /// (<paramref name="handshaking"/>), or whose routable connection is of another session generation than the one on file,
    /// is refused as well -- the facts on file are not its finished report.
    /// </para>
    /// </remarks>
    internal async Task<AdministratorCloseDecision> CloseSessionAwaitingResultAsync(
        string agvId,
        string? exceptionRecoverySessionId,
        long? connectedSessionGeneration,
        bool handshaking,
        Action<object?> facts,
        CancellationToken cancellationToken)
    {
        if (dbContext.Database.CurrentTransaction is null)
            throw new InvalidOperationException(
                "An exception recovery session is closed by an administrator only under the caller's write transaction.");

        ExceptionRecoverySessionRow? session = await dbContext.ExceptionRecoverySessions.SingleOrDefaultAsync(
            row => row.AgvId == agvId && row.State != "CLOSED", cancellationToken).ConfigureAwait(false);
        SessionRecoveryRow? connection = await dbContext.SessionRecoveries.AsNoTracking()
            .SingleOrDefaultAsync(row => row.AgvId == agvId, cancellationToken).ConfigureAwait(false);
        RecoveryWorkflowRow[] workflows = session is null
            ? []
            : await dbContext.RecoveryWorkflows
                .Where(row => row.ExceptionRecoverySessionId == session.ExceptionRecoverySessionId)
                .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        string? selectedAction = session?.SelectedAction;
        IReadOnlySet<RecoveryWorkflowState>? awaiting = selectedAction is null
            ? null
            : AdministratorClosableActions.GetValueOrDefault(selectedAction);
        // Every workflow of the selected action still awaiting its outcome: a session can hold more than one compensation,
        // one whose command is out and others still awaiting their authorization (control-server#187), and the closing judges
        // them all, so none is left behind to be authorized into a closed session.
        RecoveryWorkflowRow[] awaitingWorkflows = workflows
            .Where(row => row.WorkflowType == selectedAction && awaiting?.Contains(row.State) == true)
            .OrderByDescending(row => row.CommandMessageId is not null)
            .ThenBy(row => row.CreatedAt)
            .ToArray();
        // The one the session closes on -- the one with its command out, if any; otherwise the action's latest, for the audit.
        RecoveryWorkflowRow? workflow = awaitingWorkflows.FirstOrDefault() ??
            workflows.Where(row => row.WorkflowType == selectedAction).MaxBy(row => row.CreatedAt);
        string[] pendingAttempts = connection is null ? [] : ParseStrings(connection.PendingAttemptIdsJson);
        string[] pendingResults = connection is null ? [] : ParseStrings(connection.PendingResultIdsJson);
        int[] activeUnlockSlots = connection is null ? [] : ParseSlots(connection.ActiveUnlockSlotsJson);
        string? attemptId = workflow?.SlotOperationAttemptId;
        bool namesAttempt = attemptId is not null &&
                            (pendingAttempts.Contains(attemptId, StringComparer.Ordinal) ||
                             connection?.UnsettledSlotOperationAttemptId == attemptId);
        // What says the outcome may still be on its way, by action (control-server#484). A pending result is named by its
        // messageId alone, so any one counts, for every action. A resume, a handoff and a compensation are about the session's
        // attempt, so a report naming it counts; a handoff and a compensation open doors, and slots whose unlock output is
        // active mean the vehicle is at them. The resume is judged as #483 left it. A forced recovery is not judged on the
        // report at all -- see the refusal below.
        bool resultInFlight = connectedSessionGeneration is not null &&
                              (pendingResults.Length > 0 ||
                               selectedAction switch
                               {
                                   "RESUME_AFTER_REPAIR" => namesAttempt,
                                   _ => namesAttempt || activeUnlockSlots.Length > 0
                               });
        // A forced recovery on a connected vehicle may be under way whatever the report says (review of #484, S2): the report on
        // file is the one that ended the connection's handshake, and every recovery command still awaiting its result is sent
        // again right after it (OnboardMessageProcessor, ReplayPendingCommandsAsync). So the forced command reached this
        // connection after its report, and the report can say nothing about it: no pending result, no unlock output, while a
        // person stands at the vehicle forcing the doors. The way out for a connected vehicle is the result and its hardware
        // record; a result refused is taken up by the onboard's isolation and hardware record entry (8005-agv-onboard-hmi#150).
        bool forcedInProgress = connectedSessionGeneration is not null && selectedAction == "FORCED_MECHANICAL_RECOVERY";
        facts(new
        {
            vehicle = new
            {
                connected = connectedSessionGeneration is not null,
                connectedSessionGeneration,
                handshaking,
                recordedSessionGeneration = connection?.SessionGeneration,
                recoveryReportId = connection?.RecoveryReportId,
                pendingAttemptIds = pendingAttempts,
                pendingResultIds = pendingResults,
                unsettledSlotOperationAttemptId = connection?.UnsettledSlotOperationAttemptId,
                activeUnlockSlots
            },
            session = session is null
                ? null
                : new
                {
                    session.ExceptionRecoverySessionId,
                    session.State,
                    session.SelectedAction,
                    session.Revision,
                    session.DemandId
                },
            workflow = workflow is null
                ? null
                : new
                {
                    workflow.WorkflowId,
                    workflow.WorkflowType,
                    state = workflow.State.ToString(),
                    workflow.SlotOperationAttemptId,
                    workflow.CommandMessageId,
                    workflow.ResultMessageId
                }
        });
        if (session is null)
            return new AdministratorCloseDecision([RecoverySessionAdministratorCloseCodes.SessionNotFound], null);
        if (exceptionRecoverySessionId is not null &&
            !string.Equals(exceptionRecoverySessionId, session.ExceptionRecoverySessionId, StringComparison.Ordinal))
            return Refused(RecoverySessionAdministratorCloseCodes.SessionMismatch);
        // ACTION_SELECTED is a compensation still awaiting its authorization (control-server#484); the table decides below
        // whether the selected action may be closed in it.
        if (session.State is not ("EXECUTING" or "ACTION_SELECTED"))
            return Refused(RecoverySessionAdministratorCloseCodes.SessionNotExecuting);
        if (awaiting is null)
            return Refused(RecoverySessionAdministratorCloseCodes.ActionNotClosable);
        if (workflow is null || !awaiting.Contains(workflow.State))
            return Refused(RecoverySessionAdministratorCloseCodes.ResultNotAwaited);
        if (handshaking || (connectedSessionGeneration is not null && connectedSessionGeneration != connection?.SessionGeneration))
            return Refused(RecoverySessionAdministratorCloseCodes.VehicleHandshakeInProgress);
        if (forcedInProgress)
            return Refused(RecoverySessionAdministratorCloseCodes.ForcedInProgressOnVehicle);
        if (resultInFlight)
            return Refused(RecoverySessionAdministratorCloseCodes.ResultInFlightOnVehicle);

        foreach (RecoveryWorkflowRow closed in awaitingWorkflows)
        {
            closed.State = RecoveryWorkflowState.RecoveryRequired;
            closed.Outcome = AdministratorClosedOutcome;
            closed.UpdatedAt = timeProvider.GetUtcNow();
            await SettleAnsweredCommandAsync(closed, cancellationToken).ConfigureAwait(false);
        }
        await AdvanceSessionAfterResultAsync(workflow, connection!.SessionGeneration, cancellationToken).ConfigureAwait(false);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new AdministratorCloseDecision([], session.ExceptionRecoverySessionId);

        AdministratorCloseDecision Refused(string code) => new([code], session.ExceptionRecoverySessionId);

        static string[] ParseStrings(string json) => JsonSerializer.Deserialize<string[]>(json) ?? [];
    }

    /// <summary>
    /// Sends the vehicle the session snapshots still waiting for it, from outside its connection's loop, after the closing
    /// has committed. Nothing here may undo or misreport that: a vehicle that is not connected is not a failure (the
    /// snapshot stays in the outbox and the reconnect replay delivers it), and any other failure is logged and left to that
    /// same replay.
    /// </summary>
    internal async Task TrySendPendingSessionSnapshotsAsync(string agvId, CancellationToken cancellationToken)
    {
        try
        {
            await SendPendingSessionSnapshotsAsync(agvId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        {
            // Not on the line: the replay after the next recovery report carries it.
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            LogClosingSnapshotNotSent(logger ?? (ILogger)NullLogger.Instance, agvId, error);
        }
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
        // 这条结果若结束了一站而旅程继续，那张空清单同样随它那次保存落库（control-server#324）。
        await StopEndWorklist.SendAsync(publisher, dbContext, RequiredString(root, "agvId"), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>迟到扫码若得到了过时答复（control-server#324），在本条应答之后发出去。</summary>
    public Task SendLateSublotRejectionAsync(JsonElement root, CancellationToken cancellationToken) =>
        LateSublotSubmission.SendAsync(publisher, dbContext, RequiredString(root, "messageId"), cancellationToken);

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
                throw new InboundMessageRejectedException(ServerReasonCodes.BusinessIdContentConflict,
                    "Recovery requestId was replayed with different content.");
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
                throw new InboundMessageRejectedException(ServerReasonCodes.BusinessIdContentConflict,
                    "RecoveryActionId was replayed with different content.");
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
        bool forcedFenceLifted = action == "FORCED_MECHANICAL_RECOVERY" &&
                                 await ForcedFenceLiftedOverAdministratorClosingsAsync(connection, cancellationToken)
                                     .ConfigureAwait(false);
        string? actionProblem = ValidateActionPreconditions(action, session, connection, operation, forcedFenceLifted);
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
                throw new InboundMessageRejectedException(ServerReasonCodes.BusinessIdContentConflict,
                    "Hardware recovery record was replayed with different content.");
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
            problem = authorized ? null : RefusedCancellationProblem(
                demand, "payload.demandId", "Load cancellation is not safe in the current state.")
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
        AcceptedDemandRow? demand = authorized ? null : await dbContext.AcceptedDemands.AsNoTracking()
            .SingleOrDefaultAsync(row => row.DemandId == demandId, cancellationToken).ConfigureAwait(false);
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
            problem = authorized ? null : RefusedCancellationProblem(
                demand, "payload.slotOperationAttemptId",
                "Load cancellation before a sublot entry is not allowed in the current state.")
        });
    }

    /// <summary>
    /// 拒绝一次装货取消时给车的原因（control-server#324）：这条需求已经结束——它所在的那一站被期限、取消或补偿结束了，
    /// 或者整趟已经收尾——就是 <c>WORKLIST_REVISION_STALE</c>；其余照旧 <c>ACTION_NOT_ALLOWED_IN_STATE</c>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 车上按的是它手里那一版清单上的「取消装货」。需求已经结束，说明那一版清单过时了：服务端此后发过更高号的清单（空清单或收尾快照），
    /// 原因码据实说「你那一版过时了」。先前一律答 <c>ACTION_NOT_ALLOWED_IN_STATE</c>，车载端据此显示「请检查授权、车辆停稳信号和
    /// 服务端状态」，把操作员引去查三样都没问题的东西。
    /// </para>
    /// <para>
    /// <b>判据是「需求已终结」，不是「期限到了」或阶段。</b>车还在路上时这一站还没开始，谈不上过时，仍是
    /// <c>ACTION_NOT_ALLOWED_IN_STATE</c>；需求还开着而这一刻不能取消（已下装货命令、已有一条取消开着）同样如此。
    /// </para>
    /// </remarks>
    private static object RefusedCancellationProblem(AcceptedDemandRow? demand, string fieldPath, string displayMessage) =>
        demand?.Status is DemandExecutionStatus.Cancelled or DemandExecutionStatus.Succeeded
            ? Problem(
                ServerReasonCodes.WorklistRevisionStale, fieldPath,
                "The worklist this cancellation was made from is no longer current: the demand has already ended.")
            : Problem(ServerReasonCodes.ActionNotAllowedInState, fieldPath, displayMessage);

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
            return !await store.IsTaskTypeAllowedAtAreaEndAsync(demand.DemandId, demand.WorkType, cancellationToken)
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
            workflow.State is not (RecoveryWorkflowState.AwaitingAuthorization or RecoveryWorkflowState.CommandPending
                or RecoveryWorkflowState.AwaitingResult))
        {
            // A compensation an administrator closed while it awaited its authorization (control-server#484): its session is
            // what ended, and the refusal says so, as it does for a session closed on another result below.
            bool closedByAdministrator = workflow?.Outcome == AdministratorClosedOutcome &&
                                         workflow.State == RecoveryWorkflowState.RecoveryRequired;
            return Response(root, "LoadCompensationRejected", new
            {
                recoveryActionId = actionId,
                problem = closedByAdministrator
                    ? Problem(ServerReasonCodes.RecoverySessionNotOpen, "payload",
                        "The recovery session this compensation belongs to was closed by an administrator.")
                    : Problem(ServerReasonCodes.ActionNotAllowedInState, "payload",
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
        else
        {
            // The same compensation asked for again after its command was bound (8005-agv-onboard-hmi#236). The vehicle
            // asks again when the link dropped before it could know the first request arrived, and it cannot tell
            // "lost on the way here" from "the command was lost on the way back". So this is the request it already
            // made -- same recoveryActionId, and the manifest's other business keys checked above -- and it is answered
            // the way the correction's twin is: nothing authorized again, the persisted command re-sent by
            // SendTriggeredCommandAsync. Until #236 it was refused here, and the vehicle's answer to that refusal is to
            // drop the compensation it is about to carry out. A workflow with a result is past AwaitingResult and still
            // refused above; a session closed meanwhile is refused here and nothing is authorized for it. The refusal
            // does not stop the command going out again, though: OnboardMessageProcessor runs SendTriggeredCommandAsync
            // after every LoadCompensationRequested, refused or not, and it re-sends the command persisted under this
            // recoveryActionId while its outbox row is unsettled. The vehicle, having taken the refusal and dropped its
            // vector, fails to bind that command and answers FAILED without touching IO. Pinned by
            // ARepeatedCompensationRequestOutsideTheSameOpenBoundCompensationIsStillRefused; not changed here.
            ExceptionRecoverySessionRow session = await dbContext.ExceptionRecoverySessions.AsNoTracking().SingleAsync(
                row => row.ExceptionRecoverySessionId == workflow.ExceptionRecoverySessionId,
                cancellationToken).ConfigureAwait(false);
            if (session.State == "CLOSED")
            {
                return Response(root, "LoadCompensationRejected", new
                {
                    recoveryActionId = actionId,
                    problem = Problem(ServerReasonCodes.RecoverySessionNotOpen, "payload",
                        "The recovery session this compensation belongs to has already closed.")
                });
            }

            // The operator is not one of the business keys, so another person pressing again is still this request.
            // Who asked again, and when, is kept here; the command does not change -- it was authorized once.
            JsonElement requestedBy = payload.GetProperty("operator");
            LogCompensationRequestedAgain(
                logger ?? (ILogger)NullLogger.Instance,
                actionId,
                workflow.ExceptionRecoverySessionId!,
                RequiredString(requestedBy, "operatorId"),
                requestedBy.GetProperty("verifiedAt").GetRawText().Trim('"'),
                RequiredString(root, "messageId"),
                null);
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
        // Only for a demand still on board (control-server#505): accepted, and loaded on this journey. A correction for one
        // already unloaded ran over slots that may hold another demand's cargo by now, and when it did not reconcile the journey
        // was blocked under a code no demand could carry (KeepDemandAndJourneyBlockedAsync). Whether the vehicle is still at
        // that demand's own pickup -- REQ-0237's window proper -- is control-server#287's, not checked here.
        bool onBoard = journey is not null &&
                       await dbContext.AcceptedDemands.AsNoTracking().AnyAsync(
                           row => row.DemandId == demandId && row.Status == DemandExecutionStatus.Accepted,
                           cancellationToken).ConfigureAwait(false) &&
                       await DemandJourneyLookup.Memberships(dbContext).AsNoTracking().AnyAsync(
                           row => row.DemandId == demandId && row.JourneyId == journey.JourneyId &&
                                  row.Status == JourneyDemandStatuses.Loaded,
                           cancellationToken).ConfigureAwait(false);
        // REQ-0237: an ordinary mis-placement is corrected only before the vehicle leaves the pickup.
        // A committed load alone is not enough -- until 2026-09-13 this authorized corrections for a
        // vehicle already sent to the gate, whose onboard could only refuse to open the doors.
        if (operation is null || operation.OperationType != SlotOperationType.Load ||
            operation.Status != StationOperationStatus.Committed ||
            !slots.All(ParseSlots(operation.TargetSlotsJson).Contains) ||
            journey?.Stage != JourneyRuntimeStage.AwaitingStationDeparture ||
            !onBoard)
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
            await KeepDemandAndJourneyBlockedAsync(workflow, messageType, cancellationToken).ConfigureAwait(false);
            return;
        }
        // A result that would end a demand already delivered (control-server#481). It cannot end it: the demand is the
        // unload's, and nothing here may rewrite that. Taken as a result that does not reconcile -- the workflow
        // RecoveryRequired, nothing else written -- and acknowledged. #478 refused it with ACTION_NOT_ALLOWED_IN_STATE, but
        // the onboard keeps a refused row on file unless the code is one of four row-content conflicts
        // (8005-agv-onboard-hmi#254), so it came back in every handshake. Checked here, inside the inbox's write
        // transaction, before any termination is staged; PickupStopTermination still throws on such a demand for its
        // runtime callers. A correction is not an ending: it settles nothing about the demand, and one opened on a demand
        // already unloaded is an ordinary path (Batch7StationYieldTests), so it reconciles as before (review of #489, S1).
        if (messageType != "LoadCorrectionResult" &&
            await DemandDeliveredAsync(workflow.DemandId, cancellationToken).ConfigureAwait(false))
        {
            workflow.State = RecoveryWorkflowState.RecoveryRequired;
            LogResultForDeliveredDemand(
                logger ?? (ILogger)NullLogger.Instance,
                messageType, workflow.WorkflowId, workflow.DemandId!, null);
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
                    workflow.WorkflowId, stop.DemandId ?? stop.JourneyId, stop.Stage.ToString(), stop.BlockReasonCode, null);
                return;
            }
            // The same for the demand alone (control-server#505): the journey goes on with others, and this demand was ended
            // between the authorization and this result -- by its station deadline, say. Nothing was commanded for it and the
            // vehicle has proved its slots empty, so there is nothing left to hold the journey for. Until #505 the stage check
            // below took this for a result arriving in the wrong stage and blocked the journey under a code no demand carried.
            DemandExecutionStatus? cancelledStatus = await dbContext.AcceptedDemands.AsNoTracking()
                .Where(row => row.DemandId == workflow.DemandId)
                .Select(row => (DemandExecutionStatus?)row.Status)
                .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (cancelledStatus is DemandExecutionStatus.Cancelled or DemandExecutionStatus.Succeeded)
            {
                LogCancellationFoundDemandEnded(
                    logger ?? (ILogger)NullLogger.Instance,
                    workflow.WorkflowId, workflow.DemandId, cancelledStatus.Value.ToString(), stop.Stage.ToString(), null);
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
                await KeepDemandAndJourneyBlockedAsync(workflow, messageType, cancellationToken).ConfigureAwait(false);
                return;
            }
            // 给了路网的终结在删掉空停靠之后还换序（批次7-10，control-server#215，调度决策 6）；没给就只删不换。
            PickupStopTermination cancelledTermination =
                new(dbContext, await ReadPlanRevisionRoutingAsync(cancellationToken).ConfigureAwait(false));
            await cancelledTermination
                .StageAsync(
                    stop,
                    stopCursor.CurrentSublotRequestMessageId(stop.WorklistRevision),
                    workflow.DemandId,
                    "CANCELLED_BY_OPERATOR",
                    timeProvider.GetUtcNow(),
                    cancellationToken).ConfigureAwait(false);
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
        StationOperationRow? endedOperation = null;
        StationOperationStatus? statusBeforeEnding = null;
        // Read before the termination below rewrites it: a demand still RecoveryRequired is the mark an unreconciled result left,
        // and ending it is what may release a *_NOT_RECONCILED block (BlockedJourneyRelease, criterion 2, control-server#505).
        DemandExecutionStatus? demandStatusBeforeEnding = await dbContext.AcceptedDemands
            .Where(row => row.DemandId == workflow.DemandId)
            .Select(row => (DemandExecutionStatus?)row.Status)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (workflow.SlotOperationAttemptId is not null)
        {
            endedOperation = await dbContext.StationOperations.SingleOrDefaultAsync(
                row => row.SlotOperationAttemptId == workflow.SlotOperationAttemptId,
                cancellationToken).ConfigureAwait(false);
            if (endedOperation is not null)
            {
                statusBeforeEnding = endedOperation.Status;
                endedOperation.Status = StationOperationStatus.Cancelled;
            }
        }
        PickupStopTermination provenEmptyTermination =
            new(dbContext, await ReadPlanRevisionRoutingAsync(cancellationToken).ConfigureAwait(false));
        await provenEmptyTermination
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
                cancellationToken).ConfigureAwait(false);
        // 旅程还带着别的需求、因此没收尾，而它阻塞在的正是刚结清的这一次操作：放回等那一次结果的阶段，由引擎接着走
        // （control-server#499）。不是这一次的、或别的操作还没收敛的，留在 Blocked。条件与理由见 BlockedJourneyRelease。
        string? blockedFor = runtime.BlockReasonCode;
        if (await BlockedJourneyRelease
                .StageAsync(
                    dbContext, runtime, endedOperation, statusBeforeEnding, demandStatusBeforeEnding, timeProvider.GetUtcNow(),
                    cancellationToken)
                .ConfigureAwait(false))
        {
            // Written before the caller's save, like the other lines here: the store, not the log, is the record.
            LogBlockedJourneyReleased(
                logger ?? (ILogger)NullLogger.Instance,
                runtime.AgvId, messageType, workflow.WorkflowId, workflow.DemandId, blockedFor ?? "(none)",
                runtime.Stage.ToString(), null);
        }
        if (messageType is "FaultCargoRecoveryResult" or "ForcedMechanicalRecoveryResult")
        {
            await SettleHandedOffCargoAsync(runtime, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A demand's cargo left the vehicle by hand: the fault's cargo binding has done its work once nothing of the journey is left, and
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
    /// <b>Released by journey, never by demand</b> (control-server#376): a binding stands for the cargo of the journey, whichever
    /// demand it names -- the engine binds under the journey's anchor, which need not be the one handed off. So it goes only once
    /// nothing of the journey is left on board. It used to go by demand as well, which released it while another demand of the
    /// journey was still loaded, and the vehicle's next fault took a loaded vehicle for an empty one.
    /// </para>
    /// </remarks>
    private async Task SettleHandedOffCargoAsync(
        JourneyRuntimeRow runtime,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();

        // The anchor may have ended before its load while another demand of the journey, still to load, keeps the journey open
        // (control-server#376 review M1). The closed-journey rule cannot catch that binding later: its anchor is still an active
        // member of an open journey.
        await FaultedCargoBindings.StageReleaseWhenNothingLeftOnBoardAsync(
                dbContext, runtime.JourneyId, runtime.AgvId, HandedOffInExceptionSessionReason, now, cancellationToken)
            .ConfigureAwait(false);

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

    /// <summary>
    /// A recovery result of <paramref name="workflow"/> did not reconcile: its demand is marked RecoveryRequired and its journey
    /// blocked under <c>&lt;messageType&gt;_NOT_RECONCILED</c>. Staged; the caller saves.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Every such block names a demand that carries the mark</b> (control-server#505). The release paths
    /// (<see cref="BlockedJourneyRelease"/>) read "nothing is left unresolved" off the demands still RecoveryRequired, so a block no
    /// demand stands for would be lifted with whatever else the journey was waiting on. A demand already delivered or cancelled
    /// cannot take the mark -- that would rewrite how it ended -- and until #505 the journey was blocked under the ordinary code all
    /// the same. Now it is blocked under <see cref="BlockedJourneyRelease.OnEndedDemandSuffix"/>, which neither release path lifts:
    /// the slots that result left unknown may hold another demand's cargo by now, so letting the vehicle go is the wrong release,
    /// and staying here is the stuck state a person resolves. The authorizations keep it rare: a correction is authorized only for
    /// a demand on board, and a session is not opened for a demand that has ended.
    /// </para>
    /// <para>
    /// <b>That code is never replaced</b>, by an ordinary one here or by anything else: replaced, it would be lifted with the next
    /// ordinary block it stood under. An ordinary code may still replace another; the demand's mark, not the text, is what holds it.
    /// </para>
    /// </remarks>
    private async Task KeepDemandAndJourneyBlockedAsync(
        RecoveryWorkflowRow workflow,
        string messageType,
        CancellationToken cancellationToken)
    {
        string? demandId = workflow.DemandId;
        if (demandId is null) return;
        AcceptedDemandRow? demand = await dbContext.AcceptedDemands.SingleOrDefaultAsync(
            row => row.DemandId == demandId, cancellationToken).ConfigureAwait(false);
        bool ended = demand is null || demand.Status is DemandExecutionStatus.Succeeded or DemandExecutionStatus.Cancelled;
        if (!ended)
            demand!.Status = DemandExecutionStatus.RecoveryRequired;
        JourneyRuntimeRow? runtime = await DemandJourneyLookup.JourneyOf(dbContext, demandId)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (runtime is null) return;
        string reason = messageType + (ended ? BlockedJourneyRelease.OnEndedDemandSuffix : BlockedJourneyRelease.NotReconciledSuffix);
        runtime.Stage = JourneyRuntimeStage.Blocked;
        if (!BlockedJourneyRelease.IsUnreleasable(runtime.BlockReasonCode))
        {
            runtime.SetBlockReason(reason, timeProvider.GetUtcNow());
        }
        runtime.UpdatedAt = timeProvider.GetUtcNow();
        if (ended)
        {
            LogResultOnEndedDemandBlocked(
                logger ?? (ILogger)NullLogger.Instance,
                messageType, workflow.WorkflowId, demandId, demand?.Status.ToString() ?? "(missing)",
                runtime.BlockReasonCode ?? reason, null);
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
                throw new InboundMessageRejectedException(ServerReasonCodes.BusinessIdContentConflict,
                    "Recovery workflow id was replayed with different content.");
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
        // Not for a demand that has ended (control-server#505). Every ending -- an unload, PickupStopTermination's callers --
        // leaves none of its cargo on board by this server's account, so there is nothing for a session to recover; and a result
        // of one that did not reconcile could only block the journey under a code no release lifts.
        if (await dbContext.AcceptedDemands.AsNoTracking().AnyAsync(
                row => row.DemandId == demandId &&
                       (row.Status == DemandExecutionStatus.Succeeded || row.Status == DemandExecutionStatus.Cancelled),
                cancellationToken).ConfigureAwait(false))
        {
            return ServerReasonCodes.RecoveryDemandNotBlocked;
        }
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

    /// <summary>
    /// Whether a new forced mechanical recovery may be taken over a forced generation the vehicle has not reached, because
    /// every forced recovery above the generation it reports was closed by an administrator (control-server#484, F-b; the
    /// coordinator's decision of 2026-10-06).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What the fence guards.</b> Submitting a forced recovery advances the vehicle's generation and fences every command
    /// issued under an older one; the onboard refuses a command below the generation it holds, and raises its own only by
    /// binding a ForcedMechanicalRecoveryCommand. Until the vehicle reports the server's generation, the server cannot know it
    /// has taken the forced recovery in, so every action is refused FORCED_RECOVERY_GENERATION_STALE: a second action now
    /// could be judged on slots a forced recovery the vehicle is still carrying out has left physically unknown.
    /// </para>
    /// <para>
    /// <b>Why closing one leaves the fence shut for good.</b> An administrator's closing settles the forced command, so it is
    /// never replayed. A vehicle that never bound it -- gone before it arrived -- comes back reporting the generation below,
    /// and nothing can raise it but another forced command, which the fence itself refuses. Its hardware hold cannot be lifted
    /// either: the record needs the forced result, and the onboard offers it only for a forced recovery it carried out.
    /// </para>
    /// <para>
    /// <b>Why lifting it here does not weaken it.</b> Only for a new FORCED_MECHANICAL_RECOVERY, which advances the
    /// generation again and fences everything below -- the same move the fence exists to order. Only while the vehicle reports
    /// less than the server holds, and only when every forced workflow above what it reports was closed by an administrator:
    /// the command of each was settled, never replayed, and a vehicle reporting below its generation never bound it, so no
    /// forced recovery it might still be carrying out stands between the two. One that ended any other way -- a result arrived
    /// -- keeps the fence shut (control-server#493 is that case); so does a generation with no forced workflow behind it. The
    /// closed workflows become history under the new generation, and the new forced recovery goes the ordinary way: its result,
    /// the vehicle's report of the new generation, a hardware record. That is the cost: a person at the vehicle forces it
    /// again before it is released.
    /// </para>
    /// </remarks>
    private async Task<bool> ForcedFenceLiftedOverAdministratorClosingsAsync(
        SessionRecoveryRow connection,
        CancellationToken cancellationToken)
    {
        if (connection.ReportedForcedRecoveryGeneration >= connection.ForcedRecoveryGeneration)
            return false;
        string?[] outcomes = await dbContext.RecoveryWorkflows.AsNoTracking()
            .Where(row => row.AgvId == connection.AgvId &&
                          row.WorkflowType == "FORCED_MECHANICAL_RECOVERY" &&
                          row.ForcedRecoveryGeneration > connection.ReportedForcedRecoveryGeneration)
            .Select(row => row.Outcome)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        return outcomes.Length > 0 && outcomes.All(outcome => outcome == AdministratorClosedOutcome);
    }

    private static string? ValidateActionPreconditions(
        string action,
        ExceptionRecoverySessionRow session,
        SessionRecoveryRow connection,
        StationOperationRow? operation,
        bool forcedFenceLifted)
    {
        // The fence, lifted for a new forced recovery over administrator-closed generations alone
        // (ForcedFenceLiftedOverAdministratorClosingsAsync); every other action and case is refused as before.
        if (connection.ReportedForcedRecoveryGeneration != connection.ForcedRecoveryGeneration &&
            !(forcedFenceLifted && action == "FORCED_MECHANICAL_RECOVERY"))
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
            throw new InboundMessageRejectedException(ServerReasonCodes.BusinessIdContentConflict,
                "Recovery result demandId does not match its workflow.");
        if (workflow.SlotOperationAttemptId is not null &&
            payload.TryGetProperty("slotOperationAttemptId", out JsonElement attempt) &&
            OptionalString(attempt) != workflow.SlotOperationAttemptId)
            throw new InboundMessageRejectedException(ServerReasonCodes.BusinessIdContentConflict,
                "Recovery result operation identity does not match its workflow.");
        if (workflow.ExceptionRecoverySessionId is not null &&
            payload.TryGetProperty("exceptionRecoverySessionId", out JsonElement session) &&
            OptionalString(session) != workflow.ExceptionRecoverySessionId)
            throw new InboundMessageRejectedException(ServerReasonCodes.BusinessIdContentConflict,
                "Recovery result session does not match its workflow.");
        if (messageType == "FaultCargoRecoveryResult" &&
            RequiredString(payload, "handoffId") != workflow.HandoffId)
            throw new InboundMessageRejectedException(ServerReasonCodes.BusinessIdContentConflict,
                "Fault cargo handoff result does not match the authorized handoff.");
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

    /// <summary>
    /// Whether <paramref name="demandId"/> was delivered: its unload completed and it is
    /// <see cref="DemandExecutionStatus.Succeeded"/>, the one state no recovery result may end (control-server#481).
    /// </summary>
    private async Task<bool> DemandDeliveredAsync(string? demandId, CancellationToken cancellationToken) =>
        demandId is not null &&
        await dbContext.AcceptedDemands.AnyAsync(
            row => row.DemandId == demandId && row.Status == DemandExecutionStatus.Succeeded,
            cancellationToken).ConfigureAwait(false);

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

/// <summary>
/// What <see cref="OnboardRecoveryCoordinator.CloseSessionAwaitingResultAsync"/> decided: the refusal codes, empty when the
/// session closed, and the session it judged -- the one it closed, or the vehicle's open one it refused to close -- when there
/// was one (control-server#483).
/// </summary>
internal sealed record AdministratorCloseDecision(IReadOnlyList<string> Codes, string? ExceptionRecoverySessionId);
