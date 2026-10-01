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
    // A transport journey's legs are BUSINESS: each moves a demand from a pickup station to a dropoff station and does nothing
    // else. An idle return's one leg is WAITING_POINT (batch 8-19, control-server#390; IdleReturnPlan below) and a charging
    // journey's one leg is CHARGER (batch 9-06, control-server#404; ChargerPlan below), both with no legType and no demand.
    private const string BusinessStopPurpose = "BUSINESS";

    /// <summary><c>stopPurposeCategory</c> of an idle return's leg (protocol <c>2.0.0</c>, <c>FP-IS-12</c>).</summary>
    public const string WaitingPointStopPurpose = "WAITING_POINT";

    /// <summary><c>stopPurposeCategory</c> of a charging journey's leg (protocol <c>2.0.0</c>; batch 9-06, control-server#404).</summary>
    public const string ChargerStopPurpose = "CHARGER";

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
            redispatchGeneration is null ? null : key,
            // REQ-0204（批次8-20，control-server#391）：这条需求的公共站点是哪一个，受理与追加据此取得站点独占。
            candidate.Route.FixedStation.Station?.StationId);
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
            runtime.DispatchGeneration,
            JourneyStopRoles.OrderShapeOf(stop.StopRole));
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
    /// 它仍在停靠表里、仍有序位（修订把它排到所有开放停靠之后，审查 M1），所以不能指望调用方替这里滤掉它；
    /// 认它靠状态，不靠序位——序位只说明它排在哪，不说明它还要不要去。
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

    /// <summary>
    /// 空闲返回的计划：一条开往等待点的腿，<c>stopPurposeCategory = WAITING_POINT</c>、<c>legType</c> 与 <c>demandId</c> 为空
    /// （批次8-19，control-server#390；协议 <c>2.0.0</c> 允许两者为空）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 腿的状态只有两种：在路上 <c>ACTIVE</c>，到了 <c>ARRIVED</c>。<b>车还停在点上时这条腿不标 <c>COMPLETED</c>、也不删</b>
    /// （车载端 hmi#217 的跨票契约 P4：删了，到站格退回「旅程未同步」）：空闲返回收敛之后收尾那一张计划仍是这条 <c>ARRIVED</c> 的腿；
    /// 车被派走时，下一趟旅程的计划整体替换它（ADR-cross-0053），等待点腿随之消失，不会排在业务腿前面（P2、P3）。
    /// </para>
    /// <para>
    /// 业务腿的生成（<see cref="Plan"/>）不动：它只投影搬运停靠。
    /// </para>
    /// </remarks>
    public static UpcomingStopPlanProjection IdleReturnPlan(
        JourneyRuntimeRow runtime,
        JourneyStopRow waitingPoint,
        bool arrived,
        long revision)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(waitingPoint);
        return new UpcomingStopPlanProjection(revision, [IdleReturnLeg(runtime, waitingPoint, arrived)]);
    }

    /// <summary>空闲返回那一条腿，给计划与收尾快照共用。</summary>
    public static UpcomingMovementLeg IdleReturnLeg(JourneyRuntimeRow runtime, JourneyStopRow waitingPoint, bool arrived)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(waitingPoint);
        return new UpcomingMovementLeg(
            waitingPoint.MovementLegId,
            null,
            WaitingPointStopPurpose,
            null,
            null,
            waitingPoint.Sequence,
            waitingPoint.StationId,
            runtime.MapIdentity,
            arrived ? "ARRIVED" : "ACTIVE");
    }

    /// <summary>
    /// 空闲返回途中的车辆业务状态：<c>activePurpose = IDLE_RETURN</c>，没有装货阶段、没有阻断事实。其余四栏与搬运那一张同值
    /// （<c>JourneyRuntimeEngine.TransportBusinessState</c>），收尾时撤下用途的那一张同样如此（<c>JourneyClosure</c>）。
    /// </summary>
    public static VehicleBusinessProjection IdleReturnBusinessState(long revision) =>
        new(revision, "READY", VehicleActivePurposes.IdleReturn, false, "SUFFICIENT", ChargingCycleWireStates.NotCharging, null, []);

    /// <summary>
    /// 充电旅程的计划：一条开往充电桩的腿，<c>stopPurposeCategory = CHARGER</c>、<c>legType</c> 与 <c>demandId</c> 为空
    /// （批次9-06，control-server#404；协议 <c>2.0.0</c> 允许两者为空）。
    /// </summary>
    /// <remarks>
    /// 腿的状态在本票里有两种：承诺了、RIoT 还没确认建单（周期 <c>ALLOCATED</c>）是 <c>PLANNED</c>——车还没有出发，也可能被出发前安全门
    /// 挡着；RIoT 确认建单之后（周期 <c>EN_ROUTE</c>）是 <c>ACTIVE</c>。到桩之后的状态由批次9-07 接着写。业务腿的生成（<see cref="Plan"/>）不动。
    /// </remarks>
    public static UpcomingStopPlanProjection ChargerPlan(
        JourneyRuntimeRow runtime,
        JourneyStopRow charger,
        bool enRoute,
        long revision) =>
        new(revision, [ChargerLeg(runtime, charger, enRoute ? "ACTIVE" : "PLANNED")]);

    /// <summary>
    /// 充电旅程那一条 <c>CHARGER</c> 腿，给途中的计划、到桩的计划与充满时的收尾快照共用（批次9-07，control-server#405）。
    /// <paramref name="state"/> 是 <c>PLANNED</c>、<c>ACTIVE</c> 或 <c>ARRIVED</c>：到桩之后一直是 <c>ARRIVED</c>，充满之后车还在桩上也是——
    /// 车载端按当前腿（第一条不是 <c>COMPLETED</c> 的）判断车停在充电桩上，据此显示「已充满，在充电桩待命」、不开录入。
    /// </summary>
    public static UpcomingMovementLeg ChargerLeg(JourneyRuntimeRow runtime, JourneyStopRow charger, string state)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(charger);
        ArgumentException.ThrowIfNullOrWhiteSpace(state);
        return new UpcomingMovementLeg(
            charger.MovementLegId,
            null,
            ChargerStopPurpose,
            null,
            null,
            charger.Sequence,
            charger.StationId,
            runtime.MapIdentity,
            state);
    }

    /// <summary>
    /// 充电旅程的车辆业务状态：<c>activePurpose = CHARGING</c>，没有装货阶段、没有阻断事实，<c>manualChargingHold</c> 为假
    /// （在人工充电等待中的车不会被分配充电）。<paramref name="chargingCycleState"/> 取充电周期在线上的状态，
    /// <paramref name="batteryState"/> 取承诺时按判它的那份事实投影、记在旅程上的那一版。
    /// </summary>
    public static VehicleBusinessProjection ChargingBusinessState(long revision, string chargingCycleState, string batteryState) =>
        new(revision, "READY", VehicleActivePurposes.Charging, false, batteryState, chargingCycleState, null, []);

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
