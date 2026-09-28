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
        DateTimeOffset? heldAt = (await RebuildAsync(fixture)).VehicleHeldAt;
        Assert.NotNull(heldAt);

        // Still not ready: the record is not written again round after round (the coordinator's requirement of 2026-09-28).
        fixture.SaveChanges.Reset();
        for (int round = 0; round < 3; round++)
        {
            await TickAndHearAsync(fixture);
        }

        Assert.DoesNotContain(
            fixture.SaveChanges.Saves.SelectMany(written => written),
            column => column.StartsWith($"{nameof(OwnOrderRebuildRow)}.", StringComparison.Ordinal));
        Assert.Equal(heldAt, (await RebuildAsync(fixture)).VehicleHeldAt);

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
    /// 取消来源的窗口乙（调度 2026-09-28 要求单列）：取消之后车立刻报来一份证明货在的快照（宿主在记下重建后的第一条入站就索取），延迟期间货被取走，
    /// 之后车没有再报。修之前延迟一到就建开往卸货站的单；修之后那份快照早于到期，不算数，等到期之后的那一份，读到仓空就停住。
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-0360")]
    public async Task ASnapshotFromWithinTheDelayDoesNotProveTheCargoOfACancelledTrip()
    {
        Cancelled cancelled = await CancelledOnTheWayToGateAsync();
        await using RuntimeFixture fixture = cancelled.Fixture;
        await ReportCargoAsync(fixture);
        await TickAndHearAsync(fixture);
        Assert.Equal(cancelled.GateCreates, fixture.Riot.CreateCount("TO_GATE"));
        Assert.True((await RebuildAsync(fixture)).DueAt > fixture.Clock.GetUtcNow(), "the snapshot must come within the delay");

        await OwnOrderRebuildTests.PassTheDelayAsync(fixture);
        await AssertWaitingAsync(fixture, cancelled, "CARGO_EVIDENCE_NOT_RECEIVED", "OWN_ORDER_REBUILD_WAITING_CARGO_EVIDENCE");

        await ReportCargoAsync(fixture, physicalState: "EMPTY");
        await TickAndHearAsync(fixture);
        await AssertStoppedAsync(fixture, cancelled);
    }

    /// <summary>
    /// 车静止、只有心跳（调度 2026-09-28 要求）：「到期之后重新索取」靠的是车的下一条入站消息。这里车除了心跳什么都不发，只在被要时回一份快照
    /// （货在）；每一轮先让宿主判要不要索取（与 <c>OnboardMessageProcessor</c> 每条入站做的是同一个调用），要了就回。到期之后几条心跳之内就要到
    /// 新快照并建单，建单之后不再要。
    /// </summary>
    /// <remarks>
    /// 修复前也是绿的，而且应当是绿的：故障来源那时在清除后要一次、延迟一到就用那份快照建单；取消来源那时根本不看快照，到期就建。这一格守的是
    /// 修复之后「多了一次撤回与重新索取」不会让只有心跳的车卡住。它不判别「晚于到期」这条规则本身——那是窗口乙两条的事。
    /// </remarks>
    [Theory]
    [InlineData("cancelled")]
    [InlineData("fault-cleared")]
    [Trait("Requirement", "REQ-0360")]
    [Trait("Requirement", "REQ-0362")]
    public async Task AVehicleThatOnlySendsHeartbeatsIsAskedAgainAfterTheDueTimeAndTheTripIsRebuilt(string source)
    {
        RuntimeFixture fixture;
        int gateCreates;
        if (source == "cancelled")
        {
            Cancelled cancelled = await CancelledOnTheWayToGateAsync();
            (fixture, gateCreates) = (cancelled.Fixture, cancelled.GateCreates);
        }
        else
        {
            Cleared cleared = await ClearedWithCargoOnBoardAsync();
            (fixture, gateCreates) = (cleared.Fixture, cleared.GateCreates);
        }

        await using RuntimeFixture owned = fixture;
        DateTimeOffset dueAt = (await RebuildAsync(fixture)).DueAt;
        int requests = 0;
        int rounds = 0;
        while (fixture.Riot.CreateCount("TO_GATE") == gateCreates && rounds < 60)
        {
            rounds++;
            fixture.Clock.Advance(TimeSpan.FromSeconds(1));
            await fixture.HearFromPeerAsync();
            if (await ClaimAsync(fixture, ready: true))
            {
                requests++;
                await fixture.AddCargoSnapshotAsync(fixture.Clock.GetUtcNow());
            }

            fixture.Context.ChangeTracker.Clear();
            await fixture.Engine.ExecuteOnceAsync(Token);
            fixture.Context.ChangeTracker.Clear();
        }

        Assert.Equal(gateCreates + 1, fixture.Riot.CreateCount("TO_GATE"));
        Assert.True(fixture.Clock.GetUtcNow() - dueAt <= TimeSpan.FromSeconds(5), $"rebuilt {fixture.Clock.GetUtcNow() - dueAt} after the due time");
        Assert.InRange(requests, 1, 2);
        Assert.Equal(OwnOrderRebuildStates.Rebuilt, (await RebuildAsync(fixture)).State);

        for (int round = 0; round < 5; round++)
        {
            fixture.Clock.Advance(TimeSpan.FromSeconds(1));
            await fixture.HearFromPeerAsync();
            Assert.False(await ClaimAsync(fixture, ready: true), "a rebuilt trip is not asked about its cargo again");
        }
    }

    /// <summary>
    /// 到期前几秒刚向车要过快照（例如刚重连过），回答在到期之前就到了、按规则不算数：到期那一轮立刻撤回那次请求，宿主在下一条入站消息时再要，
    /// 不再等满 10 秒的节流。
    /// </summary>
    /// <remarks>
    /// 反向验证 M4 在第一轮时存活：请求若是记下重建时要的，到期时早已满 10 秒，节流那一条照样会撤回，所以「请求早于下限就撤回」看不出来。
    /// 这一格把请求放在到期前 3 秒，只有那一条能让到期那一轮就再要。去掉它，车要多等至多 10 秒——不是安全问题，但它是写下的行为。
    /// </remarks>
    [Fact]
    [Trait("Requirement", "REQ-0360")]
    public async Task ARequestAnsweredJustBeforeTheDueTimeIsMadeAgainAtTheDueRound()
    {
        Cancelled cancelled = await CancelledOnTheWayToGateAsync();
        await using RuntimeFixture fixture = cancelled.Fixture;
        DateTimeOffset dueAt = (await RebuildAsync(fixture)).DueAt;
        Assert.True(await ClaimAsync(fixture, ready: true));
        fixture.Clock.Advance(dueAt - fixture.Clock.GetUtcNow() - TimeSpan.FromSeconds(3));
        await using (ControlServerDbContext writing = new(fixture.DbOptionsForTests))
        {
            // A fresh request 3 s before the due time: what a reconnection just then would leave behind.
            OwnOrderRebuildRow row = await writing.OwnOrderRebuilds.SingleAsync(Token);
            OwnOrderRebuilds.WithdrawCargoEvidenceRequest(row);
            await writing.SaveChangesAsync(Token);
        }

        Assert.True(await ClaimAsync(fixture, ready: true));
        await ReportCargoAsync(fixture);
        Assert.True(fixture.Clock.GetUtcNow() < dueAt, "the answer must arrive before the due time");

        fixture.Clock.Advance(dueAt - fixture.Clock.GetUtcNow());
        await fixture.HearFromPeerAsync();
        fixture.Context.ChangeTracker.Clear();
        await fixture.Engine.ExecuteOnceAsync(Token);
        fixture.Context.ChangeTracker.Clear();

        await AssertWaitingAsync(fixture, cancelled, "CARGO_EVIDENCE_NOT_RECEIVED", "OWN_ORDER_REBUILD_WAITING_CARGO_EVIDENCE");
        Assert.True(await ClaimAsync(fixture, ready: true), "the due round must make a fresh request due at once");
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

    /// <summary>
    /// 独立审查 M1（2026-09-28，准入线③）：已经决定建单、新单却一次都没发出去（「决定」那次保存之后、问 RIoT 之前进程停了，意图仍是
    /// <c>PENDING_RECONCILIATION</c>、<c>CreateAttemptCount</c> 为 0，记录停在 <c>ORDERING</c>）。下一轮重走建单前的检查，被挡住一次——RIoT 车况读不到、别人的急停、
    /// 会话未就绪三选一——放开之后要一份放开之后的快照。宿主必须替这种记录去要：车每轮只发心跳、被要才回（货在），几轮之内就要到、
    /// 建成一张新单，记录转 <c>REBUILT</c>，旅程码清掉。
    /// </summary>
    /// <remarks>
    /// 修之前宿主的索取只认 <c>PENDING</c>：放开之后一次都不要，旅程永远停在 <c>OWN_ORDER_REBUILD_WAITING_CARGO_EVIDENCE</c>，
    /// 而 cs#345 的三个出口都要求记录是 <c>STOPPED</c>，只能改库。审查员的探针在这三格上 120 轮都是 creates=0、claims=0。
    /// 注入用的是进程停下（<c>CrashOnNextReconcileOf</c>）而不是对账答 Unknown：答 Unknown 的意图记成 <c>RESULT_UNKNOWN</c>，之后读到
    /// NotFound 也不再建单，挡不挡都一样停在 <c>OWN_ORDER_REBUILD_ORDER_UNCONFIRMED</c>——那是建单一侧早有的行为，不在本票
    /// （已报调度，evidence/cs366/review-m1/）。
    /// </remarks>
    [Theory]
    [InlineData("riot-unreadable")]
    [InlineData("emergency")]
    [InlineData("session-not-ready")]
    [Trait("Requirement", "REQ-0360")]
    public async Task ADecidedRebuildNeverSentThatIsHeldOnceIsAskedForAFreshSnapshotAndRebuilt(string hold)
    {
        (RuntimeFixture fixture, Cancelled cancelled, string newUpperId) = await DecidedButNeverSentAsync();
        await using RuntimeFixture owned = fixture;

        await HoldOneRoundAsync(fixture, hold);
        OwnOrderRebuildRow held = await RebuildAsync(fixture);
        Assert.Equal((OwnOrderRebuildStates.Ordering, cancelled.GateCreates), (held.State, fixture.Riot.CreateCount("TO_GATE")));

        int claims = await RunWithAVehicleThatAnswersAsync(fixture, rounds: 6, physicalState: "OCCUPIED");

        Assert.True(claims >= 1, "the Host must ask for a snapshot for a decided rebuild never sent");
        Assert.Equal(cancelled.GateCreates + 1, fixture.Riot.CreateCount("TO_GATE"));
        OwnOrderRebuildRow rebuilt = await RebuildAsync(fixture);
        Assert.Equal((OwnOrderRebuildStates.Rebuilt, newUpperId), (rebuilt.State, rebuilt.NewUpperId));
        JourneyRuntimeRow journey = await fixture.RuntimeAsync(FirstDemandId);
        Assert.Equal((JourneyRuntimeStage.AwaitingGateArrival, (string?)null), (journey.Stage, journey.BlockReasonCode));
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

    internal sealed record Cancelled(
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

    /// <summary>
    /// 取消来源、车上有货，到期之后车报货在；决定建单那次保存之后、问 RIoT 之前进程停了：新意图写下了、从没发出（<c>PENDING_RECONCILIATION</c>，
    /// <c>CreateAttemptCount</c> 为 0），记录 <c>ORDERING</c>。返回夹具、取消时的样子与新单号。<see cref="StoppedRebuildExitTests"/> 的交接格也用它。
    /// </summary>
    internal static async Task<(RuntimeFixture Fixture, Cancelled Cancelled, string NewUpperId)> DecidedButNeverSentAsync()
    {
        Cancelled cancelled = await CancelledOnTheWayToGateAsync();
        RuntimeFixture fixture = cancelled.Fixture;
        string newUpperId = (await RebuildAsync(fixture)).NewUpperId;
        await OwnOrderRebuildTests.PassTheDelayAsync(fixture);
        await ReportCargoAsync(fixture);
        fixture.Riot.CrashOnNextReconcileOf = newUpperId;
        await Assert.ThrowsAsync<IOException>(() => TickAndHearAsync(fixture));
        fixture.Context.ChangeTracker.Clear();

        Assert.Null(fixture.Riot.CrashOnNextReconcileOf);
        OwnOrderRebuildRow ordering = await RebuildAsync(fixture);
        Assert.Equal(OwnOrderRebuildStates.Ordering, ordering.State);
        await using ControlServerDbContext reading = new(fixture.DbOptionsForTests);
        OrderIntentRow intent = await reading.OrderIntents.AsNoTracking().SingleAsync(row => row.UpperId == newUpperId, Token);
        Assert.Equal(("PENDING_RECONCILIATION", 0), (intent.Status, intent.CreateAttemptCount));
        Assert.Equal(cancelled.GateCreates, fixture.Riot.CreateCount("TO_GATE"));
        return (fixture, cancelled, newUpperId);
    }

    /// <summary>
    /// 挡住一轮再放开：RIoT 车辆读取读不到（<c>RIOT_VEHICLE_SAFETY_UNREADABLE</c>）、别人的急停，或会话因本车在途单未就绪。
    /// </summary>
    internal static async Task HoldOneRoundAsync(RuntimeFixture fixture, string hold)
    {
        switch (hold)
        {
            case "riot-unreadable":
                fixture.Riot.BeforeReadVehicle = () => throw new HttpRequestException("L1: RIoT vehicle read failed");
                break;
            case "emergency":
                Latch(fixture);
                break;
            default:
                await OwnOrderRebuildTests.DropSessionOnOwnOrderAsync(fixture);
                break;
        }

        await TickAndHearAsync(fixture);
        Assert.NotNull((await RebuildAsync(fixture)).VehicleHeldAt);
        switch (hold)
        {
            case "riot-unreadable":
                fixture.Riot.BeforeReadVehicle = null;
                break;
            case "emergency":
                Unlatch(fixture);
                break;
            default:
                await fixture.RestoreSessionReadyAsync();
                fixture.Context.ChangeTracker.Clear();
                break;
        }
    }

    /// <summary>
    /// 车每轮只发心跳，被要才回一份快照：每轮先照宿主的做法判要不要（<see cref="OwnOrderRebuilds.ClaimCargoEvidenceRequestAsync"/>），
    /// 要了就回，再跑一轮引擎。返回要了几次。
    /// </summary>
    internal static async Task<int> RunWithAVehicleThatAnswersAsync(RuntimeFixture fixture, int rounds, string physicalState)
    {
        int claims = 0;
        for (int round = 0; round < rounds; round++)
        {
            fixture.Clock.Advance(TimeSpan.FromSeconds(1));
            await fixture.HearFromPeerAsync();
            if (await ClaimAsync(fixture, ready: true))
            {
                claims++;
                await fixture.AddCargoSnapshotAsync(fixture.Clock.GetUtcNow(), physicalState: physicalState);
            }

            fixture.Context.ChangeTracker.Clear();
            await fixture.Engine.ExecuteOnceAsync(Token);
            fixture.Context.ChangeTracker.Clear();
        }

        return claims;
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
