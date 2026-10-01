using ControlServer.Application;

namespace ControlServer.Host.Runtime.Charging;

/// <summary>
/// 充电旅程（批次9-06，control-server#404）写在旅程行上的阻断码、收尾码，以及周期、用途占有与桩独占的结束原因。
/// </summary>
/// <remarks>
/// <para>
/// 分配那一段的码在 <see cref="ChargingAllocationReasons"/>；这里是承诺之后的：出发前安全门、建单、去桩途中与失败收尾。到桩之后（充电中、
/// 充满、离桩、充不上、清桩）是批次9-07～9-09 的。
/// </para>
/// <para>
/// <b>保持类</b>（旅程停在原处，车、目标桩、用途占有、预占、周期都不放）：<see cref="DepartureNotProven"/>（只在宽限期内）、
/// <see cref="CycleMissing"/>，以及建单结果的 <c>CHARGER_{结果}</c>。途中单停住用引擎共用的那几个码（<c>ORDER_HANG</c> 等）。
/// </para>
/// <para>
/// <b>撤回类</b>（独立审查 M1、M2）：单从没发出过的承诺，出发前复核不再成立就整笔撤回——周期结束、用途占有与桩预占同一次保存里释放，
/// 车下一轮按正常链重评。什么也没发出过、车没有动过，所以预占当场放，不计入「两次即停」、不进冷却：<see cref="WithdrawnReservationLost"/>、
/// <see cref="WithdrawnChargerNoLongerEligible"/>、<see cref="WithdrawnNoLongerRequired"/>、<see cref="WithdrawnDepartureNotProven"/>。
/// </para>
/// <para>
/// <b>已确认失败的收尾类</b>：<see cref="OrderEnded"/>、<see cref="OrderFailed"/>、<see cref="OrderNeverAppeared"/>。预占按三项确认释放，
/// 计入「两次即停」，之后有冷却。
/// </para>
/// </remarks>
public static class ChargingExecutionReasons
{
    /// <summary>建单结果码的前缀：<c>CHARGER_ResultUnknown</c> 等，与搬运的 <c>PICKUP_</c>、空闲返回的 <c>WAITING_POINT_</c> 同一个形状。</summary>
    public const string LegName = "CHARGER";

    /// <summary>
    /// 出发前安全门没过（v2 发不了非业务移动的 <c>PreDepartureSafetyCheck</c>，服务端等价门，与空闲返回同一个）：不建单，承诺、预占与周期保持，
    /// 下一轮重评。
    /// </summary>
    public const string DepartureNotProven = "CHARGING_DEPARTURE_NOT_PROVEN";

    // ---- 撤回码（单从没发出过）----

    /// <summary>这趟充电的桩预占行已不在、或不是这一趟的，而单还没发出过：承诺作废，车下一轮重评。</summary>
    public const string WithdrawnReservationLost = "CHARGING_WITHDRAWN_RESERVATION_LOST";

    /// <summary>
    /// 出发前对这一个桩重跑桩侧判定，不再成立：桩不在当前生效名册里（含名册置空——窗口外不自动充电）、桩被置分配暂停、不在当前地图或站名变了、
    /// 占用事实不能确认空闲、路线不可达。承诺作废，车下一轮按正常链重评。
    /// </summary>
    public const string WithdrawnChargerNoLongerEligible = "CHARGING_WITHDRAWN_CHARGER_NO_LONGER_ELIGIBLE";

    /// <summary>出发前这辆车按它冻结的那一版策略已不低于强制充电线（或电量读不到、正被人充着电）：承诺作废。</summary>
    public const string WithdrawnNoLongerRequired = "CHARGING_WITHDRAWN_NO_LONGER_REQUIRED";

    /// <summary>
    /// 出发前安全门持续不过，超过 <c>JourneyRuntime:OwnOrderRebuildDelay</c>：承诺作废，不再占着桩等。门恢复之后车按正常链重新排队。
    /// </summary>
    public const string WithdrawnDepartureNotProven = "CHARGING_WITHDRAWN_DEPARTURE_NOT_PROVEN";

    /// <summary>这趟充电旅程找不到它未结束的充电周期：本服务端自己的不变量坏了，不建单、不动车，等人查。</summary>
    public const string CycleMissing = "CHARGING_CYCLE_MISSING";

    // ---- 收尾码 ----

    /// <summary>
    /// 充电单在 RIoT 被取消或删除（不是本服务端取消的），车已证明停稳、没有活动订单：已确认失败，周期结束、用途占有释放；
    /// 桩预占按 <c>REQ-0173</c> 三项确认后才释放。<b>不重建</b>（票面第 10 条；<c>REQ-0360</c> 的重建承载需求，充电单没有需求）。
    /// </summary>
    public const string OrderEnded = "CHARGING_ORDER_ENDED";

    /// <summary>充电单在 RIoT 上 FAILED、故障已由人工清除：已确认失败，同上。</summary>
    public const string OrderFailed = "CHARGING_ORDER_FAILED";

    /// <summary>
    /// 建单发出过而结果未知，RIoT 持续答「查无此单」满 <c>JourneyRuntime:ChargingOrderAbsentAbandonAfter</c>，车证明停稳、名下没有未完成的单：
    /// 这张单被放弃，按已确认失败收尾。它事后若在这辆车上跑起来，由外来单监督器取消并告警（<see cref="AbandonedChargeOrders"/>）。
    /// </summary>
    public const string OrderNeverAppeared = "CHARGING_ORDER_NEVER_APPEARED";

    /// <summary>桩预占的释放原因：周期已以失败结束，充电已停、原车不在桩上、桩位可确认空闲三项都确认了（<c>REQ-0173</c>）。</summary>
    public const string ReservationReleasedAfterEndedCycle = "CHARGER_RELEASED_AFTER_ENDED_CYCLE";

    // ---- 到桩之后（批次9-07，control-server#405）----

    /// <summary>
    /// 到桩之后过了宽限（<c>JourneyRuntime:OwnOrderRebuildDelay</c>）一直读不到 <c>batteryState=CHARGING</c>：不是充不上的确认（<c>REQ-0175</c>），
    /// 周期、用途、桩占用都保持，告警一次交人处理；不暂停桩、不暂停车。读到 <c>CHARGING</c> 时清掉。
    /// </summary>
    public const string ChargerNotEngaged = "CHARGER_NOT_ENGAGED";

    /// <summary>
    /// RIoT 车辆观测丢失（读不到、或报离线）：按「车可能仍在桩上」保持一切，不发任何命令；持续超过 <c>JourneyRuntime:OwnOrderRebuildRepeatWindow</c>
    /// 只升级告警（<c>REQ-0287</c>）。观测恢复时清掉。
    /// </summary>
    public const string VehicleObservationLost = "CHARGING_VEHICLE_OBSERVATION_LOST";

    /// <summary>
    /// 电量遥测丢失（读不到电量或读数过期）：暂停完成判断，恢复后以新鲜、连续的样本重新观察，不拿丢失前的读数（<c>REQ-0287</c>）；持续超时只升级告警。
    /// </summary>
    public const string BatteryTelemetryLost = "CHARGING_BATTERY_TELEMETRY_LOST";

    /// <summary>
    /// 到桩证据满足了，这个桩的独占却已不是这一趟的（被人工释放、之后可能归了别的车，control-server#406）：不转占用、不释放任何东西，保持并告警，
    /// 交人到现场确认车停在哪里。
    /// </summary>
    public const string ReservationLostAtArrival = "CHARGING_RESERVATION_LOST_AT_ARRIVAL";

    /// <summary>
    /// 充电单已是终态 <c>SUCCESS</c>，车辆那一半的到桩证据却一直不成立（车读到在别的站、读不到当前站、没停稳）：周期保持 <c>EN_ROUTE</c>、预占保持，
    /// 没有超时分支；持续超过 <c>JourneyRuntime:OwnOrderRebuildRepeatWindow</c> 告警一次（独立审查 M1）。证据成立时清掉。
    /// </summary>
    public const string ArrivalNotProven = "CHARGING_ARRIVAL_NOT_PROVEN";

    /// <summary>
    /// 已确认的充电单在到桩之前对 RIoT 答「查无此单」（真实形态 HTTP 200 不带 result，或 404），车也不在桩上充电：用途、预占、周期都保持，不建单、
    /// 不发命令；持续超过 <c>JourneyRuntime:OwnOrderRebuildRepeatWindow</c> 告警一次（独立审查 M1）。单重新读到时清掉。
    /// </summary>
    public const string OrderNotFound = "CHARGING_ORDER_NOT_FOUND";

    /// <summary>
    /// 充满之后一直留在桩上的车电量又掉到强制充电线以下（独立审查 S6）：它持有的这一轮以这个原因收尾，桩随之释放、同一轮按正常分配链在原桩上重新充电。
    /// 不是失败。
    /// </summary>
    public const string RechargedOnHeldCharger = "CHARGING_RECHARGED_ON_HELD_CHARGER";

    /// <summary>桩占用的释放原因：充满的车没离开、又需要充电，同一个桩交给它的下一轮（独立审查 S6）。</summary>
    public const string ChargerReleasedForRecharge = "CHARGER_RELEASED_FOR_RECHARGE";

    /// <summary>到桩之后那一段自己写、也由它自己清掉的码：单 <c>SUCCESS</c> 那一支不替它们清。</summary>
    public static IReadOnlySet<string> AtChargerCodes { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        ChargerNotEngaged, VehicleObservationLost, BatteryTelemetryLost, ReservationLostAtArrival,
    };

    /// <summary>
    /// 充满（<c>COMPLETE</c>）：充电旅程的收尾码与 <c>CHARGING</c> 用途占有的释放原因。不是释放桩——桩仍是这辆车的占用，等离桩三项确认（<c>REQ-0281</c>）。
    /// </summary>
    public const string Completed = "CHARGING_COMPLETE";

    /// <summary>充满的车取得下一用途、离桩三项确认之后，充电周期的结束原因（<c>REQ-0173</c>）。不是失败。</summary>
    public const string Departed = "CHARGING_DEPARTED";

    /// <summary>桩占用的释放原因：充满的车已停止充电、离开了桩、桩位可确认空闲（<c>REQ-0173</c>）。</summary>
    public const string ChargerReleasedOnDeparture = "CHARGER_RELEASED_ON_DEPARTURE";

    /// <summary>已确认失败的收尾码：这辆车之后进冷却（冷却期内一个桩都不分）、计入「两次即停」的那几种。</summary>
    public static IReadOnlySet<string> ConfirmedFailures { get; } =
        new HashSet<string>(StringComparer.Ordinal) { OrderEnded, OrderFailed, OrderNeverAppeared };

    /// <summary>建单没有确认时写在旅程上的码：<c>CHARGER_{Outcome}</c>。引擎与看板说明都经这里拼。</summary>
    public static string LegOutcomeCode(MovementDispatchOutcome outcome) => $"{LegName}_{outcome}";

    /// <summary>会被写进 <c>BlockReasonCode</c> 的建单结果码：除了确认（清掉码）与「终结后要对账」（按单已终结往下判）。</summary>
    public static IReadOnlyList<string> LegOutcomeCodes { get; } =
    [
        .. Enum.GetValues<MovementDispatchOutcome>()
            .Where(outcome => outcome is not (MovementDispatchOutcome.Confirmed or MovementDispatchOutcome.TerminalReconciliationRequired))
            .Select(LegOutcomeCode),
    ];
}
