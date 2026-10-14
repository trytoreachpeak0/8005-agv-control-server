using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using static ControlServer.Tests.Batch7CargoHoldingTests;
using static ControlServer.Tests.Batch7StopDrivenAdvanceDriver;
using static ControlServer.Tests.ClosureSnapshotAssertions;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;

namespace ControlServer.Tests;

/// <summary>
/// 持货超时截断未开始装的停靠，截掉的放回待派队列（control-server#290；ADR-cross-0057 实施决定（三）（四），用户 2026-10-14 定
/// 「截掉，放回待派队列」）。
/// </summary>
/// <remarks>
/// <para>
/// <b>规则</b>：持货期限一到，装货阶段以 <c>CLOSED</c>／<c>CARGO_HOLDING_TIMEOUT</c> 结束；这趟旅程里还没开始装的需求——不论在车此刻所在
/// 的停靠还是后面的取货停靠——从这趟旅程上释放、退回积压等改派（REQ-0328 那条路），它们因此留空的停靠由 7-10 的计划修订删掉。
/// 需求不终结、不写业务键抑制，下一轮可以派给别的车或新旅程，但不追加回原来这一趟。已经在装的那一批等它安全闭环
/// （「到期不打断正在进行的仓位操作」），已经装上的照常送到。
/// </para>
/// <para>
/// 在这之前（批次7-07 起）到期只关装货阶段：车上显示「持货超时」，车却照样开去另一个取货停靠装货，持货远超部署上限的 5 分钟。
/// ADR 原文要的是以 <c>CANCELLED_BY_STOP_COMPLETE</c> 终结，那会按业务键永久抑制、v2 从此不再派这条 MES 需求，等于静默丢单。
/// </para>
/// <para>
/// <b>判据断到需求、归属、停靠三张表与业务键抑制</b>，以及派车读口 <c>DemandJourneyLookup.ReleasedForRedispatch</c>。车去不去那一站，
/// 断在离站之后的阶段上（去卸货站还是去取货站）。
/// </para>
/// </remarks>
public sealed class CargoHoldingTruncationTests
{
    private const int SecondPickupStationRiotId = 13;
    private const string SecondPickupArea = "N1-2";
    private const string FourthDemandId = "10000000-0000-4000-8000-000000000004";
    private const string FourthSublot = "SUBLOT-004";

    /// <summary>
    /// 车还停在第一个取货站（离站等待没完），持货期限先到：另一个取货停靠上那条还没开始装的需求被释放回积压、它的停靠被删；车离站时直接去
    /// 卸货站，第一站装上的那条不受影响。这趟旅程还在跑时它不会被追加回来；这趟一结束，下一轮派车就把它作为新旅程重新受理。
    /// </summary>
    /// <remarks>
    /// 持货 2 分钟、离站等待 5 分钟，好让期限落在车还停着的时候——这正是 S4 里 agv02 的形状：第二条同站追加在第一笔落定之后才进来。
    /// 夹具只有一辆车，所以「派给别的车或新旅程」在这里取的是「新旅程」那一半。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task AtTheHoldingDeadlineADemandNotYetStartedAtAnotherStopGoesBackToTheBacklogAndIsDispatchedAgain()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await HoldingFixtureAsync();
        fixture.Options.CargoHoldingTimeout = TimeSpan.FromMinutes(2);
        fixture.Options.StationDepartureWaitTimeout = TimeSpan.FromMinutes(5);
        await LoadTheFirstDemandAsync(fixture);
        await AppendSecondAsync(fixture);
        await TickAndRunAsync(fixture);
        string journeyId = (await JourneyOfAsync(fixture, FirstDemandId)).JourneyId;
        Assert.Equal(JourneyRuntimeStage.AwaitingStationDeparture, (await JourneyOfAsync(fixture, FirstDemandId)).Stage);
        Assert.Equal(JourneyDemandStatuses.PendingLoad, (await MembershipOnAsync(fixture, journeyId, SecondDemandId)).Status);

        fixture.Clock.Advance(TimeSpan.FromMinutes(2));
        await TickAndRunAsync(fixture);

        JourneyRuntimeRow runtime = await JourneyOfAsync(fixture, FirstDemandId);
        Assert.Equal(LoadingPhaseStates.Closed, runtime.LoadingPhaseState);
        Assert.Equal(LoadingClosedReasons.CargoHoldingTimeout, runtime.LoadingClosedReason);
        await AssertReleasedForRedispatchAsync(fixture, journeyId, SecondDemandId);
        Assert.Equal(JourneyStopStatuses.Removed, (await PickupStopAtAsync(fixture, journeyId, SecondPickupStationRiotId)).Status);
        Assert.Equal(JourneyDemandStatuses.Loaded, (await MembershipOnAsync(fixture, journeyId, FirstDemandId)).Status);

        // MES 那边这条需求一直挂着：派车轮次每一轮都看得到它。
        fixture.Catalog.Set(
            fixture.Demand(FirstDemandId, FirstSublot, Now.AddMinutes(-10)),
            fixture.Demand(SecondDemandId, SecondSublot, Now.AddMinutes(-9), area: SecondPickupArea));
        for (int round = 0; round < 3; round++)
        {
            await TickAndRunAsync(fixture);
        }
        Assert.Empty(await MembershipsInForceAsync(fixture, SecondDemandId));

        // 离站等待到期，车离站：去的是卸货站，不是那个被删掉的取货停靠。
        fixture.Clock.Advance(TimeSpan.FromMinutes(3));
        await TickAndRunAsync(fixture);
        Assert.True(await DepartureCheckSentAsync(fixture), "The vehicle was never sent on after the truncation.");
        JourneyRuntimeRow departed = await AnswerDepartureSafetyAsync(fixture, FirstDemandId, FirstSafetyResultId);
        Assert.Equal(JourneyRuntimeStage.AwaitingGateArrival, departed.Stage);
        Assert.Empty(await MembershipsInForceAsync(fixture, SecondDemandId));

        Assert.Equal(JourneyRuntimeStage.Completed, (await ArriveAtGateAndUnloadAsync(fixture, FirstDemandId)).Stage);
        for (int round = 0; round < 5 && (await MembershipsInForceAsync(fixture, SecondDemandId)).Length == 0; round++)
        {
            await TickAndRunAsync(fixture);
        }
        JourneyDemandRow again = Assert.Single(await MembershipsInForceAsync(fixture, SecondDemandId));
        Assert.NotEqual(journeyId, again.JourneyId);
        Assert.Equal(
            DemandExecutionStatus.Accepted,
            (await fixture.Context.AcceptedDemands.AsNoTracking().SingleAsync(row => row.DemandId == SecondDemandId, token)).Status);
    }

    /// <summary>
    /// 第二个取货站上两条：一条到期时正在装，一条还没录入。正在装的那一批闭环、照常装上；没录入的那条被释放回积压；车上那张空清单说
    /// 「本站因任务释放结束」（<c>DEMAND_RELEASED</c>），清单号一个不重；车随即离站去卸货。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 「多条追加里部分已开始装」与「已开始装的不受影响」是同一条用例的两半：只断被截的那一条，一个把在装那条一起截掉的实现也能过。
    /// </para>
    /// <para>
    /// <b>闭环那一轮就截，不先给车发一版只剩第三条的清单。</b>先发再截，操作员会在几秒钟里看到一条马上被拿走的需求，还收得到它的录入请求。
    /// </para>
    /// <para>
    /// <b>清单号</b>：车已到站，这一站的清单早已发出，释放让这个停靠少了一条归属——释放服务在「车已到站」时拒绝释放
    /// （<c>DemandReleaseReasons.AfterArrival</c>）就是怕号对不上。这里断整条清单流没有两版同号、空清单的号在此前每一版之上、
    /// 关卡那一版又在空清单之上。
    /// </para>
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task ABatchUnderWayAtTheDeadlineLoadsAndOnlyTheUnstartedDemandBesideItIsReleased()
    {
        await using RuntimeFixture fixture = await HoldingFixtureAsync();
        await StopEndedJourneyContinuesTests.ArriveAtTheSecondPickupAsync(fixture, thirdAtTheSecondPickup: true);
        string journeyId = (await JourneyOfAsync(fixture, FirstDemandId)).JourneyId;
        await EnterSublotAsync(fixture, SecondDemandId, SecondSublot, SecondSubmissionId);
        Assert.Equal(JourneyRuntimeStage.AwaitingLoadResult, (await JourneyOfAsync(fixture, FirstDemandId)).Stage);

        fixture.Clock.Advance(TimeSpan.FromMinutes(30));
        await TickAndRunAsync(fixture);
        Assert.Equal(JourneyDemandStatuses.Loading, (await MembershipOnAsync(fixture, journeyId, SecondDemandId)).Status);
        Assert.Equal(
            JourneyDemandStatuses.PendingLoad,
            (await MembershipOnAsync(fixture, journeyId, StopEndedJourneyContinuesTests.ThirdDemandId)).Status);

        int entryRequestsBefore = await EntryRequestsAsync(fixture);
        long highestBefore = (await WorklistsAsync(fixture)).Max(item => item.Revision);
        await SettleLoadAsync(fixture, SecondDemandId);

        Assert.Equal(JourneyDemandStatuses.Loaded, (await MembershipOnAsync(fixture, journeyId, SecondDemandId)).Status);
        Assert.Equal(JourneyDemandStatuses.Loaded, (await MembershipOnAsync(fixture, journeyId, FirstDemandId)).Status);
        await AssertReleasedForRedispatchAsync(fixture, journeyId, StopEndedJourneyContinuesTests.ThirdDemandId);
        Assert.Equal(entryRequestsBefore, await EntryRequestsAsync(fixture));
        Snapshot empty = (await WorklistsAsync(fixture))
            .Where(item => item.Payload.GetProperty("items").GetArrayLength() == 0)
            .OrderBy(item => item.Revision)
            .Last();
        Assert.Equal("DEMAND_RELEASED", empty.Payload.GetProperty("stopEndedReason").GetString());
        Assert.True(empty.Revision > highestBefore, $"The empty worklist {empty.Revision} is not above {highestBefore}.");

        for (int round = 0; round < 3 && !await DepartureCheckSentFromAsync(fixture, journeyId, SecondPickupStationRiotId); round++)
        {
            await TickAndRunAsync(fixture);
        }
        JourneyRuntimeRow departed = await AnswerDepartureSafetyAsync(fixture, FirstDemandId, SecondSafetyResultId);
        Assert.Equal(JourneyRuntimeStage.AwaitingGateArrival, departed.Stage);
        await ArriveAtCurrentStopAsync(fixture, FirstDemandId, "TO_GATE");

        Snapshot[] worklists = await WorklistsAsync(fixture);
        long[] revisions = [.. worklists.Select(item => item.Revision)];
        Assert.Equal(revisions.Length, revisions.Distinct().Count());
        Assert.Contains(worklists, item => item.Revision > empty.Revision && item.Payload.GetProperty("items").GetArrayLength() > 0);
    }

    /// <summary>
    /// 期限与到站同一轮：车开往第二个取货站的路上期限到了，到站那一轮先截断——那一站没有可装的，车不等录入、不下装货命令，离站去卸货。
    /// </summary>
    /// <remarks>
    /// 当前下一站在路上不可删（REQ-0197），所以车照样开到那一站；要证的是它到了之后不卡在一个没有作业的停靠上等录入（#215 登记的那个形状）。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task WhenTheDeadlineFallsInTheArrivalRoundTheEmptiedStopIsLeftWithoutALoad()
    {
        await using RuntimeFixture fixture = await HoldingFixtureAsync();
        await LoadTheFirstDemandAsync(fixture);
        await AppendSecondAsync(fixture);
        await TickAndRunAsync(fixture);
        string journeyId = (await JourneyOfAsync(fixture, FirstDemandId)).JourneyId;
        await AnswerDepartureSafetyAsync(fixture, FirstDemandId, FirstSafetyResultId);
        Assert.Equal(JourneyRuntimeStage.AwaitingPickupArrival, (await JourneyOfAsync(fixture, FirstDemandId)).Stage);

        fixture.Clock.Advance(TimeSpan.FromMinutes(30));
        // 车在路上一直在报（真车载端每几秒一次心跳）：钟跳过去的这半小时不能被读成车失联。
        await fixture.HearFromPeerAsync();
        await ArriveAtCurrentStopAsync(fixture, FirstDemandId, "TO_GATE");

        await AssertReleasedForRedispatchAsync(fixture, journeyId, SecondDemandId);
        for (int round = 0; round < 3 && !await DepartureCheckSentFromAsync(fixture, journeyId, SecondPickupStationRiotId); round++)
        {
            await TickAndRunAsync(fixture);
        }
        Assert.Equal(0, await LoadCommandsForAsync(fixture, SecondDemandId));
        JourneyRuntimeRow departed = await AnswerDepartureSafetyAsync(fixture, FirstDemandId, SecondSafetyResultId);
        Assert.Equal(JourneyRuntimeStage.AwaitingGateArrival, departed.Stage);
    }

    /// <summary>
    /// 重启之后期限仍从第一笔装货算起：第二个取货站上又装了一批，不重置；引擎重建、只读落库的行，到「第一笔 + 期限」就截掉剩下那条。
    /// </summary>
    /// <remarks>
    /// 判别点选在「第一笔 + 30 分钟」之后一秒、「第二笔 + 30 分钟」之前：期限若被第二笔重置，这一轮不截，用例红。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task AfterARestartTheDeadlineStillCountsFromTheFirstLoad()
    {
        await using RuntimeFixture fixture = await HoldingFixtureAsync();
        await StopEndedJourneyContinuesTests.ArriveAtTheSecondPickupAsync(fixture, thirdAtTheSecondPickup: true);
        string journeyId = (await JourneyOfAsync(fixture, FirstDemandId)).JourneyId;
        // 到了第二个取货站才把离站等待拉长：要的是期限由持货判，不由站点期限先把第三条以别的码收掉。
        fixture.Options.StationDepartureWaitTimeout = TimeSpan.FromMinutes(60);
        DateTimeOffset firstLoad = (await JourneyOfAsync(fixture, FirstDemandId)).CargoHoldingStartedAt!.Value;
        fixture.Clock.Advance(TimeSpan.FromMinutes(5));
        await EnterSublotAsync(fixture, SecondDemandId, SecondSublot, SecondSubmissionId);
        await SettleLoadAsync(fixture, SecondDemandId);
        Assert.Equal(JourneyDemandStatuses.Loaded, (await MembershipOnAsync(fixture, journeyId, SecondDemandId)).Status);

        await fixture.RecreateEngineAsync();
        fixture.Clock.Advance(firstLoad + TimeSpan.FromMinutes(30) - fixture.Clock.GetUtcNow() - TimeSpan.FromSeconds(2));
        await TickAndRunAsync(fixture);
        JourneyDemandRow notYet = await MembershipOnAsync(fixture, journeyId, StopEndedJourneyContinuesTests.ThirdDemandId);
        Assert.Null(notYet.RemovedAt);
        Assert.Equal(JourneyDemandStatuses.PendingLoad, notYet.Status);

        await TickAndRunAsync(fixture);
        Assert.Equal(firstLoad, (await JourneyOfAsync(fixture, FirstDemandId)).CargoHoldingStartedAt);
        await AssertReleasedForRedispatchAsync(fixture, journeyId, StopEndedJourneyContinuesTests.ThirdDemandId);
    }

    /// <summary>
    /// 会话因本服务端自己的在途单而未就绪（<c>DEPARTURE_SAFETY_NOT_READY</c>，真车载端带着本服务端的单行驶时的常态，合成车载端看不到）：
    /// 期限在路上过去，推进段这期间不推进；会话回到 <c>Ready</c>、车到站的那一轮截断，车不卡在那一站，离站去卸货。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 截断读的是落库的起算点与服务端此刻的钟，不读会话；但它挂在推进段上，而推进段在会话未就绪时整段不跑
    /// （<c>AdvanceAsync</c> 开头的就绪闸门）。所以截断在真车上发生在到站、会话回到就绪的那一轮——车照样开到那一站（当前下一站在路上不可删，
    /// REQ-0197），到了之后不等录入、离站。这条用例钉的是「不卡」，不钉截断的时刻。
    /// </para>
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task WhileTheOwnOrderKeepsTheSessionNotReadyTheTruncationWaitsForReadinessAndTheVehicleDoesNotStall()
    {
        await using RuntimeFixture fixture = await HoldingFixtureAsync();
        await LoadTheFirstDemandAsync(fixture);
        await AppendSecondAsync(fixture);
        await TickAndRunAsync(fixture);
        string journeyId = (await JourneyOfAsync(fixture, FirstDemandId)).JourneyId;
        await AnswerDepartureSafetyAsync(fixture, FirstDemandId, FirstSafetyResultId);
        Assert.Equal(JourneyRuntimeStage.AwaitingPickupArrival, (await JourneyOfAsync(fixture, FirstDemandId)).Stage);

        await PickupDispatchPlanPastOwnOrderTests.DropSessionOnOwnOrderAsync(fixture);
        fixture.Clock.Advance(TimeSpan.FromMinutes(30));
        await TickAndRunAsync(fixture);
        Assert.Equal("ONBOARD_SESSION_NOT_READY", (await JourneyOfAsync(fixture, FirstDemandId)).BlockReasonCode);

        await fixture.RestoreSessionReadyAsync();
        await fixture.HearFromPeerAsync();
        await ArriveAtCurrentStopAsync(fixture, FirstDemandId, "TO_GATE");

        await AssertReleasedForRedispatchAsync(fixture, journeyId, SecondDemandId);
        for (int round = 0; round < 3 && !await DepartureCheckSentFromAsync(fixture, journeyId, SecondPickupStationRiotId); round++)
        {
            await TickAndRunAsync(fixture);
        }
        Assert.Equal(0, await LoadCommandsForAsync(fixture, SecondDemandId));
        JourneyRuntimeRow departed = await AnswerDepartureSafetyAsync(fixture, FirstDemandId, SecondSafetyResultId);
        Assert.Equal(JourneyRuntimeStage.AwaitingGateArrival, departed.Stage);
    }

    /// <summary>
    /// 期限到时当前停靠上开着一次扫码前取消：这一轮不截，等车报结果；取消以 <c>ALL_EMPTY</c> 结清之后，同站剩下没开始装的那条才被截掉。
    /// </summary>
    /// <remarks>
    /// 取消要车证明那一排仓位是空的，证明进行中结束这一站，被取消的那条就会以别的来路收场，它的结果到了只能对着一条已经离开的需求
    /// （与站点期限那一处同一条规矩，control-server#83）。所以两条都要断：取消的那条是 <c>CANCELLED_BY_OPERATOR</c>，不是被期限抢走。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task AnOpenCancellationAtTheStopHoldsTheTruncationUntilTheVehicleReports()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await HoldingFixtureAsync();
        await StopEndedJourneyContinuesTests.ArriveAtTheSecondPickupAsync(fixture, thirdAtTheSecondPickup: true);
        string journeyId = (await JourneyOfAsync(fixture, FirstDemandId)).JourneyId;
        await using ControlServerDbContext connection = fixture.OpenConnectionContext();
        OnboardMessageProcessor processor = TestOnboardProcessorFactory.Create(
            connection, new WireToGateStore(connection), fixture.Clock, new ConfigurationBuilder().Build(), fixture.Peer);
        OnboardConnectionState state = StopEndedJourneyContinuesTests.Connection(fixture);
        const string cancellationId = "d2900000-0000-4000-8000-000000000001";
        await processor.ProcessAsync(
            StopEndedJourneyContinuesTests.Envelope(fixture, "d2900000-0000-4000-8000-000000000002", "LoadCancellationStartRequested", new
            {
                cancellationId,
                demandId = SecondDemandId,
                slotOperationAttemptId = (string?)null,
                @operator = new { operatorId = "OP-001", verificationMethod = "BADGE", verifiedAt = fixture.Clock.GetUtcNow() },
                reason = "Nothing to load at this stop."
            }),
            state,
            token);

        fixture.Clock.Advance(TimeSpan.FromMinutes(30));
        await fixture.HearFromPeerAsync();
        await TickAndRunAsync(fixture);
        Assert.Equal(LoadingPhaseStates.Closed, (await JourneyOfAsync(fixture, FirstDemandId)).LoadingPhaseState);
        foreach (string demandId in new[] { SecondDemandId, StopEndedJourneyContinuesTests.ThirdDemandId })
        {
            JourneyDemandRow held = await MembershipOnAsync(fixture, journeyId, demandId);
            Assert.Null(held.RemovedAt);
            Assert.Equal(JourneyDemandStatuses.PendingLoad, held.Status);
        }

        await processor.ProcessAsync(
            StopEndedJourneyContinuesTests.Envelope(fixture, "d2900000-0000-4000-8000-000000000003", "LoadCancellationResult", new
            {
                cancellationId,
                demandId = SecondDemandId,
                slotOperationAttemptId = (string?)null,
                overallOutcome = "ALL_EMPTY",
                slotResults = Array.Empty<object>(),
                observedAt = fixture.Clock.GetUtcNow()
            }),
            state,
            token);
        await TickAndRunAsync(fixture);

        Assert.Equal(
            "CANCELLED_BY_OPERATOR",
            (await fixture.Context.Set<TransportDemandSuppressionRow>().AsNoTracking()
                .SingleAsync(row => row.DemandId == SecondDemandId, token)).ReasonCode);
        await AssertReleasedForRedispatchAsync(fixture, journeyId, StopEndedJourneyContinuesTests.ThirdDemandId);
    }

    /// <summary>
    /// 同站一条正在装、另一条（丙）开着扫码前取消、还有一条（丁）没录入：期限在装货期间过去，那一批闭环的那一轮不截——丙带着开着的取消被释放，
    /// 车报来的取消结果就找不到旅程（审查 H1）。取消以 <c>ALL_EMPTY</c> 结清后丙是 <c>CANCELLED_BY_OPERATOR</c>，同站其余没开始装的（丁）照截。
    /// </summary>
    /// <remarks>
    /// 截断最常发生的正是装货闭环那一轮，那时阶段是 <c>AwaitingLoadResult</c>；<c>OnboardRecoveryCoordinator</c> 在这个阶段也允许对同站另一条
    /// <c>PENDING_LOAD</c> 发起扫码前取消。守卫原先只在 <c>AwaitingSublot</c> 生效，这条用例就是审查探针的那个形状。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task ACancellationOpenedWhileAnotherDemandLoadsHoldsTheTruncationAtThatBatchesClosure()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await HoldingFixtureAsync();
        await StopEndedJourneyContinuesTests.ArriveAtTheSecondPickupAsync(fixture, thirdAtTheSecondPickup: true);
        string journeyId = (await JourneyOfAsync(fixture, FirstDemandId)).JourneyId;
        fixture.BoxCounts.Set(FourthSublot, 7);
        await Batch7ThreeStopJourneyTests.AppendDemandAsync(
            fixture, FourthDemandId, FourthSublot, SecondPickupArea, SecondPickupStationRiotId);
        await EnterSublotAsync(fixture, SecondDemandId, SecondSublot, SecondSubmissionId);
        Assert.Equal(JourneyRuntimeStage.AwaitingLoadResult, (await JourneyOfAsync(fixture, FirstDemandId)).Stage);

        await using ControlServerDbContext connection = fixture.OpenConnectionContext();
        OnboardMessageProcessor processor = TestOnboardProcessorFactory.Create(
            connection, new WireToGateStore(connection), fixture.Clock, new ConfigurationBuilder().Build(), fixture.Peer);
        OnboardConnectionState state = StopEndedJourneyContinuesTests.Connection(fixture);
        const string cancellationId = "d2900000-0000-4000-8000-0000000000a1";
        await processor.ProcessAsync(
            StopEndedJourneyContinuesTests.Envelope(fixture, "d2900000-0000-4000-8000-0000000000a2", "LoadCancellationStartRequested", new
            {
                cancellationId,
                demandId = StopEndedJourneyContinuesTests.ThirdDemandId,
                slotOperationAttemptId = (string?)null,
                @operator = new { operatorId = "OP-001", verificationMethod = "BADGE", verifiedAt = fixture.Clock.GetUtcNow() },
                reason = "Nothing to load at this stop."
            }),
            state,
            token);
        Assert.True(await LoadCancellationBeforeSublot.HasOpenCancellationAsync(
            fixture.Context, StopEndedJourneyContinuesTests.ThirdDemandId, token), "The cancellation was not opened.");

        fixture.Clock.Advance(TimeSpan.FromMinutes(30));
        await fixture.HearFromPeerAsync();
        await TickAndRunAsync(fixture);
        await SettleLoadAsync(fixture, SecondDemandId);
        Assert.Equal(LoadingPhaseStates.Closed, (await JourneyOfAsync(fixture, FirstDemandId)).LoadingPhaseState);
        Assert.Equal(JourneyDemandStatuses.Loaded, (await MembershipOnAsync(fixture, journeyId, SecondDemandId)).Status);
        foreach (string demandId in new[] { StopEndedJourneyContinuesTests.ThirdDemandId, FourthDemandId })
        {
            JourneyDemandRow held = await MembershipOnAsync(fixture, journeyId, demandId);
            Assert.True(held.RemovedAt is null, $"{demandId} was released while a cancellation was open at its stop.");
        }

        await processor.ProcessAsync(
            StopEndedJourneyContinuesTests.Envelope(fixture, "d2900000-0000-4000-8000-0000000000a3", "LoadCancellationResult", new
            {
                cancellationId,
                demandId = StopEndedJourneyContinuesTests.ThirdDemandId,
                slotOperationAttemptId = (string?)null,
                overallOutcome = "ALL_EMPTY",
                slotResults = Array.Empty<object>(),
                observedAt = fixture.Clock.GetUtcNow()
            }),
            state,
            token);
        Assert.Equal(
            "CANCELLED_BY_OPERATOR",
            (await fixture.Context.Set<TransportDemandSuppressionRow>().AsNoTracking()
                .SingleAsync(row => row.DemandId == StopEndedJourneyContinuesTests.ThirdDemandId, token)).ReasonCode);

        await TickAndRunAsync(fixture);
        await AssertReleasedForRedispatchAsync(fixture, journeyId, FourthDemandId);
    }

    /// <summary>
    /// 离站等待开着（5 分钟）时，到站那一轮被截空的取货停靠立刻离站，不白等一整段（审查 M1）。
    /// </summary>
    /// <remarks>
    /// 其余用例的夹具里离站等待是 0，看不到这件事：车已过了持货硬期限，还要在一台空机台前多停最多 5 分钟、占着那台机台。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task AStopEmptiedByTheTruncationIsLeftWithoutWaitingOutTheDepartureWait()
    {
        await using RuntimeFixture fixture = await HoldingFixtureAsync();
        await LoadTheFirstDemandAsync(fixture);
        await AppendSecondAsync(fixture);
        await TickAndRunAsync(fixture);
        string journeyId = (await JourneyOfAsync(fixture, FirstDemandId)).JourneyId;
        await AnswerDepartureSafetyAsync(fixture, FirstDemandId, FirstSafetyResultId);

        fixture.Options.StationDepartureWaitTimeout = TimeSpan.FromMinutes(5);
        fixture.Clock.Advance(TimeSpan.FromMinutes(30));
        await fixture.HearFromPeerAsync();
        await ArriveAtCurrentStopAsync(fixture, FirstDemandId, "TO_GATE");
        await AssertReleasedForRedispatchAsync(fixture, journeyId, SecondDemandId);

        for (int round = 0; round < 3 && !await DepartureCheckSentFromAsync(fixture, journeyId, SecondPickupStationRiotId); round++)
        {
            await TickAndRunAsync(fixture);
        }
        Assert.True(
            await DepartureCheckSentFromAsync(fixture, journeyId, SecondPickupStationRiotId),
            "The emptied stop waited out the departure wait instead of leaving.");
        JourneyRuntimeRow departed = await AnswerDepartureSafetyAsync(fixture, FirstDemandId, SecondSafetyResultId);
        Assert.Equal(JourneyRuntimeStage.AwaitingGateArrival, departed.Stage);
    }

    /// <summary>
    /// 离站核验已经发出（问的是开往第二个取货站）、车还没答，期限到了：截掉第二个取货停靠，作废那张核验、换新身份再问一次，新的一张问的是
    /// 卸货站；车答了新的那张就开往卸货站（审查 L2）。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task ADepartureCheckAskedTowardATruncatedStopIsRetiredAndAskedAgainTowardTheNextStop()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await HoldingFixtureAsync();
        await LoadTheFirstDemandAsync(fixture);
        await AppendSecondAsync(fixture);
        await TickAndRunAsync(fixture);
        string journeyId = (await JourneyOfAsync(fixture, FirstDemandId)).JourneyId;
        Assert.Equal(JourneyRuntimeStage.AwaitingDepartureSafety, (await JourneyOfAsync(fixture, FirstDemandId)).Stage);
        ProtocolOutboxRow first = Assert.Single(await DepartureChecksAsync(fixture));
        JourneyStopRow secondPickup = await PickupStopAtAsync(fixture, journeyId, SecondPickupStationRiotId);
        Assert.Equal(secondPickup.StationId, TargetStationOf(first));

        fixture.Clock.Advance(TimeSpan.FromMinutes(30));
        await fixture.HearFromPeerAsync();
        await TickAndRunAsync(fixture);

        await AssertReleasedForRedispatchAsync(fixture, journeyId, SecondDemandId);
        ProtocolOutboxRow[] checks = await DepartureChecksAsync(fixture);
        Assert.Equal(2, checks.Length);
        Assert.NotNull(checks.Single(row => row.MessageId == first.MessageId).FencedAt);
        ProtocolOutboxRow again = checks.Single(row => row.MessageId != first.MessageId);
        string unloadStation = (await fixture.Context.Set<JourneyStopRow>().AsNoTracking()
            .SingleAsync(row => row.JourneyId == journeyId && row.StopRole == JourneyStopRoles.Unload, token)).StationId;
        Assert.Equal(unloadStation, TargetStationOf(again));

        JourneyRuntimeRow departed = await AnswerDepartureSafetyAsync(fixture, FirstDemandId, FirstSafetyResultId);
        Assert.Equal(JourneyRuntimeStage.AwaitingGateArrival, departed.Stage);
    }

    /// <summary>
    /// 车已到第二个取货站、清单已发、这一站一条都还没装，期限到了：截掉之后这一站较早各版清单都退役，断线重连只补发那张空清单
    /// （C11 转 cs#703 实测）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 空清单的号取「算式」与「已发最高号 + 1」的大者，不会撞号；但它退役本站较早各版，是按本停靠的版数范围从首号数到算式那一号。
    /// 释放让本停靠的归属少了、而「已做完」的一条没多，算式不动，较早那一版落在范围外、不退役，重连后被补发——号比空清单低，车判回退、断会话。
    /// 所以释放几条就给本停靠补几版。
    /// </para>
    /// <para>
    /// 这个形状只在「本站一条都还没做完」时出现：本站已装过一条（<see cref="ABatchUnderWayAtTheDeadlineLoadsAndOnlyTheUnstartedDemandBesideItIsReleased"/>）
    /// 时算式已经往前走了一格，较早那一版碰巧落在范围里，所以那条用例看不出补版数的作用（变异自检里删掉补版数它照绿）。
    /// </para>
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task TruncatingTheStopTheVehicleStandsAtRetiresEveryEarlierWorklistAndAReconnectReplaysOnlyTheEmptyOne()
    {
        await using RuntimeFixture fixture = await HoldingFixtureAsync();
        JourneyStopRow secondPickup = await StopEndedJourneyContinuesTests.ArriveAtTheSecondPickupAsync(fixture);
        string journeyId = (await JourneyOfAsync(fixture, FirstDemandId)).JourneyId;
        Assert.Contains(
            await WorklistsAsync(fixture),
            item => item.Payload.GetProperty("stationId").GetString() == secondPickup.StationId &&
                    item.Payload.GetProperty("items").GetArrayLength() > 0);

        fixture.Clock.Advance(TimeSpan.FromMinutes(30));
        await fixture.HearFromPeerAsync();
        await TickAndRunAsync(fixture);
        await AssertReleasedForRedispatchAsync(fixture, journeyId, SecondDemandId);

        Snapshot[] worklists = await WorklistsAsync(fixture);
        Snapshot empty = worklists
            .Where(item => item.Payload.GetProperty("stationId").GetString() == secondPickup.StationId &&
                           item.Payload.GetProperty("items").GetArrayLength() == 0)
            .OrderBy(item => item.Revision)
            .Last();
        string[] earlierAtThisStop =
        [
            .. worklists
                .Where(item => item.MessageId != empty.MessageId &&
                               item.Payload.GetProperty("stationId").GetString() == secondPickup.StationId)
                .Select(item => item.MessageId)
        ];
        Assert.NotEmpty(earlierAtThisStop);
        ProtocolOutboxRow[] earlierRows = await fixture.Context.ProtocolOutbox.AsNoTracking()
            .Where(row => earlierAtThisStop.Contains(row.MessageId))
            .ToArrayAsync(TestContext.Current.CancellationToken);
        Assert.All(earlierRows, row => Assert.True(
            row.FencedAt is not null || row.AcknowledgedAt is not null,
            $"The earlier worklist {row.MessageId} at this stop would still be replayed."));

        await fixture.ReconnectAsync(2);
        await fixture.AdvanceSessionAsync(2);
        await fixture.HearFromPeerAsync();
        int before = fixture.Peer.Lines.Count;
        await TickAndRunAsync(fixture);
        string[] replayed = [.. fixture.Peer.Lines.Skip(before).Select(line => System.Text.Encoding.UTF8.GetString(line))];
        Assert.Contains(replayed, line => line.Contains(empty.MessageId, StringComparison.Ordinal));
        Assert.DoesNotContain(replayed, line => earlierAtThisStop.Any(id => line.Contains(id, StringComparison.Ordinal)));
    }

    /// <summary>
    /// 开往第二个取货站的订单意图已经保存（离站已授权），而阶段还没前移（保存冲突或重启），持货期限恰在这时过去：那一站和它上面的需求都不截，
    /// 也不会按截断之后的下一站再建一张单（cs#290 再审 L3）。
    /// </summary>
    /// <remarks>
    /// 「意图已存、阶段没前移」用落库状态构造：先正常答离站核验（意图、建单、阶段前移、停靠完成），再把旅程阶段与第一个停靠的状态改回离站之前。
    /// 这正是崩在「意图保存」与「阶段前移」两次保存之间时库里的样子。截掉那一站的实现，下一轮会把卸货站当成下一站重新核验、建第二张单。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task APickupStopWhoseOrderIntentIsAlreadySavedIsNotTruncated()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await HoldingFixtureAsync();
        await LoadTheFirstDemandAsync(fixture);
        await AppendSecondAsync(fixture);
        await TickAndRunAsync(fixture);
        JourneyRuntimeRow beforeDeparture = await JourneyOfAsync(fixture, FirstDemandId);
        string journeyId = beforeDeparture.JourneyId;
        Assert.Equal(JourneyRuntimeStage.AwaitingDepartureSafety, beforeDeparture.Stage);
        JourneyStopRow firstPickup = await CurrentStopAsync(fixture, FirstDemandId);
        string firstPickupStatus = firstPickup.Status;
        JourneyStopRow secondPickup = await PickupStopAtAsync(fixture, journeyId, SecondPickupStationRiotId);
        string unloadUpperId = (await fixture.Context.Set<JourneyStopRow>().AsNoTracking()
            .SingleAsync(row => row.JourneyId == journeyId && row.StopRole == JourneyStopRoles.Unload, token)).UpperId;

        await AnswerDepartureSafetyAsync(fixture, FirstDemandId, FirstSafetyResultId);
        Assert.True(
            await fixture.Context.OrderIntents.AsNoTracking().AnyAsync(row => row.UpperId == secondPickup.UpperId, token),
            "The departure did not save an order intent toward the second pickup.");
        JourneyRuntimeRow runtime = await fixture.Context.JourneyRuntimes.SingleAsync(row => row.JourneyId == journeyId, token);
        runtime.Stage = JourneyRuntimeStage.AwaitingDepartureSafety;
        JourneyStopRow trackedFirst = await fixture.Context.Set<JourneyStopRow>()
            .SingleAsync(row => row.StopId == firstPickup.StopId, token);
        trackedFirst.Status = firstPickupStatus;
        await fixture.Context.SaveChangesAsync(token);
        fixture.Context.ChangeTracker.Clear();

        fixture.Clock.Advance(TimeSpan.FromMinutes(30));
        await fixture.HearFromPeerAsync();
        await TickAndRunAsync(fixture);

        Assert.Equal(LoadingPhaseStates.Closed, (await JourneyOfAsync(fixture, FirstDemandId)).LoadingPhaseState);
        JourneyDemandRow second = await MembershipOnAsync(fixture, journeyId, SecondDemandId);
        Assert.True(second.RemovedAt is null, "The demand whose pickup already has an order was released.");
        Assert.NotEqual(JourneyStopStatuses.Removed, (await PickupStopAtAsync(fixture, journeyId, SecondPickupStationRiotId)).Status);
        Assert.False(
            await fixture.Context.OrderIntents.AsNoTracking().AnyAsync(row => row.UpperId == unloadUpperId, token),
            "A second order was planned toward the unload stop.");
    }

    /// <summary>
    /// 参数批准之前（不适用持货等单）一条都不截：与批次 6 相同，这一票对未配置分区参数的旅程零变化。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task WithoutHoldingNothingIsEverTruncated()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        await StopEndedJourneyContinuesTests.ArriveAtTheSecondPickupAsync(fixture, thirdAtTheSecondPickup: true);
        string journeyId = (await JourneyOfAsync(fixture, FirstDemandId)).JourneyId;

        fixture.Clock.Advance(TimeSpan.FromHours(2));
        await TickAndRunAsync(fixture);

        foreach (string demandId in new[] { SecondDemandId, StopEndedJourneyContinuesTests.ThirdDemandId })
        {
            JourneyDemandRow kept = await MembershipOnAsync(fixture, journeyId, demandId);
            Assert.Null(kept.RemovedAt);
            Assert.Equal(JourneyDemandStatuses.PendingLoad, kept.Status);
        }
    }

    private static Task AppendSecondAsync(RuntimeFixture fixture)
    {
        fixture.BoxCounts.Set(SecondSublot, 7);
        return Batch7ThreeStopJourneyTests.AppendDemandAsync(
            fixture, SecondDemandId, SecondSublot, SecondPickupArea, SecondPickupStationRiotId);
    }

    /// <summary>
    /// 放回了待派队列：需求仍受理中、在这趟旅程上的归属标了释放改派、没有业务键抑制，派车读口把它当作「已释放待改派」。
    /// </summary>
    private static async Task AssertReleasedForRedispatchAsync(RuntimeFixture fixture, string journeyId, string demandId)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        Assert.Equal(
            DemandExecutionStatus.Accepted,
            (await fixture.Context.AcceptedDemands.AsNoTracking().SingleAsync(row => row.DemandId == demandId, token)).Status);
        JourneyDemandRow membership = await MembershipOnAsync(fixture, journeyId, demandId);
        Assert.True(membership.RemovedAt is not null, $"{demandId} is still carried by {journeyId} ({membership.Status}).");
        Assert.Equal(DemandJourneyLookup.ReleasedForRedispatchReason, membership.RemovalReason);
        Assert.False(
            await fixture.Context.Set<TransportDemandSuppressionRow>().AsNoTracking().AnyAsync(row => row.DemandId == demandId, token),
            $"{demandId} was suppressed: it would never be dispatched again.");
        Assert.True(
            await DemandJourneyLookup.ReleasedForRedispatch(fixture.Context).AsNoTracking().AnyAsync(row => row.DemandId == demandId, token),
            $"{demandId} is not offered to the dispatch round as released for redispatch.");
    }

    private static Task<JourneyDemandRow> MembershipOnAsync(RuntimeFixture fixture, string journeyId, string demandId) =>
        fixture.Context.Set<JourneyDemandRow>().AsNoTracking()
            .SingleAsync(row => row.JourneyId == journeyId && row.DemandId == demandId, TestContext.Current.CancellationToken);

    private static Task<JourneyDemandRow[]> MembershipsInForceAsync(RuntimeFixture fixture, string demandId) =>
        fixture.Context.Set<JourneyDemandRow>().AsNoTracking()
            .Where(row => row.DemandId == demandId && row.RemovedAt == null)
            .ToArrayAsync(TestContext.Current.CancellationToken);

    private static Task<JourneyStopRow> PickupStopAtAsync(RuntimeFixture fixture, string journeyId, int stationRiotId) =>
        fixture.Context.Set<JourneyStopRow>().AsNoTracking()
            .SingleAsync(
                row => row.JourneyId == journeyId && row.StopRole == JourneyStopRoles.Pickup && row.StationRiotId == stationRiotId,
                TestContext.Current.CancellationToken);

    private static Task<int> EntryRequestsAsync(RuntimeFixture fixture) =>
        fixture.Context.ProtocolOutbox.AsNoTracking()
            .CountAsync(row => row.MessageType == "SublotEntryRequested", TestContext.Current.CancellationToken);

    private static async Task<int> LoadCommandsForAsync(RuntimeFixture fixture, string demandId) =>
        (await fixture.Context.ProtocolOutbox.AsNoTracking()
            .Where(row => row.MessageType == "SlotOperationCommand")
            .ToArrayAsync(TestContext.Current.CancellationToken))
        .Count(row => row.PayloadJson.Contains(demandId, StringComparison.Ordinal) &&
                      row.PayloadJson.Contains("\"LOAD\"", StringComparison.Ordinal));

    private static async Task<bool> DepartureCheckSentFromAsync(RuntimeFixture fixture, string journeyId, int stationRiotId)
    {
        JourneyStopRow stop = await PickupStopAtAsync(fixture, journeyId, stationRiotId);
        return stop.DepartureSafetyCheckMessageId is { } messageId &&
               await fixture.Context.ProtocolOutbox.AsNoTracking()
                   .AnyAsync(row => row.MessageId == messageId, TestContext.Current.CancellationToken);
    }

    private static Task<ProtocolOutboxRow[]> DepartureChecksAsync(RuntimeFixture fixture) =>
        fixture.Context.ProtocolOutbox.AsNoTracking()
            .Where(row => row.MessageType == "PreDepartureSafetyCheck")
            .ToArrayAsync(TestContext.Current.CancellationToken);

    private static string? TargetStationOf(ProtocolOutboxRow check)
    {
        using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(check.PayloadJson);
        return document.RootElement.GetProperty("payload").GetProperty("targetStationId").GetString();
    }

    private static async Task<Snapshot[]> WorklistsAsync(RuntimeFixture fixture) =>
        [.. (await SnapshotsAsync(fixture.Context, fixture.Options.AgvId)).Where(item => item.MessageType == "CurrentStopWorklistSnapshot")];
}
