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
/// 同一取货站上还有待装时，一个 LoadBatch 安全闭环让离站等待从完整时长重新计时（ADR-cross-0055，control-server#643）。
/// </summary>
/// <remarks>
/// <para>
/// <b>修之前。</b>本站还有待装的那一支只在续跑时补填空起点，正常落定不碰它，期限一直从到站算。探针（一站两条、等待 60 秒、装货 40 秒）
/// 跑出来：闭环之后 20 秒本站就被期限结束，还没录的那一条被终结。车上那一版清单带的也是到站算的期限——两边一致，只是都错。
/// </para>
/// <para>
/// <b>钟要真的走。</b>录入到结果之间拨 <see cref="Loading"/>，并断言新期限与到站算的不同；钟不走时两者相等，「从闭环起算」恒真。
/// </para>
/// </remarks>
public sealed class MiddleBatchDepartureWaitTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(60);

    /// <summary>录入到装货结果回来之间钟走过的时间。比期限短，这一批不会在装货途中到期。</summary>
    private static readonly TimeSpan Loading = TimeSpan.FromSeconds(40);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// 第一批闭环、本站还有一条：起点是闭环那一轮的时刻，车采纳的那一版清单带的期限等于服务端判定用的「闭环 + 等待时长」。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("IntegrationSlice", "FP-IS-08")]
    [Trait("ProtocolVector", "CV-PICKUP-SUBLOT-LOAD")]
    public async Task AMiddleBatchRestartsTheWaitAndTheVehicleHoldsTheSameDeadline()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Options.StationDepartureWaitTimeout = Wait;
        AdoptingPeer vehicle = Attach(fixture);
        (DateTimeOffset atArrival, DateTimeOffset closedAt) = await CloseTheFirstOfTwoBatchesAsync(fixture, vehicle);

        JourneyRuntimeRow runtime = await JourneyOfAsync(fixture, FirstDemandId);
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, runtime.Stage);
        Assert.NotEqual(atArrival, closedAt);
        Assert.Equal(closedAt, runtime.StationDepartureWaitStartedAt);
        DateTimeOffset server = JourneyRuntimeEngine.StationDepartureDeadline(runtime, Wait)!.Value;
        Assert.Equal(closedAt + Wait, server);
        Assert.Empty(vehicle.Conflicts);
        Assert.Empty(vehicle.Regressions);
        Assert.Equal(server, VehicleDeadline(vehicle));

        // 下一轮等录入那一处不再挪它，也不再为它多发一版。
        int worklists = vehicle.Adopted.Count(item => item.MessageType == "CurrentStopWorklistSnapshot");
        await fixture.HearFromPeerAsync();
        await TickAndRunAsync(fixture);
        await vehicle.DeliverBufferedAcksAsync();
        Assert.Equal(closedAt, (await JourneyOfAsync(fixture, FirstDemandId)).StationDepartureWaitStartedAt);
        Assert.Equal(worklists, vehicle.Adopted.Count(item => item.MessageType == "CurrentStopWorklistSnapshot"));
    }

    /// <summary>
    /// 到站算的期限过了、闭环算的还没过：本站不结束，还没录的那一条照样等着；闭环算的期限过了才结束。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task TheStopOutlivesTheArrivalDeadlineAndEndsAtTheRestartedOne()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Options.StationDepartureWaitTimeout = Wait;
        (DateTimeOffset atArrival, DateTimeOffset closedAt) = await CloseTheFirstOfTwoBatchesAsync(fixture, vehicle: null);
        await fixture.ProveSlotDoorsClosedAsync();

        await RunAtAsync(fixture, atArrival + Wait + TimeSpan.FromSeconds(1));
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, (await JourneyOfAsync(fixture, FirstDemandId)).Stage);
        Assert.Equal(
            JourneyDemandStatuses.PendingLoad, (await Batch7MultiDemandAdvanceTests.MembershipAsync(fixture, FirstDemandId)).Status);

        await RunAtAsync(fixture, closedAt + Wait);
        Assert.Equal(
            JourneyDemandStatuses.Terminated, (await Batch7MultiDemandAdvanceTests.MembershipAsync(fixture, FirstDemandId)).Status);
        Assert.Equal(
            JourneyDemandStatuses.Loaded, (await Batch7MultiDemandAdvanceTests.MembershipAsync(fixture, SecondDemandId)).Status);
    }

    /// <summary>
    /// 最后一批照旧：两条都装完，起点是最后那一批闭环的时刻（原来就如此，本票不改它）。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task TheLastBatchStillRestartsTheWaitAtItsOwnClosure()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Options.StationDepartureWaitTimeout = Wait;
        await CloseTheFirstOfTwoBatchesAsync(fixture, vehicle: null);
        JourneyRuntimeRow runtime = await JourneyOfAsync(fixture, FirstDemandId);

        await AddInboxAsync(
            fixture, SecondSubmissionId, "SublotSubmitted", await SublotSubmissionAsync(fixture, runtime, FirstSublot));
        await TickAndRunAsync(fixture);
        fixture.Clock.Advance(Loading);
        await ApplySafeResultAsync(fixture, FirstDemandId, SlotOperationType.Load, SlotBusinessState.Occupied);
        await fixture.HearFromPeerAsync();
        await fixture.Engine.ExecuteOnceAsync(Token);

        JourneyRuntimeRow after = await JourneyOfAsync(fixture, FirstDemandId);
        Assert.Equal(JourneyRuntimeStage.AwaitingStationDeparture, after.Stage);
        Assert.Equal(fixture.Clock.GetUtcNow(), after.StationDepartureWaitStartedAt);
    }

    /// <summary>
    /// 崩在结果已落库、阶段还没前移的那一刻（#289 的续跑），重启后：起点是续跑那一轮的时刻，发出的那一版清单带的就是它算出的期限。
    /// </summary>
    /// <remarks>
    /// 崩点选在那一版清单写进发件箱时：LOADED 已在前一次保存里落库，起点的改动与清单在同一次保存里，一起丢。续跑取此刻而不是闭环那一轮，
    /// 代价是期限推后宕机的时长（与 control-server#633 取服务端此刻同一个取舍）。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task AResumeAfterACrashBeforeTheStageMovedRestartsTheWaitAtTheResumingRound()
    {
        UnloadCrashResumeTests.FailOnce crash = new(command =>
            command.CommandText.Contains("INSERT INTO \"ProtocolOutbox\"", StringComparison.Ordinal) &&
            UnloadCrashResumeTests.HasParameter(command, "CurrentStopWorklistSnapshot"));
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(commands: crash);
        fixture.Options.StationDepartureWaitTimeout = Wait;
        JourneyRuntimeRow runtime = await Batch7MultiDemandAdvanceTests.TwoDemandsAtThePickupAsync(fixture);
        DateTimeOffset atArrival = runtime.StationDepartureWaitStartedAt!.Value;
        await AddInboxAsync(
            fixture, FirstSubmissionId, "SublotSubmitted", await SublotSubmissionAsync(fixture, runtime, SecondSublot));
        await TickAndRunAsync(fixture);
        fixture.Clock.Advance(Loading);
        await ApplySafeResultAsync(fixture, SecondDemandId, SlotOperationType.Load, SlotBusinessState.Occupied);
        await fixture.HearFromPeerAsync();
        crash.Armed = true;
        await TickAndRunExpectingCrashAsync(fixture);
        Assert.True(crash.Fired, "The injected failure never fired, so this proved nothing.");
        DateTimeOffset crashedAt = fixture.Clock.GetUtcNow();

        await fixture.RecreateEngineAsync();
        JourneyRuntimeRow crashed = await JourneyOfAsync(fixture, FirstDemandId);
        // 前提：崩在阶段前移之前、LOADED 之后——否则这一轮走的不是续跑那一支。
        Assert.Equal(JourneyRuntimeStage.AwaitingLoadResult, crashed.Stage);
        Assert.Equal(
            JourneyDemandStatuses.Loaded, (await Batch7MultiDemandAdvanceTests.MembershipAsync(fixture, SecondDemandId)).Status);
        Assert.Equal(atArrival, crashed.StationDepartureWaitStartedAt);

        fixture.Clock.Advance(TimeSpan.FromSeconds(7));
        await fixture.HearFromPeerAsync();
        await fixture.Engine.ExecuteOnceAsync(Token);
        DateTimeOffset resumedAt = fixture.Clock.GetUtcNow();

        JourneyRuntimeRow after = await JourneyOfAsync(fixture, FirstDemandId);
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, after.Stage);
        Assert.NotEqual(crashedAt, resumedAt);
        Assert.Equal(resumedAt, after.StationDepartureWaitStartedAt);
        Assert.Equal(resumedAt + Wait, (await LatestWorklistAsync(fixture)).Deadline);
    }

    /// <summary>
    /// 本站在装那一条被终结、同站还有一条（resumingAfterEnding）：一条都没装上，不是 LoadBatch 闭环，起点不动。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task AnEndedBatchWithAnotherStillToLoadDoesNotRestartTheWait()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        // 夹具在第一站不等离站，所以期限在到了第二站之后才打开（与 LoadEndedAtASecondPickupTests 同一个做法）；起点在到站时已种下。
        await StopEndedJourneyContinuesTests.ArriveAtTheSecondPickupAsync(fixture, thirdAtTheSecondPickup: true);
        fixture.Options.StationDepartureWaitTimeout = Wait;
        DateTimeOffset atArrival = (await JourneyOfAsync(fixture, FirstDemandId)).StationDepartureWaitStartedAt!.Value;
        await EnterSublotAsync(fixture, SecondDemandId, SecondSublot, SecondSubmissionId);
        fixture.Clock.Advance(TimeSpan.FromSeconds(20));
        await LoadEndedAtASecondPickupTests.CancelTheSecondDemandWhileLoadingAsync(fixture);

        await fixture.HearFromPeerAsync();
        await TickAndRunAsync(fixture);

        JourneyRuntimeRow after = await JourneyOfAsync(fixture, FirstDemandId);
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, after.Stage);
        Assert.Equal(
            JourneyDemandStatuses.Terminated, (await Batch7MultiDemandAdvanceTests.MembershipAsync(fixture, SecondDemandId)).Status);
        Assert.NotEqual(fixture.Clock.GetUtcNow(), atArrival);
        Assert.Equal(atArrival, after.StationDepartureWaitStartedAt);
        Assert.Equal(atArrival + Wait, (await LatestWorklistAsync(fixture)).Deadline);
    }

    /// <summary>
    /// 一站两条：到站、录第二条、钟走 <see cref="Loading"/>、第二条安全闭环、跑一轮。返回到站时的起点与闭环那一轮的时刻。
    /// </summary>
    private static async Task<(DateTimeOffset AtArrival, DateTimeOffset ClosedAt)> CloseTheFirstOfTwoBatchesAsync(
        RuntimeFixture fixture, AdoptingPeer? vehicle)
    {
        JourneyRuntimeRow runtime = await Batch7MultiDemandAdvanceTests.TwoDemandsAtThePickupAsync(fixture);
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, runtime.Stage);
        DateTimeOffset atArrival = runtime.StationDepartureWaitStartedAt!.Value;
        await AddInboxAsync(
            fixture, FirstSubmissionId, "SublotSubmitted", await SublotSubmissionAsync(fixture, runtime, SecondSublot));
        await TickAndRunAsync(fixture);
        Assert.Equal(JourneyRuntimeStage.AwaitingLoadResult, (await JourneyOfAsync(fixture, FirstDemandId)).Stage);
        if (vehicle is not null) await vehicle.DeliverBufferedAcksAsync();

        fixture.Clock.Advance(Loading);
        await ApplySafeResultAsync(fixture, SecondDemandId, SlotOperationType.Load, SlotBusinessState.Occupied);
        await fixture.HearFromPeerAsync();
        await fixture.Engine.ExecuteOnceAsync(Token);
        if (vehicle is not null) await vehicle.DeliverBufferedAcksAsync();
        return (atArrival, fixture.Clock.GetUtcNow());
    }

    private static async Task RunAtAsync(RuntimeFixture fixture, DateTimeOffset at)
    {
        fixture.Clock.Advance(at - fixture.Clock.GetUtcNow());
        await fixture.HearFromPeerAsync();
        await fixture.Engine.ExecuteOnceAsync(Token);
    }

    /// <summary>车：收到的快照按车载端的规则采纳，确认由用例决定何时送回。</summary>
    private static AdoptingPeer Attach(RuntimeFixture fixture)
    {
        AdoptingPeer vehicle = new(fixture.Context, fixture.Clock);
        fixture.Peer.OnMessageSent = line =>
        {
            vehicle.Receive(line);
            return Task.CompletedTask;
        };
        return vehicle;
    }

    private static DateTimeOffset VehicleDeadline(AdoptingPeer vehicle) =>
        (vehicle.HeldPayload("CurrentStopWorklistSnapshot")
         ?? throw new InvalidOperationException("The vehicle holds no worklist."))
        .GetProperty("stationDepartureDeadlineAt").GetDateTimeOffset();

    /// <summary>修订号最高的那一版清单的期限，也就是此刻排给车的那一版。</summary>
    private static async Task<(long Revision, DateTimeOffset? Deadline)> LatestWorklistAsync(RuntimeFixture fixture)
    {
        string[] payloads = await fixture.Context.ProtocolOutbox.AsNoTracking()
            .Where(row => row.MessageType == "CurrentStopWorklistSnapshot")
            .Select(row => row.PayloadJson)
            .ToArrayAsync(Token);
        long best = long.MinValue;
        DateTimeOffset? deadline = null;
        foreach (string json in payloads)
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement payload = document.RootElement.GetProperty("payload");
            long revision = payload.GetProperty("worklistRevision").GetInt64();
            if (revision <= best) continue;
            best = revision;
            JsonElement at = payload.GetProperty("stationDepartureDeadlineAt");
            deadline = at.ValueKind == JsonValueKind.Null ? null : at.GetDateTimeOffset();
        }
        return (best, deadline);
    }
}
