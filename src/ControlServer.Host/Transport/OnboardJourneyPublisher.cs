using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;

namespace ControlServer.Host.Transport;

public sealed class OnboardJourneyPublisher(
    WireToGateStore store,
    IOnboardPeer peer,
    TimeProvider timeProvider)
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] SublotEntryMethods = ["SCANNER", "KEYBOARD"];
    private static readonly string[] LoadCorrectionSequence = ["EMPTY", "OCCUPIED"];

    public async Task ReplayPendingForSessionAsync(
        string agvId,
        long sessionGeneration,
        IReadOnlySet<string> allowedMessageIds,
        CancellationToken cancellationToken)
    {
        ProtocolOutboxRow[] pending = await store.GetPendingOutboundEnvelopesAsync(agvId, cancellationToken)
            .ConfigureAwait(false);
        foreach (ProtocolOutboxRow row in pending.Where(row => allowedMessageIds.Contains(row.MessageId)))
        {
            JsonObject envelope = JsonNode.Parse(row.PayloadJson)?.AsObject()
                ?? throw new InvalidDataException("Persisted outbound envelope is empty.");
            long storedGeneration = envelope["sessionGeneration"]?.GetValue<long>()
                ?? throw new InvalidDataException("Persisted outbound envelope has no session generation.");
            if (sessionGeneration < storedGeneration)
            {
                throw new StaleSessionGenerationException(
                    "Cannot replay an outbound message into an older session generation.");
            }
            if (storedGeneration == sessionGeneration)
            {
                await peer.SendAsync(
                    Encoding.UTF8.GetBytes(row.PayloadJson + "\n"),
                    cancellationToken).ConfigureAwait(false);
                continue;
            }
            DateTimeOffset sentAt = timeProvider.GetUtcNow();
            envelope["sessionGeneration"] = sessionGeneration;
            envelope["sentAt"] = sentAt;
            string wire = envelope.ToJsonString(SerializerOptions);
            ProtocolOutboxRow current = await store.QueueOutboundEnvelopeAsync(
                row.MessageId,
                row.MessageType,
                wire,
                sentAt,
                cancellationToken).ConfigureAwait(false);
            await peer.SendAsync(
                Encoding.UTF8.GetBytes(current.PayloadJson + "\n"),
                cancellationToken).ConfigureAwait(false);
        }
    }

    public Task PublishVehicleBusinessStateAsync(
        string messageId,
        string agvId,
        long sessionGeneration,
        VehicleBusinessProjection projection,
        CancellationToken cancellationToken) =>
        PublishStampedSnapshotAsync(
            "VehicleBusinessStateSnapshot",
            messageId,
            agvId,
            sessionGeneration,
            // observedAt comes from the envelope's frozen sentAt rather than a fresh clock read.
            // This snapshot keeps one deterministic messageId per journey stage, so a payload
            // carrying the current time differs on every re-publish and is refused as a semantic
            // conflict -- which left an arrived journey looping between reconnects, never able to
            // re-send the snapshot the peer was waiting on.
            sentAt => new
            {
                vehicleBusinessStateRevision = projection.Revision,
                readiness = projection.Readiness,
                projection.ManualChargingHold,
                projection.BatteryState,
                blockingFacts = projection.BlockingFacts.Select(fact => new
                {
                    fact.ReasonCode,
                    fact.SubjectType,
                    fact.SubjectId
                }),
                ObservedAt = sentAt
            },
            cancellationToken);

    public Task PublishSublotEntryRequestAsync(
        string messageId,
        string agvId,
        long sessionGeneration,
        SublotEntryRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateUuid(request.DemandId, nameof(request.DemandId));
        ValidateUuid(request.OperationSessionId, nameof(request.OperationSessionId));
        ArgumentException.ThrowIfNullOrWhiteSpace(request.StationId);
        ArgumentOutOfRangeException.ThrowIfNegative(request.WorklistRevision);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ExpectedSublot);

        return PublishEnvelopeAsync(
            "SublotEntryRequested",
            messageId,
            correlationId: null,
            agvId,
            sessionGeneration,
            new
            {
                request.DemandId,
                request.OperationSessionId,
                request.StationId,
                request.WorklistRevision,
                request.ExpectedSublot,
                entryMethods = SublotEntryMethods,
                expiresOnRevisionChange = true
            },
            cancellationToken);
    }

    public Task PublishSlotOperationCommandAsync(
        string messageId,
        string agvId,
        long sessionGeneration,
        SlotOperationCommand command,
        CancellationToken cancellationToken,
        string? admissionStationId = null,
        string? admissionTaskType = null)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateUuid(command.DemandId, nameof(command.DemandId));
        ArgumentException.ThrowIfNullOrWhiteSpace(command.SublotId);
        ValidateUuid(command.OperationSessionId, nameof(command.OperationSessionId));
        ValidateUuid(command.SlotOperationAttemptId, nameof(command.SlotOperationAttemptId));
        ArgumentOutOfRangeException.ThrowIfNegative(command.ForcedRecoveryGeneration);
        ValidateSha256(command.CommandContentSha256, nameof(command.CommandContentSha256));
        ValidateSlots(command.Slots);

        string operationType;
        string expectedFinalPhysicalState;
        string? correlationId;
        switch (command.OperationType)
        {
            case SlotOperationType.Load:
                ValidateUuid(command.CorrelationId, nameof(command.CorrelationId));
                operationType = "LOAD";
                expectedFinalPhysicalState = "OCCUPIED";
                correlationId = command.CorrelationId;
                break;
            case SlotOperationType.Unload:
                if (command.CorrelationId is not null)
                {
                    throw new InvalidDataException("UNLOAD SlotOperationCommand must not have a correlationId.");
                }
                operationType = "UNLOAD";
                expectedFinalPhysicalState = "EMPTY";
                correlationId = null;
                break;
            default:
                throw new InvalidDataException("Slot operation type is not supported.");
        }

        return PublishSlotOperationEnvelopeAsync(
            "SlotOperationCommand",
            messageId,
            correlationId,
            agvId,
            sessionGeneration,
            new
            {
                command.DemandId,
                command.OperationSessionId,
                command.SlotOperationAttemptId,
                operationType,
                command.Slots,
                expectedBasketCount = command.Slots.Count,
                expectedFinalPhysicalState,
                command.CommandContentSha256
            },
            command,
            admissionStationId,
            admissionTaskType,
            cancellationToken);
    }

    public Task PublishPreDepartureSafetyCheckAsync(
        string messageId,
        string agvId,
        long sessionGeneration,
        PreDepartureSafetyCheckCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateUuid(command.PreDepartureSafetyCheckId, nameof(command.PreDepartureSafetyCheckId));
        ValidateUuid(command.DemandId, nameof(command.DemandId));
        ValidateUuid(command.MovementLegId, nameof(command.MovementLegId));
        ArgumentOutOfRangeException.ThrowIfNegative(command.ExpectedSafetyStateVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.TargetStationId);

        return PublishEnvelopeAsync(
            "PreDepartureSafetyCheck",
            messageId,
            correlationId: null,
            agvId,
            sessionGeneration,
            new
            {
                command.PreDepartureSafetyCheckId,
                command.DemandId,
                command.MovementLegId,
                command.ExpectedSafetyStateVersion,
                command.TargetStationId
            },
            cancellationToken);
    }

    public Task<ProtocolOutboxRow> QueueSlotOperationResumeCommandAsync(
        string messageId,
        string agvId,
        long sessionGeneration,
        SlotOperationResumeAuthorization command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateUuid(command.ExceptionRecoverySessionId, nameof(command.ExceptionRecoverySessionId));
        ValidateUuid(command.RecoveryActionId, nameof(command.RecoveryActionId));
        ValidateUuid(command.DemandId, nameof(command.DemandId));
        ValidateUuid(command.SlotOperationAttemptId, nameof(command.SlotOperationAttemptId));
        if (command.ProvenRecoveryCheckpoint is not ("PREPARED" or "ACTIVE_UNLOCK_SET" or "SAFE_FINISH_REACHED"))
            throw new InvalidDataException("Proven recovery checkpoint is not allowed by the protocol.");
        ValidateSlots(command.Slots);
        ValidateSha256(command.CommandContentSha256, nameof(command.CommandContentSha256));
        return QueueEnvelopeAsync(
            "SlotOperationResumeCommand", messageId, null, agvId, sessionGeneration,
            new
            {
                command.ExceptionRecoverySessionId,
                command.RecoveryActionId,
                command.DemandId,
                command.SlotOperationAttemptId,
                command.ProvenRecoveryCheckpoint,
                command.Slots,
                command.CommandContentSha256
            }, cancellationToken);
    }

    public Task<ProtocolOutboxRow> QueueExceptionRecoverySessionSnapshotAsync(
        string messageId,
        string agvId,
        long sessionGeneration,
        ExceptionRecoverySessionProjection projection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(projection);
        ValidateUuid(projection.ExceptionRecoverySessionId, nameof(projection.ExceptionRecoverySessionId));
        ArgumentOutOfRangeException.ThrowIfNegative(projection.RecoverySessionRevision);
        if (projection.State is not ("OPEN" or "ACTION_SELECTED" or "EXECUTING" or "CLOSED"))
            throw new InvalidDataException("Recovery session state is not allowed by the protocol.");
        if (projection.AdministratorRole is not ("MAINTENANCE_ADMINISTRATOR" or "SYSTEM_ADMINISTRATOR"))
            throw new InvalidDataException("Recovery administrator role is not allowed by the protocol.");
        ValidateUuid(projection.EventId, nameof(projection.EventId));
        if (projection.DemandId is not null) ValidateUuid(projection.DemandId, nameof(projection.DemandId));
        ValidateSlots(projection.Slots);
        return QueueEnvelopeAsync(
            "ExceptionRecoverySessionSnapshot", messageId, null, agvId, sessionGeneration,
            new
            {
                projection.ExceptionRecoverySessionId,
                projection.RecoverySessionRevision,
                projection.State,
                projection.AdministratorId,
                projection.AdministratorRole,
                projection.EventId,
                projection.DemandId,
                projection.Slots,
                projection.SelectedAction,
                projection.AllowedActions,
                blockingFacts = projection.BlockingFacts.Select(fact => new
                {
                    fact.ReasonCode,
                    fact.SubjectType,
                    fact.SubjectId
                })
            }, cancellationToken);
    }

    public Task<ProtocolOutboxRow> QueueLoadCompensationCommandAsync(
        string messageId,
        string agvId,
        long sessionGeneration,
        LoadCompensationAuthorizationCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateUuid(command.RecoveryActionId, nameof(command.RecoveryActionId));
        ValidateUuid(command.ExceptionRecoverySessionId, nameof(command.ExceptionRecoverySessionId));
        ValidateUuid(command.DemandId, nameof(command.DemandId));
        ValidateUuid(command.SlotOperationAttemptId, nameof(command.SlotOperationAttemptId));
        ValidateSlots(command.Slots);
        ValidateSha256(command.CommandContentSha256, nameof(command.CommandContentSha256));
        return QueueEnvelopeAsync(
            "LoadCompensationCommand", messageId, null, agvId, sessionGeneration,
            new
            {
                command.RecoveryActionId,
                command.ExceptionRecoverySessionId,
                command.DemandId,
                command.SlotOperationAttemptId,
                command.Slots,
                expectedFinalPhysicalState = "EMPTY",
                command.CommandContentSha256
            }, cancellationToken);
    }

    public Task<ProtocolOutboxRow> QueueLoadCorrectionCommandAsync(
        string messageId,
        string agvId,
        long sessionGeneration,
        LoadCorrectionAuthorizationCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateUuid(command.CorrectionId, nameof(command.CorrectionId));
        ValidateUuid(command.DemandId, nameof(command.DemandId));
        ValidateUuid(command.SlotOperationAttemptId, nameof(command.SlotOperationAttemptId));
        ValidateSlots(command.Slots);
        ValidateSha256(command.CommandContentSha256, nameof(command.CommandContentSha256));
        return QueueEnvelopeAsync(
            "LoadCorrectionCommand", messageId, null, agvId, sessionGeneration,
            new
            {
                command.CorrectionId,
                command.DemandId,
                command.SlotOperationAttemptId,
                command.Slots,
                expectedSequence = LoadCorrectionSequence,
                command.CommandContentSha256
            }, cancellationToken);
    }

    public Task<ProtocolOutboxRow> QueueFaultCargoRecoveryCommandAsync(
        string messageId,
        string agvId,
        long sessionGeneration,
        FaultCargoRecoveryAuthorizationCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateUuid(command.ExceptionRecoverySessionId, nameof(command.ExceptionRecoverySessionId));
        ValidateUuid(command.RecoveryActionId, nameof(command.RecoveryActionId));
        ValidateUuid(command.DemandId, nameof(command.DemandId));
        ValidateUuid(command.HandoffId, nameof(command.HandoffId));
        ValidateSlots(command.Slots);
        ValidateSha256(command.CommandContentSha256, nameof(command.CommandContentSha256));
        return QueueEnvelopeAsync(
            "FaultCargoRecoveryCommand", messageId, null, agvId, sessionGeneration,
            new
            {
                command.ExceptionRecoverySessionId,
                command.RecoveryActionId,
                command.DemandId,
                command.Slots,
                command.HandoffId,
                command.CommandContentSha256
            }, cancellationToken);
    }

    public Task<ProtocolOutboxRow> QueueForcedMechanicalRecoveryCommandAsync(
        string messageId,
        string agvId,
        long sessionGeneration,
        ForcedMechanicalRecoveryAuthorizationCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateUuid(command.ExceptionRecoverySessionId, nameof(command.ExceptionRecoverySessionId));
        ValidateUuid(command.RecoveryActionId, nameof(command.RecoveryActionId));
        if (command.DemandId is not null) ValidateUuid(command.DemandId, nameof(command.DemandId));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(command.ForcedRecoveryGeneration);
        ValidateSlots(command.Slots);
        ValidateSha256(command.CommandContentSha256, nameof(command.CommandContentSha256));
        return QueueEnvelopeAsync(
            "ForcedMechanicalRecoveryCommand", messageId, null, agvId, sessionGeneration,
            new
            {
                command.ExceptionRecoverySessionId,
                command.RecoveryActionId,
                command.DemandId,
                command.ForcedRecoveryGeneration,
                command.Slots,
                command.CommandContentSha256
            }, cancellationToken);
    }

    public async Task SendPersistedAsync(string messageId, CancellationToken cancellationToken)
    {
        ProtocolOutboxRow row = await store.FindOutboundEnvelopeAsync(messageId, cancellationToken)
            .ConfigureAwait(false) ?? throw new KeyNotFoundException($"Outbound message '{messageId}' does not exist.");
        if (row.AcknowledgedAt is not null || row.FencedAt is not null)
        {
            return;
        }
        await peer.SendAsync(Encoding.UTF8.GetBytes(row.PayloadJson + "\n"), cancellationToken)
            .ConfigureAwait(false);
    }

    public Task PublishCurrentStopWorklistAsync(
        string messageId,
        string agvId,
        long sessionGeneration,
        CurrentStopWorklistProjection projection,
        CancellationToken cancellationToken) =>
        PublishSnapshotAsync(
            "CurrentStopWorklistSnapshot",
            messageId,
            agvId,
            sessionGeneration,
            new
            {
                projection.StationId,
                worklistRevision = projection.Revision,
                projection.OperationSessionId,
                items = projection.Items.Select(item => new
                {
                    item.DemandId,
                    item.TransportDemandKey,
                    item.Sublot,
                    item.WorkType,
                    item.StopRole,
                    item.ExpectedBasketCount
                })
            },
            cancellationToken);

    public Task PublishUpcomingStopPlanAsync(
        string messageId,
        string agvId,
        long sessionGeneration,
        UpcomingStopPlanProjection projection,
        CancellationToken cancellationToken) =>
        PublishSnapshotAsync(
            "UpcomingStopPlanSnapshot",
            messageId,
            agvId,
            sessionGeneration,
            new
            {
                planRevision = projection.Revision,
                projection.DemandId,
                legs = projection.Legs.Select(leg => new
                {
                    leg.MovementLegId,
                    leg.LegType,
                    leg.Sequence,
                    leg.StationId,
                    leg.MapId,
                    leg.State
                })
            },
            cancellationToken);

    private async Task PublishSnapshotAsync(
        string messageType,
        string messageId,
        string agvId,
        long sessionGeneration,
        object payload,
        CancellationToken cancellationToken) =>
        await PublishStampedSnapshotAsync(
            messageType, messageId, agvId, sessionGeneration, _ => payload, cancellationToken)
            .ConfigureAwait(false);

    /// <summary>Builds the payload from the envelope's frozen sentAt, so re-publishes reproduce it.</summary>
    private async Task PublishStampedSnapshotAsync(
        string messageType,
        string messageId,
        string agvId,
        long sessionGeneration,
        Func<DateTimeOffset, object> payloadFactory,
        CancellationToken cancellationToken) =>
        await PublishStampedEnvelopeAsync(
            messageType,
            messageId,
            correlationId: null,
            agvId,
            sessionGeneration,
            payloadFactory,
            cancellationToken).ConfigureAwait(false);

    private async Task PublishEnvelopeAsync(
        string messageType,
        string messageId,
        string? correlationId,
        string agvId,
        long sessionGeneration,
        object payload,
        CancellationToken cancellationToken) =>
        await PublishStampedEnvelopeAsync(
            messageType, messageId, correlationId, agvId, sessionGeneration, _ => payload, cancellationToken)
            .ConfigureAwait(false);

    private async Task PublishStampedEnvelopeAsync(
        string messageType,
        string messageId,
        string? correlationId,
        string agvId,
        long sessionGeneration,
        Func<DateTimeOffset, object> payloadFactory,
        CancellationToken cancellationToken)
    {
        ProtocolOutboxRow stored = await QueueEnvelopeAsync(
            messageType, messageId, correlationId, agvId, sessionGeneration, payloadFactory, cancellationToken)
            .ConfigureAwait(false);
        if (stored.AcknowledgedAt is null && stored.FencedAt is null)
            await peer.SendAsync(
                Encoding.UTF8.GetBytes(stored.PayloadJson + "\n"),
                cancellationToken).ConfigureAwait(false);
    }

    private Task<ProtocolOutboxRow> QueueEnvelopeAsync(
        string messageType,
        string messageId,
        string? correlationId,
        string agvId,
        long sessionGeneration,
        object payload,
        CancellationToken cancellationToken) =>
        QueueEnvelopeAsync(
            messageType, messageId, correlationId, agvId, sessionGeneration, _ => payload, cancellationToken);

    private async Task<ProtocolOutboxRow> QueueEnvelopeAsync(
        string messageType,
        string messageId,
        string? correlationId,
        string agvId,
        long sessionGeneration,
        Func<DateTimeOffset, object> payloadFactory,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(agvId);
        ValidateUuid(messageId, nameof(messageId));
        ArgumentOutOfRangeException.ThrowIfNegative(sessionGeneration);
        ProtocolOutboxRow? existing = await store.FindOutboundEnvelopeAsync(messageId, cancellationToken)
            .ConfigureAwait(false);
        DateTimeOffset sentAt = existing?.CreatedAt ?? timeProvider.GetUtcNow();
        object payload = payloadFactory(sentAt);
        string candidateWire = JsonSerializer.Serialize(new
        {
            protocolVersion = ProtocolCandidateIdentity.ProtocolVersion,
            profileId = ProtocolCandidateIdentity.ProfileId,
            protocolReleaseVersion = ProtocolCandidateIdentity.ReleaseVersion,
            protocolReleaseManifestSha256 = ProtocolCandidateIdentity.ManifestSha256,
            messageType,
            messageId,
            correlationId,
            agvId,
            sessionGeneration,
            sentAt,
            payload
        }, SerializerOptions);
        return await store.QueueOutboundEnvelopeAsync(
            messageId, messageType, candidateWire, sentAt, cancellationToken).ConfigureAwait(false);
    }

    private async Task PublishSlotOperationEnvelopeAsync(
        string messageType,
        string messageId,
        string? correlationId,
        string agvId,
        long sessionGeneration,
        object payload,
        SlotOperationCommand command,
        string? admissionStationId,
        string? admissionTaskType,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(agvId);
        ValidateUuid(messageId, nameof(messageId));
        ArgumentOutOfRangeException.ThrowIfNegative(sessionGeneration);

        ProtocolOutboxRow? existing = await store.FindOutboundEnvelopeAsync(messageId, cancellationToken)
            .ConfigureAwait(false);
        DateTimeOffset sentAt = existing?.CreatedAt ?? timeProvider.GetUtcNow();
        string candidateWire = JsonSerializer.Serialize(new
        {
            protocolVersion = ProtocolCandidateIdentity.ProtocolVersion,
            profileId = ProtocolCandidateIdentity.ProfileId,
            protocolReleaseVersion = ProtocolCandidateIdentity.ReleaseVersion,
            protocolReleaseManifestSha256 = ProtocolCandidateIdentity.ManifestSha256,
            messageType,
            messageId,
            correlationId,
            agvId,
            sessionGeneration,
            sentAt,
            payload
        }, SerializerOptions);
        ProtocolOutboxRow stored = await store.PrepareSlotOperationAsync(
            new StationOperationPlan(
                command.SlotOperationAttemptId,
                command.DemandId,
                command.SublotId,
                command.Slots,
                command.OperationType,
                command.ForcedRecoveryGeneration,
                command.CommandContentSha256,
                sentAt,
                admissionStationId,
                admissionTaskType),
            messageId,
            candidateWire,
            cancellationToken).ConfigureAwait(false);
        if (stored.AcknowledgedAt is not null)
        {
            return;
        }
        await peer.SendAsync(
            Encoding.UTF8.GetBytes(stored.PayloadJson + "\n"),
            cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateUuid(string? value, string parameterName)
    {
        if (!Guid.TryParseExact(value, "D", out _))
        {
            throw new InvalidDataException($"{parameterName} must be a UUID.");
        }
    }

    private static void ValidateSha256(string value, string parameterName)
    {
        if (string.IsNullOrEmpty(value) ||
            value.Length != 64 ||
            value.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new InvalidDataException($"{parameterName} must be a lowercase SHA-256 value.");
        }
    }

    private static void ValidateSlots(IReadOnlyList<int> slots)
    {
        ArgumentNullException.ThrowIfNull(slots);
        if (slots.Count is < 1 or > 8 ||
            slots.Any(slot => slot is < 1 or > 8) ||
            slots.Distinct().Count() != slots.Count ||
            !slots.SequenceEqual(slots.Order()))
        {
            throw new InvalidDataException("Slots must contain one to eight unique values in ascending order.");
        }
    }
}
