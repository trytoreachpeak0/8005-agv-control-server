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
/// control-server#358：真车载端会话因本服务端在途单未就绪（<c>DEPARTURE_SAFETY_NOT_READY</c>）时，订单 FAILED 照样在同一轮记故障、
/// Hold、急停；已经急停的车，会话随后未就绪，急停确认与 REQ-0248 重触发照样推进。闸门后仍然不给车载端发任何东西。
/// </summary>
/// <remarks>
/// <para>
/// <b>修之前</b>（cs#349 的探针，<c>evidence/cs349/probe/</c>）：会话闸门（<c>JourneyRuntimeEngine.AdvanceAsync</c> 开头）在会话未就绪时
/// 只读一次在途单给 <c>NameStalledOrderAsync</c> 起名，而那里的 switch 把 FAILED 落进「单已继续」，码写回
/// <c>ONBOARD_SESSION_NOT_READY</c>；<c>VehicleFaultCoordinator.ObserveAsync</c> 在产品里只由到站分支经
/// <c>ObserveOrderFailureAsync</c> 调用，而到站分支在闸门之后。故障事实、Hold、升级急停、停车证明的采样、急停触发的确认、
/// 闩锁掉了要重触发，全都挂在这一个调用上（<c>DemandReleaseRules.FaultSupervisionInEffect</c> 的注释），于是全部停到会话就绪为止。
/// 真车载端挂着本服务端在途单时整段路都未就绪，单 FAILED 后车还在动，正是急停该发的时候。
/// </para>
/// <para>
/// <b>断言落在故障状态与命令审计这一层</b>（<c>VehicleFaultStates</c>、<c>RiotOrderCommandAudit</c> 里的 <c>OrderHold</c> 与
/// <c>triggerEmergency</c>），旅程码只作旁证：故障指南写明判断车有没有故障以故障状态为准。
/// </para>
/// <para>
/// <b>闸门后出站为空</b>，按车载端的线（<c>RecordingPeer.Lines</c>）与发件箱两处断：闸门存在的理由就是不给未就绪的会话发东西，
/// 本票加的只有对 RIoT 的读与命令。
/// </para>
/// <para>
/// 闸门前那条路（会话就绪）由 <see cref="WithTheSessionReadyTheSameFailedOrderReachesTheFaultModelInTheSameShape"/> 钉住，
/// 与闸门后那条比同一组事实：两条路走的是同一个方法，这条用例守的是它们不再分叉。
/// </para>
/// </remarks>
public sealed class FailedOrderBehindSessionGateTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// 开往取货站的单 FAILED、车还在动（<c>MT_RUNNING</c>），会话因这张单未就绪：同一轮记下故障、发出 Hold、升级急停；
    /// 旅程写 <c>VEHICLE_ORDER_FAILED</c>，会话仍未就绪，车载端这一轮一条都没收到。
    /// </summary>
    /// <remarks>
    /// Hold 的结果是 <c>Failed</c>：对一张已经 FAILED 的单，对账读回的就是「终态、不是想要的那个」，这与会话就绪时一样
    /// （见 <see cref="WithTheSessionReadyTheSameFailedOrderReachesTheFaultModelInTheSameShape"/>）。要的是它被发了。
    /// </remarks>
    [Fact]
    [Trait("Requirement", "REQ-0232")]
    [Trait("Requirement", "REQ-0234")]
    [Trait("Requirement", "REQ-0246")]
    public async Task AFailedPickupOrderBehindTheGateIsRecordedHeldAndStoppedInTheSameRound()
    {
        await using RuntimeFixture fixture = await DispatchedToPickupAsync();
        JourneyRuntimeRow dispatched = await fixture.RuntimeAsync();
        await OwnOrderRebuildTests.DropSessionOnOwnOrderAsync(fixture);
        fixture.Riot.MovementState = "MT_RUNNING";
        fixture.Riot.FailOrder(dispatched.PickupUpperId);
        Outbound before = await OutboundAsync(fixture);

        await TickAndHearAsync(fixture);

        await AssertSessionStillNotReadyAsync(fixture);
        await AssertRecordedHeldAndStoppedAsync(fixture, dispatched.PickupUpperId);
        Assert.Equal(before, await OutboundAsync(fixture));
    }

    /// <summary>
    /// 同一件事发生在装货之后、开往关卡的腿上：车上有货，除了故障、Hold 与急停，还要把货与需求绑住（REQ-0238）。
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-0232")]
    [Trait("Requirement", "REQ-0238")]
    [Trait("Requirement", "REQ-0246")]
    public async Task AFailedGateOrderBehindTheGateAlsoBindsTheCargoInTheSameRound()
    {
        await using RuntimeFixture fixture = await GateArrivalWaitAsync();
        JourneyRuntimeRow underWay = await fixture.RuntimeAsync();
        await OwnOrderRebuildTests.DropSessionOnOwnOrderAsync(fixture);
        fixture.Riot.MovementState = "MT_RUNNING";
        fixture.Riot.FailOrder(underWay.GateUpperId);
        Outbound before = await OutboundAsync(fixture);

        await TickAndHearAsync(fixture);

        await AssertSessionStillNotReadyAsync(fixture);
        await AssertRecordedHeldAndStoppedAsync(fixture, underWay.GateUpperId);
        await using (ControlServerDbContext reading = new(fixture.DbOptionsForTests))
        {
            FaultedVehicleCargoRow cargo = await reading.FaultedVehicleCargo.AsNoTracking().SingleAsync(Token);
            Assert.Equal((underWay.DemandId, (DateTimeOffset?)null), (cargo.DemandId, cargo.ReleasedAt));
        }

        Assert.Equal(before, await OutboundAsync(fixture));
    }

    /// <summary>
    /// 会话就绪时单 FAILED、急停已发出（触发 Pending），随后会话因在途单未就绪：RIoT 锁上的下一轮把触发确认成 Confirmed；
    /// 闩锁在故障未清、也没有人工解除的情况下掉了，下一轮按 REQ-0248 重触发。两轮都在闸门后，车载端一条都没收到，
    /// 旅程码仍是 <c>VEHICLE_ORDER_FAILED</c>，不被闸门自己的码盖掉。
    /// </summary>
    /// <remarks>
    /// 修前这两件事都要等会话就绪：触发停在 Pending，闩锁掉了也没有第二次触发，码被写成 <c>ONBOARD_SESSION_NOT_READY</c>。
    /// </remarks>
    [Fact]
    [Trait("Requirement", "REQ-0246")]
    [Trait("Requirement", "REQ-0248")]
    public async Task AStopAlreadyTriggeredIsConfirmedAndRetriggeredWhileTheSessionIsNotReady()
    {
        await using RuntimeFixture fixture = await DispatchedToPickupAsync();
        JourneyRuntimeRow dispatched = await fixture.RuntimeAsync();
        fixture.Riot.MovementState = "MT_RUNNING";
        fixture.Riot.FailOrder(dispatched.PickupUpperId);
        await TickAndHearAsync(fixture);
        Assert.Equal(RiotOrderCommandOutcome.Pending, await LatestTriggerOutcomeAsync(fixture));
        Assert.Equal(1, await TriggerCountAsync(fixture));

        await OwnOrderRebuildTests.DropSessionOnOwnOrderAsync(fixture);
        Outbound before = await OutboundAsync(fixture);

        // RIoT 锁上：闸门后的这一轮确认触发。
        Latch(fixture);
        fixture.Riot.MovementState = "MT_FINISHED";
        await TickAndHearAsync(fixture);
        await AssertSessionStillNotReadyAsync(fixture);
        Assert.Equal(RiotOrderCommandOutcome.Confirmed, await LatestTriggerOutcomeAsync(fixture));
        Assert.Equal(VehicleFaultEvidence.OrderFailed, (await fixture.RuntimeAsync()).BlockReasonCode);

        // 故障未清、没有人工解除，闩锁掉了：闸门后的这一轮重触发。
        Unlatch(fixture);
        await TickAndHearAsync(fixture);
        await AssertSessionStillNotReadyAsync(fixture);
        Assert.Equal(2, await TriggerCountAsync(fixture));
        Assert.Equal(VehicleFaultEvidence.OrderFailed, (await fixture.RuntimeAsync()).BlockReasonCode);

        Assert.Equal(before, await OutboundAsync(fixture));
    }

    /// <summary>
    /// 闸门前那条路：会话就绪时同一张单 FAILED、车在动，一轮下来的故障与命令审计与闸门后那条一模一样。
    /// </summary>
    /// <remarks>
    /// 修前修后都绿。它守的是 <c>JourneyRuntimeEngine.NameStalledOrderAsync</c> 注释里那句前提的另一半：到站分支里 FAILED 同样先进故障模型。
    /// 把观察挪进共用方法时若把到站分支的那一次丢了，这条红。
    /// </remarks>
    [Fact]
    [Trait("Requirement", "REQ-0232")]
    [Trait("Requirement", "REQ-0246")]
    public async Task WithTheSessionReadyTheSameFailedOrderReachesTheFaultModelInTheSameShape()
    {
        await using RuntimeFixture fixture = await DispatchedToPickupAsync();
        JourneyRuntimeRow dispatched = await fixture.RuntimeAsync();
        fixture.Riot.MovementState = "MT_RUNNING";
        fixture.Riot.FailOrder(dispatched.PickupUpperId);

        await TickAndHearAsync(fixture);

        Assert.Equal(
            SessionReadiness.Ready,
            (await fixture.Context.SessionRecoveries.AsNoTracking().SingleAsync(Token)).Readiness);
        await AssertRecordedHeldAndStoppedAsync(fixture, dispatched.PickupUpperId);
    }

    // ---- 夹具 ----------------------------------------------------------------------------------------------

    /// <summary>
    /// 车载端这边能看到的全部出站：线上发出的行数，和发件箱里的每一行（id 与存下的整行载荷——补发改写代次时载荷会变）。
    /// </summary>
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
    /// 这一轮之后：一条 <c>SuspectedBlocked</c> 故障（第 1 代、证据是订单 FAILED、已升级）；审计里对这张单发过一次 Hold，
    /// 急停触发一次、还在等闩锁（Pending）；旅程写 <c>VEHICLE_ORDER_FAILED</c>。
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
        Assert.Equal((upperId, RiotOrderCommandOutcome.Failed, (long?)1), (hold.TargetUpperId, hold.Outcome, hold.FaultGeneration));
        RiotOrderCommandAuditRow trigger = Assert.Single(
            audit, row => row.CommandType == RiotCommandTypeNames.TriggerEmergency);
        Assert.Equal(RiotOrderCommandOutcome.Pending, trigger.Outcome);

        Assert.Equal(VehicleFaultEvidence.OrderFailed, (await reading.JourneyRuntimes.AsNoTracking().SingleAsync(Token)).BlockReasonCode);
    }

    /// <summary>这一轮确实跑在闸门后：会话仍是因在途单造成的未就绪。</summary>
    private static async Task AssertSessionStillNotReadyAsync(RuntimeFixture fixture)
    {
        await using ControlServerDbContext reading = new(fixture.DbOptionsForTests);
        SessionRecoveryRow session = await reading.SessionRecoveries.AsNoTracking().SingleAsync(Token);
        Assert.Equal(
            (SessionReadiness.RecoveryRequired, "DEPARTURE_SAFETY_NOT_READY"),
            (session.Readiness, session.ReasonCode));
    }

    /// <summary>RIoT 报这辆车急停锁住：故障协调器读的闩锁与车辆安全读取的原因码一起拨（同 cs#349 的用例）。</summary>
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

    private static async Task<RuntimeFixture> GateArrivalWaitAsync()
    {
        RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(FirstDemandId, FirstSublot, createdAt: Now.AddMinutes(-10)));
        fixture.BoxCounts.Set(FirstSublot, 7);
        await fixture.AdvanceToGateArrivalAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingGateArrival, (await fixture.RuntimeAsync()).Stage);
        fixture.Riot.MovementState = "MT_FINISHED";
        fixture.Context.ChangeTracker.Clear();
        return fixture;
    }
}
