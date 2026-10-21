using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using ControlServer.FakeOnboard;
using ControlServer.Host.Runtime;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Persistence;
using ControlServer.TestDoubles;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ControlServer.Tests;

/// <summary>
/// 车载机断电后重连，不需要任何人去重启服务端（control-server#276）。
/// </summary>
/// <remarks>
/// <para>
/// <b>这一类守的是「断电恢复不需要人」。</b>车载机断电不是正常关机：旧 TCP 连接既收不到 FIN 也收不到 RST，
/// 服务端那一侧的 socket 会一直开着。MVP 线（<c>ControlServer_MVP@670bdd45</c>）上这正是现场 09-21 的故障：
/// 那里一次只服务一条连接、读不设超时，半开的旧连接永远占着接收循环，新连接在 listen backlog 里排队，
/// 只有重启服务端才恢复。
/// </para>
/// <para>
/// <b>v2 上承重的是新连接顶替旧连接（control-server#310）。</b>v2 并发服务连接，<see cref="OnboardPeer"/> 规定一辆车只有
/// 一条可路由连接。cs#310 之前，旧连接还挂着时新连接要等握手全部答完、在 Attach 时才被拒
/// （<c>An Onboard peer is already attached</c>），车「就绪了又断」，要等 <see cref="OnboardTcpServer"/> 读操作上的
/// 静默窗口（ADR-cross-0027，六秒无合法入站即关）把旧连接放掉才进得来；现场 CPE 在 AP 之间漫游一次，就被放大成连着断
/// 好几次。cs#310 起，新连接的 SessionHello 一经受理，旧连接就被摘出路由表并关掉，第一次重连就进得来。静默窗口仍在，
/// 管的是车没回来的那一种。<b>谁要改那个窗口、或改「一车一条连接」的 Attach／顶替规则，先看这一类。</b>
/// </para>
/// <para>
/// <b>怎么造断电。</b><see cref="PowerCutRelay"/> 夹在合成车载端与服务端之间转发字节；断电时它不再往服务端转发
/// 任何东西，也永远不关服务端那一侧的 socket。服务端写过来的字节，要么读走丢掉（像内核收进发送缓冲区），
/// 要么完全不读、让发送缓冲区堆满（<see cref="ReconnectsWhileTheServerIsStillWritingIntoTheDeadConnection"/>）。
/// 车载端随后按现场的做法重连：失败就隔两秒再试。
/// </para>
/// <para>
/// <b>判据按事件，不按墙钟。</b>一个旁路观察者每 10 ms 读一次路由表，记下旧代次离开它的那一刻。要求的是：
/// 断电后的第一次尝试就成功（新代次可路由，并连续三秒不被关）；旧代次离开了路由表；服务端关掉了旧连接（中继读到
/// 服务端那一侧的 EOF），此后旧连接上再没有收到一行。cs#310 之前这里判的是「旧代次离表之后的第一次尝试成功」，那时恢复时刻是双峰的
/// （第 3 次约 6 秒，或第 4 次约 9 秒）。墙钟只剩 <see cref="HangGuard"/> 一道挂死保护。
/// </para>
/// <para>
/// <b>每一次尝试也按事件判</b>（control-server#440）：等握手答完，再等服务端二选一——让新代次可路由，或者拒掉它、关掉连接。
/// 以前一次尝试只给 4 秒握手、答完之后再给 1 秒可路由，满载的机器上服务端只是慢，就被记成「失败」或「不可路由」：
/// 本机全量里旧代次离表之后的那一次尝试以 4 秒超时记为失败，类就红了。那 4 秒是这个类自己定的，产品里没有对应的预算：服务端
/// 不给握手设期限，合成车载端也不设；现场车载端设的是每一条答复 2.5 秒（<c>messageTimeoutMs</c>），超时就隔两秒重来——那管的是
/// 现场恢复得快不快，不是这里要证的「旧连接一放掉，下一次就进得来」。
/// </para>
/// <para>
/// <b>前提也按路由表判。</b>第一次尝试之前旧代次必须还在表里——这证明这一次确实造出了「旧连接半开占着这辆车」，
/// 而不是旧连接早已被关、测试平白绿了。只看车载端读到 <c>SessionReadiness</c> 不够：cs#310 之前被 Attach 拒掉的
/// 那几次握手，车载端也读到了。
/// </para>
/// </remarks>
public sealed class OnboardPowerLossReconnectTests
{
    private const string AgvId = "AGV-POWER-01";
    private const string CredentialVariable = "CONTROL_SERVER_ONBOARD_CREDENTIAL_POWER_LOSS_TESTS";
    private const string Credential = "power-loss-test-credential";

    /// <summary>车载端重连失败后等多久再试，取现场车载端日志里的「将在2秒后重连」。</summary>
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(2);

    /// <summary>
    /// 挂死保护：断电之后过了这么久还没恢复，就不再试、判失败。它不是恢复时间的判据。
    /// </summary>
    /// <remarks>
    /// 要比「六秒窗口 + 一次重试 + 一次尝试」大得多，免得慢机器上假红；又要小于六十秒，这样有人把窗口改成一分钟
    /// （反向验证 M2）时旧代次在它之内离不开路由表，这一类照样红。窗口的值本身由
    /// <c>OnboardSilentLivenessLossTests.TheProjectWideLivenessTimeoutIsTheSixSecondsAdrCross0027Fixes</c> 按字面钉住。
    /// 本类实测（cs#276，本机与审查员本机的 Release 构建）：旧代次约 5.5 秒离开路由表，第 3 次尝试、约 6 秒恢复。
    /// cs#276 第一步探针打印的 9.1～9.3 秒含之后 3 秒的稳定观察，恢复时刻同样约 6 秒。
    /// </remarks>
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

    /// <summary>可路由之后要连续多久不被服务端关掉，才算会话真的建起来了。</summary>
    private static readonly TimeSpan StaysUp = TimeSpan.FromSeconds(3);

    /// <summary>
    /// 旧连接上什么都不再发生：不推送、服务端写来的都被读走丢掉。车立刻重连（现场 CPE 漫游就是这样：断 1～2 秒就回来，
    /// 旧连接离六秒静默窗口到期还早），第一次就要进得来（control-server#310）。
    /// </summary>
    /// <remarks>
    /// cs#310 之前，这一次握手全部答完、车读到 <c>SessionReadiness</c> 之后才在 Attach 时被拒，车「就绪了又断」，
    /// 要隔两秒再试，直到旧连接被静默窗口放掉。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-05")]
    public async Task ReconnectsAfterAPowerCutThatLeftTheOldConnectionHalfOpen()
    {
        await using Rig rig = await Rig.StartAsync();
        long oldGeneration = await rig.ConnectFirstSessionAsync();

        rig.Relay.CutPower(drainServerWrites: true);
        Outcome outcome = await rig.ReconnectAfterPowerCutAsync(oldGeneration);

        outcome.AssertRecovered();
        Assert.True(outcome.OldConnectionClosedByServer, "新连接顶替之后，服务端没有关掉旧连接。" + outcome.Describe());
    }

    /// <summary>
    /// 断电后服务端还在往旧会话推送，而那条死连接不再有人读：发送缓冲区堆满，写操作卡住。
    /// </summary>
    /// <remarks>
    /// 这是最可能把接收一侧也拖住的形状（旧连接的写卡在发送门上），所以单列一条：它要证的是推送卡住的只是
    /// 推送本身，新连接照样顶替进来、卡住的那次写随旧连接被关而结束。推送走 <see cref="OnboardPeer"/>，
    /// 与引擎每一轮下发同一条路。重连之前先等到有一次推送挂满一秒，否则这一条没造出它要的形状。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-05")]
    public async Task ReconnectsWhileTheServerIsStillWritingIntoTheDeadConnection()
    {
        await using Rig rig = await Rig.StartAsync();
        long oldGeneration = await rig.ConnectFirstSessionAsync();

        rig.Relay.CutPower(drainServerWrites: false);
        using CancellationTokenSource pushing = new();
        Task pusher = rig.PushToSessionUntilCancelledAsync(oldGeneration, payloadBytes: 64 * 1024, pushing.Token);
        Outcome outcome;
        try
        {
            // Inside the old connection's six-second window, so it is still routable when the vehicle comes back.
            Assert.True(
                await rig.WaitPushStuckAsync(TimeSpan.FromSeconds(1), within: TimeSpan.FromSeconds(4)),
                "断电后 4 秒内没有一次推送挂满 1 秒：写没有卡住，这一条没造出它要的形状。");
            outcome = await rig.ReconnectAfterPowerCutAsync(oldGeneration);
        }
        finally
        {
            await pushing.CancelAsync();
            await pusher;
        }

        outcome.AssertRecovered();
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"断电后最长的一次推送：{rig.LongestPush.TotalMilliseconds:F0} ms；推送的失败：{rig.PushFailures}");
        Assert.True(
            rig.LongestPush >= TimeSpan.FromSeconds(1),
            $"断电后最长的一次推送只挂了 {rig.LongestPush.TotalMilliseconds:F0} ms：写没有卡住，这一条没造出它要的形状。");
        // The stuck write ended because the takeover closed the old connection, not because the write timeout ran out.
        Assert.Contains(
            rig.PushFailureReasons,
            reason => reason.StartsWith("OnboardConnectionUnavailableException: The connection to the Onboard peer", StringComparison.Ordinal) &&
                      reason.Contains("is closed or broken", StringComparison.Ordinal));
        Assert.DoesNotContain(rig.PushFailureReasons, reason => reason.Contains("did not finish within", StringComparison.Ordinal));
    }

    /// <summary>
    /// 握手之后的那一轮补发（<c>OnboardRecoveryCoordinator.ReplayPendingCommandsAsync</c>）在顶替进来的新连接上照常执行：
    /// 旧代次没确认的出发答复，排在 <c>SessionReadiness</c> 之后、带着新代次送到车上，车回了 DurableAck（control-server#310）。
    /// </summary>
    /// <remarks>
    /// cs#310 之前，这一轮补发根本走不到：Attach 在补发之前抛出，连接随之结束，答复要等旧连接被放掉之后的下一次重连。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-05")]
    public async Task TheReplayAfterTheHandshakeRunsOnTheConnectionThatTookOver()
    {
        await using Rig rig = await Rig.StartAsync();
        long oldGeneration = await rig.ConnectFirstSessionAsync();

        rig.Relay.CutPower(drainServerWrites: true);
        // Staged after the cut: nothing reaches the server's side of the old connection any more, so this write to the
        // database the processor shares cannot race one of its own.
        await rig.StageDepartureResultAsync(oldGeneration);
        Outcome outcome = await rig.ReconnectAfterPowerCutAsync(oldGeneration);

        outcome.AssertRecovered();
        long newGeneration = rig.Engine.Snapshot().State.SessionGeneration;
        (string Type, long? Generation, string? MessageId)[] toVehicle = rig.Relay.CurrentLinkLinesFromServer();
        string trace = Environment.NewLine + "新连接上服务端写来的：" +
                       string.Join("、", toVehicle.Select(line => $"{line.Type}#{line.Generation}"));
        int readiness = Array.FindLastIndex(toVehicle, line => line.Type == "SessionReadiness");
        int result = Array.FindIndex(toVehicle, line => line.Type == CurrentStopDepartures.ResultType);
        Assert.True(readiness >= 0, "新连接上没有 SessionReadiness。" + trace);
        Assert.True(result > readiness, "出发答复没有排在 SessionReadiness 之后补发。" + trace);
        Assert.Equal(newGeneration, toVehicle[result].Generation);
        Assert.True(
            await rig.WaitWireAsync("out", "DurableAck", TimeSpan.FromSeconds(5)) &&
            rig.Onboard.Wire().Any(item => item.Direction == "in" && item.MessageType == CurrentStopDepartures.ResultType &&
                                           item.MessageId == toVehicle[result].MessageId),
            "车没有收到补发的出发答复、或没有回 DurableAck。" + trace);
    }

    /// <summary>
    /// 顶替之后旧连接不再收到任何东西，会话代次前进一代：路由表里是新代次，给旧代次的推送被拒，给新代次的推送送到新连接
    /// （control-server#310）。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-05")]
    public async Task AfterATakeoverTheOldSocketReceivesNothingAndTheGenerationMovesOn()
    {
        await using Rig rig = await Rig.StartAsync();
        long oldGeneration = await rig.ConnectFirstSessionAsync();

        rig.Relay.CutPower(drainServerWrites: true);
        Outcome outcome = await rig.ReconnectAfterPowerCutAsync(oldGeneration);
        outcome.AssertRecovered();

        long newGeneration = rig.Engine.Snapshot().State.SessionGeneration;
        Assert.Equal(oldGeneration + 1, newGeneration);
        Assert.Equal(newGeneration, rig.Peer.ConnectedSessionGeneration(AgvId));

        OnboardConnectionUnavailableException refused = await Assert.ThrowsAsync<OnboardConnectionUnavailableException>(
            () => rig.PushOnceAsync(oldGeneration));
        Assert.Contains($"session generation {newGeneration}", refused.Message, StringComparison.Ordinal);
        string delivered = await rig.PushOnceAsync(newGeneration);
        Assert.True(
            await rig.WaitWireAsync("in", "FaultCargoRecoveryCommand", TimeSpan.FromSeconds(5), delivered),
            "给新代次的推送没有送到新连接上的车。");

        Assert.True(outcome.OldConnectionClosedByServer, "新连接顶替之后，服务端没有关掉旧连接。" + outcome.Describe());
        // From the takeover on: a HeartbeatAck already in flight at the cut may land on the old connection before it.
        Assert.Empty(rig.Relay.CutLinkLinesFromServer(outcome.OldGenerationLeftAtTimestamp!.Value));
        // The takeover is logged as one (1007), not as a connection that ended in an error (1003).
        EventRecordingLogger<OnboardTcpServer>.Entry[] serverLog = rig.ServerLogSnapshot();
        Assert.Contains(serverLog, entry => entry.EventId.Id == 1007);
        Assert.DoesNotContain(serverLog, entry => entry.EventId.Id == 1003);
    }

    /// <summary>
    /// 车还活着、还在发心跳，只是不再读服务端写来的东西（control-server#334）：服务端的写有上限，到点就关掉这条连接、
    /// 把它从路由表摘掉，车重连之后照常建起会话。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>这一格静默窗口管不到。</b>心跳一直在到，服务端读到心跳要写 HeartbeatAck，这次写排在卡住的推送后面，接收循环就停在
    /// 写上；静默窗口只挂在读上，永远走不到。修之前没有任何东西收场：旧代次一直占着路由表，推送一直挂着，车重连也会被
    /// 「already attached」拒掉。
    /// </para>
    /// <para>
    /// <b>两条前提</b>证明造出来的确实是这一格：聋了之后车往服务端的字节一直在流（心跳没停）；最长的一次推送挂到了配置超时的八成
    /// 以上（写真的卡住了；门槛取比例而不是等于超时本身，审查必修 3 实测等于时余量只有 3 ms）。
    /// </para>
    /// <para>
    /// 「挂了多久」从<b>最后一次写成功的推送开始</b>量到旧代次离开路由表，不再取最长的一次推送（control-server#440 审查 S1）。卡住的
    /// 写排在最后一次成功之后，所以在对的产品上这段时间不短于写超时，负载只会让它更长；而卡住的若是 HeartbeatAck、推送晚一点才排到
    /// 它后面，最长的一次推送就只剩写超时减去那段迟到——1 秒那一格只有约 150 ms 余量，满载时会假红。
    /// </para>
    /// <para>
    /// <b>判据按事件，不按墙钟</b>（审查建议）：旧代次离开了路由表；这条连接上有一次写以写超时失败，消息里是<b>配置的</b>秒数与这台车的
    /// 车号；服务端记下连接因错误结束（1003），没有记静默关闭（1005）——放掉它的是写超时，不是静默窗口。墙钟只剩 <see cref="HangGuard"/>。
    /// </para>
    /// <para>
    /// <b>超时的那一次写可能是推送，也可能是接收循环自己的 HeartbeatAck</b>（control-server#440）。两者排在同一道发送门后、各自计时；
    /// 推送把缓冲区写满的那一刻如果正轮到 HeartbeatAck，卡住的就是它，先到点的也是它，推送随后只看到「连接已关」。这是产品承诺过的
    /// 行为（<c>OnboardPeerConnection</c> 的注释），所以两处都要看：推送记下的失败，和 1003 记下的那个异常。1003 在 <c>Detach</c>
    /// 之后、连接释放完才写，所以先等它出现，再读日志。
    /// </para>
    /// </remarks>
    /// <param name="writeTimeoutMilliseconds">
    /// null 是生产默认值。1000 那一格钉的是「监听器把配置的值交给了连接」：它必须比默认的 5 秒早放掉旧连接。
    /// </param>
    [Theory]
    [InlineData(null)]
    [InlineData(1000)]
    [Trait("IntegrationSlice", "FP-IS-05")]
    public async Task AVehicleThatKeepsTalkingButStopsReadingIsLetGoAndReconnects(int? writeTimeoutMilliseconds)
    {
        TimeSpan? configured = writeTimeoutMilliseconds is { } ms ? TimeSpan.FromMilliseconds(ms) : null;
        await using Rig rig = await Rig.StartAsync(configured);
        long oldGeneration = await rig.ConnectFirstSessionAsync();

        rig.Relay.StopReadingServerWrites();
        // Talking first: a heartbeat has to reach the server after the vehicle stopped reading, or a short write timeout
        // would let go of the connection before this was ever the shape under test.
        Assert.True(
            await rig.Relay.WaitBytesToServerWhileDeafAsync(TimeSpan.FromSeconds(5)),
            "变聋之后 5 秒内车没往服务端送任何东西：心跳停了，这不是本条要造的形状。");
        Stopwatch sinceDeaf = Stopwatch.StartNew();
        using CancellationTokenSource pushing = new();
        Task pusher = rig.PushToSessionUntilCancelledAsync(oldGeneration, payloadBytes: 64 * 1024, pushing.Token, sinceDeaf);
        TimeSpan? leftAfter;
        try
        {
            leftAfter = await rig.WaitGenerationLeavesAsync(oldGeneration, HangGuard, sinceDeaf);
        }
        finally
        {
            await pushing.CancelAsync();
            await pusher;
        }

        // The old generation leaves the table in HandleClientAsync's finally; 1003 is written only after the exception has
        // come out through the scope, the connection and the stream (control-server#440). Wait for it, not for the Detach.
        if (leftAfter is not null)
        {
            await rig.WaitServerLogAsync(1003, HangGuard);
        }
        EventRecordingLogger<OnboardTcpServer>.Entry[] serverLog = rig.ServerLogSnapshot();
        TimeSpan timeout = configured ?? OnboardTransportOptions.DefaultWriteTimeout;
        // Every write failure on the old connection: the pusher's, and the one the receive loop ended with.
        string[] writeFailures =
        [
            .. rig.PushFailureReasons,
            .. serverLog.Where(entry => entry.EventId.Id == 1003 && entry.Error is not null)
                .Select(entry => $"{entry.Error!.GetType().Name}: {entry.Error.Message}")
        ];
        string trace =
            $"{Environment.NewLine}服务端日志：{string.Join("、", serverLog.Select(entry => entry.EventId.Id))}；" +
            $"聋了之后车往服务端送了 {rig.Relay.BytesToServerWhileDeaf} 字节；" +
            $"最长的一次推送 {rig.LongestPush.TotalMilliseconds:F0} ms；" +
            $"最后一次写成功的推送开始于 t={rig.LastSuccessfulPushStartedAt.TotalSeconds:F1}s；" +
            $"旧代次离开路由表：{(leftAfter is { } at ? $"t={at.TotalSeconds:F1}s" : "没有")}；" +
            $"推送的失败：{rig.PushFailures}；" +
            $"1003 的异常：{string.Join(" | ", writeFailures.Skip(rig.PushFailureReasons.Count))}";
        TestContext.Current.TestOutputHelper?.WriteLine(trace.TrimStart());
        Assert.True(rig.Relay.BytesToServerWhileDeaf > 0, "聋了之后车没再往服务端送任何东西：这是断电，不是本条要造的形状。" + trace);
        Assert.True(
            leftAfter is not null,
            $"车还在发心跳、只是不读，{HangGuard.TotalSeconds} 秒内旧代次一直没离开路由表：卡住的写没有上限，" +
            "这条连接永远不会被放掉（control-server#334）。" + trace);
        // The write really was stuck: from the last write that went through to the release, at least eight tenths of the
        // timeout passed. Measured from the last success rather than as the longest push, which is short whenever the
        // HeartbeatAck is the write that got stuck and the next push queued behind it late (control-server#440, review S1).
        TimeSpan stuckFor = leftAfter!.Value - rig.LastSuccessfulPushStartedAt;
        Assert.True(
            stuckFor >= timeout * 0.8,
            $"最后一次写成功的推送在 t={rig.LastSuccessfulPushStartedAt.TotalSeconds:F1}s 开始，到旧代次离开路由表只隔了 " +
            $"{stuckFor.TotalMilliseconds:F0} ms，不到写超时 {timeout.TotalSeconds:0.###} 秒的八成：写没有卡住，这一条没造出它要的形状。" + trace);
        // One and the same failure carries all three: the configured seconds, the exact type and this vehicle.
        string? timedOut = Array.Find(
            writeFailures, failure => failure.Contains($"did not finish within {timeout.TotalSeconds:0.###} s", StringComparison.Ordinal));
        Assert.True(
            timedOut is not null,
            $"这条连接上没有一次写以「did not finish within {timeout.TotalSeconds:0.###} s」失败：放掉它的不是配置的写超时。" + trace);
        // The exact type, not just an IOException: it is what the runtime yields on (incremental review X2).
        Assert.StartsWith("OnboardConnectionUnavailableException: A write to the Onboard peer", timedOut, StringComparison.Ordinal);
        Assert.Contains($"'{AgvId}'", timedOut, StringComparison.Ordinal);
        Assert.Contains(serverLog, entry => entry.EventId.Id == 1003);
        Assert.DoesNotContain(serverLog, entry => entry.EventId.Id == 1005);

        rig.Relay.ResumeReadingServerWrites();
        await rig.DisconnectOnboardAsync();
        long newGeneration = await rig.ReconnectRoutableAsync();
        Assert.NotEqual(oldGeneration, newGeneration);
    }

    /// <summary>一次重连尝试的结局。</summary>
    private enum AttemptResult
    {
        /// <summary>连接或握手本身失败了。</summary>
        Failed,

        /// <summary>车载端读完了握手的答复，但服务端没让新连接可路由（cs#310 之前：旧连接还挂着，被 Attach 拒掉）。</summary>
        AnsweredButNotRoutable,

        /// <summary>新代次可路由。</summary>
        Routable
    }

    private sealed record Attempt(int Number, TimeSpan StartedAfterCut, AttemptResult Result, string Detail);

    /// <summary>一次断电重连的结果，以及判它要用的事件时刻。</summary>
    /// <param name="OldGenerationLeftAtTimestamp">The same moment as <paramref name="OldGenerationLeftAfterCut"/>, as a
    /// <see cref="Stopwatch.GetTimestamp"/>, to compare with what the relay read off the old connection.</param>
    /// <param name="OldConnectionClosedByServer">The relay read the end of the server's side of the cut connection.</param>
    private sealed record Outcome(
        bool OldGenerationRoutableBeforeFirstAttempt,
        TimeSpan? OldGenerationLeftAfterCut,
        long? OldGenerationLeftAtTimestamp,
        IReadOnlyList<Attempt> Attempts,
        TimeSpan? RoutableAfterCut,
        bool StayedUp,
        bool OldConnectionClosedByServer)
    {
        public void AssertRecovered()
        {
            string trace = Describe();
            // Written on success too, so a green run's evidence shows when the old connection was let go.
            TestContext.Current.TestOutputHelper?.WriteLine(trace.TrimStart());
            // Premise: the power cut really left the old connection holding this vehicle.
            Assert.True(
                OldGenerationRoutableBeforeFirstAttempt,
                "第一次重连之前旧代次已经不在路由表里：这一次没造出「旧连接半开占着这辆车」，结论不算数。" + trace);

            // control-server#310: the very first attempt gets in. Before, it read SessionReadiness and was then refused at the
            // attach, and the vehicle kept retrying until the silence window let the old connection go.
            Attempt? first = Attempts.Count > 0 ? Attempts[0] : null;
            Assert.True(
                first?.Result == AttemptResult.Routable,
                "旧连接半开时，断电后的第一次重连没有建起可路由的会话：新连接没有顶替旧连接（control-server#310）。" + trace);
            Assert.True(StayedUp, "新会话可路由之后又被服务端关掉了。" + trace);

            // The table holds one route per vehicle, so the new generation being routable already means the old one left it;
            // the watcher's sighting is what the old-socket checks are timed against. The two moments are taken by two
            // pollers and are not compared with each other: which one sees its change first is the polling, not the product.
            Assert.True(OldGenerationLeftAfterCut is not null, "旧代次一直没离开路由表。" + trace);
        }

        public string Describe() =>
            Environment.NewLine +
            $"旧代次离开路由表：{(OldGenerationLeftAfterCut is { } left ? $"t={left.TotalSeconds:F1}s" : "没有")}；" +
            $"新代次可路由：{(RoutableAfterCut is { } routable ? $"t={routable.TotalSeconds:F1}s" : "没有")}；" +
            $"服务端关掉了旧连接：{(OldConnectionClosedByServer ? "是" : "否")}" +
            Environment.NewLine +
            string.Join(Environment.NewLine, Attempts.Select(attempt =>
                $"第 {attempt.Number} 次，t={attempt.StartedAfterCut.TotalSeconds:F1}s 开始：{attempt.Result} {attempt.Detail}"));
    }

    /// <summary>
    /// 一台真起在回环上的监听器（生产构造函数，即生产的静默窗口），真的消息处理器与内存库，合成车载端，
    /// 以及夹在中间的断电中继。
    /// </summary>
    private sealed class Rig : IAsyncDisposable
    {
        // Read by reflection rather than through a new accessor on OnboardPeer: this ticket changes no product
        // code. A rename fails loudly here instead of making the class pass.
        private static readonly FieldInfo ConnectionsField = typeof(OnboardPeer).GetField(
            "_connections", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("OnboardPeer._connections 改名了，这一类读路由表的办法要跟着改。");

        private static readonly FieldInfo GateField = typeof(OnboardPeer).GetField(
            "_gate", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("OnboardPeer._gate 改名了，这一类读路由表的办法要跟着改。");

        private readonly SqliteConnection _connection;
        private readonly ControlServerDbContext _context;
        private readonly ServiceProvider _provider;
        private readonly OnboardTcpServer _server;
        private long _longestPushTicks;
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _pushFailures = new(StringComparer.Ordinal);

        private Rig(
            SqliteConnection connection,
            ControlServerDbContext context,
            ServiceProvider provider,
            OnboardTcpServer server,
            OnboardPeer peer,
            PowerCutRelay relay,
            CommandEngine<FakeOnboardState> engine,
            OnboardPeerSession onboard)
        {
            _connection = connection;
            _context = context;
            _provider = provider;
            _server = server;
            Peer = peer;
            Relay = relay;
            Engine = engine;
            Onboard = onboard;
        }

        public OnboardPeer Peer { get; }

        public PowerCutRelay Relay { get; }

        public CommandEngine<FakeOnboardState> Engine { get; }

        public OnboardPeerSession Onboard { get; }

        /// <summary>断电之后，最长的一次推送挂了多久（成功、被拒、被取消都算）。</summary>
        public TimeSpan LongestPush => TimeSpan.FromTicks(Interlocked.Read(ref _longestPushTicks));

        /// <summary>What the listener logged, so a test can say how a connection ended (control-server#334).</summary>
        public EventRecordingLogger<OnboardTcpServer> ServerLog { get; private init; } = new();

        public EventRecordingLogger<OnboardTcpServer>.Entry[] ServerLogSnapshot()
        {
            lock (ServerLog.Entries)
            {
                return [.. ServerLog.Entries];
            }
        }

        /// <summary>推送失败过的每一种原因与次数（control-server#334 用它判是谁放掉了连接）。</summary>
        public string PushFailures => string.Join(
            " | ", _pushFailures.OrderBy(item => item.Key, StringComparer.Ordinal).Select(item => $"{item.Value}× {item.Key}"));

        /// <summary>推送失败过的每一种原因，「类型: 消息」。</summary>
        public IReadOnlyList<string> PushFailureReasons => [.. _pushFailures.Keys.Order(StringComparer.Ordinal)];

        /// <summary>Waits until the listener has logged the given event, or <paramref name="within"/> runs out.</summary>
        public async Task WaitServerLogAsync(int eventId, TimeSpan within)
        {
            Stopwatch waited = Stopwatch.StartNew();
            while (waited.Elapsed < within && !ServerLogSnapshot().Any(entry => entry.EventId.Id == eventId))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10), TestContext.Current.CancellationToken);
            }
        }

        public static async Task<Rig> StartAsync(TimeSpan? writeTimeout = null)
        {
            Environment.SetEnvironmentVariable(CredentialVariable, Credential);
            SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            ControlServerDbContext context = new(
                new DbContextOptionsBuilder<ControlServerDbContext>().UseSqlite(connection).Options);
            await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["OnboardTransport:CredentialEnvironmentVariable"] = CredentialVariable
                })
                .Build();
            OnboardPeer peer = new();
            OnboardMessageProcessor processor = TestOnboardProcessorFactory.Create(
                context, new WireToGateStore(context), TimeProvider.System, configuration, peer);
            ServiceCollection services = new();
            services.AddScoped(_ => processor);
            ServiceProvider provider = services.BuildServiceProvider();

            int serverPort = ReserveFreePort();
            EventRecordingLogger<OnboardTcpServer> serverLog = new();
            // The composition root's constructor on purpose: the window under test is the one production
            // runs with, not one a test chose.
            OnboardTcpServer server = new(
                Options.Create(ConfiguredTransport(serverPort, writeTimeout)),
                provider.GetRequiredService<IServiceScopeFactory>(),
                peer,
                serverLog);
            await server.StartAsync(TestContext.Current.CancellationToken);

            PowerCutRelay relay = PowerCutRelay.Start(serverPort);
            CommandEngine<FakeOnboardState> engine = new("power-loss-test", () => new FakeOnboardState());
            OnboardPeerSession onboard = new(
                engine,
                new FakeOnboardOptions
                {
                    Port = relay.Port,
                    AgvId = AgvId,
                    CredentialEnvironmentVariable = CredentialVariable
                },
                SlotStateSeed.Read(new ConfigurationBuilder().Build()));
            return new Rig(connection, context, provider, server, peer, relay, engine, onboard) { ServerLog = serverLog };
        }

        /// <summary>The production defaults, and the write timeout only when a test names one.</summary>
        private static OnboardTransportOptions ConfiguredTransport(int port, TimeSpan? writeTimeout)
        {
            OnboardTransportOptions options = new() { Enabled = true, ListenAddress = "127.0.0.1", Port = port };
            if (writeTimeout is { } configured)
            {
                options.WriteTimeout = configured;
            }
            return options;
        }

        /// <summary>The first session, routable, with its heartbeats flowing as a live session's do.</summary>
        public async Task<long> ConnectFirstSessionAsync()
        {
            await Onboard.StartAsync(TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
            long generation = Engine.Snapshot().State.SessionGeneration;
            Assert.True(
                await WaitRoutableAsync(generation, TimeSpan.FromSeconds(5)),
                "第一代会话一直没可路由，断电之前的前提就没成立。");
            // Past the first heartbeat: the connection has been live, not merely handshaken.
            await Task.Delay(TimeSpan.FromSeconds(2.5), TestContext.Current.CancellationToken);
            return generation;
        }

        /// <summary>The vehicle comes back after a power cut and retries the way the field onboard does.</summary>
        public async Task<Outcome> ReconnectAfterPowerCutAsync(long oldGeneration)
        {
            // The onboard process died with the power: drop its local session. The relay keeps the server's
            // side of that connection open.
            await Onboard.DisconnectAsync();
            Stopwatch sinceCut = Stopwatch.StartNew();

            // The event the verdict hangs on: the moment the old generation stops being routable. Sampled
            // beside the attempts rather than between them, so it is timed to within a poll, not an attempt.
            using CancellationTokenSource watching = new();
            TimeSpan? oldLeftAt = null;
            long? oldLeftAtTimestamp = null;
            Task watcher = Task.Run(async () =>
            {
                while (!watching.IsCancellationRequested)
                {
                    if (RoutableGeneration() != oldGeneration)
                    {
                        oldLeftAtTimestamp = Stopwatch.GetTimestamp();
                        oldLeftAt = sinceCut.Elapsed;
                        return;
                    }
                    try
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(10), watching.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
            }, CancellationToken.None);

            bool oldRoutableBeforeFirstAttempt = RoutableGeneration() == oldGeneration;
            List<Attempt> attempts = [];
            TimeSpan? routableAfter = null;
            bool stayedUp = false;
            try
            {
                while (sinceCut.Elapsed < HangGuard)
                {
                    int number = attempts.Count + 1;
                    TimeSpan startedAt = sinceCut.Elapsed;
                    (AttemptResult result, string detail) = await AttemptAsync(HangGuard - sinceCut.Elapsed);
                    attempts.Add(new Attempt(number, startedAt, result, detail));
                    if (result == AttemptResult.Routable)
                    {
                        routableAfter = sinceCut.Elapsed;
                        stayedUp = await StaysConnectedAsync(StaysUp);
                        break;
                    }
                    await DisconnectQuietlyAsync();
                    await Task.Delay(RetryInterval, TestContext.Current.CancellationToken);
                }
            }
            finally
            {
                await watching.CancelAsync();
                await watcher;
            }
            return new Outcome(
                oldRoutableBeforeFirstAttempt, oldLeftAt, oldLeftAtTimestamp, attempts, routableAfter, stayedUp,
                Relay.CutLinkClosedByServer);
        }

        /// <summary>
        /// Stages an unacknowledged <c>CurrentStopDepartureResult</c> for the given session, which the replay after the next
        /// handshake has to deliver (control-server#713). Only while nothing reaches the server: the processor shares this
        /// context.
        /// </summary>
        public async Task StageDepartureResultAsync(long generation)
        {
            const string requestId = "31000000-0000-4000-8000-0000000000d1";
            Assert.True(await OnboardJourneyPublisher.StageCurrentStopDepartureResultAsync(
                new WireToGateStore(_context), CurrentStopDepartures.ResultMessageId(requestId), AgvId, generation, requestId,
                problem: null, currentWorklistRevision: 3, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken));
            await _context.SaveChangesAsync(TestContext.Current.CancellationToken);
            _context.ChangeTracker.Clear();
        }

        /// <summary>Waits until one push has been in flight for <paramref name="stuckFor"/>.</summary>
        public async Task<bool> WaitPushStuckAsync(TimeSpan stuckFor, TimeSpan within)
        {
            Stopwatch waited = Stopwatch.StartNew();
            while (waited.Elapsed < within)
            {
                long since = Interlocked.Read(ref _pushInFlightSince);
                if (since != 0 && Stopwatch.GetElapsedTime(since) >= stuckFor)
                {
                    return true;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(10), TestContext.Current.CancellationToken);
            }
            return false;
        }

        /// <summary>One push through the peer, as the runtime sends one; returns its messageId.</summary>
        public async Task<string> PushOnceAsync(long generation)
        {
            string messageId = Guid.NewGuid().ToString("D");
            await Peer.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            {
                messageType = "FaultCargoRecoveryCommand",
                messageId,
                agvId = AgvId,
                sessionGeneration = generation
            }) + "\n"), TestContext.Current.CancellationToken);
            return messageId;
        }

        /// <summary>Waits until the synthetic onboard's wire log has the given line (any messageId when none is named).</summary>
        public async Task<bool> WaitWireAsync(string direction, string messageType, TimeSpan within, string? messageId = null)
        {
            Stopwatch waited = Stopwatch.StartNew();
            while (waited.Elapsed < within)
            {
                if (Onboard.Wire().Any(item => item.Direction == direction && item.MessageType == messageType &&
                                               (messageId is null || item.MessageId == messageId)))
                {
                    return true;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(20), TestContext.Current.CancellationToken);
            }
            return false;
        }

        /// <summary>One reconnect: connect, the five-step handshake, then whether the server made it routable.</summary>
        /// <remarks>
        /// Judged by what the server did, not by how fast (control-server#440): the handshake is waited out, then the server's
        /// answer to the attach -- routable, or refused and the connection closed. <paramref name="within"/> is only what is
        /// left of the hang guard; a loaded machine that answers late still answers.
        /// </remarks>
        private async Task<(AttemptResult Result, string Detail)> AttemptAsync(TimeSpan within)
        {
            Stopwatch attempt = Stopwatch.StartNew();
            within = within > TimeSpan.FromSeconds(1) ? within : TimeSpan.FromSeconds(1);
            Task handshake = Onboard.ReconnectAsync();
            try
            {
                await handshake.WaitAsync(within, TestContext.Current.CancellationToken);
            }
            catch (Exception error) when (error is IOException or SocketException or OperationCanceledException
                                              or InvalidOperationException or TimeoutException)
            {
                // WaitAsync only stops waiting; the handshake itself is still running and owns the peer's fields.
                // Let it finish (bounded) before anything tears the session down, or the two race on the same
                // socket and writer and turn a clean verdict into an unreadable exception.
                try
                {
                    await handshake.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
                }
                catch (Exception)
                {
                    // Whatever it ended with, it has ended (or the guard gave up); the attempt already failed.
                }
                return (AttemptResult.Failed, $"{error.GetType().Name}: {error.Message}");
            }

            long generation = Engine.Snapshot().State.SessionGeneration;
            return await WaitRoutableOrClosedAsync(generation, within - attempt.Elapsed)
                ? (AttemptResult.Routable, $"第 {generation} 代")
                : (AttemptResult.AnsweredButNotRoutable, $"第 {generation} 代");
        }

        /// <summary>
        /// After the vehicle has read the handshake's answer, the server either attaches the connection or refuses it -- and a
        /// refused attach ends the connection (<c>OnboardTcpServer.HandleClientAsync</c>). Waits for whichever happens.
        /// </summary>
        /// <remarks>
        /// The attach runs after the answer is on the wire, so the vehicle can be READY before it is routable; a fixed second
        /// after the answer read a merely slow attach as a refusal (control-server#440).
        /// </remarks>
        private async Task<bool> WaitRoutableOrClosedAsync(long generation, TimeSpan within)
        {
            Stopwatch waited = Stopwatch.StartNew();
            while (true)
            {
                if (RoutableGeneration() == generation)
                {
                    return true;
                }
                // Not READY any more is the pump having ended: EOF (DISCONNECTED) or a reset (FAULTED).
                if (!Onboard.IsConnected || Engine.Snapshot().State.Readiness != "READY" || waited.Elapsed >= within)
                {
                    // Read once more: the attach and the close can both have landed since the first read.
                    return RoutableGeneration() == generation;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(20), TestContext.Current.CancellationToken);
            }
        }

        private async Task DisconnectQuietlyAsync()
        {
            try
            {
                await Onboard.DisconnectAsync();
            }
            catch (Exception error) when (error is IOException or SocketException or ObjectDisposedException
                                              or InvalidOperationException)
            {
                // The session is gone either way; the next attempt starts from StartAsync.
            }
        }

        /// <summary>
        /// When the last push that went through began, on the stopwatch handed to <see cref="PushToSessionUntilCancelledAsync"/>;
        /// the moment pushing began until one does.
        /// </summary>
        public TimeSpan LastSuccessfulPushStartedAt => TimeSpan.FromTicks(Interlocked.Read(ref _lastSuccessfulPushStartedAtTicks));

        private long _lastSuccessfulPushStartedAtTicks;

        // The Stopwatch timestamp the push in flight started at; 0 while none is.
        private long _pushInFlightSince;

        /// <summary>Pushes to the given session through the peer until cancelled, as the runtime's rounds do.</summary>
        /// <param name="since">The test's clock, on which <see cref="LastSuccessfulPushStartedAt"/> is read.</param>
        public Task PushToSessionUntilCancelledAsync(
            long generation, int payloadBytes, CancellationToken cancellationToken, Stopwatch? since = null) =>
            Task.Run(async () =>
            {
                since ??= Stopwatch.StartNew();
                Interlocked.Exchange(ref _lastSuccessfulPushStartedAtTicks, since.Elapsed.Ticks);
                string padding = new('x', payloadBytes);
                while (!cancellationToken.IsCancellationRequested)
                {
                    byte[] line = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
                    {
                        messageType = "FaultCargoRecoveryCommand",
                        messageId = Guid.NewGuid().ToString("D"),
                        agvId = AgvId,
                        sessionGeneration = generation,
                        padding
                    }) + "\n");
                    Stopwatch took = Stopwatch.StartNew();
                    TimeSpan startedAt = since.Elapsed;
                    Interlocked.Exchange(ref _pushInFlightSince, Stopwatch.GetTimestamp());
                    try
                    {
                        await Peer.SendAsync(line, cancellationToken);
                        Interlocked.Exchange(ref _lastSuccessfulPushStartedAtTicks, startedAt.Ticks);
                    }
                    catch (Exception error) when (error is IOException or OperationCanceledException
                                                      or ObjectDisposedException or SocketException)
                    {
                        // Refused once the connection is gone or the session moved on, or stuck and cancelled:
                        // the runtime sees the same and leaves the outbox row for a replay.
                        if (error is not OperationCanceledException)
                        {
                            _pushFailures.AddOrUpdate($"{error.GetType().Name}: {error.Message}", 1, (_, count) => count + 1);
                        }
                    }
                    Interlocked.Exchange(ref _pushInFlightSince, 0);
                    RecordPush(took.Elapsed);
                    try
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
            }, CancellationToken.None);

        /// <summary>When the given generation stops being routable, measured on <paramref name="since"/>; null if it never did.</summary>
        public async Task<TimeSpan?> WaitGenerationLeavesAsync(long generation, TimeSpan within, Stopwatch since)
        {
            while (since.Elapsed < within)
            {
                if (RoutableGeneration() != generation)
                {
                    return since.Elapsed;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(10), TestContext.Current.CancellationToken);
            }
            return null;
        }

        public Task DisconnectOnboardAsync() => DisconnectQuietlyAsync();

        /// <summary>One reconnect that has to get in, and stay in: the new generation, routable and not closed after.</summary>
        public async Task<long> ReconnectRoutableAsync()
        {
            (AttemptResult result, string detail) = await AttemptAsync(HangGuard);
            Assert.True(result == AttemptResult.Routable, $"重连没有建起可路由的会话：{result} {detail}");
            Assert.True(await StaysConnectedAsync(StaysUp), "重连之后新会话又被服务端关掉了。");
            return Engine.Snapshot().State.SessionGeneration;
        }

        private void RecordPush(TimeSpan took)
        {
            long seen = Interlocked.Read(ref _longestPushTicks);
            while (took.Ticks > seen)
            {
                long previous = Interlocked.CompareExchange(ref _longestPushTicks, took.Ticks, seen);
                if (previous == seen)
                {
                    return;
                }
                seen = previous;
            }
        }

        private long? RoutableGeneration()
        {
            object connections = ConnectionsField.GetValue(Peer)!;
            lock (GateField.GetValue(Peer)!)
            {
                Dictionary<string, (OnboardPeerConnection Connection, long SessionGeneration)> table =
                    (Dictionary<string, (OnboardPeerConnection Connection, long SessionGeneration)>)connections;
                return table.TryGetValue(AgvId, out (OnboardPeerConnection Connection, long SessionGeneration) entry)
                    ? entry.SessionGeneration
                    : null;
            }
        }

        private async Task<bool> WaitRoutableAsync(long generation, TimeSpan within)
        {
            Stopwatch waited = Stopwatch.StartNew();
            while (waited.Elapsed < within)
            {
                if (RoutableGeneration() == generation)
                {
                    return true;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(20), TestContext.Current.CancellationToken);
            }
            return false;
        }

        private async Task<bool> StaysConnectedAsync(TimeSpan forHowLong)
        {
            Stopwatch waited = Stopwatch.StartNew();
            while (waited.Elapsed < forHowLong)
            {
                if (!Onboard.IsConnected)
                {
                    return false;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);
            }
            return true;
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Onboard.DisposeAsync();
            }
            finally
            {
                try
                {
                    await Relay.DisposeAsync();
                }
                finally
                {
                    try
                    {
                        await _server.StopAsync(CancellationToken.None);
                        _server.Dispose();
                    }
                    finally
                    {
                        await _provider.DisposeAsync();
                        await _context.DisposeAsync();
                        await _connection.DisposeAsync();
                    }
                }
            }
        }

        private static int ReserveFreePort()
        {
            TcpListener probe = new(IPAddress.Loopback, 0);
            probe.Start();
            try
            {
                return ((IPEndPoint)probe.LocalEndpoint).Port;
            }
            finally
            {
                probe.Stop();
            }
        }
    }

    /// <summary>
    /// 按字节转发车载端的连接。<see cref="CutPower"/> 把当前那一条冻住：从此不再往服务端转发任何东西，服务端那一侧
    /// 的 socket 也永远不关——没有 FIN，没有 RST，正是断电留下的样子。
    /// </summary>
    private sealed class PowerCutRelay : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly int _serverPort;
        private readonly CancellationTokenSource _stopping = new();
        private readonly List<IDisposable> _owned = [];
        private readonly List<Task> _links = [];
        private volatile Link? _current;
        private Task _accepting = Task.CompletedTask;

        private PowerCutRelay(TcpListener listener, int serverPort)
        {
            _listener = listener;
            _serverPort = serverPort;
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public static PowerCutRelay Start(int serverPort)
        {
            TcpListener listener = new(IPAddress.Loopback, 0);
            listener.Start();
            PowerCutRelay relay = new(listener, serverPort);
            relay._accepting = relay.AcceptAsync();
            return relay;
        }

        /// <param name="drainServerWrites">
        /// true：服务端写来的字节读走丢掉。false：一概不读，服务端的发送缓冲区会堆满、写操作卡住。
        /// </param>
        public void CutPower(bool drainServerWrites)
        {
            Link link = _current ?? throw new InvalidOperationException("没有可以断电的连接。");
            link.DrainServerWrites = drainServerWrites;
            link.Cut = true;
            _cut = link;
        }

        private volatile Link? _cut;

        /// <summary>断电的那一条上，服务端那一侧已经关了（中继读到了 EOF 或复位）。不读服务端的那种断电看不到，恒为 false。</summary>
        public bool CutLinkClosedByServer => _cut?.ClosedByServer ?? false;

        /// <summary>断电的那一条上，<paramref name="sinceTimestamp"/> 之后中继从服务端读到的每一行。</summary>
        public (string Type, long? Generation, string? MessageId)[] CutLinkLinesFromServer(long sinceTimestamp) =>
            _cut?.LinesFromServer(sinceTimestamp) ?? [];

        /// <summary>最新接进来的那一条上，中继从服务端读到的每一行，按先后。</summary>
        public (string Type, long? Generation, string? MessageId)[] CurrentLinkLinesFromServer() =>
            _current?.LinesFromServer(0) ?? [];

        /// <summary>
        /// 当前那一条从此不读服务端写来的东西，车往服务端的照常转发：车还活着、还在发心跳，只是不读（control-server#334）。
        /// </summary>
        public void StopReadingServerWrites()
        {
            Link link = _current ?? throw new InvalidOperationException("没有可以变聋的连接。");
            _deaf = link;
            link.Deaf = true;
        }

        /// <summary>之后新接的连接照常转发；变聋的那一条保持原样，直到它自己断开。</summary>
        public void ResumeReadingServerWrites() => _current = null;

        /// <summary>变聋之后车往服务端转发了多少字节。</summary>
        public long BytesToServerWhileDeaf => _deaf?.BytesToServerWhileDeaf ?? 0;

        private volatile Link? _deaf;

        public async Task<bool> WaitBytesToServerWhileDeafAsync(TimeSpan within)
        {
            Stopwatch waited = Stopwatch.StartNew();
            while (waited.Elapsed < within)
            {
                if (BytesToServerWhileDeaf > 0)
                {
                    return true;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(20), TestContext.Current.CancellationToken);
            }
            return false;
        }

        private async Task AcceptAsync()
        {
            try
            {
                while (!_stopping.IsCancellationRequested)
                {
                    TcpClient vehicle = await _listener.AcceptTcpClientAsync(_stopping.Token);
                    TcpClient server = new();
                    lock (_owned)
                    {
                        _owned.Add(vehicle);
                        _owned.Add(server);
                    }
                    await server.ConnectAsync(IPAddress.Loopback, _serverPort, _stopping.Token);
                    Link link = new(vehicle, server);
                    _current = link;
                    lock (_links)
                    {
                        _links.Add(link.RunAsync(_stopping.Token));
                    }
                }
            }
            catch (Exception error) when (error is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                // Torn down.
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stopping.CancelAsync();
            _listener.Stop();
            lock (_owned)
            {
                foreach (IDisposable owned in _owned)
                {
                    owned.Dispose();
                }
            }
            await _accepting;
            Task[] links;
            lock (_links)
            {
                links = [.. _links];
            }
            await Task.WhenAll(links);
            _stopping.Dispose();
        }

        private sealed class Link(TcpClient vehicle, TcpClient server)
        {
            public volatile bool Cut;

            public volatile bool DrainServerWrites = true;

            /// <summary>不读服务端写来的东西，但车往服务端的照常转发。</summary>
            public volatile bool Deaf;

            private long _bytesToServerWhileDeaf;

            public long BytesToServerWhileDeaf => Interlocked.Read(ref _bytesToServerWhileDeaf);

            /// <summary>The server's side ended: the relay read its end of stream, or a reset.</summary>
            public volatile bool ClosedByServer;

            private readonly List<(long At, string Line)> _linesFromServer = [];
            private readonly List<byte> _partialLine = [];

            public (string Type, long? Generation, string? MessageId)[] LinesFromServer(long sinceTimestamp)
            {
                lock (_linesFromServer)
                {
                    return [.. _linesFromServer.Where(item => item.At >= sinceTimestamp).Select(item => Parse(item.Line))];
                }
            }

            private static (string Type, long? Generation, string? MessageId) Parse(string line)
            {
                using JsonDocument document = JsonDocument.Parse(line);
                JsonElement root = document.RootElement;
                return (
                    root.GetProperty("messageType").GetString() ?? string.Empty,
                    root.TryGetProperty("sessionGeneration", out JsonElement generation) &&
                    generation.ValueKind == JsonValueKind.Number ? generation.GetInt64() : null,
                    root.TryGetProperty("messageId", out JsonElement messageId) ? messageId.GetString() : null);
            }

            /// <summary>Every byte read from the server, whether forwarded or dropped, split into lines as they complete.</summary>
            private void RecordFromServer(ReadOnlySpan<byte> read)
            {
                long at = Stopwatch.GetTimestamp();
                lock (_linesFromServer)
                {
                    foreach (byte value in read)
                    {
                        if (value != (byte)'\n')
                        {
                            _partialLine.Add(value);
                            continue;
                        }
                        _linesFromServer.Add((at, Encoding.UTF8.GetString([.. _partialLine])));
                        _partialLine.Clear();
                    }
                }
            }

            public Task RunAsync(CancellationToken token) => Task.WhenAll(ToServerAsync(token), ToVehicleAsync(token));

            private async Task ToServerAsync(CancellationToken token)
            {
                byte[] buffer = new byte[65536];
                try
                {
                    NetworkStream from = vehicle.GetStream();
                    NetworkStream to = server.GetStream();
                    while (true)
                    {
                        int read = await from.ReadAsync(buffer, token);
                        if (Cut)
                        {
                            // Power is gone: nothing more reaches the server, and its side is never closed.
                            return;
                        }
                        if (read == 0)
                        {
                            // Client is null once the relay's teardown has disposed this socket.
                            server.Client?.Shutdown(SocketShutdown.Send);
                            return;
                        }
                        await to.WriteAsync(buffer.AsMemory(0, read), token);
                        if (Deaf)
                        {
                            Interlocked.Add(ref _bytesToServerWhileDeaf, read);
                        }
                    }
                }
                catch (Exception error) when (error is IOException or OperationCanceledException
                                                  or ObjectDisposedException or SocketException)
                {
                    if (!Cut)
                    {
                        server.Dispose();
                    }
                }
            }

            private async Task ToVehicleAsync(CancellationToken token)
            {
                byte[] buffer = new byte[65536];
                try
                {
                    NetworkStream from = server.GetStream();
                    while (true)
                    {
                        while ((Cut && !DrainServerWrites) || Deaf)
                        {
                            // Not reading at all: the server's send buffer fills and its writes stall. The
                            // relay's end stays open until the relay itself is disposed.
                            await Task.Delay(TimeSpan.FromMilliseconds(100), token);
                        }
                        int read = await from.ReadAsync(buffer, token);
                        if (read == 0)
                        {
                            ClosedByServer = true;
                            if (!Cut)
                            {
                                vehicle.Dispose();
                            }
                            return;
                        }
                        RecordFromServer(buffer.AsSpan(0, read));
                        if (Cut)
                        {
                            continue;
                        }
                        await vehicle.GetStream().WriteAsync(buffer.AsMemory(0, read), token);
                    }
                }
                catch (Exception error) when (error is IOException or OperationCanceledException
                                                  or ObjectDisposedException or SocketException)
                {
                    // A reset from the server ends its side too; the relay's own teardown is a cancellation.
                    if (error is IOException or SocketException && !token.IsCancellationRequested)
                    {
                        ClosedByServer = true;
                    }
                    if (!Cut)
                    {
                        vehicle.Dispose();
                    }
                }
            }
        }
    }
}
