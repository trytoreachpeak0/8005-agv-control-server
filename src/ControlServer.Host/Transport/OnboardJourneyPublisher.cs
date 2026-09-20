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
    // The envelope's own settings, not a second copy of them: this instance also materialises the
    // payload and rewrites replayed lines, and both have to agree with the envelope byte for byte.
    private static readonly JsonSerializerOptions SerializerOptions = ProtocolEnvelope.SerializerOptions;
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
            // Rebinding is a transport concern: only the session generation may move. sentAt stays
            // frozen because the snapshot payload is stamped from it, so a fresh clock read here
            // left the rebound payload carrying the old observedAt while the next republish
            // computed a new one -- a semantic conflict the publisher then hit once per runtime
            // iteration. It is also re-assigned as a DateTimeOffset rather than left as the parsed
            // node so the converter, not the encoder, writes it, matching what SerializeWire emits.
            DateTimeOffset sentAt = row.CreatedAt;
            envelope["sessionGeneration"] = sessionGeneration;
            envelope["sentAt"] = sentAt;
            string wire = envelope.ToJsonString(SerializerOptions);
            // This line does not go through ProtocolEnvelope.Serialize -- it rewrites what was stored
            // rather than building from fields, and it is the only outbound byte that does not -- so
            // it hands itself to the observation point by hand. Without this the gate would be blind
            // to every replayed envelope, which is exactly the kind of line a session resume sends.
            ProtocolEnvelope.OutboundObserver?.Invoke(row.MessageType, wire);
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
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(projection);
        if (!ChargingCycleStates.Contains(projection.ChargingCycleState))
            throw new InvalidDataException("chargingCycleState is not allowed by the protocol.");
        // loadingPhase is null exactly when the vehicle has no transport journey
        // (8005-agv-program#94). The schema accepts null either way, so nothing downstream -- not even
        // the outbound schema gate -- would notice a journey reported without its loading phase.
        if ((projection.ActivePurpose == VehicleActivePurposes.Transport) != (projection.LoadingPhase is not null))
            throw new InvalidDataException(
                "loadingPhase must be present exactly when activePurpose is TRANSPORT.");
        return PublishStampedSnapshotAsync(
            "VehicleBusinessStateSnapshot",
            messageId,
            agvId,
            sessionGeneration,
            projection.Revision,
            // observedAt comes from the envelope's frozen sentAt rather than a fresh clock read.
            // This snapshot keeps one deterministic messageId per journey stage, so a payload
            // carrying the current time differs on every re-publish and is refused as a semantic
            // conflict -- which left an arrived journey looping between reconnects, never able to
            // re-send the snapshot the peer was waiting on.
            sentAt => new
            {
                vehicleBusinessStateRevision = projection.Revision,
                readiness = projection.Readiness,
                projection.ActivePurpose,
                projection.ManualChargingHold,
                projection.BatteryState,
                projection.ChargingCycleState,
                loadingPhase = projection.LoadingPhase is not { } phase
                    ? null
                    : new
                    {
                        phase.State,
                        phase.CargoHoldingDeadlineAt,
                        phase.ClosedReason
                    },
                blockingFacts = projection.BlockingFacts.Select(fact => new
                {
                    fact.ReasonCode,
                    fact.SubjectType,
                    fact.SubjectId
                }),
                ObservedAt = sentAt
            },
            cancellationToken);
    }

    private static readonly HashSet<string> ChargingCycleStates = new(StringComparer.Ordinal)
    {
        "NOT_CHARGING", "ALLOCATED", "EN_ROUTE", "CHARGING", "COMPLETE", "UNABLE_TO_CHARGE", "UNKNOWN"
    };

    public Task PublishSublotEntryRequestAsync(
        string messageId,
        string agvId,
        long sessionGeneration,
        SublotEntryRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateUuid(request.OperationSessionId, nameof(request.OperationSessionId));
        ArgumentException.ThrowIfNullOrWhiteSpace(request.StationId);
        ArgumentOutOfRangeException.ThrowIfNegative(request.WorklistRevision);
        ArgumentNullException.ThrowIfNull(request.ExpectedSublots);
        // The schema's own bounds: one to eight, each non-blank, no repeats.
        if (request.ExpectedSublots.Count is < 1 or > 8 ||
            request.ExpectedSublots.Any(string.IsNullOrWhiteSpace) ||
            request.ExpectedSublots.Distinct(StringComparer.Ordinal).Count() != request.ExpectedSublots.Count)
            throw new InvalidDataException("expectedSublots must hold one to eight distinct sublots.");

        return PublishEnvelopeAsync(
            "SublotEntryRequested",
            messageId,
            correlationId: null,
            agvId,
            sessionGeneration,
            new
            {
                request.OperationSessionId,
                request.StationId,
                request.WorklistRevision,
                request.ExpectedSublots,
                entryMethods = SublotEntryMethods,
                expiresOnRevisionChange = true
            },
            cancellationToken);
    }

    /// <summary>
    /// The server refusing one <c>SublotSubmitted</c>: BR-013 section 2 could not establish the
    /// authoritative basket count for the sublot the operator entered, or the sublot is not in this
    /// vehicle's dispatch scope (protocol 2.0.0 item 2; <c>8005-agv-control-server#82</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// It is a RESPONSE whose correlation rule is <c>REQUIRED_ORIGINAL_MESSAGE_ID</c>, so
    /// <paramref name="submittedMessageId"/> is the submission being refused and not an id of ours. MVP
    /// sent it with a null <c>correlationId</c>, which the vehicle answers with
    /// <c>CORRELATION_INVALID</c>: BR-013's explicit refusal had never once reached an operator
    /// (<c>8005-agv-control-server#20</c>).
    /// </para>
    /// <para>
    /// Sent through the durable outbox rather than as a bare send, so the reason survives the connection
    /// the entry arrived on — and so that the stored line is itself the record that this submission has
    /// been judged, which is what keeps the runtime from refusing it again on every poll and what tells
    /// the cancellation before a sublot that the stop is still the operator's to cancel.
    /// </para>
    /// </remarks>
    public Task PublishSublotRejectedAsync(
        string messageId,
        string submittedMessageId,
        string agvId,
        long sessionGeneration,
        SublotRejection rejection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rejection);
        ValidateUuid(messageId, nameof(messageId));
        ValidateUuid(submittedMessageId, nameof(submittedMessageId));
        ArgumentException.ThrowIfNullOrWhiteSpace(agvId);
        ArgumentOutOfRangeException.ThrowIfNegative(sessionGeneration);
        if (rejection.DemandId is not null)
        {
            ValidateUuid(rejection.DemandId, nameof(rejection.DemandId));
        }
        ValidateUuid(rejection.OperationSessionId, nameof(rejection.OperationSessionId));
        ArgumentException.ThrowIfNullOrWhiteSpace(rejection.RejectedSublot);
        ArgumentOutOfRangeException.ThrowIfNegative(rejection.CurrentWorklistRevision);
        ArgumentNullException.ThrowIfNull(rejection.Problem);

        return PublishEnvelopeAsync(
            "SublotRejected",
            messageId,
            submittedMessageId,
            agvId,
            sessionGeneration,
            new
            {
                rejection.DemandId,
                rejection.OperationSessionId,
                rejection.Problem,
                rejection.CurrentWorklistRevision,
                rejection.RejectedSublot
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
        if (projection.SlotOperationAttemptId is not null)
        {
            ValidateUuid(projection.SlotOperationAttemptId, nameof(projection.SlotOperationAttemptId));
            // An attempt belongs to a demand; a session with none has no attempt to name.
            if (projection.DemandId is null)
                throw new InvalidDataException("A recovery session without a demand names no slot operation attempt.");
        }
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
                projection.SlotOperationAttemptId,
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

    /// <summary>
    /// 消息 7 <c>SlotConfigurationActivationCommand</c> 排进发件箱。
    /// </summary>
    /// <remarks>
    /// 只排队，不发送——manifest 给这条消息的 <c>durableBeforeSend</c> 是真，所以「落库」与「上线」
    /// 是两步，调用方拿到这一行之后再 <see cref="SendPersistedAsync"/>。同一个 messageId 再排一次
    /// 是幂等的（<c>QueueEnvelopeAsync</c> 复用已存在那一行的 <c>sentAt</c>），断线重连的补发因此
    /// 是逐字节相同的一行，而不是一条新命令。
    /// </remarks>
    public Task<ProtocolOutboxRow> QueueSlotConfigurationActivationCommandAsync(
        string messageId,
        string agvId,
        long sessionGeneration,
        object payload,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return QueueEnvelopeAsync(
            "SlotConfigurationActivationCommand", messageId, null, agvId, sessionGeneration,
            payload, cancellationToken);
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
            projection.Revision,
            new
            {
                projection.StationId,
                worklistRevision = projection.Revision,
                projection.OperationSessionId,
                projection.StationDepartureDeadlineAt,
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
            projection.Revision,
            new
            {
                planRevision = projection.Revision,
                legs = projection.Legs.Select(leg => new
                {
                    leg.MovementLegId,
                    leg.LegType,
                    leg.StopPurposeCategory,
                    leg.DemandId,
                    leg.PublicStationFunction,
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
        long revision,
        object payload,
        CancellationToken cancellationToken) =>
        await PublishStampedSnapshotAsync(
            messageType, messageId, agvId, sessionGeneration, revision, _ => payload, cancellationToken)
            .ConfigureAwait(false);

    /// <summary>Builds the payload from the envelope's frozen sentAt, so re-publishes reproduce it.</summary>
    /// <summary>
    /// 一条按车带修订号的快照，发出去并把这条流的下一趟基准结清（批次7-06，control-server#211）。
    /// </summary>
    /// <remarks>
    /// <b><paramref name="revision"/> 是必填的，这正是它的用处。</b>三条按停靠发的快照流都只能从这里出去，
    /// 所以「发了快照却没结清」在构造上不可能；再加一种这样的快照时，编译器会要求它把自己的号交出来。
    /// 结清放在发布之前，与发件箱那一行落在同一次保存里——崩在中间也不会留下「发了没结清」的状态。
    /// </remarks>
    private async Task PublishStampedSnapshotAsync(
        string messageType,
        string messageId,
        string agvId,
        long sessionGeneration,
        long revision,
        Func<DateTimeOffset, object> payloadFactory,
        CancellationToken cancellationToken)
    {
        await store.RaiseSnapshotRevisionFloorAsync(messageType, agvId, revision, cancellationToken)
            .ConfigureAwait(false);
        await PublishStampedEnvelopeAsync(
            messageType,
            messageId,
            correlationId: null,
            agvId,
            sessionGeneration,
            payloadFactory,
            cancellationToken).ConfigureAwait(false);
    }

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
        string candidateWire = SerializeWire(
            messageType, messageId, correlationId, agvId, sessionGeneration, sentAt, payloadFactory(sentAt));
        return await store.QueueOutboundEnvelopeAsync(
            messageId, messageType, candidateWire, sentAt, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds the wire line the peer has to be able to reproduce byte for byte.
    /// </summary>
    /// <remarks>
    /// The peer parses our line into a typed envelope whose payload stays a JsonElement, then
    /// re-serialises that envelope to report the content hash it applied. A JsonElement is written
    /// through the encoder, so anything a converter emits verbatim -- the '+' in a DateTimeOffset
    /// offset -- comes back as its six-character unicode escape, so the peer can never reproduce
    /// our bytes. That failed the acknowledgement and dropped the connection. Materialising it first,
    /// the way the contract type itself builds it, makes the line reproducible. Which side of the
    /// call that happens on is deliberate: ProtocolEnvelope.Serialize serializes its payload exactly
    /// as given, so doing it there would change every other sender's bytes instead.
    /// </remarks>
    /// <remarks>
    /// It also makes the line a fixed point of the replay rewrite in ReplayPendingForSessionAsync, so
    /// re-publishing at the same generation stays a byte-identical no-op instead of a change that
    /// RefreshOutboundEnvelopeAsync then refuses as a non-advancing session generation.
    /// </remarks>
    private static string SerializeWire(
        string messageType,
        string messageId,
        string? correlationId,
        string agvId,
        long sessionGeneration,
        DateTimeOffset sentAt,
        object payload) =>
        ProtocolEnvelope.Serialize(
            messageType,
            messageId,
            correlationId,
            agvId,
            sessionGeneration,
            sentAt,
            JsonSerializer.SerializeToElement(payload, ProtocolEnvelope.SerializerOptions));

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
        string candidateWire = SerializeWire(
            messageType, messageId, correlationId, agvId, sessionGeneration, sentAt, payload);
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
