using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Commands;
using ControlServer.Host.Runtime.Faults;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using static ControlServer.Tests.Batch7StopDrivenAdvanceDriver;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;

namespace ControlServer.Tests;

/// <summary>
/// 车辆故障的人工出口（control-server#299，#299 阶段一方案 T2，用户 2026-09-22 定 F-a）：人确认故障已排除，服务端自己核判据，
/// 清除故障并处置旅程——control-server#318 起处置就是「留在本车、延迟后同车同需求重建」，不论车上有没有货
/// （用户 2026-09-23，issuecomment-5787511271：「不改派啊，留在本车上」）；订单被 Hold 成 PAUSED 的，续行原单。
/// 本服务端自己的在途单在 RIoT 里被取消不归这里：引擎自己重建，不经人确认。
/// </summary>
/// <remarks>
/// <para>
/// <b>修之前没有出口。</b>今天唯一自动产生故障的来源是在途单 FAILED，而唯一清除故障的路径是 <c>ResumeAsync</c> 确认 continue 之后——
/// 它要求订单是 PAUSED，FAILED 永远不是。所以车会一直带着故障：不接新单、需求不改派，唯一的出口是改库。
/// </para>
/// <para>
/// 旅程由真实引擎受理、派出、再由真实的故障协调器记下故障（<see cref="RuntimeFixture"/>）；只有 RIoT 那一侧是替身。
/// 车停在已知站点、运动读数为停止（<c>MT_FINISHED</c>），这样故障协调器不升级急停，急停相关的判据由每条用例自己摆。
/// </para>
/// <para>
/// 码写成字面量：它们是现场拿到的拒绝理由，改名应当让这里红。
/// </para>
/// </remarks>
public sealed class VehicleFaultRecoveryTests
{
    private const string OperatorId = "L1-OPERATOR-07";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ---- 清除：车上没货，不释放，给本车重建（control-server#318） ---------------------------------------------

    /// <summary>
    /// 开往取货站的单 FAILED、车停稳、闩锁不在、RIoT 上没有未完成订单：清除故障，需求<b>不释放</b>、留在本车；旅程停在原阶段，
    /// 码是 <c>VEHICLE_FAULT_CLEARED_NOTHING_ON_BOARD</c>；延迟到点之后给同一辆车、同一条需求建一张开往同一个取货站的新单。
    /// 之后不再记新的故障。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>翻转断言，依据是 issuecomment-5787511271</b>（用户 2026-09-23：「不改派啊，留在本车上」）。这一条原来是 #299 的
    /// <c>AClearedFaultReleasesTheDemandAndTheVehicleTakesWorkAgain</c>，钉的是「无货即释放改派、旅程关闭、车重新接单」。
    /// 新判据同时断两件事：没有释放（归属未移除、积压仍是已受理、旅程还是这一趟），<b>并且</b>重建出了正确的那一张单
    /// （<see cref="OwnOrderRebuildTests.AssertRebuiltAsync"/>）——只断前者，什么都不做也能过。
    /// </para>
    /// <para>前两轮是对照：不清除时车一直带着故障。修前红就红在清除这一步的处置上。</para>
    /// </remarks>
    [Fact]
    public async Task AClearedFaultOnAnEmptyVehicleKeepsTheDemandAndRebuildsTheOrder()
    {
        await using RuntimeFixture fixture = await FaultedOnTheWayToPickupAsync();
        SiteRiot site = new(fixture);
        JourneyRuntimeRow faulted = await fixture.RuntimeAsync();
        JourneyStopRow[] stopsBefore = await OwnOrderRebuildTests.StopsAsync(fixture, faulted.JourneyId);
        JourneyStopRow pickup = stopsBefore.Single(stop => stop.StopRole == JourneyStopRoles.Pickup);
        await TickAndRunAsync(fixture);
        await TickAndRunAsync(fixture);
        Assert.Equal(1, await JourneyCountAsync(fixture));
        Assert.Equal(VehicleFaultLevel.SuspectedBlocked, (await FaultAsync(fixture)).Level);

        VehicleFaultRecoveryDecision decision = await Service(fixture, site).RecoverAsync(Clear(fixture), Token);

        Assert.Equal(
            (VehicleFaultRecoveryOutcome.Cleared, VehicleFaultRecoveryDispositions.RebuildScheduled),
            (decision.Outcome, decision.Disposition));
        Assert.Empty(decision.Reasons);
        await using (ControlServerDbContext reading = new(fixture.DbOptionsForTests))
        {
            VehicleFaultStateRow fault = await reading.VehicleFaultStates.AsNoTracking().SingleAsync(Token);
            Assert.Equal(VehicleFaultLevel.None, fault.Level);
            Assert.Contains(OperatorId, fault.ClearedReason, StringComparison.Ordinal);
            JourneyRuntimeRow kept = await reading.JourneyRuntimes.AsNoTracking().SingleAsync(Token);
            Assert.Equal(
                (faulted.JourneyId, JourneyRuntimeStage.AwaitingPickupArrival, "VEHICLE_FAULT_CLEARED_NOTHING_ON_BOARD"),
                (kept.JourneyId, kept.Stage, kept.BlockReasonCode));
            Assert.Null((await reading.Set<JourneyDemandRow>().AsNoTracking().SingleAsync(Token)).RemovedAt);
            Assert.NotNull((await reading.JourneyBacklog.AsNoTracking().SingleAsync(row => row.DemandId == FirstDemandId, Token)).AcceptedAt);
        }

        // 清除不向 RIoT 发任何东西：那张单已经 FAILED，没有可取消的；新单由引擎在延迟之后建。
        Assert.Empty(site.OrderCommands);
        Assert.Empty(site.EmergencyCommands);

        // 延迟之内：不记新故障、不建单，码不变。
        await TickAndRunAsync(fixture);
        await TickAndRunAsync(fixture);
        Assert.Equal(1, fixture.Riot.CreateCount("TO_PICKUP"));
        Assert.Equal("VEHICLE_FAULT_CLEARED_NOTHING_ON_BOARD", (await fixture.RuntimeAsync()).BlockReasonCode);

        await OwnOrderRebuildTests.PassTheDelayAsync(fixture);

        Assert.Equal(2, fixture.Riot.CreateCount("TO_PICKUP"));
        await OwnOrderRebuildTests.AssertRebuiltAsync(fixture, faulted, stopsBefore, pickup);
        Assert.Equal(1, await JourneyCountAsync(fixture));
        VehicleFaultStateRow stillCleared = await FaultAsync(fixture);
        Assert.Equal((VehicleFaultLevel.None, 1L), (stillCleared.Level, stillCleared.FaultGeneration));
    }

    /// <summary>
    /// 清除之后旅程不收尾，所以不给车发收尾快照（control-server#323 的来路 7 随之消失）：车上那一站正是重建的单要去的那一站，
    /// 撤掉它反而错了。延迟之后车收到的是同一趟旅程接着走，不是「旅程结束」。
    /// </summary>
    /// <remarks>
    /// <b>翻转断言，依据是 issuecomment-5787511271</b>（用户 2026-09-23：「不改派啊，留在本车上」）。这一条原来是 #323 的
    /// <c>AClearedFaultThatReleasesTheLastDemandTellsTheVehicleTheJourneyIsOver</c>，钉的是「无货清除释放了最后一条需求，
    /// 当场发收尾快照」。新判据断三件事：三张收尾快照一张都没有落库、线上一行都没有；旅程没有收尾；并且重建出了正确的那一张单
    /// ——前两件空动作也满足，第三件不满足。文件级护栏 <c>EveryFileThatCanCloseAJourneySendsTheClosure</c> 照旧：
    /// 清除路径不再能收尾，所以不在它的名单里，而不是放宽了它。
    /// </remarks>
    [Fact]
    public async Task AClearedFaultKeepsTheJourneySoNoClosureIsSentAndTheOrderIsRebuilt()
    {
        await using RuntimeFixture fixture = await FaultedOnTheWayToPickupAsync();
        SiteRiot site = new(fixture);
        JourneyRuntimeRow faulted = await fixture.RuntimeAsync();
        JourneyStopRow[] stopsBefore = await OwnOrderRebuildTests.StopsAsync(fixture, faulted.JourneyId);
        int linesBefore = fixture.Peer.Lines.Count;

        VehicleFaultRecoveryDecision decision = await Service(fixture, site).RecoverAsync(Clear(fixture), Token);
        await OwnOrderRebuildTests.PassTheDelayAsync(fixture);

        Assert.Equal(
            (VehicleFaultRecoveryOutcome.Cleared, VehicleFaultRecoveryDispositions.RebuildScheduled),
            (decision.Outcome, decision.Disposition));
        await using ControlServerDbContext reading = new(fixture.DbOptionsForTests);
        IReadOnlyList<string> closureIds = JourneyClosure.SnapshotMessageIds(faulted.JourneyId);
        Assert.Empty(await reading.ProtocolOutbox.AsNoTracking().Where(row => closureIds.Contains(row.MessageId)).ToArrayAsync(Token));
        Assert.DoesNotContain(
            fixture.Peer.Lines.Skip(linesBefore).Select(line => System.Text.Encoding.UTF8.GetString(line)),
            line => closureIds.Any(id => line.Contains(id, StringComparison.Ordinal)));
        Assert.NotEqual(
            JourneyRuntimeStage.Completed,
            (await reading.JourneyRuntimes.AsNoTracking().SingleAsync(row => row.JourneyId == faulted.JourneyId, Token)).Stage);
        await OwnOrderRebuildTests.AssertRebuiltAsync(
            fixture, faulted, stopsBefore, stopsBefore.Single(stop => stop.StopRole == JourneyStopRoles.Pickup));
    }

    // ---- 判据：缺任何一项都拒绝，列出全部理由 ----------------------------------------------------------------

    /// <summary>
    /// 五项同时不满足：请求没署名、没确认故障已排除、闩锁在、RIoT 报车上还有未完成订单、当前单读不到。
    /// 全部列出，顺序固定；什么都不改、什么都不发。
    /// </summary>
    [Fact]
    public async Task EveryUnmetCriterionIsNamedAndNothingIsChanged()
    {
        await using RuntimeFixture fixture = await FaultedOnTheWayToPickupAsync();
        SiteRiot site = new(fixture) { HasUnfinishedOrder = true };
        fixture.EmergencyLatched = true;
        fixture.Riot.MakeOrderUnreadable((await fixture.RuntimeAsync()).PickupUpperId);

        VehicleFaultRecoveryDecision decision = await Service(fixture, site).RecoverAsync(
            Clear(fixture) with { OperatorId = " ", FaultRemedied = false }, Token);

        Assert.Equal(VehicleFaultRecoveryOutcome.Refused, decision.Outcome);
        Assert.Equal(
            [
                "FAULT_RECOVERY_OPERATOR_UNIDENTIFIED",
                "FAULT_RECOVERY_REMEDY_NOT_CONFIRMED",
                "FAULT_RECOVERY_EMERGENCY_LATCHED",
                "FAULT_RECOVERY_VEHICLE_ORDER_NOT_FINISHED",
                "FAULT_RECOVERY_CURRENT_ORDER_UNKNOWN",
            ],
            decision.Reasons);
        await AssertUntouchedAsync(fixture);
        Assert.Empty(site.OrderCommands);
        Assert.Empty(site.EmergencyCommands);
    }

    /// <summary>每一项单独缺，也各自拒绝、只列它自己——证明每一项都真的被判了，而不是被别的项顺带挡住。</summary>
    [Theory]
    [InlineData("operator")]
    [InlineData("remedy")]
    [InlineData("latched")]
    [InlineData("emergency-unknown")]
    [InlineData("unfinished")]
    [InlineData("orders-unknown")]
    public async Task EachCriterionRefusesOnItsOwn(string missing)
    {
        await using RuntimeFixture fixture = await FaultedOnTheWayToPickupAsync();
        SiteRiot site = new(fixture);
        VehicleFaultRecoveryRequest request = Clear(fixture);
        string expected;
        switch (missing)
        {
            case "operator":
                request = request with { OperatorId = null };
                expected = "FAULT_RECOVERY_OPERATOR_UNIDENTIFIED";
                break;
            case "remedy":
                request = request with { FaultRemedied = false };
                expected = "FAULT_RECOVERY_REMEDY_NOT_CONFIRMED";
                break;
            case "latched":
                fixture.EmergencyLatched = true;
                expected = "FAULT_RECOVERY_EMERGENCY_LATCHED";
                break;
            case "emergency-unknown":
                site.EmergencyUnreadable = true;
                expected = "FAULT_RECOVERY_EMERGENCY_STATE_UNKNOWN";
                break;
            case "unfinished":
                site.HasUnfinishedOrder = true;
                expected = "FAULT_RECOVERY_VEHICLE_ORDER_NOT_FINISHED";
                break;
            default:
                site.HasUnfinishedOrder = null;
                expected = "FAULT_RECOVERY_VEHICLE_ORDERS_UNKNOWN";
                break;
        }

        VehicleFaultRecoveryDecision decision = await Service(fixture, site).RecoverAsync(request, Token);

        Assert.Equal(VehicleFaultRecoveryOutcome.Refused, decision.Outcome);
        Assert.Equal([expected], decision.Reasons);
        await AssertUntouchedAsync(fixture);
    }

    /// <summary>
    /// 清除只认当前单 FAILED。其余一律拒：还在跑的（1、3、10）、被 Hold 的（7，那是续行的事）、挂起的（9）、语义不明的（8）、
    /// 已经成功的（5，车其实到了，不能按「没到」处置）；被人在 RIoT 里取消或删除的（2、6）单独一个码。
    /// </summary>
    /// <remarks>
    /// <b>2 与 6 为什么不清除</b>（独立审查 M1）：本服务端自己的在途单在 RIoT 里被取消，由引擎自己同车同需求重建（#318 的来源一），
    /// 不经人清除；本入口也接它的话，同一次终结会被两条路各处理一次。今天故障只来自 FAILED，所以这一格在产品里走不到；
    /// 但它不能靠「今天走不到」撑着——#317 或任何把故障记在一张已被取消的单上的路径，都会让它活过来。钉成拒绝，
    /// 是把「一次终结只有一条路处理」从现状搬到构造上。
    /// </remarks>
    [Theory]
    [InlineData(RiotOrderState.Failed, true, null)]
    [InlineData(RiotOrderState.Cancelled, true, "FAULT_RECOVERY_CURRENT_ORDER_CANCELLED_IN_RIOT")]
    [InlineData(RiotOrderState.Deleted, true, "FAULT_RECOVERY_CURRENT_ORDER_CANCELLED_IN_RIOT")]
    [InlineData(RiotOrderState.Queueing, false, "FAULT_RECOVERY_CURRENT_ORDER_NOT_ENDED")]
    [InlineData(RiotOrderState.Executing, false, "FAULT_RECOVERY_CURRENT_ORDER_NOT_ENDED")]
    [InlineData(RiotOrderState.QueuePriority, false, "FAULT_RECOVERY_CURRENT_ORDER_NOT_ENDED")]
    [InlineData(RiotOrderState.Paused, false, "FAULT_RECOVERY_CURRENT_ORDER_NOT_ENDED")]
    [InlineData(RiotOrderState.Hang, false, "FAULT_RECOVERY_CURRENT_ORDER_NOT_ENDED")]
    [InlineData(RiotOrderState.Suspended, true, "FAULT_RECOVERY_CURRENT_ORDER_NOT_ENDED")]
    [InlineData(RiotOrderState.Success, true, "FAULT_RECOVERY_CURRENT_ORDER_NOT_ENDED")]
    public async Task OnlyAFailedOrderLetsTheFaultBeCleared(int orderState, bool terminal, string? refusal)
    {
        await using RuntimeFixture fixture = await FaultedOnTheWayToPickupAsync();
        fixture.Riot.SetOrderState((await fixture.RuntimeAsync()).PickupUpperId, orderState, terminal);

        VehicleFaultRecoveryDecision decision = await Service(fixture, new SiteRiot(fixture)).RecoverAsync(Clear(fixture), Token);

        if (refusal is null)
        {
            Assert.Equal((VehicleFaultRecoveryOutcome.Cleared, 0), (decision.Outcome, decision.Reasons.Count));
        }
        else
        {
            Assert.Equal(VehicleFaultRecoveryOutcome.Refused, decision.Outcome);
            Assert.Equal([refusal], decision.Reasons);
            await AssertUntouchedAsync(fixture);
        }
    }

    /// <summary>
    /// 当前单读不到——网关不抛、返回 Unknown，或 RIoT 说没有这张单（NotFound）——一律拒绝，不当成「已终结」。
    /// </summary>
    /// <remarks>
    /// #316 第一版就栽在这里：把读不到当成「不是停住」清掉了码（独立审查第 1 条）。读不到不等于任何一种状态。
    /// </remarks>
    [Theory]
    [InlineData("unknown")]
    [InlineData("not-found")]
    public async Task AnOrderThatCannotBeReadIsNotTakenAsEnded(string read)
    {
        await using RuntimeFixture fixture = await FaultedOnTheWayToPickupAsync();
        SiteRiot site = new(fixture);
        string upperId = (await fixture.RuntimeAsync()).PickupUpperId;
        if (read == "unknown")
        {
            fixture.Riot.MakeOrderUnreadable(upperId);
        }
        else
        {
            site.OrderReadOverride = new RiotOrderObservation(upperId, RiotOrderObservationKind.NotFound, null);
        }

        VehicleFaultRecoveryDecision decision = await Service(fixture, site).RecoverAsync(Clear(fixture), Token);

        Assert.Equal(VehicleFaultRecoveryOutcome.Refused, decision.Outcome);
        Assert.Equal(["FAULT_RECOVERY_CURRENT_ORDER_UNKNOWN"], decision.Reasons);
        await AssertUntouchedAsync(fixture);
    }

    /// <summary>车没有故障：不是这个入口的事，拒绝并说明；什么都不动。</summary>
    [Fact]
    public async Task AVehicleWithoutAFaultHasNothingToClear()
    {
        await using RuntimeFixture fixture = await DispatchedToPickupAsync();

        VehicleFaultRecoveryDecision decision = await Service(fixture, new SiteRiot(fixture)).RecoverAsync(Clear(fixture), Token);

        Assert.Equal(VehicleFaultRecoveryOutcome.Refused, decision.Outcome);
        Assert.Equal(["FAULT_RECOVERY_FAULT_NOT_IN_EFFECT", "FAULT_RECOVERY_CURRENT_ORDER_NOT_ENDED"], decision.Reasons);
        Assert.Equal(JourneyRuntimeStage.AwaitingPickupArrival, (await fixture.RuntimeAsync()).Stage);
    }

    /// <summary>
    /// 急停已触发、闩锁还没锁上（或锁上后被 RIoT 以外的东西解了、服务端还没重触发）：本服务端的急停这一轮还开着。
    /// 清除会让它没人收尾，所以拒绝，先让急停走完（锁上后按 REQ-0356 人工解除）。
    /// </summary>
    [Fact]
    public async Task AnEmergencyStopStillOpenRefusesTheClearance()
    {
        await using RuntimeFixture fixture = await FaultedOnTheWayToPickupAsync(movementState: null);
        SiteRiot site = new(fixture);
        await using (ControlServerDbContext reading = new(fixture.DbOptionsForTests))
        {
            Assert.Contains(
                await reading.RiotOrderCommandAudit.AsNoTracking().Select(row => row.CommandType).ToArrayAsync(Token),
                type => type == RiotCommandTypeNames.TriggerEmergency);
        }

        VehicleFaultRecoveryDecision decision = await Service(fixture, site).RecoverAsync(Clear(fixture), Token);

        Assert.Equal(VehicleFaultRecoveryOutcome.Refused, decision.Outcome);
        Assert.Equal(["FAULT_RECOVERY_EMERGENCY_STOP_OPEN"], decision.Reasons);
        await AssertUntouchedAsync(fixture);
    }

    // ---- 已装货：给本车重建，把这趟送完（control-server#318） ------------------------------------------------

    /// <summary>
    /// 装着货开往卸货站的单 FAILED：清除故障，需求不动、货物绑定保留到重建；旅程<b>不</b>转阻断，停在原阶段，码是
    /// <c>VEHICLE_FAULT_CLEARED_CARGO_ON_BOARD</c>。延迟过了还不建：先等车报一份清除之后的快照，证明货还完整留在原仓、门锁闭、
    /// 开锁输出复位（REQ-0362 保留的 REQ-0238 修复续行前提），这期间旅程码 <c>OWN_ORDER_REBUILD_WAITING_CARGO_EVIDENCE</c>。快照到了、
    /// 证明成立，才给同一辆车、同一条需求重建开往同一个卸货站的单，货物绑定随之以 <c>REBUILT_ON_ORIGINAL_VEHICLE</c> 了结。之后几轮不再记故障。
    /// </summary>
    /// <remarks>
    /// <b>翻转断言，依据是 issuecomment-5780408158 与 issuecomment-5787511271。</b>这一条原来是 #299 的
    /// <c>ALoadedVehicleKeepsItsCargoBindingAndWaitsForAPerson</c>，钉的是「转阻断、等人处置」。新判据断「没有释放」与「重建出了去卸货站的那一张单」。
    /// 快照这一步是 cs#318 票上 2026-09-23 的范围补充（用户选 A，经调度转达）。
    /// </remarks>
    [Fact]
    [Trait("Requirement", "REQ-0362")]
    public async Task AClearedFaultWithCargoOnBoardRebuildsTheOrderToDeliverIt()
    {
        await using RuntimeFixture fixture = await FaultedOnTheWayToGateAsync();
        JourneyRuntimeRow faulted = await fixture.RuntimeAsync();
        JourneyStopRow[] stopsBefore = await OwnOrderRebuildTests.StopsAsync(fixture, faulted.JourneyId);
        int gateCreates = fixture.Riot.CreateCount("TO_GATE");
        await using (ControlServerDbContext reading = new(fixture.DbOptionsForTests))
        {
            Assert.Single(await reading.FaultedVehicleCargo.AsNoTracking().Where(row => row.ReleasedAt == null).ToArrayAsync(Token));
        }

        VehicleFaultRecoveryDecision decision = await Service(fixture, new SiteRiot(fixture)).RecoverAsync(Clear(fixture), Token);
        await TickAndRunAsync(fixture);
        await TickAndRunAsync(fixture);

        Assert.Equal(
            (VehicleFaultRecoveryOutcome.Cleared, VehicleFaultRecoveryDispositions.RebuildScheduled),
            (decision.Outcome, decision.Disposition));
        await using (ControlServerDbContext held = new(fixture.DbOptionsForTests))
        {
            JourneyRuntimeRow waiting = await held.JourneyRuntimes.AsNoTracking().SingleAsync(Token);
            Assert.Equal(
                (faulted.JourneyId, JourneyRuntimeStage.AwaitingGateArrival, "VEHICLE_FAULT_CLEARED_CARGO_ON_BOARD"),
                (waiting.JourneyId, waiting.Stage, waiting.BlockReasonCode));
            Assert.Single(await held.FaultedVehicleCargo.AsNoTracking().Where(row => row.ReleasedAt == null).ToArrayAsync(Token));
        }

        Assert.Equal(gateCreates, fixture.Riot.CreateCount("TO_GATE"));

        await OwnOrderRebuildTests.PassTheDelayAsync(fixture);
        await OwnOrderRebuildTests.PassTheDelayAsync(fixture);

        Assert.Equal(gateCreates, fixture.Riot.CreateCount("TO_GATE"));
        Assert.Equal("OWN_ORDER_REBUILD_WAITING_CARGO_EVIDENCE", (await fixture.RuntimeAsync()).BlockReasonCode);

        await fixture.AddCargoSnapshotAsync(fixture.Clock.GetUtcNow());
        await fixture.HearFromPeerAsync();
        await fixture.Engine.ExecuteOnceAsync(Token);

        Assert.Equal(gateCreates + 1, fixture.Riot.CreateCount("TO_GATE"));
        await OwnOrderRebuildTests.AssertRebuiltAsync(
            fixture, faulted, stopsBefore, stopsBefore.Single(stop => stop.StopRole == JourneyStopRoles.Unload));
        await using ControlServerDbContext after = new(fixture.DbOptionsForTests);
        FaultedVehicleCargoRow cargo = await after.FaultedVehicleCargo.AsNoTracking().SingleAsync(Token);
        Assert.Equal("REBUILT_ON_ORIGINAL_VEHICLE", cargo.ReleasedReason);
        VehicleFaultStateRow fault = await after.VehicleFaultStates.AsNoTracking().SingleAsync(Token);
        Assert.Equal((VehicleFaultLevel.None, 1L), (fault.Level, fault.FaultGeneration));
    }

    /// <summary>
    /// REQ-0362 的「清除之后」：清除之前收到的快照不算，哪怕它显示货好好地在原仓——那是故障前的样子，证明不了清除时货还在。
    /// 旅程一直停在 <c>OWN_ORDER_REBUILD_WAITING_CARGO_EVIDENCE</c>，不建单。
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-0362")]
    public async Task Req0362ASnapshotFromBeforeTheClearanceDoesNotProveTheCargo()
    {
        await using RuntimeFixture fixture = await FaultedOnTheWayToGateAsync();
        int gateCreates = fixture.Riot.CreateCount("TO_GATE");
        await fixture.AddCargoSnapshotAsync(fixture.Clock.GetUtcNow() - TimeSpan.FromSeconds(1));

        Assert.Equal(
            VehicleFaultRecoveryDispositions.RebuildScheduled,
            (await Service(fixture, new SiteRiot(fixture)).RecoverAsync(Clear(fixture), Token)).Disposition);
        fixture.Context.ChangeTracker.Clear();
        await OwnOrderRebuildTests.PassTheDelayAsync(fixture);
        await OwnOrderRebuildTests.PassTheDelayAsync(fixture);

        Assert.Equal(gateCreates, fixture.Riot.CreateCount("TO_GATE"));
        Assert.Equal("OWN_ORDER_REBUILD_WAITING_CARGO_EVIDENCE", (await fixture.RuntimeAsync()).BlockReasonCode);
        await using ControlServerDbContext reading = new(fixture.DbOptionsForTests);
        Assert.Equal(OwnOrderRebuildStates.Pending, (await reading.OwnOrderRebuilds.AsNoTracking().SingleAsync(Token)).State);
    }

    /// <summary>
    /// REQ-0362：清除之后的快照到了，确证货不在原仓——目标仓读到 EMPTY，全部是空的，或两个目标仓里有一个是空的——不重建，挡住并报
    /// 错误级告警，旅程码 <c>OWN_ORDER_REBUILD_CARGO_NOT_IN_PLACE</c>，看板让现场知道货可能不在原仓；之后再来一份好的快照也不再自动建
    /// （等人处理）。
    /// </summary>
    /// <remarks>
    /// 审查 S4 翻转：第一版把门没锁、开锁输出没复位、车报有未知也归到这里停住。那些是「判不了」，不是「货不在」，改为继续等
    /// （<see cref="Req0362ASnapshotThatCannotSettleWhereTheCargoIsKeepsTheRebuildWaiting"/>）。
    /// </remarks>
    [Theory]
    [InlineData("empty")]
    [InlineData("one-of-two-empty")]
    [Trait("Requirement", "REQ-0362")]
    public async Task Req0362AFreshSnapshotThatDoesNotShowTheCargoInPlaceStopsTheRebuild(string shows)
    {
        await using RuntimeFixture fixture = await FaultedOnTheWayToGateAsync();
        int gateCreates = fixture.Riot.CreateCount("TO_GATE");
        Assert.Equal(
            VehicleFaultRecoveryDispositions.RebuildScheduled,
            (await Service(fixture, new SiteRiot(fixture)).RecoverAsync(Clear(fixture), Token)).Disposition);
        fixture.Context.ChangeTracker.Clear();
        fixture.Clock.Advance(TimeSpan.FromSeconds(5));
        if (shows == "one-of-two-empty")
        {
            await SecondCargoSlotAsync(fixture);
        }

        int[] cargo = await (shows switch
        {
            "empty" => fixture.AddCargoSnapshotAsync(fixture.Clock.GetUtcNow(), physicalState: "EMPTY"),
            _ => fixture.AddCargoSnapshotAsync(
                fixture.Clock.GetUtcNow(),
                cargoSlot: index => index == 0 ? ("OCCUPIED", "LOCKED", "RESET") : ("EMPTY", "LOCKED", "RESET")),
        });
        Assert.True(shows == "empty" || cargo.Length == 2, $"cargo slots: {string.Join(',', cargo)}");
        await OwnOrderRebuildTests.PassTheDelayAsync(fixture);
        await fixture.AddCargoSnapshotAsync(fixture.Clock.GetUtcNow());
        await OwnOrderRebuildTests.PassTheDelayAsync(fixture);

        Assert.Equal(gateCreates, fixture.Riot.CreateCount("TO_GATE"));
        Assert.Equal("OWN_ORDER_REBUILD_CARGO_NOT_IN_PLACE", (await fixture.RuntimeAsync()).BlockReasonCode);
        await using ControlServerDbContext reading = new(fixture.DbOptionsForTests);
        OwnOrderRebuildRow record = await reading.OwnOrderRebuilds.AsNoTracking().SingleAsync(Token);
        Assert.Equal(
            (OwnOrderRebuildStates.Stopped, "CARGO_NOT_PROVEN_IN_ORIGINAL_SLOTS"),
            (record.State, record.StoppedReason));
        Assert.Single(await reading.FaultedVehicleCargo.AsNoTracking().Where(row => row.ReleasedAt == null).ToArrayAsync(Token));
        Assert.Contains(fixture.EngineLog.Entries, entry =>
            entry.Level == LogLevel.Error &&
            entry.Message.Contains("CARGO_NOT_PROVEN_IN_ORIGINAL_SLOTS", StringComparison.Ordinal));
    }

    /// <summary>
    /// REQ-0362：清除之后的快照到了，但判不了货在不在原仓——门没锁、开锁输出没复位（货还在、只是没锁好）、车报有未知、目标仓读到
    /// UNKNOWN、目标仓没在快照里、装货批次还没落定、没有一个已提交的装货——不停，继续等，旅程码
    /// <c>OWN_ORDER_REBUILD_CARGO_UNPROVEN</c>（看板另写文案），记录写明卡在哪一项；之后来一份能证明的快照，照常重建。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 审查 S4：只有确证货不在（EMPTY）才停住告警；判不了的等下一份快照。第一版把前三种停住，后四种里有的停、有的没测到。
    /// </para>
    /// <para>
    /// <b>门没锁、开锁输出没复位归「判不了」，理由</b>：货在（OCCUPIED），只是仓没锁好。这是一个会变的状态——有人在门边，或者开锁输出
    /// 还没回落——不是「货不在」。它不会让车带着没锁好的仓出发：建单还要过车载端的离站判定（审查 M2，要求目标仓全锁、开锁输出全复位）。
    /// 货真被拿走了，下一份快照读到的是 EMPTY，那时停住。
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("unlocked")]
    [InlineData("output-not-reset")]
    [InlineData("unknown-present")]
    [InlineData("physical-unknown")]
    [InlineData("slot-not-reported")]
    [InlineData("load-not-settled")]
    [InlineData("no-committed-load")]
    [Trait("Requirement", "REQ-0362")]
    public async Task Req0362ASnapshotThatCannotSettleWhereTheCargoIsKeepsTheRebuildWaiting(string shows)
    {
        await using RuntimeFixture fixture = await FaultedOnTheWayToGateAsync();
        int gateCreates = fixture.Riot.CreateCount("TO_GATE");
        Assert.Equal(
            VehicleFaultRecoveryDispositions.RebuildScheduled,
            (await Service(fixture, new SiteRiot(fixture)).RecoverAsync(Clear(fixture), Token)).Disposition);
        fixture.Context.ChangeTracker.Clear();
        fixture.Clock.Advance(TimeSpan.FromSeconds(5));
        await (shows switch
        {
            "unlocked" => fixture.AddCargoSnapshotAsync(fixture.Clock.GetUtcNow(), lockState: "UNLOCKED"),
            "output-not-reset" => fixture.AddCargoSnapshotAsync(fixture.Clock.GetUtcNow(), unlockOutputState: "ACTIVE"),
            "unknown-present" => fixture.AddCargoSnapshotAsync(fixture.Clock.GetUtcNow(), unknownPresent: true),
            "physical-unknown" => fixture.AddCargoSnapshotAsync(fixture.Clock.GetUtcNow(), physicalState: "UNKNOWN"),
            "slot-not-reported" => fixture.AddCargoSnapshotAsync(fixture.Clock.GetUtcNow(), cargoSlot: _ => null),
            _ => fixture.AddCargoSnapshotAsync(fixture.Clock.GetUtcNow()),
        });
        StationOperationRow load = await fixture.Context.StationOperations
            .SingleAsync(row => row.OperationType == SlotOperationType.Load, Token);
        if (shows == "load-not-settled")
        {
            fixture.Context.StationOperations.Add(new StationOperationRow
            {
                SlotOperationAttemptId = "LOAD-NOT-SETTLED",
                DemandId = load.DemandId,
                SublotId = load.SublotId,
                TargetSlotsJson = load.TargetSlotsJson,
                OperationType = SlotOperationType.Load,
                ContentHash = load.ContentHash,
                Status = StationOperationStatus.Prepared,
            });
        }
        else if (shows == "no-committed-load")
        {
            load.Status = StationOperationStatus.Cancelled;
        }

        await fixture.Context.SaveChangesAsync(Token);
        fixture.Context.ChangeTracker.Clear();
        await OwnOrderRebuildTests.PassTheDelayAsync(fixture);
        await OwnOrderRebuildTests.PassTheDelayAsync(fixture);

        Assert.Equal(gateCreates, fixture.Riot.CreateCount("TO_GATE"));
        Assert.Equal("OWN_ORDER_REBUILD_CARGO_UNPROVEN", (await fixture.RuntimeAsync()).BlockReasonCode);
        await using (ControlServerDbContext waiting = new(fixture.DbOptionsForTests))
        {
            OwnOrderRebuildRow record = await waiting.OwnOrderRebuilds.AsNoTracking().SingleAsync(Token);
            Assert.Equal((OwnOrderRebuildStates.Pending, null), (record.State, record.CargoProvenAt));
            Assert.StartsWith(
                shows switch
                {
                    "unknown-present" => "UNKNOWN_PRESENT",
                    "slot-not-reported" => "SLOT_",
                    "load-not-settled" => "LOAD_NOT_SETTLED:LOAD-NOT-SETTLED",
                    "no-committed-load" => "CARGO_SLOTS_UNKNOWN",
                    _ => "SLOT_",
                },
                record.WaitingReason);
        }

        if (shows is "load-not-settled" or "no-committed-load")
        {
            return;
        }

        await fixture.AddCargoSnapshotAsync(fixture.Clock.GetUtcNow());
        await fixture.HearFromPeerAsync();
        await fixture.Engine.ExecuteOnceAsync(Token);

        Assert.Equal(gateCreates + 1, fixture.Riot.CreateCount("TO_GATE"));
    }

    /// <summary>
    /// REQ-0362：判不了的快照之后要再问一次。车载端只在握手时和被要时才发 <c>SafetyStateSnapshot</c>，所以「继续等」要有人再要：
    /// 判不了的那份快照收到满 10 秒还判不了，服务端撤掉这一代的请求记号，下一条入站消息时再要一次；不满 10 秒不再要。
    /// 车没回应第二次请求时，下一次再要从那次请求起算满 10 秒——不是每轮都要。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 审查 S4 的另一半。只改成「继续等」而不再要，会话一直就绪时这一代再也收不到快照，旅程会停在
    /// <c>OWN_ORDER_REBUILD_CARGO_UNPROVEN</c> 直到重连——看起来在等，实际在等一件不会来的事。
    /// </para>
    /// <para>
    /// 后半段是增量审查 B1：第一版的节流只看那份判不了的快照收到了多久。车不回应时（车载端有两条明确不回应的分支：有未确认的
    /// <c>SafetyStateChanged</c>，或会话此刻不能发）旧快照一直够老，引擎每轮都撤回、Host 每条入站都再要，实际接近心跳频率。
    /// </para>
    /// </remarks>
    [Fact]
    [Trait("Requirement", "REQ-0362")]
    public async Task Req0362AnInconclusiveSnapshotIsAskedForAgainAfterAWhile()
    {
        await using RuntimeFixture fixture = await FaultedOnTheWayToGateAsync();
        Assert.Equal(
            VehicleFaultRecoveryDispositions.RebuildScheduled,
            (await Service(fixture, new SiteRiot(fixture)).RecoverAsync(Clear(fixture), Token)).Disposition);
        fixture.Context.ChangeTracker.Clear();
        long generation = (await fixture.Context.SessionRecoveries.AsNoTracking().SingleAsync(Token)).SessionGeneration;
        Assert.True(await ClaimAsync(fixture, generation));
        Assert.False(await ClaimAsync(fixture, generation));
        await OwnOrderRebuildTests.PassTheDelayAsync(fixture);

        await fixture.AddCargoSnapshotAsync(fixture.Clock.GetUtcNow(), lockState: "UNLOCKED");
        fixture.Clock.Advance(TimeSpan.FromSeconds(9));
        await fixture.HearFromPeerAsync();
        await fixture.Engine.ExecuteOnceAsync(Token);
        fixture.Context.ChangeTracker.Clear();

        Assert.Equal("OWN_ORDER_REBUILD_CARGO_UNPROVEN", (await fixture.RuntimeAsync()).BlockReasonCode);
        Assert.False(await ClaimAsync(fixture, generation));

        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        await fixture.HearFromPeerAsync();
        await fixture.Engine.ExecuteOnceAsync(Token);
        fixture.Context.ChangeTracker.Clear();

        Assert.True(await ClaimAsync(fixture, generation));
        Assert.False(await ClaimAsync(fixture, generation));

        // 车没回应这第二次请求：那份判不了的快照仍是最新的，早已满 10 秒，但离上一次请求不满 10 秒——不再撤。
        await fixture.HearFromPeerAsync();
        await fixture.Engine.ExecuteOnceAsync(Token);
        fixture.Context.ChangeTracker.Clear();
        Assert.False(await ClaimAsync(fixture, generation));
        fixture.Clock.Advance(TimeSpan.FromSeconds(9));
        await fixture.HearFromPeerAsync();
        await fixture.Engine.ExecuteOnceAsync(Token);
        fixture.Context.ChangeTracker.Clear();
        Assert.False(await ClaimAsync(fixture, generation));

        // 离上一次请求满 10 秒：再要一次。
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        await fixture.HearFromPeerAsync();
        await fixture.Engine.ExecuteOnceAsync(Token);
        fixture.Context.ChangeTracker.Clear();
        Assert.True(await ClaimAsync(fixture, generation));

        static async Task<bool> ClaimAsync(RuntimeFixture fixture, long generation)
        {
            await using ControlServerDbContext claiming = new(fixture.DbOptionsForTests);
            return await OwnOrderRebuilds.ClaimCargoEvidenceRequestAsync(
                claiming, fixture.Options.AgvId, generation, ready: true, fixture.Clock.GetUtcNow(), Token);
        }
    }

    /// <summary>
    /// REQ-0362 的「本车的快照」：清除之后别的车发来的快照不算，哪怕它读到货不在——旅程仍停在
    /// <c>OWN_ORDER_REBUILD_WAITING_CARGO_EVIDENCE</c>（没收到本车的快照），不停住、不建单。
    /// </summary>
    /// <remarks>审查 S3：收件箱没有车号列，按信封里的 <c>agvId</c> 过滤；这一条钉住那个过滤。</remarks>
    [Fact]
    [Trait("Requirement", "REQ-0362")]
    public async Task Req0362ASnapshotFromAnotherVehicleSaysNothingAboutThisOnesCargo()
    {
        await using RuntimeFixture fixture = await FaultedOnTheWayToGateAsync();
        int gateCreates = fixture.Riot.CreateCount("TO_GATE");
        Assert.Equal(
            VehicleFaultRecoveryDispositions.RebuildScheduled,
            (await Service(fixture, new SiteRiot(fixture)).RecoverAsync(Clear(fixture), Token)).Disposition);
        fixture.Context.ChangeTracker.Clear();
        fixture.Clock.Advance(TimeSpan.FromSeconds(5));
        await fixture.AddCargoSnapshotAsync(fixture.Clock.GetUtcNow(), physicalState: "EMPTY", agvId: "另一辆车");
        await OwnOrderRebuildTests.PassTheDelayAsync(fixture);
        await OwnOrderRebuildTests.PassTheDelayAsync(fixture);

        Assert.Equal(gateCreates, fixture.Riot.CreateCount("TO_GATE"));
        Assert.Equal("OWN_ORDER_REBUILD_WAITING_CARGO_EVIDENCE", (await fixture.RuntimeAsync()).BlockReasonCode);
        await using ControlServerDbContext reading = new(fixture.DbOptionsForTests);
        Assert.Equal(OwnOrderRebuildStates.Pending, (await reading.OwnOrderRebuilds.AsNoTracking().SingleAsync(Token)).State);
    }

    /// <summary>
    /// REQ-0362，真车载端的另一种常态：清除之后会话一直没回到就绪（本车在途单所致的那种未就绪），车也一直没回快照——旅程停在
    /// <c>OWN_ORDER_REBUILD_WAITING_CARGO_EVIDENCE</c>，不被闸门的码盖掉，也不建单；等待只告警一次，不是每轮一次。
    /// </summary>
    /// <remarks>快照请求本身在 Host 收消息那一侧，节流用例在 <c>OwnOrderRebuildCargoEvidenceRequestTests</c>。</remarks>
    [Fact]
    [Trait("Requirement", "REQ-0362")]
    public async Task Req0362ALoadedClearanceWhoseSessionNeverBecomesReadyWaitsForTheSnapshot()
    {
        await using RuntimeFixture fixture = await FaultedOnTheWayToGateAsync();
        int gateCreates = fixture.Riot.CreateCount("TO_GATE");
        await DropSessionOnOwnOrderAsync(fixture);
        await TickAndRunAsync(fixture);
        Assert.Equal(
            VehicleFaultRecoveryDispositions.RebuildScheduled,
            (await Service(fixture, new SiteRiot(fixture)).RecoverAsync(Clear(fixture), Token)).Disposition);
        fixture.Context.ChangeTracker.Clear();

        for (int round = 0; round < 4; round++)
        {
            await OwnOrderRebuildTests.PassTheDelayAsync(fixture);
        }

        Assert.Equal(gateCreates, fixture.Riot.CreateCount("TO_GATE"));
        Assert.Equal("OWN_ORDER_REBUILD_WAITING_CARGO_EVIDENCE", (await fixture.RuntimeAsync()).BlockReasonCode);
        Assert.Single(fixture.EngineLog.Entries, entry =>
            entry.Message.Contains("is held back: CARGO_EVIDENCE_NOT_RECEIVED", StringComparison.Ordinal));
        await using ControlServerDbContext reading = new(fixture.DbOptionsForTests);
        Assert.Equal(
            SessionReadiness.RecoveryRequired,
            (await reading.SessionRecoveries.AsNoTracking().SingleAsync(Token)).Readiness);
    }

    /// <summary>
    /// 护栏三在故障来源上：清除后重建出来的单，窗口之内又 FAILED、人又清除一次——这一次不再重建，处置是
    /// <c>REBUILD_STOPPED</c>，旅程码 <c>OWN_ORDER_REBUILD_STOPPED</c>，需求仍不释放；之后几轮既不建单，也不对那张 FAILED 单再记故障。
    /// </summary>
    [Fact]
    public async Task AFaultClearedAgainSoonAfterTheRebuildStopsTheRebuilding()
    {
        await using RuntimeFixture fixture = await FaultedOnTheWayToPickupAsync();
        SiteRiot site = new(fixture);
        Assert.Equal(VehicleFaultRecoveryDispositions.RebuildScheduled, (await Service(fixture, site).RecoverAsync(Clear(fixture), Token)).Disposition);
        fixture.Context.ChangeTracker.Clear();
        await OwnOrderRebuildTests.PassTheDelayAsync(fixture);
        Assert.Equal(2, fixture.Riot.CreateCount("TO_PICKUP"));
        string rebuiltUpperId = (await CurrentStopAsync(fixture, FirstDemandId)).UpperId;

        fixture.Clock.Advance(fixture.Options.OwnOrderRebuildRepeatWindow - TimeSpan.FromMinutes(2));
        await fixture.HearFromPeerAsync();
        fixture.Riot.FailOrder(rebuiltUpperId);
        await fixture.Engine.ExecuteOnceAsync(Token);
        Assert.Equal((VehicleFaultLevel.SuspectedBlocked, 2L), ((await FaultAsync(fixture)).Level, (await FaultAsync(fixture)).FaultGeneration));

        VehicleFaultRecoveryDecision again = await Service(fixture, site).RecoverAsync(Clear(fixture), Token);
        fixture.Context.ChangeTracker.Clear();
        await OwnOrderRebuildTests.PassTheDelayAsync(fixture);
        await OwnOrderRebuildTests.PassTheDelayAsync(fixture);

        Assert.Equal(
            (VehicleFaultRecoveryOutcome.Cleared, VehicleFaultRecoveryDispositions.RebuildStopped),
            (again.Outcome, again.Disposition));
        Assert.Equal(2, fixture.Riot.CreateCount("TO_PICKUP"));
        JourneyRuntimeRow stopped = await fixture.RuntimeAsync();
        Assert.Equal((JourneyRuntimeStage.AwaitingPickupArrival, "OWN_ORDER_REBUILD_STOPPED"), (stopped.Stage, stopped.BlockReasonCode));
        VehicleFaultStateRow fault = await FaultAsync(fixture);
        Assert.Equal((VehicleFaultLevel.None, 2L), (fault.Level, fault.FaultGeneration));
        await using ControlServerDbContext reading = new(fixture.DbOptionsForTests);
        Assert.Null((await reading.Set<JourneyDemandRow>().AsNoTracking().SingleAsync(Token)).RemovedAt);
        OwnOrderRebuildRow record = await reading.OwnOrderRebuilds.AsNoTracking().SingleAsync(row => row.EndedUpperId == rebuiltUpperId, Token);
        Assert.Equal(
            (OwnOrderRebuildSources.FaultClearedNothingOnBoard, OwnOrderRebuildStates.Stopped, "REBUILT_ORDER_ENDED_AGAIN_WITHIN_WINDOW", OperatorId),
            (record.Source, record.State, record.StoppedReason, record.OperatorId));
    }

    /// <summary>
    /// REQ-0361 按第一次出问题的来源认「再次」：取消来源的再次出问题只认再次被取消或删除。单被取消、同车重建之后不久，新单 FAILED、
    /// 人清除——这不是取消来源的再次出问题，照常重建。
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-0361")]
    public async Task Req0361AFailureSoonAfterACancellationRebuildIsNotARepeat()
    {
        await using RuntimeFixture fixture = await DispatchedToPickupAsync();
        SiteRiot site = new(fixture);
        fixture.Riot.MovementState = "MT_FINISHED";
        fixture.Riot.CancelOrder((await fixture.RuntimeAsync()).PickupUpperId);
        await TickAndRunAsync(fixture);
        await OwnOrderRebuildTests.PassTheDelayAsync(fixture);
        Assert.Equal(2, fixture.Riot.CreateCount("TO_PICKUP"));
        string rebuiltUpperId = (await CurrentStopAsync(fixture, FirstDemandId)).UpperId;

        fixture.Clock.Advance(TimeSpan.FromMinutes(2));
        await fixture.HearFromPeerAsync();
        fixture.Riot.FailOrder(rebuiltUpperId);
        await fixture.Engine.ExecuteOnceAsync(Token);
        Assert.Equal(VehicleFaultLevel.SuspectedBlocked, (await FaultAsync(fixture)).Level);
        fixture.Context.ChangeTracker.Clear();

        VehicleFaultRecoveryDecision decision = await Service(fixture, site).RecoverAsync(Clear(fixture), Token);
        fixture.Context.ChangeTracker.Clear();
        await OwnOrderRebuildTests.PassTheDelayAsync(fixture);

        Assert.Equal(
            (VehicleFaultRecoveryOutcome.Cleared, VehicleFaultRecoveryDispositions.RebuildScheduled),
            (decision.Outcome, decision.Disposition));
        Assert.Equal(3, fixture.Riot.CreateCount("TO_PICKUP"));
        await using ControlServerDbContext reading = new(fixture.DbOptionsForTests);
        OwnOrderRebuildRow record = await reading.OwnOrderRebuilds.AsNoTracking().SingleAsync(row => row.EndedUpperId == rebuiltUpperId, Token);
        Assert.Equal(
            (OwnOrderRebuildSources.FaultClearedNothingOnBoard, OwnOrderRebuildStates.Rebuilt),
            (record.Source, record.State));
    }

    /// <summary>
    /// REQ-0361 反方向：故障清除来源的再次出问题包括被取消、删除。清除后重建出来的单，窗口之内被人在 RIoT 里取消——不再重建，
    /// 旅程码 <c>OWN_ORDER_REBUILD_STOPPED</c>。
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-0361")]
    public async Task Req0361ACancellationSoonAfterAFaultRebuildStopsTheRebuilding()
    {
        await using RuntimeFixture fixture = await FaultedOnTheWayToPickupAsync();
        SiteRiot site = new(fixture);
        Assert.Equal(VehicleFaultRecoveryDispositions.RebuildScheduled, (await Service(fixture, site).RecoverAsync(Clear(fixture), Token)).Disposition);
        fixture.Context.ChangeTracker.Clear();
        await OwnOrderRebuildTests.PassTheDelayAsync(fixture);
        Assert.Equal(2, fixture.Riot.CreateCount("TO_PICKUP"));
        string rebuiltUpperId = (await CurrentStopAsync(fixture, FirstDemandId)).UpperId;

        fixture.Clock.Advance(fixture.Options.OwnOrderRebuildRepeatWindow - TimeSpan.FromMinutes(2));
        await fixture.HearFromPeerAsync();
        fixture.Riot.CancelOrder(rebuiltUpperId);
        await fixture.Engine.ExecuteOnceAsync(Token);
        await OwnOrderRebuildTests.PassTheDelayAsync(fixture);
        await OwnOrderRebuildTests.PassTheDelayAsync(fixture);

        Assert.Equal(2, fixture.Riot.CreateCount("TO_PICKUP"));
        Assert.Equal("OWN_ORDER_REBUILD_STOPPED", (await fixture.RuntimeAsync()).BlockReasonCode);
        await using ControlServerDbContext reading = new(fixture.DbOptionsForTests);
        OwnOrderRebuildRow record = await reading.OwnOrderRebuilds.AsNoTracking().SingleAsync(row => row.EndedUpperId == rebuiltUpperId, Token);
        Assert.Equal(
            (OwnOrderRebuildSources.CancelledInRiot, OwnOrderRebuildStates.Stopped, "REBUILT_ORDER_ENDED_AGAIN_WITHIN_WINDOW"),
            (record.Source, record.State, record.StoppedReason));
    }

    /// <summary>
    /// 重建出来的新单还没确认就 FAILED（审查 S2）：按普通 FAILED 记故障——疑似阻断、旅程码 <c>VEHICLE_ORDER_FAILED</c>——#299 的清除入口
    /// 接得住；第一次出问题是取消，FAILED 不算取消来源的「再次」（REQ-0361），人清除之后照常再重建一次。
    /// </summary>
    /// <remarks>第一版把它记成 <c>Stopped / REBUILT_ORDER_ENDED_BEFORE_CONFIRMATION</c>：不记故障，清除入口因此无事可清，这趟旅程再也动不了。</remarks>
    [Fact]
    [Trait("Requirement", "REQ-0361")]
    public async Task Req0361ARebuiltOrderThatFailsBeforeItIsConfirmedIsAFaultAPersonCanClear()
    {
        await using RuntimeFixture fixture = await DispatchedToPickupAsync();
        SiteRiot site = new(fixture);
        fixture.Riot.MovementState = "MT_FINISHED";
        fixture.Riot.CancelOrder((await fixture.RuntimeAsync()).PickupUpperId);
        await TickAndRunAsync(fixture);
        fixture.Riot.LoseNextCreateResponse = true;
        await OwnOrderRebuildTests.PassTheDelayAsync(fixture);
        string newUpperId = (await CurrentStopAsync(fixture, FirstDemandId)).UpperId;
        Assert.Equal(2, fixture.Riot.CreateCount("TO_PICKUP"));

        fixture.Riot.FailOrder(newUpperId);
        await fixture.HearFromPeerAsync();
        await TickAndRunAsync(fixture);
        await TickAndRunAsync(fixture);

        VehicleFaultStateRow fault = await FaultAsync(fixture);
        Assert.Equal((VehicleFaultLevel.SuspectedBlocked, 1L), (fault.Level, fault.FaultGeneration));
        Assert.Equal("VEHICLE_ORDER_FAILED", (await fixture.RuntimeAsync()).BlockReasonCode);
        Assert.Equal(2, fixture.Riot.CreateCount("TO_PICKUP"));
        fixture.Context.ChangeTracker.Clear();

        VehicleFaultRecoveryDecision decision = await Service(fixture, site).RecoverAsync(Clear(fixture), Token);
        fixture.Context.ChangeTracker.Clear();
        await OwnOrderRebuildTests.PassTheDelayAsync(fixture);

        Assert.Equal(
            (VehicleFaultRecoveryOutcome.Cleared, VehicleFaultRecoveryDispositions.RebuildScheduled),
            (decision.Outcome, decision.Disposition));
        Assert.Equal(3, fixture.Riot.CreateCount("TO_PICKUP"));
        await using ControlServerDbContext reading = new(fixture.DbOptionsForTests);
        OwnOrderRebuildRow record = await reading.OwnOrderRebuilds.AsNoTracking().SingleAsync(row => row.EndedUpperId == newUpperId, Token);
        Assert.Equal(
            (OwnOrderRebuildSources.FaultClearedNothingOnBoard, OwnOrderRebuildStates.Rebuilt),
            (record.Source, record.State));
    }

    /// <summary>
    /// 确认前 FAILED 的新单每一轮都还在喂故障模型，与确认过的单 FAILED 时一样：车还在动，第一轮升级急停、触发停在 Pending；RIoT 上锁、
    /// 钟走一秒后的第二轮，触发对账成 Confirmed，故障的 <c>LastEvaluatedAt</c> 推到那一刻。
    /// </summary>
    /// <remarks>
    /// 变异 M43（只喂一次）在第四轮全绿：那时没有用例断言第一轮之后还在喂。形状照
    /// <c>Batch7DemandReleaseServiceTests.AFaultedVehicleStaysUnderSupervisionWhileItsReleaseCriteriaHold</c>：上锁放在第一轮之后，
    /// 触发才必然停在 Pending，第二轮有没有喂才分得出来。
    /// </remarks>
    [Fact]
    [Trait("Requirement", "REQ-0361")]
    public async Task ARebuiltOrderThatFailedBeforeConfirmationStaysUnderFaultSupervision()
    {
        await using RuntimeFixture fixture = await DispatchedToPickupAsync();
        fixture.Riot.MovementState = "MT_FINISHED";
        fixture.Riot.CancelOrder((await fixture.RuntimeAsync()).PickupUpperId);
        await TickAndRunAsync(fixture);
        fixture.Riot.LoseNextCreateResponse = true;
        await OwnOrderRebuildTests.PassTheDelayAsync(fixture);
        string newUpperId = (await CurrentStopAsync(fixture, FirstDemandId)).UpperId;

        fixture.Riot.MovementState = "MT_RUNNING";
        fixture.Riot.FailOrder(newUpperId);
        await fixture.HearFromPeerAsync();
        fixture.Context.ChangeTracker.Clear();
        await fixture.Engine.ExecuteOnceAsync(Token);
        fixture.Context.ChangeTracker.Clear();
        Assert.Equal(RiotOrderCommandOutcome.Pending, await EmergencyTriggerOutcomeAsync(fixture));

        fixture.EmergencyLatched = true;
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        DateTimeOffset secondRound = fixture.Clock.GetUtcNow();
        await fixture.HearFromPeerAsync();
        fixture.Context.ChangeTracker.Clear();
        await fixture.Engine.ExecuteOnceAsync(Token);
        fixture.Context.ChangeTracker.Clear();

        Assert.Equal(RiotOrderCommandOutcome.Confirmed, await EmergencyTriggerOutcomeAsync(fixture));
        await using ControlServerDbContext reading = new(fixture.DbOptionsForTests);
        Assert.Equal(secondRound, (await reading.VehicleFaultStates.AsNoTracking().SingleAsync(Token)).LastEvaluatedAt);
        Assert.Equal("VEHICLE_ORDER_FAILED", (await reading.JourneyRuntimes.AsNoTracking().SingleAsync(Token)).BlockReasonCode);
    }

    /// <summary>最近一次急停触发尝试的对账结果；没有触发过时为 null。</summary>
    private static async Task<RiotOrderCommandOutcome?> EmergencyTriggerOutcomeAsync(RuntimeFixture fixture)
    {
        await using ControlServerDbContext reading = new(fixture.DbOptionsForTests);
        RiotOrderCommandAuditRow[] triggers = await reading.RiotOrderCommandAudit.AsNoTracking()
            .Where(row => row.CommandType == RiotCommandTypeNames.TriggerEmergency)
            .ToArrayAsync(Token);
        return triggers.OrderBy(row => row.AttemptNumber).LastOrDefault()?.Outcome;
    }

    // ---- 幂等与崩溃 ------------------------------------------------------------------------------------------

    /// <summary>
    /// 同一个清除请求到了两次（人连点、或回应丢了重发）：第二次答「已经清过了」，不再做任何事——
    /// 尤其不碰清除之后同一辆车刚接的新旅程。
    /// </summary>
    [Fact]
    public async Task TheSameClearanceTwiceDoesNothingTheSecondTime()
    {
        await using RuntimeFixture fixture = await FaultedOnTheWayToPickupAsync();
        SiteRiot site = new(fixture);
        VehicleFaultRecoveryDecision first = await Service(fixture, site).RecoverAsync(Clear(fixture), Token);
        Assert.Equal(VehicleFaultRecoveryOutcome.Cleared, first.Outcome);
        await TickAndRunAsync(fixture);
        await TickAndRunAsync(fixture);
        JourneyRuntimeRow[] before = await JourneysAsync(fixture);
        VehicleFaultStateRow faultBefore = await FaultAsync(fixture);

        VehicleFaultRecoveryDecision second = await Service(fixture, site).RecoverAsync(Clear(fixture), Token);

        Assert.Equal(
            (VehicleFaultRecoveryOutcome.AlreadyCleared, VehicleFaultRecoveryDispositions.None, 0),
            (second.Outcome, second.Disposition, second.Reasons.Count));
        Assert.Equivalent(before, await JourneysAsync(fixture), strict: true);
        Assert.Equivalent(faultBefore, await FaultAsync(fixture), strict: true);
        Assert.Contains(await JourneysAsync(fixture), row => row.Stage != JourneyRuntimeStage.Completed);
    }

    /// <summary>
    /// 已经清过的车，再来一个没署名、或没确认故障已排除的请求：不答「已经清过」，照样按缺的判据拒绝（独立审查 L3）。
    /// 「已经清过」是给同一个人重发同一个请求的，不是给任何人的免检通道。
    /// </summary>
    [Theory]
    [InlineData("operator")]
    [InlineData("remedy")]
    public async Task AnAlreadyClearedVehicleStillNeedsANamedConfirmedRequest(string missing)
    {
        await using RuntimeFixture fixture = await FaultedOnTheWayToPickupAsync();
        SiteRiot site = new(fixture);
        Assert.Equal(VehicleFaultRecoveryOutcome.Cleared, (await Service(fixture, site).RecoverAsync(Clear(fixture), Token)).Outcome);

        VehicleFaultRecoveryDecision again = await Service(fixture, site).RecoverAsync(
            missing == "operator" ? Clear(fixture) with { OperatorId = null } : Clear(fixture) with { FaultRemedied = false },
            Token);

        Assert.Equal(VehicleFaultRecoveryOutcome.Refused, again.Outcome);
        Assert.Equal(
            [missing == "operator" ? "FAULT_RECOVERY_OPERATOR_UNIDENTIFIED" : "FAULT_RECOVERY_REMEDY_NOT_CONFIRMED"],
            again.Reasons);
    }

    /// <summary>
    /// 续行成功之后同一个续行请求又来一次：答「已经清过」，不再发第二次 <c>CONTINUE_FROM_HELD</c>（独立审查 L3）。
    /// </summary>
    [Fact]
    public async Task TheSameResumeTwiceContinuesOnce()
    {
        await using RuntimeFixture fixture = await FaultedOnTheWayToPickupAsync();
        JourneyRuntimeRow faulted = await fixture.RuntimeAsync();
        fixture.Riot.SetOrderState(faulted.PickupUpperId, RiotOrderState.Paused, terminal: false);
        SiteRiot site = new(fixture) { OnOrderCommand = ContinueResumes(fixture, faulted.PickupUpperId) };
        Assert.Equal(VehicleFaultRecoveryOutcome.Resumed, (await Service(fixture, site).RecoverAsync(Resume(fixture), Token)).Outcome);

        VehicleFaultRecoveryDecision again = await Service(fixture, site).RecoverAsync(Resume(fixture), Token);

        Assert.Equal((VehicleFaultRecoveryOutcome.AlreadyCleared, 0), (again.Outcome, again.Reasons.Count));
        Assert.Equal([RiotOrderCommandKind.ContinueFromHeld], site.OrderCommands);
    }

    /// <summary>
    /// 两个续行请求同时到（人连点两下、两个人同时点）：只发一次 <c>CONTINUE_FROM_HELD</c>，另一个答
    /// <c>FAULT_RECOVERY_RESUME_IN_PROGRESS</c>（二轮审查第 1 条）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// continue 在放锁之后才发（M2），所以锁挡不住这种并发：第一个请求的 continue 正在去 RIoT 的路上时，第二个请求照样能拿到锁，
    /// 读到订单仍是 HELD、故障仍在效，判定全部通过，再发一次。命令服务每次尝试写一行审计，不去重。上一条
    /// <see cref="TheSameResumeTwiceContinuesOnce"/> 只管一先一后。
    /// </para>
    /// <para>
    /// 并发点造在第一个请求的 continue 发出之时：替身在那一刻同步跑完第二个请求，之后才把订单改成执行中。这是确定性的，不靠时序碰运气。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TwoResumesAtOnceContinueOnce()
    {
        await using RuntimeFixture fixture = await FaultedOnTheWayToPickupAsync();
        JourneyRuntimeRow faulted = await fixture.RuntimeAsync();
        fixture.Riot.SetOrderState(faulted.PickupUpperId, RiotOrderState.Paused, terminal: false);
        using JourneyMutationGate gate = new();
        VehicleFaultResumeFlights flights = new();
        SiteRiot site = new(fixture);
        bool secondStarted = false;
        VehicleFaultRecoveryDecision? second = null;
        site.OnOrderCommand = (kind, _) =>
        {
            if (kind != RiotOrderCommandKind.ContinueFromHeld || secondStarted)
            {
                return;
            }

            secondStarted = true;
            second = Service(fixture, site, gate: gate, flights: flights).RecoverAsync(Resume(fixture), Token)
                .GetAwaiter().GetResult();
            fixture.Riot.SetOrderState(faulted.PickupUpperId, RiotOrderState.Executing, terminal: false);
        };

        VehicleFaultRecoveryDecision first =
            await Service(fixture, site, gate: gate, flights: flights).RecoverAsync(Resume(fixture), Token);

        Assert.True(secondStarted, "the second request never ran while the first one's continue was in flight");
        Assert.Equal(VehicleFaultRecoveryOutcome.Resumed, first.Outcome);
        Assert.Equal(VehicleFaultRecoveryOutcome.Refused, second!.Outcome);
        Assert.Equal(["FAULT_RECOVERY_RESUME_IN_PROGRESS"], second.Reasons);
        Assert.Equal([RiotOrderCommandKind.ContinueFromHeld], site.OrderCommands);
    }

    /// <summary>
    /// 持锁期间一次 RIoT 调用都不发（独立审查 M2）：清除的 RIoT 读取在拿锁之前做完，锁里只复核数据库、提交；续行整个不拿锁。
    /// </summary>
    /// <remarks>
    /// 锁是整轮调度与这个请求共用的。RIoT 慢起来时——正是车出故障的时候——锁里的每次调用都可能等满 RIoT 的超时，一个请求就能
    /// 把所有车的调度轮次（急停确认、REQ-0248 重触发、停车证明、派车、计划下发）拖住几分钟。替身在每次 RIoT 调用时试拿一下锁，
    /// 拿不到就记下来；三条路各走一遍，并断言确实发生过 RIoT 调用，免得「一次都没调」也算过。
    /// </remarks>
    [Theory]
    [InlineData("clear-empty")]
    [InlineData("clear-loaded")]
    [InlineData("resume")]
    public async Task NoRiotCallIsMadeWhileTheGateIsHeld(string path)
    {
        await using RuntimeFixture fixture = path == "clear-loaded"
            ? await FaultedOnTheWayToGateAsync()
            : await FaultedOnTheWayToPickupAsync();
        using JourneyMutationGate gate = new();
        SiteRiot site = new(fixture) { Gate = gate };
        VehicleFaultRecoveryRequest request = Clear(fixture);
        if (path == "resume")
        {
            JourneyRuntimeRow faulted = await fixture.RuntimeAsync();
            fixture.Riot.SetOrderState(faulted.PickupUpperId, RiotOrderState.Paused, terminal: false);
            site.OnOrderCommand = ContinueResumes(fixture, faulted.PickupUpperId);
            request = Resume(fixture);
        }

        VehicleFaultRecoveryDecision decision = await Service(fixture, site, gate: gate).RecoverAsync(request, Token);

        Assert.Contains(decision.Outcome, new[] { VehicleFaultRecoveryOutcome.Cleared, VehicleFaultRecoveryOutcome.Resumed });
        Assert.True(site.RiotCalls > 0, "no RIoT call was made at all, so this proves nothing about where they are made");
        Assert.Empty(site.CallsUnderGate);
    }

    /// <summary>
    /// 读完 RIoT、拿到锁之前，引擎这一轮把旅程往前推了（这里：订单被报成 SUCCESS，车到站）：锁里复核发现旅程变了，拒绝，请重试；
    /// 什么都不改（独立审查 M2 的另一面：挪到锁外的读数可能过时，提交前必须对一遍）。
    /// </summary>
    [Fact]
    public async Task AJourneyThatMovedOnBetweenTheReadAndTheGateIsNotDisposedOf()
    {
        await using RuntimeFixture fixture = await FaultedOnTheWayToPickupAsync();
        JourneyRuntimeRow faulted = await fixture.RuntimeAsync();
        using JourneyMutationGate gate = new();
        SiteRiot site = new(fixture)
        {
            AfterUnfinishedOrdersRead = () =>
            {
                using ControlServerDbContext engine = new(fixture.DbOptionsForTests);
                JourneyRuntimeRow row = engine.JourneyRuntimes.Single(journey => journey.JourneyId == faulted.JourneyId);
                row.Stage = JourneyRuntimeStage.AwaitingSublot;
                row.SetBlockReason(null, fixture.Clock.GetUtcNow());
                engine.SaveChanges();
            },
        };

        VehicleFaultRecoveryDecision decision = await Service(fixture, site, gate: gate).RecoverAsync(Clear(fixture), Token);

        Assert.Equal(VehicleFaultRecoveryOutcome.Refused, decision.Outcome);
        Assert.Equal(["FAULT_RECOVERY_STATE_CHANGED"], decision.Reasons);
        Assert.Equal(VehicleFaultLevel.SuspectedBlocked, (await FaultAsync(fixture)).Level);
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, (await fixture.RuntimeAsync()).Stage);
    }

    /// <summary>
    /// 清除写到一半崩了——释放已经暂存、故障已经写下、提交之前进程没了：什么都不留下。库里仍是清除之前的样子
    /// （故障在效、旅程未关、需求未释放）；换一个进程重发同一个请求，完整地清除一次。
    /// </summary>
    /// <remarks>
    /// 崩溃点放在故障存储<b>写完之后</b>：存储自己 <c>SaveChanges</c>，没有事务包着的话，那一次保存会把故障清除与之前暂存的释放
    /// 一起落库，留下「故障清了、需求放了」而处置没做完的半截状态——这正是这条用例要抓的实现。
    /// </remarks>
    [Fact]
    public async Task AClearanceCutShortLeavesNothingBehindAndCanBeMadeAgain()
    {
        await using RuntimeFixture fixture = await FaultedOnTheWayToPickupAsync();
        SiteRiot site = new(fixture);
        ControlServerDbContext crashing = new(fixture.DbOptionsForTests);
        CrashAfterClearStore store = new(new VehicleFaultStore(crashing));

        await Assert.ThrowsAsync<IOException>(() => Service(fixture, site, crashing, store).RecoverAsync(Clear(fixture), Token));
        await crashing.DisposeAsync();

        Assert.True(store.Cleared, "the crash point was never reached, so nothing about atomicity was tested");
        await AssertUntouchedAsync(fixture);

        VehicleFaultRecoveryDecision retried = await Service(fixture, site).RecoverAsync(Clear(fixture), Token);
        Assert.Equal(
            (VehicleFaultRecoveryOutcome.Cleared, VehicleFaultRecoveryDispositions.RebuildScheduled),
            (retried.Outcome, retried.Disposition));
        await using ControlServerDbContext retriedReading = new(fixture.DbOptionsForTests);
        Assert.Single(await retriedReading.OwnOrderRebuilds.AsNoTracking().ToArrayAsync(Token));
        Assert.Equal(VehicleFaultLevel.None, (await FaultAsync(fixture)).Level);
    }

    // ---- 与引擎循环串行 ------------------------------------------------------------------------------------

    /// <summary>
    /// 引擎这一轮还在跑（持有旅程改动锁）时，清除请求等着，不与它交错；这一轮结束、锁放开，清除才做。
    /// </summary>
    /// <remarks>
    /// 交错的后果不是理论上的：引擎读到旅程还在到站阶段，清除关掉旅程、清掉故障，引擎随后对它刚读到的 FAILED 单再记一次故障。
    /// </remarks>
    [Fact]
    public async Task AClearanceWaitsForTheRuntimeRoundInProgress()
    {
        await using RuntimeFixture fixture = await FaultedOnTheWayToPickupAsync();
        JourneyMutationGate gate = new();
        IDisposable round = await gate.EnterAsync(Token);

        Task<VehicleFaultRecoveryDecision> clearing = Service(fixture, new SiteRiot(fixture), gate: gate).RecoverAsync(Clear(fixture), Token);
        await Task.Delay(TimeSpan.FromMilliseconds(300), Token);
        Assert.False(clearing.IsCompleted, "the clearance ran while the runtime round held the gate");
        Assert.Equal(VehicleFaultLevel.SuspectedBlocked, (await FaultAsync(fixture)).Level);

        round.Dispose();
        Assert.Equal(VehicleFaultRecoveryOutcome.Cleared, (await clearing).Outcome);
    }

    /// <summary>
    /// 这一轮迟迟不结束（锁一直被占着）：请求等满限时就放弃，答 <c>FAULT_RECOVERY_RUNTIME_BUSY</c>（HTTP 503），什么都不改。
    /// 等待有上限，是这一条钉住的。
    /// </summary>
    [Fact]
    public async Task ARequestThatCannotGetTheGateInTimeGivesUpAndChangesNothing()
    {
        await using RuntimeFixture fixture = await FaultedOnTheWayToPickupAsync();
        using JourneyMutationGate gate = new();
        using IDisposable stuckRound = await gate.EnterAsync(Token);

        VehicleFaultRecoveryDecision decision = await Service(
            fixture, new SiteRiot(fixture), gate: gate, gateTimeout: TimeSpan.FromMilliseconds(200)).RecoverAsync(Clear(fixture), Token);

        Assert.Equal(VehicleFaultRecoveryOutcome.Refused, decision.Outcome);
        Assert.Equal(["FAULT_RECOVERY_RUNTIME_BUSY"], decision.Reasons);
        await AssertUntouchedAsync(fixture);
    }

    /// <summary>
    /// 拿这把锁的只有两处：引擎循环（整轮持有）与故障恢复（整个请求持有）。锁不可重入，所以这一轮里调用的任何东西、以及恢复持锁期间
    /// 调用的任何东西，只要也去拿它就会死锁——worker 那一侧等锁没有上限。今天两侧调用的东西都不拿锁；这一条在出现第三个持有者的那天红，
    /// 那一天要重新看一遍会不会互等。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>按调用点认持有者，不按怎么拿到锁</b>（独立审查 L1）：扫宿主程序集每个方法体的 IL，找调用 <c>EnterAsync</c>／<c>TryEnterAsync</c>
    /// 的指令，归到最外层的声明类型（async 方法的代码在编译器生成的嵌套状态机里）。构造注入、最小 API 处理函数的参数、
    /// <c>[FromServices]</c>、<c>GetRequiredService&lt;JourneyMutationGate&gt;()</c>，拿到之后要用都得调这两个方法，所以都逃不过。
    /// </para>
    /// <para>
    /// 按字节找 <c>call</c>／<c>callvirt</c>（0x28／0x6F）后的 4 字节令牌、解析失败就跳过，是启发式：操作数里偶然出现这两个字节、
    /// 又恰好解析成这两个方法的可能极小，而一旦发生，结果是多报一个持有者（这里红），不是漏报。
    /// <see cref="TheGateHolderScanSeesACallSite"/> 证明它认得出调用点。
    /// </para>
    /// </remarks>
    [Fact]
    public void OnlyTheRuntimeRoundAndTheRecoveryTakeTheGate()
    {
        Assert.Equal(["JourneyRuntimeWorker", "VehicleFaultRecoveryService"], GateHolders(typeof(JourneyMutationGate).Assembly));
    }

    /// <summary>扫描器自己的正例：本测试程序集里的 <see cref="GateHolderProbe"/> 调了 <c>EnterAsync</c>，必须被认出来。</summary>
    [Fact]
    public void TheGateHolderScanSeesACallSite()
    {
        Assert.Contains(nameof(GateHolderProbe), GateHolders(typeof(GateHolderProbe).Assembly));
    }

    private static string[] GateHolders(System.Reflection.Assembly assembly)
    {
        System.Reflection.MethodInfo[] gateMethods =
        [
            typeof(JourneyMutationGate).GetMethod(nameof(JourneyMutationGate.EnterAsync))!,
            typeof(JourneyMutationGate).GetMethod(nameof(JourneyMutationGate.TryEnterAsync))!,
        ];
        const System.Reflection.BindingFlags all = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static |
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.DeclaredOnly;
        HashSet<string> holders = new(StringComparer.Ordinal);
        foreach (Type type in assembly.GetTypes())
        {
            IEnumerable<System.Reflection.MethodBase> methods =
                type.GetMethods(all).Cast<System.Reflection.MethodBase>().Concat(type.GetConstructors(all));
            foreach (System.Reflection.MethodBase method in methods)
            {
                byte[]? il = method.GetMethodBody()?.GetILAsByteArray();
                if (il is null || !CallsAny(method, il, gateMethods))
                {
                    continue;
                }

                Type outer = type;
                while (outer.DeclaringType is not null)
                {
                    outer = outer.DeclaringType;
                }

                holders.Add(outer.Name);
            }
        }

        return [.. holders.Order(StringComparer.Ordinal)];
    }

    private static bool CallsAny(System.Reflection.MethodBase method, byte[] il, System.Reflection.MethodInfo[] targets)
    {
        for (int index = 0; index + 4 < il.Length; index++)
        {
            if (il[index] is not (0x28 or 0x6F))
            {
                continue;
            }

            try
            {
                System.Reflection.MethodBase? called = method.Module.ResolveMethod(
                    BitConverter.ToInt32(il, index + 1),
                    method.DeclaringType?.IsGenericType == true ? method.DeclaringType.GetGenericArguments() : null,
                    method.IsGenericMethod ? method.GetGenericArguments() : null);
                if (called is not null && targets.Contains(called))
                {
                    return true;
                }
            }
            catch (Exception error) when (error is ArgumentException or BadImageFormatException or MissingMethodException or
                                          TypeLoadException)
            {
            }
        }

        return false;
    }

    // ---- 会话因本服务端自己的在途单而未就绪 --------------------------------------------------------------------

    /// <summary>
    /// 真车载端在车带着本服务端的单时读到 <c>RIOT_NONFINAL_ORDER_PRESENT</c>，会话停在 <c>DEPARTURE_SAFETY_NOT_READY</c>，
    /// 引擎在闸门处就返回。清除不依赖引擎走到在途分支：会话未就绪时照样清除、照样记下重建；闸门不把码盖成
    /// <c>ONBOARD_SESSION_NOT_READY</c>，不再记故障；延迟之后在闸门后面照样重建，重建之后码交还给闸门。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 形状照 <c>PickupDispatchPlanPastOwnOrderTests.DropSessionOnOwnOrderAsync</c>。合成车载端永远报安全，合成 L2 按构造看不见这种会话。
    /// 若把重建只挂在引擎的到站分支，这条会红：闸门后面那段代码在真车上走得到，到站分支走不到。
    /// </para>
    /// <para><b>翻转断言</b>（原 <c>AClearanceWorksWhileTheSessionIsNotReadyOnItsOwnOrder</c> 断旅程关闭），依据 issuecomment-5787511271。</para>
    /// </remarks>
    [Theory]
    [InlineData("empty")]
    [InlineData("loaded")]
    [Trait("Requirement", "REQ-0362")]
    public async Task AClearanceWhileTheSessionIsNotReadyOnItsOwnOrderRebuildsOnceTheSessionIsReady(string cargo)
    {
        await using RuntimeFixture fixture = cargo == "loaded"
            ? await FaultedOnTheWayToGateAsync()
            : await FaultedOnTheWayToPickupAsync();
        JourneyRuntimeRow faulted = await fixture.RuntimeAsync();
        JourneyStopRow[] stopsBefore = await OwnOrderRebuildTests.StopsAsync(fixture, faulted.JourneyId);
        JourneyStopRow current = stopsBefore.Single(stop =>
            stop.StopRole == (cargo == "loaded" ? JourneyStopRoles.Unload : JourneyStopRoles.Pickup));
        string code = cargo == "loaded" ? "VEHICLE_FAULT_CLEARED_CARGO_ON_BOARD" : "VEHICLE_FAULT_CLEARED_NOTHING_ON_BOARD";
        await DropSessionOnOwnOrderAsync(fixture);
        await TickAndRunAsync(fixture);
        Assert.Equal(faulted.Stage, (await fixture.RuntimeAsync()).Stage);

        VehicleFaultRecoveryDecision decision = await Service(fixture, new SiteRiot(fixture)).RecoverAsync(Clear(fixture), Token);
        // The clearance wrote through a context of its own, as the HTTP request does in the host; the engine's next round
        // opens a new scope there, so it does here too -- the fixture keeps one context across rounds, and an instance it
        // tracked from before the clearance would still read the gate's code as the one on file.
        fixture.Context.ChangeTracker.Clear();
        await TickAndRunAsync(fixture);
        await TickAndRunAsync(fixture);

        Assert.Equal(VehicleFaultRecoveryDispositions.RebuildScheduled, decision.Disposition);
        JourneyRuntimeRow waiting = await fixture.RuntimeAsync();
        Assert.Equal((faulted.JourneyId, faulted.Stage, code), (waiting.JourneyId, waiting.Stage, waiting.BlockReasonCode));

        if (cargo == "loaded")
        {
            await fixture.AddCargoSnapshotAsync(fixture.Clock.GetUtcNow());
        }
        await OwnOrderRebuildTests.PassTheDelayAsync(fixture);
        await OwnOrderRebuildTests.PassTheDelayAsync(fixture);

        // 会话未就绪：不在闸门后面建（审查 M2），记录写明在等会话。
        int createsBefore = fixture.Riot.CreateCount(cargo == "loaded" ? "TO_GATE" : "TO_PICKUP");
        await using (ControlServerDbContext held = new(fixture.DbOptionsForTests))
        {
            OwnOrderRebuildRow waitingRecord = await held.OwnOrderRebuilds.AsNoTracking().SingleAsync(Token);
            Assert.Equal((OwnOrderRebuildStates.Pending, "ONBOARD_SESSION_NOT_READY"), (waitingRecord.State, waitingRecord.WaitingReason));
            Assert.Equal(
                SessionReadiness.RecoveryRequired,
                (await held.SessionRecoveries.AsNoTracking().SingleAsync(Token)).Readiness);
        }

        fixture.Context.ChangeTracker.Clear();
        await fixture.RestoreSessionReadyAsync();
        await OwnOrderRebuildTests.PassTheDelayAsync(fixture);

        Assert.Equal(createsBefore + 1, fixture.Riot.CreateCount(cargo == "loaded" ? "TO_GATE" : "TO_PICKUP"));
        await OwnOrderRebuildTests.AssertRebuiltAsync(fixture, faulted, stopsBefore, current);
        await using ControlServerDbContext after = new(fixture.DbOptionsForTests);
        VehicleFaultStateRow fault = await after.VehicleFaultStates.AsNoTracking().SingleAsync(Token);
        Assert.Equal((VehicleFaultLevel.None, 1L), (fault.Level, fault.FaultGeneration));
    }

    // ---- 续行：PAUSED（7）接到同一个入口 ---------------------------------------------------------------------

    /// <summary>
    /// 故障期间订单被 Hold 成 PAUSED：人确认后续行——服务端发 <c>CONTINUE_FROM_HELD</c>、读回在执行，才清故障；旅程不动，照常往下走。
    /// </summary>
    [Fact]
    public async Task AHeldOrderIsContinuedAndTheFaultClearedOnlyAfterward()
    {
        await using RuntimeFixture fixture = await FaultedOnTheWayToPickupAsync();
        JourneyRuntimeRow faulted = await fixture.RuntimeAsync();
        fixture.Riot.SetOrderState(faulted.PickupUpperId, RiotOrderState.Paused, terminal: false);
        SiteRiot site = new(fixture)
        {
            OnOrderCommand = (kind, _) =>
            {
                if (kind == RiotOrderCommandKind.ContinueFromHeld)
                {
                    fixture.Riot.SetOrderState(faulted.PickupUpperId, RiotOrderState.Executing, terminal: false);
                }
            },
        };

        VehicleFaultRecoveryDecision decision = await Service(fixture, site).RecoverAsync(Resume(fixture), Token);

        Assert.Equal((VehicleFaultRecoveryOutcome.Resumed, 0), (decision.Outcome, decision.Reasons.Count));
        Assert.Equal([RiotOrderCommandKind.ContinueFromHeld], site.OrderCommands);
        VehicleFaultStateRow fault = await FaultAsync(fixture);
        Assert.Equal(VehicleFaultLevel.None, fault.Level);
        Assert.Contains(OperatorId, fault.ClearedReason, StringComparison.Ordinal);
        JourneyRuntimeRow after = await fixture.RuntimeAsync();
        Assert.Equal((faulted.JourneyId, JourneyRuntimeStage.AwaitingPickupArrival), (after.JourneyId, after.Stage));
    }

    /// <summary>
    /// 续行的判据与清除共用署名、确认、闩锁三项，外加 <c>ResumeAsync</c> 自己的判据；订单不是 PAUSED 就拒（FAILED 的单不能续行）。
    /// 闩锁在时不试：闩锁下 RIoT 拒绝 continue，而续行之后订单在跑，人工解除又因「有未完成订单」被拒——一个出不去的环。
    /// </summary>
    [Fact]
    public async Task AResumeIsRefusedForEveryUnmetCriterionIncludingALatch()
    {
        await using RuntimeFixture fixture = await FaultedOnTheWayToPickupAsync();
        SiteRiot site = new(fixture);
        fixture.EmergencyLatched = true;

        VehicleFaultRecoveryDecision decision = await Service(fixture, site).RecoverAsync(
            Resume(fixture) with { FaultRemedied = false }, Token);

        Assert.Equal(VehicleFaultRecoveryOutcome.Refused, decision.Outcome);
        Assert.Equal(
            ["FAULT_RECOVERY_REMEDY_NOT_CONFIRMED", "FAULT_RECOVERY_EMERGENCY_LATCHED", "RESUME_ORDER_NOT_HELD"],
            decision.Reasons);
        Assert.Empty(site.OrderCommands);
        await AssertUntouchedAsync(fixture);
    }

    // ---- 夹具 ----------------------------------------------------------------------------------------------

    /// <summary>假 RIoT 收到 <c>CONTINUE_FROM_HELD</c> 时把那张单置回执行中，像真 RIoT 继续了它一样。</summary>
    private static Action<RiotOrderCommandKind, string> ContinueResumes(RuntimeFixture fixture, string upperId) =>
        (kind, _) =>
        {
            if (kind == RiotOrderCommandKind.ContinueFromHeld)
            {
                fixture.Riot.SetOrderState(upperId, RiotOrderState.Executing, terminal: false);
            }
        };

    private static VehicleFaultRecoveryRequest Clear(RuntimeFixture fixture) => new(
        new EmergencyStopSubject(fixture.Options.AgvId, fixture.Options.VehicleKey),
        VehicleFaultRecoveryAction.ClearFault,
        OperatorId,
        FaultRemedied: true,
        Note: "L1");

    private static VehicleFaultRecoveryRequest Resume(RuntimeFixture fixture) =>
        Clear(fixture) with { Action = VehicleFaultRecoveryAction.ResumeHeldOrder };

    /// <summary>派往取货站；车停在已知站点、运动读数为停止，所以故障协调器不升级急停。然后在途单 FAILED，引擎记下故障。</summary>
    internal static async Task<RuntimeFixture> FaultedOnTheWayToPickupAsync(string? movementState = "MT_FINISHED")
    {
        RuntimeFixture fixture = await DispatchedToPickupAsync();
        fixture.Riot.MovementState = movementState;
        fixture.Riot.FailOrder((await fixture.RuntimeAsync()).PickupUpperId);
        await TickAndRunAsync(fixture);
        Assert.Equal("VEHICLE_ORDER_FAILED", (await fixture.RuntimeAsync()).BlockReasonCode);
        Assert.Equal(VehicleFaultLevel.SuspectedBlocked, (await FaultAsync(fixture)).Level);
        fixture.Context.ChangeTracker.Clear();
        return fixture;
    }

    private static async Task<RuntimeFixture> FaultedOnTheWayToGateAsync()
    {
        RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(FirstDemandId, FirstSublot, createdAt: Now.AddMinutes(-10)));
        fixture.BoxCounts.Set(FirstSublot, 7);
        await fixture.AdvanceToGateArrivalAsync();
        fixture.Riot.MovementState = "MT_FINISHED";
        fixture.Riot.FailOrder((await fixture.RuntimeAsync()).GateUpperId);
        await TickAndRunAsync(fixture);
        Assert.Equal("VEHICLE_ORDER_FAILED", (await fixture.RuntimeAsync()).BlockReasonCode);
        fixture.Context.ChangeTracker.Clear();
        return fixture;
    }

    /// <summary>
    /// Gives the committed load a second target slot, so a snapshot can show one slot of the cargo and not the other. The
    /// fixture loads into one slot; the check reads the load batch's own target slots, so this is all it takes.
    /// </summary>
    private static async Task SecondCargoSlotAsync(RuntimeFixture fixture)
    {
        StationOperationRow load = await fixture.Context.StationOperations
            .SingleAsync(row => row.OperationType == SlotOperationType.Load && row.Status == StationOperationStatus.Committed, Token);
        int[] slots = System.Text.Json.JsonSerializer.Deserialize<int[]>(load.TargetSlotsJson)!;
        if (slots.Length < 2)
        {
            load.TargetSlotsJson = System.Text.Json.JsonSerializer.Serialize(new[] { slots[0], slots[0] == 8 ? 7 : slots[0] + 1 }.Order());
            await fixture.Context.SaveChangesAsync(Token);
        }

        fixture.Context.ChangeTracker.Clear();
    }

    private static async Task<RuntimeFixture> DispatchedToPickupAsync()
    {
        RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(FirstDemandId, FirstSublot, Now.AddMinutes(-10)));
        fixture.BoxCounts.Set(FirstSublot, 7);
        await TickAndRunAsync(fixture);
        Assert.Equal(JourneyRuntimeStage.AwaitingPickupArrival, (await fixture.RuntimeAsync()).Stage);
        fixture.Context.ChangeTracker.Clear();
        return fixture;
    }

    /// <summary>车载端读到车辆安全接口之后会话行变成的样子，照 <c>PickupDispatchPlanPastOwnOrderTests.DropSessionOnOwnOrderAsync</c>。</summary>
    private static async Task DropSessionOnOwnOrderAsync(RuntimeFixture fixture)
    {
        SessionRecoveryRow session = await fixture.Context.SessionRecoveries.SingleAsync(Token);
        session.Readiness = SessionReadiness.RecoveryRequired;
        session.ReasonCode = "DEPARTURE_SAFETY_NOT_READY";
        session.DepartureSafe = false;
        session.SafetyReasonCodesJson = """["VEHICLE_NOT_READY"]""";
        session.SafetyUnknownPresent = true;
        session.UpdatedAt = fixture.Clock.GetUtcNow();
        await fixture.Context.SaveChangesAsync(Token);
        fixture.Context.ChangeTracker.Clear();
    }

    /// <summary>清除之前的样子原封不动：故障在效、旅程在原阶段、没有一条归属被移除、积压行仍是已受理。</summary>
    private static async Task AssertUntouchedAsync(RuntimeFixture fixture)
    {
        await using ControlServerDbContext reading = new(fixture.DbOptionsForTests);
        VehicleFaultStateRow fault = await reading.VehicleFaultStates.AsNoTracking().SingleAsync(Token);
        Assert.Equal(VehicleFaultLevel.SuspectedBlocked, fault.Level);
        Assert.Null(fault.ClearedAt);
        JourneyRuntimeRow journey = await reading.JourneyRuntimes.AsNoTracking().SingleAsync(Token);
        Assert.NotEqual(JourneyRuntimeStage.Completed, journey.Stage);
        Assert.NotEqual(JourneyRuntimeStage.Blocked, journey.Stage);
        Assert.All(
            await reading.Set<JourneyDemandRow>().AsNoTracking().ToArrayAsync(Token),
            membership => Assert.Null(membership.RemovedAt));
        Assert.All(
            await reading.JourneyBacklog.AsNoTracking().ToArrayAsync(Token),
            backlog => Assert.NotNull(backlog.AcceptedAt));
    }

    internal static async Task<VehicleFaultStateRow> FaultAsync(RuntimeFixture fixture)
    {
        await using ControlServerDbContext reading = new(fixture.DbOptionsForTests);
        return await reading.VehicleFaultStates.AsNoTracking().SingleAsync(Token);
    }

    private static async Task<JourneyRuntimeRow[]> JourneysAsync(RuntimeFixture fixture)
    {
        await using ControlServerDbContext reading = new(fixture.DbOptionsForTests);
        return await reading.JourneyRuntimes.AsNoTracking().OrderBy(row => row.JourneyId).ToArrayAsync(Token);
    }

    private static async Task<int> JourneyCountAsync(RuntimeFixture fixture) => (await JourneysAsync(fixture)).Length;

    internal static VehicleFaultRecoveryService Service(
        RuntimeFixture fixture,
        SiteRiot site,
        ControlServerDbContext? context = null,
        IVehicleFaultStore? faultStore = null,
        JourneyMutationGate? gate = null,
        TimeSpan? gateTimeout = null,
        VehicleFaultResumeFlights? flights = null)
    {
        context ??= new ControlServerDbContext(fixture.DbOptionsForTests);
        IVehicleFaultStore faults = faultStore ?? new VehicleFaultStore(context);
        RiotOrderCommandAuditStore audit = new(context);
        EmergencyStopSupervisor supervisor = new(
            site, site, site, audit, faults, Options.Create(new RiotCommandOptions()), fixture.Clock,
            NullLogger<EmergencyStopSupervisor>.Instance);
        IOptions<VehicleFaultOptions> faultOptions = Options.Create(new VehicleFaultOptions());
        VehicleMotionLedger ledger = new(faultOptions);
        VehicleFaultCoordinator coordinator = new(
            faults, site, site, site, audit, new RiotOrderCommandService(site, audit, site, fixture.Clock), supervisor,
            ledger, faultOptions, fixture.Clock, NullLogger<VehicleFaultCoordinator>.Instance);
        return new VehicleFaultRecoveryService(
            context, faults, site, site, site, supervisor, coordinator, ledger, gate ?? new JourneyMutationGate(),
            flights ?? new VehicleFaultResumeFlights(), Options.Create(fixture.Options),
            fixture.Clock, NullLogger<VehicleFaultRecoveryService>.Instance, gateTimeout);
    }

    /// <summary>
    /// RIoT 的现场一侧：急停状态跟着夹具的 <see cref="RuntimeFixture.EmergencyLatched"/>（引擎里的故障协调器读的也是它），
    /// 车上有没有未完成订单由用例定，订单读法转给夹具的 RIoT 替身（可被用例覆盖成 NotFound），订单与急停命令只记录。
    /// </summary>
    internal sealed class SiteRiot(RuntimeFixture fixture)
        : IRiotOrderCommandGateway, IRiotVehicleEmergencyFacts, IRiotVehicleOrderFacts, IRiotMovementGateway, IVehicleMotionFacts
    {
        public bool? HasUnfinishedOrder { get; set; } = false;

        public bool EmergencyUnreadable { get; set; }

        public RiotOrderObservation? OrderReadOverride { get; set; }

        public Action<RiotOrderCommandKind, string>? OnOrderCommand { get; set; }

        public List<RiotOrderCommandKind> OrderCommands { get; } = [];

        public List<RiotEmergencyCommandKind> EmergencyCommands { get; } = [];

        /// <summary>设了就在每次 RIoT 调用时试拿一下这把锁：拿不到，说明调用方正持着它（独立审查 M2）。</summary>
        public JourneyMutationGate? Gate { get; set; }

        public int RiotCalls { get; private set; }

        public List<string> CallsUnderGate { get; } = [];

        /// <summary>读完「车上有没有未完成订单」之后做的事：用来模拟读 RIoT 与拿锁之间引擎推进了一轮。</summary>
        public Action? AfterUnfinishedOrdersRead { get; set; }

        public Task<RiotCommandCallResult> IssueOrderCommandAsync(
            RiotOrderCommandKind kind, string orderId, string? reason, CancellationToken cancellationToken)
        {
            Called($"order command {kind}");
            OrderCommands.Add(kind);
            OnOrderCommand?.Invoke(kind, orderId);
            return Task.FromResult(new RiotCommandCallResult(
                RiotCommandCallDisposition.Accepted,
                new RiotOrderCallReceipt(RiotCommandTypeNames.For(kind), "SdkAccepted", fixture.Clock.GetUtcNow())));
        }

        public Task<RiotCommandCallResult> IssueEmergencyCommandAsync(
            RiotEmergencyCommandKind kind, string deviceKey, CancellationToken cancellationToken)
        {
            Called($"emergency command {kind}");
            EmergencyCommands.Add(kind);
            return Task.FromResult(new RiotCommandCallResult(
                RiotCommandCallDisposition.Accepted,
                new RiotOrderCallReceipt(RiotCommandTypeNames.For(kind), "SdkAccepted", fixture.Clock.GetUtcNow())));
        }

        public Task<RiotVehicleEmergencyObservation> ReadEmergencyStateAsync(string deviceKey, CancellationToken cancellationToken)
        {
            Called("emergency read");
            return Task.FromResult(new RiotVehicleEmergencyObservation(
                deviceKey,
                EmergencyUnreadable ? null
                    : fixture.EmergencyLatched ? RiotVehicleEmergencyObservation.CanRecover
                    : RiotVehicleEmergencyObservation.Ok,
                fixture.Clock.GetUtcNow()));
        }

        public Task<RiotVehicleOrderObservation> ReadUnfinishedOrdersAsync(string deviceKey, CancellationToken cancellationToken)
        {
            Called("unfinished orders read");
            RiotVehicleOrderObservation observation = new(
                deviceKey, HasUnfinishedOrder, HasUnfinishedOrder == true ? ["ORDER-UNFINISHED-1"] : [], fixture.Clock.GetUtcNow());
            AfterUnfinishedOrdersRead?.Invoke();
            return Task.FromResult(observation);
        }

        public Task<RiotOrderObservation> ReconcileByUpperIdAsync(string upperId, CancellationToken cancellationToken)
        {
            Called("order read");
            return OrderReadOverride is { } overridden && overridden.UpperId == upperId
                ? Task.FromResult(overridden)
                : fixture.Riot.ReconcileByUpperIdAsync(upperId, cancellationToken);
        }

        public Task<RiotOrderObservation> CreateAsync(OrderIntent intent, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Recovering a fault never creates an order.");

        public Task<RiotVehicleObservation> ReadVehicleAsync(string vehicleKey, CancellationToken cancellationToken)
        {
            Called("vehicle read");
            return fixture.Riot.ReadVehicleAsync(vehicleKey, cancellationToken);
        }

        public Task<VehicleMotionSample> SampleMotionAsync(string deviceKey, CancellationToken cancellationToken)
        {
            Called("motion sample");
            return fixture.Riot.SampleMotionAsync(deviceKey, cancellationToken);
        }

        private void Called(string what)
        {
            RiotCalls++;
            if (Gate is null)
            {
                return;
            }

            using IDisposable? free = Gate.TryEnterAsync(TimeSpan.Zero, CancellationToken.None).GetAwaiter().GetResult();
            if (free is null)
            {
                CallsUnderGate.Add(what);
            }
        }
    }

    /// <summary>故障存储：清除写下之后、提交之前「进程没了」。</summary>
    private sealed class CrashAfterClearStore(IVehicleFaultStore inner) : IVehicleFaultStore
    {
        public bool Cleared { get; private set; }

        public async Task ClearAsync(string agvId, long faultGeneration, string reason, DateTimeOffset at, CancellationToken cancellationToken)
        {
            await inner.ClearAsync(agvId, faultGeneration, reason, at, cancellationToken);
            Cleared = true;
            throw new IOException("The process stopped after the clearance was written and before it was committed.");
        }

        public Task<VehicleFaultFact?> ReadAsync(string agvId, CancellationToken cancellationToken) =>
            inner.ReadAsync(agvId, cancellationToken);

        public Task<VehicleFaultFact> RecordLevelAsync(
            string agvId, VehicleFaultLevel level, string evidenceCode, bool evidenceOnAutoConfirmWhitelist, DateTimeOffset at,
            CancellationToken cancellationToken) =>
            inner.RecordLevelAsync(agvId, level, evidenceCode, evidenceOnAutoConfirmWhitelist, at, cancellationToken);

        public Task RecordStopProofAsync(string agvId, long faultGeneration, bool proven, DateTimeOffset at, CancellationToken cancellationToken) =>
            inner.RecordStopProofAsync(agvId, faultGeneration, proven, at, cancellationToken);

        public Task RecordEscalationAsync(string agvId, long faultGeneration, DateTimeOffset at, CancellationToken cancellationToken) =>
            inner.RecordEscalationAsync(agvId, faultGeneration, at, cancellationToken);

        public Task<FaultedCargoBinding> BindCargoAsync(
            string agvId, long faultGeneration, string demandId, string? movementLegId, string transportDemandKey,
            bool loadingWitnessed, DateTimeOffset boundAt, CancellationToken cancellationToken) =>
            inner.BindCargoAsync(agvId, faultGeneration, demandId, movementLegId, transportDemandKey, loadingWitnessed, boundAt, cancellationToken);

        public Task<FaultedCargoBinding?> ReadLiveCargoAsync(string agvId, CancellationToken cancellationToken) =>
            inner.ReadLiveCargoAsync(agvId, cancellationToken);

        public Task ReleaseCargoAsync(string cargoBindingId, string reason, DateTimeOffset releasedAt, CancellationToken cancellationToken) =>
            inner.ReleaseCargoAsync(cargoBindingId, reason, releasedAt, cancellationToken);
    }
}

/// <summary>持锁者扫描器的正例：它调了 <c>EnterAsync</c>，扫描本测试程序集时必须被认出来。</summary>
internal static class GateHolderProbe
{
    internal static async Task HoldAsync(JourneyMutationGate gate)
    {
        using IDisposable held = await gate.EnterAsync(CancellationToken.None);
    }
}
