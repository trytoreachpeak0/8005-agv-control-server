using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using static ControlServer.Tests.Batch7StopDrivenAdvanceDriver;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;

namespace ControlServer.Tests;

/// <summary>
/// 在途单停住时旅程要说出原因（control-server#316，#299 拆出的 T1）：RIoT 挂起（9 HANG）、语义不明的 8、
/// 被人在 RIoT 里取消或删除。
/// </summary>
/// <remarks>
/// <para>
/// <b>修之前三种都静默。</b>网关把 9 判 Active、把 2／6／8 判 Terminal；引擎的在途分支到站只认 Terminal+SUCCESS，
/// 故障只认 Terminal+FAILED，其余一律走 <c>NameCheckpointWaitAsync</c>，车不在检查点时它把码清成空。
/// 旅程停在到站阶段，阻断码为空，看板一行都不列。
/// </para>
/// <para>
/// <b>9 只是「看得见、会告警、不接追加」，不进故障模型</b>（用户 2026-09-22 定 H-a）：不 Hold、不急停、不记故障事实。
/// 把 9 原样交给故障协调器会在两站之间触发急停，而闩锁下 continue 被拒、人工解除又因 HANG 算未完成订单而被拒——
/// #299 阶段一方案第三节。所以这里每条 9 的用例都同时断「命令审计表是空的、故障表是空的」。
/// </para>
/// <para>
/// 码写成字面量而不引常量：它们是看板上给人看的东西，改名应当让这里红。
/// </para>
/// </remarks>
public sealed class InTransitOrderStallTests
{
    private const string OrderHang = "ORDER_HANG";
    private const string OrderStateUnrecognized = "ORDER_STATE_UNRECOGNIZED";
    private const string OrderEndedWithoutArrival = "ORDER_ENDED_WITHOUT_ARRIVAL";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// 开往取货站的单挂起：旅程写 <c>ORDER_HANG</c>、告警一次；阶段不动，不发任何订单或急停命令，不记故障。
    /// </summary>
    /// <remarks>
    /// 跑三轮：第二、三轮是「同一件事再发生一次」——码与开始时刻不变，告警不重复，命令一条都没有。
    /// </remarks>
    [Fact]
    public async Task AHangingPickupOrderIsNamedAndNothingIsCommanded()
    {
        await using RuntimeFixture fixture = await DispatchedToPickupAsync();
        JourneyRuntimeRow before = await fixture.RuntimeAsync();
        fixture.Riot.SetOrderState(before.PickupUpperId, RiotOrderState.Hang, terminal: false);

        await TickAndRunAsync(fixture);
        JourneyRuntimeRow first = await fixture.RuntimeAsync();
        await TickAndRunAsync(fixture);
        await TickAndRunAsync(fixture);

        JourneyRuntimeRow after = await fixture.RuntimeAsync();
        Assert.Equal((JourneyRuntimeStage.AwaitingPickupArrival, OrderHang), (after.Stage, after.BlockReasonCode));
        Assert.True(first.BlockReasonSince < fixture.Clock.GetUtcNow(), "the clock did not move, so keeping the start time proves nothing");
        Assert.Equal(first.BlockReasonSince, after.BlockReasonSince);
        Assert.NotNull(after.BlockReasonSince);
        await AssertNothingCommandedAsync(fixture);
        Assert.Single(fixture.EngineLog.Entries, entry =>
            entry.Level == LogLevel.Warning &&
            entry.Message.Contains(before.PickupUpperId, StringComparison.Ordinal) &&
            entry.Message.Contains("HANG", StringComparison.Ordinal));
    }

    /// <summary>
    /// 装着货开往关卡的单挂起：同样的码、同样什么都不发。关卡腿与取货腿是两个分支，各自要一条。
    /// </summary>
    [Fact]
    public async Task AHangingGateOrderIsNamedAndNothingIsCommanded()
    {
        await using RuntimeFixture fixture = await GateArrivalWaitAsync();
        JourneyRuntimeRow before = await fixture.RuntimeAsync();
        fixture.Riot.SetOrderState(before.GateUpperId, RiotOrderState.Hang, terminal: false);

        await TickAndRunAsync(fixture);
        JourneyRuntimeRow first = await fixture.RuntimeAsync();
        await TickAndRunAsync(fixture);

        JourneyRuntimeRow after = await fixture.RuntimeAsync();
        Assert.Equal((JourneyRuntimeStage.AwaitingGateArrival, OrderHang), (after.Stage, after.BlockReasonCode));
        Assert.NotNull(first.BlockReasonSince);
        Assert.True(first.BlockReasonSince < fixture.Clock.GetUtcNow(), "the clock did not move, so keeping the start time proves nothing");
        Assert.Equal(first.BlockReasonSince, after.BlockReasonSince);
        await AssertNothingCommandedAsync(fixture);
    }

    /// <summary>
    /// 人在 RIoT 里 continue：订单回到 3，码清掉；continue 之后仍挂起就维持原码与原开始时刻；最后到站照常推进。
    /// </summary>
    /// <remarks>
    /// 清码必须是本票自己做的：<c>NameCheckpointWaitAsync</c> 只清它自己那两个码，<c>SetStage</c> 只在换段时清，
    /// 而「挂起→继续」发生在同一个阶段里。实验室 BC-ORDER-018：continue 接口成功而订单仍 HANG 是会发生的，
    /// 所以中间那一段「再挂起」不是虚构的格子。
    /// </remarks>
    [Fact]
    public async Task AContinuedOrderClearsTheReasonAndTheJourneyArrives()
    {
        await using RuntimeFixture fixture = await DispatchedToPickupAsync();
        JourneyRuntimeRow before = await fixture.RuntimeAsync();
        fixture.Riot.SetOrderState(before.PickupUpperId, RiotOrderState.Hang, terminal: false);
        await TickAndRunAsync(fixture);
        DateTimeOffset? since = (await fixture.RuntimeAsync()).BlockReasonSince;

        // continue 了，但仍挂着：码与开始时刻都不动。
        await TickAndRunAsync(fixture);
        Assert.True(since < fixture.Clock.GetUtcNow(), "the clock did not move, so keeping the start time proves nothing");
        JourneyRuntimeRow stillHanging = await fixture.RuntimeAsync();
        Assert.Equal((OrderHang, since), (stillHanging.BlockReasonCode, stillHanging.BlockReasonSince));

        fixture.Riot.SetOrderState(before.PickupUpperId, RiotOrderState.Executing, terminal: false);
        await TickAndRunAsync(fixture);
        JourneyRuntimeRow resumed = await fixture.RuntimeAsync();
        Assert.Equal((JourneyRuntimeStage.AwaitingPickupArrival, (string?)null, (DateTimeOffset?)null),
            (resumed.Stage, resumed.BlockReasonCode, resumed.BlockReasonSince));

        JourneyRuntimeRow arrived = await ArriveAtCurrentStopAsync(fixture, FirstDemandId, "TO_PICKUP");
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, arrived.Stage);
        await AssertNothingCommandedAsync(fixture);
    }

    /// <summary>
    /// 挂起期间一次读不到订单（网关不抛、返回 Unknown）：码与开始时刻都不动，告警不重复。
    /// </summary>
    /// <remarks>
    /// 读不到不是「订单继续了」。第一版把一切非停住的观测都当成继续、清掉码，于是一次读超时让码消失一轮、开始时刻重置、
    /// 告警再打一次，而那一轮这辆车重新进 underWay，可以被追加（独立审查第 1 条）。只有读到明确的非停住状态才清。
    /// </remarks>
    [Fact]
    public async Task AnUnreadableOrderKeepsTheHangReasonAndItsStart()
    {
        await using RuntimeFixture fixture = await DispatchedToPickupAsync();
        JourneyRuntimeRow before = await fixture.RuntimeAsync();
        fixture.Riot.SetOrderState(before.PickupUpperId, RiotOrderState.Hang, terminal: false);
        await TickAndRunAsync(fixture);
        DateTimeOffset? since = (await fixture.RuntimeAsync()).BlockReasonSince;

        fixture.Riot.MakeOrderUnreadable(before.PickupUpperId);
        await TickAndRunAsync(fixture);
        JourneyRuntimeRow unread = await fixture.RuntimeAsync();
        Assert.True(since < fixture.Clock.GetUtcNow(), "the clock did not move, so keeping the start time proves nothing");
        Assert.Equal((OrderHang, since), (unread.BlockReasonCode, unread.BlockReasonSince));

        fixture.Riot.SetOrderState(before.PickupUpperId, RiotOrderState.Hang, terminal: false);
        await TickAndRunAsync(fixture);
        JourneyRuntimeRow again = await fixture.RuntimeAsync();
        Assert.Equal((OrderHang, since), (again.BlockReasonCode, again.BlockReasonSince));
        Assert.Single(fixture.EngineLog.Entries, entry =>
            entry.Level == LogLevel.Warning && entry.Message.Contains("ORDER_HANG", StringComparison.Ordinal));
    }

    /// <summary>
    /// 8 SUSPENDED：写 <c>ORDER_STATE_UNRECOGNIZED</c>，不做任何自动动作。
    /// </summary>
    /// <remarks>
    /// 8 在实验室零观测，SDK 标注「已移除」，保守当活单；网关把它归终态的问题归 cs#296。这里只要求它不再静默。
    /// </remarks>
    [Fact]
    public async Task ASuspendedOrderIsNamedAsUnrecognizedAndNothingIsDone()
    {
        await using RuntimeFixture fixture = await DispatchedToPickupAsync();
        JourneyRuntimeRow before = await fixture.RuntimeAsync();
        fixture.Riot.SetOrderState(before.PickupUpperId, RiotOrderState.Suspended, terminal: true);

        await TickAndRunAsync(fixture);
        await TickAndRunAsync(fixture);

        JourneyRuntimeRow after = await fixture.RuntimeAsync();
        Assert.Equal((JourneyRuntimeStage.AwaitingPickupArrival, OrderStateUnrecognized),
            (after.Stage, after.BlockReasonCode));
        await AssertNothingCommandedAsync(fixture);
        Assert.Null((await fixture.LeaseAsync()).ReleasedAt);
    }

    /// <summary>
    /// 在途单被人在 RIoT 里取消或删除：旅程写 <c>ORDER_ENDED_WITHOUT_ARRIVAL</c>，不结束旅程、不释放，也不发命令。
    /// </summary>
    /// <remarks>
    /// 用户 2026-09-22：RIoT 里取消多半是误操作，该重建而不是改派。重建由 control-server#318 在延迟之后做
    /// （<see cref="OwnOrderRebuildTests"/>），这里只跑一轮，守的是「先说出原因、不释放」这一半。释放服务一侧同样不动它
    /// （<see cref="Batch7DemandReleaseServiceTests.APickupOrderEndedInRiotIsNeitherReleasedNorRedispatched"/>）。
    /// </remarks>
    [Theory]
    [InlineData(RiotOrderState.Cancelled)]
    [InlineData(RiotOrderState.Deleted)]
    public async Task AnOrderEndedInRiotIsNamed(int orderState)
    {
        await using RuntimeFixture fixture = await GateArrivalWaitAsync();
        JourneyRuntimeRow before = await fixture.RuntimeAsync();
        fixture.Riot.SetOrderState(before.GateUpperId, orderState, terminal: true);

        await fixture.Engine.ExecuteOnceAsync(Token);

        JourneyRuntimeRow after = await fixture.RuntimeAsync();
        Assert.Equal((JourneyRuntimeStage.AwaitingGateArrival, OrderEndedWithoutArrival),
            (after.Stage, after.BlockReasonCode));
        Assert.Equal(DemandExecutionStatus.Accepted, (await fixture.DemandRowAsync()).Status);
        await AssertNothingCommandedAsync(fixture);
    }

    /// <summary>
    /// FAILED 仍走故障路径，码仍是 <c>VEHICLE_ORDER_FAILED</c>：新分支排在故障判据之后，不抢它。
    /// </summary>
    [Fact]
    public async Task AFailedOrderStillGoesToTheFaultModel()
    {
        await using RuntimeFixture fixture = await DispatchedToPickupAsync();
        JourneyRuntimeRow before = await fixture.RuntimeAsync();
        fixture.Riot.FailOrder(before.PickupUpperId);

        await TickAndRunAsync(fixture);

        Assert.Equal("VEHICLE_ORDER_FAILED", (await fixture.RuntimeAsync()).BlockReasonCode);
        Assert.Single(await fixture.Context.VehicleFaultStates.AsNoTracking().ToArrayAsync(Token));
    }

    /// <summary>
    /// 挂起之后车载端也不说话了：看板上仍是「单挂起」，不被「车不说话」盖掉，开始时刻也不重置。
    /// </summary>
    /// <remarks>
    /// 与 <c>VEHICLE_ORDER_FAILED</c> 同一个理由（<c>OnboardSessionLostBlockTests.ASilentSessionDoesNotOverwriteAFailedOrderAsTheReason</c>）：
    /// 执行中关机再开机正是进 HANG 的一条路（BC-ORDER-015 P），挂起与失联常常是同一个事件的两面，
    /// 走到车前的人要知道的是订单挂着、需要 continue 或取消。
    /// </remarks>
    [Fact]
    public async Task ASilentSessionDoesNotOverwriteAHangingOrderAsTheReason()
    {
        await using RuntimeFixture fixture = await GateArrivalWaitAsync();
        JourneyRuntimeRow before = await fixture.RuntimeAsync();
        fixture.Riot.SetOrderState(before.GateUpperId, RiotOrderState.Hang, terminal: false);
        await fixture.Engine.ExecuteOnceAsync(Token);
        DateTimeOffset? since = (await fixture.RuntimeAsync()).BlockReasonSince;
        Assert.Equal(OrderHang, (await fixture.RuntimeAsync()).BlockReasonCode);

        await fixture.HearFromPeerAsync();
        fixture.Clock.Advance(SessionLiveness.Timeout + TimeSpan.FromSeconds(4));
        await fixture.Engine.ExecuteOnceAsync(Token);
        await fixture.Engine.ExecuteOnceAsync(Token);

        JourneyRuntimeRow held = await fixture.RuntimeAsync();
        Assert.True(since < fixture.Clock.GetUtcNow(), "the clock did not move, so keeping the start time proves nothing");
        Assert.Equal((OrderHang, since), (held.BlockReasonCode, held.BlockReasonSince));
    }

    // ---- 车载端因为本服务端自己的在途单而未就绪（独立审查高项） ------------------------------------------------

    /// <summary>
    /// 车在途挂起、车载端会话因本服务端自己的在途单而未就绪：旅程仍然写 <c>ORDER_HANG</c>，不被 <c>ONBOARD_SESSION_NOT_READY</c> 盖掉。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>这正是真车上的常态，不是边角。</b>9 在 <c>NonFinalOrderStates</c> 里，所以车在途时车载端读到的安全接口带
    /// <c>RIOT_NONFINAL_ORDER_PRESENT</c>，报 <c>VEHICLE_NOT_READY</c>，会话落到 <c>DEPARTURE_SAFETY_NOT_READY</c>。
    /// <c>AdvanceAsync</c> 的会话闸门在那种会话上写 <c>ONBOARD_SESSION_NOT_READY</c> 就返回，走不到在途分支——第一版在
    /// 真车载端上一次 <c>ORDER_HANG</c> 都写不出来，2127 告警也一次都不打。合成车载端永远报安全，所以合成 L2 按构造看不见它。
    /// </para>
    /// <para>会话的形状照 <c>PickupDispatchPlanPastOwnOrderTests.DropSessionOnOwnOrderAsync</c>（失败现场的 <c>SessionRecoveries</c>）。</para>
    /// </remarks>
    [Fact]
    public async Task AHangingOrderIsNamedWhileTheSessionIsNotReadyOnItsOwnOrder()
    {
        await using RuntimeFixture fixture = await DispatchedToPickupAsync();
        JourneyRuntimeRow before = await fixture.RuntimeAsync();
        fixture.Riot.SetOrderState(before.PickupUpperId, RiotOrderState.Hang, terminal: false);
        await DropSessionOnOwnOrderAsync(fixture);

        await TickAndRunAsync(fixture);
        DateTimeOffset? since = (await fixture.RuntimeAsync()).BlockReasonSince;
        await TickAndRunAsync(fixture);

        JourneyRuntimeRow after = await fixture.RuntimeAsync();
        Assert.Equal((JourneyRuntimeStage.AwaitingPickupArrival, OrderHang), (after.Stage, after.BlockReasonCode));
        Assert.NotNull(since);
        Assert.True(since < fixture.Clock.GetUtcNow(), "the clock did not move, so keeping the start time proves nothing");
        Assert.Equal(since, after.BlockReasonSince);
        await AssertNothingCommandedAsync(fixture);
        Assert.Single(fixture.EngineLog.Entries, entry =>
            entry.Level == LogLevel.Warning && entry.Message.Contains("ORDER_HANG", StringComparison.Ordinal));
    }

    /// <summary>
    /// 未就绪期间人在 RIoT 里 continue：订单回到 3，停住码清掉，旅程回到会话闸门本来的码 <c>ONBOARD_SESSION_NOT_READY</c>。
    /// </summary>
    /// <remarks>
    /// <para>清码必须能在闸门后面跑：否则一次挂起过后，看板永远说「挂起」，而车其实在走。</para>
    /// <para>
    /// <b>这一条的判别力来自它的前置，不来自最后那一句断言。</b>修前闸门本来就写 <c>ONBOARD_SESSION_NOT_READY</c>，所以只看最后一句，
    /// 修前修后都绿；它修前红，是红在中间那句「未就绪时先写出了 ORDER_HANG」。守「会清码」的是最后一句——闸门路径写出码之后若不清，
    /// 它会读到 <c>ORDER_HANG</c>。两句要一起读（增量审查）。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AContinueWhileTheSessionIsNotReadyGivesTheReasonBackToTheGate()
    {
        await using RuntimeFixture fixture = await DispatchedToPickupAsync();
        JourneyRuntimeRow before = await fixture.RuntimeAsync();
        fixture.Riot.SetOrderState(before.PickupUpperId, RiotOrderState.Hang, terminal: false);
        await DropSessionOnOwnOrderAsync(fixture);
        await TickAndRunAsync(fixture);
        Assert.Equal(OrderHang, (await fixture.RuntimeAsync()).BlockReasonCode);

        fixture.Riot.SetOrderState(before.PickupUpperId, RiotOrderState.Executing, terminal: false);
        await TickAndRunAsync(fixture);

        Assert.Equal("ONBOARD_SESSION_NOT_READY", (await fixture.RuntimeAsync()).BlockReasonCode);
    }

    /// <summary>
    /// 未就绪期间订单被人在 RIoT 里取消：同样写 <c>ORDER_ENDED_WITHOUT_ARRIVAL</c>，不被闸门盖掉。
    /// </summary>
    [Fact]
    public async Task AnOrderEndedWhileTheSessionIsNotReadyIsNamed()
    {
        await using RuntimeFixture fixture = await DispatchedToPickupAsync();
        JourneyRuntimeRow before = await fixture.RuntimeAsync();
        fixture.Riot.CancelOrder(before.PickupUpperId);
        await DropSessionOnOwnOrderAsync(fixture);

        await TickAndRunAsync(fixture);
        await TickAndRunAsync(fixture);

        Assert.Equal(OrderEndedWithoutArrival, (await fixture.RuntimeAsync()).BlockReasonCode);
        await AssertNothingCommandedAsync(fixture);
    }

    /// <summary>
    /// 未就绪期间一次读不到订单：挂起码与开始时刻都不动（与闸门打开时同一条规则）。
    /// </summary>
    [Fact]
    public async Task AnUnreadableOrderWhileTheSessionIsNotReadyKeepsTheHangReason()
    {
        await using RuntimeFixture fixture = await DispatchedToPickupAsync();
        JourneyRuntimeRow before = await fixture.RuntimeAsync();
        fixture.Riot.SetOrderState(before.PickupUpperId, RiotOrderState.Hang, terminal: false);
        await DropSessionOnOwnOrderAsync(fixture);
        await TickAndRunAsync(fixture);
        DateTimeOffset? since = (await fixture.RuntimeAsync()).BlockReasonSince;
        Assert.Equal(OrderHang, (await fixture.RuntimeAsync()).BlockReasonCode);

        fixture.Riot.MakeOrderUnreadable(before.PickupUpperId);
        await TickAndRunAsync(fixture);

        JourneyRuntimeRow after = await fixture.RuntimeAsync();
        Assert.True(since < fixture.Clock.GetUtcNow(), "the clock did not move, so keeping the start time proves nothing");
        Assert.Equal((OrderHang, since), (after.BlockReasonCode, after.BlockReasonSince));
    }

    /// <summary>车载端读到车辆安全接口之后会话行变成的样子，照 <c>PickupDispatchPlanPastOwnOrderTests.DropSessionOnOwnOrderAsync</c>。</summary>
    private static async Task DropSessionOnOwnOrderAsync(RuntimeFixture fixture)
    {
        SessionRecoveryRow session = await fixture.Context.SessionRecoveries.SingleAsync(Token);
        session.Readiness = SessionReadiness.RecoveryRequired;
        session.ReasonCode = "DEPARTURE_SAFETY_NOT_READY";
        session.DepartureSafe = false;
        session.SafetyReasonCodesJson = """["VEHICLE_NOT_READY"]""";
        session.SafetyUnknownPresent = true;
        session.UpdatedAt = fixture.Clock.GetUtcNow();
        await fixture.Context.SaveChangesAsync(Token);
        fixture.Context.ChangeTracker.Clear();
    }

    private static async Task AssertNothingCommandedAsync(RuntimeFixture fixture)
    {
        await using ControlServerDbContext reading = new(fixture.DbOptionsForTests);
        Assert.Empty(await reading.RiotOrderCommandAudit.AsNoTracking().ToArrayAsync(Token));
        Assert.Empty(await reading.VehicleFaultStates.AsNoTracking().ToArrayAsync(Token));
    }

    private static async Task<RuntimeFixture> DispatchedToPickupAsync()
    {
        RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(FirstDemandId, FirstSublot, Now.AddMinutes(-10)));
        fixture.BoxCounts.Set(FirstSublot, 7);
        await TickAndRunAsync(fixture);
        Assert.Equal(JourneyRuntimeStage.AwaitingPickupArrival, (await fixture.RuntimeAsync()).Stage);
        fixture.Context.ChangeTracker.Clear();
        return fixture;
    }

    private static async Task<RuntimeFixture> GateArrivalWaitAsync()
    {
        RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(FirstDemandId, FirstSublot, createdAt: Now.AddMinutes(-10)));
        fixture.BoxCounts.Set(FirstSublot, 7);
        await fixture.AdvanceToGateArrivalAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingGateArrival, (await fixture.RuntimeAsync()).Stage);
        fixture.Context.ChangeTracker.Clear();
        return fixture;
    }
}
