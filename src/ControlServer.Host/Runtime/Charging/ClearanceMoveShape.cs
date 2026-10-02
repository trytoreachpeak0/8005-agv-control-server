using System.Globalization;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;

namespace ControlServer.Host.Runtime.Charging;

/// <summary>
/// 清桩开往等待点的第 n 次尝试（批次9-11，control-server#409）在充电旅程上留下的停靠与订单意图，以及它发给车载端的快照的 id。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么挂在原充电旅程上</b>：引擎要求一辆车同一时刻只有一趟未完成的旅程（<c>JourneyRuntimeEngine</c> 的 doubleBooked 校验），而这辆车此刻的旅程就是
/// 这趟充电——它持有 <c>CLEARING_MAINTENANCE</c> 用途与原桩的独占。所以开往等待点是这趟旅程的第二个停靠（角色 <c>WAITING_POINT</c>），用途、
/// 桩独占都不换持有者，等待点预占也记在同一个旅程 id 上。
/// </para>
/// <para>
/// <b>身份全部从旅程 id 与尝试序号派生</b>：停靠 <c>{journeyId}:clearance-move:{n}</c>、RIoT 单号 <c>W2G-CHARGE-…-CLR{n}</c>、移动段与快照 id
/// <c>StableUuid(journeyId|clearance-move-…-{n})</c>。同一次尝试重跑、崩溃后重来都是同一组 id（<c>REQ-0294</c>：稳定 <c>upperId</c>）；新的尝试
/// 只在上一次以释放预占结束之后才有，序号加一，id 不撞。
/// </para>
/// <para>
/// 订单形态经 <see cref="JourneyStopRoles.OrderShapeOf"/> 从角色 <c>WAITING_POINT</c> 取为单段 <c>move</c>（白名单 1.2 形态一）。
/// </para>
/// </remarks>
internal static class ClearanceMoveShape
{
    /// <summary>订单意图的 <c>Purpose</c>：清桩开往等待点。</summary>
    public const string IntentPurpose = "CLEARANCE_TO_WAITING_POINT";

    private const string StopIdMarker = ":clearance-move:";

    public static string StopIdFor(string journeyId, int attempt) =>
        journeyId + StopIdMarker + attempt.ToString(CultureInfo.InvariantCulture);

    public static bool IsClearanceMove(JourneyStopRow stop) =>
        stop.StopRole == JourneyStopRoles.WaitingPoint && stop.StopId.Contains(StopIdMarker, StringComparison.Ordinal);

    public static string UpperIdFor(string journeyId, int attempt) =>
        ChargingIdentity.UpperIdFor(journeyId) + "-CLR" + attempt.ToString(CultureInfo.InvariantCulture);

    /// <summary>这次尝试在途中的计划（原桩腿 <c>COMPLETED</c>，等待点腿 <c>ACTIVE</c>）。</summary>
    public static string PlanMessageId(string journeyId, int attempt) =>
        JourneyPlanBuilder.StableGuid(journeyId, $"clearance-move-plan-{attempt}");

    /// <summary>这次尝试在途中的业务状态（仍是 <c>CLEARING_MAINTENANCE</c>、<c>UNABLE_TO_CHARGE</c>）。</summary>
    public static string StateMessageId(string journeyId, int attempt) =>
        JourneyPlanBuilder.StableGuid(journeyId, $"clearance-move-state-{attempt}");

    /// <summary>这次尝试没到点就结束、车回到清桩中之后的计划（原桩腿 <c>ARRIVED</c>，车载端靠它取原桩站点号做人工清桩）。</summary>
    public static string BackPlanMessageId(string journeyId, int attempt) =>
        JourneyPlanBuilder.StableGuid(journeyId, $"clearance-move-back-plan-{attempt}");

    /// <inheritdoc cref="BackPlanMessageId"/>
    public static string BackStateMessageId(string journeyId, int attempt) =>
        JourneyPlanBuilder.StableGuid(journeyId, $"clearance-move-back-state-{attempt}");

    /// <summary>停靠 id 里的尝试序号。</summary>
    public static int AttemptOf(JourneyStopRow stop) =>
        int.Parse(stop.StopId[(stop.StopId.LastIndexOf(':') + 1)..], NumberStyles.None, CultureInfo.InvariantCulture);

    /// <summary>这趟旅程到第 <paramref name="attempts"/> 次为止每一次尝试的四张快照 id。</summary>
    public static IEnumerable<string> SnapshotIds(string journeyId, int attempts) =>
        Enumerable.Range(1, attempts).SelectMany(attempt => new[]
        {
            PlanMessageId(journeyId, attempt), StateMessageId(journeyId, attempt),
            BackPlanMessageId(journeyId, attempt), BackStateMessageId(journeyId, attempt),
        });

    public static (JourneyStopRow Stop, OrderIntent Intent) Build(
        JourneyRuntimeRow runtime,
        JourneyStopRow charger,
        int attempt,
        int stationId,
        string stationName,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(charger);
        ArgumentException.ThrowIfNullOrWhiteSpace(stationName);
        JourneyStopRow stop = new()
        {
            StopId = StopIdFor(runtime.JourneyId, attempt),
            JourneyId = runtime.JourneyId,
            Sequence = charger.Sequence + attempt,
            StopRole = JourneyStopRoles.WaitingPoint,
            StationId = stationName,
            StationRiotId = stationId,
            DispatchZone = charger.DispatchZone,
            OperationSessionId = charger.OperationSessionId,
            MovementLegId = JourneyPlanBuilder.StableGuid(runtime.JourneyId, $"clearance-move-leg-{attempt}"),
            UpperId = UpperIdFor(runtime.JourneyId, attempt),
            VehicleBusinessMessageId = StateMessageId(runtime.JourneyId, attempt),
            WorklistMessageId = charger.WorklistMessageId,
            PlanMessageId = PlanMessageId(runtime.JourneyId, attempt),
            Status = JourneyStopStatuses.Pending,
            CreatedAt = now,
        };
        return (stop, JourneyPlanBuilder.LegIntent(runtime, stop, now) with { Purpose = IntentPurpose });
    }
}

/// <summary>清桩开往等待点的预占在释放时写在它经过上的原因（等待点的经过，不在旅程行上）。</summary>
internal static class ClearanceMoveReleaseReasons
{
    /// <summary>单从没发出过、出发前复核不再成立而撤回：什么也没发生过，不排除这个点，下一轮照常重评。</summary>
    public const string Withdrawn = "CLEARANCE_MOVE_WITHDRAWN";

    /// <summary>
    /// 建单发出过、RIoT 持续答查无此单，车证明停稳、名下没有未完成的单（<c>REQ-0296</c> 的「证明没单没动」）：放预占，重评时不选这个点。
    /// </summary>
    public const string NeverAppeared = "CLEARANCE_MOVE_NEVER_APPEARED";

    /// <summary>
    /// 单被人在 RIoT 里取消或删除、或 FAILED 后故障由人清除，车证明停稳：放预占；这个周期不再自动出发，只留人工清桩（control-server#404 的
    /// 「清桩阶段不重建」）。
    /// </summary>
    public const string Ended = "CLEARANCE_MOVE_ENDED";
}
