using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Commands;
using ControlServer.Host.Runtime.Faults;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using static ControlServer.Tests.Batch7StopDrivenAdvanceDriver;
using static ControlServer.Tests.InTransitDoorUnknownGraceTests;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;

namespace ControlServer.Tests;

/// <summary>
/// control-server#789 第二步（CP-0017 修订项二，REQ-0167）：只因「读不到」（仓位未知、没有观测、观测过时）引起的门锁类三类急停，
/// 锁反馈恢复、连续 10 秒读回全部锁闭、急停锁着（<c>CAN_RECOVER</c>，即停稳）、本服务端为这次故障按住的那张单仍是 HELD 时，服务端自己
/// <c>cancelEmergency</c> 并回查 <c>OK</c>。单仍按住、故障阻断保持，要人经故障处置「继续原单」，车不会自己走。
/// 整次故障里读到过一次「没锁」的，照旧只能人工解除（CP-0011）。
/// </summary>
/// <remarks>
/// <para>
/// <b>急停仍只有故障模型一个调用方</b>（<see cref="InTransitDoorLockFaultTests.OnlyTheFaultCoordinatorRequestsAnEmergencyStop"/>）：
/// 自动解除走 <see cref="EmergencyStopSupervisor"/> 的 <c>ConsiderReleaseAsync</c>，由故障协调器在它每轮驱动监看时带上「读数已恢复」的放行条件。
/// </para>
/// <para>
/// <b>一代故障只自动解除一次</b>：解除之后又读不到、车被重新急停，第二次交给人。CP-0017 没要求这一条，是实现选的偏严一侧，免得一台
/// IO 时好时坏的车停着反复「急停—解除」；PR 里报调度。
/// </para>
/// </remarks>
public sealed class InTransitDoorReadingRestoredReleaseTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// 整条路：读不到 → 宽限满 3 秒按住、急停锁住 → 读回锁闭，头 10 秒一条解除都不发 → 满 10 秒自动解除，回查 OK →
    /// 单仍 HELD、故障仍在、没有 CONTINUE、车停在两站之间不再被急停 → 人按「继续原单」，CONTINUE 发出、故障清除。
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-0167")]
    [Trait("Requirement", "REQ-0246")]
    [Trait("Requirement", "REQ-0239")]
    public async Task AStopForDoorsThatCouldNotBeReadIsReleasedOnceTheyReadLockedForTenSeconds()
    {
        await using RuntimeFixture fixture = await LatchedForUnreadDoorsAsync();
        Latched latched = await LatchedFactsAsync(fixture);

        await ReportLockedAsync(fixture);
        for (int second = 0; second < 10; second++)
        {
            await DriveOneRoundAsync(fixture);
            Assert.Equal(0, await CountAsync(fixture, RiotCommandTypeNames.CancelEmergency));
        }

        await DriveOneRoundAsync(fixture);
        Assert.Equal(1, await CountAsync(fixture, RiotCommandTypeNames.CancelEmergency));
        fixture.EmergencyLatched = false;
        await DriveOneRoundAsync(fixture);

        await using (ControlServerDbContext reading = new(fixture.DbOptionsForTests))
        {
            RiotOrderCommandAuditRow release = Assert.Single(await reading.RiotOrderCommandAudit.AsNoTracking()
                .Where(row => row.CommandType == RiotCommandTypeNames.CancelEmergency).ToArrayAsync(Token));
            Assert.Equal((RiotOrderCommandOutcome.Confirmed, (long?)1), (release.Outcome, release.FaultGeneration));
            Assert.Contains(EmergencyStopSupervisor.DoorReadingRestoredReason, release.ReceiptJson ?? string.Empty, StringComparison.Ordinal);
            VehicleFaultStateRow fault = await reading.VehicleFaultStates.AsNoTracking().SingleAsync(Token);
            Assert.Equal((VehicleFaultLevel.SuspectedBlocked, 1L, InTransitDoorLockFaultTests.DoorSymptom),
                (fault.Level, fault.FaultGeneration, fault.EvidenceCode));
        }

        for (int round = 0; round < 10; round++)
        {
            await DriveOneRoundAsync(fixture);
            Assert.Equal(0, await CountAsync(fixture, RiotCommandTypeNames.OrderContinue));
            Assert.Equal(1, await CountAsync(fixture, RiotCommandTypeNames.TriggerEmergency));
            Assert.Equal(RiotOrderState.Paused, fixture.Riot.OrderStateOf(latched.UpperId));
        }

        await ResumeByAPersonAsync(fixture, latched.UpperId);
        await using ControlServerDbContext after = new(fixture.DbOptionsForTests);
        Assert.Equal(VehicleFaultLevel.None, (await after.VehicleFaultStates.AsNoTracking().SingleAsync(Token)).Level);
        Assert.Equal(1, await CountAsync(fixture, RiotCommandTypeNames.OrderContinue));
    }

    /// <summary>闸门后（真车载端在途时会话因本单未就绪）也一样自动解除。</summary>
    [Fact]
    [Trait("Requirement", "REQ-0167")]
    public async Task BehindTheReadinessGateTheStopIsReleasedToo()
    {
        await using RuntimeFixture fixture = await LatchedForUnreadDoorsAsync(behindTheReadinessGate: true);

        await ReportLockedAsync(fixture);
        for (int second = 0; second < 11; second++)
        {
            await DriveOneRoundAsync(fixture);
        }

        Assert.Equal(1, await CountAsync(fixture, RiotCommandTypeNames.CancelEmergency));
    }

    /// <summary>读回锁闭不够 10 秒就又读不到：连续计时清零，重新读回锁闭后从头数 10 秒。</summary>
    [Fact]
    [Trait("Requirement", "REQ-0167")]
    public async Task ABreakInTheLockedReadingsStartsTheTenSecondsAgain()
    {
        await using RuntimeFixture fixture = await LatchedForUnreadDoorsAsync();

        await ReportLockedAsync(fixture);
        await DriveRoundsAsync(fixture, 8);
        await ReportUnknownAsync(fixture);
        await DriveOneRoundAsync(fixture);
        await ReportLockedAsync(fixture);
        await DriveRoundsAsync(fixture, 9);
        Assert.Equal(0, await CountAsync(fixture, RiotCommandTypeNames.CancelEmergency));

        await DriveRoundsAsync(fixture, 2);
        Assert.Equal(1, await CountAsync(fixture, RiotCommandTypeNames.CancelEmergency));
    }

    /// <summary>
    /// 条件不满足的都不自动解除：单不是 HELD（在 RIoT 跑起来了、被取消了）、车上还挂着别的单、急停是 <c>CAN_NOT_RECOVER</c>（按钮按着）。
    /// 读回锁闭 20 秒，一条 cancelEmergency 都没有。
    /// </summary>
    [Theory]
    [InlineData("order-running")]
    [InlineData("order-ended")]
    [InlineData("foreign-order")]
    [InlineData("can-not-recover")]
    [Trait("Requirement", "REQ-0167")]
    public async Task NoAutomaticReleaseUnlessTheHeldOrderIsStillHeldAndTheLatchCanRecover(string condition)
    {
        await using RuntimeFixture fixture = await LatchedForUnreadDoorsAsync();
        Latched latched = await LatchedFactsAsync(fixture);
        switch (condition)
        {
            case "order-running":
                fixture.Riot.SetOrderState(latched.UpperId, RiotOrderState.Executing, terminal: false);
                break;
            case "order-ended":
                fixture.Riot.SetOrderState(latched.UpperId, RiotOrderState.Cancelled, terminal: true);
                fixture.UnfinishedOrderIds = [];
                break;
            case "foreign-order":
                fixture.UnfinishedOrderIds = [latched.OrderId, "FOREIGN-ORDER-0001"];
                break;
            case "can-not-recover":
                fixture.ButtonHeld = true;
                break;
        }

        await ReportLockedAsync(fixture);
        await DriveRoundsAsync(fixture, 20);

        Assert.Equal(0, await CountAsync(fixture, RiotCommandTypeNames.CancelEmergency));
    }

    /// <summary>一扇门读到过没锁的急停：读回锁闭多久都不自动解除，照旧要人确认门锁已检修（CP-0011）。</summary>
    [Fact]
    [Trait("Requirement", "REQ-0167")]
    public async Task AStopForADoorReadNotLockedIsNeverReleasedOnItsOwn()
    {
        await using RuntimeFixture fixture = await InTransitDoorEmergencyReleaseTests.LatchedForTheDoorsAsync();

        await ReportLockedAsync(fixture);
        await DriveRoundsAsync(fixture, 20);

        Assert.Equal(0, await CountAsync(fixture, RiotCommandTypeNames.CancelEmergency));
    }

    /// <summary>
    /// 同一次故障里先读不到、后读到没锁（急停锁着之后才读到）：整次故障按「读到没锁」算，之后读回锁闭也不自动解除（CP-0017 修订项二说明 3）。
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-0167")]
    public async Task ADoorReadNotLockedLaterInTheSameFaultKeepsTheStopWithAPerson()
    {
        await using RuntimeFixture fixture = await LatchedForUnreadDoorsAsync();

        await ReportNotLockedAsync(fixture);
        await DriveOneRoundAsync(fixture);
        await ReportLockedAsync(fixture);
        await DriveRoundsAsync(fixture, 20);

        Assert.Equal(0, await CountAsync(fixture, RiotCommandTypeNames.CancelEmergency));
    }

    /// <summary>
    /// 两轮之间车载端连发「没锁」「锁闭」两条摘要（锁跳了一下），中间没有一轮读到「没锁」那条：服务端照样算读到过没锁，读回锁闭多久都不自动解除
    /// （cs#839 审查 H1：每轮只取当前那一条时，这一跳落在两轮之间就看不见，急停被解除）。
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-0167")]
    public async Task ANotLockedReadBetweenTwoRoundsKeepsTheStopWithAPerson()
    {
        await using RuntimeFixture fixture = await LatchedForUnreadDoorsAsync();

        await ReportNotLockedAsync(fixture);
        await ReportLockedAsync(fixture);
        await DriveRoundsAsync(fixture, 20);

        Assert.Equal(0, await CountAsync(fixture, RiotCommandTypeNames.CancelEmergency));
    }

    /// <summary>
    /// 还没有任何门锁异常时，两轮之间先收到「没锁」、再收到「读不到」，没有一轮读到那条「没锁」：同一轮按读到没锁交故障模型，之后读回锁闭
    /// 多久都不自动解除（cs#839 复审情况 a、探针 P1）。修之前这一形宽限满 3 秒以 GRACE_EXPIRED 交出、记成「只是读不到」，读回锁闭被自动解除。
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-0167")]
    [Trait("Requirement", "REQ-0246")]
    public async Task ANotLockedThenAnUnreadBetweenTwoRoundsBeforeAnyFaultKeepsTheStopWithAPerson()
    {
        await using RuntimeFixture fixture = await DrivingBetweenStationsAsync(behindTheReadinessGate: false);
        fixture.Clock.Advance(TimeSpan.FromMilliseconds(300));
        await ReportNotLockedAsync(fixture);
        fixture.Clock.Advance(TimeSpan.FromMilliseconds(300));
        await ReportUnknownAsync(fixture);
        await DriveOneRoundAsync(fixture);
        Assert.Equal(1, await CountAsync(fixture, RiotCommandTypeNames.TriggerEmergency));
        await LatchAsync(fixture);

        await ReportLockedAsync(fixture);
        await DriveRoundsAsync(fixture, 20);

        Assert.Equal(0, await CountAsync(fixture, RiotCommandTypeNames.CancelEmergency));
    }

    /// <summary>
    /// 同一形发生在服务端刚重启之后：这一进程还没看过这台车，没有「上一轮以来」可读；倒查连续「读不到」那一段时，断开它的是一条行驶中的
    /// 「没锁」，照样按读到没锁交出，不自动解除（复审修法第 3 条）。
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-0167")]
    public async Task ANotLockedJustBeforeTheUnreadRunIsReadAfterARestartToo()
    {
        await using RuntimeFixture fixture = await DrivingBetweenStationsAsync(behindTheReadinessGate: false);
        fixture.DoorReadings = new InTransitDoorReadings(Microsoft.Extensions.Options.Options.Create(new VehicleFaultOptions()));
        await fixture.RecreateEngineAsync();
        fixture.Clock.Advance(TimeSpan.FromMilliseconds(300));
        await ReportNotLockedAsync(fixture);
        fixture.Clock.Advance(TimeSpan.FromMilliseconds(300));
        await ReportUnknownAsync(fixture);
        await DriveOneRoundAsync(fixture);
        Assert.Equal(1, await CountAsync(fixture, RiotCommandTypeNames.TriggerEmergency));
        await LatchAsync(fixture);

        await ReportLockedAsync(fixture);
        await DriveRoundsAsync(fixture, 20);

        Assert.Equal(0, await CountAsync(fixture, RiotCommandTypeNames.CancelEmergency));
    }

    /// <summary>
    /// 宽限中 10 秒没被任何一轮看过（一轮读 RIoT 超时就够），期间先「没锁」又「读不到」：那条「没锁」照样记下，交故障模型之后读回锁闭
    /// 也不会成为「读数已恢复」（复审情况 b、探针 P2：丢弃过期宽限发生在判两轮之间的没锁之前）。
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-0167")]
    public void ANotLockedReadWhileAGraceWentUnobservedIsNotForgotten()
    {
        InTransitDoorReadings readings = new(Microsoft.Extensions.Options.Options.Create(new VehicleFaultOptions()));
        Assert.Equal(InTransitDoorAction.InGrace, readings.Observe(
            "AGV-A", new InTransitDoorVerdict(InTransitDoorState.SlotStateUnknown, 1, 1, Now) { UnknownSince = Now }, false,
            Now.AddSeconds(0.5)).Action);
        Assert.Equal(Now, readings.SinceFor("AGV-A"));

        InTransitDoorVerdict after = new(InTransitDoorState.SlotStateUnknown, 1, 4, Now.AddSeconds(8))
        {
            UnknownSince = Now.AddSeconds(8),
            Between = [new(InTransitDoorState.ProvenLocked, Now.AddSeconds(5)), new(InTransitDoorState.NotLocked, Now.AddSeconds(7))],
        };
        InTransitDoorReading handed = readings.Observe("AGV-A", after, false, Now.AddSeconds(12));
        Assert.Equal(
            (InTransitDoorAction.HandToFault, InTransitDoorReadings.HandReadNotLocked), (handed.Action, handed.HandedBecause));

        bool restored = false;
        for (int second = 13; second <= 26; second++)
        {
            restored |= readings.Observe(
                "AGV-A", new InTransitDoorVerdict(InTransitDoorState.ProvenLocked, 1, 5, Now.AddSeconds(12.5)), true,
                Now.AddSeconds(second)).ReadingRestored;
        }

        Assert.False(restored);
        Assert.False(readings.OnlyUnreadAndLockedSince("AGV-A"));
    }

    /// <summary>两轮之间夹着一条「读不到」：连续 10 秒读回锁闭从头数，满 10 秒才解除（审查 H1 的另一半）。</summary>
    [Fact]
    [Trait("Requirement", "REQ-0167")]
    public async Task AnUnreadReadingBetweenTwoRoundsStartsTheTenSecondsAgain()
    {
        await using RuntimeFixture fixture = await LatchedForUnreadDoorsAsync();

        await ReportLockedAsync(fixture);
        await DriveRoundsAsync(fixture, 8);
        await ReportUnknownAsync(fixture);
        await ReportLockedAsync(fixture);
        await DriveRoundsAsync(fixture, 9);
        Assert.Equal(0, await CountAsync(fixture, RiotCommandTypeNames.CancelEmergency));

        await DriveRoundsAsync(fixture, 2);
        Assert.Equal(1, await CountAsync(fixture, RiotCommandTypeNames.CancelEmergency));
    }

    /// <summary>
    /// 自动解除的四个前提单独核（审查 M4）：本代 Hold 回查未确认、故障已到第二级（隔离）、RIoT 上这张单不是 7、读到的是另一张单——
    /// 任一条不满足都没有放行条件；全部满足才有，而且挂在本代故障下。
    /// </summary>
    [Theory]
    [InlineData("all-met", true)]
    [InlineData("hold-pending", false)]
    [InlineData("hold-unknown", false)]
    [InlineData("isolated", false)]
    [InlineData("order-running", false)]
    [InlineData("another-order", false)]
    [InlineData("not-restored", false)]
    [InlineData("released-before", false)]
    [Trait("Requirement", "REQ-0167")]
    public void TheDoorReadingReleaseNeedsEveryPremise(string condition, bool earned)
    {
        RiotOrderCommandTarget target = new("AGV-X", "W2G-X", "ORDER-X");
        VehicleFaultFact fault = new(
            "AGV-X",
            condition == "isolated" ? VehicleFaultLevel.ConfirmedIsolated : VehicleFaultLevel.SuspectedBlocked,
            4,
            VehicleFaultEvidence.DoorNotProvenLocked,
            false, Now, Now, true, Now, Now, null, null);
        FaultedVehicleContext context = new(target, null, DoorReadingRestored: condition != "not-restored");
        RiotOrderCommandOutcome? hold = condition switch
        {
            "hold-pending" => RiotOrderCommandOutcome.Pending,
            "hold-unknown" => RiotOrderCommandOutcome.Unknown,
            _ => RiotOrderCommandOutcome.Confirmed,
        };
        RiotOrderObservation order = new(
            "W2G-X",
            RiotOrderObservationKind.Active,
            condition == "another-order" ? "ORDER-Y" : "ORDER-X",
            condition == "order-running" ? RiotOrderState.Executing : RiotOrderState.Paused);

        DoorReadingReleaseAllowance? allowance = VehicleFaultCoordinator.DoorReadingRelease(
            fault, context, hold, order, releasedBefore: condition == "released-before");

        Assert.Equal(earned ? new DoorReadingReleaseAllowance(4, "ORDER-X") : null, allowance);
    }

    /// <summary>
    /// 急停监管只认本次急停那一代、也是当前故障那一代的放行条件（审查 M4 第 4 条）：放行条件属于另一代、或当前故障已经换代，都照旧挡在
    /// 「原因未消除」上；属于这一代时只放过它点名的那张 7。
    /// </summary>
    /// <remarks>
    /// 触发行没有代次（<c>null</c>）那一格单独守「放行条件要属于本次急停那一代」：有代次而对不上时另有「故障代次已变」挡着，
    /// 删掉代次核对它也不红。
    /// </remarks>
    [Theory]
    [InlineData(4L, 4L, 4L, true)]
    [InlineData(3L, 4L, 4L, false)]
    [InlineData(4L, 3L, 4L, false)]
    [InlineData(4L, 4L, 5L, false)]
    [InlineData(4L, null, 4L, false)]
    [Trait("Requirement", "REQ-0167")]
    public void TheSupervisorTakesAnAllowanceOnlyForTheTriggersAndTheFaultsGeneration(
        long allowanceGeneration, long? triggerGeneration, long faultGeneration, bool released)
    {
        VehicleFaultFact fault = new(
            "AGV-X", VehicleFaultLevel.SuspectedBlocked, faultGeneration, VehicleFaultEvidence.DoorNotProvenLocked,
            false, Now, Now, true, Now, Now, null, null);
        RiotVehicleEmergencyObservation latch = new("DEV-X", RiotVehicleEmergencyObservation.CanRecover, Now);
        RiotVehicleOrderObservation orders = new(
            "DEV-X", true, ["ORDER-X"], Now, new Dictionary<string, int?> { ["ORDER-X"] = RiotOrderState.Paused });

        IReadOnlyList<string> obstacles = EmergencyStopSupervisor.ReleaseObstacles(
            latch, triggerGeneration, fault, orders, new DoorReadingReleaseAllowance(allowanceGeneration, "ORDER-X"));

        Assert.Equal(released, obstacles.Count == 0);
    }

    /// <summary>
    /// 急停监管在 cancelEmergency 之前那一次读到的清单里，放行条件点名的那张单不是 7（3、9、读不出状态）就拒，原因
    /// <c>EMERGENCY_HELD_ORDER_NOT_PAUSED</c>（审查 M4 第 1 条：协调器读到 7 之后、解除之前单被跑起来）；清单里多出别的单也拒。
    /// </summary>
    [Theory]
    [InlineData(7, false, null)]
    [InlineData(3, false, EmergencyStopSupervisor.HeldOrderNotPausedReason)]
    [InlineData(9, false, EmergencyStopSupervisor.HeldOrderNotPausedReason)]
    [InlineData(null, false, EmergencyStopSupervisor.HeldOrderNotPausedReason)]
    [InlineData(7, true, "EMERGENCY_VEHICLE_ORDER_NOT_FINISHED")]
    [Trait("Requirement", "REQ-0167")]
    public void TheSupervisorReadsTheHeldOrderAgainRightBeforeTheRelease(int? state, bool another, string? obstacle)
    {
        VehicleFaultFact fault = new(
            "AGV-X", VehicleFaultLevel.SuspectedBlocked, 4, VehicleFaultEvidence.DoorNotProvenLocked,
            false, Now, Now, true, Now, Now, null, null);
        RiotVehicleEmergencyObservation latch = new("DEV-X", RiotVehicleEmergencyObservation.CanRecover, Now);
        Dictionary<string, int?> states = new() { ["ORDER-X"] = state };
        if (another)
        {
            states["ORDER-Y"] = RiotOrderState.Executing;
        }

        IReadOnlyList<string> obstacles = EmergencyStopSupervisor.ReleaseObstacles(
            latch, 4, fault, new RiotVehicleOrderObservation("DEV-X", true, [.. states.Keys], Now, states),
            new DoorReadingReleaseAllowance(4, "ORDER-X"));

        Assert.Equal(obstacle is null ? [] : [obstacle], obstacles);
    }

    /// <summary>宽限期间读到过一次没锁、随后又只是读不到：同样按「读到没锁」算，不自动解除。</summary>
    [Fact]
    [Trait("Requirement", "REQ-0167")]
    public async Task ADoorReadNotLockedDuringTheGraceKeepsTheStopWithAPerson()
    {
        await using RuntimeFixture fixture = await DrivingBetweenStationsAsync(behindTheReadinessGate: false);
        await ReportUnknownAsync(fixture);
        await DriveOneRoundAsync(fixture);
        await ReportNotLockedAsync(fixture);
        await DriveOneRoundAsync(fixture);
        await ReportUnknownAsync(fixture);
        await LatchAsync(fixture);

        await ReportLockedAsync(fixture);
        await DriveRoundsAsync(fixture, 20);

        Assert.Equal(0, await CountAsync(fixture, RiotCommandTypeNames.CancelEmergency));
    }

    /// <summary>
    /// 服务端在急停锁着时重启：这一次故障的读数经过在新进程里没有记录，按「读到过没锁」从严，不自动解除，交给人（与 CP-0017 之前一样）。
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-0167")]
    public async Task AfterARestartTheStopIsLeftToAPerson()
    {
        await using RuntimeFixture fixture = await LatchedForUnreadDoorsAsync();
        fixture.DoorReadings = new InTransitDoorReadings(Microsoft.Extensions.Options.Options.Create(new VehicleFaultOptions()));
        await fixture.RecreateEngineAsync();

        await ReportLockedAsync(fixture);
        await DriveRoundsAsync(fixture, 20);

        Assert.Equal(0, await CountAsync(fixture, RiotCommandTypeNames.CancelEmergency));
    }

    /// <summary>
    /// 自动解除之后又读不到：车停着、门锁没证明锁闭，照 CP-0011 起的规矩重新急停；这一代故障已经自动解除过一次，第二次读回锁闭不再自动解除。
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-0167")]
    [Trait("Requirement", "REQ-0248")]
    public async Task OneFaultIsReleasedOnItsOwnOnlyOnce()
    {
        await using RuntimeFixture fixture = await LatchedForUnreadDoorsAsync();
        await ReportLockedAsync(fixture);
        await DriveRoundsAsync(fixture, 11);
        fixture.EmergencyLatched = false;
        await DriveOneRoundAsync(fixture);
        Assert.Equal(1, await CountAsync(fixture, RiotCommandTypeNames.CancelEmergency));

        await ReportUnknownAsync(fixture);
        await DriveOneRoundAsync(fixture);
        Assert.Equal(2, await CountAsync(fixture, RiotCommandTypeNames.TriggerEmergency));
        fixture.EmergencyLatched = true;
        await DriveOneRoundAsync(fixture);

        await ReportLockedAsync(fixture);
        await DriveRoundsAsync(fixture, 20);
        Assert.Equal(1, await CountAsync(fixture, RiotCommandTypeNames.CancelEmergency));
    }

    /// <summary>自动解除之后那张单在 RIoT 自己跑起来（没人按继续）：照人工解除之后的规矩点名 <c>HELD_ORDER_RESUMED_WITHOUT_CONTINUE</c>。</summary>
    [Fact]
    [Trait("Requirement", "REQ-0167")]
    public async Task AHeldOrderThatRunsAfterTheAutomaticReleaseIsNamed()
    {
        await using RuntimeFixture fixture = await LatchedForUnreadDoorsAsync();
        Latched latched = await LatchedFactsAsync(fixture);
        await ReportLockedAsync(fixture);
        await DriveRoundsAsync(fixture, 11);
        fixture.EmergencyLatched = false;
        await DriveRoundsAsync(fixture, 2);

        fixture.Riot.SetOrderState(latched.UpperId, RiotOrderState.Executing, terminal: false);
        await DriveOneRoundAsync(fixture);

        Assert.Equal(JourneyRuntimeEngine.HeldOrderResumedWithoutContinueReason, (await fixture.RuntimeAsync()).BlockReasonCode);
        Assert.Equal(0, await CountAsync(fixture, RiotCommandTypeNames.OrderContinue));
    }

    /// <summary>
    /// 人工入口（CP-0017 第六节第 3 条）：只因读不到的急停、读数已恢复，人解除时不再要求勾「门锁已检修」；读到过没锁的照旧要求。
    /// </summary>
    [Theory]
    [InlineData("unread-and-locked", false)]
    [InlineData("unread-still-unknown", true)]
    [InlineData("read-not-locked", true)]
    [Trait("Requirement", "REQ-0356")]
    public async Task TheClassThreeEntryAsksForARepairedLockOnlyWhenADoorReadNotLocked(string history, bool repairRequired)
    {
        await using RuntimeFixture fixture = history == "read-not-locked"
            ? await InTransitDoorEmergencyReleaseTests.LatchedForTheDoorsAsync()
            : await LatchedForUnreadDoorsAsync();
        if (history != "unread-still-unknown")
        {
            await ReportLockedAsync(fixture);
            await DriveRoundsAsync(fixture, 2);
        }

        await using ControlServerDbContext context = new(fixture.DbOptionsForTests);
        bool doorCause = await EmergencyStopDoorCause.ReadAsync(
            context,
            new VehicleFaultStore(context),
            fixture.CreateEmergencyStopSupervisor(context),
            new EmergencyStopSubject(fixture.Options.AgvId, fixture.Options.VehicleKey),
            Token,
            fixture.DoorReadings);

        Assert.Equal(repairRequired, doorCause);
    }

    // ---- helpers -------------------------------------------------------------------------------------------------

    private sealed record Latched(string UpperId, string OrderId);

    /// <summary>
    /// 关卡段有货、两站之间行驶，车载端报读不到：宽限满 3 秒按住、急停；RIoT 把单停成 7、闩锁锁上、车停下，下一轮确认触发。
    /// </summary>
    private static async Task<RuntimeFixture> LatchedForUnreadDoorsAsync(bool behindTheReadinessGate = false)
    {
        RuntimeFixture fixture = await DrivingBetweenStationsAsync(behindTheReadinessGate);
        await ReportUnknownAsync(fixture);
        await DriveRoundsAsync(fixture, 3);
        await LatchAsync(fixture);
        return fixture;
    }

    private static async Task<RuntimeFixture> DrivingBetweenStationsAsync(bool behindTheReadinessGate)
    {
        RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(FirstDemandId, FirstSublot, createdAt: Now.AddMinutes(-10)));
        fixture.BoxCounts.Set(FirstSublot, 7);
        await fixture.AdvanceToGateArrivalAsync();
        fixture.Context.ChangeTracker.Clear();
        if (behindTheReadinessGate)
        {
            await OwnOrderRebuildTests.DropSessionOnOwnOrderAsync(fixture);
        }
        fixture.Riot.MovementState = "MT_RUNNING";
        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { CurrentStationId = 0 };
        await ReportLockedAsync(fixture);
        await DriveOneRoundAsync(fixture);
        return fixture;
    }

    /// <summary>已经按住、急停之后：RIoT 停住单，闩锁锁上，车停下，再跑一轮把触发确认下来。前置都在这里断。</summary>
    private static async Task LatchAsync(RuntimeFixture fixture)
    {
        Assert.Equal(1, await CountAsync(fixture, RiotCommandTypeNames.OrderHold));
        Assert.Equal(1, await CountAsync(fixture, RiotCommandTypeNames.TriggerEmergency));
        Latched latched = await LatchedFactsAsync(fixture);
        fixture.Riot.SetOrderState(latched.UpperId, RiotOrderState.Paused, terminal: false);
        fixture.Riot.MovementState = "MT_PAUSED";
        fixture.EmergencyLatched = true;
        fixture.UnfinishedOrderIds = [latched.OrderId];
        await DriveOneRoundAsync(fixture);
        Assert.Equal(0, await CountAsync(fixture, RiotCommandTypeNames.CancelEmergency));
    }

    private static async Task<Latched> LatchedFactsAsync(RuntimeFixture fixture)
    {
        string upperId = (await fixture.RuntimeAsync()).GateUpperId!;
        await using ControlServerDbContext reading = new(fixture.DbOptionsForTests);
        return new Latched(
            upperId, (await reading.OrderIntents.AsNoTracking().SingleAsync(row => row.UpperId == upperId, Token)).OrderId!);
    }

    private static async Task DriveRoundsAsync(RuntimeFixture fixture, int rounds)
    {
        for (int round = 0; round < rounds; round++)
        {
            await DriveOneRoundAsync(fixture);
        }
    }

    private static async Task<int> CountAsync(RuntimeFixture fixture, string commandType)
    {
        await using ControlServerDbContext reading = new(fixture.DbOptionsForTests);
        return await reading.RiotOrderCommandAudit.AsNoTracking().CountAsync(row => row.CommandType == commandType, Token);
    }

    private static async Task ResumeByAPersonAsync(RuntimeFixture fixture, string upperId)
    {
        VehicleFaultRecoveryTests.SiteRiot site = new(fixture)
        {
            HasUnfinishedOrder = true,
            OnOrderCommand = (kind, _) =>
            {
                if (kind == RiotOrderCommandKind.ContinueFromHeld)
                {
                    fixture.Riot.SetOrderState(upperId, RiotOrderState.Executing, terminal: false);
                }
            },
        };
        VehicleFaultRecoveryDecision resumed = await VehicleFaultRecoveryTests.Service(fixture, site).RecoverAsync(
            new VehicleFaultRecoveryRequest(
                new EmergencyStopSubject(fixture.Options.AgvId, fixture.Options.VehicleKey),
                VehicleFaultRecoveryAction.ResumeHeldOrder,
                "L1-OPERATOR",
                FaultRemedied: true,
                Note: "door reading restored"),
            Token);
        Assert.Equal(VehicleFaultRecoveryOutcome.Resumed, resumed.Outcome);
        fixture.Context.ChangeTracker.Clear();
    }
}
