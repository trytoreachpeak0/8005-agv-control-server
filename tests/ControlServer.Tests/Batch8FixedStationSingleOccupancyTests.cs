using System.Data.Common;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Dispatch;
using ControlServer.Host.Runtime.Dispatch.Criteria;
using ControlServer.Host.Runtime.Fleet;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;

namespace ControlServer.Tests;

/// <summary>
/// 固定公共站点单车位（<c>REQ-0204</c> 修订，批次8-20，control-server#391）：公共站点用站点独占原语预占、占用、凭离点证据释放；
/// 别的车不承接下一站为该点的新任务；取货起点是公共站点时已在点的车取得一个选车软层。
/// </summary>
/// <remarks>
/// <para>
/// 三层入口各测各的：判据与软层直接对它们的公开入口（<see cref="FixedStationSingleOccupancyCriterion"/>、
/// <see cref="FixedOriginStationPresenceLayer"/>），受理走真实的 <see cref="WireToGateStore"/>，到站转占用与离点释放走引擎
/// （<c>STAGING_TO_WIRE</c> 的单车夹具，与 <c>ReversedDirectionJourneyRuntimeTests</c> 同一套绑定）。
/// </para>
/// <para>
/// 站号：305 是 <c>STAGING_TO_WIRE</c> 的公共站点（派工待送取货站），210／202 是 <c>WIRE_TO_GATE</c> 的关卡，12、101 是机台。
/// </para>
/// </remarks>
public sealed class Batch8FixedStationSingleOccupancyTests
{
    private const int StagingStation = 305;
    private const string StagingName = "派工待送取货";
    private const int Machine = 12;
    private const int Gate = 210;
    private const string DemandA = "20000000-0000-4000-8000-00000000000a";
    private const string DemandB = "20000000-0000-4000-8000-00000000000b";
    // The demand id the runtime fixture's arrival helpers derive their upper ids from.
    private const string FixtureDemand = "10000000-0000-4000-8000-000000000001";
    private const string AgvA = "AGV-A";
    private const string KeyA = "KEY-A";
    private const string AgvB = "AGV-B";
    private const string KeyB = "KEY-B";

    private static readonly DateTimeOffset At = Batch7JourneyFixture.Now;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ---- 受理：预占与受理同一次保存 -----------------------------------------------------------------

    /// <summary>取货站就是公共站点的受理，在同一次保存里预占它：一行 RESERVED，持有者是这趟旅程，经过里记下预占时刻。</summary>
    [Fact]
    public async Task AcceptingADemandWhosePickupIsItsPublicStationReservesItInTheSameSave()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();

        await AcceptAsync(fixture.Context, DemandA, AgvA, KeyA, StagingStation, fixedStation: StagingStation);

        StationExclusivity held = Assert.IsType<StationExclusivity>(
            await new StationExclusivityStore(fixture.Context).ReadAsync(25, StagingStation, Token));
        Assert.Equal(
            (StationExclusivityKinds.FixedTaskStation, StationExclusivityStates.Reserved, KeyA,
                JourneyIdentity.ForAnchorDemand(DemandA), At, (long?)null),
            (held.StationKind, held.State, held.VehicleKey, held.JourneyId, held.StateSince, held.WaitingPointVersion));
        StationExclusivityRecord record = Assert.Single(
            await new StationExclusivityStore(fixture.Context).ListHistoryAsync(25, StagingStation, Token));
        Assert.Equal((At, (DateTimeOffset?)null, (DateTimeOffset?)null), (record.ReservedAt!.Value, record.OccupiedAt, record.ReleasedAt));
    }

    /// <summary>公共站点只是终点（<c>WIRE_TO_GATE</c> 的关卡）：受理时它不是下一站，不取任何独占。</summary>
    [Fact]
    public async Task AcceptingADemandWhosePublicStationIsOnlyItsEndReservesNothing()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();

        await AcceptAsync(fixture.Context, DemandA, AgvA, KeyA, pickup: 101, fixedStation: 202);

        Assert.Empty(await fixture.Context.Set<StationExclusivityRow>().AsNoTracking().ToArrayAsync(Token));
    }

    /// <summary>
    /// 同轮两车都以同一公共站点为下一站：判据读的那一刻两边都还空着，两次受理都走到了写。谁占到由主键定——第二次受理被拒，
    /// 整笔回滚：没有旅程、没有用途占有、没有受理行、没有订单意图，站仍是第一辆车的。
    /// </summary>
    [Fact]
    public async Task TwoVehiclesAcceptedOntoOnePublicStationAreDecidedByTheKeyAndTheLoserLeavesNothing()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        await using ControlServerDbContext second = fixture.NewContext();

        await AcceptAsync(fixture.Context, DemandA, AgvA, KeyA, StagingStation, StagingStation);
        BusinessIdentityConflictException refused = await Assert.ThrowsAsync<BusinessIdentityConflictException>(
            () => AcceptAsync(second, DemandB, AgvB, KeyB, StagingStation, StagingStation));

        Assert.Contains("fixed task station", refused.Message, StringComparison.Ordinal);
        await AssertNothingOfTheAcceptanceAsync(fixture, DemandB, KeyB);
        Assert.Equal(KeyA, (await new StationExclusivityStore(fixture.NewContext()).ReadAsync(25, StagingStation, Token))!.VehicleKey);
    }

    /// <summary>
    /// 崩溃点：写预占那一刻数据库出错（注入在独占行的 INSERT 上）。受理与预占是同一次保存，所以受理也不在——没有半截承诺。
    /// </summary>
    [Fact]
    public async Task AFailureWritingTheReservationRollsTheAcceptanceBack()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        FailingInsertInterceptor failing = new("\"StationExclusivities\"");
        await using ControlServerDbContext injected = fixture.NewContext(failing);

        await Assert.ThrowsAnyAsync<Exception>(
            () => AcceptAsync(injected, DemandA, AgvA, KeyA, StagingStation, StagingStation));

        Assert.True(failing.Fired, "The injection never reached the reservation's insert, so it proves nothing.");
        await AssertNothingOfTheAcceptanceAsync(fixture, DemandA, KeyA);
        Assert.Empty(await fixture.NewContext().Set<StationExclusivityRow>().AsNoTracking().ToArrayAsync(Token));
    }

    /// <summary>
    /// 同一辆车在公共站点上卸完上一趟、接着接下一趟从这里取货：独占交给新旅程，仍是占用（车一直在那里），上一段经过记「交给下一趟」。
    /// 不是被别的车占着——插第二行会被主键拒掉，这辆车就永远接不了自己脚下的活。
    /// </summary>
    [Fact]
    public async Task TheVehicleStandingAtItsPublicStationHandsItToItsNextJourney()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        await AcceptAsync(fixture.Context, DemandA, AgvA, KeyA, StagingStation, StagingStation);
        Assert.True(await new StationExclusivityStore(fixture.Context).MarkOccupiedAsync(
            25, StagingStation, JourneyIdentity.ForAnchorDemand(DemandA), At.AddMinutes(1), Token));
        await Batch7JourneyFixture.CompleteByUnloadAsync(fixture.Context, DemandA, At.AddMinutes(2));
        await fixture.RenewContextAsync();

        await AcceptAsync(fixture.Context, DemandB, AgvA, KeyA, StagingStation, StagingStation, At.AddMinutes(3));

        StationExclusivityStore store = new(fixture.NewContext());
        StationExclusivity held = (await store.ReadAsync(25, StagingStation, Token))!;
        Assert.Equal(
            (JourneyIdentity.ForAnchorDemand(DemandB), StationExclusivityStates.Occupied, KeyA),
            (held.JourneyId, held.State, held.VehicleKey));
        StationExclusivityRecord[] history = [.. await store.ListHistoryAsync(25, StagingStation, Token)];
        Assert.Equal(2, history.Length);
        Assert.Equal(FixedStationExclusivity.HandedToNextJourney, history[0].ReleaseReason);
        Assert.Equal((At.AddMinutes(3), (DateTimeOffset?)null), (history[1].OccupiedAt!.Value, history[1].ReleasedAt));
    }

    // ---- 判据：别的车占着时，下一站为该点的新任务不合格 ------------------------------------------------

    /// <summary>
    /// 三态对「下一站为该点的新任务」：别的车预占时不合格（RESERVED 码），到点占用时不合格（OCCUPIED 码），离点释放之后合格；
    /// 自己占着的不算别的车。
    /// </summary>
    [Fact]
    public async Task AnIdleVehicleIsRefusedAPublicStationAnotherVehicleReservesOrOccupiesUntilItIsReleased()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        StationExclusivityStore store = new(fixture.Context);
        FixedStationSingleOccupancyCriterion criterion = new(fixture.Context);
        string holder = JourneyIdentity.ForAnchorDemand(DemandA);

        Assert.Equal(DispatchAdmissionChain.Eligible, await criterion.EvaluateAsync(IdleStaging(KeyB), Token));

        await store.TryAcquireAsync(Reserve(StagingStation), KeyA, holder, At, Token);
        Assert.Equal(
            DispatchReasonCodes.FixedTaskStationReservedByOtherVehicle,
            await criterion.EvaluateAsync(IdleStaging(KeyB), Token));

        Assert.True(await store.MarkOccupiedAsync(25, StagingStation, holder, At.AddMinutes(1), Token));
        Assert.Equal(
            DispatchReasonCodes.FixedTaskStationOccupiedByOtherVehicle,
            await criterion.EvaluateAsync(IdleStaging(KeyB), Token));
        Assert.Equal(DispatchAdmissionChain.Eligible, await criterion.EvaluateAsync(IdleStaging(KeyA), Token));

        Assert.True(await store.ReleaseAsync(
            25, StagingStation, holder, At.AddMinutes(2), FixedStationExclusivity.ReleasedOnDepartureEvidence, Token));
        Assert.Equal(DispatchAdmissionChain.Eligible, await criterion.EvaluateAsync(IdleStaging(KeyB), Token));
    }

    /// <summary>
    /// 只挡下一站：站在停靠上的在途车，把被占的公共站点插在紧接着的位置——不合格；插在更靠后（中间隔着一个既有停靠）——不挡；
    /// 车在路上（下一站就是当前停靠，追加改不了它）——不挡。
    /// </summary>
    [Fact]
    public async Task OnlyACandidateThatMakesThePublicStationTheNextStopIsRefused()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        await new StationExclusivityStore(fixture.Context).TryAcquireAsync(
            Reserve(StagingStation), KeyA, JourneyIdentity.ForAnchorDemand(DemandA), At, Token);
        FixedStationSingleOccupancyCriterion criterion = new(fixture.Context);

        Assert.Equal(
            DispatchReasonCodes.FixedTaskStationReservedByOtherVehicle,
            await criterion.EvaluateAsync(InTransitStaging(KeyB, standing: true, pickupRightAfterCurrent: true), Token));
        Assert.Equal(
            DispatchAdmissionChain.Eligible,
            await criterion.EvaluateAsync(InTransitStaging(KeyB, standing: true, pickupRightAfterCurrent: false), Token));
        Assert.Equal(
            DispatchAdmissionChain.Eligible,
            await criterion.EvaluateAsync(InTransitStaging(KeyB, standing: false, pickupRightAfterCurrent: true), Token));
    }

    /// <summary>
    /// 公共站点只是终点（<c>WIRE_TO_GATE</c>）：关卡被别的车占着，也不挡一辆空闲车去机台取货——候选让它的下一站变成的是机台。
    /// </summary>
    [Fact]
    public async Task AHeldGateDoesNotRefuseAWireToGateCandidateWhoseNextStopIsTheMachine()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        await new StationExclusivityStore(fixture.Context).TryAcquireAsync(
            new StationExclusivityRequest(
                25, Gate, StationExclusivityKinds.FixedTaskStation, StationExclusivityStates.Occupied, null),
            KeyA, JourneyIdentity.ForAnchorDemand(DemandA), At, Token);

        Assert.Equal(
            DispatchAdmissionChain.Eligible,
            await new FixedStationSingleOccupancyCriterion(fixture.Context).EvaluateAsync(IdleWireToGate(KeyB), Token));
    }

    /// <summary>
    /// 同一个 <c>(MapId, StationId)</c> 不会既是等待点又是公共站点（批次8-17 的导入已拒绝）：判据不重复那条校验，但读到等待点的行就抛，
    /// 不把一个配置缺陷折成一条普通积压。
    /// </summary>
    [Fact]
    public async Task APublicStationHeldAsAWaitingPointIsAnInvariantBreachNotABacklogReason()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        await new StationExclusivityStore(fixture.Context).TryAcquireAsync(
            new StationExclusivityRequest(
                25, StagingStation, StationExclusivityKinds.WaitingPoint, StationExclusivityStates.Reserved, null),
            KeyA, JourneyIdentity.ForAnchorDemand(DemandA), At, Token);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new FixedStationSingleOccupancyCriterion(fixture.Context).EvaluateAsync(IdleStaging(KeyB), Token));
    }

    /// <summary>两个原因码都归普通积压，登记在它们判据的位置上。</summary>
    [Fact]
    public void BothReasonCodesAreOrdinaryBacklogAtTheCriterionsPlace()
    {
        foreach (string code in new[]
                 {
                     DispatchReasonCodes.FixedTaskStationReservedByOtherVehicle,
                     DispatchReasonCodes.FixedTaskStationOccupiedByOtherVehicle,
                 })
        {
            DispatchReasonClassification row = StructuralDispatchClassification.ByCode[code];
            Assert.Equal((DispatchReasonClass.Backlog, (int?)new FixedStationSingleOccupancyCriterion(null!).Order),
                (row.Class, row.ChainOrder));
        }
    }

    // ---- 软层 ---------------------------------------------------------------------------------------

    /// <summary>
    /// <c>STAGING_TO_WIRE</c>：一台车已在派工待送站（过了全部硬准入，才会有出价），另一台更近（边际成本更低）——前者胜。
    /// </summary>
    [Fact]
    public void AVehicleAlreadyAtTheStagingStationIsChosenOverACheaperOne()
    {
        EligibleVehicleOffer present = Offer("agv-present", currentStation: StagingStation, cost: 50_000, Staging());
        EligibleVehicleOffer cheaper = Offer("agv-cheaper", currentStation: Machine, cost: 10_000, Staging());

        Assert.Equal("agv-present", DispatchVehicleOrdering.SelectNext([cheaper, present]).Vehicle.AgvId);
    }

    /// <summary><c>WIRE_TO_GATE</c>：停在关卡的车不得软层，成本照常决定。</summary>
    [Fact]
    public void AVehicleStandingAtTheGateGetsNoLayerForAWireToGateDemand()
    {
        EligibleVehicleOffer atGate = Offer("agv-at-gate", currentStation: Gate, cost: 50_000, WireToGate());
        EligibleVehicleOffer cheaper = Offer("agv-cheaper", currentStation: Machine, cost: 10_000, WireToGate());

        Assert.Equal(0, new FixedOriginStationPresenceLayer().Compare(atGate, cheaper));
        Assert.Equal("agv-cheaper", DispatchVehicleOrdering.SelectNext([atGate, cheaper]).Vehicle.AgvId);
    }

    // ---- 引擎：到站转占用、离点证据释放 ---------------------------------------------------------------

    /// <summary>
    /// 一趟 <c>STAGING_TO_WIRE</c> 走完：受理即预占，到站转占用；离站订单下达、车还没到下一站时仍是占用（「下达即释放」的缺陷版本在这里红）；
    /// RIoT 报出车已在机台之后，下一轮凭离点证据释放，经过里记下原因。
    /// </summary>
    [Fact]
    public async Task APublicStationIsReservedOccupiedAndReleasedOnlyOnDepartureEvidence()
    {
        await using RuntimeFixture fixture = await WithStagingToWireBoundAsync();
        fixture.Catalog.Set(Reverse(fixture, FixtureDemand, "SUBLOT-001"));
        fixture.BoxCounts.Set("SUBLOT-001", 4);

        await fixture.Engine.ExecuteOnceAsync(Token);
        Assert.Equal(StationExclusivityStates.Reserved, (await HeldAsync(fixture))!.State);

        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, (await fixture.AdvanceToSublotWaitAsync()).Stage);
        Assert.Equal(StationExclusivityStates.Occupied, (await HeldAsync(fixture))!.State);

        JourneyRuntimeRow leaving = await fixture.AdvanceToGateArrivalAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingGateArrival, leaving.Stage);
        await fixture.Engine.ExecuteOnceAsync(Token);
        Assert.Equal(StationExclusivityStates.Occupied, (await HeldAsync(fixture))!.State);

        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { CurrentStationId = Machine };
        await fixture.Engine.ExecuteOnceAsync(Token);
        Assert.Null(await HeldAsync(fixture));
        StationExclusivityRecord record = Assert.Single(
            await new StationExclusivityStore(fixture.OpenConnectionContext()).ListHistoryAsync(25, StagingStation, Token));
        Assert.Equal(FixedStationExclusivity.ReleasedOnDepartureEvidence, record.ReleaseReason);
    }

    /// <summary>
    /// FAILED／UNKNOWN：预占的车旅程阻断了（订单结果未知、要人介入），车也不在那个站——预占不放，与等待点同一条规则。
    /// </summary>
    [Fact]
    public async Task AReservationIsKeptWhileItsJourneyIsBlocked()
    {
        await using RuntimeFixture fixture = await WithStagingToWireBoundAsync();
        fixture.Catalog.Set(Reverse(fixture, FixtureDemand, "SUBLOT-001"));
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        await fixture.Engine.ExecuteOnceAsync(Token);
        JourneyRuntimeRow runtime = await fixture.Context.JourneyRuntimes.SingleAsync(Token);
        runtime.Stage = JourneyRuntimeStage.Blocked;
        runtime.SetBlockReason("MOVEMENT_RESULT_UNKNOWN", fixture.Clock.GetUtcNow());
        await fixture.Context.SaveChangesAsync(Token);
        fixture.Context.ChangeTracker.Clear();
        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { CurrentStationId = Machine };

        await fixture.Engine.ExecuteOnceAsync(Token);

        Assert.Equal(StationExclusivityStates.Reserved, (await HeldAsync(fixture))!.State);
    }

    // ---- 正开往公共站点、还没预占的车（调度 2026-09-29 定：照常出发，判据算它占着，每轮补预占） ----------------------

    /// <summary>
    /// A 占着关卡；B 站在最后一个取货停靠上，下一站就是关卡，还没预占（离站时取不到就照常出发）。新任务 C（下一站会是关卡）被挡；
    /// B 的旅程不受任何影响。A 离点释放之后：C 仍被挡——这时没有行，是 B 正开往它；下一轮补预占把关卡给 B，C 被 B 的预占挡住。
    /// </summary>
    /// <remarks>
    /// <c>WIRE_TO_GATE</c> 的新任务不会让关卡成为下一站（关卡排在取货之后），所以 C 是直接对判据构造的一条「下一站是关卡」的候选：
    /// 这条用例证的是持有者的认定——行、以及没有行时正开往它的车——与新任务是什么类型无关。<c>STAGING_TO_WIRE</c> 的派工待送站上，
    /// 这正是一条同站新需求。
    /// </remarks>
    [Fact]
    public async Task AVehicleHeadingForAHeldPublicStationGoesOnHoldsItAgainstNewTasksAndIsGivenItOnceReleased()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        string holder = JourneyIdentity.ForAnchorDemand(DemandA);
        await new StationExclusivityStore(fixture.Context).TryAcquireAsync(
            new StationExclusivityRequest(
                25, 202, StationExclusivityKinds.FixedTaskStation, StationExclusivityStates.Occupied, null),
            KeyA, holder, At, Token);
        await SeedHolderAsync(fixture, pickup: 101, fixedStation: 202, demandId: DemandB, agvId: AgvB, vehicleKey: KeyB);
        JourneyRuntimeRow before = await fixture.NewContext().JourneyRuntimes.AsNoTracking()
            .SingleAsync(row => row.DemandId == DemandB, Token);

        await Sweep(fixture.Context).ReserveApproachingAsync(new HashSet<int> { 202 }, Token);
        Assert.Equal(KeyA, (await HeldAtAsync(fixture, 202))!.VehicleKey);
        JourneyRuntimeRow after = await fixture.NewContext().JourneyRuntimes.AsNoTracking()
            .SingleAsync(row => row.DemandId == DemandB, Token);
        Assert.Equal((before.Stage, before.BlockReasonCode), (after.Stage, after.BlockReasonCode));
        Assert.Equal(
            DispatchReasonCodes.FixedTaskStationOccupiedByOtherVehicle,
            await new FixedStationSingleOccupancyCriterion(fixture.NewContext()).EvaluateAsync(IdleOnto(202, "KEY-C"), Token));

        Assert.True(await new StationExclusivityStore(fixture.NewContext()).ReleaseAsync(
            25, 202, holder, At.AddMinutes(1), FixedStationExclusivity.ReleasedOnDepartureEvidence, Token));
        Assert.Equal(
            DispatchReasonCodes.FixedTaskStationApproachedByOtherVehicle,
            await new FixedStationSingleOccupancyCriterion(fixture.NewContext()).EvaluateAsync(IdleOnto(202, "KEY-C"), Token));

        await fixture.RenewContextAsync();
        await Sweep(fixture.Context).ReserveApproachingAsync(new HashSet<int> { 202 }, Token);
        StationExclusivity given = (await HeldAtAsync(fixture, 202))!;
        Assert.Equal(
            (KeyB, JourneyIdentity.ForAnchorDemand(DemandB), StationExclusivityStates.Reserved),
            (given.VehicleKey, given.JourneyId, given.State));
        Assert.Equal(
            DispatchReasonCodes.FixedTaskStationReservedByOtherVehicle,
            await new FixedStationSingleOccupancyCriterion(fixture.NewContext()).EvaluateAsync(IdleOnto(202, "KEY-C"), Token));
    }

    /// <summary>
    /// 崩溃点：补预占那次保存出错（注入在独占行的 INSERT 上）。不留半截——没有独占行，也没有开着的经过；下一轮照常补上。
    /// </summary>
    [Fact]
    public async Task AFailureWritingTheCatchUpReservationLeavesNothingAndTheNextRoundTakesIt()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        await SeedHolderAsync(fixture, pickup: 101, fixedStation: 202, demandId: DemandB, agvId: AgvB, vehicleKey: KeyB);
        FailingInsertInterceptor failing = new("\"StationExclusivities\"");
        await using ControlServerDbContext injected = fixture.NewContext(failing);

        await Sweep(injected).ReserveApproachingAsync(new HashSet<int> { 202 }, Token);

        Assert.True(failing.Fired, "The injection never reached the reservation's insert, so it proves nothing.");
        await using ControlServerDbContext read = fixture.NewContext();
        Assert.Empty(await read.Set<StationExclusivityRow>().AsNoTracking().ToArrayAsync(Token));
        Assert.Empty(await read.Set<StationExclusivityRecordRow>().AsNoTracking().ToArrayAsync(Token));
        Assert.False(injected.ChangeTracker.HasChanges(), "The failed reservation stayed staged for the round's next save.");

        await Sweep(fixture.NewContext()).ReserveApproachingAsync(new HashSet<int> { 202 }, Token);
        Assert.Equal(KeyB, (await HeldAtAsync(fixture, 202))!.VehicleKey);
    }

    /// <summary>车不开往公共站点（下一站是机台）时不补；站不在公共站点集合里时也不补。</summary>
    [Fact]
    public async Task OnlyAVehicleWhoseNextStopIsAPublicStationIsGivenACatchUpReservation()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        await AcceptAsync(fixture.Context, DemandB, AgvB, KeyB, pickup: 101, fixedStation: 202);
        await fixture.RenewContextAsync();

        await Sweep(fixture.Context).ReserveApproachingAsync(new HashSet<int> { 202 }, Token);
        Assert.Null(await HeldAtAsync(fixture, 202));
        Assert.Null(await HeldAtAsync(fixture, 101));
    }

    // ---- 第 4 条：与让站的关系 --------------------------------------------------------------------------

    /// <summary>
    /// 公共站点上有持货等单的车（它占用着站）：另一辆车同站的新需求不合格，而受理就算越过判据走到了写，也被主键拒掉、整笔回滚——
    /// 连同那次受理本会写下的让站触发，所以让站不会被触发。
    /// </summary>
    [Fact]
    public async Task AHolderAtAPublicStationIsNeitherJoinedByAnotherVehicleNorAskedToYield()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        await SeedHolderAsync(fixture, StagingStation, fixedStation: StagingStation);

        Assert.Equal(
            DispatchReasonCodes.FixedTaskStationOccupiedByOtherVehicle,
            await new FixedStationSingleOccupancyCriterion(fixture.Context).EvaluateAsync(IdleStaging(KeyB), Token));
        await Assert.ThrowsAsync<BusinessIdentityConflictException>(
            () => AcceptAsync(fixture.NewContext(), DemandB, AgvB, KeyB, StagingStation, StagingStation));

        JourneyRuntimeRow holder = await fixture.NewContext().JourneyRuntimes.AsNoTracking()
            .SingleAsync(row => row.DemandId == DemandA, Token);
        Assert.Null(holder.YieldTriggeredAt);
    }

    /// <summary>机台（AREA）站点不是公共站点：另一辆车被承诺以它为下一站，那里持货等单的车照旧被让站。</summary>
    [Fact]
    public async Task AHolderAtAMachineStationStillYieldsToAVehicleCommittedThere()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        await SeedHolderAsync(fixture, pickup: 101, fixedStation: 202);

        await AcceptAsync(fixture.NewContext(), DemandB, AgvB, KeyB, pickup: 101, fixedStation: 202);

        JourneyRuntimeRow holder = await fixture.NewContext().JourneyRuntimes.AsNoTracking()
            .SingleAsync(row => row.DemandId == DemandA, Token);
        Assert.Equal(KeyB, holder.YieldTriggeredByVehicleKey);
        Assert.Empty(await fixture.NewContext().Set<StationExclusivityRow>().AsNoTracking().ToArrayAsync(Token));
    }

    // ---- helpers ------------------------------------------------------------------------------------

    private static async Task AcceptAsync(
        ControlServerDbContext context,
        string demandId,
        string agvId,
        string vehicleKey,
        int pickup,
        int fixedStation,
        DateTimeOffset? at = null)
    {
        DateTimeOffset when = at ?? At;
        JourneyExecutionPlan plan = Batch7JourneyFixture.Plan(demandId, agvId, vehicleKey, when) with
        {
            PickupStationId = $"ST-{pickup}",
            PickupStationRiotId = pickup,
            FixedTaskStationRiotId = fixedStation,
        };
        await new WireToGateStore(context).AcceptWithOrderIntentAsync(
            Batch7JourneyFixture.Snapshot(demandId, when),
            JourneyPlanBuilder.PickupIntent(plan, demandId, when),
            plan,
            Token);
    }

    /// <summary>车 A 站在取货停靠上持货等单，它的取货站是 <paramref name="pickup"/>；公共站点的话它占用着。</summary>
    private static async Task SeedHolderAsync(
        Batch7JourneyFixture fixture,
        int pickup,
        int fixedStation,
        string demandId = DemandA,
        string agvId = AgvA,
        string vehicleKey = KeyA)
    {
        await AcceptAsync(fixture.Context, demandId, agvId, vehicleKey, pickup, fixedStation);
        JourneyRuntimeRow holder = await fixture.Context.JourneyRuntimes.SingleAsync(row => row.DemandId == demandId, Token);
        holder.Stage = JourneyRuntimeStage.AwaitingStationDeparture;
        holder.LoadingPhaseState = LoadingPhaseStates.CargoHoldingWait;
        await fixture.Context.SaveChangesAsync(Token);
        if (pickup == fixedStation)
        {
            Assert.True(await new StationExclusivityStore(fixture.Context).MarkOccupiedAsync(
                25, pickup, holder.JourneyId, At.AddMinutes(1), Token));
        }
        await fixture.RenewContextAsync();
    }

    private static async Task AssertNothingOfTheAcceptanceAsync(Batch7JourneyFixture fixture, string demandId, string vehicleKey)
    {
        await using ControlServerDbContext read = fixture.NewContext();
        Assert.False(await read.AcceptedDemands.AnyAsync(row => row.DemandId == demandId, Token));
        Assert.False(await read.JourneyRuntimes.AnyAsync(row => row.DemandId == demandId, Token));
        Assert.False(await read.OrderIntents.AnyAsync(row => row.DemandId == demandId, Token));
        Assert.False(await read.Set<VehiclePurposeClaimRow>().AnyAsync(row => row.VehicleKey == vehicleKey, Token));
    }

    private static FixedStationExclusivitySweep Sweep(ControlServerDbContext context) =>
        new(context, null!, new FixedClock(At.AddMinutes(5)), Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

    private static async Task<StationExclusivity?> HeldAtAsync(Batch7JourneyFixture fixture, int station)
    {
        await using ControlServerDbContext read = fixture.NewContext();
        return await new StationExclusivityStore(read).ReadAsync(25, station, Token);
    }

    /// <summary>一辆空闲车的候选，取货站（也就是它的下一站）是公共站点 <paramref name="station"/>。</summary>
    private static DispatchCandidateEvaluation IdleOnto(int station, string vehicleKey) =>
        Evaluation(
            vehicleKey,
            new ResolvedJourneyRoute(
                "ZONE", "EVIDENCE", $"ST-{station}", station, "N1-1", Machine,
                FixedTaskStationResolution.Resolved(
                    TransportTaskTypes.StagingToWire, FixedStationEnd.Origin, new RiotMapStation(station, $"ST-{station}"))),
            plan: null,
            placement: null);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static StationExclusivityRequest Reserve(int station) =>
        new(25, station, StationExclusivityKinds.FixedTaskStation, StationExclusivityStates.Reserved, null);

    private static ResolvedJourneyRoute Staging() => new(
        "ZONE", "EVIDENCE", StagingName, StagingStation, "N1-1", Machine,
        FixedTaskStationResolution.Resolved(
            TransportTaskTypes.StagingToWire, FixedStationEnd.Origin, new RiotMapStation(StagingStation, StagingName)));

    private static ResolvedJourneyRoute WireToGate() => new(
        "ZONE", "EVIDENCE", "N1-1", Machine, "关卡", Gate,
        FixedTaskStationResolution.Resolved(
            TransportTaskTypes.WireToGate, FixedStationEnd.Destination, new RiotMapStation(Gate, "关卡")));

    private static DispatchCandidateEvaluation IdleStaging(string vehicleKey) =>
        Evaluation(vehicleKey, Staging(), plan: null, placement: null);

    private static DispatchCandidateEvaluation IdleWireToGate(string vehicleKey) =>
        Evaluation(vehicleKey, WireToGate(), plan: null, placement: null);

    /// <summary>
    /// 一辆在途车，计划是 [已完成的 S0, 当前停靠 S1（机台 101）, S2（机台 102）]。候选（<c>STAGING_TO_WIRE</c>）带来取货 305 与卸货 12：
    /// <paramref name="pickupRightAfterCurrent"/> 为真时 305 插在 S1 之后，否则插在 S2 之后。
    /// </summary>
    private static DispatchCandidateEvaluation InTransitStaging(string vehicleKey, bool standing, bool pickupRightAfterCurrent)
    {
        string pickup = JourneyIdentity.AppendedPickupStopId(DemandB);
        string unload = JourneyIdentity.AppendedUnloadStopId(DemandB);
        EnRouteVehiclePlan plan = new(
            [
                new EnRouteStop("S0", "ST-100", 100, "ZONE", JourneyStopRoles.Pickup),
                new EnRouteStop("S1", "ST-101", 101, "ZONE", JourneyStopRoles.Pickup),
                new EnRouteStop("S2", "ST-102", 102, "ZONE", JourneyStopRoles.Unload),
            ],
            VehicleStationRiotId: 101,
            CurrentNextStopIndex: 1,
            new Dictionary<string, int>(StringComparer.Ordinal) { ["S0"] = 0, ["S1"] = 1, ["S2"] = 1 },
            StandsAtCurrentStop: standing);
        EnRouteStopSequence[] resequenced = pickupRightAfterCurrent
            ? [new("S0", 1), new("S1", 2), new(pickup, 3), new("S2", 4), new(unload, 5)]
            : [new("S0", 1), new("S1", 2), new("S2", 3), new(pickup, 4), new(unload, 5)];
        EnRouteAppendPlacement placement = new(
            null, pickupRightAfterCurrent ? 3 : 4, null, 5, 1000, resequenced);
        return Evaluation(vehicleKey, Staging(), plan, placement);
    }

    private static DispatchCandidateEvaluation Evaluation(
        string vehicleKey, ResolvedJourneyRoute route, EnRouteVehiclePlan? plan, EnRouteAppendPlacement? placement)
    {
        AcceptedDemandSnapshot candidate = Batch7JourneyFixture.Snapshot(DemandB, At);
        DispatchCandidateEvaluation evaluation = new(
            candidate,
            new DispatchRoundFacts(
                new DemandCatalogSnapshot(candidate.HistoryEpoch, 21, [candidate]),
                new RiotMapStationCatalogSnapshot(25, At, new string('c', 64), []),
                new SingleStationView(new RiotMapStation(Gate, "关卡")),
                new HashSet<string>(StringComparer.Ordinal),
                At,
                new VehicleDispatchPolicy([], new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal), "TEST")),
            new DispatchVehicleFacts(
                vehicleKey,
                $"AGV-{vehicleKey}",
                null,
                new RiotVehicleObservation(vehicleKey, true, true, "IDLE", "MAP-25", 101, 80, "NO_CHARGE", 0, At),
                At,
                Plan: plan))
        {
            Route = route,
            AppendPlacement = placement,
        };
        return evaluation;
    }

    private static EligibleVehicleOffer Offer(string agvId, int currentStation, long cost, ResolvedJourneyRoute route) => new(
        new FleetVehicle(agvId, $"VK-{agvId}", 1),
        new DispatchVehicleFacts(
            $"VK-{agvId}",
            agvId,
            new OnboardDispatchFacts(1, [1], true, true, true, true, false),
            new RiotVehicleObservation($"VK-{agvId}", true, true, "IDLE", "MAP-25", currentStation, 50, "DISCHARGING", 0, At),
            At),
        new EligibleDispatchCandidate(Batch7JourneyFixture.Snapshot(DemandB, At), route, 1, [1], At, cost),
        cost,
        Placement: null,
        DispatchZoneParameterVersion: null);

    private static async Task<StationExclusivity?> HeldAsync(RuntimeFixture fixture)
    {
        await using ControlServerDbContext read = fixture.OpenConnectionContext();
        return await new StationExclusivityStore(read).ReadAsync(25, StagingStation, Token);
    }

    private static async Task<RuntimeFixture> WithStagingToWireBoundAsync()
    {
        RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Options.AllowedWorkTypes = [TransportTaskTypes.WireToGate, TransportTaskTypes.StagingToWire];
        fixture.Riot.SetMapStations(
            new RiotMapStation(Machine, "N1-1"),
            new RiotMapStation(13, "N1-2_N1-3"),
            new RiotMapStation(TaskTypeStationRuntimeSeed.GateStationRiotId, TaskTypeStationRuntimeSeed.GateStationName),
            new RiotMapStation(300, "等待点"),
            new RiotMapStation(StagingStation, StagingName));
        await TaskTypeStationRuntimeSeed.ActivateAsync(
            fixture.DbOptionsForTests,
            Now,
            requiredTaskTypes: [TransportTaskTypes.WireToGate, TransportTaskTypes.StagingToWire],
            bindings:
            [
                TaskTypeStationRuntimeSeed.GateBinding,
                new TaskTypeStationBinding(TransportTaskTypes.StagingToWire, StagingStation, StagingName, "SITE-CHECK-STAGING"),
            ]);
        return fixture;
    }

    private static AcceptedDemandSnapshot Reverse(RuntimeFixture fixture, string demandId, string sublot) =>
        fixture.Demand(demandId, sublot, Now.AddMinutes(-10), "N1-1") with
        {
            WorkType = TransportTaskTypes.StagingToWire,
            TransportDemandKey = $"{sublot}|{TransportTaskTypes.StagingToWire}",
        };

    /// <summary>Throws on the first INSERT into <paramref name="table"/>, the way a failing disk or a lost connection would.</summary>
    private sealed class FailingInsertInterceptor(string table) : DbCommandInterceptor
    {
        public bool Fired { get; private set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            ThrowOnInsert(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            ThrowOnInsert(command);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void ThrowOnInsert(DbCommand command)
        {
            if (command.CommandText.Contains($"INSERT INTO {table}", StringComparison.Ordinal))
            {
                Fired = true;
                throw new InvalidOperationException("Injected: the reservation insert failed.");
            }
        }
    }
}
