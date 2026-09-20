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

    /// <summary>
    /// 一趟半途终结的旅程，仍然按整趟的份额占掉修订号：同车下一趟的基准，还是上一趟基准加 2／2／3，不因为上一趟少发了
    /// 几张快照就往回收紧。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>这条钉的是按车计数器的语义，而不是它的值。</b>本票把「下一趟从哪里起步」的来源从「对 JourneyRuntimes 那三列
    /// 做一次聚合」换成了按车计数器。计数器里存的是<b>本趟的基准</b>，不是「已经发出去的最高一号」——两者在跑完整一趟的
    /// 旅程上恰好只差一号，怎么读都看不出分别，只有在半途终结的旅程上才分岔：站点期限结束的这一趟只发了取货停靠的三条，
    /// 「已发出的最高」会比基准低，下一趟的基准就会跟着往回缩。
    /// </para>
    /// <para>
    /// 往回缩本身不会让车载端断会话（号还是在涨），所以它不会被 <c>SNAPSHOT_REVISION_REGRESSION</c> 抓到，也不会被
    /// 跑完整两趟的那条对照 pin 抓到——<c>two-journeys-one-vehicle</c> 两趟都跑满。这条测试是它唯一的守卫。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AJourneyEndedHalfwayStillCostsTheVehicleAWholeJourneyOfRevisions()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Options.StationDepartureWaitTimeout = TimeSpan.FromSeconds(10);
        fixture.Catalog.Set(fixture.Demand(FirstDemandId, FirstSublot, Now.AddMinutes(-10)));
        fixture.BoxCounts.Set(FirstSublot, 7);
        JourneyRuntimeRow first = await ArriveAtPickupAsync(fixture, FirstDemandId);
        await fixture.ProveSlotDoorsClosedAsync();
        fixture.Clock.Advance(TimeSpan.FromSeconds(10));
        await TickAndRunAsync(fixture);
        Assert.Equal("CANCELLED_BY_STATION_TIMEOUT", (await fixture.RuntimeAsync(FirstDemandId)).BlockReasonCode);

        fixture.Options.StationDepartureWaitTimeout = TimeSpan.Zero;
        fixture.Catalog.Set(fixture.Demand(SecondDemandId, SecondSublot, Now.AddMinutes(-9)));
        fixture.BoxCounts.Set(SecondSublot, 4);
        await TickAndRunAsync(fixture);
        JourneyRuntimeRow second = await fixture.RuntimeAsync(SecondDemandId);

        Assert.Equal(first.VehicleBusinessRevision + 2, second.VehicleBusinessRevision);
        Assert.Equal(first.WorklistRevision + 2, second.WorklistRevision);
        Assert.Equal(first.PlanRevision + 3, second.PlanRevision);
    }

    /// <summary>
    /// 一个停靠上挂两条需求时，推进段照样走得动：清单把两条都列出来，其中一条被终结之后旅程不停在 Blocked，下一轮
    /// 仍在当前停靠上继续。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>这是给 批次7-06（control-server#211）留的接手点，不是多需求语义本身。</b>今天没有任何路径会产生两条需求的旅程——
    /// 初始派车只取一条需求，途中追加是 7-06 的事——所以这里的两条需求是直接写进库里的。本票只保证一件事：结构上摆出
    /// 两条需求时，推进段不会抛、不会把旅程卡死。「第二条需求该怎么推进」（装货阶段、清单升版、锚需求终结后切到谁）
    /// 是 7-06 要决定的，这条测试不替它决定。
    /// </para>
    /// <para>
    /// 之所以要有这条，是因为本票动的恰好是「从哪里读」：清单与录入请求改成从停靠上未终结的需求集合生成，而锚需求的
    /// 查找一度写成「从未终结的那一份里找」——那样一条需求终结的那一刻推进段就开始抛
    /// （<c>JourneyStopCursor.Anchor</c> 的注释记着这件事）。没有这条测试，那个洞要等到 7-06 才会被发现。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ASecondDemandOnTheSameStopIsListedAndEndingItLeavesTheJourneyRunning()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(FirstDemandId, FirstSublot, Now.AddMinutes(-10)));
        fixture.BoxCounts.Set(FirstSublot, 4);
        await TickAndRunAsync(fixture);
        JourneyRuntimeRow runtime = await fixture.RuntimeAsync(FirstDemandId);
        await AddSecondDemandToJourneyAsync(fixture, runtime);

        fixture.Riot.SetSuccessfulArrival("TO_PICKUP", runtime.PickupUpperId, runtime.PickupStationRiotId);
        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { CurrentStationId = runtime.PickupStationRiotId };
        await TickAndRunAsync(fixture);

        JsonElement worklist = await OutboundPayloadAsync(fixture, runtime.WorklistMessageId);
        Assert.Equal(
            [FirstDemandId, SecondDemandId],
            worklist.GetProperty("items").EnumerateArray()
                .Select(item => item.GetProperty("demandId").GetString())
                .Order(StringComparer.Ordinal));
        JsonElement request = await OutboundPayloadAsync(fixture, runtime.SublotRequestMessageId);
        Assert.Equal(
            [FirstSublot, SecondSublot],
            request.GetProperty("expectedSublots").EnumerateArray()
                .Select(item => item.GetString())
                .Order(StringComparer.Ordinal));

        // 终结第二条需求。旅程还带着锚需求，所以它既不该关闭，也不该停在 Blocked。
        AcceptedDemandRow second = await fixture.Context.AcceptedDemands
            .SingleAsync(row => row.DemandId == SecondDemandId, TestContext.Current.CancellationToken);
        second.Status = DemandExecutionStatus.Cancelled;
        await fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        await TickAndRunAsync(fixture);

        JourneyRuntimeRow after = await fixture.RuntimeAsync(FirstDemandId);
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, after.Stage);
        Assert.Null(after.BlockReasonCode);
    }

    /// <summary>
    /// 被终结的是<b>锚需求</b>、旅程还带着另一条时，推进段仍找得到锚需求的归属行，不抛。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="JourneyStopCursor.Anchor"/> 从「未移除的<b>全部</b>归属」里找，不是从「还没终结的那一份」。差别只在
    /// 这一种局面上显出来：锚需求自己终结了，而旅程因为还有别的需求没有关闭——从未终结的那一份里找就找不到它，
    /// 推进段当场抛。
    /// </para>
    /// <para>
    /// <b>这条测试是补出来的，补它的理由值得留着。</b>原先只有
    /// <see cref="ASecondDemandOnTheSameStopIsListedAndEndingItLeavesTheJourneyRunning"/>，它终结的是<b>第二条</b>需求，
    /// 锚需求还好好的，所以两种找法都找得到——把 <c>Anchor</c> 改回未终结那一份，整套测试 360 条全绿。也就是说那处加固
    /// 当时<b>没有任何守卫</b>，下一个人「顺手统一一下」就能把它改回去而不被任何东西拦住。
    /// </para>
    /// <para>
    /// 今天走不到这里：单需求旅程终结即关闭。<b>到 批次7-06（control-server#211）就天天走到</b>。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task EndingTheAnchorDemandOfATwoDemandJourneyStillLeavesItsMembershipFindable()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(FirstDemandId, FirstSublot, Now.AddMinutes(-10)));
        fixture.BoxCounts.Set(FirstSublot, 4);
        // 第二条需求的花篮数也要给，否则录入在重算那一步就被拒（BR-013），根本走不到下装货命令的地方——
        // 第一版漏了这一句，注入故障后这条测试照样绿。
        fixture.BoxCounts.Set(SecondSublot, 4);
        await TickAndRunAsync(fixture);
        JourneyRuntimeRow runtime = await fixture.RuntimeAsync(FirstDemandId);
        await AddSecondDemandToJourneyAsync(fixture, runtime);
        fixture.Riot.SetSuccessfulArrival("TO_PICKUP", runtime.PickupUpperId, runtime.PickupStationRiotId);
        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { CurrentStationId = runtime.PickupStationRiotId };
        await TickAndRunAsync(fixture);

        // 终结锚需求本身。旅程还带着第二条，所以它既不关闭，也不该在下一轮里抛。
        AcceptedDemandRow anchor = await fixture.Context.AcceptedDemands
            .SingleAsync(row => row.DemandId == FirstDemandId, TestContext.Current.CancellationToken);
        anchor.Status = DemandExecutionStatus.Cancelled;
        await fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        // 让录入落到第二条需求上，推进段因此要对它下装货命令——而命令的 attempt id 与目标仓位取自
        // Anchor(runtime.DemandId)，也就是那条已经终结的锚需求的归属行。
        await AddInboxAsync(
            fixture, FirstSubmissionId, "SublotSubmitted", SublotSubmission(fixture, runtime, SecondSublot));
        await TickAndRunAsync(fixture);

        // 装货命令发出去了，就是 Anchor(runtime.DemandId) 被调用过且没抛的直接证据——命令的 attempt id 与目标仓位
        // 都取自那条已终结锚需求的归属行。只断言「没停在 Blocked」是不够的：录入被拒同样不会进 Blocked。
        JourneyRuntimeRow after = await fixture.RuntimeAsync(FirstDemandId);
        Assert.Equal(JourneyRuntimeStage.AwaitingLoadResult, after.Stage);
        Assert.Equal(
            1,
            await fixture.Context.ProtocolOutbox.CountAsync(
                row => row.MessageId == runtime.LoadCommandMessageId, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// 往这趟旅程的两个停靠上再挂一条需求，直接写库——今天没有任何路径会这么做。
    /// </summary>
    private static async Task AddSecondDemandToJourneyAsync(RuntimeFixture fixture, JourneyRuntimeRow runtime)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        AcceptedDemandRow anchor = await fixture.Context.AcceptedDemands.AsNoTracking()
            .SingleAsync(row => row.DemandId == runtime.DemandId, token);
        JourneyDemandRow anchorMembership = await fixture.Context.Set<JourneyDemandRow>().AsNoTracking()
            .SingleAsync(row => row.JourneyId == runtime.JourneyId && row.DemandId == runtime.DemandId, token);

        // AcceptedDemandRow 不是 record，字段又多，所以整行序列化再读回来当克隆用。
        AcceptedDemandRow second = JsonSerializer.Deserialize<AcceptedDemandRow>(JsonSerializer.Serialize(anchor))!;
        second.DemandId = SecondDemandId;
        second.Sublot = SecondSublot;
        second.TransportDemandKey = $"{SecondSublot}|WIRE_TO_GATE";
        second.SeriesId = $"SERIES-{SecondDemandId}";
        fixture.Context.AcceptedDemands.Add(second);

        fixture.Context.Set<JourneyDemandRow>().Add(new JourneyDemandRow
        {
            JourneyId = anchorMembership.JourneyId,
            DemandId = SecondDemandId,
            PickupStopId = anchorMembership.PickupStopId,
            UnloadStopId = anchorMembership.UnloadStopId,
            ExpectedBasketCount = anchorMembership.ExpectedBasketCount,
            TargetSlotsJson = anchorMembership.TargetSlotsJson,
            LoadSlotOperationAttemptId = JourneyPlanBuilder.StableGuid(SecondDemandId, "load-attempt"),
            LoadCommandMessageId = JourneyPlanBuilder.StableGuid(SecondDemandId, "load-command"),
            UnloadSlotOperationAttemptId = JourneyPlanBuilder.StableGuid(SecondDemandId, "unload-attempt"),
            UnloadCommandMessageId = JourneyPlanBuilder.StableGuid(SecondDemandId, "unload-command"),
            DispatchZone = anchorMembership.DispatchZone,
            DispatchGeneration = anchorMembership.DispatchGeneration,
            Status = JourneyDemandStatuses.PendingLoad,
            AddedAt = anchorMembership.AddedAt.AddSeconds(1)
        });
        await fixture.Context.SaveChangesAsync(token);
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
