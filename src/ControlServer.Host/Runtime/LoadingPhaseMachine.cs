using ControlServer.Domain;

namespace ControlServer.Host.Runtime;

/// <summary>
/// 装货阶段此刻该是什么状态：由事实推出来，不由阶段推（批次7-07，control-server#212；REQ-0354、ADR-cross-0057／0059）。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么是一个函数而不是一张「状态 × 事件」的表。</b>装货阶段的四个状态都说的是「车此刻与装货的关系」：
/// 还有没有待装、两侧满没满、持货期限到没到、离没离开最后一个装货停靠。这些事实在推进段与派车轮里各自变化，
/// 而且会在同一轮里一起变——一次追加让待装重新出现，同一轮里一个候选又让一侧判满。按事件写，每一对「事件先后」
/// 都是一个要单独想清楚的格子；按事实写，结论只有一处，每一轮都从同一组事实重新推一遍。状态仍然落库，
/// 是为了两件事：<c>CLOSED</c> 是终态（关了就不再开），以及「上一次告诉车的是什么」——快照只在变了的时候发。
/// </para>
/// <para>
/// <b>判定的先后就是规则本身</b>，从上到下第一条成立的生效：
/// </para>
/// <list type="number">
/// <item>已经 <c>CLOSED</c>：保持。持货超时与让站之后不再接单（REQ-0354 末句），装满后离开最后一个装货停靠也是终点。</item>
/// <item>已经离开最后一个装货停靠：<c>CLOSED</c>。原来是 <c>VEHICLE_FULL</c> 就记 <c>VEHICLE_FULL</c>，否则记
/// <c>PLANNED_LOADING_COMPLETE</c>。适用持货等单时车只会从 <c>VEHICLE_FULL</c> 走到这里（<c>CARGO_HOLDING_WAIT</c>
/// 不发离站请求），另一支接住的是不适用持货的旅程，以及列落地之前就在途、从没写过这几列的旅程。</item>
/// <item>不适用持货等单（车能服务的分区都禁止途中追加，REQ-0198）：还有待装就 <c>LOADING</c>，没有就
/// <c>CLOSED</c>／<c>PLANNED_LOADING_COMPLETE</c>——与批次 7 之前完全相同，持货期限也不适用。</item>
/// <item>持货期限已过、而且没有 <c>LoadBatch</c> 正在执行：<c>CLOSED</c>／<c>CARGO_HOLDING_TIMEOUT</c>。
/// 有一批在执行就等它安全闭环（ADR-cross-0057「到期不打断正在进行的仓位操作」），下一轮再判。</item>
/// <item>离站安全核验已经发出而原来是 <c>VEHICLE_FULL</c>：保持 <c>VEHICLE_FULL</c>。核验发出之后车就要动了，
/// 这时再判回等单，要么收回一个已经发出的核验，要么让车带着一个「等单」的状态开走——两样都不对。追加仍然接，
/// 那由第 2 条之前的事实（待装重新出现）在车真正离开时体现。</item>
/// <item>两侧都满：<c>VEHICLE_FULL</c>。不满：还有待装就 <c>LOADING</c>，没有就 <c>CARGO_HOLDING_WAIT</c>。
/// 满没满<b>说不出来</b>（派车轮这一轮没问到这辆车，典型是重启之后第一轮之前）时不改判：原来是
/// <c>VEHICLE_FULL</c> 就保持，否则按不满判。说不出来就翻状态，会让一次重启给车发两张来回翻的快照。</item>
/// </list>
/// <para>
/// <b>第 6 条让 <c>VEHICLE_FULL</c> 可以出现在装货途中</b>：满是按「已装或已预留」的货算的（ADR-cross-0059），
/// 两侧都被预留满时车还没装完，但已经不再等新单——REQ-0354「全部分组均装满即 VehicleFull……完成已承诺的待装
/// Demand 后前往卸货」。反过来，<c>CARGO_HOLDING_WAIT</c> 之后追加成功会回到 <c>LOADING</c>：追加总是新开一个停靠
/// （批次7-06 的口径，当前停靠不并），车要去装新的那条，那不是「等单」。
/// </para>
/// </remarks>
public static class LoadingPhaseMachine
{
    /// <summary>判定一次装货阶段的全部事实。</summary>
    /// <param name="State">落库的状态；空与 <c>LOADING</c> 同义（新旅程从不写它）。</param>
    /// <param name="ClosedReason">落库的结束原因。</param>
    /// <param name="HoldingApplicable">车能服务的分区里至少有一个允许途中追加。</param>
    /// <param name="PendingLoadsRemain">这趟旅程还有需求没装（也没终结）。</param>
    /// <param name="LastLoadingStopDeparted">计划里已经没有开着的装货停靠：车已经为离开最后一个装货停靠请求了移动。</param>
    /// <param name="VehicleFull">两侧是否都满；说不出来为空。</param>
    /// <param name="HoldingDeadlinePassed">持货期限已过；第一个 <c>LoadBatch</c> 闭环之前恒为假。</param>
    /// <param name="LoadBatchInProgress">有一批装货命令已发、结果未到。</param>
    /// <param name="DepartureUnderWay">离站安全核验已经发出、车还没为离站请求移动。</param>
    public sealed record Facts(
        string? State,
        string? ClosedReason,
        bool HoldingApplicable,
        bool PendingLoadsRemain,
        bool LastLoadingStopDeparted,
        bool? VehicleFull,
        bool HoldingDeadlinePassed,
        bool LoadBatchInProgress,
        bool DepartureUnderWay);

    /// <summary>一次判定的结论。<see cref="ClosedReason"/> 只在 <c>CLOSED</c> 时非空，与协议的 <c>if/then/else</c> 同一条。</summary>
    public sealed record Decision(string State, string? ClosedReason)
    {
        public bool IsClosed => State == LoadingPhaseStates.Closed;
    }

    /// <summary>见类注释的六条。</summary>
    public static Decision Decide(Facts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        string current = facts.State ?? LoadingPhaseStates.Loading;

        if (current == LoadingPhaseStates.Closed)
        {
            return new Decision(LoadingPhaseStates.Closed, facts.ClosedReason
                ?? throw new InvalidOperationException("A closed loading phase names why it closed."));
        }

        if (facts.LastLoadingStopDeparted)
        {
            return Closed(current == LoadingPhaseStates.VehicleFull
                ? LoadingClosedReasons.VehicleFull
                : LoadingClosedReasons.PlannedLoadingComplete);
        }

        if (!facts.HoldingApplicable)
        {
            return facts.PendingLoadsRemain
                ? Open(LoadingPhaseStates.Loading)
                : Closed(LoadingClosedReasons.PlannedLoadingComplete);
        }

        if (facts.HoldingDeadlinePassed && !facts.LoadBatchInProgress)
        {
            return Closed(LoadingClosedReasons.CargoHoldingTimeout);
        }

        if (facts.DepartureUnderWay && current == LoadingPhaseStates.VehicleFull)
        {
            return Open(LoadingPhaseStates.VehicleFull);
        }

        bool full = facts.VehicleFull ?? current == LoadingPhaseStates.VehicleFull;
        if (full)
        {
            return Open(LoadingPhaseStates.VehicleFull);
        }

        return Open(facts.PendingLoadsRemain ? LoadingPhaseStates.Loading : LoadingPhaseStates.CargoHoldingWait);
    }

    /// <summary>
    /// 从 <paramref name="from"/> 变到 <paramref name="to"/> 要不要给车发一张车辆业务状态快照。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 票面第 7 条：进出 <c>CARGO_HOLDING_WAIT</c>、<c>VEHICLE_FULL</c>、<c>CLOSED</c> 时各发一次，车载端要显示倒计时与结束原因。
    /// </para>
    /// <para>
    /// <b>唯一不发的变化是「不适用持货的旅程装完了」</b>，即 <c>LOADING</c> → <c>CLOSED</c>／<c>PLANNED_LOADING_COMPLETE</c>。
    /// 批次 7 之前这一步也发生，只是没有落库，也没有发快照：车在卸货站到站那一张快照上才看到 <c>CLOSED</c>。
    /// 在这里多发一张，就会把合成对端与 G3 断言的修订号序列整体推后一号（它们今天读的是逐条相同的那一串），
    /// 而这正是票面第 7 条要求不动的东西。
    /// </para>
    /// </remarks>
    public static bool Announces(string? fromState, string? fromReason, Decision to, bool holdingApplicable)
    {
        ArgumentNullException.ThrowIfNull(to);
        string from = fromState ?? LoadingPhaseStates.Loading;
        if (from == to.State && string.Equals(fromReason, to.ClosedReason, StringComparison.Ordinal))
        {
            return false;
        }

        return !(!holdingApplicable &&
                 from == LoadingPhaseStates.Loading &&
                 to.State == LoadingPhaseStates.Closed &&
                 to.ClosedReason == LoadingClosedReasons.PlannedLoadingComplete);
    }

    /// <summary>
    /// 持货期限（<c>cargoHoldingDeadlineAt</c>）：适用持货等单、第一个 <c>LoadBatch</c> 已经闭环、阶段还没结束时，
    /// 是起算点加期限；其余一律为空（program#94：「第一个 LoadBatch 安全闭环之前、或不适用持货等单时为 null」）。
    /// </summary>
    public static DateTimeOffset? Deadline(
        string? state,
        DateTimeOffset? cargoHoldingStartedAt,
        TimeSpan cargoHoldingTimeout,
        bool holdingApplicable) =>
        holdingApplicable && cargoHoldingStartedAt is { } startedAt && state != LoadingPhaseStates.Closed
            ? startedAt + cargoHoldingTimeout
            : null;

    /// <summary>发给车的那一份：状态、期限、原因三样。</summary>
    public static LoadingPhaseProjection Project(
        string? state,
        string? closedReason,
        DateTimeOffset? cargoHoldingStartedAt,
        TimeSpan cargoHoldingTimeout,
        bool holdingApplicable)
    {
        string current = state ?? LoadingPhaseStates.Loading;
        return new LoadingPhaseProjection(
            current,
            Deadline(current, cargoHoldingStartedAt, cargoHoldingTimeout, holdingApplicable),
            current == LoadingPhaseStates.Closed ? closedReason : null);
    }

    private static Decision Open(string state) => new(state, null);

    private static Decision Closed(string reason) => new(LoadingPhaseStates.Closed, reason);
}
