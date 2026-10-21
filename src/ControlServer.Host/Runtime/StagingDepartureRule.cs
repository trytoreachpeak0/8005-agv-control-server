using ControlServer.Application;
using ControlServer.Host.Runtime.Dispatch;

namespace ControlServer.Host.Runtime;

/// <summary>
/// 待送点取货停靠上车该不该自己走（control-server#710，program#184 R1；CP-0012、ADR-cross-0065；口径由 CP-0018 修订，control-server#800）：
/// 车上每一侧都被已装货物占满，或者等着的待送需求全部装不下。
/// </summary>
/// <remarks>
/// <para>
/// <b>口径是用户 10-14 晚定的</b>（analysis 0.0），<b>判满只按已装货物</b>由用户 10-20 改定（CP-0018 修订项一）：
/// </para>
/// <list type="number">
/// <item>车上每一侧都被已装货物占满（会话基线里这一侧的仓，去掉已安全闭环装上车的货占的，一个不剩）→ 走，有没有等着的需求都走。
/// 已预留而还没装的待装需求不算（10-20 agv02 在 217 一篮未装就判满，就是把预留算进去的结果）。禁用的仓不在会话基线里，视同不可用
/// （调度 10-21 对「物理仓位」的解释，用户确认，条文另出小 CP 补）。</item>
/// <item>否则，取派车轮这一轮对这辆车判过的、取货站是本站的全部待送候选，先去掉因与容量无关的判据被拒的（电量、准入、分区、MES 字段等），
/// 以及<b>只因本车已预留、还没装的需求占位而装不下的</b>（CP-0018 第五节第二张表第 1 项，用户 10-21 定「不算」），剩下的记为 S。
/// S 非空、且 S 里每一条都只因容量被拒 → 走。合格的候选留在 S 里，它就是「还有一条装得下」。</item>
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
/// <b>「本车占位」分两种，派车分不出来，这里分</b>：派车按已装或已预留核对（ADR-cross-0059，不改），拒绝码只有一个
/// <see cref="DispatchReasonCodes.SlotGroupOccupiedByOwnCargo"/>。候选要的那一侧只扣已装货物之后还放得下它的篮数，它就只是被预留挡住
/// （<see cref="OnlyOwnReservations"/>），换成 <see cref="RefusedOnlyForOwnReservations"/> 这个非容量的码，从 S 里去掉。派车的拒绝码不动：
/// 机台取货的判满与看板读的还是它。<see cref="DispatchReasonCodes.SlotGroupCapacityTemporarilyUnavailable"/>（会话基线本身不够）同理分两种：篮数超过那一侧
/// 能用的仓，挡住它的只是禁用仓，换成 <see cref="RefusedOnlyForDisabledSlots"/>、不计入（CP-0024）；没超过，那是装货途中重连、已装的仓不在新基线里，照旧是容量
/// （<see cref="OnlyDisabledSlots"/>）。
/// </para>
/// <para>
/// <b>每一轮重判</b>（CP-0018 修订项一，用户 10-20 定；取代 CP-0012 第五节②「一旦判为该走就不翻回」）：判满前后追加的待装需求一样计入，
/// 判定成立之后情形不再成立就撤回，车回到持货等单。撤回只到离站安全核验发出为止（第五节第二张表第 2 项，用户 10-21 定）：核验发出之后由
/// <see cref="LoadingPhaseMachine"/> 原有的保持点保持 <c>VEHICLE_FULL</c>；为离站请求移动的那一轮与本停靠已受理出发（cs#780 独立审查 M1）由调用方保持。
/// <b>派车轮这一轮没有这辆车的结论时说不出来</b>（重启之后第一轮之前），由 <see cref="LoadingPhaseMachine"/> 按「不改判」处理，不自动走。
/// </para>
/// <para>
/// 判出该走之后用 <c>VEHICLE_FULL</c> 表达（票面第 5 条，不改协议）：一侧还空着、等着的都装不下，对车载端说的也是这一个。
/// </para>
/// </remarks>
public static class StagingDepartureRule
{
    /// <summary>
    /// 候选只因本车已预留、还没装的需求占位而装不下（<see cref="OnlyOwnReservations"/>）时，<see cref="WaitingAt"/> 换上的码。不是派车的拒绝码，
    /// 只活在这条规则里；它不是容量，所以与别的非容量拒绝一样从 S 里去掉。
    /// </summary>
    public const string RefusedOnlyForOwnReservations = "STAGING_OWN_RESERVATIONS_ONLY";

    /// <summary>
    /// 候选只因本车禁用的仓装不下（<see cref="OnlyDisabledSlots"/>）时，<see cref="WaitingAt"/> 换上的码（CP-0024，cs#800 独立审查 M1）。同样只活在这条规则里、
    /// 不是容量。
    /// </summary>
    public const string RefusedOnlyForDisabledSlots = "STAGING_DISABLED_SLOTS_ONLY";

    /// <summary>
    /// 一侧的仓位余量，判一条候选被什么挡住用。
    /// </summary>
    /// <param name="Usable">这一侧能用的仓：会话基线里的（禁用的不在里面），加上这一侧已装货物占着的（装货途中重连，新会话的基线里没有它们）。</param>
    /// <param name="FreeOfLoadedCargo">这一侧只扣已装货物还空着的仓：会话基线里的，去掉已装货物占着的。</param>
    public sealed record SlotRoom(int Usable, int FreeOfLoadedCargo);

    /// <summary>判定一次所需的事实。</summary>
    /// <param name="EverySlotGroupFull">车上每一侧都被已装货物占满；车的仓位模型或车载端事实读不到时为空。</param>
    /// <param name="WaitingCandidateReasons">
    /// 派车轮这一轮对这辆车判过的、取货站是本站的每一条待送候选的裁决（<see cref="DispatchAdmissionChain.Eligible"/>、拒绝码，或
    /// <see cref="RefusedOnlyForOwnReservations"/>、<see cref="RefusedOnlyForDisabledSlots"/>）；这一轮没有这辆车的结论时为空引用。
    /// </param>
    public sealed record Facts(bool? EverySlotGroupFull, IReadOnlyList<string>? WaitingCandidateReasons);

    /// <summary>该走为真，还该等为假，说不出来为空。</summary>
    public static bool? Decide(Facts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
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
    /// 一辆车一轮的裁决里，取货站是 <paramref name="stationRiotId"/> 的待送候选各自的裁决；给了 <paramref name="room"/> 时，只被预留挡住的换成
    /// <see cref="RefusedOnlyForOwnReservations"/>，只被禁用仓挡住的换成 <see cref="RefusedOnlyForDisabledSlots"/>。
    /// </summary>
    /// <remarks>
    /// 取货站来自站点解析（Order 60）。在它之前就被拒的候选没有路线，这里认不出它取货在哪——它们反正是被非容量的判据拒的，本来就要从 S 里去掉。
    /// 容量的三个码都在站点解析之后（98、100），所以 S 里不会漏掉一条。
    /// </remarks>
    public static IReadOnlyList<string> WaitingAt(
        IEnumerable<DispatchCandidateVerdict> verdicts,
        int stationRiotId,
        IReadOnlyDictionary<string, SlotRoom>? room = null)
    {
        ArgumentNullException.ThrowIfNull(verdicts);
        return
        [
            .. verdicts
                .Where(verdict =>
                    string.Equals(verdict.Evaluation.Candidate.WorkType, TransportTaskTypes.StagingToWire, StringComparison.Ordinal) &&
                    verdict.Evaluation.Route?.PickupStationRiotId == stationRiotId)
                .Select(verdict => room is null ? verdict.ReasonCode
                    : OnlyOwnReservations(verdict, room) ? RefusedOnlyForOwnReservations
                    : OnlyDisabledSlots(verdict, room) ? RefusedOnlyForDisabledSlots
                    : verdict.ReasonCode)
        ];
    }

    /// <summary>
    /// 有候选因本车已装货物（不只是预留）而装不进去的那几侧：<c>FullSlotPositionsJson</c> 在待送点上写的「满」，与 <see cref="SlotGroupFullnessBoard.OwnCargoBlockedGroups"/>
    /// 同一个含义，只是去掉了只被预留挡住的候选。
    /// </summary>
    public static IReadOnlySet<string> GroupsBlockedByLoadedCargo(
        IEnumerable<DispatchCandidateVerdict> verdicts,
        IReadOnlyDictionary<string, SlotRoom> room)
    {
        ArgumentNullException.ThrowIfNull(verdicts);
        ArgumentNullException.ThrowIfNull(room);
        return SlotGroupFullnessBoard.OwnCargoBlockedGroups(verdicts.Where(verdict => !OnlyOwnReservations(verdict, room)));
    }

    /// <summary>
    /// 派车因本车占位拒了它（<see cref="DispatchReasonCodes.SlotGroupOccupiedByOwnCargo"/>），而它要的那一侧只扣已装货物还放得下它的篮数：挡住它的只是预留。
    /// 篮数或侧说不出来（派车轮在判容量之前就已写上，这里是防御）按原码处理。
    /// </summary>
    public static bool OnlyOwnReservations(DispatchCandidateVerdict verdict, IReadOnlyDictionary<string, SlotRoom> room) =>
        verdict?.ReasonCode == DispatchReasonCodes.SlotGroupOccupiedByOwnCargo &&
        RoomOf(verdict, room) is { } groupRoom &&
        verdict.Evaluation.ExpectedBasketCount <= groupRoom.FreeOfLoadedCargo;

    /// <summary>
    /// 派车因会话基线本身不够拒了它（<see cref="DispatchReasonCodes.SlotGroupCapacityTemporarilyUnavailable"/>，SlotCapacityCriterion），而它的篮数比这一侧能用的仓
    /// （基线加上已装货物占着的）还多：挡住它的只是禁用的仓，CP-0024「因本车仓位禁用而无法装入的候选不计入」。
    /// </summary>
    /// <remarks>
    /// 不能把这个码整个从容量里拿掉（cs#800 独立审查 M1）：会话基线只收会话建立时为空的仓，装货途中重连之后，本旅程已装的仓不在新基线里，同一个码说的其实是
    /// 「被已装货物挡住」。所以按「能用的仓」重看：篮数不超过它、只是超过只扣已装后的空仓，照旧是容量。篮数或侧说不出来按原码处理。
    /// </remarks>
    public static bool OnlyDisabledSlots(DispatchCandidateVerdict verdict, IReadOnlyDictionary<string, SlotRoom> room) =>
        verdict?.ReasonCode == DispatchReasonCodes.SlotGroupCapacityTemporarilyUnavailable &&
        RoomOf(verdict, room) is { } groupRoom &&
        verdict.Evaluation.ExpectedBasketCount > groupRoom.Usable;

    private static SlotRoom? RoomOf(DispatchCandidateVerdict verdict, IReadOnlyDictionary<string, SlotRoom> room)
    {
        ArgumentNullException.ThrowIfNull(room);
        return verdict.Evaluation.RequiredSlotPosition is { } group &&
               verdict.Evaluation.ExpectedBasketCount > 0 &&
               room.TryGetValue(group, out SlotRoom? groupRoom)
            ? groupRoom
            : null;
    }
}
