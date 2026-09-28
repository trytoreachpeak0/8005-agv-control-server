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
/// control-server#335，用户 2026-09-28 选的 A（经 Coordinator 8 转）：车在路上因门锁被按住、急停锁住之后，门锁恢复为新鲜、锁闭、
/// 仓位已知，服务端自动解除急停（REQ-0167）；单仍是本服务端按住的 HELD，不自动继续，要人经故障处置入口「继续原单」
/// （REQ-0239、来源决定 issue 70 答案第 9 条）。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么需要它。</b>在这之前，这种车四条出路全被挡：人工继续在锁住时拒绝，人工清除只认 FAILED 的单，人工确认解除
/// （REQ-0356）要车上无货且没有未终态的单，自动解除要故障已清且没有未完成单——而本服务端按住的单是 PAUSED(7)，
/// 算未完成。有货的车在途因门锁急停，现场只能改库。
/// </para>
/// <para>
/// <b>放行收得很窄</b>（调度 2026-09-28 列的四条）：只放行一种未完成单——本服务端、本代故障、自己发出并回查确认过的
/// <c>OrderHold</c> 留下的 PAUSED(7)；1、3、9 照旧挡，别的单照旧挡。只针对门锁症状，订单 FAILED 照旧只能人工处置。
/// 门锁要新鲜、锁闭，而且没有 <c>SLOT_STATE_UNKNOWN</c>。故障事实本身不自动清除：人工继续（<c>ResumeRefusals</c>）
/// 要求故障仍在效，清掉了人就再也继续不了。
/// </para>
/// <para>
/// <b>REQ-0356 的人工确认解除不动</b>：它的条文写明「该车仍有未进入终态的订单时不得解除」。本票放宽的是自动解除，
/// REQ-0167 与白名单 1.5 节的自动解除都没有这一句（program 仓 v1.6.0 基线实读）。
/// </para>
/// </remarks>
public sealed class InTransitDoorEmergencyReleaseTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// 整条路：门没锁 → 按住、急停锁住 → 门锁恢复 → 自动解除急停 → 单仍 HELD、没有任何 CONTINUE、
    /// 车停在两站之间也不再被急停 → 人按「继续原单」，CONTINUE 发出、故障清除。全程不用改库。
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-0246")]
    [Trait("Requirement", "REQ-0167")]
    [Trait("Requirement", "REQ-0239")]
    public async Task ALatchRaisedForTheDoorsIsReleasedOnceTheyAreLockedAndTheHeldOrderWaitsForAPerson()
    {
        await using RuntimeFixture fixture = await LatchedForTheDoorsAsync();
        Latched latched = await LatchedFactsAsync(fixture);

        // 门锁恢复：新鲜、锁闭、仓位已知。这一轮发出 cancelEmergency。
        await ReportLockedAsync(fixture);
        await DriveOneRoundAsync(fixture);
        Assert.Equal(1, await CountAsync(fixture, RiotCommandTypeNames.CancelEmergency));

        // RIoT 放开闩锁；下一轮把解除确认下来。
        fixture.EmergencyLatched = false;
        await DriveOneRoundAsync(fixture);

        await using (ControlServerDbContext reading = new(fixture.DbOptionsForTests))
        {
            RiotOrderCommandAuditRow release = Assert.Single(await reading.RiotOrderCommandAudit.AsNoTracking()
                .Where(row => row.CommandType == RiotCommandTypeNames.CancelEmergency).ToArrayAsync(Token));
            Assert.Equal((RiotOrderCommandOutcome.Confirmed, (long?)1), (release.Outcome, release.FaultGeneration));
            RiotOrderCommandAuditRow hold = Assert.Single(await reading.RiotOrderCommandAudit.AsNoTracking()
                .Where(row => row.CommandType == RiotCommandTypeNames.OrderHold).ToArrayAsync(Token));
            Assert.Equal((latched.UpperId, RiotOrderCommandOutcome.Confirmed), (hold.TargetUpperId, hold.Outcome));
            // 故障仍在效：人工继续靠它。
            VehicleFaultStateRow fault = await reading.VehicleFaultStates.AsNoTracking().SingleAsync(Token);
            Assert.Equal((VehicleFaultLevel.SuspectedBlocked, 1L, InTransitDoorLockFaultTests.DoorSymptom),
                (fault.Level, fault.FaultGeneration, fault.EvidenceCode));
        }

        // 解除之后：单仍 HELD，没有任何 CONTINUE；车停在两站之间（报不出站点），不因此再急停。
        for (int round = 0; round < 10; round++)
        {
            await DriveOneRoundAsync(fixture);
            Assert.Equal(0, await CountAsync(fixture, RiotCommandTypeNames.OrderContinue));
            Assert.Equal(1, await CountAsync(fixture, RiotCommandTypeNames.TriggerEmergency));
            Assert.Equal(RiotOrderState.Paused, fixture.Riot.OrderStateOf(latched.UpperId));
        }

        // 人按「继续原单」：CONTINUE 发出、单回到执行、故障清除。
        VehicleFaultRecoveryTests.SiteRiot site = new(fixture)
        {
            HasUnfinishedOrder = true,
            OnOrderCommand = (kind, _) =>
            {
                if (kind == RiotOrderCommandKind.ContinueFromHeld)
                {
                    fixture.Riot.SetOrderState(latched.UpperId, RiotOrderState.Executing, terminal: false);
                }
            },
        };
        VehicleFaultRecoveryDecision resumed = await VehicleFaultRecoveryTests.Service(fixture, site).RecoverAsync(
            new VehicleFaultRecoveryRequest(
                new EmergencyStopSubject(fixture.Options.AgvId, fixture.Options.VehicleKey),
                VehicleFaultRecoveryAction.ResumeHeldOrder,
                "L1-OPERATOR",
                FaultRemedied: true,
                Note: "doors checked on site"),
            Token);
        Assert.Equal(VehicleFaultRecoveryOutcome.Resumed, resumed.Outcome);
        Assert.Equal([RiotOrderCommandKind.ContinueFromHeld], site.OrderCommands);
    }

    /// <summary>
    /// 门锁恢复了，但车上那张单不是「本代自己按住并确认的 HELD」：RIoT 报它 QUEUEING(1)、EXECUTING(3) 或 HANG(9)。
    /// 一概不解除——那几种单解锁之后 RIoT 有东西可以驱动车。
    /// </summary>
    [Theory]
    [InlineData(RiotOrderState.Queueing)]
    [InlineData(RiotOrderState.Executing)]
    [InlineData(RiotOrderState.Hang)]
    [Trait("Requirement", "REQ-0167")]
    public async Task AnOrderThatIsNotTheOwnConfirmedHoldKeepsTheLatch(int orderState)
    {
        await using RuntimeFixture fixture = await LatchedForTheDoorsAsync();
        Latched latched = await LatchedFactsAsync(fixture);
        fixture.Riot.SetOrderState(latched.UpperId, orderState, terminal: false);

        await ReportLockedAsync(fixture);
        await AssertNoReleaseOverRoundsAsync(fixture);
    }

    /// <summary>车上除了本服务端按住的那张，RIoT 还列着另一张未完成单：不解除。</summary>
    [Fact]
    [Trait("Requirement", "REQ-0167")]
    public async Task AnotherUnfinishedOrderOnTheVehicleKeepsTheLatch()
    {
        await using RuntimeFixture fixture = await LatchedForTheDoorsAsync();
        Latched latched = await LatchedFactsAsync(fixture);
        fixture.UnfinishedOrderIds = [latched.OrderId, "FOREIGN-ORDER-0001"];

        await ReportLockedAsync(fixture);
        await AssertNoReleaseOverRoundsAsync(fixture);
    }

    /// <summary>
    /// 门锁没有恢复到「新鲜、锁闭、仓位已知」：报仓位状态未知，或门锁报锁着但车载端已超过静默窗口没有说话。不解除。
    /// </summary>
    [Theory]
    [InlineData("slot-state-unknown")]
    [InlineData("stale")]
    [Trait("Requirement", "REQ-0167")]
    public async Task DoorsNotProvenLockedKeepTheLatch(string doors)
    {
        await using RuntimeFixture fixture = await LatchedForTheDoorsAsync();
        if (doors == "slot-state-unknown")
        {
            await fixture.ReportSafetySummaryAsync(
                allTargetSlotsLocked: false,
                unknownPresent: true,
                ["SLOT_STATE_UNKNOWN", "LOCK_NOT_CLOSED", "UNLOCK_OUTPUT_NOT_RESET", "ACTION_NOT_ALLOWED_IN_STATE"],
                allUnlockOutputsReset: false);
            await AssertNoReleaseOverRoundsAsync(fixture);
        }
        else
        {
            await ReportLockedAsync(fixture);
            // 这一条锁着的摘要之后，车载端再不说话：静默窗口过后它不再算新鲜。
            fixture.Clock.Advance(SessionLiveness.Timeout + TimeSpan.FromSeconds(1));
            await AssertNoReleaseOverRoundsAsync(fixture, hear: false);
        }
    }

    /// <summary>
    /// 反向：订单 FAILED 那一类症状照旧只能人工处置。急停锁住、门锁锁着、车上没有未完成单，也不自动解除——
    /// 故障没有被人清除，放行只认门锁症状。
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-0167")]
    [Trait("Requirement", "REQ-0232")]
    public async Task AFailedOrderFaultIsNotReleasedAutomaticallyWhateverTheDoorsSay()
    {
        await using RuntimeFixture fixture = await GateArrivalWaitAsync();
        JourneyRuntimeRow underWay = await fixture.RuntimeAsync();
        fixture.Riot.MovementState = "MT_RUNNING";
        fixture.Riot.FailOrder(underWay.GateUpperId!);
        await DriveOneRoundAsync(fixture);
        Assert.Equal(1, await CountAsync(fixture, RiotCommandTypeNames.TriggerEmergency));
        fixture.EmergencyLatched = true;
        fixture.Riot.MovementState = "MT_FINISHED";
        await DriveOneRoundAsync(fixture);

        await ReportLockedAsync(fixture);
        await AssertNoReleaseOverRoundsAsync(fixture);
    }

    // ---- helpers -------------------------------------------------------------------------------------------------

    private sealed record Latched(string UpperId, string OrderId);

    /// <summary>
    /// 关卡段有货行驶中车载端报门没锁：同一轮按住、急停。随后 RIoT 把单停成 PAUSED(7)、闩锁锁上、车停下（MT_PAUSED），
    /// 车上唯一的未完成单就是这张；下一轮确认触发。前置条件都在这里断：之后的红只能来自解除这一段。
    /// </summary>
    private static async Task<RuntimeFixture> LatchedForTheDoorsAsync()
    {
        RuntimeFixture fixture = await GateArrivalWaitAsync();
        JourneyRuntimeRow underWay = await fixture.RuntimeAsync();
        string upperId = underWay.GateUpperId!;
        fixture.Riot.MovementState = "MT_RUNNING";
        await fixture.ReportSafetySummaryAsync(
            allTargetSlotsLocked: false, unknownPresent: false, ["LOCK_NOT_CLOSED", "ACTION_NOT_ALLOWED_IN_STATE"]);
        await DriveOneRoundAsync(fixture);
        Assert.Equal(1, await CountAsync(fixture, RiotCommandTypeNames.OrderHold));
        Assert.Equal(1, await CountAsync(fixture, RiotCommandTypeNames.TriggerEmergency));

        string orderId = await OrderIdAsync(fixture, upperId);
        fixture.Riot.SetOrderState(upperId, RiotOrderState.Paused, terminal: false);
        fixture.Riot.MovementState = "MT_PAUSED";
        fixture.EmergencyLatched = true;
        fixture.UnfinishedOrderIds = [orderId];
        await DriveOneRoundAsync(fixture);

        await using ControlServerDbContext reading = new(fixture.DbOptionsForTests);
        RiotOrderCommandAuditRow trigger = Assert.Single(await reading.RiotOrderCommandAudit.AsNoTracking()
            .Where(row => row.CommandType == RiotCommandTypeNames.TriggerEmergency).ToArrayAsync(Token));
        Assert.Equal(RiotOrderCommandOutcome.Confirmed, trigger.Outcome);
        Assert.Equal(0, await CountAsync(fixture, RiotCommandTypeNames.CancelEmergency));
        Assert.Equal(InTransitDoorLockFaultTests.DoorSymptom, (await fixture.RuntimeAsync()).BlockReasonCode);
        return fixture;
    }

    private static async Task<Latched> LatchedFactsAsync(RuntimeFixture fixture)
    {
        string upperId = (await fixture.RuntimeAsync()).GateUpperId!;
        return new Latched(upperId, await OrderIdAsync(fixture, upperId));
    }

    private static async Task<string> OrderIdAsync(RuntimeFixture fixture, string upperId)
    {
        await using ControlServerDbContext reading = new(fixture.DbOptionsForTests);
        return (await reading.OrderIntents.AsNoTracking().SingleAsync(row => row.UpperId == upperId, Token)).OrderId!;
    }

    private static Task ReportLockedAsync(RuntimeFixture fixture) =>
        fixture.ReportSafetySummaryAsync(allTargetSlotsLocked: true, unknownPresent: false, ["ACTION_NOT_ALLOWED_IN_STATE"]);

    /// <summary>跑几轮：一条 cancelEmergency 都没有，闩锁照旧，没有 CONTINUE。</summary>
    private static async Task AssertNoReleaseOverRoundsAsync(RuntimeFixture fixture, bool hear = true)
    {
        for (int round = 0; round < 5; round++)
        {
            await DriveOneRoundAsync(fixture, hear);
            Assert.Equal(0, await CountAsync(fixture, RiotCommandTypeNames.CancelEmergency));
            Assert.Equal(0, await CountAsync(fixture, RiotCommandTypeNames.OrderContinue));
        }
    }

    private static async Task<int> CountAsync(RuntimeFixture fixture, string commandType)
    {
        await using ControlServerDbContext reading = new(fixture.DbOptionsForTests);
        return await reading.RiotOrderCommandAudit.AsNoTracking().CountAsync(row => row.CommandType == commandType, Token);
    }

    private static async Task DriveOneRoundAsync(RuntimeFixture fixture, bool hear = true)
    {
        DateTimeOffset before = fixture.Clock.GetUtcNow();
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(fixture.Clock.GetUtcNow() > before, "the clock did not move");
        if (hear)
        {
            await fixture.HearFromPeerAsync();
        }
        fixture.Context.ChangeTracker.Clear();
        await fixture.Engine.ExecuteOnceAsync(Token);
        fixture.Context.ChangeTracker.Clear();
    }

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
}
