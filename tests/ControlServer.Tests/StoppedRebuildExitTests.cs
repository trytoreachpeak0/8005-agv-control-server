using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Commands;
using ControlServer.Host.Runtime.Faults;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using static ControlServer.Tests.Batch7StopDrivenAdvanceDriver;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;

namespace ControlServer.Tests;

/// <summary>
/// 自动重建停住之后的人工出口（control-server#345）：护栏三停住（<c>OWN_ORDER_REBUILD_STOPPED</c>）时人工重建一次，
/// 走 #299 的同一个入口、同一套署名与审计。
/// </summary>
/// <remarks>
/// <para>
/// <b>出口形态是调度 Coordinator 8 于 2026-09-23 确认的</b>（本票第一步的提案与答复）：甲（人工重建）做；丙（释放改派）不做；
/// 乙（放弃这趟、终止需求）等用户答复，本类不含它。人工重建之后 REQ-0361 的窗口从人工请求那一刻重新算——人工重建之后再出问题，
/// 照样停住等人。
/// </para>
/// <para>
/// 人工重建不绕过 #318 的任何一道建单前检查：车况、建单门禁、车载端会话就绪与离站判定、有货来源的新快照证据，全都照走引擎那条路。
/// 它只替人做一件事：把「停住」换成「现在就重建一次」。
/// </para>
/// <para>码写成字面量：它们是现场拿到的东西，改名应当让这里红。</para>
/// </remarks>
public sealed class StoppedRebuildExitTests
{
    private const string OperatorId = "L1-OPERATOR-345";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ---- 甲：人工重建 ----------------------------------------------------------------------------------------------

    /// <summary>
    /// 护栏三停住的旅程（重建出来的单在窗口内又被取消）：人工重建——停住的那条记录转 <c>ENDED</c>、停住原因原样留着，另落一条
    /// 由这个人发起的新记录；请求本身不向 RIoT 发任何东西，也不释放、不改派；引擎下一轮给同一辆车、同一条需求建第三张单，
    /// 停靠指向它，旅程码清空。每次调用记告警（事件 9203，写明动作与人）。
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-0361")]
    public async Task AStoppedRebuildIsRebuiltOnceOnAPersonsRequest()
    {
        await using RuntimeFixture fixture = await StoppedByTheThirdGuardAsync();
        JourneyRuntimeRow stopped = await fixture.RuntimeAsync();
        JourneyStopRow stop = await CurrentStopAsync(fixture, FirstDemandId);
        VehicleFaultRecoveryTests.SiteRiot site = new(fixture);
        RecordingLogger<VehicleFaultRecoveryService> log = new();

        VehicleFaultRecoveryDecision decision = await VehicleFaultRecoveryTests.Service(fixture, site, logger: log)
            .RecoverAsync(Rebuild(fixture), Token);
        DateTimeOffset requestedAt = fixture.Clock.GetUtcNow();

        Assert.Equal(
            (VehicleFaultRecoveryOutcome.RebuildRequested, VehicleFaultRecoveryDispositions.RebuildScheduled),
            (decision.Outcome, decision.Disposition));
        Assert.Empty(decision.Reasons);
        Assert.Empty(site.OrderCommands);
        Assert.Empty(site.EmergencyCommands);
        await using (ControlServerDbContext reading = new(fixture.DbOptionsForTests))
        {
            // One RIoT order has one record (unique EndedUpperId): the stopped record is reopened in place, keeping why and
            // when it stopped, now naming the person who asked.
            OwnOrderRebuildRow reopened = await reading.OwnOrderRebuilds.AsNoTracking()
                .SingleAsync(row => row.EndedUpperId == stop.UpperId, Token);
            Assert.Equal(
                (OwnOrderRebuildSources.CancelledInRiot, OwnOrderRebuildStates.Pending, OperatorId, stop.StopId),
                (reopened.Source, reopened.State, reopened.OperatorId, reopened.StopId));
            Assert.Equal("REBUILT_ORDER_ENDED_AGAIN_WITHIN_WINDOW", reopened.StoppedReason);
            Assert.NotNull(reopened.StoppedAt);
            Assert.True(reopened.StoppedAt < requestedAt, "the clock did not move between the stop and the request");
            Assert.Equal((requestedAt, requestedAt, requestedAt), (reopened.IncidentAt, reopened.RecordedAt, reopened.DueAt));
            JourneyRuntimeRow kept = await reading.JourneyRuntimes.AsNoTracking().SingleAsync(Token);
            Assert.Equal((stopped.JourneyId, JourneyRuntimeStage.AwaitingPickupArrival), (kept.JourneyId, kept.Stage));
            Assert.Null((await reading.Set<JourneyDemandRow>().AsNoTracking().SingleAsync(Token)).RemovedAt);
        }

        Assert.Single(log.Entries, entry =>
            entry.Level == Microsoft.Extensions.Logging.LogLevel.Warning &&
            entry.Message.Contains("RebuildStoppedOrder", StringComparison.Ordinal) &&
            entry.Message.Contains(OperatorId, StringComparison.Ordinal) &&
            entry.Message.Contains("RebuildRequested", StringComparison.Ordinal));

        fixture.Context.ChangeTracker.Clear();
        await fixture.HearFromPeerAsync();
        await fixture.Engine.ExecuteOnceAsync(Token);

        Assert.Equal(3, fixture.Riot.CreateCount("TO_PICKUP"));
        await using ControlServerDbContext after = new(fixture.DbOptionsForTests);
        OwnOrderRebuildRow rebuilt = await after.OwnOrderRebuilds.AsNoTracking()
            .SingleAsync(row => row.EndedUpperId == stop.UpperId && row.OperatorId == OperatorId, Token);
        Assert.Equal(OwnOrderRebuildStates.Rebuilt, rebuilt.State);
        JourneyStopRow repointed = await after.Set<JourneyStopRow>().AsNoTracking().SingleAsync(row => row.StopId == stop.StopId, Token);
        Assert.Equal(rebuilt.NewUpperId, repointed.UpperId);
        OrderIntentRow intent = await after.OrderIntents.AsNoTracking().SingleAsync(row => row.UpperId == rebuilt.NewUpperId, Token);
        Assert.Equal(
            (stopped.DemandId, stopped.VehicleKey, stop.StationRiotId, "CONFIRMED"),
            (intent.DemandId, intent.VehicleKey, intent.DestinationStationId, intent.Status));
        JourneyRuntimeRow moving = await after.JourneyRuntimes.AsNoTracking().SingleAsync(Token);
        Assert.Equal(
            (stopped.JourneyId, JourneyRuntimeStage.AwaitingPickupArrival, (string?)null),
            (moving.JourneyId, moving.Stage, moving.BlockReasonCode));
    }

    /// <summary>
    /// 只对护栏三停住的旅程生效：重建还在等延迟（引擎会自己建）、旅程正常在途（根本没有重建）、重建因车不再合格而停（交释放服务改派）、
    /// 重建因货不在原仓而停（按 REQ-0238 只能交接，见交接那一组）——一律拒绝，说出原因，什么都不改：记录、旅程码、单数原样。
    /// </summary>
    /// <remarks>
    /// 反向验证：去掉「只对停住的旅程生效」的判据，这一组全红（PR 正文记录）。
    /// </remarks>
    [Theory]
    [InlineData("rebuild-waiting", "OWN_ORDER_REBUILD_EXIT_NOT_STOPPED")]
    [InlineData("under-way", "OWN_ORDER_REBUILD_EXIT_NOT_STOPPED")]
    [InlineData("vehicle-ineligible", "OWN_ORDER_REBUILD_EXIT_VEHICLE_INELIGIBLE")]
    [InlineData("cargo-not-in-place", "OWN_ORDER_REBUILD_EXIT_CARGO_NOT_IN_PLACE")]
    public async Task ARequestForAJourneyNotStoppedByTheThirdGuardIsRefusedAndChangesNothing(string state, string refusal)
    {
        await using RuntimeFixture fixture = await (state switch
        {
            "rebuild-waiting" => RebuildWaitingForTheDelayAsync(),
            "under-way" => DispatchedToPickupAsync(),
            "vehicle-ineligible" => StoppedAsIneligibleAsync(),
            _ => StoppedWithCargoNotInPlaceAsync(),
        });
        Snapshot before = await SnapshotAsync(fixture);
        VehicleFaultRecoveryTests.SiteRiot site = new(fixture);

        VehicleFaultRecoveryDecision decision = await VehicleFaultRecoveryTests.Service(fixture, site)
            .RecoverAsync(Rebuild(fixture), Token);

        Assert.Equal(VehicleFaultRecoveryOutcome.Refused, decision.Outcome);
        Assert.Equal([refusal], decision.Reasons);
        Assert.Empty(site.OrderCommands);
        Assert.Equal(before, await SnapshotAsync(fixture));
    }

    /// <summary>
    /// 与 #299 同样的两件人要给的东西：没署名、没确认已查明原因——停住的旅程也拒绝，两条原因都列出，什么都不改。
    /// </summary>
    [Fact]
    public async Task AnUnnamedOrUnconfirmedRequestIsRefusedForBoth()
    {
        await using RuntimeFixture fixture = await StoppedByTheThirdGuardAsync();
        Snapshot before = await SnapshotAsync(fixture);

        VehicleFaultRecoveryDecision decision = await VehicleFaultRecoveryTests.Service(fixture, new(fixture))
            .RecoverAsync(Rebuild(fixture) with { OperatorId = " ", FaultRemedied = false }, Token);

        Assert.Equal(VehicleFaultRecoveryOutcome.Refused, decision.Outcome);
        Assert.Equal(["FAULT_RECOVERY_OPERATOR_UNIDENTIFIED", "FAULT_RECOVERY_REMEDY_NOT_CONFIRMED"], decision.Reasons);
        Assert.Equal(before, await SnapshotAsync(fixture));
    }

    /// <summary>
    /// 同一个请求到达两次（HTTP 重试、两个人前后点了一下）：第二次答 <c>AlreadyDone</c>，什么都不改；新单建成之后再来一次，仍是
    /// <c>AlreadyDone</c>。前后只多建了一张单。
    /// </summary>
    [Fact]
    public async Task TheSameRequestTwiceRebuildsOnce()
    {
        await using RuntimeFixture fixture = await StoppedByTheThirdGuardAsync();
        Assert.Equal(
            VehicleFaultRecoveryOutcome.RebuildRequested,
            (await VehicleFaultRecoveryTests.Service(fixture, new(fixture)).RecoverAsync(Rebuild(fixture), Token)).Outcome);
        Snapshot afterFirst = await SnapshotAsync(fixture);
        fixture.Clock.Advance(TimeSpan.FromSeconds(2));

        VehicleFaultRecoveryDecision again = await VehicleFaultRecoveryTests.Service(fixture, new(fixture))
            .RecoverAsync(Rebuild(fixture), Token);

        Assert.Equal((VehicleFaultRecoveryOutcome.AlreadyDone, 0), (again.Outcome, again.Reasons.Count));
        Assert.Equal(afterFirst, await SnapshotAsync(fixture));

        fixture.Context.ChangeTracker.Clear();
        await fixture.HearFromPeerAsync();
        await fixture.Engine.ExecuteOnceAsync(Token);
        Assert.Equal(3, fixture.Riot.CreateCount("TO_PICKUP"));

        VehicleFaultRecoveryDecision third = await VehicleFaultRecoveryTests.Service(fixture, new(fixture))
            .RecoverAsync(Rebuild(fixture), Token);
        fixture.Context.ChangeTracker.Clear();
        await fixture.HearFromPeerAsync();
        await fixture.Engine.ExecuteOnceAsync(Token);

        Assert.Equal(VehicleFaultRecoveryOutcome.AlreadyDone, third.Outcome);
        Assert.Equal(3, fixture.Riot.CreateCount("TO_PICKUP"));
    }

    // ---- 夹具 ----------------------------------------------------------------------------------------------

    /// <summary>「什么都没改」比的东西：每条重建记录的状态、署名与时刻，旅程的阶段与码，建过几张取货单、卸货单。</summary>
    private sealed record Snapshot(string Rebuilds, JourneyRuntimeStage Stage, string? Code, int PickupCreates, int GateCreates);

    private static async Task<Snapshot> SnapshotAsync(RuntimeFixture fixture)
    {
        await using ControlServerDbContext reading = new(fixture.DbOptionsForTests);
        OwnOrderRebuildRow[] rows = await reading.OwnOrderRebuilds.AsNoTracking().ToArrayAsync(Token);
        JourneyRuntimeRow journey = await reading.JourneyRuntimes.AsNoTracking().SingleAsync(Token);
        return new Snapshot(
            string.Join(
                ';',
                rows.OrderBy(row => row.RebuildId, StringComparer.Ordinal).Select(row =>
                    $"{row.RebuildId}|{row.State}|{row.StoppedReason}|{row.OperatorId}|{row.IncidentAt:O}|{row.DueAt:O}")),
            journey.Stage,
            journey.BlockReasonCode,
            fixture.Riot.CreateCount("TO_PICKUP"),
            fixture.Riot.CreateCount("TO_GATE"));
    }

    private static async Task<RuntimeFixture> DispatchedToPickupAsync()
    {
        RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(FirstDemandId, FirstSublot, Now.AddMinutes(-10)));
        fixture.BoxCounts.Set(FirstSublot, 7);
        await TickAndRunAsync(fixture);
        Assert.Equal(JourneyRuntimeStage.AwaitingPickupArrival, (await fixture.RuntimeAsync()).Stage);
        fixture.Riot.MovementState = "MT_FINISHED";
        fixture.Context.ChangeTracker.Clear();
        return fixture;
    }

    /// <summary>单被取消、重建记下了，还在等延迟：记录 PENDING，旅程码是过渡码。</summary>
    private static async Task<RuntimeFixture> RebuildWaitingForTheDelayAsync()
    {
        RuntimeFixture fixture = await DispatchedToPickupAsync();
        fixture.Riot.CancelOrder((await fixture.RuntimeAsync()).PickupUpperId);
        await TickAndRunAsync(fixture);
        Assert.Equal("ORDER_ENDED_WITHOUT_ARRIVAL", (await fixture.RuntimeAsync()).BlockReasonCode);
        fixture.Context.ChangeTracker.Clear();
        return fixture;
    }

    /// <summary>
    /// 等重建期间车对这条需求不合格了（任务类型准入被收回，照 <c>Batch7DemandReleaseServiceTests</c> 的造法）：记录停在
    /// <c>VEHICLE_NO_LONGER_ELIGIBLE</c>，旅程码 <c>OWN_ORDER_REBUILD_VEHICLE_INELIGIBLE</c>。
    /// </summary>
    private static async Task<RuntimeFixture> StoppedAsIneligibleAsync()
    {
        RuntimeFixture fixture = await RebuildWaitingForTheDelayAsync();
        fixture.Options.AllowedWorkTypes = ["STAGING_TO_WIRE"];
        await OwnOrderRebuildTests.PassTheDelayAsync(fixture);
        Assert.Equal("OWN_ORDER_REBUILD_VEHICLE_INELIGIBLE", (await fixture.RuntimeAsync()).BlockReasonCode);
        fixture.Context.ChangeTracker.Clear();
        return fixture;
    }

    /// <summary>
    /// 去卸货站途中单 FAILED、人清除故障（车上有货），清除之后车报的快照读到目标仓 EMPTY：记录停在
    /// <c>CARGO_NOT_PROVEN_IN_ORIGINAL_SLOTS</c>，旅程码 <c>OWN_ORDER_REBUILD_CARGO_NOT_IN_PLACE</c>。
    /// </summary>
    internal static async Task<RuntimeFixture> StoppedWithCargoNotInPlaceAsync()
    {
        RuntimeFixture fixture = await VehicleFaultRecoveryTests.FaultedOnTheWayToGateAsync();
        Assert.Equal(
            VehicleFaultRecoveryDispositions.RebuildScheduled,
            (await VehicleFaultRecoveryTests.Service(fixture, new(fixture))
                .RecoverAsync(VehicleFaultRecoveryTests.Clear(fixture), Token)).Disposition);
        fixture.Context.ChangeTracker.Clear();
        fixture.Clock.Advance(TimeSpan.FromSeconds(5));
        await fixture.AddCargoSnapshotAsync(fixture.Clock.GetUtcNow(), physicalState: "EMPTY");
        await OwnOrderRebuildTests.PassTheDelayAsync(fixture);
        Assert.Equal("OWN_ORDER_REBUILD_CARGO_NOT_IN_PLACE", (await fixture.RuntimeAsync()).BlockReasonCode);
        fixture.Context.ChangeTracker.Clear();
        return fixture;
    }

    internal static VehicleFaultRecoveryRequest Rebuild(RuntimeFixture fixture) => new(
        new EmergencyStopSubject(fixture.Options.AgvId, fixture.Options.VehicleKey),
        VehicleFaultRecoveryAction.RebuildStoppedOrder,
        OperatorId,
        FaultRemedied: true,
        Note: "L1");

    /// <summary>
    /// 护栏三在取消来源上停住：开往取货站的单被取消、延迟后重建；重建出来的单在窗口内又被取消——不再重建，
    /// 旅程码 <c>OWN_ORDER_REBUILD_STOPPED</c>，前后建过两张取货单。
    /// </summary>
    internal static async Task<RuntimeFixture> StoppedByTheThirdGuardAsync()
    {
        RuntimeFixture fixture = await DispatchedToPickupAsync();
        fixture.Riot.CancelOrder((await fixture.RuntimeAsync()).PickupUpperId);
        await TickAndRunAsync(fixture);
        await OwnOrderRebuildTests.PassTheDelayAsync(fixture);
        Assert.Equal(2, fixture.Riot.CreateCount("TO_PICKUP"));

        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        await fixture.HearFromPeerAsync();
        fixture.Riot.CancelOrder((await CurrentStopAsync(fixture, FirstDemandId)).UpperId);
        await fixture.Engine.ExecuteOnceAsync(Token);
        await OwnOrderRebuildTests.PassTheDelayAsync(fixture);

        Assert.Equal(2, fixture.Riot.CreateCount("TO_PICKUP"));
        Assert.Equal("OWN_ORDER_REBUILD_STOPPED", (await fixture.RuntimeAsync()).BlockReasonCode);
        fixture.Context.ChangeTracker.Clear();
        return fixture;
    }
}
