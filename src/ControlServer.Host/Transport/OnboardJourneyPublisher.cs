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

    /// <param name="keepAcknowledgedIgnoring">见 <see cref="QueueEnvelopeAsync"/> 的同名参数。</param>
    public Task PublishVehicleBusinessStateAsync(
        string messageId,
        string agvId,
        long sessionGeneration,
        VehicleBusinessProjection projection,
        CancellationToken cancellationToken,
        IReadOnlySet<string>? keepAcknowledgedIgnoring = null)
    {
        ValidateVehicleBusinessState(projection);
        return PublishStampedSnapshotAsync(
            "VehicleBusinessStateSnapshot",
            messageId,
            agvId,
            sessionGeneration,
            projection.Revision,
            VehicleBusinessStatePayload(projection),
            cancellationToken,
            keepAcknowledgedIgnoring);
    }

    private static void ValidateVehicleBusinessState(VehicleBusinessProjection projection)
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
    }

    // observedAt comes from the envelope's frozen sentAt rather than a fresh clock read.
    // This snapshot keeps one deterministic messageId per journey stage, so a payload
    // carrying the current time differs on every re-publish and is refused as a semantic
    // conflict -- which left an arrived journey looping between reconnects, never able to
    // re-send the snapshot the peer was waiting on.
    private static Func<DateTimeOffset, object> VehicleBusinessStatePayload(VehicleBusinessProjection projection) =>
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
        };

    private static readonly HashSet<string> ChargingCycleStates = new(StringComparer.Ordinal)
    {
        "NOT_CHARGING", "ALLOCATED", "EN_ROUTE", "CHARGING", "COMPLETE", "UNABLE_TO_CHARGE", "UNKNOWN"
    };

    /// <param name="keepAcknowledgedIgnoring">见 <see cref="QueueEnvelopeAsync"/> 的同名参数。</param>
    public Task PublishSublotEntryRequestAsync(
        string messageId,
        string agvId,
        long sessionGeneration,
        SublotEntryRequest request,
        CancellationToken cancellationToken,
        IReadOnlySet<string>? keepAcknowledgedIgnoring = null)
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
            cancellationToken,
            keepAcknowledgedIgnoring);
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

    /// <param name="keepAcknowledgedIgnoring">见 <see cref="QueueEnvelopeAsync"/> 的同名参数。</param>
    public Task PublishCurrentStopWorklistAsync(
        string messageId,
        string agvId,
        long sessionGeneration,
        CurrentStopWorklistProjection projection,
        CancellationToken cancellationToken,
        IReadOnlySet<string>? keepAcknowledgedIgnoring = null) =>
        PublishSnapshotAsync(
            "CurrentStopWorklistSnapshot",
            messageId,
            agvId,
            sessionGeneration,
            projection.Revision,
            CurrentStopWorklistPayload(projection),
            cancellationToken,
            keepAcknowledgedIgnoring);

    /// <param name="keepAcknowledgedIgnoring">见 <see cref="QueueEnvelopeAsync"/> 的同名参数。</param>
    public Task PublishUpcomingStopPlanAsync(
        string messageId,
        string agvId,
        long sessionGeneration,
        UpcomingStopPlanProjection projection,
        CancellationToken cancellationToken,
        IReadOnlySet<string>? keepAcknowledgedIgnoring = null) =>
        PublishSnapshotAsync(
            "UpcomingStopPlanSnapshot",
            messageId,
            agvId,
            sessionGeneration,
            projection.Revision,
            UpcomingStopPlanPayload(projection),
            cancellationToken,
            keepAcknowledgedIgnoring);

    private static object CurrentStopWorklistPayload(CurrentStopWorklistProjection projection) => new
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
    };

    private static object UpcomingStopPlanPayload(UpcomingStopPlanProjection projection) => new
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
    };

    /// <summary>
    /// 旅程收尾的三张快照之一（control-server#323）：与对应的发布方法同一个信封、同一套载荷，但只暂存进调用方那一次还没保存的
    /// 改动——不保存、不发送。发送由调用方在保存之后做（<c>JourneyClosure.SendAsync</c>）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 静态、不带对端，因为收尾尾巴（<c>PickupStopTermination</c>）只拿得到上下文：它的五个调用方里有几个根本没有发布器。
    /// 按车计数器照样由这里推高（<see cref="WireToGateStore.RaiseSnapshotRevisionFloorAsync"/>，同样不保存），
    /// 所以「暂存了快照却没结清」与发布那条路一样在构造上不可能。
    /// </para>
    /// <para>
    /// 信封经 <c>ProtocolEnvelope.Serialize</c> 生成，出站 schema 门禁照样看得见这几行。返回 false 表示这个 id 已经在发件箱里、
    /// 这一次什么也没加。
    /// </para>
    /// </remarks>
    public static Task<bool> StageVehicleBusinessStateAsync(
        WireToGateStore store,
        string messageId,
        string agvId,
        long sessionGeneration,
        VehicleBusinessProjection projection,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken)
    {
        ValidateVehicleBusinessState(projection);
        return StageStampedSnapshotAsync(
            store, "VehicleBusinessStateSnapshot", messageId, agvId, sessionGeneration, projection.Revision,
            VehicleBusinessStatePayload(projection), createdAt, cancellationToken);
    }

    /// <inheritdoc cref="StageVehicleBusinessStateAsync"/>
    public static Task<bool> StageCurrentStopWorklistAsync(
        WireToGateStore store,
        string messageId,
        string agvId,
        long sessionGeneration,
        CurrentStopWorklistProjection projection,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(projection);
        object payload = CurrentStopWorklistPayload(projection);
        return StageStampedSnapshotAsync(
            store, "CurrentStopWorklistSnapshot", messageId, agvId, sessionGeneration, projection.Revision,
            _ => payload, createdAt, cancellationToken);
    }

    /// <inheritdoc cref="StageVehicleBusinessStateAsync"/>
    public static Task<bool> StageUpcomingStopPlanAsync(
        WireToGateStore store,
        string messageId,
        string agvId,
        long sessionGeneration,
        UpcomingStopPlanProjection projection,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(projection);
        object payload = UpcomingStopPlanPayload(projection);
        return StageStampedSnapshotAsync(
            store, "UpcomingStopPlanSnapshot", messageId, agvId, sessionGeneration, projection.Revision,
            _ => payload, createdAt, cancellationToken);
    }

    private static async Task<bool> StageStampedSnapshotAsync(
        WireToGateStore store,
        string messageType,
        string messageId,
        string agvId,
        long sessionGeneration,
        long revision,
        Func<DateTimeOffset, object> payloadFactory,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(agvId);
        ValidateUuid(messageId, nameof(messageId));
        ArgumentOutOfRangeException.ThrowIfNegative(sessionGeneration);
        await store.RaiseSnapshotRevisionFloorAsync(messageType, agvId, revision, cancellationToken)
            .ConfigureAwait(false);
        string wire = SerializeWire(
            messageType, messageId, correlationId: null, agvId, sessionGeneration, createdAt, payloadFactory(createdAt));
        return await store.StageOutboundEnvelopeAsync(messageId, messageType, wire, createdAt, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task PublishSnapshotAsync(
        string messageType,
        string messageId,
        string agvId,
        long sessionGeneration,
        long revision,
        object payload,
        CancellationToken cancellationToken,
        IReadOnlySet<string>? keepAcknowledgedIgnoring = null) =>
        await PublishStampedSnapshotAsync(
            messageType, messageId, agvId, sessionGeneration, revision, _ => payload, cancellationToken,
            keepAcknowledgedIgnoring)
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
        CancellationToken cancellationToken,
        IReadOnlySet<string>? keepAcknowledgedIgnoring = null)
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
            cancellationToken,
            keepAcknowledgedIgnoring).ConfigureAwait(false);
    }

    private async Task PublishEnvelopeAsync(
        string messageType,
        string messageId,
        string? correlationId,
        string agvId,
        long sessionGeneration,
        object payload,
        CancellationToken cancellationToken,
        IReadOnlySet<string>? keepAcknowledgedIgnoring = null) =>
        await PublishStampedEnvelopeAsync(
            messageType, messageId, correlationId, agvId, sessionGeneration, _ => payload, cancellationToken,
            keepAcknowledgedIgnoring)
            .ConfigureAwait(false);

    private async Task PublishStampedEnvelopeAsync(
        string messageType,
        string messageId,
        string? correlationId,
        string agvId,
        long sessionGeneration,
        Func<DateTimeOffset, object> payloadFactory,
        CancellationToken cancellationToken,
        IReadOnlySet<string>? keepAcknowledgedIgnoring = null)
    {
        ProtocolOutboxRow stored = await QueueEnvelopeAsync(
            messageType, messageId, correlationId, agvId, sessionGeneration, payloadFactory, cancellationToken,
            keepAcknowledgedIgnoring)
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

    /// <summary>把这一条报文落进发件箱（或认出已经在那里的那一行），返回落着的那一行。</summary>
    /// <param name="keepAcknowledgedIgnoring">
    /// <para>
    /// 给了它、而发件箱里这一行车已经确认过时：候选报文与那一行去掉信封的 <c>sessionGeneration</c>、<c>sentAt</c> 以及
    /// payload 里这几个字段之后一字不差，就原样返回那一行——不改写、不入队，调用方也就不发（已确认的行本来就不发）。
    /// 有任何别的不同，照常交给 <see cref="WireToGateStore.QueueOutboundEnvelopeAsync"/>，由它的重放校验按原样拒绝。
    /// 不给这个参数时行为与之前完全相同。
    /// </para>
    /// <para>
    /// <b>为什么需要它（control-server#331）。</b>到站那一段发布是好几条报文，做完的标志是阶段前移。断线把它打断在中间时，
    /// 重连后整段从头重跑，而前半段车早已确认。cs#331 起调用方给的忽略集合只有清单的 <c>stationDepartureDeadlineAt</c>：断线那一轮
    /// 作废了站点离站等待，重连后从此刻重填（ADR-cross-0055），期限会合法地变。control-server#339 起期限变了的清单作为新的一版发出
    /// （新的号、新的 id），调用方不再忽略任何 payload 字段；参数留着，是因为「只有信封可以不同」本身就要靠它表达。
    /// </para>
    /// <para>
    /// <b>按内容比，不按「确认过」这一件事。</b>判据若只是「已确认」，同一 messageId 下内容真的变了的那一版会被静默吞掉，
    /// 车永远收不到——持货等单的旅程在断线窗口里进出装货阶段时，车辆业务状态就会这样（修订号与 <c>loadingPhase</c> 都变）。
    /// 按内容比，那一种照旧被护栏拒，在看板上显示推进失败，而不是悄悄消失。
    /// </para>
    /// </param>
    private async Task<ProtocolOutboxRow> QueueEnvelopeAsync(
        string messageType,
        string messageId,
        string? correlationId,
        string agvId,
        long sessionGeneration,
        Func<DateTimeOffset, object> payloadFactory,
        CancellationToken cancellationToken,
        IReadOnlySet<string>? keepAcknowledgedIgnoring = null)
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
        if (keepAcknowledgedIgnoring is not null &&
            existing is { AcknowledgedAt: not null } &&
            string.Equals(existing.MessageType, messageType, StringComparison.Ordinal) &&
            SaysTheSameIgnoring(existing.PayloadJson, candidateWire, keepAcknowledgedIgnoring))
        {
            return existing;
        }
        return await store.QueueOutboundEnvelopeAsync(
            messageId, messageType, candidateWire, sentAt, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 两行报文去掉信封的 <c>sessionGeneration</c>、<c>sentAt</c> 与 payload 里 <paramref name="ignoredPayloadFields"/> 之后是否一字不差，
    /// 且候选的代次不比已存的旧（control-server#331）。其余每一个字段——信封的身份、类型、messageId，payload 里的修订号与每一项——
    /// 都参与比较。
    /// </summary>
    private static bool SaysTheSameIgnoring(
        string storedWire,
        string candidateWire,
        IReadOnlySet<string> ignoredPayloadFields)
    {
        // 代次不许倒退：比较里去掉代次，是为了放过「重连后同一件事在新的一代再说一次」，不是放过一次发进旧一代的重发——
        // 那是重放校验本来就拒的形状，这里不替它开口子。代次相同是允许的：会话掉到未就绪、没有换代就回来时，离站等待
        // 一样被作废重填（ADR-cross-0055），期限变了而代次没变。
        if (Generation(candidateWire) < Generation(storedWire))
        {
            return false;
        }
        return JsonNode.DeepEquals(Stripped(storedWire), Stripped(candidateWire));

        static long Generation(string wire)
        {
            using JsonDocument document = JsonDocument.Parse(wire);
            return document.RootElement.GetProperty("sessionGeneration").GetInt64();
        }

        JsonObject Stripped(string wire)
        {
            JsonObject envelope = JsonNode.Parse(wire)?.AsObject()
                ?? throw new InvalidDataException("Outbound envelope is empty.");
            envelope.Remove("sessionGeneration");
            envelope.Remove("sentAt");
            if (envelope["payload"] is JsonObject payload)
            {
                foreach (string field in ignoredPayloadFields)
                {
                    payload.Remove(field);
                }
            }
            return envelope;
        }
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
