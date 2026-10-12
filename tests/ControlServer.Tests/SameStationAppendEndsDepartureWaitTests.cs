using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using static ControlServer.Tests.Batch7StopDrivenAdvanceDriver;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;

namespace ControlServer.Tests;

/// <summary>
/// 车装完停在取货站等离站时，同一站追加了一条需求：不等原停靠的倒计时，这一轮就走原地的离站核验与「到站」，新停靠到站时拿到
/// 完整等待时长（control-server#646，用户 2026-10-12 定「提前结束等待」）。
/// </summary>
/// <remarks>
/// <para>
/// <b>修之前。</b>追加不并入当前停靠（REQ-0196），同站的新需求成为当前停靠之后的新停靠；车在原停靠一直等到原期限才发离站核验，
/// 这期间新需求不在车上的清单里，录不进去（cs#643 的探针 <c>ProbeAnAppendAtTheStationTheVehicleStandsAt</c>，提交 75f2593e4）。
/// </para>
/// <para>
/// <b>纠错宽限（PR #660 审查 S1）。</b>提前结束要等最后一批落定（或纠错闭环）之后停够 <c>SameStationAppendCorrectionGrace</c>：服务端只在
/// <c>AwaitingStationDeparture</c> 授权纠错，车载端只对最近一次完成的装货开纠错入口，新停靠一装完，这一站已装的就纠正不了了。没有宽限时，
/// 追加早于落定的那一种窗口是零——落定那一轮直接发离站核验，这个阶段一次都不落库。
/// </para>
/// <para>
/// <b>每条用例都显式打开等待。</b>夹具默认等待为零，零等于装完当轮离站，在那个设置下提前不提前看不出来。
/// 不放行的几条都把钟拨到原期限之前一点再跑：只跑一轮、钟不走，「没发离站核验」对修前修后都成立，什么也说明不了。
/// </para>
/// </remarks>
public sealed class SameStationAppendEndsDepartureWaitTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(60);

    /// <summary>纠错宽限。比 <see cref="BeforeAppend"/> 短，比期限短得多：放行既不是因为原期限到了，宽限也看得出来。</summary>
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(10);

    /// <summary>装完到追加之间钟走过的时间：比期限短得多，放行不是因为原期限到了。</summary>
    private static readonly TimeSpan BeforeAppend = TimeSpan.FromSeconds(20);

    private const string OtherPickupArea = "N1-2";
    private const int OtherPickupStationRiotId = 13;
    private const string PendingDemandId = "10000000-0000-4000-8000-000000000646";
    private const string PendingSublot = "SUBLOT-646";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// 宽限已过之后同站追加：下一轮就发离站核验（原期限还远）；原地「到站」后新停靠从到站那一轮起算完整时长，车上那一版清单列着新需求、
    /// 带着这个期限。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task ASameStationAppendAfterTheGraceSendsTheVehicleOnAtOnceAndTheNewStopGetsTheFullWait()
    {
        await using RuntimeFixture fixture = await Batch7CargoHoldingTests.HoldingFixtureAsync();
        Configure(fixture);
        DateTimeOffset loadedAt = await Batch7CargoHoldingTests.LoadTheFirstDemandAsync(fixture);
        JourneyStopRow firstPickup = await CurrentStopAsync(fixture, FirstDemandId);
        DateTimeOffset originalDeadline = loadedAt + Wait;

        await AppendAtAsync(fixture, firstPickup.StationId, firstPickup.StationRiotId);
        await RoundAsync(fixture);

        JourneyRuntimeRow leaving = await JourneyOfAsync(fixture, FirstDemandId);
        Assert.True(fixture.Clock.GetUtcNow() < originalDeadline - TimeSpan.FromSeconds(30));
        Assert.True(
            await DepartureCheckOfStopSentAsync(fixture, firstPickup.StopId),
            $"The stop is done and the next one is at the same station, yet the vehicle was not asked to leave. " +
            $"stage={leaving.Stage} phase={leaving.LoadingPhaseState} block={leaving.BlockReasonCode}");
        Assert.Equal(JourneyRuntimeStage.AwaitingDepartureSafety, leaving.Stage);

        await AnswerDepartureSafetyAsync(fixture, FirstDemandId, FirstSafetyResultId);
        JourneyRuntimeRow arrived = await ArriveAtCurrentStopAsync(fixture, SecondDemandId, "TO_GATE");
        DateTimeOffset arrivedAt = fixture.Clock.GetUtcNow();
        JourneyStopRow appended = await CurrentStopAsync(fixture, SecondDemandId);
        Assert.NotEqual(firstPickup.StopId, appended.StopId);
        Assert.Equal(firstPickup.StationId, appended.StationId);
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, arrived.Stage);
        Assert.Equal(arrivedAt, arrived.StationDepartureWaitStartedAt);

        (string[] demands, DateTimeOffset? deadline) = await LatestWorklistAsync(fixture);
        Assert.Equal([SecondDemandId], demands);
        Assert.Equal(arrivedAt + Wait, deadline);
    }

    /// <summary>
    /// 追加在另一站：照旧等满原期限才离站（期限之前一轮不发离站核验，到期那一轮发）。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task AnAppendAtAnotherStationStillWaitsOutTheStationDeadline()
    {
        await using RuntimeFixture fixture = await Batch7CargoHoldingTests.HoldingFixtureAsync();
        Configure(fixture);
        DateTimeOffset loadedAt = await Batch7CargoHoldingTests.LoadTheFirstDemandAsync(fixture);
        JourneyStopRow firstPickup = await CurrentStopAsync(fixture, FirstDemandId);

        await AppendAtAsync(fixture, OtherPickupArea, OtherPickupStationRiotId);
        await AssertHeldUntilAsync(fixture, firstPickup.StopId, loadedAt + Wait);

        await RunAtAsync(fixture, loadedAt + Wait);
        Assert.True(await DepartureCheckOfStopSentAsync(fixture, firstPickup.StopId), "The deadline passed and the vehicle was not sent on.");
    }

    /// <summary>
    /// 同站追加，但当前停靠上还有一条待装：不提前放行（当前停靠的活没做完）。
    /// </summary>
    /// <remarks>
    /// 这个状态正常流程走不到——还有待装时旅程在等录入，不在离站等待里——所以直接在库里给当前停靠挂上一条待装，钉住的是条件里
    /// 「已无待装」那一项本身。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task ASameStationAppendDoesNotEndTheWaitWhileTheCurrentStopHasALoadOutstanding()
    {
        await using RuntimeFixture fixture = await Batch7CargoHoldingTests.HoldingFixtureAsync();
        Configure(fixture);
        DateTimeOffset loadedAt = await Batch7CargoHoldingTests.LoadTheFirstDemandAsync(fixture);
        JourneyStopRow firstPickup = await CurrentStopAsync(fixture, FirstDemandId);
        await AppendAtAsync(fixture, firstPickup.StationId, firstPickup.StationRiotId);
        fixture.BoxCounts.Set(PendingSublot, 4);
        await Batch7MultiDemandAdvanceTests.AddDemandToJourneyAsync(
            fixture, await JourneyOfAsync(fixture, FirstDemandId), PendingDemandId, PendingSublot, slotBlock: 2);
        fixture.Context.ChangeTracker.Clear();
        Assert.Equal(
            firstPickup.StopId,
            (await fixture.Context.Set<JourneyDemandRow>().AsNoTracking()
                .SingleAsync(row => row.DemandId == PendingDemandId, Token)).PickupStopId);
        Assert.Equal(JourneyRuntimeStage.AwaitingStationDeparture, (await JourneyOfAsync(fixture, FirstDemandId)).Stage);

        await AssertHeldUntilAsync(fixture, firstPickup.StopId, loadedAt + Wait);
    }

    /// <summary>
    /// 同站追加，但本旅程有一条纠错开着：纠错闸照样挡住，不提前放行，阻断原因是纠错进行中。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task ASameStationAppendDoesNotEndTheWaitWhileACorrectionIsOpen()
    {
        await using RuntimeFixture fixture = await Batch7CargoHoldingTests.HoldingFixtureAsync();
        Configure(fixture);
        DateTimeOffset loadedAt = await Batch7CargoHoldingTests.LoadTheFirstDemandAsync(fixture);
        JourneyStopRow firstPickup = await CurrentStopAsync(fixture, FirstDemandId);
        await OpenCorrectionOnTheFirstDemandAsync(fixture);
        await AppendAtAsync(fixture, firstPickup.StationId, firstPickup.StationRiotId);

        await AssertHeldUntilAsync(fixture, firstPickup.StopId, loadedAt + Wait);
        Assert.Equal(
            JourneyRuntimeEngine.LoadCorrectionInProgressReason, (await JourneyOfAsync(fixture, FirstDemandId)).BlockReasonCode);
    }

    /// <summary>
    /// 同站追加，但车被仓门扣着（REQ-0364）：不提前放行，原期限过了也不发离站核验。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task ASameStationAppendDoesNotSendAVehicleHeldForItsSlotDoorOn()
    {
        await using RuntimeFixture fixture = await Batch7CargoHoldingTests.HoldingFixtureAsync();
        Configure(fixture);
        DateTimeOffset loadedAt = await Batch7CargoHoldingTests.LoadTheFirstDemandAsync(fixture);
        JourneyStopRow firstPickup = await CurrentStopAsync(fixture, FirstDemandId);
        fixture.Context.SlotDoorHolds.Add(new SlotDoorHoldRow
        {
            HoldId = "70000000-0000-4000-8000-000000000646",
            AgvId = fixture.Options.AgvId,
            DemandId = FirstDemandId,
            SlotsJson = "[1]",
            HeldAt = fixture.Clock.GetUtcNow()
        });
        await fixture.Context.SaveChangesAsync(Token);
        fixture.Context.ChangeTracker.Clear();
        await AppendAtAsync(fixture, firstPickup.StationId, firstPickup.StationRiotId);

        await AssertHeldUntilAsync(fixture, firstPickup.StopId, loadedAt + Wait + TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// 下一个停靠同站但是卸货（规划器落下的先装后卸）：不在本票的条件里，照旧等满期限。那个顺序归 control-server#647。
    /// </summary>
    /// <remarks>
    /// 计划形状 P1(N1-1，第一条装) → P2(N1-2，第二条装) → U1(N1-2，第一条卸) → U2(关卡，第二条卸)，直接在库里排出来；车停在 P2 装完时，
    /// 下一个停靠就是同站的卸货停靠。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task ASameStationUnloadNextDoesNotEndThePickupWait()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        Configure(fixture);
        fixture.Catalog.Set(
            fixture.Demand(FirstDemandId, FirstSublot, Now.AddMinutes(-10)),
            fixture.Demand(SecondDemandId, SecondSublot, Now.AddMinutes(-9), area: OtherPickupArea));
        fixture.BoxCounts.Set(FirstSublot, 7);
        fixture.BoxCounts.Set(SecondSublot, 7);
        await TickAndRunAsync(fixture);
        await Batch7ThreeStopJourneyTests.AppendSecondDemandAsync(fixture);
        await UnloadTheFirstDemandRightAfterTheSecondPickupAsync(fixture);

        await ArriveAtPickupAsync(fixture, FirstDemandId);
        await EnterSublotAsync(fixture, FirstDemandId, FirstSublot, FirstSubmissionId);
        await SettleLoadAsync(fixture, FirstDemandId);
        await RunAtAsync(fixture, (await JourneyOfAsync(fixture, FirstDemandId)).StationDepartureWaitStartedAt!.Value + Wait);
        await AnswerDepartureSafetyAsync(fixture, FirstDemandId, FirstSafetyResultId);
        await ArriveAtCurrentStopAsync(fixture, SecondDemandId, "TO_GATE");
        JourneyStopRow secondPickup = await CurrentStopAsync(fixture, SecondDemandId);
        Assert.Equal(JourneyStopRoles.Pickup, secondPickup.StopRole);
        await EnterSublotAsync(fixture, SecondDemandId, SecondSublot, SecondSubmissionId);
        JourneyRuntimeRow settled = await SettleLoadAsync(fixture, SecondDemandId);
        Assert.Equal(JourneyRuntimeStage.AwaitingStationDeparture, settled.Stage);

        await AssertHeldUntilAsync(fixture, secondPickup.StopId, settled.StationDepartureWaitStartedAt!.Value + Wait);
    }

    /// <summary>
    /// 追加早于第一条装货落定（审查 S1 的零窗口探针）：落定那一轮停在离站等待并落库，宽限内服务端照常授权纠错，纠错开着时宽限过了也不走。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CORRECTION")]
    public async Task AnAppendBeforeTheLoadCommitsLeavesTheGraceForACorrectionTheServerAuthorizes()
    {
        await using RuntimeFixture fixture = await Batch7CargoHoldingTests.HoldingFixtureAsync();
        Configure(fixture);
        JourneyStopRow firstPickup = await AppendWhileTheFirstLoadsAsync(fixture);
        DateTimeOffset committedAt = fixture.Clock.GetUtcNow();
        JourneyRuntimeRow settled = await JourneyOfAsync(fixture, FirstDemandId);
        Assert.True(
            settled.Stage == JourneyRuntimeStage.AwaitingStationDeparture,
            $"The load committed and the stop never stood in the departure wait. stage={settled.Stage} block={settled.BlockReasonCode}");
        Assert.False(await DepartureCheckOfStopSentAsync(fixture, firstPickup.StopId));

        fixture.Clock.Advance(TimeSpan.FromSeconds(2));
        string answer = await RequestCorrectionOfTheFirstDemandAsync(fixture);
        Assert.DoesNotContain("LoadCorrectionRejected", answer, StringComparison.Ordinal);
        Assert.Contains("LoadCorrectionCommand", await fixture.OutboxTypesAsync());

        await RunAtAsync(fixture, committedAt + Grace + TimeSpan.FromSeconds(5));
        JourneyRuntimeRow held = await JourneyOfAsync(fixture, FirstDemandId);
        Assert.False(await DepartureCheckOfStopSentAsync(fixture, firstPickup.StopId));
        Assert.Equal(JourneyRuntimeEngine.LoadCorrectionInProgressReason, held.BlockReasonCode);
    }

    /// <summary>
    /// 追加早于落定、没人纠错：宽限之前一秒仍停在离站等待，宽限到的那一轮发离站核验（离原期限还远）。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task AnAppendBeforeTheLoadCommitsEndsTheWaitWhenTheGraceRunsOut()
    {
        await using RuntimeFixture fixture = await Batch7CargoHoldingTests.HoldingFixtureAsync();
        Configure(fixture);
        JourneyStopRow firstPickup = await AppendWhileTheFirstLoadsAsync(fixture);
        DateTimeOffset committedAt = fixture.Clock.GetUtcNow();
        Assert.Equal(committedAt, (await JourneyOfAsync(fixture, FirstDemandId)).StationDepartureWaitStartedAt);

        await RunAtAsync(fixture, committedAt + Grace - TimeSpan.FromSeconds(1));
        Assert.False(await DepartureCheckOfStopSentAsync(fixture, firstPickup.StopId));
        Assert.Equal(JourneyRuntimeStage.AwaitingStationDeparture, (await JourneyOfAsync(fixture, FirstDemandId)).Stage);

        await RunAtAsync(fixture, committedAt + Grace);
        Assert.True(
            await DepartureCheckOfStopSentAsync(fixture, firstPickup.StopId),
            "The grace ran out and the vehicle was not sent on to the same-station stop.");
        Assert.True(fixture.Clock.GetUtcNow() < committedAt + Wait - TimeSpan.FromSeconds(30));
    }

    /// <summary>
    /// 宽限内开的纠错闭环：离站等待起点挪到闭环那一刻（ADR-cross-0055，期限从完整时长重算），提前结束再给一段宽限——
    /// 闭环 + 宽限之前一秒仍在等，到了才走。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CORRECTION")]
    public async Task ACorrectionClosedInTheGraceEarnsAnotherGraceBeforeTheVehicleIsSentOn()
    {
        await using RuntimeFixture fixture = await Batch7CargoHoldingTests.HoldingFixtureAsync();
        Configure(fixture);
        JourneyStopRow firstPickup = await AppendWhileTheFirstLoadsAsync(fixture);
        DateTimeOffset committedAt = fixture.Clock.GetUtcNow();
        fixture.Clock.Advance(TimeSpan.FromSeconds(2));
        Assert.DoesNotContain("LoadCorrectionRejected", await RequestCorrectionOfTheFirstDemandAsync(fixture), StringComparison.Ordinal);
        await RunAtAsync(fixture, committedAt + Grace + TimeSpan.FromSeconds(5));
        Assert.False(await DepartureCheckOfStopSentAsync(fixture, firstPickup.StopId));

        DateTimeOffset closedAt = await CloseTheCorrectionAsync(fixture);
        await RoundAsync(fixture);
        JourneyRuntimeRow restarted = await JourneyOfAsync(fixture, FirstDemandId);
        Assert.Equal(closedAt, restarted.StationDepartureWaitStartedAt);
        Assert.Null(restarted.BlockReasonCode);
        Assert.False(await DepartureCheckOfStopSentAsync(fixture, firstPickup.StopId));

        await RunAtAsync(fixture, closedAt + Grace - TimeSpan.FromSeconds(1));
        Assert.False(await DepartureCheckOfStopSentAsync(fixture, firstPickup.StopId));

        await RunAtAsync(fixture, closedAt + Grace);
        Assert.True(
            await DepartureCheckOfStopSentAsync(fixture, firstPickup.StopId),
            "The correction closed and its grace ran out, yet the vehicle was not sent on.");
        Assert.True(fixture.Clock.GetUtcNow() < closedAt + Wait);
    }

    /// <summary>
    /// 宽限为零：退回没有宽限时的行为——追加早于落定，落定那一轮就发离站核验。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task WithTheGraceAtZeroTheWaitEndsInTheRoundTheLoadCommits()
    {
        await using RuntimeFixture fixture = await Batch7CargoHoldingTests.HoldingFixtureAsync();
        Configure(fixture);
        fixture.Options.SameStationAppendCorrectionGrace = TimeSpan.Zero;
        JourneyStopRow firstPickup = await AppendWhileTheFirstLoadsAsync(fixture);

        Assert.True(await DepartureCheckOfStopSentAsync(fixture, firstPickup.StopId));
        Assert.Equal(JourneyRuntimeStage.AwaitingDepartureSafety, (await JourneyOfAsync(fixture, FirstDemandId)).Stage);
    }

    /// <summary>
    /// 「下一个停靠」只看开着的：当前停靠与关卡之间夹着一个已删的同站取货停靠（旧版本修订留下的序位）时，不算同站追加，照旧等满期限
    /// （PR #660 审查 L1 X2）。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task ARemovedSameStationStopDoesNotEndTheWait()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        Configure(fixture);
        fixture.Catalog.Set(fixture.Demand(FirstDemandId, FirstSublot, Now.AddMinutes(-10)));
        fixture.BoxCounts.Set(FirstSublot, 7);
        await ArriveAtPickupAsync(fixture, FirstDemandId);
        JourneyStopRow firstPickup = await CurrentStopAsync(fixture, FirstDemandId);
        await InsertStopAfterAsync(fixture, firstPickup, JourneyStopRoles.Pickup, JourneyStopStatuses.Removed, "REMOVED-PICKUP");
        await EnterSublotAsync(fixture, FirstDemandId, FirstSublot, FirstSubmissionId);
        JourneyRuntimeRow settled = await SettleLoadAsync(fixture, FirstDemandId);
        Assert.Equal(JourneyRuntimeStage.AwaitingStationDeparture, settled.Stage);

        await AssertHeldUntilAsync(fixture, firstPickup.StopId, settled.StationDepartureWaitStartedAt!.Value + Wait);
    }

    /// <summary>
    /// 纠错闭环时车又被仓门扣着：宽限过后提前结束成立、扣车闸挡住离站，看板上不残留过期的 <c>LOAD_CORRECTION_IN_PROGRESS</c>
    /// （PR #660 审查 L1 X4）。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task AClosedCorrectionUnderASlotDoorHoldLeavesNoStaleCorrectionReason()
    {
        await using RuntimeFixture fixture = await Batch7CargoHoldingTests.HoldingFixtureAsync();
        Configure(fixture);
        await Batch7CargoHoldingTests.LoadTheFirstDemandAsync(fixture);
        JourneyStopRow firstPickup = await CurrentStopAsync(fixture, FirstDemandId);
        await OpenCorrectionOnTheFirstDemandAsync(fixture);
        await AppendAtAsync(fixture, firstPickup.StationId, firstPickup.StationRiotId);
        await RoundAsync(fixture);
        Assert.Equal(
            JourneyRuntimeEngine.LoadCorrectionInProgressReason, (await JourneyOfAsync(fixture, FirstDemandId)).BlockReasonCode);

        await HoldTheSlotDoorAsync(fixture);
        DateTimeOffset closedAt = await CloseTheCorrectionAsync(fixture);
        await RunAtAsync(fixture, closedAt + Grace + TimeSpan.FromSeconds(1));

        JourneyRuntimeRow held = await JourneyOfAsync(fixture, FirstDemandId);
        Assert.False(await DepartureCheckOfStopSentAsync(fixture, firstPickup.StopId));
        Assert.Equal(JourneyRuntimeStage.AwaitingStationDeparture, held.Stage);
        Assert.NotEqual(JourneyRuntimeEngine.LoadCorrectionInProgressReason, held.BlockReasonCode);
    }

    private static void Configure(RuntimeFixture fixture)
    {
        fixture.Options.StationDepartureWaitTimeout = Wait;
        fixture.Options.SameStationAppendCorrectionGrace = Grace;
    }

    /// <summary>
    /// 第一条到站、录入、装货命令发出；装货结果回来之前在同一站追加第二条，然后结果落定、跑一轮。返回第一个取货停靠。
    /// </summary>
    private static async Task<JourneyStopRow> AppendWhileTheFirstLoadsAsync(RuntimeFixture fixture)
    {
        fixture.Catalog.Set(fixture.Demand(FirstDemandId, FirstSublot, Now.AddMinutes(-10)));
        fixture.BoxCounts.Set(FirstSublot, 7);
        await ArriveAtPickupAsync(fixture, FirstDemandId);
        await EnterSublotAsync(fixture, FirstDemandId, FirstSublot, FirstSubmissionId);
        Assert.Equal(JourneyRuntimeStage.AwaitingLoadResult, (await JourneyOfAsync(fixture, FirstDemandId)).Stage);
        JourneyStopRow firstPickup = await CurrentStopAsync(fixture, FirstDemandId);

        fixture.BoxCounts.Set(SecondSublot, 7);
        await Batch7ThreeStopJourneyTests.AppendDemandAsync(
            fixture, SecondDemandId, SecondSublot, firstPickup.StationId, firstPickup.StationRiotId);
        fixture.Context.ChangeTracker.Clear();
        JourneyStopRow appended = await fixture.Context.Set<JourneyStopRow>().AsNoTracking()
            .SingleAsync(row => row.StopId == JourneyIdentity.AppendedPickupStopId(SecondDemandId), Token);
        Assert.Equal(firstPickup.Sequence + 1, appended.Sequence);

        await ApplySafeResultAsync(fixture, FirstDemandId, SlotOperationType.Load, SlotBusinessState.Occupied);
        await TickAndRunAsync(fixture);
        return firstPickup;
    }

    /// <summary>车载端对第一条需求发起纠错，走服务端真实的授权路径。返回服务端的应答。</summary>
    private static async Task<string> RequestCorrectionOfTheFirstDemandAsync(RuntimeFixture fixture)
    {
        JourneyDemandRow membership = await fixture.Context.Set<JourneyDemandRow>().AsNoTracking()
            .SingleAsync(row => row.DemandId == FirstDemandId, Token);
        StationOperationRow load = await fixture.Context.StationOperations.AsNoTracking()
            .SingleAsync(row => row.SlotOperationAttemptId == membership.LoadSlotOperationAttemptId, Token);
        await using ControlServerDbContext connection = fixture.OpenConnectionContext();
        return await fixture.RequestLoadCorrectionOnConnectionAsync(
            connection,
            "70000000-0000-4000-8000-000000000649",
            FirstDemandId,
            load.SlotOperationAttemptId,
            JsonSerializer.Deserialize<int[]>(load.TargetSlotsJson)!);
    }

    /// <summary>把本旅程那一条纠错标成已闭环，闭环时刻取此刻。返回闭环时刻。</summary>
    private static async Task<DateTimeOffset> CloseTheCorrectionAsync(RuntimeFixture fixture)
    {
        DateTimeOffset closedAt = fixture.Clock.GetUtcNow();
        RecoveryWorkflowRow correction = await fixture.Context.RecoveryWorkflows
            .SingleAsync(row => row.WorkflowType == "LOAD_CORRECTION", Token);
        correction.State = RecoveryWorkflowState.Reconciled;
        correction.UpdatedAt = closedAt;
        await fixture.Context.SaveChangesAsync(Token);
        fixture.Context.ChangeTracker.Clear();
        return closedAt;
    }

    private static async Task HoldTheSlotDoorAsync(RuntimeFixture fixture)
    {
        fixture.Context.SlotDoorHolds.Add(new SlotDoorHoldRow
        {
            HoldId = "70000000-0000-4000-8000-000000000646",
            AgvId = fixture.Options.AgvId,
            DemandId = FirstDemandId,
            SlotsJson = "[1]",
            HeldAt = fixture.Clock.GetUtcNow()
        });
        await fixture.Context.SaveChangesAsync(Token);
        fixture.Context.ChangeTracker.Clear();
    }

    /// <summary>在 <paramref name="after"/> 之后插一个同站的停靠，后面的停靠序位各让一位。</summary>
    private static async Task InsertStopAfterAsync(
        RuntimeFixture fixture, JourneyStopRow after, string role, string status, string suffix)
    {
        foreach (JourneyStopRow later in await fixture.Context.Set<JourneyStopRow>()
                     .Where(row => row.JourneyId == after.JourneyId && row.Sequence > after.Sequence)
                     .ToArrayAsync(Token))
        {
            later.Sequence += 1;
        }
        fixture.Context.Set<JourneyStopRow>().Add(
            StopRow($"{after.JourneyId}|{suffix}", after, after.Sequence + 1, role, after.StationId, after.StationRiotId, status));
        await fixture.Context.SaveChangesAsync(Token);
        fixture.Context.ChangeTracker.Clear();
    }

    /// <summary>车停在原停靠等离站，钟走 <see cref="BeforeAppend"/> 后在给定的站追加第二条需求。</summary>
    private static async Task AppendAtAsync(RuntimeFixture fixture, string area, int stationRiotId)
    {
        Assert.Equal(JourneyRuntimeStage.AwaitingStationDeparture, (await JourneyOfAsync(fixture, FirstDemandId)).Stage);
        fixture.Clock.Advance(BeforeAppend);
        fixture.BoxCounts.Set(SecondSublot, 7);
        await Batch7ThreeStopJourneyTests.AppendDemandAsync(fixture, SecondDemandId, SecondSublot, area, stationRiotId);
        fixture.Context.ChangeTracker.Clear();
    }

    /// <summary>
    /// 追加之后连跑两轮、再在期限之前两秒跑一轮：这个停靠的离站核验一直没发，旅程还停在离站等待。
    /// </summary>
    private static async Task AssertHeldUntilAsync(RuntimeFixture fixture, string stopId, DateTimeOffset deadline)
    {
        await RoundAsync(fixture);
        await RoundAsync(fixture);
        await RunAtAsync(fixture, deadline - TimeSpan.FromSeconds(2));
        JourneyRuntimeRow held = await fixture.Context.JourneyRuntimes.AsNoTracking().SingleAsync(Token);
        Assert.False(
            await DepartureCheckOfStopSentAsync(fixture, stopId),
            $"The vehicle was asked to leave before {deadline:O}. stage={held.Stage} block={held.BlockReasonCode}");
        Assert.Equal(JourneyRuntimeStage.AwaitingStationDeparture, held.Stage);
    }

    private static async Task RoundAsync(RuntimeFixture fixture)
    {
        await fixture.HearFromPeerAsync();
        await TickAndRunAsync(fixture);
    }

    private static async Task RunAtAsync(RuntimeFixture fixture, DateTimeOffset at)
    {
        fixture.Clock.Advance(at - fixture.Clock.GetUtcNow());
        await fixture.HearFromPeerAsync();
        await fixture.Engine.ExecuteOnceAsync(Token);
    }

    /// <summary>这个停靠的离站核验有没有写进发件箱。按停靠认，不按条数：后面停靠的核验不算。</summary>
    private static async Task<bool> DepartureCheckOfStopSentAsync(RuntimeFixture fixture, string stopId)
    {
        JourneyStopRow stop = await fixture.Context.Set<JourneyStopRow>().AsNoTracking()
            .SingleAsync(row => row.StopId == stopId, Token);
        return stop.DepartureSafetyCheckMessageId is { } messageId &&
               await fixture.Context.ProtocolOutbox.AsNoTracking().AnyAsync(row => row.MessageId == messageId, Token);
    }

    /// <summary>第一条需求上开着一条本车、本旅程的纠错（已发命令、未闭环）。</summary>
    private static async Task OpenCorrectionOnTheFirstDemandAsync(RuntimeFixture fixture)
    {
        JourneyDemandRow membership = await fixture.Context.Set<JourneyDemandRow>().AsNoTracking()
            .SingleAsync(row => row.DemandId == FirstDemandId, Token);
        fixture.Context.RecoveryWorkflows.Add(new RecoveryWorkflowRow
        {
            WorkflowId = "70000000-0000-4000-8000-000000000647",
            WorkflowType = "LOAD_CORRECTION",
            AgvId = fixture.Options.AgvId,
            DemandId = FirstDemandId,
            SlotOperationAttemptId = membership.LoadSlotOperationAttemptId,
            SlotsJson = membership.TargetSlotsJson,
            State = RecoveryWorkflowState.AwaitingResult,
            RequestMessageId = "70000000-0000-4000-8000-000000000648",
            RequestContentHash = new string('c', 64),
            CreatedAt = fixture.Clock.GetUtcNow(),
            UpdatedAt = fixture.Clock.GetUtcNow()
        });
        await fixture.Context.SaveChangesAsync(Token);
        fixture.Context.ChangeTracker.Clear();
    }

    /// <summary>
    /// 把 P1, P2, U 排成 P1, P2, U1, U：第一条改在第二个取货站卸，卸货停靠新开一个、紧跟在 P2 之后，第二条仍在原卸货停靠卸。
    /// </summary>
    private static async Task UnloadTheFirstDemandRightAfterTheSecondPickupAsync(RuntimeFixture fixture)
    {
        JourneyRuntimeRow runtime = await fixture.RuntimeAsync(FirstDemandId);
        JourneyDemandRow first = await fixture.Context.Set<JourneyDemandRow>()
            .SingleAsync(row => row.DemandId == FirstDemandId, Token);
        JourneyDemandRow second = await fixture.Context.Set<JourneyDemandRow>()
            .SingleAsync(row => row.DemandId == SecondDemandId, Token);
        JourneyStopRow secondPickup = await fixture.Context.Set<JourneyStopRow>()
            .SingleAsync(row => row.StopId == second.PickupStopId, Token);
        JourneyStopRow unload = await fixture.Context.Set<JourneyStopRow>()
            .SingleAsync(row => row.StopId == second.UnloadStopId, Token);
        Assert.Equal(first.UnloadStopId, unload.StopId);
        Assert.Equal(secondPickup.Sequence + 1, unload.Sequence);

        string unloadHalfId = $"{runtime.JourneyId}|MIXED-UNLOAD-AFTER";
        fixture.Context.Set<JourneyStopRow>().Add(StopRow(
            unloadHalfId, secondPickup, unload.Sequence, JourneyStopRoles.Unload, secondPickup.StationId,
            secondPickup.StationRiotId, JourneyStopStatuses.Pending));
        unload.Sequence += 1;
        first.UnloadStopId = unloadHalfId;
        await fixture.Context.SaveChangesAsync(Token);
        fixture.Context.ChangeTracker.Clear();
    }

    /// <summary>
    /// 一个停靠行，形状与 <c>SingleDemandJourneyShape</c> 写下的一致：录入请求与离站核验的两个 id 留空，由引擎第一次要用时补上。
    /// </summary>
    private static JourneyStopRow StopRow(
        string stopId, JourneyStopRow like, int sequence, string role, string stationId, int stationRiotId, string status) => new()
        {
            StopId = stopId,
            JourneyId = like.JourneyId,
            Sequence = sequence,
            StopRole = role,
            StationId = stationId,
            StationRiotId = stationRiotId,
            DispatchZone = like.DispatchZone,
            OperationSessionId = JourneyPlanBuilder.StableGuid(stopId, "session"),
            MovementLegId = JourneyPlanBuilder.StableGuid(stopId, "leg"),
            UpperId = $"W2G-{stopId}",
            VehicleBusinessMessageId = JourneyPlanBuilder.StableGuid(stopId, "vehicle-state"),
            WorklistMessageId = JourneyPlanBuilder.StableGuid(stopId, "worklist"),
            PlanMessageId = JourneyPlanBuilder.StableGuid(stopId, "plan"),
            Status = status,
            CreatedAt = like.CreatedAt
        };

    /// <summary>修订号最高的那一版清单（此刻排给车的那一版）列着哪些需求、带的期限。</summary>
    private static async Task<(string[] Demands, DateTimeOffset? Deadline)> LatestWorklistAsync(RuntimeFixture fixture)
    {
        string[] payloads = await fixture.Context.ProtocolOutbox.AsNoTracking()
            .Where(row => row.MessageType == "CurrentStopWorklistSnapshot")
            .Select(row => row.PayloadJson)
            .ToArrayAsync(Token);
        long best = long.MinValue;
        string[] demands = [];
        DateTimeOffset? deadline = null;
        foreach (string json in payloads)
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement payload = document.RootElement.GetProperty("payload");
            long revision = payload.GetProperty("worklistRevision").GetInt64();
            if (revision <= best) continue;
            best = revision;
            demands = [.. payload.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("demandId").GetString()!)];
            JsonElement at = payload.GetProperty("stationDepartureDeadlineAt");
            deadline = at.ValueKind == JsonValueKind.Null ? null : at.GetDateTimeOffset();
        }
        return (demands, deadline);
    }
}
