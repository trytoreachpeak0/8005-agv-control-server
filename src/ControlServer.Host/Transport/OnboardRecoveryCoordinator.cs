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
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
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

        workflow.State = disposition == OperationResultDisposition.Accepted
            ? RecoveryWorkflowState.Reconciled
            : RecoveryWorkflowState.RecoveryRequired;
        workflow.UpdatedAt = timeProvider.GetUtcNow();
        JourneyRuntimeRow? runtime = workflow.DemandId is null
            ? null
            : await dbContext.JourneyRuntimes.SingleOrDefaultAsync(
                row => row.DemandId == workflow.DemandId, cancellationToken).ConfigureAwait(false);
        if (runtime is not null && disposition == OperationResultDisposition.Accepted)
        {
            StationOperationRow operation = await dbContext.StationOperations.SingleAsync(
                row => row.SlotOperationAttemptId == slotOperationAttemptId,
                cancellationToken).ConfigureAwait(false);
            runtime.Stage = operation.OperationType == SlotOperationType.Load
                ? JourneyRuntimeStage.AwaitingLoadResult
                : JourneyRuntimeStage.AwaitingUnloadResult;
            runtime.BlockReasonCode = null;
            runtime.UpdatedAt = timeProvider.GetUtcNow();
        }
        if (workflow.ExceptionRecoverySessionId is not null)
        {
            ExceptionRecoverySessionRow session = await dbContext.ExceptionRecoverySessions.SingleAsync(
                row => row.ExceptionRecoverySessionId == workflow.ExceptionRecoverySessionId,
                cancellationToken).ConfigureAwait(false);
            session.State = disposition == OperationResultDisposition.Accepted ? "CLOSED" : "EXECUTING";
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
        await SendPendingSessionSnapshotsAsync(
            RequiredString(root, "agvId"), cancellationToken).ConfigureAwait(false);
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
        string[] pendingIds = commandIds.Concat(snapshotIds).ToArray();
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
            return OpenedResponse(root, replay);
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
        await QueueSessionSnapshotAsync(row, generation, cancellationToken).ConfigureAwait(false);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        _ = contentHash;
        _ = messageId;
        return OpenedResponse(root, row);
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
            return AcceptedAction(root, session, actionId, action);
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
        return AcceptedAction(root, session, actionId, action);
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
        bool authorized = demand is not null && demand.Status == DemandExecutionStatus.Accepted &&
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
            string hash = CommandHash(actionId, workflow.DemandId!, workflow.SlotOperationAttemptId!, workflow.SlotsJson);
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
            string hash = CommandHash(correctionId, demandId, attemptId, JsonSerializer.Serialize(slots));
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
        string hash = CommandHash(
            workflow.WorkflowId,
            workflow.DemandId ?? string.Empty,
            operation?.SlotOperationAttemptId ?? string.Empty,
            workflow.SlotsJson,
            workflow.ForcedRecoveryGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture));
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
        VehicleDispatchLeaseRow lease = await dbContext.VehicleDispatchLeases.SingleAsync(
            row => row.DemandId == workflow.DemandId, cancellationToken).ConfigureAwait(false);
        lease.ReleasedAt ??= observedAt;
        if (workflow.SlotOperationAttemptId is not null)
        {
            StationOperationRow? operation = await dbContext.StationOperations.SingleOrDefaultAsync(
                row => row.SlotOperationAttemptId == workflow.SlotOperationAttemptId,
                cancellationToken).ConfigureAwait(false);
            if (operation is not null) operation.Status = StationOperationStatus.Cancelled;
        }
        JourneyRuntimeRow? runtime = await dbContext.JourneyRuntimes.SingleOrDefaultAsync(
            row => row.DemandId == workflow.DemandId, cancellationToken).ConfigureAwait(false);
        if (runtime is not null)
        {
            runtime.Stage = JourneyRuntimeStage.Completed;
            runtime.BlockReasonCode = messageType == "FaultCargoRecoveryResult"
                ? "TERMINATED_BY_FAULT_CARGO_HANDOFF"
                : messageType == "LoadCompensationResult"
                    ? "CANCELLED_BY_LOAD_COMPENSATION"
                    : "CANCELLED_BY_OPERATOR";
            runtime.UpdatedAt = observedAt;
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
        JourneyRuntimeRow? runtime = await dbContext.JourneyRuntimes.SingleOrDefaultAsync(
            row => row.DemandId == demandId, cancellationToken).ConfigureAwait(false);
        if (runtime is not null)
        {
            runtime.Stage = JourneyRuntimeStage.Blocked;
            runtime.BlockReasonCode = reason;
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
        JourneyRuntimeRow? runtime = await dbContext.JourneyRuntimes.SingleOrDefaultAsync(
            row => row.DemandId == demandId && row.AgvId == agvId && row.Stage == JourneyRuntimeStage.Blocked,
            cancellationToken).ConfigureAwait(false);
        if (runtime is null) return "RECOVERY_DEMAND_NOT_BLOCKED";
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

    private string OpenedResponse(JsonElement request, ExceptionRecoverySessionRow session) =>
        Response(request, "ExceptionRecoverySessionOpened", new
        {
            requestId = session.RequestId,
            exceptionRecoverySessionId = session.ExceptionRecoverySessionId,
            openedAt = session.OpenedAt,
            eventId = session.EventId,
            demandId = session.DemandId,
            slots = ParseSlots(session.SlotsJson),
            recoverySessionRevision = session.Revision
        });

    private string AcceptedAction(
        JsonElement request,
        ExceptionRecoverySessionRow session,
        string actionId,
        string action) =>
        Response(request, "RecoveryActionAccepted", new
        {
            recoveryActionId = actionId,
            exceptionRecoverySessionId = session.ExceptionRecoverySessionId,
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
        JsonSerializer.Serialize(new
        {
            protocolVersion = ProtocolCandidateIdentity.ProtocolVersion,
            profileId = ProtocolCandidateIdentity.ProfileId,
            protocolReleaseVersion = ProtocolCandidateIdentity.ReleaseVersion,
            protocolReleaseManifestSha256 = ProtocolCandidateIdentity.ManifestSha256,
            messageType,
            messageId = Guid.NewGuid().ToString("D"),
            correlationId = RequiredString(request, "messageId"),
            agvId = RequiredString(request, "agvId"),
            sessionGeneration = request.GetProperty("sessionGeneration").GetInt64(),
            sentAt = timeProvider.GetUtcNow(),
            payload
        }, SerializerOptions);

    private string DurableAck(
        string acceptedMessageType,
        string acceptedMessageId,
        string agvId,
        long generation,
        string contentHash) =>
        JsonSerializer.Serialize(new
        {
            protocolVersion = ProtocolCandidateIdentity.ProtocolVersion,
            profileId = ProtocolCandidateIdentity.ProfileId,
            protocolReleaseVersion = ProtocolCandidateIdentity.ReleaseVersion,
            protocolReleaseManifestSha256 = ProtocolCandidateIdentity.ManifestSha256,
            messageType = "DurableAck",
            messageId = Guid.NewGuid().ToString("D"),
            correlationId = acceptedMessageId,
            agvId,
            sessionGeneration = generation,
            sentAt = timeProvider.GetUtcNow(),
            payload = new
            {
                acceptedMessageId,
                acceptedMessageType,
                acceptedContentSha256 = contentHash,
                durablyAcceptedAt = timeProvider.GetUtcNow()
            }
        }, SerializerOptions);

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

    private static string CommandHash(params string[] parts) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|', parts))))
            .ToLowerInvariant();

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
