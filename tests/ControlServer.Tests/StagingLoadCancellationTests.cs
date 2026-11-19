using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.TaskTypeStations;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;
using static ControlServer.Tests.StagingPickupNoCountdownTests;

namespace ControlServer.Tests;

/// <summary>
/// 待送点取货停靠上的两种取消：装货中取消放回待派、不抑制（control-server#713 票正文第 5 条，CP-0012）；扫码前取消照机台终结并抑制
/// （control-server#726，CP-0014 修订项二，取代 cs#713 第 6 条的拒绝）。机台取货与开关关着时两样都照旧（验收 7 的回归）。
/// </summary>
public sealed class StagingLoadCancellationTests
{
    private const string CancellationId = "71300000-0000-4000-8000-000000000001";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// 验收 6：待送点上装货中取消、车证明仓位全空：需求放回积压（仍 Accepted、不抑制、不计站点超时次数），一篮没装，旅程原地收尾、
    /// 收尾清单说 <c>LOAD_CANCELLED</c>。另两种照旧终结为 <c>CANCELLED_BY_OPERATOR</c> 并抑制。把放回改回终结，待送点那一种红。
    /// </summary>
    [Theory]
    [MemberData(nameof(Kinds), MemberType = typeof(StagingPickupNoCountdownTests))]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("IntegrationSlice", "FP-IS-11")]
    public async Task ACancellationDuringTheLoadReleasesTheDemandOnlyAtTheStagingStation(StopKind kind)
    {
        await using RuntimeFixture fixture = await ArrangeAsync(kind);
        await fixture.AdvanceToLoadResultAsync();
        StationOperationRow load = await fixture.OperationAsync(SlotOperationType.Load);

        await CancelDuringTheLoadAsync(fixture, Demand, load);

        fixture.Context.ChangeTracker.Clear();
        AcceptedDemandRow demand = await fixture.DemandRowAsync();
        JourneyRuntimeRow journey = await fixture.Context.JourneyRuntimes.AsNoTracking().SingleAsync(Token);
        Assert.Equal(JourneyRuntimeStage.Completed, journey.Stage);
        if (kind == StopKind.StagingRulesOn)
        {
            Assert.Equal(DemandExecutionStatus.Accepted, demand.Status);
            JourneyDemandRow membership = await fixture.Context.Set<JourneyDemandRow>().AsNoTracking()
                .SingleAsync(row => row.DemandId == Demand, Token);
            Assert.Equal(DemandJourneyLookup.ReleasedForRedispatchReason, membership.RemovalReason);
            Assert.True(
                await DemandJourneyLookup.ReleasedForRedispatch(fixture.Context).AnyAsync(row => row.DemandId == Demand, Token));
            await SuppressionAssertions.AssertNothingSuppressedAsync(fixture.Context);
            Assert.Equal(CurrentStopDepartures.LoadCancelledReleasedReason, journey.BlockReasonCode);
            Assert.Contains("LOAD_CANCELLED", await StagingDepartureRequestTests.StopEndedReasonsSentAsync(fixture));
        }
        else
        {
            Assert.Equal(DemandExecutionStatus.Cancelled, demand.Status);
            await SuppressionAssertions.AssertSuppressedAsync(fixture.Context, Demand, "CANCELLED_BY_OPERATOR");
        }
        Assert.Equal(StationOperationStatus.Cancelled, (await fixture.OperationAsync(SlotOperationType.Load)).Status);
    }

    /// <summary>
    /// 验收 6 的另一格：同站追加之后第 1 个停靠已装上 A，车在第 2 个停靠装 B 时取消，车证明 B 的仓位全空。B 放回待派、不抑制，A 还在车上，
    /// 旅程不收尾；引擎接着推进不抛（「装货途中被结束之后的续跑」认得被放回的那一条），本站结束后车停在离站等待里。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task ACancellationDuringASecondStagingLoadKeepsTheFirstOnBoardAndTheJourneyGoing()
    {
        await using RuntimeFixture fixture = await ArrangeAsync(StopKind.StagingRulesOn);
        await AllowEnRouteAppendAsync(fixture);
        await LoadTheBasketAsync(fixture);
        await AppendStagingDemandAsync(fixture, SecondDemand, SecondSublot);
        fixture.Clock.Advance(fixture.Options.SameStationAppendCorrectionGrace + TimeSpan.FromSeconds(1));
        await fixture.ProveSlotDoorsClosedAsync();
        await fixture.HearFromPeerAsync();
        await fixture.Engine.ExecuteOnceAsync(Token);
        await Batch7StopDrivenAdvanceDriver.AnswerDepartureSafetyAsync(
            fixture, Demand, Batch7StopDrivenAdvanceDriver.FirstSafetyResultId);
        await Batch7StopDrivenAdvanceDriver.ArriveAtCurrentStopAsync(fixture, SecondDemand, "TO_GATE");
        // 夹具追加时预留两篮；录入时按 BR-013 由盒数重算篮数（每篮四盒），所以子批要读出 8 盒，否则录入被拒。
        fixture.BoxCounts.Set(SecondSublot, 8);
        await SubmitAtCurrentStopAsync(fixture, SecondSublot);
        await fixture.ProveSlotDoorsClosedAsync();
        await fixture.HearFromPeerAsync();
        await fixture.Engine.ExecuteOnceAsync(Token);
        Assert.Equal(JourneyRuntimeStage.AwaitingLoadResult, (await fixture.RuntimeAsync()).Stage);
        StationOperationRow load = await fixture.Context.StationOperations.AsNoTracking()
            .SingleAsync(row => row.DemandId == SecondDemand && row.OperationType == SlotOperationType.Load, Token);

        await CancelDuringTheLoadAsync(fixture, SecondDemand, load);
        fixture.Context.ChangeTracker.Clear();
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        await fixture.HearFromPeerAsync();
        await fixture.Engine.ExecuteOnceAsync(Token);

        JourneyDemandRow released = await fixture.Context.Set<JourneyDemandRow>().AsNoTracking()
            .SingleAsync(row => row.DemandId == SecondDemand, Token);
        Assert.Equal(DemandJourneyLookup.ReleasedForRedispatchReason, released.RemovalReason);
        await SuppressionAssertions.AssertNothingSuppressedAsync(fixture.Context);
        JourneyDemandRow first = await fixture.Context.Set<JourneyDemandRow>().AsNoTracking()
            .SingleAsync(row => row.DemandId == Demand, Token);
        Assert.Equal((JourneyDemandStatuses.Loaded, (DateTimeOffset?)null), (first.Status, first.RemovedAt));
        JourneyRuntimeRow journey = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingStationDeparture, journey.Stage);
        Assert.DoesNotContain(fixture.EngineLog.Entries, entry => entry.Level >= Microsoft.Extensions.Logging.LogLevel.Error);
        Assert.Contains("LOAD_CANCELLED", await StagingDepartureRequestTests.StopEndedReasonsSentAsync(fixture));
    }

    /// <summary>
    /// control-server#904（11-19 agv02 在 217 卡约两小时的形状）：同上一格，但取消之后每一轮都是一个新引擎——生产里引擎按轮取自新的 scope
    /// （<c>JourneyRuntimeWorker</c>），不带上一轮认过的待送点停靠。第 2 个停靠上唯一的归属已被放回、移除，按未移除的归属认不出它是待送点，
    /// #713「装货中取消、按释放放回」的续跑接不住，每轮抛 <c>waits for a load result with no demand loading at its stop</c>、
    /// <c>JOURNEY_ADVANCE_FAILED</c>。修好之后：本站收尾、持货等单，持货期限到了带着 A 离站去卸，停靠序号不变、计划与清单的号不倒退。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task ACancellationDuringAnAppendedStagingLoadIsCarriedThroughWithAFreshEngineEachRound()
    {
        await using RuntimeFixture fixture = await ArrangeAsync(StopKind.StagingRulesOn);
        await AllowEnRouteAppendAsync(fixture);
        await LoadTheBasketAsync(fixture);
        await AppendStagingDemandAsync(fixture, SecondDemand, SecondSublot);
        await MoveToTheAppendedStopAsync(fixture, SecondDemand, Batch7StopDrivenAdvanceDriver.FirstSafetyResultId);
        StationOperationRow load = await StartLoadAtCurrentStopAsync(fixture, SecondDemand, SecondSublot);

        await AssertCancellationCarriedThroughAsync(fixture, [Demand], SecondDemand, load);
    }

    /// <summary>
    /// control-server#904 的变体：同站追加两次。第 2 个停靠上的 B 装上了，车还在待送点时 C 又追加、另开第 3 个停靠（追加不并入当前停靠），
    /// C 装货中取消、放回。第 3 个停靠与上一格同一个形状，取消之后每轮新引擎照样续上，带着 A、B 离站。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task ACancellationDuringASecondSameStationAppendIsCarriedThroughWithAFreshEngineEachRound()
    {
        await using RuntimeFixture fixture = await ArrangeAsync(StopKind.StagingRulesOn);
        await AllowEnRouteAppendAsync(fixture);
        await LoadTheBasketAsync(fixture);
        await AppendStagingDemandAsync(fixture, SecondDemand, SecondSublot);
        await MoveToTheAppendedStopAsync(fixture, SecondDemand, Batch7StopDrivenAdvanceDriver.FirstSafetyResultId);
        StationOperationRow second = await StartLoadAtCurrentStopAsync(fixture, SecondDemand, SecondSublot);
        await fixture.ApplySafeResultAsync(second, SlotOperationType.Load, SlotBusinessState.Occupied);
        await RoundAsync(fixture);
        Assert.Equal(JourneyDemandStatuses.Loaded, (await Batch7MultiDemandAdvanceTests.MembershipAsync(fixture, SecondDemand)).Status);
        string secondStopId = (await Batch7MultiDemandAdvanceTests.MembershipAsync(fixture, SecondDemand)).PickupStopId;

        await AppendStagingDemandAsync(fixture, ThirdDemand, ThirdSublot);
        await MoveToTheAppendedStopAsync(fixture, ThirdDemand, Batch7StopDrivenAdvanceDriver.SecondSafetyResultId);
        Assert.NotEqual(secondStopId, (await Batch7MultiDemandAdvanceTests.MembershipAsync(fixture, ThirdDemand)).PickupStopId);
        StationOperationRow third = await StartLoadAtCurrentStopAsync(fixture, ThirdDemand, ThirdSublot);

        await AssertCancellationCarriedThroughAsync(fixture, [Demand, SecondDemand], ThirdDemand, third);
    }

    /// <summary>
    /// control-server#906（cs#904 审查 L1 的窄路径）：同 cs#904 那一格，但车还停在清空的追加停靠上时，导入了新的绑定集、把 STAGING_TO_WIRE 的
    /// 固定站改到别处，被放回的那条又被另一辆车再派受理。再派受理删掉旧冻结、按新版本重冻（<c>ThawForRedispatchAsync</c>），这个停靠按冻结
    /// 再也认不出是待送点——cs#904 的识别修法到这里失效，每轮新引擎又抛 <c>waits for a load result with no demand loading at its stop</c>。
    /// 修好之后不再依赖认待送点：本停靠上有「已移除、仍是 LOADING」的归属，就按装货途中被结束续上，车带着 A 离站去卸。
    /// </summary>
    /// <remarks>
    /// 认不出待送点之后，这个停靠按机台取货走离站等待（站点期限，不持货等单）；它上面已经一条需求都没有，两种走法都是等到期限离站，
    /// 所以这里断言的是「到期限前离站核验发出、答复后离站」，不断言走的是哪一种期限。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task ACancelledAppendedStagingLoadRedispatchedUnderAMovedBindingIsCarriedThroughWithAFreshEngineEachRound()
    {
        await using RuntimeFixture fixture = await ArrangeAsync(StopKind.StagingRulesOn);
        await AllowEnRouteAppendAsync(fixture);
        await LoadTheBasketAsync(fixture);
        await AppendStagingDemandAsync(fixture, SecondDemand, SecondSublot);
        await MoveToTheAppendedStopAsync(fixture, SecondDemand, Batch7StopDrivenAdvanceDriver.FirstSafetyResultId);
        StationOperationRow load = await StartLoadAtCurrentStopAsync(fixture, SecondDemand, SecondSublot);

        await AssertCancellationCarriedThroughAsync(
            fixture,
            [Demand],
            SecondDemand,
            load,
            whileReleased: async () =>
            {
                long movedBindings = await MoveTheStagingBindingAsync(fixture);
                await RedispatchToAnotherVehicleAsync(fixture, SecondDemand, SecondSublot, movedBindings);
            });
    }

    /// <summary>
    /// C11 的裁定（control-server#713 第 4 条）：待送点上锚需求装货中取消、旅程后面还有一条没开始装的，按一篮没装处理——旅程原地收尾，
    /// 锚与那一条一起放回待派、不抑制。只放回锚不行：旅程开着时锚会被改派到别的车（<c>FaultedCargoBindings</c> 的前提）；裁定之前这一支
    /// 照旧终结锚（取消并抑制），删掉收尾里对另一条的放回，这条红。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("IntegrationSlice", "FP-IS-11")]
    public async Task CancellingTheAnchorDuringItsLoadClosesTheJourneyAndReleasesEverything()
    {
        await using RuntimeFixture fixture = await ArrangeAsync(StopKind.StagingRulesOn);
        await AllowEnRouteAppendAsync(fixture);
        await fixture.AdvanceToLoadResultAsync();
        await AppendStagingDemandAsync(fixture, SecondDemand, SecondSublot);
        fixture.Context.ChangeTracker.Clear();
        string journeyId = (await fixture.RuntimeAsync()).JourneyId;
        StationOperationRow load = await fixture.OperationAsync(SlotOperationType.Load);

        await CancelDuringTheLoadAsync(fixture, Demand, load);
        fixture.Context.ChangeTracker.Clear();
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        await fixture.HearFromPeerAsync();
        await fixture.Engine.ExecuteOnceAsync(Token);

        // 放回之后那一轮派车可能把两条重新受理进新旅程，所以只看原来那一趟。
        JourneyRuntimeRow journey = await fixture.Context.JourneyRuntimes.AsNoTracking()
            .SingleAsync(row => row.JourneyId == journeyId, Token);
        Assert.Equal(JourneyRuntimeStage.Completed, journey.Stage);
        Assert.Equal(CurrentStopDepartures.LoadCancelledReleasedReason, journey.BlockReasonCode);
        JourneyDemandRow[] memberships = await fixture.Context.Set<JourneyDemandRow>().AsNoTracking()
            .Where(row => row.JourneyId == journeyId)
            .ToArrayAsync(Token);
        Assert.Equal(2, memberships.Length);
        Assert.All(memberships, row => Assert.Equal(DemandJourneyLookup.ReleasedForRedispatchReason, row.RemovalReason));
        Assert.Equal(
            [DemandExecutionStatus.Accepted, DemandExecutionStatus.Accepted],
            await fixture.Context.AcceptedDemands.AsNoTracking()
                .Where(row => row.DemandId == Demand || row.DemandId == SecondDemand)
                .Select(row => row.Status)
                .ToArrayAsync(Token));
        await SuppressionAssertions.AssertNothingSuppressedAsync(fixture.Context);
        Assert.DoesNotContain(fixture.EngineLog.Entries, entry => entry.Level >= Microsoft.Extensions.Logging.LogLevel.Error);
    }

    /// <summary>
    /// CP-0014 修订项二（control-server#726）：扫码前取消在待送点取货停靠上照机台的语义授权并落定——需求以 <c>CANCELLED_BY_OPERATOR</c> 终结、
    /// 业务键永久抑制，不是放回待派。三种停靠结果相同；cs#713 时待送点这一格是拒绝（<c>ACTION_NOT_ALLOWED_IN_STATE</c>）。
    /// </summary>
    [Theory]
    [MemberData(nameof(Kinds), MemberType = typeof(StagingPickupNoCountdownTests))]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("IntegrationSlice", "FP-IS-11")]
    public async Task ACancellationBeforeAnyEntryEndsAndSuppressesTheDemandAtEveryStop(StopKind kind)
    {
        await using RuntimeFixture fixture = await ArrangeAsync(kind);
        await fixture.AdvanceToSublotWaitAsync();

        await CancelBeforeScanAsync(fixture, Demand);

        fixture.Context.ChangeTracker.Clear();
        Assert.Equal(DemandExecutionStatus.Cancelled, (await fixture.DemandRowAsync()).Status);
        await SuppressionAssertions.AssertSuppressedAsync(fixture.Context, Demand, "CANCELLED_BY_OPERATOR");
        Assert.False(
            await DemandJourneyLookup.ReleasedForRedispatch(fixture.Context).AnyAsync(row => row.DemandId == Demand, Token),
            "A cancellation before any entry put the demand back to be dispatched again instead of ending it.");
        Assert.Equal(JourneyRuntimeStage.Completed, (await fixture.RuntimeAsync()).Stage);
    }

    /// <summary>
    /// 待送点一站两条，扫码前取消乙（hmi#353 的 G2 按此写，C11 10-15 转来的约定）：乙终结并抑制；下一轮车收到去掉乙的新一版清单与<b>同号</b>的
    /// 新录入请求（cs#723 的补发路径），甲照常录入、开始装。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task CancellingOneRowBeforeScanAtTheStagingStopRefreshesTheWorklistAndTheEntryRequest()
    {
        await using RuntimeFixture fixture = await TwoDemandsAtTheStagingStopAsync();
        Sent before = await LatestAsync(fixture, "CurrentStopWorklistSnapshot");
        Assert.Equal([Demand, SecondDemand], before.Demands);

        await CancelBeforeScanAsync(fixture, SecondDemand);
        await RoundAsync(fixture);

        fixture.Context.ChangeTracker.Clear();
        Assert.Equal(DemandExecutionStatus.Cancelled, await StatusOfAsync(fixture, SecondDemand));
        await SuppressionAssertions.AssertSuppressedAsync(fixture.Context, SecondDemand, "CANCELLED_BY_OPERATOR");
        Sent worklist = await LatestAsync(fixture, "CurrentStopWorklistSnapshot");
        Assert.True(worklist.Revision > before.Revision, $"The vehicle still holds worklist revision {before.Revision}.");
        Assert.Equal([Demand], worklist.Demands);
        Sent entry = await LatestAsync(fixture, "SublotEntryRequested");
        Assert.Equal(worklist.Revision, entry.Revision);
        Assert.Equal([StagingSublot], entry.Sublots);

        JourneyRuntimeRow loading = await Batch7StopDrivenAdvanceDriver.EnterSublotAsync(
            fixture, Demand, StagingSublot, Batch7StopDrivenAdvanceDriver.FirstSubmissionId);
        Assert.Equal(JourneyRuntimeStage.AwaitingLoadResult, loading.Stage);
    }

    /// <summary>
    /// hmi#353 审查 M2 的服务端那一道：待送点一站两条，甲已装上（或正在装）、车上有它的货，服务端重启后车载端对甲发扫码前取消——拒绝，
    /// <c>ACTION_NOT_ALLOWED_IN_STATE</c>，甲不变（仍已受理、归属仍是已装或在装、不抑制、不开取消流程）。
    /// 同时钉住：甲装完之后排给车的清单只列乙（装完的项移出当前清单）。
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task ACancellationBeforeScanOfADemandWithCargoOnBoardIsRefusedAfterARestart(bool loaded)
    {
        await using RuntimeFixture fixture = await TwoDemandsAtTheStagingStopAsync();
        Assert.Equal(
            JourneyRuntimeStage.AwaitingLoadResult,
            (await Batch7StopDrivenAdvanceDriver.EnterSublotAsync(
                fixture, Demand, StagingSublot, Batch7StopDrivenAdvanceDriver.FirstSubmissionId)).Stage);
        string expected = JourneyDemandStatuses.Loading;
        if (loaded)
        {
            await Batch7StopDrivenAdvanceDriver.SettleLoadAsync(fixture, Demand);
            expected = JourneyDemandStatuses.Loaded;
            Assert.Equal([SecondDemand], (await LatestAsync(fixture, "CurrentStopWorklistSnapshot")).Demands);
        }
        Assert.Equal(expected, (await Batch7MultiDemandAdvanceTests.MembershipAsync(fixture, Demand)).Status);
        await fixture.RecreateEngineAsync();

        string answer;
        await using (ControlServerDbContext connection = fixture.OpenConnectionContext())
        {
            answer = await ProcessAsync(fixture, connection, "LoadCancellationStartRequested", BeforeScanRequest(fixture, Demand));
        }

        JsonElement payload = FirstPayload(answer);
        Assert.Equal("REJECTED", payload.GetProperty("decision").GetString());
        Assert.Equal(
            ServerReasonCodes.ActionNotAllowedInState, payload.GetProperty("problem").GetProperty("reasonCode").GetString());
        fixture.Context.ChangeTracker.Clear();
        Assert.False(await fixture.Context.RecoveryWorkflows.AnyAsync(Token));
        Assert.Equal(DemandExecutionStatus.Accepted, await StatusOfAsync(fixture, Demand));
        JourneyDemandRow membership = await Batch7MultiDemandAdvanceTests.MembershipAsync(fixture, Demand);
        Assert.Equal((expected, (DateTimeOffset?)null), (membership.Status, membership.RemovedAt));
        await SuppressionAssertions.AssertNothingSuppressedAsync(fixture.Context);
    }

    /// <summary>
    /// 与 cs#727 持货期限的关系（CP-0014 第五节第 3 处）：甲已装、期限已起，乙追加进本站后期限挪到追加时刻；之后对乙扫码前取消——乙终结，
    /// 期限不因取消重新起算（起算点仍是乙的追加时刻），车上仍有甲的货，期限也不撤。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    [Trait("IntegrationSlice", "FP-IS-11")]
    public async Task ACancellationBeforeScanDoesNotRestartTheStagingHoldingDeadline()
    {
        await using RuntimeFixture fixture = await ArrangeAsync(StopKind.StagingRulesOn);
        await AllowEnRouteAppendAsync(fixture);
        await LoadTheBasketAsync(fixture);
        fixture.Clock.Advance(TimeSpan.FromSeconds(30));
        DateTimeOffset appendedAt = fixture.Clock.GetUtcNow();
        await AppendStagingDemandAsync(fixture, SecondDemand, SecondSublot);
        fixture.Clock.Advance(fixture.Options.SameStationAppendCorrectionGrace + TimeSpan.FromSeconds(1));
        await fixture.ProveSlotDoorsClosedAsync();
        await fixture.HearFromPeerAsync();
        await fixture.Engine.ExecuteOnceAsync(Token);
        await Batch7StopDrivenAdvanceDriver.AnswerDepartureSafetyAsync(
            fixture, Demand, Batch7StopDrivenAdvanceDriver.FirstSafetyResultId);
        await Batch7StopDrivenAdvanceDriver.ArriveAtCurrentStopAsync(fixture, SecondDemand, "TO_GATE");
        await fixture.ProveSlotDoorsClosedAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, (await fixture.RuntimeAsync()).Stage);
        Assert.Equal(appendedAt, (await fixture.RuntimeAsync()).CargoHoldingStartedAt);

        fixture.Clock.Advance(TimeSpan.FromSeconds(20));
        await CancelBeforeScanAsync(fixture, SecondDemand);
        await RoundAsync(fixture);
        await RoundAsync(fixture);

        fixture.Context.ChangeTracker.Clear();
        Assert.Equal(DemandExecutionStatus.Cancelled, await StatusOfAsync(fixture, SecondDemand));
        Assert.Equal(appendedAt, (await fixture.RuntimeAsync()).CargoHoldingStartedAt);
        Assert.Equal(JourneyDemandStatuses.Loaded, (await Batch7MultiDemandAdvanceTests.MembershipAsync(fixture, Demand)).Status);
        Assert.Contains("LOAD_CANCELLED", await StagingDepartureRequestTests.StopEndedReasonsSentAsync(fixture));

        // 审查 L1：期限照追加时刻算，到了就走——装货阶段以持货到期关闭，或车已在离站核验上（两者都说明钟没被取消拨回去）。
        while (fixture.Clock.GetUtcNow() < appendedAt + fixture.Options.CargoHoldingTimeout + TimeSpan.FromSeconds(10))
        {
            fixture.Clock.Advance(TimeSpan.FromSeconds(20));
            await fixture.ProveSlotDoorsClosedAsync();
            await fixture.HearFromPeerAsync();
            await fixture.Engine.ExecuteOnceAsync(Token);
        }
        JourneyRuntimeRow expired = await fixture.RuntimeAsync();
        Assert.True(
            expired.LoadingClosedReason == LoadingClosedReasons.CargoHoldingTimeout ||
            expired.Stage == JourneyRuntimeStage.AwaitingDepartureSafety,
            $"The deadline from the append passed and the vehicle is still at {expired.Stage}, loading phase {expired.LoadingPhaseState}/{expired.LoadingClosedReason}.");
    }

    /// <summary>
    /// 审查 M1（control-server#726）：锚甲、已被扫码前取消的乙、待装的丙同在一个待送点停靠上，甲装货中撤销、仓位证明全空——按一篮没装处理，
    /// 甲与丙放回待派、旅程原地收尾；乙仍是它自己的终结（不被改写成放回）。修之前已终结的乙被算作「不是待装」，甲被终结并永久抑制。
    /// 今天追加不并进当前停靠，这个形状要等 cs#714；这里用 <c>AddDemandToJourneyAsync</c> 直接造。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("IntegrationSlice", "FP-IS-11")]
    public async Task UndoingTheAnchorsLoadAfterAnotherRowWasCancelledStillReleasesTheAnchor()
    {
        await using RuntimeFixture fixture = await TwoDemandsAtTheStagingStopAsync(withThird: true);
        string journeyId = (await fixture.RuntimeAsync(Demand)).JourneyId;
        await CancelBeforeScanAsync(fixture, SecondDemand);
        await RoundAsync(fixture);
        JourneyRuntimeRow loading = await Batch7StopDrivenAdvanceDriver.EnterSublotAsync(
            fixture, Demand, StagingSublot, Batch7StopDrivenAdvanceDriver.FirstSubmissionId);
        Assert.Equal(JourneyRuntimeStage.AwaitingLoadResult, loading.Stage);
        StationOperationRow load = await fixture.Context.StationOperations.AsNoTracking()
            .SingleAsync(row => row.DemandId == Demand && row.OperationType == SlotOperationType.Load, Token);

        await CancelDuringTheLoadAsync(fixture, Demand, load, UndoCancellationId);

        fixture.Context.ChangeTracker.Clear();
        JourneyRuntimeRow journey = await fixture.Context.JourneyRuntimes.AsNoTracking()
            .SingleAsync(row => row.JourneyId == journeyId, Token);
        Assert.Equal(JourneyRuntimeStage.Completed, journey.Stage);
        Assert.Equal(CurrentStopDepartures.LoadCancelledReleasedReason, journey.BlockReasonCode);
        foreach (string released in new[] { Demand, ThirdDemand })
        {
            Assert.Equal(DemandExecutionStatus.Accepted, await StatusOfAsync(fixture, released));
            JourneyDemandRow membership = await fixture.Context.Set<JourneyDemandRow>().AsNoTracking()
                .SingleAsync(row => row.JourneyId == journeyId && row.DemandId == released, Token);
            Assert.Equal(DemandJourneyLookup.ReleasedForRedispatchReason, membership.RemovalReason);
        }
        Assert.Equal(DemandExecutionStatus.Cancelled, await StatusOfAsync(fixture, SecondDemand));
        JourneyDemandRow cancelled = await fixture.Context.Set<JourneyDemandRow>().AsNoTracking()
            .SingleAsync(row => row.JourneyId == journeyId && row.DemandId == SecondDemand, Token);
        Assert.Equal(JourneyDemandStatuses.Terminated, cancelled.Status);
        Assert.NotEqual(DemandJourneyLookup.ReleasedForRedispatchReason, cancelled.RemovalReason);
        await SuppressionAssertions.AssertSuppressedAsync(fixture.Context, SecondDemand, "CANCELLED_BY_OPERATOR");
    }

    /// <summary>
    /// 审查 L2：待送点锚需求是本站唯一一条，后面同站的停靠上还有乙，车上零篮；扫码前取消锚之后不卡住——车载端点出发被受理，旅程原地收尾，
    /// 乙放回待派、不抑制，锚是它自己的终结与抑制。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("IntegrationSlice", "FP-IS-11")]
    public async Task CancellingTheOnlyRowOfTheAnchorsStopWithNothingOnBoardStillLetsTheOperatorDepart()
    {
        await using RuntimeFixture fixture = await ArrangeAsync(StopKind.StagingRulesOn);
        await AllowEnRouteAppendAsync(fixture);
        JourneyRuntimeRow waiting = await fixture.AdvanceToSublotWaitAsync();
        await AppendStagingDemandAsync(fixture, SecondDemand, SecondSublot);
        Assert.NotEqual(
            (await Batch7MultiDemandAdvanceTests.MembershipAsync(fixture, Demand)).PickupStopId,
            (await Batch7MultiDemandAdvanceTests.MembershipAsync(fixture, SecondDemand)).PickupStopId);

        await CancelBeforeScanAsync(fixture, Demand);
        for (int round = 0; round < 3; round++)
        {
            await RoundAsync(fixture);
        }

        string departureRequestId = await StagingDepartureRequestTests.RequestDepartureAsync(fixture);
        await RoundAsync(fixture);

        fixture.Context.ChangeTracker.Clear();
        Assert.Equal(
            "ACCEPTED",
            (await StagingDepartureRequestTests.ResultAsync(fixture, departureRequestId)).GetProperty("outcome").GetString());
        JourneyRuntimeRow journey = await fixture.Context.JourneyRuntimes.AsNoTracking()
            .SingleAsync(row => row.JourneyId == waiting.JourneyId, Token);
        Assert.Equal(JourneyRuntimeStage.Completed, journey.Stage);
        Assert.Equal(DemandExecutionStatus.Cancelled, await StatusOfAsync(fixture, Demand));
        await SuppressionAssertions.AssertSuppressedAsync(fixture.Context, Demand, "CANCELLED_BY_OPERATOR");
        Assert.Equal(DemandExecutionStatus.Accepted, await StatusOfAsync(fixture, SecondDemand));
        Assert.True(
            await DemandJourneyLookup.ReleasedForRedispatch(fixture.Context).AnyAsync(row => row.DemandId == SecondDemand, Token),
            "The demand left on the later stop is not waiting to be dispatched again.");
    }

    // ---- helpers ---------------------------------------------------------------------------------------------------

    // ArrangeAsync 给锚需求录的那一条子批。
    private const string StagingSublot = "SUBLOT-001";

    private const string ThirdDemand = "10000000-0000-4000-8000-000000000726";

    private const string ThirdSublot = "SUBLOT-726";

    /// <summary>
    /// control-server#742 不碰待送点（守卫 a）：同一个待送点停靠上甲装上一篮、乙扫码前取消，本停靠的待做项归零。机台取货上这种停靠（本站有 Loaded）
    /// 照旧等离站期限，待送点上则没有站点期限：车既不当轮离站，也不在任何站点期限上离站，持货等单照旧，直到持货期限到了才走（cs#709、CP-0014 R3）。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    [Trait("IntegrationSlice", "FP-IS-11")]
    public async Task ACancellationBesideALoadedBasketAtAStagingStopKeepsTheVehicleHoldingCargo()
    {
        await using RuntimeFixture fixture = await TwoDemandsAtTheStagingStopAsync();
        await SubmitAtCurrentStopAsync(fixture, "SUBLOT-001");
        await RoundAsync(fixture);
        await fixture.ApplySafeResultAsync(
            await fixture.OperationAsync(SlotOperationType.Load), SlotOperationType.Load, SlotBusinessState.Occupied);
        await RoundAsync(fixture);
        Assert.Equal(JourneyDemandStatuses.Loaded, (await Batch7MultiDemandAdvanceTests.MembershipAsync(fixture, Demand)).Status);

        await CancelBeforeScanAsync(fixture, SecondDemand);
        await RoundAsync(fixture);

        await AssertHoldsUntilTheHoldingDeadlineAsync(fixture);
    }

    /// <summary>
    /// control-server#742 不碰待送点（守卫 b，现场 agv02 217 站 10-17 的形状）：待送点停靠 1 上甲装上一篮，乙同站追加、另开停靠 2（REQ-0196），同站宽限到点
    /// 车原地走到停靠 2，乙在那里扫码前取消，停靠 2 一篮没装。#742 的新条件只认机台取货、待送点排除：车不离站，持货等单照旧，持货期限到了才走。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    [Trait("IntegrationSlice", "FP-IS-11")]
    public async Task ACancellationEmptyingASameStationStagingStopKeepsTheVehicleHoldingCargo()
    {
        await using RuntimeFixture fixture = await ArrangeAsync(StopKind.StagingRulesOn);
        await AllowEnRouteAppendAsync(fixture);
        await LoadTheBasketAsync(fixture);
        await AppendStagingDemandAsync(fixture, SecondDemand, SecondSublot);
        fixture.Clock.Advance(fixture.Options.SameStationAppendCorrectionGrace + TimeSpan.FromSeconds(1));
        await fixture.ProveSlotDoorsClosedAsync();
        await fixture.HearFromPeerAsync();
        await fixture.Engine.ExecuteOnceAsync(Token);
        await Batch7StopDrivenAdvanceDriver.AnswerDepartureSafetyAsync(
            fixture, Demand, Batch7StopDrivenAdvanceDriver.FirstSafetyResultId);
        await Batch7StopDrivenAdvanceDriver.ArriveAtCurrentStopAsync(fixture, SecondDemand, "TO_GATE");
        await fixture.ProveSlotDoorsClosedAsync();
        JourneyRuntimeRow atSecondStop = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, atSecondStop.Stage);
        string secondStopId = (await Batch7MultiDemandAdvanceTests.MembershipAsync(fixture, SecondDemand)).PickupStopId;
        Assert.NotEqual((await Batch7MultiDemandAdvanceTests.MembershipAsync(fixture, Demand)).PickupStopId, secondStopId);
        Assert.Equal(secondStopId, (await Batch7StopDrivenAdvanceDriver.CurrentStopAsync(fixture, Demand)).StopId);

        await CancelBeforeScanAsync(fixture, SecondDemand);
        await RoundAsync(fixture);

        Assert.Equal(JourneyRuntimeStage.AwaitingStationDeparture, (await fixture.RuntimeAsync()).Stage);
        await AssertHoldsUntilTheHoldingDeadlineAsync(fixture);
    }

    /// <summary>
    /// control-server#742 扩项只对机台取货（审查建议 2，CP-0015 修订项一）：待送点上出发请求带的清单号过期，即使新旧两版都没有待做项、已装没变，也照旧答
    /// <c>WORKLIST_REVISION_STALE</c>，不出发（待送点另议）。待送点没有站点期限，不会有「只补期限」的那一版顶掉旧号，这里在库里补一版内容相同、号加一的空清单来造。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("IntegrationSlice", "FP-IS-11")]
    public async Task AStaleDepartureAtAStagingStopIsRefusedEvenWithoutASubstantiveChange()
    {
        await using RuntimeFixture fixture = await TwoDemandsAtTheStagingStopAsync();
        await SubmitAtCurrentStopAsync(fixture, "SUBLOT-001");
        await RoundAsync(fixture);
        await StaleWorklistDepartureTests.SettleLoadThroughTheInboxAsync(fixture, Demand);
        await RoundAsync(fixture);
        await CancelBeforeScanAsync(fixture, SecondDemand);
        await RoundAsync(fixture);
        Assert.Equal(JourneyRuntimeStage.AwaitingStationDeparture, (await fixture.RuntimeAsync()).Stage);

        long shown = await CurrentStopDepartures.CurrentWorklistRevisionAsync(fixture.Context, fixture.Options.AgvId, Token);
        JourneyRuntimeRow runtime = await fixture.RuntimeAsync();
        JourneyStopCursor stops = await JourneyStopCursor.LoadAsync(fixture.Context, runtime, Token);
        ProtocolOutboxRow last = await fixture.Context.ProtocolOutbox.AsNoTracking()
            .SingleAsync(row => row.MessageId == stops.WorklistMessageIdAt(runtime.WorklistRevision, stops.Current, shown), Token);
        System.Text.Json.Nodes.JsonNode next = System.Text.Json.Nodes.JsonNode.Parse(last.PayloadJson)!;
        next["payload"]!["worklistRevision"] = shown + 1;
        Assert.Empty(next["payload"]!["items"]!.AsArray());
        fixture.Context.ProtocolOutbox.Add(new ProtocolOutboxRow
        {
            MessageId = stops.WorklistMessageIdAt(runtime.WorklistRevision, stops.Current, shown + 1),
            MessageType = last.MessageType,
            PayloadJson = next.ToJsonString(),
            CreatedAt = fixture.Clock.GetUtcNow()
        });
        await fixture.Context.SaveChangesAsync(Token);
        fixture.Context.ChangeTracker.Clear();

        string departureRequestId = await StagingDepartureRequestTests.RequestDepartureAsync(
            fixture, worklistRevision: shown, operationSessionId: null);
        await RoundAsync(fixture);

        JsonElement result = await StagingDepartureRequestTests.ResultAsync(fixture, departureRequestId);
        Assert.True(result.GetProperty("outcome").GetString() == "REJECTED", $"A stale departure at a staging stop was taken: {result}");
        Assert.Equal(ServerReasonCodes.WorklistRevisionStale, result.GetProperty("problem").GetProperty("reasonCode").GetString());
    }

    /// <summary>
    /// 车停在待送点上带着货等单：一路跑到持货期限前两秒，装货阶段一直是 CARGO_HOLDING_WAIT、离站核验一次都没发；过了持货期限，装货阶段以持货到期关闭
    /// （或车已在离站核验上）。
    /// </summary>
    private static async Task AssertHoldsUntilTheHoldingDeadlineAsync(RuntimeFixture fixture, bool freshEngineEachRound = false)
    {
        DateTimeOffset holdingSince = (await fixture.RuntimeAsync()).CargoHoldingStartedAt
            ?? throw new InvalidOperationException("Cargo holding never started at the staging stop.");
        DateTimeOffset holdingDeadline = holdingSince + fixture.Options.CargoHoldingTimeout;
        int checksBefore = await Batch7CargoHoldingTests.DepartureChecksSentAsync(fixture);
        while (fixture.Clock.GetUtcNow() + TimeSpan.FromSeconds(20) < holdingDeadline - TimeSpan.FromSeconds(2))
        {
            fixture.Clock.Advance(TimeSpan.FromSeconds(20));
            await fixture.ProveSlotDoorsClosedAsync();
            await fixture.HearFromPeerAsync();
            if (freshEngineEachRound)
            {
                await fixture.RecreateEngineAsync();
            }
            await fixture.Engine.ExecuteOnceAsync(Token);
            JourneyRuntimeRow holding = await fixture.RuntimeAsync();
            Assert.True(
                holding.LoadingPhaseState == LoadingPhaseStates.CargoHoldingWait &&
                await Batch7CargoHoldingTests.DepartureChecksSentAsync(fixture) == checksBefore,
                $"At {fixture.Clock.GetUtcNow():O}, before the holding deadline {holdingDeadline:O}, the vehicle stopped holding: " +
                $"{holding.Stage}, loading phase {holding.LoadingPhaseState}/{holding.LoadingClosedReason}.");
        }

        fixture.Clock.Advance(holdingDeadline + TimeSpan.FromSeconds(1) - fixture.Clock.GetUtcNow());
        await fixture.ProveSlotDoorsClosedAsync();
        await fixture.HearFromPeerAsync();
        if (freshEngineEachRound)
        {
            await fixture.RecreateEngineAsync();
        }
        await fixture.Engine.ExecuteOnceAsync(Token);
        JourneyRuntimeRow expired = await fixture.RuntimeAsync();
        Assert.True(
            expired.LoadingClosedReason == LoadingClosedReasons.CargoHoldingTimeout ||
            expired.Stage == JourneyRuntimeStage.AwaitingDepartureSafety,
            $"The holding deadline passed and the vehicle is still at {expired.Stage}, loading phase {expired.LoadingPhaseState}/{expired.LoadingClosedReason}.");
    }

    /// <summary>
    /// 同站追加之后：纠错宽限到点，第 1 个（或上一个）停靠发离站核验、车答安全，车原地「到站」追加的那个停靠，停在等录入。
    /// </summary>
    private static async Task MoveToTheAppendedStopAsync(RuntimeFixture fixture, string appendedDemand, string safetyResultId)
    {
        fixture.Clock.Advance(fixture.Options.SameStationAppendCorrectionGrace + TimeSpan.FromSeconds(1));
        await fixture.ProveSlotDoorsClosedAsync();
        await fixture.HearFromPeerAsync();
        await fixture.Engine.ExecuteOnceAsync(Token);
        await Batch7StopDrivenAdvanceDriver.AnswerDepartureSafetyAsync(fixture, Demand, safetyResultId);
        await Batch7StopDrivenAdvanceDriver.ArriveAtCurrentStopAsync(fixture, appendedDemand, "TO_GATE");
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, (await fixture.RuntimeAsync()).Stage);
        Assert.Equal(
            (await Batch7MultiDemandAdvanceTests.MembershipAsync(fixture, appendedDemand)).PickupStopId,
            (await Batch7StopDrivenAdvanceDriver.CurrentStopAsync(fixture, Demand)).StopId);
    }

    /// <summary>在当前停靠录入这条追加需求的子批，服务端下装货命令，停在等装货结果；返回那次装货。</summary>
    private static async Task<StationOperationRow> StartLoadAtCurrentStopAsync(RuntimeFixture fixture, string demandId, string sublot)
    {
        // 夹具追加时预留两篮；录入时按 BR-013 由盒数重算篮数（每篮四盒），所以子批要读出 8 盒，否则录入被拒。
        fixture.BoxCounts.Set(sublot, 8);
        await SubmitAtCurrentStopAsync(fixture, sublot);
        await RoundAsync(fixture);
        Assert.Equal(JourneyRuntimeStage.AwaitingLoadResult, (await fixture.RuntimeAsync()).Stage);
        return await fixture.Context.StationOperations.AsNoTracking()
            .SingleAsync(row => row.DemandId == demandId && row.OperationType == SlotOperationType.Load, Token);
    }

    /// <summary>
    /// control-server#904 的断言：<paramref name="cancelled"/> 装货中取消、仓位证明全空之后，每轮一个新引擎（同生产），旅程照 #713 续上——
    /// 不报错、本站收尾、持货等单，持货期限到了离站去卸 <paramref name="loaded"/> 的第一条；被取消的放回待派、不抑制；停靠序号不变，
    /// 计划与清单的号不倒退（control-server#879 的教训）。
    /// </summary>
    /// <remarks>
    /// <paramref name="whileReleased"/> 在取消落定之后、下一轮之前跑（control-server#906：改绑定集、再派给别的车）。给了它，这个停靠此后认不出是
    /// 待送点（那正是要测的形状，这里先断言这一前提），就不断言持货等单，只断言在持货期限与站点期限都过去之前发出离站核验。
    /// </remarks>
    private static async Task AssertCancellationCarriedThroughAsync(
        RuntimeFixture fixture, string[] loaded, string cancelled, StationOperationRow load, Func<Task>? whileReleased = null)
    {
        string journeyId = (await fixture.RuntimeAsync()).JourneyId;
        Dictionary<string, int> sequencesBefore = await StopSequencesAsync(fixture, journeyId);
        string cancelledStopId = (await Batch7MultiDemandAdvanceTests.MembershipAsync(fixture, cancelled)).PickupStopId;
        HashSet<string> outboxBefore = [.. await fixture.Context.ProtocolOutbox.AsNoTracking().Select(row => row.MessageId).ToArrayAsync(Token)];
        long planBefore = (await RevisionsSentAsync(fixture, PlanType, PlanField, outboxBefore, afterOnly: false)).Max(sent => sent.Revision);
        long worklistBefore = (await RevisionsSentAsync(fixture, WorklistType, WorklistField, outboxBefore, afterOnly: false)).Max(sent => sent.Revision);

        Task<JourneyRuntimeRow> JourneyAsync() =>
            fixture.Context.JourneyRuntimes.AsNoTracking().SingleAsync(row => row.JourneyId == journeyId, Token);

        await CancelDuringTheLoadAsync(fixture, cancelled, load);
        if (whileReleased is not null)
        {
            await whileReleased();
            // 前提（control-server#906）：按冻结已经认不出这个停靠是待送点，cs#904 的识别修法在这里不起作用。
            Assert.False(await StagingPickupStops.CurrentStopIsStagingAsync(
                fixture.Context, await JourneyAsync(), fixture.Options.StagingDepartureRules, Token));
        }
        await fixture.RecreateEngineAsync();
        await RoundAsync(fixture);

        JourneyRuntimeRow afterCancel = await JourneyAsync();
        Assert.True(
            afterCancel.Stage == JourneyRuntimeStage.AwaitingStationDeparture && afterCancel.BlockReasonCode != JourneyRuntimeEngine.AdvanceFailedReason,
            $"The journey did not carry on after the cancellation: {afterCancel.Stage}, block {afterCancel.BlockReasonCode}.");
        Assert.DoesNotContain(fixture.EngineLog.Entries, entry => entry.Level >= Microsoft.Extensions.Logging.LogLevel.Error);
        if (whileReleased is null)
        {
            // 推进段之外读期限的地方（装货结果回执、看板、恢复协调器）用的是另一个入口，它同样要认得出这个只剩已移除归属的停靠。
            Assert.True(await StagingPickupStops.CurrentStopIsStagingAsync(
                fixture.Context, afterCancel, fixture.Options.StagingDepartureRules, Token));
        }
        // 按旅程取：再派受理之后（control-server#906）这条需求在别的车的旅程上另有一行归属。
        JourneyDemandRow released = await fixture.Context.Set<JourneyDemandRow>().AsNoTracking()
            .SingleAsync(row => row.DemandId == cancelled && row.JourneyId == journeyId, Token);
        Assert.Equal(DemandJourneyLookup.ReleasedForRedispatchReason, released.RemovalReason);
        Assert.Equal(DemandExecutionStatus.Accepted, await StatusOfAsync(fixture, cancelled));
        await SuppressionAssertions.AssertNothingSuppressedAsync(fixture.Context);
        foreach (string demandId in loaded)
        {
            JourneyDemandRow onBoard = await fixture.Context.Set<JourneyDemandRow>().AsNoTracking()
                .SingleAsync(row => row.DemandId == demandId, Token);
            Assert.Equal((JourneyDemandStatuses.Loaded, (DateTimeOffset?)null), (onBoard.Status, onBoard.RemovedAt));
        }

        if (whileReleased is null)
        {
            await AssertHoldsUntilTheHoldingDeadlineAsync(fixture, freshEngineEachRound: true);
        }
        else
        {
            await RoundsUntilTheDepartureCheckAsync(fixture, JourneyAsync);
        }
        if ((await JourneyAsync()).Stage != JourneyRuntimeStage.AwaitingDepartureSafety)
        {
            await fixture.RecreateEngineAsync();
            await RoundAsync(fixture);
        }
        Assert.Equal(JourneyRuntimeStage.AwaitingDepartureSafety, (await JourneyAsync()).Stage);
        await fixture.RecreateEngineAsync();
        await Batch7StopDrivenAdvanceDriver.AnswerDepartureSafetyAsync(fixture, loaded[0], "20000000-0000-4000-8000-000000000904");

        JourneyRuntimeRow departed = await fixture.Context.JourneyRuntimes.AsNoTracking()
            .SingleAsync(row => row.JourneyId == journeyId, Token);
        Assert.True(
            departed.Stage == JourneyRuntimeStage.AwaitingGateArrival,
            $"The vehicle did not leave after a safe departure answer: {departed.Stage}, block {departed.BlockReasonCode}.");
        JourneyStopRow[] stops = await fixture.Context.Set<JourneyStopRow>().AsNoTracking()
            .Where(row => row.JourneyId == journeyId)
            .ToArrayAsync(Token);
        Assert.Equal(JourneyStopStatuses.Completed, stops.Single(row => row.StopId == cancelledStopId).Status);
        Assert.Equal(
            (await Batch7MultiDemandAdvanceTests.MembershipAsync(fixture, loaded[0])).UnloadStopId,
            (await Batch7StopDrivenAdvanceDriver.CurrentStopAsync(fixture, loaded[0])).StopId);
        Assert.Equal(sequencesBefore, await StopSequencesAsync(fixture, journeyId));
        // 号不倒退只看取消之后新建的发件箱行（PR #905 审查 M1）：发件箱只标 FencedAt 不删行，拿全部行的最大号比，取消前的行也算，恒真。
        // 清单：取消之后每一版的号都严格大于取消前车上那一版——车拒收不升号的清单（同号换内容）与倒退的号。
        SentRevision[] worklistsAfter = await RevisionsSentAsync(fixture, WorklistType, WorklistField, outboxBefore, afterOnly: true);
        Assert.NotEmpty(worklistsAfter);
        Assert.All(worklistsAfter, sent => Assert.True(
            sent.Revision > worklistBefore,
            $"A worklist sent after the cancellation went back to revision {sent.Revision}; the vehicle held {worklistBefore}."));
        // 计划：取消之后每一版不低于取消前的号，而且同一个号（取消前后合起来）只对应一种内容。
        SentRevision[] plansAfter = await RevisionsSentAsync(fixture, PlanType, PlanField, outboxBefore, afterOnly: true);
        Assert.All(plansAfter, sent => Assert.True(
            sent.Revision >= planBefore,
            $"A plan sent after the cancellation went back to revision {sent.Revision}; the vehicle held {planBefore}."));
        SentRevision[] plansEver = await RevisionsSentAsync(fixture, PlanType, PlanField, outboxBefore, afterOnly: false);
        Assert.All(
            plansEver.Concat(plansAfter).GroupBy(sent => sent.Revision),
            group => Assert.True(
                group.Select(sent => sent.Payload).Distinct(StringComparer.Ordinal).Count() == 1,
                $"Plan revision {group.Key} was sent with more than one content."));
        Assert.DoesNotContain(fixture.EngineLog.Entries, entry => entry.Level >= Microsoft.Extensions.Logging.LogLevel.Error);
    }

    /// <summary>
    /// 每轮一个新引擎往前推，直到离站核验发出；持货期限与站点期限都过了还没发就失败。每一轮都不许是推进失败。
    /// </summary>
    private static async Task RoundsUntilTheDepartureCheckAsync(RuntimeFixture fixture, Func<Task<JourneyRuntimeRow>> journey)
    {
        DateTimeOffset latest = fixture.Clock.GetUtcNow() + fixture.Options.CargoHoldingTimeout +
                                fixture.Options.StationDepartureWaitTimeout + TimeSpan.FromSeconds(30);
        while (true)
        {
            JourneyRuntimeRow now = await journey();
            Assert.True(
                now.BlockReasonCode != JourneyRuntimeEngine.AdvanceFailedReason,
                $"At {fixture.Clock.GetUtcNow():O} the journey failed to advance: {now.Stage}.");
            if (now.Stage == JourneyRuntimeStage.AwaitingDepartureSafety)
            {
                return;
            }
            Assert.True(
                fixture.Clock.GetUtcNow() < latest,
                $"Both deadlines passed and the vehicle is still at {now.Stage}, block {now.BlockReasonCode}.");
            fixture.Clock.Advance(TimeSpan.FromSeconds(20));
            await fixture.ProveSlotDoorsClosedAsync();
            await fixture.HearFromPeerAsync();
            await fixture.RecreateEngineAsync();
            await fixture.Engine.ExecuteOnceAsync(Token);
        }
    }

    /// <summary>别的待送站：新绑定集把 STAGING_TO_WIRE 改到这里（control-server#906）。</summary>
    private const int MovedStagingStationRiotId = 306;

    private const string MovedStagingStationName = "派工待送取货二";

    /// <summary>
    /// 导入一版新的绑定集并生效：关卡不变，STAGING_TO_WIRE 的固定站改到 <see cref="MovedStagingStationRiotId"/>。返回新版本号。
    /// </summary>
    private static async Task<long> MoveTheStagingBindingAsync(RuntimeFixture fixture)
    {
        DemandTaskTypeStationFreeze freeze = await new DemandTaskTypeStationFreezeStore(fixture.Context).ReadAsync(Demand, Token)
            ?? throw new InvalidOperationException("The first demand froze no task type station binding.");
        // 新站在地图上（StagingPickupNoCountdownTests.ArrangeAsync 那几个站之外再加它）：绑定到地图上没有的站会把任务类型挂起，测的就成了别的事。
        fixture.Riot.SetMapStations(
            new RiotMapStation(12, "N1-1"),
            new RiotMapStation(13, "N1-2_N1-3"),
            new RiotMapStation(TaskTypeStationRuntimeSeed.GateStationRiotId, TaskTypeStationRuntimeSeed.GateStationName),
            new RiotMapStation(300, "等待点"),
            new RiotMapStation(StagingStationRiotId, StagingStationName),
            new RiotMapStation(MovedStagingStationRiotId, MovedStagingStationName));
        await using ControlServerDbContext context = new(fixture.DbOptionsForTests);
        TaskTypeStationAccess access = TaskTypeStationRuntimeSeed.Access(context);
        long version = (await access.Bindings.WriteVersionAsync(
            freeze.MapId,
            freeze.RuleVersion,
            [TransportTaskTypes.WireToGate, TransportTaskTypes.StagingToWire],
            [
                TaskTypeStationRuntimeSeed.GateBinding,
                new TaskTypeStationBinding(
                    TransportTaskTypes.StagingToWire, MovedStagingStationRiotId, MovedStagingStationName, "SITE-CHECK-STAGING-2"),
            ],
            null,
            "preset:test",
            fixture.Clock.GetUtcNow(),
            Token)).Version.Version;
        await access.Bindings.SetActiveAsync(freeze.MapId, version, fixture.Clock.GetUtcNow(), Token);
        return version;
    }

    /// <summary>
    /// 被放回的需求由另一辆车再派受理，走产品的受理写入（<c>AcceptWithOrderIntentAsync</c>，再派那一支删旧冻结、按 <paramref name="bindingSetVersion"/>
    /// 重冻）。那辆车的旅程不归这个夹具推（夹具只有一辆车的 RIoT 与车载端），受理之后直接记成已完成：要测的只是原车这一侧。
    /// </summary>
    private static async Task RedispatchToAnotherVehicleAsync(
        RuntimeFixture fixture, string demandId, string sublot, long bindingSetVersion)
    {
        DemandTaskTypeStationFreeze freeze = await new DemandTaskTypeStationFreezeStore(fixture.Context).ReadAsync(demandId, Token)
            ?? throw new InvalidOperationException("The released demand froze no task type station binding.");
        JourneyRuntimeRow original = await fixture.RuntimeAsync();
        string key = JourneyIdentity.DerivationKey(demandId, 2);
        JourneyExecutionPlan plan = new(
            "agv-906",
            "VK-906",
            1,
            original.MapId,
            original.MapIdentity,
            original.DispatchZone,
            $"route-{key}",
            MovedStagingStationName,
            MovedStagingStationRiotId,
            original.GateStationId!,
            original.GateStationRiotId,
            2,
            [1, 2],
            JourneyPlanBuilder.StableGuid(key, "operation-session"),
            JourneyPlanBuilder.StableGuid(key, "pickup-leg"),
            $"W2G-{demandId}-PICKUP-2",
            JourneyPlanBuilder.StableGuid(key, "gate-leg"),
            $"W2G-{demandId}-GATE-2",
            2,
            fixture.Clock.GetUtcNow())
        {
            IdentityKey = key,
            StationCatalogRevision = await fixture.Context.FrozenDemandStations.AsNoTracking()
                .Where(row => row.DemandId == demandId)
                .Select(row => (long?)row.CatalogRevision)
                .FirstOrDefaultAsync(Token),
            TaskTypeStationRuleVersion = freeze.RuleVersion,
            TaskTypeStationBindingSetVersion = bindingSetVersion,
        };
        AcceptedDemandRow accepted = await fixture.Context.AcceptedDemands.AsNoTracking()
            .SingleAsync(row => row.DemandId == demandId, Token);
        await using ControlServerDbContext context = new(fixture.DbOptionsForTests);
        await new WireToGateStore(context).AcceptWithOrderIntentAsync(
            fixture.Demand(demandId, sublot, accepted.CreatedAt) with
            {
                WorkType = TransportTaskTypes.StagingToWire,
                TransportDemandKey = $"{sublot}|{TransportTaskTypes.StagingToWire}",
            },
            JourneyPlanBuilder.PickupIntent(plan, demandId, fixture.Clock.GetUtcNow()),
            plan,
            Token);
        JourneyRuntimeRow other = await context.JourneyRuntimes.SingleAsync(row => row.AgvId == "agv-906", Token);
        other.Stage = JourneyRuntimeStage.Completed;
        await context.SaveChangesAsync(Token);
        fixture.Context.ChangeTracker.Clear();
        Assert.Equal(
            bindingSetVersion,
            (await new DemandTaskTypeStationFreezeStore(fixture.Context).ReadAsync(demandId, Token))!.BindingSetVersion);
    }

    private const string PlanType = "UpcomingStopPlanSnapshot";
    private const string PlanField = "planRevision";
    private const string WorklistType = "CurrentStopWorklistSnapshot";
    private const string WorklistField = "worklistRevision";

    private sealed record SentRevision(long Revision, string Payload);

    private static async Task<Dictionary<string, int>> StopSequencesAsync(RuntimeFixture fixture, string journeyId) =>
        await fixture.Context.Set<JourneyStopRow>().AsNoTracking()
            .Where(row => row.JourneyId == journeyId)
            .ToDictionaryAsync(row => row.StopId, row => row.Sequence, StringComparer.Ordinal, Token);

    /// <summary>
    /// 某类报文的修订号与内容（信封之外的 <c>payload</c>）：<paramref name="afterOnly"/> 为真只取 <paramref name="before"/> 里没有的发件箱行
    /// （取消之后新建的），否则只取 <paramref name="before"/> 里的。
    /// </summary>
    private static async Task<SentRevision[]> RevisionsSentAsync(
        RuntimeFixture fixture, string messageType, string field, HashSet<string> before, bool afterOnly)
    {
        var rows = await fixture.Context.ProtocolOutbox.AsNoTracking()
            .Where(row => row.MessageType == messageType)
            .Select(row => new { row.MessageId, row.PayloadJson })
            .ToArrayAsync(Token);
        List<SentRevision> sent = [];
        foreach (var row in rows.Where(row => before.Contains(row.MessageId) != afterOnly))
        {
            using JsonDocument document = JsonDocument.Parse(row.PayloadJson);
            JsonElement payload = document.RootElement.GetProperty("payload");
            sent.Add(new SentRevision(payload.GetProperty(field).GetInt64(), payload.GetRawText()));
        }
        return [.. sent];
    }

    private const string UndoCancellationId = "71300000-0000-4000-8000-000000000726";

    /// <summary>待送点（规则开）受理锚需求，到站之前往同一个取货停靠上再挂乙：一站两条，都还没装，停在等录入。</summary>
    private static async Task<RuntimeFixture> TwoDemandsAtTheStagingStopAsync(bool withThird = false)
    {
        RuntimeFixture fixture = await ArrangeAsync(StopKind.StagingRulesOn);
        fixture.BoxCounts.Set(SecondSublot, 4);
        fixture.BoxCounts.Set(ThirdSublot, 4);
        await Batch7StopDrivenAdvanceDriver.TickAndRunAsync(fixture);
        JourneyRuntimeRow runtime = await fixture.RuntimeAsync(Demand);
        await Batch7MultiDemandAdvanceTests.AddDemandToJourneyAsync(fixture, runtime, SecondDemand, SecondSublot, slotBlock: 1);
        if (withThird)
        {
            await Batch7MultiDemandAdvanceTests.AddDemandToJourneyAsync(fixture, runtime, ThirdDemand, ThirdSublot, slotBlock: 2);
        }
        fixture.Riot.SetSuccessfulArrival("TO_PICKUP", runtime.PickupUpperId, runtime.PickupStationRiotId);
        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { CurrentStationId = runtime.PickupStationRiotId };
        await Batch7StopDrivenAdvanceDriver.TickAndRunAsync(fixture);
        JourneyRuntimeRow waiting = await fixture.RuntimeAsync(Demand);
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, waiting.Stage);
        Assert.True(await StagingPickupStops.CurrentStopIsStagingAsync(
            fixture.Context, waiting, fixture.Options.StagingDepartureRules, Token));
        return fixture;
    }

    private static object BeforeScanRequest(RuntimeFixture fixture, string demandId, string cancellationId = CancellationId) => new
    {
        cancellationId,
        demandId,
        slotOperationAttemptId = (string?)null,
        @operator = Operator(fixture),
        reason = "Nothing to load here."
    };

    /// <summary>操作员对一条需求扫码前取消，走服务端真实的授权与结果受理；车报空范围 ALL_EMPTY。</summary>
    private static async Task CancelBeforeScanAsync(RuntimeFixture fixture, string demandId, string cancellationId = CancellationId)
    {
        await using ControlServerDbContext connection = fixture.OpenConnectionContext();
        string authorization = await ProcessAsync(
            fixture, connection, "LoadCancellationStartRequested", BeforeScanRequest(fixture, demandId, cancellationId));
        Assert.Equal("AUTHORIZED", FirstPayload(authorization).GetProperty("decision").GetString());
        string ack = await ProcessAsync(fixture, connection, "LoadCancellationResult", new
        {
            cancellationId,
            demandId,
            slotOperationAttemptId = (string?)null,
            overallOutcome = "ALL_EMPTY",
            slotResults = Array.Empty<object>(),
            observedAt = fixture.Clock.GetUtcNow()
        });
        Assert.Contains("\"DurableAck\"", ack, StringComparison.Ordinal);
    }

    private static async Task<DemandExecutionStatus> StatusOfAsync(RuntimeFixture fixture, string demandId) =>
        (await fixture.Context.AcceptedDemands.AsNoTracking().SingleAsync(row => row.DemandId == demandId, Token)).Status;

    private static async Task RoundAsync(RuntimeFixture fixture)
    {
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        await fixture.ProveSlotDoorsClosedAsync();
        await fixture.HearFromPeerAsync();
        await fixture.Engine.ExecuteOnceAsync(Token);
    }

    private sealed record Sent(long Revision, string[] Demands, string[] Sublots);

    /// <summary>某一类里修订号最高的那一行，也就是此刻排给车的那一版。</summary>
    private static async Task<Sent> LatestAsync(RuntimeFixture fixture, string messageType)
    {
        string[] payloads = await fixture.Context.ProtocolOutbox.AsNoTracking()
            .Where(row => row.MessageType == messageType)
            .Select(row => row.PayloadJson)
            .ToArrayAsync(Token);
        Sent? latest = null;
        foreach (string json in payloads)
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement payload = document.RootElement.GetProperty("payload");
            long revision = payload.GetProperty("worklistRevision").GetInt64();
            if (latest is not null && revision <= latest.Revision) continue;
            string[] demands = payload.TryGetProperty("items", out JsonElement items)
                ? [.. items.EnumerateArray().Select(item => item.GetProperty("demandId").GetString()!)]
                : [];
            string[] sublots = payload.TryGetProperty("expectedSublots", out JsonElement expected)
                ? [.. expected.EnumerateArray().Select(item => item.GetString()!)]
                : [];
            latest = new Sent(revision, demands, sublots);
        }
        return Assert.IsType<Sent>(latest);
    }

    private static async Task CancelDuringTheLoadAsync(
        RuntimeFixture fixture, string demandId, StationOperationRow load, string cancellationId = CancellationId)
    {
        int[] slots = JsonSerializer.Deserialize<int[]>(load.TargetSlotsJson)!;
        // Nothing loaded and every door shut since the load began (control-server#837).
        await TestSlotReadings.SeedProvenEmptyAsync(fixture.Context, fixture.Options.AgvId, fixture.Clock.GetUtcNow());
        await using ControlServerDbContext connection = fixture.OpenConnectionContext();
        string authorization = await ProcessAsync(fixture, connection, "LoadCancellationStartRequested", new
        {
            cancellationId,
            demandId,
            slotOperationAttemptId = load.SlotOperationAttemptId,
            @operator = Operator(fixture),
            reason = "Scanned the wrong door."
        });
        Assert.Equal("AUTHORIZED", FirstPayload(authorization).GetProperty("decision").GetString());
        string ack = await ProcessAsync(fixture, connection, "LoadCancellationResult", new
        {
            cancellationId,
            demandId,
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
        });
        Assert.Contains("\"DurableAck\"", ack, StringComparison.Ordinal);
    }

    private static async Task SubmitAtCurrentStopAsync(RuntimeFixture fixture, string sublot)
    {
        JourneyRuntimeRow runtime = await fixture.RuntimeAsync();
        StopEntryAddress address = (await JourneyStopCursor.LoadAsync(fixture.Context, runtime, Token))
            .EntryAddressOfCurrentStop(runtime.WorklistRevision);
        await fixture.AddInboxAsync(
            Guid.NewGuid().ToString("D"), "SublotSubmitted", Batch7StopDrivenAdvanceDriver.SublotSubmission(fixture, address, sublot));
    }

    private static object Operator(RuntimeFixture fixture) =>
        new { operatorId = "OP-713", verificationMethod = "BADGE", verifiedAt = fixture.Clock.GetUtcNow() };

    private static Task<string> ProcessAsync(
        RuntimeFixture fixture,
        ControlServerDbContext connection,
        string messageType,
        object payload)
    {
        OnboardMessageProcessor processor = TestOnboardProcessorFactory.Create(
            connection, new WireToGateStore(connection), fixture.Clock, new ConfigurationBuilder().Build(),
            runtimeOptions: fixture.Options);
        OnboardConnectionState state = new()
        {
            AgvId = fixture.Options.AgvId,
            SessionGeneration = 1,
            CapabilityRevision = 1,
            SafetyRevision = 7,
            Readiness = SessionReadiness.Ready
        };
        string line = JsonSerializer.Serialize(new
        {
            protocolVersion = ProtocolCandidateIdentity.ProtocolVersion,
            profileId = ProtocolCandidateIdentity.ProfileId,
            protocolReleaseVersion = ProtocolCandidateIdentity.ReleaseVersion,
            protocolReleaseManifestSha256 = ProtocolCandidateIdentity.ManifestSha256,
            messageType,
            messageId = Guid.NewGuid().ToString("D"),
            correlationId = (string?)null,
            agvId = fixture.Options.AgvId,
            sessionGeneration = 1L,
            sentAt = fixture.Clock.GetUtcNow(),
            payload
        }, SerializerOptions);
        return processor.ProcessAsync(line, state, Token);
    }

    private static JsonElement FirstPayload(string answer)
    {
        using JsonDocument document = JsonDocument.Parse(answer.Split('\n')[0]);
        return document.RootElement.GetProperty("payload").Clone();
    }
}
