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
/// 行驶中仓门未证明锁闭被停下的车怎么出来（CP-0011，需求基线 v1.10.0，control-server#539）：门锁读回锁闭不解除任何东西，车退出服务、
/// 故障在效；人确认门锁已检修，经三类入口解除急停（有货走受治理解除，本服务端按住的那张 HELD 单可以留着），再经故障处置入口
/// 「继续原单」或清除故障（REQ-0239）。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么改。</b>control-server#335 到 #539 之前，门锁恢复为新鲜、锁闭、仓位已知时服务端自动解除急停。用户 2026-10-13 裁定：
/// 「锁可能坏了，识别不到车门的实际状态，车子移动时车门打开可能把车门撞坏」——门锁反馈回来了证明不了锁是好的，所以这一类不自动解除，
/// 交给检修过门锁的人。随之删掉的是自动解除那一整套放行判定（本代自确认的 7、外来单、门锁新鲜度），以及 control-server#527
/// 为那套放行补的振荡用例：没有自动解除，就没有「解除—再急停—再解除」。
/// </para>
/// <para>
/// <b>留下的</b>：解除之后车停在两站之间不因报不出站点再急停，其余照旧（REQ-0247）；豁免只属于那一代故障；闩锁还锁着、
/// 或本服务端还有急停开着时不发 CONTINUE；单在 RIoT 被终结之后仍有出路；解除后单自己跑起来要点名。
/// </para>
/// </remarks>
public sealed class InTransitDoorEmergencyReleaseTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// 整条路：门没锁 → 按住、急停锁住 → 门锁恢复，几轮里一条解除都不发 → 人确认门锁已检修，经三类入口（有货）解除 →
    /// 单仍 HELD、没有任何 CONTINUE、车停在两站之间也不再被急停 → 人按「继续原单」，CONTINUE 发出、故障清除。全程不用改库。
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-0246")]
    [Trait("Requirement", "REQ-0167")]
    [Trait("Requirement", "REQ-0356")]
    [Trait("Requirement", "REQ-0239")]
    public async Task ALatchRaisedForTheDoorsWaitsForAPersonWhoRepairedTheLockAndTheHeldOrderForAResume()
    {
        await using RuntimeFixture fixture = await LatchedForTheDoorsAsync();
        Latched latched = await LatchedFactsAsync(fixture);

        // 门锁恢复：新鲜、锁闭、仓位已知。不解除（CP-0011）。
        await ReportLockedAsync(fixture);
        await AssertNoReleaseOverRoundsAsync(fixture);

        // 人确认门锁已检修，经三类入口解除；RIoT 放开闩锁，下一轮把解除确认下来。
        await ReleaseByAPersonAsync(fixture, latched);
        Assert.Equal(1, await CountAsync(fixture, RiotCommandTypeNames.CancelEmergency));
        fixture.EmergencyLatched = false;
        await DriveOneRoundAsync(fixture);

        await using (ControlServerDbContext reading = new(fixture.DbOptionsForTests))
        {
            RiotOrderCommandAuditRow release = Assert.Single(await reading.RiotOrderCommandAudit.AsNoTracking()
                .Where(row => row.CommandType == RiotCommandTypeNames.CancelEmergency).ToArrayAsync(Token));
            Assert.Equal(RiotOrderCommandOutcome.Confirmed, release.Outcome);
            Assert.Contains(EmergencyStopSupervisor.GovernedLoadedReleaseReason, release.ReceiptJson ?? string.Empty, StringComparison.Ordinal);
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
        await ResumeByAPersonAsync(fixture, latched.UpperId);
        await using (ControlServerDbContext reading = new(fixture.DbOptionsForTests))
        {
            VehicleFaultStateRow cleared = await reading.VehicleFaultStates.AsNoTracking().SingleAsync(Token);
            Assert.Equal(VehicleFaultLevel.None, cleared.Level);
            Assert.NotNull(cleared.ClearedAt);
        }
    }

    /// <summary>
    /// 同一条路，但会话一直未就绪（真车载端在本服务端在途单上的常态：RecoveryRequired / DEPARTURE_SAFETY_NOT_READY）。每一轮走的是
    /// <c>NameInFlightOrderWithoutThePeerAsync</c> 而不是阶段正文：门锁恢复照样不解除，人解除之后照样不因报不出站点再急停，单仍停着。
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-0167")]
    [Trait("Requirement", "REQ-0246")]
    public async Task ALatchRaisedForTheDoorsWaitsForAPersonBehindTheReadinessGateToo()
    {
        await using RuntimeFixture fixture = await LatchedForTheDoorsAsync(behindTheReadinessGate: true);
        Latched latched = await LatchedFactsAsync(fixture);

        await ReportLockedAsync(fixture);
        await AssertNoReleaseOverRoundsAsync(fixture);
        await ReleaseByAPersonAsync(fixture, latched);
        fixture.EmergencyLatched = false;
        for (int round = 0; round < 5; round++)
        {
            await DriveOneRoundAsync(fixture);
        }

        Assert.NotEqual(SessionReadiness.Ready, (await fixture.Context.SessionRecoveries.AsNoTracking().SingleAsync(Token)).Readiness);
        Assert.Equal(1, await CountAsync(fixture, RiotCommandTypeNames.CancelEmergency));
        Assert.Equal(1, await CountAsync(fixture, RiotCommandTypeNames.TriggerEmergency));
        Assert.Equal(0, await CountAsync(fixture, RiotCommandTypeNames.OrderContinue));
        Assert.Equal(RiotOrderState.Paused, fixture.Riot.OrderStateOf(latched.UpperId));
    }

    /// <summary>
    /// 人解除之后，豁免只免「报不出站点」这一项（调度 2026-09-28；REQ-0247）。下面几种照旧重新急停，下一轮就发：
    /// 读到车在动、读不到运动、运动读数过期、门锁又报没锁、门锁又报仓位状态未知。
    /// </summary>
    /// <remarks>
    /// 门锁那两格是豁免范围的边界：豁免只在「此刻门锁仍是新鲜锁闭」时成立。车停在两站之间、门又打开，REQ-0246 的条件
    /// 全都成立——门锁未能证明锁闭，停稳证不出（报不出站点），监控无法排除继续移动——所以要重新急停。
    /// </remarks>
    [Theory]
    [InlineData("moving")]
    [InlineData("motion-unreadable")]
    [InlineData("motion-stale")]
    [InlineData("doors-not-locked-again")]
    [InlineData("slot-state-unknown-again")]
    [Trait("Requirement", "REQ-0246")]
    [Trait("Requirement", "REQ-0248")]
    public async Task AfterTheDoorReleaseTheVehicleIsStoppedAgainOnAnythingButAMissingStation(string reading)
    {
        await using RuntimeFixture fixture = await ReleasedForTheDoorsAsync();

        switch (reading)
        {
            case "moving":
                fixture.Riot.MovementState = "MT_RUNNING";
                break;
            case "motion-unreadable":
                fixture.Riot.MovementState = null;
                break;
            case "motion-stale":
                fixture.Riot.MotionObservedAtLag = new VehicleFaultOptions().MaximumEvidenceAge + TimeSpan.FromSeconds(2);
                break;
            case "doors-not-locked-again":
                await fixture.ReportSafetySummaryAsync(
                    allTargetSlotsLocked: false, unknownPresent: false, ["LOCK_NOT_CLOSED", "ACTION_NOT_ALLOWED_IN_STATE"]);
                break;
            case "slot-state-unknown-again":
                await fixture.ReportSafetySummaryAsync(
                    allTargetSlotsLocked: false,
                    unknownPresent: true,
                    ["SLOT_STATE_UNKNOWN", "LOCK_NOT_CLOSED", "UNLOCK_OUTPUT_NOT_RESET", "ACTION_NOT_ALLOWED_IN_STATE"],
                    allUnlockOutputsReset: false);
                break;
        }
        await DriveOneRoundAsync(fixture);

        Assert.Equal(2, await CountAsync(fixture, RiotCommandTypeNames.TriggerEmergency));
        Assert.Equal(0, await CountAsync(fixture, RiotCommandTypeNames.OrderContinue));
    }

    /// <summary>
    /// control-server#527 第 2 条：按住在升级那一轮发出时 RIoT 还没把单停成 7（Pending）；单随后读到 7，门锁仍然没锁。之后每一轮
    /// 都回查按住结果，下一轮就确认，<c>ORDER_HOLD_PENDING</c> 不再一直挂到门锁恢复（现场 05:22:10–05:26:08 挂了约 4 分钟）。
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-0234")]
    public async Task AHoldIssuedInTheEscalatingRoundIsReadBackWithoutWaitingForTheDoors()
    {
        await using RuntimeFixture fixture = await LatchedForTheDoorsAsync();

        await using ControlServerDbContext reading = new(fixture.DbOptionsForTests);
        RiotOrderCommandAuditRow hold = Assert.Single(await reading.RiotOrderCommandAudit.AsNoTracking()
            .Where(row => row.CommandType == RiotCommandTypeNames.OrderHold).ToArrayAsync(Token));
        Assert.Equal(RiotOrderCommandOutcome.Confirmed, hold.Outcome);
        Assert.Equal(0, await CountAsync(fixture, RiotCommandTypeNames.CancelEmergency));
    }

    /// <summary>
    /// 豁免只属于那一代故障：人按继续、故障清除之后，车又在两站之间报门没锁，这是新一代故障，照常按住、急停——
    /// 上一代的人工解除不替它豁免。
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-0246")]
    public async Task TheExemptionEndsWithTheFaultGeneration()
    {
        await using RuntimeFixture fixture = await ReleasedForTheDoorsAsync();
        Latched latched = await LatchedFactsAsync(fixture);
        await ResumeByAPersonAsync(fixture, latched.UpperId);

        // 车照原单开了一段，停在两站之间（不在动：否则「读到运动」本身就会急停，这一格就看不出豁免带没带过来）；门又报没锁。
        fixture.Riot.MovementState = "MT_RUNNING";
        fixture.UnfinishedOrderIds = [latched.OrderId];
        await DriveOneRoundAsync(fixture);
        fixture.Riot.MovementState = "MT_PAUSED";
        await fixture.ReportSafetySummaryAsync(
            allTargetSlotsLocked: false, unknownPresent: false, ["LOCK_NOT_CLOSED", "ACTION_NOT_ALLOWED_IN_STATE"]);
        await DriveOneRoundAsync(fixture);

        await using ControlServerDbContext reading = new(fixture.DbOptionsForTests);
        VehicleFaultStateRow fault = await reading.VehicleFaultStates.AsNoTracking().SingleAsync(Token);
        Assert.Equal((VehicleFaultLevel.SuspectedBlocked, 2L), (fault.Level, fault.FaultGeneration));
        Assert.Equal(2, await CountAsync(fixture, RiotCommandTypeNames.TriggerEmergency));
    }

    /// <summary>
    /// 同上，但新一代故障里门锁已经恢复为锁闭：豁免仍不带过来。上一条用例里门又报没锁，「此刻门锁锁闭」那个条件已经把豁免
    /// 关掉了，代次条件轮不到起作用（变异 M12 在那条上存活）；这一条让两个条件分开。
    /// </summary>
    /// <remarks>
    /// 新一代故障立在站点上（位置已知、停稳），按住即可，不急停；随后门锁恢复、车报不出站点。本代还没自己解除过，
    /// 所以「报不出站点」照常升级为急停——与第一代解除之前一样。借用上一代的解除记录就会漏掉这次急停。
    /// </remarks>
    [Fact]
    [Trait("Requirement", "REQ-0246")]
    public async Task TheExemptionEndsWithTheFaultGenerationEvenOnceTheDoorsAreLockedAgain()
    {
        await using RuntimeFixture fixture = await ReleasedForTheDoorsAsync();
        Latched latched = await LatchedFactsAsync(fixture);
        await ResumeByAPersonAsync(fixture, latched.UpperId);

        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { CurrentStationId = 1 };
        fixture.Riot.MovementState = "MT_PAUSED";
        fixture.UnfinishedOrderIds = [latched.OrderId];
        await fixture.ReportSafetySummaryAsync(
            allTargetSlotsLocked: false, unknownPresent: false, ["LOCK_NOT_CLOSED", "ACTION_NOT_ALLOWED_IN_STATE"]);
        await DriveOneRoundAsync(fixture);
        await using (ControlServerDbContext reading = new(fixture.DbOptionsForTests))
        {
            VehicleFaultStateRow raised = await reading.VehicleFaultStates.AsNoTracking().SingleAsync(Token);
            Assert.Equal((VehicleFaultLevel.SuspectedBlocked, 2L), (raised.Level, raised.FaultGeneration));
        }
        Assert.Equal(1, await CountAsync(fixture, RiotCommandTypeNames.TriggerEmergency));

        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { CurrentStationId = 0 };
        await ReportLockedAsync(fixture);
        await DriveOneRoundAsync(fixture);

        Assert.Equal(2, await CountAsync(fixture, RiotCommandTypeNames.TriggerEmergency));
        Assert.Equal(1, await CountAsync(fixture, RiotCommandTypeNames.CancelEmergency));
    }

    /// <summary>
    /// 急停仍锁着（CAN_RECOVER）时人按「继续原单」：拒绝，RIoT 一条 CONTINUE 都收不到。除了闩锁，别的条件全都满足——单是本代
    /// 自己按住并确认的 7、人确认了原因已排除——所以拒绝只能来自闩锁这一项。
    /// </summary>
    /// <remarks>
    /// 为什么要挡：round-44（rcs/riot-behavior-lab/evidence/rounds/2026-09-28-round-44，BC-ORDER-020，OBSERVED）实测锁住期间
    /// RIoT 接受 CONTINUE_FROM_HELD，单从 7 变成 3，这时只剩急停挡着车。解除那一刻车会不会自己开走没有观测过
    /// （BC-ORDER-020 第 5 条，INFERRED），按最坏情况处理。
    /// <para>
    /// 两格。<c>own</c>：锁着的是本服务端为门锁发的急停，还没解除——这时「本服务端的急停还没关单」
    /// （<c>FAULT_RECOVERY_EMERGENCY_STOP_OPEN</c>）排在闩锁之后也会拒，去掉闩锁检查这一格仍拒、只是理由变了。
    /// <c>external</c>：本服务端的急停已经人解除并确认，之后有人在 RIoT 上按了急停——没有本服务端的急停在开，挡住 CONTINUE
    /// 的只有两处闩锁检查（恢复服务一处、协调器发出前一处），两处都去掉就会真的发出 CONTINUE（变异 M16）。
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("own")]
    [InlineData("external")]
    [Trait("Requirement", "REQ-0239")]
    [Trait("Requirement", "REQ-0167")]
    public async Task AResumeWhileTheLatchIsStillOnSendsNoContinue(string latch)
    {
        await using RuntimeFixture fixture = latch == "own"
            ? await LatchedForTheDoorsAsync()
            : await ReleasedForTheDoorsAsync();
        fixture.EmergencyLatched = true;
        VehicleFaultRecoveryTests.SiteRiot site = new(fixture) { HasUnfinishedOrder = true };

        VehicleFaultRecoveryDecision decision = await VehicleFaultRecoveryTests.Service(fixture, site)
            .RecoverAsync(ResumeRequest(fixture), Token);

        Assert.Equal(VehicleFaultRecoveryOutcome.Refused, decision.Outcome);
        Assert.Equal(["FAULT_RECOVERY_EMERGENCY_LATCHED"], decision.Reasons);
        Assert.Empty(site.OrderCommands);
        Assert.Equal(0, await CountAsync(fixture, RiotCommandTypeNames.OrderContinue));
    }

    /// <summary>
    /// 恢复服务读急停时是 OK，读完之后、CONTINUE 发出之前急停又锁上了（引擎某一轮豁免失效重新急停，或有人在 RIoT 上按了）：
    /// 协调器在发出前最后一刻再读一次急停，拒绝，RIoT 一条 CONTINUE 都收不到，故障仍在效。
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-0239")]
    public async Task ALatchThatComesOnAfterTheServiceReadItStillStopsTheContinue()
    {
        await using RuntimeFixture fixture = await ReleasedForTheDoorsAsync();
        VehicleFaultRecoveryTests.SiteRiot site = new(fixture)
        {
            HasUnfinishedOrder = true,
            AfterUnfinishedOrdersRead = () => fixture.EmergencyLatched = true,
        };

        VehicleFaultRecoveryDecision decision = await VehicleFaultRecoveryTests.Service(fixture, site)
            .RecoverAsync(ResumeRequest(fixture), Token);

        Assert.Equal(VehicleFaultRecoveryOutcome.Refused, decision.Outcome);
        Assert.Equal(["RESUME_EMERGENCY_LATCHED"], decision.Reasons);
        Assert.Empty(site.OrderCommands);
        Assert.Equal(0, await CountAsync(fixture, RiotCommandTypeNames.OrderContinue));
        await using ControlServerDbContext reading = new(fixture.DbOptionsForTests);
        VehicleFaultStateRow fault = await reading.VehicleFaultStates.AsNoTracking().SingleAsync(Token);
        Assert.Equal((VehicleFaultLevel.SuspectedBlocked, 1L), (fault.Level, fault.FaultGeneration));
    }

    /// <summary>
    /// 审查必修 3（探针 P3）：恢复服务过了闸门之后、协调器发 CONTINUE 之前，引擎这一轮因为门又没锁重新急停，而 RIoT 的闩锁
    /// 还没锁上（实车约 1 s）。协调器最后一刻读到急停 OK，但本服务端有一次急停还开着：拒绝，零 CONTINUE。
    /// </summary>
    /// <remarks>
    /// 不挡的话：单变 3、故障被清除，急停随后锁上。门锁恢复之后这次急停没有故障在监看，有货的车只能改库。
    /// </remarks>
    [Fact]
    [Trait("Requirement", "REQ-0239")]
    [Trait("Requirement", "REQ-0248")]
    public async Task AStopReTriggeredAfterTheServiceGateStillStopsTheContinue()
    {
        await using RuntimeFixture fixture = await ReleasedForTheDoorsAsync();
        VehicleFaultRecoveryTests.SiteRiot site = new(fixture)
        {
            HasUnfinishedOrder = true,
            AfterOrderReadAsync = async read =>
            {
                // 第 2 次读单是协调器在 ResumeAsync 里那一次：恢复服务的闸门已经放开。
                if (read == 2)
                {
                    await fixture.ReportSafetySummaryAsync(
                        allTargetSlotsLocked: false, unknownPresent: false, ["LOCK_NOT_CLOSED", "ACTION_NOT_ALLOWED_IN_STATE"]);
                    await DriveOneRoundAsync(fixture);
                }
            },
        };

        VehicleFaultRecoveryDecision decision = await VehicleFaultRecoveryTests.Service(fixture, site)
            .RecoverAsync(ResumeRequest(fixture), Token);

        // 前提：引擎确实在那个时间窗里重新急停了，而闩锁还没锁上。
        Assert.Equal(2, await CountAsync(fixture, RiotCommandTypeNames.TriggerEmergency));
        Assert.False(fixture.EmergencyLatched);
        Assert.Equal(VehicleFaultRecoveryOutcome.Refused, decision.Outcome);
        Assert.Equal(["RESUME_EMERGENCY_STOP_OPEN"], decision.Reasons);
        Assert.Empty(site.OrderCommands);
        Assert.Equal(0, await CountAsync(fixture, RiotCommandTypeNames.OrderContinue));
    }

    /// <summary>
    /// 同上，但协调器最后一刻读不到急停状态（审查第二路变异 B）：读不到不当作 OK，拒绝，零 CONTINUE。与必修 2 那道重读缺口叠在一起，
    /// 就是「单变 3、闩锁状态不明、CONTINUE 照发」的完整危险链。
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-0239")]
    public async Task AnEmergencyStateThatCannotBeReadAfterTheServiceReadItStopsTheContinue()
    {
        await using RuntimeFixture fixture = await ReleasedForTheDoorsAsync();
        VehicleFaultRecoveryTests.SiteRiot site = new(fixture) { HasUnfinishedOrder = true };
        site.AfterUnfinishedOrdersRead = () => site.EmergencyUnreadable = true;

        VehicleFaultRecoveryDecision decision = await VehicleFaultRecoveryTests.Service(fixture, site)
            .RecoverAsync(ResumeRequest(fixture), Token);

        Assert.Equal(VehicleFaultRecoveryOutcome.Refused, decision.Outcome);
        Assert.Equal(["RESUME_EMERGENCY_STATE_UNKNOWN"], decision.Reasons);
        Assert.Empty(site.OrderCommands);
        Assert.Equal(0, await CountAsync(fixture, RiotCommandTypeNames.OrderContinue));
    }

    /// <summary>
    /// 审查必修 1（探针 P1）：门锁故障在效期间，有人在 RIoT 上取消或删除了本服务端按住的那张单。之后不能只剩改库：
    /// 门锁恢复照样不自动解除（CP-0011），人经三类入口解除（锁着那几格）；人工清除对门锁故障放行 CANCELLED／DELETED，
    /// 只清故障，重建交给引擎已经登记的那一条 cs#318 记录，不登记第二条；故障不自动清除。
    /// </summary>
    [Theory]
    [InlineData("latched", RiotOrderState.Cancelled)]
    [InlineData("released", RiotOrderState.Cancelled)]
    [InlineData("latched", RiotOrderState.Deleted)]
    [InlineData("latched-behind-the-readiness-gate", RiotOrderState.Cancelled)]
    [Trait("Requirement", "REQ-0167")]
    [Trait("Requirement", "REQ-0246")]
    public async Task AHeldOrderEndedInRiotUnderADoorFaultStillHasAWayOut(string latch, int ending)
    {
        bool behindTheGate = latch.EndsWith("behind-the-readiness-gate", StringComparison.Ordinal);
        bool wasLatched = latch.StartsWith("latched", StringComparison.Ordinal);
        await using RuntimeFixture fixture = wasLatched
            ? await LatchedForTheDoorsAsync(behindTheGate)
            : await ReleasedForTheDoorsAsync();
        Latched latched = await LatchedFactsAsync(fixture);
        int releasesBefore = await CountAsync(fixture, RiotCommandTypeNames.CancelEmergency);

        fixture.Riot.SetOrderState(latched.UpperId, ending, terminal: true);
        fixture.UnfinishedOrderIds = [];
        await ReportLockedAsync(fixture);
        await DriveOneRoundAsync(fixture);

        // 门锁锁着、车上没有任何未完成单，这一轮也不自动解除。锁着的那几格由人解除；RIoT 随后放开闩锁（假 RIoT 不会自己放）。
        Assert.Equal(releasesBefore, await CountAsync(fixture, RiotCommandTypeNames.CancelEmergency));
        if (behindTheGate)
        {
            // 前提：会话一直未就绪，这一格走的确实是会话未就绪那一侧。
            Assert.NotEqual(SessionReadiness.Ready, (await fixture.Context.SessionRecoveries.AsNoTracking().SingleAsync(Token)).Readiness);
        }
        if (wasLatched)
        {
            await ReleaseByAPersonAsync(fixture, latched);
        }
        fixture.EmergencyLatched = false;
        for (int round = 0; round < 4; round++)
        {
            await DriveOneRoundAsync(fixture);
        }

        // 引擎登记的 cs#318 重建到期之后，正是卡在这个故障上（审查探针 P1 看到的现场）：先让它真的到期，前提才算立住。
        await using (ControlServerDbContext reading = new(fixture.DbOptionsForTests))
        {
            OwnOrderRebuildRow recorded = Assert.Single(await reading.OwnOrderRebuilds.AsNoTracking().ToArrayAsync(Token));
            TimeSpan untilDue = recorded.DueAt - fixture.Clock.GetUtcNow();
            if (untilDue > TimeSpan.Zero)
            {
                fixture.Clock.Advance(untilDue + TimeSpan.FromSeconds(1));
            }
        }
        await ReportLockedAsync(fixture);
        await DriveOneRoundAsync(fixture);

        // 故障仍在效，不自动清除；没有再次急停，也没有再次解除。
        await using (ControlServerDbContext reading = new(fixture.DbOptionsForTests))
        {
            VehicleFaultStateRow standing = await reading.VehicleFaultStates.AsNoTracking().SingleAsync(Token);
            Assert.Equal((VehicleFaultLevel.SuspectedBlocked, InTransitDoorLockFaultTests.DoorSymptom),
                (standing.Level, standing.EvidenceCode));
            OwnOrderRebuildRow waiting = Assert.Single(await reading.OwnOrderRebuilds.AsNoTracking().ToArrayAsync(Token));
            // Behind the readiness gate the rebuild does not create, so it never reaches the vehicle-condition check: it waits on
            // the session instead (NameInFlightOrderWithoutThePeerAsync, mayCreate: false). Either way it has not moved on.
            Assert.Contains(
                behindTheGate ? "ONBOARD_SESSION_NOT_READY" : "VEHICLE_FAULT_IN_EFFECT",
                waiting.WaitingReason ?? string.Empty,
                StringComparison.Ordinal);
        }
        Assert.Equal(releasesBefore + (wasLatched ? 1 : 0), await CountAsync(fixture, RiotCommandTypeNames.CancelEmergency));
        Assert.Equal(1, await CountAsync(fixture, RiotCommandTypeNames.TriggerEmergency));

        VehicleFaultRecoveryTests.SiteRiot site = new(fixture) { HasUnfinishedOrder = false };
        VehicleFaultRecoveryDecision decision = await VehicleFaultRecoveryTests.Service(fixture, site).RecoverAsync(
            new VehicleFaultRecoveryRequest(
                new EmergencyStopSubject(fixture.Options.AgvId, fixture.Options.VehicleKey),
                VehicleFaultRecoveryAction.ClearFault,
                "L1-OPERATOR",
                FaultRemedied: true,
                Note: "doors checked on site"),
            Token);
        fixture.Context.ChangeTracker.Clear();

        Assert.True(VehicleFaultRecoveryOutcome.Cleared == decision.Outcome, string.Join(", ", decision.Reasons));
        // 只清故障，不处置旅程：重建是引擎登记的那一条（见下一条用例为什么要紧）。
        Assert.Equal(VehicleFaultRecoveryDispositions.None, decision.Disposition);
        await DriveOneRoundAsync(fixture);
        await using (ControlServerDbContext reading = new(fixture.DbOptionsForTests))
        {
            VehicleFaultStateRow cleared = await reading.VehicleFaultStates.AsNoTracking().SingleAsync(Token);
            Assert.Equal(VehicleFaultLevel.None, cleared.Level);
            OwnOrderRebuildRow rebuild = Assert.Single(await reading.OwnOrderRebuilds.AsNoTracking().ToArrayAsync(Token));
            Assert.Equal((OwnOrderRebuildSources.CancelledInRiot, latched.UpperId), (rebuild.Source, rebuild.EndedUpperId));
            Assert.DoesNotContain("VEHICLE_FAULT_IN_EFFECT", rebuild.WaitingReason ?? string.Empty, StringComparison.Ordinal);
        }
        Assert.Equal(1, await CountAsync(fixture, RiotCommandTypeNames.TriggerEmergency));
    }

    /// <summary>
    /// 同上，但人按清除时引擎还没看到这次取消（单刚在 RIoT 被取消，引擎下一轮才会读到）。清除只清故障，不替它登记重建：
    /// 重建记录按被终结那张单的单号唯一，谁先登记就是谁的。清除若照 FAILED 那样处置旅程，登记下的是来源「故障清除」、
    /// 单态 FAILED 的记录，引擎随后读到取消也只会拿回这一条——来源和单态都记错了，REQ-0361 的重复窗口按来源判，也跟着错。
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-0246")]
    [Trait("Requirement", "REQ-0361")]
    public async Task AClearanceBeforeTheEngineSeesTheCancellationLeavesTheRebuildToTheEngine()
    {
        await using RuntimeFixture fixture = await ReleasedForTheDoorsAsync();
        Latched latched = await LatchedFactsAsync(fixture);
        fixture.Riot.SetOrderState(latched.UpperId, RiotOrderState.Cancelled, terminal: true);
        fixture.UnfinishedOrderIds = [];

        VehicleFaultRecoveryTests.SiteRiot site = new(fixture) { HasUnfinishedOrder = false };
        VehicleFaultRecoveryDecision decision = await VehicleFaultRecoveryTests.Service(fixture, site).RecoverAsync(
            new VehicleFaultRecoveryRequest(
                new EmergencyStopSubject(fixture.Options.AgvId, fixture.Options.VehicleKey),
                VehicleFaultRecoveryAction.ClearFault,
                "L1-OPERATOR",
                FaultRemedied: true,
                Note: "doors checked on site"),
            Token);
        fixture.Context.ChangeTracker.Clear();
        Assert.True(VehicleFaultRecoveryOutcome.Cleared == decision.Outcome, string.Join(", ", decision.Reasons));
        Assert.Equal(VehicleFaultRecoveryDispositions.None, decision.Disposition);

        await DriveOneRoundAsync(fixture);

        await using ControlServerDbContext reading = new(fixture.DbOptionsForTests);
        OwnOrderRebuildRow rebuild = Assert.Single(await reading.OwnOrderRebuilds.AsNoTracking().ToArrayAsync(Token));
        Assert.Equal(
            (OwnOrderRebuildSources.CancelledInRiot, latched.UpperId, (int?)RiotOrderState.Cancelled),
            (rebuild.Source, rebuild.EndedUpperId, rebuild.EndedOrderState));
    }

    /// <summary>
    /// 解除之后 RIoT 把单跑了起来（3），而没有人按继续：写告警与旅程码 <c>HELD_ORDER_RESUMED_WITHOUT_CONTINUE</c>。
    /// round-44（agv03，2026-09-28）单次观测到 HELD 的单在解除后 60 秒里一直是 7；这一格防的是 RIoT 行为以后变了。
    /// 车若真在动，前一格「moving」那条已经证明它会被重新急停；这里让车停着，只看码。
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-0167")]
    public async Task AHeldOrderThatRunsAgainWithoutAContinueIsNamed()
    {
        await using RuntimeFixture fixture = await ReleasedForTheDoorsAsync();
        Latched latched = await LatchedFactsAsync(fixture);
        fixture.Riot.SetOrderState(latched.UpperId, RiotOrderState.Executing, terminal: false);

        await DriveOneRoundAsync(fixture);

        Assert.Equal("HELD_ORDER_RESUMED_WITHOUT_CONTINUE", (await fixture.RuntimeAsync()).BlockReasonCode);
        Assert.Equal(0, await CountAsync(fixture, RiotCommandTypeNames.OrderContinue));
    }

    /// <summary>
    /// 门锁读回锁闭，不解除任何东西（CP-0011）：不论车上那张单是本代自己按住并确认的 HELD、被人在 RIoT 跑了起来、
    /// 还是已经被取消，也不论车上有没有别的未完成单。几轮下来一条 cancelEmergency 都没有，故障仍在效。
    /// </summary>
    /// <remarks>
    /// 修前「own-held」与「order-ended」这两格每一格都在门锁恢复的那一轮自动解除。
    /// </remarks>
    [Theory]
    [InlineData("own-held")]
    [InlineData("order-ended")]
    [InlineData("order-running")]
    [InlineData("foreign-order")]
    [Trait("Requirement", "REQ-0167")]
    public async Task DoorsReadLockedAgainReleaseNothingOnTheirOwn(string orders)
    {
        await using RuntimeFixture fixture = await LatchedForTheDoorsAsync();
        Latched latched = await LatchedFactsAsync(fixture);
        switch (orders)
        {
            case "order-ended":
                fixture.Riot.SetOrderState(latched.UpperId, RiotOrderState.Cancelled, terminal: true);
                fixture.UnfinishedOrderIds = [];
                break;
            case "order-running":
                fixture.Riot.SetOrderState(latched.UpperId, RiotOrderState.Executing, terminal: false);
                break;
            case "foreign-order":
                fixture.UnfinishedOrderIds = [latched.OrderId, "FOREIGN-ORDER-0001"];
                break;
        }

        await ReportLockedAsync(fixture);
        await AssertNoReleaseOverRoundsAsync(fixture);

        Assert.True(fixture.EmergencyLatched);
        await using ControlServerDbContext reading = new(fixture.DbOptionsForTests);
        VehicleFaultStateRow fault = await reading.VehicleFaultStates.AsNoTracking().SingleAsync(Token);
        Assert.Equal((VehicleFaultLevel.SuspectedBlocked, InTransitDoorLockFaultTests.DoorSymptom), (fault.Level, fault.EvidenceCode));
    }

    /// <summary>
    /// 订单 FAILED 不急停（REQ-0246 按 CP-0011 修订）：关卡段行驶中单被 RIoT 报 FAILED，车读到在动、报不出站点——修前这一格同一轮就急停。
    /// 现在故障照记、派车照挡，一条 triggerEmergency 都不发。
    /// </summary>
    /// <remarks>用户 2026-10-13：「这个不用下急停，FAILED的话，订单也不会执行的」。</remarks>
    [Fact]
    [Trait("Requirement", "REQ-0246")]
    [Trait("Requirement", "REQ-0232")]
    public async Task AFailedOrderNeverStopsTheVehicle()
    {
        await using RuntimeFixture fixture = await GateArrivalWaitAsync();
        JourneyRuntimeRow underWay = await fixture.RuntimeAsync();
        fixture.Riot.MovementState = "MT_RUNNING";
        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { CurrentStationId = 0 };
        fixture.Riot.FailOrder(underWay.GateUpperId!);
        for (int round = 0; round < 5; round++)
        {
            await DriveOneRoundAsync(fixture);
            Assert.Equal(0, await CountAsync(fixture, RiotCommandTypeNames.TriggerEmergency));
        }

        await using ControlServerDbContext reading = new(fixture.DbOptionsForTests);
        VehicleFaultStateRow fault = await reading.VehicleFaultStates.AsNoTracking().SingleAsync(Token);
        Assert.NotEqual(VehicleFaultLevel.None, fault.Level);
        Assert.Null(fault.EscalatedAt);
    }

    /// <summary>
    /// CP-0011 判定表第 4 行（审查 H-1，探针 P1 的序列）：车在路上门报没锁时，有人按着急停按钮（<c>CAN_NOT_RECOVER</c>）。
    /// 服务端在那个闩锁上只记本服务端的一类、不发调用。门锁修好读回锁闭、按钮松开（<c>CAN_RECOVER</c>）、有人用二类入口解开（<c>OK</c>）：
    /// 本服务端这一类还在，立即重下一次。之后人经三类入口解除（这次急停记着门锁原因，所以要确认门锁已检修），人按续行，故障清除。
    /// </summary>
    /// <remarks>
    /// 修前这条路卡死：按钮按着时故障的证据码变成 <c>RIOT_EMERGENCY_CAN_NOT_RECOVER</c>，门锁读回锁闭后门锁那一路认不出这是门锁故障、
    /// 不再调故障模型；急停监看只挂在那一路上，于是二类入口解开之后没有重下，开着的急停又挡住续行和清除
    /// （<c>FAULT_RECOVERY_EMERGENCY_STOP_OPEN</c>），三类入口要 <c>CAN_RECOVER</c> 而闩锁已是 <c>OK</c>。现在引擎每一轮对有未结束急停的车都驱动监看。
    /// </remarks>
    [Fact]
    [Trait("Requirement", "REQ-0248")]
    [Trait("Requirement", "REQ-0356")]
    [Trait("Requirement", "REQ-0239")]
    public async Task AStopRecordedUnderAHeldButtonIsReTriggeredWhenAClassTwoEntryClearsTheLatchAndAPersonCanThenRelease()
    {
        await using RuntimeFixture fixture = await GateArrivalWaitAsync();
        string upperId = (await fixture.RuntimeAsync()).GateUpperId!;
        fixture.Riot.MovementState = "MT_RUNNING";
        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { CurrentStationId = 0 };
        fixture.ButtonHeld = true;
        await fixture.ReportSafetySummaryAsync(
            allTargetSlotsLocked: false, unknownPresent: false, ["LOCK_NOT_CLOSED", "ACTION_NOT_ALLOWED_IN_STATE"]);
        await DriveOneRoundAsync(fixture);

        // 只记不发；故障证据码被按钮盖成 CAN_NOT_RECOVER，这次急停自己记着门锁原因。
        Assert.Equal(0, await CountAsync(fixture, RiotCommandTypeNames.TriggerEmergency));
        Assert.Equal(1, await CountAsync(fixture, EmergencyStopSupervisor.LatchAdoptedCommandType));
        await using (ControlServerDbContext reading = new(fixture.DbOptionsForTests))
        {
            VehicleFaultStateRow fault = await reading.VehicleFaultStates.AsNoTracking().SingleAsync(Token);
            Assert.Equal(VehicleFaultEvidence.EmergencyCanNotRecover, fault.EvidenceCode);
            Assert.Contains(
                VehicleFaultEvidence.DoorNotProvenLocked,
                await fixture.CreateEmergencyStopSupervisor(reading).OpenEpisodeCausesAsync(
                    new EmergencyStopSubject(fixture.Options.AgvId, fixture.Options.VehicleKey), Token));
        }

        // RIoT 停住单；门锁修好、读回锁闭；按钮松开。
        string orderId = await OrderIdAsync(fixture, upperId);
        fixture.Riot.SetOrderState(upperId, RiotOrderState.Paused, terminal: false);
        fixture.Riot.MovementState = "MT_PAUSED";
        fixture.UnfinishedOrderIds = [orderId];
        await ReportLockedAsync(fixture);
        await DriveOneRoundAsync(fixture);
        fixture.ButtonHeld = false;
        fixture.EmergencyLatched = true;
        await DriveOneRoundAsync(fixture);
        Assert.Equal(0, await CountAsync(fixture, RiotCommandTypeNames.TriggerEmergency));

        // 有人用二类入口解开：本服务端这一类还在，下一轮重下一次。
        fixture.EmergencyLatched = false;
        await DriveOneRoundAsync(fixture);
        Assert.Equal(1, await CountAsync(fixture, RiotCommandTypeNames.TriggerEmergency));
        fixture.EmergencyLatched = true;
        await AssertNoReleaseOverRoundsAsync(fixture);
        Assert.Equal(1, await CountAsync(fixture, RiotCommandTypeNames.TriggerEmergency));

        // 人经三类入口解除（确认门锁已检修），之后不再重下；人按续行，故障清除。
        await fixture.ReleaseByAPersonAsync();
        fixture.EmergencyLatched = false;
        for (int round = 0; round < 5; round++)
        {
            await DriveOneRoundAsync(fixture);
        }
        Assert.Equal(1, await CountAsync(fixture, RiotCommandTypeNames.TriggerEmergency));
        Assert.Equal(1, await CountAsync(fixture, RiotCommandTypeNames.CancelEmergency));

        await ResumeByAPersonAsync(fixture, upperId);
        await using (ControlServerDbContext reading = new(fixture.DbOptionsForTests))
        {
            Assert.Equal(VehicleFaultLevel.None, (await reading.VehicleFaultStates.AsNoTracking().SingleAsync(Token)).Level);
        }
    }

    /// <summary>
    /// 审查 H-1 的同根问题：开着的急停不靠「这一轮看到症状」来监看。门锁急停锁住之后那张单在 RIoT 被报 SUCCESS（车到站了），
    /// 门锁那一路不再走；有人在 RIoT 上解开，下一轮照样重下。
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-0248")]
    public async Task AnOpenStopIsWatchedEvenWhenNoSymptomPathRunsThisRound()
    {
        await using RuntimeFixture fixture = await LatchedForTheDoorsAsync();
        Latched latched = await LatchedFactsAsync(fixture);
        fixture.Riot.SetOrderState(latched.UpperId, RiotOrderState.Success, terminal: true);
        fixture.UnfinishedOrderIds = [];
        await DriveOneRoundAsync(fixture);

        fixture.EmergencyLatched = false;
        await DriveOneRoundAsync(fixture);

        Assert.Equal(2, await CountAsync(fixture, RiotCommandTypeNames.TriggerEmergency));
        Assert.Equal(0, await CountAsync(fixture, RiotCommandTypeNames.CancelEmergency));
    }

    /// <summary>
    /// control-server#835（cs#791 审查 M1）：人按「继续原单」，CONTINUE 发出、RIoT 接受，读回预算内单仍是 HELD（入口答「等待确认」）；
    /// 随后 RIoT 执行，单 3、车在两站之间开起来。这是本服务端自己的续行让车动了：之后几轮不急停、不写
    /// <c>HELD_ORDER_RESUMED_WITHOUT_CONTINUE</c>；人再按一次，核的是同一条续行（不发第二条），确认了就清故障。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 三格。<c>plain</c>；<c>behind-the-readiness-gate</c>：会话因本服务端自己的在途单一直未就绪（真车载端行驶全程如此），
    /// 每轮走 <c>NameInFlightOrderWithoutThePeerAsync</c>；<c>restarted</c>：续行未定时进程重启（新引擎、新运动窗口），
    /// 续行状态只从持久化的命令审计读，重启后照样认得。
    /// </para>
    /// <para>
    /// 修前第一轮就急停（车在动、故障仍在效，<c>STOP_PROOF_MOTION_OBSERVED</c>），并把旅程码换成
    /// <c>HELD_ORDER_RESUMED_WITHOUT_CONTINUE</c>（<c>JourneyRuntimeEngine.InTransitDoors.cs:214-218</c>）。
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("plain")]
    [InlineData("behind-the-readiness-gate")]
    [InlineData("restarted")]
    [Trait("Requirement", "REQ-0239")]
    [Trait("Requirement", "REQ-0246")]
    public async Task AnOwnContinueReadHeldFirstAndRunningLaterIsNeitherStoppedNorNamed(string path)
    {
        await using RuntimeFixture fixture = await ReleasedForTheDoorsAsync(
            behindTheReadinessGate: path == "behind-the-readiness-gate");
        Latched latched = await LatchedFactsAsync(fixture);
        VehicleFaultRecoveryTests.SiteRiot site = new(fixture) { HasUnfinishedOrder = true };

        VehicleFaultRecoveryDecision first = await VehicleFaultRecoveryTests.Service(fixture, site)
            .RecoverAsync(ResumeRequest(fixture), Token);
        fixture.Context.ChangeTracker.Clear();
        Assert.Equal(["RESUME_CONTINUE_AWAITING_CONFIRMATION"], first.Reasons);
        Assert.Equal([RiotOrderCommandKind.ContinueFromHeld], site.OrderCommands);

        if (path == "restarted")
        {
            await fixture.RecreateEngineAsync();
        }

        fixture.Riot.SetOrderState(latched.UpperId, RiotOrderState.Executing, terminal: false);
        fixture.Riot.MovementState = "MT_RUNNING";
        for (int round = 0; round < 3; round++)
        {
            await DriveOneRoundAsync(fixture);
            Assert.Equal(1, await CountAsync(fixture, RiotCommandTypeNames.TriggerEmergency));
            Assert.NotEqual(
                JourneyRuntimeEngine.HeldOrderResumedWithoutContinueReason, (await fixture.RuntimeAsync()).BlockReasonCode);
        }

        if (path == "behind-the-readiness-gate")
        {
            Assert.NotEqual(SessionReadiness.Ready, (await fixture.Context.SessionRecoveries.AsNoTracking().SingleAsync(Token)).Readiness);
        }

        VehicleFaultRecoveryTests.SiteRiot again = new(fixture) { HasUnfinishedOrder = true };
        VehicleFaultRecoveryDecision resumed = await VehicleFaultRecoveryTests.Service(fixture, again)
            .RecoverAsync(ResumeRequest(fixture), Token);
        fixture.Context.ChangeTracker.Clear();

        Assert.Equal((VehicleFaultRecoveryOutcome.Resumed, 0), (resumed.Outcome, resumed.Reasons.Count));
        Assert.Empty(again.OrderCommands);
        Assert.Equal(1, await CountAsync(fixture, RiotCommandTypeNames.OrderContinue));
        Assert.Equal(1, await CountAsync(fixture, RiotCommandTypeNames.TriggerEmergency));
        await using ControlServerDbContext reading = new(fixture.DbOptionsForTests);
        Assert.Equal(VehicleFaultLevel.None, (await reading.VehicleFaultStates.AsNoTracking().SingleAsync(Token)).Level);
    }

    /// <summary>
    /// control-server#835 第二处复现（cs#797 的 L2 run 39640211525）：CONTINUE 发出之后、读回确认与清故障之前，引擎跑了一轮，
    /// 这时 RIoT 已把单置 3、车 <c>MT_RUNNING</c>。车是本服务端的续行放走的：这一轮不急停，续行确认、故障清除，不回
    /// <c>RESUME_EMERGENCY_STOP_OPEN</c>。
    /// </summary>
    /// <remarks>
    /// 修前这一轮以 <c>STOP_PROOF_MOTION_OBSERVED</c> 急停，续行随后被 cs#791 审查 H1 的保护拒掉（409），人得再解除、再按。
    /// <c>behind-the-readiness-gate</c> 一格是会话因本服务端自己的在途单未就绪的同一时序。
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Requirement", "REQ-0239")]
    [Trait("Requirement", "REQ-0246")]
    public async Task AnEngineRoundInsideTheContinuesReadBackDoesNotStopTheVehicleItResumed(bool behindTheReadinessGate)
    {
        await using RuntimeFixture fixture = await ReleasedForTheDoorsAsync(behindTheReadinessGate);
        Latched latched = await LatchedFactsAsync(fixture);
        bool continued = false;
        bool roundDriven = false;
        VehicleFaultRecoveryTests.SiteRiot site = new(fixture) { HasUnfinishedOrder = true };
        site.OnOrderCommand = (kind, _) => continued |= kind == RiotOrderCommandKind.ContinueFromHeld;
        site.AfterOrderReadAsync = async _ =>
        {
            // 续行之后第一次读（命令服务里那一次）仍是 HELD；随后 RIoT 执行：单 3、车动，下一次重读之前引擎跑一轮。
            if (continued && !roundDriven)
            {
                roundDriven = true;
                fixture.Riot.SetOrderState(latched.UpperId, RiotOrderState.Executing, terminal: false);
                fixture.Riot.MovementState = "MT_RUNNING";
                await DriveOneRoundAsync(fixture);
            }
        };

        VehicleFaultRecoveryDecision decision = await VehicleFaultRecoveryTests.Service(fixture, site)
            .RecoverAsync(ResumeRequest(fixture), Token);
        fixture.Context.ChangeTracker.Clear();

        Assert.True(roundDriven, "no engine round ran inside the read-back window");
        if (behindTheReadinessGate)
        {
            Assert.NotEqual(SessionReadiness.Ready, (await fixture.Context.SessionRecoveries.AsNoTracking().SingleAsync(Token)).Readiness);
        }
        Assert.Equal(1, await CountAsync(fixture, RiotCommandTypeNames.TriggerEmergency));
        Assert.True(VehicleFaultRecoveryOutcome.Resumed == decision.Outcome, string.Join(", ", decision.Reasons));
        Assert.Equal([RiotOrderCommandKind.ContinueFromHeld], site.OrderCommands);
        await using ControlServerDbContext reading = new(fixture.DbOptionsForTests);
        Assert.Equal(VehicleFaultLevel.None, (await reading.VehicleFaultStates.AsNoTracking().SingleAsync(Token)).Level);
    }

    /// <summary>
    /// control-server#835 的安全边：续行被 RIoT 拒了（读回 Failed，入口答 <c>RESUME_CONTINUE_NOT_CONFIRMED</c>），之后单却在 RIoT
    /// 上跑了起来、车在动——这不是本服务端放走的。照旧急停，并写 <c>HELD_ORDER_RESUMED_WITHOUT_CONTINUE</c>。
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-0246")]
    public async Task AVehicleThatMovesAfterItsContinueWasRefusedIsStillStoppedAndNamed()
    {
        await using RuntimeFixture fixture = await ReleasedForTheDoorsAsync();
        Latched latched = await LatchedFactsAsync(fixture);
        VehicleFaultRecoveryTests.SiteRiot site = new(fixture)
        {
            HasUnfinishedOrder = true,
            OrderCommandDisposition = RiotCommandCallDisposition.Failed,
        };

        VehicleFaultRecoveryDecision refused = await VehicleFaultRecoveryTests.Service(fixture, site)
            .RecoverAsync(ResumeRequest(fixture), Token);
        fixture.Context.ChangeTracker.Clear();
        Assert.Equal(["RESUME_CONTINUE_NOT_CONFIRMED"], refused.Reasons);
        Assert.Equal([RiotOrderCommandKind.ContinueFromHeld], site.OrderCommands);

        fixture.Riot.SetOrderState(latched.UpperId, RiotOrderState.Executing, terminal: false);
        fixture.Riot.MovementState = "MT_RUNNING";
        await DriveOneRoundAsync(fixture);

        Assert.Equal(2, await CountAsync(fixture, RiotCommandTypeNames.TriggerEmergency));
        Assert.Equal(JourneyRuntimeEngine.HeldOrderResumedWithoutContinueReason, (await fixture.RuntimeAsync()).BlockReasonCode);
    }

    /// <summary>
    /// control-server#835 的安全边：本服务端的续行还未定（入口答等待确认），RIoT 仍报那张单 HELD，车却在动。续行没让单跑起来，
    /// 车的运动没有本服务端的解释：照旧急停。
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-0246")]
    public async Task AVehicleThatMovesWhileItsOwnContinueIsOpenAndTheOrderStillReadsHeldIsStopped()
    {
        await using RuntimeFixture fixture = await ReleasedForTheDoorsAsync();
        VehicleFaultRecoveryTests.SiteRiot site = new(fixture) { HasUnfinishedOrder = true };

        VehicleFaultRecoveryDecision first = await VehicleFaultRecoveryTests.Service(fixture, site)
            .RecoverAsync(ResumeRequest(fixture), Token);
        fixture.Context.ChangeTracker.Clear();
        Assert.Equal(["RESUME_CONTINUE_AWAITING_CONFIRMATION"], first.Reasons);

        fixture.Riot.MovementState = "MT_RUNNING";
        await DriveOneRoundAsync(fixture);

        Assert.Equal(2, await CountAsync(fixture, RiotCommandTypeNames.TriggerEmergency));
    }

    /// <summary>
    /// control-server#835 的安全边：续行未定，RIoT 在同一 upperId 下报的却是另一张单（orderId 不同）在执行、车在动。那不是本服务端
    /// 续的那张单：照旧急停。
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-0246")]
    public async Task AnotherOrderRunningUnderTheSameUpperIdIsNotTheOwnContinue()
    {
        await using RuntimeFixture fixture = await ReleasedForTheDoorsAsync();
        Latched latched = await LatchedFactsAsync(fixture);
        VehicleFaultRecoveryTests.SiteRiot site = new(fixture) { HasUnfinishedOrder = true };
        VehicleFaultRecoveryDecision first = await VehicleFaultRecoveryTests.Service(fixture, site)
            .RecoverAsync(ResumeRequest(fixture), Token);
        fixture.Context.ChangeTracker.Clear();
        Assert.Equal(["RESUME_CONTINUE_AWAITING_CONFIRMATION"], first.Reasons);

        fixture.Riot.ReplaceOrderId(latched.UpperId, "ORDER-NOT-THIS-SERVERS");
        fixture.Riot.SetOrderState(latched.UpperId, RiotOrderState.Executing, terminal: false);
        fixture.Riot.MovementState = "MT_RUNNING";
        await DriveOneRoundAsync(fixture);

        Assert.Equal(2, await CountAsync(fixture, RiotCommandTypeNames.TriggerEmergency));
    }

    /// <summary>
    /// control-server#835 的安全边：续行只认这一代故障的。上一代人按继续、续行确认、故障清除；同一张单在路上又报门没锁，进第二代，
    /// 急停、人解除。之后单在 RIoT 上被人跑了起来、车在动，而这一代本服务端没发续行：照旧急停，照旧写
    /// <c>HELD_ORDER_RESUMED_WITHOUT_CONTINUE</c>。借上一代的续行就会放过它。
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-0246")]
    public async Task AContinueOfAnEarlierFaultGenerationIsNotThisOnesOwnContinue()
    {
        await using RuntimeFixture fixture = await ReleasedForTheDoorsAsync();
        Latched latched = await LatchedFactsAsync(fixture);
        await ResumeByAPersonAsync(fixture, latched.UpperId);

        // 第二代：车照原单在两站之间开着，门又报没锁 → 按住、急停；RIoT 停住单、闩锁锁上。
        fixture.Riot.MovementState = "MT_RUNNING";
        fixture.UnfinishedOrderIds = [latched.OrderId];
        await fixture.ReportSafetySummaryAsync(
            allTargetSlotsLocked: false, unknownPresent: false, ["LOCK_NOT_CLOSED", "ACTION_NOT_ALLOWED_IN_STATE"]);
        await DriveOneRoundAsync(fixture);
        Assert.Equal(2, await CountAsync(fixture, RiotCommandTypeNames.TriggerEmergency));
        fixture.Riot.SetOrderState(latched.UpperId, RiotOrderState.Paused, terminal: false);
        fixture.Riot.MovementState = "MT_PAUSED";
        fixture.EmergencyLatched = true;
        await DriveOneRoundAsync(fixture);

        // 门锁修好，人解除，闩锁放开。
        await ReportLockedAsync(fixture);
        await DriveOneRoundAsync(fixture);
        await ReleaseByAPersonAsync(fixture, latched);
        fixture.EmergencyLatched = false;
        await DriveOneRoundAsync(fixture);
        await DriveOneRoundAsync(fixture);
        await using (ControlServerDbContext reading = new(fixture.DbOptionsForTests))
        {
            VehicleFaultStateRow fault = await reading.VehicleFaultStates.AsNoTracking().SingleAsync(Token);
            Assert.Equal((VehicleFaultLevel.SuspectedBlocked, 2L), (fault.Level, fault.FaultGeneration));
        }
        Assert.Equal(2, await CountAsync(fixture, RiotCommandTypeNames.TriggerEmergency));
        Assert.Equal(1, await CountAsync(fixture, RiotCommandTypeNames.OrderContinue));

        fixture.Riot.SetOrderState(latched.UpperId, RiotOrderState.Executing, terminal: false);
        fixture.Riot.MovementState = "MT_RUNNING";
        await DriveOneRoundAsync(fixture);

        Assert.Equal(3, await CountAsync(fixture, RiotCommandTypeNames.TriggerEmergency));
        Assert.Equal(JourneyRuntimeEngine.HeldOrderResumedWithoutContinueReason, (await fixture.RuntimeAsync()).BlockReasonCode);
    }

    // ---- helpers -------------------------------------------------------------------------------------------------

    private sealed record Latched(string UpperId, string OrderId);

    /// <summary>
    /// 关卡段有货行驶中车载端报门没锁：同一轮按住、急停。随后 RIoT 把单停成 PAUSED(7)、闩锁锁上、车停下（MT_PAUSED），
    /// 车上唯一的未完成单就是这张；下一轮确认触发。前置条件都在这里断：之后的红只能来自解除这一段。
    /// </summary>
    internal static async Task<RuntimeFixture> LatchedForTheDoorsAsync(bool behindTheReadinessGate = false)
    {
        RuntimeFixture fixture = await GateArrivalWaitAsync();
        if (behindTheReadinessGate)
        {
            // A real onboard is not ready for the whole of a leg on this server's order (RecoveryRequired /
            // DEPARTURE_SAFETY_NOT_READY): every round then goes through NameInFlightOrderWithoutThePeerAsync, not the stage
            // body (control-server#335 incremental review, P1g).
            await OwnOrderRebuildTests.DropSessionOnOwnOrderAsync(fixture);
        }
        JourneyRuntimeRow underWay = await fixture.RuntimeAsync();
        string upperId = underWay.GateUpperId!;
        fixture.Riot.MovementState = "MT_RUNNING";
        // Between stations, as a driving vehicle is: RIoT reports station 0 there (ADR-cross-0060), so no sample pins it to a
        // place. Left at the fixture's station 1, every sample reads as a known position and the rule that a release must not be
        // undone merely for want of one is never exercised -- mutation M8 survived exactly that way on the first run.
        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { CurrentStationId = 0 };
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

    /// <summary>
    /// <see cref="LatchedForTheDoorsAsync"/> 之后门锁恢复、人经三类入口解除并确认，再跑一轮：单仍 PAUSED、急停一次、解除一次，
    /// 车停在两站之间（MT_PAUSED）。前置都在这里断，之后的红只能来自解除之后那一段。
    /// </summary>
    private static async Task<RuntimeFixture> ReleasedForTheDoorsAsync(bool behindTheReadinessGate = false)
    {
        RuntimeFixture fixture = await LatchedForTheDoorsAsync(behindTheReadinessGate);
        await ReportLockedAsync(fixture);
        await DriveOneRoundAsync(fixture);
        await ReleaseByAPersonAsync(fixture, await LatchedFactsAsync(fixture));
        fixture.EmergencyLatched = false;
        await DriveOneRoundAsync(fixture);
        await DriveOneRoundAsync(fixture);

        await using ControlServerDbContext reading = new(fixture.DbOptionsForTests);
        RiotOrderCommandAuditRow release = Assert.Single(await reading.RiotOrderCommandAudit.AsNoTracking()
            .Where(row => row.CommandType == RiotCommandTypeNames.CancelEmergency).ToArrayAsync(Token));
        Assert.Equal(RiotOrderCommandOutcome.Confirmed, release.Outcome);
        Assert.Equal(1, await CountAsync(fixture, RiotCommandTypeNames.TriggerEmergency));
        Assert.Equal("MT_PAUSED", fixture.Riot.MovementState);
        return fixture;
    }

    /// <summary>人在三类入口办有货解除（<see cref="RuntimeFixture.ReleaseByAPersonAsync"/>）；本服务端按住的那张单 <paramref name="latched"/> 留着。</summary>
    private static async Task ReleaseByAPersonAsync(RuntimeFixture fixture, Latched latched)
    {
        Assert.Contains(latched.OrderId, await OwnOrderIdsAsync(fixture));
        await fixture.ReleaseByAPersonAsync();
    }

    private static async Task<string[]> OwnOrderIdsAsync(RuntimeFixture fixture)
    {
        await using ControlServerDbContext reading = new(fixture.DbOptionsForTests);
        return await reading.OrderIntents.AsNoTracking()
            .Where(row => row.OrderId != null).Select(row => row.OrderId!).ToArrayAsync(Token);
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
                Note: "doors checked on site"),
            Token);
        Assert.Equal(VehicleFaultRecoveryOutcome.Resumed, resumed.Outcome);
        fixture.Context.ChangeTracker.Clear();
    }

    private static VehicleFaultRecoveryRequest ResumeRequest(RuntimeFixture fixture) =>
        new(
            new EmergencyStopSubject(fixture.Options.AgvId, fixture.Options.VehicleKey),
            VehicleFaultRecoveryAction.ResumeHeldOrder,
            "L1-OPERATOR",
            FaultRemedied: true,
            Note: "doors checked on site");

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

    /// <summary>
    /// 续行发出后的重读窗口里引擎又急停了这辆车（control-server#791 独立审查 H1）：续行读回在执行，故障也<b>不</b>清，
    /// 答 <c>RESUME_EMERGENCY_STOP_OPEN</c>。之后照原路出得去：RIoT 上暂停那张单、有货解除、再续行，单回到执行。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 窗口期间运行锁已经放开，门锁故障在效时引擎每轮都把车交给故障协调器；人解除急停之后，这一代故障里车一动就升级急停。
    /// 续行让车动了，下一轮引擎就重新急停。修前重读读到 3 就清了故障：故障 None、本服务端急停仍开着，之后有货解除答
    /// <c>EMERGENCY_HELD_ORDER_NOT_PAUSED</c>、续行与清除都答已清，RIoT 上暂停后续行也是已清、0 次续行，单停在 7——卡死。
    /// </para>
    /// <para>
    /// control-server#835 之后，本服务端自己的续行让车动不再急停（见
    /// <see cref="AnEngineRoundInsideTheContinuesReadBackDoesNotStopTheVehicleItResumed"/>）。所以这一轮里门锁又报没锁：
    /// 急停有真原因（REQ-0246 的触发本身），H1 那道保护照旧要守住它。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AStopTheEngineReissuesWhileTheContinueIsReadBackKeepsTheFaultAndLeavesAWayOut()
    {
        await using RuntimeFixture fixture = await ReleasedForTheDoorsAsync();
        Latched latched = await LatchedFactsAsync(fixture);
        bool continued = false;
        bool roundDriven = false;
        VehicleFaultRecoveryTests.SiteRiot site = new(fixture) { HasUnfinishedOrder = true };
        site.OnOrderCommand = (kind, _) => continued |= kind == RiotOrderCommandKind.ContinueFromHeld;
        site.AfterOrderReadAsync = async _ =>
        {
            // 续行之后第一次读（命令服务里那一次）仍是 HELD。随后 RIoT 执行：单 3、车动；下一次重读之前引擎跑一轮，闩锁随之锁上。
            if (continued && !roundDriven)
            {
                roundDriven = true;
                fixture.Riot.SetOrderState(latched.UpperId, RiotOrderState.Executing, terminal: false);
                fixture.Riot.MovementState = "MT_RUNNING";
                await fixture.ReportSafetySummaryAsync(
                    allTargetSlotsLocked: false, unknownPresent: false, ["LOCK_NOT_CLOSED", "ACTION_NOT_ALLOWED_IN_STATE"]);
                await DriveOneRoundAsync(fixture);
                fixture.EmergencyLatched = true;
            }
        };

        VehicleFaultRecoveryDecision decision = await VehicleFaultRecoveryTests.Service(fixture, site)
            .RecoverAsync(ResumeRequest(fixture), Token);
        fixture.Context.ChangeTracker.Clear();

        Assert.True(roundDriven, "no engine round ran inside the read-back window");
        Assert.Equal(VehicleFaultRecoveryOutcome.Refused, decision.Outcome);
        Assert.Equal(["RESUME_EMERGENCY_STOP_OPEN"], decision.Reasons);
        await using (ControlServerDbContext reading = new(fixture.DbOptionsForTests))
        {
            VehicleFaultStateRow fault = await reading.VehicleFaultStates.AsNoTracking().SingleAsync(Token);
            Assert.Equal((VehicleFaultLevel.SuspectedBlocked, (DateTimeOffset?)null), (fault.Level, fault.ClearedAt));
        }

        Assert.Equal(2, await CountAsync(fixture, RiotCommandTypeNames.TriggerEmergency));

        // 出路：门锁修好，值班工程师在 RIoT 上暂停那张单，人做有货解除，闩锁放开，再续行。
        await ReportLockedAsync(fixture);
        fixture.Riot.SetOrderState(latched.UpperId, RiotOrderState.Paused, terminal: false);
        fixture.Riot.MovementState = "MT_PAUSED";
        await using (ControlServerDbContext context = new(fixture.DbOptionsForTests))
        {
            HashSet<string> own = (await context.OrderIntents.AsNoTracking()
                    .Where(row => row.OrderId != null).Select(row => row.OrderId!).ToListAsync(Token))
                .ToHashSet(StringComparer.Ordinal);
            await fixture.CreateEmergencyStopSupervisor(context).ReleaseLoadedOnGovernedConfirmationAsync(
                new LoadedEmergencyStopReleaseConfirmation(
                    new EmergencyStopSubject(fixture.Options.AgvId, fixture.Options.VehicleKey),
                    "L1-ADMIN", "cs791 H1", CauseCleared: true, AllDoorsClosed: true, DoorLockRepaired: true),
                doorCause: true, own, Token);
        }

        fixture.Context.ChangeTracker.Clear();
        fixture.EmergencyLatched = false;
        await DriveOneRoundAsync(fixture);
        await DriveOneRoundAsync(fixture);
        VehicleFaultRecoveryTests.SiteRiot again = new(fixture)
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

        VehicleFaultRecoveryDecision resumed = await VehicleFaultRecoveryTests.Service(fixture, again)
            .RecoverAsync(ResumeRequest(fixture), Token);

        Assert.Equal((VehicleFaultRecoveryOutcome.Resumed, 0), (resumed.Outcome, resumed.Reasons.Count));
        Assert.Equal([RiotOrderCommandKind.ContinueFromHeld], again.OrderCommands);
        Assert.Equal(RiotOrderState.Executing, fixture.Riot.OrderStateOf(latched.UpperId));
    }
}
