using System.Text.Json;
using ControlServer.Domain;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using static ControlServer.Tests.ClosureSnapshotAssertions;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;

namespace ControlServer.Tests;

/// <summary>
/// 旅程收尾之后，车上那一站的清单、计划与业务状态要被撤掉（control-server#323，program#86 v2 的 A 形态）。
/// </summary>
/// <remarks>
/// <para>
/// <b>修前的样子。</b>收尾（<c>PickupStopTermination.StageJourneyClosureAsync</c>）只写租约、占用、录入请求结算与 Completed，
/// 不碰发件箱；空清单那条路在 <c>PublishStopWorklistAsync</c> 里直接 return。车于是一直显示已经结束的那一站：清单、
/// 「取消装货」按钮、装货阶段那一行（agv01 09-15 的形态）。正常卸完也一样，车上留着卸货站那一版。
/// </para>
/// <para>
/// <b>车载端会把旅程快照写进本地日志库、重连前恢复</b>（onboard-hmi <c>WireToGateSessionClient.RestorePersistedJourneyProjectionAsync</c>），
/// 所以一次断线救不了残留：收尾那一刻没送到的收尾快照，要在重连答复恢复报告之后补发，直到这辆车有了下一趟旅程。
/// </para>
/// <para>
/// 来路 4（协调器里已下命令之后的几种结束）在 <c>RecoveryStateMachineG2Tests</c>，来路 5（到站之前的释放）在
/// <c>Batch7DemandReleaseServiceTests</c>，各自的夹具在那里。
/// </para>
/// </remarks>
public sealed class JourneyClosureSnapshotTests
{
    private const string FirstDemandId = "10000000-0000-4000-8000-000000000001";
    private const string NextDemandId = "10000000-0000-4000-8000-000000000002";
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>来路 1：站点期限到期，结束的是旅程里最后一条开着的需求。</summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("IntegrationSlice", "FP-IS-02")]
    public async Task AStationDeadlineThatEndsTheJourneyTellsTheVehicleTheStopIsOver()
    {
        await using RuntimeFixture fixture = await AtSublotWaitWithDeadlineAsync();

        fixture.Clock.Advance(TimeSpan.FromSeconds(10));
        await fixture.Engine.ExecuteOnceAsync(Token);

        JourneyRuntimeRow runtime = await fixture.RuntimeAsync();
        Assert.Equal((JourneyRuntimeStage.Completed, "CANCELLED_BY_STATION_TIMEOUT"), (runtime.Stage, runtime.BlockReasonCode));
        await AssertClosureSentAsync(fixture.Context, runtime.AgvId, Sent(fixture), 1, runtime.PickupStationId);
    }

    /// <summary>来路 2：站点期限之后到达的确定的装货失败。</summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("IntegrationSlice", "FP-IS-02")]
    public async Task ADeterminateLoadFailureThatEndsTheJourneyTellsTheVehicleTheStopIsOver()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        await JourneyRuntimeWorkerLoadDeadlineTests.AdvanceToLoadWithStationDeadlineAsync(fixture);
        fixture.Clock.Advance(TimeSpan.FromSeconds(11));
        await JourneyRuntimeWorkerLoadDeadlineTests.ReportLoadResultAsync(
            fixture, JourneyRuntimeWorkerLoadDeadlineTests.DeterminateFailureSlots(fixture, "OPERATOR_TIMEOUT"));

        await fixture.Engine.ExecuteOnceAsync(Token);

        JourneyRuntimeRow runtime = await fixture.RuntimeAsync();
        Assert.Equal((JourneyRuntimeStage.Completed, "CANCELLED_BY_STATION_TIMEOUT"), (runtime.Stage, runtime.BlockReasonCode));
        await AssertClosureSentAsync(fixture.Context, runtime.AgvId, Sent(fixture), 1, runtime.PickupStationId);
    }

    /// <summary>
    /// 来路 3：扫码前取消的 ALL_EMPTY 结果。收尾在协调器里暂存、随结果那一次保存落库，发送走协调器已有的「答复之后再发」那条链。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("IntegrationSlice", "FP-IS-02")]
    public async Task AnAllEmptyCancellationBeforeAnySublotTellsTheVehicleTheStopIsOver()
    {
        await using RuntimeFixture fixture = await JourneyRuntimeWorkerLoadCancellationBeforeSublotTests.ReachSublotWaitAsync();
        await using ControlServerDbContext connection = fixture.OpenConnectionContext();
        OnboardMessageProcessor processor = TestOnboardProcessorFactory.Create(
            connection, new WireToGateStore(connection), fixture.Clock, new ConfigurationBuilder().Build(), fixture.Peer);
        OnboardConnectionState state = JourneyRuntimeWorkerLoadCancellationBeforeSublotTests.BeforeSublotConnection(fixture, 1);
        // 会话中途的连接：握手已经结束。握手里服务端不推任何东西（control-server#202），协调器那条「答复之后再发」的链
        // 只对握手之后的消息开，而取消的结果只会在那之后到。
        state.HandshakeCompleted = true;

        await processor.ProcessAsync(
            JourneyRuntimeWorkerLoadCancellationBeforeSublotTests.CancellationBeforeSublotRequest(
                fixture, JourneyRuntimeWorkerLoadCancellationBeforeSublotTests.BeforeSublotCancellationId, 1),
            state, Token);
        await processor.ProcessAsync(
            JourneyRuntimeWorkerLoadCancellationBeforeSublotTests.CancellationBeforeSublotResult(
                fixture, "c3230000-0000-4000-8000-000000000101", 1),
            state, Token);

        JourneyRuntimeRow runtime = await fixture.RuntimeAsync();
        Assert.Equal((JourneyRuntimeStage.Completed, "CANCELLED_BY_OPERATOR"), (runtime.Stage, runtime.BlockReasonCode));
        await AssertClosureSentAsync(fixture.Context, runtime.AgvId, Sent(fixture), 1, runtime.PickupStationId);
    }

    /// <summary>
    /// 来路 6：正常卸完、旅程 Completed。车上留着的是卸货站那一版清单与到站那一版计划，业务状态还带着装货阶段。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("IntegrationSlice", "FP-IS-04")]
    public async Task ANormalUnloadThatCompletesTheJourneyTellsTheVehicleTheJourneyIsOver()
    {
        await using RuntimeFixture fixture = await ReadyWithOneDemandAsync();

        JourneyRuntimeRow runtime = await fixture.RunToCompletionAsync();

        Assert.Equal((JourneyRuntimeStage.Completed, (string?)null), (runtime.Stage, runtime.BlockReasonCode));
        await AssertClosureSentAsync(fixture.Context, runtime.AgvId, Sent(fixture), 1, runtime.GateStationId);
    }

    /// <summary>
    /// 下一趟旅程的第一版号在三条流上都大于收尾号，车载端一次回退都没有；下一趟开始之后，旧的收尾快照不再补发——
    /// 既不经引擎的补发（白名单是新旅程的），也不经重连之后的补发（只补「最近一趟、已收尾」的）。
    /// </summary>
    /// <remarks>
    /// 收尾快照故意一张都不确认：确认过的行本来就不补发，那样的「不补发」证明不了任何东西。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("IntegrationSlice", "FP-IS-04")]
    public async Task TheNextJourneyStartsAboveTheClosureAndTheClosureIsNeverReplayedIntoIt()
    {
        await using RuntimeFixture fixture = await ReadyWithOneDemandAsync();
        AdoptingPeer peer = new(fixture.Context, fixture.Clock);
        fixture.Peer.OnMessageSent = line =>
        {
            peer.Receive(line);
            return Task.CompletedTask;
        };
        JourneyRuntimeRow first = await fixture.RunToCompletionAsync();
        IReadOnlyList<Snapshot> closure = await AssertClosureSentAsync(
            fixture.Context, first.AgvId, Sent(fixture), 1, first.GateStationId);
        peer.LoseBufferedAcks();
        int linesAtClosure = fixture.Peer.Lines.Count;
        // 第一趟途中这个替身已经记过回退：用例一张确认都不投递，引擎每轮把没确认的旧版补发一遍。那是夹具的用法，与收尾无关，
        // 所以只看收尾之后新增的。
        int regressionsAtClosure = peer.Regressions.Count;

        AcceptedDemandSnapshot firstDemand = fixture.Demand(FirstDemandId, "SUBLOT-001", createdAt: Now.AddMinutes(-10));
        AcceptedDemandSnapshot next = fixture.Demand(NextDemandId, "SUBLOT-002", createdAt: Now.AddMinutes(-5));
        fixture.Catalog.Set([firstDemand, next]);
        fixture.BoxCounts.Set("SUBLOT-002", 7);
        await fixture.Engine.ExecuteOnceAsync(Token);
        await fixture.Engine.ExecuteOnceAsync(Token);
        JourneyRuntimeRow second = await fixture.RuntimeAsync(NextDemandId);
        Assert.Equal(JourneyRuntimeStage.AwaitingPickupArrival, second.Stage);

        // 第一版号：受理时由按车计数器给定的三个基准。
        Assert.True(second.VehicleBusinessRevision > Revision(closure, "VehicleBusinessStateSnapshot"));
        Assert.True(second.WorklistRevision > Revision(closure, "CurrentStopWorklistSnapshot"));
        Assert.True(second.PlanRevision > Revision(closure, "UpcomingStopPlanSnapshot"));

        // 车一辆一辆地重连：握手之后协调器补发它该补的一切。
        await using ControlServerDbContext connection = fixture.OpenConnectionContext();
        OnboardRecoveryCoordinator coordinator = TestOnboardProcessorFactory.CreateRecoveryCoordinator(
            connection, new WireToGateStore(connection), fixture.Clock, new ConfigurationBuilder().Build(), fixture.Peer);
        await coordinator.ReplayPendingCommandsAsync(first.AgvId, 1, Token);
        await fixture.Engine.ExecuteOnceAsync(Token);

        string[] closureIds = [.. closure.Select(item => item.MessageId)];
        Assert.DoesNotContain(
            fixture.Peer.Lines.Skip(linesAtClosure).Select(Line),
            line => closureIds.Any(id => SentAs(line, id, 1)));
        Assert.Empty(peer.Regressions.Skip(regressionsAtClosure));
        Assert.Contains(peer.Adopted, item =>
            item.MessageType == "UpcomingStopPlanSnapshot" && item.Revision == second.PlanRevision);
    }

    /// <summary>
    /// 收尾那一刻车没收到（链路断在这一刻）：收尾快照留在发件箱里，车重连、恢复报告被答复之后，以新的一代补发。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("IntegrationSlice", "FP-IS-05")]
    public async Task AClosureTheVehicleMissedIsReplayedAfterItReconnects()
    {
        await using RuntimeFixture fixture = await AtSublotWaitWithDeadlineAsync();
        // 只拒收件箱里「收尾形状」的那几行：收尾那一轮开头的补发还要照常走通，否则这一轮根本走不到收尾。
        fixture.Peer.OnMessageSent = line => IsClosureShaped(line)
            ? throw new IOException("The link dropped as the closure went out.")
            : Task.CompletedTask;

        fixture.Clock.Advance(TimeSpan.FromSeconds(10));
        await fixture.Engine.ExecuteOnceAsync(Token);
        JourneyRuntimeRow runtime = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.Completed, runtime.Stage);
        await AssertClosureStagedAsync(fixture.Context, runtime.AgvId, runtime.PickupStationId);

        fixture.Peer.OnMessageSent = null;
        await fixture.ReconnectAsync(2);
        await fixture.AdvanceSessionAsync(2);
        int linesBeforeReplay = fixture.Peer.Lines.Count;
        await using ControlServerDbContext connection = fixture.OpenConnectionContext();
        OnboardRecoveryCoordinator coordinator = TestOnboardProcessorFactory.CreateRecoveryCoordinator(
            connection, new WireToGateStore(connection), fixture.Clock, new ConfigurationBuilder().Build(), fixture.Peer);
        await coordinator.ReplayPendingCommandsAsync(runtime.AgvId, 2, Token);

        await AssertClosureSentAsync(
            fixture.Context, runtime.AgvId, fixture.Peer.Lines.Skip(linesBeforeReplay).Select(Line), 2, runtime.PickupStationId);
    }

    /// <summary>
    /// 同上，但车重连回来时会话仍因本服务端自己的在途单而未就绪（<c>DEPARTURE_SAFETY_NOT_READY</c>，安全原因只有
    /// <c>VEHICLE_NOT_READY</c>）——真车载端在车带着本服务端的单行驶时就是这个状态，合成车载端看不到（control-server#314）。
    /// </summary>
    /// <remarks>
    /// 补发不看会话就不就绪：它挂在恢复报告的答复之后（握手已完成），而车载端收行程快照不看自己的就绪状态。
    /// 等会话回到 <c>Ready</c> 再发没有意义——旅程已经收尾，引擎不再推进它，那时也没有谁会再发。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("IntegrationSlice", "FP-IS-05")]
    public async Task AMissedClosureIsReplayedAfterAReconnectEvenWhileTheOwnOrderKeepsTheSessionNotReady()
    {
        await using RuntimeFixture fixture = await AtSublotWaitWithDeadlineAsync();
        fixture.Peer.OnMessageSent = line => IsClosureShaped(line)
            ? throw new IOException("The link dropped as the closure went out.")
            : Task.CompletedTask;
        fixture.Clock.Advance(TimeSpan.FromSeconds(10));
        await fixture.Engine.ExecuteOnceAsync(Token);
        JourneyRuntimeRow runtime = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.Completed, runtime.Stage);

        fixture.Peer.OnMessageSent = null;
        await fixture.ReconnectAsync(2);
        await fixture.AdvanceSessionAsync(2);
        await PickupDispatchPlanPastOwnOrderTests.DropSessionOnOwnOrderAsync(fixture);
        Assert.Equal("DEPARTURE_SAFETY_NOT_READY",
            (await fixture.Context.SessionRecoveries.AsNoTracking().SingleAsync(Token)).ReasonCode);
        int linesBeforeReplay = fixture.Peer.Lines.Count;
        await using ControlServerDbContext connection = fixture.OpenConnectionContext();
        OnboardRecoveryCoordinator coordinator = TestOnboardProcessorFactory.CreateRecoveryCoordinator(
            connection, new WireToGateStore(connection), fixture.Clock, new ConfigurationBuilder().Build(), fixture.Peer);
        await coordinator.ReplayPendingCommandsAsync(runtime.AgvId, 2, Token);

        await AssertClosureSentAsync(
            fixture.Context, runtime.AgvId, fixture.Peer.Lines.Skip(linesBeforeReplay).Select(Line), 2, runtime.PickupStationId);
    }

    private static async Task<RuntimeFixture> ReadyWithOneDemandAsync()
    {
        RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(FirstDemandId, "SUBLOT-001", createdAt: Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 7);
        return fixture;
    }

    private static async Task<RuntimeFixture> AtSublotWaitWithDeadlineAsync()
    {
        RuntimeFixture fixture = await ReadyWithOneDemandAsync();
        fixture.Options.StationDepartureWaitTimeout = TimeSpan.FromSeconds(10);
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, (await fixture.AdvanceToSublotWaitAsync()).Stage);
        await fixture.ProveSlotDoorsClosedAsync();
        return fixture;
    }

    private static long Revision(IReadOnlyList<Snapshot> closure, string messageType) =>
        closure.Single(item => item.MessageType == messageType).Revision;

    private static IEnumerable<string> Sent(RuntimeFixture fixture) => fixture.Peer.Lines.Select(Line);

    private static string Line(byte[] bytes) => System.Text.Encoding.UTF8.GetString(bytes);

    /// <summary>空清单、空计划、不带旅程的业务状态——收尾快照的三种形状，按内容认，不按 id。</summary>
    private static bool IsClosureShaped(string line)
    {
        using JsonDocument document = JsonDocument.Parse(line.TrimEnd('\n'));
        JsonElement root = document.RootElement;
        JsonElement payload = root.GetProperty("payload");
        return root.GetProperty("messageType").GetString() switch
        {
            "CurrentStopWorklistSnapshot" => payload.GetProperty("items").GetArrayLength() == 0,
            "UpcomingStopPlanSnapshot" => payload.GetProperty("legs").GetArrayLength() == 0,
            "VehicleBusinessStateSnapshot" => payload.GetProperty("activePurpose").ValueKind == JsonValueKind.Null,
            _ => false
        };
    }
}
