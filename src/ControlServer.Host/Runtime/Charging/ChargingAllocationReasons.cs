namespace ControlServer.Host.Runtime.Charging;

/// <summary>
/// 一辆空闲车这一轮为什么取得、或为什么没取得充电分配（<c>REQ-0170</c>～<c>0173</c>、<c>REQ-0290</c>；批次9-06，control-server#404）。
/// 写进 <see cref="ChargingAllocationVerdict"/>、<see cref="ChargingAllocationBoard"/> 与日志（事件 2240、2241）；看板（批次9-10）读那块板。
/// </summary>
/// <remarks>
/// 故障阻断、投运策略与车辆动态事实那几格不在这里另起名字：它们用的是派车链与空闲返回共用的判定
/// （<c>VehicleNewPurposeReadiness</c>、<c>VehicleDynamicFactsCriterion.Evaluate</c>），码也是那边的。
/// </remarks>
public static class ChargingAllocationReasons
{
    /// <summary>形成了承诺：周期、<c>CHARGING</c> 用途占有、<c>CHARGER</c> 预占、旅程行与订单意图在同一次保存里。</summary>
    public const string Committed = "CHARGING_COMMITTED";

    /// <summary>这辆车此刻不需要充电：电量不低于它这一轮所用策略版本的强制充电线。</summary>
    public const string NotRequired = "CHARGING_NOT_REQUIRED";

    /// <summary>这辆车在服务端持有的人工充电等待中：不分配，要先经「充电后返回服务」。</summary>
    public const string ManualChargingHoldActive = "CHARGING_MANUAL_HOLD_ACTIVE";

    /// <summary>车已有用途占有（搬运、空闲返回，或一次还没结束的充电）：已开始或结果未知的用途保持占有（<c>REQ-0290</c>）。</summary>
    public const string VehicleHasPurpose = "CHARGING_VEHICLE_HAS_PURPOSE";

    /// <summary>车辆充电资格暂停着（批次9-09 写）：恢复之前不分配。</summary>
    public const string VehicleEligibilityHeld = "CHARGING_VEHICLE_ELIGIBILITY_HELD";

    /// <summary>
    /// 这辆车自己还占着一个充电桩（上一次充电已结束，桩还没按 <c>REQ-0173</c> 的三项确认释放）：不再向它分配，免得向原桩再发一次充电动作。
    /// </summary>
    public const string VehicleStillHoldsCharger = "CHARGING_VEHICLE_STILL_HOLDS_CHARGER";

    /// <summary>不知道车在哪个站：路网代价没有起点。</summary>
    public const string VehiclePositionUnknown = "CHARGING_VEHICLE_POSITION_UNKNOWN";

    /// <summary>路网引擎没开，或这张图的快照不可用：最近优先无从计算，不猜。</summary>
    public const string RouteGraphUnavailable = "CHARGING_ROUTE_GRAPH_UNAVAILABLE";

    /// <summary>
    /// 名册里没有这辆车可用的桩（从没导入过、当前版本为空，或没有一条在本图且把这辆车列在范围内）：置人工充电等待并告警（<c>REQ-0171</c>，规格 8.6）。
    /// </summary>
    public const string RosterEmptyManualHold = "CHARGING_ROSTER_EMPTY_MANUAL_HOLD";

    /// <summary>这辆车的充电单短时间内第二次被取消、删除或 FAILED：不再自动分配，置人工充电等待并告警。</summary>
    public const string RepeatedlyFailedManualHold = "CHARGING_REPEATEDLY_FAILED_MANUAL_HOLD";

    /// <summary>名册里这辆车可用的桩逐个核验后一个都不剩；逐桩原因在细节里（例如 <c>211=CHARGER_RESERVED_OR_OCCUPIED</c>）。车留在队里。</summary>
    public const string NoChargerAvailable = "CHARGING_NO_CHARGER_AVAILABLE";

    /// <summary>候选算出来了，承诺那一次保存被数据库约束拒掉（车或桩被别人先拿到）；整笔回滚，本轮不换桩，下一轮重评。</summary>
    public const string CommitmentRefused = "CHARGING_COMMITMENT_REFUSED";

    /// <summary>评估这辆车时出了异常：它这一轮什么也没留下，别的车照常。</summary>
    public const string EvaluationFailed = "CHARGING_EVALUATION_FAILED";

    // ---- 候选桩被排除的原因（REQ-0170 的候选链，按这个顺序判）----

    /// <summary>这辆车最近一次充电在这个桩上已确认失败，还在冷却期内（<c>REQ-0170</c>「排除刚失败的站点」）。</summary>
    public const string ChargerFailedJustNow = "CHARGER_FAILED_JUST_NOW";

    /// <summary>这个桩的分配暂停着（批次9-08、9-09 写）。</summary>
    public const string ChargerAllocationHeld = "CHARGER_ALLOCATION_HELD";

    /// <summary>这个桩不在 RIoT 当前地图的站点目录里，或站名与名册登记的不一致：身份无效。</summary>
    public const string ChargerNotOnCurrentMap = "CHARGER_NOT_ON_CURRENT_MAP";

    /// <summary>本服务端已有这个桩的独占行：被别的车预占或占用着（<c>REQ-0173</c>：预占成功后不可抢占）。</summary>
    public const string ChargerReservedOrOccupied = "CHARGER_RESERVED_OR_OCCUPIED";

    /// <summary>这个桩的占用事实读不到或过期（车辆位置、未完成订单清单或订单目的站）：未知即不分配（<c>REQ-0170</c>，fail-closed）。</summary>
    public const string ChargerOccupancyUnknown = "CHARGER_OCCUPANCY_UNKNOWN";

    /// <summary>本项目的另一辆车停在这个桩上。</summary>
    public const string ChargerOccupiedByVehicle = "CHARGER_OCCUPIED_BY_VEHICLE";

    /// <summary>本项目车辆有一张在跑的单以这个桩为目的站。</summary>
    public const string ChargerTargetedByRunningOrder = "CHARGER_TARGETED_BY_RUNNING_ORDER";

    /// <summary>路网上从车的位置到不了这个桩，或它不在路网上。</summary>
    public const string ChargerUnreachable = "CHARGER_UNREACHABLE";
}
