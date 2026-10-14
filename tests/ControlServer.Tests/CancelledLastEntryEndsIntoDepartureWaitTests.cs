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
    /// 本站一条都没装上就被取消结束（车上的货是更早一站装的）：起点不动，按原期限离站——没有可纠正的放置，不延长等待。
    /// 与装货途中被终结、本站没装上过货的那一路（<c>resumingAfterEnding</c>）同一个规则。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task ACancelThatEndsAStopWithNothingLoadedThereKeepsTheOriginalDeadline()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        JourneyStopRow secondPickup = await StopEndedJourneyContinuesTests.ArriveAtTheSecondPickupAsync(fixture);
        fixture.Options.StationDepartureWaitTimeout = Wait;
        DateTimeOffset arrivedAt = (await JourneyOfAsync(fixture, FirstDemandId)).StationDepartureWaitStartedAt!.Value;
        fixture.Clock.Advance(BeforeCancel);
        await CancelTheSecondBeforeScanAsync(fixture);

        await RoundAsync(fixture);

        JourneyRuntimeRow waiting = await JourneyOfAsync(fixture, FirstDemandId);
        Assert.Equal(JourneyRuntimeStage.AwaitingStationDeparture, waiting.Stage);
        Assert.Equal(arrivedAt, waiting.StationDepartureWaitStartedAt);
        Assert.Equal(arrivedAt + Wait, (await LatestWorklistAsync(fixture)).Deadline);
        await AssertHeldUntilAsync(fixture, secondPickup.StopId, arrivedAt + Wait);
        await RunAtAsync(fixture, arrivedAt + Wait);
        Assert.True(
            await DepartureCheckOfStopSentAsync(fixture, secondPickup.StopId), "The deadline passed and the vehicle was not sent on.");
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
