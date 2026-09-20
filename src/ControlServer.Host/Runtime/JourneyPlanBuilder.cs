using System.Security.Cryptography;
using System.Text;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime.Dispatch;
using ControlServer.Host.Runtime.Fleet;
using ControlServer.Infrastructure.Persistence;

namespace ControlServer.Host.Runtime;

/// <summary>
/// What a journey looks like: its route, its plan, the endpoints it freezes, its two move orders and
/// the plan snapshots it sends, and the stable ids all of them are derived under.
/// </summary>
/// <remarks>
/// <para>
/// Every decision about the shape of a journey -- which end is the pickup, which leg runs first,
/// where the vehicle loads -- belongs here rather than in a dispatch criterion or in the engine. That
/// is what let the reverse journey shape (control-server#163, STAGING_TO_WIRE) be one change to this file
/// instead of a change threaded through the criterion chain and the engine's 2800 lines.
/// </para>
/// <para>
/// Built by the engine from its own <see cref="JourneyRuntimeOptions"/>, not registered in DI: it holds
/// no state and reads nothing, and taking it through the container would add a constructor parameter
/// to every engine a test builds.
/// </para>
/// </remarks>
public sealed class JourneyPlanBuilder(JourneyRuntimeOptions options)
{
    // Every leg this runtime plans is BUSINESS: it moves a demand from a pickup station to a
    // dropoff station and does nothing else. WAITING_POINT is FP-C4, batch 5, and CHARGER is
    // FP-C1, batch 8 -- neither exists here to be reported, so the constant is a fact about this
    // profile rather than a placeholder for one.
    private const string BusinessStopPurpose = "BUSINESS";

    /// <summary>
    /// Builds a candidate's route from its AREA station and its task type's fixed station, or names
    /// why it cannot.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The fixed end decides which station is which, and nothing else does: at
    /// <see cref="FixedStationEnd.Destination"/> (WIRE_TO_GATE) the route runs from the AREA station to the
    /// fixed station; at <see cref="FixedStationEnd.Origin"/> (STAGING_TO_WIRE) from the fixed station to the
    /// AREA station (REQ-0184). The origin is always the pickup and the destination the drop-off, so the legs
    /// keep their order -- TO_PICKUP first, TO_DROPOFF second -- and only the stations at their ends change.
    /// </para>
    /// <para>
    /// The direction is not stored anywhere: it is the task type's rule, which the demand freezes with its
    /// rule version, and <see cref="MapStationResolver.BuildRouteEvidenceId"/> takes the two ends by name
    /// so that a swapped route cannot pass for this one on replay (scope specification 5.3).
    /// </para>
    /// </remarks>
    public static JourneyRouteDecision ResolveRoute(
        RiotMapStationCatalogSnapshot map,
        string dispatchZone,
        RiotMapStation areaStation,
        FixedTaskStationResolution fixedStation,
        string area,
        string eqp)
    {
        ArgumentNullException.ThrowIfNull(fixedStation);
        if (fixedStation.Station is not { } fixedEnd)
        {
            return JourneyRouteDecision.Refused(fixedStation.RefusalReasonCode!);
        }

        RouteEndpoints endpoints = fixedStation.FixedEnd switch
        {
            FixedStationEnd.Destination => new() { Origin = areaStation, Destination = fixedEnd },
            FixedStationEnd.Origin => new() { Origin = fixedEnd, Destination = areaStation },
            _ => throw new ArgumentOutOfRangeException(
                nameof(fixedStation), fixedStation.FixedEnd, "A fixed station is at the origin or the destination."),
        };
        return JourneyRouteDecision.Resolved(new ResolvedJourneyRoute(
            dispatchZone,
            MapStationResolver.BuildRouteEvidenceId(map, endpoints, area, eqp),
            endpoints.Origin.StationName,
            endpoints.Origin.StationId,
            endpoints.Destination.StationName,
            endpoints.Destination.StationId,
            fixedStation));
    }

    /// <summary>The plan a candidate is accepted under.</summary>
    /// <remarks>
    /// The fixed station's rule and binding set versions, and the catalog revision the endpoints were taken from, are
    /// carried through untouched: the acceptance freezes them with the endpoints (REQ-0305, REQ-0344,
    /// control-server#160).
    /// </remarks>
    public JourneyExecutionPlan CreatePlan(
        FleetVehicle fleetVehicle,
        EligibleDispatchCandidate candidate,
        DateTimeOffset now)
    {
        string demandId = candidate.Snapshot.DemandId;
        return new JourneyExecutionPlan(
            fleetVehicle.AgvId,
            fleetVehicle.VehicleKey,
            fleetVehicle.AgvLifecycleGeneration,
            options.MapId,
            options.MapIdentity,
            candidate.Route.DispatchZone,
            candidate.Route.RouteEvidenceId,
            candidate.Route.PickupStationId,
            candidate.Route.PickupStationRiotId,
            candidate.Route.DropoffStationId,
            candidate.Route.DropoffStationRiotId,
            candidate.ExpectedBasketCount,
            candidate.TargetSlots,
            StableGuid(demandId, "operation-session"),
            StableGuid(demandId, "pickup-leg"),
            $"W2G-{demandId}-PICKUP-{options.DispatchGeneration}",
            StableGuid(demandId, "gate-leg"),
            $"W2G-{demandId}-GATE-{options.DispatchGeneration}",
            options.DispatchGeneration,
            now,
            candidate.AreaAssignmentVersion,
            candidate.RequiredSlotPosition,
            candidate.Route.FixedStation.RuleVersion,
            candidate.Route.FixedStation.BindingSetVersion,
            candidate.CatalogRevision);
    }

    /// <summary>The first move order, which is created with the journey.</summary>
    public static OrderIntent PickupIntent(JourneyExecutionPlan plan, string demandId, DateTimeOffset now) => new(
        plan.PickupMovementLegId,
        demandId,
        plan.PickupUpperId,
        "TO_PICKUP",
        plan.PickupStationId,
        now,
        plan.VehicleKey,
        plan.MapId,
        plan.PickupStationRiotId,
        plan.AgvLifecycleGeneration,
        plan.DispatchGeneration);

    /// <summary>The second move order, created once the vehicle is loaded and cleared to leave.</summary>
    public static OrderIntent GateIntent(JourneyRuntimeRow runtime, DateTimeOffset now) => new(
        runtime.GateMovementLegId,
        runtime.DemandId,
        runtime.GateUpperId,
        "TO_GATE",
        runtime.GateStationId,
        now,
        runtime.VehicleKey,
        runtime.MapId,
        runtime.GateStationRiotId,
        runtime.AgvLifecycleGeneration,
        runtime.DispatchGeneration);

    // The plan stream advances three times per journey: before the pickup arrival at the stored
    // revision, at the pickup one above it, at the gate two above it. WireToGateStore seeds the next
    // journey on the vehicle three above, so the stream never steps back across journeys.

    /// <summary>
    /// 旅程的停靠序列，投影成车载端看到的那张计划：每个停靠一条腿，腿的状态由它与当前停靠的先后关系给出
    /// （批次7-03，control-server#208）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 在这之前这里是三个方法、六条写死的腿，「第 1 条是取货、第 2 条是关卡」直接写在字面量里。现在只有一条规则：
    /// 当前停靠之前的腿 <c>COMPLETED</c>，之后的 <c>PLANNED</c>，当前这条按车到没到分 <c>ARRIVED</c> 与 <c>ACTIVE</c>。
    /// 单需求两个停靠下，这条规则算出来的三张快照与原来那六条字面量逐字相同。
    /// </para>
    /// <para>
    /// 腿上的 <c>demandId</c> 取锚需求。协议允许它为空（<c>UpcomingStopPlanSnapshot.legs[].demandId</c>），一个停靠挂几条
    /// 需求时该填什么由 批次7-06（control-server#211）决定；本票不改它填什么。
    /// </para>
    /// </remarks>
    public static UpcomingStopPlanProjection Plan(
        JourneyRuntimeRow runtime,
        IReadOnlyList<JourneyStopRow> stops,
        JourneyStopRow current,
        bool arrivedAtCurrent,
        long revision)
    {
        ArgumentNullException.ThrowIfNull(stops);
        ArgumentNullException.ThrowIfNull(current);
        return new UpcomingStopPlanProjection(
            revision,
            [.. stops.Select(stop => PlanLeg(
                runtime,
                stop.MovementLegId,
                stop.StopRole == JourneyStopRoles.Pickup ? "TO_PICKUP" : "TO_DROPOFF",
                stop.Sequence,
                stop.StationId,
                LegState(stop, current, arrivedAtCurrent)))]);
    }

    private static string LegState(JourneyStopRow stop, JourneyStopRow current, bool arrivedAtCurrent) =>
        stop.Sequence < current.Sequence ? "COMPLETED"
        : stop.Sequence > current.Sequence ? "PLANNED"
        : arrivedAtCurrent ? "ARRIVED" : "ACTIVE";

    /// <summary>
    /// A deterministic id derived from a stable identity and what it is for, in the RFC 4122 version 5
    /// layout. Every id a journey carries is one of these, so a replay derives the same ids again.
    /// </summary>
    public static string StableGuid(string demandId, string purpose)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{demandId}|{purpose}"));
        Span<byte> guidBytes = bytes.AsSpan(0, 16);
        guidBytes[6] = (byte)((guidBytes[6] & 0x0f) | 0x50);
        guidBytes[8] = (byte)((guidBytes[8] & 0x3f) | 0x80);
        return new Guid(guidBytes).ToString("D");
    }

    // publicStationFunction, the fifth argument, is always null: the product does not maintain a
    // public station classification (scope specification 5.3), so the protocol field stays empty.
    private static UpcomingMovementLeg PlanLeg(
        JourneyRuntimeRow runtime,
        string movementLegId,
        string legType,
        int sequence,
        string stationId,
        string state) => new(
            movementLegId,
            legType,
            BusinessStopPurpose,
            runtime.DemandId,
            null,
            sequence,
            stationId,
            runtime.MapIdentity,
            state);
}

/// <summary>A candidate's route, or the reason it has none.</summary>
public sealed record JourneyRouteDecision
{
    private JourneyRouteDecision(ResolvedJourneyRoute? route, string? refusalReasonCode)
    {
        Route = route;
        RefusalReasonCode = refusalReasonCode;
    }

    public ResolvedJourneyRoute? Route { get; }

    public string? RefusalReasonCode { get; }

    public static JourneyRouteDecision Resolved(ResolvedJourneyRoute route) =>
        new(route ?? throw new ArgumentNullException(nameof(route)), null);

    public static JourneyRouteDecision Refused(string refusalReasonCode) =>
        new(null, refusalReasonCode);
}
