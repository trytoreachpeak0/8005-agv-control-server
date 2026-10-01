using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime.Fleet;
using ControlServer.Infrastructure.Persistence;

namespace ControlServer.Host.Runtime.Charging;

/// <summary>
/// 一次充电承诺里，除周期、用途占有与桩预占之外的三行（批次9-06，control-server#404）：旅程行、开往充电桩的那一个停靠、它的订单意图。
/// 它们与周期、<c>CHARGING</c> 用途占有、<c>CHARGER</c> 预占在同一次保存里写（<c>IChargingCycleStore.TryStartAsync</c> 的 <c>sameSave</c>）。
/// </summary>
/// <remarks>
/// <para>
/// <b>身份全部从承诺的旅程 id 派生</b>（<see cref="ChargingIdentity"/>）：消息 id 与移动段 id 是 <c>StableUuid(journeyId|用途)</c>，
/// RIoT 单号是 <see cref="ChargingIdentity.UpperIdFor"/>，周期 id 是 <see cref="ChargingIdentity.CycleIdFor"/>。重试、重启都是同一组 id
/// （<c>REQ-0283</c>：不换 <c>upperId</c>）。
/// </para>
/// <para>
/// <b>没有需求，就没有需求才有的列</b>：与空闲返回同一个形态（control-server#386 的方案甲），充电桩这一段放在取货那几列上与停靠行上。
/// </para>
/// <para>
/// <b>订单意图就是引擎给任何后续腿构造的那一个</b>（<see cref="JourneyPlanBuilder.LegIntent"/>），只把 <c>Purpose</c> 换成
/// <see cref="IntentPurpose"/>：形态因此经 <see cref="JourneyStopRoles.OrderShapeOf"/> 从停靠角色 <c>CHARGER</c> 取为 <c>CHARGE</c>
/// （<c>move(桩) + act(78,1,0)</c>），不手写——停靠角色若没精确写成 <c>CHARGER</c>，充电单会降成单段、车到桩不通电。
/// </para>
/// </remarks>
internal static class ChargingJourneyShape
{
    /// <summary>订单意图的 <c>Purpose</c>：开往充电桩。</summary>
    public const string IntentPurpose = "TO_CHARGER";

    /// <summary>旅程行上的派车分区：充电不属于任何派车分区，这一栏只为非空约束写一个一看就知道不是分区的值。</summary>
    public const string NoDispatchZone = "CHARGING";

    /// <summary>途中业务状态的两张（<c>ALLOCATED</c>、<c>EN_ROUTE</c>）与计划的两张（腿 <c>PLANNED</c>、<c>ACTIVE</c>）的消息 id。</summary>
    public static string AllocatedStateMessageId(string journeyId) => JourneyPlanBuilder.StableGuid(journeyId, "charger-vehicle-state-allocated");

    /// <inheritdoc cref="AllocatedStateMessageId"/>
    public static string EnRouteStateMessageId(string journeyId) => JourneyPlanBuilder.StableGuid(journeyId, "charger-vehicle-state-en-route");

    /// <inheritdoc cref="AllocatedStateMessageId"/>
    public static string AllocatedPlanMessageId(string journeyId) => JourneyPlanBuilder.StableGuid(journeyId, "charger-plan-allocated");

    /// <inheritdoc cref="AllocatedStateMessageId"/>
    public static string EnRoutePlanMessageId(string journeyId) => JourneyPlanBuilder.StableGuid(journeyId, "charger-plan-en-route");

    /// <summary>到桩那一张计划（那条 <c>CHARGER</c> 腿报 <c>ARRIVED</c>）的消息 id（批次9-07）。</summary>
    public static string ArrivedPlanMessageId(string journeyId) => JourneyPlanBuilder.StableGuid(journeyId, "charger-plan-arrived");

    /// <summary>开始充电那一张业务状态（<c>chargingCycleState=CHARGING</c>）的消息 id（批次9-07）。</summary>
    public static string ChargingStateMessageId(string journeyId) => JourneyPlanBuilder.StableGuid(journeyId, "charger-vehicle-state-charging");

    /// <summary>
    /// 充不上之后清桩中的那一张计划（那条 <c>CHARGER</c> 腿留着，车载端靠它取原桩的站点号，调度 09-30 对齐第 6 条）的消息 id（批次9-08）。
    /// </summary>
    public static string ClearingPlanMessageId(string journeyId) => JourneyPlanBuilder.StableGuid(journeyId, "charger-plan-clearing");

    /// <summary>
    /// 清桩中那一张业务状态（<c>chargingCycleState=UNABLE_TO_CHARGE</c>、<c>activePurpose=CLEARING_MAINTENANCE</c>）的消息 id（批次9-08）。
    /// </summary>
    public static string ClearingStateMessageId(string journeyId) => JourneyPlanBuilder.StableGuid(journeyId, "charger-vehicle-state-clearing");

    public static (JourneyRuntimeRow Runtime, JourneyStopRow Stop, OrderIntent Intent) Build(
        string journeyId,
        FleetVehicle vehicle,
        ChargerRosterEntry charger,
        long chargingPolicyVersion,
        string publishedBatteryState,
        JourneyRuntimeOptions options,
        DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(journeyId);
        ArgumentNullException.ThrowIfNull(vehicle);
        ArgumentNullException.ThrowIfNull(charger);
        ArgumentException.ThrowIfNullOrWhiteSpace(publishedBatteryState);
        ArgumentNullException.ThrowIfNull(options);

        string Id(string purpose) => JourneyPlanBuilder.StableGuid(journeyId, purpose);
        string upperId = ChargingIdentity.UpperIdFor(journeyId);
        string legId = Id("charger-leg");
        string operationSessionId = Id("operation-session");
        JourneyRuntimeRow runtime = new()
        {
            JourneyId = journeyId,
            DemandId = null,
            Stage = JourneyRuntimeStage.AwaitingPickupArrival,
            AgvId = vehicle.AgvId,
            VehicleKey = vehicle.VehicleKey,
            AgvLifecycleGeneration = vehicle.AgvLifecycleGeneration,
            MapId = options.MapId,
            MapIdentity = options.MapIdentity,
            DispatchZone = NoDispatchZone,
            RouteEvidenceId = journeyId,
            PickupStationId = charger.StationName,
            PickupStationRiotId = charger.StationId,
            OperationSessionId = operationSessionId,
            PickupMovementLegId = legId,
            PickupUpperId = upperId,
            DispatchGeneration = options.DispatchGeneration,
            // REQ-0282：这一次充电按分配它的那一版策略冻结；batteryState 的第一版按同一份事实投影。
            ChargingPolicyVersion = chargingPolicyVersion,
            PublishedBatteryState = publishedBatteryState,
            VehicleBusinessRevision = 1,
            WorklistRevision = 1,
            PlanRevision = 1,
            VehicleBusinessMessageId = AllocatedStateMessageId(journeyId),
            WorklistMessageId = Id("charger-worklist"),
            PlanMessageId = AllocatedPlanMessageId(journeyId),
            CreatedAt = now,
            UpdatedAt = now,
        };
        JourneyStopRow stop = new()
        {
            StopId = journeyId + ":charger",
            JourneyId = journeyId,
            Sequence = 1,
            StopRole = JourneyStopRoles.Charger,
            StationId = charger.StationName,
            StationRiotId = charger.StationId,
            DispatchZone = NoDispatchZone,
            OperationSessionId = operationSessionId,
            MovementLegId = legId,
            UpperId = upperId,
            VehicleBusinessMessageId = runtime.VehicleBusinessMessageId,
            WorklistMessageId = runtime.WorklistMessageId,
            PlanMessageId = runtime.PlanMessageId,
            Status = JourneyStopStatuses.Pending,
            CreatedAt = now,
        };
        return (runtime, stop, IntentOf(runtime, stop, now));
    }

    /// <summary>这一个停靠的订单意图：<see cref="JourneyPlanBuilder.LegIntent"/> 的那一个，<c>Purpose</c> 为 <see cref="IntentPurpose"/>。</summary>
    public static OrderIntent IntentOf(JourneyRuntimeRow runtime, JourneyStopRow stop, DateTimeOffset createdAt) =>
        JourneyPlanBuilder.LegIntent(runtime, stop, createdAt) with { Purpose = IntentPurpose };
}
