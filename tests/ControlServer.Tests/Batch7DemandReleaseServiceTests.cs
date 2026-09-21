using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Commands;
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
    /// 释放与到站撞在一起：车在 RIoT 上已经到了取货站（订单 SUCCESS），引擎还没来得及记下到站，释放服务先跑。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 释放服务与引擎推进在同一个循环里串行跑（<c>JourneyRuntimeWorker</c>），所以两者不会交错写同一趟旅程；剩下的窗口
    /// 只有这一个——「车到了」这件事在 RIoT 上已经成立、在库里还没有。这里把它确定性地造出来：
    /// 取消撞上一张已经成功结束的订单，对账得到的不是 Confirmed，于是不释放；引擎下一轮记下到站之后，
    /// 裁决改判「到站后不释放」，而且不会再发第二次取消。
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

        Assert.Equal(DemandReleaseReasons.OrderCancelNotConfirmed, Assert.Single(raced).Result);
        await using (ControlServerDbContext reading = new(fixture.DbOptionsForTests))
        {
            Assert.Null((await reading.Set<JourneyDemandRow>().AsNoTracking().SingleAsync(Token)).RemovedAt);
            Assert.Equal(JourneyRuntimeStage.AwaitingPickupArrival,
                (await reading.JourneyRuntimes.AsNoTracking().SingleAsync(Token)).Stage);
        }

        // 引擎这一轮记下到站：阻断原因是释放服务写的「取消没确认」，它不妨碍到站被承认。
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
        Assert.Equal(1, gateway.Cancels);
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
    /// 端到端守两层：释放服务把这个形状当作没读到（早退），规则入口的门对离线观测一律不判不合格（保证在这一层）。
    /// 退掉任何一层这条都应当仍然绿，两层都退掉才红——这是有意的：保证只由门承担，早退只是省一次判定。
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

    /// <summary>受理第一条并派往取货站；需要时再把第二条追加进来。</summary>
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

    private static DemandReleaseService Service(RuntimeFixture fixture, CancellingGateway gateway)
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
            new RiotOrderCommandService(gateway, audit, fixture.Riot, fixture.Clock),
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
