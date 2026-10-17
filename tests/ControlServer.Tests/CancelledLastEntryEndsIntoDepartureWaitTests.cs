using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using static ControlServer.Tests.Batch7StopDrivenAdvanceDriver;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;

namespace ControlServer.Tests;

/// <summary>
/// 扫码前取消让当前取货停靠的待做项归零、旅程因车上还有货而继续：下一轮就转入正常的离站等待（control-server#668）。
/// </summary>
/// <remarks>
/// <para>
/// <b>修之前。</b>取消那一路（<c>OnboardRecoveryCoordinator</c> → <c>PickupStopTermination</c> → <c>StopEndWorklist</c>）不改阶段，旅程留在
/// <c>AwaitingSublot</c>，本站一条待做项都没有；这个阶段唯一的出口是到期结束本站（<c>TryEndStopAtStationDeadlineAsync</c>），它按原期限判，
/// 期限为空（<c>StationDepartureWaitTimeout = 0</c>）时永远返回 false：车载着货永久停在站上（PR #660 第三轮审查 P5）。期限非零时车要等到原期限，
/// 同站追加的提前结束不生效（P4、P5），车上那张空清单期限为空，屏上「无倒计时」。
/// </para>
/// <para>
/// <b>修之后</b>按离站等待的常规处理：起点与装货途中被终结那一路（<c>AwaitingLoadResult</c> 的续跑）同一个规则——本站装上过货就从此刻起算
/// （给已装的那几条留出纠错时间），一条都没装上就不动；纠错闸、同站追加的提前结束、持货等单、仓门扣车照常判；车上那张空清单补上期限。
/// </para>
/// </remarks>
public sealed class CancelledLastEntryEndsIntoDepartureWaitTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(10);

    /// <summary>甲装完到取消乙之间钟走过的时间：比宽限长，原起点加宽限早已过去；比期限短得多。</summary>
    private static readonly TimeSpan BeforeCancel = TimeSpan.FromSeconds(20);

    private const string AppendedDemandId = "10000000-0000-4000-8000-000000000668";
    private const string AppendedSublot = "SUBLOT-668";
    private const string ThirdSubmissionId = "20000000-0000-4000-8000-000000000742";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// P5：同一站甲已装、乙待录入，同站追加了丙（排成下一个停靠），操作员扫码前取消乙。下一轮转入离站等待，起点是此刻；宽限到点那一轮发离站
    /// 核验，前两秒不发；车上最新那一版空清单带着这个时刻，原因仍是装货取消、作业会话仍为空。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task ACancelBeforeScanThatEmptiesTheStopEndsTheWaitEarlyForASameStationAppend()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        Configure(fixture, Wait);
        JourneyStopRow pickup = await LoadTheFirstOfTwoAsync(fixture);
        fixture.Clock.Advance(BeforeCancel);
        await AppendAtTheSameStationAsync(fixture, pickup);
        await CancelTheSecondBeforeScanAsync(fixture);

        await RoundAsync(fixture);

        JourneyRuntimeRow waiting = await JourneyOfAsync(fixture, FirstDemandId);
        Assert.True(
            waiting.Stage == JourneyRuntimeStage.AwaitingStationDeparture,
            $"The stop has nothing left to do and should wait for departure, but it is in {waiting.Stage}.");
        DateTimeOffset startedAt = fixture.Clock.GetUtcNow();
        Assert.Equal(startedAt, waiting.StationDepartureWaitStartedAt);

        Worklist latest = await LatestWorklistAsync(fixture);
        Assert.Empty(latest.Demands);
        Assert.Equal(startedAt + Grace, latest.Deadline);
        Assert.Equal("LOAD_CANCELLED", latest.StopEndedReason);
        Assert.Null(latest.OperationSessionId);

        await AssertHeldUntilAsync(fixture, pickup.StopId, startedAt + Grace);
        await RunAtAsync(fixture, startedAt + Grace);
        Assert.True(await DepartureCheckOfStopSentAsync(fixture, pickup.StopId), "The grace passed and the vehicle was not sent on.");
    }

    /// <summary>
    /// P4：同一个形状，但乙的终结直接写进库（不经取消那一路、也没有空清单）。同样下一轮转入离站等待，宽限到点离站。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task AStopEmptiedInTheStoreEndsTheWaitEarlyForASameStationAppend()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        Configure(fixture, Wait);
        JourneyStopRow pickup = await LoadTheFirstOfTwoAsync(fixture);
        fixture.Clock.Advance(BeforeCancel);
        await AppendAtTheSameStationAsync(fixture, pickup);
        JourneyDemandRow second = await fixture.Context.Set<JourneyDemandRow>()
            .SingleAsync(row => row.DemandId == SecondDemandId, Token);
        second.Status = JourneyDemandStatuses.Terminated;
        await fixture.Context.SaveChangesAsync(Token);
        fixture.Context.ChangeTracker.Clear();

        await RoundAsync(fixture);

        JourneyRuntimeRow waiting = await JourneyOfAsync(fixture, FirstDemandId);
        Assert.Equal(JourneyRuntimeStage.AwaitingStationDeparture, waiting.Stage);
        DateTimeOffset startedAt = fixture.Clock.GetUtcNow();
        Assert.Equal(startedAt, waiting.StationDepartureWaitStartedAt);
        await AssertHeldUntilAsync(fixture, pickup.StopId, startedAt + Grace);
        await RunAtAsync(fixture, startedAt + Grace);
        Assert.True(await DepartureCheckOfStopSentAsync(fixture, pickup.StopId), "The grace passed and the vehicle was not sent on.");
    }

    /// <summary>
    /// 没有同站追加：取消之后从此刻起等满完整时长，车上那张空清单带着这个期限（不再是「无倒计时」），到点离站、前两秒不离站。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task ACancelBeforeScanThatEmptiesTheStopShowsTheDeadlineTheVehicleLeavesBy()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        Configure(fixture, Wait);
        JourneyStopRow pickup = await LoadTheFirstOfTwoAsync(fixture);
        fixture.Clock.Advance(BeforeCancel);
        await CancelTheSecondBeforeScanAsync(fixture);

        await RoundAsync(fixture);

        JourneyRuntimeRow waiting = await JourneyOfAsync(fixture, FirstDemandId);
        Assert.Equal(JourneyRuntimeStage.AwaitingStationDeparture, waiting.Stage);
        DateTimeOffset startedAt = fixture.Clock.GetUtcNow();
        Assert.Equal(startedAt, waiting.StationDepartureWaitStartedAt);
        Worklist latest = await LatestWorklistAsync(fixture);
        Assert.Empty(latest.Demands);
        Assert.Equal(startedAt + Wait, latest.Deadline);
        Assert.Equal("LOAD_CANCELLED", latest.StopEndedReason);

        await AssertHeldUntilAsync(fixture, pickup.StopId, startedAt + Wait);
        await RunAtAsync(fixture, startedAt + Wait);
        Assert.True(await DepartureCheckOfStopSentAsync(fixture, pickup.StopId), "The deadline passed and the vehicle was not sent on.");
    }

    /// <summary>
    /// 等待配为 0（关闭）：取消让本站归零的下一轮就发离站核验，不再永久停在等录入（PR #660 第三轮审查 P5 跑了 600 秒仍停着）。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task WithTheWaitOffACancelBeforeScanThatEmptiesTheStopSendsTheVehicleOn()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        Configure(fixture, TimeSpan.Zero);
        JourneyStopRow pickup = await LoadTheFirstOfTwoAsync(fixture);
        await fixture.ProveSlotDoorsClosedAsync();
        await CancelTheSecondBeforeScanAsync(fixture);

        await RoundAsync(fixture);

        JourneyRuntimeRow leaving = await JourneyOfAsync(fixture, FirstDemandId);
        Assert.True(
            await DepartureCheckOfStopSentAsync(fixture, pickup.StopId),
            $"The wait is off and the stop has nothing left, yet the vehicle was not asked to leave. stage={leaving.Stage}");
        Assert.Equal(JourneyRuntimeStage.AwaitingDepartureSafety, leaving.Stage);
    }

    /// <summary>
    /// 本站一条都没装上就被取消结束（车上的货是更早一站装的），旅程还要继续：取消之后的那一轮就发离站核验，不等站点离站期限
    /// （control-server#742，CP-0015 方案 A）。本站没有已装的货，就没有要留纠错窗口的放置（#668 那道护栏的理由在这里不成立）。
    /// </summary>
    /// <remarks>
    /// 这条在 #742 之前叫 <c>ACancelThatEndsAStopWithNothingLoadedThereKeepsTheOriginalDeadline</c>，断言的是「起点不动、等到原期限才走」。
    /// 改的是断言，不是认法：形状（第二个取货站、本站一条没装、扫码前取消、走服务端真实的授权与结果受理）一字不改，变的是用户 10-17 定的行为
    /// ——现场 agv03 145、164 站取消后各白等了约 5 分钟。再跑一轮核验身份不变：只放行一次，不重发。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task ACancelThatEndsAStopWithNothingLoadedThereSendsTheVehicleOnAtOnce()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        JourneyStopRow secondPickup = await StopEndedJourneyContinuesTests.ArriveAtTheSecondPickupAsync(fixture);
        fixture.Options.StationDepartureWaitTimeout = Wait;
        DateTimeOffset arrivedAt = (await JourneyOfAsync(fixture, FirstDemandId)).StationDepartureWaitStartedAt!.Value;
        fixture.Clock.Advance(BeforeCancel);
        await CancelTheSecondBeforeScanAsync(fixture);

        await RoundAsync(fixture);

        JourneyRuntimeRow leaving = await JourneyOfAsync(fixture, FirstDemandId);
        Assert.True(
            await DepartureCheckOfStopSentAsync(fixture, secondPickup.StopId),
            $"Nothing was loaded at this stop and nothing is left to do, yet the vehicle waits for {arrivedAt + Wait:O}. stage={leaving.Stage}");
        Assert.True(fixture.Clock.GetUtcNow() < arrivedAt + Wait);
        Assert.Equal(JourneyRuntimeStage.AwaitingDepartureSafety, leaving.Stage);
        string checkId = (await CurrentStopAsync(fixture, FirstDemandId)).DepartureSafetyCheckId!;

        await RoundAsync(fixture);

        Assert.Equal(JourneyRuntimeStage.AwaitingDepartureSafety, (await JourneyOfAsync(fixture, FirstDemandId)).Stage);
        Assert.Equal(checkId, (await CurrentStopAsync(fixture, FirstDemandId)).DepartureSafetyCheckId);
    }

    /// <summary>
    /// 旅程继续的多需求（control-server#742）：第二个取货站取消、本站空了之后车立即开往下一站，下一站（甲的卸货）照常做完，旅程完成。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task AfterACancelEmptiesAStopTheJourneyGoesOnToTheNextStopAndCompletes()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        await StopEndedJourneyContinuesTests.ArriveAtTheSecondPickupAsync(fixture);
        fixture.Options.StationDepartureWaitTimeout = Wait;
        DateTimeOffset arrivedAt = (await JourneyOfAsync(fixture, FirstDemandId)).StationDepartureWaitStartedAt!.Value;
        fixture.Clock.Advance(BeforeCancel);
        await CancelTheSecondBeforeScanAsync(fixture);
        await RoundAsync(fixture);

        JourneyRuntimeRow departing = await AnswerDepartureSafetyAsync(fixture, FirstDemandId, SecondSafetyResultId);
        Assert.True(
            fixture.Clock.GetUtcNow() < arrivedAt + Wait,
            $"The vehicle only left at {fixture.Clock.GetUtcNow():O}, not before the deadline {arrivedAt + Wait:O}.");
        Assert.Equal(JourneyStopRoles.Unload, (await CurrentStopAsync(fixture, FirstDemandId)).StopRole);
        Assert.NotEqual(JourneyRuntimeStage.AwaitingDepartureSafety, departing.Stage);

        JourneyRuntimeRow done = await ArriveAtGateAndUnloadAsync(fixture, FirstDemandId);
        Assert.Equal(JourneyRuntimeStage.Completed, done.Stage);
    }

    /// <summary>
    /// 下发与判定读同一个期限（control-server#742，PR #660 增量审查 S1 的约束）：取消让本站空了、车当轮就走，车上那一版清单不带一个还在将来的期限，
    /// 发件箱里也没有一版带着原来那段倒计时（到站起点 + 完整时长）——否则车已经走了，屏上还剩约 5 分钟。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task ACancelThatSendsTheVehicleOnLeavesNoCountdownOnTheVehicle()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        await StopEndedJourneyContinuesTests.ArriveAtTheSecondPickupAsync(fixture);
        fixture.Options.StationDepartureWaitTimeout = Wait;
        DateTimeOffset arrivedAt = (await JourneyOfAsync(fixture, FirstDemandId)).StationDepartureWaitStartedAt!.Value;
        fixture.Clock.Advance(BeforeCancel);
        await CancelTheSecondBeforeScanAsync(fixture);

        await RoundAsync(fixture);

        DateTimeOffset now = fixture.Clock.GetUtcNow();
        Worklist latest = await LatestWorklistAsync(fixture);
        Assert.True(
            latest.Deadline is not { } shown || shown <= now,
            $"The vehicle is leaving at {now:O} while its screen counts down to {latest.Deadline:O}.");
        Assert.Equal("LOAD_CANCELLED", latest.StopEndedReason);
        Assert.Null(latest.OperationSessionId);
        Assert.DoesNotContain(await WorklistDeadlinesAsync(fixture), deadline => deadline == arrivedAt + Wait);
    }

    /// <summary>
    /// 会话就绪闸在新条件之前（control-server#742）：取消之后车载端断线重连、会话还没就绪，离站等待的起点作废、不发离站核验；就绪之后起点重填，
    /// 新条件才放行，仍远早于原期限。新条件只读旅程与停靠，不读会话，所以绕不过上游那道闸（<c>AdvanceAsync</c> 的 <c>session is null</c> 一支）。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task ACancelThatEmptiesTheStopWaitsForTheSessionToBeReadyBeforeSendingTheVehicleOn()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        JourneyStopRow secondPickup = await StopEndedJourneyContinuesTests.ArriveAtTheSecondPickupAsync(fixture);
        fixture.Options.StationDepartureWaitTimeout = Wait;
        DateTimeOffset arrivedAt = (await JourneyOfAsync(fixture, FirstDemandId)).StationDepartureWaitStartedAt!.Value;
        fixture.Clock.Advance(BeforeCancel);
        await CancelTheSecondBeforeScanAsync(fixture);
        await fixture.ReconnectAsync(2);

        await TickAndRunAsync(fixture);
        await TickAndRunAsync(fixture);

        JourneyRuntimeRow gated = await JourneyOfAsync(fixture, FirstDemandId);
        Assert.False(
            await DepartureCheckOfStopSentAsync(fixture, secondPickup.StopId),
            $"The vehicle was sent on while its session was not ready. stage={gated.Stage} block={gated.BlockReasonCode}");
        Assert.Null(gated.StationDepartureWaitStartedAt);

        await fixture.AdvanceSessionAsync(2);
        await RoundAsync(fixture);
        await RoundAsync(fixture);

        Assert.True(
            await DepartureCheckOfStopSentAsync(fixture, secondPickup.StopId),
            $"The session is ready again and the stop is empty, yet the vehicle waits. stage={(await JourneyOfAsync(fixture, FirstDemandId)).Stage}");
        Assert.True(fixture.Clock.GetUtcNow() < arrivedAt + Wait);
    }

    /// <summary>
    /// #668 的护栏（control-server#742 不放行的一侧）：第二个取货站乙、丙两条，先扫码前取消乙，再录入并装上丙。本站有了一条 Loaded，
    /// 取消留下的终结归属不让车提前走，照旧从丙落定起等满完整时长，给丙留纠错窗口。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task ACancelAtAStopThatThenLoadsAnotherStillWaitsTheFullDeadline()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        JourneyStopRow secondPickup = await StopEndedJourneyContinuesTests.ArriveAtTheSecondPickupAsync(
            fixture, thirdAtTheSecondPickup: true);
        fixture.Options.StationDepartureWaitTimeout = Wait;
        await CancelTheSecondBeforeScanAsync(fixture);
        await RoundAsync(fixture);
        Assert.False(await DepartureCheckOfStopSentAsync(fixture, secondPickup.StopId), "The third demand was still to load.");

        await EnterSublotAsync(fixture, StopEndedJourneyContinuesTests.ThirdDemandId, StopEndedJourneyContinuesTests.ThirdSublot, ThirdSubmissionId);
        JourneyRuntimeRow settled = await SettleLoadAsync(fixture, StopEndedJourneyContinuesTests.ThirdDemandId);

        Assert.Equal(JourneyRuntimeStage.AwaitingStationDeparture, settled.Stage);
        DateTimeOffset loadedAt = settled.StationDepartureWaitStartedAt!.Value;
        Assert.Equal(loadedAt + Wait, (await LatestWorklistAsync(fixture)).Deadline);
        await AssertHeldUntilAsync(fixture, secondPickup.StopId, loadedAt + Wait);
        await RunAtAsync(fixture, loadedAt + Wait);
        Assert.True(await DepartureCheckOfStopSentAsync(fixture, secondPickup.StopId), "The deadline passed and the vehicle was not sent on.");
    }

    /// <summary>
    /// 同站另有一条还在装（control-server#742 不放行的一侧）：取消乙之后录入丙、装货命令已下、结果没到。本站有一条 Loading，不放行，离站核验不发。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task ACancelAtAStopWhereAnotherIsStillLoadingDoesNotSendTheVehicleOn()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        JourneyStopRow secondPickup = await StopEndedJourneyContinuesTests.ArriveAtTheSecondPickupAsync(
            fixture, thirdAtTheSecondPickup: true);
        fixture.Options.StationDepartureWaitTimeout = Wait;
        await CancelTheSecondBeforeScanAsync(fixture);
        await RoundAsync(fixture);

        JourneyRuntimeRow loading = await EnterSublotAsync(
            fixture, StopEndedJourneyContinuesTests.ThirdDemandId, StopEndedJourneyContinuesTests.ThirdSublot, ThirdSubmissionId);
        await RoundAsync(fixture);
        await RoundAsync(fixture);

        Assert.Equal(JourneyRuntimeStage.AwaitingLoadResult, loading.Stage);
        Assert.Equal(
            JourneyDemandStatuses.Loading,
            (await Batch7MultiDemandAdvanceTests.MembershipAsync(fixture, StopEndedJourneyContinuesTests.ThirdDemandId)).Status);
        Assert.False(
            await DepartureCheckOfStopSentAsync(fixture, secondPickup.StopId), "The vehicle was sent on while a slot was still loading.");
    }

    /// <summary>
    /// 门未证明关闭时扣车（REQ-0364，control-server#742 不改）：取消让本站空了、离站等待当轮结束，但扣车开着，离站核验不发；维修放行之后才发。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task ACancelThatEmptiesTheStopIsStillHeldWhileTheDoorsAreNotProvenClosed()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        JourneyStopRow secondPickup = await StopEndedJourneyContinuesTests.ArriveAtTheSecondPickupAsync(fixture);
        fixture.Options.StationDepartureWaitTimeout = Wait;
        DateTimeOffset arrivedAt = (await JourneyOfAsync(fixture, FirstDemandId)).StationDepartureWaitStartedAt!.Value;
        fixture.Clock.Advance(BeforeCancel);
        await CancelTheSecondBeforeScanAsync(fixture);
        // 取消受理之后才扣：受理那一刻会按扣车重算会话就绪，这里要看的是离站等待那一段之后的扣车闸，不是就绪闸。
        SlotDoorHoldRow hold = new()
        {
            HoldId = Guid.NewGuid().ToString("D"),
            AgvId = fixture.Options.AgvId,
            DemandId = SecondDemandId,
            SlotsJson = "[1]",
            HeldAt = fixture.Clock.GetUtcNow()
        };
        fixture.Context.SlotDoorHolds.Add(hold);
        await fixture.Context.SaveChangesAsync(Token);
        fixture.Context.ChangeTracker.Clear();

        await RoundAsync(fixture);
        await RoundAsync(fixture);

        JourneyRuntimeRow held = await JourneyOfAsync(fixture, FirstDemandId);
        Assert.False(
            await DepartureCheckOfStopSentAsync(fixture, secondPickup.StopId),
            $"The doors were not proven closed and the vehicle was sent on. stage={held.Stage}");
        Assert.Equal(JourneyRuntimeStage.AwaitingStationDeparture, held.Stage);

        SlotDoorHoldRow tracked = await fixture.Context.SlotDoorHolds.SingleAsync(row => row.HoldId == hold.HoldId, Token);
        tracked.ReleasedAt = fixture.Clock.GetUtcNow();
        tracked.ReleasedByActionId = "release";
        await fixture.Context.SaveChangesAsync(Token);
        fixture.Context.ChangeTracker.Clear();
        await RoundAsync(fixture);

        JourneyRuntimeRow released = await JourneyOfAsync(fixture, FirstDemandId);
        SessionRecoveryRow session = await fixture.Context.SessionRecoveries.AsNoTracking().SingleAsync(Token);
        Assert.True(
            await DepartureCheckOfStopSentAsync(fixture, secondPickup.StopId),
            $"The hold was released and the vehicle was not sent on. stage={released.Stage} block={released.BlockReasonCode} " +
            $"start={released.StationDepartureWaitStartedAt:O} readiness={session.Readiness} reason={session.ReasonCode}");
        Assert.True(fixture.Clock.GetUtcNow() < arrivedAt + Wait);
    }

    /// <summary>
    /// 持货等单照旧（用户 10-17 选甲，control-server#742 不改 CARGO_HOLDING_WAIT 那道闸）：车上有前站的货、装货阶段在持货等单时，取消让本站空了，
    /// 本站的离站等待结束，但车照旧停着等，不发离站核验；持货期限到了才走。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task ACancelThatEmptiesTheStopKeepsAHoldingVehicleWaitingForCargo()
    {
        await using RuntimeFixture fixture = await Batch7CargoHoldingTests.HoldingFixtureAsync();
        JourneyStopRow secondPickup = await StopEndedJourneyContinuesTests.ArriveAtTheSecondPickupAsync(fixture);
        fixture.Options.StationDepartureWaitTimeout = Wait;
        DateTimeOffset arrivedAt = (await JourneyOfAsync(fixture, FirstDemandId)).StationDepartureWaitStartedAt!.Value;
        fixture.Clock.Advance(BeforeCancel);
        await CancelTheSecondBeforeScanAsync(fixture);

        await RoundAsync(fixture);
        await RunAtAsync(fixture, arrivedAt + Wait + TimeSpan.FromSeconds(5));

        JourneyRuntimeRow holding = await JourneyOfAsync(fixture, FirstDemandId);
        Assert.Equal(LoadingPhaseStates.CargoHoldingWait, holding.LoadingPhaseState);
        Assert.Equal(JourneyRuntimeStage.AwaitingStationDeparture, holding.Stage);
        Assert.False(
            await DepartureCheckOfStopSentAsync(fixture, secondPickup.StopId), "A vehicle holding cargo was sent on by the cancel.");

        await RunAtAsync(fixture, holding.CargoHoldingStartedAt!.Value + TimeSpan.FromMinutes(30));
        await RoundAsync(fixture);
        Assert.Equal(LoadingClosedReasons.CargoHoldingTimeout, (await JourneyOfAsync(fixture, FirstDemandId)).LoadingClosedReason);
        Assert.True(await DepartureCheckOfStopSentAsync(fixture, secondPickup.StopId), "The holding deadline passed and the vehicle was not sent on.");
    }

    /// <summary>
    /// 下一站同站（control-server#646 那条路不变）：本站一条没装、同站追加了丙（排成下一个停靠），取消乙。照旧按同站宽限结束，不按 #742 立即走，
    /// 车上的期限也照旧是「起点 + 宽限」。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task ACancelThatEmptiesTheStopBeforeASameStationAppendStillWaitsTheGrace()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        JourneyStopRow secondPickup = await StopEndedJourneyContinuesTests.ArriveAtTheSecondPickupAsync(fixture);
        Configure(fixture, Wait);
        DateTimeOffset arrivedAt = (await JourneyOfAsync(fixture, FirstDemandId)).StationDepartureWaitStartedAt!.Value;
        await AppendAtTheSameStationAsync(fixture, secondPickup);
        await CancelTheSecondBeforeScanAsync(fixture);

        await RoundAsync(fixture);

        Assert.True(fixture.Clock.GetUtcNow() < arrivedAt + Grace - TimeSpan.FromSeconds(2));
        Assert.Equal(arrivedAt, (await JourneyOfAsync(fixture, FirstDemandId)).StationDepartureWaitStartedAt);
        Assert.Equal(arrivedAt + Grace, (await LatestWorklistAsync(fixture)).Deadline);
        await RunAtAsync(fixture, arrivedAt + Grace - TimeSpan.FromSeconds(1));
        Assert.False(await DepartureCheckOfStopSentAsync(fixture, secondPickup.StopId), "The vehicle left before the same-station grace.");
        await RunAtAsync(fixture, arrivedAt + Grace);
        Assert.True(await DepartureCheckOfStopSentAsync(fixture, secondPickup.StopId), "The grace passed and the vehicle was not sent on.");
    }

    /// <summary>
    /// 重放（control-server#742）：本站的终结直接写在库里、旅程停在离站等待（重启之后读到的就是这个状态，不经过取消那一路、也不靠内存里的任何东西），
    /// 第一轮就放行。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task AStopFoundEmptiedInTheStoreSendsTheVehicleOnInTheFirstRound()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        JourneyStopRow secondPickup = await StopEndedJourneyContinuesTests.ArriveAtTheSecondPickupAsync(fixture);
        fixture.Options.StationDepartureWaitTimeout = Wait;
        DateTimeOffset arrivedAt = (await JourneyOfAsync(fixture, FirstDemandId)).StationDepartureWaitStartedAt!.Value;
        JourneyDemandRow second = await fixture.Context.Set<JourneyDemandRow>()
            .SingleAsync(row => row.DemandId == SecondDemandId, Token);
        second.Status = JourneyDemandStatuses.Terminated;
        JourneyRuntimeRow runtime = await fixture.Context.JourneyRuntimes.SingleAsync(Token);
        runtime.Stage = JourneyRuntimeStage.AwaitingStationDeparture;
        await fixture.Context.SaveChangesAsync(Token);
        fixture.Context.ChangeTracker.Clear();

        await RoundAsync(fixture);

        Assert.True(
            await DepartureCheckOfStopSentAsync(fixture, secondPickup.StopId),
            $"stage={(await JourneyOfAsync(fixture, FirstDemandId)).Stage}");
        Assert.True(fixture.Clock.GetUtcNow() < arrivedAt + Wait);
    }

    /// <summary>
    /// 审查 M1（PR #674）：取消之后车上已经补上一版期限（此刻 + 完整时长），随后同站追加让离站提前到一个已过的时刻（起点 + 宽限）。
    /// 那一轮就发离站核验，车上最新那一版的期限也跟着到了，不再剩着倒计时。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task ASameStationAppendThatMovesTheDepartureIntoThePastLeavesNoCountdownOnTheVehicle()
    {
        // 持货等单开着：车在离站等待里装载阶段不关，才能再收同站追加（夹具默认装完即关，追加被拒）。
        await using RuntimeFixture fixture = await Batch7CargoHoldingTests.HoldingFixtureAsync();
        Configure(fixture, Wait);
        JourneyStopRow pickup = await LoadTheFirstOfTwoAsync(fixture);
        fixture.Clock.Advance(BeforeCancel);
        await CancelTheSecondBeforeScanAsync(fixture);
        await RoundAsync(fixture);
        DateTimeOffset startedAt = (await JourneyOfAsync(fixture, FirstDemandId)).StationDepartureWaitStartedAt!.Value;
        Assert.Equal(startedAt + Wait, (await LatestWorklistAsync(fixture)).Deadline);

        fixture.Clock.Advance(TimeSpan.FromSeconds(30));
        await AppendAtTheSameStationAsync(fixture, pickup);
        await RunAtAsync(fixture, fixture.Clock.GetUtcNow());

        DateTimeOffset now = fixture.Clock.GetUtcNow();
        Assert.True(await DepartureCheckOfStopSentAsync(fixture, pickup.StopId), "The grace has passed and the vehicle was not sent on.");
        Worklist latest = await LatestWorklistAsync(fixture);
        Assert.True(
            latest.Deadline is { } shown && shown <= now,
            $"The vehicle is leaving at {now:O} while its screen still counts down to {latest.Deadline:O}.");
        Assert.Equal("LOAD_CANCELLED", latest.StopEndedReason);
    }

    /// <summary>
    /// 被期限结束的一站（第二个取货站没人扫码）：期限已过，车当轮就走，车上那张空清单不再多补一版已到期的期限（审查 S3）。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task AStopEndedByItsDeadlineGetsNoExtraWorklistBeforeTheVehicleLeaves()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        // control-server#703：这条测的是站点期限的终结尾巴，那条路今天只在放回次数用完时走（终结并告警）。上限设 0，让第一次期限就走它；放回那一支在 StationTimeoutRedispatchTests。
        fixture.Options.StationTimeoutRedispatchLimit = 0;
        JourneyStopRow secondPickup = await StopEndedJourneyContinuesTests.ArriveAtTheSecondPickupAsync(fixture);
        fixture.Options.StationDepartureWaitTimeout = TimeSpan.FromSeconds(10);
        await fixture.ProveSlotDoorsClosedAsync();
        fixture.Clock.Advance(TimeSpan.FromSeconds(10));
        await RoundAsync(fixture);
        Assert.Equal(
            JourneyDemandStatuses.Terminated, (await Batch7MultiDemandAdvanceTests.MembershipAsync(fixture, SecondDemandId)).Status);
        Worklist ended = await LatestWorklistAsync(fixture);
        Assert.Equal("STATION_DEADLINE_EXPIRED", ended.StopEndedReason);
        Assert.Null(ended.Deadline);

        await RoundAsync(fixture);

        Assert.True(
            await DepartureCheckOfStopSentAsync(fixture, secondPickup.StopId), "The deadline passed and the vehicle was not sent on.");
        Worklist after = await LatestWorklistAsync(fixture);
        Assert.Null(after.Deadline);
        Assert.Equal("STATION_DEADLINE_EXPIRED", after.StopEndedReason);
    }

    private static void Configure(RuntimeFixture fixture, TimeSpan wait)
    {
        fixture.Options.StationDepartureWaitTimeout = wait;
        fixture.Options.SameStationAppendCorrectionGrace = Grace;
    }

    /// <summary>一站两条（甲、乙），甲录入并装完，旅程回到等录入等乙。返回这个取货停靠。</summary>
    private static async Task<JourneyStopRow> LoadTheFirstOfTwoAsync(RuntimeFixture fixture)
    {
        await Batch7MultiDemandAdvanceTests.TwoDemandsAtThePickupAsync(fixture);
        JourneyStopRow pickup = await CurrentStopAsync(fixture, FirstDemandId);
        await EnterSublotAsync(fixture, FirstDemandId, FirstSublot, FirstSubmissionId);
        JourneyRuntimeRow back = await SettleLoadAsync(fixture, FirstDemandId);
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, back.Stage);
        Assert.Equal(JourneyDemandStatuses.Loaded, (await Batch7MultiDemandAdvanceTests.MembershipAsync(fixture, FirstDemandId)).Status);
        return pickup;
    }

    /// <summary>在当前停靠那一站追加丙：成为紧接着的下一个取货停靠（REQ-0196，当前停靠不可改）。</summary>
    private static async Task AppendAtTheSameStationAsync(RuntimeFixture fixture, JourneyStopRow pickup)
    {
        fixture.BoxCounts.Set(AppendedSublot, 4);
        await Batch7ThreeStopJourneyTests.AppendDemandAsync(
            fixture, AppendedDemandId, AppendedSublot, pickup.StationId, pickup.StationRiotId);
        fixture.Context.ChangeTracker.Clear();
        JourneyDemandRow appended = await fixture.Context.Set<JourneyDemandRow>().AsNoTracking()
            .SingleAsync(row => row.DemandId == AppendedDemandId, Token);
        JourneyStopRow appendedStop = await fixture.Context.Set<JourneyStopRow>().AsNoTracking()
            .SingleAsync(row => row.StopId == appended.PickupStopId, Token);
        Assert.Equal(pickup.Sequence + 1, appendedStop.Sequence);
        Assert.Equal(pickup.StationId, appendedStop.StationId);
        Assert.Equal(JourneyStopRoles.Pickup, appendedStop.StopRole);
    }

    /// <summary>操作员对乙扫码前取消，走服务端真实的授权与结果受理；车报 ALL_EMPTY。</summary>
    private static async Task CancelTheSecondBeforeScanAsync(RuntimeFixture fixture)
    {
        await using ControlServerDbContext connection = fixture.OpenConnectionContext();
        OnboardMessageProcessor processor = TestOnboardProcessorFactory.Create(
            connection, new WireToGateStore(connection), fixture.Clock, new ConfigurationBuilder().Build(), fixture.Peer);
        OnboardConnectionState state = StopEndedJourneyContinuesTests.Connection(fixture);
        const string cancellationId = "d6680000-0000-4000-8000-000000000001";
        string authorization = await processor.ProcessAsync(
            StopEndedJourneyContinuesTests.CancellationRequest(fixture, cancellationId), state, Token);
        Assert.Equal(
            "AUTHORIZED",
            StopEndedJourneyContinuesTests.FirstLinePayload(authorization).GetProperty("decision").GetString());
        await processor.ProcessAsync(StopEndedJourneyContinuesTests.CancellationResult(fixture, cancellationId), state, Token);
        fixture.Context.ChangeTracker.Clear();
        Assert.Equal(
            JourneyDemandStatuses.Terminated, (await Batch7MultiDemandAdvanceTests.MembershipAsync(fixture, SecondDemandId)).Status);
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, (await JourneyOfAsync(fixture, FirstDemandId)).Stage);
    }

    /// <summary>连跑两轮、再在 <paramref name="deadline"/> 之前两秒跑一轮：这个停靠的离站核验一直没发，旅程停在离站等待。</summary>
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

    private static async Task<bool> DepartureCheckOfStopSentAsync(RuntimeFixture fixture, string stopId)
    {
        JourneyStopRow stop = await fixture.Context.Set<JourneyStopRow>().AsNoTracking()
            .SingleAsync(row => row.StopId == stopId, Token);
        return stop.DepartureSafetyCheckMessageId is { } messageId &&
               await fixture.Context.ProtocolOutbox.AsNoTracking().AnyAsync(row => row.MessageId == messageId, Token);
    }

    internal sealed record Worklist(
        string[] Demands, DateTimeOffset? Deadline, string? StopEndedReason, string? OperationSessionId);

    /// <summary>发件箱里每一版清单带的离站期限（空的不算）。</summary>
    private static async Task<DateTimeOffset[]> WorklistDeadlinesAsync(RuntimeFixture fixture)
    {
        string[] payloads = await fixture.Context.ProtocolOutbox.AsNoTracking()
            .Where(row => row.MessageType == "CurrentStopWorklistSnapshot")
            .Select(row => row.PayloadJson)
            .ToArrayAsync(Token);
        List<DateTimeOffset> deadlines = [];
        foreach (string json in payloads)
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement at = document.RootElement.GetProperty("payload").GetProperty("stationDepartureDeadlineAt");
            if (at.ValueKind != JsonValueKind.Null) deadlines.Add(at.GetDateTimeOffset());
        }
        return [.. deadlines];
    }

    /// <summary>修订号最高的那一版清单，也就是此刻排给车的那一版。</summary>
    internal static async Task<Worklist> LatestWorklistAsync(RuntimeFixture fixture)
    {
        string[] payloads = await fixture.Context.ProtocolOutbox.AsNoTracking()
            .Where(row => row.MessageType == "CurrentStopWorklistSnapshot")
            .Select(row => row.PayloadJson)
            .ToArrayAsync(Token);
        long best = long.MinValue;
        Worklist? latest = null;
        foreach (string json in payloads)
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement payload = document.RootElement.GetProperty("payload");
            long revision = payload.GetProperty("worklistRevision").GetInt64();
            if (revision <= best) continue;
            best = revision;
            JsonElement at = payload.GetProperty("stationDepartureDeadlineAt");
            JsonElement reason = payload.GetProperty("stopEndedReason");
            JsonElement session = payload.GetProperty("operationSessionId");
            latest = new Worklist(
                [.. payload.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("demandId").GetString()!)],
                at.ValueKind == JsonValueKind.Null ? null : at.GetDateTimeOffset(),
                reason.ValueKind == JsonValueKind.Null ? null : reason.GetString(),
                session.ValueKind == JsonValueKind.Null ? null : session.GetString());
        }
        return Assert.IsType<Worklist>(latest);
    }
}
