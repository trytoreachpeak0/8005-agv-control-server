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
/// 一站几条需求，扫码前取消了其中一条、这一站还有别的待做项：车上要收到去掉它的新一版清单与录入请求（control-server#723）。
/// </summary>
/// <remarks>
/// <para>
/// <b>修之前。</b>取消那一路（<c>OnboardRecoveryCoordinator</c> → <c>PickupStopTermination</c>）终结需求，按 <c>JourneyStopCursor.WorklistRevisionAt</c>
/// 本站的号随之前进一号，但只有「本站因此结束」时 <c>StopEndWorklist</c> 才发一张空清单；还剩待做项时谁也不发。引擎在等录入阶段只在期限变了时
/// 重发清单。车上留着旧的那一版，屏上仍列着已取消的需求、录入请求仍期待它的子批。10-15 生产 agv02：操作员以为没取消成功，重按三次，
/// 车载端每次重记一份 pending（只差 <c>operatorVerifiedAt</c>），服务端判 <c>BUSINESS_ID_CONTENT_CONFLICT</c>。
/// </para>
/// <para>
/// 待送点规则开关（<c>JourneyRuntime:StagingDepartureRules</c>）开、关两种都测：开着时这里是机台取货停靠，扫码前取消照旧授权。
/// </para>
/// </remarks>
public sealed class CancelBeforeScanRefreshesWorklistTests
{
    private const string CancellationId = "c7230000-0000-4000-8000-000000000001";
    private const string ThirdDemandId = "10000000-0000-4000-8000-000000000003";
    private const string ThirdSublot = "SUBLOT-003";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>两条都待录入，取消乙：下一轮车上最新那一版清单只列甲、号更高，录入请求同号只期待甲；本站较早各版都不会再被补发。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task ACancelBeforeScanThatLeavesTheStopOpenSendsAWorklistWithoutTheCancelledDemand(bool stagingRules)
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Options.StagingDepartureRules.Enabled = stagingRules;
        await Batch7MultiDemandAdvanceTests.TwoDemandsAtThePickupAsync(fixture);
        Sent before = await LatestAsync(fixture, "CurrentStopWorklistSnapshot");
        Assert.Equal([FirstDemandId, SecondDemandId], before.Demands);

        await CancelTheSecondBeforeScanAsync(fixture);
        await RoundAsync(fixture);

        Sent worklist = await LatestAsync(fixture, "CurrentStopWorklistSnapshot");
        Assert.True(
            worklist.Revision > before.Revision,
            $"The cancelled demand ended but the vehicle still holds worklist revision {before.Revision}.");
        Assert.Equal([FirstDemandId], worklist.Demands);
        Assert.Equal(before.OperationSessionId, worklist.OperationSessionId);
        Sent entry = await LatestAsync(fixture, "SublotEntryRequested");
        Assert.Equal(worklist.Revision, entry.Revision);
        Assert.Equal([FirstSublot], entry.Sublots);
        await AssertNothingOlderIsReplayedAsync(fixture, worklist.Revision);
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, (await JourneyOfAsync(fixture, FirstDemandId)).Stage);

        // 一轮之后不再多发：号停在这一版。
        await RoundAsync(fixture);
        Assert.Equal(worklist.Revision, (await LatestAsync(fixture, "CurrentStopWorklistSnapshot")).Revision);

        // 甲照常录入、开始装。
        JourneyRuntimeRow loading = await EnterSublotAsync(fixture, FirstDemandId, FirstSublot, FirstSubmissionId);
        Assert.Equal(JourneyRuntimeStage.AwaitingLoadResult, loading.Stage);
    }

    /// <summary>甲正在装时取消乙：下一轮车上最新那一版清单只列甲；甲在装，没有开着的录入，不发录入请求。甲装完，本站结束、车照常往下走（夹具离站等待为 0，当轮就发离站核验）。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task ACancelBeforeScanWhileAnotherLoadsSendsAWorklistWithoutTheCancelledDemand(bool stagingRules)
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Options.StagingDepartureRules.Enabled = stagingRules;
        await Batch7MultiDemandAdvanceTests.TwoDemandsAtThePickupAsync(fixture);
        Assert.Equal(
            JourneyRuntimeStage.AwaitingLoadResult,
            (await EnterSublotAsync(fixture, FirstDemandId, FirstSublot, FirstSubmissionId)).Stage);
        Sent before = await LatestAsync(fixture, "CurrentStopWorklistSnapshot");
        Sent entryBefore = await LatestAsync(fixture, "SublotEntryRequested");

        await CancelTheSecondBeforeScanAsync(fixture);
        await RoundAsync(fixture);

        Sent worklist = await LatestAsync(fixture, "CurrentStopWorklistSnapshot");
        Assert.True(
            worklist.Revision > before.Revision,
            $"The cancelled demand ended but the vehicle still holds worklist revision {before.Revision}.");
        Assert.Equal([FirstDemandId], worklist.Demands);
        Assert.Equal(entryBefore.Revision, (await LatestAsync(fixture, "SublotEntryRequested")).Revision);
        await AssertNothingOlderIsReplayedAsync(fixture, worklist.Revision, "CurrentStopWorklistSnapshot");

        JourneyRuntimeRow settled = await SettleLoadAsync(fixture, FirstDemandId);
        Assert.Equal(JourneyRuntimeStage.AwaitingDepartureSafety, settled.Stage);
    }

    /// <summary>
    /// 一站三条，引擎两轮之间乙被扫码前取消、丙又被终结：号跳过一号。车上那一版（到站那一版）也要退役——只退上一号的话，它留着未确认，
    /// 重连补发时排在新的一版之后送到，车判修订号回退。
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task TwoEndingsBetweenRoundsRetireTheVersionStillOnTheVehicle(bool stagingRules)
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Options.StagingDepartureRules.Enabled = stagingRules;
        await ThreeDemandsAtThePickupAsync(fixture);
        Sent before = await LatestAsync(fixture, "CurrentStopWorklistSnapshot");
        Assert.Equal([FirstDemandId, SecondDemandId, ThirdDemandId], before.Demands);

        await CancelTheSecondBeforeScanAsync(fixture);
        string journeyId = (await JourneyOfAsync(fixture, FirstDemandId)).JourneyId;
        JourneyRuntimeRow tracked = await fixture.Context.JourneyRuntimes.SingleAsync(row => row.JourneyId == journeyId, Token);
        await new PickupStopTermination(fixture.Context).StageAsync(
            tracked, ThirdDemandId, "CANCELLED_BY_OPERATOR", fixture.Clock.GetUtcNow(), Token);
        await fixture.Context.SaveChangesAsync(Token);
        fixture.Context.ChangeTracker.Clear();
        await RoundAsync(fixture);

        Sent worklist = await LatestAsync(fixture, "CurrentStopWorklistSnapshot");
        Assert.Equal(before.Revision + 2, worklist.Revision);
        Assert.Equal([FirstDemandId], worklist.Demands);
        Assert.Equal([FirstSublot], (await LatestAsync(fixture, "SublotEntryRequested")).Sublots);
        await AssertNothingOlderIsReplayedAsync(fixture, worklist.Revision);
    }

    /// <summary>同 <c>Batch7MultiDemandAdvanceTests.TwoDemandsAtThePickupAsync</c>，到站之前挂上三条。</summary>
    private static async Task ThreeDemandsAtThePickupAsync(RuntimeFixture fixture)
    {
        fixture.Catalog.Set(fixture.Demand(FirstDemandId, FirstSublot, Now.AddMinutes(-10)));
        fixture.BoxCounts.Set(FirstSublot, 4);
        fixture.BoxCounts.Set(SecondSublot, 4);
        fixture.BoxCounts.Set(ThirdSublot, 4);
        await TickAndRunAsync(fixture);
        JourneyRuntimeRow runtime = await fixture.RuntimeAsync(FirstDemandId);
        await Batch7MultiDemandAdvanceTests.AddSecondDemandToJourneyAsync(fixture, runtime);
        await Batch7MultiDemandAdvanceTests.AddDemandToJourneyAsync(fixture, runtime, ThirdDemandId, ThirdSublot, slotBlock: 2);
        fixture.Riot.SetSuccessfulArrival("TO_PICKUP", runtime.PickupUpperId, runtime.PickupStationRiotId);
        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { CurrentStationId = runtime.PickupStationRiotId };
        await TickAndRunAsync(fixture);
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, (await fixture.RuntimeAsync(FirstDemandId)).Stage);
    }

    /// <summary>操作员对乙扫码前取消，走服务端真实的授权与结果受理；车报 ALL_EMPTY。乙终结，甲还在本站。</summary>
    private static async Task CancelTheSecondBeforeScanAsync(RuntimeFixture fixture)
    {
        await using ControlServerDbContext connection = fixture.OpenConnectionContext();
        OnboardMessageProcessor processor = TestOnboardProcessorFactory.Create(
            connection, new WireToGateStore(connection), fixture.Clock, new ConfigurationBuilder().Build(), fixture.Peer,
            runtimeOptions: fixture.Options);
        OnboardConnectionState state = StopEndedJourneyContinuesTests.Connection(fixture);
        string authorization = await processor.ProcessAsync(
            StopEndedJourneyContinuesTests.CancellationRequest(fixture, CancellationId), state, Token);
        Assert.Equal(
            "AUTHORIZED",
            StopEndedJourneyContinuesTests.FirstLinePayload(authorization).GetProperty("decision").GetString());
        await processor.ProcessAsync(StopEndedJourneyContinuesTests.CancellationResult(fixture, CancellationId), state, Token);
        fixture.Context.ChangeTracker.Clear();
        Assert.Equal(
            JourneyDemandStatuses.Terminated, (await Batch7MultiDemandAdvanceTests.MembershipAsync(fixture, SecondDemandId)).Status);
        Assert.NotEqual(
            JourneyDemandStatuses.Terminated, (await Batch7MultiDemandAdvanceTests.MembershipAsync(fixture, FirstDemandId)).Status);
    }

    private static async Task RoundAsync(RuntimeFixture fixture)
    {
        await fixture.HearFromPeerAsync();
        await TickAndRunAsync(fixture);
    }

    /// <summary>
    /// 比 <paramref name="latest"/> 低的清单（与录入请求）没有一行还会被补发：要么车确认过，要么已退役。补发按未确认的行送，
    /// 旧号排在新号之后送到，车判修订号回退而断会话。
    /// </summary>
    private static async Task AssertNothingOlderIsReplayedAsync(
        RuntimeFixture fixture, long latest, params string[] types)
    {
        string[] checkedTypes = types.Length > 0 ? types : ["CurrentStopWorklistSnapshot", "SublotEntryRequested"];
        ProtocolOutboxRow[] rows = await fixture.Context.ProtocolOutbox.AsNoTracking()
            .Where(row => checkedTypes.Contains(row.MessageType) && row.AcknowledgedAt == null && row.FencedAt == null)
            .ToArrayAsync(Token);
        foreach (ProtocolOutboxRow row in rows)
        {
            using JsonDocument document = JsonDocument.Parse(row.PayloadJson);
            long revision = document.RootElement.GetProperty("payload").GetProperty("worklistRevision").GetInt64();
            Assert.True(
                revision >= latest,
                $"{row.MessageType} revision {revision} is still pending replay behind revision {latest}.");
        }
    }

    private sealed record Sent(long Revision, string[] Demands, string[] Sublots, string? OperationSessionId);

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
            string? session = payload.TryGetProperty("operationSessionId", out JsonElement value) &&
                              value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
            latest = new Sent(revision, demands, sublots, session);
        }
        return Assert.IsType<Sent>(latest);
    }
}
