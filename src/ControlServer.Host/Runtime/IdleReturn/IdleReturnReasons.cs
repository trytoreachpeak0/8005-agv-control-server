namespace ControlServer.Host.Runtime.IdleReturn;

/// <summary>
/// 一辆空闲车这一轮为什么形成、或为什么没形成空闲返回承诺（<c>REQ-0291</c>、<c>REQ-0292</c>、<c>REQ-0293</c> 前半句）。
/// 每条资格一个码，写进 <see cref="IdleReturnVerdict"/> 与日志（事件 2198、2199）；看板（批次8-21，control-server#392）读它。
/// </summary>
/// <remarks>
/// 车辆动态事实那几格（新鲜、地图、在线、停止、RIoT 上有没有它的订单……）不在这里另起名字：它们用的是派车那条链的
/// <c>VehicleDynamicFactsCriterion.Evaluate</c>，码也是那边的（例如 <c>RIOT_VEHICLE_FACT_STALE</c>、<c>RIOT_VEHICLE_ORDER_OCCUPIED</c>）。
/// 两处各判一份「这辆车现在能不能动」会分叉。
/// </remarks>
public static class IdleReturnReasons
{
    /// <summary>形成了承诺：用途占有与等待点预占在同一次保存里取得。</summary>
    public const string Committed = "IDLE_RETURN_COMMITTED";

    /// <summary>开关关着（默认）。关掉只挡新承诺，既有承诺不动。</summary>
    public const string Disabled = "IDLE_RETURN_DISABLED";

    /// <summary>这一轮任务优先派车选中了它，或它这一轮中途退出了（预算用尽、受理时垮掉）：空闲返回只是兜底。</summary>
    public const string NotLeftOverByTransportThisRound = "IDLE_RETURN_NOT_LEFT_OVER_BY_TRANSPORT";

    /// <summary>车已有用途占有：搬运、或一次还没收敛的空闲返回。已开始或结果未知的用途保持占有（<c>REQ-0290</c>）。</summary>
    public const string VehicleHasPurpose = "IDLE_RETURN_VEHICLE_HAS_PURPOSE";

    /// <summary>车还持有某个站点的预占或占用（例如停在上一次返回的等待点、等离点证据）：它已经在一个等待点上了。</summary>
    public const string VehicleHoldsStation = "IDLE_RETURN_VEHICLE_HOLDS_STATION";

    /// <summary>车还有一趟没结束的旅程：有下一业务目标。</summary>
    public const string HasNextBusinessTarget = "IDLE_RETURN_HAS_NEXT_BUSINESS_TARGET";

    /// <summary>一张外来订单正占着这辆车（<c>REQ-0164</c>，control-server#330）。</summary>
    public const string ForeignOrderRunning = "IDLE_RETURN_FOREIGN_ORDER_RUNNING";

    /// <summary>本服务端给这辆车的某张订单结果未知：建单发出过而没核实（<c>CREATE_ATTEMPTED</c>、<c>RESULT_UNKNOWN</c>、<c>TERMINAL_RECONCILIATION_REQUIRED</c>）。</summary>
    public const string OwnOrderResultUnknown = "IDLE_RETURN_OWN_ORDER_RESULT_UNKNOWN";

    /// <summary>电量读不到，或车正在充电。</summary>
    public const string BatteryUnknownOrCharging = "IDLE_RETURN_BATTERY_UNKNOWN_OR_CHARGING";

    /// <summary>电量低于强制充电入口线（<see cref="ControlServer.Application.IMandatoryChargeLine"/>；按车读充电策略版本，control-server#403）。</summary>
    public const string BelowMandatoryChargeLine = "IDLE_RETURN_BELOW_MANDATORY_CHARGE_LINE";

    /// <summary>不知道车在哪个站：路网代价没有起点。</summary>
    public const string VehiclePositionUnknown = "IDLE_RETURN_VEHICLE_POSITION_UNKNOWN";

    /// <summary>路网引擎没开，或这张图的快照不可用：<c>RouteCost</c> 可达无从核验，不猜（<c>REQ-0293</c>）。</summary>
    public const string RouteGraphUnavailable = "IDLE_RETURN_ROUTE_GRAPH_UNAVAILABLE";

    /// <summary>候选等待点逐个核验后一个都不剩：不接这辆车、被占、不可达……逐点原因写在日志里。</summary>
    public const string NoWaitingPointAvailable = "IDLE_RETURN_NO_WAITING_POINT_AVAILABLE";

    /// <summary>候选算出来了，承诺那一次保存被数据库约束拒掉（车或站被别人先拿到）；整笔回滚，本轮不换点，下一轮重评。</summary>
    public const string CommitmentRefused = "IDLE_RETURN_COMMITMENT_REFUSED";

    /// <summary>评估这辆车时出了异常：它这一轮什么也没留下，别的车照常。</summary>
    public const string EvaluationFailed = "IDLE_RETURN_EVALUATION_FAILED";

    /// <summary>
    /// 这辆车上一趟空闲返回的单刚被取消、删除，或 FAILED 后故障刚被人清除（已确认失败，control-server#390）：收尾后
    /// <c>JourneyRuntime:OwnOrderRebuildDelay</c>（默认 30 秒）之内不承诺新的空闲返回，给车旁的人走开或把车停住的时间——
    /// 与搬运自建单被取消后的第一道护栏（control-server#318）同一个时长、同一个理由。
    /// </summary>
    public const string CooldownAfterEndedOrder = "IDLE_RETURN_COOLDOWN_AFTER_ENDED_ORDER";

    /// <summary>
    /// 这辆车自最近一趟非空闲返回的旅程之后，已确认失败的空闲返回在 <c>JourneyRuntime:OwnOrderRebuildRepeatWindow</c>（默认 10 分钟）
    /// 之内累计两次（夹在中间的别的收尾不计数、也不打断计数）：反复取消说明有人要这辆车别动，不再自动承诺空闲返回，告警等人。
    /// 与搬运的第三道护栏（<c>REQ-0361</c>）对等；窗口过去不自动解除，车做了别的旅程（被派了搬运）才解除。
    /// </summary>
    public const string StoppedAfterRepeatedEndedOrders = "IDLE_RETURN_STOPPED_AFTER_REPEATED_ENDED_ORDERS";

    // 候选等待点被排除的原因：前五个是 WaitingPointEligibilityReasons（批次8-17）的码，原样沿用；下面两个是本票的。

    /// <summary>这个等待点此刻被别的承诺预占或占用着。</summary>
    public const string PointTaken = "WAITING_POINT_RESERVED_OR_OCCUPIED";

    /// <summary>路网上从车的位置到不了这个等待点，或它不在路网上。</summary>
    public const string PointUnreachable = "WAITING_POINT_UNREACHABLE";

    /// <summary>
    /// 这辆车上一次开往这个等待点已确认失败（单被取消或删除后车停稳、单 FAILED 后故障被人工清除；control-server#390）：
    /// 下一次承诺排除原失败点（<c>REQ-0296</c> 末句）。车做完别的用途（有了更新的旅程）之后不再排除。
    /// </summary>
    public const string PointFailedLastAttempt = "WAITING_POINT_FAILED_LAST_ATTEMPT";
}
