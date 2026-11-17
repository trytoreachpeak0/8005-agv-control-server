using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Commands;
using ControlServer.Host.Runtime.Faults;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using static ControlServer.Tests.Batch7StopDrivenAdvanceDriver;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;
using SiteRiot = ControlServer.Tests.VehicleFaultRecoveryTests.SiteRiot;

namespace ControlServer.Tests;

/// <summary>
/// control-server#897：门锁故障升级、依据被改写之后，本服务端的单在 RIoT 被人取消——清除入口仍按「起源是门锁故障」接受，不再死锁。
/// </summary>
/// <remarks>
/// <para>
/// <b>现场（2026-11-15 agv03，v2 生产）。</b>车装着 4 篮开往卸货站，4 号仓行驶中读到没锁：服务端记门锁故障、按住单、急停。之后有人按了车上
/// 急停，故障升级为 <c>ConfirmedIsolated</c>，依据改写成 <c>RIOT_EMERGENCY_CAN_NOT_RECOVER</c>。按住的单被人在 RIoT 取消，引擎记下同车重建
/// （PENDING，等故障清除）；有货急停人工解除之后，清除入口以 <c>FAULT_RECOVERY_CURRENT_ORDER_CANCELLED_IN_RIOT</c> 拒绝——修前只认「当前依据是门锁」。
/// 重建等清除、清除拒重建，只能改库。
/// </para>
/// <para>
/// <b>修法。</b>故障行在进入新一代时记下服务端看到的症状（<c>OriginSymptomCode</c>），本代内不再改写；清除入口认「起源是门锁、并且本代故障
/// 按住过当前这张单」。#335 审查 M1 那条规则（被取消的单归引擎重建，清除不能是第二条路）对其余故障不变：第二、第三条用例守它。
/// </para>
/// </remarks>
public sealed class DoorFaultEscalatedThenCancelledInRiotTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// 现场序列：门锁故障（运行中的单被按住、急停）→ 车上急停按下，故障升级、依据改写 → 单在 RIoT 被取消，引擎记重建、等故障清除 →
    /// 有货急停人工解除 → 人工清除成功，只清故障、不记第二次重建 → 重建过了护栏（延迟、车况、#349 的仓位读数）才建，建且只建一张开往卸货站的单。
    /// 修前在「人工清除」那一步红：<c>Refused / FAULT_RECOVERY_CURRENT_ORDER_CANCELLED_IN_RIOT</c>。
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-0246")]
    [Trait("Requirement", "REQ-0360")]
    public async Task ADoorFaultEscalatedByAHeldButtonClearsAfterItsOrderIsCancelledInRiotAndTheOneRebuildRuns()
    {
        await using RuntimeFixture fixture = await GateArrivalWaitAsync();
        string upperId = (await fixture.RuntimeAsync()).GateUpperId!;
        int gateCreates = fixture.Riot.CreateCount("TO_GATE");

        // 一：行驶中门锁没锁 -> 门锁故障，同一轮按住这张单、急停；RIoT 停住单，闩锁锁上。
        fixture.Riot.MovementState = "MT_RUNNING";
        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { CurrentStationId = 0 };
        await fixture.ReportSafetySummaryAsync(
            allTargetSlotsLocked: false, unknownPresent: false, ["LOCK_NOT_CLOSED", "ACTION_NOT_ALLOWED_IN_STATE"]);
        await DriveOneRoundAsync(fixture);
        string orderId = await OrderIdAsync(fixture, upperId);
        fixture.Riot.SetOrderState(upperId, RiotOrderState.Paused, terminal: false);
        fixture.Riot.MovementState = "MT_PAUSED";
        fixture.EmergencyLatched = true;
        fixture.UnfinishedOrderIds = [orderId];
        await DriveOneRoundAsync(fixture);
        VehicleFaultStateRow doorFault = await FaultAsync(fixture);
        Assert.Equal(
            (VehicleFaultLevel.SuspectedBlocked, VehicleFaultEvidence.DoorNotProvenLocked),
            (doorFault.Level, doorFault.EvidenceCode));
        Assert.Equal(1, await CountAsync(fixture, RiotCommandTypeNames.TriggerEmergency));

        // 二：有人按下车上急停 -> 升级为 ConfirmedIsolated，依据改写成 CAN_NOT_RECOVER；同一代故障。
        fixture.ButtonHeld = true;
        await DriveOneRoundAsync(fixture);
        VehicleFaultStateRow escalated = await FaultAsync(fixture);
        Assert.Equal(
            (VehicleFaultLevel.ConfirmedIsolated, VehicleFaultEvidence.EmergencyCanNotRecover, doorFault.FaultGeneration),
            (escalated.Level, escalated.EvidenceCode, escalated.FaultGeneration));
        Assert.Equal(VehicleFaultEvidence.DoorNotProvenLocked, escalated.OriginSymptomCode);

        // 三：按钮松开（CAN_RECOVER）；按住的单被人在 RIoT 取消 -> 引擎记重建，过了延迟仍等故障清除，一张单都不建。
        fixture.ButtonHeld = false;
        fixture.Riot.SetOrderState(upperId, RiotOrderState.Cancelled, terminal: true);
        fixture.UnfinishedOrderIds = [];
        await ReportLockedAsync(fixture);
        await DriveOneRoundAsync(fixture);
        OwnOrderRebuildRow recorded = Assert.Single(await RebuildsAsync(fixture));
        Assert.Equal((OwnOrderRebuildSources.CancelledInRiot, OwnOrderRebuildStates.Pending), (recorded.Source, recorded.State));
        fixture.Clock.Advance(fixture.Options.OwnOrderRebuildDelay);
        await DriveOneRoundAsync(fixture);
        OwnOrderRebuildRow waiting = Assert.Single(await RebuildsAsync(fixture));
        Assert.True(fixture.Clock.GetUtcNow() > waiting.DueAt);
        Assert.Contains("VEHICLE_FAULT_IN_EFFECT", waiting.WaitingReason, StringComparison.Ordinal);
        Assert.Equal(gateCreates, fixture.Riot.CreateCount("TO_GATE"));

        // 四：有货急停人工解除（确认门锁已检修），闩锁读回 OK。
        await fixture.ReleaseByAPersonAsync();
        fixture.EmergencyLatched = false;
        for (int round = 0; round < 3; round++)
        {
            await DriveOneRoundAsync(fixture);
        }

        Assert.Equal(gateCreates, fixture.Riot.CreateCount("TO_GATE"));
        Assert.Equal(VehicleFaultLevel.ConfirmedIsolated, (await FaultAsync(fixture)).Level);

        // 五：人工清除。修前在这里拒（FAULT_RECOVERY_CURRENT_ORDER_CANCELLED_IN_RIOT）。只清故障，不记第二次重建。
        VehicleFaultRecoveryDecision cleared = await VehicleFaultRecoveryTests.Service(fixture, new(fixture) { HasUnfinishedOrder = false })
            .RecoverAsync(VehicleFaultRecoveryTests.Clear(fixture), Token);
        fixture.Context.ChangeTracker.Clear();
        Assert.True(VehicleFaultRecoveryOutcome.Cleared == cleared.Outcome, string.Join(", ", cleared.Reasons));
        Assert.Equal(VehicleFaultRecoveryDispositions.None, cleared.Disposition);
        Assert.Equal(VehicleFaultLevel.None, (await FaultAsync(fixture)).Level);
        Assert.Equal(recorded.RebuildId, Assert.Single(await RebuildsAsync(fixture)).RebuildId);

        // 六：重建在 #349 的仓位读数到之前不建（车上有货，要先证明货还在原仓位）。
        await fixture.RestoreSessionReadyAsync();
        fixture.Context.ChangeTracker.Clear();
        fixture.Riot.MovementState = "MT_FINISHED";
        await fixture.SetDepartureSummaryAsync();
        await DriveOneRoundAsync(fixture);
        Assert.Equal(gateCreates, fixture.Riot.CreateCount("TO_GATE"));
        Assert.Equal(OwnOrderRebuildStates.Pending, Assert.Single(await RebuildsAsync(fixture)).State);

        // 七：仓位读数到了 -> 建且只建一张开往卸货站的单，还是那一条重建记录。
        await OwnOrderRebuildTests.ReportCargoInPlaceAndRunAsync(fixture);
        for (int round = 0; round < 3; round++)
        {
            await DriveOneRoundAsync(fixture);
        }

        Assert.Equal(gateCreates + 1, fixture.Riot.CreateCount("TO_GATE"));
        OwnOrderRebuildRow rebuilt = Assert.Single(await RebuildsAsync(fixture));
        Assert.Equal((recorded.RebuildId, OwnOrderRebuildStates.Rebuilt), (rebuilt.RebuildId, rebuilt.State));
        Assert.Equal(JourneyRuntimeStage.AwaitingGateArrival, (await fixture.RuntimeAsync()).Stage);
    }

    /// <summary>
    /// 从来不是门锁故障：以别的症状记下、按钮按着所以依据同样是 <c>RIOT_EMERGENCY_CAN_NOT_RECOVER</c>（与现场升级后的样子一模一样）；
    /// 单被人在 RIoT 取消之后，清除入口照旧拒绝——被取消的单归引擎重建（#335 审查 M1），清除不能是第二条路。
    /// </summary>
    /// <remarks>
    /// 起点是构造的，照 <see cref="EmergencyReleaseVersusOwnOrderRebuildTests"/> 第四条：产品里在运行中的单上记故障的只有门锁那一路。它守的是
    /// 「按依据放行」这种修法：只看 <c>EvidenceCode</c> 是不是 CAN_NOT_RECOVER，这一格会被放过去。
    /// </remarks>
    [Fact]
    [Trait("Requirement", "REQ-0360")]
    public async Task AFaultThatNeverWasADoorFaultStillRefusesAClearanceOverAnOrderCancelledInRiot()
    {
        await using RuntimeFixture fixture = await GateArrivalWaitAsync();
        string upperId = (await fixture.RuntimeAsync()).GateUpperId!;
        string orderId = await OrderIdAsync(fixture, upperId);
        SiteRiot site = new(fixture) { HasUnfinishedOrder = false };

        fixture.ButtonHeld = true;
        await ObserveAsync(fixture, site, "L1_CONSTRUCTED_SYMPTOM", new RiotOrderCommandTarget(fixture.Options.AgvId, upperId, orderId));
        VehicleFaultStateRow fault = await FaultAsync(fixture);
        Assert.Equal(
            (VehicleFaultLevel.ConfirmedIsolated, VehicleFaultEvidence.EmergencyCanNotRecover),
            (fault.Level, fault.EvidenceCode));
        Assert.True(await HeldUnderGenerationAsync(fixture, upperId, fault.FaultGeneration));

        fixture.ButtonHeld = false;
        fixture.Riot.SetOrderState(upperId, RiotOrderState.Cancelled, terminal: true);
        VehicleFaultRecoveryDecision clearance = await VehicleFaultRecoveryTests.Service(fixture, site)
            .RecoverAsync(VehicleFaultRecoveryTests.Clear(fixture), Token);

        Assert.Equal(VehicleFaultRecoveryOutcome.Refused, clearance.Outcome);
        Assert.Contains("FAULT_RECOVERY_CURRENT_ORDER_CANCELLED_IN_RIOT", clearance.Reasons);
    }

    /// <summary>
    /// 起源是门锁故障，但它按住的是另一张单（这里是同一趟旅程已经跑完的取货单），旅程现在等的这张单从没在这一代故障下被按住过：
    /// 单在 RIoT 被取消之后清除照旧拒绝。票面的条件是「在该单运行时发起」，不是「这辆车曾有门锁故障」。
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-0360")]
    public async Task ADoorFaultRaisedOnAnotherOrderDoesNotClearOverTheCurrentOrderCancelledInRiot()
    {
        await using RuntimeFixture fixture = await GateArrivalWaitAsync();
        JourneyRuntimeRow runtime = await fixture.RuntimeAsync();
        string upperId = runtime.GateUpperId!;
        string pickupUpperId = runtime.PickupUpperId;
        SiteRiot site = new(fixture) { HasUnfinishedOrder = false };

        await ObserveAsync(
            fixture, site, VehicleFaultEvidence.DoorNotProvenLocked,
            new RiotOrderCommandTarget(fixture.Options.AgvId, pickupUpperId, await OrderIdAsync(fixture, pickupUpperId)));
        VehicleFaultStateRow fault = await FaultAsync(fixture);
        Assert.Equal(VehicleFaultEvidence.DoorNotProvenLocked, fault.OriginSymptomCode);
        Assert.True(await HeldUnderGenerationAsync(fixture, pickupUpperId, fault.FaultGeneration));
        Assert.False(await HeldUnderGenerationAsync(fixture, upperId, fault.FaultGeneration));

        fixture.Riot.SetOrderState(upperId, RiotOrderState.Cancelled, terminal: true);
        VehicleFaultRecoveryDecision clearance = await VehicleFaultRecoveryTests.Service(fixture, site)
            .RecoverAsync(VehicleFaultRecoveryTests.Clear(fixture), Token);

        Assert.Equal(VehicleFaultRecoveryOutcome.Refused, clearance.Outcome);
        Assert.Contains("FAULT_RECOVERY_CURRENT_ORDER_CANCELLED_IN_RIOT", clearance.Reasons);
    }

    // ---- 夹具 ----------------------------------------------------------------------------------------------------------

    private static async Task<RuntimeFixture> GateArrivalWaitAsync()
    {
        RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(FirstDemandId, FirstSublot, createdAt: Now.AddMinutes(-10)));
        fixture.BoxCounts.Set(FirstSublot, 7);
        await fixture.AdvanceToGateArrivalAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingGateArrival, (await fixture.RuntimeAsync()).Stage);
        fixture.Context.ChangeTracker.Clear();
        return fixture;
    }

    /// <summary>经故障协调器的公开入口记一次症状，站在 RIoT 现场一侧的替身上（照 <c>EmergencyReleaseVersusOwnOrderRebuildTests.Coordinator</c>）。</summary>
    private static async Task ObserveAsync(RuntimeFixture fixture, SiteRiot site, string symptom, RiotOrderCommandTarget target)
    {
        await using ControlServerDbContext context = new(fixture.DbOptionsForTests);
        VehicleFaultStore faults = new(context);
        RiotOrderCommandAuditStore audit = new(context);
        EmergencyStopSupervisor supervisor = new(
            site, site, site, audit, faults, Options.Create(new RiotCommandOptions()), fixture.Clock,
            NullLogger<EmergencyStopSupervisor>.Instance);
        IOptions<VehicleFaultOptions> faultOptions = Options.Create(new VehicleFaultOptions());
        VehicleFaultCoordinator coordinator = new(
            faults, site, site, site, audit, new RiotOrderCommandService(site, audit, site, fixture.Clock), supervisor,
            new VehicleMotionLedger(faultOptions), faultOptions, fixture.Clock, NullLogger<VehicleFaultCoordinator>.Instance);
        await coordinator.ObserveAsync(
            new EmergencyStopSubject(fixture.Options.AgvId, fixture.Options.VehicleKey), symptom,
            new FaultedVehicleContext(target, Cargo: null), Token);
        fixture.Context.ChangeTracker.Clear();
    }

    private static async Task<bool> HeldUnderGenerationAsync(RuntimeFixture fixture, string upperId, long generation)
    {
        await using ControlServerDbContext reading = new(fixture.DbOptionsForTests);
        return await reading.RiotOrderCommandAudit.AsNoTracking().AnyAsync(
            row => row.CommandType == RiotCommandTypeNames.OrderHold && row.TargetUpperId == upperId && row.FaultGeneration == generation,
            Token);
    }

    private static async Task<VehicleFaultStateRow> FaultAsync(RuntimeFixture fixture)
    {
        await using ControlServerDbContext reading = new(fixture.DbOptionsForTests);
        return await reading.VehicleFaultStates.AsNoTracking().SingleAsync(Token);
    }

    private static async Task<OwnOrderRebuildRow[]> RebuildsAsync(RuntimeFixture fixture)
    {
        await using ControlServerDbContext reading = new(fixture.DbOptionsForTests);
        return await reading.OwnOrderRebuilds.AsNoTracking().ToArrayAsync(Token);
    }

    private static async Task<int> CountAsync(RuntimeFixture fixture, string commandType)
    {
        await using ControlServerDbContext reading = new(fixture.DbOptionsForTests);
        return await reading.RiotOrderCommandAudit.AsNoTracking().CountAsync(row => row.CommandType == commandType, Token);
    }

    private static async Task<string> OrderIdAsync(RuntimeFixture fixture, string upperId)
    {
        await using ControlServerDbContext reading = new(fixture.DbOptionsForTests);
        return (await reading.OrderIntents.AsNoTracking().SingleAsync(row => row.UpperId == upperId, Token)).OrderId!;
    }

    private static async Task DriveOneRoundAsync(RuntimeFixture fixture)
    {
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        await fixture.HearFromPeerAsync();
        fixture.Context.ChangeTracker.Clear();
        await fixture.Engine.ExecuteOnceAsync(Token);
        fixture.Context.ChangeTracker.Clear();
    }

    private static Task ReportLockedAsync(RuntimeFixture fixture) =>
        fixture.ReportSafetySummaryAsync(allTargetSlotsLocked: true, unknownPresent: false, ["ACTION_NOT_ALLOWED_IN_STATE"]);
}
