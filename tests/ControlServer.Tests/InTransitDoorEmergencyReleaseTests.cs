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
        // 故障只在人按继续、CONTINUE 确认之后才清除：它一直在效到这一刻，上面的循环里已断过。
        await using (ControlServerDbContext reading = new(fixture.DbOptionsForTests))
        {
            VehicleFaultStateRow cleared = await reading.VehicleFaultStates.AsNoTracking().SingleAsync(Token);
            Assert.Equal(VehicleFaultLevel.None, cleared.Level);
            Assert.NotNull(cleared.ClearedAt);
        }
    }

    /// <summary>
    /// 门锁自动解除之后，豁免只免「报不出站点」这一项（调度 2026-09-28）。下面几种照旧重新急停，下一轮就发：
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
    /// 豁免只属于那一代故障：人按继续、故障清除之后，车又在两站之间报门没锁，这是新一代故障，照常按住、急停——
    /// 上一代的自动解除不替它豁免。
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
    /// <c>external</c>：本服务端的急停已自动解除并确认，之后有人在 RIoT 上按了急停——没有本服务端的急停在开，挡住 CONTINUE
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
    /// 审查必修 2 第二路补的那一格：按住在较早一轮已经回查确认，那一轮被一张外来的未完成单挡住没解除；之后有人在 RIoT 上把这张单
    /// CONTINUE 成执行中（3），外来单也没了，门锁报锁闭。放行时的重读读到 3：不解除。
    /// </summary>
    /// <remarks>
    /// 这一格有两道检查各自挡住：放行那次重读（<c>DoorReleaseAllowanceAsync</c>，单必须是 7），和监督器最后列车上未完成单时的单态
    /// （<c>ReleaseObstacles</c>）。单独去掉任何一道，另一道照样挡，这条仍绿；两道都去掉才红（见 <c>evidence/cs335/mutations.md</c>
    /// 的 M24–M26）。只有第二道看得见的时间窗由 <see cref="AHeldOrderThatRunsBeforeTheLastReadKeepsTheLatch"/> 守着。
    /// </remarks>
    [Fact]
    [Trait("Requirement", "REQ-0167")]
    public async Task AHoldConfirmedEarlierThenContinuedInRiotKeepsTheLatch()
    {
        await using RuntimeFixture fixture = await LatchedForTheDoorsAsync();
        Latched latched = await LatchedFactsAsync(fixture);
        fixture.UnfinishedOrderIds = [latched.OrderId, "FOREIGN-ORDER-0001"];
        await ReportLockedAsync(fixture);
        await DriveOneRoundAsync(fixture);

        // 前提：按住已回查确认，这一轮只是被外来单挡住。
        await using (ControlServerDbContext reading = new(fixture.DbOptionsForTests))
        {
            RiotOrderCommandAuditRow hold = Assert.Single(await reading.RiotOrderCommandAudit.AsNoTracking()
                .Where(row => row.CommandType == RiotCommandTypeNames.OrderHold).ToArrayAsync(Token));
            Assert.Equal(RiotOrderCommandOutcome.Confirmed, hold.Outcome);
        }
        Assert.Equal(0, await CountAsync(fixture, RiotCommandTypeNames.CancelEmergency));

        fixture.Riot.SetOrderState(latched.UpperId, RiotOrderState.Executing, terminal: false);
        fixture.UnfinishedOrderIds = [latched.OrderId];
        await ReportLockedAsync(fixture);
        await AssertNoReleaseOverRoundsAsync(fixture);
    }

    /// <summary>
    /// 审查必修 2（探针 P2）：解除那一轮里，放行已经读到本代自确认的 7，而在监督器最后一次列车上未完成单之前，单变成了执行中（3）。
    /// 最后那次读取也要核单态：不是 7 就不解除。
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-0167")]
    public async Task AHeldOrderThatRunsBeforeTheLastReadKeepsTheLatch()
    {
        await using RuntimeFixture fixture = await LatchedForTheDoorsAsync();
        Latched latched = await LatchedFactsAsync(fixture);
        fixture.BeforeUnfinishedOrdersRead = () =>
            fixture.Riot.SetOrderState(latched.UpperId, RiotOrderState.Executing, terminal: false);

        await ReportLockedAsync(fixture);
        await DriveOneRoundAsync(fixture);

        Assert.Equal(RiotOrderState.Executing, fixture.Riot.OrderStateOf(latched.UpperId));
        Assert.Equal(0, await CountAsync(fixture, RiotCommandTypeNames.CancelEmergency));
    }

    /// <summary>
    /// 审查必修 1（探针 P1）：门锁故障在效期间，有人在 RIoT 上取消或删除了本服务端按住的那张单。之后不能只剩改库：
    /// 门锁恢复新鲜锁闭、车上没有任何未完成单时自动解除急停（锁着那一格）；人工清除对门锁故障放行 CANCELLED／DELETED，
    /// 只清故障，重建交给引擎已经登记的那一条 cs#318 记录，不登记第二条；故障不自动清除。
    /// </summary>
    [Theory]
    [InlineData("latched", RiotOrderState.Cancelled)]
    [InlineData("released", RiotOrderState.Cancelled)]
    [InlineData("latched", RiotOrderState.Deleted)]
    [Trait("Requirement", "REQ-0167")]
    [Trait("Requirement", "REQ-0246")]
    public async Task AHeldOrderEndedInRiotUnderADoorFaultStillHasAWayOut(string latch, int ending)
    {
        await using RuntimeFixture fixture = latch == "latched"
            ? await LatchedForTheDoorsAsync()
            : await ReleasedForTheDoorsAsync();
        Latched latched = await LatchedFactsAsync(fixture);
        int releasesBefore = await CountAsync(fixture, RiotCommandTypeNames.CancelEmergency);

        fixture.Riot.SetOrderState(latched.UpperId, ending, terminal: true);
        fixture.UnfinishedOrderIds = [];
        await ReportLockedAsync(fixture);
        await DriveOneRoundAsync(fixture);

        // 锁着的那一格这一轮自动解除一次；RIoT 随后放开闩锁（假 RIoT 不会自己放，同整条路那条用例）。
        Assert.Equal(releasesBefore + (latch == "latched" ? 1 : 0), await CountAsync(fixture, RiotCommandTypeNames.CancelEmergency));
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
            Assert.Contains("VEHICLE_FAULT_IN_EFFECT", waiting.WaitingReason ?? string.Empty, StringComparison.Ordinal);
        }
        Assert.Equal(releasesBefore + (latch == "latched" ? 1 : 0), await CountAsync(fixture, RiotCommandTypeNames.CancelEmergency));
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

    // ---- 放行判定本身：每一种挡法一条 ----------------------------------------------------------------------------

    private const string OwnUpperId = "UPPER-OWN";
    private const string OwnOrderId = "ORDER-OWN";

    /// <summary>
    /// <c>VehicleFaultCoordinator.DoorReleaseAllowance</c> 的正例与每一种挡法。放行只给「门锁恢复 + 门锁症状仍在效 +
    /// 本代自己的 OrderHold 回查确认 + RIoT 此刻报这同一张单 PAUSED(7)」。
    /// </summary>
    /// <remarks>
    /// 「别的来路的 7」在这里有三种长相：本代没有按住过（holdOutcome 为空）、按住没确认（Pending）、RIoT 报的 7 是另一个
    /// orderId。把判定放宽成「任何 7」，这三条会红（变异 M1）；放宽成「任何症状」，<c>order-failed</c> 那条会红（变异 M2）。
    /// </remarks>
    [Theory]
    [InlineData("allowed")]
    [InlineData("doors-not-locked-again")]
    [InlineData("order-failed")]
    [InlineData("fault-cleared")]
    [InlineData("no-hold-this-generation")]
    [InlineData("hold-pending")]
    [InlineData("hold-failed")]
    [InlineData("other-order-id-paused")]
    [InlineData("queueing")]
    [InlineData("executing")]
    [InlineData("hang")]
    [InlineData("order-unread")]
    [InlineData("cancelled")]
    [InlineData("deleted")]
    [InlineData("cancelled-without-a-confirmed-hold")]
    [InlineData("cancelled-other-order-id")]
    [InlineData("cancelled-doors-not-locked-again")]
    [InlineData("cancelled-order-failed-fault")]
    [InlineData("terminal-failed")]
    [Trait("Requirement", "REQ-0167")]
    public void TheAllowanceIsGivenOnlyPastTheOwnConfirmedHold(string variant)
    {
        VehicleFaultFact fault = Fault(
            variant is "order-failed" or "cancelled-order-failed-fault"
                ? VehicleFaultEvidence.OrderFailed
                : VehicleFaultEvidence.DoorNotProvenLocked,
            variant == "fault-cleared" ? VehicleFaultLevel.None : VehicleFaultLevel.SuspectedBlocked);
        FaultedVehicleContext context = new(
            new RiotOrderCommandTarget("AGV-1", OwnUpperId, OwnOrderId),
            Cargo: null,
            DoorCauseRemoved: variant is not ("doors-not-locked-again" or "cancelled-doors-not-locked-again"));
        RiotOrderCommandOutcome? hold = variant switch
        {
            "no-hold-this-generation" or "cancelled-without-a-confirmed-hold" => null,
            "hold-pending" => RiotOrderCommandOutcome.Pending,
            "hold-failed" => RiotOrderCommandOutcome.Failed,
            _ => RiotOrderCommandOutcome.Confirmed,
        };
        RiotOrderObservation order = variant switch
        {
            "other-order-id-paused" => new(OwnUpperId, RiotOrderObservationKind.Active, "ORDER-SOMEONE-ELSE", RiotOrderState.Paused),
            "queueing" => new(OwnUpperId, RiotOrderObservationKind.Active, OwnOrderId, RiotOrderState.Queueing),
            "executing" => new(OwnUpperId, RiotOrderObservationKind.Active, OwnOrderId, RiotOrderState.Executing),
            "hang" => new(OwnUpperId, RiotOrderObservationKind.Active, OwnOrderId, RiotOrderState.Hang),
            "order-unread" => new(OwnUpperId, RiotOrderObservationKind.Unknown, null),
            "cancelled" or "cancelled-without-a-confirmed-hold" or "cancelled-doors-not-locked-again" or "cancelled-order-failed-fault" =>
                new(OwnUpperId, RiotOrderObservationKind.Terminal, OwnOrderId, RiotOrderState.Cancelled),
            "deleted" => new(OwnUpperId, RiotOrderObservationKind.Terminal, OwnOrderId, RiotOrderState.Deleted),
            "cancelled-other-order-id" =>
                new(OwnUpperId, RiotOrderObservationKind.Terminal, "ORDER-SOMEONE-ELSE", RiotOrderState.Cancelled),
            "terminal-failed" => new(OwnUpperId, RiotOrderObservationKind.Terminal, OwnOrderId, RiotOrderState.Failed),
            _ => new(OwnUpperId, RiotOrderObservationKind.Active, OwnOrderId, RiotOrderState.Paused),
        };

        EmergencyReleaseAllowance? allowance = VehicleFaultCoordinator.DoorReleaseAllowance(fault, context, hold, order);

        if (variant == "allowed")
        {
            Assert.Equal(new EmergencyReleaseAllowance(3, OwnOrderId), allowance);
        }
        else if (variant is "cancelled" or "deleted" or "cancelled-without-a-confirmed-hold")
        {
            // 单已在 RIoT 结束（审查 P1）：放行不点名任何单，监督器因此要求车上一张未完成单都没有。按住有没有确认过无关——
            // 没有单了，就没有能让车动的东西。
            Assert.Equal(new EmergencyReleaseAllowance(3, HeldOrderId: null), allowance);
        }
        else
        {
            Assert.Null(allowance);
        }
    }

    /// <summary>
    /// <c>EmergencyStopSupervisor.ReleaseObstacles</c> 那一侧：放行只让过它点名的那一张单、只对它点名的那一代。
    /// 车上另有一张未完成单、放行点的是别的代次、故障已换代、没有放行——都照旧挡。
    /// 点名那张单在这次列表里必须读到 7：读到 3 或读不到单态都挡（审查 P2）。不点名任何单的放行（单已在 RIoT 取消或删除，
    /// 审查 P1）要求车上一张未完成单都没有。
    /// </summary>
    [Theory]
    [InlineData("allowed", "")]
    [InlineData("held-order-listed-executing", "EMERGENCY_VEHICLE_ORDER_NOT_FINISHED")]
    [InlineData("held-order-listed-without-state", "EMERGENCY_VEHICLE_ORDER_NOT_FINISHED")]
    [InlineData("no-order-named-none-listed", "")]
    [InlineData("no-order-named-one-listed", "EMERGENCY_VEHICLE_ORDER_NOT_FINISHED")]
    [InlineData("another-unfinished-order", "EMERGENCY_VEHICLE_ORDER_NOT_FINISHED")]
    [InlineData("allowance-for-another-generation", "EMERGENCY_VEHICLE_ORDER_NOT_FINISHED,EMERGENCY_CAUSE_NOT_CLEARED")]
    [InlineData("trigger-of-another-generation", "EMERGENCY_VEHICLE_ORDER_NOT_FINISHED,EMERGENCY_FAULT_GENERATION_MOVED,EMERGENCY_CAUSE_NOT_CLEARED")]
    [InlineData("no-allowance", "EMERGENCY_VEHICLE_ORDER_NOT_FINISHED,EMERGENCY_CAUSE_NOT_CLEARED")]
    [Trait("Requirement", "REQ-0167")]
    public void TheSupervisorLetsThroughOnlyTheOrderAndGenerationTheAllowanceNames(string variant, string expected)
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-09-28T08:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        RiotVehicleEmergencyObservation latched = new("DEVICE-1", RiotVehicleEmergencyObservation.CanRecover, now);
        string[] unfinished = variant switch
        {
            "another-unfinished-order" => [OwnOrderId, "ORDER-FOREIGN"],
            "no-order-named-none-listed" => [],
            _ => [OwnOrderId],
        };
        Dictionary<string, int?> states = new(StringComparer.Ordinal)
        {
            [OwnOrderId] = variant == "held-order-listed-executing" ? RiotOrderState.Executing : RiotOrderState.Paused,
            ["ORDER-FOREIGN"] = RiotOrderState.Executing,
        };
        if (variant == "held-order-listed-without-state")
        {
            states.Remove(OwnOrderId);
        }
        EmergencyReleaseAllowance? allowance = variant switch
        {
            "no-allowance" => null,
            "no-order-named-none-listed" or "no-order-named-one-listed" => new EmergencyReleaseAllowance(3, HeldOrderId: null),
            "allowance-for-another-generation" => new EmergencyReleaseAllowance(2, OwnOrderId),
            _ => new EmergencyReleaseAllowance(3, OwnOrderId),
        };

        IReadOnlyList<string> obstacles = EmergencyStopSupervisor.ReleaseObstacles(
            latched,
            triggerFaultGeneration: variant == "trigger-of-another-generation" ? 2 : 3,
            Fault(VehicleFaultEvidence.DoorNotProvenLocked, VehicleFaultLevel.SuspectedBlocked),
            new RiotVehicleOrderObservation("DEVICE-1", unfinished.Length > 0, unfinished, now, states),
            allowance);

        Assert.Equal(expected, string.Join(',', obstacles));
    }

    private static VehicleFaultFact Fault(string evidence, VehicleFaultLevel level) =>
        new(
            "AGV-1",
            level,
            FaultGeneration: 3,
            evidence,
            EvidenceOnAutoConfirmWhitelist: false,
            EnteredAt: null,
            LastEvaluatedAt: null,
            StopProven: false,
            StopProvenAt: null,
            EscalatedAt: null,
            ClearedAt: level == VehicleFaultLevel.None ? DateTimeOffset.UnixEpoch : null,
            ClearedReason: null);

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
    /// <see cref="LatchedForTheDoorsAsync"/> 之后门锁恢复、自动解除并确认，再跑一轮：单仍 PAUSED、急停一次、解除一次，
    /// 车停在两站之间（MT_PAUSED）。前置都在这里断，之后的红只能来自解除之后那一段。
    /// </summary>
    private static async Task<RuntimeFixture> ReleasedForTheDoorsAsync()
    {
        RuntimeFixture fixture = await LatchedForTheDoorsAsync();
        await ReportLockedAsync(fixture);
        await DriveOneRoundAsync(fixture);
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
}
