using ControlServer.Domain;
using ControlServer.Host.Runtime;

namespace ControlServer.Tests;

/// <summary>
/// 装货阶段的判定表（批次7-07，control-server#212）：<see cref="LoadingPhaseMachine"/> 类注释那六条，每条至少一格，
/// 以及它们之间的先后。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么按「规则」而不是按「状态 × 事件」组织。</b>判定是一个函数，输入是一组事实；票面要的「每个 (state, 事件) 一例」
/// 在这里是「每个起始状态 × 每一条会让结论变的事实」，下面的 <see cref="Transitions"/> 就是那张表。每一行写明它在钉哪一条
/// 规则，改动一条规则时能直接看出哪几行该变。
/// </para>
/// <para>
/// <b>先后是判据的一半。</b>六条规则各自成立不等于合起来对：「持货期限到了」与「两侧都满」同时成立时该是哪一个，只有规则的
/// 先后说得出。<see cref="PrecedenceIsTheRuleOrder"/> 那几格就是为此单列的——把第 4 条挪到第 6 条之后，满车会在期限到时
/// 仍报 <c>VEHICLE_FULL</c>，别的每一行都还是绿的。
/// </para>
/// </remarks>
public sealed class LoadingPhaseMachineTests
{
    private const string Loading = LoadingPhaseStates.Loading;
    private const string Wait = LoadingPhaseStates.CargoHoldingWait;
    private const string Full = LoadingPhaseStates.VehicleFull;
    private const string Closed = LoadingPhaseStates.Closed;

    /// <summary>一辆适用持货等单、还有待装、没满、没到期、没在离站的车；每一行只改它要钉的那几样。</summary>
    private static LoadingPhaseMachine.Facts Holding(string? state = null, string? reason = null) =>
        new(state, reason,
            HoldingApplicable: true,
            PendingLoadsRemain: true,
            LastLoadingStopDeparted: false,
            VehicleFull: false,
            HoldingDeadlinePassed: false,
            LoadBatchInProgress: false,
            DepartureUnderWay: false);

    public static TheoryData<string, LoadingPhaseMachine.Facts, string, string?> Transitions() => new()
    {
        // ---- 第 1 条：CLOSED 是终态，任何事实都开不回来 ----------------------------------------------
        { "closed by timeout stays closed though loads reappear",
            Holding(Closed, LoadingClosedReasons.CargoHoldingTimeout), Closed, LoadingClosedReasons.CargoHoldingTimeout },
        { "closed by full stays closed though a side frees",
            Holding(Closed, LoadingClosedReasons.VehicleFull) with { PendingLoadsRemain = false }, Closed, LoadingClosedReasons.VehicleFull },
        { "planned-complete stays closed when appending becomes allowed",
            Holding(Closed, LoadingClosedReasons.PlannedLoadingComplete) with { PendingLoadsRemain = false }, Closed, LoadingClosedReasons.PlannedLoadingComplete },

        // ---- 第 2 条：离开最后一个装货停靠 -------------------------------------------------------------
        { "full vehicle leaving its last pickup closes as VEHICLE_FULL",
            Holding(Full) with { PendingLoadsRemain = false, LastLoadingStopDeparted = true, VehicleFull = true }, Closed, LoadingClosedReasons.VehicleFull },
        { "loading journey leaving its last pickup (not holding) closes as planned-complete",
            Holding(Loading) with { HoldingApplicable = false, PendingLoadsRemain = false, LastLoadingStopDeparted = true }, Closed, LoadingClosedReasons.PlannedLoadingComplete },
        { "a journey from before the columns (null) past its pickup closes as planned-complete",
            Holding(null) with { PendingLoadsRemain = false, LastLoadingStopDeparted = true }, Closed, LoadingClosedReasons.PlannedLoadingComplete },

        // ---- 第 3 条：不适用持货等单 -------------------------------------------------------------------
        { "not holding, loads pending: LOADING",
            Holding(null) with { HoldingApplicable = false }, Loading, null },
        { "not holding, loads done: planned-complete",
            Holding(null) with { HoldingApplicable = false, PendingLoadsRemain = false }, Closed, LoadingClosedReasons.PlannedLoadingComplete },
        { "not holding ignores a passed deadline",
            Holding(null) with { HoldingApplicable = false, HoldingDeadlinePassed = true }, Loading, null },
        { "not holding ignores fullness",
            Holding(null) with { HoldingApplicable = false, VehicleFull = true }, Loading, null },
        { "waiting vehicle whose zones stop allowing appends closes as planned-complete",
            Holding(Wait) with { HoldingApplicable = false, PendingLoadsRemain = false }, Closed, LoadingClosedReasons.PlannedLoadingComplete },

        // ---- 第 4 条：持货期限 -------------------------------------------------------------------------
        { "deadline passed while waiting: timeout",
            Holding(Wait) with { PendingLoadsRemain = false, HoldingDeadlinePassed = true }, Closed, LoadingClosedReasons.CargoHoldingTimeout },
        // 「到期时别的停靠上还有待装」不在这张表里，见 ADeadlinePassedWithLoadsPendingElsewhereClosesTodayPendingCs290。
        { "deadline passed with a load batch executing: not yet (ADR-cross-0057)",
            Holding(Loading) with { HoldingDeadlinePassed = true, LoadBatchInProgress = true }, Loading, null },
        { "deadline passed, batch executing, vehicle full: stays full until the batch closes",
            Holding(Full) with { HoldingDeadlinePassed = true, LoadBatchInProgress = true, VehicleFull = true }, Full, null },

        // ---- 第 5 条：离站核验已发出 -------------------------------------------------------------------
        { "full with departure under way stays full though a side frees",
            Holding(Full) with { PendingLoadsRemain = false, VehicleFull = false, DepartureUnderWay = true }, Full, null },
        { "departure under way does not make a loading vehicle full",
            Holding(Loading) with { DepartureUnderWay = true }, Loading, null },

        // ---- 第 6 条：满没满 ---------------------------------------------------------------------------
        { "both sides full while loads pending: VEHICLE_FULL mid-plan",
            Holding(Loading) with { VehicleFull = true }, Full, null },
        { "loading done, not full: CARGO_HOLDING_WAIT",
            Holding(Loading) with { PendingLoadsRemain = false }, Wait, null },
        { "waiting, then full: VEHICLE_FULL",
            Holding(Wait) with { PendingLoadsRemain = false, VehicleFull = true }, Full, null },
        { "full, then a side frees with loading done: back to CARGO_HOLDING_WAIT",
            Holding(Full) with { PendingLoadsRemain = false }, Wait, null },
        { "full, then a side frees with loads pending: back to LOADING",
            Holding(Full), Loading, null },
        { "waiting, then an append makes a load pending: LOADING",
            Holding(Wait), Loading, null },
        { "fullness unknown keeps VEHICLE_FULL",
            Holding(Full) with { PendingLoadsRemain = false, VehicleFull = null }, Full, null },
        { "fullness unknown keeps waiting as waiting",
            Holding(Wait) with { PendingLoadsRemain = false, VehicleFull = null }, Wait, null },
        { "fullness unknown on a loading vehicle is judged not full",
            Holding(Loading) with { VehicleFull = null }, Loading, null },
    };

    [Theory]
    [MemberData(nameof(Transitions))]
    public void EachRuleDecidesWhatItSays(string row, LoadingPhaseMachine.Facts facts, string state, string? reason)
    {
        LoadingPhaseMachine.Decision decision = LoadingPhaseMachine.Decide(facts);

        Assert.True(state == decision.State && reason == decision.ClosedReason,
            $"{row}: expected {state}/{reason}, decided {decision.State}/{decision.ClosedReason}");
    }

    /// <summary>
    /// <b>现行为，不是期望</b>：到期时别的停靠上还有待装，今天关为 <c>CLOSED</c>／<c>CARGO_HOLDING_TIMEOUT</c>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 审查 S1：到期之后车照样开往那个停靠去装（本票不截断停靠），车上收到的却是「已结束」，与 program#94 语义表对 CLOSED 的
    /// 定义对不上。正确的形状取决于 cs#290 怎么实现 ADR-cross-0057 实施决定（三）的截断：截断了待装，这一格就对了；不截断，
    /// 这一格该是 LOADING 直到装完。那张票定，不在本票。
    /// </para>
    /// <para>
    /// 所以这一格单列、名字里写着 cs#290，而不放进上面那张「每条规则说什么」的表：放在表里，它就把现状钉成了规则，cs#290 的
    /// 正确修法会被当成回归。cs#290 改它时，改这一条就够了。
    /// </para>
    /// </remarks>
    [Fact]
    public void ADeadlinePassedWithLoadsPendingElsewhereClosesTodayPendingCs290()
    {
        LoadingPhaseMachine.Decision decision = LoadingPhaseMachine.Decide(
            Holding(Loading) with { HoldingDeadlinePassed = true });

        Assert.Equal(Closed, decision.State);
        Assert.Equal(LoadingClosedReasons.CargoHoldingTimeout, decision.ClosedReason);
    }

    public static TheoryData<string, LoadingPhaseMachine.Facts, string, string?> Precedence() => new()
    {
        // 第 2 条先于第 4 条：离开最后一个装货停靠时期限也过了，结束原因是「装满走了」，不是「超时」。
        { "departed full beats deadline",
            Holding(Full) with { PendingLoadsRemain = false, LastLoadingStopDeparted = true, HoldingDeadlinePassed = true, VehicleFull = true },
            Closed, LoadingClosedReasons.VehicleFull },
        // 第 3 条先于第 4、6 条：不适用持货等单，期限与满都不算数。
        { "not holding beats deadline and full",
            Holding(Loading) with { HoldingApplicable = false, PendingLoadsRemain = false, HoldingDeadlinePassed = true, VehicleFull = true },
            Closed, LoadingClosedReasons.PlannedLoadingComplete },
        // 第 4 条先于第 5、6 条：期限到了，满车不再为「还在离站」保持 FULL——它结束，结束原因是超时。
        { "deadline beats full",
            Holding(Full) with { PendingLoadsRemain = false, HoldingDeadlinePassed = true, VehicleFull = true },
            Closed, LoadingClosedReasons.CargoHoldingTimeout },
        { "deadline beats departure under way",
            Holding(Full) with { PendingLoadsRemain = false, HoldingDeadlinePassed = true, DepartureUnderWay = true, VehicleFull = true },
            Closed, LoadingClosedReasons.CargoHoldingTimeout },
    };

    [Theory]
    [MemberData(nameof(Precedence))]
    public void PrecedenceIsTheRuleOrder(string row, LoadingPhaseMachine.Facts facts, string state, string? reason)
    {
        LoadingPhaseMachine.Decision decision = LoadingPhaseMachine.Decide(facts);

        Assert.True(state == decision.State && reason == decision.ClosedReason,
            $"{row}: expected {state}/{reason}, decided {decision.State}/{decision.ClosedReason}");
    }

    /// <summary>
    /// 协议的 <c>if/then/else</c>：<c>closedReason</c> 恰在 <c>CLOSED</c> 时非空。在全部事实组合上判，不只在上面那几行上。
    /// </summary>
    /// <remarks>
    /// 出站 schema 门禁也会拦这件事，但只拦被发出去的那几张；这里拦的是判定本身，包括不会被发出去的那些
    /// （例如不适用持货等单时静默写下的 <c>CLOSED</c>）。
    /// </remarks>
    [Fact]
    public void AClosedReasonExactlyWhenClosedAcrossEveryCombination()
    {
        string?[] states = [null, Loading, Wait, Full];
        bool[] flags = [false, true];
        bool?[] fullness = [null, false, true];
        LoadingPhaseMachine.Facts[] combinations =
        [
            .. from state in states
               from applicable in flags
               from pending in flags
               from departed in flags
               from full in fullness
               from deadline in flags
               from batch in flags
               from departing in flags
               select new LoadingPhaseMachine.Facts(
                   state, null, applicable, pending, departed, full, deadline, batch, departing)
        ];
        foreach (LoadingPhaseMachine.Facts facts in combinations)
        {
            LoadingPhaseMachine.Decision decision = LoadingPhaseMachine.Decide(facts);
            Assert.Equal(decision.State == Closed, decision.ClosedReason is not null);
        }

        // 4 种状态 × 6 个布尔（2⁶）× 3 种满没满：少一维就少一半，一个被悄悄删掉的循环在这里看得见。
        Assert.Equal(4 * 64 * 3, combinations.Length);
    }

    /// <summary>
    /// 什么时候给车发快照（票面第 7 条）：进出 WAIT、FULL、CLOSED 各发一次；唯一不发的变化是不适用持货的旅程装完了。
    /// </summary>
    [Theory]
    [InlineData(null, null, Loading, null, true, false)]
    [InlineData(Loading, null, Wait, null, true, true)]
    [InlineData(Wait, null, Full, null, true, true)]
    [InlineData(Full, null, Wait, null, true, true)]
    [InlineData(Wait, null, Loading, null, true, true)]
    [InlineData(Full, null, Closed, LoadingClosedReasons.VehicleFull, true, true)]
    [InlineData(Wait, null, Closed, LoadingClosedReasons.CargoHoldingTimeout, true, true)]
    [InlineData(Loading, null, Closed, LoadingClosedReasons.CargoHoldingTimeout, true, true)]
    // 适用持货等单的旅程装完之后进 WAIT 或 FULL，不会直接到 PLANNED；真到了（列落地之前在途的旅程离站）也发，因为那是持货的车。
    [InlineData(Loading, null, Closed, LoadingClosedReasons.PlannedLoadingComplete, true, true)]
    // 不适用持货等单：装完不发——与批次 7 之前逐条相同的那个保证。
    [InlineData(Loading, null, Closed, LoadingClosedReasons.PlannedLoadingComplete, false, false)]
    [InlineData(null, null, Closed, LoadingClosedReasons.PlannedLoadingComplete, false, false)]
    // 不适用了，但原来在等：离开 WAIT 要告诉车。
    [InlineData(Wait, null, Closed, LoadingClosedReasons.PlannedLoadingComplete, false, true)]
    [InlineData(Closed, LoadingClosedReasons.VehicleFull, Closed, LoadingClosedReasons.VehicleFull, true, false)]
    public void AnnouncesExactlyTheTransitionsTheVehicleShows(
        string? fromState, string? fromReason, string toState, string? toReason, bool applicable, bool announces)
    {
        Assert.Equal(
            announces,
            LoadingPhaseMachine.Announces(
                fromState, fromReason, new LoadingPhaseMachine.Decision(toState, toReason), applicable));
    }

    /// <summary>
    /// 持货期限（program#94）：适用持货等单、已经起算时是起算点加期限，其余为空；进入 CLOSED 后保留原值、不清空。
    /// </summary>
    [Fact]
    public void TheDeadlineIsShownWhenHoldingAppliesAndKeptAfterClosing()
    {
        DateTimeOffset started = new(2026, 9, 21, 8, 0, 0, TimeSpan.Zero);
        TimeSpan timeout = TimeSpan.FromMinutes(30);

        Assert.Equal(started + timeout, LoadingPhaseMachine.Deadline(Wait, started, timeout, holdingApplicable: true));
        Assert.Equal(started + timeout, LoadingPhaseMachine.Deadline(Loading, started, timeout, holdingApplicable: true));
        Assert.Equal(started + timeout, LoadingPhaseMachine.Deadline(Full, started, timeout, holdingApplicable: true));
        Assert.Equal(started + timeout, LoadingPhaseMachine.Deadline(null, started, timeout, holdingApplicable: true));
        // 第一个 LoadBatch 闭环之前（空载）不适用。
        Assert.Null(LoadingPhaseMachine.Deadline(Loading, null, timeout, holdingApplicable: true));
        // 不适用持货等单：与批次 7 之前逐条相同。
        Assert.Null(LoadingPhaseMachine.Deadline(Loading, started, timeout, holdingApplicable: false));
        // 阶段结束后保留结束前的值（program#94 语义表：「进入 CLOSED 后保留原值、不清空」，车载端显示依赖它）。
        Assert.Equal(started + timeout, LoadingPhaseMachine.Deadline(Closed, started, timeout, holdingApplicable: true));
    }
}
