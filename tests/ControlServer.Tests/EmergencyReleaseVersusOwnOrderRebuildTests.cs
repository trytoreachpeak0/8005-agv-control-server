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
/// control-server#349：急停人工解除（REQ-0356）与「本服务端的单被取消即同车重建」（REQ-0360）按真实顺序连起来走一遍——
/// 急停锁住 → 人在 RIoT 处理这辆车的单 → 人工解除急停 → 人工清除故障（#299）——每一步之后断言三件事：有没有新建 RIoT 订单、
/// 新订单承载哪条需求、REQ-0361 的延迟与「短时二次出问题即停」有没有介入。
/// </summary>
/// <remarks>
/// <para>
/// <b>两条链要分开看。</b>今天服务端只在旅程当前那张单 FAILED 时记故障、才可能升级急停
/// （<c>JourneyRuntimeEngine.ObserveOrderFailureAsync</c> 是 <c>VehicleFaultCoordinator.ObserveAsync</c> 在产品代码里唯一的调用方），
/// FAILED 是终态，所以「本服务端的急停锁着、它自己的单还没结束」这个起点服务端自己造不出来——清除入口的类注释也这么写
/// （<c>FAULT_RECOVERY_CURRENT_ORDER_CANCELLED_IN_RIOT</c> "is not reachable in the product"）。前三条用例走的是今天走得到的链；
/// 第四条是票面字面那条链（锁着时单还活着、人去 RIoT 取消它），用故障协调器的公开入口构造起点，结论只对「将来有了别的急停来源」
/// （把 HANG 纳入故障模型的 #319、REQ-0249 两条人工急停来源的入口）成立；第五条是急停不是本服务端发的那条链，没有 REQ-0356 这一步，
/// <b>今天走得到，票面担心的两种形状在那里都出现了</b>（已报调度，是否改由用户决定）。
/// 逐步的表格与反向验证在 <c>evidence/cs349/SUMMARY.md</c>。
/// </para>
/// <para>
/// <b>断言落在「有没有新建 RIoT 订单」「新订单承载哪条需求」这一层</b>（<c>RecordingRiot.CreateCount</c> 与
/// <see cref="OwnOrderRebuildTests.AssertRebuiltAsync"/>），旅程码只作旁证。延迟前后各拨一次钟，并断言钟确实动了。
/// </para>
/// <para>
/// 急停在两处读：引擎里的故障协调器读 <see cref="RuntimeFixture.EmergencyLatched"/>，重建的车况护栏读 RIoT 车辆安全读取的原因码
/// （<c>RecordingRiot.SafetyReasons</c>）。真 RIoT 上这是同一个事实，所以用例总是两处一起拨（<see cref="Latch"/>、<see cref="Unlatch"/>）。
/// </para>
/// </remarks>
public sealed class EmergencyReleaseVersusOwnOrderRebuildTests
{
    private const string ConfirmerId = "L1-CONFIRMER-349";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ---- 今天走得到的链：单 FAILED → 急停锁住 → 人工解除 → 人工清除 --------------------------------------------

    /// <summary>
    /// 开往取货站（车上没货）的单 FAILED、车证不出停稳，服务端急停并锁住。RIoT 里这张单已经 FAILED，没有要取消的；人工解除急停被接受，
    /// 解除本身不建单，过了多久都不建；人经 #299 清除故障之后，延迟一到才给同一辆车、同一条需求建一张开往同一个取货站的单。
    /// 只有一次出问题，护栏三不介入。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 「解除之后过五分钟仍不建」守的是票面担心的第一件事：人刚在车旁确认解除，车不能因此开走。车动的那一刻是清除之后的延迟到点，
    /// 那是人经清除入口说「可以走了」（用户 2026-09-23，vehicle-fault-clearance-field-guide.md 第 4 步写明「清除之后……车会再动」）。
    /// </para>
    /// <para>
    /// <b>挡住它的不是车况护栏里的「故障在效」</b>：FAILED 这一来源的重建只由清除入口记下，清除之前根本没有重建记录，所以这里断的是
    /// 「没有重建记录」而不只是「没建单」。反向验证 M2 拿掉「故障在效」，这一格照绿；把「解除之后记重建」加进解除入口，它会红。
    /// </para>
    /// </remarks>
    [Fact]
    [Trait("Requirement", "REQ-0356")]
    [Trait("Requirement", "REQ-0360")]
    public async Task OnTheWayToPickupTheReleaseAloneBuildsNothingAndOnlyTheClearanceRebuildsAfterTheDelay()
    {
        await using RuntimeFixture fixture = await DispatchedToPickupAsync();
        SiteRiot site = new(fixture);
        JourneyRuntimeRow dispatched = await fixture.RuntimeAsync();
        JourneyStopRow[] stopsBefore = await OwnOrderRebuildTests.StopsAsync(fixture, dispatched.JourneyId);
        JourneyStopRow pickup = stopsBefore.Single(stop => stop.StopRole == JourneyStopRoles.Pickup);

        // 第一步：单 FAILED，车还在动 -> 记故障、急停；RIoT 锁上，下一轮确认锁住。
        await FailWhileMovingThenLatchAsync(fixture, pickup.UpperId);
        await AssertNothingRebuiltAsync(fixture, "TO_PICKUP", creates: 1);
        Assert.Equal("VEHICLE_ORDER_FAILED", (await fixture.RuntimeAsync()).BlockReasonCode);

        // 第二步：人在 RIoT 里看这台车——旅程等的那张单已经 FAILED，是终态，没有可取消的；RIoT 上这台车也没有别的未结束订单。
        Assert.Equal(
            (RiotOrderObservationKind.Terminal, (int?)RiotOrderState.Failed),
            await OrderStateAsync(fixture, pickup.UpperId));
        site.HasUnfinishedOrder = false;

        // 第三步：人工解除急停（确认原因已消除、车上无货、仓门已关）。
        EmergencyStopDecision released = await ReleaseAsync(fixture, site);
        AssertReleaseIssued(released);
        Assert.Equal([RiotEmergencyCommandKind.Cancel], site.EmergencyCommands);
        Assert.Empty(site.OrderCommands);
        Unlatch(fixture);
        int triggersAfterRelease = await TriggerCountAsync(fixture);
        await TickAndHearAsync(fixture);

        // 解除只结束急停：清除之前没有任何东西记重建，所以不建单；故障还在，急停也不重触发。拨过十个延迟也一样。
        DateTimeOffset releasedAt = fixture.Clock.GetUtcNow();
        for (int round = 0; round < 10; round++)
        {
            await OwnOrderRebuildTests.PassTheDelayAsync(fixture);
        }

        Assert.Equal(TimeSpan.FromSeconds(300), fixture.Clock.GetUtcNow() - releasedAt);
        await AssertNothingRebuiltAsync(fixture, "TO_PICKUP", creates: 1);
        Assert.Equal(VehicleFaultLevel.SuspectedBlocked, (await VehicleFaultRecoveryTests.FaultAsync(fixture)).Level);
        Assert.Equal(triggersAfterRelease, await TriggerCountAsync(fixture));

        // 第四步：人经 #299 清除故障。记下重建，延迟从这一刻算。
        fixture.Context.ChangeTracker.Clear();
        VehicleFaultRecoveryDecision cleared = await VehicleFaultRecoveryTests.Service(fixture, site)
            .RecoverAsync(VehicleFaultRecoveryTests.Clear(fixture), Token);
        DateTimeOffset clearedAt = fixture.Clock.GetUtcNow();
        fixture.Context.ChangeTracker.Clear();
        Assert.Equal(
            (VehicleFaultRecoveryOutcome.Cleared, VehicleFaultRecoveryDispositions.RebuildScheduled),
            (cleared.Outcome, cleared.Disposition));
        OwnOrderRebuildRow scheduled = await SingleRebuildAsync(fixture);
        Assert.Equal(
            (OwnOrderRebuildSources.FaultClearedNothingOnBoard, OwnOrderRebuildStates.Pending, clearedAt + fixture.Options.OwnOrderRebuildDelay),
            (scheduled.Source, scheduled.State, scheduled.DueAt));

        // 护栏一：差一秒不建。
        fixture.Clock.Advance(fixture.Options.OwnOrderRebuildDelay - TimeSpan.FromSeconds(1));
        Assert.Equal(fixture.Options.OwnOrderRebuildDelay - TimeSpan.FromSeconds(1), fixture.Clock.GetUtcNow() - clearedAt);
        await fixture.HearFromPeerAsync();
        await fixture.Engine.ExecuteOnceAsync(Token);
        Assert.Equal(1, fixture.Riot.CreateCount("TO_PICKUP"));

        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        await fixture.HearFromPeerAsync();
        await fixture.Engine.ExecuteOnceAsync(Token);

        Assert.Equal(2, fixture.Riot.CreateCount("TO_PICKUP"));
        await OwnOrderRebuildTests.AssertRebuiltAsync(fixture, dispatched, stopsBefore, pickup);
        OwnOrderRebuildRow rebuilt = await SingleRebuildAsync(fixture);
        Assert.Equal(clearedAt + fixture.Options.OwnOrderRebuildDelay, rebuilt.RebuiltAt);
        Assert.Null(rebuilt.StoppedReason);
    }

    /// <summary>
    /// 装着货开往卸货站的单 FAILED、急停锁住；人把货取出，按 REQ-0356 确认「车上无货」解除急停——服务端接受，它不拿自己记的「已装货」去核这句确认。
    /// 清除故障之后重建来源是「车上可能有货」：延迟过了先等车报一份清除之后的仓位读数；读数显示放货的仓是空的，重建停住，永远不建开往卸货站的单。
    /// 需求仍记在本车上、仍是已装货，等人经 #345 的出口处置。
    /// </summary>
    /// <remarks>
    /// 票面担心的第二件事——「重建单承载一条车上有货才成立的送货需求，而解除时已确认车上无货」——在这条链上由 REQ-0362 的仓位读数挡住。
    /// 挡住它的是清除入口按「旅程里有没有已装需求或货物绑定」选的重建来源（<c>VehicleFaultRecoveryService.DisposeOfTheJourneyAsync</c> 的
    /// <c>mayCarry</c>），不是解除时那句确认：解除入口不看服务端自己的货物事实。
    /// </remarks>
    [Fact]
    [Trait("Requirement", "REQ-0356")]
    [Trait("Requirement", "REQ-0362")]
    public async Task OnTheWayToTheGateAnEmptiedVehicleIsReleasedButItsDeliveryIsNeverRebuilt()
    {
        await using RuntimeFixture fixture = await GateArrivalWaitAsync();
        SiteRiot site = new(fixture);
        JourneyRuntimeRow dispatched = await fixture.RuntimeAsync();
        JourneyStopRow unload = (await OwnOrderRebuildTests.StopsAsync(fixture, dispatched.JourneyId))
            .Single(stop => stop.StopRole == JourneyStopRoles.Unload);
        int gateCreates = fixture.Riot.CreateCount("TO_GATE");

        await FailWhileMovingThenLatchAsync(fixture, unload.UpperId);
        await AssertNothingRebuiltAsync(fixture, "TO_GATE", gateCreates);
        Assert.Equal(JourneyDemandStatuses.Loaded, (await MembershipAsync(fixture)).Status);

        // 人把货取出，确认车上无货、仓门已关，解除急停：服务端接受。
        site.HasUnfinishedOrder = false;
        EmergencyStopDecision released = await ReleaseAsync(fixture, site);
        AssertReleaseIssued(released);
        Assert.Empty(site.OrderCommands);
        Unlatch(fixture);
        await TickAndHearAsync(fixture);
        await AssertNothingRebuiltAsync(fixture, "TO_GATE", gateCreates);

        fixture.Context.ChangeTracker.Clear();
        VehicleFaultRecoveryDecision cleared = await VehicleFaultRecoveryTests.Service(fixture, site)
            .RecoverAsync(VehicleFaultRecoveryTests.Clear(fixture), Token);
        fixture.Context.ChangeTracker.Clear();
        Assert.Equal(VehicleFaultRecoveryDispositions.RebuildScheduled, cleared.Disposition);
        Assert.Equal(OwnOrderRebuildSources.FaultClearedCargoOnBoard, (await SingleRebuildAsync(fixture)).Source);

        await OwnOrderRebuildTests.PassTheDelayAsync(fixture);
        Assert.Equal(gateCreates, fixture.Riot.CreateCount("TO_GATE"));
        Assert.Equal("OWN_ORDER_REBUILD_WAITING_CARGO_EVIDENCE", (await fixture.RuntimeAsync()).BlockReasonCode);

        // 车报来清除之后的仓位读数：放货的仓是空的。
        await fixture.AddCargoSnapshotAsync(fixture.Clock.GetUtcNow(), physicalState: "EMPTY");
        await TickAndHearAsync(fixture);
        for (int round = 0; round < 10; round++)
        {
            await OwnOrderRebuildTests.PassTheDelayAsync(fixture);
        }

        Assert.Equal(gateCreates, fixture.Riot.CreateCount("TO_GATE"));
        OwnOrderRebuildRow stopped = await SingleRebuildAsync(fixture);
        Assert.Equal(
            (OwnOrderRebuildStates.Stopped, OwnOrderRebuilds.CargoNotProvenInOriginalSlots),
            (stopped.State, stopped.StoppedReason));
        Assert.Equal("OWN_ORDER_REBUILD_CARGO_NOT_IN_PLACE", (await fixture.RuntimeAsync()).BlockReasonCode);
        JourneyDemandRow membership = await MembershipAsync(fixture);
        Assert.Equal((JourneyDemandStatuses.Loaded, (DateTimeOffset?)null), (membership.Status, membership.RemovedAt));
    }

    /// <summary>
    /// 同一条链带上真车载端的会话，分两段看。前半段是本服务端在途单造成的未就绪（<c>DEPARTURE_SAFETY_NOT_READY</c>）：单 FAILED 时车还在动，
    /// 这期间服务端既不记故障也不急停，车停下、会话就绪的那一轮才记故障、发急停——<b>被推迟的是记故障与急停（cs#358）</b>，不是重建。
    /// 后半段是锁住之后会话又未就绪，一直到清除之后：解除与清除都照样成立（都不看会话），重建过了延迟也不建，等到会话就绪的那一轮才建——
    /// 重建时机从「清除后延迟到点」变成「延迟到点且会话就绪」，两者取晚。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 会话的样子照 <c>PickupDispatchPlanPastOwnOrderTests.DropSessionOnOwnOrderAsync</c>。合成车载端永远报安全，合成 L2 按构造看不见这一格。
    /// </para>
    /// <para>
    /// <b>前半段钉的是今天的行为，cs#358 修好之后要翻。</b>记故障只在引擎的到站分支里，那在会话闸门之后；调度判为上真车准入规则第 (1) 条
    /// 「急停失效」，开成 cs#358。
    /// </para>
    /// <para>
    /// <b>后半段的未就绪不是本服务端在途单造成的</b>：那张单已经 FAILED，是终态。锁住的车在真车载端上会不会未就绪，没在真车上核实过
    /// （vehicle-fault-clearance-field-guide.md 末节），这里借同一个形状取未就绪，是对重建更不利的那一种。「不建」在这一段有两道挡：
    /// 重建的会话闸门，和车况护栏里的 <c>ONBOARD_FACTS_NOT_READY</c>（会话未就绪时车载端事实读不到）；拿掉前一道，等待理由换成后一道，
    /// 仍然不建（反向验证 M4）。
    /// </para>
    /// </remarks>
    [Fact]
    [Trait("Requirement", "REQ-0356")]
    [Trait("Requirement", "REQ-0360")]
    public async Task WithTheRealOnboardsSessionNotReadyTheRebuildWaitsForTheSessionAsWellAsTheDelay()
    {
        await using RuntimeFixture fixture = await DispatchedToPickupAsync();
        SiteRiot site = new(fixture);
        JourneyRuntimeRow dispatched = await fixture.RuntimeAsync();
        JourneyStopRow[] stopsBefore = await OwnOrderRebuildTests.StopsAsync(fixture, dispatched.JourneyId);
        JourneyStopRow pickup = stopsBefore.Single(stop => stop.StopRole == JourneyStopRoles.Pickup);
        await OwnOrderRebuildTests.DropSessionOnOwnOrderAsync(fixture);

        // 单 FAILED、车还在动，会话未就绪：三轮下来不记故障、不急停。
        fixture.Riot.MovementState = "MT_RUNNING";
        fixture.Riot.FailOrder(pickup.UpperId);
        for (int round = 0; round < 3; round++)
        {
            await TickAndHearAsync(fixture);
        }

        Assert.Equal("ONBOARD_SESSION_NOT_READY", (await fixture.RuntimeAsync()).BlockReasonCode);
        await using (ControlServerDbContext reading = new(fixture.DbOptionsForTests))
        {
            Assert.Empty(await reading.VehicleFaultStates.AsNoTracking().ToArrayAsync(Token));
        }

        Assert.Null(await LatestTriggerOutcomeAsync(fixture));

        // 会话就绪的那一轮才记故障、发急停；锁住之后会话又未就绪。
        await fixture.RestoreSessionReadyAsync();
        fixture.Context.ChangeTracker.Clear();
        await TickAndHearAsync(fixture);
        Assert.Equal(VehicleFaultLevel.SuspectedBlocked, (await VehicleFaultRecoveryTests.FaultAsync(fixture)).Level);
        Assert.Equal(RiotOrderCommandOutcome.Pending, await LatestTriggerOutcomeAsync(fixture));
        Latch(fixture);
        fixture.Riot.MovementState = "MT_FINISHED";
        await TickAndHearAsync(fixture);
        Assert.Equal(RiotOrderCommandOutcome.Confirmed, await LatestTriggerOutcomeAsync(fixture));
        await OwnOrderRebuildTests.DropSessionOnOwnOrderAsync(fixture);
        await TickAndHearAsync(fixture);
        await AssertNothingRebuiltAsync(fixture, "TO_PICKUP", creates: 1);

        // 解除与清除都不看会话。
        site.HasUnfinishedOrder = false;
        AssertReleaseIssued(await ReleaseAsync(fixture, site));
        Unlatch(fixture);
        await TickAndHearAsync(fixture);
        await AssertNothingRebuiltAsync(fixture, "TO_PICKUP", creates: 1);
        fixture.Context.ChangeTracker.Clear();
        VehicleFaultRecoveryDecision cleared = await VehicleFaultRecoveryTests.Service(fixture, site)
            .RecoverAsync(VehicleFaultRecoveryTests.Clear(fixture), Token);
        DateTimeOffset clearedAt = fixture.Clock.GetUtcNow();
        fixture.Context.ChangeTracker.Clear();
        Assert.Equal(VehicleFaultRecoveryDispositions.RebuildScheduled, cleared.Disposition);

        // 延迟过了两遍，会话仍未就绪：不建，记录写明在等会话。
        await OwnOrderRebuildTests.PassTheDelayAsync(fixture);
        await OwnOrderRebuildTests.PassTheDelayAsync(fixture);
        Assert.True(fixture.Clock.GetUtcNow() - clearedAt >= fixture.Options.OwnOrderRebuildDelay * 2);
        Assert.Equal(1, fixture.Riot.CreateCount("TO_PICKUP"));
        OwnOrderRebuildRow waiting = await SingleRebuildAsync(fixture);
        Assert.Equal((OwnOrderRebuildStates.Pending, "ONBOARD_SESSION_NOT_READY"), (waiting.State, waiting.WaitingReason));

        // 会话就绪的下一轮就建，不再另等一个延迟。
        await fixture.RestoreSessionReadyAsync();
        fixture.Context.ChangeTracker.Clear();
        await TickAndHearAsync(fixture);
        DateTimeOffset readyRound = fixture.Clock.GetUtcNow();

        Assert.Equal(2, fixture.Riot.CreateCount("TO_PICKUP"));
        await OwnOrderRebuildTests.AssertRebuiltAsync(fixture, dispatched, stopsBefore, pickup);
        Assert.Equal(readyRound, (await SingleRebuildAsync(fixture)).RebuiltAt);
    }

    // ---- 票面字面那条链：锁着时本服务端的单还活着，人在 RIoT 里取消它（今天造不出来，构造起点） ----------------------

    /// <summary>
    /// 构造的起点：开往取货站的单在 RIoT 上 HANG（还没结束），服务端对这辆车记了故障并急停锁住。人先试人工解除——被拒
    /// （<c>EMERGENCY_VEHICLE_ORDER_NOT_FINISHED</c>），解除入口不替人取消、不发任何订单命令。人于是在 RIoT 里取消这张单：服务端把它当误操作，
    /// 记下「同车同需求重建」，延迟到点后等车况（故障在、急停锁着）。人再解除——这次被接受；车况剩下「故障在」，仍不建。人去清除故障——
    /// 清除入口拒绝（<c>FAULT_RECOVERY_CURRENT_ORDER_CANCELLED_IN_RIOT</c>），故障永远不清，重建永远在等，一张单都不建。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>起点是构造的。</b>产品里记故障的只有「旅程当前那张单 FAILED」，而 FAILED 是终态；这里用故障协调器的公开入口、以一个今天没人传的
    /// 症状码记故障，模拟将来 #319（把 HANG 纳入故障模型）或 REQ-0249 两条人工急停来源有了入口之后的样子。今天这个结局走不到。
    /// </para>
    /// <para>
    /// 结局是「车不动、单不建」，所以不属于票面的两种停下条件（车在无人预期时移动、送不存在的货）。但它是一个出不去的环：清除入口不接、
    /// #345 的出口只接停住的重建、续行要 PAUSED 的单、重建又在等故障清除。今天走不到，所以不算上真车准入规则第 (3) 条「只能改库」；
    /// 将来那两种来源落地时就是第 (3) 条，要一起解决（为什么今天走不到，见 evidence/cs349/SUMMARY.md）。
    /// </para>
    /// </remarks>
    [Fact]
    [Trait("Requirement", "REQ-0356")]
    [Trait("Requirement", "REQ-0360")]
    public async Task ConstructedALatchOverALiveOwnOrderThatAPersonCancelsInRiotNeverRebuildsBecauseTheFaultCannotBeCleared()
    {
        await using RuntimeFixture fixture = await DispatchedToPickupAsync();
        SiteRiot site = new(fixture);
        JourneyRuntimeRow dispatched = await fixture.RuntimeAsync();
        string upperId = dispatched.PickupUpperId;

        // 构造：单 HANG，服务端对这辆车记故障、急停、锁住。
        fixture.Riot.SetOrderState(upperId, RiotOrderState.Hang, terminal: false);
        site.HasUnfinishedOrder = true;
        fixture.Riot.MovementState = "MT_RUNNING";
        VehicleFaultCoordinator coordinator = Coordinator(fixture, site, out ControlServerDbContext faultContext);
        await using (faultContext)
        {
            FaultedVehicleContext hanging = new(
                new RiotOrderCommandTarget(fixture.Options.AgvId, upperId, "ORDER-TO_PICKUP"), Cargo: null);
            VehicleFaultDecision observed = await coordinator.ObserveAsync(Subject(fixture), "L1_CONSTRUCTED_SYMPTOM", hanging, Token);
            Assert.True(observed.Escalated);
            Latch(fixture);
            fixture.Riot.MovementState = "MT_FINISHED";
            fixture.Clock.Advance(TimeSpan.FromSeconds(1));
            await coordinator.ObserveAsync(Subject(fixture), "L1_CONSTRUCTED_SYMPTOM", hanging, Token);
        }

        Assert.Equal(RiotOrderCommandOutcome.Confirmed, await LatestTriggerOutcomeAsync(fixture));
        await TickAndHearAsync(fixture);
        Assert.Equal("ORDER_HANG", (await fixture.RuntimeAsync()).BlockReasonCode);
        await AssertNothingRebuiltAsync(fixture, "TO_PICKUP", creates: 1);

        // 人先试解除：单没结束，拒绝；解除入口不替人取消，一条订单命令都不发。
        List<RiotOrderCommandKind> commandsBeforeRelease = [.. site.OrderCommands];
        EmergencyStopDecision refused = await ReleaseAsync(fixture, site);
        Assert.Equal(EmergencyStopAction.RecoveryRefused, refused.Action);
        Assert.Equal(["EMERGENCY_VEHICLE_ORDER_NOT_FINISHED"], refused.Reasons);
        Assert.Equal(commandsBeforeRelease, site.OrderCommands);
        Assert.DoesNotContain(RiotEmergencyCommandKind.Cancel, site.EmergencyCommands);

        // 人在 RIoT 里取消这张单：服务端没发过取消，按误操作记重建。
        fixture.Riot.CancelOrder(upperId);
        site.HasUnfinishedOrder = false;
        await TickAndHearAsync(fixture);
        DateTimeOffset cancelSeenAt = fixture.Clock.GetUtcNow();
        OwnOrderRebuildRow recorded = await SingleRebuildAsync(fixture);
        Assert.Equal(
            (OwnOrderRebuildSources.CancelledInRiot, OwnOrderRebuildStates.Pending, cancelSeenAt + fixture.Options.OwnOrderRebuildDelay),
            (recorded.Source, recorded.State, recorded.DueAt));

        await OwnOrderRebuildTests.PassTheDelayAsync(fixture);
        Assert.Equal(1, fixture.Riot.CreateCount("TO_PICKUP"));
        OwnOrderRebuildRow waiting = await SingleRebuildAsync(fixture);
        Assert.Contains("VEHICLE_FAULT_IN_EFFECT", waiting.WaitingReason, StringComparison.Ordinal);
        Assert.Contains("RIOT_EMERGENCY_NOT_OK", waiting.WaitingReason, StringComparison.Ordinal);

        // 人再解除：这次车上没有未结束订单，接受。
        EmergencyStopDecision released = await ReleaseAsync(fixture, site);
        AssertReleaseIssued(released);
        Unlatch(fixture);
        await TickAndHearAsync(fixture);
        Assert.Equal(1, fixture.Riot.CreateCount("TO_PICKUP"));
        Assert.Equal("VEHICLE_FAULT_IN_EFFECT", (await SingleRebuildAsync(fixture)).WaitingReason);

        // 人去清除故障：清除入口不接被 RIoT 取消的单。
        fixture.Context.ChangeTracker.Clear();
        VehicleFaultRecoveryDecision clearance = await VehicleFaultRecoveryTests.Service(fixture, site)
            .RecoverAsync(VehicleFaultRecoveryTests.Clear(fixture), Token);
        fixture.Context.ChangeTracker.Clear();
        Assert.Equal(VehicleFaultRecoveryOutcome.Refused, clearance.Outcome);
        Assert.Equal(["FAULT_RECOVERY_CURRENT_ORDER_CANCELLED_IN_RIOT"], clearance.Reasons);

        DateTimeOffset refusedAt = fixture.Clock.GetUtcNow();
        for (int round = 0; round < 120; round++)
        {
            await OwnOrderRebuildTests.PassTheDelayAsync(fixture);
        }

        Assert.Equal(TimeSpan.FromHours(1), fixture.Clock.GetUtcNow() - refusedAt);
        Assert.Equal(1, fixture.Riot.CreateCount("TO_PICKUP"));
        OwnOrderRebuildRow stillWaiting = await SingleRebuildAsync(fixture);
        Assert.Equal(
            (OwnOrderRebuildStates.Pending, "VEHICLE_FAULT_IN_EFFECT"),
            (stillWaiting.State, stillWaiting.WaitingReason));
        Assert.Equal(VehicleFaultLevel.SuspectedBlocked, (await VehicleFaultRecoveryTests.FaultAsync(fixture)).Level);
    }

    // ---- 对照：急停不是本服务端发的，没有 REQ-0356 这一步 ------------------------------------------------------

    /// <summary>
    /// 装着货开往卸货站，车被别人急停（现场物理急停或 RIoT 人员，服务端的命令审计里没有触发），单在 RIoT 上 HANG。人工解除不归服务端
    /// （<c>EMERGENCY_NOT_RAISED_BY_8005</c>）。人在 RIoT 里取消本服务端这张单、把货取走，车报来的仓位读数显示放货的仓是空的。
    /// 急停由别人解开之后，服务端照 REQ-0360 给同一辆车重建开往卸货站的单，承载的仍是那条已装货的需求——取消来源的重建不看仓位读数。
    /// 重建的延迟从读到取消算，锁着期间就走完了，所以解开急停的那一轮就建单，没有缓冲。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>这一格今天走得到，票面担心的两种形状在这里都出现了</b>：解开急停的那一轮车就被派走（断言到「那一轮新建了 <c>TO_GATE</c>」），
    /// 重建单承载的需求要车上有货，而车已报放货的仓是空的。它不在票面的停下条件里，只因为链上没有 REQ-0356 的解除、服务端从没被告知
    /// 「车上无货」。同样的情形在故障清除来源上由 REQ-0362 的仓位读数挡住
    /// （<see cref="OnTheWayToTheGateAnEmptiedVehicleIsReleasedButItsDeliveryIsNeverRebuilt"/>），取消来源没有这一道
    /// （<c>JourneyRuntimeEngine.AdvanceOwnOrderRebuildAsync</c> 只对 <c>FaultClearedCargoOnBoard</c> 取仓位读数）。
    /// </para>
    /// <para>
    /// <b>钉的是今天的行为，不是判断它对。</b>已报调度，是否改由用户决定（REQ-0360 字面要求继续承载原需求；它引用的 REQ-0239 又写着
    /// 「相关阻断收敛」后才可重建、结果不明时继续载货保全）。将来给取消来源加上仓位证明，这一条要翻。
    /// </para>
    /// <para>
    /// <b>这一格不需要有人违反说明。</b>现场说明（vehicle-fault-clearance-field-guide.md「要分清两种取消」）禁止的是「为了让车停下」而取消
    /// 服务端的单；急停不是本服务端发的，按说明转 RIoT 人员处置，他们为清场取消这张单不一定违反它，而 REQ-0356 条文本身也写着「须先取消该订单」。
    /// </para>
    /// <para>
    /// 仓位读数的时刻要严格晚于重建记录的时刻（<c>CargoEvidenceAsync</c> 只认 <c>ReceivedAt &gt; RecordedAt</c>）。这里先拨一秒再报读数，并断言了
    /// 这个先后：同一时刻的读数今天无所谓，将来加上仓位证明时会被当成「没收到读数」，这一格就会因为错误的理由变红。
    /// </para>
    /// </remarks>
    [Fact]
    [Trait("Requirement", "REQ-0360")]
    public async Task UnderSomeoneElsesStopAnEmptiedVehicleWhoseOwnOrderIsCancelledIsRebuiltToDeliverAnyway()
    {
        await using RuntimeFixture fixture = await GateArrivalWaitAsync();
        SiteRiot site = new(fixture);
        JourneyRuntimeRow dispatched = await fixture.RuntimeAsync();
        JourneyStopRow[] stopsBefore = await OwnOrderRebuildTests.StopsAsync(fixture, dispatched.JourneyId);
        JourneyStopRow unload = stopsBefore.Single(stop => stop.StopRole == JourneyStopRoles.Unload);
        int gateCreates = fixture.Riot.CreateCount("TO_GATE");

        // 别人的急停：RIoT 锁着，单 HANG；服务端没有发过触发。
        Latch(fixture);
        fixture.Riot.SetOrderState(unload.UpperId, RiotOrderState.Hang, terminal: false);
        site.HasUnfinishedOrder = true;
        await TickAndHearAsync(fixture);
        Assert.Null(await LatestTriggerOutcomeAsync(fixture));

        EmergencyStopDecision refused = await ReleaseAsync(fixture, site);
        Assert.Equal(EmergencyStopAction.RecoveryRefused, refused.Action);
        Assert.Equal(["EMERGENCY_NOT_RAISED_BY_8005", "EMERGENCY_VEHICLE_ORDER_NOT_FINISHED"], refused.Reasons);
        Assert.Empty(site.OrderCommands);

        // 人在 RIoT 里取消这张单，把货取走。
        fixture.Riot.CancelOrder(unload.UpperId);
        site.HasUnfinishedOrder = false;
        await TickAndHearAsync(fixture);
        DateTimeOffset cancelSeenAt = fixture.Clock.GetUtcNow();
        OwnOrderRebuildRow recorded = await SingleRebuildAsync(fixture);
        Assert.Equal(
            (OwnOrderRebuildSources.CancelledInRiot, cancelSeenAt, cancelSeenAt + fixture.Options.OwnOrderRebuildDelay),
            (recorded.Source, recorded.RecordedAt, recorded.DueAt));

        // 车报放货的仓是空的：读数晚于重建记录一秒，是读到取消之后才收到的读数（见 remarks）。
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        DateTimeOffset emptyReadAt = fixture.Clock.GetUtcNow();
        Assert.True(emptyReadAt > recorded.RecordedAt, "the snapshot must be received after the rebuild was recorded");
        await fixture.AddCargoSnapshotAsync(emptyReadAt, physicalState: "EMPTY");

        await OwnOrderRebuildTests.PassTheDelayAsync(fixture);
        Assert.Equal(gateCreates, fixture.Riot.CreateCount("TO_GATE"));
        Assert.Contains("RIOT_EMERGENCY_NOT_OK", (await SingleRebuildAsync(fixture)).WaitingReason, StringComparison.Ordinal);

        // 别人解开急停：延迟早已走完，解开的那一轮就建开往卸货站的单，承载那条已装货的需求。
        Unlatch(fixture);
        await TickAndHearAsync(fixture);
        DateTimeOffset unlatchedRound = fixture.Clock.GetUtcNow();
        Assert.True(recorded.DueAt < unlatchedRound, "the delay must have run out while the vehicle was latched");

        Assert.Equal(gateCreates + 1, fixture.Riot.CreateCount("TO_GATE"));
        await OwnOrderRebuildTests.AssertRebuiltAsync(fixture, dispatched, stopsBefore, unload);
        OwnOrderRebuildRow rebuilt = await SingleRebuildAsync(fixture);
        Assert.Equal(unlatchedRound, rebuilt.RebuiltAt);
        Assert.Null(rebuilt.CargoEvidenceMessageId);
        Assert.Equal(JourneyDemandStatuses.Loaded, (await MembershipAsync(fixture)).Status);
    }

    // ---- 夹具 ----------------------------------------------------------------------------------------------

    /// <summary>
    /// 在途单 FAILED 时车还在动（<c>MT_RUNNING</c>）：引擎记故障、证不出停稳、升级急停，触发停在 Pending；然后 RIoT 锁上，
    /// 钟走一秒后的下一轮把触发对账成 Confirmed。锁住之后车读作停止。
    /// </summary>
    private static async Task FailWhileMovingThenLatchAsync(RuntimeFixture fixture, string upperId)
    {
        fixture.Riot.MovementState = "MT_RUNNING";
        fixture.Riot.FailOrder(upperId);
        await TickAndHearAsync(fixture);
        Assert.Equal(VehicleFaultLevel.SuspectedBlocked, (await VehicleFaultRecoveryTests.FaultAsync(fixture)).Level);
        Assert.Equal(RiotOrderCommandOutcome.Pending, await LatestTriggerOutcomeAsync(fixture));

        Latch(fixture);
        fixture.Riot.MovementState = "MT_FINISHED";
        await TickAndHearAsync(fixture);
        Assert.Equal(RiotOrderCommandOutcome.Confirmed, await LatestTriggerOutcomeAsync(fixture));
    }

    /// <summary>RIoT 报这辆车急停锁住：故障协调器读的闩锁与重建车况护栏读的安全原因码一起拨。</summary>
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

    private static EmergencyStopSubject Subject(RuntimeFixture fixture) =>
        new(fixture.Options.AgvId, fixture.Options.VehicleKey);

    /// <summary>REQ-0356 的人工确认解除，走的是 HTTP 入口背后的同一个方法，确认三件事都勾上。</summary>
    private static async Task<EmergencyStopDecision> ReleaseAsync(RuntimeFixture fixture, SiteRiot site)
    {
        await using ControlServerDbContext context = new(fixture.DbOptionsForTests);
        EmergencyStopSupervisor supervisor = new(
            site, site, site, new RiotOrderCommandAuditStore(context), new VehicleFaultStore(context),
            Options.Create(new RiotCommandOptions()), fixture.Clock, NullLogger<EmergencyStopSupervisor>.Instance);
        EmergencyStopDecision decision = await supervisor.ReleaseOnConfirmationAsync(
            new EmergencyStopReleaseConfirmation(
                Subject(fixture), ConfirmerId, CauseCleared: true, VehicleEmpty: true, AllDoorsClosed: true, Note: "L1 #349"),
            Token);
        fixture.Context.ChangeTracker.Clear();
        return decision;
    }

    /// <summary>
    /// 解除命令发出、没被拒：回读时 RIoT 还锁着（真车解锁要几秒，2026-09-15 实测 3.6 秒），所以结果是「已发出、未读到 OK」——HTTP 入口的 202。
    /// 用例随后才把闩锁拨开，由之后读到 <c>OK</c> 的那一次把它记成解除成功。
    /// </summary>
    private static void AssertReleaseIssued(EmergencyStopDecision released)
    {
        Assert.Equal(EmergencyStopAction.RecoveryUnconfirmed, released.Action);
        Assert.Equal(["EMERGENCY_RELEASE_NOT_CONFIRMED"], released.Reasons);
    }

    /// <summary>
    /// 与 <see cref="VehicleFaultRecoveryTests.Service"/> 里同样拼起来的故障协调器，站在 RIoT 现场一侧的替身上。只给构造起点用：
    /// 产品里这个入口只由引擎在单 FAILED 时调用。
    /// </summary>
    private static VehicleFaultCoordinator Coordinator(RuntimeFixture fixture, SiteRiot site, out ControlServerDbContext context)
    {
        context = new ControlServerDbContext(fixture.DbOptionsForTests);
        VehicleFaultStore faults = new(context);
        RiotOrderCommandAuditStore audit = new(context);
        EmergencyStopSupervisor supervisor = new(
            site, site, site, audit, faults, Options.Create(new RiotCommandOptions()), fixture.Clock,
            NullLogger<EmergencyStopSupervisor>.Instance);
        IOptions<VehicleFaultOptions> faultOptions = Options.Create(new VehicleFaultOptions());
        return new VehicleFaultCoordinator(
            faults, site, site, site, audit, new RiotOrderCommandService(site, audit, site, fixture.Clock), supervisor,
            new VehicleMotionLedger(faultOptions), faultOptions, fixture.Clock, NullLogger<VehicleFaultCoordinator>.Instance);
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

    /// <summary>这一步之后：没有重建记录，指定用途的单还是 <paramref name="creates"/> 张。</summary>
    private static async Task AssertNothingRebuiltAsync(RuntimeFixture fixture, string purpose, int creates)
    {
        Assert.Equal(creates, fixture.Riot.CreateCount(purpose));
        await using ControlServerDbContext reading = new(fixture.DbOptionsForTests);
        Assert.Empty(await reading.OwnOrderRebuilds.AsNoTracking().ToArrayAsync(Token));
    }

    private static async Task<OwnOrderRebuildRow> SingleRebuildAsync(RuntimeFixture fixture)
    {
        await using ControlServerDbContext reading = new(fixture.DbOptionsForTests);
        return await reading.OwnOrderRebuilds.AsNoTracking().SingleAsync(Token);
    }

    private static async Task<JourneyDemandRow> MembershipAsync(RuntimeFixture fixture)
    {
        await using ControlServerDbContext reading = new(fixture.DbOptionsForTests);
        return await reading.Set<JourneyDemandRow>().AsNoTracking().SingleAsync(Token);
    }

    private static async Task<(RiotOrderObservationKind Kind, int? State)> OrderStateAsync(RuntimeFixture fixture, string upperId)
    {
        RiotOrderObservation order = await fixture.Riot.ReconcileByUpperIdAsync(upperId, Token);
        return (order.Kind, order.OrderState);
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
