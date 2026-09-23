using System.Text;
using System.Text.Json;
using ControlServer.Application;
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
    /// 等录入时断线重连：重填的期限随新的一版清单送到车上，录入请求跟着那一版重发——车在会话离开 <c>Ready</c> 时已经清掉了
    /// 手上的录入请求（车载端 <c>1184bb07</c> 的 <c>WireToGateBusinessService.OnSessionStateChanged</c>），不重发操作员就没有可答的请求。
    /// 升版本身并不让车上的录入请求作废：同一站、同一作业会话、还有待录入时，更高的号不算本站结束（同文件 <c>EndsStopOf</c>）。
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
    /// 旧的那一版清单车还没确认时重填：升版的次数、旧一版的退役与新的一版在同一次保存里落库。崩在新的一版入队的那一次保存上，
    /// 就什么都没落库，重启之后的下一轮照样认出车上的期限是旧的、照样升版。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 反过来的样子（PR #353 审查 M1）：旧的一版还没确认时，发件箱按库里的值查得到它，退役那一步自己先保存一次，把升版的次数
    /// 一起带进了库，新的一版却还没入队。崩在两次保存之间，此后按新的号在发件箱里找不到排给车的那一版，「车上的期限是不是旧的」
    /// 就再也判不出来，车停在旧期限上。
    /// </para>
    /// <para>
    /// 崩溃用 <see cref="SaveChangesCounter.FailWhen"/> 注入在这一轮第一次往发件箱插行的那次保存上——新的一版清单。
    /// </para>
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-SESSION-RECONNECT-DURING-RECOVERY")]
    [Trait("ProtocolVector", "CV-PICKUP-SUBLOT-LOAD")]
    public async Task ARefillOverAnUnconfirmedWorklistLandsInOneSaveAndSurvivesACrashAtIt()
    {
        await using RuntimeFixture fixture = await DeadlineFixtureAsync();
        AdoptingPeer vehicle = Attach(fixture);
        JourneyRuntimeRow atPickup = await fixture.AdvanceToSublotWaitAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, atPickup.Stage);
        // 不送确认：车手上有到站那一版，服务端发件箱里它还没被确认。
        vehicle.LoseBufferedAcks();
        DateTimeOffset before = VehicleDeadline(vehicle);
        Assert.Null((await ArrivalWorklistRowAsync(fixture)).AcknowledgedAt);

        await DisconnectThroughAnUnreadyRoundThenReconnectAsync(fixture);
        fixture.SaveChanges.Reset();
        int outboxInserts = 0;
        fixture.SaveChanges.FailWhen = written =>
            written.Contains("ProtocolOutboxRow.MessageId") && ++outboxInserts == 1;
        Exception? crash = await Record.ExceptionAsync(() => fixture.Engine.ExecuteOnceAsync(Token));
        fixture.SaveChanges.FailWhen = null;
        await fixture.RecreateEngineAsync();

        TestContext.Current.TestOutputHelper?.WriteLine(
            $"crash: {crash?.Message}; saves in the crashed round: " +
            string.Join(" / ", fixture.SaveChanges.Saves.Select(save => string.Join(",", save))));
        Assert.NotNull(crash);
        Assert.Equal(0, (await PickupStopAsync(fixture)).WorklistRefills);
        Assert.Null((await ArrivalWorklistRowAsync(fixture)).FencedAt);

        fixture.SaveChanges.Reset();
        await fixture.HearFromPeerAsync();
        await fixture.Engine.ExecuteOnceAsync(Token);
        await vehicle.DeliverBufferedAcksAsync();

        await AssertTheVehicleHoldsTheServersDeadlineAsync(fixture, vehicle, before);
        AssertTheLastEntryRequestAnswersTheHeldWorklist(fixture, vehicle);
        Assert.Equal(1, (await PickupStopAsync(fixture)).WorklistRefills);
        Assert.NotNull((await ArrivalWorklistRowAsync(fixture)).FencedAt);
        string[] refill = Assert.Single(
            fixture.SaveChanges.Saves, save => save.Contains("JourneyStopRow.WorklistRefills"));
        Assert.Contains("ProtocolOutboxRow.FencedAt", refill);
        Assert.Contains("ProtocolOutboxRow.MessageId", refill);
    }

    /// <summary>
    /// 等录入时重填，新的一版清单已经落库、跟着它的录入请求还没入队时崩了：下一轮补发录入请求。车在会话离开 <c>Ready</c> 时
    /// 已经清掉了手上的录入请求，不补发，操作员这一站就没有可答的请求。
    /// </summary>
    /// <remarks>
    /// 清单与录入请求是两条报文、两次保存，中间崩掉时清单那一侧已经对上了，「车上的期限是不是旧的」判不出任何事，
    /// 所以补发靠的是另一条判据：这一版清单已排、这一版的录入请求却没有。崩溃注入在这一轮第二次往发件箱插行的那次保存上。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-SESSION-RECONNECT-DURING-RECOVERY")]
    [Trait("ProtocolVector", "CV-PICKUP-SUBLOT-LOAD")]
    public async Task ACrashBetweenTheRefilledWorklistAndItsEntryRequestIsHealedOnTheNextRound()
    {
        await using RuntimeFixture fixture = await DeadlineFixtureAsync();
        AdoptingPeer vehicle = Attach(fixture);
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, (await fixture.AdvanceToSublotWaitAsync()).Stage);
        await vehicle.DeliverBufferedAcksAsync();
        DateTimeOffset before = VehicleDeadline(vehicle);

        await DisconnectThroughAnUnreadyRoundThenReconnectAsync(fixture);
        int outboxInserts = 0;
        fixture.SaveChanges.FailWhen = written =>
            written.Contains("ProtocolOutboxRow.MessageId") && ++outboxInserts == 2;
        Exception? crash = await Record.ExceptionAsync(() => fixture.Engine.ExecuteOnceAsync(Token));
        fixture.SaveChanges.FailWhen = null;
        await fixture.RecreateEngineAsync();
        await vehicle.DeliverBufferedAcksAsync();

        TestContext.Current.TestOutputHelper?.WriteLine($"crash: {crash?.Message}");
        Assert.NotNull(crash);
        Assert.Equal(1, (await PickupStopAsync(fixture)).WorklistRefills);
        int entryRequestsBefore = SentLines(fixture, "SublotEntryRequested").Length;

        await fixture.HearFromPeerAsync();
        await fixture.Engine.ExecuteOnceAsync(Token);
        await vehicle.DeliverBufferedAcksAsync();

        await AssertTheVehicleHoldsTheServersDeadlineAsync(fixture, vehicle, before);
        Assert.Equal(entryRequestsBefore + 1, SentLines(fixture, "SublotEntryRequested").Length);
        AssertTheLastEntryRequestAnswersTheHeldWorklist(fixture, vehicle);
        Assert.Equal(1, (await PickupStopAsync(fixture)).WorklistRefills);

        // 补发一次就够：之后就绪的几轮不再发。
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        await fixture.HearFromPeerAsync();
        await fixture.Engine.ExecuteOnceAsync(Token);
        Assert.Equal(entryRequestsBefore + 1, SentLines(fixture, "SublotEntryRequested").Length);
    }

    /// <summary>
    /// 断两次、中间那一版车收到了却没来得及确认：第二次重填再升一版，车上的期限跟着第二次的走，号严格前进，车没有拒收任何一张。
    /// </summary>
    /// <remarks>
    /// 第一次重填发出的那一版（N+1）没被确认，第二次重连后它会被重放，而它带的是第一次重填的期限。车已经采纳了它，
    /// 同号同内容照收；随后第二次重填按「排给车的那一版与服务端此刻不一致」认出它，退役、发 N+2。若重填改在同一个号上改期限，
    /// 车就会按 <c>SNAPSHOT_REVISION_CONTENT_CONFLICT</c> 拒收——模型测试看不见这一种（<see cref="AdoptingPeer.Conflicts"/>
    /// 没有接进 <c>ReconnectModel</c> 的违规），所以在这里单独钉住。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-SESSION-RECONNECT-DURING-RECOVERY")]
    [Trait("ProtocolVector", "CV-PICKUP-SUBLOT-LOAD")]
    public async Task TwoDisconnectsWithNoConfirmationBetweenThemEachReachTheVehicle()
    {
        await using RuntimeFixture fixture = await DeadlineFixtureAsync();
        AdoptingPeer vehicle = Attach(fixture);
        long first = (await fixture.AdvanceToSublotWaitAsync()).WorklistRevision;
        await vehicle.DeliverBufferedAcksAsync();

        await DisconnectThroughAnUnreadyRoundThenReconnectAsync(fixture);
        await fixture.Engine.ExecuteOnceAsync(Token);
        DateTimeOffset firstRefill = VehicleDeadline(vehicle);
        Assert.Equal(firstRefill, ServerDeadline(await fixture.RuntimeAsync(), fixture));
        // 第一次重填那一版的确认在路上丢了。
        vehicle.LoseBufferedAcks();

        await DisconnectThroughAnUnreadyRoundThenReconnectAsync(fixture, generation: 3);
        await fixture.Engine.ExecuteOnceAsync(Token);
        await vehicle.DeliverBufferedAcksAsync();

        await AssertTheVehicleHoldsTheServersDeadlineAsync(fixture, vehicle, firstRefill);
        Assert.Equal([first, first + 1, first + 2], AdoptedWorklists(vehicle));
        Assert.Equal(2, (await PickupStopAsync(fixture)).WorklistRefills);
        AssertTheLastEntryRequestAnswersTheHeldWorklist(fixture, vehicle);
    }

    /// <summary>
    /// 重填那一轮走了「取消开着」的出口：起点在那一轮保存了，清单没发。取消之后不再开着（这里直接把它改成 <c>HistoricalOnly</c>，
    /// 等同于撤回），下一轮照样升版，车上的期限对上服务端。
    /// </summary>
    /// <remarks>
    /// 钉的是 <c>AdvanceWorklistPastAStaleDeadlineAsync</c> 的判据本身：按「排给车的那一版与服务端此刻的期限是否一致」判，
    /// 而不是「这一轮重填了」。后者只在重填那一轮成立一次，而那一轮从取消的出口走了，之后每一轮都读不到它。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-SESSION-RECONNECT-DURING-RECOVERY")]
    [Trait("ProtocolVector", "CV-PICKUP-SUBLOT-LOAD")]
    public async Task ARefillSavedThroughTheOpenCancellationExitStillReachesTheVehicleOnceItCloses()
    {
        await using RuntimeFixture fixture = await DeadlineFixtureAsync();
        AdoptingPeer vehicle = Attach(fixture);
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, (await fixture.AdvanceToSublotWaitAsync()).Stage);
        await vehicle.DeliverBufferedAcksAsync();
        DateTimeOffset before = VehicleDeadline(vehicle);

        await DisconnectThroughAnUnreadyRoundThenReconnectAsync(fixture);
        string workflowId = await OpenALoadCancellationAsync(fixture);
        int worklistsSent = SentLines(fixture, "CurrentStopWorklistSnapshot").Length;
        await fixture.Engine.ExecuteOnceAsync(Token);
        JourneyRuntimeRow held = await fixture.RuntimeAsync();
        Assert.NotNull(held.StationDepartureWaitStartedAt);
        Assert.Equal(worklistsSent, SentLines(fixture, "CurrentStopWorklistSnapshot").Length);
        Assert.Equal(0, (await PickupStopAsync(fixture)).WorklistRefills);

        await using (ControlServerDbContext connection = fixture.OpenConnectionContext())
        {
            RecoveryWorkflowRow workflow = await connection.RecoveryWorkflows.SingleAsync(
                row => row.WorkflowId == workflowId, Token);
            workflow.State = RecoveryWorkflowState.HistoricalOnly;
            await connection.SaveChangesAsync(Token);
        }
        fixture.Context.ChangeTracker.Clear();
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        await fixture.HearFromPeerAsync();
        await fixture.Engine.ExecuteOnceAsync(Token);
        await vehicle.DeliverBufferedAcksAsync();

        await AssertTheVehicleHoldsTheServersDeadlineAsync(fixture, vehicle, before);
        Assert.Equal(held.StationDepartureWaitStartedAt, (await fixture.RuntimeAsync()).StationDepartureWaitStartedAt);
        AssertTheLastEntryRequestAnswersTheHeldWorklist(fixture, vehicle);
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

    /// <summary>
    /// 重填升版之后这趟旅程照常走完取货、到关卡：录入答旧的那一版（升版之前就上路的提交）与答新的那一版都照常受理，
    /// 清单修订号在车上严格前进，卸货停靠的首号跟着后移一号。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 单需求两停靠下，没有重填时取货停靠发一版（号 N）、卸货停靠从 N+1 起；重填一次之后取货停靠发两版（N、N+1），卸货停靠从 N+2 起。
    /// 期望值按这条算式手算，不从游标取——从游标取就是拿实现去验实现。
    /// </para>
    /// <para>
    /// 答旧的那一版能受理，靠的是录入地址区间 <c>[首号, 首号 + 版本数 − 1]</c> 把重填那一版也算进版本数；
    /// 同一个停靠的号在车上不会回退，靠的是卸货停靠的首号也把它算进去。把重填次数从这两处任一处拿掉，这里都会红。
    /// </para>
    /// </remarks>
    [Theory]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-SESSION-RECONNECT-DURING-RECOVERY")]
    [Trait("ProtocolVector", "CV-PICKUP-SUBLOT-LOAD")]
    [InlineData(0)]
    [InlineData(1)]
    public async Task AfterARefillTheJourneyReachesTheGateWithTheWorklistRevisionStrictlyAdvancing(long enteredAgainst)
    {
        await using RuntimeFixture fixture = await DeadlineFixtureAsync();
        AdoptingPeer vehicle = Attach(fixture);
        JourneyRuntimeRow atPickup = await fixture.AdvanceToSublotWaitAsync();
        long first = atPickup.WorklistRevision;
        await vehicle.DeliverBufferedAcksAsync();
        await DisconnectThroughAnUnreadyRoundThenReconnectAsync(fixture);
        await fixture.Engine.ExecuteOnceAsync(Token);
        await vehicle.DeliverBufferedAcksAsync();
        Assert.Equal([first, first + 1], AdoptedWorklists(vehicle));

        await SubmitSublotAgainstAsync(fixture, first + enteredAgainst);
        await fixture.Engine.ExecuteOnceAsync(Token);
        Assert.Equal(JourneyRuntimeStage.AwaitingLoadResult, (await fixture.RuntimeAsync()).Stage);
        await fixture.ApplySafeResultAsync(
            await fixture.OperationAsync(SlotOperationType.Load), SlotOperationType.Load, SlotBusinessState.Occupied);
        await fixture.Engine.ExecuteOnceAsync(Token);
        Assert.Equal(JourneyRuntimeStage.AwaitingStationDeparture, (await fixture.RuntimeAsync()).Stage);
        fixture.Clock.Advance(Wait);
        await fixture.HearFromPeerAsync();
        await fixture.Engine.ExecuteOnceAsync(Token);
        await AnswerDepartureSafetyAsync(fixture, await fixture.RuntimeAsync());
        await fixture.Engine.ExecuteOnceAsync(Token);
        Assert.Equal(JourneyRuntimeStage.AwaitingGateArrival, (await fixture.RuntimeAsync()).Stage);
        fixture.Riot.SetSuccessfulArrival("TO_GATE", TaskTypeStationRuntimeSeed.GateStationRiotId);
        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { CurrentStationId = TaskTypeStationRuntimeSeed.GateStationRiotId };
        await fixture.Engine.ExecuteOnceAsync(Token);
        await vehicle.DeliverBufferedAcksAsync();

        Assert.Equal(JourneyRuntimeStage.AwaitingUnloadResult, (await fixture.RuntimeAsync()).Stage);
        Assert.Equal([first, first + 1, first + 2], AdoptedWorklists(vehicle));
        Assert.Empty(vehicle.Conflicts);
        Assert.Empty(vehicle.Regressions);
    }

    /// <summary>
    /// 没有断线的一次到站：到站那一张车辆业务状态照常发、车照常确认——「被更高号的快照取代就不再发」只在重跑时成立，
    /// 不能在正常的到站里误触发。装货阶段在路上就变了（车两侧都满）那一格一并走：到站那一张按抬过的基准发，同样照常发、照常确认。
    /// </summary>
    /// <remarks>
    /// 把 <c>JourneyRuntimeEngine.ArrivalBusinessStateSupersededAsync</c> 改成恒真，两行都红：到站那一张一行都不会有。
    /// </remarks>
    [Theory]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("IntegrationSlice", "FP-IS-08")]
    [Trait("ProtocolVector", "CV-PICKUP-SUBLOT-LOAD")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnUninterruptedArrivalStillSendsItsBusinessStateAndTheVehicleConfirmsIt(bool fullOnTheWay)
    {
        await using RuntimeFixture fixture = await Batch7CargoHoldingTests.HoldingFixtureAsync();
        AdoptingPeer vehicle = Attach(fixture);
        fixture.Catalog.Set(fixture.Demand(DemandId, "SUBLOT-001", Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        await fixture.Engine.ExecuteOnceAsync(Token);
        if (fullOnTheWay)
        {
            StillFullOnBothSides(fixture);
            await fixture.Engine.ExecuteOnceAsync(Token);
            Assert.Equal(JourneyRuntimeStage.AwaitingPickupArrival, (await fixture.RuntimeAsync()).Stage);
            Assert.Equal(LoadingPhaseStates.VehicleFull, (await fixture.RuntimeAsync()).LoadingPhaseState);
        }
        await vehicle.DeliverBufferedAcksAsync();

        JourneyRuntimeRow runtime = await fixture.RuntimeAsync();
        fixture.Riot.SetSuccessfulArrival("TO_PICKUP", runtime.PickupStationRiotId);
        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { CurrentStationId = runtime.PickupStationRiotId };
        if (fullOnTheWay)
        {
            StillFullOnBothSides(fixture);
        }
        await fixture.Engine.ExecuteOnceAsync(Token);
        await vehicle.DeliverBufferedAcksAsync();

        runtime = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, runtime.Stage);
        string arrivalStateId = (await fixture.Context.Set<JourneyStopRow>().AsNoTracking()
            .SingleAsync(row => row.JourneyId == runtime.JourneyId && row.StopRole == JourneyStopRoles.Pickup, Token))
            .VehicleBusinessMessageId;
        ProtocolOutboxRow arrivalState = await fixture.Context.ProtocolOutbox.AsNoTracking()
            .SingleAsync(row => row.MessageId == arrivalStateId, Token);
        Assert.NotNull(arrivalState.AcknowledgedAt);
        Assert.Empty(vehicle.Conflicts);
        Assert.Empty(vehicle.Regressions);
        Assert.Equal(runtime.LoadingPhaseState ?? LoadingPhaseStates.Loading, HeldLoadingPhase(vehicle));
        long[] adopted = [.. vehicle.Adopted
            .Where(item => item.MessageType == "VehicleBusinessStateSnapshot")
            .Select(item => item.Revision)];
        Assert.Equal(fullOnTheWay ? 2 : 1, adopted.Length);
        Assert.Equal(adopted.Order().Distinct(), adopted);
    }

    private static long[] AdoptedWorklists(AdoptingPeer vehicle) =>
        [.. vehicle.Adopted.Where(item => item.MessageType == "CurrentStopWorklistSnapshot").Select(item => item.Revision)];

    /// <summary>
    /// 操作员的录入，按车上那一刻手里那一版清单的号提交——与 <see cref="RuntimeFixture.SubmitSublotAsync"/> 同形，只是号由用例给，
    /// 代次是重连之后的第 2 代（推进段只认当前这一代的录入）。
    /// </summary>
    private static async Task SubmitSublotAgainstAsync(RuntimeFixture fixture, long worklistRevision)
    {
        JourneyRuntimeRow runtime = await fixture.RuntimeAsync();
        await fixture.AddInboxAsync(
            Guid.NewGuid().ToString("D"),
            "SublotSubmitted",
            new
            {
                operationSessionId = runtime.OperationSessionId,
                stationId = runtime.PickupStationId,
                worklistRevision,
                sublot = "SUBLOT-001",
                entryMethod = "SCANNER",
                @operator = new
                {
                    operatorId = "OP-001",
                    verificationMethod = "BADGE",
                    verifiedAt = fixture.Clock.GetUtcNow()
                }
            },
            sessionGeneration: 2);
    }

    /// <summary>车载端答离站核验：安全。与 <see cref="RuntimeFixture.AdvanceToGateArrivalAsync"/> 里那一条同形，代次是第 2 代。</summary>
    private static async Task AnswerDepartureSafetyAsync(RuntimeFixture fixture, JourneyRuntimeRow runtime)
    {
        Assert.Equal(JourneyRuntimeStage.AwaitingDepartureSafety, runtime.Stage);
        await fixture.AddInboxAsync(
            Guid.NewGuid().ToString("D"),
            "PreDepartureSafetyCheckResult",
            new
            {
                preDepartureSafetyCheckId = runtime.PreDepartureSafetyCheckId,
                outcome = "SAFE",
                observedAt = fixture.Clock.GetUtcNow(),
                safetyStateVersion = 7,
                validUntil = fixture.Clock.GetUtcNow().AddMinutes(1),
                safety = new
                {
                    departureSafe = true,
                    vehicleStopped = true,
                    allTargetSlotsLocked = true,
                    allUnlockOutputsReset = true,
                    unknownPresent = false,
                    reasonCodes = Array.Empty<string>()
                }
            },
            runtime.PreDepartureSafetyCheckMessageId,
            sessionGeneration: 2);
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
    private static async Task DisconnectThroughAnUnreadyRoundThenReconnectAsync(RuntimeFixture fixture, long generation = 2)
    {
        await fixture.DropOnboardSessionAsync();
        fixture.Clock.Advance(Gap);
        await fixture.Engine.ExecuteOnceAsync(Token);
        JourneyRuntimeRow voided = await fixture.RuntimeAsync();
        Assert.Null(voided.StationDepartureWaitStartedAt);
        Assert.Equal("ONBOARD_SESSION_NOT_READY", voided.BlockReasonCode);
        await ArrivalPublishInterruptedThenReconnectedTests.ReconnectAtGenerationAsync(fixture, generation);
    }

    /// <summary>取货停靠那一行，从库里读。</summary>
    private static async Task<JourneyStopRow> PickupStopAsync(RuntimeFixture fixture)
    {
        JourneyRuntimeRow runtime = await fixture.RuntimeAsync();
        return await fixture.Context.Set<JourneyStopRow>().AsNoTracking()
            .SingleAsync(row => row.JourneyId == runtime.JourneyId && row.StopRole == JourneyStopRoles.Pickup, Token);
    }

    /// <summary>到站那一版清单在发件箱里的那一行（取货停靠的第一版）。</summary>
    private static async Task<ProtocolOutboxRow> ArrivalWorklistRowAsync(RuntimeFixture fixture)
    {
        string messageId = (await PickupStopAsync(fixture)).WorklistMessageId;
        return await fixture.Context.ProtocolOutbox.AsNoTracking().SingleAsync(row => row.MessageId == messageId, Token);
    }

    /// <summary>
    /// 这条需求的一次装货取消，开着（等车的结果）。形状同 <c>JourneyRuntimeWorkerLoadDeadlineTests</c> 里那一条，从另一个上下文写，
    /// 像入站处理器那样。
    /// </summary>
    private static async Task<string> OpenALoadCancellationAsync(RuntimeFixture fixture)
    {
        JourneyRuntimeRow runtime = await fixture.RuntimeAsync();
        string workflowId = Guid.NewGuid().ToString("D");
        await using (ControlServerDbContext connection = fixture.OpenConnectionContext())
        {
            connection.RecoveryWorkflows.Add(new RecoveryWorkflowRow
            {
                WorkflowId = workflowId,
                WorkflowType = LoadCancellationBeforeSublot.WorkflowType,
                AgvId = runtime.AgvId,
                DemandId = DemandId,
                SlotOperationAttemptId = runtime.LoadSlotOperationAttemptId,
                SlotsJson = runtime.TargetSlotsJson,
                State = RecoveryWorkflowState.AwaitingResult,
                RequestMessageId = Guid.NewGuid().ToString("D"),
                RequestContentHash = new string('c', 64),
                CreatedAt = fixture.Clock.GetUtcNow(),
                UpdatedAt = fixture.Clock.GetUtcNow()
            });
            await connection.SaveChangesAsync(Token);
        }
        fixture.Context.ChangeTracker.Clear();
        return workflowId;
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
