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
        // af01fd27 上 300 个组合里 17 个卡在取货站，确定性删减到这三步：到站那一轮派车计划与车辆业务状态发出去了，
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
