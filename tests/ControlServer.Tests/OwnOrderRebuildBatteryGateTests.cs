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

        Assert.Equal(
            VehicleFaultRecoveryOutcome.RebuildRequested,
            (await VehicleFaultRecoveryTests.Service(fixture, new(fixture))
                .RecoverAsync(StoppedRebuildExitTests.Rebuild(fixture), Token)).Outcome);
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

    private static void Flat(RuntimeFixture fixture, int percent) =>
        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { BatteryPercent = percent };

    private static async Task<OwnOrderRebuildRow> RebuildAsync(RuntimeFixture fixture, string endedUpperId)
    {
        await using ControlServerDbContext reading = new(fixture.DbOptionsForTests);
        return await reading.OwnOrderRebuilds.AsNoTracking().SingleAsync(row => row.EndedUpperId == endedUpperId, Token);
    }

    private static async Task AssertStoppedForChargeAsync(
        RuntimeFixture fixture, string endedUpperId, int battery, int pickupCreates = 1)
    {
        Assert.Equal(pickupCreates, fixture.Riot.CreateCount("TO_PICKUP"));
        Assert.Equal("OWN_ORDER_REBUILD_MANDATORY_CHARGE", (await fixture.RuntimeAsync(FirstDemandId)).BlockReasonCode);
        OwnOrderRebuildRow stopped = await RebuildAsync(fixture, endedUpperId);
        Assert.Equal(
            (OwnOrderRebuildStates.Stopped, "MANDATORY_CHARGE_REQUIRED", $"BATTERY_{battery}_BELOW_40"),
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
