using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Faults;
using ControlServer.Host.Runtime.Release;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using static ControlServer.Tests.Batch7StopDrivenAdvanceDriver;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;

namespace ControlServer.Tests;

/// <summary>
/// 自动重建建单之前先判电量（control-server#770）：10-18 生产上 agv01 的单在 RIoT 被取消，服务端按 control-server#318 自动重建，
/// 建单时电量 9%、强制充电线 40%，车照样被派回取货站。新派车受 <c>BatteryEligibility</c> 管，重建不受。
/// </summary>
/// <remarks>
/// <para>
/// <b>闸在哪里。</b>重建有五个入口，都只记一条重建记录；真正建单只有 <c>AdvanceOwnOrderRebuildAsync</c> 一处，建单前的检查都在
/// <c>HeldBeforeCreateAsync</c>。电量闸放在那里，五个入口各走一遍（本类「入口」一节）。
/// </para>
/// <para>
/// <b>判法。</b>按这趟旅程冻结的那一版充电策略（REQ-0282），读法与 control-server#709 的待送点缺电同一个：读完再取时钟、过期或读不到算不知道。
/// 线用 <c>BatteryEligibility.IsMandatoryCharge</c>，与派车、空闲返回、充电分配同一个函数。不知道就不建、等着（与新派车「不知道就不派」一致），
/// 也不释放（释放服务的规则本来就不拿会自己恢复的事实释放）。
/// </para>
/// <para>
/// <b>车上没货、低于强制线：</b>不建，记录停在 <c>MANDATORY_CHARGE_REQUIRED</c>，旅程码 <c>OWN_ORDER_REBUILD_MANDATORY_CHARGE</c>；释放服务把待装需求
/// 放回积压（不抑制），旅程收尾，车成一辆没有用途的低电车，交给充电分配。与车不再合格（审查 S1）同一个交接方式。
/// </para>
/// <para>
/// <b>人工重建不拦</b>：人点「重建」是有意为之，照常建，记一条告警。
/// </para>
/// </remarks>
public sealed class OwnOrderRebuildBatteryGateTests
{
    private const int FlatBattery = 9;

    /// <summary>What the warning for a person's rebuild on a flat battery says.</summary>
    private const string PersonsRebuildOnFlatBattery = "asked for by a person";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ---- 来源一：单在 RIoT 被取消，车上没货 ------------------------------------------------------------------------

    /// <summary>
    /// 10-18 的现场：开往取货站的单在 RIoT 被取消，延迟到点时电量 9%（线 40%）。不发重建单；记录停在 <c>MANDATORY_CHARGE_REQUIRED</c>，
    /// 旅程码 <c>OWN_ORDER_REBUILD_MANDATORY_CHARGE</c>；释放服务把需求放回积压——仍是已受理、没有抑制、等待年龄不重置，旅程收尾、用途全部放掉，
    /// 不发取消（那张单已经终结）。
    /// </summary>
    /// <remarks>
    /// 两种时序：取消被读到时已经低电；以及重建已经排队、还没到点时才跌破线（票面「并发」）——到点那一轮重新判。
    /// 39 是线下一格：等于线不算强制充电（<see cref="ARebuildAtTheMandatoryLineIsMadeAsBefore"/>）。
    /// </remarks>
    [Theory]
    [InlineData("flat-before-cancel", FlatBattery)]
    [InlineData("flat-after-staged", FlatBattery)]
    [InlineData("flat-after-staged", 39)]
    [Trait("Requirement", "REQ-0360")]
    public async Task AFlatVehicleWithNothingOnBoardIsNotSentBackAndItsDemandGoesBackToTheBacklog(string when, int battery)
    {
        await using RuntimeFixture fixture = await StoppedRebuildExitTests.DispatchedToPickupAsync();
        JourneyRuntimeRow before = await fixture.RuntimeAsync(FirstDemandId);
        DateTimeOffset firstSeen = (await fixture.BacklogAsync(FirstDemandId)).FirstSeenAt;
        if (when == "flat-before-cancel")
        {
            Flat(fixture, battery);
        }
        fixture.Riot.CancelOrder(before.PickupUpperId);
        await TickAndRunAsync(fixture);
        Assert.Equal("ORDER_ENDED_WITHOUT_ARRIVAL", (await fixture.RuntimeAsync(FirstDemandId)).BlockReasonCode);
        if (when == "flat-after-staged")
        {
            Flat(fixture, battery);
        }

        await OwnOrderRebuildTests.PassTheDelayAsync(fixture);
        fixture.Context.ChangeTracker.Clear();

        await AssertStoppedForChargeAsync(fixture, before.PickupUpperId, battery);
        await AssertReleasedAsync(fixture, firstSeen);
    }

    /// <summary>电量正好在线上：不算强制充电，照旧重建（反向对照，闸不能一律不建）。</summary>
    [Fact]
    public async Task ARebuildAtTheMandatoryLineIsMadeAsBefore()
    {
        await using RuntimeFixture fixture = await StoppedRebuildExitTests.DispatchedToPickupAsync();
        JourneyRuntimeRow before = await fixture.RuntimeAsync(FirstDemandId);
        JourneyStopRow[] stopsBefore = await OwnOrderRebuildTests.StopsAsync(fixture, before.JourneyId);
        fixture.Riot.CancelOrder(before.PickupUpperId);
        await TickAndRunAsync(fixture);
        Flat(fixture, 40);

        await OwnOrderRebuildTests.PassTheDelayAsync(fixture);

        Assert.Equal(2, fixture.Riot.CreateCount("TO_PICKUP"));
        await OwnOrderRebuildTests.AssertRebuiltAsync(
            fixture, before, stopsBefore, stopsBefore.Single(stop => stop.StopRole == JourneyStopRoles.Pickup));
        AssertNoPersonsRebuildWarning(fixture);
    }

    /// <summary>
    /// 在强制线以上、但扣掉一趟耗电保不住余量（新派车会答 <c>BATTERY_POLICY_NOT_SATISFIED</c>、投影 <c>LOW</c>）：照建。重建续做的是已经派出的活，
    /// 只有强制线是「什么都不发」的那条（REQ-0281）。审查 S-6：把闸放宽成「不是 SUFFICIENT 就不建」，这一条红。
    /// </summary>
    [Fact]
    public async Task ARebuildAboveTheLineWithoutThePostTaskMarginIsMadeAsBefore()
    {
        await using RuntimeFixture fixture = await StoppedRebuildExitTests.DispatchedToPickupAsync();
        JourneyRuntimeRow before = await fixture.RuntimeAsync(FirstDemandId);
        fixture.Riot.CancelOrder(before.PickupUpperId);
        await TickAndRunAsync(fixture);
        // The same frozen version 1, now read with a consumption estimate: 45 - 10 = 35, under the margin of 40.
        fixture.ChargingPolicy = TestChargingPolicies.AllApprovedWith(
            TestChargingPolicies.ContentAt(40) with { EstimatedTaskConsumptionPercent = 10 });
        await fixture.RecreateEngineAsync();
        Flat(fixture, 45);

        await OwnOrderRebuildTests.PassTheDelayAsync(fixture);
        fixture.Context.ChangeTracker.Clear();

        Assert.Equal(2, fixture.Riot.CreateCount("TO_PICKUP"));
        Assert.Equal(OwnOrderRebuildStates.Rebuilt, (await RebuildAsync(fixture, before.PickupUpperId)).State);
        AssertNoPersonsRebuildWarning(fixture);
    }

    /// <summary>
    /// 读数过期（RIoT 给的观测时刻比服务端读完时早过 <c>MaximumEvidenceAge</c>）：算不知道，等着、不建、不释放，与读不到同一处理。
    /// 审查 S-6：把「新鲜」那一项拿掉，过期的读数会被当成知道。
    /// </summary>
    [Fact]
    public async Task ARebuildWaitsWhileTheBatteryReadIsStale()
    {
        await using RuntimeFixture fixture = await StoppedRebuildExitTests.DispatchedToPickupAsync();
        JourneyRuntimeRow before = await fixture.RuntimeAsync(FirstDemandId);
        fixture.Riot.CancelOrder(before.PickupUpperId);
        await TickAndRunAsync(fixture);
        fixture.Riot.VehicleObservedAtLag = fixture.Options.MaximumEvidenceAge + TimeSpan.FromSeconds(1);

        await OwnOrderRebuildTests.PassTheDelayAsync(fixture);
        fixture.Context.ChangeTracker.Clear();

        Assert.Equal(1, fixture.Riot.CreateCount("TO_PICKUP"));
        Assert.Equal("OWN_ORDER_REBUILD_WAITING_VEHICLE", (await fixture.RuntimeAsync(FirstDemandId)).BlockReasonCode);
        OwnOrderRebuildRow waiting = await RebuildAsync(fixture, before.PickupUpperId);
        Assert.Equal((OwnOrderRebuildStates.Pending, "BATTERY_FACT_UNKNOWN"), (waiting.State, waiting.WaitingReason));
        await ReleaseOnceAsync(fixture);
        await AssertNotReleasedAsync(fixture);
    }

    /// <summary>
    /// 电量读不到（RIoT 没给电量）：与新派车一样不派——不建，记录仍 PENDING、等待原因 <c>BATTERY_FACT_UNKNOWN</c>，旅程码
    /// <c>OWN_ORDER_REBUILD_WAITING_VEHICLE</c>；释放服务不释放（说不清不是释放的理由）。读到正常电量之后照常重建。
    /// </summary>
    [Fact]
    public async Task ARebuildWaitsWhileTheBatteryCannotBeReadAndIsMadeOnceItCan()
    {
        await using RuntimeFixture fixture = await StoppedRebuildExitTests.DispatchedToPickupAsync();
        JourneyRuntimeRow before = await fixture.RuntimeAsync(FirstDemandId);
        fixture.Riot.CancelOrder(before.PickupUpperId);
        await TickAndRunAsync(fixture);
        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { BatteryPercent = null };

        await OwnOrderRebuildTests.PassTheDelayAsync(fixture);
        fixture.Context.ChangeTracker.Clear();

        Assert.Equal(1, fixture.Riot.CreateCount("TO_PICKUP"));
        Assert.Equal("OWN_ORDER_REBUILD_WAITING_VEHICLE", (await fixture.RuntimeAsync(FirstDemandId)).BlockReasonCode);
        OwnOrderRebuildRow waiting = await RebuildAsync(fixture, before.PickupUpperId);
        Assert.Equal((OwnOrderRebuildStates.Pending, "BATTERY_FACT_UNKNOWN"), (waiting.State, waiting.WaitingReason));
        await ReleaseOnceAsync(fixture);
        await AssertNotReleasedAsync(fixture);

        Flat(fixture, 80);
        await fixture.HearFromPeerAsync();
        await TickAndRunAsync(fixture);

        Assert.Equal(2, fixture.Riot.CreateCount("TO_PICKUP"));
        Assert.Equal(OwnOrderRebuildStates.Rebuilt, (await RebuildAsync(fixture, before.PickupUpperId)).State);
    }

    // ---- 车上有货 -----------------------------------------------------------------------------------------------

    /// <summary>
    /// 装着货开往卸货站，单在 RIoT 被取消，重建到点时低电：不建、不释放、车不动——记录停在 <c>MANDATORY_CHARGE_WITH_CARGO_ON_BOARD</c>，
    /// 旅程码 <c>OWN_ORDER_REBUILD_STOPPED</c>，告警 2173；交 control-server#345 的人工出口（人点「重建」照常受理）。
    /// </summary>
    /// <remarks>
    /// 用户 2026-10-19 定的最终做法是先充电再续送（control-server#782）；那一支落地之前本票按「停下等人」兜底（调度裁定）。车上的货不放回：
    /// 释放服务只放待装的，且只认没货那条停止原因。
    /// </remarks>
    [Fact]
    [Trait("Requirement", "REQ-0360")]
    public async Task AFlatVehicleWithCargoOnBoardIsNotSentOnAndWaitsForAPerson()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(FirstDemandId, FirstSublot, createdAt: Now.AddMinutes(-10)));
        fixture.BoxCounts.Set(FirstSublot, 7);
        await fixture.AdvanceToGateArrivalAsync();
        fixture.Riot.MovementState = "MT_FINISHED";
        fixture.Context.ChangeTracker.Clear();
        int gateCreates = fixture.Riot.CreateCount("TO_GATE");
        string gateUpperId = (await fixture.RuntimeAsync(FirstDemandId)).GateUpperId!;
        fixture.Riot.CancelOrder(gateUpperId);
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        await fixture.HearFromPeerAsync();
        await fixture.Engine.ExecuteOnceAsync(Token);
        Flat(fixture, FlatBattery);

        // The cargo shown in its slots after the rebuild fell due: without the battery gate this round would create.
        await OwnOrderRebuildTests.PassTheDelayAsync(fixture);
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        await fixture.AddCargoSnapshotAsync(fixture.Clock.GetUtcNow());
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        await fixture.HearFromPeerAsync();
        fixture.Context.ChangeTracker.Clear();
        await fixture.Engine.ExecuteOnceAsync(Token);
        fixture.Context.ChangeTracker.Clear();

        Assert.Equal(gateCreates, fixture.Riot.CreateCount("TO_GATE"));
        Assert.Equal("OWN_ORDER_REBUILD_STOPPED", (await fixture.RuntimeAsync(FirstDemandId)).BlockReasonCode);
        OwnOrderRebuildRow stopped = await RebuildAsync(fixture, gateUpperId);
        Assert.Equal(
            (OwnOrderRebuildStates.Stopped, "MANDATORY_CHARGE_WITH_CARGO_ON_BOARD", $"BATTERY_{FlatBattery}_BELOW_40"),
            (stopped.State, stopped.StoppedReason, stopped.WaitingReason));
        var alarm = Assert.Single(fixture.EngineLog.Entries, entry =>
            entry.Message.Contains("is not rebuilt", StringComparison.Ordinal) &&
            entry.Message.Contains("MANDATORY_CHARGE_WITH_CARGO_ON_BOARD", StringComparison.Ordinal));
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Error, alarm.Level);
        await ReleaseOnceAsync(fixture);
        await AssertNotReleasedAsync(fixture);
        AssertNoPersonsRebuildWarning(fixture);

        // A person's rebuild goes ahead on the same flat battery once the cargo is shown again (REQ-0362), warned as 2175.
        // Review S-6: taking the person's exemption out of the cargo branch stops it again here.
        Assert.Equal(
            VehicleFaultRecoveryOutcome.RebuildRequested,
            (await VehicleFaultRecoveryTests.Service(fixture, new(fixture))
                .RecoverAsync(StoppedRebuildExitTests.Rebuild(fixture), Token)).Outcome);
        fixture.Context.ChangeTracker.Clear();
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        await fixture.AddCargoSnapshotAsync(fixture.Clock.GetUtcNow());
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        await fixture.HearFromPeerAsync();
        fixture.Context.ChangeTracker.Clear();
        await fixture.Engine.ExecuteOnceAsync(Token);
        fixture.Context.ChangeTracker.Clear();

        Assert.Equal(gateCreates + 1, fixture.Riot.CreateCount("TO_GATE"));
        AssertPersonsRebuildWarning(fixture);
    }

    /// <summary>
    /// 车上没货、低于强制线，但当前站不是取货站（造法：装着货开往卸货站时单被取消，之后那条需求被记成已卸）：既不是「没货的取货站」也不是「有货」，
    /// 交不了释放服务（它只放待装的），也不能照建——停下等人，记录 <c>MANDATORY_CHARGE_NOT_AT_PICKUP</c>，旅程码 <c>OWN_ORDER_REBUILD_STOPPED</c>
    /// （审查 S-5）。
    /// </summary>
    [Fact]
    public async Task AFlatEmptyVehicleWhoseStopIsNotAPickupStopsForAPerson()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(FirstDemandId, FirstSublot, createdAt: Now.AddMinutes(-10)));
        fixture.BoxCounts.Set(FirstSublot, 7);
        await fixture.AdvanceToGateArrivalAsync();
        fixture.Riot.MovementState = "MT_FINISHED";
        fixture.Context.ChangeTracker.Clear();
        int gateCreates = fixture.Riot.CreateCount("TO_GATE");
        string gateUpperId = (await fixture.RuntimeAsync(FirstDemandId)).GateUpperId!;
        fixture.Riot.CancelOrder(gateUpperId);
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        await fixture.HearFromPeerAsync();
        await fixture.Engine.ExecuteOnceAsync(Token);
        fixture.Context.ChangeTracker.Clear();
        Assert.Equal(OwnOrderRebuildStates.Pending, (await RebuildAsync(fixture, gateUpperId)).State);
        await using (ControlServerDbContext writing = new(fixture.DbOptionsForTests))
        {
            JourneyDemandRow membership = await writing.Set<JourneyDemandRow>()
                .SingleAsync(row => row.DemandId == FirstDemandId, Token);
            membership.Status = JourneyDemandStatuses.Unloaded;
            await writing.SaveChangesAsync(Token);
        }
        Flat(fixture, FlatBattery);

        await OwnOrderRebuildTests.PassTheDelayAsync(fixture);
        fixture.Context.ChangeTracker.Clear();

        Assert.Equal(gateCreates, fixture.Riot.CreateCount("TO_GATE"));
        Assert.Equal("OWN_ORDER_REBUILD_STOPPED", (await fixture.RuntimeAsync(FirstDemandId)).BlockReasonCode);
        OwnOrderRebuildRow stopped = await RebuildAsync(fixture, gateUpperId);
        Assert.Equal(
            (OwnOrderRebuildStates.Stopped, "MANDATORY_CHARGE_NOT_AT_PICKUP", $"BATTERY_{FlatBattery}_BELOW_40"),
            (stopped.State, stopped.StoppedReason, stopped.WaitingReason));
    }

    // ---- 重放与重复 ---------------------------------------------------------------------------------------------

    /// <summary>
    /// 重启之后仍在排队的重建按新闸判：取消记下、引擎重建（重启）、之后才低电，到点不建，停下交释放。另一个崩溃点「已决定、未建单」
    /// （记录 ORDERING、停靠已指向新单、一次创建都没发出）重启后同样先过电量：不建，停下；释放服务认出那张新单从没发出过，照常释放、不发取消。
    /// </summary>
    [Theory]
    [InlineData("pending")]
    [InlineData("decided-not-created")]
    [Trait("Requirement", "REQ-0360")]
    public async Task ARebuildStillQueuedAcrossARestartIsJudgedByTheBatteryGate(string crashPoint)
    {
        await using RuntimeFixture fixture = await StoppedRebuildExitTests.DispatchedToPickupAsync();
        JourneyRuntimeRow before = await fixture.RuntimeAsync(FirstDemandId);
        DateTimeOffset firstSeen = (await fixture.BacklogAsync(FirstDemandId)).FirstSeenAt;
        fixture.Riot.CancelOrder(before.PickupUpperId);
        await TickAndRunAsync(fixture);
        if (crashPoint == "decided-not-created")
        {
            fixture.Riot.CrashOnNextReconcileOf = (await RebuildAsync(fixture, before.PickupUpperId)).NewUpperId;
            fixture.Clock.Advance(fixture.Options.OwnOrderRebuildDelay);
            await fixture.HearFromPeerAsync();
            await Assert.ThrowsAsync<IOException>(() => fixture.Engine.ExecuteOnceAsync(Token));
            Assert.Equal(OwnOrderRebuildStates.Ordering, (await RebuildAsync(fixture, before.PickupUpperId)).State);
        }

        await fixture.RecreateEngineAsync();
        Flat(fixture, FlatBattery);
        await OwnOrderRebuildTests.PassTheDelayAsync(fixture);
        fixture.Context.ChangeTracker.Clear();

        await AssertStoppedForChargeAsync(fixture, before.PickupUpperId, FlatBattery);
        await AssertReleasedAsync(fixture, firstSeen);
    }

    /// <summary>
    /// 同一条需求第二次重建：第一次时电量正常、建成了；新单又在窗口之外被取消，第二次重建到点时车已低电——第二次同样过闸，不建，停下交释放。
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-0361")]
    public async Task TheSecondRebuildOfTheSameDemandIsJudgedByTheBatteryGateToo()
    {
        await using RuntimeFixture fixture = await StoppedRebuildExitTests.DispatchedToPickupAsync();
        fixture.Options.OwnOrderRebuildRepeatWindow = TimeSpan.FromSeconds(10);
        JourneyRuntimeRow before = await fixture.RuntimeAsync(FirstDemandId);
        DateTimeOffset firstSeen = (await fixture.BacklogAsync(FirstDemandId)).FirstSeenAt;
        fixture.Riot.CancelOrder(before.PickupUpperId);
        await TickAndRunAsync(fixture);
        await OwnOrderRebuildTests.PassTheDelayAsync(fixture);
        fixture.Context.ChangeTracker.Clear();
        OwnOrderRebuildRow first = await RebuildAsync(fixture, before.PickupUpperId);
        Assert.Equal(OwnOrderRebuildStates.Rebuilt, first.State);
        Assert.Equal(2, fixture.Riot.CreateCount("TO_PICKUP"));

        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        fixture.Riot.CancelOrder(first.NewUpperId);
        await fixture.HearFromPeerAsync();
        await TickAndRunAsync(fixture);
        Assert.Equal(OwnOrderRebuildStates.Pending, (await RebuildAsync(fixture, first.NewUpperId)).State);
        Flat(fixture, FlatBattery);
        await OwnOrderRebuildTests.PassTheDelayAsync(fixture);
        fixture.Context.ChangeTracker.Clear();

        await AssertStoppedForChargeAsync(fixture, first.NewUpperId, FlatBattery, pickupCreates: 2);
        await AssertReleasedAsync(fixture, firstSeen);
    }

    // ---- 入口：单从没出现过、故障清除、人工 ------------------------------------------------------------------------

    /// <summary>入口二（引擎判「单从没出现过」，control-server#673）：延迟到点时低电，不建，停下交释放。</summary>
    [Fact]
    [Trait("Requirement", "REQ-0360")]
    public async Task ANeverAppearedOrderIsNotRebuiltOnAFlatBattery()
    {
        await using RuntimeFixture fixture = await OwnOrderNeverAppearedTests.PickupCreateRefusedAsync();
        string upperId = (await CurrentStopAsync(fixture, FirstDemandId)).UpperId;
        DateTimeOffset firstSeen = (await fixture.BacklogAsync(FirstDemandId)).FirstSeenAt;
        await OwnOrderNeverAppearedTests.RunAfterAsync(fixture, TimeSpan.FromSeconds(130));
        Assert.Equal("OWN_ORDER_NEVER_APPEARED", (await fixture.RuntimeAsync(FirstDemandId)).BlockReasonCode);
        Flat(fixture, FlatBattery);

        await OwnOrderRebuildTests.PassTheDelayAsync(fixture);
        fixture.Context.ChangeTracker.Clear();

        await AssertStoppedForChargeAsync(fixture, upperId, FlatBattery);
        await AssertReleasedAsync(fixture, firstSeen);
    }

    /// <summary>
    /// 入口四（人清除故障，车上没货，control-server#318）：清除照常排重建，延迟到点时低电，不建，停下交释放。故障已清，释放服务不因故障拒绝。
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-0360")]
    public async Task AClearedFaultOnAnEmptyFlatVehicleIsNotRebuilt()
    {
        await using RuntimeFixture fixture = await VehicleFaultRecoveryTests.FaultedOnTheWayToPickupAsync();
        string upperId = (await fixture.RuntimeAsync(FirstDemandId)).PickupUpperId;
        DateTimeOffset firstSeen = (await fixture.BacklogAsync(FirstDemandId)).FirstSeenAt;
        Assert.Equal(
            VehicleFaultRecoveryDispositions.RebuildScheduled,
            (await VehicleFaultRecoveryTests.Service(fixture, new(fixture))
                .RecoverAsync(VehicleFaultRecoveryTests.Clear(fixture), Token)).Disposition);
        fixture.Context.ChangeTracker.Clear();
        Flat(fixture, FlatBattery);

        await OwnOrderRebuildTests.PassTheDelayAsync(fixture);
        fixture.Context.ChangeTracker.Clear();

        await AssertStoppedForChargeAsync(fixture, upperId, FlatBattery);
        await AssertReleasedAsync(fixture, firstSeen);
    }

    /// <summary>
    /// 入口五（人对停住的重建点「重建」，control-server#345）与入口三（人声明单从没建成，之后点「重建」，control-server#673）：人是有意为之，
    /// 低电也照常建，引擎记一条告警写明电量与线。
    /// </summary>
    [Theory]
    [InlineData("third-guard")]
    [InlineData("declared-never-appeared")]
    public async Task APersonsRebuildGoesAheadOnAFlatBatteryWithAWarning(string stoppedBy)
    {
        RuntimeFixture fixture;
        int createsBefore;
        if (stoppedBy == "third-guard")
        {
            fixture = await StoppedRebuildExitTests.StoppedByTheThirdGuardAsync();
            createsBefore = 2;
        }
        else
        {
            fixture = await OwnOrderNeverAppearedTests.PickupCreateRefusedAsync();
            fixture.Riot.ListingComplete = false;
            await OwnOrderNeverAppearedTests.RunAfterAsync(
                fixture, fixture.Options.OwnOrderRebuildRepeatWindow + TimeSpan.FromSeconds(1));
            Assert.Equal(
                VehicleFaultRecoveryOutcome.NeverAppearedDeclared,
                (await VehicleFaultRecoveryTests.Service(fixture, new(fixture))
                    .RecoverAsync(OwnOrderNeverAppearedTests.Declare(fixture), Token)).Outcome);
            fixture.Context.ChangeTracker.Clear();
            createsBefore = 1;
        }

        await using (fixture)
        {
            Assert.Equal("OWN_ORDER_REBUILD_STOPPED", (await fixture.RuntimeAsync(FirstDemandId)).BlockReasonCode);
            Flat(fixture, FlatBattery);

            Assert.Equal(
                VehicleFaultRecoveryOutcome.RebuildRequested,
                (await VehicleFaultRecoveryTests.Service(fixture, new(fixture))
                    .RecoverAsync(StoppedRebuildExitTests.Rebuild(fixture), Token)).Outcome);
            fixture.Context.ChangeTracker.Clear();
            await fixture.HearFromPeerAsync();
            await fixture.Engine.ExecuteOnceAsync(Token);
            fixture.Context.ChangeTracker.Clear();

            Assert.Equal(createsBefore + 1, fixture.Riot.CreateCount("TO_PICKUP"));
            Assert.Null((await fixture.RuntimeAsync(FirstDemandId)).BlockReasonCode);
            var warning = Assert.Single(fixture.EngineLog.Entries, entry =>
                entry.Message.Contains(PersonsRebuildOnFlatBattery, StringComparison.Ordinal));
            Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Warning, warning.Level);
            Assert.Contains($"{FlatBattery}%", warning.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// 护栏三在没货的低电车上停住（重复窗口内第二次被取消，<c>REBUILT_ORDER_ENDED_AGAIN_WITHIN_WINDOW</c> 在记下取消那一刻就写好了，走不到建单前的
    /// 电量闸）：引擎照电量闸的交接改记 <c>MANDATORY_CHARGE_REQUIRED</c>，释放服务把需求放回、车去充电——否则车占着用途进不了充电队，需求也一直挂着
    /// （审查 S-2）。
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-0361")]
    public async Task TheThirdGuardOnAFlatEmptyVehicleHandsTheDemandBackToo()
    {
        await using RuntimeFixture fixture = await StoppedRebuildExitTests.DispatchedToPickupAsync();
        JourneyRuntimeRow before = await fixture.RuntimeAsync(FirstDemandId);
        DateTimeOffset firstSeen = (await fixture.BacklogAsync(FirstDemandId)).FirstSeenAt;
        fixture.Riot.CancelOrder(before.PickupUpperId);
        await TickAndRunAsync(fixture);
        await OwnOrderRebuildTests.PassTheDelayAsync(fixture);
        Assert.Equal(2, fixture.Riot.CreateCount("TO_PICKUP"));
        string rebuilt = (await CurrentStopAsync(fixture, FirstDemandId)).UpperId;

        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        Flat(fixture, FlatBattery);
        await fixture.HearFromPeerAsync();
        fixture.Riot.CancelOrder(rebuilt);
        await fixture.Engine.ExecuteOnceAsync(Token);
        await OwnOrderRebuildTests.PassTheDelayAsync(fixture);
        fixture.Context.ChangeTracker.Clear();

        // Review round 3, suggestion 2: the stop the third guard made is kept in the waiting reason.
        await AssertStoppedForChargeAsync(
            fixture, rebuilt, FlatBattery, pickupCreates: 2, stoppedBefore: "REBUILT_ORDER_ENDED_AGAIN_WITHIN_WINDOW");
        await AssertReleasedAsync(fixture, firstSeen);
    }

    /// <summary>
    /// 护栏三停住时电量还在线上，等人期间才跌破线：不自动交出（护栏三的本意是反复被取消就等人查明原因，自动交出后车可能被派到正被查的那条路上），
    /// 记录仍是 <c>REBUILT_ORDER_ENDED_AGAIN_WITHIN_WINDOW</c>，记一次告警 2176 提示电量已低于线；需求不放回（审查第三轮建议 2）。
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-0361")]
    public async Task ABatteryThatFallsWhileTheThirdGuardWaitsIsWarnedNotHandedOver()
    {
        await using RuntimeFixture fixture = await StoppedRebuildExitTests.StoppedByTheThirdGuardAsync();
        string ended = (await CurrentStopAsync(fixture, FirstDemandId)).UpperId;
        await TickAndRunAsync(fixture);
        Flat(fixture, FlatBattery);

        await TickAndRunAsync(fixture);
        await TickAndRunAsync(fixture);
        await TickAndRunAsync(fixture);
        fixture.Context.ChangeTracker.Clear();

        Assert.Equal(2, fixture.Riot.CreateCount("TO_PICKUP"));
        Assert.Equal("OWN_ORDER_REBUILD_STOPPED", (await fixture.RuntimeAsync(FirstDemandId)).BlockReasonCode);
        OwnOrderRebuildRow stopped = await RebuildAsync(fixture, ended);
        Assert.Equal(
            (OwnOrderRebuildStates.Stopped, "REBUILT_ORDER_ENDED_AGAIN_WITHIN_WINDOW", $"BATTERY_LOW_WHILE_WAITING:BATTERY_{FlatBattery}_BELOW_40"),
            (stopped.State, stopped.StoppedReason, stopped.WaitingReason));
        var warning = Assert.Single(fixture.EngineLog.Entries, entry =>
            entry.Message.Contains("fell below the mandatory charge line", StringComparison.Ordinal));
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Warning, warning.Level);
        await ReleaseOnceAsync(fixture);
        await AssertNotReleasedAsync(fixture);
    }

    /// <summary>
    /// 护栏三在有货的低电车上停住（去卸货站的重建单在窗口内又被取消）：不交释放服务，记录仍是 <c>REBUILT_ORDER_ENDED_AGAIN_WITHIN_WINDOW</c>
    /// （审查第三轮建议 1：去掉「车上没货」那个条件，这一条红）。
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-0361")]
    public async Task TheThirdGuardOnAFlatVehicleWithCargoIsNotHandedOver()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(FirstDemandId, FirstSublot, createdAt: Now.AddMinutes(-10)));
        fixture.BoxCounts.Set(FirstSublot, 7);
        await fixture.AdvanceToGateArrivalAsync();
        fixture.Riot.MovementState = "MT_FINISHED";
        fixture.Context.ChangeTracker.Clear();
        fixture.Riot.CancelOrder((await fixture.RuntimeAsync(FirstDemandId)).GateUpperId!);
        await TickAndRunAsync(fixture);
        await OwnOrderRebuildTests.PassTheDelayAsync(fixture);
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        await fixture.AddCargoSnapshotAsync(fixture.Clock.GetUtcNow());
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        await fixture.HearFromPeerAsync();
        fixture.Context.ChangeTracker.Clear();
        await fixture.Engine.ExecuteOnceAsync(Token);
        Assert.Equal(2, fixture.Riot.CreateCount("TO_GATE"));
        string rebuilt = (await CurrentStopAsync(fixture, FirstDemandId)).UpperId;

        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        Flat(fixture, FlatBattery);
        await fixture.HearFromPeerAsync();
        fixture.Riot.CancelOrder(rebuilt);
        await fixture.Engine.ExecuteOnceAsync(Token);
        await OwnOrderRebuildTests.PassTheDelayAsync(fixture);
        fixture.Context.ChangeTracker.Clear();

        Assert.Equal(2, fixture.Riot.CreateCount("TO_GATE"));
        Assert.Equal("OWN_ORDER_REBUILD_STOPPED", (await fixture.RuntimeAsync(FirstDemandId)).BlockReasonCode);
        OwnOrderRebuildRow stopped = await RebuildAsync(fixture, rebuilt);
        Assert.Equal(
            (OwnOrderRebuildStates.Stopped, "REBUILT_ORDER_ENDED_AGAIN_WITHIN_WINDOW"), (stopped.State, stopped.StoppedReason));
    }

    /// <summary>
    /// 新单确认前被 RIoT 报成 8（SUSPENDED）而停住（<c>REBUILT_ORDER_ENDED_BEFORE_CONFIRMATION</c>），车没货、低电：不交释放服务——释放服务会把那张单
    /// 当成已结束、不发取消，而 8 说不清车上发生了什么（审查第三轮建议 1：去掉「只认护栏三」那个条件，这一条红）。
    /// </summary>
    [Fact]
    public async Task ARebuildStoppedOnASuspendedNewOrderIsNotHandedOverOnAFlatBattery()
    {
        await using RuntimeFixture fixture = await StoppedRebuildExitTests.DispatchedToPickupAsync();
        JourneyRuntimeRow before = await fixture.RuntimeAsync(FirstDemandId);
        fixture.Riot.CancelOrder(before.PickupUpperId);
        await TickAndRunAsync(fixture);
        fixture.Riot.LoseNextCreateResponse = true;
        await OwnOrderRebuildTests.PassTheDelayAsync(fixture);
        OwnOrderRebuildRow ordering = await RebuildAsync(fixture, before.PickupUpperId);
        fixture.Riot.SetOrderState(ordering.NewUpperId, RiotOrderState.Suspended, terminal: true);
        Flat(fixture, FlatBattery);

        await fixture.HearFromPeerAsync();
        await TickAndRunAsync(fixture);
        await fixture.HearFromPeerAsync();
        await TickAndRunAsync(fixture);
        fixture.Context.ChangeTracker.Clear();

        Assert.Equal("OWN_ORDER_REBUILD_STOPPED", (await fixture.RuntimeAsync(FirstDemandId)).BlockReasonCode);
        OwnOrderRebuildRow stopped = await RebuildAsync(fixture, before.PickupUpperId);
        Assert.Equal(
            (OwnOrderRebuildStates.Stopped, "REBUILT_ORDER_ENDED_BEFORE_CONFIRMATION"), (stopped.State, stopped.StoppedReason));
        await ReleaseOnceAsync(fixture);
        await AssertNotReleasedAsync(fixture);
    }

    /// <summary>
    /// 没货低电停下（<c>MANDATORY_CHARGE_REQUIRED</c>）之后，人经出口点「重建」：拒绝，说出 <c>OWN_ORDER_REBUILD_EXIT_MANDATORY_CHARGE</c>，什么都不改——
    /// 这一趟交给释放服务，与车不再合格同一个拒法（审查 S-3）。
    /// </summary>
    [Fact]
    public async Task APersonsRebuildIsRefusedOnceAFlatEmptyTripIsHandedToTheRelease()
    {
        await using RuntimeFixture fixture = await StoppedRebuildExitTests.DispatchedToPickupAsync();
        JourneyRuntimeRow before = await fixture.RuntimeAsync(FirstDemandId);
        fixture.Riot.CancelOrder(before.PickupUpperId);
        await TickAndRunAsync(fixture);
        Flat(fixture, FlatBattery);
        await OwnOrderRebuildTests.PassTheDelayAsync(fixture);
        fixture.Context.ChangeTracker.Clear();
        await AssertStoppedForChargeAsync(fixture, before.PickupUpperId, FlatBattery);

        VehicleFaultRecoveryDecision decision = await VehicleFaultRecoveryTests.Service(fixture, new(fixture))
            .RecoverAsync(StoppedRebuildExitTests.Rebuild(fixture), Token);
        fixture.Context.ChangeTracker.Clear();

        Assert.Equal(VehicleFaultRecoveryOutcome.Refused, decision.Outcome);
        Assert.Contains("OWN_ORDER_REBUILD_EXIT_MANDATORY_CHARGE", decision.Reasons);
        await AssertStoppedForChargeAsync(fixture, before.PickupUpperId, FlatBattery);
    }

    /// <summary>
    /// 没货低电停下之后，放弃这一趟仍然受理（审查第三轮 M-A）：释放服务可能一直拒绝放回（例如当前取货站上还有别的待装需求，
    /// <c>RELEASE_CURRENT_STOP_WITH_OTHER_DEMANDS</c>），那时人手里得有出口，否则车挂着用途、电量只往下掉。拒绝只对人工重建。
    /// </summary>
    [Fact]
    public async Task GivingUpAFlatEmptyTripHandedToTheReleaseIsStillAccepted()
    {
        await using RuntimeFixture fixture = await StoppedRebuildExitTests.DispatchedToPickupAsync();
        JourneyRuntimeRow before = await fixture.RuntimeAsync(FirstDemandId);
        fixture.Riot.CancelOrder(before.PickupUpperId);
        await TickAndRunAsync(fixture);
        Flat(fixture, FlatBattery);
        await OwnOrderRebuildTests.PassTheDelayAsync(fixture);
        fixture.Context.ChangeTracker.Clear();
        await AssertStoppedForChargeAsync(fixture, before.PickupUpperId, FlatBattery);

        VehicleFaultRecoveryDecision decision = await VehicleFaultRecoveryTests.Service(fixture, new(fixture))
            .RecoverAsync(StoppedRebuildExitTests.GiveUp(fixture), Token);

        Assert.Equal(
            (VehicleFaultRecoveryOutcome.TripTerminated, VehicleFaultRecoveryDispositions.TripTerminated),
            (decision.Outcome, decision.Disposition));
        Assert.Empty(decision.Reasons);
    }

    // ---- 已发出的新单：FAILED 与结果未知 -----------------------------------------------------------------------------

    /// <summary>
    /// 新单的建单已经发出、回应丢了（结果未知），之后车才低电：闸只管还没发出的单。RIoT 上可能已经有那张单，停下释放会让它留在 RIoT、
    /// 需求却改派了，所以照常按同一个单号对账、确认，不停、不释放，也不发第二次创建。
    /// </summary>
    [Fact]
    public async Task ARebuildWhoseCreateWentOutWithItsResultUnknownIsReconciledNotStopped()
    {
        await using RuntimeFixture fixture = await StoppedRebuildExitTests.DispatchedToPickupAsync();
        JourneyRuntimeRow before = await fixture.RuntimeAsync(FirstDemandId);
        fixture.Riot.CancelOrder(before.PickupUpperId);
        await TickAndRunAsync(fixture);
        fixture.Riot.LoseNextCreateResponse = true;
        await OwnOrderRebuildTests.PassTheDelayAsync(fixture);
        Assert.Equal(OwnOrderRebuildStates.Ordering, (await RebuildAsync(fixture, before.PickupUpperId)).State);
        Flat(fixture, FlatBattery);

        await fixture.HearFromPeerAsync();
        await TickAndRunAsync(fixture);
        fixture.Context.ChangeTracker.Clear();

        Assert.Equal(2, fixture.Riot.CreateCount("TO_PICKUP"));
        Assert.Equal(OwnOrderRebuildStates.Rebuilt, (await RebuildAsync(fixture, before.PickupUpperId)).State);
        await ReleaseOnceAsync(fixture);
        await AssertNotReleasedAsync(fixture);
    }

    /// <summary>
    /// 新单发出后、确认前 FAILED（审查 S2），这时车低电：照旧记成故障（<c>VEHICLE_ORDER_FAILED</c>），交故障清除入口；不因电量改判成停下释放——
    /// 故障监看挂在旅程上，释放会停掉它。
    /// </summary>
    [Fact]
    public async Task ARebuiltOrderThatFailsBeforeConfirmationOnAFlatBatteryStaysAFault()
    {
        await using RuntimeFixture fixture = await StoppedRebuildExitTests.DispatchedToPickupAsync();
        JourneyRuntimeRow before = await fixture.RuntimeAsync(FirstDemandId);
        fixture.Riot.CancelOrder(before.PickupUpperId);
        await TickAndRunAsync(fixture);
        fixture.Riot.LoseNextCreateResponse = true;
        await OwnOrderRebuildTests.PassTheDelayAsync(fixture);
        OwnOrderRebuildRow ordering = await RebuildAsync(fixture, before.PickupUpperId);
        fixture.Riot.SetOrderState(ordering.NewUpperId, RiotOrderState.Failed, terminal: true);
        Flat(fixture, FlatBattery);

        await fixture.HearFromPeerAsync();
        await TickAndRunAsync(fixture);
        await fixture.HearFromPeerAsync();
        await TickAndRunAsync(fixture);
        fixture.Context.ChangeTracker.Clear();

        Assert.Equal(OwnOrderRebuildStates.Failed, (await RebuildAsync(fixture, before.PickupUpperId)).State);
        Assert.Equal("VEHICLE_ORDER_FAILED", (await fixture.RuntimeAsync(FirstDemandId)).BlockReasonCode);
        Assert.Equal(2, fixture.Riot.CreateCount("TO_PICKUP"));
        await ReleaseOnceAsync(fixture);
        await AssertNotReleasedAsync(fixture);
    }

    // ---- 会话因本服务端自己的在途单而未就绪 --------------------------------------------------------------------------

    /// <summary>
    /// 真车载端的样子：会话因本服务端自己的单停在 <c>DEPARTURE_SAFETY_NOT_READY</c>（形状照
    /// <c>PickupDispatchPlanPastOwnOrderTests.DropSessionOnOwnOrderAsync</c>），单在这时被取消、车低电。电量这一问排在会话之前（与车不再合格同一处）：
    /// 不等会话恢复，照样停下交释放——会话不回就绪，低电车也不会一直挂着这条需求。
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-0360")]
    public async Task AFlatVehicleWhoseSessionIsNotReadyOnItsOwnOrderIsStillReleased()
    {
        await using RuntimeFixture fixture = await StoppedRebuildExitTests.DispatchedToPickupAsync();
        JourneyRuntimeRow before = await fixture.RuntimeAsync(FirstDemandId);
        DateTimeOffset firstSeen = (await fixture.BacklogAsync(FirstDemandId)).FirstSeenAt;
        await OwnOrderRebuildTests.DropSessionOnOwnOrderAsync(fixture);
        fixture.Riot.CancelOrder(before.PickupUpperId);
        await TickAndRunAsync(fixture);
        Flat(fixture, FlatBattery);

        await OwnOrderRebuildTests.PassTheDelayAsync(fixture);
        fixture.Context.ChangeTracker.Clear();

        Assert.Equal(
            SessionReadiness.RecoveryRequired, (await fixture.Context.SessionRecoveries.AsNoTracking().SingleAsync(Token)).Readiness);
        await AssertStoppedForChargeAsync(fixture, before.PickupUpperId, FlatBattery);
        await AssertReleasedAsync(fixture, firstSeen);
    }

    // ---- helpers ------------------------------------------------------------------------------------------------

    private static void AssertNoPersonsRebuildWarning(RuntimeFixture fixture) =>
        Assert.DoesNotContain(fixture.EngineLog.Entries, entry =>
            entry.Message.Contains(PersonsRebuildOnFlatBattery, StringComparison.Ordinal));

    private static void AssertPersonsRebuildWarning(RuntimeFixture fixture)
    {
        var warning = Assert.Single(fixture.EngineLog.Entries, entry =>
            entry.Message.Contains(PersonsRebuildOnFlatBattery, StringComparison.Ordinal));
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Warning, warning.Level);
        Assert.Contains($"{FlatBattery}%", warning.Message, StringComparison.Ordinal);
    }

    private static void Flat(RuntimeFixture fixture, int percent) =>
        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { BatteryPercent = percent };

    private static async Task<OwnOrderRebuildRow> RebuildAsync(RuntimeFixture fixture, string endedUpperId)
    {
        await using ControlServerDbContext reading = new(fixture.DbOptionsForTests);
        return await reading.OwnOrderRebuilds.AsNoTracking().SingleAsync(row => row.EndedUpperId == endedUpperId, Token);
    }

    private static async Task AssertStoppedForChargeAsync(
        RuntimeFixture fixture, string endedUpperId, int battery, int pickupCreates = 1, string? stoppedBefore = null)
    {
        Assert.Equal(pickupCreates, fixture.Riot.CreateCount("TO_PICKUP"));
        Assert.Equal("OWN_ORDER_REBUILD_MANDATORY_CHARGE", (await fixture.RuntimeAsync(FirstDemandId)).BlockReasonCode);
        OwnOrderRebuildRow stopped = await RebuildAsync(fixture, endedUpperId);
        Assert.Equal(
            (OwnOrderRebuildStates.Stopped, "MANDATORY_CHARGE_REQUIRED",
                (stoppedBefore is null ? "" : $"{stoppedBefore};") + $"BATTERY_{battery}_BELOW_40"),
            (stopped.State, stopped.StoppedReason, stopped.WaitingReason));
    }

    private static async Task<IReadOnlyList<DemandReleaseOutcome>> ReleaseOnceAsync(RuntimeFixture fixture)
    {
        Batch7DemandReleaseServiceTests.CancellingGateway gateway = new(fixture.Clock, _ => { });
        IReadOnlyList<DemandReleaseOutcome> outcomes =
            await Batch7DemandReleaseServiceTests.Service(fixture, gateway).RunOnceAsync(Token);
        Assert.Equal(0, gateway.Cancels);
        return outcomes;
    }

    /// <summary>
    /// 释放服务放掉这条需求：退回积压、仍是已受理、没写抑制、首见时刻不变；旅程收尾、用途全部放掉——下一轮充电分配看到的是一辆空闲的低电车。
    /// </summary>
    private static async Task AssertReleasedAsync(RuntimeFixture fixture, DateTimeOffset firstSeen)
    {
        IReadOnlyList<DemandReleaseOutcome> outcomes = await ReleaseOnceAsync(fixture);

        Assert.Equal(
            [(FirstDemandId, "MANDATORY_CHARGE_REQUIRED", "RELEASED")],
            outcomes.Select(outcome => (outcome.DemandId, outcome.Trigger, outcome.Result)));
        await using ControlServerDbContext reading = new(fixture.DbOptionsForTests);
        JourneyRuntimeRow after = await reading.JourneyRuntimes.AsNoTracking().SingleAsync(Token);
        Assert.Equal((JourneyRuntimeStage.Completed, DemandReleaseReasons.Released), (after.Stage, after.BlockReasonCode));
        Assert.True(await DemandJourneyLookup.ReleasedForRedispatch(reading).AnyAsync(row => row.DemandId == FirstDemandId, Token));
        Assert.Equal(DemandExecutionStatus.Accepted,
            (await reading.AcceptedDemands.AsNoTracking().SingleAsync(row => row.DemandId == FirstDemandId, Token)).Status);
        Assert.Equal(firstSeen,
            (await reading.JourneyBacklog.AsNoTracking().SingleAsync(row => row.DemandId == FirstDemandId, Token)).FirstSeenAt);
        await SuppressionAssertions.AssertNothingSuppressedAsync(reading);
        Assert.False(await reading.Set<VehiclePurposeClaimRow>().AnyAsync(Token));
        Assert.False(await reading.Set<VehiclePurposeClaimRecordRow>().AnyAsync(row => row.ReleasedAt == null, Token));
    }

    private static async Task AssertNotReleasedAsync(RuntimeFixture fixture)
    {
        await using ControlServerDbContext reading = new(fixture.DbOptionsForTests);
        Assert.Null((await reading.Set<JourneyDemandRow>().AsNoTracking().SingleAsync(Token)).RemovedAt);
        Assert.NotEqual(JourneyRuntimeStage.Completed, (await reading.JourneyRuntimes.AsNoTracking().SingleAsync(Token)).Stage);
    }
}
