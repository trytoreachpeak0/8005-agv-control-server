using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
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
