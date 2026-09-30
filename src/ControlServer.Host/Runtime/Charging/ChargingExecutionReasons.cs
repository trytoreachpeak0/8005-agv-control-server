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
/// <b>保持类</b>（旅程停在原处，车、目标桩、用途占有、预占、周期都不放）：<see cref="DepartureNotProven"/>、<see cref="ReservationNotHeld"/>、
/// <see cref="CycleMissing"/>，以及建单结果的 <c>CHARGER_{结果}</c>。途中单停住用引擎共用的那几个码（<c>ORDER_HANG</c> 等）。
/// <b>收尾类</b>：<see cref="OrderEnded"/>、<see cref="OrderFailed"/>。
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

    /// <summary>这趟充电的桩预占行已不在、或不是这一趟的，而单还没发出过：不建单，等人查（充电桩的人工释放归批次9-08）。</summary>
    public const string ReservationNotHeld = "CHARGING_RESERVATION_NOT_HELD";

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

    /// <summary>桩预占的释放原因：周期已以失败结束，充电已停、原车不在桩上、桩位可确认空闲三项都确认了（<c>REQ-0173</c>）。</summary>
    public const string ReservationReleasedAfterEndedCycle = "CHARGER_RELEASED_AFTER_ENDED_CYCLE";

    /// <summary>已确认失败的收尾码：这辆车下一次分配要在冷却期内排除原桩、二次即停的那几种。</summary>
    public static IReadOnlySet<string> ConfirmedFailures { get; } =
        new HashSet<string>(StringComparer.Ordinal) { OrderEnded, OrderFailed };

    /// <summary>建单没有确认时写在旅程上的码：<c>CHARGER_{Outcome}</c>。引擎与看板说明都经这里拼。</summary>
    public static string LegOutcomeCode(MovementDispatchOutcome outcome) => $"{LegName}_{outcome}";

    /// <summary>会被写进 <c>BlockReasonCode</c> 的建单结果码：除了确认（清掉码）与「终结后要对账」（按单已终结往下判）。</summary>
    public static IReadOnlyList<string> LegOutcomeCodes { get; } =
    [
        .. Enum.GetValues<MovementDispatchOutcome>()
            .Where(outcome => outcome is not (MovementDispatchOutcome.Confirmed or MovementDispatchOutcome.TerminalReconciliationRequired))
            .Select(LegOutcomeCode),
    ];

    /// <summary>这一族写在旅程行上的全部码（保持类与收尾类），给看板说明与测试核对用。</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        DepartureNotProven,
        ReservationNotHeld,
        CycleMissing,
        OrderEnded,
        OrderFailed,
    ];
}
