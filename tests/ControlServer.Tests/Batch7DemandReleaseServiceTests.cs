using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Commands;
using ControlServer.Host.Runtime.Dispatch.Criteria;
using ControlServer.Host.Runtime.Fleet;
using ControlServer.Host.Runtime.Release;
using ControlServer.Host.Runtime.RouteGraph;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using static ControlServer.Tests.Batch7StopDrivenAdvanceDriver;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;

namespace ControlServer.Tests;

/// <summary>
/// REQ-0328 的释放服务落到库里（批次7-10，control-server#215）：取消取货单并对账、释放与退回积压同一事务、年龄保留；
/// 取消没确认不释放、不重发取消；到站之后不释放；取货停靠在后面的需求不碰订单直接释放。
/// </summary>
/// <remarks>
/// <para>
/// 旅程由真实引擎受理并派往取货站（<see cref="RuntimeFixture"/>），车辆级准入的失效用「车离开本图」造：
/// 把 RIoT 观测里的地图换掉，其余一样不动。取消命令走真的订单命令面与审计存储，只有 RIoT 那一侧是替身——
/// 它收到取消时是否真把订单置成 CANCELLED，由每条用例自己定，于是「对账确认」与「对账没确认」两条路都走得到。
/// </para>
/// </remarks>
public sealed class Batch7DemandReleaseServiceTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheLastDemandOnItsWayToPickupIsReleasedOnceItsOrderIsConfirmedCancelled()
    {
        await using RuntimeFixture fixture = await DispatchedToPickupAsync();
        JourneyRuntimeRow before = await fixture.RuntimeAsync(FirstDemandId);
        DateTimeOffset firstSeen = (await fixture.BacklogAsync(FirstDemandId)).FirstSeenAt;
        // 钟先往前拨：不拨的话「重置成此刻」与「原样保留」是同一个值，年龄保留那条断言分不出两者（反向验证时就是这样漏的）。
        fixture.Clock.Advance(TimeSpan.FromMinutes(5));
        LeaveTheMap(fixture);
        CancellingGateway gateway = new(fixture.Clock, orderId => fixture.Riot.CancelOrder(before.PickupUpperId));

        IReadOnlyList<DemandReleaseOutcome> outcomes = await Service(fixture, gateway).RunOnceAsync(Token);

        Assert.Equal([(FirstDemandId, DemandReleaseRules.MapMismatchReason, "RELEASED")],
            outcomes.Select(outcome => (outcome.DemandId, outcome.Trigger, outcome.Result)));
        Assert.Equal(1, gateway.Cancels);

        await using ControlServerDbContext reading = new ControlServerDbContext(fixture.DbOptionsForTests);
        JourneyDemandRow membership = await reading.Set<JourneyDemandRow>().AsNoTracking()
            .SingleAsync(row => row.DemandId == FirstDemandId, Token);
        Assert.NotNull(membership.RemovedAt);
        Assert.Equal(DemandReleaseReasons.Released, membership.RemovalReason);
        JourneyRuntimeRow after = await reading.JourneyRuntimes.AsNoTracking().SingleAsync(Token);
        Assert.Equal((JourneyRuntimeStage.Completed, DemandReleaseReasons.Released), (after.Stage, after.BlockReasonCode));
        Assert.False(await reading.VehicleDispatchLeases.AnyAsync(row => row.ReleasedAt == null, Token));
        Assert.Equal(DemandExecutionStatus.Accepted,
            (await reading.AcceptedDemands.AsNoTracking().SingleAsync(row => row.DemandId == FirstDemandId, Token)).Status);
        Assert.True(await DemandJourneyLookup.ReleasedForRedispatch(reading).AnyAsync(row => row.DemandId == FirstDemandId, Token));

        JourneyBacklogRow backlog = await reading.JourneyBacklog.AsNoTracking().SingleAsync(row => row.DemandId == FirstDemandId, Token);
        Assert.Null(backlog.AcceptedAt);
        Assert.Equal(DemandReleaseReasons.Released, backlog.ReasonCode);
        Assert.Equal(firstSeen, backlog.FirstSeenAt);
        Assert.True(firstSeen < fixture.Clock.GetUtcNow() - TimeSpan.FromMinutes(4), "the clock did not move, so the age check proves nothing");
    }

    /// <summary>
    /// RIoT 没把订单置成 CANCELLED：结果不是 Confirmed 就不释放，写阻断原因；下一轮只对账那一次取消，不发第二次
    /// ——这就是「取消已发出而释放没落库时崩溃，重启后先对账」那条崩溃点，库里只有一条审计行是它的证据。
    /// </summary>
    [Fact]
    public async Task AnUnconfirmedCancellationReleasesNothingAndIsNeverIssuedTwice()
    {
        await using RuntimeFixture fixture = await DispatchedToPickupAsync();
        JourneyRuntimeRow before = await fixture.RuntimeAsync(FirstDemandId);
        LeaveTheMap(fixture);
        CancellingGateway gateway = new(fixture.Clock, _ => { });

        IReadOnlyList<DemandReleaseOutcome> first = await Service(fixture, gateway).RunOnceAsync(Token);
        IReadOnlyList<DemandReleaseOutcome> second = await Service(fixture, gateway).RunOnceAsync(Token);

        Assert.Equal(DemandReleaseReasons.OrderCancelNotConfirmed, Assert.Single(first).Result);
        Assert.Equal(DemandReleaseReasons.OrderCancelNotConfirmed, Assert.Single(second).Result);
        Assert.Equal(1, gateway.Cancels);
        await using ControlServerDbContext reading = new ControlServerDbContext(fixture.DbOptionsForTests);
        Assert.Equal(1, await reading.RiotOrderCommandAudit.CountAsync(row => row.TargetUpperId == before.PickupUpperId, Token));
        JourneyRuntimeRow after = await reading.JourneyRuntimes.AsNoTracking().SingleAsync(Token);
        Assert.Equal((before.Stage, DemandReleaseReasons.OrderCancelNotConfirmed), (after.Stage, after.BlockReasonCode));
        Assert.Null((await reading.Set<JourneyDemandRow>().AsNoTracking().SingleAsync(Token)).RemovedAt);
        Assert.NotNull((await reading.JourneyBacklog.AsNoTracking().SingleAsync(row => row.DemandId == FirstDemandId, Token)).AcceptedAt);

        // 订单后来真的取消了：下一轮对账那一次、确认、释放，仍然只发过一次取消。
        fixture.Riot.CancelOrder(before.PickupUpperId);
        IReadOnlyList<DemandReleaseOutcome> third = await Service(fixture, gateway).RunOnceAsync(Token);
        Assert.Equal("RELEASED", Assert.Single(third).Result);
        Assert.Equal(1, gateway.Cancels);
        // 那一次取消的审计行也对账成了 Confirmed。复审中 2 之后释放先读订单，读到 CANCELLED 就当「没有活订单」直接放，
        // 一度跳过了对账，审计行永远停在 Pending——服务端明明知道取消成了，记录却说不知道（L2-RVI-04 抓到，L1 当时没断这一项）。
        await using ControlServerDbContext settled = new ControlServerDbContext(fixture.DbOptionsForTests);
        Assert.Equal(RiotOrderCommandOutcome.Confirmed, (await settled.RiotOrderCommandAudit.AsNoTracking()
            .SingleAsync(row => row.TargetUpperId == before.PickupUpperId, Token)).Outcome);
    }

    [Fact]
    public async Task AnEligibleVehicleKeepsItsDemands()
    {
        await using RuntimeFixture fixture = await DispatchedToPickupAsync();
        CancellingGateway gateway = new(fixture.Clock, _ => { });

        Assert.Empty(await Service(fixture, gateway).RunOnceAsync(Token));
        Assert.Equal(0, gateway.Cancels);
    }

    [Fact]
    public async Task OnceTheVehicleHasArrivedTheDemandIsNotReleasedAndNoOrderIsTouched()
    {
        await using RuntimeFixture fixture = await DispatchedToPickupAsync();
        await ArriveAtPickupAsync(fixture, FirstDemandId);
        LeaveTheMap(fixture);
        CancellingGateway gateway = new(fixture.Clock, _ => { });

        IReadOnlyList<DemandReleaseOutcome> outcomes = await Service(fixture, gateway).RunOnceAsync(Token);

        Assert.Equal(DemandReleaseReasons.AfterArrival, Assert.Single(outcomes).Result);
        Assert.Equal(0, gateway.Cancels);
        await using ControlServerDbContext reading = new ControlServerDbContext(fixture.DbOptionsForTests);
        Assert.Null((await reading.Set<JourneyDemandRow>().AsNoTracking().SingleAsync(Token)).RemovedAt);
    }

    /// <summary>
    /// 到站之后不释放是正常作业，不是阻断：不写旅程的阻断码（审查 M4）。
    /// </summary>
    /// <remarks>
    /// 旧实现写 RELEASE_AFTER_ARRIVAL；引擎在站上等离站那一段每轮清阻断码，释放服务下一轮又写回去，
    /// <c>BlockReasonSince</c> 每轮重置，看板显示阻断而车在正常作业；引擎只在阻断码为空时才写的码（预离站核验过期）还会被它遮住。
    /// </remarks>
    [Fact]
    public async Task ARefusalAfterArrivalWritesNoBlockReason()
    {
        await using RuntimeFixture fixture = await DispatchedToPickupAsync();
        await ArriveAtPickupAsync(fixture, FirstDemandId);
        fixture.Context.ChangeTracker.Clear();
        Assert.Null((await fixture.RuntimeAsync(FirstDemandId)).BlockReasonCode);
        LeaveTheMap(fixture);

        Assert.Equal(DemandReleaseReasons.AfterArrival,
            Assert.Single(await Service(fixture, new CancellingGateway(fixture.Clock, _ => { })).RunOnceAsync(Token)).Result);

        fixture.Context.ChangeTracker.Clear();
        Assert.Null((await fixture.RuntimeAsync(FirstDemandId)).BlockReasonCode);
    }

    /// <summary>
    /// 释放被拒写下的码，换成另一个拒绝码时改写；车重新合格之后清掉（审查 M4）。
    /// </summary>
    /// <remarks>
    /// 旧实现只在阻断码为空时写、从不清：第一个拒绝码一直挂着，原因变了看不见，车好了也不掉。
    /// </remarks>
    [Fact]
    public async Task AReleaseRefusalFollowsTheReasonAndIsClearedOnceTheVehicleIsEligibleAgain()
    {
        await using RuntimeFixture fixture = await DispatchedToPickupAsync();
        JourneyRuntimeRow before = await fixture.RuntimeAsync(FirstDemandId);
        LeaveTheMap(fixture);
        CancellingGateway gateway = new(fixture.Clock, _ => { });
        Assert.Equal(DemandReleaseReasons.OrderCancelNotConfirmed,
            Assert.Single(await Service(fixture, gateway).RunOnceAsync(Token)).Result);

        // 换成另一个拒绝原因：意图对账没有结论的样子（没有订单号、创建已发出），码跟着换。
        OrderIntentRow intent = await fixture.Context.OrderIntents
            .SingleAsync(row => row.UpperId == before.PickupUpperId, Token);
        string orderId = intent.OrderId!;
        intent.OrderId = null;
        intent.Status = "RESULT_UNKNOWN";
        await fixture.Context.SaveChangesAsync(Token);
        fixture.Context.ChangeTracker.Clear();
        Assert.Equal(DemandReleaseReasons.OrderStateUnknown,
            Assert.Single(await Service(fixture, gateway).RunOnceAsync(Token)).Result);
        Assert.Equal(DemandReleaseReasons.OrderStateUnknown, (await fixture.RuntimeAsync(FirstDemandId)).BlockReasonCode);
        Assert.NotNull(orderId);

        // 车回到本图：没有任何需求被判不合格，释放留下的码清掉。
        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { CurrentMap = fixture.Options.MapIdentity };
        Assert.Empty(await Service(fixture, gateway).RunOnceAsync(Token));
        fixture.Context.ChangeTracker.Clear();
        Assert.Null((await fixture.RuntimeAsync(FirstDemandId)).BlockReasonCode);
    }

    /// <summary>
    /// 读不到车不等于车重新合格：拒绝码不清（审查 M4 的清码条件）。
    /// </summary>
    /// <remarks>
    /// 读不到时规则入口的门让每条判据都说「仍合格」（审查 S1）。拿这个去清码，下一轮读到了又写回去，阻断开始时刻每轮重置。
    /// </remarks>
    [Fact]
    public async Task AFailedReadDoesNotClearARefusal()
    {
        await using RuntimeFixture fixture = await DispatchedToPickupAsync();
        LeaveTheMap(fixture);
        CancellingGateway gateway = new(fixture.Clock, _ => { });
        Assert.Equal(DemandReleaseReasons.OrderCancelNotConfirmed,
            Assert.Single(await Service(fixture, gateway).RunOnceAsync(Token)).Result);

        fixture.Riot.Vehicle = fixture.Riot.Vehicle with
        {
            Connected = false, Enabled = false, ProcState = "UNKNOWN", CurrentMap = string.Empty, CurrentStationId = null,
        };
        Assert.Empty(await Service(fixture, gateway).RunOnceAsync(Token));
        fixture.Context.ChangeTracker.Clear();
        Assert.Equal(DemandReleaseReasons.OrderCancelNotConfirmed, (await fixture.RuntimeAsync(FirstDemandId)).BlockReasonCode);
    }

    /// <summary>
    /// 引擎自己的阻断码既不被释放的拒绝盖掉，也不被「车重新合格」清掉：清的只是释放写下的那几个码。
    /// </summary>
    /// <remarks>
    /// 这一条修前也绿（旧实现只在为空时写、从不清），守的是这次新加的「清」：把清的范围写宽成「任何阻断码」，这条红。
    /// </remarks>
    [Fact]
    public async Task TheEnginesOwnBlockIsNeitherMaskedNorCleared()
    {
        await using RuntimeFixture fixture = await DispatchedToPickupAsync();
        JourneyRuntimeRow row = await fixture.Context.JourneyRuntimes.SingleAsync(item => item.DemandId == FirstDemandId, Token);
        row.SetBlockReason("PREDEPARTURE_CHECK_EXPIRED", fixture.Clock.GetUtcNow());
        await fixture.Context.SaveChangesAsync(Token);
        fixture.Context.ChangeTracker.Clear();
        LeaveTheMap(fixture);
        CancellingGateway gateway = new(fixture.Clock, _ => { });

        Assert.Equal(DemandReleaseReasons.OrderCancelNotConfirmed,
            Assert.Single(await Service(fixture, gateway).RunOnceAsync(Token)).Result);
        Assert.Equal("PREDEPARTURE_CHECK_EXPIRED", (await fixture.RuntimeAsync(FirstDemandId)).BlockReasonCode);

        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { CurrentMap = fixture.Options.MapIdentity };
        Assert.Empty(await Service(fixture, gateway).RunOnceAsync(Token));
        fixture.Context.ChangeTracker.Clear();
        Assert.Equal("PREDEPARTURE_CHECK_EXPIRED", (await fixture.RuntimeAsync(FirstDemandId)).BlockReasonCode);
    }

    /// <summary>
    /// 释放与到站撞在一起：车在 RIoT 上已经到了取货站（订单 SUCCESS），引擎还没来得及记下到站，释放服务先跑。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 释放服务与引擎推进在同一个循环里串行跑（<c>JourneyRuntimeWorker</c>），所以两者不会交错写同一趟旅程；剩下的窗口
    /// 只有这一个——「车到了」这件事在 RIoT 上已经成立、在库里还没有。这里把它确定性地造出来：
    /// 释放取消之前先读订单（复审中 2），读到 SUCCESS 就不释放、也不发取消；引擎下一轮记下到站之后，
    /// 裁决改判「到站后不释放」，整个过程一次取消都没发。复审之前这里是「取消撞上已成功的订单、对账不是 Confirmed」，
    /// 对一张已完成的订单发过一次取消。
    /// </para>
    /// <para>
    /// 反过来的次序（引擎先记下到站、释放后跑）由 <see cref="OnceTheVehicleHasArrivedTheDemandIsNotReleasedAndNoOrderIsTouched"/> 覆盖。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AReleaseThatRacesTheArrivalLosesAndTheArrivalStands()
    {
        await using RuntimeFixture fixture = await DispatchedToPickupAsync();
        JourneyRuntimeRow before = await fixture.RuntimeAsync(FirstDemandId);
        fixture.Riot.SetSuccessfulArrival("TO_PICKUP", before.PickupUpperId, before.PickupStationRiotId);
        LeaveTheMap(fixture);
        CancellingGateway gateway = new(fixture.Clock, _ => { });

        IReadOnlyList<DemandReleaseOutcome> raced = await Service(fixture, gateway).RunOnceAsync(Token);

        Assert.Equal(DemandReleaseReasons.PickupOrderSucceeded, Assert.Single(raced).Result);
        await using (ControlServerDbContext reading = new(fixture.DbOptionsForTests))
        {
            Assert.Null((await reading.Set<JourneyDemandRow>().AsNoTracking().SingleAsync(Token)).RemovedAt);
            Assert.Equal(JourneyRuntimeStage.AwaitingPickupArrival,
                (await reading.JourneyRuntimes.AsNoTracking().SingleAsync(Token)).Stage);
        }

        // 引擎这一轮记下到站：阻断原因是释放服务写的拒绝码，它不妨碍到站被承认。
        fixture.Riot.Vehicle = fixture.Riot.Vehicle with
        {
            CurrentMap = fixture.Options.MapIdentity,
            CurrentStationId = before.PickupStationRiotId,
        };
        await TickAndRunAsync(fixture);
        fixture.Context.ChangeTracker.Clear();
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, (await fixture.RuntimeAsync(FirstDemandId)).Stage);

        LeaveTheMap(fixture);
        IReadOnlyList<DemandReleaseOutcome> afterArrival = await Service(fixture, gateway).RunOnceAsync(Token);
        Assert.Equal(DemandReleaseReasons.AfterArrival, Assert.Single(afterArrival).Result);
        Assert.Equal(0, gateway.Cancels);
    }

    /// <summary>
    /// 取货停靠在后面的需求：它没有 RIoT 订单（追加的停靠到车离开上一站才建单），不碰任何订单直接释放；
    /// 它的取货停靠从计划里删掉，锚需求原样留在车上。
    /// </summary>
    [Fact]
    public async Task ADemandWhosePickupIsFurtherAlongIsReleasedWithoutTouchingAnyOrder()
    {
        await using RuntimeFixture fixture = await DispatchedToPickupAsync(appendSecond: true);
        LeaveTheMap(fixture);
        CancellingGateway gateway = new(fixture.Clock, _ => { });

        IReadOnlyList<DemandReleaseOutcome> outcomes = await Service(fixture, gateway).RunOnceAsync(Token);

        // 锚需求的取货停靠是当前下一站、旅程上还有别的需求：本票限度，不释放；第二条照常释放。
        Assert.Equal(
            [(FirstDemandId, DemandReleaseReasons.CurrentStopWithOtherDemands), (SecondDemandId, "RELEASED")],
            outcomes.Select(outcome => (outcome.DemandId, outcome.Result)).OrderBy(item => item.DemandId, StringComparer.Ordinal));
        Assert.Equal(0, gateway.Cancels);
        await using ControlServerDbContext reading = new ControlServerDbContext(fixture.DbOptionsForTests);
        JourneyDemandRow second = await reading.Set<JourneyDemandRow>().AsNoTracking()
            .SingleAsync(row => row.DemandId == SecondDemandId, Token);
        Assert.NotNull(second.RemovedAt);
        Assert.Equal(JourneyStopStatuses.Removed,
            (await reading.Set<JourneyStopRow>().AsNoTracking().SingleAsync(row => row.StopId == second.PickupStopId, Token)).Status);
        Assert.Null((await reading.Set<JourneyDemandRow>().AsNoTracking()
            .SingleAsync(row => row.DemandId == FirstDemandId, Token)).RemovedAt);
        Assert.NotEqual(JourneyRuntimeStage.Completed, (await reading.JourneyRuntimes.AsNoTracking().SingleAsync(Token)).Stage);
    }

    /// <summary>
    /// RIoT 读不到车：网关不抛、返回 <c>UnknownVehicle</c> 那个形状（离线、地图为空、观测时刻为此刻）。
    /// 一次网络抖动不许把车上的需求取消订单、释放掉（审查 S1）。
    /// </summary>
    /// <remarks>
    /// 端到端守两层：释放服务把这个形状当作没读到（早退），规则里的门对离线观测不判观测派生的判据（保证在这一层）。
    /// 退掉任何一层这条都应当仍然绿，两层都退掉才红——这是有意的：保证只由门承担，早退只是省一次判定。
    /// 门只挡观测派生的判据，服务端自己的事实照常判，见 <see cref="AFaultedVehicleRiotCannotReadStillHasItsDemandReleased"/>。
    /// </remarks>
    [Fact]
    public async Task AFailedVehicleReadReleasesNothingAndCancelsNothing()
    {
        await using RuntimeFixture fixture = await DispatchedToPickupAsync();
        JourneyRuntimeRow before = await fixture.RuntimeAsync(FirstDemandId);
        fixture.Riot.Vehicle = fixture.Riot.Vehicle with
        {
            Connected = false, Enabled = false, ProcState = "UNKNOWN", CurrentMap = string.Empty,
            CurrentStationId = null, BatteryPercent = null, BatteryState = null, Speed = null,
            ObservedAt = fixture.Clock.GetUtcNow(),
        };
        CancellingGateway gateway = new(fixture.Clock, _ => fixture.Riot.CancelOrder(before.PickupUpperId));

        Assert.Empty(await Service(fixture, gateway).RunOnceAsync(Token));
        Assert.Equal(0, gateway.Cancels);
        await using ControlServerDbContext reading = new ControlServerDbContext(fixture.DbOptionsForTests);
        Assert.Null((await reading.Set<JourneyDemandRow>().AsNoTracking().SingleAsync(Token)).RemovedAt);
    }

    /// <summary>
    /// 车被判疑似故障、RIoT 也读不到它：不释放、不取消，写 <see cref="DemandReleaseReasons.FaultSupervisionInEffect"/>
    /// （调度 2026-09-21 定 A）。
    /// </summary>
    /// <remarks>
    /// 这条原来断的是「照常取消并释放」。释放一辆故障车的需求会终结它的旅程，而故障监看只由引擎每轮对这趟旅程调
    /// <c>VehicleFaultCoordinator.ObserveAsync</c> 推进——释放就等于停掉这辆车的急停监督（CI l2 红了三条故障／急停场景）。
    /// 读不到车不能成为释放理由（S1），故障本身现在也不再是：两者都让车和需求留在原地、留给故障协调器。
    /// </remarks>
    [Fact]
    public async Task AFaultedVehicleRiotCannotReadKeepsItsDemandUnderFaultSupervision()
    {
        await using RuntimeFixture fixture = await DispatchedToPickupAsync();
        JourneyRuntimeRow before = await fixture.RuntimeAsync(FirstDemandId);
        await new VehicleFaultStore(fixture.Context).RecordLevelAsync(
            before.AgvId, VehicleFaultLevel.SuspectedBlocked, "COMMS_LOST", false, fixture.Clock.GetUtcNow(), Token);
        fixture.Context.ChangeTracker.Clear();
        fixture.Riot.Vehicle = fixture.Riot.Vehicle with
        {
            Connected = false, Enabled = false, ProcState = "UNKNOWN", CurrentMap = string.Empty,
            CurrentStationId = null, BatteryPercent = null, BatteryState = null, Speed = null,
        };
        CancellingGateway gateway = new(fixture.Clock, _ => fixture.Riot.CancelOrder(before.PickupUpperId));

        Assert.Equal([(VehicleFaultBlockCriterion.SuspectedReason, DemandReleaseReasons.FaultSupervisionInEffect)],
            (await Service(fixture, gateway).RunOnceAsync(Token)).Select(outcome => (outcome.Trigger, outcome.Result)));
        Assert.Equal(0, gateway.Cancels);
        await using ControlServerDbContext reading = new ControlServerDbContext(fixture.DbOptionsForTests);
        Assert.Null((await reading.Set<JourneyDemandRow>().AsNoTracking().SingleAsync(Token)).RemovedAt);
    }

    /// <summary>
    /// 取货单在 RIoT 上已是终态（取消、失败、删除）：没有活订单可取消，不发取消，直接释放（复审中 2）。
    /// SUSPENDED 不在内，见 <see cref="ASuspendedPickupOrderIsTreatedAsLiveAndReleasedOnlyOnAConfirmedCancel"/>。
    /// </summary>
    /// <remarks>
    /// 旧实现有订单号就发 CANCEL；对账把「终态但不是 CANCELLED」判 Failed，之后每轮对已结的 Failed 直接返回、不重读，
    /// 于是永远停在 RELEASE_ORDER_CANCEL_NOT_CONFIRMED，还对一张终态订单发过一次取消。订单 FAILED 正是疑似故障的典型来路。
    /// </remarks>
    [Theory]
    [InlineData(RiotOrderState.Cancelled)]
    [InlineData(RiotOrderState.Failed)]
    [InlineData(RiotOrderState.Deleted)]
    public async Task APickupOrderAlreadyEndedOnRiotIsReleasedWithoutACancel(int orderState)
    {
        await using RuntimeFixture fixture = await DispatchedToPickupAsync();
        JourneyRuntimeRow before = await fixture.RuntimeAsync(FirstDemandId);
        fixture.Riot.SetOrderState(before.PickupUpperId, orderState, terminal: true);
        LeaveTheMap(fixture);
        CancellingGateway gateway = new(fixture.Clock, _ => { });

        Assert.Equal("RELEASED", Assert.Single(await Service(fixture, gateway).RunOnceAsync(Token)).Result);
        Assert.Equal(0, gateway.Cancels);
    }

    /// <summary>
    /// SUSPENDED（8）按活单处理：发取消并对账，确认才释放（复审，调度 2026-09-21）；有没有故障都一样。
    /// </summary>
    /// <remarks>
    /// 8 在实验室零观测，SDK 标注为「已移除」，语义不明；按保守方向当活单。当终态直接放，若它其实是一张还活着的单，
    /// RIoT 上会留下它而需求已改派给别的车（安全方向）；当活单，最坏只是需求卡在「取消没确认」（活性）。
    /// 这里 RIoT 收到取消什么都不做，所以不释放、取消恰好一次（重发归 cs#296）。
    /// </remarks>
    [Fact]
    public async Task ASuspendedPickupOrderIsTreatedAsLiveAndReleasedOnlyOnAConfirmedCancel()
    {
        await using RuntimeFixture fixture = await DispatchedToPickupAsync();
        JourneyRuntimeRow before = await fixture.RuntimeAsync(FirstDemandId);
        fixture.Riot.SetOrderState(before.PickupUpperId, RiotOrderState.Suspended, terminal: true);
        LeaveTheMap(fixture);
        CancellingGateway gateway = new(fixture.Clock, _ => { });

        Assert.Equal(DemandReleaseReasons.OrderCancelNotConfirmed,
            Assert.Single(await Service(fixture, gateway).RunOnceAsync(Token)).Result);
        Assert.Equal(1, gateway.Cancels);
        await using ControlServerDbContext reading = new ControlServerDbContext(fixture.DbOptionsForTests);
        Assert.Null((await reading.Set<JourneyDemandRow>().AsNoTracking().SingleAsync(Token)).RemovedAt);
    }

    /// <summary>
    /// 活单与没映射的未知值一律发取消并对账：取消确认后才释放（调度 2026-09-21）。
    /// </summary>
    /// <remarks>
    /// 这条修前也绿——未知值与活态本来就走取消那一支。它守的是规则本身：谁把「其余」改成「当作终态直接放」，这里红。
    /// </remarks>
    [Theory]
    [InlineData(RiotOrderState.Queueing)]
    [InlineData(RiotOrderState.Executing)]
    [InlineData(RiotOrderState.Hang)]
    [InlineData(RiotOrderState.QueuePriority)]
    [InlineData(RiotOrderState.Suspended)]
    [InlineData(42)]
    public async Task ALiveOrUnknownPickupOrderIsCancelledBeforeTheRelease(int orderState)
    {
        await using RuntimeFixture fixture = await DispatchedToPickupAsync();
        JourneyRuntimeRow before = await fixture.RuntimeAsync(FirstDemandId);
        fixture.Riot.SetOrderState(before.PickupUpperId, orderState, terminal: false);
        LeaveTheMap(fixture);
        CancellingGateway gateway = new(fixture.Clock, _ => fixture.Riot.CancelOrder(before.PickupUpperId));

        Assert.Equal("RELEASED", Assert.Single(await Service(fixture, gateway).RunOnceAsync(Token)).Result);
        Assert.Equal(1, gateway.Cancels);
    }

    /// <summary>
    /// 取货单在 RIoT 上已 SUCCESS：车已经到了取货站（引擎可能还没记下），这不是「没有活订单」，不释放、不取消（复审中 2）。
    /// </summary>
    [Fact]
    public async Task APickupOrderThatSucceededOnRiotIsNotReleased()
    {
        await using RuntimeFixture fixture = await DispatchedToPickupAsync();
        JourneyRuntimeRow before = await fixture.RuntimeAsync(FirstDemandId);
        fixture.Riot.SetOrderState(before.PickupUpperId, RiotOrderState.Success, terminal: true);
        LeaveTheMap(fixture);
        CancellingGateway gateway = new(fixture.Clock, _ => { });

        Assert.Equal(DemandReleaseReasons.PickupOrderSucceeded,
            Assert.Single(await Service(fixture, gateway).RunOnceAsync(Token)).Result);
        Assert.Equal(0, gateway.Cancels);
    }

    /// <summary>
    /// 车有未清除的故障：不管取货单在 RIoT 上读到什么，都不取消、不释放，写
    /// <see cref="DemandReleaseReasons.FaultSupervisionInEffect"/>，阻断码按 M4 规则写上（调度 2026-09-21 定 A）。
    /// </summary>
    /// <remarks>
    /// 旧实现按订单状态分叉：执行中与 SUSPENDED 发取消再释放，FAILED 不发取消直接释放，PAUSED 与 HANG 看 Hold／可 continue。
    /// 每一种释放都会终结旅程、停掉故障监看，所以分叉本身没有意义了：有故障就不动。RIoT 收到取消会照做（网关回调把单置成
    /// CANCELLED），所以旧实现在会发取消的那几种状态下真的会释放——断言取消 0 次才分得开。
    /// </remarks>
    [Theory]
    [InlineData(RiotOrderState.Executing)]
    [InlineData(RiotOrderState.Paused)]
    [InlineData(RiotOrderState.Failed)]
    [InlineData(RiotOrderState.Suspended)]
    [InlineData(RiotOrderState.Hang)]
    public async Task AFaultedVehicleKeepsItsDemandWhateverItsPickupOrderReads(int orderState)
    {
        await using RuntimeFixture fixture = await DispatchedToPickupAsync();
        JourneyRuntimeRow before = await fixture.RuntimeAsync(FirstDemandId);
        await RecordFaultAsync(fixture, before);
        fixture.Riot.SetOrderState(
            before.PickupUpperId, orderState, terminal: orderState is RiotOrderState.Failed or RiotOrderState.Suspended);
        CancellingGateway gateway = new(fixture.Clock, _ => fixture.Riot.CancelOrder(before.PickupUpperId));

        Assert.Equal([(VehicleFaultBlockCriterion.SuspectedReason, DemandReleaseReasons.FaultSupervisionInEffect)],
            (await Service(fixture, gateway).RunOnceAsync(Token)).Select(outcome => (outcome.Trigger, outcome.Result)));
        Assert.Equal(0, gateway.Cancels);
        await using ControlServerDbContext reading = new ControlServerDbContext(fixture.DbOptionsForTests);
        Assert.Null((await reading.Set<JourneyDemandRow>().AsNoTracking().SingleAsync(Token)).RemovedAt);
        Assert.Equal(DemandReleaseReasons.FaultSupervisionInEffect,
            (await reading.JourneyRuntimes.AsNoTracking().SingleAsync(Token)).BlockReasonCode);
    }

    /// <summary>
    /// 车有故障、同时也离开了本图：同样不释放。「有故障就不动」看的是故障事实，不看这次由哪条判据触发。
    /// </summary>
    [Fact]
    public async Task AFaultedVehicleThatAlsoLeftTheMapKeepsItsDemand()
    {
        await using RuntimeFixture fixture = await DispatchedToPickupAsync();
        JourneyRuntimeRow before = await fixture.RuntimeAsync(FirstDemandId);
        await RecordFaultAsync(fixture, before);
        LeaveTheMap(fixture);
        CancellingGateway gateway = new(fixture.Clock, _ => fixture.Riot.CancelOrder(before.PickupUpperId));

        Assert.Equal(DemandReleaseReasons.FaultSupervisionInEffect,
            Assert.Single(await Service(fixture, gateway).RunOnceAsync(Token)).Result);
        Assert.Equal(0, gateway.Cancels);
    }

    /// <summary>
    /// 故障车在释放判据成立时：旅程仍在、引擎每轮仍调 <c>ObserveAsync</c>、急停触发能从 Pending 走到 Confirmed
    /// （调度 2026-09-21 定 A 的护栏）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 按真 worker 的顺序跑（<c>JourneyRuntimeWorker</c>：引擎一轮，接着释放一轮）。第一轮引擎看到取货单 FAILED、车在动：
    /// 记疑似故障、升级急停；RIoT 还没上锁，触发记 Pending。释放服务随后看到「车有故障」这条判据成立——旧实现在这里释放需求、
    /// 终结旅程，引擎之后再不评估它，触发永远 Pending（CI l2 的 emergency-stop-single-trigger 就是这样红的）。
    /// 然后 RIoT 上锁、钟走一秒，第二轮引擎：只有监看还在，才会读到锁、把触发对账成 Confirmed，并把故障的
    /// <c>LastEvaluatedAt</c> 推到此刻。
    /// </para>
    /// <para>
    /// 断的是「监看没断」，不只是「码写上了」：码可以写对而旅程照样被终结。上锁放在释放之后才打开，是为了让第一轮的触发必然停在
    /// Pending——若触发当场就被确认，这条用例分不出监看有没有断。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AFaultedVehicleStaysUnderSupervisionWhileItsReleaseCriteriaHold()
    {
        await using RuntimeFixture fixture = await DispatchedToPickupAsync();
        JourneyRuntimeRow before = await fixture.RuntimeAsync(FirstDemandId);
        fixture.Riot.MovementState = "MT_RUNNING";
        fixture.Riot.FailOrder(before.PickupUpperId);
        CancellingGateway gateway = new(fixture.Clock, _ => fixture.Riot.CancelOrder(before.PickupUpperId));

        fixture.Context.ChangeTracker.Clear();
        await fixture.Engine.ExecuteOnceAsync(Token);
        fixture.Context.ChangeTracker.Clear();
        Assert.Equal(RiotOrderCommandOutcome.Pending, await EmergencyTriggerOutcomeAsync(fixture));
        Assert.Equal([DemandReleaseReasons.FaultSupervisionInEffect],
            (await Service(fixture, gateway).RunOnceAsync(Token)).Select(outcome => outcome.Result));

        fixture.EmergencyLatched = true;
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        DateTimeOffset secondRound = fixture.Clock.GetUtcNow();
        fixture.Context.ChangeTracker.Clear();
        await fixture.Engine.ExecuteOnceAsync(Token);
        fixture.Context.ChangeTracker.Clear();

        await using ControlServerDbContext reading = new ControlServerDbContext(fixture.DbOptionsForTests);
        Assert.Null((await reading.Set<JourneyDemandRow>().AsNoTracking().SingleAsync(Token)).RemovedAt);
        Assert.Equal(secondRound, (await reading.VehicleFaultStates.AsNoTracking().SingleAsync(Token)).LastEvaluatedAt);
        Assert.Equal(RiotOrderCommandOutcome.Confirmed, await EmergencyTriggerOutcomeAsync(fixture));
    }

    /// <summary>最近一次急停触发尝试的对账结果；没有触发过时为 null。</summary>
    private static async Task<RiotOrderCommandOutcome?> EmergencyTriggerOutcomeAsync(RuntimeFixture fixture)
    {
        await using ControlServerDbContext reading = new ControlServerDbContext(fixture.DbOptionsForTests);
        RiotOrderCommandAuditRow[] triggers = await reading.RiotOrderCommandAudit.AsNoTracking()
            .Where(row => row.CommandType == RiotCommandTypeNames.TriggerEmergency)
            .ToArrayAsync(Token);
        return triggers.OrderBy(row => row.AttemptNumber).LastOrDefault()?.Outcome;
    }

    /// <summary>
    /// <see cref="DemandReleaseReasons"/> 里除 <see cref="DemandReleaseReasons.Released"/> 之外的每一个码都是释放拒绝码。
    /// </summary>
    /// <remarks>
    /// 新加一个拒绝码而忘了登记进 <see cref="DemandReleaseReasons.IsRefusalCode"/>，写码那一步照样成功（阻断码为空时总能写），
    /// 坏在后面：车重新合格时这个码不会被清掉，旅程一直挂着一个过时的阻断原因，而没有任何用例会红。所以按反射逐个核，
    /// 不按名单核——名单会和 IsRefusalCode 一起漏。
    /// </remarks>
    [Fact]
    public void EveryReleaseReasonExceptReleasedIsARefusalCode()
    {
        IReadOnlyList<(string Name, bool Refusal)> codes = typeof(DemandReleaseReasons)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (field.Name, DemandReleaseReasons.IsRefusalCode((string)field.GetRawConstantValue()!)))
            .ToList();

        // 保底：反射真的看到了这个类（至少看到了两个一定存在的码），而不是对一个空集合断言「全都对」。
        Assert.Contains(codes, code => code.Name == nameof(DemandReleaseReasons.Released));
        Assert.Contains(codes, code => code.Name == nameof(DemandReleaseReasons.FaultSupervisionInEffect));
        Assert.Equal(
            codes.Select(code => (code.Name, code.Name != nameof(DemandReleaseReasons.Released))),
            codes);
    }

    /// <summary>
    /// 已发过的取消对账没有结论（这次读不到），而随后读订单时订单已是终态：终态分支里再对账一次，审计行不会停在 Unknown
    /// （第三轮复审低 1）。
    /// </summary>
    /// <remarks>
    /// 对账与读订单是两次独立读取，之间订单可能变了状态；<c>ReconcileAsync</c> 只有释放服务调用，这一行过了这一轮就再没人对账。
    /// 已取消应记 Confirmed；SUCCESS 是终态但不是取消想要的状态，应记 Failed——两个分支都会走捷径，两个都要守。
    /// </remarks>
    [Theory]
    [InlineData(RiotOrderState.Cancelled, RiotOrderCommandOutcome.Confirmed, "RELEASED")]
    [InlineData(RiotOrderState.Success, RiotOrderCommandOutcome.Failed, DemandReleaseReasons.PickupOrderSucceeded)]
    public async Task ACancelWhoseReconcileReadFailedIsReconciledAgainWhenTheOrderReadsTerminal(
        int orderState, RiotOrderCommandOutcome expectedAudit, string expectedResult)
    {
        await using RuntimeFixture fixture = await DispatchedToPickupAsync();
        JourneyRuntimeRow before = await fixture.RuntimeAsync(FirstDemandId);
        LeaveTheMap(fixture);
        CancellingGateway gateway = new(fixture.Clock, _ => { });
        Assert.Equal(DemandReleaseReasons.OrderCancelNotConfirmed,
            Assert.Single(await Service(fixture, gateway).RunOnceAsync(Token)).Result);

        fixture.Riot.SetOrderState(before.PickupUpperId, orderState, terminal: true);
        Assert.Equal(expectedResult,
            Assert.Single(await Service(fixture, gateway, new UnreadableOnce(fixture.Riot)).RunOnceAsync(Token)).Result);

        Assert.Equal(1, gateway.Cancels);
        await using ControlServerDbContext reading = new ControlServerDbContext(fixture.DbOptionsForTests);
        Assert.Equal(expectedAudit, (await reading.RiotOrderCommandAudit.AsNoTracking()
            .SingleAsync(row => row.TargetUpperId == before.PickupUpperId, Token)).Outcome);
    }

    /// <summary>记一次疑似故障（本代次没有任何 Hold 尝试）。</summary>
    private static async Task RecordFaultAsync(RuntimeFixture fixture, JourneyRuntimeRow journey)
    {
        await new VehicleFaultStore(fixture.Context).RecordLevelAsync(
            journey.AgvId, VehicleFaultLevel.SuspectedBlocked, "VEHICLE_ORDER_FAILED", false, fixture.Clock.GetUtcNow(), Token);
        fixture.Context.ChangeTracker.Clear();
    }

    /// <summary>第一次按 upperId 读订单返回「读不到」，之后照常转给 <paramref name="inner"/>：造出两次读取之间的状态变化。</summary>
    private sealed class UnreadableOnce(IRiotMovementGateway inner) : IRiotMovementGateway
    {
        private bool _failed;

        public Task<RiotOrderObservation> ReconcileByUpperIdAsync(string upperId, CancellationToken cancellationToken)
        {
            if (_failed)
            {
                return inner.ReconcileByUpperIdAsync(upperId, cancellationToken);
            }

            _failed = true;
            return Task.FromResult(new RiotOrderObservation(upperId, RiotOrderObservationKind.Unknown, null));
        }

        public Task<RiotOrderObservation> CreateAsync(OrderIntent intent, CancellationToken cancellationToken) =>
            inner.CreateAsync(intent, cancellationToken);
    }

    /// <summary>
    /// 取货单的创建结果未知（创建请求已经发出、回应丢了）：意图上还没有 RIoT 订单号，但 RIoT 上可能已经有一张活的订单。
    /// 这不是「没有订单」，不释放、不发取消（REQ-0328：结果未知不释放；审查 S2）。
    /// </summary>
    /// <remarks>
    /// 引擎建单与释放服务在同一个循环里前后脚跑，所以「建单结果还没对账」这个窗口在生产上必然出现。旧实现把
    /// 「意图上没有订单号」当作「没有订单」，于是释放、关旅程，RIoT 上那张活订单再也没人取消。
    /// </remarks>
    [Fact]
    public async Task AnUnknownCreateResultIsNotNoOrderAndNothingIsReleased()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(FirstDemandId, FirstSublot, Now.AddMinutes(-10)));
        fixture.BoxCounts.Set(FirstSublot, 7);
        fixture.Riot.LoseNextCreateResponse = true;
        await TickAndRunAsync(fixture);
        fixture.Context.ChangeTracker.Clear();
        JourneyRuntimeRow before = await fixture.RuntimeAsync(FirstDemandId);
        // 前提：意图上没有订单号，而 RIoT 上确实有一张活的——少了后一条，「不释放」可能只是因为真的没有订单。
        Assert.Null((await fixture.Context.OrderIntents.AsNoTracking()
            .SingleAsync(row => row.UpperId == before.PickupUpperId, Token)).OrderId);
        Assert.Equal(RiotOrderObservationKind.Active,
            (await fixture.Riot.ReconcileByUpperIdAsync(before.PickupUpperId, Token)).Kind);

        LeaveTheMap(fixture);
        CancellingGateway gateway = new(fixture.Clock, _ => fixture.Riot.CancelOrder(before.PickupUpperId));
        IReadOnlyList<DemandReleaseOutcome> outcomes = await Service(fixture, gateway).RunOnceAsync(Token);

        Assert.Equal(DemandReleaseReasons.OrderStateUnknown, Assert.Single(outcomes).Result);
        Assert.Equal(0, gateway.Cancels);
        Assert.Equal(RiotOrderObservationKind.Active,
            (await fixture.Riot.ReconcileByUpperIdAsync(before.PickupUpperId, Token)).Kind);
        await using ControlServerDbContext reading = new ControlServerDbContext(fixture.DbOptionsForTests);
        Assert.Null((await reading.Set<JourneyDemandRow>().AsNoTracking().SingleAsync(Token)).RemovedAt);
        Assert.NotEqual(JourneyRuntimeStage.Completed, (await reading.JourneyRuntimes.AsNoTracking().SingleAsync(Token)).Stage);
    }

    /// <summary>
    /// 上一条的后半：引擎下一轮把意图对账成 CONFIRMED、拿到订单号之后，释放照常走「取消并对账 → 释放」，
    /// 不会永远停在「订单状态未知」。
    /// </summary>
    [Fact]
    public async Task OnceTheCreateResultIsReconciledTheOrderIsCancelledAndTheDemandReleased()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(FirstDemandId, FirstSublot, Now.AddMinutes(-10)));
        fixture.BoxCounts.Set(FirstSublot, 7);
        fixture.Riot.LoseNextCreateResponse = true;
        await TickAndRunAsync(fixture);
        fixture.Context.ChangeTracker.Clear();
        JourneyRuntimeRow before = await fixture.RuntimeAsync(FirstDemandId);
        LeaveTheMap(fixture);
        CancellingGateway gateway = new(fixture.Clock, _ => fixture.Riot.CancelOrder(before.PickupUpperId));
        Assert.Equal(DemandReleaseReasons.OrderStateUnknown,
            Assert.Single(await Service(fixture, gateway).RunOnceAsync(Token)).Result);

        // 引擎对账：意图拿到订单号。车仍在别的图上，释放服务下一轮照常取消、对账、释放。
        await TickAndRunAsync(fixture);
        fixture.Context.ChangeTracker.Clear();
        Assert.NotNull((await fixture.Context.OrderIntents.AsNoTracking()
            .SingleAsync(row => row.UpperId == before.PickupUpperId, Token)).OrderId);

        Assert.Equal("RELEASED", Assert.Single(await Service(fixture, gateway).RunOnceAsync(Token)).Result);
        Assert.Equal(1, gateway.Cancels);
    }

    /// <summary>
    /// 锚需求释放之后，下一轮把它作为一趟新旅程的锚再受理：派往取货站的计划真的发出，到站后离站期限照常结束这一站
    /// （看板例外第 12 条的三处，场景 2 的 L1 版）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 三处在同一条路上依次挡着，所以修前这条用例红在第一处，修一处往下挪一处：
    /// 孤儿检查把待改派需求当孤儿、整轮抛出（引擎 <c>OrphanCandidates</c>）；新旅程的派车计划 messageId 与第一趟相同，
    /// 发件箱里已有那一行，于是一次都不发（<c>PickupDispatchPlanMessageId</c>）；离站期限按 <c>DemandId</c> 查旅程行，
    /// 同一条需求此时有两行，<c>SingleOrDefault</c> 抛出（<c>TryEndStopAtStationDeadlineAsync</c>）。
    /// </para>
    /// <para>
    /// 第一趟旅程此时已是 Completed：判据里「新旅程」一律按 <c>JourneyId</c> 取，不按 <c>DemandId</c>——
    /// 后者正是被测的那种写法。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AReleasedAnchorIsDispatchedAgainAsANewJourneyAndItsStationDeadlineStillEndsTheStop()
    {
        await using RuntimeFixture fixture = await DispatchedToPickupAsync();
        JourneyRuntimeRow first = await fixture.RuntimeAsync(FirstDemandId);
        // 再推一轮，让第一趟把它派往取货站的计划发出去：发件箱里有了那一行，改派那一趟若沿用同一个 messageId
        // 就会被它挡住。少了这一轮，第一趟一张计划都没发过，那一处按构造走不到。
        await TickAndRunAsync(fixture);
        Assert.Single(await PickupPlansAsync(fixture));
        // 零变化钉子：首次受理的那张派车计划，messageId 仍按需求本身派生，与改派出现之前逐字节相同。
        Assert.True(await fixture.Context.ProtocolOutbox.AsNoTracking().AnyAsync(
            row => row.MessageId == JourneyPlanBuilder.StableGuid(FirstDemandId, "pickup-dispatch-plan"), Token));
        LeaveTheMap(fixture);
        CancellingGateway gateway = new(fixture.Clock, orderId => fixture.Riot.CancelOrder(first.PickupUpperId));
        Assert.Equal("RELEASED", Assert.Single(await Service(fixture, gateway).RunOnceAsync(Token)).Result);

        // 车回到本图、回到出发时那一站：它又是一辆空闲、合格的车。
        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { CurrentMap = fixture.Options.MapIdentity };
        await TickAndRunAsync(fixture);
        await TickAndRunAsync(fixture);
        fixture.Context.ChangeTracker.Clear();

        JourneyDemandRow membership = await fixture.Context.Set<JourneyDemandRow>().AsNoTracking()
            .SingleAsync(row => row.DemandId == FirstDemandId && row.RemovedAt == null, Token);
        Assert.NotEqual(first.JourneyId, membership.JourneyId);
        JourneyRuntimeRow second = await fixture.Context.JourneyRuntimes.AsNoTracking()
            .SingleAsync(row => row.JourneyId == membership.JourneyId, Token);
        Assert.Equal(FirstDemandId, second.DemandId);

        // 派往取货站的那张计划发给了新旅程：它投影的是新旅程取货那一段腿。
        JourneyStopRow pickup = await fixture.Context.Set<JourneyStopRow>().AsNoTracking()
            .Where(row => row.JourneyId == second.JourneyId)
            .OrderBy(row => row.Sequence)
            .FirstAsync(Token);
        string[] plans = await PickupPlansAsync(fixture);
        Assert.Equal(2, plans.Length);
        Assert.Contains(plans, payload => payload.Contains(pickup.MovementLegId, StringComparison.Ordinal));

        // 到站，录入迟迟不来，期限到：这一站结束、这条需求终结，引擎不抛。
        fixture.Riot.SetSuccessfulArrival("TO_PICKUP", pickup.UpperId, pickup.StationRiotId);
        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { CurrentStationId = pickup.StationRiotId };
        await TickAndRunAsync(fixture);
        fixture.Options.StationDepartureWaitTimeout = TimeSpan.FromSeconds(10);
        await fixture.ProveSlotDoorsClosedAsync();
        fixture.Clock.Advance(TimeSpan.FromSeconds(10));
        await TickAndRunAsync(fixture);
        fixture.Context.ChangeTracker.Clear();

        Assert.Equal(JourneyDemandStatuses.Terminated, (await fixture.Context.Set<JourneyDemandRow>().AsNoTracking()
            .SingleAsync(row => row.JourneyId == second.JourneyId && row.DemandId == FirstDemandId, Token)).Status);
        // 第一趟一个字没被第二趟的期限动过。
        Assert.Equal((JourneyRuntimeStage.Completed, DemandReleaseReasons.Released),
            await fixture.Context.JourneyRuntimes.AsNoTracking()
                .Where(row => row.JourneyId == first.JourneyId)
                .Select(row => ValueTuple.Create(row.Stage, row.BlockReasonCode))
                .SingleAsync(Token));
    }

    private static Task<string[]> PickupPlansAsync(RuntimeFixture fixture) =>
        fixture.Context.ProtocolOutbox.AsNoTracking()
            .Where(row => row.MessageType == "UpcomingStopPlanSnapshot")
            .Select(row => row.PayloadJson)
            .ToArrayAsync(Token);

    /// <summary>
    /// 改派出来的锚需求建单没确认：新旅程记下阻断码，派车轮次不抛（审查 M2）。
    /// </summary>
    /// <remarks>
    /// 轮次按 <c>DemandId</c> 取刚建的旅程行（<c>SingleAsync</c>），而改派之后同一条需求有两行——旧的 Completed 与新的。
    /// 旧实现在这里抛，阻断码不写，整轮中止。
    /// </remarks>
    [Fact]
    public async Task ARedispatchWhoseCreateIsNotConfirmedRecordsItsBlockOnTheNewJourney()
    {
        await using RuntimeFixture fixture = await DispatchedToPickupAsync();
        JourneyRuntimeRow first = await ReleaseTheAnchorAsync(fixture);

        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { CurrentMap = fixture.Options.MapIdentity };
        fixture.Riot.LoseNextCreateResponse = true;
        await TickAndRunAsync(fixture);
        fixture.Context.ChangeTracker.Clear();

        JourneyRuntimeRow second = await NewJourneyAsync(fixture, first);
        Assert.NotNull(second.BlockReasonCode);
        Assert.Equal(DemandReleaseReasons.Released,
            (await fixture.Context.JourneyRuntimes.AsNoTracking().SingleAsync(row => row.JourneyId == first.JourneyId, Token))
            .BlockReasonCode);
    }

    /// <summary>
    /// 改派出来的锚需求撞上这辆车已有的占用：新旅程被 Block（VEHICLE_OCCUPANCY_CONFLICT），不在没有占用认领的情况下往下走（审查 M2）。
    /// </summary>
    /// <remarks>
    /// 占用冲突用「第一趟那张单又占着这辆车」造：释放时它的占用已放掉，这里把它重新挂上，唯一索引就会拒绝新单的认领。
    /// </remarks>
    [Fact]
    public async Task ARedispatchThatFindsTheVehicleOccupiedBlocksTheNewJourney()
    {
        await using RuntimeFixture fixture = await DispatchedToPickupAsync();
        JourneyRuntimeRow first = await ReleaseTheAnchorAsync(fixture);
        OrderIntentRow old = await fixture.Context.OrderIntents
            .SingleAsync(row => row.UpperId == first.PickupUpperId, Token);
        Assert.NotNull(old.VehicleOccupancyReleasedAt);
        old.VehicleOccupancyReleasedAt = null;
        await fixture.Context.SaveChangesAsync(Token);
        fixture.Context.ChangeTracker.Clear();

        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { CurrentMap = fixture.Options.MapIdentity };
        await TickAndRunAsync(fixture);
        fixture.Context.ChangeTracker.Clear();

        JourneyRuntimeRow second = await NewJourneyAsync(fixture, first);
        Assert.Equal((JourneyRuntimeStage.Blocked, "VEHICLE_OCCUPANCY_CONFLICT"), (second.Stage, second.BlockReasonCode));
    }

    /// <summary>
    /// 被释放、等着改派的需求，以及改派之后又上了车的需求，都仍在「在途」清单上（审查 M2 的同形状，在 Store 里）。
    /// </summary>
    /// <remarks>
    /// 分区归属与任务类型站点两张冻结的「在途」清单都用「它的旅程已 Completed」排除已结束的需求，按 <c>DemandId</c> 查旅程行。
    /// 释放把第一趟关成 Completed，于是一条仍然活着的需求被当作已结束——新版本的导入预览与站点启用都会漏掉它。
    /// </remarks>
    [Fact]
    public async Task AReleasedAndThenARedispatchedDemandStaysInFlight()
    {
        await using RuntimeFixture fixture = await DispatchedToPickupAsync();
        Assert.Contains(FirstDemandId, await InFlightAsync(fixture));

        JourneyRuntimeRow first = await ReleaseTheAnchorAsync(fixture);
        Assert.Contains(FirstDemandId, await InFlightAsync(fixture));

        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { CurrentMap = fixture.Options.MapIdentity };
        await TickAndRunAsync(fixture);
        fixture.Context.ChangeTracker.Clear();
        _ = await NewJourneyAsync(fixture, first);
        Assert.Contains(FirstDemandId, await InFlightAsync(fixture));
    }

    /// <summary>车离开本图，锚需求（这趟旅程唯一的一条）确认取消后被释放。返回第一趟旅程行。</summary>
    private static async Task<JourneyRuntimeRow> ReleaseTheAnchorAsync(RuntimeFixture fixture)
    {
        JourneyRuntimeRow first = await fixture.RuntimeAsync(FirstDemandId);
        LeaveTheMap(fixture);
        CancellingGateway gateway = new(fixture.Clock, _ => fixture.Riot.CancelOrder(first.PickupUpperId));
        Assert.Equal("RELEASED", Assert.Single(await Service(fixture, gateway).RunOnceAsync(Token)).Result);
        fixture.Context.ChangeTracker.Clear();
        return first;
    }

    /// <summary>这条需求的第二趟旅程行：按 <c>JourneyId</c> 取，不按 <c>DemandId</c>——后者正是被测的那种写法。</summary>
    private static Task<JourneyRuntimeRow> NewJourneyAsync(RuntimeFixture fixture, JourneyRuntimeRow first) =>
        fixture.Context.JourneyRuntimes.AsNoTracking()
            .SingleAsync(row => row.DemandId == FirstDemandId && row.JourneyId != first.JourneyId, Token);

    /// <summary>两张冻结清单都认为在途的需求：两者的交集，好让任何一张漏掉它都红。</summary>
    private static async Task<string[]> InFlightAsync(RuntimeFixture fixture)
    {
        await using ControlServerDbContext reading = new ControlServerDbContext(fixture.DbOptionsForTests);
        string[] area = [.. (await new DemandAreaAssignmentFreezeStore(reading).ListInFlightAsync(Token))
            .Select(freeze => freeze.DemandId)];
        string[] stations = [.. (await TaskTypeStationActivationHarness.StackOver(reading).Activations
                .ListInFlightDemandsAsync(fixture.Options.MapId, Token))
            .Select(demand => demand.DemandId)];
        return [.. area.Intersect(stations, StringComparer.Ordinal)];
    }

    /// <summary>受理第一条并派往取货站；需要时再把第二条追加进来。</summary>
    /// <summary>
    /// 释放之后的等待年龄仍从原来的本地建单时刻（MesIngest 的 <c>CreatedAt</c>）算（批次7-09，control-server#214）：
    /// 经真实释放路径——车离开本图、取货单取消并对账确认、需求退回积压——再整轮跑一次，这条需求照原建单时刻越过阈值、升级告警。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 需求在 <c>Now - 10min</c> 建单、受理那几轮里被服务端第一次看到，阈值 15 分钟，钟再拨 5 分钟：按建单时刻它已等满
    /// 15 分钟；按首次看到还不到，不会告警。两个年龄在用例里按实际时刻算出来再断言，所以这条只在年龄真的从 <c>CreatedAt</c> 算时才绿。
    /// </para>
    /// <para>
    /// 触发用「车离开本图」而不是故障：车有故障时释放服务不释放（cs#215 的最终形态）。释放之后把车放回本图，再让这条需求被一道
    /// 瞬时门禁挡住（查不到箱数）：否则它这一轮就被重新受理、不再等，也就谈不上告警；车留在图外的话，本图不符对名册里唯一那辆车
    /// 是结构性的，结构性阻断的需求按调度 2026-09-21 的决定不告警，同样看不出年龄。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AReleasedDemandKeepsItsAgeFromItsLocalCreationAndIsEscalatedOnIt()
    {
        await using RuntimeFixture fixture = await DispatchedToPickupAsync();
        JourneyRuntimeRow before = await fixture.RuntimeAsync(FirstDemandId);
        DateTimeOffset firstSeen = (await fixture.BacklogAsync(FirstDemandId)).FirstSeenAt;
        long version = (await fixture.ImportStarvationThresholdsAsync((fixture.Options.DispatchZone, 900))).Version;
        DateTimeOffset startedAt = fixture.Clock.GetUtcNow();
        fixture.Clock.Advance(TimeSpan.FromMinutes(5));
        RiotVehicleObservation onTheMap = fixture.Riot.Vehicle;
        LeaveTheMap(fixture);
        CancellingGateway gateway = new(fixture.Clock, orderId => fixture.Riot.CancelOrder(before.PickupUpperId));

        IReadOnlyList<DemandReleaseOutcome> outcomes = await Service(fixture, gateway).RunOnceAsync(Token);

        Assert.Equal([(FirstDemandId, "RELEASED")], outcomes.Select(outcome => (outcome.DemandId, outcome.Result)));
        fixture.Riot.Vehicle = onTheMap;
        fixture.BoxCounts.Remove(FirstSublot);
        await fixture.RecreateEngineAsync();
        await fixture.Engine.ExecuteOnceAsync(Token);

        Assert.Equal(startedAt.AddMinutes(5), fixture.Clock.GetUtcNow());
        Assert.Equal(startedAt, firstSeen);
        await using ControlServerDbContext reading = new ControlServerDbContext(fixture.DbOptionsForTests);
        JourneyBacklogRow backlog = await reading.JourneyBacklog.AsNoTracking()
            .SingleAsync(row => row.DemandId == FirstDemandId, Token);
        Assert.Null(backlog.AcceptedAt);
        Assert.Equal(firstSeen, backlog.FirstSeenAt);
        Assert.Equal(fixture.Clock.GetUtcNow(), backlog.StarvationEscalatedAt);
        Assert.Equal(version, backlog.StarvationEscalationParameterVersion);
        // 驱动受理那几轮自己也拨过钟，所以两个年龄按实际时刻算出来再比：按建单时刻已达阈值，按首次看到还差得远。
        long sinceCreated = (long)(fixture.Clock.GetUtcNow() - Now.AddMinutes(-10)).TotalSeconds;
        Assert.True(sinceCreated >= 900, $"age since local creation {sinceCreated} s should have reached the threshold");
        Assert.True(fixture.Clock.GetUtcNow() - firstSeen < TimeSpan.FromSeconds(900), "age since first seen must stay below it");
        Assert.Contains(
            fixture.StarvationLog.Entries,
            entry => entry.Message.Contains(FirstDemandId, StringComparison.Ordinal) &&
                entry.Message.Contains($"has waited {sinceCreated} s", StringComparison.Ordinal));
    }

    private static async Task<RuntimeFixture> DispatchedToPickupAsync(bool appendSecond = false)
    {
        RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(
            fixture.Demand(FirstDemandId, FirstSublot, Now.AddMinutes(-10)),
            fixture.Demand(SecondDemandId, SecondSublot, Now.AddMinutes(-9), area: "N1-2"));
        fixture.BoxCounts.Set(FirstSublot, 7);
        fixture.BoxCounts.Set(SecondSublot, 7);
        await TickAndRunAsync(fixture);
        if (appendSecond)
        {
            await Batch7ThreeStopJourneyTests.AppendSecondDemandAsync(fixture);
        }

        fixture.Context.ChangeTracker.Clear();
        return fixture;
    }

    private static void LeaveTheMap(RuntimeFixture fixture) =>
        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { CurrentMap = "MAP-ELSEWHERE" };

    private static DemandReleaseService Service(
        RuntimeFixture fixture, CancellingGateway gateway, IRiotMovementGateway? commandReads = null)
    {
        Microsoft.Extensions.Options.IOptions<JourneyRuntimeOptions> options =
            Microsoft.Extensions.Options.Options.Create(fixture.Options);
        ControlServerDbContext context = new ControlServerDbContext(fixture.DbOptionsForTests);
        RiotOrderCommandAuditStore audit = new(context);
        return new DemandReleaseService(
            context,
            new VehicleFaultStore(context),
            new VehicleDispatchPolicyAccess(new VehicleDispatchPolicyStore(context), options, fixture.Clock),
            fixture.Riot,
            new RiotOrderCommandService(gateway, audit, commandReads ?? fixture.Riot, fixture.Clock),
            audit,
            new PlanRevisionRoutingSource(
                new RouteGraphAccess(
                    new RouteGraphSnapshotStore(context),
                    Microsoft.Extensions.Options.Options.Create(new RouteGraphOptions()),
                    fixture.Clock),
                new NoZoneParameters()),
            options,
            fixture.Clock,
            NullLogger<DemandReleaseService>.Instance);
    }

    /// <summary>RIoT 的订单命令面替身：数取消次数，收到取消时做用例交代的事（把订单置成 CANCELLED，或者什么都不做）。</summary>
    private sealed class CancellingGateway(TimeProvider clock, Action<string> onCancel) : IRiotOrderCommandGateway
    {
        public int Cancels { get; private set; }

        public Task<RiotCommandCallResult> IssueOrderCommandAsync(
            RiotOrderCommandKind kind, string orderId, string? reason, CancellationToken cancellationToken)
        {
            _ = reason;
            _ = cancellationToken;
            if (kind == RiotOrderCommandKind.Cancel)
            {
                Cancels++;
                onCancel(orderId);
            }

            return Task.FromResult(new RiotCommandCallResult(
                RiotCommandCallDisposition.Accepted,
                new RiotOrderCallReceipt(RiotCommandTypeNames.For(kind), "SdkAccepted", clock.GetUtcNow())));
        }

        public Task<RiotCommandCallResult> IssueEmergencyCommandAsync(
            RiotEmergencyCommandKind kind, string deviceKey, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The release service never issues an emergency command.");
    }

    private sealed class NoZoneParameters : IDispatchZoneParameterStore
    {
        public Task<DispatchZoneParameterTableVersion> WriteVersionAsync(
            IReadOnlyList<DispatchZoneParameters> zones, DateTimeOffset loadedAt, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<DispatchZoneParameterTableVersion?> ReadCurrentAsync(CancellationToken cancellationToken) =>
            Task.FromResult<DispatchZoneParameterTableVersion?>(null);

        public Task<DispatchZoneParameterTableVersion?> ReadVersionAsync(long version, CancellationToken cancellationToken) =>
            Task.FromResult<DispatchZoneParameterTableVersion?>(null);
    }
}
