using System.Text;
using System.Text.Json;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Dispatch;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;

namespace ControlServer.Tests;

/// <summary>
/// 断线重连之后，服务端按 ADR-cross-0055 重填的离站期限要送到车上；持货旅程在断线窗口里合法变了的装货阶段也要送到车上，
/// 而不是每轮被拒（control-server#339）。
/// </summary>
/// <remarks>
/// <para>
/// <b>车上那一版期限为什么要紧。</b>车载端（<c>w2g/fp-v2-impl</c>，cs#331 作者读 <c>1184bb07</c>）不作废也不重新计满期限，
/// 永远照它手上最新一版 <c>CurrentStopWorklistSnapshot</c> 的 <c>stationDepartureDeadlineAt</c> 显示倒计时；过了期限只改显示
/// （「已到期，等待本站结束」），不结算。服务端却在断联那一轮把期限起点清空、重连后从此刻重填，按新期限结束本站。两边一不一致，
/// 现场看到的就不是服务端判定用的那一个。
/// </para>
/// <para>
/// <b>每条都按真实顺序走「清单已确认 → 断线 → 一轮会话未就绪 → 重连」。</b>中间那一轮未就绪不能省：它就是清空期限起点的那一轮
/// （<c>AdvanceAsync</c> 会话为空那一段）；cs#331 的「复现现场」用例第一版正是漏了它。车用 <see cref="AdoptingPeer"/> 模拟：
/// 按消息类型记它采纳的那一版，同号不同内容按 <c>SNAPSHOT_REVISION_CONTENT_CONFLICT</c> 拒收、不确认，与车载端相同。
/// </para>
/// <para>
/// <b>钟要真的走。</b>断线那一轮之前拨 7 秒，并断言重填出来的期限确实与原来的不同——钟不走时重填出来的还是原来那个时刻，
/// 「车上与服务端一致」恒真。
/// </para>
/// </remarks>
public sealed class RefilledStationDeadlineReachesVehicleTests
{
    private const string DemandId = "10000000-0000-4000-8000-000000000001";

    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    /// <summary>断线到重连之间钟走过的时间。比期限短，这一站不会在窗口里到期。</summary>
    private static readonly TimeSpan Gap = TimeSpan.FromSeconds(7);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// 等录入时断线重连：重填的期限随新的一版清单送到车上，录入请求跟着那一版重发——旧的一张在车上随修订号变化作废
    /// （<c>expiresOnRevisionChange</c>），不重发操作员就没有可答的请求。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-SESSION-RECONNECT-DURING-RECOVERY")]
    [Trait("ProtocolVector", "CV-PICKUP-SUBLOT-LOAD")]
    public async Task ARefillWhileAwaitingTheEntryReachesTheVehicle()
    {
        await using RuntimeFixture fixture = await DeadlineFixtureAsync();
        AdoptingPeer vehicle = Attach(fixture);
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, (await fixture.AdvanceToSublotWaitAsync()).Stage);
        await vehicle.DeliverBufferedAcksAsync();
        DateTimeOffset before = VehicleDeadline(vehicle);

        await DisconnectThroughAnUnreadyRoundThenReconnectAsync(fixture);
        await fixture.Engine.ExecuteOnceAsync(Token);
        await vehicle.DeliverBufferedAcksAsync();

        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, (await fixture.RuntimeAsync()).Stage);
        await AssertTheVehicleHoldsTheServersDeadlineAsync(fixture, vehicle, before);
        AssertTheLastEntryRequestAnswersTheHeldWorklist(fixture, vehicle);
    }

    /// <summary>
    /// 装货命令已发、结果未回时断线重连：重填的期限同样送到车上；录入请求不重发——这一站此刻没有开着的录入。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-SESSION-RECONNECT-DURING-RECOVERY")]
    [Trait("ProtocolVector", "CV-PICKUP-SUBLOT-LOAD")]
    public async Task ARefillWhileTheLoadResultIsOutstandingReachesTheVehicle()
    {
        await using RuntimeFixture fixture = await DeadlineFixtureAsync();
        AdoptingPeer vehicle = Attach(fixture);
        Assert.Equal(JourneyRuntimeStage.AwaitingLoadResult, (await fixture.AdvanceToLoadResultAsync()).Stage);
        await vehicle.DeliverBufferedAcksAsync();
        DateTimeOffset before = VehicleDeadline(vehicle);
        int entryRequestsBefore = SentLines(fixture, "SublotEntryRequested").Length;

        await DisconnectThroughAnUnreadyRoundThenReconnectAsync(fixture);
        await fixture.Engine.ExecuteOnceAsync(Token);
        await vehicle.DeliverBufferedAcksAsync();

        Assert.Equal(JourneyRuntimeStage.AwaitingLoadResult, (await fixture.RuntimeAsync()).Stage);
        await AssertTheVehicleHoldsTheServersDeadlineAsync(fixture, vehicle, before);
        Assert.Equal(entryRequestsBefore, SentLines(fixture, "SublotEntryRequested").Length);
    }

    /// <summary>
    /// 到站那一段发布被断线打断、重连后重跑：车手上那一版清单是断线之前确认的（旧期限），重跑算出的新期限要送到车上，
    /// 而且这一轮不失败——cs#331 修掉的「每轮失败」不能回来。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-SESSION-RECONNECT-DURING-RECOVERY")]
    [Trait("ProtocolVector", "CV-PICKUP-SUBLOT-LOAD")]
    public async Task ARefillOnTheArrivalRerunReachesTheVehicle()
    {
        (RuntimeFixture fixture, ArrivalPublishInterruptedThenReconnectedTests.ConnectionCut cut) =
            await ArrivalPublishInterruptedThenReconnectedTests.ArrivalPublishCutAfterTheWorklistAsync(Wait);
        await using RuntimeFixture disposing = fixture;
        AdoptingPeer vehicle = cut.Peer;
        DateTimeOffset before = VehicleDeadline(vehicle);

        await DisconnectThroughAnUnreadyRoundThenReconnectAsync(fixture);
        await fixture.Engine.ExecuteOnceAsync(Token);
        await vehicle.DeliverBufferedAcksAsync();

        JourneyRuntimeRow runtime = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, runtime.Stage);
        Assert.Null(runtime.BlockReasonCode);
        await AssertTheVehicleHoldsTheServersDeadlineAsync(fixture, vehicle, before);
        AssertTheLastEntryRequestAnswersTheHeldWorklist(fixture, vehicle);
    }

    /// <summary>
    /// 真车载端的未就绪形状（车挂着本服务端在途单时的样子，<see cref="PickupDispatchPlanPastOwnOrderTests.DropSessionOnOwnOrderAsync"/>）
    /// 落在等录入的一轮上，同一代回到就绪：重填的期限送到车上；之后就绪的几轮不再清空起点、不再重发——期限不会每轮往后挪。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-SESSION-RECONNECT-DURING-RECOVERY")]
    [Trait("ProtocolVector", "CV-PICKUP-SUBLOT-LOAD")]
    public async Task TheRealOnboardsUnreadyShapeRefillsOnceAndTheVehicleHoldsIt()
    {
        await using RuntimeFixture fixture = await DeadlineFixtureAsync();
        AdoptingPeer vehicle = Attach(fixture);
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, (await fixture.AdvanceToSublotWaitAsync()).Stage);
        await vehicle.DeliverBufferedAcksAsync();
        DateTimeOffset before = VehicleDeadline(vehicle);

        await PickupDispatchPlanPastOwnOrderTests.DropSessionOnOwnOrderAsync(fixture);
        fixture.Clock.Advance(Gap);
        await fixture.Engine.ExecuteOnceAsync(Token);
        Assert.Null((await fixture.RuntimeAsync()).StationDepartureWaitStartedAt);
        await fixture.RestoreSessionReadyAsync();
        await fixture.HearFromPeerAsync();
        await fixture.Engine.ExecuteOnceAsync(Token);
        await vehicle.DeliverBufferedAcksAsync();

        await AssertTheVehicleHoldsTheServersDeadlineAsync(fixture, vehicle, before);
        DateTimeOffset refilled = VehicleDeadline(vehicle);
        int worklistsSent = SentLines(fixture, "CurrentStopWorklistSnapshot").Length;
        for (int round = 0; round < 2; round++)
        {
            fixture.Clock.Advance(TimeSpan.FromSeconds(1));
            await fixture.HearFromPeerAsync();
            await fixture.Engine.ExecuteOnceAsync(Token);
            await vehicle.DeliverBufferedAcksAsync();
        }

        Assert.Equal(worklistsSent, SentLines(fixture, "CurrentStopWorklistSnapshot").Length);
        Assert.Equal(refilled, VehicleDeadline(vehicle));
        Assert.Equal(refilled, ServerDeadline(await fixture.RuntimeAsync(), fixture));
    }

    /// <summary>
    /// 持货等单的旅程：到站那一段被断线打断，断线窗口里车两侧都被判满（装货阶段 <c>LOADING</c> → <c>VEHICLE_FULL</c>），
    /// 重连后推进照常走到等录入，车手上的车辆业务状态是 <c>VEHICLE_FULL</c>，车没有拒收任何一张。
    /// </summary>
    /// <remarks>
    /// 「两侧都满」按 <see cref="Batch7CargoHoldingTests"/> 的做法写进派车轮的读口（两侧各一条「只因本车货物装不下」的候选）；
    /// 满先于有待装（<see cref="LoadingPhaseMachine"/> 规则 7），所以首个取货停靠上就能变。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("IntegrationSlice", "FP-IS-08")]
    [Trait("ProtocolVector", "CV-SESSION-RECONNECT-DURING-RECOVERY")]
    [Trait("ProtocolVector", "CV-PICKUP-SUBLOT-LOAD")]
    public async Task AHoldingJourneyWhoseLoadingPhaseChangedAcrossTheInterruptedArrivalStillAdvances()
    {
        (RuntimeFixture fixture, ArrivalPublishInterruptedThenReconnectedTests.ConnectionCut cut) =
            await ArrivalPublishInterruptedThenReconnectedTests.ArrivalPublishCutAfterTheWorklistAsync(
                create: () => Batch7CargoHoldingTests.HoldingFixtureAsync());
        await using RuntimeFixture disposing = fixture;
        AdoptingPeer vehicle = cut.Peer;
        Assert.Equal(LoadingPhaseStates.Loading, HeldLoadingPhase(vehicle));

        await DisconnectThroughAnUnreadyRoundThenReconnectAsync(fixture);
        // 每一轮之前都写一次：每一轮的派车都用自己的裁决整张替换读口（夹具里没有真候选，它的裁决是「哪一侧都不满」），
        // 只写一次，下一轮就会被上一轮的派车盖掉、判回不满。这里要的是「两侧一直都满」这一个事实。
        StillFullOnBothSides(fixture);
        Exception? first = await Record.ExceptionAsync(() => fixture.Engine.ExecuteOnceAsync(Token));
        await fixture.RecreateEngineAsync();
        await vehicle.DeliverBufferedAcksAsync();
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        await fixture.HearFromPeerAsync();
        StillFullOnBothSides(fixture);
        Exception? second = await Record.ExceptionAsync(() => fixture.Engine.ExecuteOnceAsync(Token));
        await fixture.RecreateEngineAsync();
        await vehicle.DeliverBufferedAcksAsync();

        JourneyRuntimeRow runtime = await fixture.RuntimeAsync();
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"first round: {first?.GetType().Name ?? "ok"} {first?.Message}; second round: " +
            $"{second?.GetType().Name ?? "ok"} {second?.Message}; stage {runtime.Stage}, code {runtime.BlockReasonCode}, " +
            $"server phase {runtime.LoadingPhaseState}, vehicle phase {HeldLoadingPhase(vehicle)}, " +
            $"vehicle refused {string.Join(",", vehicle.Conflicts)}, regressions {vehicle.Regressions.Count}");
        Assert.Equal(LoadingPhaseStates.VehicleFull, runtime.LoadingPhaseState);
        Assert.Empty(vehicle.Conflicts);
        Assert.Empty(vehicle.Regressions);
        Assert.Null(first);
        Assert.Null(second);
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, runtime.Stage);
        Assert.Null(runtime.BlockReasonCode);
        Assert.Equal(LoadingPhaseStates.VehicleFull, HeldLoadingPhase(vehicle));
        // 车手上的业务状态是按严格前进的号一张张换上去的，并且最后那一张车确认了。
        long[] adopted = [.. vehicle.Adopted
            .Where(item => item.MessageType == "VehicleBusinessStateSnapshot")
            .Select(item => item.Revision)];
        Assert.Equal(adopted.Order().Distinct(), adopted);
        Assert.Equal(2, adopted.Length);
        Assert.NotNull((await fixture.Context.ProtocolOutbox.AsNoTracking().SingleAsync(
            row => row.MessageType == "VehicleBusinessStateSnapshot" &&
                   row.PayloadJson.Contains("\"VEHICLE_FULL\""), Token)).AcknowledgedAt);
    }

    private static void StillFullOnBothSides(RuntimeFixture fixture) =>
        Batch7CargoHoldingTests.RecordRound(
            fixture,
            (DispatchReasonCodes.SlotGroupOccupiedByOwnCargo, "FRONT"),
            (DispatchReasonCodes.SlotGroupOccupiedByOwnCargo, "REAR"));

    private static async Task<RuntimeFixture> DeadlineFixtureAsync()
    {
        RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Options.StationDepartureWaitTimeout = Wait;
        fixture.Catalog.Set(fixture.Demand(DemandId, "SUBLOT-001", Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        return fixture;
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

    /// <summary>
    /// 断线（会话离开 <c>Ready</c>）→ 钟走 <see cref="Gap"/> → 引擎在闸门关着时跑一轮，断言这一轮清空了期限起点 → 新的一代握手完成、
    /// 车听得到。
    /// </summary>
    private static async Task DisconnectThroughAnUnreadyRoundThenReconnectAsync(RuntimeFixture fixture)
    {
        await fixture.DropOnboardSessionAsync();
        fixture.Clock.Advance(Gap);
        await fixture.Engine.ExecuteOnceAsync(Token);
        JourneyRuntimeRow voided = await fixture.RuntimeAsync();
        Assert.Null(voided.StationDepartureWaitStartedAt);
        Assert.Equal("ONBOARD_SESSION_NOT_READY", voided.BlockReasonCode);
        await ArrivalPublishInterruptedThenReconnectedTests.ReconnectAtGenerationAsync(fixture, generation: 2);
    }

    /// <summary>
    /// 车手上那一版清单的期限等于服务端此刻判定用的期限，且与断线之前的不同（钟确实走了、重填确实发生了）；车没有拒收任何一张。
    /// </summary>
    private static async Task AssertTheVehicleHoldsTheServersDeadlineAsync(
        RuntimeFixture fixture,
        AdoptingPeer vehicle,
        DateTimeOffset before)
    {
        DateTimeOffset server = ServerDeadline(await fixture.RuntimeAsync(), fixture);
        DateTimeOffset held = VehicleDeadline(vehicle);
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"before the disconnect {before:O}; server now {server:O}; vehicle holds {held:O}; " +
            $"vehicle refused {string.Join(",", vehicle.Conflicts)}, regressions {vehicle.Regressions.Count}");
        Assert.NotEqual(before, server);
        Assert.Empty(vehicle.Conflicts);
        Assert.Empty(vehicle.Regressions);
        Assert.Equal(server, held);
    }

    /// <summary>
    /// 车上还开着的录入请求答的是车手上这一版清单：最后发出的那张录入请求的 <c>worklistRevision</c> 等于清单的修订号。
    /// </summary>
    private static void AssertTheLastEntryRequestAnswersTheHeldWorklist(RuntimeFixture fixture, AdoptingPeer vehicle)
    {
        JsonElement worklist = vehicle.HeldPayload("CurrentStopWorklistSnapshot")
            ?? throw new InvalidOperationException("The vehicle holds no worklist.");
        JsonElement entry = SentLines(fixture, "SublotEntryRequested").Last();
        Assert.Equal(
            worklist.GetProperty("worklistRevision").GetInt64(),
            entry.GetProperty("payload").GetProperty("worklistRevision").GetInt64());
    }

    private static DateTimeOffset ServerDeadline(JourneyRuntimeRow runtime, RuntimeFixture fixture) =>
        JourneyRuntimeEngine.StationDepartureDeadline(runtime, fixture.Options.StationDepartureWaitTimeout)
        ?? throw new InvalidOperationException("The server holds no station departure deadline.");

    private static DateTimeOffset VehicleDeadline(AdoptingPeer vehicle) =>
        (vehicle.HeldPayload("CurrentStopWorklistSnapshot")
         ?? throw new InvalidOperationException("The vehicle holds no worklist."))
        .GetProperty("stationDepartureDeadlineAt").GetDateTimeOffset();

    private static string HeldLoadingPhase(AdoptingPeer vehicle) =>
        (vehicle.HeldPayload("VehicleBusinessStateSnapshot")
         ?? throw new InvalidOperationException("The vehicle holds no business state."))
        .GetProperty("loadingPhase").GetProperty("state").GetString()!;

    /// <summary>车收到的这一类报文，按发出的顺序。</summary>
    private static JsonElement[] SentLines(RuntimeFixture fixture, string messageType) =>
        [.. fixture.Peer.Lines
            .Select(line => JsonDocument.Parse(Encoding.UTF8.GetString(line)).RootElement.Clone())
            .Where(root => root.GetProperty("messageType").GetString() == messageType)];
}
