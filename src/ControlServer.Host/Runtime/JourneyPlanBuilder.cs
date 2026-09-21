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
    /// <param name="redispatchGeneration">
    /// 释放之后再派时的新代次（批次7-10，control-server#215），第一次受理为空。它进两个地方：id 派生的键
    /// （<see cref="JourneyIdentity.DerivationKey"/>），以及 RIoT 订单号里的代次——同一条需求的两张订单号因此不撞
    /// （<c>OrderIntents.UpperId</c> 是唯一索引）。为空时一切与改派出现之前逐字相同。
    /// </param>
    public JourneyExecutionPlan CreatePlan(
        FleetVehicle fleetVehicle,
        EligibleDispatchCandidate candidate,
        DateTimeOffset now,
        long? redispatchGeneration = null)
    {
        string demandId = candidate.Snapshot.DemandId;
        string key = JourneyIdentity.DerivationKey(demandId, redispatchGeneration);
        long generation = redispatchGeneration ?? options.DispatchGeneration;
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
            StableGuid(key, "operation-session"),
            StableGuid(key, "pickup-leg"),
            $"W2G-{demandId}-PICKUP-{generation}",
            StableGuid(key, "gate-leg"),
            $"W2G-{demandId}-GATE-{generation}",
            generation,
            now,
            candidate.AreaAssignmentVersion,
            candidate.RequiredSlotPosition,
            candidate.Route.FixedStation.RuleVersion,
            candidate.Route.FixedStation.BindingSetVersion,
            candidate.CatalogRevision,
            redispatchGeneration is null ? null : key);
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

    /// <summary>
    /// 开往下一个停靠的那张订单，在车装完、离站核验通过之后建（批次7-06，control-server#211）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 腿、订单号与目标站都取<b>那个停靠的行</b>，不再取旅程行上写死的 <c>Gate*</c> 四列。写死的那四列就是
    /// 「一趟只有取货、卸货两个停靠」这个假设本身：多停靠计划里第三段腿在旅程行上没有地方放。单需求两停靠下，
    /// 停靠行的这四个值由受理时从旅程行原样搬入，所以建出来的订单逐字相同。
    /// </para>
    /// <para>
    /// <c>demandId</c> 仍取锚需求：一张 RIoT 订单是一次整车移动，协议与 RIoT 那一侧都只放得下一个需求
    /// （票面第 10 条）。
    /// </para>
    /// </remarks>
    public static OrderIntent LegIntent(JourneyRuntimeRow runtime, JourneyStopRow stop, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(stop);
        return new OrderIntent(
            stop.MovementLegId,
            runtime.DemandId,
            stop.UpperId,
            "TO_GATE",
            stop.StationId,
            now,
            runtime.VehicleKey,
            runtime.MapId,
            stop.StationRiotId,
            runtime.AgvLifecycleGeneration,
            runtime.DispatchGeneration);
    }

    // 计划流每趟推进「到站次数 + 1」次：派车时先发一张「车还在路上」的（CV-DEMAND-ACCEPT-TO-PICKUP，用存着的
    // 那个号），此后每到一个停靠再发一张。两个停靠的旅程因此是三次，这也正是 WireToGateStore 为下一趟预留的量
    // （PlanRevisionsPerJourney）。
    //
    // 多停靠旅程会发得更多，途中追加引起的重发还会再多发几张——那时预留量不够（批次7-06，control-server#211）。
    // 接住它的是两处，而不是把这个常量改大：JourneyRuntimeEngine.AdvanceSnapshotRevisionCountersAsync 在每次
    // 发布之后把按车计数器抬到这一号之上，重发那一处同时把本趟的基准抬高，好让后面按序位算出来的号仍在其上。
    // 常量改大治不了这件事——停靠数没有上界，而预留量是个常数。

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
    /// <para>
    /// <see cref="JourneyStopStatuses.Removed"/> 的停靠不投影：计划修订只把停靠行标成已删、不删行（批次7-10，control-server#215），
    /// 它的序位保持原值、可能与重新编号后的开放停靠撞号，所以不能指望调用方替这里滤掉它，也不能靠序位认出它。
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
            [.. stops.Where(stop => stop.Status != JourneyStopStatuses.Removed).Select(stop => PlanLeg(
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
