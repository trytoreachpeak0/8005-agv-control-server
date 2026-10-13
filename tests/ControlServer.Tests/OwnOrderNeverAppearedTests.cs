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
/// 需求腿（开往取货站、开往卸货站）建单发出过、结果未知，RIoT 之后一直查无此单（control-server#673）。修之前旅程停在
/// <c>PICKUP_ResultUnknown</c>／<c>GATE_ResultUnknown</c> 永远不动，只能改库。现在凭 control-server#611 的共用判定（连续精确查无此单满
/// 120 秒、车证明停稳、未完成订单清单读全且没有本车的单）判「没建成」，交给 control-server#318 的同车重建；凑不齐证据时由人经故障处置入口确认。
/// </summary>
/// <remarks>
/// <para>
/// 替身按真实 RIoT 的样子答：建单回 HTTP 200、业务码 <c>00002</c>（网关判结果未知，单没落地）；之后的对账是真实 RIoT 的「查无此单」——
/// HTTP 200、业务码 0、不带 result，不是只有替身才给的 404（<see cref="RecordingRiot.AbsentOrdersReadAsRealRiot"/>）。
/// </para>
/// <para>码写成字面量：它们是现场拿到的东西，改名应当让这里红。</para>
/// </remarks>
public sealed class OwnOrderNeverAppearedTests
{
    private const string OperatorId = "L1-OPERATOR-673";

    /// <summary>What event 2234 says; the recording logger keeps the level and the message.</summary>
    private const string NeverCreated = "taken as never created";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// 开往取货站（车上没货）：不到 120 秒保持 <c>PICKUP_ResultUnknown</c>、不记重建；满了判没建成，码 <c>OWN_ORDER_NEVER_APPEARED</c>，记一条
    /// 「从没出现过」来源的重建，告警事件 2234；延迟过后同车同需求重建一张新单号的单，旧单号再没发过建单；车照常到站、进等录入。
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-0360")]
    public async Task APickupLegWhoseCreateNeverAppearedIsRebuiltOnceRiotSaidNoSuchOrderLongEnough()
    {
        await using RuntimeFixture fixture = await PickupCreateRefusedAsync();
        JourneyStopRow pickup = await CurrentStopAsync(fixture, FirstDemandId);
        Assert.Equal("PICKUP_ResultUnknown", (await fixture.RuntimeAsync()).BlockReasonCode);

        await RunAfterAsync(fixture, TimeSpan.FromSeconds(100));
        Assert.Equal("PICKUP_ResultUnknown", (await fixture.RuntimeAsync()).BlockReasonCode);
        Assert.Empty(await RebuildsAsync(fixture));

        await RunAfterAsync(fixture, TimeSpan.FromSeconds(30));
        Assert.Equal("OWN_ORDER_NEVER_APPEARED", (await fixture.RuntimeAsync()).BlockReasonCode);
        OwnOrderRebuildRow recorded = Assert.Single(await RebuildsAsync(fixture));
        Assert.Equal(
            ("ORDER_NEVER_APPEARED", OwnOrderRebuildStates.Pending, pickup.UpperId, (string?)null, pickup.StopId),
            (recorded.Source, recorded.State, recorded.EndedUpperId, recorded.EndedOrderId, recorded.StopId));
        var alarm = Assert.Single(fixture.EngineLog.Entries, entry => entry.Message.Contains(NeverCreated, StringComparison.Ordinal));
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Warning, alarm.Level);
        Assert.Contains(pickup.UpperId, alarm.Message, StringComparison.Ordinal);
        Assert.Equal(1, fixture.Riot.CreateCount("TO_PICKUP"));

        await OwnOrderRebuildTests.PassTheDelayAsync(fixture);
        fixture.Context.ChangeTracker.Clear();

        Assert.Equal(2, fixture.Riot.CreateCount("TO_PICKUP"));
        OwnOrderRebuildRow rebuilt = Assert.Single(await RebuildsAsync(fixture));
        Assert.Equal(OwnOrderRebuildStates.Rebuilt, rebuilt.State);
        Assert.NotEqual(pickup.UpperId, rebuilt.NewUpperId);
        Assert.Equal(rebuilt.NewUpperId, (await CurrentStopAsync(fixture, FirstDemandId)).UpperId);
        // The old upperId was sent once and never again.
        Assert.Equal(("RESULT_UNKNOWN", 1), await IntentStateAsync(fixture, pickup.UpperId));
        Assert.Null((await fixture.RuntimeAsync()).BlockReasonCode);

        JourneyRuntimeRow arrived = await ArriveAtCurrentStopAsync(fixture, FirstDemandId, "TO_PICKUP");
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, arrived.Stage);
    }

    /// <summary>
    /// 开往卸货站（车上有货）：判没建成之后，重建照 REQ-0360（经 CP-0007 修订）先等车报一份新的仓位读数证明货在原仓、门锁着——
    /// 与被取消的单同一条路：等读数期间码 <c>OWN_ORDER_REBUILD_WAITING_CARGO_EVIDENCE</c>、不建单，宿主会向车要读数；读数到了才建。
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-0360")]
    public async Task AGateLegWithCargoOnBoardWhoseCreateNeverAppearedWaitsForTheCargoProofBeforeTheRebuild()
    {
        await using RuntimeFixture fixture = await GateCreateRefusedAsync();
        JourneyStopRow gate = await CurrentStopAsync(fixture, FirstDemandId);
        Assert.Equal("GATE_ResultUnknown", (await fixture.RuntimeAsync()).BlockReasonCode);
        Assert.Equal(1, fixture.Riot.CreateCount("TO_GATE"));

        await RunAfterAsync(fixture, TimeSpan.FromSeconds(130));
        Assert.Equal("OWN_ORDER_NEVER_APPEARED", (await fixture.RuntimeAsync()).BlockReasonCode);
        Assert.Equal(gate.UpperId, Assert.Single(await RebuildsAsync(fixture)).EndedUpperId);

        await OwnOrderRebuildTests.PassTheDelayAsync(fixture);
        fixture.Context.ChangeTracker.Clear();
        Assert.Equal(1, fixture.Riot.CreateCount("TO_GATE"));
        Assert.Equal("OWN_ORDER_REBUILD_WAITING_CARGO_EVIDENCE", (await fixture.RuntimeAsync()).BlockReasonCode);
        await using (ControlServerDbContext claiming = new(fixture.DbOptionsForTests))
        {
            long generation = (await claiming.SessionRecoveries.AsNoTracking().SingleAsync(Token)).SessionGeneration;
            Assert.True(await OwnOrderRebuilds.ClaimCargoEvidenceRequestAsync(
                claiming, fixture.Options.AgvId, generation, ready: true, fixture.Clock.GetUtcNow(), Token));
        }

        await OwnOrderRebuildTests.ReportCargoInPlaceAndRunAsync(fixture);

        Assert.Equal(2, fixture.Riot.CreateCount("TO_GATE"));
        Assert.Equal(OwnOrderRebuildStates.Rebuilt, Assert.Single(await RebuildsAsync(fixture)).State);
        Assert.Equal(JourneyRuntimeStage.AwaitingGateArrival, (await fixture.RuntimeAsync()).Stage);
        Assert.Equal(("RESULT_UNKNOWN", 1), await IntentStateAsync(fixture, gate.UpperId));
    }

    /// <summary>
    /// 窗口内单出现了（RIoT 在应答之后才把它落下）：照常确认、接上，旅程码清空；之后再久也不判没建成、不重建，只建过一张。
    /// </summary>
    [Fact]
    public async Task AnOrderThatTurnsUpWithinTheWindowIsConfirmedAndNeverRebuilt()
    {
        await using RuntimeFixture fixture = await PickupCreateRefusedAsync();
        JourneyStopRow pickup = await CurrentStopAsync(fixture, FirstDemandId);
        await RunAfterAsync(fixture, TimeSpan.FromSeconds(60));
        Assert.Equal("PICKUP_ResultUnknown", (await fixture.RuntimeAsync()).BlockReasonCode);

        fixture.Riot.ReleaseWithheldOrder(pickup.UpperId);
        await RunAfterAsync(fixture, TimeSpan.FromSeconds(1));
        Assert.Equal("CONFIRMED", (await IntentAsync(fixture, pickup.UpperId)).Status);
        Assert.Null((await fixture.RuntimeAsync()).BlockReasonCode);

        await RunAfterAsync(fixture, TimeSpan.FromSeconds(200));
        Assert.Empty(await RebuildsAsync(fixture));
        Assert.Equal(1, fixture.Riot.CreateCount("TO_PICKUP"));
        Assert.DoesNotContain(fixture.EngineLog.Entries, entry => entry.Message.Contains(NeverCreated, StringComparison.Ordinal));
    }

    /// <summary>
    /// 窗口满了但证据不齐，一律保持 <c>PICKUP_ResultUnknown</c>、什么也不记：未完成订单清单读不全、车在动、车离线。证据齐了的下一轮才判。
    /// </summary>
    [Theory]
    [InlineData("listing-incomplete")]
    [InlineData("vehicle-moving")]
    [InlineData("vehicle-offline")]
    public async Task WithoutTheWholeEvidenceTheLegIsHeldAsBefore(string missing)
    {
        await using RuntimeFixture fixture = await PickupCreateRefusedAsync();
        RiotVehicleObservation vehicle = fixture.Riot.Vehicle;
        switch (missing)
        {
            case "listing-incomplete":
                fixture.Riot.ListingComplete = false;
                break;
            case "vehicle-moving":
                fixture.Riot.Vehicle = vehicle with { Speed = 0.3, ProcState = "RUNNING" };
                break;
            default:
                fixture.Riot.Vehicle = vehicle with { Connected = false };
                break;
        }

        await RunAfterAsync(fixture, TimeSpan.FromSeconds(130));
        await RunAfterAsync(fixture, TimeSpan.FromSeconds(60));
        Assert.Equal("PICKUP_ResultUnknown", (await fixture.RuntimeAsync()).BlockReasonCode);
        Assert.Empty(await RebuildsAsync(fixture));

        fixture.Riot.ListingComplete = true;
        fixture.Riot.Vehicle = vehicle;
        await RunAfterAsync(fixture, TimeSpan.FromSeconds(1));
        Assert.Equal("OWN_ORDER_NEVER_APPEARED", (await fixture.RuntimeAsync()).BlockReasonCode);
        Assert.Single(await RebuildsAsync(fixture));
    }

    /// <summary>
    /// 意图拿到过 RIoT 单号（那张单在 RIoT 上存在过），之后 RIoT 怎么答查无此单都不判没建成：码照旧，不记重建，不再建单。
    /// </summary>
    [Fact]
    public async Task AnIntentThatEverGotAnOrderIdIsNeverTakenAsNeverCreated()
    {
        await using RuntimeFixture fixture = await PickupCreateRefusedAsync();
        string upperId = (await CurrentStopAsync(fixture, FirstDemandId)).UpperId;
        await GiveTheIntentAnOrderIdAsync(fixture, upperId);

        await RunAfterAsync(fixture, TimeSpan.FromSeconds(130));
        await RunAfterAsync(fixture, TimeSpan.FromSeconds(300));

        Assert.Empty(await RebuildsAsync(fixture));
        Assert.Equal(1, fixture.Riot.CreateCount("TO_PICKUP"));
        Assert.NotEqual("OWN_ORDER_NEVER_APPEARED", (await fixture.RuntimeAsync()).BlockReasonCode);
    }

    /// <summary>
    /// 审查 S1：建单应答带回过单号（只留在派车审计里，意图上的 <c>OrderId</c> 仍为空），之后 RIoT 怎么答查无此单都不判没建成。
    /// </summary>
    [Fact]
    public async Task ACreateAnswerThatCarriedAnOrderIdIsNeverTakenAsNeverCreated()
    {
        await using RuntimeFixture fixture = await PickupCreateRefusedAsync();
        await AuditAReturnedOrderIdAsync(fixture, (await CurrentStopAsync(fixture, FirstDemandId)).UpperId);

        await RunAfterAsync(fixture, TimeSpan.FromSeconds(130));
        await RunAfterAsync(fixture, TimeSpan.FromSeconds(300));

        Assert.Empty(await RebuildsAsync(fixture));
        Assert.Equal(1, fixture.Riot.CreateCount("TO_PICKUP"));
        Assert.Equal("PICKUP_ResultUnknown", (await fixture.RuntimeAsync()).BlockReasonCode);
    }

    /// <summary>
    /// 重建出来的新单又一次结果未知、又判没建成，落在 REQ-0361 的窗口里：第一条重建以 <c>REBUILT_ORDER_NEVER_APPEARED</c> 结束，
    /// 第二条直接停住（<c>REBUILT_ORDER_ENDED_AGAIN_WITHIN_WINDOW</c>），码 <c>OWN_ORDER_REBUILD_STOPPED</c>，不再建——一个 RIoT 一直拒绝的建单不会每几分钟循环一次。
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-0361")]
    public async Task ARebuiltOrderThatNeverAppearsEitherStopsForAPerson()
    {
        await using RuntimeFixture fixture = await PickupCreateRefusedAsync();
        await RunAfterAsync(fixture, TimeSpan.FromSeconds(130));
        OwnOrderRebuildRow first = Assert.Single(await RebuildsAsync(fixture));

        fixture.Riot.RefuseNextCreateOf = "TO_PICKUP";
        await OwnOrderRebuildTests.PassTheDelayAsync(fixture);
        fixture.Context.ChangeTracker.Clear();
        Assert.Equal(2, fixture.Riot.CreateCount("TO_PICKUP"));
        Assert.Equal("OWN_ORDER_REBUILD_ORDER_UNCONFIRMED", (await fixture.RuntimeAsync()).BlockReasonCode);

        await RunAfterAsync(fixture, TimeSpan.FromSeconds(1));
        await RunAfterAsync(fixture, TimeSpan.FromSeconds(130));

        OwnOrderRebuildRow[] records = await RebuildsAsync(fixture);
        Assert.Equal(2, records.Length);
        OwnOrderRebuildRow ended = records.Single(row => row.RebuildId == first.RebuildId);
        Assert.Equal((OwnOrderRebuildStates.Ended, "REBUILT_ORDER_NEVER_APPEARED"), (ended.State, ended.StoppedReason));
        OwnOrderRebuildRow stopped = records.Single(row => row.RebuildId != first.RebuildId);
        Assert.Equal(
            (ended.NewUpperId, "ORDER_NEVER_APPEARED", OwnOrderRebuildStates.Stopped, "REBUILT_ORDER_ENDED_AGAIN_WITHIN_WINDOW"),
            (stopped.EndedUpperId, stopped.Source, stopped.State, stopped.StoppedReason));
        Assert.Equal("OWN_ORDER_REBUILD_STOPPED", (await fixture.RuntimeAsync()).BlockReasonCode);

        await RunAfterAsync(fixture, TimeSpan.FromSeconds(300));
        Assert.Equal(2, fixture.Riot.CreateCount("TO_PICKUP"));
        Assert.Equal("OWN_ORDER_REBUILD_STOPPED", (await fixture.RuntimeAsync()).BlockReasonCode);
    }

    /// <summary>
    /// 车载端会话不就绪（闸门后面）：照样判没建成、记下重建，但不建单，码转 <c>OWN_ORDER_REBUILD_WAITING_VEHICLE</c>；会话回到就绪才建，只建一张。
    /// </summary>
    [Fact]
    public async Task BehindTheSessionGateTheLegIsRecordedAndRebuiltOnlyOnceTheSessionIsReady()
    {
        await using RuntimeFixture fixture = await PickupCreateRefusedAsync();
        await OwnOrderRebuildTests.DropSessionOnOwnOrderAsync(fixture);

        await RunAfterAsync(fixture, TimeSpan.FromSeconds(1));
        await RunAfterAsync(fixture, TimeSpan.FromSeconds(130));
        Assert.Equal("OWN_ORDER_NEVER_APPEARED", (await fixture.RuntimeAsync()).BlockReasonCode);
        Assert.Single(await RebuildsAsync(fixture));

        await OwnOrderRebuildTests.PassTheDelayAsync(fixture);
        fixture.Context.ChangeTracker.Clear();
        Assert.Equal(1, fixture.Riot.CreateCount("TO_PICKUP"));
        Assert.Equal("OWN_ORDER_REBUILD_WAITING_VEHICLE", (await fixture.RuntimeAsync()).BlockReasonCode);

        await fixture.RestoreSessionReadyAsync();
        fixture.Context.ChangeTracker.Clear();
        await RunAfterAsync(fixture, TimeSpan.FromSeconds(1));
        Assert.Equal(2, fixture.Riot.CreateCount("TO_PICKUP"));
        Assert.Equal(OwnOrderRebuildStates.Rebuilt, Assert.Single(await RebuildsAsync(fixture)).State);
    }

    /// <summary>
    /// 判没建成之后，那张旧单在 RIoT 上冒出来、在本车上跑：外来单监督器认作「归属证明不了」（依据 <c>OWN_DEMAND_LEG_ORDER_ABANDONED</c>），
    /// 挡车、告警，不发任何取消；车上挂着单时重建也不建新单。
    /// </summary>
    [Fact]
    public async Task TheOldOrderTurningUpRunningIsHeldNotCancelledAndTheRebuildWaits()
    {
        await using RuntimeFixture fixture = await PickupCreateRefusedAsync();
        string oldUpperId = (await CurrentStopAsync(fixture, FirstDemandId)).UpperId;
        await RunAfterAsync(fixture, TimeSpan.FromSeconds(130));
        Assert.Single(await RebuildsAsync(fixture));

        fixture.Riot.ReleaseWithheldOrder(oldUpperId);
        // What RIoT's vehicle safety read reports for a vehicle with an unfinished order on it.
        fixture.Riot.SafetyReasons = ["RIOT_NONFINAL_ORDER_PRESENT"];
        await RunAfterAsync(fixture, TimeSpan.FromSeconds(1));

        await using (ControlServerDbContext reading = new(fixture.DbOptionsForTests))
        {
            ForeignRiotOrderRow held = Assert.Single(await reading.ForeignRiotOrders.AsNoTracking().ToArrayAsync(Token));
            Assert.Equal(
                (oldUpperId, ForeignRiotOrderOwnership.Unproven, "OWN_DEMAND_LEG_ORDER_ABANDONED"),
                (held.UpperId, held.Ownership, held.OwnershipBasis));
        }
        Assert.Empty(fixture.Riot.OrderCommands);

        await OwnOrderRebuildTests.PassTheDelayAsync(fixture);
        fixture.Context.ChangeTracker.Clear();
        Assert.Equal(1, fixture.Riot.CreateCount("TO_PICKUP"));
        Assert.Equal("OWN_ORDER_REBUILD_WAITING_VEHICLE", (await fixture.RuntimeAsync()).BlockReasonCode);
        Assert.Empty(fixture.Riot.OrderCommands);
    }

    // ---- 人工出口：DECLARE_ORDER_NEVER_APPEARED ----------------------------------------------------------------------

    /// <summary>
    /// 自动那条凑不齐证据（清单一直读不全）：过了重复窗口，人经故障处置入口确认没建成——记成「没建成」并直接停住，署上这个人，
    /// 不向 RIoT 发任何命令、不动车、不终结需求；再点一次答 <c>AlreadyDone</c>。之后人工重建：引擎照常过车况与门禁，建一张新单号的单。
    /// </summary>
    [Fact]
    public async Task APersonDeclaresTheOrderNeverCreatedAndThenRebuildsIt()
    {
        await using RuntimeFixture fixture = await PickupCreateRefusedAsync();
        string upperId = (await CurrentStopAsync(fixture, FirstDemandId)).UpperId;
        fixture.Riot.ListingComplete = false;
        await RunAfterAsync(fixture, fixture.Options.OwnOrderRebuildRepeatWindow + TimeSpan.FromSeconds(1));
        Assert.Equal("PICKUP_ResultUnknown", (await fixture.RuntimeAsync()).BlockReasonCode);
        VehicleFaultRecoveryTests.SiteRiot site = new(fixture);
        RecordingLogger<VehicleFaultRecoveryService> log = new();

        VehicleFaultRecoveryDecision decision = await VehicleFaultRecoveryTests.Service(fixture, site, logger: log)
            .RecoverAsync(Declare(fixture), Token);

        Assert.Equal(
            (VehicleFaultRecoveryOutcome.NeverAppearedDeclared, VehicleFaultRecoveryDispositions.RebuildStopped),
            (decision.Outcome, decision.Disposition));
        Assert.Empty(decision.Reasons);
        Assert.Empty(site.OrderCommands);
        Assert.Empty(site.EmergencyCommands);
        OwnOrderRebuildRow declared = Assert.Single(await RebuildsAsync(fixture));
        Assert.Equal(
            (upperId, "ORDER_NEVER_APPEARED", OwnOrderRebuildStates.Stopped, "ORDER_NEVER_APPEARED_DECLARED_BY_OPERATOR", OperatorId),
            (declared.EndedUpperId, declared.Source, declared.State, declared.StoppedReason, declared.OperatorId));
        JourneyRuntimeRow kept = await fixture.RuntimeAsync();
        Assert.Equal(
            (JourneyRuntimeStage.AwaitingPickupArrival, "OWN_ORDER_REBUILD_STOPPED"), (kept.Stage, kept.BlockReasonCode));
        Assert.Contains(log.Entries, entry =>
            entry.Message.Contains("DeclareOrderNeverAppeared", StringComparison.Ordinal) &&
            entry.Message.Contains(OperatorId, StringComparison.Ordinal));

        Assert.Equal(
            VehicleFaultRecoveryOutcome.AlreadyDone,
            (await VehicleFaultRecoveryTests.Service(fixture, site).RecoverAsync(Declare(fixture), Token)).Outcome);

        await RunAfterAsync(fixture, TimeSpan.FromSeconds(60));
        Assert.Equal(1, fixture.Riot.CreateCount("TO_PICKUP"));
        Assert.Equal("OWN_ORDER_REBUILD_STOPPED", (await fixture.RuntimeAsync()).BlockReasonCode);

        Assert.Equal(
            VehicleFaultRecoveryOutcome.RebuildRequested,
            (await VehicleFaultRecoveryTests.Service(fixture, site)
                .RecoverAsync(StoppedRebuildExitTests.Rebuild(fixture) with { OperatorId = OperatorId }, Token)).Outcome);
        fixture.Context.ChangeTracker.Clear();
        await RunAfterAsync(fixture, TimeSpan.FromSeconds(1));

        Assert.Equal(2, fixture.Riot.CreateCount("TO_PICKUP"));
        Assert.NotEqual(upperId, (await CurrentStopAsync(fixture, FirstDemandId)).UpperId);
        Assert.Null((await fixture.RuntimeAsync()).BlockReasonCode);
    }

    /// <summary>
    /// 审查 H1（探针 P1）：重建出来的新单也结果未知、自动证据凑不齐，人对这张新单确认没建成——那条还在 Ordering 的重建在同一次保存里以
    /// <c>REBUILT_ORDER_NEVER_APPEARED</c> 结束，新记一条停住的；之后引擎每轮正常推进（修之前停靠上同时有两条记录，<c>ForStopAsync</c> 每轮抛），
    /// cs#345 的人工重建照常可用。
    /// </summary>
    [Fact]
    public async Task APersonDeclaringARebuiltOrderNeverCreatedEndsTheRebuildItSupersedes()
    {
        await using RuntimeFixture fixture = await PickupCreateRefusedAsync();
        await RunAfterAsync(fixture, TimeSpan.FromSeconds(130));
        OwnOrderRebuildRow first = Assert.Single(await RebuildsAsync(fixture));
        fixture.Riot.RefuseNextCreateOf = "TO_PICKUP";
        await OwnOrderRebuildTests.PassTheDelayAsync(fixture);
        fixture.Context.ChangeTracker.Clear();
        OwnOrderRebuildRow ordering = Assert.Single(await RebuildsAsync(fixture));
        Assert.Equal(OwnOrderRebuildStates.Ordering, ordering.State);
        await StampTheCreateAnswerOnTheFixtureClockAsync(fixture, ordering.NewUpperId);
        fixture.Riot.ListingComplete = false;
        await RunAfterAsync(fixture, fixture.Options.OwnOrderRebuildRepeatWindow + TimeSpan.FromSeconds(1));
        Assert.Equal("OWN_ORDER_REBUILD_ORDER_UNCONFIRMED", (await fixture.RuntimeAsync()).BlockReasonCode);
        VehicleFaultRecoveryTests.SiteRiot site = new(fixture);

        VehicleFaultRecoveryDecision decision = await VehicleFaultRecoveryTests.Service(fixture, site)
            .RecoverAsync(Declare(fixture), Token);

        Assert.Equal(VehicleFaultRecoveryOutcome.NeverAppearedDeclared, decision.Outcome);
        OwnOrderRebuildRow[] records = await RebuildsAsync(fixture);
        Assert.Equal(2, records.Length);
        OwnOrderRebuildRow ended = records.Single(row => row.RebuildId == first.RebuildId);
        Assert.Equal((OwnOrderRebuildStates.Ended, "REBUILT_ORDER_NEVER_APPEARED"), (ended.State, ended.StoppedReason));
        OwnOrderRebuildRow declared = records.Single(row => row.RebuildId != first.RebuildId);
        Assert.Equal(
            (ordering.NewUpperId, OwnOrderRebuildStates.Stopped, OperatorId),
            (declared.EndedUpperId, declared.State, declared.OperatorId));

        await RunAfterAsync(fixture, TimeSpan.FromSeconds(1));
        await RunAfterAsync(fixture, TimeSpan.FromSeconds(1));
        Assert.Equal("OWN_ORDER_REBUILD_STOPPED", (await fixture.RuntimeAsync()).BlockReasonCode);
        Assert.Equal(
            VehicleFaultRecoveryOutcome.AlreadyDone,
            (await VehicleFaultRecoveryTests.Service(fixture, site).RecoverAsync(Declare(fixture), Token)).Outcome);

        fixture.Riot.ListingComplete = true;
        Assert.Equal(
            VehicleFaultRecoveryOutcome.RebuildRequested,
            (await VehicleFaultRecoveryTests.Service(fixture, site)
                .RecoverAsync(StoppedRebuildExitTests.Rebuild(fixture) with { OperatorId = OperatorId }, Token)).Outcome);
        await RunAfterAsync(fixture, TimeSpan.FromSeconds(1));
        Assert.Equal(3, fixture.Riot.CreateCount("TO_PICKUP"));
        Assert.Null((await fixture.RuntimeAsync()).BlockReasonCode);
    }

    /// <summary>
    /// 确认没建成之后，车上没货的这一趟可以放弃（control-server#345 的乙）：需求终结、旅程收尾，返回列出被终结的需求给 MES 收尾。
    /// </summary>
    [Fact]
    public async Task AfterTheDeclarationAnEmptyTripCanBeGivenUp()
    {
        await using RuntimeFixture fixture = await PickupCreateRefusedAsync();
        fixture.Riot.ListingComplete = false;
        await RunAfterAsync(fixture, fixture.Options.OwnOrderRebuildRepeatWindow + TimeSpan.FromSeconds(1));
        VehicleFaultRecoveryTests.SiteRiot site = new(fixture);
        Assert.Equal(
            VehicleFaultRecoveryOutcome.NeverAppearedDeclared,
            (await VehicleFaultRecoveryTests.Service(fixture, site).RecoverAsync(Declare(fixture), Token)).Outcome);

        VehicleFaultRecoveryDecision given = await VehicleFaultRecoveryTests.Service(fixture, site)
            .RecoverAsync(StoppedRebuildExitTests.GiveUp(fixture), Token);

        Assert.Equal(VehicleFaultRecoveryOutcome.TripTerminated, given.Outcome);
        Assert.Equal([FirstDemandId], given.TerminatedDemandIds);
        Assert.Equal(JourneyRuntimeStage.Completed, (await fixture.RuntimeAsync()).Stage);
        Assert.Equal(1, fixture.Riot.CreateCount("TO_PICKUP"));
        Assert.Empty(site.OrderCommands);
    }

    /// <summary>
    /// 人工确认的每一条前提，缺一样就拒绝、说出原因，什么都不改（记录、旅程码、单数原样）：太早（自动出口还没给够机会）；RIoT 按单号查得到单；
    /// RIoT 读不到（超时）；意图拿到过单号；这一段根本不是结果未知（正常在途）；没写人、没确认。
    /// </summary>
    [Theory]
    [InlineData("too-early", "ORDER_NEVER_APPEARED_EXIT_TOO_EARLY")]
    [InlineData("order-present", "ORDER_NEVER_APPEARED_EXIT_ORDER_NOT_ABSENT")]
    [InlineData("order-unreadable", "ORDER_NEVER_APPEARED_EXIT_ORDER_UNREADABLE")]
    [InlineData("intent-has-order-id", "ORDER_NEVER_APPEARED_EXIT_ORDER_ID_KNOWN")]
    [InlineData("create-answered-with-order-id", "ORDER_NEVER_APPEARED_EXIT_ORDER_ID_KNOWN")]
    [InlineData("under-way", "ORDER_NEVER_APPEARED_EXIT_NOT_UNKNOWN")]
    [InlineData("no-person", "FAULT_RECOVERY_OPERATOR_UNIDENTIFIED")]
    public async Task EachUnmetPremiseRefusesTheDeclarationAndChangesNothing(string premise, string refusal)
    {
        await using RuntimeFixture fixture = premise == "under-way"
            ? await StoppedRebuildExitTests.DispatchedToPickupAsync()
            : await PickupCreateRefusedAsync();
        string upperId = (await CurrentStopAsync(fixture, FirstDemandId)).UpperId;
        fixture.Riot.ListingComplete = false;
        await RunAfterAsync(
            fixture,
            premise == "too-early" ? TimeSpan.FromSeconds(60) : fixture.Options.OwnOrderRebuildRepeatWindow + TimeSpan.FromSeconds(1));
        VehicleFaultRecoveryTests.SiteRiot site = new(fixture);
        switch (premise)
        {
            case "order-present":
                site.OrderReadOverride = new RiotOrderObservation(
                    upperId, RiotOrderObservationKind.Active, "ORDER-LATE", OrderState: 1, VehicleKey: fixture.Options.VehicleKey);
                break;
            case "order-unreadable":
                site.OrderReadOverride = new RiotOrderObservation(
                    upperId, RiotOrderObservationKind.Unknown, null,
                    Receipt: new RiotOrderCallReceipt("RECONCILE", "SdkFailure", fixture.Clock.GetUtcNow(), FailureCategory: "TIMEOUT"));
                break;
            case "intent-has-order-id":
                await GiveTheIntentAnOrderIdAsync(fixture, upperId);
                break;
            case "create-answered-with-order-id":
                await AuditAReturnedOrderIdAsync(fixture, upperId);
                break;
        }
        string? codeBefore = (await fixture.RuntimeAsync()).BlockReasonCode;
        VehicleFaultRecoveryRequest request = premise == "no-person" ? Declare(fixture) with { OperatorId = " " } : Declare(fixture);

        VehicleFaultRecoveryDecision decision = await VehicleFaultRecoveryTests.Service(fixture, site).RecoverAsync(request, Token);

        Assert.Equal(VehicleFaultRecoveryOutcome.Refused, decision.Outcome);
        Assert.True(decision.Reasons.Contains(refusal), string.Join(",", decision.Reasons));
        Assert.Empty(await RebuildsAsync(fixture));
        Assert.Equal(codeBefore, (await fixture.RuntimeAsync()).BlockReasonCode);
        Assert.Equal(1, fixture.Riot.CreateCount("TO_PICKUP"));
        Assert.Empty(site.OrderCommands);
    }

    private static VehicleFaultRecoveryRequest Declare(RuntimeFixture fixture) => new(
        new EmergencyStopSubject(fixture.Options.AgvId, fixture.Options.VehicleKey),
        VehicleFaultRecoveryAction.DeclareOrderNeverAppeared,
        OperatorId,
        FaultRemedied: true,
        Note: "L1 673");

    /// <summary>受理后派往取货站：建单回 00002（结果未知、单没落地），之后的对账是真实 RIoT 的查无此单。</summary>
    private static async Task<RuntimeFixture> PickupCreateRefusedAsync()
    {
        RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(FirstDemandId, FirstSublot, Now.AddMinutes(-10)));
        fixture.BoxCounts.Set(FirstSublot, 7);
        fixture.Riot.AbsentOrdersReadAsRealRiot = true;
        fixture.Riot.RefuseNextCreateOf = "TO_PICKUP";
        await TickAndRunAsync(fixture);
        fixture.Context.ChangeTracker.Clear();
        Assert.Equal(JourneyRuntimeStage.AwaitingPickupArrival, (await fixture.RuntimeAsync()).Stage);
        Assert.Equal(1, fixture.Riot.CreateCount("TO_PICKUP"));
        await StampTheCreateAnswerOnTheFixtureClockAsync(fixture, (await CurrentStopAsync(fixture, FirstDemandId)).UpperId);
        await RunAfterAsync(fixture, TimeSpan.FromSeconds(1));
        return fixture;
    }

    /// <summary>
    /// The fixture builds its dispatch service on the system clock while everything else here runs on the fixture's; the host
    /// gives both the same one. The manual exit measures from the create answer, so it is put on the fixture's clock.
    /// </summary>
    private static async Task StampTheCreateAnswerOnTheFixtureClockAsync(RuntimeFixture fixture, string upperId)
    {
        await using ControlServerDbContext writing = new(fixture.DbOptionsForTests);
        OrderIntentRow intent = await writing.OrderIntents.SingleAsync(row => row.UpperId == upperId, Token);
        Assert.NotNull(intent.LastCreateOutcomeAt);
        intent.LastCreateOutcomeAt = fixture.Clock.GetUtcNow();
        await writing.SaveChangesAsync(Token);
    }

    /// <summary>装好货、离站核验通过：开往卸货站的建单回 00002，之后的对账是真实 RIoT 的查无此单。</summary>
    private static async Task<RuntimeFixture> GateCreateRefusedAsync()
    {
        RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(FirstDemandId, FirstSublot, Now.AddMinutes(-10)));
        fixture.BoxCounts.Set(FirstSublot, 7);
        await fixture.AdvanceToGateArrivalAsync(() =>
        {
            fixture.Riot.AbsentOrdersReadAsRealRiot = true;
            fixture.Riot.RefuseNextCreateOf = "TO_GATE";
        });
        fixture.Riot.MovementState = "MT_FINISHED";
        fixture.Context.ChangeTracker.Clear();
        Assert.Equal(JourneyRuntimeStage.AwaitingGateArrival, (await fixture.RuntimeAsync()).Stage);
        await RunAfterAsync(fixture, TimeSpan.FromSeconds(1));
        return fixture;
    }

    /// <summary>拨钟、车载端说一句话、跑一轮。</summary>
    private static async Task RunAfterAsync(RuntimeFixture fixture, TimeSpan elapsed)
    {
        fixture.Clock.Advance(elapsed);
        await fixture.HearFromPeerAsync();
        fixture.Context.ChangeTracker.Clear();
        await fixture.Engine.ExecuteOnceAsync(Token);
        fixture.Context.ChangeTracker.Clear();
    }

    private static async Task GiveTheIntentAnOrderIdAsync(RuntimeFixture fixture, string upperId)
    {
        await using ControlServerDbContext writing = new(fixture.DbOptionsForTests);
        OrderIntentRow intent = await writing.OrderIntents.SingleAsync(row => row.UpperId == upperId, Token);
        Assert.Equal("RESULT_UNKNOWN", intent.Status);
        intent.OrderId = "ORDER-SEEN-ONCE";
        await writing.SaveChangesAsync(Token);
    }

    /// <summary>The create answer of this leg carried an orderId, which only the dispatch audit keeps (review S1).</summary>
    private static async Task AuditAReturnedOrderIdAsync(RuntimeFixture fixture, string upperId)
    {
        await using ControlServerDbContext writing = new(fixture.DbOptionsForTests);
        string leg = (await writing.OrderIntents.SingleAsync(row => row.UpperId == upperId, Token)).MovementLegId;
        Assert.True(await writing.RiotDispatchAuditEvents.Where(row => row.MovementLegId == leg)
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.ReturnedOrderId, "ORDER-ANSWERED"), Token) > 0);
    }

    private static async Task<OwnOrderRebuildRow[]> RebuildsAsync(RuntimeFixture fixture)
    {
        await using ControlServerDbContext reading = new(fixture.DbOptionsForTests);
        return await reading.OwnOrderRebuilds.AsNoTracking().ToArrayAsync(Token);
    }

    private static async Task<OrderIntentRow> IntentAsync(RuntimeFixture fixture, string upperId)
    {
        await using ControlServerDbContext reading = new(fixture.DbOptionsForTests);
        return await reading.OrderIntents.AsNoTracking().SingleAsync(row => row.UpperId == upperId, Token);
    }

    private static async Task<(string Status, int? Attempts)> IntentStateAsync(RuntimeFixture fixture, string upperId)
    {
        OrderIntentRow intent = await IntentAsync(fixture, upperId);
        return (intent.Status, intent.CreateAttemptCount);
    }
}
