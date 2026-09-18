using System.Net.Http.Json;
using System.Text.Json;
using ControlServer.Dashboard;
using ControlServer.Domain;
using ControlServer.Host.Composition;
using ControlServer.Host.Dashboard;
using ControlServer.Host.Runtime;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ControlServer.Tests;

/// <summary>
/// 看板「期待动作超时」只读卡片（REQ-0358，CP-0005 实现票 2，control-server#142）。
/// </summary>
/// <remarks>
/// <para>
/// 车载端的告警（hmi#109）还没合入，所以这里的告警快照是按 CP-0005 第 4.1 节构造的：<c>code=SLOT_EXPECTED_ACTION_OVERDUE</c>、
/// <c>subjectType=SLOT</c>、<c>subjectId</c> 为仓位号、<c>raisedAt</c> 为越过门槛的时刻、<c>displayMessage</c> 为期待的动作，
/// <c>alarmId</c> 在同一次超时的整个生命期不变（与 hmi#109 会话 2026-09-18 对齐）。
/// </para>
/// <para>
/// 分四个接缝：Host 收消息（告警到达发一次 <c>SafetyStateSnapshotRequested</c>、涉及超时仓的 <c>SafetyStateChanged</c> 再发一次、
/// 会话中途的 <c>SafetyStateSnapshot</c> 只作读数）；派车引擎（中途读数不改可用仓位与安全摘要）；只读查询端点；卡片。
/// </para>
/// </remarks>
public sealed class ExpectedActionOverdueTests
{
    private const string CredentialVariable = "CONTROL_SERVER_TEST_EXPECTED_ACTION_OVERDUE_CREDENTIAL";

    private const string Credential = "test-credential-not-for-production";

    private const string AgvId = "AGV-001";

    private const string OverdueCode = "SLOT_EXPECTED_ACTION_OVERDUE";

    private const string OverdueAlarmId = "00000000-0000-4000-8000-00000000a003";

    private static readonly DateTimeOffset Now = new(2026, 9, 18, 8, 0, 0, TimeSpan.Zero);

    private static readonly JsonSerializerOptions WireJson = new(JsonSerializerDefaults.Web);

    // --- Host：告警到达时取一次读数 ------------------------------------------------------------------------------

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-15")]
    public async Task ANewOverdueAlarmAfterTheHandshakeAsksTheVehicleOnceForItsSafetySnapshot()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await fixture.ReachReadyAsync();

        string[] first = Lines(await fixture.SendAlarmsAsync(1, Overdue(3, "关好3号仓门")));

        Assert.Equal(2, first.Length);
        Assert.Equal("SnapshotAppliedAck", MessageType(first[0]));
        using JsonDocument request = JsonDocument.Parse(first[1]);
        Assert.Equal("SafetyStateSnapshotRequested", request.RootElement.GetProperty("messageType").GetString());
        Assert.Equal(AgvId, request.RootElement.GetProperty("agvId").GetString());
        Assert.Equal(fixture.State.SessionGeneration, request.RootElement.GetProperty("sessionGeneration").GetInt64());
        Assert.Equal(JsonValueKind.Null, request.RootElement.GetProperty("correlationId").ValueKind);
        JsonElement payload = request.RootElement.GetProperty("payload");
        // 服务端手上的仓位读数停在会话首份快照的版本上，落后于当前 safetyStateVersion：这就是协议 enum 里的「版本缺口」。
        Assert.Equal("VERSION_GAP", payload.GetProperty("reason").GetString());
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("requestedSafetyStateVersion").ValueKind);

        // 同一次超时的下一份快照（alarmId 不变，期待的动作随读数改了措辞）不再请求：「发一次」。
        string[] second = Lines(await fixture.SendAlarmsAsync(2, Overdue(3, "放入货物并关好3号仓门")));
        Assert.Equal(["SnapshotAppliedAck"], second.Select(MessageType));

        // 别的告警来来去去也不请求：只有新出现的期待动作超时才算。
        string[] third = Lines(await fixture.SendAlarmsAsync(
            3, Overdue(3, "放入货物并关好3号仓门"), Alarm("ONBOARD_FLEET_CLOCK_SKEW", "VEHICLE", null)));
        Assert.Equal(["SnapshotAppliedAck"], third.Select(MessageType));
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-15")]
    public async Task AnOverdueAlarmInsideTheHandshakeIsNotAnsweredWithARequestBetweenTheSnapshotsTheVehicleIsWaitingOn()
    {
        // 车载端握手时按「能力、安全、告警、恢复报告」的顺序发，每发一份就读一个回应；这时插进一条请求，会被它当成下一份回应读掉，
        // 握手就断了。握手里刚到的那份安全快照本来就是新的，不需要再要。
        await using Fixture fixture = await Fixture.CreateAsync();
        await fixture.HelloAsync();
        await fixture.CapabilityAsync();
        await fixture.SafetySnapshotAsync(1, Slots());

        string[] response = Lines(await fixture.SendAlarmsAsync(1, Overdue(3, "关好3号仓门")));

        Assert.Equal(["SnapshotAppliedAck"], response.Select(MessageType));

        // 握手做完以后，那条告警已经在库里了，不算「新出现」，也不补发。
        await fixture.RecoveryReportAsync();
        Assert.Equal(["SnapshotAppliedAck"], Lines(await fixture.SendAlarmsAsync(2, Overdue(3, "关好3号仓门"))).Select(MessageType));
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-15")]
    public async Task ASafetyChangeTouchingAnOverdueSlotAsksAgainAndOneTouchingOnlyOtherSlotsDoesNot()
    {
        // SafetyStateChanged 只说哪些仓变了，不带新读数；「随 SafetyStateChanged 更新」因此是：变的正是超时仓时再要一份快照。
        await using Fixture fixture = await Fixture.CreateAsync();
        await fixture.ReachReadyAsync();
        await fixture.SendAlarmsAsync(1, Overdue(3, "关好3号仓门"));

        string[] otherSlot = Lines(await fixture.SafetyChangedAsync(2, affectedSlots: [5]));
        Assert.DoesNotContain("SafetyStateSnapshotRequested", otherSlot.Select(MessageType));

        string[] overdueSlot = Lines(await fixture.SafetyChangedAsync(3, affectedSlots: [2, 3]));
        Assert.Equal("DurableAck", MessageType(overdueSlot[0]));
        Assert.Equal("SafetyStateSnapshotRequested", MessageType(overdueSlot[^1]));

        // 告警撤下之后，同一个仓再变也不再要。
        await fixture.SendAlarmsAsync(2);
        string[] afterWithdrawal = Lines(await fixture.SafetyChangedAsync(4, affectedSlots: [3]));
        Assert.DoesNotContain("SafetyStateSnapshotRequested", afterWithdrawal.Select(MessageType));
    }

    // --- Host：会话中途的安全快照只作读数 ------------------------------------------------------------------------

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-15")]
    public async Task AMidSessionSnapshotAdvancesTheSafetyRevisionWithoutDroppingTheSessionBackIntoTheHandshake()
    {
        // 车载端中途回的快照用下一个 safetyStateVersion（与 SafetyStateChanged 同一把锁分配，hmi#109）。它是一次真的安全状态前进，
        // 照常走修订链；变的只是它不再把会话打回 HANDSHAKE_INCOMPLETE——那是握手专用的写法。
        await using Fixture fixture = await Fixture.CreateAsync();
        await fixture.ReachReadyAsync();
        await fixture.SafetyChangedAsync(2, affectedSlots: [3]);

        string response = await fixture.SafetySnapshotAsync(3, Slots(slot3Physical: "OCCUPIED"));

        string[] lines = Lines(response);
        Assert.Equal(["SnapshotAppliedAck", "SessionReadiness"], lines.Select(MessageType));
        using JsonDocument ack = JsonDocument.Parse(lines[0]);
        Assert.Equal("SAFETY_STATE", ack.RootElement.GetProperty("payload").GetProperty("snapshotKind").GetString());
        Assert.Equal(3, ack.RootElement.GetProperty("payload").GetProperty("appliedRevision").GetInt64());
        Assert.Equal("READY", Readiness(response));
        SessionRecoveryRow after = await fixture.SessionAsync();
        Assert.Equal(SessionReadiness.Ready, after.Readiness);
        Assert.Equal("READY", after.ReasonCode);
        Assert.Equal(3, after.SafetyRevision);
        Assert.Equal(3, fixture.State.SafetyRevision);
        Assert.Equal(SessionReadiness.Ready, fixture.State.Readiness);
    }

    [Theory]
    [Trait("IntegrationSlice", "FP-IS-15")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheSameSafetyFactReachesTheSameReadinessWhetherItCameAsAChangeOrAsAMidSessionSnapshot(bool departureSafe)
    {
        await using Fixture byChange = await Fixture.CreateAsync();
        await byChange.ReachReadyAsync();
        await using Fixture bySnapshot = await Fixture.CreateAsync();
        await bySnapshot.ReachReadyAsync();

        string changeResponse = await byChange.SafetyChangedAsync(2, affectedSlots: [3], departureSafe);
        string snapshotResponse = await bySnapshot.SafetySnapshotAsync(2, Slots(slot3Lock: "UNLOCKED"), departureSafe);

        SessionRecoveryRow changed = await byChange.SessionAsync();
        SessionRecoveryRow snapshotted = await bySnapshot.SessionAsync();
        Assert.Equal(changed.Readiness, snapshotted.Readiness);
        Assert.Equal(changed.ReasonCode, snapshotted.ReasonCode);
        Assert.Equal(changed.DepartureSafe, snapshotted.DepartureSafe);
        Assert.Equal(changed.SafetyReasonCodesJson, snapshotted.SafetyReasonCodesJson);
        Assert.Equal(changed.SafetyUnknownPresent, snapshotted.SafetyUnknownPresent);
        Assert.Equal(Readiness(changeResponse), Readiness(snapshotResponse));
        Assert.Equal(byChange.State.Readiness, bySnapshot.State.Readiness);
        if (!departureSafe)
        {
            // 没有在途操作时锁没关好，本来就不就绪；两条路都得到这个结论，才说明中途快照没有被特殊放行。
            Assert.NotEqual(SessionReadiness.Ready, snapshotted.Readiness);
        }
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-15")]
    public async Task AMidSessionSnapshotStillMayNotChangeARevisionsContentOrGoBackwards()
    {
        // 一条冲突都不吞：同一版本换内容、版本倒退，与会话里任何安全消息一样按内容冲突失败关闭，库不动。
        await using Fixture fixture = await Fixture.CreateAsync();
        await fixture.ReachReadyAsync();
        await fixture.SafetyChangedAsync(2, affectedSlots: [3]);
        SessionRecoveryRow before = await fixture.SessionAsync();

        await Assert.ThrowsAsync<ProtocolContentConflictException>(
            () => fixture.SafetySnapshotAsync(2, Slots(slot3Lock: "UNLOCKED")));
        await Assert.ThrowsAsync<ProtocolContentConflictException>(
            () => fixture.SafetySnapshotAsync(1, Slots()));

        SessionRecoveryRow after = await fixture.SessionAsync();
        Assert.Equal(before.SafetyRevision, after.SafetyRevision);
        Assert.Equal(before.SafetyHash, after.SafetyHash);
        Assert.Equal(SessionReadiness.Ready, after.Readiness);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    public async Task TheHandshakeSnapshotStillCarriesTheSessionToReadyExactlyAsBefore()
    {
        // 反向用例：会话还没有安全基线时到达的那份，照旧是握手快照——记下修订号与摘要、把会话放在 HANDSHAKE_INCOMPLETE，
        // 由恢复报告推到 Ready。读数规则只管基线之后的那些。
        await using Fixture fixture = await Fixture.CreateAsync();
        await fixture.HelloAsync();
        await fixture.CapabilityAsync();

        string response = await fixture.SafetySnapshotAsync(4, Slots(), departureSafe: true);

        Assert.Equal(["SnapshotAppliedAck"], Lines(response).Select(MessageType));
        SessionRecoveryRow handshaking = await fixture.SessionAsync();
        Assert.Equal(4, handshaking.SafetyRevision);
        Assert.True(handshaking.DepartureSafe);
        Assert.Equal(SessionReadiness.RecoveryRequired, handshaking.Readiness);
        Assert.Equal("HANDSHAKE_INCOMPLETE", handshaking.ReasonCode);
        Assert.Equal(4, fixture.State.SafetyRevision);

        string recovery = await fixture.RecoveryReportAsync();

        Assert.Equal("READY", Readiness(recovery));
        Assert.Equal(SessionReadiness.Ready, (await fixture.SessionAsync()).Readiness);
    }

    // --- 只读查询端点 ---------------------------------------------------------------------------------------------

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-15")]
    public async Task TheEndpointListsTheOverdueSlotWithVehicleStationActionWaitAndTheReadingsTheVehicleSent()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await fixture.ReachReadyAsync();
        await fixture.AddJourneyAsync(JourneyRuntimeStage.AwaitingLoadResult, blockReasonCode: null);
        await fixture.SendAlarmsAsync(1, Overdue(3, "关好3号仓门", raisedAt: Now.AddMinutes(-2)));
        await fixture.SafetySnapshotAsync(2, Slots(slot3Lock: "UNLOCKED", slot3Physical: "OCCUPIED"), observedAt: Now.AddSeconds(-5));

        using JsonDocument fact = await fixture.ReadEndpointAsync();

        Assert.Equal(360, fact.RootElement.GetProperty("thresholdSeconds").GetInt64());
        Assert.Empty(fact.RootElement.GetProperty("unavailableVehicles").EnumerateArray());
        JsonElement row = Assert.Single(fact.RootElement.GetProperty("slots").EnumerateArray());
        Assert.Equal(AgvId, row.GetProperty("agvId").GetString());
        Assert.Equal("PICKUP-1", row.GetProperty("stationId").GetString());
        Assert.Equal("LOAD", row.GetProperty("operationType").GetString());
        Assert.Equal(3, row.GetProperty("slotNo").GetInt32());
        Assert.Equal("关好3号仓门", row.GetProperty("expectedAction").GetString());
        Assert.Equal(Now.AddMinutes(-2), row.GetProperty("raisedAt").GetDateTimeOffset());
        // 越过门槛之后又过了 2 分钟：已等 6 + 2 = 8 分钟。
        Assert.Equal(480, row.GetProperty("waitedSeconds").GetInt64());
        Assert.False(row.GetProperty("stationTimeoutDoorNotClosed").GetBoolean());
        JsonElement readings = row.GetProperty("readings");
        Assert.Equal("UNLOCKED", readings.GetProperty("lockState").GetString());
        Assert.Equal("OCCUPIED", readings.GetProperty("physicalState").GetString());
        Assert.Equal("RESET", readings.GetProperty("unlockOutputState").GetString());
        Assert.Equal(Now.AddSeconds(-5), readings.GetProperty("observedAt").GetDateTimeOffset());
        Assert.False(readings.GetProperty("changedSinceObserved").GetBoolean());
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-15")]
    public async Task WithTheStationDeadlinePassedTheTwoShowAsOneRowNotTwo()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await fixture.ReachReadyAsync();
        await fixture.AddJourneyAsync(
            JourneyRuntimeStage.AwaitingLoadResult, JourneyRuntimeEngine.StationTimeoutDoorNotClosedReason);
        await fixture.SendAlarmsAsync(1, Overdue(3, "关好3号仓门"));

        using JsonDocument fact = await fixture.ReadEndpointAsync();

        JsonElement row = Assert.Single(fact.RootElement.GetProperty("slots").EnumerateArray());
        Assert.Equal(3, row.GetProperty("slotNo").GetInt32());
        Assert.True(row.GetProperty("stationTimeoutDoorNotClosed").GetBoolean());
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-15")]
    public async Task AnUnloadAtTheGateIsListedAgainstTheGateStation()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await fixture.ReachReadyAsync();
        await fixture.AddJourneyAsync(JourneyRuntimeStage.AwaitingUnloadResult, blockReasonCode: null);
        await fixture.SendAlarmsAsync(1, Overdue(6, "取出货物并关好6号仓门"));

        using JsonDocument fact = await fixture.ReadEndpointAsync();

        JsonElement row = Assert.Single(fact.RootElement.GetProperty("slots").EnumerateArray());
        Assert.Equal("GATE-1", row.GetProperty("stationId").GetString());
        Assert.Equal("UNLOAD", row.GetProperty("operationType").GetString());
        Assert.False(row.GetProperty("stationTimeoutDoorNotClosed").GetBoolean());
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-15")]
    public async Task WhenTheVehicleWithdrawsTheAlarmTheRowDisappears()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await fixture.ReachReadyAsync();
        await fixture.AddJourneyAsync(
            JourneyRuntimeStage.AwaitingLoadResult, JourneyRuntimeEngine.StationTimeoutDoorNotClosedReason);
        await fixture.SendAlarmsAsync(1, Overdue(3, "关好3号仓门"));

        await fixture.SendAlarmsAsync(2, Alarm("ONBOARD_FLEET_CLOCK_SKEW", "VEHICLE", null));

        using JsonDocument fact = await fixture.ReadEndpointAsync();
        Assert.Empty(fact.RootElement.GetProperty("slots").EnumerateArray());
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-15")]
    public async Task ReadingsTheVehicleHasSinceSaidChangedAreMarkedAsSuchAndAnOverdueSlotWithoutReadingsSaysSo()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await fixture.ReachReadyAsync();
        await fixture.SendAlarmsAsync(1, Overdue(3, "关好3号仓门"), Overdue(4, "关好4号仓门", alarmId: "00000000-0000-4000-8000-00000000a004"));
        await fixture.SafetySnapshotAsync(2, Slots(), observedAt: Now.AddSeconds(-30));
        await fixture.SafetyChangedAsync(3, affectedSlots: [3]);

        using JsonDocument fact = await fixture.ReadEndpointAsync();

        Dictionary<int, JsonElement> bySlot = fact.RootElement.GetProperty("slots").EnumerateArray()
            .ToDictionary(row => row.GetProperty("slotNo").GetInt32());
        // 读数之后车说 3 号变了、新读数还没到：如实标出来，不把旧读数当成当下的（REQ-0269）。
        Assert.True(bySlot[3].GetProperty("readings").GetProperty("changedSinceObserved").GetBoolean());
        Assert.False(bySlot[4].GetProperty("readings").GetProperty("changedSinceObserved").GetBoolean());
        // 没有车载旅程的车，站点与操作类型说不出来就是 null，不猜。
        Assert.Equal(JsonValueKind.Null, bySlot[3].GetProperty("stationId").ValueKind);
        Assert.Equal(JsonValueKind.Null, bySlot[3].GetProperty("operationType").ValueKind);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-15")]
    public async Task AVehicleThatIsNotLinkedIsListedAsUnknownRatherThanShowingItsLastOverdueSlots()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await fixture.ReachReadyAsync();
        await fixture.SendAlarmsAsync(1, Overdue(3, "关好3号仓门"));
        fixture.Clock.Advance(OnboardAlarmProjectionStore.LinkLivenessTimeout + TimeSpan.FromSeconds(1));

        using JsonDocument fact = await fixture.ReadEndpointAsync();

        Assert.Empty(fact.RootElement.GetProperty("slots").EnumerateArray());
        JsonElement vehicle = Assert.Single(fact.RootElement.GetProperty("unavailableVehicles").EnumerateArray());
        Assert.Equal(AgvId, vehicle.GetProperty("agvId").GetString());
        Assert.Equal(VehicleAlarmProjection.LinkDownReason, vehicle.GetProperty("reason").GetString());
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-15")]
    public void TheThresholdComesFromItsOwnSettingsFileWhichShipsTheOnboardDefault()
    {
        Assert.Equal(TimeSpan.FromMinutes(6), ExpectedActionOverdueOptions.Default.Threshold);
        string root = RepositoryRoot();
        using JsonDocument file = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(root, "src", "ControlServer.Host", ExpectedActionOverdueOptions.FileName)));
        Assert.Equal(
            "00:06:00",
            file.RootElement.GetProperty(ExpectedActionOverdueOptions.SectionName).GetProperty("threshold").GetString());
        Assert.Throws<InvalidDataException>(() => ExpectedActionOverdueOptions.From(Section(("threshold", "00:00:00"))));
        Assert.Throws<InvalidDataException>(() => ExpectedActionOverdueOptions.From(Section(("threshold", "six minutes"))));
        Assert.Equal(
            TimeSpan.FromMinutes(8),
            ExpectedActionOverdueOptions.From(Section(("threshold", "00:08:00"))).Threshold);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-15")]
    public async Task TheEndpointAnswersAGetOverHttpAndTheCardRendersWhatItReturns()
    {
        // 集成用例：真起一个 Kestrel，经 DashboardQueryModule 挂上全部看板端点，用 HTTP GET 读这一个，再交给卡片渲染——
        // 与看板进程每 2 秒做的是同一件事。
        // 端点经默认构造挂上，用的是真时钟；车的消息也按真时钟收，否则这台车在端点眼里早已失联。
        await using Fixture fixture = await Fixture.CreateAsync(migrate: true, liveClock: true);
        await fixture.ReachReadyAsync();
        await fixture.AddJourneyAsync(
            JourneyRuntimeStage.AwaitingLoadResult, JourneyRuntimeEngine.StationTimeoutDoorNotClosedReason);
        await fixture.SendAlarmsAsync(1, Overdue(3, "关好3号仓门", raisedAt: DateTimeOffset.UtcNow.AddMinutes(-1)));
        await fixture.SafetySnapshotAsync(2, Slots(slot3Lock: "UNLOCKED"), observedAt: DateTimeOffset.UtcNow.AddSeconds(-3));

        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddDbContext<ControlServerDbContext>(options => options.UseSqlite(fixture.Connection));
        await using WebApplication app = builder.Build();
        app.MapDashboardQueries();
        await app.StartAsync(TestContext.Current.CancellationToken);
        string address = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using HttpClient client = new() { BaseAddress = new Uri(address) };
        // 存活窗口只有 6 秒，起 Kestrel 之后先让车说一句话，免得在慢机器上被判失联。
        await fixture.HeartbeatAsync();

        using HttpResponseMessage response = await client.GetAsync(
            new ExpectedActionOverdueCard().SourcePath, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        using JsonDocument fact = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        using HttpResponseMessage write = await client.PostAsJsonAsync(
            new ExpectedActionOverdueCard().SourcePath, new { }, TestContext.Current.CancellationToken);
        await app.StopAsync(TestContext.Current.CancellationToken);

        JsonElement row = Assert.Single(fact.RootElement.GetProperty("slots").EnumerateArray());
        Assert.Equal(3, row.GetProperty("slotNo").GetInt32());
        Assert.True(row.GetProperty("stationTimeoutDoorNotClosed").GetBoolean());
        // 端点用的是真时钟，已等时长只断言下限：至少是门槛加上越过门槛之后的那一分钟。
        Assert.True(row.GetProperty("waitedSeconds").GetInt64() >= 360);
        // 只读：同一路径上没有写入口。
        Assert.Equal(System.Net.HttpStatusCode.MethodNotAllowed, write.StatusCode);

        string html = new ExpectedActionOverdueCard().RenderFact(fact.RootElement);
        Assert.Contains("关好3号仓门", html, StringComparison.Ordinal);
    }

    // --- 卡片 ------------------------------------------------------------------------------------------------------

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-15")]
    public void TheCardRegistersItselfOnTheFleetViewAgainstTheEndpoint()
    {
        IDashboardCard card = Assert.Single(
            DashboardCardCatalog.Discovered.Cards, candidate => candidate is ExpectedActionOverdueCard);
        Assert.Equal(DashboardView.Fleet, card.View);
        Assert.Equal("期待动作超时", card.Title);
        IDashboardQueryEndpoint endpoint = Assert.Single(
            DashboardQueryEndpointCatalog.Discover(typeof(ExpectedActionOverdueQueryEndpoint).Assembly).Endpoints,
            candidate => candidate is ExpectedActionOverdueQueryEndpoint);
        Assert.Equal("/api/dashboard/expected-action-overdue", endpoint.Path);
        Assert.Equal(endpoint.Path, card.SourcePath);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-15")]
    public void TheCardShowsOneRowPerSlotWithTheStationDeadlineAndTheReadingsInThatRowAndNoForm()
    {
        using JsonDocument fact = JsonDocument.Parse("""
            {
              "thresholdSeconds": 360,
              "unavailableVehicles": [ { "agvId": "AGV-009", "reason": "车辆失联" } ],
              "slots": [
                {
                  "agvId": "AGV-001", "slotNo": 3, "stationId": "PICKUP-1", "operationType": "LOAD",
                  "expectedAction": "关好3号仓门", "raisedAt": "2026-09-18T07:58:00+00:00", "waitedSeconds": 480,
                  "stationTimeoutDoorNotClosed": true,
                  "readings": { "observedAt": "2026-09-18T07:59:55+00:00", "lockState": "UNLOCKED",
                                "physicalState": "OCCUPIED", "unlockOutputState": "RESET", "changedSinceObserved": true }
                },
                {
                  "agvId": "AGV-002", "slotNo": 6, "stationId": null, "operationType": null,
                  "expectedAction": "取出货物并关好6号仓门", "raisedAt": "2026-09-18T07:59:00+00:00", "waitedSeconds": 420,
                  "stationTimeoutDoorNotClosed": false, "readings": null
                }
              ]
            }
            """);

        string html = new ExpectedActionOverdueCard().RenderFact(fact.RootElement);
        string text = System.Net.WebUtility.HtmlDecode(html);

        Assert.Equal(3, CountOf(html, "<tr")); // 表头 + 两个仓，站点期限没有另起一行
        Assert.Contains("门槛 6 分钟", text, StringComparison.Ordinal);
        Assert.Contains("关好3号仓门", text, StringComparison.Ordinal);
        Assert.Contains("8 分钟", text, StringComparison.Ordinal);
        Assert.Contains("装货", text, StringComparison.Ordinal);
        Assert.Contains("站点期限已过，门未关（STATION_TIMEOUT_DOOR_NOT_CLOSED）", text, StringComparison.Ordinal);
        Assert.Contains("锁 UNLOCKED", text, StringComparison.Ordinal);
        Assert.Contains("光幕 OCCUPIED", text, StringComparison.Ordinal);
        Assert.Contains("开锁输出 RESET", text, StringComparison.Ordinal);
        Assert.Contains("读数之后车报告该仓有变化，新读数未到", text, StringComparison.Ordinal);
        Assert.Contains("尚无读数", text, StringComparison.Ordinal);
        Assert.Contains("不明", text, StringComparison.Ordinal);
        Assert.Contains("AGV-009：车辆失联", text, StringComparison.Ordinal);
        Assert.DoesNotContain("<form", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<input", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<button", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-15")]
    public void WithNothingOverdueTheCardSaysSo()
    {
        using JsonDocument fact = JsonDocument.Parse(
            """{ "thresholdSeconds": 360, "unavailableVehicles": [], "slots": [] }""");

        string html = new ExpectedActionOverdueCard().RenderFact(fact.RootElement);

        Assert.Contains("无期待动作超时的仓位", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<table", html, StringComparison.Ordinal);
    }

    // --- helpers ---------------------------------------------------------------------------------------------------

    private static int CountOf(string text, string value)
    {
        int count = 0;
        for (int index = text.IndexOf(value, StringComparison.Ordinal);
             index >= 0;
             index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
        {
            count++;
        }
        return count;
    }

    private static string[] Lines(string response) => response.Split('\n', StringSplitOptions.RemoveEmptyEntries);

    private static string MessageType(string line)
    {
        using JsonDocument document = JsonDocument.Parse(line);
        return document.RootElement.GetProperty("messageType").GetString()!;
    }

    private static string Readiness(string response)
    {
        using JsonDocument readiness = JsonDocument.Parse(Lines(response)[1]);
        return readiness.RootElement.GetProperty("payload").GetProperty("readiness").GetString()!;
    }

    private static object Overdue(
        int slot, string expectedAction, DateTimeOffset? raisedAt = null, string alarmId = OverdueAlarmId) => new
        {
            alarmId,
            code = OverdueCode,
            severity = "WARNING",
            raisedAt = raisedAt ?? Now.AddMinutes(-2),
            subjectType = "SLOT",
            subjectId = slot.ToString(System.Globalization.CultureInfo.InvariantCulture),
            displayMessage = expectedAction
        };

    private static object Alarm(string code, string subjectType, string? subjectId) => new
    {
        alarmId = "00000000-0000-4000-8000-00000000b001",
        code,
        severity = "WARNING",
        raisedAt = Now.AddMinutes(-10),
        subjectType,
        subjectId,
        displayMessage = (string?)null
    };

    private static object[] Slots(
        string slot3Lock = "LOCKED",
        string slot3Physical = "EMPTY",
        string slot3Output = "RESET") =>
        [.. Enumerable.Range(1, 8).Select(slot => new
        {
            slotNo = slot,
            operability = "OPERABLE",
            administrativeAvailability = "ENABLED",
            physicalState = slot == 3 ? slot3Physical : "EMPTY",
            lockState = slot == 3 ? slot3Lock : "LOCKED",
            unlockOutputState = slot == 3 ? slot3Output : "RESET",
            reasonCodes = Array.Empty<string>()
        })];

    private static object Safety(bool departureSafe) => new
    {
        departureSafe,
        vehicleStopped = true,
        allTargetSlotsLocked = departureSafe,
        allUnlockOutputsReset = true,
        unknownPresent = false,
        reasonCodes = departureSafe ? Array.Empty<string>() : new[] { "LOCK_NOT_CLOSED" }
    };

    private static IConfiguration Section(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(pair =>
                new KeyValuePair<string, string?>(pair.Key, pair.Value)))
            .Build();

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ControlServer.sln")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private sealed class MovableClock : TimeProvider
    {
        private readonly bool _live;
        private DateTimeOffset _now;

        public MovableClock(DateTimeOffset startAt, bool live)
        {
            _now = startAt;
            _live = live;
        }

        public override DateTimeOffset GetUtcNow() => _live ? DateTimeOffset.UtcNow : _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    /// <summary>
    /// 一台车经 <see cref="OnboardMessageProcessor"/> 走完真实握手的那一套：每条消息都是线上的一行，库由迁移或 EnsureCreated 建出。
    /// </summary>
    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(SqliteConnection connection, ControlServerDbContext context, bool liveClock)
        {
            Connection = connection;
            Context = context;
            Clock = new MovableClock(Now, liveClock);
            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["OnboardTransport:CredentialEnvironmentVariable"] = CredentialVariable
                })
                .Build();
            Processor = TestOnboardProcessorFactory.Create(context, new WireToGateStore(context), Clock, configuration);
        }

        public SqliteConnection Connection { get; }

        public ControlServerDbContext Context { get; }

        public MovableClock Clock { get; }

        public OnboardMessageProcessor Processor { get; }

        public OnboardConnectionState State { get; } = new();

        public static async Task<Fixture> CreateAsync(bool migrate = false, bool liveClock = false)
        {
            Environment.SetEnvironmentVariable(CredentialVariable, Credential);
            SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            ControlServerDbContext context = new(
                new DbContextOptionsBuilder<ControlServerDbContext>().UseSqlite(connection).Options);
            if (migrate)
            {
                await context.Database.MigrateAsync(TestContext.Current.CancellationToken);
            }
            else
            {
                await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            }
            return new Fixture(connection, context, liveClock);
        }

        public async Task ReachReadyAsync()
        {
            await HelloAsync();
            await CapabilityAsync();
            await SafetySnapshotAsync(1, Slots());
            string recovery = await RecoveryReportAsync();
            Assert.Equal("READY", Readiness(recovery));
        }

        public Task<string> HelloAsync() => Send(
            "SessionHello",
            null,
            new { protocolReleaseIdentity = ReleaseIdentity(), credentialProof = Credential });

        public Task<string> HeartbeatAsync() => Send("Heartbeat", State.SessionGeneration, new { });

        public Task<string> CapabilityAsync() => Send(
            "CapabilitySnapshot",
            State.SessionGeneration,
            new { capabilityVersion = 1, activeSlotConfigurationFingerprint = new string('0', 64) });

        public Task<string> SafetySnapshotAsync(
            long revision, object[] slotStates, bool departureSafe = true, DateTimeOffset? observedAt = null) => Send(
            "SafetyStateSnapshot",
            State.SessionGeneration,
            new
            {
                safetyStateVersion = revision,
                observedAt = observedAt ?? Clock.GetUtcNow(),
                safety = Safety(departureSafe),
                slotStates
            });

        public Task<string> SafetyChangedAsync(long revision, int[] affectedSlots, bool departureSafe = true) => Send(
            "SafetyStateChanged",
            State.SessionGeneration,
            new
            {
                safetyStateVersion = revision,
                observedAt = Clock.GetUtcNow(),
                safety = Safety(departureSafe),
                affectedSlots
            });

        public Task<string> RecoveryReportAsync() => Send(
            "RecoveryStateReport",
            State.SessionGeneration,
            new
            {
                reportId = Guid.NewGuid().ToString("D"),
                unsettledSlotOperationAttemptId = (string?)null,
                provenRecoveryCheckpoint = (string?)null,
                activeUnlockSlots = Array.Empty<int>(),
                forcedRecoveryGeneration = 0,
                pendingResults = Array.Empty<object>()
            });

        public Task<string> SendAlarmsAsync(long revision, params object[] alarms) => Send(
            "OnboardAlarmSnapshot",
            State.SessionGeneration,
            new { alarmSnapshotRevision = revision, observedAt = Clock.GetUtcNow(), alarms });

        public async Task AddJourneyAsync(JourneyRuntimeStage stage, string? blockReasonCode)
        {
            JourneyRuntimeRow runtime = Runtime("D-142", AgvId);
            runtime.Stage = stage;
            if (blockReasonCode is not null)
            {
                runtime.SetBlockReason(blockReasonCode, Clock.GetUtcNow().AddMinutes(-1));
            }
            Context.JourneyRuntimes.Add(runtime);
            await Context.SaveChangesAsync(TestContext.Current.CancellationToken);
            Context.ChangeTracker.Clear();
        }

        public async Task<SessionRecoveryRow> SessionAsync()
        {
            Context.ChangeTracker.Clear();
            return await Context.SessionRecoveries.AsNoTracking()
                .SingleAsync(row => row.AgvId == AgvId, TestContext.Current.CancellationToken);
        }

        public async Task<JsonDocument> ReadEndpointAsync()
        {
            await using ControlServerDbContext reading = new(
                new DbContextOptionsBuilder<ControlServerDbContext>().UseSqlite(Connection).Options);
            object result = await new ExpectedActionOverdueQueryEndpoint(ExpectedActionOverdueOptions.Default, Clock)
                .ReadAsync(reading, TestContext.Current.CancellationToken);
            Assert.Empty(reading.ChangeTracker.Entries());
            return JsonDocument.Parse(JsonSerializer.Serialize(result));
        }

        private Task<string> Send(string messageType, long? generation, object payload) =>
            Processor.ProcessAsync(
                JsonSerializer.Serialize(new
                {
                    protocolVersion = ProtocolCandidateIdentity.ProtocolVersion,
                    profileId = ProtocolCandidateIdentity.ProfileId,
                    protocolReleaseVersion = ProtocolCandidateIdentity.ReleaseVersion,
                    protocolReleaseManifestSha256 = ProtocolCandidateIdentity.ManifestSha256,
                    messageType,
                    messageId = Guid.NewGuid().ToString("D"),
                    correlationId = (string?)null,
                    agvId = AgvId,
                    sessionGeneration = generation,
                    sentAt = Clock.GetUtcNow(),
                    payload = JsonSerializer.SerializeToElement(payload, WireJson)
                }),
                State,
                TestContext.Current.CancellationToken);

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await Connection.DisposeAsync();
            Environment.SetEnvironmentVariable(CredentialVariable, null);
        }
    }

    private static object ReleaseIdentity() => new
    {
        repository = "8005-agv-protocol",
        releaseVersion = ProtocolCandidateIdentity.ReleaseVersion,
        tag = ProtocolCandidateIdentity.Tag,
        commit = ProtocolCandidateIdentity.RepositoryCommit,
        protocolVersion = ProtocolCandidateIdentity.ProtocolVersion,
        profileId = ProtocolCandidateIdentity.ProfileId,
        manifestSha256 = ProtocolCandidateIdentity.ManifestSha256,
        schemaBundleSha256 = ProtocolCandidateIdentity.SchemaBundleSha256,
        vectorsSha256 = ProtocolCandidateIdentity.VectorsSha256
    };

    private static JourneyRuntimeRow Runtime(string demandId, string agvId) => new()
    {
        DemandId = demandId,
        Stage = JourneyRuntimeStage.AwaitingSublot,
        AgvId = agvId,
        VehicleKey = "KEY-" + agvId,
        AgvLifecycleGeneration = 1,
        MapId = 25,
        MapIdentity = "map-25",
        DispatchZone = "MAP-25-WIRE_TO_GATE",
        RouteEvidenceId = "route-" + demandId,
        PickupStationId = "PICKUP-1",
        PickupStationRiotId = 11,
        GateStationId = "GATE-1",
        GateStationRiotId = 22,
        ExpectedBasketCount = 2,
        TargetSlotsJson = "[3]",
        OperationSessionId = "session-" + demandId,
        PickupMovementLegId = "pickup-leg-" + demandId,
        PickupUpperId = "UPPER-PICKUP-" + demandId,
        GateMovementLegId = "gate-leg-" + demandId,
        GateUpperId = "UPPER-GATE-" + demandId,
        DispatchGeneration = 1,
        VehicleBusinessRevision = 1,
        WorklistRevision = 1,
        PlanRevision = 1,
        VehicleBusinessMessageId = "vb-" + demandId,
        WorklistMessageId = "wl-" + demandId,
        PlanMessageId = "plan-" + demandId,
        SublotRequestMessageId = "sublot-" + demandId,
        LoadCommandMessageId = "load-" + demandId,
        LoadSlotOperationAttemptId = "load-attempt-" + demandId,
        PreDepartureSafetyCheckMessageId = "check-message-" + demandId,
        PreDepartureSafetyCheckId = "check-" + demandId,
        GateVehicleBusinessMessageId = "gate-vb-" + demandId,
        GateWorklistMessageId = "gate-wl-" + demandId,
        GatePlanMessageId = "gate-plan-" + demandId,
        UnloadCommandMessageId = "unload-" + demandId,
        UnloadSlotOperationAttemptId = "unload-attempt-" + demandId,
        CreatedAt = Now.AddHours(-1),
        UpdatedAt = Now
    };
}
