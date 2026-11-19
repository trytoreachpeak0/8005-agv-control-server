using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using static ControlServer.Tests.Batch7StopDrivenAdvanceDriver;
using static ControlServer.Tests.JourneyRuntimeWorkerLoadDeadlineTests;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;
using static ControlServer.Tests.StopEndedJourneyContinuesTests;

namespace ControlServer.Tests;

/// <summary>
/// 第二个取货停靠上在装的那一条被终结、车上还带着第一站的货时，旅程接着走（control-server#291 的 S2 及其同形来路）。
/// </summary>
/// <remarks>
/// <para>
/// 终结走 <c>PickupStopTermination</c>；旅程还带着别的需求时不收尾，而阶段原来一直留在 <c>AwaitingLoadResult</c>。这一站已经没有在装的、
/// 也没有装上的，下一轮起每轮抛 <c>waits for a load result with no demand loading at its stop.</c>——cs#487 之后不再拖停别的车，
/// 但这辆车自己永远不走，车上第一站的货也永远不卸。
/// </para>
/// <para>
/// 两道修：确定的装货失败在终结的同一次保存里把阶段带走（<c>TrySettleDeterminateLoadFailureAsync</c>，新发生的不再进卡住状态）；
/// 等装货结果那一段认出「没有在装、没有装上、本站有一条 TERMINATED」并接着走（兜住不带走阶段的来路——恢复协调器的装货途中取消——
/// 以及已经卡在这个状态的库）。
/// </para>
/// </remarks>
public sealed class LoadEndedAtASecondPickupTests
{
    private const string ThirdSubmissionId = "20000000-0000-4000-8000-000000000021";

    /// <summary>
    /// S2：确定的装货失败（ADR-cross-0058 决定 5）终结了第二个取货停靠上唯一那条。防御路径：v2 车载端过了期限不报这种 FAILED
    /// （8005-agv-program#55），协议允许别的车载端版本报，L1 是它唯一的证明。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task ADeterminateLoadFailureEndingTheOnlyDemandOfASecondPickupSendsTheVehicleOnWithWhatItCarries()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        JourneyStopRow secondPickup = await ArriveAtTheSecondPickupAsync(fixture);
        await LoadTheSecondDemandAndFailItDeterminatelyAsync(fixture);

        Exception? settling = await RunRoundAsync(fixture);
        Assert.Equal(DemandExecutionStatus.Cancelled, (await DemandAsync(fixture, SecondDemandId)).Status);
        Exception? following = await RunRoundAsync(fixture);

        await AssertTheVehicleWasAskedToLeaveAsync(fixture, secondPickup, settling, following);
    }

    /// <summary>
    /// 同一个形状的第二条来路：操作员在第二个取货站<b>装货途中</b>按「取消装货」，车证明仓位全空，取消结果经恢复协调器终结这一条。
    /// 授权取消不阻塞旅程，协调器也不动阶段，所以终结之后阶段同样留在 <c>AwaitingLoadResult</c>。这一条是 v2 车载端真会走的路。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task ACancellationWhileLoadingTheOnlyDemandOfASecondPickupSendsTheVehicleOnWithWhatItCarries()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        JourneyStopRow secondPickup = await ArriveAtTheSecondPickupAsync(fixture);
        await EnterSublotAsync(fixture, SecondDemandId, SecondSublot, SecondSubmissionId);
        await CancelTheSecondDemandWhileLoadingAsync(fixture);

        Exception? first = await RunRoundAsync(fixture);
        Exception? second = await RunRoundAsync(fixture);

        await AssertTheVehicleWasAskedToLeaveAsync(fixture, secondPickup, first, second);
    }

    /// <summary>
    /// 期限已过才取消：本站一条都没装上就结束，没有可纠正的放置，离站等待不重新起算——车当场被请离，不再等一整个期限。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task ACancellationWhileLoadingPastTheDeadlineDoesNotRestartTheDepartureWait()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        JourneyStopRow secondPickup = await ArriveAtTheSecondPickupAsync(fixture);
        fixture.Options.StationDepartureWaitTimeout = TimeSpan.FromSeconds(10);
        await EnterSublotAsync(fixture, SecondDemandId, SecondSublot, SecondSubmissionId);
        await fixture.ProveSlotDoorsClosedAsync();
        fixture.Clock.Advance(TimeSpan.FromSeconds(11));
        await CancelTheSecondDemandWhileLoadingAsync(fixture);

        Exception? first = await RunRoundAsync(fixture);
        Exception? second = await RunRoundAsync(fixture);

        await AssertTheVehicleWasAskedToLeaveAsync(fixture, secondPickup, first, second);
    }

    /// <summary>
    /// 库里已经卡在这个状态（修复之前留下的）：阶段 <c>AwaitingLoadResult</c>，本站那一条已 <c>TERMINATED</c>、需求已取消，没有在装的、
    /// 没有装上的。升级之后下一轮就续上。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task AJourneyAlreadyStuckWithItsOnlyDemandHereTerminatedLeavesOnTheNextRound()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        JourneyStopRow secondPickup = await ArriveAtTheSecondPickupAsync(fixture);
        await EnterSublotAsync(fixture, SecondDemandId, SecondSublot, SecondSubmissionId);

        await using (ControlServerDbContext context = fixture.OpenConnectionContext())
        {
            (await context.Set<JourneyDemandRow>().SingleAsync(row => row.DemandId == SecondDemandId, token)).Status =
                JourneyDemandStatuses.Terminated;
            (await context.AcceptedDemands.SingleAsync(row => row.DemandId == SecondDemandId, token)).Status =
                DemandExecutionStatus.Cancelled;
            (await context.StationOperations.SingleAsync(
                row => row.DemandId == SecondDemandId && row.OperationType == SlotOperationType.Load, token)).Status =
                StationOperationStatus.Cancelled;
            await context.SaveChangesAsync(token);
        }
        fixture.Context.ChangeTracker.Clear();
        await fixture.RecreateEngineAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingLoadResult, (await JourneyOfAsync(fixture, FirstDemandId)).Stage);

        Exception? first = await RunRoundAsync(fixture);
        Exception? second = await RunRoundAsync(fixture);

        await AssertTheVehicleWasAskedToLeaveAsync(fixture, secondPickup, first, second);
    }

    /// <summary>
    /// control-server#906：等装货结果，而本站的需求一条都解释不了——没有在装的、没有装上的、没有被终结的、也没有装货途中被放回的
    /// （这里把在装的那一条直接改库成「待装」，模拟说不清来历的库）。修之前每轮抛 <c>waits for a load result with no demand loading at its stop</c>、
    /// 看板只有笼统的 <c>JOURNEY_ADVANCE_FAILED</c>；修之后不抛，旅程停在原处写 <c>LOAD_RESULT_WITH_NOTHING_LOADING</c>，Error 只记一次、
    /// 列出本站每条需求的状态，不发任何报文；人核对之后改好库，下一轮码清掉、照常往下走。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task AStopWaitingForALoadResultThatNothingExplainsIsHeldUnderItsOwnReasonUntilTheDatabaseIsPutRight()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        await ArriveAtTheSecondPickupAsync(fixture);
        await EnterSublotAsync(fixture, SecondDemandId, SecondSublot, SecondSubmissionId);
        await SetSecondMembershipStatusAsync(fixture, JourneyDemandStatuses.PendingLoad);
        int outboxBefore = await fixture.Context.ProtocolOutbox.AsNoTracking().CountAsync(token);
        int logBefore = fixture.EngineLog.Entries.Count;

        await fixture.RecreateEngineAsync();
        Exception? first = await RunRoundAsync(fixture);
        JourneyRuntimeRow held = await JourneyOfAsync(fixture, FirstDemandId);
        await fixture.RecreateEngineAsync();
        Exception? second = await RunRoundAsync(fixture);
        JourneyRuntimeRow stillHeld = await JourneyOfAsync(fixture, FirstDemandId);

        Assert.True(first is null && second is null, $"A round failed: {first?.Message ?? second?.Message}");
        Assert.Equal(
            (JourneyRuntimeStage.AwaitingLoadResult, "LOAD_RESULT_WITH_NOTHING_LOADING"),
            (held.Stage, held.BlockReasonCode));
        Assert.Equal(
            (JourneyRuntimeStage.AwaitingLoadResult, "LOAD_RESULT_WITH_NOTHING_LOADING", held.BlockReasonSince),
            (stillHeld.Stage, stillHeld.BlockReasonCode, stillHeld.BlockReasonSince));
        (LogLevel Level, string Message)[] logSince = [.. fixture.EngineLog.Entries.Skip(logBefore)];
        string logged = Assert.Single(
            logSince.Where(entry => entry.Level >= LogLevel.Error),
            entry => entry.Message.Contains("control-server#906", StringComparison.Ordinal)).Message;
        Assert.Contains(SecondDemandId, logged, StringComparison.Ordinal);
        Assert.Contains(JourneyDemandStatuses.PendingLoad, logged, StringComparison.Ordinal);
        string[] otherErrors = [.. logSince
            .Where(entry => entry.Level >= LogLevel.Error && !entry.Message.Contains("control-server#906", StringComparison.Ordinal))
            .Select(entry => entry.Message)];
        Assert.True(otherErrors.Length == 0, string.Join(Environment.NewLine, otherErrors));
        Assert.Equal(outboxBefore, await fixture.Context.ProtocolOutbox.AsNoTracking().CountAsync(token));
        Assert.Equal(StationOperationStatus.Prepared, (await SecondLoadAsync(fixture)).Status);

        // 人核对过车上与库之后改回去：下一轮码清掉，照常等这一条的装货结果。
        await SetSecondMembershipStatusAsync(fixture, JourneyDemandStatuses.Loading);
        await fixture.RecreateEngineAsync();
        Exception? repaired = await RunRoundAsync(fixture);
        JourneyRuntimeRow resumed = await JourneyOfAsync(fixture, FirstDemandId);
        Assert.True(repaired is null, $"The round after the repair failed: {repaired?.Message}");
        Assert.Equal((JourneyRuntimeStage.AwaitingLoadResult, (string?)null), (resumed.Stage, resumed.BlockReasonCode));
    }

    /// <summary>
    /// control-server#906：停在 <c>LOAD_RESULT_WITH_NOTHING_LOADING</c> 上的旅程不接途中追加。它在等人核对、改库；追加会给同一趟旅程重新编号、
    /// 发新计划，改的正是要人去核的那份数据。同一个夹具、同样两条需求，<c>MultiVehicleExecutionTests.AnInTransitVehicleTakesAnAppendedDemandIntoItsExistingJourney</c>
    /// 那一条追加进来了（判别力，照 <c>JourneyStopSequenceRaceTests.AJourneyWhoseStopSequencesAreBrokenTakesNoAppendedDemand</c> 的写法）。
    /// </summary>
    /// <remarks>
    /// 引擎不把它放进可追加的车（underWay），派车轮读在途计划时也不给它计划：两道各自独立，单拆一道另一道兜着，所以变异要两道一起拆。
    /// 「说不清」的形状直接改库造：阶段改成等装货结果，而取货停靠上那一条还是待装。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task AJourneyHeldOnALoadResultNothingExplainsTakesNoAppendedDemand()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using MultiVehicleExecutionTests.FleetFixture fixture = await MultiVehicleExecutionTests.FleetFixture.CreateAsync(
            configure: options => options.Fleet = options.Fleet[..1], withRouteGraph: true);
        await fixture.AllowEnRouteAppendAsync(1_000_000);
        fixture.Catalog.Set([MultiVehicleExecutionTests.FleetFixture.Demand(0, "N1-1", 0)]);
        await fixture.RunRoundAsync();
        JourneyRuntimeRow journey = await fixture.JourneyOfAsync(MultiVehicleExecutionTests.FleetFixture.AgvIds[0]);
        (await fixture.Context.JourneyRuntimes.SingleAsync(row => row.JourneyId == journey.JourneyId, token)).Stage =
            JourneyRuntimeStage.AwaitingLoadResult;
        await fixture.Context.SaveChangesAsync(token);
        fixture.Context.ChangeTracker.Clear();

        fixture.Catalog.Set(
        [
            MultiVehicleExecutionTests.FleetFixture.Demand(0, "N1-1", 0),
            MultiVehicleExecutionTests.FleetFixture.Demand(1, "N1-2", 1)
        ]);
        await fixture.RunRoundAsync(TimeSpan.FromSeconds(1));

        JourneyRuntimeRow only = Assert.Single(await fixture.Context.JourneyRuntimes.AsNoTracking().ToArrayAsync(token));
        Assert.Equal(
            (JourneyRuntimeStage.AwaitingLoadResult, "LOAD_RESULT_WITH_NOTHING_LOADING"),
            (only.Stage, only.BlockReasonCode));
        JourneyDemandRow membership = Assert.Single(
            await fixture.Context.Set<JourneyDemandRow>().AsNoTracking().ToArrayAsync(token));
        Assert.Equal(MultiVehicleExecutionTests.FleetFixture.Demand(0, "N1-1", 0).DemandId, membership.DemandId);
    }

    /// <summary>
    /// control-server#906（PR #907 审查 M1）：挂在 <c>LOAD_RESULT_WITH_NOTHING_LOADING</c> 上时车载会话掉成不就绪一轮、再恢复，码不被闸门改写成会话码，
    /// 开始时间不变，恢复之后也不多记一条 2348。这是另外三道排除（引擎不放进可追加的车、派车轮不读在途计划、释放服务跳过）共同的前提：
    /// 它们都按码认这趟旅程，码一被改写，掉线这一轮里三道都不起作用，恢复时又从头计时、重记一次。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task AHeldLoadWaitKeepsItsReasonAndItsStartThroughASessionDrop()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        await ArriveAtTheSecondPickupAsync(fixture);
        await EnterSublotAsync(fixture, SecondDemandId, SecondSublot, SecondSubmissionId);
        await SetSecondMembershipStatusAsync(fixture, JourneyDemandStatuses.PendingLoad);
        int logBefore = fixture.EngineLog.Entries.Count;
        await fixture.RecreateEngineAsync();
        Assert.Null(await RunRoundAsync(fixture));
        JourneyRuntimeRow held = await JourneyOfAsync(fixture, FirstDemandId);
        Assert.Equal("LOAD_RESULT_WITH_NOTHING_LOADING", held.BlockReasonCode);

        // 会话掉成不就绪（与 OwnOrderRebuildTests 同一个做法），之后原样恢复。
        SessionRecoveryRow ready = await fixture.Context.SessionRecoveries.AsNoTracking().SingleAsync(token);
        await OwnOrderRebuildTests.DropSessionOnOwnOrderAsync(fixture);
        await fixture.RecreateEngineAsync();
        Exception? dropped = await RunRoundAsync(fixture);
        JourneyRuntimeRow duringDrop = await JourneyOfAsync(fixture, FirstDemandId);

        SessionRecoveryRow session = await fixture.Context.SessionRecoveries.SingleAsync(token);
        session.Readiness = ready.Readiness;
        session.ReasonCode = ready.ReasonCode;
        session.DepartureSafe = ready.DepartureSafe;
        session.SafetyReasonCodesJson = ready.SafetyReasonCodesJson;
        session.SafetyUnknownPresent = ready.SafetyUnknownPresent;
        session.UpdatedAt = fixture.Clock.GetUtcNow();
        await fixture.Context.SaveChangesAsync(token);
        fixture.Context.ChangeTracker.Clear();
        await fixture.HearFromPeerAsync();
        await fixture.RecreateEngineAsync();
        Exception? restored = await RunRoundAsync(fixture);
        JourneyRuntimeRow afterRestore = await JourneyOfAsync(fixture, FirstDemandId);

        Assert.True(dropped is null && restored is null, $"A round failed: {dropped?.Message ?? restored?.Message}");
        Assert.Equal(
            ("LOAD_RESULT_WITH_NOTHING_LOADING", held.BlockReasonSince),
            (duringDrop.BlockReasonCode, duringDrop.BlockReasonSince));
        Assert.Equal(
            (JourneyRuntimeStage.AwaitingLoadResult, "LOAD_RESULT_WITH_NOTHING_LOADING", held.BlockReasonSince),
            (afterRestore.Stage, afterRestore.BlockReasonCode, afterRestore.BlockReasonSince));
        Assert.Single(
            fixture.EngineLog.Entries.Skip(logBefore),
            entry => entry.Level >= LogLevel.Error && entry.Message.Contains("control-server#906", StringComparison.Ordinal));
    }

    private static async Task SetSecondMembershipStatusAsync(RuntimeFixture fixture, string status)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using (ControlServerDbContext context = fixture.OpenConnectionContext())
        {
            (await context.Set<JourneyDemandRow>().SingleAsync(row => row.DemandId == SecondDemandId, token)).Status = status;
            await context.SaveChangesAsync(token);
        }
        fixture.Context.ChangeTracker.Clear();
    }

    /// <summary>
    /// 装货途中取消了第二个取货站上的一条，同站还有一条没装：回到等录入，发出下一版清单与录入请求，第三条照常录入、下装货命令。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task ACancellationWhileLoadingOneOfTwoDemandsOfASecondPickupGoesBackToTheEntryForTheOther()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        await ArriveAtTheSecondPickupAsync(fixture, thirdAtTheSecondPickup: true);
        await EnterSublotAsync(fixture, SecondDemandId, SecondSublot, SecondSubmissionId);
        await CancelTheSecondDemandWhileLoadingAsync(fixture);

        Exception? round = await RunRoundAsync(fixture);
        JourneyRuntimeRow after = await JourneyOfAsync(fixture, FirstDemandId);
        Assert.True(
            after.Stage == JourneyRuntimeStage.AwaitingSublot,
            $"The stop still has a demand to load, so the journey should be back at the entry. stage={after.Stage} round={round?.Message}");

        await EnterSublotAsync(fixture, FirstDemandId, ThirdSublot, ThirdSubmissionId);
        Assert.Equal(JourneyRuntimeStage.AwaitingLoadResult, (await JourneyOfAsync(fixture, FirstDemandId)).Stage);
        Assert.Equal(JourneyDemandStatuses.Loading, (await MembershipAsync(fixture, ThirdDemandId)).Status);
    }

    /// <summary>
    /// 确定的装货失败终结了第二个取货站上的一条，同站还有一条没装：阶段在同一次保存里回到等录入（期限已过），随后由站点期限的出口
    /// 结束本站——终结剩下那一条、车带着第一站的货离站。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task ADeterminateLoadFailureOfOneOfTwoDemandsOfASecondPickupHandsTheStopToItsDeadline()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        // control-server#703：这条测的是站点期限的终结尾巴，那条路今天只在放回次数用完时走（终结并告警）。上限设 0，让第一次期限就走它；放回那一支在 StationTimeoutRedispatchTests。
        fixture.Options.StationTimeoutRedispatchLimit = 0;
        JourneyStopRow secondPickup = await ArriveAtTheSecondPickupAsync(fixture, thirdAtTheSecondPickup: true);
        await LoadTheSecondDemandAndFailItDeterminatelyAsync(fixture);

        Exception? settling = await RunRoundAsync(fixture);
        JourneyRuntimeRow settled = await JourneyOfAsync(fixture, FirstDemandId);
        Assert.True(
            settled.Stage == JourneyRuntimeStage.AwaitingSublot,
            $"The stop still has a demand to load, so the settlement should hand it back to the entry. stage={settled.Stage} round={settling?.Message}");

        await fixture.ProveSlotDoorsClosedAsync();
        await fixture.HearFromPeerAsync();
        Exception? ending = await RunRoundAsync(fixture);
        await fixture.HearFromPeerAsync();
        Exception? leaving = await RunRoundAsync(fixture);

        Assert.Equal(JourneyDemandStatuses.Terminated, (await MembershipAsync(fixture, ThirdDemandId)).Status);
        await AssertTheVehicleWasAskedToLeaveAsync(fixture, secondPickup, ending, leaving);
    }

    /// <summary>第二条在第二个取货站录入、下装货命令，过了十秒期限之后车报确定的失败（首仓 FAILED/OPERATOR_TIMEOUT，其余 NOT_STARTED）。</summary>
    private static async Task LoadTheSecondDemandAndFailItDeterminatelyAsync(RuntimeFixture fixture)
    {
        // 期限从到站那一轮起算；放在到站之后设，第一站不受它影响。
        fixture.Options.StationDepartureWaitTimeout = TimeSpan.FromSeconds(10);
        await EnterSublotAsync(fixture, SecondDemandId, SecondSublot, SecondSubmissionId);
        Assert.Equal(JourneyRuntimeStage.AwaitingLoadResult, (await JourneyOfAsync(fixture, FirstDemandId)).Stage);

        fixture.Clock.Advance(TimeSpan.FromSeconds(11));
        await ReportLoadResultAsync(
            fixture, DeterminateFailureSlots(fixture, "OPERATOR_TIMEOUT", SecondDemandId), SecondDemandId);
        Assert.Equal(StationOperationStatus.Failed, (await SecondLoadAsync(fixture)).Status);
    }

    /// <summary>车在装第二条时操作员按「取消装货」，授权之后车报仓位全空。经入站处理器，像真连接那样。</summary>
    internal static async Task CancelTheSecondDemandWhileLoadingAsync(RuntimeFixture fixture)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        Assert.Equal(JourneyRuntimeStage.AwaitingLoadResult, (await JourneyOfAsync(fixture, FirstDemandId)).Stage);
        StationOperationRow load = await SecondLoadAsync(fixture);
        int[] slots = JsonSerializer.Deserialize<int[]>(load.TargetSlotsJson) ?? [];
        // Nothing loaded and every door shut since the second load began (control-server#837).
        await TestSlotReadings.SeedProvenEmptyAsync(fixture.Context, fixture.Options.AgvId, fixture.Clock.GetUtcNow());
        await using (ControlServerDbContext connection = fixture.OpenConnectionContext())
        {
            OnboardMessageProcessor processor = TestOnboardProcessorFactory.Create(
                connection, new WireToGateStore(connection), fixture.Clock, new ConfigurationBuilder().Build(), fixture.Peer);
            OnboardConnectionState state = Connection(fixture);
            const string cancellationId = "d3000000-0000-4000-8000-000000000001";
            string authorization = await processor.ProcessAsync(
                Envelope(fixture, "d3000000-0000-4000-8000-000000000002", "LoadCancellationStartRequested", new
                {
                    cancellationId,
                    demandId = SecondDemandId,
                    slotOperationAttemptId = load.SlotOperationAttemptId,
                    @operator = new { operatorId = "OP-001", verificationMethod = "BADGE", verifiedAt = fixture.Clock.GetUtcNow() },
                    reason = "Nothing to load at this stop."
                }),
                state,
                token);
            Assert.Equal("AUTHORIZED", FirstLinePayload(authorization).GetProperty("decision").GetString());
            await processor.ProcessAsync(
                Envelope(fixture, "d3000000-0000-4000-8000-000000000003", "LoadCancellationResult", new
                {
                    cancellationId,
                    demandId = SecondDemandId,
                    slotOperationAttemptId = load.SlotOperationAttemptId,
                    overallOutcome = "ALL_EMPTY",
                    slotResults = slots.Select(slot => new
                    {
                        slotNo = slot,
                        outcome = "COMPLETED",
                        finalPhysicalState = "EMPTY",
                        lockState = "LOCKED",
                        unlockOutputState = "RESET",
                        reasonCodes = Array.Empty<string>()
                    }).ToArray(),
                    observedAt = fixture.Clock.GetUtcNow()
                }),
                state,
                token);
        }
        fixture.Context.ChangeTracker.Clear();
        // 前提：第二条被取消终结，旅程因还带着第一条而继续，阶段仍是等装货结果。
        Assert.Equal(JourneyDemandStatuses.Terminated, (await MembershipAsync(fixture, SecondDemandId)).Status);
        Assert.Equal(JourneyRuntimeStage.AwaitingLoadResult, (await JourneyOfAsync(fixture, FirstDemandId)).Stage);
    }

    /// <summary>判据与 <c>AfterTheStationDeadlineEndsASecondPickupTheVehicleLeavesWithWhatItCarries</c> 相同：这个停靠发出了离站核验，第一条仍在车上。</summary>
    private static async Task AssertTheVehicleWasAskedToLeaveAsync(
        RuntimeFixture fixture, JourneyStopRow secondPickup, Exception? first, Exception? second)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        JourneyRuntimeRow after = await JourneyOfAsync(fixture, FirstDemandId);
        Assert.True(
            secondPickup.DepartureSafetyCheckMessageId is { } checkId &&
            await fixture.Context.ProtocolOutbox.AsNoTracking().AnyAsync(row => row.MessageId == checkId, token),
            $"The stop ended and the vehicle was never asked to leave it. stage={after.Stage} block={after.BlockReasonCode} " +
            $"first={first?.Message} second={second?.Message}");
        Assert.Equal(JourneyDemandStatuses.Loaded, (await MembershipAsync(fixture, FirstDemandId)).Status);
    }

    private static Task<StationOperationRow> SecondLoadAsync(RuntimeFixture fixture) =>
        fixture.Context.StationOperations.AsNoTracking().SingleAsync(
            row => row.DemandId == SecondDemandId && row.OperationType == SlotOperationType.Load,
            TestContext.Current.CancellationToken);

    private static Task<AcceptedDemandRow> DemandAsync(RuntimeFixture fixture, string demandId) =>
        fixture.Context.AcceptedDemands.AsNoTracking().SingleAsync(
            row => row.DemandId == demandId, TestContext.Current.CancellationToken);

    private static async Task<Exception?> RunRoundAsync(RuntimeFixture fixture)
    {
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        return await Record.ExceptionAsync(() => fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken));
    }
}
