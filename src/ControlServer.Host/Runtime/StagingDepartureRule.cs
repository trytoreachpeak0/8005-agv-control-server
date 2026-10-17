using ControlServer.Application;
using ControlServer.Host.Runtime.Dispatch;

namespace ControlServer.Host.Runtime;

/// <summary>
/// 待送点取货停靠上车该不该自己走（control-server#710，program#184 R1；CP-0012、ADR-cross-0065）：车上每一侧都满，或者等着的待送需求
/// 全部装不下。
/// </summary>
/// <remarks>
/// <para>
/// <b>口径是用户 10-14 晚定的</b>（analysis 0.0）：
/// </para>
/// <list type="number">
/// <item>车上每一侧都满（按账本：已装或已预留，没有空仓）→ 走，有没有等着的需求都走。</item>
/// <item>否则，取派车轮这一轮对这辆车判过的、取货站是本站的全部待送候选，先去掉因与容量无关的判据被拒的（电量、准入、分区、MES 字段等），
/// 剩下的记为 S。S 非空、且 S 里每一条都只因容量被拒 → 走。合格的候选留在 S 里，它就是「还有一条装得下」。</item>
/// <item>S 为空（没有等着的需求，或者都因别的原因被拒）→ 不自动走，等客户在车载屏点出发（control-server#713）。</item>
/// </list>
/// <para>
/// <b>容量</b>是 <see cref="IsCapacityRefusal"/> 那四个码：本车货占着它要的那一侧、那一侧空仓不够、途中追加的 9 段或 8 条上限
/// （<see cref="EnRouteAppendPlanner.MaximumLegs"/>、<see cref="EnRouteAppendPlanner.MaximumWorklistItems"/>，两者同一个码），以及整趟停靠总数上限
/// （<c>JourneyRuntime:MaximumJourneyStops</c>，control-server#749）——这一趟再也接不下任何一条，与装满同理。
/// 超大需求（<see cref="DispatchReasonCodes.ExpectedBasketCountExceedsSlotGroup"/>，比那一侧的物理仓位还多）不算：它装不上任何一辆车，
/// 与这辆车满没满无关，票面列的容量也只有这几样。它与别的非容量拒绝一样从 S 里去掉——不推车走，也不拦车走。
/// </para>
/// <para>
/// <b>一旦判为该走就不翻回</b>（CP-0012 第五节②，analysis 1.6）：等着的需求随 MES 目录变化，前后两轮的结论可能不同，翻回去就是收回一个已经
/// 发出的离站核验。<b>派车轮这一轮没有这辆车的结论时说不出来</b>（重启之后第一轮之前），由 <see cref="LoadingPhaseMachine"/> 按「不改判」处理，
/// 不自动走。
/// </para>
/// <para>
/// 判出该走之后用 <c>VEHICLE_FULL</c> 表达（票面第 5 条，不改协议）：一侧还空着、等着的都装不下，对车载端说的也是这一个。
/// </para>
/// </remarks>
public static class StagingDepartureRule
{
    /// <summary>判定一次所需的事实。</summary>
    /// <param name="AlreadyDue">这趟旅程在待送点已经判过该走（落库的装货阶段是 <c>VEHICLE_FULL</c>）。</param>
    /// <param name="EverySlotGroupFull">车上每一侧都没有空仓；车的仓位模型或车载端事实读不到时为空。</param>
    /// <param name="WaitingCandidateReasons">
    /// 派车轮这一轮对这辆车判过的、取货站是本站的每一条待送候选的裁决（<see cref="DispatchAdmissionChain.Eligible"/> 或拒绝码）；
    /// 这一轮没有这辆车的结论时为空引用。
    /// </param>
    public sealed record Facts(bool AlreadyDue, bool? EverySlotGroupFull, IReadOnlyList<string>? WaitingCandidateReasons);

    /// <summary>该走为真，还该等为假，说不出来为空。</summary>
    public static bool? Decide(Facts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (facts.AlreadyDue)
        {
            return true;
        }
        if (facts.WaitingCandidateReasons is not { } reasons || facts.EverySlotGroupFull is not { } everyFull)
        {
            return null;
        }
        if (everyFull)
        {
            return true;
        }
        string[] stillInPlay = [.. reasons.Where(reason =>
            string.Equals(reason, DispatchAdmissionChain.Eligible, StringComparison.Ordinal) || IsCapacityRefusal(reason))];
        return stillInPlay.Length > 0 && stillInPlay.All(IsCapacityRefusal);
    }

    /// <summary>这个拒绝码说的是不是「车上装不下」（见类注释）。</summary>
    public static bool IsCapacityRefusal(string reasonCode) =>
        reasonCode is DispatchReasonCodes.SlotGroupOccupiedByOwnCargo
            or DispatchReasonCodes.SlotGroupCapacityTemporarilyUnavailable
            or DispatchReasonCodes.EnRouteAppendPlanLimitReached
            or DispatchReasonCodes.EnRouteAppendJourneyStopLimitReached;

    /// <summary>
    /// 一辆车一轮的裁决里，取货站是 <paramref name="stationRiotId"/> 的待送候选各自的裁决。
    /// </summary>
    /// <remarks>
    /// 取货站来自站点解析（Order 60）。在它之前就被拒的候选没有路线，这里认不出它取货在哪——它们反正是被非容量的判据拒的，本来就要从 S 里去掉。
    /// 容量的三个码都在站点解析之后（98、100），所以 S 里不会漏掉一条。
    /// </remarks>
    public static IReadOnlyList<string> WaitingAt(IEnumerable<DispatchCandidateVerdict> verdicts, int stationRiotId)
    {
        ArgumentNullException.ThrowIfNull(verdicts);
        return
        [
            .. verdicts
                .Where(verdict =>
                    string.Equals(verdict.Evaluation.Candidate.WorkType, TransportTaskTypes.StagingToWire, StringComparison.Ordinal) &&
                    verdict.Evaluation.Route?.PickupStationRiotId == stationRiotId)
                .Select(verdict => verdict.ReasonCode)
        ];
    }
}
