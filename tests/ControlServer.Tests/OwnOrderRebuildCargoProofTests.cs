using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Faults;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using static ControlServer.Tests.Batch7StopDrivenAdvanceDriver;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;

namespace ControlServer.Tests;

/// <summary>
/// control-server#366：车上有货时，自动重建之前先证明货物仍在原仓位（CP-0007 修订的 REQ-0360；REQ-0362 同一前提）。取消来源从此与故障清除来源
/// 用同一个判法；两种来源都不再「证明一次就记下」，而是在建单那一轮判当时能读到的最新一份快照。
/// </summary>
/// <remarks>
/// <para>
/// <b>多新才算数</b>，按服务端接收时刻比，三条同时满足：晚于发现这次取消（或清除）；晚于重建到期；这辆车在那之后被车况护栏或会话未就绪挡过的，
/// 晚于第一次观察到不再挡住的那一轮。第二条比 CP-0007 的条文更严：快照在延迟之内就到了、延迟期间货被取走，按条文字面仍会被那份旧快照放行
/// （REQ-0362 的「窗口乙」，调度 2026-09-28 定一并修）。
/// </para>
/// <para>
/// 断言落在三层：重建记录的状态与原因、旅程码、RIoT 上建过几张单。快照都在拨钟之后报，并以「报快照 → 下一轮」的节奏推进，
/// 这样每份快照的接收时刻都严格晚于它之前那一轮。
/// </para>
/// <para>码写成字面量：它们是现场拿到的东西，改名应当让这里红。</para>
/// </remarks>
public sealed class OwnOrderRebuildCargoProofTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ---- 取消来源、车上有货 ----------------------------------------------------------------------------------

    /// <summary>
    /// 装着货开往卸货站，本服务端的单在 RIoT 被取消：延迟过了、没有到期之后的快照，不建单，记录写明在等快照；到期之后车报放货的仓是空的，
    /// 重建停住等人（与故障清除来源同一个停住原因与旅程码），需求仍记在本车上、仍是已装货。
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-0360")]
    public async Task AnEmptySlotAfterTheCancellationStopsTheRebuildForAPerson()
    {
        Cancelled cancelled = await CancelledOnTheWayToGateAsync();
        await using RuntimeFixture fixture = cancelled.Fixture;

        await OwnOrderRebuildTests.PassTheDelayAsync(fixture);
        await AssertWaitingAsync(fixture, cancelled, "CARGO_EVIDENCE_NOT_RECEIVED", "OWN_ORDER_REBUILD_WAITING_CARGO_EVIDENCE");

        await ReportCargoAsync(fixture, physicalState: "EMPTY");
        await TickAndHearAsync(fixture);

        await AssertStoppedAsync(fixture, cancelled);
    }

    /// <summary>取消后车一直没报到期之后的快照：过多少个延迟都不建，记录停在「在等快照」，车保持阻断。</summary>
    [Fact]
    [Trait("Requirement", "REQ-0360")]
    public async Task WithoutASnapshotAfterTheCancellationTheRebuildWaits()
    {
        Cancelled cancelled = await CancelledOnTheWayToGateAsync();
        await using RuntimeFixture fixture = cancelled.Fixture;

        for (int round = 0; round < 5; round++)
        {
            await OwnOrderRebuildTests.PassTheDelayAsync(fixture);
        }

        await AssertWaitingAsync(fixture, cancelled, "CARGO_EVIDENCE_NOT_RECEIVED", "OWN_ORDER_REBUILD_WAITING_CARGO_EVIDENCE");
    }

    /// <summary>
    /// 快照证明不了（仓没锁）：等，不建、不停；下一份证明货在、锁闭、输出复位，就重建开往卸货站的单，承载同一条需求。
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-0360")]
    public async Task AnInconclusiveSnapshotWaitsAndACargoShownInPlaceRebuildsToTheGate()
    {
        Cancelled cancelled = await CancelledOnTheWayToGateAsync();
        await using RuntimeFixture fixture = cancelled.Fixture;
        await OwnOrderRebuildTests.PassTheDelayAsync(fixture);

        await ReportCargoAsync(fixture, lockState: "UNLOCKED");
        await TickAndHearAsync(fixture);
        await AssertWaitingAsync(fixture, cancelled, "SLOT_", "OWN_ORDER_REBUILD_CARGO_UNPROVEN");
        Assert.Contains("UNLOCKED", (await RebuildAsync(fixture)).WaitingReason, StringComparison.Ordinal);

        await ReportCargoAsync(fixture);
        await TickAndHearAsync(fixture);

        await AssertRebuiltToTheGateAsync(fixture, cancelled);
    }

    /// <summary>
    /// 「多新」第 2、3 条：取消之后别人按了急停；锁着期间先来一份证明货在的快照，货随后被取走、又来一份仓空的快照。急停解开的那一轮不建——
    /// 锁着期间的快照都不算数；解开之后车报放货的仓是空的，停住。
    /// </summary>
    /// <remarks>修之前：解开的那一轮就建开往卸货站的单（取消来源不读快照），cs#349 第 5 格。</remarks>
    [Fact]
    [Trait("Requirement", "REQ-0360")]
    public async Task CargoProvenThenTakenUnderSomeoneElsesStopIsNotRebuilt()
    {
        Cancelled cancelled = await CancelledOnTheWayToGateAsync();
        await using RuntimeFixture fixture = cancelled.Fixture;
        Latch(fixture);
        await OwnOrderRebuildTests.PassTheDelayAsync(fixture);
        await ReportCargoAsync(fixture);
        await TickAndHearAsync(fixture);
        await ReportCargoAsync(fixture, physicalState: "EMPTY");
        await TickAndHearAsync(fixture);
        Assert.Equal(cancelled.GateCreates, fixture.Riot.CreateCount("TO_GATE"));
        Assert.Contains("RIOT_EMERGENCY_NOT_OK", (await RebuildAsync(fixture)).WaitingReason, StringComparison.Ordinal);

        Unlatch(fixture);
        await TickAndHearAsync(fixture);
        await AssertWaitingAsync(fixture, cancelled, "CARGO_EVIDENCE_NOT_RECEIVED", "OWN_ORDER_REBUILD_WAITING_CARGO_EVIDENCE");

        await ReportCargoAsync(fixture, physicalState: "EMPTY");
        await TickAndHearAsync(fixture);
        await AssertStoppedAsync(fixture, cancelled);
    }

    /// <summary>
    /// 「多新」第 2 条单独看：锁着期间收到一份证明货在的快照，解开之后它不算数，要等解开之后的那一份；那一份证明货在，才重建。
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-0360")]
    public async Task ASnapshotReceivedBeforeTheRecoveryDoesNotCount()
    {
        Cancelled cancelled = await CancelledOnTheWayToGateAsync();
        await using RuntimeFixture fixture = cancelled.Fixture;
        Latch(fixture);
        await OwnOrderRebuildTests.PassTheDelayAsync(fixture);
        await ReportCargoAsync(fixture);
        await TickAndHearAsync(fixture);

        Unlatch(fixture);
        await TickAndHearAsync(fixture);
        await AssertWaitingAsync(fixture, cancelled, "CARGO_EVIDENCE_NOT_RECEIVED", "OWN_ORDER_REBUILD_WAITING_CARGO_EVIDENCE");

        await ReportCargoAsync(fixture);
        await TickAndHearAsync(fixture);
        await AssertRebuiltToTheGateAsync(fixture, cancelled);
    }

    /// <summary>
    /// 真车载端在途单致会话未就绪（照 <c>PickupDispatchPlanPastOwnOrderTests.DropSessionOnOwnOrderAsync</c>）：未就绪期间收到的快照不算数；会话恢复
    /// 就绪的那一轮正是「不再挡住」的那一轮，这时向车要一份新的（宿主那一侧的请求到期），收到之后才重建。
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-0360")]
    public async Task WhileTheSessionIsNotReadyOnItsOwnOrderASnapshotDoesNotCountAndReadinessAsksForAFreshOne()
    {
        Cancelled cancelled = await CancelledOnTheWayToGateAsync();
        await using RuntimeFixture fixture = cancelled.Fixture;
        await OwnOrderRebuildTests.DropSessionOnOwnOrderAsync(fixture);
        await OwnOrderRebuildTests.PassTheDelayAsync(fixture);
        await ReportCargoAsync(fixture);
        await TickAndHearAsync(fixture);
        Assert.Equal(cancelled.GateCreates, fixture.Riot.CreateCount("TO_GATE"));
        Assert.Equal("ONBOARD_SESSION_NOT_READY", (await RebuildAsync(fixture)).WaitingReason);

        await fixture.RestoreSessionReadyAsync();
        fixture.Context.ChangeTracker.Clear();
        await TickAndHearAsync(fixture);
        await AssertWaitingAsync(fixture, cancelled, "CARGO_EVIDENCE_NOT_RECEIVED", "OWN_ORDER_REBUILD_WAITING_CARGO_EVIDENCE");
        Assert.True(await ClaimAsync(fixture, ready: true), "readiness must make a fresh snapshot request due");

        await ReportCargoAsync(fixture);
        await TickAndHearAsync(fixture);
        await AssertRebuiltToTheGateAsync(fixture, cancelled);
    }

    /// <summary>
    /// 两列的写法（调度 2026-09-28 的要求）：车况护栏挡着的每一轮，<c>VehicleHeldAt</c> 只在第一轮写一次，之后的轮次重建记录一列都不写；
    /// 第一次观察到不再挡住的那一轮清掉它、把 <c>CargoEvidenceNotBefore</c> 写成那一轮的时刻，并撤回快照请求让宿主重新索取。
    /// </summary>
    /// <remarks>
    /// 「一列都不写」按保存拦截器记下的列断，不按读回的值断：值不变也可能每轮都写了一遍同样的值。挡住期间的等待理由不变，
    /// <c>WaitForRebuildAsync</c> 也不写（它只在理由变化时写）。
    /// </remarks>
    [Fact]
    [Trait("Requirement", "REQ-0360")]
    public async Task WhileTheVehicleStaysHeldTheRecordIsWrittenOnceAndTheFirstFreeRoundMovesTheFloor()
    {
        Cancelled cancelled = await CancelledOnTheWayToGateAsync();
        await using RuntimeFixture fixture = cancelled.Fixture;
        Latch(fixture);
        await OwnOrderRebuildTests.PassTheDelayAsync(fixture);
        DateTimeOffset heldAt = fixture.Clock.GetUtcNow();
        OwnOrderRebuildRow firstHeld = await RebuildAsync(fixture);
        Assert.Equal((heldAt, (DateTimeOffset?)null), (firstHeld.VehicleHeldAt, firstHeld.CargoEvidenceNotBefore));

        fixture.SaveChanges.Reset();
        for (int round = 0; round < 5; round++)
        {
            await TickAndHearAsync(fixture);
        }

        Assert.DoesNotContain(
            fixture.SaveChanges.Saves.SelectMany(written => written),
            column => column.StartsWith($"{nameof(OwnOrderRebuildRow)}.", StringComparison.Ordinal));
        OwnOrderRebuildRow stillHeld = await RebuildAsync(fixture);
        Assert.Equal((heldAt, (DateTimeOffset?)null), (stillHeld.VehicleHeldAt, stillHeld.CargoEvidenceNotBefore));
        Assert.True(await ClaimAsync(fixture, ready: true), "the fixture's vehicle has not been asked yet in this generation");

        Unlatch(fixture);
        await TickAndHearAsync(fixture);
        DateTimeOffset freeRound = fixture.Clock.GetUtcNow();

        OwnOrderRebuildRow free = await RebuildAsync(fixture);
        Assert.Equal((null, (DateTimeOffset?)freeRound), (free.VehicleHeldAt, free.CargoEvidenceNotBefore));
        Assert.Null(free.CargoEvidenceRequestedGeneration);
        Assert.True(await ClaimAsync(fixture, ready: true), "the free round must make a fresh snapshot request due");
        await AssertWaitingAsync(fixture, cancelled, "CARGO_EVIDENCE_NOT_RECEIVED", "OWN_ORDER_REBUILD_WAITING_CARGO_EVIDENCE");
    }

    /// <summary>
    /// 一趟承载两条已装货的需求（另有一条待装）：一条的仓位证明货在，另一条的仓位读空，整趟停住；报出来的只有读空的那一仓，待装那条的仓位不看。
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-0360")]
    public async Task OneEmptySlotAmongTheLoadedDemandsStopsTheWholeTrip()
    {
        RuntimeFixture fixture = await OnTheWayToGateAsync();
        await using RuntimeFixture owned = fixture;
        JourneyRuntimeRow dispatched = await fixture.RuntimeAsync(FirstDemandId);
        await Batch7MultiDemandAdvanceTests.AddSecondDemandToJourneyAsync(fixture, dispatched);
        await LoadDemandAsync(fixture, SecondDemandId);
        const string thirdDemandId = "d3000000-0000-4000-8000-000000000366";
        await Batch7MultiDemandAdvanceTests.AddDemandToJourneyAsync(fixture, dispatched, thirdDemandId, "SUBLOT-366-3", slotBlock: 2);
        fixture.Context.ChangeTracker.Clear();
        int[] secondSlots = JsonSerializer.Deserialize<int[]>(
            (await Batch7MultiDemandAdvanceTests.MembershipAsync(fixture, SecondDemandId)).TargetSlotsJson)!;
        Cancelled cancelled = await CancelAsync(fixture);

        // The snapshot helper takes the cargo slots by position among every committed load's slots, sorted: the same list here.
        int[] cargoSlots = [.. (await fixture.Context.StationOperations.AsNoTracking()
                .Where(row => row.OperationType == SlotOperationType.Load && row.Status == StationOperationStatus.Committed)
                .Select(row => row.TargetSlotsJson)
                .ToArrayAsync(Token))
            .SelectMany(json => JsonSerializer.Deserialize<int[]>(json)!)
            .Distinct()
            .Order()];
        Assert.Superset(secondSlots.ToHashSet(), cargoSlots.ToHashSet());
        Assert.True(cargoSlots.Length > secondSlots.Length, "the first demand's slots are cargo slots too");

        await OwnOrderRebuildTests.PassTheDelayAsync(fixture);
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        int[] cargo = await fixture.AddCargoSnapshotAsync(
            fixture.Clock.GetUtcNow(),
            cargoSlot: position => secondSlots.Contains(cargoSlots[position])
                ? ("EMPTY", "LOCKED", "RESET")
                : ("OCCUPIED", "LOCKED", "RESET"));
        Assert.Equal(cargoSlots, cargo);
        await TickAndHearAsync(fixture);

        await AssertStoppedAsync(fixture, cancelled);
        string reason = (await RebuildAsync(fixture)).WaitingReason!;
        Assert.Equal(
            secondSlots.Select(slot => $"SLOT_{slot}:EMPTY,LOCKED,RESET"),
            reason.Split(';'));
    }

    /// <summary>去取货站的路上被取消（还没装货）：照旧直接重建，不等快照，也不留快照的痕迹。</summary>
    [Fact]
    [Trait("Requirement", "REQ-0360")]
    public async Task WithNothingLoadedTheCancelledOrderIsRebuiltWithoutASnapshot()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(FirstDemandId, FirstSublot, Now.AddMinutes(-10)));
        fixture.BoxCounts.Set(FirstSublot, 7);
        await TickAndRunAsync(fixture);
        fixture.Riot.MovementState = "MT_FINISHED";
        fixture.Context.ChangeTracker.Clear();
        JourneyRuntimeRow dispatched = await fixture.RuntimeAsync();
        JourneyStopRow[] stopsBefore = await OwnOrderRebuildTests.StopsAsync(fixture, dispatched.JourneyId);
        JourneyStopRow pickup = stopsBefore.Single(stop => stop.StopRole == JourneyStopRoles.Pickup);
        fixture.Riot.CancelOrder(pickup.UpperId);
        await TickAndHearAsync(fixture);

        await OwnOrderRebuildTests.PassTheDelayAsync(fixture);

        Assert.Equal(2, fixture.Riot.CreateCount("TO_PICKUP"));
        await OwnOrderRebuildTests.AssertRebuiltAsync(fixture, dispatched, stopsBefore, pickup);
        OwnOrderRebuildRow rebuilt = await RebuildAsync(fixture);
        Assert.Equal((OwnOrderRebuildStates.Rebuilt, null, null), (rebuilt.State, rebuilt.CargoEvidenceMessageId, rebuilt.CargoProvenAt));
    }

    /// <summary>
    /// cs#367 接进来的入口：装着货开往卸货站的单在确认之前就被取消（建单应答丢了），同样要先证明货在原仓——没有到期之后的快照不建；仓空停住。
    /// </summary>
    /// <remarks>它的入口不同于确认过的单，只测确认过的那条路看不见这一格（cs#367 的交界评论）。</remarks>
    [Fact]
    [Trait("Requirement", "REQ-0360")]
    public async Task AGateOrderCancelledBeforeConfirmationWithCargoOnBoardNeedsTheSameProof()
    {
        RuntimeFixture fixture = await FailedOrderBeforeConfirmationTests.GateCreateAnswerLostAsync();
        await using RuntimeFixture owned = fixture;
        Cancelled cancelled = await CancelAsync(fixture);

        await OwnOrderRebuildTests.PassTheDelayAsync(fixture);
        await AssertWaitingAsync(fixture, cancelled, "CARGO_EVIDENCE_NOT_RECEIVED", "OWN_ORDER_REBUILD_WAITING_CARGO_EVIDENCE");

        await ReportCargoAsync(fixture, physicalState: "EMPTY");
        await TickAndHearAsync(fixture);
        await AssertStoppedAsync(fixture, cancelled);
    }

    // ---- 故障清除来源（REQ-0362）：「证明一次就记下」的两个窗口 --------------------------------------------------

    /// <summary>
    /// 窗口甲：清除之后别人按了急停；锁着期间车报的快照证明货在，于是记下「已证明」，车况护栏挡着不建。锁着期间货被取走、车报仓空；急停解开，
    /// 修之前的那一轮直接建单（记下的证明不再重看）。修之后：锁着期间的快照都不算，解开之后读到仓空，停住。
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-0362")]
    public async Task FaultWindowACargoProvenThenTakenWhileTheVehicleIsHeldIsNotRebuilt()
    {
        Cleared cleared = await ClearedWithCargoOnBoardAsync();
        await using RuntimeFixture fixture = cleared.Fixture;
        Latch(fixture);
        await OwnOrderRebuildTests.PassTheDelayAsync(fixture);
        await ReportCargoAsync(fixture);
        await TickAndHearAsync(fixture);
        await ReportCargoAsync(fixture, physicalState: "EMPTY");
        await TickAndHearAsync(fixture);
        Assert.Equal(cleared.GateCreates, fixture.Riot.CreateCount("TO_GATE"));

        Unlatch(fixture);
        await TickAndHearAsync(fixture);
        Assert.Equal(cleared.GateCreates, fixture.Riot.CreateCount("TO_GATE"));
        Assert.Equal(OwnOrderRebuildStates.Pending, (await RebuildAsync(fixture)).State);

        await ReportCargoAsync(fixture, physicalState: "EMPTY");
        await TickAndHearAsync(fixture);
        Assert.Equal(cleared.GateCreates, fixture.Riot.CreateCount("TO_GATE"));
        OwnOrderRebuildRow stopped = await RebuildAsync(fixture);
        Assert.Equal(
            (OwnOrderRebuildStates.Stopped, OwnOrderRebuilds.CargoNotProvenInOriginalSlots),
            (stopped.State, stopped.StoppedReason));
        Assert.Equal("OWN_ORDER_REBUILD_CARGO_NOT_IN_PLACE", (await fixture.RuntimeAsync()).BlockReasonCode);
    }

    /// <summary>
    /// 窗口乙：清除之后车立刻报来一份证明货在的快照（宿主在记下重建后的第一条入站就索取），延迟期间货被取走，之后车没有再报。修之前延迟一到就建单；
    /// 修之后那份快照早于到期，不算数，等到期之后的那一份，读到仓空就停住。
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-0362")]
    public async Task FaultWindowBASnapshotFromWithinTheDelayDoesNotProveTheCargoAtTheRebuild()
    {
        Cleared cleared = await ClearedWithCargoOnBoardAsync();
        await using RuntimeFixture fixture = cleared.Fixture;
        await ReportCargoAsync(fixture);
        await TickAndHearAsync(fixture);
        Assert.Equal(cleared.GateCreates, fixture.Riot.CreateCount("TO_GATE"));

        await OwnOrderRebuildTests.PassTheDelayAsync(fixture);
        Assert.Equal(cleared.GateCreates, fixture.Riot.CreateCount("TO_GATE"));
        OwnOrderRebuildRow waiting = await RebuildAsync(fixture);
        Assert.Equal((OwnOrderRebuildStates.Pending, "CARGO_EVIDENCE_NOT_RECEIVED"), (waiting.State, waiting.WaitingReason));

        await ReportCargoAsync(fixture, physicalState: "EMPTY");
        await TickAndHearAsync(fixture);
        Assert.Equal(cleared.GateCreates, fixture.Riot.CreateCount("TO_GATE"));
        Assert.Equal(OwnOrderRebuildStates.Stopped, (await RebuildAsync(fixture)).State);
    }

    // ---- 夹具 ----------------------------------------------------------------------------------------------

    private sealed record Cancelled(
        RuntimeFixture Fixture, JourneyRuntimeRow Dispatched, JourneyStopRow[] StopsBefore, JourneyStopRow Unload, int GateCreates);

    private sealed record Cleared(RuntimeFixture Fixture, int GateCreates);

    private static async Task<RuntimeFixture> OnTheWayToGateAsync()
    {
        RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(FirstDemandId, FirstSublot, createdAt: Now.AddMinutes(-10)));
        fixture.BoxCounts.Set(FirstSublot, 7);
        await fixture.AdvanceToGateArrivalAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingGateArrival, (await fixture.RuntimeAsync(FirstDemandId)).Stage);
        fixture.Riot.MovementState = "MT_FINISHED";
        fixture.Context.ChangeTracker.Clear();
        return fixture;
    }

    private static async Task<Cancelled> CancelledOnTheWayToGateAsync() => await CancelAsync(await OnTheWayToGateAsync());

    /// <summary>取消旅程开往卸货站的那张单，跑一轮：记下取消来源的重建，在等延迟。</summary>
    private static async Task<Cancelled> CancelAsync(RuntimeFixture fixture)
    {
        JourneyRuntimeRow dispatched = await fixture.RuntimeAsync(FirstDemandId);
        JourneyStopRow[] stopsBefore = await OwnOrderRebuildTests.StopsAsync(fixture, dispatched.JourneyId);
        JourneyStopRow unload = stopsBefore.Single(stop => stop.StopRole == JourneyStopRoles.Unload);
        Assert.Equal(dispatched.GateUpperId, unload.UpperId);
        int gateCreates = fixture.Riot.CreateCount("TO_GATE");
        fixture.Riot.CancelOrder(unload.UpperId);
        await TickAndHearAsync(fixture);
        OwnOrderRebuildRow recorded = await RebuildAsync(fixture);
        Assert.Equal(
            (OwnOrderRebuildSources.CancelledInRiot, OwnOrderRebuildStates.Pending),
            (recorded.Source, recorded.State));
        Assert.Equal(JourneyDemandStatuses.Loaded, (await Batch7MultiDemandAdvanceTests.MembershipAsync(fixture, FirstDemandId)).Status);
        return new Cancelled(fixture, dispatched, stopsBefore, unload, gateCreates);
    }

    /// <summary>装着货开往卸货站的单 FAILED，人清除故障：记下「车上有货」来源的重建，在等延迟。</summary>
    private static async Task<Cleared> ClearedWithCargoOnBoardAsync()
    {
        RuntimeFixture fixture = await VehicleFaultRecoveryTests.FaultedOnTheWayToGateAsync();
        Assert.Equal(
            VehicleFaultRecoveryDispositions.RebuildScheduled,
            (await VehicleFaultRecoveryTests.Service(fixture, new(fixture))
                .RecoverAsync(VehicleFaultRecoveryTests.Clear(fixture), Token)).Disposition);
        fixture.Context.ChangeTracker.Clear();
        Assert.Equal(OwnOrderRebuildSources.FaultClearedCargoOnBoard, (await RebuildAsync(fixture)).Source);
        return new Cleared(fixture, fixture.Riot.CreateCount("TO_GATE"));
    }

    /// <summary>
    /// 把 <paramref name="demandId"/> 写成已装货：归属行转 <c>LOADED</c>，照锚需求那一条已提交的装货批次加一条它自己的，仓位是它归属行上的仓位。
    /// 直接写库：一站多条需求真实装货的路径不是本票要测的。
    /// </summary>
    private static async Task LoadDemandAsync(RuntimeFixture fixture, string demandId)
    {
        await using ControlServerDbContext writing = new(fixture.DbOptionsForTests);
        JourneyDemandRow membership = await writing.Set<JourneyDemandRow>().SingleAsync(row => row.DemandId == demandId, Token);
        membership.Status = JourneyDemandStatuses.Loaded;
        StationOperationRow anchor = await writing.StationOperations.AsNoTracking()
            .SingleAsync(row => row.DemandId == FirstDemandId && row.OperationType == SlotOperationType.Load &&
                                row.Status == StationOperationStatus.Committed, Token);
        writing.StationOperations.Add(new StationOperationRow
        {
            SlotOperationAttemptId = membership.LoadSlotOperationAttemptId,
            DemandId = demandId,
            SublotId = $"{anchor.SublotId}-{demandId[..4]}",
            TargetSlotsJson = membership.TargetSlotsJson,
            OperationType = SlotOperationType.Load,
            ForcedRecoveryGeneration = anchor.ForcedRecoveryGeneration,
            ContentHash = $"L1-366-{demandId}",
            Status = StationOperationStatus.Committed,
            EvidenceJson = anchor.EvidenceJson,
            CreatedAt = anchor.CreatedAt,
            CommittedAt = anchor.CommittedAt,
        });
        await writing.SaveChangesAsync(Token);
    }

    /// <summary>拨一秒，车报一份快照：接收时刻严格晚于上一轮。</summary>
    private static async Task ReportCargoAsync(
        RuntimeFixture fixture, string physicalState = "OCCUPIED", string lockState = "LOCKED")
    {
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        await fixture.AddCargoSnapshotAsync(fixture.Clock.GetUtcNow(), physicalState: physicalState, lockState: lockState);
        fixture.Context.ChangeTracker.Clear();
    }

    private static async Task AssertWaitingAsync(RuntimeFixture fixture, Cancelled cancelled, string waitingPrefix, string journeyCode)
    {
        Assert.Equal(cancelled.GateCreates, fixture.Riot.CreateCount("TO_GATE"));
        OwnOrderRebuildRow waiting = await RebuildAsync(fixture);
        Assert.Equal(OwnOrderRebuildStates.Pending, waiting.State);
        Assert.StartsWith(waitingPrefix, waiting.WaitingReason ?? "", StringComparison.Ordinal);
        Assert.Equal(journeyCode, (await fixture.RuntimeAsync(FirstDemandId)).BlockReasonCode);
    }

    private static async Task AssertStoppedAsync(RuntimeFixture fixture, Cancelled cancelled)
    {
        Assert.Equal(cancelled.GateCreates, fixture.Riot.CreateCount("TO_GATE"));
        OwnOrderRebuildRow stopped = await RebuildAsync(fixture);
        Assert.Equal(
            (OwnOrderRebuildSources.CancelledInRiot, OwnOrderRebuildStates.Stopped, OwnOrderRebuilds.CargoNotProvenInOriginalSlots),
            (stopped.Source, stopped.State, stopped.StoppedReason));
        Assert.Contains("EMPTY", stopped.WaitingReason, StringComparison.Ordinal);
        Assert.NotNull(stopped.CargoEvidenceMessageId);
        JourneyRuntimeRow journey = await fixture.RuntimeAsync(FirstDemandId);
        Assert.Equal(
            (cancelled.Dispatched.JourneyId, JourneyRuntimeStage.AwaitingGateArrival, "OWN_ORDER_REBUILD_CARGO_NOT_IN_PLACE"),
            (journey.JourneyId, journey.Stage, journey.BlockReasonCode));
        JourneyDemandRow membership = await Batch7MultiDemandAdvanceTests.MembershipAsync(fixture, FirstDemandId);
        Assert.Equal((JourneyDemandStatuses.Loaded, (DateTimeOffset?)null), (membership.Status, membership.RemovedAt));
    }

    private static async Task AssertRebuiltToTheGateAsync(RuntimeFixture fixture, Cancelled cancelled)
    {
        Assert.Equal(cancelled.GateCreates + 1, fixture.Riot.CreateCount("TO_GATE"));
        await OwnOrderRebuildTests.AssertRebuiltAsync(fixture, cancelled.Dispatched, cancelled.StopsBefore, cancelled.Unload);
        OwnOrderRebuildRow rebuilt = await RebuildAsync(fixture);
        Assert.Equal(OwnOrderRebuildStates.Rebuilt, rebuilt.State);
        Assert.NotNull(rebuilt.CargoEvidenceMessageId);
        Assert.Equal(JourneyDemandStatuses.Loaded, (await Batch7MultiDemandAdvanceTests.MembershipAsync(fixture, FirstDemandId)).Status);
    }

    /// <summary>RIoT 报这辆车急停锁住：故障协调器读的闩锁与重建车况护栏读的安全原因码一起拨（同 <c>EmergencyReleaseVersusOwnOrderRebuildTests</c>）。</summary>
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

    /// <summary>这辆车唯一的一条重建记录（每个用例只取消或清除一次）。</summary>
    private static async Task<OwnOrderRebuildRow> RebuildAsync(RuntimeFixture fixture)
    {
        await using ControlServerDbContext reading = new(fixture.DbOptionsForTests);
        return await reading.OwnOrderRebuilds.AsNoTracking().SingleAsync(Token);
    }

    private static async Task<bool> ClaimAsync(RuntimeFixture fixture, bool ready)
    {
        await using ControlServerDbContext claiming = new(fixture.DbOptionsForTests);
        long generation = (await claiming.SessionRecoveries.AsNoTracking().SingleAsync(Token)).SessionGeneration;
        return await OwnOrderRebuilds.ClaimCargoEvidenceRequestAsync(
            claiming, fixture.Options.AgvId, generation, ready, fixture.Clock.GetUtcNow(), Token);
    }
}
