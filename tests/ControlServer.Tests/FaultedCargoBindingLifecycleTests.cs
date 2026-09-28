using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Commands;
using ControlServer.Host.Runtime.Faults;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using static ControlServer.Tests.Batch7StopDrivenAdvanceDriver;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;

namespace ControlServer.Tests;

/// <summary>
/// 故障货物绑定（<c>FaultedVehicleCargo</c>，REQ-0238）的生命周期（control-server#376）：货离开车时释放，货还在车上时保留，
/// 下一次故障只认本趟旅程的货，重建不会停在一个没有出口的等待里。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么要有它。</b>在这之前，全仓只有续行、按原车重建确认（清除时车上有货的那个来源）、#345 的交接结算三处释放绑定。cs#335 的入口
/// （门锁故障期间本服务端按住的单在 RIoT 被取消，人工清除）和「重建出来的单在确认前被取消」两条路都会让绑定在卸完货、旅程收尾之后仍然在效。
/// 同一辆车下一次故障时，故障协调器「一次故障只绑一次」，把那条旧绑定当成这次的货：空车清除后按「车上有货」重建，而空车没有要查的仓位，
/// 重建永远等在 <c>CARGO_SLOTS_UNKNOWN</c> 上，#345 的三个出口都因为它不是 STOPPED 被拒；有新货的车「继续原单」被
/// <c>RESUME_CARGO_BINDING_MISMATCH</c> 拒绝。现场只能改库（调度 Coordinator 8 2026-09-28 判准入线③）。
/// </para>
/// <para>
/// <b>四条不变量</b>（调度 2026-09-28）：一、货被证明离开这辆车时释放，货还在车上时保留；二、新故障只认本趟旅程的货，别的旅程留下的绑定
/// 按证据释放（它的旅程已经收尾）并记下原因；三、不许有没有出口的等待；四、真有货时保护不丢，已发出的单不重复建。
/// </para>
/// <para>
/// 「留下的旧绑定」有两种造法：真实路径（cs#335 的入口、确认前取消，这两格从头走）；以及直接在库里放一条在效绑定，代表旧版本已经留在库里的行
/// ——这正是上线时现场库里会有的东西，第二层的自愈就是为它写的。
/// </para>
/// </remarks>
public sealed class FaultedCargoBindingLifecycleTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private const string ProofVariable = "CONTROL_SERVER_TEST_RECOVERY_PROOF_CS376";
    private const string Proof = "cs376-recovery-proof-not-a-production-secret";
    private const string SessionEventId = "33760000-0000-4000-8000-000000000001";

    // ---- 一、货离开车时释放，货还在车上时保留 ----------------------------------------------------------------------------

    /// <summary>
    /// cs#335 的入口：有货去卸货站，门锁故障、急停锁住，按住的单在 RIoT 被取消，门锁恢复后人工清除，引擎按「单在 RIoT 被取消」重建并确认。
    /// 货还在车上，绑定一直在效；到站卸完、车上什么都不剩的那一刻，绑定以 <c>UNLOADED_WITH_NOTHING_LEFT_ON_BOARD</c> 释放。
    /// </summary>
    /// <remarks>
    /// 真车载端在本服务端在途单期间整段未就绪（<c>DropSessionOnOwnOrderAsync</c> 的形状），第一次故障照样要在会话未就绪那一侧走一遍。
    /// </remarks>
    [Theory]
    [InlineData("ready")]
    [InlineData("behind-the-readiness-gate")]
    [Trait("Requirement", "REQ-0238")]
    public async Task AnUnloadAfterADoorFaultOnAnOrderCancelledInRiotReleasesTheBinding(string session)
    {
        await using RuntimeFixture fixture = await DoorFaultedTripRebuiltAfterACancellationAsync(
            behindTheGate: session == "behind-the-readiness-gate");

        FaultedVehicleCargoRow onTheWay = await SingleBindingAsync(fixture);
        Assert.Null(onTheWay.ReleasedAt);

        await UnloadAtTheGateAsync(fixture, FirstDemandId);

        FaultedVehicleCargoRow unloaded = await SingleBindingAsync(fixture);
        Assert.Equal(JourneyRuntimeStage.Completed, (await fixture.RuntimeAsync(FirstDemandId)).Stage);
        Assert.Equal("UNLOADED_WITH_NOTHING_LEFT_ON_BOARD", unloaded.ReleasedReason);
        Assert.NotNull(unloaded.ReleasedAt);
    }

    /// <summary>
    /// 清除时车上有货，重建出来的单建单应答丢了、确认之前又被人在 RIoT 取消：这被记成窗口内的第二次问题，护栏三停住，来源是「在 RIoT 被取消」。
    /// 人经 #345 的出口再建一次，按原车重建确认——这一来源的确认不释放，货还在车上；到站卸完之后释放。
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-0238")]
    public async Task AnUnloadAfterARebuildCancelledBeforeConfirmationReleasesTheBinding()
    {
        await using RuntimeFixture fixture = await VehicleFaultRecoveryTests.FaultedOnTheWayToGateAsync();
        Assert.Equal(
            VehicleFaultRecoveryDispositions.RebuildScheduled,
            (await VehicleFaultRecoveryTests.Service(fixture, new(fixture))
                .RecoverAsync(VehicleFaultRecoveryTests.Clear(fixture), Token)).Disposition);
        fixture.Context.ChangeTracker.Clear();
        await OwnOrderRebuildTests.PassTheDelayAsync(fixture);
        fixture.Riot.LoseNextCreateResponse = true;
        await OwnOrderRebuildTests.ReportCargoInPlaceAndRunAsync(fixture);
        string newUpperId = (await RebuildsAsync(fixture)).Single().NewUpperId;
        fixture.Riot.SetOrderState(newUpperId, RiotOrderState.Cancelled, terminal: true);
        await DriveOneRoundAsync(fixture);
        OwnOrderRebuildRow stopped = (await RebuildsAsync(fixture)).Single(row => row.State == OwnOrderRebuildStates.Stopped);
        Assert.Equal(
            (OwnOrderRebuildSources.CancelledInRiot, OwnOrderRebuilds.EndedAgainWithinWindow),
            (stopped.Source, stopped.StoppedReason));

        Assert.Equal(
            VehicleFaultRecoveryOutcome.RebuildRequested,
            (await VehicleFaultRecoveryTests.Service(fixture, new(fixture))
                .RecoverAsync(StoppedRebuildExitTests.Rebuild(fixture), Token)).Outcome);
        fixture.Context.ChangeTracker.Clear();
        await OwnOrderRebuildTests.PassTheDelayAsync(fixture);
        await OwnOrderRebuildTests.ReportCargoInPlaceAndRunAsync(fixture);
        Assert.Equal(OwnOrderRebuildStates.Rebuilt, (await RebuildsAsync(fixture)).Single(row => row.RebuildId == stopped.RebuildId).State);
        Assert.Null((await SingleBindingAsync(fixture)).ReleasedAt);

        await UnloadAtTheGateAsync(fixture, FirstDemandId);

        Assert.Equal(JourneyRuntimeStage.Completed, (await fixture.RuntimeAsync(FirstDemandId)).Stage);
        Assert.Equal("UNLOADED_WITH_NOTHING_LEFT_ON_BOARD", (await SingleBindingAsync(fixture)).ReleasedReason);
    }

    /// <summary>
    /// 两条需求同趟、都装上车，卸货站逐条卸：第一条卸完时第二条的货还在车上，绑定（它记的是旅程的锚需求）保留；第二条卸完才释放。
    /// 绑定在这里经产品的 <c>BindCargoAsync</c> 直接放上去——被测的是卸货时释放的条件，不是绑定怎么来的。
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-0238")]
    public async Task TheFirstOfTwoUnloadsKeepsTheBindingAndTheLastReleasesIt()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(
            fixture.Demand(FirstDemandId, FirstSublot, Now.AddMinutes(-10)),
            fixture.Demand(SecondDemandId, SecondSublot, Now.AddMinutes(-9), area: "N1-2"));
        fixture.BoxCounts.Set(FirstSublot, 7);
        fixture.BoxCounts.Set(SecondSublot, 7);
        await TickAndRunAsync(fixture);
        await Batch7ThreeStopJourneyTests.AppendSecondDemandAsync(fixture);
        await ArriveAtPickupAsync(fixture, FirstDemandId);
        await EnterSublotAsync(fixture, FirstDemandId, FirstSublot, FirstSubmissionId);
        await SettleLoadAsync(fixture, FirstDemandId);
        await AnswerDepartureSafetyAsync(fixture, FirstDemandId, FirstSafetyResultId);
        await ArriveAtCurrentStopAsync(fixture, SecondDemandId, "TO_GATE");
        await EnterSublotAsync(fixture, SecondDemandId, SecondSublot, SecondSubmissionId);
        await SettleLoadAsync(fixture, SecondDemandId);
        await AnswerDepartureSafetyAsync(fixture, SecondDemandId, SecondSafetyResultId);
        await BindAsync(fixture, FirstDemandId);

        await ArriveAtCurrentStopAsync(fixture, FirstDemandId, "TO_GATE");
        await ApplySafeResultAsync(fixture, FirstDemandId, SlotOperationType.Unload, SlotBusinessState.Empty);
        await TickAndRunAsync(fixture);
        fixture.Context.ChangeTracker.Clear();
        Assert.Equal(
            JourneyDemandStatuses.Unloaded,
            (await fixture.Context.Set<JourneyDemandRow>().AsNoTracking().SingleAsync(row => row.DemandId == FirstDemandId, Token)).Status);
        Assert.Null((await SingleBindingAsync(fixture)).ReleasedAt);

        await ApplySafeResultAsync(fixture, SecondDemandId, SlotOperationType.Unload, SlotBusinessState.Empty);
        await TickAndRunAsync(fixture);
        fixture.Context.ChangeTracker.Clear();

        Assert.Equal(JourneyRuntimeStage.Completed, (await fixture.RuntimeAsync(FirstDemandId)).Stage);
        Assert.Equal("UNLOADED_WITH_NOTHING_LEFT_ON_BOARD", (await SingleBindingAsync(fixture)).ReleasedReason);
    }

    // ---- 二、新故障只认本趟旅程的货 --------------------------------------------------------------------------------------

    /// <summary>
    /// 探针 P2：库里留着上一趟的在效绑定，这一趟装着自己的货在路上因门锁被按住、锁住，门锁恢复后自动解除。人按「继续原单」：修之前被
    /// <c>RESUME_CARGO_BINDING_MISMATCH</c> 拒绝——这次故障沿用了上一趟那条绑定，没给这一趟的货建绑定。现在上一趟那条以
    /// <c>NOT_CARGO_OF_THE_VEHICLES_CURRENT_JOURNEY</c> 释放，这一趟的货自己绑上，继续原单成功，这条绑定随之释放。
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-0238")]
    [Trait("Requirement", "REQ-0239")]
    public async Task ALoadedSecondTripHeldForTheDoorsResumesOnItsOwnCargo()
    {
        await using RuntimeFixture fixture = await CompletedTripLeavingALiveBindingAsync();
        await DispatchSecondDemandAsync(fixture);
        await ArriveAtCurrentStopAsync(fixture, SecondDemandId, "TO_PICKUP");
        await EnterSublotAsync(fixture, SecondDemandId, SecondSublot, SecondSubmissionId);
        await SettleLoadAsync(fixture, SecondDemandId);
        JourneyRuntimeRow toGate = await AnswerDepartureSafetyAsync(
            fixture, SecondDemandId, SecondSafetyResultId, await SafetyRevisionAsync(fixture));
        Assert.Equal(JourneyRuntimeStage.AwaitingGateArrival, toGate.Stage);
        string upperId = (await CurrentStopAsync(fixture, SecondDemandId)).UpperId!;
        await ForgetFirstTripsOrdersAsync(fixture);

        await LatchForTheDoorsAsync(fixture, upperId);
        await ReportLockedAsync(fixture);
        await DriveOneRoundAsync(fixture);
        fixture.EmergencyLatched = false;
        await DriveOneRoundAsync(fixture);
        await DriveOneRoundAsync(fixture);

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
            VehicleFaultRecoveryTests.Clear(fixture) with { Action = VehicleFaultRecoveryAction.ResumeHeldOrder }, Token);
        fixture.Context.ChangeTracker.Clear();

        Assert.True(VehicleFaultRecoveryOutcome.Resumed == resumed.Outcome, string.Join(", ", resumed.Reasons));
        FaultedVehicleCargoRow[] bindings = await BindingsAsync(fixture);
        Assert.Equal(
            [
                (FirstDemandId, "NOT_CARGO_OF_THE_VEHICLES_CURRENT_JOURNEY"),
                (SecondDemandId, "RESUMED_ON_ORIGINAL_VEHICLE"),
            ],
            bindings.OrderBy(row => row.DemandId, StringComparer.Ordinal).Select(row => (row.DemandId, row.ReleasedReason)));
    }

    /// <summary>
    /// 探针 P1：库里留着上一趟的在效绑定，这一趟空车去取货，单 FAILED、人清除。修之前清除按「车上有货」处置，重建永远等在
    /// <c>CARGO_SLOTS_UNKNOWN</c> 上。现在故障不认那条绑定：清除按「车上没货」处置，延迟过后照常重建去取货，只建一张。
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-0238")]
    [Trait("Requirement", "REQ-0362")]
    public async Task AnEmptySecondTripWhoseOrderFailedIsClearedAsNothingOnBoardAndRebuilt()
    {
        await using RuntimeFixture fixture = await CompletedTripLeavingALiveBindingAsync();
        await DispatchSecondDemandAsync(fixture);
        int pickupCreates = fixture.Riot.CreateCount("TO_PICKUP");
        fixture.Riot.FailOrder((await fixture.RuntimeAsync(SecondDemandId)).PickupUpperId);
        await TickAndRunAsync(fixture);
        fixture.Context.ChangeTracker.Clear();

        VehicleFaultRecoveryDecision cleared = await VehicleFaultRecoveryTests.Service(fixture, new(fixture))
            .RecoverAsync(VehicleFaultRecoveryTests.Clear(fixture), Token);
        fixture.Context.ChangeTracker.Clear();
        Assert.Equal(VehicleFaultRecoveryDispositions.RebuildScheduled, cleared.Disposition);
        Assert.Equal("VEHICLE_FAULT_CLEARED_NOTHING_ON_BOARD", (await fixture.RuntimeAsync(SecondDemandId)).BlockReasonCode);

        await OwnOrderRebuildTests.PassTheDelayAsync(fixture);
        for (int round = 0; round < 3; round++)
        {
            await DriveOneRoundAsync(fixture);
        }

        Assert.Equal(pickupCreates + 1, fixture.Riot.CreateCount("TO_PICKUP"));
        Assert.Equal(
            OwnOrderRebuildStates.Rebuilt,
            (await RebuildsAsync(fixture)).Single(row => row.DemandId == SecondDemandId).State);
        Assert.DoesNotContain(await BindingsAsync(fixture), row => row.ReleasedAt is null);
    }

    /// <summary>
    /// 探针 P3：库里留着上一趟的在效绑定，这一趟空车的取货单被取消、重建出来的又在窗口内被取消，护栏三停住。修之前「放弃这趟」以
    /// <c>OWN_ORDER_REBUILD_EXIT_CARGO_ON_BOARD</c> 被拒，交接又走不到底，只能改库。现在放弃成功，旧绑定以本趟之外的原因释放。
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-0361")]
    public async Task AnEmptySecondTripStoppedByTheThirdGuardCanBeGivenUp()
    {
        await using RuntimeFixture fixture = await CompletedTripLeavingALiveBindingAsync();
        await DispatchSecondDemandAsync(fixture);
        fixture.Riot.CancelOrder((await CurrentStopAsync(fixture, SecondDemandId)).UpperId!);
        await TickAndRunAsync(fixture);
        await OwnOrderRebuildTests.PassTheDelayAsync(fixture);
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        await fixture.HearFromPeerAsync();
        fixture.Riot.CancelOrder((await CurrentStopAsync(fixture, SecondDemandId)).UpperId!);
        await fixture.Engine.ExecuteOnceAsync(Token);
        await OwnOrderRebuildTests.PassTheDelayAsync(fixture);
        fixture.Context.ChangeTracker.Clear();
        Assert.Equal("OWN_ORDER_REBUILD_STOPPED", (await fixture.RuntimeAsync(SecondDemandId)).BlockReasonCode);

        VehicleFaultRecoveryDecision givenUp = await VehicleFaultRecoveryTests.Service(fixture, new(fixture))
            .RecoverAsync(StoppedRebuildExitTests.GiveUp(fixture), Token);
        fixture.Context.ChangeTracker.Clear();

        Assert.True(VehicleFaultRecoveryOutcome.TripTerminated == givenUp.Outcome, string.Join(", ", givenUp.Reasons));
        Assert.Equal(JourneyRuntimeStage.Completed, (await fixture.RuntimeAsync(SecondDemandId)).Stage);
        Assert.Equal("NOT_CARGO_OF_THE_VEHICLES_CURRENT_JOURNEY", (await SingleBindingAsync(fixture)).ReleasedReason);
    }

    /// <summary>
    /// 旧版本留在库里的形状，续行入口：这一趟的故障是旧版本记下的，它沿用了上一趟的绑定、没给这一趟的货建绑定，此后引擎还没来得及再评估一轮。
    /// 人按「继续原单」时，入口先把上一趟那条释放，再判续行，不被 <c>RESUME_CARGO_BINDING_MISMATCH</c> 挡住。
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-0239")]
    public async Task AResumptionEntryReleasesALeftoverBindingBeforeJudgingTheCargo()
    {
        await using RuntimeFixture fixture = await CompletedTripLeavingALiveBindingAsync();
        await DispatchSecondDemandAsync(fixture);
        await ArriveAtCurrentStopAsync(fixture, SecondDemandId, "TO_PICKUP");
        await EnterSublotAsync(fixture, SecondDemandId, SecondSublot, SecondSubmissionId);
        await SettleLoadAsync(fixture, SecondDemandId);
        await AnswerDepartureSafetyAsync(fixture, SecondDemandId, SecondSafetyResultId, await SafetyRevisionAsync(fixture));
        string upperId = (await CurrentStopAsync(fixture, SecondDemandId)).UpperId!;
        await ForgetFirstTripsOrdersAsync(fixture);
        await LatchForTheDoorsAsync(fixture, upperId);
        await ReportLockedAsync(fixture);
        await DriveOneRoundAsync(fixture);
        fixture.EmergencyLatched = false;
        await DriveOneRoundAsync(fixture);
        await DriveOneRoundAsync(fixture);
        await RestoreWhatThePreviousVersionLeftAsync(fixture);

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
            VehicleFaultRecoveryTests.Clear(fixture) with { Action = VehicleFaultRecoveryAction.ResumeHeldOrder }, Token);
        fixture.Context.ChangeTracker.Clear();

        Assert.True(VehicleFaultRecoveryOutcome.Resumed == resumed.Outcome, string.Join(", ", resumed.Reasons));
        Assert.Equal("NOT_CARGO_OF_THE_VEHICLES_CURRENT_JOURNEY", (await SingleBindingAsync(fixture)).ReleasedReason);
    }

    /// <summary>
    /// 同上，清除入口：空车这一趟的故障是旧版本记下的、沿用了上一趟的绑定。人清除时，处置先把上一趟那条释放，再判车上有没有货——按「车上没货」处置。
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-0362")]
    public async Task AClearanceReleasesALeftoverBindingBeforeJudgingTheCargo()
    {
        await using RuntimeFixture fixture = await CompletedTripLeavingALiveBindingAsync();
        await DispatchSecondDemandAsync(fixture);
        fixture.Riot.FailOrder((await fixture.RuntimeAsync(SecondDemandId)).PickupUpperId);
        await TickAndRunAsync(fixture);
        fixture.Context.ChangeTracker.Clear();
        await RestoreWhatThePreviousVersionLeftAsync(fixture);

        VehicleFaultRecoveryDecision cleared = await VehicleFaultRecoveryTests.Service(fixture, new(fixture))
            .RecoverAsync(VehicleFaultRecoveryTests.Clear(fixture), Token);
        fixture.Context.ChangeTracker.Clear();

        Assert.Equal(VehicleFaultRecoveryDispositions.RebuildScheduled, cleared.Disposition);
        Assert.Equal("VEHICLE_FAULT_CLEARED_NOTHING_ON_BOARD", (await fixture.RuntimeAsync(SecondDemandId)).BlockReasonCode);
        Assert.Equal("NOT_CARGO_OF_THE_VEHICLES_CURRENT_JOURNEY", (await SingleBindingAsync(fixture)).ReleasedReason);
    }

    // ---- 三、不许有没有出口的等待 ----------------------------------------------------------------------------------------

    /// <summary>
    /// 旧版本已经留在库里的卡死形状：空车这一趟的重建记着「清除时车上有货」、车上还挂着一条在效绑定，重建等在 <c>CARGO_SLOTS_UNKNOWN</c> 上
    /// （修之前它会永远等下去）。现在「要查的仓位是空集」按空集判定：车上没有一条已装未卸的需求，就没有要证明的货，延迟过后照常重建，
    /// 不用人、不改库，而且只建一张。
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-0362")]
    public async Task ARebuildLeftWaitingOnAnEmptySetOfSlotsGoesOnWithoutAPerson()
    {
        await using RuntimeFixture fixture = await CompletedTripLeavingALiveBindingAsync();
        await DispatchSecondDemandAsync(fixture);
        int pickupCreates = fixture.Riot.CreateCount("TO_PICKUP");
        fixture.Riot.FailOrder((await fixture.RuntimeAsync(SecondDemandId)).PickupUpperId);
        await TickAndRunAsync(fixture);
        fixture.Context.ChangeTracker.Clear();
        await VehicleFaultRecoveryTests.Service(fixture, new(fixture)).RecoverAsync(VehicleFaultRecoveryTests.Clear(fixture), Token);
        fixture.Context.ChangeTracker.Clear();
        // What the previous version left in the store: the record under the loaded source, and the earlier trip's binding live.
        await using (ControlServerDbContext writing = new(fixture.DbOptionsForTests))
        {
            OwnOrderRebuildRow record = await writing.OwnOrderRebuilds.SingleAsync(row => row.DemandId == SecondDemandId, Token);
            record.Source = OwnOrderRebuildSources.FaultClearedCargoOnBoard;
            foreach (FaultedVehicleCargoRow binding in await writing.FaultedVehicleCargo.ToArrayAsync(Token))
            {
                binding.ReleasedAt = null;
                binding.ReleasedReason = null;
            }

            await writing.SaveChangesAsync(Token);
        }

        await OwnOrderRebuildTests.PassTheDelayAsync(fixture);
        for (int round = 0; round < 6; round++)
        {
            fixture.Clock.Advance(TimeSpan.FromSeconds(11));
            await fixture.AddCargoSnapshotAsync(fixture.Clock.GetUtcNow(), physicalState: "EMPTY");
            await DriveOneRoundAsync(fixture);
        }

        Assert.Equal(pickupCreates + 1, fixture.Riot.CreateCount("TO_PICKUP"));
        Assert.Equal(
            OwnOrderRebuildStates.Rebuilt,
            (await RebuildsAsync(fixture)).Single(row => row.DemandId == SecondDemandId).State);
    }

    /// <summary>
    /// 车上确有已装未卸的需求，库里却找不到它落定的装货批次（要查的仓位仍是空集）：证明不了、也等不来，重建停住等人
    /// （<c>CARGO_SLOTS_NOT_RECORDED</c>），不再无限等待；#345 的交接出口可用，会话开得出来。
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-0362")]
    public async Task AnOnBoardDemandWithNoRecordedSlotsStopsTheRebuildForAPerson()
    {
        Environment.SetEnvironmentVariable(ProofVariable, Proof);
        try
        {
            await using RuntimeFixture fixture = await VehicleFaultRecoveryTests.FaultedOnTheWayToGateAsync();
            await VehicleFaultRecoveryTests.Service(fixture, new(fixture)).RecoverAsync(VehicleFaultRecoveryTests.Clear(fixture), Token);
            fixture.Context.ChangeTracker.Clear();
            int[] slots;
            await using (ControlServerDbContext writing = new(fixture.DbOptionsForTests))
            {
                StationOperationRow load = await writing.StationOperations
                    .SingleAsync(row => row.OperationType == SlotOperationType.Load, Token);
                slots = System.Text.Json.JsonSerializer.Deserialize<int[]>(load.TargetSlotsJson)!;
                load.Status = StationOperationStatus.Cancelled;
                await writing.SaveChangesAsync(Token);
            }

            int gateCreates = fixture.Riot.CreateCount("TO_GATE");
            await OwnOrderRebuildTests.PassTheDelayAsync(fixture);
            fixture.Clock.Advance(TimeSpan.FromSeconds(1));
            await fixture.AddCargoSnapshotAsync(fixture.Clock.GetUtcNow(), cargoSlots: slots);
            await DriveOneRoundAsync(fixture);

            OwnOrderRebuildRow stopped = (await RebuildsAsync(fixture)).Single();
            Assert.Equal(
                (OwnOrderRebuildStates.Stopped, "CARGO_SLOTS_NOT_RECORDED"),
                (stopped.State, stopped.StoppedReason));
            Assert.Equal("OWN_ORDER_REBUILD_STOPPED", (await fixture.RuntimeAsync()).BlockReasonCode);
            Assert.Equal(gateCreates, fixture.Riot.CreateCount("TO_GATE"));

            Assert.Equal(
                VehicleFaultRecoveryOutcome.HandoffPrepared,
                (await VehicleFaultRecoveryTests.Service(fixture, new(fixture))
                    .RecoverAsync(StoppedRebuildExitTests.Prepare(fixture), Token)).Outcome);
            fixture.Context.ChangeTracker.Clear();
            await using ControlServerDbContext connection = fixture.OpenConnectionContext();
            string opened = await RecoveryProcessor(fixture, connection).ProcessAsync(
                OpenSession(fixture, FirstDemandId, slots), Connection(fixture), Token);
            Assert.Equal("ExceptionRecoverySessionOpened", FirstLineType(opened));
        }
        finally
        {
            Environment.SetEnvironmentVariable(ProofVariable, null);
        }
    }

    // ---- 四、真有货时保护不丢 ----------------------------------------------------------------------------------------------

    /// <summary>
    /// 反向：库里留着上一趟的在效绑定，这一趟真装着货在路上单 FAILED。清掉旧绑定不能连这一趟的保护一起丢：故障给这一趟的货建绑定，清除按
    /// 「车上有货」处置，重建照 cs#366 等一份到期之后的仓位读数；读数显示仓空，停住；「放弃这趟」被拒；转交接、车载端交接这一趟的货走到底，
    /// 两条绑定都了结，旅程收尾。
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-0238")]
    [Trait("Requirement", "REQ-0362")]
    public async Task ARealCargoOnASecondTripKeepsItsProtectionBesideAStaleBinding()
    {
        Environment.SetEnvironmentVariable(ProofVariable, Proof);
        try
        {
            await using RuntimeFixture fixture = await CompletedTripLeavingALiveBindingAsync();
            await DispatchSecondDemandAsync(fixture);
            await ArriveAtCurrentStopAsync(fixture, SecondDemandId, "TO_PICKUP");
            await EnterSublotAsync(fixture, SecondDemandId, SecondSublot, SecondSubmissionId);
            await SettleLoadAsync(fixture, SecondDemandId);
            await AnswerDepartureSafetyAsync(fixture, SecondDemandId, SecondSafetyResultId, await SafetyRevisionAsync(fixture));
            await ForgetFirstTripsOrdersAsync(fixture);
            int gateCreates = fixture.Riot.CreateCount("TO_GATE");
            fixture.Riot.MovementState = "MT_FINISHED";
            fixture.Riot.FailOrder((await CurrentStopAsync(fixture, SecondDemandId)).UpperId!);
            await TickAndRunAsync(fixture);
            fixture.Context.ChangeTracker.Clear();

            Assert.Contains(
                await BindingsAsync(fixture),
                row => row.DemandId == SecondDemandId && row.ReleasedAt is null);
            await VehicleFaultRecoveryTests.Service(fixture, new(fixture)).RecoverAsync(VehicleFaultRecoveryTests.Clear(fixture), Token);
            fixture.Context.ChangeTracker.Clear();
            Assert.Equal("VEHICLE_FAULT_CLEARED_CARGO_ON_BOARD", (await fixture.RuntimeAsync(SecondDemandId)).BlockReasonCode);
            await OwnOrderRebuildTests.PassTheDelayAsync(fixture);
            Assert.Equal("OWN_ORDER_REBUILD_WAITING_CARGO_EVIDENCE", (await fixture.RuntimeAsync(SecondDemandId)).BlockReasonCode);

            int[] slots = await LoadSlotsAsync(fixture, SecondDemandId);
            fixture.Clock.Advance(TimeSpan.FromSeconds(1));
            await fixture.AddCargoSnapshotAsync(fixture.Clock.GetUtcNow(), physicalState: "EMPTY", cargoSlots: slots);
            await DriveOneRoundAsync(fixture);
            Assert.Equal("OWN_ORDER_REBUILD_CARGO_NOT_IN_PLACE", (await fixture.RuntimeAsync(SecondDemandId)).BlockReasonCode);
            Assert.Equal(gateCreates, fixture.Riot.CreateCount("TO_GATE"));

            VehicleFaultRecoveryDecision givenUp = await VehicleFaultRecoveryTests.Service(fixture, new(fixture))
                .RecoverAsync(StoppedRebuildExitTests.GiveUp(fixture), Token);
            fixture.Context.ChangeTracker.Clear();
            Assert.Equal(VehicleFaultRecoveryOutcome.Refused, givenUp.Outcome);
            Assert.Equal(
                VehicleFaultRecoveryOutcome.HandoffPrepared,
                (await VehicleFaultRecoveryTests.Service(fixture, new(fixture))
                    .RecoverAsync(StoppedRebuildExitTests.Prepare(fixture), Token)).Outcome);
            fixture.Context.ChangeTracker.Clear();

            await using (ControlServerDbContext connection = fixture.OpenConnectionContext())
            {
                OnboardMessageProcessor processor = RecoveryProcessor(fixture, connection);
                OnboardConnectionState state = Connection(fixture);
                string opened = await processor.ProcessAsync(OpenSession(fixture, SecondDemandId, slots), state, Token);
                Assert.Equal("ExceptionRecoverySessionOpened", FirstLineType(opened));
                string sessionId = FirstLinePayload(opened).GetProperty("exceptionRecoverySessionId").GetString()!;
                string actionId = Guid.NewGuid().ToString("D");
                Assert.Equal("RecoveryActionAccepted", FirstLineType(await processor.ProcessAsync(
                    Action(fixture, sessionId, SecondDemandId, slots, actionId), state, Token)));
                string handoffId = (await connection.RecoveryWorkflows.AsNoTracking().SingleAsync(Token)).HandoffId!;
                Assert.Equal("DurableAck", FirstLineType(await processor.ProcessAsync(
                    HandedOff(fixture, sessionId, actionId, SecondDemandId, handoffId, slots), state, Token)));
            }

            fixture.Context.ChangeTracker.Clear();
            Assert.Equal(JourneyRuntimeStage.Completed, (await fixture.RuntimeAsync(SecondDemandId)).Stage);
            Assert.DoesNotContain(await BindingsAsync(fixture), row => row.ReleasedAt is null);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ProofVariable, null);
        }
    }

    // ---- 夹具 ----------------------------------------------------------------------------------------------------------

    /// <summary>
    /// 有货去卸货站，门锁故障、急停锁住；按住的单在 RIoT 被取消，门锁恢复、自动解除后人工清除（cs#335 的路）；引擎按「在 RIoT 被取消」重建，
    /// 拿到到期之后的仓位读数后建单确认。停在去卸货站的路上。
    /// </summary>
    private static async Task<RuntimeFixture> DoorFaultedTripRebuiltAfterACancellationAsync(bool behindTheGate)
    {
        RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(FirstDemandId, FirstSublot, createdAt: Now.AddMinutes(-10)));
        fixture.BoxCounts.Set(FirstSublot, 7);
        await fixture.AdvanceToGateArrivalAsync();
        fixture.Context.ChangeTracker.Clear();
        if (behindTheGate)
        {
            await OwnOrderRebuildTests.DropSessionOnOwnOrderAsync(fixture);
        }

        string upperId = (await fixture.RuntimeAsync()).GateUpperId!;
        await LatchForTheDoorsAsync(fixture, upperId);
        if (behindTheGate)
        {
            Assert.NotEqual(
                SessionReadiness.Ready, (await fixture.Context.SessionRecoveries.AsNoTracking().SingleAsync(Token)).Readiness);
        }

        fixture.Riot.SetOrderState(upperId, RiotOrderState.Cancelled, terminal: true);
        fixture.UnfinishedOrderIds = [];
        await ReportLockedAsync(fixture);
        await DriveOneRoundAsync(fixture);
        fixture.EmergencyLatched = false;
        for (int round = 0; round < 4; round++)
        {
            await DriveOneRoundAsync(fixture);
        }

        VehicleFaultRecoveryDecision cleared = await VehicleFaultRecoveryTests.Service(fixture, new(fixture) { HasUnfinishedOrder = false })
            .RecoverAsync(VehicleFaultRecoveryTests.Clear(fixture), Token);
        Assert.True(VehicleFaultRecoveryOutcome.Cleared == cleared.Outcome, string.Join(", ", cleared.Reasons));
        fixture.Context.ChangeTracker.Clear();
        // The ended order is final: a real onboard's session is ready again with the vehicle standing.
        await fixture.RestoreSessionReadyAsync();
        fixture.Context.ChangeTracker.Clear();
        OwnOrderRebuildRow recorded = (await RebuildsAsync(fixture)).Single();
        Assert.Equal(OwnOrderRebuildSources.CancelledInRiot, recorded.Source);
        TimeSpan untilDue = recorded.DueAt - fixture.Clock.GetUtcNow();
        if (untilDue > TimeSpan.Zero)
        {
            fixture.Clock.Advance(untilDue + TimeSpan.FromSeconds(1));
        }

        fixture.Riot.MovementState = "MT_FINISHED";
        await fixture.SetDepartureSummaryAsync();
        await DriveOneRoundAsync(fixture);
        await OwnOrderRebuildTests.ReportCargoInPlaceAndRunAsync(fixture);
        Assert.Equal(OwnOrderRebuildStates.Rebuilt, (await RebuildsAsync(fixture)).Single().State);
        return fixture;
    }

    /// <summary>门锁没锁：同一轮按住、急停；随后 RIoT 把单停成 PAUSED(7)、闩锁锁上、车停下（照 <c>InTransitDoorEmergencyReleaseTests</c>）。</summary>
    private static async Task LatchForTheDoorsAsync(RuntimeFixture fixture, string upperId)
    {
        fixture.Riot.MovementState = "MT_RUNNING";
        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { CurrentStationId = 0 };
        await fixture.ReportSafetySummaryAsync(
            allTargetSlotsLocked: false, unknownPresent: false, ["LOCK_NOT_CLOSED", "ACTION_NOT_ALLOWED_IN_STATE"]);
        await DriveOneRoundAsync(fixture);
        string orderId = await OrderIdAsync(fixture, upperId);
        fixture.Riot.SetOrderState(upperId, RiotOrderState.Paused, terminal: false);
        fixture.Riot.MovementState = "MT_PAUSED";
        fixture.EmergencyLatched = true;
        fixture.UnfinishedOrderIds = [orderId];
        await DriveOneRoundAsync(fixture);
        await using ControlServerDbContext reading = new(fixture.DbOptionsForTests);
        Assert.Equal(
            1, await reading.RiotOrderCommandAudit.AsNoTracking().CountAsync(row => row.CommandType == RiotCommandTypeNames.TriggerEmergency, Token));
    }

    /// <summary>
    /// 第一趟正常跑完，库里留着一条它的在效绑定——旧版本在 cs#335 的入口上会留下的那种行，上线时现场库里会有。
    /// </summary>
    private static async Task<RuntimeFixture> CompletedTripLeavingALiveBindingAsync()
    {
        RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(FirstDemandId, FirstSublot, createdAt: Now.AddMinutes(-10)));
        fixture.BoxCounts.Set(FirstSublot, 7);
        Assert.Equal(JourneyRuntimeStage.Completed, (await fixture.RunToCompletionAsync()).Stage);
        fixture.Context.ChangeTracker.Clear();
        await BindAsync(fixture, FirstDemandId);
        return fixture;
    }

    /// <summary>
    /// What the previous version leaves once a fault of the second trip has been evaluated: the first trip's binding live, and no
    /// binding of the second trip's own -- it bound once and took the live one for this fault's cargo.
    /// </summary>
    private static async Task RestoreWhatThePreviousVersionLeftAsync(RuntimeFixture fixture)
    {
        await using ControlServerDbContext writing = new(fixture.DbOptionsForTests);
        foreach (FaultedVehicleCargoRow binding in await writing.FaultedVehicleCargo.ToArrayAsync(Token))
        {
            if (binding.DemandId == FirstDemandId)
            {
                binding.ReleasedAt = null;
                binding.ReleasedReason = null;
            }
            else
            {
                writing.FaultedVehicleCargo.Remove(binding);
            }
        }

        await writing.SaveChangesAsync(Token);
    }

    private static async Task BindAsync(RuntimeFixture fixture, string demandId)
    {
        await using ControlServerDbContext writing = new(fixture.DbOptionsForTests);
        string key = await writing.AcceptedDemands.AsNoTracking()
            .Where(row => row.DemandId == demandId).Select(row => row.TransportDemandKey).SingleAsync(Token);
        await new VehicleFaultStore(writing).BindCargoAsync(
            fixture.Options.AgvId, 1, demandId, null, key, loadingWitnessed: true, fixture.Clock.GetUtcNow(), Token);
        Assert.Single(await writing.FaultedVehicleCargo.AsNoTracking().Where(row => row.ReleasedAt == null).ToArrayAsync(Token));
    }

    private static async Task DispatchSecondDemandAsync(RuntimeFixture fixture)
    {
        fixture.Context.ChangeTracker.Clear();
        await fixture.RestoreSessionReadyAsync();
        fixture.Riot.MovementState = "MT_FINISHED";
        fixture.Catalog.Set([
            fixture.Demand(FirstDemandId, FirstSublot, Now.AddMinutes(-10)),
            fixture.Demand(SecondDemandId, SecondSublot, createdAt: Now.AddMinutes(-5))]);
        fixture.BoxCounts.Set(SecondSublot, 4);
        await fixture.HearFromPeerAsync();
        await TickAndRunAsync(fixture);
        fixture.Context.ChangeTracker.Clear();
        Assert.Equal(JourneyRuntimeStage.AwaitingPickupArrival, (await fixture.RuntimeAsync(SecondDemandId)).Stage);
    }

    /// <summary>
    /// The recording RIoT names every order after its purpose alone ("ORDER-TO_GATE"), so the first trip's ended orders would
    /// collide with the second trip's in its listing of unfinished orders. They are final; RIoT forgetting them changes nothing
    /// the product reads about the second trip. A limitation of the double, not a product fact.
    /// </summary>
    private static async Task ForgetFirstTripsOrdersAsync(RuntimeFixture fixture)
    {
        await using ControlServerDbContext reading = new(fixture.DbOptionsForTests);
        foreach (string ended in await reading.OrderIntents.AsNoTracking()
                     .Where(row => row.DemandId == FirstDemandId).Select(row => row.UpperId).ToArrayAsync(Token))
        {
            fixture.Riot.ForgetOrder(ended);
        }
    }

    private static async Task UnloadAtTheGateAsync(RuntimeFixture fixture, string demandId)
    {
        JourneyRuntimeRow arrived = await ArriveAtCurrentStopAsync(fixture, demandId, "TO_GATE");
        Assert.Equal(JourneyRuntimeStage.AwaitingUnloadResult, arrived.Stage);
        await ApplySafeResultAsync(fixture, demandId, SlotOperationType.Unload, SlotBusinessState.Empty);
        await TickAndRunAsync(fixture);
        fixture.Context.ChangeTracker.Clear();
    }

    private static async Task<long> SafetyRevisionAsync(RuntimeFixture fixture)
    {
        await using ControlServerDbContext reading = new(fixture.DbOptionsForTests);
        return (await reading.SessionRecoveries.AsNoTracking().SingleAsync(Token)).SafetyRevision ?? 7;
    }

    private static async Task<int[]> LoadSlotsAsync(RuntimeFixture fixture, string demandId)
    {
        await using ControlServerDbContext reading = new(fixture.DbOptionsForTests);
        return System.Text.Json.JsonSerializer.Deserialize<int[]>((await reading.StationOperations.AsNoTracking()
            .SingleAsync(row => row.DemandId == demandId && row.OperationType == SlotOperationType.Load, Token)).TargetSlotsJson)!;
    }

    private static async Task<FaultedVehicleCargoRow> SingleBindingAsync(RuntimeFixture fixture) =>
        Assert.Single(await BindingsAsync(fixture));

    private static async Task<FaultedVehicleCargoRow[]> BindingsAsync(RuntimeFixture fixture)
    {
        await using ControlServerDbContext reading = new(fixture.DbOptionsForTests);
        return await reading.FaultedVehicleCargo.AsNoTracking().ToArrayAsync(Token);
    }

    private static async Task<OwnOrderRebuildRow[]> RebuildsAsync(RuntimeFixture fixture)
    {
        await using ControlServerDbContext reading = new(fixture.DbOptionsForTests);
        return await reading.OwnOrderRebuilds.AsNoTracking().ToArrayAsync(Token);
    }

    private static async Task DriveOneRoundAsync(RuntimeFixture fixture)
    {
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        await fixture.HearFromPeerAsync();
        fixture.Context.ChangeTracker.Clear();
        await fixture.Engine.ExecuteOnceAsync(Token);
        fixture.Context.ChangeTracker.Clear();
    }

    private static Task ReportLockedAsync(RuntimeFixture fixture) =>
        fixture.ReportSafetySummaryAsync(allTargetSlotsLocked: true, unknownPresent: false, ["ACTION_NOT_ALLOWED_IN_STATE"]);

    private static async Task<string> OrderIdAsync(RuntimeFixture fixture, string upperId)
    {
        await using ControlServerDbContext reading = new(fixture.DbOptionsForTests);
        return (await reading.OrderIntents.AsNoTracking().SingleAsync(row => row.UpperId == upperId, Token)).OrderId!;
    }

    private static OnboardMessageProcessor RecoveryProcessor(RuntimeFixture fixture, ControlServerDbContext connection) =>
        TestOnboardProcessorFactory.Create(
            connection,
            new WireToGateStore(connection),
            fixture.Clock,
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Recovery:AuthenticationProofEnvironmentVariable"] = ProofVariable
                })
                .Build());

    private static OnboardConnectionState Connection(RuntimeFixture fixture) => new()
    {
        AgvId = fixture.Options.AgvId,
        SessionGeneration = 1,
        CapabilityRevision = 1,
        SafetyRevision = 7,
        Readiness = SessionReadiness.RecoveryRequired
    };

    private static string OpenSession(RuntimeFixture fixture, string demandId, int[] slots) =>
        Envelope(fixture, "ExceptionRecoverySessionRequested", new
        {
            requestId = Guid.NewGuid().ToString("D"),
            administrator = BeforeSublotOperator(fixture),
            administratorRole = "MAINTENANCE_ADMINISTRATOR",
            eventId = SessionEventId,
            demandId,
            slots,
            reason = "The cargo is not in its slots; take it out and hand it over.",
            authenticationProof = Proof
        });

    private static string Action(RuntimeFixture fixture, string sessionId, string demandId, int[] slots, string actionId) =>
        Envelope(fixture, "RecoveryActionSubmitted", new
        {
            recoveryActionId = actionId,
            exceptionRecoverySessionId = sessionId,
            action = "FAULT_CARGO_HANDOFF",
            eventId = SessionEventId,
            demandId,
            slots,
            @operator = BeforeSublotOperator(fixture),
            reason = "Take the cargo out by hand."
        });

    private static string HandedOff(
        RuntimeFixture fixture, string sessionId, string actionId, string demandId, string handoffId, int[] slots) =>
        Envelope(fixture, "FaultCargoRecoveryResult", new
        {
            exceptionRecoverySessionId = sessionId,
            recoveryActionId = actionId,
            demandId,
            handoffId,
            overallOutcome = "HANDED_OFF",
            slotResults = slots.Select(slot => new
            {
                slotNo = slot,
                outcome = "COMPLETED",
                finalPhysicalState = "EMPTY",
                lockState = "LOCKED",
                unlockOutputState = "RESET",
                reasonCodes = Array.Empty<string>()
            }).ToArray(),
            @operator = BeforeSublotOperator(fixture),
            observedAt = Now
        });

    private static string Envelope(RuntimeFixture fixture, string messageType, object payload) =>
        BeforeSublotEnvelope(fixture, Guid.NewGuid().ToString("D"), messageType, generation: 1, payload);

    private static System.Text.Json.JsonElement FirstLinePayload(string wire)
    {
        string first = wire.Split('\n', StringSplitOptions.RemoveEmptyEntries).First();
        using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(first);
        return document.RootElement.GetProperty("payload").Clone();
    }

    private static string FirstLineType(string wire)
    {
        string first = wire.Split('\n', StringSplitOptions.RemoveEmptyEntries).First();
        using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(first);
        return document.RootElement.GetProperty("messageType").GetString()!;
    }
}
