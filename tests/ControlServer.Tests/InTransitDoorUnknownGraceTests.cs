using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Faults;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using static ControlServer.Tests.Batch7StopDrivenAdvanceDriver;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;

namespace ControlServer.Tests;

/// <summary>
/// control-server#789 第一步（CP-0017 修订项一，REQ-0246）：行驶中车载端报「仓位状态未知」（<c>SLOT_STATE_UNKNOWN</c>），先宽限 3 秒再交故障模型；
/// 读到「没锁」照旧同一轮处理。宽限期间只告警，不记故障、不按住、不急停；读到一次锁闭就结束本次宽限；同一车任意 10 分钟内开始过 2 次宽限，
/// 第 3 次起不再宽限。
/// </summary>
/// <remarks>
/// <para>
/// <b>从服务端收到「未知」那一刻起算</b>（CP-0017 第五节第 6 项），不用车载机的时钟，也不从「哪一轮第一次看到」起算：后者最坏会多给一轮。
/// 夹具每轮走一秒，摘要在轮与轮之间收到，所以「收到后第 3 轮」就是满 3 秒。
/// </para>
/// <para>
/// <b>重启</b>：宽限计时与次数放单例（记忆 cross-round-state-singleton-not-static），随进程清零；但起点重新取那条摘要的收到时刻，
/// 所以重启不会把宽限拉长。次数清零是放宽的一面，写进 PR 的剩余风险。
/// </para>
/// </remarks>
public sealed class InTransitDoorUnknownGraceTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ---- 宽限：不交故障、只告警 ------------------------------------------------------------------------------------------

    /// <summary>
    /// 「未知」1 秒后读回锁闭：不记故障、不发任何命令；告警有两条——开始宽限、读数中断约 1 秒后恢复（CP-0017 修订项一说明 8）。
    /// 闸门两侧各一遍：真车载端在途时会话未就绪（闸门后），合成车载端就绪（闸门前）。
    /// </summary>
    [Theory]
    [InlineData("behind-gate")]
    [InlineData("ready")]
    [Trait("Requirement", "REQ-0246")]
    public async Task AnUnknownThatReadsLockedWithinTheGraceRaisesNothingAndIsAlarmed(string side)
    {
        await using RuntimeFixture fixture = await DrivingAsync(side);

        await ReportUnknownAsync(fixture);
        await DriveOneRoundAsync(fixture);
        await AssertNothingRaisedAsync(fixture);
        await ReportLockedAsync(fixture);
        for (int round = 0; round < 10; round++)
        {
            await DriveOneRoundAsync(fixture);
            await AssertNothingRaisedAsync(fixture);
        }

        Assert.Empty(fixture.RiotCommandsSent);
        Assert.Single(fixture.EngineLog.Entries, entry =>
            entry.Level == LogLevel.Warning && entry.Message.Contains("SLOT_STATE_UNKNOWN", StringComparison.Ordinal) &&
            entry.Message.Contains("graced for 3 s", StringComparison.Ordinal));
        Assert.Single(fixture.EngineLog.Entries, entry =>
            entry.Message.Contains("came back locked after 1 s unread", StringComparison.Ordinal));
    }

    /// <summary>「未知」持续满 3 秒仍未读到锁闭：第 3 秒那一轮交故障模型，按住、急停，一样不少（REQ-0246 原文的处理）。</summary>
    [Theory]
    [InlineData("behind-gate")]
    [InlineData("ready")]
    [Trait("Requirement", "REQ-0246")]
    public async Task AnUnknownStillUnknownWhenTheGraceRunsOutIsHandedToTheFaultModel(string side)
    {
        await using RuntimeFixture fixture = await DrivingAsync(side);
        string upperId = (await fixture.RuntimeAsync()).GateUpperId!;

        await ReportUnknownAsync(fixture);
        await DriveOneRoundAsync(fixture);
        await DriveOneRoundAsync(fixture);
        await AssertNothingRaisedAsync(fixture);
        await DriveOneRoundAsync(fixture);

        await AssertHeldThenStoppedAsync(fixture, upperId);
        Assert.Contains(fixture.EngineLog.Entries, entry =>
            entry.Message.Contains(InTransitDoorReadings.HandGraceExpired, StringComparison.Ordinal));
    }

    /// <summary>宽限期间读到一扇门明确没锁：同一轮交故障模型，不等宽限用完（CP-0017 修订项一说明 6）。</summary>
    [Fact]
    [Trait("Requirement", "REQ-0246")]
    public async Task ADoorReadNotLockedDuringTheGraceIsHandedAtOnce()
    {
        await using RuntimeFixture fixture = await DrivingAsync("behind-gate");
        string upperId = (await fixture.RuntimeAsync()).GateUpperId!;

        await ReportUnknownAsync(fixture);
        await DriveOneRoundAsync(fixture);
        await AssertNothingRaisedAsync(fixture);
        await ReportNotLockedAsync(fixture);
        await DriveOneRoundAsync(fixture);

        await AssertHeldThenStoppedAsync(fixture, upperId);
    }

    /// <summary>
    /// 宽限期间两轮之间车载端报了一条「没锁」又报回「读不到」，没有一轮读到那条「没锁」：照样同一轮交故障模型，不等宽限用完
    /// （cs#839 审查 H1，宽限这一侧的同一抽样缺口）。
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-0246")]
    public async Task ADoorReadNotLockedBetweenTwoRoundsOfTheGraceIsHandedAtOnce()
    {
        await using RuntimeFixture fixture = await DrivingAsync("behind-gate");
        string upperId = (await fixture.RuntimeAsync()).GateUpperId!;

        await ReportUnknownAsync(fixture);
        await DriveOneRoundAsync(fixture);
        await AssertNothingRaisedAsync(fixture);
        await ReportNotLockedAsync(fixture);
        await ReportUnknownAsync(fixture);
        await DriveOneRoundAsync(fixture);

        await AssertHeldThenStoppedAsync(fixture, upperId);
    }

    /// <summary>
    /// 宽限从首次收到的那条「未知」起算，不从某一轮看到的那条起算（审查 M3）：两轮之间先后收到两条「未知」，
    /// 轮次只看到第二条；第一条收到满 3 秒那一轮就交故障模型。
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-0246")]
    public async Task TheGraceCountsFromTheFirstUnknownReceivedNotTheOneARoundSaw()
    {
        await using RuntimeFixture fixture = await DrivingAsync("behind-gate");
        string upperId = (await fixture.RuntimeAsync()).GateUpperId!;

        await ReportUnknownAsync(fixture);
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        await fixture.ReportSafetySummaryAsync(
            allTargetSlotsLocked: false,
            unknownPresent: true,
            ["SLOT_STATE_UNKNOWN", "LOCK_NOT_CLOSED", "ACTION_NOT_ALLOWED_IN_STATE"],
            allUnlockOutputsReset: false);
        await DriveOneRoundAsync(fixture);
        await AssertNothingRaisedAsync(fixture);
        await DriveOneRoundAsync(fixture);

        await AssertHeldThenStoppedAsync(fixture, upperId);
    }

    /// <summary>读到「没锁」从来不宽限：没有「未知」在前，同一轮按住、急停。拆分「没锁」与「未知」前后这一格行为一样。</summary>
    [Fact]
    [Trait("Requirement", "REQ-0246")]
    public async Task ADoorReadNotLockedIsNeverGraced()
    {
        await using RuntimeFixture fixture = await DrivingAsync("behind-gate");
        string upperId = (await fixture.RuntimeAsync()).GateUpperId!;

        await ReportNotLockedAsync(fixture);
        await DriveOneRoundAsync(fixture);

        await AssertHeldThenStoppedAsync(fixture, upperId);
        Assert.DoesNotContain(fixture.EngineLog.Entries, entry =>
            entry.Message.Contains("graced for", StringComparison.Ordinal));
    }

    /// <summary>
    /// 宽限期间读到一次锁闭就清零；下一次「未知」从它自己的收到时刻重新算满 3 秒——不接着上一次的计时。
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-0246")]
    public async Task ALockedReadingEndsTheGraceAndTheNextUnknownStartsAfresh()
    {
        await using RuntimeFixture fixture = await DrivingAsync("behind-gate");
        string upperId = (await fixture.RuntimeAsync()).GateUpperId!;

        await ReportUnknownAsync(fixture);
        await DriveOneRoundAsync(fixture);
        await DriveOneRoundAsync(fixture);
        await ReportLockedAsync(fixture);
        await DriveOneRoundAsync(fixture);
        await ReportUnknownAsync(fixture);
        await DriveOneRoundAsync(fixture);
        await DriveOneRoundAsync(fixture);
        await AssertNothingRaisedAsync(fixture);
        await DriveOneRoundAsync(fixture);

        await AssertHeldThenStoppedAsync(fixture, upperId);
    }

    // ---- 限次 ----------------------------------------------------------------------------------------------------------

    /// <summary>
    /// 同一车 10 分钟内已开始过 2 次宽限（都在 3 秒内恢复了），第 3 次报「未知」同一轮就交故障模型（CP-0017 第五节第 1、2 项）。
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-0246")]
    public async Task TheThirdGraceWithinTenMinutesIsNotGiven()
    {
        await using RuntimeFixture fixture = await DrivingAsync("behind-gate");
        string upperId = (await fixture.RuntimeAsync()).GateUpperId!;
        await GraceThatRecoversAsync(fixture);
        await GraceThatRecoversAsync(fixture);

        await ReportUnknownAsync(fixture);
        await DriveOneRoundAsync(fixture);

        await AssertHeldThenStoppedAsync(fixture, upperId);
        Assert.Contains(fixture.EngineLog.Entries, entry =>
            entry.Message.Contains(InTransitDoorReadings.HandGraceLimitReached, StringComparison.Ordinal));
    }

    /// <summary>
    /// 两轮之间一次「锁闭」把「未知」断成两段，第二段算新开始的一次宽限（复审 M1、探针 P3）：10 分钟内第 3 段「未知」不再宽限。
    /// 修之前已开着的宽限只沿用原来的起点，那条锁闭不算新一段，少算一次。
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-0246")]
    public void ALockedReadingBetweenTwoRoundsSplitsTheGraceIntoTwo()
    {
        InTransitDoorReadings readings = new(Microsoft.Extensions.Options.Options.Create(new VehicleFaultOptions()));
        Assert.Equal(InTransitDoorAction.InGrace, readings.Observe(
            "AGV-A", new InTransitDoorVerdict(InTransitDoorState.SlotStateUnknown, 1, 1, Now) { UnknownSince = Now }, false,
            Now.AddSeconds(0.2)).Action);
        InTransitDoorReading second = readings.Observe(
            "AGV-A",
            new InTransitDoorVerdict(InTransitDoorState.SlotStateUnknown, 1, 3, Now.AddSeconds(1.2))
            {
                UnknownSince = Now.AddSeconds(1.2),
                Between = [new(InTransitDoorState.ProvenLocked, Now.AddSeconds(0.8))],
            },
            false,
            Now.AddSeconds(1.4));
        Assert.Equal((InTransitDoorAction.InGrace, true), (second.Action, second.GraceStarted));
        Assert.Equal(InTransitDoorAction.Nothing, readings.Observe(
            "AGV-A", new InTransitDoorVerdict(InTransitDoorState.ProvenLocked, 1, 4, Now.AddSeconds(1.6)), false,
            Now.AddSeconds(2)).Action);

        InTransitDoorReading third = readings.Observe(
            "AGV-A", new InTransitDoorVerdict(InTransitDoorState.SlotStateUnknown, 1, 5, Now.AddSeconds(30)) { UnknownSince = Now.AddSeconds(30) },
            false, Now.AddSeconds(30.2));

        Assert.Equal(
            (InTransitDoorAction.HandToFault, InTransitDoorReadings.HandGraceLimitReached), (third.Action, third.HandedBecause));
    }

    /// <summary>
    /// 故障之前，两轮之间那条「没锁」是车停着时报的（站内授权装卸开着门），不算行驶中读到没锁：接着报「读不到」照样宽限，不当场交出。
    /// 同一条若是行驶中报的，当场交出（复审情况 a）。
    /// </summary>
    [Theory]
    [InlineData(false, InTransitDoorAction.HandToFault)]
    [InlineData(true, InTransitDoorAction.InGrace)]
    [Trait("Requirement", "REQ-0246")]
    public void BeforeAFaultADoorReadNotLockedCountsOnlyFromAMovingVehicle(bool stopped, InTransitDoorAction expected)
    {
        InTransitDoorReadings readings = new(Microsoft.Extensions.Options.Options.Create(new VehicleFaultOptions()));
        Assert.Equal(InTransitDoorAction.Nothing, readings.Observe(
            "AGV-A", new InTransitDoorVerdict(InTransitDoorState.ProvenLocked, 1, 1, Now), false, Now.AddSeconds(0.2)).Action);

        InTransitDoorReading reading = readings.Observe(
            "AGV-A",
            new InTransitDoorVerdict(InTransitDoorState.SlotStateUnknown, 1, 3, Now.AddSeconds(0.8))
            {
                UnknownSince = Now.AddSeconds(0.8),
                Between = [new(InTransitDoorState.NotLocked, Now.AddSeconds(0.5), Moving: !stopped)],
            },
            false,
            Now.AddSeconds(1.2));

        Assert.Equal(expected, reading.Action);
    }

    /// <summary>窗口是滑动的：第 1 次宽限开始 10 分钟之后，又能给 1 次。</summary>
    [Fact]
    [Trait("Requirement", "REQ-0246")]
    public async Task AGraceOlderThanTheWindowNoLongerCounts()
    {
        await using RuntimeFixture fixture = await DrivingAsync("behind-gate");
        await GraceThatRecoversAsync(fixture);
        fixture.Clock.Advance(TimeSpan.FromMinutes(5));
        await GraceThatRecoversAsync(fixture);
        fixture.Clock.Advance(TimeSpan.FromMinutes(5));

        await ReportUnknownAsync(fixture);
        await DriveOneRoundAsync(fixture);
        await AssertNothingRaisedAsync(fixture);
    }

    /// <summary>
    /// 每次开始宽限都算一次，不论它是恢复了还是撑满 3 秒交了故障（CP-0017 修订项一说明 7）：第一次撑满、故障由人清除之后，
    /// 再来一次恢复了的，第 3 次就不再宽限。
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-0246")]
    public void AGraceThatRanOutCountsTowardsTheLimitToo()
    {
        InTransitDoorReadings readings = new(Microsoft.Extensions.Options.Options.Create(new VehicleFaultOptions()));
        InTransitDoorVerdict unknown = new(InTransitDoorState.SlotStateUnknown, 1, 1, Now);
        InTransitDoorVerdict locked = new(InTransitDoorState.ProvenLocked, 1, 2, Now);

        Assert.Equal(InTransitDoorAction.InGrace, readings.Observe("AGV-X", unknown, false, Now).Action);
        Assert.Equal(InTransitDoorAction.HandToFault, readings.Observe("AGV-X", unknown, false, Now.AddSeconds(3)).Action);
        // The fault was cleared by a person with the doors read locked again.
        Assert.Equal(InTransitDoorAction.Nothing, readings.Observe("AGV-X", locked with { ReceivedAt = Now.AddSeconds(60) }, false, Now.AddSeconds(60)).Action);
        Assert.Equal(
            InTransitDoorAction.InGrace,
            readings.Observe("AGV-X", unknown with { ReceivedAt = Now.AddSeconds(70) }, false, Now.AddSeconds(70)).Action);
        Assert.Equal(
            InTransitDoorAction.Nothing,
            readings.Observe("AGV-X", locked with { ReceivedAt = Now.AddSeconds(71) }, false, Now.AddSeconds(71)).Action);

        InTransitDoorReading third = readings.Observe("AGV-X", unknown with { ReceivedAt = Now.AddSeconds(80) }, false, Now.AddSeconds(80));
        Assert.Equal((InTransitDoorAction.HandToFault, InTransitDoorReadings.HandGraceLimitReached), (third.Action, third.HandedBecause));
    }

    /// <summary>限次按车数，不按单数：另一辆车的宽限不占这辆车的次数。</summary>
    [Fact]
    [Trait("Requirement", "REQ-0246")]
    public void TheLimitIsCountedPerVehicle()
    {
        InTransitDoorReadings readings = new(Microsoft.Extensions.Options.Options.Create(new VehicleFaultOptions()));
        for (int grace = 0; grace < 2; grace++)
        {
            DateTimeOffset at = Now.AddSeconds(grace * 10);
            readings.Observe("AGV-A", new(InTransitDoorState.SlotStateUnknown, 1, grace * 2 + 1, at), false, at);
            readings.Observe("AGV-A", new(InTransitDoorState.ProvenLocked, 1, grace * 2 + 2, at.AddSeconds(1)), false, at.AddSeconds(1));
        }

        Assert.Equal(
            InTransitDoorAction.InGrace,
            readings.Observe("AGV-B", new(InTransitDoorState.SlotStateUnknown, 1, 1, Now.AddSeconds(30)), false, Now.AddSeconds(30)).Action);
        Assert.Equal(
            InTransitDoorAction.HandToFault,
            readings.Observe("AGV-A", new(InTransitDoorState.SlotStateUnknown, 1, 9, Now.AddSeconds(30)), false, Now.AddSeconds(30)).Action);
    }

    // ---- 计时从收到时起、重启 ---------------------------------------------------------------------------------------------

    /// <summary>
    /// 计时从服务端收到那条「未知」起，不从第一次看到它的那一轮起：摘要收到 2.5 秒后才第一次跑到这一轮，再过 0.5 秒就满 3 秒。
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-0246")]
    public void TheGraceIsCountedFromWhenTheUnknownWasReceived()
    {
        InTransitDoorReadings readings = new(Microsoft.Extensions.Options.Options.Create(new VehicleFaultOptions()));
        InTransitDoorVerdict unknown = new(InTransitDoorState.SlotStateUnknown, 1, 1, Now);

        Assert.Equal(InTransitDoorAction.InGrace, readings.Observe("AGV-A", unknown, false, Now.AddSeconds(2.5)).Action);
        Assert.Equal(Now.AddSeconds(3), readings.NextDueAfter(Now.AddSeconds(2.5)));
        Assert.Equal(InTransitDoorAction.HandToFault, readings.Observe("AGV-A", unknown, false, Now.AddSeconds(3)).Action);
        Assert.Null(readings.NextDueAfter(Now.AddSeconds(3)));
    }

    /// <summary>
    /// 满 3 秒时就判，不等下一轮（CP-0017 修订项一说明 5）：宽限到期早于下一个 2 秒的定时，引擎循环在到期那一刻多跑一轮；
    /// 到期已过（那一轮已经跑过）或晚于下一个定时，照常等定时，循环不会空转。
    /// </summary>
    [Theory]
    [InlineData(500, 500)]
    [InlineData(1999, 1999)]
    [InlineData(2000, null)]
    [InlineData(0, null)]
    [InlineData(-300, null)]
    [Trait("Requirement", "REQ-0246")]
    public void TheWorkerRunsARoundWhenAGraceRunsOutBeforeTheNextTick(int dueInMilliseconds, int? expectedMilliseconds)
    {
        TimeSpan? early = JourneyRuntimeWorker.EarlyRoundAfter(
            Now.AddMilliseconds(dueInMilliseconds), Now, TimeSpan.FromSeconds(2));

        Assert.Equal(expectedMilliseconds is int ms ? TimeSpan.FromMilliseconds(ms) : null, early);
        Assert.Null(JourneyRuntimeWorker.EarlyRoundAfter(null, Now, TimeSpan.FromSeconds(2)));
    }

    /// <summary>
    /// 一台车的宽限已经过期、却再没被判过（宽限中断线、单变 HANG、到站），不挡另一台车的宽限按时到期（审查 M1）：引擎循环照样在另一台车
    /// 满 3 秒那一刻多跑一轮。
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-0246")]
    public void AnExpiredGraceOfOneVehicleDoesNotHideAnotherVehiclesDueTime()
    {
        InTransitDoorReadings readings = new(Microsoft.Extensions.Options.Options.Create(new VehicleFaultOptions()));
        Assert.Equal(
            InTransitDoorAction.InGrace,
            readings.Observe("AGV-A", new InTransitDoorVerdict(InTransitDoorState.SlotStateUnknown, 1, 1, Now), false, Now.AddSeconds(0.5)).Action);
        Assert.Equal(
            InTransitDoorAction.Nothing,
            readings.Observe("AGV-A", new InTransitDoorVerdict(InTransitDoorState.Stale, 1, 1, Now), false, Now.AddSeconds(5)).Action);
        Assert.Equal(
            InTransitDoorAction.InGrace,
            readings.Observe("AGV-B", new InTransitDoorVerdict(InTransitDoorState.SlotStateUnknown, 1, 1, Now.AddSeconds(10)), false, Now.AddSeconds(10.2)).Action);

        DateTimeOffset askedAt = Now.AddMilliseconds(12_200);
        Assert.Equal(Now.AddSeconds(13), readings.NextDueAfter(askedAt));
        Assert.Equal(
            TimeSpan.FromSeconds(0.8),
            JourneyRuntimeWorker.EarlyRoundAfter(readings.NextDueAfter(askedAt), askedAt, TimeSpan.FromSeconds(2)));
    }

    /// <summary>
    /// 服务端重启（单例换新、引擎重建）：起点不随进程清零到「重启后第一次看到」，而是重新取那条摘要的收到时刻，所以宽限不被拉长——
    /// 收到后第 1 秒重启，第 3 秒照样交故障模型。
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-0246")]
    public async Task ARestartDoesNotLengthenTheGrace()
    {
        await using RuntimeFixture fixture = await DrivingAsync("behind-gate");
        string upperId = (await fixture.RuntimeAsync()).GateUpperId!;

        await ReportUnknownAsync(fixture);
        await DriveOneRoundAsync(fixture);
        fixture.DoorReadings = new InTransitDoorReadings(Microsoft.Extensions.Options.Options.Create(new VehicleFaultOptions()));
        await fixture.RecreateEngineAsync();
        await DriveOneRoundAsync(fixture);
        await AssertNothingRaisedAsync(fixture);
        await DriveOneRoundAsync(fixture);

        await AssertHeldThenStoppedAsync(fixture, upperId);
    }

    /// <summary>
    /// 重启不清零次数（审查 M2，CP-0017 第六节第 1 条要偏保守）：次数从收件箱里本车 10 分钟内收到的「未知」重建——每一段连续的「未知」算开始过
    /// 一次宽限。重启前已用满 2 次，重启后的「未知」同一轮就交故障模型。
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-0246")]
    public async Task ARestartRebuildsHowManyGracesWereGivenFromTheInbox()
    {
        await using RuntimeFixture fixture = await DrivingAsync("behind-gate");
        string upperId = (await fixture.RuntimeAsync()).GateUpperId!;
        await GraceThatRecoversAsync(fixture);
        await GraceThatRecoversAsync(fixture);
        fixture.DoorReadings = new InTransitDoorReadings(Microsoft.Extensions.Options.Options.Create(new VehicleFaultOptions()));
        await fixture.RecreateEngineAsync();

        await ReportUnknownAsync(fixture);
        await DriveOneRoundAsync(fixture);

        await AssertHeldThenStoppedAsync(fixture, upperId);
    }

    /// <summary>重建只数窗口里的：重启前那两次都已超过 10 分钟，重启后照样有宽限。</summary>
    [Fact]
    [Trait("Requirement", "REQ-0246")]
    public async Task ARestartCountsOnlyTheGracesInsideTheWindow()
    {
        await using RuntimeFixture fixture = await DrivingAsync("behind-gate");
        await GraceThatRecoversAsync(fixture);
        await GraceThatRecoversAsync(fixture);
        fixture.Clock.Advance(TimeSpan.FromMinutes(11));
        fixture.DoorReadings = new InTransitDoorReadings(Microsoft.Extensions.Options.Options.Create(new VehicleFaultOptions()));
        await fixture.RecreateEngineAsync();

        await ReportUnknownAsync(fixture);
        await DriveOneRoundAsync(fixture);
        await AssertNothingRaisedAsync(fixture);
    }

    /// <summary>宽限设成 0 就是 CP-0017 之前的行为：「未知」同一轮交故障模型。</summary>
    [Fact]
    [Trait("Requirement", "REQ-0246")]
    public async Task AZeroGraceHandsAnUnknownOnAtOnce()
    {
        await using RuntimeFixture fixture = await DrivingAsync("behind-gate");
        string upperId = (await fixture.RuntimeAsync()).GateUpperId!;
        fixture.DoorReadings = new InTransitDoorReadings(Microsoft.Extensions.Options.Options.Create(
            new VehicleFaultOptions { DoorUnknownGrace = TimeSpan.Zero }));
        await fixture.RecreateEngineAsync();

        await ReportUnknownAsync(fixture);
        await DriveOneRoundAsync(fixture);

        await AssertHeldThenStoppedAsync(fixture, upperId);
    }

    /// <summary>配置校验：负数与过大的值拒收，0 收（0 即不宽限）。上限：宽限 10 秒、次数 10、窗口 24 小时、自动解除前 2 秒到 5 分钟。</summary>
    [Theory]
    [InlineData(-1, 2, 10, 10, false)]
    [InlineData(11, 2, 10, 10, false)]
    [InlineData(3, -1, 10, 10, false)]
    [InlineData(3, 11, 10, 10, false)]
    [InlineData(3, 2, 0, 10, false)]
    [InlineData(3, 2, 1441, 10, false)]
    [InlineData(3, 2, 10, 1, false)]
    [InlineData(3, 2, 10, 301, false)]
    [InlineData(0, 0, 10, 2, true)]
    [InlineData(3, 2, 10, 10, true)]
    [InlineData(10, 10, 1440, 300, true)]
    public void TheGraceSettingsAreBounded(int graceSeconds, int limit, int windowMinutes, int restoredSeconds, bool valid)
    {
        VehicleFaultOptions options = new()
        {
            DoorUnknownGrace = TimeSpan.FromSeconds(graceSeconds),
            DoorUnknownGraceLimit = limit,
            DoorUnknownGraceWindow = TimeSpan.FromMinutes(windowMinutes),
            DoorReadingRestoredFor = TimeSpan.FromSeconds(restoredSeconds),
        };

        Assert.Equal(valid, new VehicleFaultOptionsValidator().Validate(null, options).Succeeded);
    }

    /// <summary>默认值就是 CP-0017 定稿的取值。</summary>
    [Fact]
    public void TheDefaultsAreTheValuesTheUserChose()
    {
        VehicleFaultOptions options = new();
        Assert.Equal(
            (TimeSpan.FromSeconds(3), 2, TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(10)),
            (options.DoorUnknownGrace, options.DoorUnknownGraceLimit, options.DoorUnknownGraceWindow, options.DoorReadingRestoredFor));
    }

    // ---- helpers -------------------------------------------------------------------------------------------------

    /// <summary>关卡段有货、行驶中、报不出站点，门锁锁着；闸门后（真车载端在途时）或就绪（合成车载端）。</summary>
    private static async Task<RuntimeFixture> DrivingAsync(string side)
    {
        RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(FirstDemandId, FirstSublot, createdAt: Now.AddMinutes(-10)));
        fixture.BoxCounts.Set(FirstSublot, 7);
        await fixture.AdvanceToGateArrivalAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingGateArrival, (await fixture.RuntimeAsync()).Stage);
        fixture.Context.ChangeTracker.Clear();
        if (side == "behind-gate")
        {
            await OwnOrderRebuildTests.DropSessionOnOwnOrderAsync(fixture);
        }
        fixture.Riot.MovementState = "MT_RUNNING";
        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { CurrentStationId = 0 };
        await ReportLockedAsync(fixture);
        await DriveOneRoundAsync(fixture);
        await AssertNothingRaisedAsync(fixture);
        return fixture;
    }

    /// <summary>一次在 1 秒内恢复了的宽限。</summary>
    private static async Task GraceThatRecoversAsync(RuntimeFixture fixture)
    {
        await ReportUnknownAsync(fixture);
        await DriveOneRoundAsync(fixture);
        await ReportLockedAsync(fixture);
        await DriveOneRoundAsync(fixture);
        await AssertNothingRaisedAsync(fixture);
    }

    /// <summary>IO 读不到时真车载端报的样子（真装置实测，control-server#335）：带 <c>LOCK_NOT_CLOSED</c>、<c>allTargetSlotsLocked=false</c>。</summary>
    internal static Task ReportUnknownAsync(RuntimeFixture fixture) =>
        fixture.ReportSafetySummaryAsync(
            allTargetSlotsLocked: false,
            unknownPresent: true,
            ["SLOT_STATE_UNKNOWN", "LOCK_NOT_CLOSED", "UNLOCK_OUTPUT_NOT_RESET", "ACTION_NOT_ALLOWED_IN_STATE"],
            allUnlockOutputsReset: false);

    internal static Task ReportNotLockedAsync(RuntimeFixture fixture) =>
        fixture.ReportSafetySummaryAsync(
            allTargetSlotsLocked: false, unknownPresent: false, ["LOCK_NOT_CLOSED", "ACTION_NOT_ALLOWED_IN_STATE"]);

    internal static Task ReportLockedAsync(RuntimeFixture fixture) =>
        fixture.ReportSafetySummaryAsync(allTargetSlotsLocked: true, unknownPresent: false, ["ACTION_NOT_ALLOWED_IN_STATE"]);

    /// <summary>钟走一秒，车载端说一句话，跑一轮。</summary>
    internal static async Task DriveOneRoundAsync(RuntimeFixture fixture)
    {
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        await fixture.HearFromPeerAsync();
        fixture.Context.ChangeTracker.Clear();
        await fixture.Engine.ExecuteOnceAsync(Token);
        fixture.Context.ChangeTracker.Clear();
    }

    /// <summary>没有故障事实、没有 Hold、没有急停、没有 Cancel。</summary>
    private static async Task AssertNothingRaisedAsync(RuntimeFixture fixture)
    {
        await using ControlServerDbContext reading = new(fixture.DbOptionsForTests);
        Assert.Empty(await reading.VehicleFaultStates.AsNoTracking()
            .Where(row => row.Level != VehicleFaultLevel.None).ToArrayAsync(Token));
        Assert.Empty(await reading.RiotOrderCommandAudit.AsNoTracking()
            .Where(row => row.CommandType == RiotCommandTypeNames.OrderHold ||
                          row.CommandType == RiotCommandTypeNames.TriggerEmergency ||
                          row.CommandType == RiotCommandTypeNames.CancelOrder)
            .ToArrayAsync(Token));
    }

    /// <summary>一条门锁症状的故障、一次对本单的 Hold、一次急停，Hold 先到 RIoT。</summary>
    private static async Task AssertHeldThenStoppedAsync(RuntimeFixture fixture, string upperId)
    {
        await using ControlServerDbContext reading = new(fixture.DbOptionsForTests);
        VehicleFaultStateRow fault = Assert.Single(await reading.VehicleFaultStates.AsNoTracking().ToArrayAsync(Token));
        Assert.Equal(
            (VehicleFaultLevel.SuspectedBlocked, InTransitDoorLockFaultTests.DoorSymptom, true),
            (fault.Level, fault.EvidenceCode, fault.EscalatedAt is not null));
        RiotOrderCommandAuditRow[] audit = await reading.RiotOrderCommandAudit.AsNoTracking().ToArrayAsync(Token);
        Assert.Equal(upperId, Assert.Single(audit, row => row.CommandType == RiotCommandTypeNames.OrderHold).TargetUpperId);
        Assert.Single(audit, row => row.CommandType == RiotCommandTypeNames.TriggerEmergency);
        int holdSent = fixture.RiotCommandsSent.IndexOf(RiotCommandTypeNames.OrderHold);
        int stopSent = fixture.RiotCommandsSent.IndexOf(RiotCommandTypeNames.TriggerEmergency);
        Assert.True(holdSent >= 0 && stopSent > holdSent, string.Join(" -> ", fixture.RiotCommandsSent));
    }
}
