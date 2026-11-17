using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Commands;
using ControlServer.Host.Runtime.Faults;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;
using D = ControlServer.Tests.DoorFaultEscalatedThenCancelledInRiotTests;

namespace ControlServer.Tests;

/// <summary>
/// control-server#900（cs#899 审查 M1、L1、L4）：门锁故障升级、依据码被改写之后，在途门锁监看、单被取消后的监看与急停的门锁原因兜底
/// 仍按「这一代故障起源于门锁」认它（<c>OriginSymptomCode ?? EvidenceCode</c>），和清除入口（<c>DoorFaultHeldThisOrderAsync</c>）同一个判法。
/// </summary>
/// <remarks>
/// <para>
/// <b>修前。</b>三处只比当前依据码。按钮一按，依据码改写成 <c>RIOT_EMERGENCY_CAN_NOT_RECOVER</c>：单 HELD、门锁读回锁闭时
/// <c>InTransitDoorReadings.BeforeFault</c> 走「锁闭」分支、什么都不做，故障模型不再被调——人解除之后单若被 RIoT 跑起来、车在动，
/// 没有人再急停，也不写 <c>HELD_ORDER_RESUMED_WITHOUT_CONTINUE</c>；单被取消之后 <c>SuperviseDoorFaultOnEndedOrderAsync</c> 直接返回，
/// 故障的 <c>LastEvaluatedAt</c> 停住（11-15 agv03 停在 16:14:24）。
/// </para>
/// <para>
/// <b>按钮先按着、进程在记级别与按住之间退出</b>（cs#899 审查 L1，<c>VehicleFaultCoordinator.ApplyAsync</c> 两次保存之间）：这一代故障一开始依据就是
/// <c>CAN_NOT_RECOVER</c>，修前之后再没有一轮调它，这一代永远不按住那张单；单再被取消，清除入口就一直拒。
/// </para>
/// <para>
/// 从来不是门锁的故障（起源是别的症状）照今天的行为：门锁这一路不调它、不按住，清除照旧拒。
/// </para>
/// </remarks>
public sealed class EscalatedDoorFaultSupervisionTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// 门锁故障被按钮升级（<c>after</c>：先门锁后按钮，现场的样子；<c>before</c>：按钮在第一次看到门锁之前就按着），单 HELD、门锁读回锁闭、按钮松开、
    /// 闩锁仍锁着。之后：
    /// <c>external-release</c>——有人在 RIoT 一侧把闩锁解开（不经本服务端三类入口）：本服务端这一类还在，下一轮重下急停（REQ-0248）；
    /// <c>runs-without-continue</c>——人经三类入口解除之后，没人按继续，RIoT 却把那张单跑了起来、车在动：急停再下，旅程写
    /// <c>HELD_ORDER_RESUMED_WITHOUT_CONTINUE</c>。
    /// </summary>
    /// <remarks>
    /// <c>external-release</c> 两格修前也是绿的：开着的急停每轮由 <c>VehicleFaultCoordinator.WatchOpenStopAsync</c> 监看（cs#539 审查 H-1），
    /// 不靠门锁这一路。留着它守那条边。<c>runs-without-continue</c> 两格修前红：人解除之后没有开着的急停，能再停车的只有故障模型这一路。
    /// </remarks>
    [Theory]
    [InlineData("after", "external-release")]
    [InlineData("before", "external-release")]
    [InlineData("after", "runs-without-continue")]
    [InlineData("before", "runs-without-continue")]
    [Trait("Requirement", "REQ-0246")]
    [Trait("Requirement", "REQ-0248")]
    public async Task AnEscalatedDoorFaultHeldWithItsDoorsReadLockedStillStopsTheVehicleAgain(string escalation, string then)
    {
        await using RuntimeFixture fixture = await D.GateArrivalWaitAsync();
        (string upperId, int triggers) = await EscalatedHeldAndLockedAsync(fixture, buttonFirst: escalation == "before");

        if (then == "external-release")
        {
            fixture.EmergencyLatched = false;
            await D.DriveOneRoundAsync(fixture);
            Assert.Equal(triggers + 1, await D.CountAsync(fixture, RiotCommandTypeNames.TriggerEmergency));
            return;
        }

        await fixture.ReleaseByAPersonAsync();
        fixture.EmergencyLatched = false;
        for (int round = 0; round < 3; round++)
        {
            await D.DriveOneRoundAsync(fixture);
        }

        // 人解除之后单仍 HELD、车不动：不再急停（豁免只免「报不出站点」）。
        Assert.Equal(triggers, await D.CountAsync(fixture, RiotCommandTypeNames.TriggerEmergency));

        fixture.Riot.SetOrderState(upperId, RiotOrderState.Executing, terminal: false);
        fixture.Riot.MovementState = "MT_RUNNING";
        await D.DriveOneRoundAsync(fixture);

        Assert.Equal(triggers + 1, await D.CountAsync(fixture, RiotCommandTypeNames.TriggerEmergency));
        Assert.Equal(JourneyRuntimeEngine.HeldOrderResumedWithoutContinueReason, (await fixture.RuntimeAsync()).BlockReasonCode);
    }

    /// <summary>
    /// 门锁故障被按钮升级、按住的单在 RIoT 被取消（cs#897 的现场序列）：故障模型照旧每轮被调——<c>LastEvaluatedAt</c> 每轮前移；
    /// 有货急停人工解除之后车被读到在动，急停再下（单已结束，门锁读数不再收回豁免，运动照旧停车：
    /// <c>SuperviseDoorFaultOnEndedOrderAsync</c> 的说明，cs#539 审查 S-2）。
    /// </summary>
    [Theory]
    [InlineData("after")]
    [InlineData("before")]
    [Trait("Requirement", "REQ-0246")]
    [Trait("Requirement", "REQ-0248")]
    public async Task AnEscalatedDoorFaultIsStillSupervisedAfterItsOrderIsCancelledInRiot(string escalation)
    {
        await using RuntimeFixture fixture = await D.GateArrivalWaitAsync();
        (string upperId, int triggers) = await EscalatedHeldAndLockedAsync(fixture, buttonFirst: escalation == "before");

        fixture.Riot.SetOrderState(upperId, RiotOrderState.Cancelled, terminal: true);
        fixture.UnfinishedOrderIds = [];
        await D.DriveOneRoundAsync(fixture);
        Assert.Equal(OwnOrderRebuildSources.CancelledInRiot, Assert.Single(await D.RebuildsAsync(fixture)).Source);

        DateTimeOffset? before = (await D.FaultAsync(fixture)).LastEvaluatedAt;
        await D.DriveOneRoundAsync(fixture);
        VehicleFaultStateRow evaluated = await D.FaultAsync(fixture);
        Assert.Equal(VehicleFaultEvidence.EmergencyCanNotRecover, evaluated.EvidenceCode);
        Assert.True(evaluated.LastEvaluatedAt > before, $"LastEvaluatedAt stayed at {before}");

        await fixture.ReleaseByAPersonAsync();
        fixture.EmergencyLatched = false;
        for (int round = 0; round < 3; round++)
        {
            await D.DriveOneRoundAsync(fixture);
        }

        Assert.Equal(triggers, await D.CountAsync(fixture, RiotCommandTypeNames.TriggerEmergency));
        fixture.Riot.MovementState = "MT_RUNNING";
        await D.DriveOneRoundAsync(fixture);
        Assert.Equal(triggers + 1, await D.CountAsync(fixture, RiotCommandTypeNames.TriggerEmergency));
    }

    /// <summary>
    /// cs#899 审查 L1：按钮先按着，服务端第一次看到门锁就记下这一代故障（<c>ConfirmedIsolated</c> / <c>CAN_NOT_RECOVER</c>，起源门锁），
    /// 进程在按住那张单之前退出。之后门锁读回锁闭。<c>running</c>：单还在（没人按住过）——下一轮这一代故障按住它。
    /// <c>cancelled</c>：重启前单已在 RIoT 被取消——下一轮仍按这一代记一次按住（单已取消，RIoT 不会停住它，只是留下记录），
    /// 按钮松开、人解除急停之后清除入口接受（只清故障，引擎那一条重建接着走）。修前两格都不按住，<c>cancelled</c> 那格清除被拒。
    /// </summary>
    [Theory]
    [InlineData("running")]
    [InlineData("cancelled")]
    [Trait("Requirement", "REQ-0234")]
    [Trait("Requirement", "REQ-0360")]
    public async Task AGenerationRecordedWithoutItsHoldIsHeldOnTheNextRound(string order)
    {
        await using RuntimeFixture fixture = await D.GateArrivalWaitAsync();
        string upperId = (await fixture.RuntimeAsync()).GateUpperId!;
        long generation = await StoppedBetweenRecordAndHoldAsync(fixture, upperId, VehicleFaultEvidence.DoorNotProvenLocked, order);

        await D.DriveOneRoundAsync(fixture);

        Assert.True(await D.HeldUnderGenerationAsync(fixture, upperId, generation), "this generation never held its order");
        if (order == "running")
        {
            return;
        }

        fixture.ButtonHeld = false;
        fixture.EmergencyLatched = true;
        await D.DriveOneRoundAsync(fixture);
        await fixture.ReleaseByAPersonAsync();
        fixture.EmergencyLatched = false;
        await D.DriveOneRoundAsync(fixture);

        VehicleFaultRecoveryDecision cleared = await D.ClearAsync(fixture);
        Assert.True(VehicleFaultRecoveryOutcome.Cleared == cleared.Outcome, string.Join(", ", cleared.Reasons));
        Assert.Equal(VehicleFaultRecoveryDispositions.None, cleared.Disposition);
        Assert.Single(await D.RebuildsAsync(fixture));
    }

    /// <summary>
    /// 从来不是门锁的故障保持今天的行为：同样的崩溃窗口、同样 <c>ConfirmedIsolated</c> / <c>CAN_NOT_RECOVER</c>，起源是别的症状。门锁读回锁闭时门锁
    /// 这一路不调故障模型、不按住那张单；单被取消时清除照旧拒（#335 审查 M1）。
    /// </summary>
    [Theory]
    [InlineData("running")]
    [InlineData("cancelled")]
    [Trait("Requirement", "REQ-0234")]
    public async Task AFaultThatNeverWasADoorFaultIsNotPickedUpByTheDoorSupervision(string order)
    {
        await using RuntimeFixture fixture = await D.GateArrivalWaitAsync();
        string upperId = (await fixture.RuntimeAsync()).GateUpperId!;
        long generation = await StoppedBetweenRecordAndHoldAsync(fixture, upperId, VehicleFaultEvidence.OrderFailed, order);

        await D.DriveOneRoundAsync(fixture);
        await D.DriveOneRoundAsync(fixture);

        Assert.False(await D.HeldUnderGenerationAsync(fixture, upperId, generation));
        if (order == "cancelled")
        {
            VehicleFaultRecoveryDecision refused = await D.ClearAsync(fixture);
            Assert.Equal(VehicleFaultRecoveryOutcome.Refused, refused.Outcome);
            Assert.Contains("FAULT_RECOVERY_CURRENT_ORDER_CANCELLED_IN_RIOT", refused.Reasons);
        }
    }

    /// <summary>
    /// 急停的门锁原因兜底（<c>EmergencyStopDoorCause</c>，为记原因之前的急停留的）：只剩故障行可问时，起源是门锁的升级故障仍要人确认门锁已检修；
    /// 起源是别的症状的不要；起源列为空的旧行照依据码判。修前 <c>escalated-door</c> 那格答 false。
    /// </summary>
    [Theory]
    [InlineData("escalated-door", VehicleFaultEvidence.DoorNotProvenLocked, VehicleFaultEvidence.EmergencyCanNotRecover, true)]
    [InlineData("never-door", VehicleFaultEvidence.OrderFailed, VehicleFaultEvidence.EmergencyCanNotRecover, false)]
    [InlineData("legacy-door", null, VehicleFaultEvidence.DoorNotProvenLocked, true)]
    [InlineData("legacy-other", null, VehicleFaultEvidence.EmergencyCanNotRecover, false)]
    [Trait("Requirement", "REQ-0356")]
    public async Task TheStopsDoorCauseFallbackReadsTheFaultsOrigin(
        string row, string? origin, string evidence, bool repairRequired)
    {
        Assert.NotNull(row);
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        await using ControlServerDbContext context = new(fixture.DbOptionsForTests);
        context.VehicleFaultStates.Add(new VehicleFaultStateRow
        {
            AgvId = fixture.Options.AgvId,
            Level = VehicleFaultLevel.ConfirmedIsolated,
            FaultGeneration = 1,
            EvidenceCode = evidence,
            OriginSymptomCode = origin,
            EnteredAt = fixture.Clock.GetUtcNow(),
            LastEvaluatedAt = fixture.Clock.GetUtcNow(),
        });
        await context.SaveChangesAsync(Token);

        bool doorCause = await EmergencyStopDoorCause.ReadAsync(
            context,
            new VehicleFaultStore(context),
            fixture.CreateEmergencyStopSupervisor(context),
            new EmergencyStopSubject(fixture.Options.AgvId, fixture.Options.VehicleKey),
            Token);

        Assert.Equal(repairRequired, doorCause);
    }

    /// <summary>
    /// 行驶中门锁没锁 -> 门锁故障按住单；按钮按下（<paramref name="buttonFirst"/> 时第一次观测前就按着），故障升级、依据改写；RIoT 停住单（PAUSED）、闩锁锁着；
    /// 门锁读回锁闭，按钮松开（CAN_RECOVER）。返回单的 upperId 与此时的急停次数。
    /// </summary>
    private static async Task<(string UpperId, int Triggers)> EscalatedHeldAndLockedAsync(RuntimeFixture fixture, bool buttonFirst)
    {
        string upperId = (await fixture.RuntimeAsync()).GateUpperId!;
        fixture.Riot.MovementState = "MT_RUNNING";
        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { CurrentStationId = 0 };
        fixture.ButtonHeld = buttonFirst;
        await fixture.ReportSafetySummaryAsync(
            allTargetSlotsLocked: false, unknownPresent: false, ["LOCK_NOT_CLOSED", "ACTION_NOT_ALLOWED_IN_STATE"]);
        await D.DriveOneRoundAsync(fixture);
        string orderId = await D.OrderIdAsync(fixture, upperId);
        fixture.Riot.SetOrderState(upperId, RiotOrderState.Paused, terminal: false);
        fixture.Riot.MovementState = "MT_PAUSED";
        fixture.EmergencyLatched = true;
        fixture.UnfinishedOrderIds = [orderId];
        await D.DriveOneRoundAsync(fixture);
        fixture.ButtonHeld = true;
        await D.DriveOneRoundAsync(fixture);

        await D.ReportLockedAsync(fixture);
        await D.DriveOneRoundAsync(fixture);
        fixture.ButtonHeld = false;
        await D.DriveOneRoundAsync(fixture);
        await D.DriveOneRoundAsync(fixture);

        VehicleFaultStateRow fault = await D.FaultAsync(fixture);
        Assert.Equal(
            (VehicleFaultLevel.ConfirmedIsolated, VehicleFaultEvidence.EmergencyCanNotRecover, VehicleFaultEvidence.DoorNotProvenLocked),
            (fault.Level, fault.EvidenceCode, fault.OriginSymptomCode));
        Assert.True(await D.HeldUnderGenerationAsync(fixture, upperId, fault.FaultGeneration));
        return (upperId, await D.CountAsync(fixture, RiotCommandTypeNames.TriggerEmergency));
    }

    /// <summary>
    /// 进程在 <c>ApplyAsync</c> 记下级别之后、按住之前退出留下的样子：按钮按着，故障行 <c>ConfirmedIsolated</c> / <c>CAN_NOT_RECOVER</c>、起源
    /// <paramref name="origin"/>，没有按住记录、没有急停记录。门锁读回锁闭；<paramref name="order"/> 为 <c>cancelled</c> 时单已在 RIoT 被取消。
    /// </summary>
    private static async Task<long> StoppedBetweenRecordAndHoldAsync(
        RuntimeFixture fixture, string upperId, string origin, string order)
    {
        string orderId = await D.OrderIdAsync(fixture, upperId);
        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { CurrentStationId = 0 };
        fixture.Riot.MovementState = "MT_PAUSED";
        fixture.ButtonHeld = true;
        fixture.EmergencyLatched = true;
        if (order == "cancelled")
        {
            fixture.Riot.SetOrderState(upperId, RiotOrderState.Cancelled, terminal: true);
            fixture.UnfinishedOrderIds = [];
        }
        else
        {
            fixture.UnfinishedOrderIds = [orderId];
        }

        long generation;
        await using (ControlServerDbContext context = new(fixture.DbOptionsForTests))
        {
            VehicleFaultFact fault = await new VehicleFaultStore(context).RecordLevelAsync(
                fixture.Options.AgvId,
                VehicleFaultLevel.ConfirmedIsolated,
                VehicleFaultEvidence.EmergencyCanNotRecover,
                VehicleFaultEvidence.IsAutoConfirmable(VehicleFaultEvidence.EmergencyCanNotRecover),
                origin,
                fixture.Clock.GetUtcNow(),
                Token);
            generation = fault.FaultGeneration;
        }

        await D.ReportLockedAsync(fixture);
        fixture.Context.ChangeTracker.Clear();
        Assert.False(await D.HeldUnderGenerationAsync(fixture, upperId, generation));
        return generation;
    }
}
