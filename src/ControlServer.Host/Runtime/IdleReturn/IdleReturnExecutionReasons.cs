using ControlServer.Application;

namespace ControlServer.Host.Runtime.IdleReturn;

/// <summary>
/// 空闲返回执行（批次8-19，control-server#390）写在旅程行上的阻断码、收尾码与释放原因。
/// </summary>
/// <remarks>
/// <para>
/// 资格与承诺那一段的码在 <see cref="IdleReturnReasons"/>（批次8-18）；这里是承诺之后的：物化、出发前安全门、建单、到点、离点与失败分流。
/// </para>
/// <para>
/// <b>保持类</b>（旅程停在原处、承诺与独占都不放）：<see cref="DepartureNotProven"/>、<see cref="OrderEndedStopNotProven"/>、
/// <see cref="WaitingPointLostOrderInFlight"/>，以及建单结果的 <c>WAITING_POINT_{结果}</c>（与搬运的 <c>PICKUP_ResultUnknown</c> 同一个
/// 形状）。<b>收尾类</b>（旅程关闭、用途占有释放）：其余。收尾码写在收尾的旅程行上，也是用途占有记录的释放原因。
/// </para>
/// </remarks>
public static class IdleReturnExecutionReasons
{
    /// <summary>建单结果码的前缀：<c>WAITING_POINT_ResultUnknown</c> 等，与搬运的 <c>PICKUP_</c>、<c>GATE_</c> 同一个形状。</summary>
    public const string LegName = "WAITING_POINT";

    /// <summary>
    /// 出发前安全门没过（v2 发不了 <c>PreDepartureSafetyCheck</c>，服务端等价门）：会话就绪判定不是可移动，或没有一份足够新的、
    /// 全部仓位锁闭、无阻断事实的安全状态，或车况（故障、急停、不在本图、RIoT 报在动）不允许出发。不建单，承诺与预占保持，下一轮重评。
    /// </summary>
    public const string DepartureNotProven = "IDLE_RETURN_DEPARTURE_NOT_PROVEN";

    /// <summary>
    /// 等待点独占行已不在、或属于别的旅程（人工释放，control-server#419），而这张单可能已在 RIoT 上：服务端已对它发取消，
    /// 等 RIoT 报单终结、车证明停稳之后才收尾，期间用途占有不放、车不再被派往那个点。
    /// </summary>
    public const string WaitingPointLostOrderInFlight = "IDLE_RETURN_WAITING_POINT_LOST_ORDER_IN_FLIGHT";

    /// <summary>
    /// 空闲返回单在 RIoT 被取消或删除（不是本服务端取消的），车还没证明停稳：承诺与独占保持，不重建（<c>REQ-0296</c>）。
    /// </summary>
    public const string OrderEndedStopNotProven = "IDLE_RETURN_ORDER_ENDED_STOP_NOT_PROVEN";

    // ---- 收尾码 ----

    /// <summary>承诺物化不出旅程：等待点已不在登记里、被停用、不在实时目录、不再接这辆车，或预占行已不在。承诺整个释放。</summary>
    public const string CommitmentOrphaned = "IDLE_RETURN_COMMITMENT_ORPHANED";

    /// <summary>首次建单之前重新核验等待点失败（同上几种）：用途占有与预占同一次保存释放，车回到重新评估（<c>REQ-0296</c> 第一支）。</summary>
    public const string WaitingPointNoLongerEligible = "IDLE_RETURN_WAITING_POINT_NO_LONGER_ELIGIBLE";

    /// <summary>等待点独占行已不在或属于别的车，而这张单从没发出过：作废承诺、释放用途占有，车不出发。</summary>
    public const string WaitingPointLost = "IDLE_RETURN_WAITING_POINT_LOST";

    /// <summary>到点证据全满足时等待点独占已不在或属于别的车：不转占用，作废承诺、释放用途占有，告警。</summary>
    public const string WaitingPointLostAtArrival = "IDLE_RETURN_WAITING_POINT_LOST_AT_ARRIVAL";

    /// <summary>
    /// 单在 RIoT 被取消或删除，之后车证明停稳、没有活动订单：已确认失败，释放用途占有；等待点预占由离点清扫凭离点证据释放。
    /// 下一次承诺排除这个点（<c>REQ-0296</c> 末句）。
    /// </summary>
    public const string OrderEnded = "IDLE_RETURN_ORDER_ENDED";

    /// <summary>
    /// 单在 RIoT 上 FAILED、故障已由人工清除（车无未完成订单、无急停锁存、人确认已排除原因）：已确认失败，释放用途占有；
    /// 下一次承诺排除这个点。
    /// </summary>
    public const string OrderFailed = "IDLE_RETURN_ORDER_FAILED";

    /// <summary>用途占有的释放原因：到点证据全满足、预占已转占用，车回到可选择（<c>REQ-0293</c>）。等待点占用继续保持。</summary>
    public const string ConvergedAtWaitingPoint = "IDLE_RETURN_CONVERGED_AT_WAITING_POINT";

    /// <summary>已确认失败的收尾码：下一次承诺要排除原失败点的那几种。</summary>
    public static IReadOnlySet<string> ConfirmedFailures { get; } =
        new HashSet<string>(StringComparer.Ordinal) { OrderEnded, OrderFailed };

    /// <summary>
    /// 建单没有确认时写在旅程上的码：<c>WAITING_POINT_{Outcome}</c>（与搬运腿的 <c>PICKUP_…</c>／<c>GATE_…</c> 同一种拼法）。引擎与看板说明
    /// 都经这里拼，免得两边各拼一份、一边改了另一边不知道。
    /// </summary>
    public static string LegOutcomeCode(MovementDispatchOutcome outcome) => $"{LegName}_{outcome}";

    /// <summary>
    /// 会被写进 <c>BlockReasonCode</c> 的建单结果码：<see cref="MovementDispatchOutcome"/> 的每个取值，除了确认（清掉码）与
    /// 「终结后要对账」（按单已终结往下判，不写码）。新加一个取值就多一个码，看板说明的检查会逼着给它写说明。
    /// </summary>
    public static IReadOnlyList<string> LegOutcomeCodes { get; } =
    [
        .. Enum.GetValues<MovementDispatchOutcome>()
            .Where(outcome => outcome is not (MovementDispatchOutcome.Confirmed or MovementDispatchOutcome.TerminalReconciliationRequired))
            .Select(LegOutcomeCode),
    ];

    /// <summary>这一族全部的码（保持类、收尾类与建单结果码），给看板说明与测试核对用。</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        DepartureNotProven,
        WaitingPointLostOrderInFlight,
        OrderEndedStopNotProven,
        CommitmentOrphaned,
        WaitingPointNoLongerEligible,
        WaitingPointLost,
        WaitingPointLostAtArrival,
        OrderEnded,
        OrderFailed,
    ];
}
