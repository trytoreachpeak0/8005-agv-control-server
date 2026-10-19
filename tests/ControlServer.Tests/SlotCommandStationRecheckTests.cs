using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
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
    // 审查 S-2：只有 Connected=false，站号、地图、速度全对。真网关按读取时刻盖时间，离线车的最后读数永远不过期，挡住它的只有在线这一项。
    [InlineData("offline", VehicleNotReady)]
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
            case "offline":
                fixture.Riot.Vehicle = atStop with { Connected = false };
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

    /// <summary>
    /// 真车载端的常态：会话因本服务端自己的单而未就绪（<c>DEPARTURE_SAFETY_NOT_READY</c>，形状照
    /// <c>PickupDispatchPlanPastOwnOrderTests.DropSessionOnOwnOrderAsync</c>）。新的一代连上之后先停在这个样子：这期间未答复的装货命令一次都不发，
    /// 推进停在就绪闸前，位置核对根本走不到。会话回到 <c>Ready</c> 的第一轮，车仍不在站上，照样不发；车回到站上才发。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    public async Task AnUnansweredLoadCommandStaysUnsentThroughASessionNotReadyOnTheServersOwnOrder()
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
        await PickupDispatchPlanPastOwnOrderTests.DropSessionOnOwnOrderAsync(fixture);
        fixture.Peer.Lines.Clear();

        await TickAndRunAsync(fixture);
        await TickAndRunAsync(fixture);
        JourneyRuntimeRow notReady = await fixture.RuntimeAsync();
        Assert.Equal(
            (JourneyRuntimeStage.AwaitingLoadResult, "ONBOARD_SESSION_NOT_READY"),
            (notReady.Stage, notReady.BlockReasonCode));
        Assert.False(Sent(fixture, command), "The unanswered load command went out while the session was not ready.");

        await fixture.RestoreSessionReadyAsync();
        await TickAndRunAsync(fixture);
        Assert.False(Sent(fixture, command), "The first ready round sent the load command with the vehicle still away.");

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

    /// <summary>
    /// 审查 S-4：位置读数有自己的预算。RIoT 卡住时这一次读在预算到点就放弃、按位置证明不了处理（拒收，<c>VEHICLE_NOT_READY</c>），
    /// 这一轮在预算量级内结束，不等网关自己的 30 秒——否则每辆车每轮多等一个网关超时，急停确认也排在后面。替身的读车 20 秒才答，
    /// 预算设 200 毫秒，判据给 5 秒；计时用真实时钟，预算的计时器就是真实的（同 <c>WaitingJourneyBatteryWatchTests</c>）。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    public async Task APositionReadThatHangsIsCutOffAtItsBudgetAndTheScanRefused()
    {
        await using RuntimeFixture fixture = await ReachSublotWaitAsync();
        fixture.Options.SlotCommandPositionReadBudget = TimeSpan.FromMilliseconds(200);
        await fixture.RecreateEngineAsync();
        fixture.Riot.ReadVehicleDelay = token => Task.Delay(TimeSpan.FromSeconds(20), token);

        await fixture.SubmitSublotAsync("SUBLOT-001");
        System.Diagnostics.Stopwatch elapsed = System.Diagnostics.Stopwatch.StartNew();
        await TickAndRunAsync(fixture);
        elapsed.Stop();

        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(5), $"The round took {elapsed.Elapsed}.");
        await AssertRefusedAsync(fixture, VehicleNotReady);
    }

    /// <summary>读位置抛异常（网关之外的失败）：这一轮不抛，按位置证明不了拒收。</summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    public async Task APositionReadThatFailsRefusesTheScanWithoutFailingTheRound()
    {
        await using RuntimeFixture fixture = await ReachSublotWaitAsync();
        fixture.Riot.BeforeReadVehicle = () => throw new HttpRequestException("RIoT is not answering.");

        await fixture.SubmitSublotAsync("SUBLOT-001");
        await TickAndRunAsync(fixture);

        await AssertRefusedAsync(fixture, VehicleNotReady);
    }

    /// <summary>卸货那一侧同样有预算：读挂住时这一轮在预算量级内结束，下一条卸货命令不发。</summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task AnUnloadPositionReadThatHangsIsCutOffAtItsBudgetAndHoldsTheUnload()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        await LoadBothAndArriveAtTheUnloadStopAsync(fixture);
        (string first, string second) = await UnloadOrderAsync(fixture);
        fixture.Options.SlotCommandPositionReadBudget = TimeSpan.FromMilliseconds(200);
        await fixture.RecreateEngineAsync();
        await ApplySafeResultAsync(fixture, first, SlotOperationType.Unload, SlotBusinessState.Empty);
        fixture.Riot.ReadVehicleDelay = token => Task.Delay(TimeSpan.FromSeconds(20), token);

        System.Diagnostics.Stopwatch elapsed = System.Diagnostics.Stopwatch.StartNew();
        await TickAndRunAsync(fixture);
        elapsed.Stop();

        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(5), $"The round took {elapsed.Elapsed}.");
        Assert.False(await UnloadCommandedAsync(fixture, second), "The second unload went out on a position read that never answered.");
        Assert.Equal(NotAtStopCode, (await JourneyOfAsync(fixture, first)).BlockReasonCode);
    }

    /// <summary>
    /// 审查 S-1：卸货站没有离站期限，车被推回站上而 RIoT 仍报 0（10-18 实测要在 RIoT 上重新定位），卸货会一直扣着。挂码期间每 5 分钟
    /// 记一次当前读数（2318），不只记第一次：现场要能从日志看出车此刻读成哪一站。十二分钟里记三次：挂码那一轮、第 5 分钟、第 10 分钟。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task AHeldUnloadLogsThePositionAgainEveryFiveMinutes()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        await LoadBothAndArriveAtTheUnloadStopAsync(fixture);
        (string first, _) = await UnloadOrderAsync(fixture);
        await ApplySafeResultAsync(fixture, first, SlotOperationType.Unload, SlotBusinessState.Empty);
        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { CurrentStationId = 0 };

        await TickAndRunAsync(fixture);
        for (int minute = 1; minute <= 12; minute++)
        {
            fixture.Clock.Advance(TimeSpan.FromSeconds(59));
            await fixture.HearFromPeerAsync();
            await TickAndRunAsync(fixture);
        }

        Assert.Equal(NotAtStopCode, (await JourneyOfAsync(fixture, first)).BlockReasonCode);
        int logged;
        lock (fixture.EngineLog.Entries)
        {
            logged = fixture.EngineLog.Entries.Count(entry =>
                entry.Message.Contains("was not sent: the vehicle is not proven at its stop", StringComparison.Ordinal));
        }
        Assert.Equal(3, logged);
    }

    /// <summary>读数预算默认 2 秒；0 与超过 10 秒的值在启动时被拒（与 <c>WaitingJourneyBatteryReadBudget</c> 同一个范围）。</summary>
    [Theory]
    [InlineData(null, true)]
    [InlineData("00:00:00", false)]
    [InlineData("00:00:11", false)]
    [InlineData("00:00:10", true)]
    public async Task ThePositionReadBudgetDefaultsToTwoSecondsAndStaysWithinTen(string? value, bool valid)
    {
        Assert.Equal(TimeSpan.FromSeconds(2), new JourneyRuntimeOptions().SlotCommandPositionReadBudget);
        // 校验器只在 Enabled 时逐项查，所以用夹具那份已启用的选项（同 WaitingJourneyBatteryWatchTests 的做法）。
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        JourneyRuntimeOptions options = fixture.Options;
        if (value is not null)
        {
            options.SlotCommandPositionReadBudget = TimeSpan.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
        }

        Microsoft.Extensions.Options.ValidateOptionsResult result =
            new JourneyRuntimeOptionsValidator(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()).Validate(null, options);

        Assert.Equal(
            !valid,
            (result.Failures ?? []).Contains("SlotCommandPositionReadBudget must be positive and at most 10 s."));
    }

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
