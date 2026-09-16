using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Host.Transport;

public sealed class OnboardRecoveryCoordinator(
    ControlServerDbContext dbContext,
    WireToGateStore store,
    OnboardJourneyPublisher publisher,
    TimeProvider timeProvider,
    IConfiguration configuration)
{
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

        RecoveryResultEvidenceRow? existing = await dbContext.RecoveryResultEvidence
            .SingleOrDefaultAsync(row => row.MessageId == messageId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            if (existing.ContentHash != contentHash || existing.WorkflowId != workflowId ||
                existing.MessageType != messageType)
            {
                throw new ProtocolContentConflictException(
                    "Recovery result MessageId was replayed with different identity or content.");
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
            await ApplyCurrentResultAsync(messageType, payload, workflow, observedAt, cancellationToken)
                .ConfigureAwait(false);
            await AdvanceSessionAfterResultAsync(
                workflow,
                sessionGeneration,
                cancellationToken).ConfigureAwait(false);
        }
        // The result answers the command that asked for it, and a recovery command has no ack of its own
        // (LoadCompensationCommandAck is on the profile denylist). A workflow holding its first result takes
        // no other, so a command left pending can only be replayed into a later session to draw a duplicate
        // -- a failed workflow's was -- and every other one sat in each replay scan for the life of the
        // database, settled by hand if at all (8005-agv-control-server#31).
        if (workflow.CommandMessageId is not null)
        {
            await store.SettleAnsweredCommandAsync(workflow.CommandMessageId, observedAt, cancellationToken)
                .ConfigureAwait(false);
        }
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return DurableAck(messageType, messageId, agvId, sessionGeneration, contentHash);
    }

    public async Task ObserveOperationResultAsync(
        string slotOperationAttemptId,
        OperationResultDisposition disposition,
        CancellationToken cancellationToken)
    {
        RecoveryWorkflowRow? workflow = await dbContext.RecoveryWorkflows.SingleOrDefaultAsync(
            row => row.WorkflowType == "RESUME_AFTER_REPAIR" &&
                   row.SlotOperationAttemptId == slotOperationAttemptId &&
                   row.State != RecoveryWorkflowState.Reconciled &&
                   row.State != RecoveryWorkflowState.HistoricalOnly,
            cancellationToken).ConfigureAwait(false);
        if (workflow is null || disposition is OperationResultDisposition.Replay or OperationResultDisposition.HistoricalOnly)
            return;

        // ADR-cross-0058 decision 5: a determinate failure settles this workflow exactly as an
        // accepted result does. What a recovery workflow exists to obtain is a trustworthy account
        // of the physical world, and "known state, door locked, unlock output reset, nobody handed
        // the cargo over" is one. The business outcome is a failure; the uncertainty that called for
        // an administrator is gone, so the workflow is finished and the journey is unblocked.
        bool settled = disposition is OperationResultDisposition.Accepted
            or OperationResultDisposition.DeterminateFailure;
        workflow.State = settled
            ? RecoveryWorkflowState.Reconciled
            : RecoveryWorkflowState.RecoveryRequired;
        workflow.UpdatedAt = timeProvider.GetUtcNow();
        JourneyRuntimeRow? runtime = await JourneyForDemandAsync(workflow.DemandId, cancellationToken)
            .ConfigureAwait(false);
        if (runtime is not null && settled)
        {
            StationOperationRow operation = await dbContext.StationOperations.SingleAsync(
                row => row.SlotOperationAttemptId == slotOperationAttemptId,
                cancellationToken).ConfigureAwait(false);
            runtime.Stage = AwaitingResultOf(operation);
            runtime.BlockReasonCode = null;
            runtime.UpdatedAt = timeProvider.GetUtcNow();
        }
        if (workflow.ExceptionRecoverySessionId is not null)
        {
            ExceptionRecoverySessionRow session = await dbContext.ExceptionRecoverySessions.SingleAsync(
                row => row.ExceptionRecoverySessionId == workflow.ExceptionRecoverySessionId,
                cancellationToken).ConfigureAwait(false);
            session.State = settled ? "CLOSED" : "EXECUTING";
            session.Revision++;
            session.UpdatedAt = timeProvider.GetUtcNow();
            long sessionGeneration = await dbContext.SessionRecoveries.Where(row => row.AgvId == workflow.AgvId)
                .Select(row => row.SessionGeneration).SingleAsync(cancellationToken).ConfigureAwait(false);
            await QueueSessionSnapshotAsync(session, sessionGeneration, cancellationToken).ConfigureAwait(false);
        }
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
        if (messageType == "SublotSubmitted")
        {
            await RejectSublotForEndedJourneyAsync(root, cancellationToken).ConfigureAwait(false);
        }
        string agvId = RequiredString(root, "agvId");
        await SendPendingSessionSnapshotsAsync(agvId, cancellationToken).ConfigureAwait(false);
        foreach (string id in await PendingStopClosedSnapshotIdsAsync(agvId, cancellationToken).ConfigureAwait(false))
            await publisher.SendPersistedAsync(id, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Refuses a sublot entered against a journey that has already ended, instead of acknowledging it
    /// and never answering.
    /// </summary>
    /// <remarks>
    /// The runtime matches submissions only to the stop a running journey is waiting at. One answering a
    /// journey the server has since ended -- the station wait expired, a cancellation was authorised, a
    /// recovery ended the last demand -- was acknowledged and left in the inbox for good, and the vehicle
    /// sat on 「已提交，等待服务端下发仓位操作」. The stop-closed snapshots withdraw the entry at the vehicle,
    /// but a scan can still land before they arrive, or while a dropped connection holds them back
    /// (8005-agv-program#86). BR-013 wants a refusal to be explicit.
    ///
    /// Only an operation session whose every journey is Completed is judged. A running journey belongs to
    /// the runtime, the one party that knows which revision is open at its stop.
    /// </remarks>
    private async Task RejectSublotForEndedJourneyAsync(JsonElement root, CancellationToken cancellationToken)
    {
        JsonElement payload = root.GetProperty("payload");
        string agvId = RequiredString(root, "agvId");
        string operationSessionId = RequiredString(payload, "operationSessionId");
        JourneyRuntimeRow[] journeys = await dbContext.JourneyRuntimes.AsNoTracking()
            .Where(row => row.AgvId == agvId && row.OperationSessionId == operationSessionId)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        if (journeys.Length == 0 || journeys.Any(row => row.Stage != JourneyRuntimeStage.Completed))
        {
            return;
        }

        string submittedMessageId = RequiredString(root, "messageId");
        await publisher.PublishSublotRejectedAsync(
            StableGuid(submittedMessageId, "sublot-rejected-journey-ended"),
            submittedMessageId,
            agvId,
            root.GetProperty("sessionGeneration").GetInt64(),
            new SublotRejection(
                RequiredString(payload, "demandId"),
                operationSessionId,
                // The last revision the journey published: its cursor is the next one to take.
                Math.Max(0, journeys.Max(row => row.WorklistRevision) - 1),
                "WORKLIST_REVISION_STALE",
                "payload.worklistRevision",
                "本站已结束，这次录入不再处理。"),
            cancellationToken).ConfigureAwait(false);
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
        string[] stopClosedIds = await PendingStopClosedSnapshotIdsAsync(agvId, cancellationToken).ConfigureAwait(false);
        string[] pendingIds = commandIds.Concat(snapshotIds).Concat(stopClosedIds).ToArray();
        if (pendingIds.Length > 0)
            await publisher.ReplayPendingForSessionAsync(
                agvId, sessionGeneration, pendingIds.ToHashSet(StringComparer.Ordinal), cancellationToken)
                .ConfigureAwait(false);
    }

    /// <summary>
    /// The stop-closed snapshots the vehicle has not acknowledged, for the one journey they can still
    /// be true of: its latest, and only while no journey is running.
    /// </summary>
    /// <remarks>
    /// The runtime replays a journey's messages only while the journey is active, and a stop is closed
    /// exactly as its journey completes -- so a closing snapshot lost to a dropped connection was never
    /// sent again, and the vehicle kept the ended stop. Any older journey's snapshot would carry a
    /// revision below what the vehicle has adopted since and be refused as a regression, and a running
    /// journey's own snapshots already supersede it. The latest journey is the one holding the highest
    /// cursor: each is seeded past every earlier one (WireToGateStore.SeedSnapshotRevisionsAsync).
    /// </remarks>
    private async Task<string[]> PendingStopClosedSnapshotIdsAsync(
        string agvId,
        CancellationToken cancellationToken)
    {
        if (await dbContext.JourneyRuntimes.AsNoTracking()
                .AnyAsync(row => row.AgvId == agvId && row.Stage != JourneyRuntimeStage.Completed, cancellationToken)
                .ConfigureAwait(false))
            return [];
        string? journeyId = await dbContext.JourneyRuntimes.AsNoTracking()
            .Where(row => row.AgvId == agvId)
            .OrderByDescending(row => row.WorklistRevision)
            .Select(row => row.JourneyId)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (journeyId is null) return [];
        int[] sequences = await dbContext.JourneyStops.AsNoTracking()
            .Where(row => row.JourneyId == journeyId)
            .Select(row => row.Sequence)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        string[] candidates = sequences
            .Select(sequence => OnboardJourneyPublisher.StopClosedMessageIds(journeyId, sequence))
            .SelectMany(ids => new[] { ids.WorklistId, ids.PlanId })
            .ToArray();
        ProtocolOutboxRow[] rows = await dbContext.ProtocolOutbox.AsNoTracking()
            .Where(row => candidates.Contains(row.MessageId) && row.AcknowledgedAt == null && row.FencedAt == null)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        return rows.OrderBy(row => row.CreatedAt)
            .ThenBy(row => row.MessageType == "CurrentStopWorklistSnapshot" ? 0 : 1)
            .Select(row => row.MessageId)
            .ToArray();
    }

    /// <summary>
    /// Queues the stop-closed snapshots when a demand's termination has just completed its journey.
    /// Sending is left to <see cref="SendTriggeredCommandAsync"/>, which runs after the response to the
    /// request that ended it has been written.
    /// </summary>
    private async Task QueueStopClosedIfJourneyEndedAsync(
        JourneyRuntimeRow? runtime,
        string? demandId,
        long sessionGeneration,
        CancellationToken cancellationToken)
    {
        if (runtime?.Stage != JourneyRuntimeStage.Completed || demandId is null) return;
        JourneyDemandRow? membership = await dbContext.JourneyDemands.SingleOrDefaultAsync(
            row => row.DemandId == demandId, cancellationToken).ConfigureAwait(false);
        if (membership is null) return;
        JourneyStopRow? stop = await dbContext.JourneyStops.SingleOrDefaultAsync(
            row => row.JourneyId == runtime.JourneyId && row.Sequence == membership.StopSequence,
            cancellationToken).ConfigureAwait(false);
        if (stop is null) return;
        await publisher.QueueStopClosedAsync(runtime, stop, sessionGeneration, cancellationToken)
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
                problem = Problem("RECOVERY_AUTHENTICATION_REQUIRED", "payload.authenticationProof",
                    "The recovery administrator proof was not accepted.")
            });
        }

        ExceptionRecoverySessionRow? replay = await dbContext.ExceptionRecoverySessions
            .SingleOrDefaultAsync(row => row.RequestId == requestId, cancellationToken).ConfigureAwait(false);
        if (replay is not null)
        {
            if (replay.RequestContentHash != businessHash || replay.AgvId != agvId)
                throw new ProtocolContentConflictException("Recovery requestId was replayed with different content.");
            StationOperationRow? replayed = await FindScopedOperationAsync(replay, cancellationToken)
                .ConfigureAwait(false);
            return OpenedResponse(root, replay, replayed?.SlotOperationAttemptId);
        }
        ExceptionRecoverySessionRow? active = await dbContext.ExceptionRecoverySessions
            .SingleOrDefaultAsync(row => row.AgvId == agvId && row.State != "CLOSED", cancellationToken)
            .ConfigureAwait(false);
        if (active is not null)
        {
            return Response(root, "ExceptionRecoverySessionRejected", new
            {
                requestId,
                problem = Problem("RECOVERY_SESSION_ALREADY_OPEN", "payload.requestId",
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
        StationOperationRow? scoped = await FindScopedOperationAsync(row, cancellationToken)
            .ConfigureAwait(false);
        await QueueSessionSnapshotAsync(row, generation, cancellationToken).ConfigureAwait(false);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        _ = contentHash;
        _ = messageId;
        return OpenedResponse(root, row, scoped?.SlotOperationAttemptId);
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
            // The workflow's own copy, not a fresh lookup: a replayed acceptance has to answer with
            // what was accepted, and the station operation could have moved on since.
            return AcceptedAction(root, session, actionId, action, replay.SlotOperationAttemptId);
        }

        StationOperationRow? operation = await FindScopedOperationAsync(session, cancellationToken).ConfigureAwait(false);
        SessionRecoveryRow connection = await dbContext.SessionRecoveries.SingleAsync(
            row => row.AgvId == session.AgvId, cancellationToken).ConfigureAwait(false);
        string? actionProblem = ValidateActionPreconditions(action, session, connection, operation);
        if (actionProblem is not null)
            return RejectedAction(root, actionId, recoverySessionId, session.Revision, actionProblem);

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
        if (session is null || workflow is null ||
            role is not ("MAINTENANCE_ADMINISTRATOR" or "SYSTEM_ADMINISTRATOR") ||
            !slots.SequenceEqual(ParseSlots(session?.SlotsJson ?? "[]")))
        {
            outcome = "REJECTED";
            problem = Problem("RECOVERY_SCOPE_MISMATCH", "payload", "Hardware recovery record scope is not current.");
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
        AcceptedDemandRow? demand = await dbContext.AcceptedDemands.SingleOrDefaultAsync(
            row => row.DemandId == demandId, cancellationToken).ConfigureAwait(false);
        StationOperationRow? operation = attemptId is null ? null : await dbContext.StationOperations
            .SingleOrDefaultAsync(row => row.SlotOperationAttemptId == attemptId, cancellationToken)
            .ConfigureAwait(false);
        // A request this server has already authorised must be answered the same way for as long
        // as the peer keeps asking. Re-deciding from the demand status cannot do that on the
        // before-load path below, which cancels the demand as part of authorising it: the first
        // reply says AUTHORIZED, and every replay would then read Cancelled and say REJECTED.
        RecoveryWorkflowRow? priorAuthorization = await dbContext.RecoveryWorkflows.SingleOrDefaultAsync(
            row => row.WorkflowId == cancellationId && row.WorkflowType == "LOAD_CANCELLATION",
            cancellationToken).ConfigureAwait(false);
        bool authorized = priorAuthorization is not null ||
                          demand is not null && demand.Status == DemandExecutionStatus.Accepted &&
                          (operation is null || operation.DemandId == demandId &&
                           operation.OperationType == SlotOperationType.Load &&
                           operation.Status != StationOperationStatus.RecoveryRequired);
        int[] slots = priorAuthorization is not null
            ? ParseSlots(priorAuthorization.SlotsJson)
            : operation is null ? [] : ParseSlots(operation.TargetSlotsJson);
        if (authorized)
        {
            // A replay of an authorised cancellation for the same demand and attempt is answered from
            // the workflow, not re-checked byte for byte. The operator context carries verifiedAt,
            // which the peer takes afresh on a later press, and "this demand is not loaded" means
            // the same thing at either time. Treating that as a content conflict dropped the whole
            // session (8005-agv-onboard-hmi#89). A different demand or attempt under the same id is
            // still a conflict, and UpsertSimpleWorkflowAsync still throws for it.
            RecoveryWorkflowRow workflow =
                priorAuthorization is not null &&
                priorAuthorization.DemandId == demandId &&
                priorAuthorization.SlotOperationAttemptId == attemptId
                    ? priorAuthorization
                    : await UpsertSimpleWorkflowAsync(
                        cancellationId, "LOAD_CANCELLATION", root, contentHash, demandId, attemptId, slots,
                        cancellationToken).ConfigureAwait(false);
            // Cancelling at the pickup stop before any slot operation was commanded leaves nothing
            // to clear: no door was opened, so the peer has no emptiness to prove and sends no
            // LoadCancellationResult. Its schema could not carry one anyway -- slotResults is
            // minItems 1. The authorisation is therefore the whole handshake, and the termination
            // belongs here rather than in ApplyCurrentResultAsync.
            if (attemptId is null && workflow.State != RecoveryWorkflowState.Reconciled)
            {
                DateTimeOffset now = timeProvider.GetUtcNow();
                workflow.State = RecoveryWorkflowState.Reconciled;
                workflow.Outcome = "CANCELLED_BEFORE_LOAD";
                workflow.UpdatedAt = now;
                await store.CancelDemandBeforeLoadAsync(
                    demandId, "CANCELLED_BY_OPERATOR", now, cancellationToken)
                    .ConfigureAwait(false);
                await QueueStopClosedIfJourneyEndedAsync(
                        await JourneyForDemandAsync(demandId, cancellationToken).ConfigureAwait(false),
                        demandId,
                        root.GetProperty("sessionGeneration").GetInt64(),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        return Response(root, "LoadCancellationAuthorization", new
        {
            cancellationId,
            decision = authorized ? "AUTHORIZED" : "REJECTED",
            demandId,
            slotOperationAttemptId = attemptId,
            slots,
            problem = authorized ? null : Problem(
                "ACTION_NOT_ALLOWED_IN_STATE", "payload.demandId", "Load cancellation is not safe in the current state.")
        });
    }

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
                problem = Problem("ACTION_NOT_ALLOWED_IN_STATE", "payload", "Load compensation is not authorized.")
            });
        }
        if (workflow.CommandMessageId is null)
        {
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
            ExceptionRecoverySessionRow session = await dbContext.ExceptionRecoverySessions.SingleAsync(
                row => row.ExceptionRecoverySessionId == workflow.ExceptionRecoverySessionId,
                cancellationToken).ConfigureAwait(false);
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
        if (operation is null || operation.OperationType != SlotOperationType.Load ||
            operation.Status != StationOperationStatus.Committed ||
            !slots.All(ParseSlots(operation.TargetSlotsJson).Contains))
        {
            return Response(root, "LoadCorrectionRejected", new
            {
                correctionId,
                problem = Problem("ACTION_NOT_ALLOWED_IN_STATE", "payload", "Load correction is not authorized.")
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
                await publisher.QueueSlotOperationResumeCommandAsync(
                    commandId, session.AgvId, sessionGeneration,
                    new SlotOperationResumeAuthorization(
                        session.ExceptionRecoverySessionId,
                        workflow.WorkflowId,
                        workflow.DemandId!,
                        operation.SlotOperationAttemptId,
                        recovery.ProvenRecoveryCheckpoint!,
                        slots,
                        hash), cancellationToken).ConfigureAwait(false);
                BindCommand(workflow, commandId, "SlotOperationResumeCommand", hash);
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

    private async Task AdvanceSessionAfterResultAsync(
        RecoveryWorkflowRow workflow,
        long sessionGeneration,
        CancellationToken cancellationToken)
    {
        if (workflow.ExceptionRecoverySessionId is null) return;
        ExceptionRecoverySessionRow session = await dbContext.ExceptionRecoverySessions.SingleAsync(
            row => row.ExceptionRecoverySessionId == workflow.ExceptionRecoverySessionId,
            cancellationToken).ConfigureAwait(false);
        session.State = workflow.State == RecoveryWorkflowState.Reconciled ? "CLOSED" : "EXECUTING";
        session.Revision++;
        session.UpdatedAt = timeProvider.GetUtcNow();
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
        StationOperationRow? operation = session.DemandId is null
            ? null
            : await FindLatestOperationAsync(session.DemandId, cancellationToken).ConfigureAwait(false);
        string[] allowedActions = session.State == "OPEN"
            ? AllowedActions(session, operation)
            : [];
        VehicleBusinessBlockingFact[] blockingFacts = session.State == "CLOSED"
            ? []
            : [new VehicleBusinessBlockingFact(
                session.State == "OPEN" ? "RECOVERY_ACTION_REQUIRED" : "RECOVERY_RESULT_REQUIRED",
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
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
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
            await KeepDemandAndJourneyBlockedAsync(workflow.DemandId, messageType + "_NOT_RECONCILED", cancellationToken)
                .ConfigureAwait(false);
            return;
        }
        if (messageType == "ForcedMechanicalRecoveryResult")
        {
            workflow.State = RecoveryWorkflowState.RecoveryRequired;
            await KeepDemandAndJourneyBlockedAsync(
                workflow.DemandId, "FORCED_MECHANICAL_RECOVERY_REQUIRES_FRESH_RECONCILIATION", cancellationToken)
                .ConfigureAwait(false);
            return;
        }
        workflow.State = RecoveryWorkflowState.Reconciled;
        if (messageType == "LoadCorrectionResult") return;
        if (workflow.DemandId is null) return;
        AcceptedDemandRow demand = await dbContext.AcceptedDemands.SingleAsync(
            row => row.DemandId == workflow.DemandId, cancellationToken).ConfigureAwait(false);
        if (demand.Status == DemandExecutionStatus.Succeeded)
            throw new BusinessIdentityConflictException("A completed demand cannot be replaced by recovery termination.");
        demand.Status = DemandExecutionStatus.Cancelled;
        bool journeyComplete = await store.SettleDemandInJourneyAsync(
            workflow.DemandId, JourneyDemandState.Cancelled, observedAt, cancellationToken)
            .ConfigureAwait(false);
        StationOperationRow? terminated = null;
        if (workflow.SlotOperationAttemptId is not null)
        {
            terminated = await dbContext.StationOperations.SingleOrDefaultAsync(
                row => row.SlotOperationAttemptId == workflow.SlotOperationAttemptId,
                cancellationToken).ConfigureAwait(false);
            if (terminated is not null) terminated.Status = StationOperationStatus.Cancelled;
        }
        string terminalReasonCode = messageType == "FaultCargoRecoveryResult"
            ? "TERMINATED_BY_FAULT_CARGO_HANDOFF"
            : messageType == "LoadCompensationResult"
                ? "CANCELLED_BY_LOAD_COMPENSATION"
                : "CANCELLED_BY_OPERATOR";
        // The ban travels with the cancellation, not with the journey: ADR-cross-0047 and FR-004
        // require all three of these terminal reasons to bar the business key permanently. Without
        // it the same SUBLOT returns as a new DemandId on a later catalog poll and the vehicle is
        // dispatched back to a stop a human already settled.
        await store.SuppressTransportDemandAsync(
            demand.TransportDemandKey, demand.DemandId, terminalReasonCode, observedAt, cancellationToken)
            .ConfigureAwait(false);
        JourneyRuntimeRow? runtime = await JourneyForDemandAsync(workflow.DemandId, cancellationToken)
            .ConfigureAwait(false);
        if (runtime is not null)
        {
            // Terminating one demand does not end a journey still carrying others: their cargo is
            // aboard and their stops are ahead. The reason is recorded either way, so the block
            // reason still names what happened here.
            if (journeyComplete)
            {
                runtime.Stage = JourneyRuntimeStage.Completed;
            }
            else if (runtime.Stage == JourneyRuntimeStage.Blocked && terminated is not null)
            {
                // ...and it has to be able to carry on (8005-agv-program#47). A compensation or a
                // fault-cargo handoff is only ever authorised against a Blocked journey, and the
                // runtime does nothing with Blocked, so a journey whose other demands were still
                // aboard sat at the stop for good: L2 real-onboard-multi-demand-compensate.
                //
                // The journey goes back to waiting on the result of the operation it was blocked
                // on, exactly as ObserveOperationResultAsync hands it back after a resume. What
                // happens next is not decided here. That operation's demand is Cancelled now, so
                // the runtime reads it as a batch that closed the other way round and finishes it
                // through the one path every terminal end takes (#28, #39): another round at this
                // stop, the next stop or the gate for a load, the next demand to unload for an
                // unload. Deciding any of that here would write the same judgement a second time,
                // and marking the journey on from a message handler is what #39 learnt not to do.
                // The block reason is cleared by the runtime when it moves the journey on.
                runtime.Stage = AwaitingResultOf(terminated);
            }
            runtime.BlockReasonCode = terminalReasonCode;
            runtime.UpdatedAt = observedAt;
        }
        // The command this result terminates will never receive a result of its own, and only a
        // closed batch settles one. Left unsettled it is replayed into every later session carrying
        // a new session generation, which the peer refuses as a business id whose content changed --
        // the same failure CancelDemandBeforeLoadAsync settles the sublot entry request for. That is
        // the LoadBatch command for a load and the UnloadBatch command for an unload at the gate;
        // settling one that was already answered, or never sent, changes nothing.
        JourneyDemandRow? membership = await dbContext.JourneyDemands.SingleOrDefaultAsync(
            row => row.DemandId == workflow.DemandId, cancellationToken).ConfigureAwait(false);
        if (membership is not null)
        {
            await store.SettleAnsweredCommandAsync(
                membership.LoadCommandMessageId, observedAt, cancellationToken).ConfigureAwait(false);
            await store.SettleAnsweredCommandAsync(
                membership.UnloadCommandMessageId, observedAt, cancellationToken).ConfigureAwait(false);
        }
        // A compensation or a fault-cargo handoff that ends the last demand ends the journey where the
        // vehicle stands, and the vehicle has to be told that as much as for any other ending.
        if (journeyComplete)
        {
            long sessionGeneration = await dbContext.SessionRecoveries.Where(row => row.AgvId == workflow.AgvId)
                .Select(row => row.SessionGeneration).SingleAsync(cancellationToken).ConfigureAwait(false);
            await QueueStopClosedIfJourneyEndedAsync(runtime, workflow.DemandId, sessionGeneration, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The stage a journey waits in for the result of one station operation -- where it was when the
    /// operation's result sent it to Blocked, and where the runtime settles that operation.
    /// </summary>
    private static JourneyRuntimeStage AwaitingResultOf(StationOperationRow operation) =>
        operation.OperationType == SlotOperationType.Load
            ? JourneyRuntimeStage.AwaitingLoadResult
            : JourneyRuntimeStage.AwaitingUnloadResult;

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
        JourneyRuntimeRow? runtime = await JourneyForDemandAsync(demandId, cancellationToken)
            .ConfigureAwait(false);
        if (runtime is not null)
        {
            runtime.Stage = JourneyRuntimeStage.Blocked;
            runtime.BlockReasonCode = reason;
            runtime.UpdatedAt = timeProvider.GetUtcNow();
        }
    }

    /// <summary>
    /// The journey a demand belongs to. A demand no longer keys a journey directly -- ADR-cross-0057
    /// gave the journey its own identity and hung the demands off it -- so this hop through the
    /// membership row is what "the journey of this demand" means now.
    /// </summary>
    private async Task<JourneyRuntimeRow?> JourneyForDemandAsync(
        string? demandId,
        CancellationToken cancellationToken)
    {
        if (demandId is null) return null;
        JourneyDemandRow? membership = await dbContext.JourneyDemands.SingleOrDefaultAsync(
            row => row.DemandId == demandId, cancellationToken).ConfigureAwait(false);
        if (membership is null) return null;
        JourneyRuntimeRow? runtime = await dbContext.JourneyRuntimes.SingleOrDefaultAsync(
            row => row.JourneyId == membership.JourneyId, cancellationToken).ConfigureAwait(false);
        // Re-read it. This coordinator shares one DbContext with the whole TCP connection, and the
        // handshake has usually already loaded this journey into it, tracked; a query then hands back
        // that tracked copy, not the row. The journey's stage is written by the runtime worker from
        // its own context, so a vehicle that reconnected mid-load and then got blocked was refused
        // RECOVERY_DEMAND_NOT_BLOCKED against a journey the database held as Blocked
        // (8005-agv-program#40). Every caller here decides on the stage or rewrites it.
        if (runtime is not null)
        {
            await dbContext.Entry(runtime).ReloadAsync(cancellationToken).ConfigureAwait(false);
        }
        return runtime;
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
        JourneyRuntimeRow? runtime = await JourneyForDemandAsync(demandId, cancellationToken)
            .ConfigureAwait(false);
        if (runtime is null || runtime.AgvId != agvId ||
            runtime.Stage != JourneyRuntimeStage.Blocked)
        {
            return "RECOVERY_DEMAND_NOT_BLOCKED";
        }
        StationOperationRow? operation = await FindLatestOperationAsync(demandId, cancellationToken).ConfigureAwait(false);
        return operation is null || !slots.SequenceEqual(ParseSlots(operation.TargetSlotsJson))
            ? "RECOVERY_SCOPE_MISMATCH"
            : null;
    }

    private static string? ValidateActionScope(JsonElement payload, ExceptionRecoverySessionRow session)
    {
        int[] slots = RequiredSlots(payload, "slots");
        string? demandId = OptionalUuid(payload, "demandId");
        string operatorId = RequiredString(payload.GetProperty("operator"), "operatorId");
        return session.State == "CLOSED" ? "RECOVERY_SESSION_CLOSED" :
            RequiredUuid(payload, "eventId") != session.EventId ? "RECOVERY_EVENT_MISMATCH" :
            demandId != session.DemandId ? "RECOVERY_DEMAND_MISMATCH" :
            !slots.SequenceEqual(ParseSlots(session.SlotsJson)) ? "RECOVERY_SCOPE_MISMATCH" :
            operatorId != session.AdministratorId ? "RECOVERY_OPERATOR_MISMATCH" :
            session.SelectedAction is not null && session.SelectedAction != RequiredString(payload, "action")
                ? "RECOVERY_ACTION_ALREADY_SELECTED" : null;
    }

    private static string? ValidateActionPreconditions(
        string action,
        ExceptionRecoverySessionRow session,
        SessionRecoveryRow connection,
        StationOperationRow? operation)
    {
        if (connection.ReportedForcedRecoveryGeneration != connection.ForcedRecoveryGeneration)
            return "FORCED_RECOVERY_GENERATION_MISMATCH";
        if (session.DemandId is not null && operation is null) return "RECOVERY_OPERATION_NOT_FOUND";
        return action switch
        {
            "RESUME_AFTER_REPAIR" when operation?.Status != StationOperationStatus.RecoveryRequired =>
                "ACTION_NOT_ALLOWED_IN_STATE",
            "RESUME_AFTER_REPAIR" when connection.UnsettledSlotOperationAttemptId != operation.SlotOperationAttemptId ||
                                       connection.ProvenRecoveryCheckpoint is not
                                           ("PREPARED" or "ACTIVE_UNLOCK_SET" or "SAFE_FINISH_REACHED") =>
                "PROVEN_RECOVERY_CHECKPOINT_REQUIRED",
            "COMPENSATE_LOAD_ALL_EMPTY" when operation?.OperationType != SlotOperationType.Load ||
                                              operation.Status != StationOperationStatus.RecoveryRequired =>
                "ACTION_NOT_ALLOWED_IN_STATE",
            "FAULT_CARGO_HANDOFF" when session.DemandId is null => "ACTION_NOT_ALLOWED_IN_STATE",
            "FORCED_MECHANICAL_RECOVERY" => null,
            "RESUME_AFTER_REPAIR" or "COMPENSATE_LOAD_ALL_EMPTY" or "FAULT_CARGO_HANDOFF" => null,
            _ => "ACTION_NOT_ALLOWED_IN_STATE"
        };
    }

    private async Task<StationOperationRow?> FindScopedOperationAsync(
        ExceptionRecoverySessionRow session,
        CancellationToken cancellationToken) =>
        session.DemandId is null ? null : await FindLatestOperationAsync(session.DemandId, cancellationToken)
            .ConfigureAwait(false);

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

    /// <summary>
    /// The vehicle cannot name the attempt it is recovering: the load it believes succeeded cleared
    /// its own copy of that identity (control-server issue #5), and every recovery request it has to
    /// send afterwards -- LoadCompensationRequested above all -- carries slotOperationAttemptId as a
    /// required field. The server has always known the value; from protocol-v0.3.0 it says so, in
    /// this response and in the two below it. Null where the session is not scoped to a demand, or
    /// where no station operation was ever commanded for it.
    /// </summary>
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
            acceptedAction = action,
            slotOperationAttemptId,
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
        ProtocolEnvelope.Serialize(
            "DurableAck",
            Guid.NewGuid().ToString("D"),
            acceptedMessageId,
            agvId,
            generation,
            // Read before durablyAcceptedAt, as it was when both sat in one object initializer.
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
}
