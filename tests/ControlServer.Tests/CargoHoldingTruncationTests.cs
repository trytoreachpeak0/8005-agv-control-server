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
    /// 「本站因超时结束」（<c>STATION_DEADLINE_EXPIRED</c>），清单号一个不重；车随即离站去卸货。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 「多条追加里部分已开始装」与「已开始装的不受影响」是同一条用例的两半：只断被截的那一条，一个把在装那条一起截掉的实现也能过。
    /// </para>
    /// <para>
    /// <b>闭环那一轮就截，不先给车发一版只剩第三条的清单。</b>先发再截，操作员会在几秒钟里看到一条马上被拿走的需求，还收得到它的录入请求。
    /// </para>
    /// <para>
    /// <b>清单号</b>：车已到站，这一站的清单早已发出。释放让这个停靠少了一条归属，按版数算的号会往回缩——释放服务因此在「车已到站」时拒绝释放
    /// （<c>DemandReleaseReasons.AfterArrival</c>）。截断补上同样多的版数，这里断整条清单流没有两版同号、空清单的号在此前每一版之上、
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
        Assert.Equal("STATION_DEADLINE_EXPIRED", empty.Payload.GetProperty("stopEndedReason").GetString());
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

    private static async Task<Snapshot[]> WorklistsAsync(RuntimeFixture fixture) =>
        [.. (await SnapshotsAsync(fixture.Context, fixture.Options.AgvId)).Where(item => item.MessageType == "CurrentStopWorklistSnapshot")];
}
