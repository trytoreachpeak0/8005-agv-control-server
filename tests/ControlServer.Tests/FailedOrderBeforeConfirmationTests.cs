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
/// control-server#367：普通腿（不是重建出来的单）建单应答丢了、单还没确认，RIoT 就报 FAILED。会话闸门前、闸门后都要在同一轮记故障、
/// Hold、急停，并照常推进急停确认与 REQ-0248 重触发；这个故障要能经受控的清除入口清掉，清掉之后按 REQ-0362 同车重建。
/// </summary>
/// <remarks>
/// <para>
/// <b>修之前</b>：对账把这张单判成终态，建单确认只写 <c>{PICKUP|GATE}_TerminalReconciliationRequired</c> 并结束这一轮
/// （<c>JourneyRuntimeEngine.EnsureMovementConfirmedAsync</c>），走不到到站分支里的故障观测；闸门后
/// （<c>NameInFlightOrderWithoutThePeerAsync</c>）只读已确认的意图，这张单连读都不读，也不去对账。于是单已经 FAILED、车可能还在动，
/// 服务端不记故障、不 Hold、不急停。清除入口（<c>VehicleFaultRecoveryService.ReadAsync</c>）有意不读普通腿的终态意图，所以只修引擎那一半，
/// 这个故障就没有入口能清，车一直挂着故障，只能改库。
/// </para>
/// <para>
/// <b>闸门后是这张票最常见的情形</b>：真车载端在本服务端的单还在 RIoT 上未终结时整段报 <c>DEPARTURE_SAFETY_NOT_READY</c>，
/// 夹具照 <c>OwnOrderRebuildTests.DropSessionOnOwnOrderAsync</c> 把会话放成这个样子。闸门后的这几条都断车载端出站为空。
/// </para>
/// <para>
/// 断言落在故障状态与命令审计这一层（与 <see cref="FailedOrderBehindSessionGateTests"/> 同一套），旅程码只作旁证。
/// </para>
/// </remarks>
public sealed class FailedOrderBeforeConfirmationTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ---- 闸门前 ------------------------------------------------------------------------------------------------

    /// <summary>
    /// 会话就绪：开往取货站的单建单应答丢了，下一轮对账读到它 FAILED、车还在动。同一轮记故障、Hold、升级急停，旅程写
    /// <c>VEHICLE_ORDER_FAILED</c>；意图照旧是终态对账（建单确认流程本身不改）。
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-0232")]
    [Trait("Requirement", "REQ-0234")]
    [Trait("Requirement", "REQ-0246")]
    public async Task APickupOrderThatFailedBeforeConfirmationIsRecordedHeldAndStoppedInFrontOfTheGate()
    {
        await using RuntimeFixture fixture = await PickupCreateAnswerLostAsync();
        JourneyRuntimeRow dispatched = await fixture.RuntimeAsync();
        fixture.Riot.MovementState = "MT_RUNNING";
        fixture.Riot.FailOrder(dispatched.PickupUpperId);

        await TickAndHearAsync(fixture);

        await AssertSessionReadyAsync(fixture);
        await AssertRecordedHeldAndStoppedAsync(fixture, dispatched.PickupUpperId);
        Assert.Equal("TERMINAL_RECONCILIATION_REQUIRED", (await IntentAsync(fixture, dispatched.PickupUpperId)).Status);
        Assert.Equal(1, fixture.Riot.CreateCount("TO_PICKUP"));
    }

    /// <summary>
    /// 同一件事在装货之后、开往关卡的腿上：车上有货，另外把货与需求绑住（REQ-0238）。
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-0232")]
    [Trait("Requirement", "REQ-0238")]
    [Trait("Requirement", "REQ-0246")]
    public async Task AGateOrderThatFailedBeforeConfirmationAlsoBindsTheCargoInFrontOfTheGate()
    {
        await using RuntimeFixture fixture = await GateCreateAnswerLostAsync();
        JourneyRuntimeRow underWay = await fixture.RuntimeAsync();
        fixture.Riot.MovementState = "MT_RUNNING";
        fixture.Riot.FailOrder(underWay.GateUpperId);

        await TickAndHearAsync(fixture);

        await AssertSessionReadyAsync(fixture);
        await AssertRecordedHeldAndStoppedAsync(fixture, underWay.GateUpperId);
        await AssertCargoBoundAsync(fixture, underWay.DemandId);
    }

    // ---- 闸门后：会话因本车在途单未就绪 ----------------------------------------------------------------------

    /// <summary>
    /// 会话因本车在途单未就绪，建单应答丢了的那张单一直没被对账过（意图停在 <c>RESULT_UNKNOWN</c>）：闸门后这一轮去对账，读到 FAILED、
    /// 车还在动，同一轮记故障、Hold、急停。会话仍未就绪，车载端这一轮一条都没收到，也没有建第二张单。
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-0232")]
    [Trait("Requirement", "REQ-0246")]
    [Trait("Requirement", "REQ-0287")]
    public async Task AnUnreconciledPickupOrderThatFailedIsRecordedHeldAndStoppedBehindTheGate()
    {
        await using RuntimeFixture fixture = await PickupCreateAnswerLostAsync();
        JourneyRuntimeRow dispatched = await fixture.RuntimeAsync();
        await OwnOrderRebuildTests.DropSessionOnOwnOrderAsync(fixture);
        fixture.Riot.MovementState = "MT_RUNNING";
        fixture.Riot.FailOrder(dispatched.PickupUpperId);
        Outbound before = await OutboundAsync(fixture);

        await TickAndHearAsync(fixture);

        await AssertSessionStillNotReadyAsync(fixture);
        await AssertRecordedHeldAndStoppedAsync(fixture, dispatched.PickupUpperId);
        Assert.Equal(1, fixture.Riot.CreateCount("TO_PICKUP"));
        Assert.Equal(before, await OutboundAsync(fixture));
    }

    /// <summary>
    /// 同一件事在关卡腿上、闸门后：车上有货，货与需求同一轮绑住；车载端一条都没收到。
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-0232")]
    [Trait("Requirement", "REQ-0238")]
    [Trait("Requirement", "REQ-0246")]
    public async Task AnUnreconciledGateOrderThatFailedAlsoBindsTheCargoBehindTheGate()
    {
        await using RuntimeFixture fixture = await GateCreateAnswerLostAsync();
        JourneyRuntimeRow underWay = await fixture.RuntimeAsync();
        await OwnOrderRebuildTests.DropSessionOnOwnOrderAsync(fixture);
        fixture.Riot.MovementState = "MT_RUNNING";
        fixture.Riot.FailOrder(underWay.GateUpperId);
        Outbound before = await OutboundAsync(fixture);

        await TickAndHearAsync(fixture);

        await AssertSessionStillNotReadyAsync(fixture);
        await AssertRecordedHeldAndStoppedAsync(fixture, underWay.GateUpperId);
        await AssertCargoBoundAsync(fixture, underWay.DemandId);
        Assert.Equal(before, await OutboundAsync(fixture));
    }

    /// <summary>
    /// 失联那一路（cs#358 的第二个位置）：会话行仍是 Ready，但车载端 6 秒没有任何入站，旅程写 <c>ONBOARD_SESSION_LOST</c>。这时建单应答丢了的那张单
    /// 被报 FAILED、车在动：同一轮记故障、Hold、急停，车载端一条都没收到。
    /// </summary>
    /// <remarks>
    /// 前提先断：注入 FAILED 之前那一轮旅程写的是 <c>ONBOARD_SESSION_LOST</c>、会话行是 Ready——这条走的确实是失联截停，不是会话闸门。
    /// </remarks>
    [Fact]
    [Trait("Requirement", "REQ-0232")]
    [Trait("Requirement", "REQ-0246")]
    public async Task AnUnconfirmedOrderThatFailedIsRecordedHeldAndStoppedWhileTheReadySessionIsSilent()
    {
        await using RuntimeFixture fixture = await PickupCreateAnswerLostAsync();
        JourneyRuntimeRow dispatched = await fixture.RuntimeAsync();
        await fixture.HearFromPeerAsync();
        fixture.Clock.Advance(SessionLiveness.Timeout + TimeSpan.FromSeconds(2));
        await TickSilentAsync(fixture);
        Assert.Equal(JourneyRuntimeEngine.OnboardSessionLostReason, (await fixture.RuntimeAsync()).BlockReasonCode);
        await AssertSessionReadyAsync(fixture);

        fixture.Riot.MovementState = "MT_RUNNING";
        fixture.Riot.FailOrder(dispatched.PickupUpperId);
        Outbound before = await OutboundAsync(fixture);
        await TickSilentAsync(fixture);

        await AssertSessionReadyAsync(fixture);
        await AssertRecordedHeldAndStoppedAsync(fixture, dispatched.PickupUpperId);
        Assert.Equal(1, fixture.Riot.CreateCount("TO_PICKUP"));
        Assert.Equal(before, await OutboundAsync(fixture));
    }

    /// <summary>
    /// 意图已经是终态对账、却没有故障的旅程——修之前的版本留在库里的样子（码 <c>PICKUP_TerminalReconciliationRequired</c>），或者在
    /// 「意图写成终态」与「故障观测」两次保存之间崩掉的样子。会话未就绪：闸门后这一轮照样把它交给故障模型。
    /// </summary>
    /// <remarks>
    /// 这个状态直接写进库里造，不靠跑一轮：修前修后跑同一轮得到的状态不一样（修后那一轮自己就记了故障），用例要的是同一个起点。
    /// </remarks>
    [Fact]
    [Trait("Requirement", "REQ-0232")]
    [Trait("Requirement", "REQ-0246")]
    public async Task ATerminalReconciledPickupOrderLeftWithoutAFaultIsRecordedBehindTheGate()
    {
        await using RuntimeFixture fixture = await PickupCreateAnswerLostAsync();
        JourneyRuntimeRow dispatched = await fixture.RuntimeAsync();
        fixture.Riot.MovementState = "MT_RUNNING";
        fixture.Riot.FailOrder(dispatched.PickupUpperId);
        await LeaveTerminalReconciledWithoutAFaultAsync(fixture, dispatched.PickupUpperId, "PICKUP");
        await OwnOrderRebuildTests.DropSessionOnOwnOrderAsync(fixture);
        Outbound before = await OutboundAsync(fixture);

        await TickAndHearAsync(fixture);

        await AssertSessionStillNotReadyAsync(fixture);
        await AssertRecordedHeldAndStoppedAsync(fixture, dispatched.PickupUpperId);
        Assert.Equal(before, await OutboundAsync(fixture));
    }

    /// <summary>
    /// 闸门后记下故障、急停触发 Pending 之后，会话一直未就绪：RIoT 锁上的那一轮把触发确认成 Confirmed、停车证明照常采样；闩锁在故障未清、
    /// 也没有人工解除时掉了，下一轮按 REQ-0248 重触发。旅程码一直是 <c>VEHICLE_ORDER_FAILED</c>，车载端一条都没收到。
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-0246")]
    [Trait("Requirement", "REQ-0248")]
    public async Task BehindTheGateTheStopIsConfirmedAndRetriggeredForAnOrderThatFailedBeforeConfirmation()
    {
        await using RuntimeFixture fixture = await PickupCreateAnswerLostAsync();
        JourneyRuntimeRow dispatched = await fixture.RuntimeAsync();
        await OwnOrderRebuildTests.DropSessionOnOwnOrderAsync(fixture);
        fixture.Riot.MovementState = "MT_RUNNING";
        fixture.Riot.FailOrder(dispatched.PickupUpperId);
        Outbound before = await OutboundAsync(fixture);
        await TickAndHearAsync(fixture);
        Assert.Equal(RiotOrderCommandOutcome.Pending, await LatestTriggerOutcomeAsync(fixture));

        Latch(fixture);
        fixture.Riot.MovementState = "MT_FINISHED";
        await TickAndHearAsync(fixture);
        await AssertSessionStillNotReadyAsync(fixture);
        Assert.Equal(RiotOrderCommandOutcome.Confirmed, await LatestTriggerOutcomeAsync(fixture));
        Assert.Equal(VehicleFaultEvidence.OrderFailed, (await fixture.RuntimeAsync()).BlockReasonCode);
        await AssertStopProofSampledThisRoundAsync(fixture);

        Unlatch(fixture);
        await TickAndHearAsync(fixture);
        await AssertSessionStillNotReadyAsync(fixture);
        Assert.Equal(2, await TriggerCountAsync(fixture));
        Assert.Equal(VehicleFaultEvidence.OrderFailed, (await fixture.RuntimeAsync()).BlockReasonCode);
        await AssertStopProofSampledThisRoundAsync(fixture);

        Assert.Equal(before, await OutboundAsync(fixture));
    }

    /// <summary>
    /// 闸门后绝不建单：取货腿的意图已经定下、建单从没发出过（「定了、没建」那个崩溃点：意图保存之后、问 RIoT 之前进程停了），RIoT 上没有这张单。
    /// 会话因本车在途单未就绪的这几轮，不对账、不建单，旅程写闸门自己的码；会话回到就绪、闸门前那一轮才建。
    /// </summary>
    /// <remarks>
    /// 本票在闸门后新加了对账，而对账在「待对账、一次没发过、RIoT 上没有」时会建单。闸门后只对发过建单的意图对账，守的就是这一格：
    /// 去掉那道判断，会话未就绪的车会被派出一张新单开走——准入线第 1 条「车在无人预期时移动」。
    /// </remarks>
    [Fact]
    public async Task AnOrderNeverSentIsNotCreatedBehindTheGate()
    {
        await using RuntimeFixture fixture = await PickupCreateAnswerLostAsync();
        JourneyRuntimeRow dispatched = await fixture.RuntimeAsync();
        OrderIntentRow intent = await fixture.Context.OrderIntents.SingleAsync(row => row.UpperId == dispatched.PickupUpperId, Token);
        intent.Status = "PENDING_RECONCILIATION";
        intent.CreateAttemptCount = 0;
        intent.CreateAttemptId = null;
        await fixture.Context.SaveChangesAsync(Token);
        fixture.Context.ChangeTracker.Clear();
        fixture.Riot.ForgetOrder(dispatched.PickupUpperId);
        await OwnOrderRebuildTests.DropSessionOnOwnOrderAsync(fixture);
        Outbound before = await OutboundAsync(fixture);

        await TickAndHearAsync(fixture);
        await TickAndHearAsync(fixture);

        Assert.Equal(1, fixture.Riot.CreateCount("TO_PICKUP"));
        Assert.Equal("ONBOARD_SESSION_NOT_READY", (await fixture.RuntimeAsync()).BlockReasonCode);
        Assert.Equal("PENDING_RECONCILIATION", (await IntentAsync(fixture, dispatched.PickupUpperId)).Status);
        Assert.Equal(before, await OutboundAsync(fixture));
    }

    /// <summary>
    /// 闸门后对账确认了建单应答丢失的那张单，它此刻在 RIoT 上挂起（HANG）：意图确认、旅程写 <c>ORDER_HANG</c>，与确认过的单一样起名；
    /// 不记故障、不发命令。
    /// </summary>
    [Fact]
    public async Task AnUnconfirmedOrderFoundHangingBehindTheGateIsConfirmedAndNamed()
    {
        await using RuntimeFixture fixture = await PickupCreateAnswerLostAsync();
        JourneyRuntimeRow dispatched = await fixture.RuntimeAsync();
        await OwnOrderRebuildTests.DropSessionOnOwnOrderAsync(fixture);
        fixture.Riot.SetOrderState(dispatched.PickupUpperId, RiotOrderState.Hang, terminal: false);
        Outbound before = await OutboundAsync(fixture);

        await TickAndHearAsync(fixture);

        Assert.Equal("CONFIRMED", (await IntentAsync(fixture, dispatched.PickupUpperId)).Status);
        Assert.Equal(JourneyRuntimeEngine.OrderHangReason, (await fixture.RuntimeAsync()).BlockReasonCode);
        await using (ControlServerDbContext reading = new(fixture.DbOptionsForTests))
        {
            Assert.Empty(await reading.VehicleFaultStates.AsNoTracking().ToArrayAsync(Token));
            Assert.Empty(await reading.RiotOrderCommandAudit.AsNoTracking().ToArrayAsync(Token));
        }

        Assert.Equal(before, await OutboundAsync(fixture));
    }

    /// <summary>
    /// 记下故障之后，一轮读不到这张单（RIoT 不回答）：码仍是 <c>VEHICLE_ORDER_FAILED</c>、开始时刻不动，闸门前后都一样——FAILED 是终态，
    /// 读不到不说明它变了。
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnUnreadableOrderKeepsTheFailedCodeAndItsStart(bool behindTheGate)
    {
        await using RuntimeFixture fixture = await PickupCreateAnswerLostAsync();
        JourneyRuntimeRow dispatched = await fixture.RuntimeAsync();
        if (behindTheGate)
        {
            await OwnOrderRebuildTests.DropSessionOnOwnOrderAsync(fixture);
        }

        fixture.Riot.FailOrder(dispatched.PickupUpperId);
        await TickAndHearAsync(fixture);
        JourneyRuntimeRow failed = await fixture.RuntimeAsync();
        Assert.Equal(VehicleFaultEvidence.OrderFailed, failed.BlockReasonCode);

        fixture.Riot.MakeOrderUnreadable(dispatched.PickupUpperId);
        await TickAndHearAsync(fixture);

        JourneyRuntimeRow after = await fixture.RuntimeAsync();
        Assert.True(failed.BlockReasonSince < fixture.Clock.GetUtcNow(), "the clock did not move, so keeping the start time proves nothing");
        Assert.Equal(
            (VehicleFaultEvidence.OrderFailed, failed.BlockReasonSince),
            (after.BlockReasonCode, after.BlockReasonSince));
    }

    // ---- 清除与重建 ----------------------------------------------------------------------------------------------

    /// <summary>
    /// 车上无货：引擎为确认前 FAILED 的取货单记下的故障，经受控入口清除被接受；延迟过后同车同需求重建开往同一取货站的单（REQ-0362），
    /// 重建记录指着那张确认前就 FAILED 的单，来源是现有的「故障清除、车上无货」。
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-0362")]
    public async Task AClearedFaultOfAPickupOrderThatFailedBeforeConfirmationRebuildsItOnTheSameVehicle()
    {
        await using RuntimeFixture fixture = await PickupCreateAnswerLostAsync();
        JourneyRuntimeRow dispatched = await fixture.RuntimeAsync();
        fixture.Riot.FailOrder(dispatched.PickupUpperId);
        await TickAndHearAsync(fixture);
        Assert.Equal(VehicleFaultLevel.SuspectedBlocked, (await VehicleFaultRecoveryTests.FaultAsync(fixture)).Level);

        VehicleFaultRecoveryDecision decision = await VehicleFaultRecoveryTests
            .Service(fixture, new VehicleFaultRecoveryTests.SiteRiot(fixture))
            .RecoverAsync(VehicleFaultRecoveryTests.Clear(fixture), Token);
        fixture.Context.ChangeTracker.Clear();
        await OwnOrderRebuildTests.PassTheDelayAsync(fixture);

        Assert.Equal(
            (VehicleFaultRecoveryOutcome.Cleared, VehicleFaultRecoveryDispositions.RebuildScheduled),
            (decision.Outcome, decision.Disposition));
        Assert.Equal(2, fixture.Riot.CreateCount("TO_PICKUP"));
        await using ControlServerDbContext reading = new(fixture.DbOptionsForTests);
        OwnOrderRebuildRow record = await reading.OwnOrderRebuilds.AsNoTracking().SingleAsync(Token);
        Assert.Equal(
            (dispatched.PickupUpperId, OwnOrderRebuildSources.FaultClearedNothingOnBoard, OwnOrderRebuildStates.Rebuilt),
            (record.EndedUpperId, record.Source, record.State));
        Assert.Equal(VehicleFaultLevel.None, (await reading.VehicleFaultStates.AsNoTracking().SingleAsync(Token)).Level);
    }

    /// <summary>
    /// 车上有货：清除被接受，货物绑定保留；先等车报一份清除之后的快照证明货在原仓位（REQ-0362 保留的 REQ-0238 前提），才重建开往同一关卡的单，
    /// 货物绑定随之以 <c>REBUILT_ON_ORIGINAL_VEHICLE</c> 了结。
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-0362")]
    public async Task AClearedFaultOfAGateOrderThatFailedBeforeConfirmationRebuildsOnlyOnCargoEvidence()
    {
        await using RuntimeFixture fixture = await GateCreateAnswerLostAsync();
        JourneyRuntimeRow underWay = await fixture.RuntimeAsync();
        fixture.Riot.FailOrder(underWay.GateUpperId);
        await TickAndHearAsync(fixture);
        Assert.Equal(VehicleFaultLevel.SuspectedBlocked, (await VehicleFaultRecoveryTests.FaultAsync(fixture)).Level);
        int gateCreates = fixture.Riot.CreateCount("TO_GATE");

        VehicleFaultRecoveryDecision decision = await VehicleFaultRecoveryTests
            .Service(fixture, new VehicleFaultRecoveryTests.SiteRiot(fixture))
            .RecoverAsync(VehicleFaultRecoveryTests.Clear(fixture), Token);
        fixture.Context.ChangeTracker.Clear();
        await OwnOrderRebuildTests.PassTheDelayAsync(fixture);
        await OwnOrderRebuildTests.PassTheDelayAsync(fixture);

        Assert.Equal(
            (VehicleFaultRecoveryOutcome.Cleared, VehicleFaultRecoveryDispositions.RebuildScheduled),
            (decision.Outcome, decision.Disposition));
        Assert.Equal(gateCreates, fixture.Riot.CreateCount("TO_GATE"));
        Assert.Equal(
            JourneyRuntimeEngine.OwnOrderRebuildWaitingCargoEvidenceReason, (await fixture.RuntimeAsync()).BlockReasonCode);

        await fixture.AddCargoSnapshotAsync(fixture.Clock.GetUtcNow());
        await fixture.HearFromPeerAsync();
        await fixture.Engine.ExecuteOnceAsync(Token);
        fixture.Context.ChangeTracker.Clear();

        Assert.Equal(gateCreates + 1, fixture.Riot.CreateCount("TO_GATE"));
        await using ControlServerDbContext reading = new(fixture.DbOptionsForTests);
        OwnOrderRebuildRow record = await reading.OwnOrderRebuilds.AsNoTracking().SingleAsync(Token);
        Assert.Equal(
            (underWay.GateUpperId, OwnOrderRebuildSources.FaultClearedCargoOnBoard, OwnOrderRebuildStates.Rebuilt),
            (record.EndedUpperId, record.Source, record.State));
        FaultedVehicleCargoRow cargo = await reading.FaultedVehicleCargo.AsNoTracking().SingleAsync(Token);
        Assert.Equal(JourneyRuntimeEngine.CargoRebuiltOnOriginalVehicleReason, cargo.ReleasedReason);
    }

    /// <summary>
    /// 反向：引擎曾为这张确认前 FAILED 的单记过故障（审计里有那一代的 Hold），那一次已经清掉；眼下这辆车身上的故障是别的来路记的、没有
    /// 对这张单的 Hold。清除入口不经这张终态意图放行它：以 <c>FAULT_RECOVERY_CURRENT_ORDER_UNKNOWN</c> 拒绝，不清故障、不记重建。
    /// </summary>
    /// <remarks>
    /// 认法按故障代次：一张单被引擎喂过故障模型，说明的只是<b>那一代</b>故障是它造成的。只看「有没有对这张单的 Hold」、不看代次，这条会被放行。
    /// </remarks>
    [Fact]
    public async Task AFaultOfAnotherOriginIsNotClearedThroughATerminalIntentAnEarlierFaultWasRecordedOn()
    {
        await using RuntimeFixture fixture = await PickupCreateAnswerLostAsync();
        JourneyRuntimeRow dispatched = await fixture.RuntimeAsync();
        await LeaveTerminalReconciledWithoutAFaultAsync(fixture, dispatched.PickupUpperId, "PICKUP");
        VehicleFaultStore store = new(fixture.Context);
        VehicleFaultFact earlier = await store.RecordLevelAsync(
            dispatched.AgvId, VehicleFaultLevel.SuspectedBlocked, VehicleFaultEvidence.OrderFailed, false, fixture.Clock.GetUtcNow(), Token);
        fixture.Context.RiotOrderCommandAudit.Add(new RiotOrderCommandAuditRow
        {
            CommandAuditId = Guid.NewGuid().ToString("D"),
            CommandType = RiotCommandTypeNames.OrderHold,
            AgvId = dispatched.AgvId,
            TargetUpperId = dispatched.PickupUpperId,
            TargetOrderId = fixture.Riot.OrderIdOf(dispatched.PickupUpperId),
            AttemptNumber = 1,
            RequestSemanticSha256 = new string('0', 64),
            IssuedAt = fixture.Clock.GetUtcNow(),
            Outcome = RiotOrderCommandOutcome.Failed,
            FaultGeneration = earlier.FaultGeneration,
        });
        await fixture.Context.SaveChangesAsync(Token);
        await store.ClearAsync(dispatched.AgvId, earlier.FaultGeneration, "L1_EARLIER_EPISODE", fixture.Clock.GetUtcNow(), Token);
        VehicleFaultFact other = await store.RecordLevelAsync(
            dispatched.AgvId, VehicleFaultLevel.SuspectedBlocked, "VEHICLE_ORDER_FAILED", false, fixture.Clock.GetUtcNow(), Token);
        Assert.True(other.FaultGeneration > earlier.FaultGeneration, "the second fault did not open a new generation");
        fixture.Context.ChangeTracker.Clear();

        VehicleFaultRecoveryDecision decision = await VehicleFaultRecoveryTests
            .Service(fixture, new VehicleFaultRecoveryTests.SiteRiot(fixture))
            .RecoverAsync(VehicleFaultRecoveryTests.Clear(fixture), Token);

        Assert.Equal(VehicleFaultRecoveryOutcome.Refused, decision.Outcome);
        Assert.Contains("FAULT_RECOVERY_CURRENT_ORDER_UNKNOWN", decision.Reasons);
        await using ControlServerDbContext reading = new(fixture.DbOptionsForTests);
        Assert.Equal(VehicleFaultLevel.SuspectedBlocked, (await reading.VehicleFaultStates.AsNoTracking().SingleAsync(Token)).Level);
        Assert.Empty(await reading.OwnOrderRebuilds.AsNoTracking().ToArrayAsync(Token));
    }

    // ---- 确认前被取消、删除、SUSPENDED（调度 2026-09-28 并入本票） ------------------------------------------------

    /// <summary>
    /// 建单应答丢了的取货单，确认前在 RIoT 被取消（本服务端没发过取消）：按 REQ-0360 当误操作，登记同车同需求重建，旅程写
    /// <c>ORDER_ENDED_WITHOUT_ARRIVAL</c>；延迟过后重建开往同一取货站的单。不记故障、不发命令。
    /// </summary>
    /// <remarks>
    /// 修之前这一格停在 <c>PICKUP_TerminalReconciliationRequired</c>，重建接不上（登记重建要求意图已确认），没有故障可清，
    /// 只能改库——准入线第 3 条。
    /// </remarks>
    [Fact]
    [Trait("Requirement", "REQ-0360")]
    public async Task APickupOrderCancelledBeforeConfirmationIsRebuiltOnTheSameVehicle()
    {
        await using RuntimeFixture fixture = await PickupCreateAnswerLostAsync();
        JourneyRuntimeRow dispatched = await fixture.RuntimeAsync();
        fixture.Riot.CancelOrder(dispatched.PickupUpperId);

        await TickAndHearAsync(fixture);

        Assert.Equal(JourneyRuntimeEngine.OrderEndedWithoutArrivalReason, (await fixture.RuntimeAsync()).BlockReasonCode);
        await using (ControlServerDbContext reading = new(fixture.DbOptionsForTests))
        {
            OwnOrderRebuildRow recorded = await reading.OwnOrderRebuilds.AsNoTracking().SingleAsync(Token);
            Assert.Equal(
                (dispatched.PickupUpperId, OwnOrderRebuildSources.CancelledInRiot, OwnOrderRebuildStates.Pending),
                (recorded.EndedUpperId, recorded.Source, recorded.State));
            Assert.Empty(await reading.VehicleFaultStates.AsNoTracking().ToArrayAsync(Token));
            Assert.Empty(await reading.RiotOrderCommandAudit.AsNoTracking().ToArrayAsync(Token));
        }

        // REQ-0361's delay: nothing is created before it is over, although the vehicle is stopped and the session Ready.
        await TickAndHearAsync(fixture);
        Assert.Equal(1, fixture.Riot.CreateCount("TO_PICKUP"));

        await OwnOrderRebuildTests.PassTheDelayAsync(fixture);
        fixture.Context.ChangeTracker.Clear();

        Assert.Equal(2, fixture.Riot.CreateCount("TO_PICKUP"));
        await using ControlServerDbContext after = new(fixture.DbOptionsForTests);
        Assert.Equal(OwnOrderRebuildStates.Rebuilt, (await after.OwnOrderRebuilds.AsNoTracking().SingleAsync(Token)).State);
    }

    /// <summary>
    /// 同一件事在闸门后，单是被删除（DELETED）：会话因本车在途单未就绪，这一轮照样登记重建、写码；延迟过了、会话仍没就绪，就不建新单，
    /// 车载端一条都没收到。会话回到就绪之后建单是 cs#318 既有的那一段，这里不重复断。
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-0360")]
    public async Task APickupOrderDeletedBeforeConfirmationIsRecordedBehindTheGateAndNothingIsCreatedWhileNotReady()
    {
        await using RuntimeFixture fixture = await PickupCreateAnswerLostAsync();
        JourneyRuntimeRow dispatched = await fixture.RuntimeAsync();
        await OwnOrderRebuildTests.DropSessionOnOwnOrderAsync(fixture);
        fixture.Riot.SetOrderState(dispatched.PickupUpperId, RiotOrderState.Deleted, terminal: true);
        Outbound before = await OutboundAsync(fixture);

        await TickAndHearAsync(fixture);
        await OwnOrderRebuildTests.PassTheDelayAsync(fixture);
        fixture.Context.ChangeTracker.Clear();

        await AssertSessionStillNotReadyAsync(fixture);
        Assert.Equal(1, fixture.Riot.CreateCount("TO_PICKUP"));
        await using (ControlServerDbContext reading = new(fixture.DbOptionsForTests))
        {
            OwnOrderRebuildRow recorded = await reading.OwnOrderRebuilds.AsNoTracking().SingleAsync(Token);
            Assert.Equal(
                (dispatched.PickupUpperId, OwnOrderRebuildSources.CancelledInRiot, OwnOrderRebuildStates.Pending),
                (recorded.EndedUpperId, recorded.Source, recorded.State));
        }

        Assert.Equal(before, await OutboundAsync(fixture));
    }

    /// <summary>
    /// 装着货开往关卡的单，确认前被取消：登记同车重建（来源是 RIoT 里被取消）。本票只把这一格接进重建路径；车上有货时重建前的仓位证明
    /// （CP-0007 修订的 REQ-0360）由 control-server#366 补上，所以这里只断「登记了、在等」，不断重建出没出单。
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-0360")]
    public async Task AGateOrderCancelledBeforeConfirmationIsRecordedToBeRebuilt()
    {
        await using RuntimeFixture fixture = await GateCreateAnswerLostAsync();
        JourneyRuntimeRow underWay = await fixture.RuntimeAsync();
        fixture.Riot.CancelOrder(underWay.GateUpperId);

        await TickAndHearAsync(fixture);

        Assert.Equal(JourneyRuntimeEngine.OrderEndedWithoutArrivalReason, (await fixture.RuntimeAsync()).BlockReasonCode);
        await using ControlServerDbContext reading = new(fixture.DbOptionsForTests);
        OwnOrderRebuildRow recorded = await reading.OwnOrderRebuilds.AsNoTracking().SingleAsync(Token);
        Assert.Equal(
            (underWay.GateUpperId, OwnOrderRebuildSources.CancelledInRiot, OwnOrderRebuildStates.Pending),
            (recorded.EndedUpperId, recorded.Source, recorded.State));
        Assert.Empty(await reading.VehicleFaultStates.AsNoTracking().ToArrayAsync(Token));
    }

    /// <summary>
    /// REQ-0361 的护栏照样生效：确认前被取消、重建出来的新单在窗口之内又被取消，不再重建，旅程写 <c>OWN_ORDER_REBUILD_STOPPED</c>。
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-0361")]
    public async Task ASecondCancellationWithinTheWindowStopsTheRebuild()
    {
        await using RuntimeFixture fixture = await PickupCreateAnswerLostAsync();
        JourneyRuntimeRow dispatched = await fixture.RuntimeAsync();
        fixture.Riot.CancelOrder(dispatched.PickupUpperId);
        await TickAndHearAsync(fixture);
        await OwnOrderRebuildTests.PassTheDelayAsync(fixture);
        fixture.Context.ChangeTracker.Clear();
        Assert.Equal(2, fixture.Riot.CreateCount("TO_PICKUP"));
        string newUpperId = (await CurrentStopAsync(fixture, FirstDemandId)).UpperId;
        Assert.NotEqual(dispatched.PickupUpperId, newUpperId);

        fixture.Riot.CancelOrder(newUpperId);
        await TickAndHearAsync(fixture);
        await OwnOrderRebuildTests.PassTheDelayAsync(fixture);
        fixture.Context.ChangeTracker.Clear();

        Assert.Equal(2, fixture.Riot.CreateCount("TO_PICKUP"));
        Assert.Equal(JourneyRuntimeEngine.OwnOrderRebuildStoppedReason, (await fixture.RuntimeAsync()).BlockReasonCode);
    }

    /// <summary>
    /// REQ-0361 的另一条护栏：延迟到点时车在急停里，不建新单，旅程写 <c>OWN_ORDER_REBUILD_WAITING_VEHICLE</c>；急停解了才建。
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-0361")]
    public async Task ARebuildDueWhileTheVehicleIsInAnEmergencyStopWaitsForIt()
    {
        await using RuntimeFixture fixture = await PickupCreateAnswerLostAsync();
        JourneyRuntimeRow dispatched = await fixture.RuntimeAsync();
        fixture.Riot.CancelOrder(dispatched.PickupUpperId);
        await TickAndHearAsync(fixture);
        fixture.Riot.SafetyReasons = ["RIOT_EMERGENCY_NOT_OK"];

        await OwnOrderRebuildTests.PassTheDelayAsync(fixture);
        fixture.Context.ChangeTracker.Clear();

        Assert.Equal(1, fixture.Riot.CreateCount("TO_PICKUP"));
        Assert.Equal(JourneyRuntimeEngine.OwnOrderRebuildWaitingVehicleReason, (await fixture.RuntimeAsync()).BlockReasonCode);

        fixture.Riot.SafetyReasons = [];
        await TickAndHearAsync(fixture);
        Assert.Equal(2, fixture.Riot.CreateCount("TO_PICKUP"));
    }

    /// <summary>
    /// 确认前读到 SUSPENDED（8，实验室零观测、SDK 标注已移除）：按未知终态交人，旅程写 <c>ORDER_STATE_UNRECOGNIZED</c>——与确认过的单读到 8
    /// 时一样。不登记重建、不记故障、不发命令。闸门前后都一样。
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ASuspendedOrderBeforeConfirmationIsNamedForAPerson(bool behindTheGate)
    {
        await using RuntimeFixture fixture = await PickupCreateAnswerLostAsync();
        JourneyRuntimeRow dispatched = await fixture.RuntimeAsync();
        if (behindTheGate)
        {
            await OwnOrderRebuildTests.DropSessionOnOwnOrderAsync(fixture);
        }

        fixture.Riot.SetOrderState(dispatched.PickupUpperId, RiotOrderState.Suspended, terminal: true);

        await TickAndHearAsync(fixture);
        await TickAndHearAsync(fixture);

        Assert.Equal(JourneyRuntimeEngine.OrderStateUnrecognizedReason, (await fixture.RuntimeAsync()).BlockReasonCode);
        Assert.Equal(1, fixture.Riot.CreateCount("TO_PICKUP"));
        await using ControlServerDbContext reading = new(fixture.DbOptionsForTests);
        Assert.Empty(await reading.OwnOrderRebuilds.AsNoTracking().ToArrayAsync(Token));
        Assert.Empty(await reading.VehicleFaultStates.AsNoTracking().ToArrayAsync(Token));
        Assert.Empty(await reading.RiotOrderCommandAudit.AsNoTracking().ToArrayAsync(Token));
    }

    // ---- 夹具 ----------------------------------------------------------------------------------------------

    /// <summary>受理、派往取货站，建单应答丢了：单在 RIoT 上存在并在执行，意图没确认、至少发过一次建单。车停着（<c>MT_FINISHED</c>）。</summary>
    internal static async Task<RuntimeFixture> PickupCreateAnswerLostAsync()
    {
        RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(FirstDemandId, FirstSublot, Now.AddMinutes(-10)));
        fixture.BoxCounts.Set(FirstSublot, 7);
        fixture.Riot.LoseNextCreateResponse = true;
        await TickAndRunAsync(fixture);
        JourneyRuntimeRow runtime = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingPickupArrival, runtime.Stage);
        OrderIntentRow intent = await IntentAsync(fixture, runtime.PickupUpperId);
        Assert.NotEqual("CONFIRMED", intent.Status);
        Assert.Equal(1, intent.CreateAttemptCount);
        fixture.Riot.MovementState = "MT_FINISHED";
        fixture.Context.ChangeTracker.Clear();
        return fixture;
    }

    /// <summary>取货、装货、离站，开往关卡那张单的建单应答丢了：车上有货，关卡腿的意图没确认。车停着（<c>MT_FINISHED</c>）。</summary>
    internal static async Task<RuntimeFixture> GateCreateAnswerLostAsync()
    {
        RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(FirstDemandId, FirstSublot, createdAt: Now.AddMinutes(-10)));
        fixture.BoxCounts.Set(FirstSublot, 7);
        fixture.Riot.LoseNextCreateResponseOf = "TO_GATE";
        await fixture.AdvanceToGateArrivalAsync();
        JourneyRuntimeRow runtime = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingGateArrival, runtime.Stage);
        Assert.Null(fixture.Riot.LoseNextCreateResponseOf);
        OrderIntentRow intent = await IntentAsync(fixture, runtime.GateUpperId);
        Assert.NotEqual("CONFIRMED", intent.Status);
        Assert.Equal(1, intent.CreateAttemptCount);
        fixture.Riot.MovementState = "MT_FINISHED";
        fixture.Context.ChangeTracker.Clear();
        return fixture;
    }

    /// <summary>
    /// 把这张单的意图写成终态对账（带 RIoT 的 orderId），旅程写建单确认给它的码，不记任何故障——修之前的版本对这种单留下的全部痕迹。
    /// </summary>
    internal static async Task LeaveTerminalReconciledWithoutAFaultAsync(RuntimeFixture fixture, string upperId, string legName)
    {
        OrderIntentRow intent = await fixture.Context.OrderIntents.SingleAsync(row => row.UpperId == upperId, Token);
        intent.Status = "TERMINAL_RECONCILIATION_REQUIRED";
        intent.OrderId = fixture.Riot.OrderIdOf(upperId);
        JourneyRuntimeRow runtime = await fixture.Context.JourneyRuntimes.SingleAsync(Token);
        runtime.SetBlockReason($"{legName}_{MovementDispatchOutcome.TerminalReconciliationRequired}", fixture.Clock.GetUtcNow());
        await fixture.Context.SaveChangesAsync(Token);
        fixture.Context.ChangeTracker.Clear();
        await using ControlServerDbContext reading = new(fixture.DbOptionsForTests);
        Assert.Empty(await reading.VehicleFaultStates.AsNoTracking().Where(row => row.Level != VehicleFaultLevel.None).ToArrayAsync(Token));
    }

    private static async Task<OrderIntentRow> IntentAsync(RuntimeFixture fixture, string upperId)
    {
        await using ControlServerDbContext reading = new(fixture.DbOptionsForTests);
        return await reading.OrderIntents.AsNoTracking().SingleAsync(row => row.UpperId == upperId, Token);
    }

    private sealed record Outbound(int Lines, string OutboxRows);

    private static async Task<Outbound> OutboundAsync(RuntimeFixture fixture)
    {
        await using ControlServerDbContext reading = new(fixture.DbOptionsForTests);
        string[] rows = await reading.ProtocolOutbox.AsNoTracking()
            .OrderBy(row => row.MessageId)
            .Select(row => row.MessageId + "#" + row.PayloadJson)
            .ToArrayAsync(Token);
        return new Outbound(fixture.Peer.Lines.Count, string.Join(',', rows));
    }

    /// <summary>
    /// 一条 <c>SuspectedBlocked</c> 故障（第 1 代、证据是订单 FAILED、已升级）；审计里对这张单发过一次 Hold，带 RIoT 的 orderId；
    /// 急停触发一次、Pending；旅程写 <c>VEHICLE_ORDER_FAILED</c>。
    /// </summary>
    private static async Task AssertRecordedHeldAndStoppedAsync(RuntimeFixture fixture, string upperId)
    {
        await using ControlServerDbContext reading = new(fixture.DbOptionsForTests);
        VehicleFaultStateRow fault = Assert.Single(await reading.VehicleFaultStates.AsNoTracking().ToArrayAsync(Token));
        Assert.Equal(
            (VehicleFaultLevel.SuspectedBlocked, 1L, VehicleFaultEvidence.OrderFailed, true),
            (fault.Level, fault.FaultGeneration, fault.EvidenceCode, fault.EscalatedAt is not null));

        RiotOrderCommandAuditRow[] audit = await reading.RiotOrderCommandAudit.AsNoTracking().ToArrayAsync(Token);
        RiotOrderCommandAuditRow hold = Assert.Single(audit, row => row.CommandType == RiotCommandTypeNames.OrderHold);
        Assert.Equal(
            (upperId, fixture.Riot.OrderIdOf(upperId), (long?)1),
            (hold.TargetUpperId, hold.TargetOrderId, hold.FaultGeneration));
        RiotOrderCommandAuditRow trigger = Assert.Single(
            audit, row => row.CommandType == RiotCommandTypeNames.TriggerEmergency);
        Assert.Equal(RiotOrderCommandOutcome.Pending, trigger.Outcome);

        Assert.Equal(VehicleFaultEvidence.OrderFailed, (await reading.JourneyRuntimes.AsNoTracking().SingleAsync(Token)).BlockReasonCode);
    }

    private static async Task AssertCargoBoundAsync(RuntimeFixture fixture, string demandId)
    {
        await using ControlServerDbContext reading = new(fixture.DbOptionsForTests);
        FaultedVehicleCargoRow cargo = await reading.FaultedVehicleCargo.AsNoTracking().SingleAsync(Token);
        Assert.Equal((demandId, (DateTimeOffset?)null), (cargo.DemandId, cargo.ReleasedAt));
    }

    private static async Task AssertStopProofSampledThisRoundAsync(RuntimeFixture fixture)
    {
        await using ControlServerDbContext reading = new(fixture.DbOptionsForTests);
        VehicleFaultStateRow fault = await reading.VehicleFaultStates.AsNoTracking().SingleAsync(Token);
        Assert.Equal(fixture.Clock.GetUtcNow(), fault.LastEvaluatedAt);
    }

    private static async Task AssertSessionStillNotReadyAsync(RuntimeFixture fixture)
    {
        await using ControlServerDbContext reading = new(fixture.DbOptionsForTests);
        SessionRecoveryRow session = await reading.SessionRecoveries.AsNoTracking().SingleAsync(Token);
        Assert.Equal(
            (SessionReadiness.RecoveryRequired, "DEPARTURE_SAFETY_NOT_READY"),
            (session.Readiness, session.ReasonCode));
    }

    private static async Task AssertSessionReadyAsync(RuntimeFixture fixture)
    {
        await using ControlServerDbContext reading = new(fixture.DbOptionsForTests);
        Assert.Equal(SessionReadiness.Ready, (await reading.SessionRecoveries.AsNoTracking().SingleAsync(Token)).Readiness);
    }

    private static void Latch(RuntimeFixture fixture)
    {
        fixture.EmergencyLatched = true;
        fixture.Riot.SafetyReasons = ["RIOT_EMERGENCY_NOT_OK"];
    }

    private static void Unlatch(RuntimeFixture fixture)
    {
        fixture.EmergencyLatched = false;
        fixture.Riot.SafetyReasons = [];
    }

    /// <summary>钟走一秒、车载端不说话，跑一轮：失联一直持续。</summary>
    private static async Task TickSilentAsync(RuntimeFixture fixture)
    {
        DateTimeOffset before = fixture.Clock.GetUtcNow();
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(fixture.Clock.GetUtcNow() > before, "the clock did not move");
        fixture.Context.ChangeTracker.Clear();
        await fixture.Engine.ExecuteOnceAsync(Token);
        fixture.Context.ChangeTracker.Clear();
    }

    private static async Task TickAndHearAsync(RuntimeFixture fixture)
    {
        DateTimeOffset before = fixture.Clock.GetUtcNow();
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(fixture.Clock.GetUtcNow() > before, "the clock did not move");
        await fixture.HearFromPeerAsync();
        fixture.Context.ChangeTracker.Clear();
        await fixture.Engine.ExecuteOnceAsync(Token);
        fixture.Context.ChangeTracker.Clear();
    }

    private static async Task<int> TriggerCountAsync(RuntimeFixture fixture)
    {
        await using ControlServerDbContext reading = new(fixture.DbOptionsForTests);
        return await reading.RiotOrderCommandAudit.AsNoTracking()
            .CountAsync(row => row.CommandType == RiotCommandTypeNames.TriggerEmergency, Token);
    }

    private static async Task<RiotOrderCommandOutcome?> LatestTriggerOutcomeAsync(RuntimeFixture fixture)
    {
        await using ControlServerDbContext reading = new(fixture.DbOptionsForTests);
        RiotOrderCommandAuditRow[] triggers = await reading.RiotOrderCommandAudit.AsNoTracking()
            .Where(row => row.CommandType == RiotCommandTypeNames.TriggerEmergency)
            .ToArrayAsync(Token);
        return triggers.OrderBy(row => row.AttemptNumber).LastOrDefault()?.Outcome;
    }
}
