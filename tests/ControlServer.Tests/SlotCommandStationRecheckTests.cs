using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using static ControlServer.Tests.Batch7StopDrivenAdvanceDriver;
using static ControlServer.Tests.JourneyRuntimeWorkerLoadCancellationBeforeSublotTests;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;
using static ControlServer.Tests.UnloadCrashResumeTests;

namespace ControlServer.Tests;

/// <summary>
/// 装卸命令发出之前，服务端再核一次车此刻是不是停在这个停靠上（control-server#783）。
/// </summary>
/// <remarks>
/// <para>
/// 修之前唯一的位置检查是到站判定（<c>CheckArrivalAsync</c>）。过了那一刻，旅程进了等录入、等卸货结果，再也不比对 RIoT 的当前站：
/// 补偿放回、重连、服务端重启、急停释放之后，车被人开离或推离站点、在别处停稳、会话恢复 <c>Ready</c>，操作员一扫码服务端照样下装货命令，
/// 车载端第一次开锁也不设闸——车在站外开仓门。10-19 agv03 一事读码坐实。
/// </para>
/// <para>
/// 修法放在开门的那一处，不放在每一个回到装卸阶段的入口：补偿放回、修复续行、重连、重启、急停释放走到最后都要经过「下装货命令」
/// 或「下一条卸货命令」，在那里核，哪一条入口都漏不过去。核的内容就是到站判定里的车辆位置那一部分：在线、地图对、当前站等于这个停靠、
/// 速度为 0、读数新鲜。当前站读成 0 按「没有当前站」处理（与 control-server#772 同一口径）。
/// </para>
/// <para>
/// 不在站时：装货那一侧拒收这次录入（位置读得出而不对，<c>STATION_MISMATCH</c>；读不到、过期、在动，<c>VEHICLE_NOT_READY</c>），
/// 不开门，旅程停在等录入，看板挂 <c>SLOT_COMMAND_VEHICLE_NOT_AT_STOP</c>；车回到站上之后操作员再扫一次就照常装。卸货那一侧不发命令，
/// 挂同一个码，每一轮再核，车回到站上的那一轮照常发。
/// </para>
/// </remarks>
public sealed class SlotCommandStationRecheckTests
{
    // 码写成字面量：看板与车载屏上看得见的东西，改名应当让这里红，而不是跟着常量悄悄改。
    private const string NotAtStopCode = "SLOT_COMMAND_VEHICLE_NOT_AT_STOP";
    private const string StationMismatch = "STATION_MISMATCH";
    private const string VehicleNotReady = "VEHICLE_NOT_READY";

    /// <summary>不是任何一个停靠的站号：车被人开到了别处。</summary>
    private const int ElsewhereStationId = 999;

    /// <summary>
    /// 票面第一条（红证据）：旅程在等录入，车已不在取货站上、停在别处，操作员扫码。不发装货命令，这次录入按 <c>STATION_MISMATCH</c>
    /// 拒收，旅程停在等录入，看板上是这个码。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    public async Task AScanWhileTheVehicleStandsAtAnotherStationIsRefusedAndOpensNoSlot()
    {
        await using RuntimeFixture fixture = await ReachSublotWaitAsync();
        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { CurrentStationId = ElsewhereStationId };

        await fixture.SubmitSublotAsync("SUBLOT-001");
        await TickAndRunAsync(fixture);

        await AssertRefusedAsync(fixture, StationMismatch);
    }

    /// <summary>对照：车就在取货站上，扫码照常下装货命令，看板上没有这个码。</summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    public async Task AScanWhileTheVehicleStandsAtTheStopStillCommandsTheLoad()
    {
        await using RuntimeFixture fixture = await ReachSublotWaitAsync();

        await fixture.SubmitSublotAsync("SUBLOT-001");
        await TickAndRunAsync(fixture);

        await AssertLoadCommandedAsync(fixture);
    }

    /// <summary>
    /// 位置证明不了的每一种样子都不开门：当前站读成 0（RIoT 用它表示没有当前站，cs#772 同一口径）、读成空、读数过期、读不到
    /// （网关读失败时返回的就是离线、地图空、站空的那个样子）、车在动、车在别的地图上。站号读得出而不对答 <c>STATION_MISMATCH</c>，
    /// 其余答 <c>VEHICLE_NOT_READY</c>。
    /// </summary>
    [Theory]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [InlineData("position-zero", StationMismatch)]
    [InlineData("position-null", StationMismatch)]
    [InlineData("stale", VehicleNotReady)]
    [InlineData("unreadable", VehicleNotReady)]
    [InlineData("moving", VehicleNotReady)]
    [InlineData("other-map", VehicleNotReady)]
    public async Task AScanIsRefusedWhenThePositionCannotBeProven(string reading, string expectedReason)
    {
        await using RuntimeFixture fixture = await ReachSublotWaitAsync();
        RiotVehicleObservation atStop = fixture.Riot.Vehicle;
        switch (reading)
        {
            case "position-zero":
                fixture.Riot.Vehicle = atStop with { CurrentStationId = 0 };
                break;
            case "position-null":
                fixture.Riot.Vehicle = atStop with { CurrentStationId = null };
                break;
            case "stale":
                fixture.Riot.VehicleObservedAtLag = fixture.Options.MaximumEvidenceAge + TimeSpan.FromSeconds(1);
                break;
            case "unreadable":
                fixture.Riot.Vehicle = atStop with
                {
                    Connected = false,
                    Enabled = false,
                    ProcState = "UNKNOWN",
                    CurrentMap = string.Empty,
                    CurrentStationId = null,
                    Speed = null,
                };
                break;
            case "moving":
                fixture.Riot.Vehicle = atStop with { Speed = 0.4 };
                break;
            case "other-map":
                fixture.Riot.Vehicle = atStop with { CurrentMap = "another-map" };
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(reading), reading, null);
        }

        await fixture.SubmitSublotAsync("SUBLOT-001");
        await TickAndRunAsync(fixture);

        await AssertRefusedAsync(fixture, expectedReason);
    }

    /// <summary>
    /// 恢复路径，也是同一条命令的第二次下发：车不在站时扫的那一次被拒；车被开回站上、读数恢复之后，操作员再扫一次，照常下装货命令，
    /// 看板上的码随之清掉。位置每一次下命令之前都重读，不沿用上一次的结论。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    public async Task OnceTheVehicleIsBackAtTheStopTheNextScanLoads()
    {
        await using RuntimeFixture fixture = await ReachSublotWaitAsync();
        RiotVehicleObservation atStop = fixture.Riot.Vehicle;
        fixture.Riot.Vehicle = atStop with { CurrentStationId = ElsewhereStationId };
        await fixture.SubmitSublotAsync("SUBLOT-001");
        await TickAndRunAsync(fixture);
        await AssertRefusedAsync(fixture, StationMismatch);

        fixture.Riot.Vehicle = atStop;
        await fixture.SubmitSublotAsync("SUBLOT-001");
        await TickAndRunAsync(fixture);

        await AssertLoadCommandedAsync(fixture);
    }

    /// <summary>
    /// 服务端重启：旅程落库在等录入，重启前后车被开走。重启之后的第一轮照落库的阶段接着走，扫码仍要核位置。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    public async Task AScanAfterAServerRestartIsRefusedWhileTheVehicleIsAway()
    {
        await using RuntimeFixture fixture = await ReachSublotWaitAsync();
        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { CurrentStationId = ElsewhereStationId };
        await fixture.RecreateEngineAsync();

        await fixture.SubmitSublotAsync("SUBLOT-001");
        await TickAndRunAsync(fixture);

        await AssertRefusedAsync(fixture, StationMismatch);
    }

    /// <summary>
    /// 重连：会话掉了，车在这期间被开走，车载端以新的一代重新连上、会话恢复 <c>Ready</c>。重连不重进到站阶段，扫码仍要核位置。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    public async Task AScanAfterAReconnectIsRefusedWhileTheVehicleIsAway()
    {
        await using RuntimeFixture fixture = await ReachSublotWaitAsync();
        await fixture.DropOnboardSessionAsync();
        await TickAndRunAsync(fixture);
        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { CurrentStationId = ElsewhereStationId };
        await fixture.ReconnectAsync(2);
        await fixture.AdvanceSessionAsync(2);
        await TickAndRunAsync(fixture);

        JourneyRuntimeRow waiting = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, waiting.Stage);
        await fixture.AddInboxAsync(
            Guid.NewGuid().ToString("D"),
            "SublotSubmitted",
            SublotPayload(fixture, await CurrentStopAsync(fixture, BeforeSublotDemandId), waiting, "SUBLOT-001"),
            sessionGeneration: 2);
        await TickAndRunAsync(fixture);

        await AssertRefusedAsync(fixture, StationMismatch);
    }

    /// <summary>
    /// 急停释放：车在站上被急停、会话掉出 <c>Ready</c>，急停期间车被挪走，人解除急停之后车在别处停稳、会话恢复 <c>Ready</c>。
    /// 急停的解除不碰旅程阶段（<c>LoadedEmergencyStopRelease</c>、<c>EmergencyStopSupervisor</c>），旅程仍在等录入，扫码仍要核位置。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    public async Task AScanAfterAnEmergencyStopIsReleasedIsRefusedWhileTheVehicleIsAway()
    {
        await using RuntimeFixture fixture = await ReachSublotWaitAsync();
        fixture.EmergencyLatched = true;
        await fixture.DropOnboardSessionAsync();
        await TickAndRunAsync(fixture);
        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { CurrentStationId = ElsewhereStationId };
        fixture.EmergencyLatched = false;
        await fixture.RestoreSessionReadyAsync();

        await fixture.SubmitSublotAsync("SUBLOT-001");
        await TickAndRunAsync(fixture);

        await AssertRefusedAsync(fixture, StationMismatch);
    }

    /// <summary>
    /// 卸货同理：一站两条要卸，第一条卸完时车已不在卸货站上。第二条的卸货命令不发，看板挂这个码；服务端重启之后仍不发；
    /// 车回到站上的那一轮照常发，码清掉。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task TheNextUnloadAtAStopWaitsWhileTheVehicleIsAwayAndGoesOutOnceItIsBack()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        await LoadBothAndArriveAtTheUnloadStopAsync(fixture);
        (string first, string second) = await UnloadOrderAsync(fixture);
        RiotVehicleObservation atStop = fixture.Riot.Vehicle;

        await ApplySafeResultAsync(fixture, first, SlotOperationType.Unload, SlotBusinessState.Empty);
        fixture.Riot.Vehicle = atStop with { CurrentStationId = ElsewhereStationId };
        await TickAndRunAsync(fixture);

        JourneyRuntimeRow waiting = await JourneyOfAsync(fixture, first);
        Assert.False(await UnloadCommandedAsync(fixture, second), "The second unload went out while the vehicle was away from the stop.");
        Assert.Equal((JourneyRuntimeStage.AwaitingUnloadResult, NotAtStopCode), (waiting.Stage, waiting.BlockReasonCode));

        await fixture.RecreateEngineAsync();
        await TickAndRunAsync(fixture);
        Assert.False(await UnloadCommandedAsync(fixture, second), "After a restart the second unload went out while the vehicle was away.");

        fixture.Riot.Vehicle = atStop;
        await TickAndRunAsync(fixture);

        JourneyRuntimeRow resumed = await JourneyOfAsync(fixture, first);
        Assert.True(await UnloadCommandedAsync(fixture, second), "The second unload was never commanded once the vehicle was back.");
        Assert.Null(resumed.BlockReasonCode);
    }

    /// <summary>
    /// 同一条命令的第二次下发：装货命令在站上发出，车载端还没答结果（发件箱里那一行未答复），会话掉线，车在这期间被开走，
    /// 车载端以新的一代连上。服务端每一轮都把未答复的命令再发一次（<c>ReplayPendingForSessionAsync</c>），车载端按 attempt 去重——
    /// 可它若根本没收到第一次，这一次就是第一次，会在站外执行。所以车回到站上之前不再发，回到站上那一轮照常发。
    /// </summary>
    /// <remarks>
    /// 扣住不发不丢结果：车载端执行过的，握手时自己重放没被确认的结果（<c>WireToGateStore</c> 的 OperationResult 去重那一段）。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    public async Task AnUnansweredLoadCommandIsNotSentAgainWhileTheVehicleIsAway()
    {
        await using RuntimeFixture fixture = await ReachSublotWaitAsync();
        await fixture.SubmitSublotAsync("SUBLOT-001");
        await TickAndRunAsync(fixture);
        await AssertLoadCommandedAsync(fixture);
        string command = (await Batch7MultiDemandAdvanceTests.MembershipAsync(fixture, BeforeSublotDemandId)).LoadCommandMessageId;
        RiotVehicleObservation atStop = fixture.Riot.Vehicle;

        await fixture.DropOnboardSessionAsync();
        await TickAndRunAsync(fixture);
        fixture.Riot.Vehicle = atStop with { CurrentStationId = ElsewhereStationId };
        await fixture.ReconnectAsync(2);
        await fixture.AdvanceSessionAsync(2);
        fixture.Peer.Lines.Clear();
        await TickAndRunAsync(fixture);

        Assert.False(Sent(fixture, command), "The unanswered load command went out again while the vehicle stood away from the stop.");

        fixture.Riot.Vehicle = atStop;
        await TickAndRunAsync(fixture);

        Assert.True(Sent(fixture, command), "The unanswered load command was never sent again once the vehicle was back.");
    }

    /// <summary>这一条报文有没有经连接发给车。</summary>
    private static bool Sent(RuntimeFixture fixture, string messageId) =>
        fixture.Peer.Lines.Any(line =>
        {
            using JsonDocument document = JsonDocument.Parse(line);
            return document.RootElement.GetProperty("messageId").GetString() == messageId;
        });

    /// <summary>判据：录入被拒、原因码如期，没有装货命令，旅程停在等录入，看板挂不在站的码，没有分出仓位。</summary>
    private static async Task AssertRefusedAsync(RuntimeFixture fixture, string expectedReason)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        Assert.DoesNotContain("SlotOperationCommand", await fixture.OutboxTypesAsync());
        ProtocolOutboxRow[] refusals = await fixture.Context.ProtocolOutbox.AsNoTracking()
            .Where(row => row.MessageType == "SublotRejected")
            .ToArrayAsync(token);
        ProtocolOutboxRow refusal = Assert.Single(refusals);
        using JsonDocument payload = JsonDocument.Parse(refusal.PayloadJson);
        JourneyRuntimeRow runtime = await fixture.RuntimeAsync();
        Assert.Equal(
            (expectedReason, JourneyRuntimeStage.AwaitingSublot, NotAtStopCode),
            (payload.RootElement.GetProperty("payload").GetProperty("problem").GetProperty("reasonCode").GetString(),
                runtime.Stage, runtime.BlockReasonCode));
        Assert.Equal("[]", (await Batch7MultiDemandAdvanceTests.MembershipAsync(fixture, BeforeSublotDemandId)).TargetSlotsJson);
    }

    /// <summary>判据：装货命令发出了，旅程在等装货结果，看板上没有不在站的码。</summary>
    private static async Task AssertLoadCommandedAsync(RuntimeFixture fixture)
    {
        Assert.Contains("SlotOperationCommand", await fixture.OutboxTypesAsync());
        JourneyRuntimeRow runtime = await fixture.RuntimeAsync();
        Assert.Equal(
            (JourneyRuntimeStage.AwaitingLoadResult, (string?)null),
            (runtime.Stage, runtime.BlockReasonCode));
    }

    /// <summary>车载端提交的录入，按当前停靠此刻这一版清单。</summary>
    private static object SublotPayload(RuntimeFixture fixture, JourneyStopRow stop, JourneyRuntimeRow runtime, string sublot) => new
    {
        operationSessionId = stop.OperationSessionId,
        stationId = stop.StationId,
        worklistRevision = runtime.WorklistRevision,
        sublot,
        entryMethod = "SCANNER",
        @operator = new
        {
            operatorId = "OP-001",
            verificationMethod = "BADGE",
            verifiedAt = fixture.Clock.GetUtcNow()
        }
    };
}
