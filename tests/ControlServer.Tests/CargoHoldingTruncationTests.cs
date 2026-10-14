using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using static ControlServer.Tests.Batch7CargoHoldingTests;
using static ControlServer.Tests.Batch7StopDrivenAdvanceDriver;
using static ControlServer.Tests.ClosureSnapshotAssertions;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;

namespace ControlServer.Tests;

/// <summary>
/// 持货超时截断未开始装的停靠（control-server#290；ADR-cross-0057 实施决定（三）（四））。
/// </summary>
/// <remarks>
/// <para>
/// <b>规则</b>：持货期限一到，装货阶段以 <c>CLOSED</c>／<c>CARGO_HOLDING_TIMEOUT</c> 结束；这趟旅程里还没开始装的需求——不论在车此刻所在
/// 的停靠还是后面的取货停靠——以 <c>CANCELLED_BY_STOP_COMPLETE</c> 终结，它们因此留空的停靠由 7-10 的计划修订删掉。已经在装的那一批等它
/// 安全闭环（「到期不打断正在进行的仓位操作」），已经装上的照常送到。
/// </para>
/// <para>
/// 在这之前（批次7-07 起）到期只关装货阶段：车上显示「持货超时」，车却照样开去另一个取货停靠装货，持货远超部署上限的 5 分钟。
/// </para>
/// <para>
/// <b>判据断到需求、归属、停靠三张表与业务键抑制</b>：终结码只在抑制行与日志里看得见，需求行只记 <c>Cancelled</c>。车去不去那一站，
/// 断在离站之后的阶段上（去卸货站还是去取货站）。
/// </para>
/// </remarks>
public sealed class CargoHoldingTruncationTests
{
    private const string StopCompleteReason = "CANCELLED_BY_STOP_COMPLETE";
    private const int SecondPickupStationRiotId = 13;

    /// <summary>
    /// 车还停在第一个取货站（离站等待没完），持货期限先到：另一个取货停靠上那条还没开始装的需求以 <c>CANCELLED_BY_STOP_COMPLETE</c> 终结、
    /// 它的停靠被删，车离站时直接去卸货站；第一站装上的那条不受影响。
    /// </summary>
    /// <remarks>
    /// 持货 2 分钟、离站等待 5 分钟，好让期限落在车还停着的时候——这正是 S4 里 agv02 的形状：第二条同站追加在第一笔落定之后才进来。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task AtTheHoldingDeadlineADemandNotYetStartedAtAnotherStopIsEndedAsStopComplete()
    {
        await using RuntimeFixture fixture = await HoldingFixtureAsync();
        fixture.Options.CargoHoldingTimeout = TimeSpan.FromMinutes(2);
        fixture.Options.StationDepartureWaitTimeout = TimeSpan.FromMinutes(5);
        await LoadTheFirstDemandAsync(fixture);
        await AppendSecondAsync(fixture);
        await TickAndRunAsync(fixture);
        Assert.Equal(JourneyRuntimeStage.AwaitingStationDeparture, (await JourneyOfAsync(fixture, FirstDemandId)).Stage);
        Assert.Equal(JourneyDemandStatuses.PendingLoad, (await MembershipAsync(fixture, SecondDemandId)).Status);

        fixture.Clock.Advance(TimeSpan.FromMinutes(2));
        await TickAndRunAsync(fixture);

        JourneyRuntimeRow runtime = await JourneyOfAsync(fixture, FirstDemandId);
        Assert.Equal(LoadingPhaseStates.Closed, runtime.LoadingPhaseState);
        Assert.Equal(LoadingClosedReasons.CargoHoldingTimeout, runtime.LoadingClosedReason);
        await AssertEndedAsStopCompleteAsync(fixture, SecondDemandId);
        Assert.Equal(JourneyStopStatuses.Removed, (await PickupStopAtAsync(fixture, SecondPickupStationRiotId)).Status);
        Assert.Equal(JourneyDemandStatuses.Loaded, (await MembershipAsync(fixture, FirstDemandId)).Status);

        // 离站等待到期，车离站：去的是卸货站，不是那个被删掉的取货停靠。
        fixture.Clock.Advance(TimeSpan.FromMinutes(3));
        await TickAndRunAsync(fixture);
        Assert.True(await DepartureCheckSentAsync(fixture), "The vehicle was never sent on after the truncation.");
        JourneyRuntimeRow departed = await AnswerDepartureSafetyAsync(fixture, FirstDemandId, FirstSafetyResultId);
        Assert.Equal(JourneyRuntimeStage.AwaitingGateArrival, departed.Stage);
    }

    /// <summary>
    /// 第二个取货站上两条：一条到期时正在装，一条还没录入。正在装的那一批闭环、照常装上；没录入的那条以 <c>CANCELLED_BY_STOP_COMPLETE</c>
    /// 终结；车上那张空清单说「本站因超时结束」（<c>STATION_DEADLINE_EXPIRED</c>）；车随即离站去卸货。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 「多条追加里部分已开始装」与「已开始装的不受影响」是同一条用例的两半：只断被截的那一条，一个把在装那条一起终结的实现也能过。
    /// </para>
    /// <para>
    /// <b>闭环那一轮就截，不先给车发一版只剩第三条的清单。</b>先发再截，操作员会在几秒钟里看到一条马上被取消的需求，还收得到它的录入请求。
    /// </para>
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task ABatchUnderWayAtTheDeadlineLoadsAndOnlyTheUnstartedDemandBesideItIsEnded()
    {
        await using RuntimeFixture fixture = await HoldingFixtureAsync();
        await StopEndedJourneyContinuesTests.ArriveAtTheSecondPickupAsync(fixture, thirdAtTheSecondPickup: true);
        await EnterSublotAsync(fixture, SecondDemandId, SecondSublot, SecondSubmissionId);
        Assert.Equal(JourneyRuntimeStage.AwaitingLoadResult, (await JourneyOfAsync(fixture, FirstDemandId)).Stage);

        fixture.Clock.Advance(TimeSpan.FromMinutes(30));
        await TickAndRunAsync(fixture);
        Assert.Equal(JourneyDemandStatuses.Loading, (await MembershipAsync(fixture, SecondDemandId)).Status);
        Assert.Equal(
            JourneyDemandStatuses.PendingLoad,
            (await MembershipAsync(fixture, StopEndedJourneyContinuesTests.ThirdDemandId)).Status);

        int entryRequestsBefore = await EntryRequestsAsync(fixture);
        await SettleLoadAsync(fixture, SecondDemandId);

        Assert.Equal(JourneyDemandStatuses.Loaded, (await MembershipAsync(fixture, SecondDemandId)).Status);
        Assert.Equal(JourneyDemandStatuses.Loaded, (await MembershipAsync(fixture, FirstDemandId)).Status);
        await AssertEndedAsStopCompleteAsync(fixture, StopEndedJourneyContinuesTests.ThirdDemandId);
        Assert.Equal(entryRequestsBefore, await EntryRequestsAsync(fixture));
        Assert.Equal("STATION_DEADLINE_EXPIRED", await LastStopEndedReasonAsync(fixture));

        for (int round = 0; round < 3 && !await DepartureCheckSentFromAsync(fixture, SecondPickupStationRiotId); round++)
        {
            await TickAndRunAsync(fixture);
        }
        JourneyRuntimeRow departed = await AnswerDepartureSafetyAsync(fixture, FirstDemandId, SecondSafetyResultId);
        Assert.Equal(JourneyRuntimeStage.AwaitingGateArrival, departed.Stage);
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
        await AnswerDepartureSafetyAsync(fixture, FirstDemandId, FirstSafetyResultId);
        Assert.Equal(JourneyRuntimeStage.AwaitingPickupArrival, (await JourneyOfAsync(fixture, SecondDemandId)).Stage);

        fixture.Clock.Advance(TimeSpan.FromMinutes(30));
        await ArriveAtCurrentStopAsync(fixture, SecondDemandId, "TO_GATE");

        await AssertEndedAsStopCompleteAsync(fixture, SecondDemandId);
        for (int round = 0; round < 3 && !await DepartureCheckSentFromAsync(fixture, SecondPickupStationRiotId); round++)
        {
            await TickAndRunAsync(fixture);
        }
        Assert.Equal(0, await LoadCommandsForAsync(fixture, SecondDemandId));
        JourneyRuntimeRow departed = await AnswerDepartureSafetyAsync(fixture, FirstDemandId, SecondSafetyResultId);
        Assert.Equal(JourneyRuntimeStage.AwaitingGateArrival, departed.Stage);
    }

    /// <summary>
    /// 重启之后期限仍从第一笔装货算起：第二个取货站上又装了一批，不重置；引擎重建、只读落库的行，到「第一笔 + 期限」就截断剩下那条。
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
        // 到了第二个取货站才把离站等待拉长：要的是期限由持货判，不由站点期限先把第三条以别的码收掉。
        fixture.Options.StationDepartureWaitTimeout = TimeSpan.FromMinutes(60);
        DateTimeOffset firstLoad = (await JourneyOfAsync(fixture, FirstDemandId)).CargoHoldingStartedAt!.Value;
        fixture.Clock.Advance(TimeSpan.FromMinutes(5));
        await EnterSublotAsync(fixture, SecondDemandId, SecondSublot, SecondSubmissionId);
        await SettleLoadAsync(fixture, SecondDemandId);
        Assert.Equal(JourneyDemandStatuses.Loaded, (await MembershipAsync(fixture, SecondDemandId)).Status);

        await fixture.RecreateEngineAsync();
        fixture.Clock.Advance(firstLoad + TimeSpan.FromMinutes(30) - fixture.Clock.GetUtcNow() - TimeSpan.FromSeconds(2));
        await TickAndRunAsync(fixture);
        Assert.Equal(
            JourneyDemandStatuses.PendingLoad,
            (await MembershipAsync(fixture, StopEndedJourneyContinuesTests.ThirdDemandId)).Status);

        await TickAndRunAsync(fixture);
        Assert.Equal(firstLoad, (await JourneyOfAsync(fixture, FirstDemandId)).CargoHoldingStartedAt);
        await AssertEndedAsStopCompleteAsync(fixture, StopEndedJourneyContinuesTests.ThirdDemandId);
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
        await AnswerDepartureSafetyAsync(fixture, FirstDemandId, FirstSafetyResultId);
        Assert.Equal(JourneyRuntimeStage.AwaitingPickupArrival, (await JourneyOfAsync(fixture, SecondDemandId)).Stage);

        await PickupDispatchPlanPastOwnOrderTests.DropSessionOnOwnOrderAsync(fixture);
        fixture.Clock.Advance(TimeSpan.FromMinutes(30));
        await TickAndRunAsync(fixture);
        Assert.Equal("ONBOARD_SESSION_NOT_READY", (await JourneyOfAsync(fixture, SecondDemandId)).BlockReasonCode);

        await fixture.RestoreSessionReadyAsync();
        await ArriveAtCurrentStopAsync(fixture, SecondDemandId, "TO_GATE");

        await AssertEndedAsStopCompleteAsync(fixture, SecondDemandId);
        for (int round = 0; round < 3 && !await DepartureCheckSentFromAsync(fixture, SecondPickupStationRiotId); round++)
        {
            await TickAndRunAsync(fixture);
        }
        Assert.Equal(0, await LoadCommandsForAsync(fixture, SecondDemandId));
        JourneyRuntimeRow departed = await AnswerDepartureSafetyAsync(fixture, FirstDemandId, SecondSafetyResultId);
        Assert.Equal(JourneyRuntimeStage.AwaitingGateArrival, departed.Stage);
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

        fixture.Clock.Advance(TimeSpan.FromHours(2));
        await TickAndRunAsync(fixture);

        Assert.Equal(JourneyDemandStatuses.PendingLoad, (await MembershipAsync(fixture, SecondDemandId)).Status);
        Assert.DoesNotContain(
            await fixture.Context.Set<TransportDemandSuppressionRow>().AsNoTracking()
                .ToArrayAsync(TestContext.Current.CancellationToken),
            row => row.ReasonCode == StopCompleteReason);
    }

    private static Task AppendSecondAsync(RuntimeFixture fixture)
    {
        fixture.BoxCounts.Set(SecondSublot, 7);
        return Batch7ThreeStopJourneyTests.AppendDemandAsync(fixture, SecondDemandId, SecondSublot, "N1-2", SecondPickupStationRiotId);
    }

    private static async Task AssertEndedAsStopCompleteAsync(RuntimeFixture fixture, string demandId)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        Assert.Equal(
            DemandExecutionStatus.Cancelled,
            (await fixture.Context.AcceptedDemands.AsNoTracking().SingleAsync(row => row.DemandId == demandId, token)).Status);
        Assert.Equal(JourneyDemandStatuses.Terminated, (await MembershipAsync(fixture, demandId)).Status);
        TransportDemandSuppressionRow? suppression = await fixture.Context.Set<TransportDemandSuppressionRow>().AsNoTracking()
            .SingleOrDefaultAsync(row => row.DemandId == demandId, token);
        Assert.True(suppression is not null, $"{demandId} was not ended: no suppression row.");
        Assert.Equal(StopCompleteReason, suppression.ReasonCode);
    }

    private static Task<JourneyDemandRow> MembershipAsync(RuntimeFixture fixture, string demandId) =>
        StopEndedJourneyContinuesTests.MembershipAsync(fixture, demandId);

    private static Task<JourneyStopRow> PickupStopAtAsync(RuntimeFixture fixture, int stationRiotId) =>
        fixture.Context.Set<JourneyStopRow>().AsNoTracking()
            .SingleAsync(
                row => row.StopRole == JourneyStopRoles.Pickup && row.StationRiotId == stationRiotId,
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

    private static async Task<bool> DepartureCheckSentFromAsync(RuntimeFixture fixture, int stationRiotId)
    {
        JourneyStopRow stop = await PickupStopAtAsync(fixture, stationRiotId);
        return stop.DepartureSafetyCheckMessageId is { } messageId &&
               await fixture.Context.ProtocolOutbox.AsNoTracking()
                   .AnyAsync(row => row.MessageId == messageId, TestContext.Current.CancellationToken);
    }

    /// <summary>最近一张空清单里的 <c>stopEndedReason</c>，读发件箱的载荷。</summary>
    private static async Task<string?> LastStopEndedReasonAsync(RuntimeFixture fixture)
    {
        Snapshot empty = (await SnapshotsAsync(fixture.Context, fixture.Options.AgvId))
            .Where(item => item.MessageType == "CurrentStopWorklistSnapshot" &&
                           item.Payload.GetProperty("items").GetArrayLength() == 0)
            .OrderBy(item => item.Revision)
            .Last();
        return empty.Payload.GetProperty("stopEndedReason").GetString();
    }
}
