using System.Text;
using System.Text.Json;
using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;

namespace ControlServer.Tests;

/// <summary>
/// 断线重连的基于模型随机测试（control-server#342）找到的序列，固定成确定性的回归用例。
/// </summary>
/// <remarks>
/// 模型本身见 <see cref="ReconnectModel"/>。这里每一条都是化简之后的原样序列，或者由它写成的普通 L1；它们在哪个提交上红、
/// 为什么红，写在各自的注释里。
/// </remarks>
public sealed class ReconnectModelRegressionTests
{
    private const string DemandId = "10000000-0000-4000-8000-000000000001";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// 化简出的序列与校准序列，每一条收尾之后都要把录入请求以当前这一代送到车上。
    /// </summary>
    public static TheoryData<string> Sequences => [.. SequencesByName.Keys];

    private static readonly Dictionary<string, ReconnectStep[]> SequencesByName = new(StringComparer.Ordinal)
    {
        // 原型模型在 af01fd27 上 300 个组合里 17 个卡在取货站，确定性删减到这三步（最终模型动作多了，同样的种子生成的序列不同，
        // 300 个里 8 个，首个种子 0000000005_1，删减结果与这里相同）：到站那一轮派车计划与车辆业务状态发出去了，
        // 清单那一条发送时断线，车一张都没确认；收尾重连后每一轮都在重放校验上抛异常。62d5c560 上同样卡住。
        ["cut-after-the-business-state-nothing-acknowledged"] =
            [new ReconnectStep.Arrive(), new ReconnectStep.CutAfter(2), new ReconnectStep.Round(1)],
        // 失败现场的形状（cs#331）：车辆业务状态与清单都已确认，到站计划发送时断线，立刻重连。af01fd27、62d5c560 上卡住。
        ["field-shape-immediate-reconnect"] =
            [new ReconnectStep.Arrive(), new ReconnectStep.CutAfter(3), new ReconnectStep.Round(1), new ReconnectStep.Ack()],
        // 同上，握手进行中引擎跑了一轮（会话未就绪的一轮）。
        ["field-shape-round-inside-the-handshake"] =
        [
            new ReconnectStep.Arrive(), new ReconnectStep.CutAfter(3), new ReconnectStep.Round(1), new ReconnectStep.Ack(),
            new ReconnectStep.BeginHandshake(), new ReconnectStep.Round(7),
        ],
        // 同上，重连之前车报未就绪跑了一轮（cs#331 审查补的那一轮）。
        ["field-shape-not-ready-round-before-the-reconnect"] =
        [
            new ReconnectStep.Arrive(), new ReconnectStep.CutAfter(3), new ReconnectStep.Round(1), new ReconnectStep.Ack(),
            new ReconnectStep.VehicleNotReady(false), new ReconnectStep.Round(7),
        ],
    };

    /// <summary>
    /// 模型找到并化简出的序列，收尾之后录入请求以当前这一代送到了车上，没有违规。
    /// </summary>
    [Theory]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-SESSION-RECONNECT-DURING-RECOVERY")]
    [Trait("ProtocolVector", "CV-PICKUP-SUBLOT-LOAD")]
    [MemberData(nameof(Sequences))]
    public async Task ASequenceTheModelOnceFoundStuckNowReachesTheEntryRequest(string name)
    {
        ReconnectStep[] steps = SequencesByName[name];

        ReconnectVerdict verdict = await ReconnectModel.RunAsync(steps);

        Assert.True(
            verdict.Violation is null,
            $"{name} {ReconnectModel.Print(steps)} violated {verdict.Violation}:\n{verdict.Detail}");
        Assert.Contains("entryReachedVehicle=True", verdict.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// RIoT 报告去取货站的那张单挂起（<c>ORDER_HANG</c>，等人去 RIoT 里处理）时，这一轮推进因为连接不在而失败：码与开始时间都不变。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 模型在 cs#331 第一版修复 <c>62d5c560</c> 上找到（300 个组合里 6 个，首个种子 <c>0000000005_1</c>，最短的 10 步删减到 4 步；
    /// 模型删出的顺序是先断线后挂起，这里写成先挂起，两种顺序在 <c>62d5c560</c> 上都红）：单挂起、连接在下一条
    /// 发送时断；挂起那一轮写上 <c>ORDER_HANG</c>，下一轮开头补发没确认的计划时抛 <see cref="IOException"/>，那一版把 <c>ORDER_HANG</c>
    /// 换成了 <c>JOURNEY_ADVANCE_FAILED</c>——「不覆盖指名在等谁的码」是 cs#331 第二轮审查才补的。
    /// </para>
    /// <para>
    /// 断言机会确实出现过（<see cref="ReconnectVerdict.WaitOnPersonChances"/>）：带着等人码进来的那一轮要真的失败，否则「没有违规」恒真。
    /// </para>
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-01")]
    [Trait("ProtocolVector", "CV-DEMAND-ACCEPT-TO-PICKUP")]
    public async Task AHungOrderKeepsItsCodeAndStartWhenARoundFailsOnTheLostConnection()
    {
        ReconnectStep[] steps =
        [
            new ReconnectStep.OrderHang(), new ReconnectStep.CutAfter(1), new ReconnectStep.Round(1), new ReconnectStep.Round(1),
        ];

        ReconnectVerdict verdict = await ReconnectModel.RunAsync(steps);

        Assert.Contains("block=ORDER_HANG", verdict.Detail, StringComparison.Ordinal);
        Assert.True(verdict.WaitOnPersonChances >= 1, verdict.Detail);
        Assert.Empty(verdict.Violations);
    }

    /// <summary>
    /// 一条已被收下的安全变化，同一个 messageId、安全内容不同，再发一次：服务端拒绝，不回 <c>DurableAck</c>。
    /// </summary>
    /// <remarks>
    /// 不变量「防重放护栏不放过语义不同的消息」的确定性用例，手写的，给它一个肯定会出现的机会；判别力靠对护栏的变异证明
    /// （control-server#342 的 PR 正文）。断言「拒绝」真的发生了：不然「没有违规」在那一条根本没发出去时也成立。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("ProtocolVector", "CV-SESSION-RECONNECT-DURING-RECOVERY")]
    public async Task ASafetyChangeResentWithDifferentContentUnderItsMessageIdIsRefused()
    {
        ReconnectStep[] steps =
        [
            new ReconnectStep.SafetyChange(SafetyKind.NotSafe, SafetyDelivery.Delivered),
            new ReconnectStep.ConflictingResend(),
        ];

        ReconnectVerdict verdict = await ReconnectModel.RunAsync(steps);

        Assert.Contains("with different content refused", verdict.Detail, StringComparison.Ordinal);
        Assert.Empty(verdict.Violations);
    }

    /// <summary>
    /// 车的一条安全变化在路上丢了（连接在它送到之前断了），下一次握手补发它：服务端对它只回一条 <c>DurableAck</c>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 模型在 <c>18172346</c> 上 300 个组合里找到 157 个，最短的一个（2 步）确定性删减到这一步。补发的
    /// <c>SafetyStateChanged</c> 回的是 <c>DurableAck</c> 加一行 <c>SessionReadiness</c>，车每发一条只读一条答复，把多出的那一行当成
    /// 能力快照的答复，断开重连——control-server#340，由 PR #343 修复（合入提交 <c>384b9b69</c>），同一个种子在那上面不再报这一类。
    /// </para>
    /// <para>
    /// 安全内容相同与不同各一行：补发的那一条与握手快照的内容是否相同，对这一条不该有影响。
    /// </para>
    /// </remarks>
    [Theory]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("ProtocolVector", "CV-SESSION-RECONNECT-DURING-RECOVERY")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ASafetyChangeLostInFlightIsResentInTheHandshakeAndAnsweredOnce(bool unsafeChange)
    {
        SafetyKind kind = unsafeChange ? SafetyKind.NotSafe : SafetyKind.Safe;
        ReconnectVerdict verdict = await ReconnectModel.RunAsync([new ReconnectStep.SafetyChange(kind, SafetyDelivery.LostInFlight)]);

        Assert.False(verdict.Has(ReconnectViolation.OneInboundManyAnswers), verdict.Detail);
        // 补发真的发生了、只回了一条：没有这一行，上面那条断言在「根本没补发」时也成立。
        Assert.Contains("handshake SafetyStateChanged -> DurableAck" + Environment.NewLine, verdict.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// 同上，而且整个握手要走完：同一代里补发 <c>SafetyStateChanged</c> vN 之后，同为 vN 的安全快照不被拒。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 模型在含 cs#340 修复的本分支上 300 个组合里找到 129 个，最短的一个（2 步）确定性删减到这一步，与上面那条相同。服务端按整行报文的哈希判「同号不同内容」，补发的变化通知与握手快照哪怕安全内容逐字相同，整行也必然不同，于是
    /// <c>safety revision N has conflicting content</c>，握手被拒——onboard-hmi#206。
    /// </para>
    /// <para>
    /// 在 cs#340 修好之前走不到这里：握手在补发那一条的答复上就断了。所以这一条要等 cs#340 与 hmi#206 都修好才会绿。
    /// </para>
    /// </remarks>
    [Theory(Skip = "known defect: onboard-hmi#206 https://github.com/trytoreachpeak0/8005-agv-onboard-hmi/issues/206")]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("ProtocolVector", "CV-SESSION-RECONNECT-DURING-RECOVERY")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ASafetyChangeLostInFlightDoesNotGetTheNextHandshakeRefused(bool unsafeChange)
    {
        SafetyKind kind = unsafeChange ? SafetyKind.NotSafe : SafetyKind.Safe;
        ReconnectVerdict verdict = await ReconnectModel.RunAsync([new ReconnectStep.SafetyChange(kind, SafetyDelivery.LostInFlight)]);

        Assert.Empty(verdict.Violations);
        Assert.Contains("entryReachedVehicle=True", verdict.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// 离站等待期限开着时，到站那一段被断线打断之后立刻重连（中间没有任何一轮看见会话未就绪），录入请求照样以第 2 代发出。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>这一条在 cs#331 的第一版修复 <c>62d5c560</c> 上是红的</b>（control-server#342 的模型找到，这里写成普通 L1）：
    /// 重连之后第一轮就把离站等待的起点作废、从此刻重填，清单于是带着一个新的期限，与车已确认的那一版不同，第一版修复只放过
    /// 「一字不差」的已确认快照，每一轮都在清单上抛 <c>ProtocolContentConflictException</c>。
    /// </para>
    /// <para>
    /// PR #338 里同样形状的 <c>TheEntryRequestReachesTheVehicleAfterAReconnectInterruptedTheArrivalPublish</c> 在
    /// <c>62d5c560</c> 上是绿的，因为它没开期限（夹具默认 <c>StationDepartureWaitTimeout</c> 为零，清单里的期限永远是 null）。
    /// 审查是靠「重连之前闸门关着跑一轮」才碰到期限这一层的；这一轮并不必要，重连本身就作废期限。
    /// </para>
    /// <para>
    /// 两个条件缺一个就测不出来：期限要开着；重连前后钟要走（钟不走时重填出来的还是原来那个时刻）。
    /// </para>
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-SESSION-RECONNECT-DURING-RECOVERY")]
    [Trait("ProtocolVector", "CV-PICKUP-SUBLOT-LOAD")]
    public async Task AnImmediateReconnectWithTheStationDepartureWaitOnStillSendsTheEntryRequest()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Options.StationDepartureWaitTimeout = TimeSpan.FromSeconds(30);
        AdoptingPeer vehicle = new(fixture.Context, fixture.Clock);
        bool cutOnTheArrivedPlan = false;
        fixture.Peer.OnMessageSent = line =>
        {
            if (cutOnTheArrivedPlan && IsArrivedPickupPlan(line))
            {
                throw new IOException("No recovered Onboard peer is connected for the test vehicle.");
            }

            vehicle.Receive(line);
            return Task.CompletedTask;
        };
        fixture.Catalog.Set(fixture.Demand(DemandId, "SUBLOT-001", Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        await fixture.Engine.ExecuteOnceAsync(Token);
        JourneyRuntimeRow dispatched = await fixture.RuntimeAsync();
        fixture.Riot.SetSuccessfulArrival("TO_PICKUP", dispatched.PickupStationRiotId);
        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { CurrentStationId = dispatched.PickupStationRiotId };

        cutOnTheArrivedPlan = true;
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<IOException>(() => fixture.Engine.ExecuteOnceAsync(Token));
        await fixture.RecreateEngineAsync();
        cutOnTheArrivedPlan = false;
        await vehicle.DeliverBufferedAcksAsync();
        DateTimeOffset waitStarted = (await fixture.RuntimeAsync()).StationDepartureWaitStartedAt
            ?? throw new InvalidOperationException("The station departure wait must be running before the reconnect.");
        Assert.Contains(
            vehicle.Adopted,
            adopted => adopted.MessageType == "CurrentStopWorklistSnapshot");

        await fixture.ReconnectAsync(2);
        await fixture.AdvanceSessionAsync(2);
        fixture.Clock.Advance(TimeSpan.FromSeconds(2));
        Assert.NotEqual(waitStarted, fixture.Clock.GetUtcNow());
        await fixture.HearFromPeerAsync();
        await fixture.Engine.ExecuteOnceAsync(Token);

        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, (await fixture.RuntimeAsync()).Stage);
        JsonElement entry = Assert.Single(SentLines(fixture, "SublotEntryRequested"));
        Assert.Equal(2, entry.GetProperty("sessionGeneration").GetInt64());
    }

    /// <summary>到站那一版取货计划：取货那一段已经是 <c>ARRIVED</c>。</summary>
    private static bool IsArrivedPickupPlan(string ndjsonLine)
    {
        using JsonDocument document = JsonDocument.Parse(ndjsonLine.TrimEnd('\n'));
        JsonElement root = document.RootElement;
        return root.GetProperty("messageType").GetString() == "UpcomingStopPlanSnapshot" &&
               root.GetProperty("payload").GetProperty("legs").EnumerateArray()
                   .Any(leg => leg.GetProperty("legType").GetString() == "TO_PICKUP" &&
                               leg.GetProperty("state").GetString() == "ARRIVED");
    }

    private static JsonElement[] SentLines(RuntimeFixture fixture, string messageType) =>
        [.. fixture.Peer.Lines
            .Select(line => JsonDocument.Parse(Encoding.UTF8.GetString(line)).RootElement.Clone())
            .Where(root => root.GetProperty("messageType").GetString() == messageType)];
}
