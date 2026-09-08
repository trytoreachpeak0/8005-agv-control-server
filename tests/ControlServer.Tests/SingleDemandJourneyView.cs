using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;

namespace ControlServer.Tests;

/// <summary>
/// A journey that carries one demand and one pickup stop, read back as if it were still the single
/// row it used to be. ADR-cross-0057 split that row into a journey, its stops and its demands; the
/// tests that predate the split are about exactly this degenerate shape, so they keep reading it
/// this way instead of being rewritten to walk three tables.
/// </summary>
/// <remarks>
/// Deliberately refuses anything that is not that shape. A multi-demand journey has no single
/// pickup station, no single expected basket count and no single load command, so a test that grew
/// one has to say which stop and which demand it means -- and this throwing is what tells it so,
/// rather than silently reporting the first.
/// </remarks>
internal sealed class SingleDemandJourneyView
{
    private readonly JourneyRuntimeRow runtime;
    private readonly JourneyStopRow pickup;
    private readonly JourneyStopRow gate;
    private readonly JourneyDemandRow demand;

    public SingleDemandJourneyView(
        JourneyRuntimeRow runtime,
        IReadOnlyList<JourneyStopRow> stops,
        IReadOnlyList<JourneyDemandRow> demands)
    {
        this.runtime = runtime;
        pickup = stops.Single(stop => stop.Role == JourneyStopRole.Pickup);
        gate = stops.Single(stop => stop.Role == JourneyStopRole.Gate);
        demand = demands.Single();
    }

    public JourneyRuntimeRow Row => runtime;
    public JourneyStopRow PickupStop => pickup;
    public JourneyStopRow GateStop => gate;
    public JourneyDemandRow Demand => demand;

    public string JourneyId => runtime.JourneyId;
    public JourneyRuntimeStage Stage => runtime.Stage;
    public string? BlockReasonCode => runtime.BlockReasonCode;
    public string AgvId => runtime.AgvId;
    public string VehicleKey => runtime.VehicleKey;
    public string MapIdentity => runtime.MapIdentity;
    public int MapId => runtime.MapId;
    public string DispatchZone => runtime.DispatchZone;
    public string OperationSessionId => runtime.OperationSessionId;
    public long DispatchGeneration => runtime.DispatchGeneration;
    public DateTimeOffset? HoldingStartedAt => runtime.HoldingStartedAt;
    public string? LoadingClosedReason => runtime.LoadingClosedReason;
    public DateTimeOffset CreatedAt => runtime.CreatedAt;
    public DateTimeOffset UpdatedAt => runtime.UpdatedAt;

    public string DemandId => demand.DemandId;
    public int ExpectedBasketCount => demand.ExpectedBasketCount;
    public string TargetSlotsJson => demand.TargetSlotsJson;
    public string LoadCommandMessageId => demand.LoadCommandMessageId;
    public string LoadSlotOperationAttemptId => demand.LoadSlotOperationAttemptId;
    public string UnloadCommandMessageId => demand.UnloadCommandMessageId;
    public string UnloadSlotOperationAttemptId => demand.UnloadSlotOperationAttemptId;
    public string? ConsumedSublotMessageId => demand.ConsumedSublotMessageId;

    public string RouteEvidenceId => pickup.RouteEvidenceId;
    public string PickupStationId => pickup.StationId;
    public int PickupStationRiotId => pickup.StationRiotId;
    public string PickupMovementLegId => pickup.MovementLegId;
    public string PickupUpperId => pickup.UpperId;
    public string PreDepartureSafetyCheckId => pickup.PreDepartureSafetyCheckId;
    public string PreDepartureSafetyCheckMessageId => pickup.PreDepartureSafetyCheckMessageId;
    public DateTimeOffset? SublotWaitStartedAt => pickup.SublotWaitStartedAt;
    public string? ConsumedSafetyResultMessageId => pickup.ConsumedSafetyResultMessageId;
    public long WorklistRevision => pickup.WorklistRevision;
    public long PlanRevision => pickup.PlanRevision;
    public long VehicleBusinessRevision => pickup.VehicleBusinessRevision;
    public string VehicleBusinessMessageId => pickup.VehicleBusinessMessageId;
    public string PlanMessageId => pickup.PlanMessageId;

    public string GateStationId => runtime.GateStationId;
    public int GateStationRiotId => runtime.GateStationRiotId;
    public string GateMovementLegId => gate.MovementLegId;
    public string GateUpperId => gate.UpperId;
    public string GateVehicleBusinessMessageId => gate.VehicleBusinessMessageId;
    public string GatePlanMessageId => gate.PlanMessageId;

    /// <summary>
    /// The worklist and entry-request ids of the round in progress. They are per round now, so a
    /// stop that has not published yet reports the ids its first round will use -- which is what a
    /// test asserting "this message was queued" means either way.
    /// </summary>
    public string WorklistMessageId =>
        WireToGateStore.WorklistId(runtime.JourneyId, pickup.Sequence, Math.Max(1, pickup.LoadRound));

    public string SublotRequestMessageId =>
        WireToGateStore.SublotRequestId(runtime.JourneyId, pickup.Sequence, Math.Max(1, pickup.LoadRound));

    public string GateWorklistMessageId =>
        WireToGateStore.WorklistId(runtime.JourneyId, gate.Sequence, Math.Max(1, gate.LoadRound));
}
