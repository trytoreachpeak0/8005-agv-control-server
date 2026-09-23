using System.Text;
using System.Text.Json;
using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;

namespace ControlServer.Tests;

/// <summary>
/// 到站那一段发布被断线打断之后，重连到新的一代仍然把这个停靠发完；推进失败的那几轮在看板上说得出自己
/// （control-server#331）。
/// </summary>
/// <remarks>
/// <para>
/// 失败现场是 control-server#323 二分时的 <c>g3-fault-cargo-handoff</c>（<c>C:\w2g\bisect-cs323\hmi-7cf1dcba</c>）。
/// 从它的库与日志读到的顺序：第 1 代里车到取货站，到站发布发出车辆业务状态与清单，车都确认了；到站那一版计划
/// 落了库，发送时 <c>OnboardPeer</c> 报 <c>No recovered Onboard peer is connected</c>；阶段没前移。第 3 代建立后
/// 这一段从头重跑，在第一张已确认的快照上被重放校验拒绝，之后 160 轮都一样，录入请求那一行从来没进过发件箱。
/// </para>
/// <para>
/// <b>窗口是构造出来的。</b>断线用 <see cref="ConnectionCut"/> 在指定的那一条报文发送时抛异常来造，与真实
/// <c>OnboardPeer</c> 的失败点相同：发件箱那一行已经落库，车一个字节都没收到。夹具的时钟不走，除非用例自己拨。
/// </para>
/// </remarks>
public sealed class ArrivalPublishInterruptedThenReconnectedTests
{
    private const string DemandId = "10000000-0000-4000-8000-000000000001";

    /// <summary>
    /// 推进失败的阻断码。写成字面量而不引用 <c>JourneyRuntimeEngine.AdvanceFailedReason</c>：它是看板上现场看得见的东西，
    /// 改名应当让这里红，而不是跟着常量悄悄改。
    /// </summary>
    private const string AdvanceFailedReason = "JOURNEY_ADVANCE_FAILED";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// 失败现场本身：重连到第 2 代之后，到站那一段发完，录入请求以第 2 代送到车上，阶段进入等录入。
    /// 修前这一轮在第一张已确认的快照上抛 <c>ProtocolContentConflictException</c>。
    /// </summary>
    /// <remarks>
    /// 已确认的两张快照不再发第二遍：车已经确认过它们，重发没有用处；而那一条如果真被重发，
    /// 车会以同一个修订号再收一次，这不是本票修正该有的副作用。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-SESSION-RECONNECT-DURING-RECOVERY")]
    [Trait("ProtocolVector", "CV-PICKUP-SUBLOT-LOAD")]
    public async Task TheEntryRequestReachesTheVehicleAfterAReconnectInterruptedTheArrivalPublish()
    {
        (RuntimeFixture fixture, ConnectionCut _) = await ArrivalPublishCutAfterTheWorklistAsync();
        await using RuntimeFixture disposing = fixture;

        await ReconnectAtGenerationAsync(fixture, generation: 2);
        await fixture.Engine.ExecuteOnceAsync(Token);

        // 这一串真的走到了底：录入请求那一行写进了发件箱（现场那一行从来没有过），并以第 2 代发了出去。
        Assert.Contains("SublotEntryRequested", await OutboxTypesAsync(fixture, acknowledged: false));
        JsonElement entry = Assert.Single(SentLines(fixture, "SublotEntryRequested"));
        Assert.Equal(2, entry.GetProperty("sessionGeneration").GetInt64());
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, (await fixture.RuntimeAsync()).Stage);
        Assert.Single(SentLines(fixture, "VehicleBusinessStateSnapshot"));
        Assert.Single(SentLines(fixture, "CurrentStopWorklistSnapshot"));
        Assert.Null((await fixture.RuntimeAsync()).BlockReasonCode);
    }

    /// <summary>
    /// 推进抛异常的那一轮，看板上的阻断原因说的是「这一轮推进失败了」，而不是之前留下的码。
    /// </summary>
    /// <remarks>
    /// 失败现场的库里，<c>BlockReasonCode</c> 从断线那一刻起再没动过：之后每一轮都在发布里抛异常，走不到任何
    /// 写码的地方，于是看板一直说「会话未就绪」，真正的原因只落在日志里（control-server#331 的票评论）。
    /// 这里先让旅程带上那个旧码，再造失败，断言它被换掉。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-SESSION-RECONNECT-DURING-RECOVERY")]
    [Trait("ProtocolVector", "CV-PICKUP-SUBLOT-LOAD")]
    public async Task AFailedAdvanceNamesItselfOnTheBoardInsteadOfTheCodeLeftBeforeIt()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        ConnectionCut cut = ConnectionCut.Attach(fixture);
        fixture.Catalog.Set(fixture.Demand(DemandId, "SUBLOT-001", Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        await fixture.Engine.ExecuteOnceAsync(Token);
        await PutCodeAsync(fixture, "ONBOARD_SESSION_NOT_READY");
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        await fixture.HearFromPeerAsync();
        await ArriveAtPickupAsync(fixture);
        cut.On(IsArrivedPickupPlan);

        await Assert.ThrowsAsync<IOException>(() => fixture.Engine.ExecuteOnceAsync(Token));

        JourneyRuntimeRow runtime = await ReadRuntimeAfterFailedRoundAsync(fixture);
        Assert.Equal(JourneyRuntimeStage.AwaitingPickupArrival, runtime.Stage);
        Assert.Equal(AdvanceFailedReason, runtime.BlockReasonCode);
        Assert.Equal(fixture.Clock.GetUtcNow(), runtime.BlockReasonSince);
    }

    /// <summary>
    /// 每一轮都以同样的方式失败时，开始时间停在第一次失败那一刻：看板上「已挂多久」要从那里算起，
    /// 升级档位才爬得上去。
    /// </summary>
    /// <remarks>
    /// 时钟在两轮之间拨了一分钟，并断言确实拨动了——钟不走时「开始时间保留」恒真，断言不出重置成此刻这种错。
    /// 第二轮失败在重放上（到站那一版计划没确认，每轮开头都会补发它），不在到站发布上；对「推进失败」这个码来说两者一样。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-SESSION-RECONNECT-DURING-RECOVERY")]
    [Trait("ProtocolVector", "CV-PICKUP-SUBLOT-LOAD")]
    public async Task TheFailedAdvanceKeepsTheTimeItStartedWhileEveryRoundFailsTheSameWay()
    {
        (RuntimeFixture fixture, ConnectionCut cut) = await ArrivalPublishCutAfterTheWorklistAsync();
        await using RuntimeFixture disposing = fixture;
        DateTimeOffset firstFailure = (await fixture.RuntimeAsync()).BlockReasonSince!.Value;

        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.NotEqual(firstFailure, fixture.Clock.GetUtcNow());
        await fixture.HearFromPeerAsync();
        cut.On(IsArrivedPickupPlan);
        await Assert.ThrowsAsync<IOException>(() => fixture.Engine.ExecuteOnceAsync(Token));

        JourneyRuntimeRow runtime = await ReadRuntimeAfterFailedRoundAsync(fixture);
        Assert.Equal(AdvanceFailedReason, runtime.BlockReasonCode);
        Assert.Equal(firstFailure, runtime.BlockReasonSince);
    }

    /// <summary>
    /// 推进重新走通、阶段却不前移的那一轮（这里是等录入的一轮）把这个码清掉：它说的是「上一轮失败了」，
    /// 一轮走通就不再成立。
    /// </summary>
    /// <remarks>
    /// 特意选一轮不前移阶段的：阶段前移时 <c>SetStage</c> 本来就把码清掉，用那样的一轮来测，清码的那段代码删掉也照样绿。
    /// 等录入这一轮什么码都不写，所以码变没变只取决于清码那一段。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-SESSION-RECONNECT-DURING-RECOVERY")]
    [Trait("ProtocolVector", "CV-PICKUP-SUBLOT-LOAD")]
    public async Task TheFailedAdvanceCodeIsClearedByAWaitingRoundThatGetsThroughAgain()
    {
        (RuntimeFixture fixture, ConnectionCut cut) = await ArrivalPublishCutAfterTheWorklistAsync();
        await using RuntimeFixture disposing = fixture;
        await ReconnectAtGenerationAsync(fixture, generation: 2);
        await fixture.Engine.ExecuteOnceAsync(Token);
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, (await fixture.RuntimeAsync()).Stage);

        // 录入请求没有确认（它的答复是录入本身），每一轮开头都会补发它；断在这一条上，这一轮就失败在重放里。
        cut.On(line => MessageType(line) == "SublotEntryRequested");
        await Assert.ThrowsAsync<IOException>(() => fixture.Engine.ExecuteOnceAsync(Token));
        Assert.Equal(
            AdvanceFailedReason, (await ReadRuntimeAfterFailedRoundAsync(fixture)).BlockReasonCode);

        cut.Heal();
        await fixture.Engine.ExecuteOnceAsync(Token);

        JourneyRuntimeRow runtime = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, runtime.Stage);
        Assert.Null(runtime.BlockReasonCode);
        Assert.Null(runtime.BlockReasonSince);
    }

    /// <summary>
    /// 已经 <c>Blocked</c> 的旅程推进失败时不改码：它的码指名了在等哪一次人工处置，覆盖掉就没有别的地方记着了。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-SESSION-RECONNECT-DURING-RECOVERY")]
    [Trait("ProtocolVector", "CV-PICKUP-SUBLOT-LOAD")]
    public async Task ABlockedJourneyKeepsTheCodeNamingItsRecoveryWhenItsRoundFails()
    {
        (RuntimeFixture fixture, ConnectionCut cut) = await ArrivalPublishCutAfterTheWorklistAsync();
        await using RuntimeFixture disposing = fixture;
        JourneyRuntimeRow blocked = await fixture.Context.JourneyRuntimes.SingleAsync(Token);
        blocked.Stage = JourneyRuntimeStage.Blocked;
        blocked.SetBlockReason("OPERATION_RECOVERY_REQUIRED", fixture.Clock.GetUtcNow());
        await fixture.Context.SaveChangesAsync(Token);
        DateTimeOffset blockedSince = blocked.BlockReasonSince!.Value;
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        await fixture.HearFromPeerAsync();

        cut.On(IsArrivedPickupPlan);
        await Assert.ThrowsAsync<IOException>(() => fixture.Engine.ExecuteOnceAsync(Token));

        JourneyRuntimeRow runtime = await ReadRuntimeAfterFailedRoundAsync(fixture);
        Assert.Equal(JourneyRuntimeStage.Blocked, runtime.Stage);
        Assert.Equal("OPERATION_RECOVERY_REQUIRED", runtime.BlockReasonCode);
        Assert.Equal(blockedSince, runtime.BlockReasonSince);
    }

    /// <summary>
    /// 真车载端在路上的常态：会话因本服务端自己的在途单而未就绪（control-server#314 的形状）。这时补发失败，码换成
    /// 「推进失败」；下一轮补发走通，码回到会话未就绪——清掉的只有「推进失败」，不是把旅程的码一并清空。
    /// </summary>
    /// <remarks>
    /// 这条走的是就绪闸门关着的那条路（<c>PublishPickupDispatchPlanPastOwnOrderAsync</c>），与上面几条走的
    /// 闸门开着的那条路不是同一段代码。合成台上的车载端不会因为自己的单未就绪，只有这里造得出来。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-01")]
    [Trait("ProtocolVector", "CV-DEMAND-ACCEPT-TO-PICKUP")]
    public async Task OnTheOwnOrderAFailedReplayNamesItselfAndTheNextRoundGoesBackToSessionNotReady()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        ConnectionCut cut = ConnectionCut.Attach(fixture);
        fixture.Catalog.Set(fixture.Demand(DemandId, "SUBLOT-001", Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        await fixture.Engine.ExecuteOnceAsync(Token);
        await PickupDispatchPlanPastOwnOrderTests.DropSessionOnOwnOrderAsync(fixture);
        await fixture.Engine.ExecuteOnceAsync(Token);
        Assert.Equal("ONBOARD_SESSION_NOT_READY", (await fixture.RuntimeAsync()).BlockReasonCode);
        Assert.Single(SentLines(fixture, "UpcomingStopPlanSnapshot"));

        // 车在收到之前断了，重连后仍因自己的单未就绪；补发那一版计划时连接又断了。
        await ReconnectStillOnOwnOrderAsync(fixture, generation: 2);
        cut.On(line => MessageType(line) == "UpcomingStopPlanSnapshot");
        await Assert.ThrowsAsync<IOException>(() => fixture.Engine.ExecuteOnceAsync(Token));
        Assert.Equal(
            AdvanceFailedReason, (await ReadRuntimeAfterFailedRoundAsync(fixture)).BlockReasonCode);

        cut.Heal();
        await fixture.Engine.ExecuteOnceAsync(Token);

        JourneyRuntimeRow runtime = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingPickupArrival, runtime.Stage);
        Assert.Equal("ONBOARD_SESSION_NOT_READY", runtime.BlockReasonCode);
        Assert.Equal(
            [1L, 2L, 2L],
            SentLines(fixture, "UpcomingStopPlanSnapshot").Select(line => line.GetProperty("sessionGeneration").GetInt64()));
    }

    /// <summary>
    /// 现场的前半段：车到取货站，到站发布把车辆业务状态与清单发出去并被确认，到站那一版计划在发送时断线。
    /// 返回的夹具停在「阶段仍是 <c>AwaitingPickupArrival</c>、发件箱里前两张已确认、连接已恢复」的那一刻。
    /// </summary>
    private static async Task<(RuntimeFixture Fixture, ConnectionCut Cut)> ArrivalPublishCutAfterTheWorklistAsync()
    {
        RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        ConnectionCut cut = ConnectionCut.Attach(fixture);
        fixture.Catalog.Set(fixture.Demand(DemandId, "SUBLOT-001", Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        await fixture.Engine.ExecuteOnceAsync(Token);
        await ArriveAtPickupAsync(fixture);
        cut.On(IsArrivedPickupPlan);

        await Assert.ThrowsAsync<IOException>(() => fixture.Engine.ExecuteOnceAsync(Token));

        await fixture.RecreateEngineAsync();
        cut.Heal();
        await cut.Peer.DeliverBufferedAcksAsync();

        Assert.Equal(JourneyRuntimeStage.AwaitingPickupArrival, (await fixture.RuntimeAsync()).Stage);
        // 到站那两张快照已确认，与失败现场的发件箱一样；派车那一版计划在到站之前就发了、也已确认。
        Assert.Equal(
            ["CurrentStopWorklistSnapshot", "UpcomingStopPlanSnapshot", "VehicleBusinessStateSnapshot"],
            await OutboxTypesAsync(fixture, acknowledged: true));
        // 到站那一版计划落了库、没被确认：车没收到它，也没收到后面的录入请求。
        Assert.Equal(["UpcomingStopPlanSnapshot"], await OutboxTypesAsync(fixture, acknowledged: false));
        Assert.Empty(SentLines(fixture, "SublotEntryRequested"));
        return (fixture, cut);
    }

    private static async Task ArriveAtPickupAsync(RuntimeFixture fixture)
    {
        JourneyRuntimeRow runtime = await fixture.RuntimeAsync();
        fixture.Riot.SetSuccessfulArrival("TO_PICKUP", runtime.PickupStationRiotId);
        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { CurrentStationId = runtime.PickupStationRiotId };
    }

    private static async Task PutCodeAsync(RuntimeFixture fixture, string code)
    {
        JourneyRuntimeRow runtime = await fixture.Context.JourneyRuntimes.SingleAsync(Token);
        runtime.SetBlockReason(code, fixture.Clock.GetUtcNow());
        await fixture.Context.SaveChangesAsync(Token);
    }

    /// <summary>
    /// 失败那一轮之后，库里真正提交了的那一行。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 引擎每一轮开一个新的作用域（<c>JourneyRuntimeWorker</c>），失败那一轮没保存的改动不会带到下一轮。夹具共用一个上下文，
    /// 所以先照那样清掉变更跟踪。
    /// </para>
    /// <para>
    /// <b>再确认这个连接上没有还开着的事务。</b>夹具的库是同一个连接上的内存 SQLite，同一个连接看得见自己还没提交的写入：
    /// 要是写码落在一个随后会被回滚的事务里，这里照样读得到它，用例就会在修前修后都绿。<c>BEGIN</c> 在事务里会被 SQLite 拒绝，
    /// 所以它能执行，就说明此刻没有未结束的事务，读到的是已经提交的值。
    /// </para>
    /// </remarks>
    private static async Task<JourneyRuntimeRow> ReadRuntimeAfterFailedRoundAsync(RuntimeFixture fixture)
    {
        await fixture.RecreateEngineAsync();
        Assert.Null(fixture.Context.Database.CurrentTransaction);
        await fixture.Context.Database.ExecuteSqlRawAsync("BEGIN", Token);
        await fixture.Context.Database.ExecuteSqlRawAsync("ROLLBACK", Token);
        return await fixture.RuntimeAsync();
    }

    /// <summary>车断开又连上：新的一代握手完成、会话回到 <c>Ready</c>，车此刻是听得到的。</summary>
    private static async Task ReconnectAtGenerationAsync(RuntimeFixture fixture, long generation)
    {
        await fixture.ReconnectAsync(generation);
        await fixture.AdvanceSessionAsync(generation);
        await fixture.HearFromPeerAsync();
    }

    /// <summary>新的一代握手完成，而车仍挂着本服务端的在途单，会话照旧因它未就绪。</summary>
    private static async Task ReconnectStillOnOwnOrderAsync(RuntimeFixture fixture, long generation)
    {
        SessionRecoveryRow session = await fixture.Context.SessionRecoveries.SingleAsync(Token);
        session.SessionGeneration = generation;
        session.RecoveryReportId = Guid.NewGuid().ToString("D");
        await fixture.Context.SaveChangesAsync(Token);
        await PickupDispatchPlanPastOwnOrderTests.DropSessionOnOwnOrderAsync(fixture);
        await fixture.HearFromPeerAsync();
    }

    /// <summary>到站那一版取货计划：取货那一段已经是 <c>ARRIVED</c>，派车那一版还是 <c>ACTIVE</c>。</summary>
    private static bool IsArrivedPickupPlan(string ndjsonLine)
    {
        using JsonDocument document = JsonDocument.Parse(ndjsonLine.TrimEnd('\n'));
        JsonElement root = document.RootElement;
        return root.GetProperty("messageType").GetString() == "UpcomingStopPlanSnapshot" &&
               root.GetProperty("payload").GetProperty("legs").EnumerateArray()
                   .Any(leg => leg.GetProperty("legType").GetString() == "TO_PICKUP" &&
                               leg.GetProperty("state").GetString() == "ARRIVED");
    }

    private static string MessageType(string ndjsonLine)
    {
        using JsonDocument document = JsonDocument.Parse(ndjsonLine.TrimEnd('\n'));
        return document.RootElement.GetProperty("messageType").GetString()!;
    }

    /// <summary>车收到的这一类报文，按发出的顺序；断线那一条也在内（它落了库、进了发送，只是没到车上）。</summary>
    private static JsonElement[] SentLines(RuntimeFixture fixture, string messageType) =>
        [.. fixture.Peer.Lines
            .Select(line => JsonDocument.Parse(Encoding.UTF8.GetString(line)).RootElement.Clone())
            .Where(root => root.GetProperty("messageType").GetString() == messageType)];

    /// <summary>
    /// 已确认的行按类型列一次；未确认的只列还会被补发的（退役的不算）。派车那一版计划在到站时退役，
    /// 车的确认随后才送回，所以它两样都带着，归在已确认里。
    /// </summary>
    private static async Task<string[]> OutboxTypesAsync(RuntimeFixture fixture, bool acknowledged) =>
        [.. (await fixture.Context.ProtocolOutbox.AsNoTracking().ToArrayAsync(Token))
            .Where(row => acknowledged
                ? row.AcknowledgedAt is not null
                : row.AcknowledgedAt is null && row.FencedAt is null)
            .Select(row => row.MessageType)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];

    /// <summary>
    /// 在指定的那一条报文上断线：那一条照旧进发件箱、进发送，然后抛出 <c>OnboardPeer</c> 在连接不在时抛的那种异常。
    /// 其余报文交给 <see cref="AdoptingPeer"/>，确认由用例决定何时送回。
    /// </summary>
    private sealed class ConnectionCut
    {
        private Func<string, bool>? _cutOn;

        private ConnectionCut(AdoptingPeer peer) => Peer = peer;

        public AdoptingPeer Peer { get; }

        public static ConnectionCut Attach(RuntimeFixture fixture)
        {
            ConnectionCut cut = new(new AdoptingPeer(fixture.Context, fixture.Clock));
            fixture.Peer.OnMessageSent = line =>
            {
                if (cut._cutOn?.Invoke(line) == true)
                {
                    throw new IOException("No recovered Onboard peer is connected for the test vehicle.");
                }

                cut.Peer.Receive(line);
                return Task.CompletedTask;
            };
            return cut;
        }

        public void On(Func<string, bool> predicate) => _cutOn = predicate;

        public void Heal() => _cutOn = null;
    }
}
