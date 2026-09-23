using ControlServer.Application;
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
    /// <remarks>
    /// 原行重开之后记录已经不是 <c>STOPPED</c>，所以第二次请求必须：答 <c>AlreadyDone</c> 而不是「不在停住状态」的拒绝；不再改写
    /// 出问题时刻、到期时刻与署名（比较的快照里都有）；新单已经发出之后不被重置回 <c>PENDING</c>（调度 2026-09-23 的补充要求）。
    /// 反向验证：去掉幂等判断，这一条红在第二次请求的结果上（PR 正文记录）。重开后又被护栏三停住、再来一次请求要当新的一次处理，
    /// 见 <see cref="AfterAPersonsRebuildTheWindowRunsFromTheRequest"/> 的第二次请求。
    /// </remarks>
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
        Snapshot afterRebuilt = await SnapshotAsync(fixture);
        Assert.Contains($"|{OwnOrderRebuildStates.Rebuilt}|", afterRebuilt.Rebuilds, StringComparison.Ordinal);
        fixture.Clock.Advance(TimeSpan.FromSeconds(2));

        VehicleFaultRecoveryDecision third = await VehicleFaultRecoveryTests.Service(fixture, new(fixture))
            .RecoverAsync(Rebuild(fixture), Token);

        // Not put back to PENDING, and neither the time nor the person rewritten, once the new order is out.
        Assert.Equal((VehicleFaultRecoveryOutcome.AlreadyDone, 0), (third.Outcome, third.Reasons.Count));
        Assert.Equal(afterRebuilt, await SnapshotAsync(fixture));
        fixture.Context.ChangeTracker.Clear();
        await fixture.HearFromPeerAsync();
        await fixture.Engine.ExecuteOnceAsync(Token);
        Assert.Equal(3, fixture.Riot.CreateCount("TO_PICKUP"));
    }

    /// <summary>
    /// 人工重建之后 REQ-0361 的窗口从人工请求那一刻重新算（调度 2026-09-23 定）：请求时离第一次出问题已经过了窗口，人工重建的单
    /// 2 分钟后又被取消——照样停住等人（从请求算在窗口内）；再人工重建一次，这次过了窗口才被取消——照常自动重建。
    /// </summary>
    /// <remarks>
    /// 前半段区分「重新算」与「不重新算」：先断言这次取消离停住那次出问题已过窗口（不重新算就会自动重建），离请求在窗口内。
    /// 理由写进 PR：人工重建之后再出问题照样停住等人，更保守。
    /// </remarks>
    [Fact]
    [Trait("Requirement", "REQ-0361")]
    public async Task AfterAPersonsRebuildTheWindowRunsFromTheRequest()
    {
        await using RuntimeFixture fixture = await StoppedByTheThirdGuardAsync();
        TimeSpan window = fixture.Options.OwnOrderRebuildRepeatWindow;
        JourneyStopRow stop = await CurrentStopAsync(fixture, FirstDemandId);
        DateTimeOffset stoppedIncident = (await RebuildForAsync(fixture, stop.UpperId)).IncidentAt;
        fixture.Clock.Advance(window);
        DateTimeOffset requestedAt = fixture.Clock.GetUtcNow();
        await RequestAndRunAsync(fixture);
        Assert.Equal(3, fixture.Riot.CreateCount("TO_PICKUP"));

        fixture.Clock.Advance(TimeSpan.FromMinutes(2));
        string manualOrder = (await CurrentStopAsync(fixture, FirstDemandId)).UpperId;
        await fixture.HearFromPeerAsync();
        fixture.Riot.CancelOrder(manualOrder);
        await fixture.Engine.ExecuteOnceAsync(Token);
        await OwnOrderRebuildTests.PassTheDelayAsync(fixture);

        OwnOrderRebuildRow again = await RebuildForAsync(fixture, manualOrder);
        Assert.True(again.IncidentAt - stoppedIncident > window, "the ending must fall outside the window from the stopped incident");
        Assert.True(again.IncidentAt - requestedAt < window, "the ending must fall inside the window from the request");
        Assert.Equal(
            (OwnOrderRebuildStates.Stopped, "REBUILT_ORDER_ENDED_AGAIN_WITHIN_WINDOW"),
            (again.State, again.StoppedReason));
        Assert.Equal("OWN_ORDER_REBUILD_STOPPED", (await fixture.RuntimeAsync()).BlockReasonCode);
        Assert.Equal(3, fixture.Riot.CreateCount("TO_PICKUP"));

        await RequestAndRunAsync(fixture);
        Assert.Equal(4, fixture.Riot.CreateCount("TO_PICKUP"));
        fixture.Clock.Advance(window + TimeSpan.FromMinutes(1));
        string secondManualOrder = (await CurrentStopAsync(fixture, FirstDemandId)).UpperId;
        await fixture.HearFromPeerAsync();
        fixture.Riot.CancelOrder(secondManualOrder);
        await fixture.Engine.ExecuteOnceAsync(Token);
        await OwnOrderRebuildTests.PassTheDelayAsync(fixture);

        Assert.Equal(OwnOrderRebuildStates.Rebuilt, (await RebuildForAsync(fixture, secondManualOrder)).State);
        Assert.Equal(5, fixture.Riot.CreateCount("TO_PICKUP"));
        Assert.Null((await fixture.RuntimeAsync()).BlockReasonCode);
    }

    /// <summary>
    /// 真车载端的常态：车带着本服务端的单时会话整段未就绪（形状照 <c>PickupDispatchPlanPastOwnOrderTests.DropSessionOnOwnOrderAsync</c>）。
    /// 人工重建照样受理、照样记下，但不在闸门后面建单——记录写明在等会话，旅程码 <c>OWN_ORDER_REBUILD_WAITING_VEHICLE</c>；会话回到就绪
    /// 之后才建，只建一张。与 #318 自动重建的那一格一致。
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-0360")]
    public async Task APersonsRebuildWhileTheSessionIsNotReadyOnItsOwnOrderWaitsForTheSession()
    {
        await using RuntimeFixture fixture = await StoppedByTheThirdGuardAsync();
        await OwnOrderRebuildTests.DropSessionOnOwnOrderAsync(fixture);
        JourneyStopRow stop = await CurrentStopAsync(fixture, FirstDemandId);

        VehicleFaultRecoveryDecision decision = await VehicleFaultRecoveryTests.Service(fixture, new(fixture))
            .RecoverAsync(Rebuild(fixture), Token);
        fixture.Context.ChangeTracker.Clear();
        await TickAndRunAsync(fixture);
        await TickAndRunAsync(fixture);

        Assert.Equal(VehicleFaultRecoveryOutcome.RebuildRequested, decision.Outcome);
        Assert.Equal(2, fixture.Riot.CreateCount("TO_PICKUP"));
        OwnOrderRebuildRow waiting = await RebuildForAsync(fixture, stop.UpperId);
        Assert.Equal((OwnOrderRebuildStates.Pending, "ONBOARD_SESSION_NOT_READY"), (waiting.State, waiting.WaitingReason));
        Assert.Equal("OWN_ORDER_REBUILD_WAITING_VEHICLE", (await fixture.RuntimeAsync()).BlockReasonCode);
        Assert.Equal(SessionReadiness.RecoveryRequired, (await fixture.Context.SessionRecoveries.AsNoTracking().SingleAsync(Token)).Readiness);

        fixture.Context.ChangeTracker.Clear();
        await fixture.RestoreSessionReadyAsync();
        await fixture.HearFromPeerAsync();
        await TickAndRunAsync(fixture);
        await TickAndRunAsync(fixture);

        Assert.Equal(3, fixture.Riot.CreateCount("TO_PICKUP"));
        Assert.Equal(OwnOrderRebuildStates.Rebuilt, (await RebuildForAsync(fixture, stop.UpperId)).State);
    }

    /// <summary>
    /// 故障来源、车上有货的护栏三停住（清除后重建出来的去卸货站的单，窗口内又 FAILED、人又清除一次）：人工重建之后，照 REQ-0362 仍要
    /// 一份<b>请求之后</b>收到的、显示货完整在原仓的快照——请求之前那份不算，旅程等在 <c>OWN_ORDER_REBUILD_WAITING_CARGO_EVIDENCE</c>；
    /// 新快照到了才建去卸货站的单。
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-0362")]
    public async Task APersonsRebuildWithCargoOnBoardStillNeedsAFreshSnapshotOfTheCargo()
    {
        await using RuntimeFixture fixture = await VehicleFaultRecoveryTests.FaultedOnTheWayToGateAsync();
        VehicleFaultRecoveryTests.SiteRiot site = new(fixture);
        await VehicleFaultRecoveryTests.Service(fixture, site).RecoverAsync(VehicleFaultRecoveryTests.Clear(fixture), Token);
        fixture.Context.ChangeTracker.Clear();
        await fixture.AddCargoSnapshotAsync(fixture.Clock.GetUtcNow() + TimeSpan.FromSeconds(1));
        await OwnOrderRebuildTests.PassTheDelayAsync(fixture);
        int gateCreates = fixture.Riot.CreateCount("TO_GATE");
        string rebuiltOrder = (await CurrentStopAsync(fixture, FirstDemandId)).UpperId;
        Assert.Equal(OwnOrderRebuildStates.Rebuilt, (await RebuildForAsync(fixture, (await fixture.RuntimeAsync()).GateUpperId)).State);

        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        await fixture.HearFromPeerAsync();
        fixture.Riot.FailOrder(rebuiltOrder);
        await fixture.Engine.ExecuteOnceAsync(Token);
        Assert.Equal(
            VehicleFaultRecoveryDispositions.RebuildStopped,
            (await VehicleFaultRecoveryTests.Service(fixture, site).RecoverAsync(VehicleFaultRecoveryTests.Clear(fixture), Token)).Disposition);
        fixture.Context.ChangeTracker.Clear();
        await fixture.AddCargoSnapshotAsync(fixture.Clock.GetUtcNow() + TimeSpan.FromSeconds(1));
        fixture.Clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(
            (OwnOrderRebuildStates.Stopped, OwnOrderRebuildSources.FaultClearedCargoOnBoard),
            ((await RebuildForAsync(fixture, rebuiltOrder)).State, (await RebuildForAsync(fixture, rebuiltOrder)).Source));

        Assert.Equal(
            VehicleFaultRecoveryOutcome.RebuildRequested,
            (await VehicleFaultRecoveryTests.Service(fixture, site).RecoverAsync(Rebuild(fixture), Token)).Outcome);
        fixture.Context.ChangeTracker.Clear();
        await OwnOrderRebuildTests.PassTheDelayAsync(fixture);
        await OwnOrderRebuildTests.PassTheDelayAsync(fixture);

        Assert.Equal(gateCreates, fixture.Riot.CreateCount("TO_GATE"));
        Assert.Equal("OWN_ORDER_REBUILD_WAITING_CARGO_EVIDENCE", (await fixture.RuntimeAsync()).BlockReasonCode);

        await fixture.AddCargoSnapshotAsync(fixture.Clock.GetUtcNow());
        await fixture.HearFromPeerAsync();
        await fixture.Engine.ExecuteOnceAsync(Token);

        Assert.Equal(gateCreates + 1, fixture.Riot.CreateCount("TO_GATE"));
        Assert.Equal(OwnOrderRebuildStates.Rebuilt, (await RebuildForAsync(fixture, rebuiltOrder)).State);
    }

    /// <summary>
    /// 另一种护栏三停住：重建出来的新单确认前就被 RIoT 报成状态 8（SUSPENDED），停靠此时指着那张新单。人工重建另落一条接手记录
    /// （以那张新单为终结单、署名、立即到期），停住那条转 <c>ENDED</c>、停住原因原样；引擎下一轮建第三张单。
    /// </summary>
    /// <remarks>
    /// 两种形状的另一半：<see cref="AStoppedRebuildIsRebuiltOnceOnAPersonsRequest"/> 是原行重开。这一格不能原行重开——停住那条的新单号
    /// 已经用掉了，停靠指着的也不是它的终结单。
    /// </remarks>
    [Fact]
    public async Task ARebuildStoppedOnItsSuspendedNewOrderIsTakenOverByAPersonsRecord()
    {
        await using RuntimeFixture fixture = await DispatchedToPickupAsync();
        JourneyRuntimeRow before = await fixture.RuntimeAsync();
        fixture.Riot.CancelOrder(before.PickupUpperId);
        await TickAndRunAsync(fixture);
        fixture.Riot.LoseNextCreateResponse = true;
        await OwnOrderRebuildTests.PassTheDelayAsync(fixture);
        OwnOrderRebuildRow ordering = await RebuildForAsync(fixture, before.PickupUpperId);
        fixture.Riot.SetOrderState(ordering.NewUpperId, RiotOrderState.Suspended, terminal: true);
        await fixture.HearFromPeerAsync();
        await TickAndRunAsync(fixture);
        Assert.Equal("OWN_ORDER_REBUILD_STOPPED", (await fixture.RuntimeAsync()).BlockReasonCode);
        Assert.Equal(ordering.NewUpperId, (await CurrentStopAsync(fixture, FirstDemandId)).UpperId);
        fixture.Context.ChangeTracker.Clear();

        VehicleFaultRecoveryDecision decision = await VehicleFaultRecoveryTests.Service(fixture, new(fixture))
            .RecoverAsync(Rebuild(fixture), Token);
        DateTimeOffset requestedAt = fixture.Clock.GetUtcNow();

        Assert.Equal(VehicleFaultRecoveryOutcome.RebuildRequested, decision.Outcome);
        OwnOrderRebuildRow ended = await RebuildForAsync(fixture, before.PickupUpperId);
        Assert.Equal(
            (OwnOrderRebuildStates.Ended, "REBUILT_ORDER_ENDED_BEFORE_CONFIRMATION"),
            (ended.State, ended.StoppedReason));
        OwnOrderRebuildRow takenOver = await RebuildForAsync(fixture, ordering.NewUpperId);
        Assert.Equal(
            (OwnOrderRebuilds.ManualRebuildIdFor(ended.RebuildId), OwnOrderRebuildStates.Pending, OperatorId, requestedAt, "ORDER-TO_PICKUP"),
            (takenOver.RebuildId, takenOver.State, takenOver.OperatorId, takenOver.DueAt, takenOver.EndedOrderId));

        fixture.Context.ChangeTracker.Clear();
        await fixture.HearFromPeerAsync();
        await fixture.Engine.ExecuteOnceAsync(Token);

        Assert.Equal(3, fixture.Riot.CreateCount("TO_PICKUP"));
        OwnOrderRebuildRow rebuilt = await RebuildForAsync(fixture, ordering.NewUpperId);
        Assert.Equal(OwnOrderRebuildStates.Rebuilt, rebuilt.State);
        Assert.Equal(rebuilt.NewUpperId, (await CurrentStopAsync(fixture, FirstDemandId)).UpperId);
        Assert.Null((await fixture.RuntimeAsync()).BlockReasonCode);
    }

    // ---- 夹具 ----------------------------------------------------------------------------------------------

    /// <summary>人工重建一次并跑一轮引擎（车载端刚说过话）。</summary>
    private static async Task RequestAndRunAsync(RuntimeFixture fixture)
    {
        Assert.Equal(
            VehicleFaultRecoveryOutcome.RebuildRequested,
            (await VehicleFaultRecoveryTests.Service(fixture, new(fixture)).RecoverAsync(Rebuild(fixture), Token)).Outcome);
        fixture.Context.ChangeTracker.Clear();
        await fixture.HearFromPeerAsync();
        await fixture.Engine.ExecuteOnceAsync(Token);
    }

    private static async Task<OwnOrderRebuildRow> RebuildForAsync(RuntimeFixture fixture, string endedUpperId)
    {
        await using ControlServerDbContext reading = new(fixture.DbOptionsForTests);
        return await reading.OwnOrderRebuilds.AsNoTracking().SingleAsync(row => row.EndedUpperId == endedUpperId, Token);
    }

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
