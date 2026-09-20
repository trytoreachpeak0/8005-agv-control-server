using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using static ControlServer.Tests.Batch7StopDrivenAdvanceDriver;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;

namespace ControlServer.Tests;

/// <summary>
/// 批次7-03（control-server#208）里，逐字对照看不见的那几件事：推进到一半崩掉之后重启还对不对，修订号跨旅程还单调不单调，
/// 离站核验填的是哪一条需求。
/// </summary>
/// <remarks>
/// <see cref="Batch7StopDrivenAdvanceWireParityTests"/> 钉的是「一趟跑完发了什么」，它看不见中途崩溃，也说不出
/// 「为什么两趟之间不回退」——那要单独断言。
/// </remarks>
public sealed class Batch7StopDrivenAdvanceTests
{
    /// <summary>
    /// 装货落定的那一轮里，服务端一口气走完三段：结算装货命令、进入离站等待、发离站核验。<c>goto case</c> 把它们串在
    /// 一轮里，中间没有返回点。在发核验那一刻崩掉，重启之后核验的身份必须还是同一个。
    /// </summary>
    /// <remarks>
    /// 本票把这个身份从旅程行搬到了停靠行，所以「崩溃前后是不是同一个 id」正是要盯的：停靠行没写成，重启后会派生出
    /// 第二个身份，车载端就会收到两条它认不出关系的核验请求。计数器也一并断言没动——它只在受理时推进，推进段一次
    /// 崩溃不该碰它。
    /// </remarks>
    [Fact]
    public async Task ACrashWhileAskingForDepartureSafetyLeavesTheSameCheckIdentityBehind()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(FirstDemandId, FirstSublot, Now.AddMinutes(-10)));
        fixture.BoxCounts.Set(FirstSublot, 4);
        await ArriveAtPickupAsync(fixture, FirstDemandId);
        await EnterSublotAsync(fixture, FirstDemandId, FirstSublot, FirstSubmissionId);
        VehicleSnapshotRevisionRow counterBefore = await CounterAsync(fixture);

        await ApplySafeResultAsync(fixture, FirstDemandId, SlotOperationType.Load, SlotBusinessState.Occupied);
        fixture.Peer.OnMessageSent = CrashOn("PreDepartureSafetyCheck");
        await TickAndRunExpectingCrashAsync(fixture);

        JourneyStopRow stopAfterCrash = await PickupStopAsync(fixture, FirstDemandId);
        string checkId = stopAfterCrash.DepartureSafetyCheckId!;
        string checkMessageId = stopAfterCrash.DepartureSafetyCheckMessageId!;

        fixture.Peer.OnMessageSent = null;
        await fixture.RecreateEngineAsync();
        await TickAndRunAsync(fixture);

        JourneyStopRow stopAfterRestart = await PickupStopAsync(fixture, FirstDemandId);
        Assert.Equal(checkId, stopAfterRestart.DepartureSafetyCheckId);
        Assert.Equal(checkMessageId, stopAfterRestart.DepartureSafetyCheckMessageId);
        Assert.Equal(
            1,
            await fixture.Context.ProtocolOutbox.CountAsync(
                row => row.MessageType == "PreDepartureSafetyCheck", TestContext.Current.CancellationToken));
        VehicleSnapshotRevisionRow counterAfter = await CounterAsync(fixture);
        Assert.Equal(
            (counterBefore.VehicleBusinessRevision, counterBefore.WorklistRevision, counterBefore.PlanRevision),
            (counterAfter.VehicleBusinessRevision, counterAfter.WorklistRevision, counterAfter.PlanRevision));
    }

    /// <summary>
    /// 同一段，但这一轮是从「已经在等离站」进来的：离站等待还没走完时旅程停在 <c>AwaitingStationDeparture</c>，
    /// 下一轮才发核验。这条盯的是 <c>goto case</c> 链的另一个入口。
    /// </summary>
    [Fact]
    public async Task ACrashEnteringDepartureSafetyFromTheStationWaitLeavesTheSameCheckIdentityBehind()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Options.StationDepartureWaitTimeout = TimeSpan.FromSeconds(30);
        fixture.Catalog.Set(fixture.Demand(FirstDemandId, FirstSublot, Now.AddMinutes(-10)));
        fixture.BoxCounts.Set(FirstSublot, 4);
        await ArriveAtPickupAsync(fixture, FirstDemandId);
        await EnterSublotAsync(fixture, FirstDemandId, FirstSublot, FirstSubmissionId);

        // 装货落定，但离站等待还没走完：这一轮停在 AwaitingStationDeparture，核验还没发。
        await ApplySafeResultAsync(fixture, FirstDemandId, SlotOperationType.Load, SlotBusinessState.Occupied);
        await TickAndRunAsync(fixture);
        Assert.Equal(
            JourneyRuntimeStage.AwaitingStationDeparture,
            (await fixture.RuntimeAsync(FirstDemandId)).Stage);
        Assert.Empty(await fixture.Context.ProtocolOutbox
            .Where(row => row.MessageType == "PreDepartureSafetyCheck")
            .ToArrayAsync(TestContext.Current.CancellationToken));

        fixture.Clock.Advance(TimeSpan.FromSeconds(30));
        fixture.Peer.OnMessageSent = CrashOn("PreDepartureSafetyCheck");
        await TickAndRunExpectingCrashAsync(fixture);

        JourneyStopRow stopAfterCrash = await PickupStopAsync(fixture, FirstDemandId);
        string checkMessageId = stopAfterCrash.DepartureSafetyCheckMessageId!;

        fixture.Peer.OnMessageSent = null;
        await fixture.RecreateEngineAsync();
        await TickAndRunAsync(fixture);

        Assert.Equal(checkMessageId, (await PickupStopAsync(fixture, FirstDemandId)).DepartureSafetyCheckMessageId);
        Assert.Equal(
            1,
            await fixture.Context.ProtocolOutbox.CountAsync(
                row => row.MessageType == "PreDepartureSafetyCheck", TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// 第三个 <c>goto case</c>：核验过期后就地重发，重发完原地再判一次答复。在重发那一刻崩掉，重启之后不能再派生出
    /// 第三个身份——那样车上会积三条核验请求，而服务端只认得最后一条。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>它顺带钉住了「一次保存里落下」这件事。</b>新身份写停靠行、报文写发件箱，两者落在同一次 <c>SaveChanges</c>
    /// 里（发布走的是引擎自己那个 <c>DbContext</c>），所以崩在发送那一刻，库里没有「报文发了、身份没落」的半截状态。
    /// 哪天有人把发布挪到独立的上下文或独立事务里，这条会红在崩溃后那次比对上。
    /// </para>
    /// <para>
    /// <b>重启不会派生出第三个身份</b>，靠的是派生本身确定——<c>StableGuid(旧 id, "reissued-after-expiry")</c>——
    /// 重启之后从同一个旧 id 出发算出同一个新 id，发件箱按 messageId 认出那一行是同一条。哪天有人把这个派生改成带
    /// 随机数或带时间的，这条会红在「第三条核验请求」上。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ACrashWhileReissuingAnExpiredCheckDoesNotDeriveAThirdIdentity()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(FirstDemandId, FirstSublot, Now.AddMinutes(-10)));
        fixture.BoxCounts.Set(FirstSublot, 4);
        await ArriveAtPickupAsync(fixture, FirstDemandId);
        await EnterSublotAsync(fixture, FirstDemandId, FirstSublot, FirstSubmissionId);
        JourneyRuntimeRow runtime = await SettleLoadAsync(fixture, FirstDemandId);
        string firstCheckId = runtime.PreDepartureSafetyCheckId;

        await AddInboxAsync(
            fixture, FirstSafetyResultId, "PreDepartureSafetyCheckResult",
            SafeDepartureAnswer(fixture, firstCheckId, safetyStateVersion: 7), firstCheckId);
        await fixture.AddSafetyStateChangedAsync(8, departureSafe: true, vehicleStopped: true);
        // 只崩在重发的那一条上。每轮开头的重放会把第一条核验（还没被确认）再发一遍，按类型崩会崩在那里，
        // 根本走不到重发——第一次这么写，测出来发件箱里只有一条核验，正是崩错了地方。
        fixture.Peer.OnMessageSent = line =>
            line.Contains("\"messageType\":\"PreDepartureSafetyCheck\"", StringComparison.Ordinal) &&
            !line.Contains(firstCheckId, StringComparison.Ordinal)
                ? throw new IOException("crash while reissuing the expired check")
                : Task.CompletedTask;
        await TickAndRunExpectingCrashAsync(fixture);

        // 崩在发送那一刻，库里已经是一致的：发件箱多了第二条核验，停靠行也已经指着它。写新身份与写发件箱行落在
        // 同一次 SaveChanges 里（发布走的是同一个 DbContext），所以这里没有「报文发了、身份没落」的半截状态。
        string[] checksAfterCrash = await DepartureCheckMessageIdsAsync(fixture);
        Assert.Equal(2, checksAfterCrash.Length);
        string reissuedMessageId = checksAfterCrash[1];
        Assert.NotEqual(runtime.PreDepartureSafetyCheckMessageId, reissuedMessageId);
        Assert.Equal(
            reissuedMessageId,
            (await PickupStopAsync(fixture, FirstDemandId)).DepartureSafetyCheckMessageId);

        fixture.Peer.OnMessageSent = null;
        await fixture.RecreateEngineAsync();
        await TickAndRunAsync(fixture);

        // 重启后派生出同一个新身份，发件箱认出那一行是同一条，没有第三条。
        Assert.Equal(checksAfterCrash, await DepartureCheckMessageIdsAsync(fixture));
        Assert.Equal(reissuedMessageId, (await PickupStopAsync(fixture, FirstDemandId)).DepartureSafetyCheckMessageId);
    }

    /// <summary>
    /// 离站核验里的 <c>demandId</c> 取锚需求，也就是旅程行上那一条。
    /// </summary>
    /// <remarks>
    /// 协议只放得下一个 <c>demandId</c>，而离站安全本来就是一次整车判断，不是对某一条需求的判断。多需求时填第一条
    /// 受理的需求（票面「已定」一节），那就是锚需求。
    /// </remarks>
    [Fact]
    public async Task ThePreDepartureCheckNamesTheAnchorDemand()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(FirstDemandId, FirstSublot, Now.AddMinutes(-10)));
        fixture.BoxCounts.Set(FirstSublot, 4);
        await ArriveAtPickupAsync(fixture, FirstDemandId);
        await EnterSublotAsync(fixture, FirstDemandId, FirstSublot, FirstSubmissionId);
        JourneyRuntimeRow runtime = await SettleLoadAsync(fixture, FirstDemandId);

        JsonElement payload = await OutboundPayloadAsync(fixture, runtime.PreDepartureSafetyCheckMessageId);
        Assert.Equal(runtime.DemandId, payload.GetProperty("demandId").GetString());
        Assert.Equal(
            runtime.PreDepartureSafetyCheckId, payload.GetProperty("preDepartureSafetyCheckId").GetString());
    }

    /// <summary>
    /// 同一辆车第二趟的第一条快照，修订号要大于第一趟的最后一条——三条流各自都要。
    /// </summary>
    /// <remarks>
    /// 车载端按消息类型记住已采纳的修订号，而那个记录跨重连、跨旅程都不清空（<c>WireToGateStore.cs</c> 注释引的
    /// <c>OnboardHmi_MVP</c> 行为）。所以第二趟哪怕只回退一号，收到的那一刻就是 <c>SNAPSHOT_REVISION_REGRESSION</c>，
    /// 会话当场断。本票把修订号的来源换成按车计数器，这条就是那次搬家的安全网。
    /// </remarks>
    [Fact]
    public async Task TheFirstSnapshotOfTheSecondJourneyOutranksTheLastOfTheFirst()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(FirstDemandId, FirstSublot, Now.AddMinutes(-10)));
        fixture.BoxCounts.Set(FirstSublot, 4);
        await RunJourneyToCompletionAsync(
            fixture, FirstDemandId, FirstSublot, FirstSubmissionId, FirstSafetyResultId);
        Dictionary<string, long> highestOfFirst = await HighestRevisionByTypeAsync(fixture);

        fixture.Catalog.Set(fixture.Demand(SecondDemandId, SecondSublot, Now.AddMinutes(-9)));
        fixture.BoxCounts.Set(SecondSublot, 4);
        await RunJourneyToCompletionAsync(
            fixture, SecondDemandId, SecondSublot, SecondSubmissionId, SecondSafetyResultId);
        Dictionary<string, long> lowestOfSecond = await LowestRevisionByTypeAsync(fixture, highestOfFirst);

        Assert.Equal(
            ["CurrentStopWorklistSnapshot", "UpcomingStopPlanSnapshot", "VehicleBusinessStateSnapshot"],
            lowestOfSecond.Keys.Order(StringComparer.Ordinal));
        foreach ((string messageType, long lowest) in lowestOfSecond)
        {
            Assert.True(
                lowest > highestOfFirst[messageType],
                $"{messageType}: 第二趟最低 {lowest} 没有高过第一趟最高 {highestOfFirst[messageType]}");
        }
    }

    /// <summary>崩在写到线上的那一刻：报文已经进了发件箱，车却没收到。</summary>
    private static Func<string, Task> CrashOn(string messageType) => line =>
        line.Contains($"\"messageType\":\"{messageType}\"", StringComparison.Ordinal)
            ? throw new IOException($"crash while sending {messageType}")
            : Task.CompletedTask;

    /// <summary>发出去过的离站核验请求，按发送先后。</summary>
    private static async Task<string[]> DepartureCheckMessageIdsAsync(RuntimeFixture fixture)
    {
        ProtocolOutboxRow[] rows = await fixture.Context.ProtocolOutbox.AsNoTracking()
            .Where(row => row.MessageType == "PreDepartureSafetyCheck")
            .ToArrayAsync(TestContext.Current.CancellationToken);
        return [.. rows.OrderBy(row => row.CreatedAt).Select(row => row.MessageId)];
    }

    private static Task<JourneyStopRow> PickupStopAsync(RuntimeFixture fixture, string demandId) =>
        fixture.Context.Set<JourneyStopRow>().AsNoTracking()
            .SingleAsync(
                row => row.JourneyId == JourneyIdentity.ForAnchorDemand(demandId) &&
                       row.StopRole == JourneyStopRoles.Pickup,
                TestContext.Current.CancellationToken);

    private static async Task<Dictionary<string, long>> HighestRevisionByTypeAsync(RuntimeFixture fixture) =>
        (await RevisionsAsync(fixture))
        .GroupBy(item => item.MessageType, StringComparer.Ordinal)
        .ToDictionary(group => group.Key, group => group.Max(item => item.Revision), StringComparer.Ordinal);

    /// <summary>第二趟发的那些快照里，每条流最低的一个修订号——「第二趟」就是第一趟之后新出现的那些报文。</summary>
    private static async Task<Dictionary<string, long>> LowestRevisionByTypeAsync(
        RuntimeFixture fixture,
        Dictionary<string, long> highestOfFirst) =>
        (await RevisionsAsync(fixture))
        .Where(item => item.Revision > highestOfFirst.GetValueOrDefault(item.MessageType, long.MinValue))
        .GroupBy(item => item.MessageType, StringComparer.Ordinal)
        .ToDictionary(group => group.Key, group => group.Min(item => item.Revision), StringComparer.Ordinal);

    private static async Task<(string MessageType, long Revision)[]> RevisionsAsync(RuntimeFixture fixture)
    {
        ProtocolOutboxRow[] rows = await fixture.Context.ProtocolOutbox.AsNoTracking()
            .ToArrayAsync(TestContext.Current.CancellationToken);
        List<(string, long)> revisions = [];
        foreach (ProtocolOutboxRow row in rows)
        {
            if (SnapshotRevisionProperty(row.MessageType) is not { } property)
            {
                continue;
            }

            using JsonDocument document = JsonDocument.Parse(row.PayloadJson);
            revisions.Add((row.MessageType, document.RootElement.GetProperty("payload").GetProperty(property).GetInt64()));
        }

        return [.. revisions];
    }
}
