using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    /// 断线之后立刻重连（中间没有一轮看到会话未就绪）：到站那一段发完，录入请求以第 2 代送到车上，阶段进入等录入。
    /// 修前这一轮在第一张已确认的快照上抛 <c>ProtocolContentConflictException</c>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 这不是失败现场的完整时序——现场在重连之前还跑过一轮闸门关着的推进，那一条见
    /// <see cref="TheEntryRequestReachesTheVehicleWhenARoundBehindTheClosedGateVoidedTheWaitBeforeTheReconnect"/>。
    /// 这一种也会发生：引擎一秒一轮，断线与重连落在同一个间隔里时，没有哪一轮看见会话未就绪。
    /// </para>
    /// <para>
    /// 已确认的两张快照不再发第二遍：车已经确认过它们，重发没有用处；而那一条如果真被重发，
    /// 车会以同一个修订号再收一次，这不是本票修正该有的副作用。
    /// </para>
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
    /// 现场的完整时序：断线之后、重连之前，引擎先在就绪闸门关着的状态下跑了一轮。录入请求仍然要以新的一代写出并发到车上。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 这一轮是审查指出、调度按行号核实的（control-server#331 审查必修 1），上面那条用例没有它。闸门关着的那一轮把站点离站等待清空
    /// （ADR-cross-0055「断联使本轮截止失效」，<c>AdvanceAsync</c> 会话为空那一段），重连后到站那一段重跑时从此刻重填，
    /// 清单于是带着一个新的期限——与车已确认的那一版内容不同。失败现场的库里正是这样：<c>StationDepartureWaitStartedAt</c> 为空，
    /// 已确认的清单行期限是到站时刻加 30 秒。
    /// </para>
    /// <para>
    /// 两个条件缺一个这条就测不出来，所以都写在这里：期限要开着（夹具默认关，关着时清单的期限永远是 null），两轮之间钟要走
    /// （钟不走时重填出来的还是原来那个时刻）。
    /// </para>
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-SESSION-RECONNECT-DURING-RECOVERY")]
    [Trait("ProtocolVector", "CV-PICKUP-SUBLOT-LOAD")]
    public async Task TheEntryRequestReachesTheVehicleWhenARoundBehindTheClosedGateVoidedTheWaitBeforeTheReconnect()
    {
        (RuntimeFixture fixture, ConnectionCut _) =
            await ArrivalPublishCutAfterTheWorklistAsync(stationDepartureWait: TimeSpan.FromSeconds(30));
        await using RuntimeFixture disposing = fixture;
        Assert.NotNull((await fixture.RuntimeAsync()).StationDepartureWaitStartedAt);

        await fixture.DropOnboardSessionAsync();
        fixture.Clock.Advance(TimeSpan.FromSeconds(7));
        await fixture.Engine.ExecuteOnceAsync(Token);
        JourneyRuntimeRow voided = await fixture.RuntimeAsync();
        Assert.Null(voided.StationDepartureWaitStartedAt);
        Assert.Equal("ONBOARD_SESSION_NOT_READY", voided.BlockReasonCode);

        await ReconnectAtGenerationAsync(fixture, generation: 2);
        await fixture.Engine.ExecuteOnceAsync(Token);

        Assert.Contains("SublotEntryRequested", await OutboxTypesAsync(fixture, acknowledged: false));
        JsonElement entry = Assert.Single(SentLines(fixture, "SublotEntryRequested"));
        Assert.Equal(2, entry.GetProperty("sessionGeneration").GetInt64());
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, (await fixture.RuntimeAsync()).Stage);
    }

    /// <summary>
    /// 断两次：第一次断在到站计划上；重连到第 2 代，那一轮开头补发的计划被车确认；第二次断在录入请求上；
    /// 重连到第 3 代之后，录入请求仍要写出并以第 3 代发出（control-server#331 第三轮审查必修 1）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 只断一次时，到站计划是没确认的那一行，由每轮开头的补发按新的一代改写，不需要沿用。断第二次时它已经在第 2 代被确认，
    /// 第 3 代重跑时代次不同，若不带 <c>keepAcknowledgedIgnoring</c>，就在「已确认即拒」上每轮失败——和第一次断线时车辆业务状态那一张
    /// 一模一样的形状，只是换了一张。
    /// </para>
    /// <para>
    /// 确认是在第二次断线之后才送回的：审查推的时序是计划的确认在同一轮的两次 RIoT 请求之间回来，这里把它放在那一轮结束之后，
    /// 对第 3 代那一轮看到的库是一样的——计划已确认、录入请求没确认。
    /// </para>
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-SESSION-RECONNECT-DURING-RECOVERY")]
    [Trait("ProtocolVector", "CV-PICKUP-SUBLOT-LOAD")]
    public async Task TheEntryRequestReachesTheVehicleAfterASecondDisconnectLandsOnItOnceThePlanWasAcknowledged()
    {
        (RuntimeFixture fixture, ConnectionCut cut) = await ArrivalPublishCutAfterTheWorklistAsync();
        await using RuntimeFixture disposing = fixture;

        await ReconnectAtGenerationAsync(fixture, generation: 2);
        cut.On(line => MessageType(line) == "SublotEntryRequested");
        await Assert.ThrowsAsync<IOException>(() => fixture.Engine.ExecuteOnceAsync(Token));
        await fixture.RecreateEngineAsync();
        cut.Heal();
        await cut.Peer.DeliverBufferedAcksAsync();
        ProtocolOutboxRow plan = await fixture.Context.ProtocolOutbox.AsNoTracking().SingleAsync(
            row => row.MessageType == "UpcomingStopPlanSnapshot" && row.FencedAt == null, Token);
        Assert.NotNull(plan.AcknowledgedAt);
        Assert.Equal(["SublotEntryRequested"], await OutboxTypesAsync(fixture, acknowledged: false));

        await ReconnectAtGenerationAsync(fixture, generation: 3);
        await fixture.Engine.ExecuteOnceAsync(Token);

        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, (await fixture.RuntimeAsync()).Stage);
        Assert.Equal(3, SentLines(fixture, "SublotEntryRequested").Last().GetProperty("sessionGeneration").GetInt64());
        // 车已确认的计划不再以第 3 代发出。只断这一条：第 2 代里它发过几次是现有行为，不是本票要的性质，
        // 钉成期望会让以后去掉那次多余的发送的人撞红（第四轮审查低 3）。
        Assert.DoesNotContain(
            3L,
            SentLines(fixture, "UpcomingStopPlanSnapshot")
                .Where(line => IsArrivedPickupPlan(line.GetRawText()))
                .Select(line => line.GetProperty("sessionGeneration").GetInt64()));
    }

    /// <summary>
    /// 断在清单那一张、随后闸门关着跑一轮（期限作废）、再重连：重跑不再失败，重填的期限作为新的一版清单发出，录入请求跟着那一版发出
    /// （control-server#339；cs#331 时这里是「第一轮失败一次、车确认补发的旧清单之后下一轮走通」）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 这一回车没确认过任何一版清单。第 2 代那一轮开头补发旧清单（旧期限），随后到站那一段重跑算出新期限。cs#331 时那一行没确认、不在沿用
    /// 范围内、内容又不同，由重放校验拒绝，这一轮失败。现在排给车的那一版期限与服务端此刻不一致，清单升一版：新号、新 id，旧的那一行
    /// 退役，重放校验不会被问到——它没有被放宽，只是这里不再拿同一个 id 去发不同的内容。
    /// </para>
    /// <para>
    /// cs#331 时这条还钉着「只沿用已确认的行」：那时清单沿用会忽略期限，没确认的旧清单若被沿用，车上就留着一个车还没说收到的旧期限。
    /// 现在沿用不再忽略任何 payload 字段，期限变了就一定是新的一版，那个风险在构造上不存在了。车先收到补发的旧版、再收到新的一版，
    /// 号严格前进，车没有拒收任何一张，这里一并断言。
    /// </para>
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-SESSION-RECONNECT-DURING-RECOVERY")]
    [Trait("ProtocolVector", "CV-PICKUP-SUBLOT-LOAD")]
    public async Task ACutOnTheWorklistThenARefillSendsANewerWorklistInsteadOfFailingARound()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Options.StationDepartureWaitTimeout = TimeSpan.FromSeconds(30);
        ConnectionCut cut = ConnectionCut.Attach(fixture);
        fixture.Catalog.Set(fixture.Demand(DemandId, "SUBLOT-001", Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        await fixture.Engine.ExecuteOnceAsync(Token);
        await ArriveAtPickupAsync(fixture);
        cut.On(line => MessageType(line) == "CurrentStopWorklistSnapshot");
        await Assert.ThrowsAsync<IOException>(() => fixture.Engine.ExecuteOnceAsync(Token));
        await fixture.RecreateEngineAsync();
        cut.Heal();
        await cut.Peer.DeliverBufferedAcksAsync();
        Assert.Equal(["CurrentStopWorklistSnapshot"], await OutboxTypesAsync(fixture, acknowledged: false));

        await fixture.DropOnboardSessionAsync();
        fixture.Clock.Advance(TimeSpan.FromSeconds(7));
        await fixture.Engine.ExecuteOnceAsync(Token);
        Assert.Null((await fixture.RuntimeAsync()).StationDepartureWaitStartedAt);
        await ReconnectAtGenerationAsync(fixture, generation: 2);

        await fixture.Engine.ExecuteOnceAsync(Token);
        await cut.Peer.DeliverBufferedAcksAsync();

        JourneyRuntimeRow runtime = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, runtime.Stage);
        Assert.Null(runtime.BlockReasonCode);
        long[] worklists = [.. cut.Peer.Adopted
            .Where(item => item.MessageType == "CurrentStopWorklistSnapshot")
            .Select(item => item.Revision)];
        Assert.Equal([worklists[0], worklists[0] + 1], worklists);
        Assert.Empty(cut.Peer.Conflicts);
        Assert.Empty(cut.Peer.Regressions);
        JsonElement entry = Assert.Single(SentLines(fixture, "SublotEntryRequested"));
        Assert.Equal(2, entry.GetProperty("sessionGeneration").GetInt64());
        Assert.Equal(worklists[^1], entry.GetProperty("payload").GetProperty("worklistRevision").GetInt64());
    }

    /// <summary>
    /// 车已确认的那一版清单与重跑算出的这一版，期限相同而别的字段不同：不沿用、照旧被重放校验拒绝，看板显示推进失败。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 到站那一段重跑时，清单只在除信封外一字不差时沿用车已确认的那一版（control-server#331；control-server#339 起期限也不再忽略）。
    /// 别的字段不同，说明同一 messageId 下内容真的变了——车没收到的东西不能被当成已经收到。这条钉住「按内容比、不按确认过」：
    /// 把判据换成「只要确认过就沿用」，这里会变成录入请求照发、测试变红。
    /// </para>
    /// <para>
    /// <b>期限要相同</b>，所以这里不跑闸门关着的那一轮、钟也不走：重连照样作废离站等待，但重填落在同一个时刻，期限与车手上那一版一样。
    /// 期限一变，清单就作为新的一版发出（control-server#339），不会走到这个比较——cs#331 时这条用的是「断线、未就绪一轮、钟走 7 秒」，
    /// 那样造出来的现在测的是升版，不是比较。
    /// </para>
    /// <para>
    /// <b>「内容真的变了」是改写发件箱那一行造出来的</b>，不是走一条真实路径：读代码时，当前停靠的清单项在这个窗口里不会变
    /// （取消会升修订号、途中追加不并进当前下一站、释放在取货单已成功时被拒），会变的真实例子在车辆业务状态上（持货等单的旅程
    /// 进出装货阶段），造起来要整套持货参数，那一格记在 PR 的剩余风险与 control-server#339 里。这里要钉的是比较本身。
    /// </para>
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-SESSION-RECONNECT-DURING-RECOVERY")]
    [Trait("ProtocolVector", "CV-PICKUP-SUBLOT-LOAD")]
    public async Task AnAcknowledgedWorklistThatDiffersBeyondItsDeadlineIsStillRefusedAndTheBoardSaysSo()
    {
        (RuntimeFixture fixture, ConnectionCut _) =
            await ArrivalPublishCutAfterTheWorklistAsync(stationDepartureWait: TimeSpan.FromSeconds(30));
        await using RuntimeFixture disposing = fixture;
        ProtocolOutboxRow worklist = await fixture.Context.ProtocolOutbox.SingleAsync(
            row => row.MessageType == "CurrentStopWorklistSnapshot", Token);
        Assert.NotNull(worklist.AcknowledgedAt);
        JsonObject acknowledged = JsonNode.Parse(worklist.PayloadJson)!.AsObject();
        JsonObject item = acknowledged["payload"]!["items"]![0]!.AsObject();
        item["expectedBasketCount"] = item["expectedBasketCount"]!.GetValue<int>() + 1;
        worklist.PayloadJson = acknowledged.ToJsonString();
        await fixture.Context.SaveChangesAsync(Token);

        await ReconnectAtGenerationAsync(fixture, generation: 2);
        // 重连确实作废了等待（重填是真的发生了），只是落在同一个时刻。
        Assert.Null((await fixture.RuntimeAsync()).StationDepartureWaitStartedAt);
        await Assert.ThrowsAsync<ProtocolContentConflictException>(() => fixture.Engine.ExecuteOnceAsync(Token));

        JourneyRuntimeRow runtime = await ReadRuntimeAfterFailedRoundAsync(fixture);
        Assert.Equal(JourneyRuntimeStage.AwaitingPickupArrival, runtime.Stage);
        Assert.Equal(AdvanceFailedReason, runtime.BlockReasonCode);
        Assert.Empty(SentLines(fixture, "SublotEntryRequested"));
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
    /// 旅程的码指名了它在等谁时，推进失败不改码、不动开始时间（control-server#331 审查必修 2）。每类一行。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 这几个码与失联写码不许覆盖的是同一组（<c>JourneyRuntimeEngine.CarriesACodeThatNamesAWaitOnAPerson</c>）。覆盖掉的代价：
    /// 在途单停住的码一换，派单轮次挡途中追加的闸就开了；下一轮写回原码时开始时间归零、升级档位清零、告警重发。
    /// </para>
    /// <para>
    /// 码写成字面量，理由同 <see cref="AdvanceFailedReason"/>。AREA 站等准入的码只在去关卡那一段成立，所以那一行连阶段一起设。
    /// 两轮之间钟走了一分钟：钟不走时「开始时间没变」恒真，看不出被重写成此刻。
    /// </para>
    /// </remarks>
    [Theory]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-SESSION-RECONNECT-DURING-RECOVERY")]
    [Trait("ProtocolVector", "CV-PICKUP-SUBLOT-LOAD")]
    [InlineData("VEHICLE_ORDER_FAILED", JourneyRuntimeStage.AwaitingPickupArrival)]
    [InlineData("ORDER_HANG", JourneyRuntimeStage.AwaitingPickupArrival)]
    [InlineData("ORDER_STATE_UNRECOGNIZED", JourneyRuntimeStage.AwaitingPickupArrival)]
    [InlineData("ORDER_ENDED_WITHOUT_ARRIVAL", JourneyRuntimeStage.AwaitingPickupArrival)]
    [InlineData("TASK_TYPE_NOT_ALLOWED_AT_STATION", JourneyRuntimeStage.AwaitingGateArrival)]
    // 第三轮审查建议 2：门没关的站点超时告警与纠错进行中，各在它们自己的停站阶段。
    [InlineData("STATION_TIMEOUT_DOOR_NOT_CLOSED", JourneyRuntimeStage.AwaitingLoadResult)]
    [InlineData("LOAD_CORRECTION_IN_PROGRESS", JourneyRuntimeStage.AwaitingStationDeparture)]
    // 第四轮审查建议 2：车载端答了「不安全」，等人去车前处理；写入处按 control-server#80 不许开始时间重启。
    [InlineData("PRE_DEPARTURE_SAFETY_NOT_VALID", JourneyRuntimeStage.AwaitingDepartureSafety)]
    public async Task AFailedAdvanceLeavesACodeThatNamesAWaitOnAPersonAsItIs(string code, JourneyRuntimeStage stage)
    {
        (RuntimeFixture fixture, ConnectionCut cut) = await ArrivalPublishCutAfterTheWorklistAsync();
        await using RuntimeFixture disposing = fixture;
        JourneyRuntimeRow waiting = await fixture.Context.JourneyRuntimes.SingleAsync(Token);
        waiting.Stage = stage;
        waiting.SetBlockReason(null, fixture.Clock.GetUtcNow());
        waiting.SetBlockReason(code, fixture.Clock.GetUtcNow());
        await fixture.Context.SaveChangesAsync(Token);
        DateTimeOffset since = waiting.BlockReasonSince!.Value;
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        await fixture.HearFromPeerAsync();

        cut.On(IsArrivedPickupPlan);
        await Assert.ThrowsAsync<IOException>(() => fixture.Engine.ExecuteOnceAsync(Token));

        JourneyRuntimeRow runtime = await ReadRuntimeAfterFailedRoundAsync(fixture);
        Assert.Equal(stage, runtime.Stage);
        Assert.Equal(code, runtime.BlockReasonCode);
        Assert.Equal(since, runtime.BlockReasonSince);
    }

    /// <summary>
    /// 真车载端在路上的常态：会话因本服务端自己的在途单而未就绪（control-server#314 的形状）。这时补发连续失败三轮，
    /// 码一直是 <c>ONBOARD_SESSION_NOT_READY</c>、开始时间停在第一次；走通之后仍是它（审查建议 3）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 会话此刻确实没就绪，这个码就是真的，而补发失败多半就是它造成的（连接不在）。修前这里每轮先写会话未就绪、随后抛异常
    /// 改写成推进失败，两个码来回切：开始时间每轮归零，在路上的阶段会话未就绪又不算「在等」，等待起点与告警也每轮清零重来。
    /// </para>
    /// <para>
    /// 这条走的是就绪闸门关着的那条路（<c>PublishPickupDispatchPlanPastOwnOrderAsync</c>），与上面几条走的闸门开着的那条路
    /// 不是同一段代码。合成台上的车载端不会因为自己的单未就绪，只有这里造得出来。
    /// </para>
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-01")]
    [Trait("ProtocolVector", "CV-DEMAND-ACCEPT-TO-PICKUP")]
    public async Task OnTheOwnOrderThreeFailedReplaysKeepTheSessionNotReadyCodeAndItsStart()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        ConnectionCut cut = ConnectionCut.Attach(fixture);
        fixture.Catalog.Set(fixture.Demand(DemandId, "SUBLOT-001", Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        await fixture.Engine.ExecuteOnceAsync(Token);
        await PickupDispatchPlanPastOwnOrderTests.DropSessionOnOwnOrderAsync(fixture);
        await fixture.Engine.ExecuteOnceAsync(Token);
        JourneyRuntimeRow first = await fixture.RuntimeAsync();
        Assert.Equal("ONBOARD_SESSION_NOT_READY", first.BlockReasonCode);
        DateTimeOffset since = first.BlockReasonSince!.Value;
        Assert.Single(SentLines(fixture, "UpcomingStopPlanSnapshot"));

        // 车在收到之前断了，重连后仍因自己的单未就绪；此后每一轮补发那一版计划时连接都又断了。
        await ReconnectStillOnOwnOrderAsync(fixture, generation: 2);
        cut.On(line => MessageType(line) == "UpcomingStopPlanSnapshot");
        for (int round = 1; round <= 3; round++)
        {
            fixture.Clock.Advance(TimeSpan.FromSeconds(10));
            await fixture.HearFromPeerAsync();
            await Assert.ThrowsAsync<IOException>(() => fixture.Engine.ExecuteOnceAsync(Token));
            JourneyRuntimeRow failed = await ReadRuntimeAfterFailedRoundAsync(fixture);
            Assert.Equal("ONBOARD_SESSION_NOT_READY", failed.BlockReasonCode);
            Assert.Equal(since, failed.BlockReasonSince);
        }
        Assert.NotEqual(since, fixture.Clock.GetUtcNow());

        cut.Heal();
        await fixture.Engine.ExecuteOnceAsync(Token);

        JourneyRuntimeRow runtime = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingPickupArrival, runtime.Stage);
        Assert.Equal("ONBOARD_SESSION_NOT_READY", runtime.BlockReasonCode);
        Assert.Equal(since, runtime.BlockReasonSince);
        Assert.Equal(2, SentLines(fixture, "UpcomingStopPlanSnapshot").Last().GetProperty("sessionGeneration").GetInt64());
    }

    /// <summary>
    /// 会话因本服务端自己的在途单而未就绪，但这一轮失败与连接无关：看板显示推进失败，不被「会话未就绪」遮住
    /// （control-server#331 第三轮审查建议 3）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 保留 <c>ONBOARD_SESSION_NOT_READY</c> 的理由是「这一轮的失败多半就是它造成的」，而那只对传输类失败成立（<c>OnboardPeer</c>
    /// 在连接不在时抛 <see cref="IOException"/>）。真车一路都挂着本服务端在途单、整段未就绪，一个与会话无关、每轮都抛的异常若也被
    /// 当成未就绪，整段路上看板说的都是一件不相干的事。所以只有传输类失败且会话确实未就绪时才保留，其余照样写推进失败。
    /// </para>
    /// <para>
    /// 用 <see cref="InvalidOperationException"/> 代表「与连接无关的失败」：它从发送这一步抛出，只是为了让失败落在同一个位置上，
    /// 断言关心的是异常的类型，不是它从哪里来。
    /// </para>
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-01")]
    [Trait("ProtocolVector", "CV-DEMAND-ACCEPT-TO-PICKUP")]
    public async Task OnTheOwnOrderAFailureUnrelatedToTheConnectionStillNamesItself()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        ConnectionCut cut = ConnectionCut.Attach(fixture);
        fixture.Catalog.Set(fixture.Demand(DemandId, "SUBLOT-001", Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        await fixture.Engine.ExecuteOnceAsync(Token);
        await PickupDispatchPlanPastOwnOrderTests.DropSessionOnOwnOrderAsync(fixture);
        await fixture.Engine.ExecuteOnceAsync(Token);
        Assert.Equal("ONBOARD_SESSION_NOT_READY", (await fixture.RuntimeAsync()).BlockReasonCode);

        await ReconnectStillOnOwnOrderAsync(fixture, generation: 2);
        cut.On(
            line => MessageType(line) == "UpcomingStopPlanSnapshot",
            () => new InvalidOperationException("A failure that has nothing to do with the connection."));
        fixture.Clock.Advance(TimeSpan.FromSeconds(10));
        await fixture.HearFromPeerAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Engine.ExecuteOnceAsync(Token));

        JourneyRuntimeRow runtime = await ReadRuntimeAfterFailedRoundAsync(fixture);
        Assert.Equal(AdvanceFailedReason, runtime.BlockReasonCode);
        Assert.Equal(fixture.Clock.GetUtcNow(), runtime.BlockReasonSince);
    }

    /// <summary>
    /// 传输类失败不只长成顶层的 <see cref="IOException"/>：被包了一层，或者连接已经释放，会话确实未就绪时同样保留
    /// <c>ONBOARD_SESSION_NOT_READY</c> 与它的开始时间（control-server#331 第四轮审查建议 1）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 两行各守一处：<c>wrapped-io</c> 守「沿内层异常找」——把查找改成只看顶层，只有这一行会红，上面几条抛的都是顶层
    /// <see cref="IOException"/>；<c>disposed-connection</c> 守 <see cref="ObjectDisposedException"/>，那是连接释放时
    /// <c>OnboardPeer.DisposeAsync</c> 释放发送闸门、与之赛跑的发送在等闸门时抛的。
    /// </para>
    /// <para>
    /// 注意 <see cref="ObjectDisposedException"/> 派生自 <see cref="InvalidOperationException"/>，而上一条用后者代表「与连接无关」。
    /// 两条的判据相反，靠的是判定按具体类型认它，所以这里断言抛出的正是造出来的那个类型，不是它的基类。
    /// </para>
    /// </remarks>
    [Theory]
    [Trait("IntegrationSlice", "FP-IS-01")]
    [Trait("ProtocolVector", "CV-DEMAND-ACCEPT-TO-PICKUP")]
    [InlineData("wrapped-io")]
    [InlineData("disposed-connection")]
    public async Task OnTheOwnOrderATransportFailureInAnyShapeKeepsTheSessionNotReadyCode(string shape)
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        ConnectionCut cut = ConnectionCut.Attach(fixture);
        fixture.Catalog.Set(fixture.Demand(DemandId, "SUBLOT-001", Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        await fixture.Engine.ExecuteOnceAsync(Token);
        await PickupDispatchPlanPastOwnOrderTests.DropSessionOnOwnOrderAsync(fixture);
        await fixture.Engine.ExecuteOnceAsync(Token);
        JourneyRuntimeRow first = await fixture.RuntimeAsync();
        Assert.Equal("ONBOARD_SESSION_NOT_READY", first.BlockReasonCode);
        DateTimeOffset since = first.BlockReasonSince!.Value;

        Exception Fault() => shape switch
        {
            "wrapped-io" => new InvalidOperationException(
                "The send failed.", new IOException("No recovered Onboard peer is connected for the test vehicle.")),
            "disposed-connection" => new ObjectDisposedException("SemaphoreSlim"),
            _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, null),
        };
        await ReconnectStillOnOwnOrderAsync(fixture, generation: 2);
        cut.On(line => MessageType(line) == "UpcomingStopPlanSnapshot", Fault);
        fixture.Clock.Advance(TimeSpan.FromSeconds(10));
        await fixture.HearFromPeerAsync();
        Exception thrown = await Assert.ThrowsAnyAsync<Exception>(() => fixture.Engine.ExecuteOnceAsync(Token));
        Assert.IsType(Fault().GetType(), thrown, exactMatch: true);

        JourneyRuntimeRow runtime = await ReadRuntimeAfterFailedRoundAsync(fixture);
        Assert.NotEqual(since, fixture.Clock.GetUtcNow());
        Assert.Equal("ONBOARD_SESSION_NOT_READY", runtime.BlockReasonCode);
        Assert.Equal(since, runtime.BlockReasonSince);
    }

    /// <summary>
    /// 现场的前半段：车到取货站，到站发布把车辆业务状态与清单发出去并被确认，到站那一版计划在发送时断线。
    /// 返回的夹具停在「阶段仍是 <c>AwaitingPickupArrival</c>、发件箱里前两张已确认、连接已恢复」的那一刻。
    /// </summary>
    internal static async Task<(RuntimeFixture Fixture, ConnectionCut Cut)> ArrivalPublishCutAfterTheWorklistAsync(
        TimeSpan? stationDepartureWait = null,
        Func<Task<RuntimeFixture>>? create = null)
    {
        RuntimeFixture fixture = await (create ?? (() => RuntimeFixture.CreateAsync()))();
        if (stationDepartureWait is { } wait)
        {
            fixture.Options.StationDepartureWaitTimeout = wait;
        }
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
    internal static async Task<JourneyRuntimeRow> ReadRuntimeAfterFailedRoundAsync(RuntimeFixture fixture)
    {
        await fixture.RecreateEngineAsync();
        Assert.Null(fixture.Context.Database.CurrentTransaction);
        await fixture.Context.Database.ExecuteSqlRawAsync("BEGIN", Token);
        await fixture.Context.Database.ExecuteSqlRawAsync("ROLLBACK", Token);
        return await fixture.RuntimeAsync();
    }

    /// <summary>车断开又连上：新的一代握手完成、会话回到 <c>Ready</c>，车此刻是听得到的。</summary>
    internal static async Task ReconnectAtGenerationAsync(RuntimeFixture fixture, long generation)
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
    /// <remarks>
    /// 默认抛 <see cref="IOException"/>——真实 <c>OnboardPeer</c> 在连接不在时抛的就是它。要造「与连接无关的失败」时，
    /// 给 <see cref="On"/> 传别的异常（审查建议 3 那一条）。
    /// </remarks>
    internal sealed class ConnectionCut
    {
        private Func<string, bool>? _cutOn;

        private Func<Exception> _fault = TransportFault;

        private ConnectionCut(AdoptingPeer peer) => Peer = peer;

        public AdoptingPeer Peer { get; }

        public static ConnectionCut Attach(RuntimeFixture fixture)
        {
            ConnectionCut cut = new(new AdoptingPeer(fixture.Context, fixture.Clock));
            fixture.Peer.OnMessageSent = line =>
            {
                if (cut._cutOn?.Invoke(line) == true)
                {
                    throw cut._fault();
                }

                cut.Peer.Receive(line);
                return Task.CompletedTask;
            };
            return cut;
        }

        public void On(Func<string, bool> predicate, Func<Exception>? fault = null)
        {
            _cutOn = predicate;
            _fault = fault ?? TransportFault;
        }

        public void Heal() => _cutOn = null;

        private static Exception TransportFault() =>
            new IOException("No recovered Onboard peer is connected for the test vehicle.");
    }
}
