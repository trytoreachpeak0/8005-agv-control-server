using ControlServer.Domain;

namespace ControlServer.Host.Runtime;

/// <summary>
/// 一次「出发」按键该怎么答（协议 4.0.0 候选 wire-notes「<c>CurrentStopDepartureRequested</c> and <c>CurrentStopDepartureResult</c>」第 4 条，
/// control-server#713）。不做 I/O：事实由引擎在写锁里读好交进来。
/// </summary>
/// <remarks>
/// <para>
/// <b>先后就是规则</b>，第一条不成立的决定答复：
/// </para>
/// <list type="number">
/// <item><c>STATION_MISMATCH</c>：请求里的 <c>stationId</c> 不是车此刻所在的停靠（车没有在途旅程、或旅程没有开着的停靠，也是这一条）。</item>
/// <item><c>WORKLIST_REVISION_STALE</c>：<c>worklistRevision</c> 不是此刻的清单号；或请求带了 <c>operationSessionId</c> 而它不是当前停靠的。
/// 后者协议没有单列，归到这里：车回显的是它屏上那张清单，作业会话对不上就是那张清单已经不是当前的。为空不比（空清单都不带会话，
/// 持货等单时也要能出发）。号不是当前号、而新旧两版没有实质差别（<see cref="Facts.StaleWorklistWithoutSubstantiveChange"/>，control-server#742，
/// CP-0015）时不算这一条：车屏上那一版与此刻的这一版对操作员要做的事是同一个答案，拒了只会让他再按一次。</item>
/// <item><c>ACTION_NOT_ALLOWED_IN_STATE</c>：停靠既不是待送点取货停靠（开关关着时没有待送点），也不是本站装货已完成的机台取货停靠（control-server#725，
/// CP-0014：本停靠已承诺的装货都已完成、本站至少装上一篮、车停在离站等待里；不论装货阶段还开着还是已结束），旅程不在等录入或离站等待，
/// 有仓位操作在跑，有纠错或扫码前取消开着，已经在出发，或一篮没装而仓门没有证明关着（放了车就会被派走）。</item>
/// </list>
/// <para>
/// <b>锚需求没装、别处已装</b>也答 <c>ACTION_NOT_ALLOWED_IN_STATE</c>，纵深防御：今天在构造上到不了（初次派车只带锚一条、在第一个停靠；
/// 途中追加从不并入当前停靠（EnRouteAppendPlanner.MergeTargetAt）；待送点没有期限），而放回锚又让旅程开着时锚可以被改派到别的车（<c>FaultedCargoBindings</c> 的前提），换锚则会让
/// 旅程派生的全部出站 messageId 换号。理由与 control-server#290 的持货截断跳过锚需求相同。
/// </para>
/// </remarks>
internal static class CurrentStopDepartureRule
{
    /// <summary>判定一次按键要读的事实。</summary>
    /// <param name="CurrentStationId">车此刻所在停靠的站；没有在途旅程或没有开着的停靠为空。</param>
    /// <param name="CurrentOperationSessionId">当前停靠的作业会话。</param>
    /// <param name="CurrentWorklistRevision">这辆车清单流此刻的号（答复里也带它）。</param>
    /// <param name="AtStagingPickup">当前停靠是待送点取货停靠（开关关着时恒为假）。</param>
    /// <param name="AtStopAwaitingDeparture">旅程停在等录入或离站等待里：车在站上、没有装货命令在途、还没发离站核验。</param>
    /// <param name="SlotOperationRunning">这趟旅程有一条需求正在装（命令已下、结果未到）。</param>
    /// <param name="CorrectionOrCancellationOpen">有纠错或扫码前取消开着。</param>
    /// <param name="AlreadyDeparting">装货阶段已经以出发或缺电关闭。</param>
    /// <param name="AnchorUnloadedWithCargo">锚需求还没装，而这趟旅程别处已装了货。</param>
    /// <param name="NothingLoadedAndDoorsNotProvenClosed">一篮没装，而仓门没有证明关着。</param>
    /// <param name="MachinePickupLoadsComplete">
    /// 当前停靠是机台取货停靠（不是待送点）、车停在离站等待里、本停靠没有待做项、本停靠至少有一条已装上（CP-0014：一篮没装、需求全被取消不算装完）。
    /// </param>
    /// <param name="StaleWorklistWithoutSubstantiveChange">
    /// 请求带的清单号小于当前号，但那一版与此刻之间没有实质差别（control-server#742，口径调度 10-17 核定）：那一版属于当前停靠、是当前站的；从那一版到当前号
    /// 每一版都没有待做项；那一版落库之后本站的已装集合没变。号就是当前号时为假，不用读。
    /// </param>
    public sealed record Facts(
        string? CurrentStationId,
        string? CurrentOperationSessionId,
        long CurrentWorklistRevision,
        bool AtStagingPickup,
        bool AtStopAwaitingDeparture,
        bool SlotOperationRunning,
        bool CorrectionOrCancellationOpen,
        bool AlreadyDeparting,
        bool AnchorUnloadedWithCargo,
        bool NothingLoadedAndDoorsNotProvenClosed,
        bool MachinePickupLoadsComplete = false,
        bool StaleWorklistWithoutSubstantiveChange = false);

    /// <summary>为空是 <c>ACCEPTED</c>；否则是 <c>REJECTED</c> 带的那个问题。</summary>
    public static WireProblem? Decide(CurrentStopDepartures.Request request, Facts facts)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(facts);

        if (facts.CurrentStationId is null ||
            !string.Equals(request.StationId, facts.CurrentStationId, StringComparison.Ordinal))
        {
            return new WireProblem(
                ServerReasonCodes.StationMismatch,
                "payload.stationId",
                "The vehicle is not at the stop this departure was requested for.");
        }

        if (request.WorklistRevision != facts.CurrentWorklistRevision && !facts.StaleWorklistWithoutSubstantiveChange ||
            request.OperationSessionId is not null &&
            !string.Equals(request.OperationSessionId, facts.CurrentOperationSessionId, StringComparison.Ordinal))
        {
            return new WireProblem(
                ServerReasonCodes.WorklistRevisionStale,
                "payload.worklistRevision",
                "The worklist this departure was requested from is no longer the current one.");
        }

        string? notAllowed =
            !facts.AtStagingPickup && !facts.MachinePickupLoadsComplete
                ? "Departure on request is offered at a staging pickup stop, or at a machine pickup stop once its loads are complete." :
            !facts.AtStopAwaitingDeparture || facts.SlotOperationRunning ? "A slot operation is running or the vehicle is not waiting at the stop." :
            facts.CorrectionOrCancellationOpen ? "A load correction or a cancellation is open at this stop." :
            facts.AlreadyDeparting ? "The vehicle is already departing from this stop." :
            facts.AnchorUnloadedWithCargo ? "The journey's first demand is not loaded while other cargo is on board." :
            facts.NothingLoadedAndDoorsNotProvenClosed ? "The slot doors are not proven closed." :
            null;
        return notAllowed is null
            ? null
            : new WireProblem(ServerReasonCodes.ActionNotAllowedInState, null, notAllowed);
    }
}
