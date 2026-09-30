using System.Data.Common;
using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Charging;
using ControlServer.Host.Runtime.Dispatch;
using ControlServer.Host.Runtime.Dispatch.Criteria;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using FleetFixture = ControlServer.Tests.MultiVehicleExecutionTests.FleetFixture;

namespace ControlServer.Tests;

/// <summary>
/// 充电分配（批次9-06，control-server#404）：谁进待充电集合、候选链、按电量排队、原子承诺、预占后不被抢，以及名册为空时退化到服务端持有的
/// 人工充电等待。在引擎的真实轮次里跑（派车轮的任务循环之前分配），RIoT 与车载端用车队夹具的替身。
/// </summary>
/// <remarks>
/// <b>路网</b>：车队夹具默认的单向链（300 → 12 → 13 → 210）之外，加两个充电桩 211、221 与一个等待点 214，从车位 300 两个方向都连着
/// （代价 5000、8000、9000 毫米）。车默认停在 300，所以 211 是最近的桩。夹具的测试策略两道线都是 40、救命线 15。
/// </remarks>
public sealed class ChargingAllocationTests
{
    private const int Map = 25;
    internal static readonly ChargerRosterEntry Near = new(Map, 211, "充电点1", null, null, []);
    internal static readonly ChargerRosterEntry Far = new(Map, 221, "充电点2", null, null, []);
    private static readonly WaitingPointEntry WaitingPoint = new(Map, 214, "等待点214", true, []);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string AgvA => FleetFixture.AgvIds[0];
    private static string KeyA => FleetFixture.VehicleKeys[0];
    private static string AgvB => FleetFixture.AgvIds[1];
    private static string KeyB => FleetFixture.VehicleKeys[1];
    private static string AgvC => FleetFixture.AgvIds[2];
    private static string KeyC => FleetFixture.VehicleKeys[2];

    // ---- 承诺 ----------------------------------------------------------------------------------------------------------

    /// <summary>
    /// 低于强制充电线、此刻没有用途的车：同一轮里取得充电周期（<c>ALLOCATED</c>，记下名册版本与这一轮判它的策略版本）、<c>CHARGING</c>
    /// 用途占有、最近那个桩的 <c>CHARGER</c> 预占、一趟没有需求的旅程、一个 <c>CHARGER</c> 停靠与一张还没发出的充电意图。
    /// </summary>
    [Fact]
    public async Task AVehicleBelowItsLineCommitsTheCycleThePurposeTheReservationTheJourneyAndTheIntentTogether()
    {
        await using FleetFixture fleet = await FleetAsync(chargers: [Far, Near]);
        fleet.Riot.BatteryByVehicle[KeyA] = 20;

        await RoundAsync(fleet);

        ChargingCycleRow cycle = await fleet.Context.Set<ChargingCycleRow>().AsNoTracking().SingleAsync(Token);
        Assert.Equal(
            (KeyA, Near.StationId, ChargingCycleWireStates.Allocated, ChargingCyclePhases.Active, 1L, 1L),
            (cycle.VehicleKey, cycle.StationId, cycle.WireState, cycle.Phase, cycle.ChargerRosterVersion, cycle.ChargingPolicyVersion));
        Assert.Equal((VehiclePurposes.Charging, cycle.JourneyId), await ClaimOfAsync(fleet, KeyA));
        StationExclusivityRow reserved = (await StationAsync(fleet, Near.StationId))!;
        Assert.Equal(
            (StationExclusivityKinds.Charger, StationExclusivityStates.Reserved, KeyA, cycle.JourneyId, (long?)1),
            (reserved.StationKind, reserved.State, reserved.VehicleKey, reserved.JourneyId, reserved.ChargerRosterVersion));
        Assert.Null(await StationAsync(fleet, Far.StationId));

        JourneyRuntimeRow journey = await fleet.Context.JourneyRuntimes.AsNoTracking().SingleAsync(Token);
        Assert.True(journey.IsCharging());
        Assert.Equal(
            (cycle.JourneyId, (string?)null, AgvA, Near.StationId, (long?)1, BatteryStates.MandatoryCharge),
            (journey.JourneyId, journey.DemandId, journey.AgvId, journey.PickupStationRiotId, journey.ChargingPolicyVersion,
                journey.PublishedBatteryState));
        Assert.Equal(ChargingIdentity.CycleIdFor(journey.JourneyId), cycle.CycleId);
        JourneyStopRow stop = await fleet.Context.Set<JourneyStopRow>().AsNoTracking().SingleAsync(Token);
        Assert.Equal((JourneyStopRoles.Charger, Near.StationId, Near.StationName), (stop.StopRole, stop.StationRiotId, stop.StationId));
        OrderIntentRow intent = await fleet.Context.OrderIntents.AsNoTracking().SingleAsync(Token);
        Assert.Equal(
            (ChargingIdentity.UpperIdFor(journey.JourneyId), OrderShapes.Charge, ChargingJourneyShape.IntentPurpose,
                "PENDING_RECONCILIATION", Near.StationId, (string?)null),
            (intent.UpperId, intent.OrderShape, intent.Purpose, intent.Status, intent.DestinationStationId, intent.DemandId));
        // Nothing is sent to RIoT in the round that commits: the order is created by the advance, behind the departure gate.
        Assert.Empty(fleet.Riot.Creates);
        Assert.Equal(ChargingAllocationReasons.Committed, fleet.ChargingBoard.Verdicts[AgvA].Reason);
    }

    /// <summary>
    /// 承诺时存下的意图就是引擎给这一段腿构造的那一个（<c>JourneyPlanBuilder.LegIntent</c>），只有 <c>Purpose</c> 是充电自己的；形态经停靠角色
    /// 取为 <c>CHARGE</c>，不是手写的。两处各构造各的，授权时每轮抛冲突（准入线 2），或者充电单降成单段、车到桩不通电。
    /// </summary>
    [Fact]
    public async Task TheCommittedIntentIsTheOneTheEngineBuildsForThatLeg()
    {
        await using FleetFixture fleet = await FleetAsync();
        fleet.Riot.BatteryByVehicle[KeyA] = 20;
        await RoundAsync(fleet);

        JourneyRuntimeRow journey = await fleet.Context.JourneyRuntimes.AsNoTracking().SingleAsync(Token);
        JourneyStopRow stop = await fleet.Context.Set<JourneyStopRow>().AsNoTracking().SingleAsync(Token);
        OrderIntentRow stored = await fleet.Context.OrderIntents.AsNoTracking().SingleAsync(Token);
        OrderIntent legIntent = JourneyPlanBuilder.LegIntent(journey, stop, stored.CreatedAt);

        Assert.Equal(OrderShapes.Charge, JourneyStopRoles.OrderShapeOf(stop.StopRole));
        Assert.Equal(
            legIntent with { Purpose = ChargingJourneyShape.IntentPurpose },
            new OrderIntent(
                stored.MovementLegId, stored.DemandId, stored.UpperId, stored.Purpose, stored.TargetStationId, stored.CreatedAt,
                stored.VehicleKey, stored.MapId, stored.DestinationStationId, stored.AgvLifecycleGeneration,
                stored.DispatchGeneration, stored.OrderShape));
    }

    /// <summary>电量不低于线的车不进待充电集合：什么也不写、不问 RIoT 的订单清单，结论是「不需要充电」。</summary>
    [Fact]
    public async Task AVehicleAtOrAboveItsLineIsNotAllocated()
    {
        await using FleetFixture fleet = await FleetAsync();
        fleet.Riot.BatteryByVehicle[KeyA] = 40;

        await RoundAsync(fleet);

        Assert.Null(await ClaimOfAsync(fleet, KeyA));
        Assert.Empty(await fleet.Context.Set<ChargingCycleRow>().AsNoTracking().ToArrayAsync(Token));
        Assert.Equal(ChargingAllocationReasons.NotRequired, fleet.ChargingBoard.Verdicts[AgvA].Reason);
    }

    // ---- 候选链（REQ-0170、REQ-0171）------------------------------------------------------------------------------------

    /// <summary>
    /// 名册之外的站一个都不进，不管它在 RIoT 上叫什么（<c>REQ-0171</c>）：地图上有一个更近、名字就叫「充电点1」的站 211，名册里只有 221——
    /// 车去 221。
    /// </summary>
    [Fact]
    public async Task AStationOutsideTheRosterIsNeverACandidateWhateverItIsCalled()
    {
        await using FleetFixture fleet = await FleetAsync(chargers: [Far]);
        fleet.Riot.ExtraStations.Add(new RiotMapStation(Near.StationId, Near.StationName));
        fleet.Riot.BatteryByVehicle[KeyA] = 20;

        await RoundAsync(fleet);

        Assert.NotNull(await StationAsync(fleet, Far.StationId));
        Assert.Null(await StationAsync(fleet, Near.StationId));
    }

    /// <summary>每辆车的候选集是名册的子集：桩的车辆范围里没有这辆车，它就不是这辆车的候选，哪怕它更近。</summary>
    [Fact]
    public async Task AChargerWhoseVehicleScopeLeavesTheVehicleOutIsNotItsCandidate()
    {
        await using FleetFixture fleet = await FleetAsync(chargers: [Near with { VehicleScope = [KeyB] }, Far]);
        fleet.Riot.BatteryByVehicle[KeyA] = 20;

        await RoundAsync(fleet);

        Assert.Equal(KeyA, (await StationAsync(fleet, Far.StationId))!.VehicleKey);
        Assert.Null(await StationAsync(fleet, Near.StationId));
    }

    public static TheoryData<string, string, bool> ExcludedChargers => new()
    {
        { "just-failed", ChargingAllocationReasons.ChargerFailedJustNow, true },
        { "allocation-held", ChargingAllocationReasons.ChargerAllocationHeld, true },
        { "not-on-current-map", ChargingAllocationReasons.ChargerNotOnCurrentMap, true },
        { "renamed-on-current-map", ChargingAllocationReasons.ChargerNotOnCurrentMap, true },
        { "reserved-by-another-vehicle", ChargingAllocationReasons.ChargerReservedOrOccupied, true },
        { "project-vehicle-standing-on-it", ChargingAllocationReasons.ChargerOccupiedByVehicle, true },
        { "running-order-heading-for-it", ChargingAllocationReasons.ChargerTargetedByRunningOrder, true },
        { "a-vehicle-position-unreadable", ChargingAllocationReasons.ChargerOccupancyUnknown, false },
        { "a-vehicle-position-stale", ChargingAllocationReasons.ChargerOccupancyUnknown, false },
        { "a-vehicle-position-missing", ChargingAllocationReasons.ChargerOccupancyUnknown, false },
        { "order-listing-incomplete", ChargingAllocationReasons.ChargerOccupancyUnknown, false },
        { "running-order-destination-unreadable", ChargingAllocationReasons.ChargerOccupancyUnknown, false },
    };

    /// <summary>
    /// 候选链的每一步（<c>REQ-0170</c>）：刚失败、分配暂停、不在当前地图（或站名变了）、被别的车预占、本项目的车停在上面、有在跑的单以它为目的站，
    /// 以及占用事实读不到或过期——每一条都让最近的桩 211 不是候选，原因码精确。前七条只排除 211，车去过滤后最近的 221（不从失败的桩回填）；
    /// 后五条是「事实未知」，未知不分桩，两个桩都不是候选，车不被分配、什么也不留下。
    /// </summary>
    [Theory]
    [MemberData(nameof(ExcludedChargers))]
    public async Task EachReasonAChargerIsNotACandidateIsNamedAndOnlyTheFilteredSetIsSearched(
        string why, string expected, bool theFarChargerIsStillChosen)
    {
        await using FleetFixture fleet = await FleetAsync(vehicles: 2, chargers: [Near, Far]);
        fleet.Riot.BatteryByVehicle[KeyA] = 20;
        DateTimeOffset now = fleet.Clock.GetUtcNowWithoutTick();
        switch (why)
        {
            case "just-failed":
                await EndedCycleAsync(fleet, KeyA, Near.StationId, ChargingExecutionReasons.OrderEnded, now.AddSeconds(-5));
                break;
            case "allocation-held":
                await new ChargingHoldStore(fleet.Context).RecordStationHoldAsync(
                    new ChargingStationAllocationHold(
                        "hold-1", "hold-key-1", ChargingStationHoldTriggers.Maintenance, Map, Near.StationId, 1, null, null, null,
                        null, null, null, null, null, null, null, now, null, null, null, null, null, null, null, null, null, null,
                        null),
                    Token);
                break;
            case "not-on-current-map":
                fleet.Riot.ExtraStations.RemoveAll(station => station.StationId == Near.StationId);
                break;
            case "renamed-on-current-map":
                fleet.Riot.ExtraStations.RemoveAll(station => station.StationId == Near.StationId);
                fleet.Riot.ExtraStations.Add(new RiotMapStation(Near.StationId, "改了名的站"));
                break;
            case "reserved-by-another-vehicle":
                Assert.Equal(
                    StationExclusivityAcquisitionOutcome.Acquired,
                    await new StationExclusivityStore(fleet.Context).TryAcquireAsync(
                        new StationExclusivityRequest(
                            Map, Near.StationId, StationExclusivityKinds.Charger, StationExclusivityStates.Reserved, null, 1),
                        KeyB, "charging:someone-else", now, Token));
                break;
            case "project-vehicle-standing-on-it":
                fleet.Riot.VehicleOverrides[KeyB] = seen => seen with { CurrentStationId = Near.StationId };
                break;
            case "running-order-heading-for-it":
                fleet.Riot.ForeignOrders.Add((new RiotListedOrder("F-1", "FOREIGN-1", RiotOrderState.Executing, KeyB, KeyB), Near.StationId));
                break;
            case "a-vehicle-position-unreadable":
                fleet.Riot.FailOn = KeyB;
                break;
            case "a-vehicle-position-stale":
                fleet.Riot.VehicleOverrides[KeyB] = seen => seen with { ObservedAt = seen.ObservedAt.AddMinutes(-10) };
                break;
            case "a-vehicle-position-missing":
                fleet.Riot.VehicleOverrides[KeyB] = seen => seen with { CurrentStationId = null };
                break;
            case "order-listing-incomplete":
                fleet.Riot.OrderListingIncomplete = true;
                break;
            case "running-order-destination-unreadable":
                fleet.Riot.ForeignOrders.Add((new RiotListedOrder("F-2", "FOREIGN-2", RiotOrderState.Executing, KeyB, KeyB), null));
                break;
        }
        fleet.Context.ChangeTracker.Clear();

        await RoundAsync(fleet);

        if (theFarChargerIsStillChosen)
        {
            Assert.Equal(KeyA, (await StationAsync(fleet, Far.StationId))?.VehicleKey);
            Assert.NotEqual(KeyA, (await StationAsync(fleet, Near.StationId))?.VehicleKey);
            Assert.Equal(ChargingAllocationReasons.Committed, fleet.ChargingBoard.Verdicts[AgvA].Reason);
            Assert.Contains($"{Near.StationId}={expected}", fleet.ChargingBoard.Verdicts[AgvA].Detail, StringComparison.Ordinal);
            return;
        }

        (string reason, string detail) = fleet.ChargingBoard.Verdicts[AgvA];
        Assert.Equal(ChargingAllocationReasons.NoChargerAvailable, reason);
        Assert.Contains($"{Near.StationId}={expected}", detail, StringComparison.Ordinal);
        Assert.Contains($"{Far.StationId}={expected}", detail, StringComparison.Ordinal);
        await AssertNothingOfACommitmentAsync(fleet, KeyA);
    }

    /// <summary>路网上到不了的桩不是候选；最近优先只在过滤后的集合里算。</summary>
    [Fact]
    public async Task AChargerTheRouteGraphCannotReachIsNotACandidate()
    {
        ChargerRosterEntry offTheGraph = new(Map, 231, "充电点3", null, null, []);
        await using FleetFixture fleet = await FleetAsync(chargers: [offTheGraph, Far]);
        fleet.Riot.BatteryByVehicle[KeyA] = 20;

        await RoundAsync(fleet);

        Assert.Equal(KeyA, (await StationAsync(fleet, Far.StationId))?.VehicleKey);
        Assert.Contains(
            $"{offTheGraph.StationId}={ChargingAllocationReasons.ChargerUnreachable}",
            fleet.ChargingBoard.Verdicts[AgvA].Detail, StringComparison.Ordinal);
    }

    // ---- 排队（REQ-0172、REQ-0173）--------------------------------------------------------------------------------------

    /// <summary>一桩三车：电量最低的那辆取得；另两辆留在队里，原因是桩已被预占，什么也没留下。</summary>
    [Fact]
    public async Task WithOneChargerAndThreeVehiclesTheLowestBatteryGetsIt()
    {
        await using FleetFixture fleet = await FleetAsync(vehicles: 3);
        fleet.Riot.BatteryByVehicle[KeyA] = 30;
        fleet.Riot.BatteryByVehicle[KeyB] = 20;
        fleet.Riot.BatteryByVehicle[KeyC] = 25;

        await RoundAsync(fleet);

        Assert.Equal(KeyB, (await StationAsync(fleet, Near.StationId))!.VehicleKey);
        Assert.Equal(VehiclePurposes.Charging, (await ClaimOfAsync(fleet, KeyB))!.Value.Purpose);
        foreach ((string agv, string key) in new[] { (AgvA, KeyA), (AgvC, KeyC) })
        {
            (string reason, string detail) = fleet.ChargingBoard.Verdicts[agv];
            Assert.Equal(ChargingAllocationReasons.NoChargerAvailable, reason);
            Assert.Contains(
                $"{Near.StationId}={ChargingAllocationReasons.ChargerReservedOrOccupied}", detail, StringComparison.Ordinal);
            await AssertNothingOfACommitmentAsync(fleet, key);
        }
    }

    /// <summary>
    /// 充电失败过的车不取得独立优先级（<c>REQ-0172</c>）：它与普通待充车同一队、统一按电量排，失败这件事不让它排到电量更低的车前面。
    /// </summary>
    [Fact]
    public async Task AVehicleWhoseChargingFailedBeforeGetsNoPlaceAheadOfALowerBattery()
    {
        await using FleetFixture fleet = await FleetAsync(vehicles: 2);
        fleet.Riot.BatteryByVehicle[KeyA] = 30;
        fleet.Riot.BatteryByVehicle[KeyB] = 20;
        // A failed at this charger long enough ago that the charger is no longer "just failed" for it.
        await EndedCycleAsync(
            fleet, KeyA, Near.StationId, ChargingExecutionReasons.OrderEnded, fleet.Clock.GetUtcNowWithoutTick().AddHours(-2));

        await RoundAsync(fleet);

        Assert.Equal(KeyB, (await StationAsync(fleet, Near.StationId))!.VehicleKey);
        Assert.Null(await ClaimOfAsync(fleet, KeyA));
    }

    /// <summary>电量未知的车不参与排序，也不因为「未知可能更低」而挡住读得到电量的车。</summary>
    [Fact]
    public async Task AVehicleWhoseBatteryIsUnknownIsNotQueued()
    {
        await using FleetFixture fleet = await FleetAsync(vehicles: 2);
        fleet.Riot.BatteryByVehicle[KeyA] = null;
        fleet.Riot.BatteryByVehicle[KeyB] = 35;

        await RoundAsync(fleet);

        Assert.Equal(KeyB, (await StationAsync(fleet, Near.StationId))!.VehicleKey);
        Assert.Equal(VehicleDynamicFactsCriterion.BatteryFactUnknownReason, fleet.ChargingBoard.Verdicts[AgvA].Reason);
        await AssertNothingOfACommitmentAsync(fleet, KeyA);
    }

    // ---- 并发读改写 ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// 两辆车同一轮争同一个桩：绕开选桩的预读，直接在承诺那一刻构造——只有一辆成功；另一辆的用途占有、周期行、旅程行、停靠与订单意图
    /// 全部没有留下，变更跟踪里也没有留下任何待写的行。输赢由站点独占的主键决定，不先读后写。
    /// </summary>
    [Fact]
    public async Task TwoVehiclesCommittingToOneChargerAtTheSameMomentLeaveOnlyOneWithAnything()
    {
        await using FleetFixture fleet = await FleetAsync(vehicles: 2);
        DateTimeOffset now = fleet.Clock.GetUtcNowWithoutTick();

        ChargingCycleStartOutcome first = await CommitDirectlyAsync(fleet, 0, Near, now);
        ChargingCycleStartOutcome second = await CommitDirectlyAsync(fleet, 1, Near, now);

        Assert.Equal((ChargingCycleStartOutcome.Started, ChargingCycleStartOutcome.StationHeld), (first, second));
        Assert.DoesNotContain(
            fleet.Context.ChangeTracker.Entries(),
            entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted);
        fleet.Context.ChangeTracker.Clear();
        Assert.Equal(KeyA, (await StationAsync(fleet, Near.StationId))!.VehicleKey);
        await AssertNothingOfACommitmentAsync(fleet, KeyB);
        Assert.Null(await fleet.Context.Set<VehicleSnapshotRevisionRow>().AsNoTracking()
            .SingleOrDefaultAsync(row => row.AgvId == AgvB, Token));
    }

    /// <summary>
    /// 同一辆车既是搬运候选又进待充电（它在两次读之间越过强制充电线）：用途占有的主键决定只有一个成立。先承诺充电，搬运的受理被整个拒掉；
    /// 先受理搬运，充电的承诺答「车被占着」、什么也不留——不会出现两份用途、两趟旅程或两张单。
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OneVehicleNeverHoldsATransportAndAChargingPurposeAtOnce(bool chargingFirst)
    {
        await using FleetFixture fleet = await FleetAsync();
        DateTimeOffset now = fleet.Clock.GetUtcNowWithoutTick();
        AcceptedDemandSnapshot demand = FleetFixture.Demand(0, "N1-1", 0);

        if (chargingFirst)
        {
            Assert.Equal(ChargingCycleStartOutcome.Started, await CommitDirectlyAsync(fleet, 0, Near, now));
            fleet.Context.ChangeTracker.Clear();
            await Assert.ThrowsAnyAsync<Exception>(() => Batch7JourneyFixture.AcceptAsync(fleet.Context, demand.DemandId, AgvA, KeyA, now));
        }
        else
        {
            await Batch7JourneyFixture.AcceptAsync(fleet.Context, demand.DemandId, AgvA, KeyA, now);
            fleet.Context.ChangeTracker.Clear();
            Assert.Equal(ChargingCycleStartOutcome.VehicleHeld, await CommitDirectlyAsync(fleet, 0, Near, now));
        }

        fleet.Context.ChangeTracker.Clear();
        VehiclePurposeClaimRow claim = await fleet.Context.Set<VehiclePurposeClaimRow>().AsNoTracking().SingleAsync(Token);
        Assert.Equal(chargingFirst ? VehiclePurposes.Charging : VehiclePurposes.Transport, claim.Purpose);
        Assert.Single(await fleet.Context.JourneyRuntimes.AsNoTracking().ToArrayAsync(Token));
        Assert.Equal(chargingFirst ? 1 : 0, await fleet.Context.Set<ChargingCycleRow>().AsNoTracking().CountAsync(Token));
        Assert.Equal(chargingFirst ? 1 : 0, await fleet.Context.Set<StationExclusivityRow>().AsNoTracking().CountAsync(Token));
    }

    /// <summary>
    /// 在轮次里：已承诺充电的车这一轮与之后都不接搬运——需求留在积压里，原因是「这辆车已承诺充电」；它只有一份用途、没有搬运旅程。
    /// </summary>
    [Fact]
    public async Task AVehicleCommittedToChargingTakesNoTransport()
    {
        await using FleetFixture fleet = await FleetAsync();
        fleet.Riot.BatteryByVehicle[KeyA] = 20;
        AcceptedDemandSnapshot demand = FleetFixture.Demand(0, "N1-1", 0);
        fleet.Catalog.Set([demand]);

        await RoundAsync(fleet);
        await RoundAsync(fleet);

        Assert.Equal(VehiclePurposes.Charging, (await ClaimOfAsync(fleet, KeyA))!.Value.Purpose);
        Assert.Empty(await fleet.Context.JourneyRuntimes.AsNoTracking().Where(row => row.DemandId != null).ToArrayAsync(Token));
        Assert.Equal(
            DispatchReasonCodes.VehicleCommittedToCharging,
            (await fleet.Context.JourneyBacklog.AsNoTracking().SingleAsync(row => row.DemandId == demand.DemandId, Token)).ReasonCode);
    }

    /// <summary>
    /// 写 <c>CHARGER</c> 预占那一刻崩了：用途占有、周期、旅程、停靠、订单意图全都不留，按车计数器也没被推进；这辆车这一轮答「评估失败」，
    /// 下一轮照常承诺。
    /// </summary>
    [Fact]
    public async Task ACrashWhileWritingTheReservationLeavesNothingOfTheCommitment()
    {
        FailingInsert crash = new("INSERT INTO \"StationExclusivities\"");
        await using FleetFixture fleet = await FleetAsync(commands: crash);
        fleet.Riot.BatteryByVehicle[KeyA] = 20;
        crash.Armed = true;

        await RoundAsync(fleet);

        Assert.Equal(1, crash.Fired);
        await AssertNothingOfACommitmentAsync(fleet, KeyA);
        Assert.Null(await StationAsync(fleet, Near.StationId));
        Assert.Equal(ChargingAllocationReasons.EvaluationFailed, fleet.ChargingBoard.Verdicts[AgvA].Reason);

        await RoundAsync(fleet);

        Assert.Equal(VehiclePurposes.Charging, (await ClaimOfAsync(fleet, KeyA))!.Value.Purpose);
        Assert.Equal(KeyA, (await StationAsync(fleet, Near.StationId))!.VehicleKey);
    }

    // ---- 预占后不可抢占（REQ-0173）--------------------------------------------------------------------------------------

    /// <summary>
    /// A 已预占、建单结果未知；之后 C 的电量降到比 A 更低。电量优先只在没有预占的车之间比：C 不被分到这个桩（原因是桩已预占），A 的预占、
    /// 周期与单号逐字不变，也没有取消命令发出。
    /// </summary>
    [Fact]
    public async Task AReservationIsNotTakenByAVehicleWhoseBatteryFallsLowerWhileTheCreateResultIsUnknown()
    {
        await using FleetFixture fleet = await FleetAsync(vehicles: 3);
        fleet.Riot.BatteryByVehicle[KeyA] = 20;
        fleet.Riot.CreateAnswer = intent => new RiotOrderObservation(intent.UpperId, RiotOrderObservationKind.Unknown, null);
        await RoundAsync(fleet);
        await RoundAsync(fleet);
        (string CycleId, string JourneyId, string UpperId) before = await CommitmentOfAsync(fleet, KeyA);
        Assert.Single(fleet.Riot.Creates);

        fleet.Riot.BatteryByVehicle[KeyC] = 5;
        for (int round = 0; round < 3; round++)
        {
            await RoundAsync(fleet);
        }

        Assert.Equal(before, await CommitmentOfAsync(fleet, KeyA));
        Assert.Equal((KeyA, StationExclusivityStates.Reserved), await HolderAsync(fleet, Near.StationId));
        (string reason, string detail) = fleet.ChargingBoard.Verdicts[AgvC];
        Assert.Equal(ChargingAllocationReasons.NoChargerAvailable, reason);
        Assert.Contains($"{Near.StationId}={ChargingAllocationReasons.ChargerReservedOrOccupied}", detail, StringComparison.Ordinal);
        await AssertNothingOfACommitmentAsync(fleet, KeyC);
        Assert.Single(fleet.Riot.Creates);
        Assert.Empty(fleet.Riot.OrderCommands);
    }

    /// <summary>同上，换成「A 已预占、订单意图已写、RIoT 单还没建」那一刻（A 被出发前安全门挡着）：C 同样抢不走。</summary>
    [Fact]
    public async Task AReservationIsNotTakenWhileItsOrderHasNotBeenCreatedYet()
    {
        await using FleetFixture fleet = await FleetAsync(vehicles: 3);
        fleet.Riot.BatteryByVehicle[KeyA] = 20;
        await RoundAsync(fleet);
        (string CycleId, string JourneyId, string UpperId) before = await CommitmentOfAsync(fleet, KeyA);
        await fleet.ReplaceSafetySnapshotAsync(AgvA, unlockedSlot: 3);

        fleet.Riot.BatteryByVehicle[KeyC] = 5;
        for (int round = 0; round < 3; round++)
        {
            await RoundAsync(fleet);
        }

        Assert.Empty(fleet.Riot.Creates);
        Assert.Equal(before, await CommitmentOfAsync(fleet, KeyA));
        Assert.Equal((KeyA, StationExclusivityStates.Reserved), await HolderAsync(fleet, Near.StationId));
        Assert.Contains(
            $"{Near.StationId}={ChargingAllocationReasons.ChargerReservedOrOccupied}",
            fleet.ChargingBoard.Verdicts[AgvC].Detail, StringComparison.Ordinal);
        await AssertNothingOfACommitmentAsync(fleet, KeyC);
    }

    // ---- 共用的「能不能接新用途」判定 -----------------------------------------------------------------------------------

    public static TheoryData<string, string> VehiclesThatMayNotTakeANewPurpose => new()
    {
        { "fault-suspected", VehicleFaultBlockCriterion.SuspectedReason },
        { "no-approved-policy", DispatchReasonCodes.ChargingPolicyNotApproved },
        { "policy-entry-not-above-rescue-line", DispatchReasonCodes.ChargingPolicyEntryNotAboveRescueLine },
        { "session-not-ready", "ONBOARD_FACTS_NOT_READY" },
        { "departure-unsafe", "ONBOARD_DEPARTURE_UNSAFE" },
        { "moving", "RIOT_VEHICLE_NOT_STOPPED" },
        { "riot-order-on-it", "RIOT_VEHICLE_ORDER_OCCUPIED" },
        { "charging-eligibility-held", ChargingAllocationReasons.VehicleEligibilityHeld },
        { "position-unknown", ChargingAllocationReasons.VehiclePositionUnknown },
    };

    /// <summary>
    /// 充电分配与派车、空闲返回共用同一份「这辆车此刻能不能承接新用途」：故障阻断、没有已批准的策略、策略的强制充电线不高于救命线
    /// （整版不可用，充电也一样）、会话未就绪、车载端说不能出发、车在动、RIoT 上有它的单——各答各自的码，不分配、也不置人工充电等待。
    /// 车辆充电资格暂停与位置未知是充电自己的两格。
    /// </summary>
    [Theory]
    [MemberData(nameof(VehiclesThatMayNotTakeANewPurpose))]
    public async Task AVehicleThatMayNotTakeANewPurposeIsNotAllocatedAndNamesWhy(string why, string expected)
    {
        await using FleetFixture fleet = await FleetAsync();
        fleet.Riot.BatteryByVehicle[KeyA] = 12;
        switch (why)
        {
            case "fault-suspected":
                await new VehicleFaultStore(fleet.Context).RecordLevelAsync(
                    AgvA, VehicleFaultLevel.SuspectedBlocked, "COMMS_LOST", false, fleet.Clock.GetUtcNowWithoutTick(), Token);
                break;
            case "no-approved-policy":
                fleet.ChargingPolicy = TestChargingPolicies.None;
                await fleet.RecreateEngineAsync();
                break;
            case "policy-entry-not-above-rescue-line":
                fleet.ChargingPolicy = TestChargingPolicies.AllApprovedAt(15);
                await fleet.RecreateEngineAsync();
                break;
            case "session-not-ready":
                await fleet.DropSessionAsync(AgvA);
                break;
            case "departure-unsafe":
                await fleet.MakeDepartureUnsafeAsync(AgvA);
                break;
            case "moving":
                fleet.Riot.VehicleOverrides[KeyA] = seen => seen with { Speed = 0.4 };
                break;
            case "riot-order-on-it":
                fleet.Riot.VehicleOverrides[KeyA] = seen => seen with { LockStatus = 1, OrderTaskId = "RIOT-TASK-1" };
                break;
            case "charging-eligibility-held":
                await new ChargingHoldStore(fleet.Context).RecordVehicleHoldAsync(
                    new VehicleChargingEligibilityHold(
                        "vehicle-hold-1", "vehicle-hold-key-1", KeyA, null, VehicleChargingEligibilityHoldReasons.InterruptionConfirmed,
                        fleet.Clock.GetUtcNowWithoutTick(), null),
                    Token);
                break;
            case "position-unknown":
                fleet.Riot.VehicleOverrides[KeyA] = seen => seen with { CurrentStationId = null };
                break;
        }
        fleet.Context.ChangeTracker.Clear();

        await RoundAsync(fleet);
        await RoundAsync(fleet);

        Assert.Equal(expected, fleet.ChargingBoard.Verdicts[AgvA].Reason);
        await AssertNothingOfACommitmentAsync(fleet, KeyA);
        Assert.Null(await StationAsync(fleet, Near.StationId));
        Assert.Empty(await fleet.Context.Set<ManualChargingHoldRow>().AsNoTracking().ToArrayAsync(Token));
        Assert.Empty(fleet.Riot.Creates);
    }

    /// <summary>
    /// 一轮派车只认一份策略版本：分配用的是这一轮为这辆车读定的那一份（与搬运判它的是同一份），不在同一轮里再解析一次。解析器第二次被问
    /// 会答另一版（强制充电线 10，30% 的车在那一版下不需要充电）——车仍按读定的那一版被分配，周期与旅程冻结的都是它。
    /// </summary>
    [Fact]
    public async Task TheAllocationJudgesUnderTheVersionTheRoundReadAndFreezesThatOne()
    {
        await using FleetFixture fleet = await FleetAsync();
        FlippingResolver resolver = new(
            TestChargingPolicies.Effective(TestChargingPolicies.ContentAt(40), version: 1),
            TestChargingPolicies.Effective(TestChargingPolicies.ContentAt(20), version: 2));
        fleet.ChargingPolicy = resolver;
        await fleet.RecreateEngineAsync();
        fleet.Riot.BatteryByVehicle[KeyA] = 30;

        await fleet.HearFromEveryVehicleAsync();
        resolver.Reset();
        await fleet.RunRoundAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(1, resolver.Resolved);
        ChargingCycleRow cycle = await fleet.Context.Set<ChargingCycleRow>().AsNoTracking().SingleAsync(Token);
        Assert.Equal(1, cycle.ChargingPolicyVersion);
        Assert.Equal(1, (await fleet.Context.JourneyRuntimes.AsNoTracking().SingleAsync(Token)).ChargingPolicyVersion);
    }

    // ---- 名册为空：退化到人工充电等待（REQ-0171，规格 8.6）------------------------------------------------------------

    /// <summary>
    /// 名册为空（从没导入过，或当前版本置空）：需要充电的车进入服务端持有的人工充电等待，原因记「名册为空」；告警一次，之后每一轮不再重复；
    /// 车收到一张 <c>manualChargingHold=true</c> 的业务状态，也只有一张。不分配、不建单、不动车。
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WithAnEmptyRosterAVehicleThatNeedsChargingIsHeldForManualChargingWarnedOnceAndTold(bool anEmptyVersionWasImported)
    {
        await using FleetFixture fleet = await FleetAsync(roster: anEmptyVersionWasImported, chargers: []);
        fleet.Riot.BatteryByVehicle[KeyA] = 20;

        for (int round = 0; round < 4; round++)
        {
            await RoundAsync(fleet);
        }

        ManualChargingHoldRow hold = await fleet.Context.Set<ManualChargingHoldRow>().AsNoTracking().SingleAsync(Token);
        Assert.Equal((KeyA, ManualChargingHoldReasons.RosterEmpty), (hold.VehicleKey, hold.Reason));
        Assert.NotNull(hold.WarnedAt);
        var warning = Assert.Single(fleet.ChargingLog.Entries, entry => entry.EventId.Id == 2242);
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Warning, warning.Level);
        Assert.Contains(AgvA, warning.Message, StringComparison.Ordinal);
        JsonElement state = Assert.Single(await PayloadsAsync(fleet, AgvA, "VehicleBusinessStateSnapshot"));
        Assert.True(state.GetProperty("manualChargingHold").GetBoolean());
        Assert.Equal(JsonValueKind.Null, state.GetProperty("activePurpose").ValueKind);
        Assert.Equal(
            (ChargingCycleWireStates.NotCharging, BatteryStates.MandatoryCharge),
            (state.GetProperty("chargingCycleState").GetString(), state.GetProperty("batteryState").GetString()));
        Assert.Contains(fleet.Peer.Delivered, line => line.AgvId == AgvA && line.MessageType == "VehicleBusinessStateSnapshot");
        await AssertNothingOfACommitmentAsync(fleet, KeyA);
        Assert.Empty(fleet.Riot.Creates);
        Assert.Equal(ChargingAllocationReasons.ManualChargingHoldActive, fleet.ChargingBoard.Verdicts[AgvA].Reason);
    }

    /// <summary>
    /// 名册在两轮之间由有变空：已取得预占、在途的周期按自己记下的名册版本继续——不撤单、不释放预占、没有任何取消命令发出；还在队里、没有预占的车
    /// 进入人工充电等待。
    /// </summary>
    [Fact]
    public async Task WhenTheRosterIsEmptiedBetweenRoundsCommittedCyclesGoOnAndQueuedVehiclesAreHeld()
    {
        await using FleetFixture fleet = await FleetAsync(vehicles: 2);
        fleet.Riot.BatteryByVehicle[KeyA] = 20;
        fleet.Riot.BatteryByVehicle[KeyB] = 30;
        await RoundAsync(fleet);
        await RoundAsync(fleet);
        (string CycleId, string JourneyId, string UpperId) before = await CommitmentOfAsync(fleet, KeyA);
        Assert.Single(fleet.Riot.Creates);
        Assert.Empty(await fleet.Context.Set<ManualChargingHoldRow>().AsNoTracking().ToArrayAsync(Token));

        await fleet.WriteChargerRosterAsync();
        for (int round = 0; round < 3; round++)
        {
            await RoundAsync(fleet);
        }

        Assert.Equal(before, await CommitmentOfAsync(fleet, KeyA));
        Assert.Equal((KeyA, StationExclusivityStates.Reserved), await HolderAsync(fleet, Near.StationId));
        ChargingCycleRow cycle = await fleet.Context.Set<ChargingCycleRow>().AsNoTracking().SingleAsync(Token);
        Assert.Equal((ChargingCyclePhases.Active, 1L), (cycle.Phase, cycle.ChargerRosterVersion));
        Assert.Single(fleet.Riot.Creates);
        Assert.Empty(fleet.Riot.OrderCommands);
        Assert.Equal(
            (KeyB, ManualChargingHoldReasons.RosterEmpty),
            await fleet.Context.Set<ManualChargingHoldRow>().AsNoTracking().Select(row => new ValueTuple<string, string>(row.VehicleKey, row.Reason))
                .SingleAsync(Token));
    }

    /// <summary>
    /// 名册重新启用之后，处于人工充电等待的车不自动回到自动充电：要先经「充电后返回服务」（用户 2026-09-29 定）。返回服务之前名册非空也不分配；
    /// 之后它按正常候选链进队并取得预占，车收到一张撤下等待的业务状态。
    /// </summary>
    [Fact]
    public async Task AfterTheRosterIsBackAHeldVehicleIsAllocatedOnlyOnceItHasReturnedToService()
    {
        await using FleetFixture fleet = await FleetAsync(roster: false);
        fleet.Riot.BatteryByVehicle[KeyA] = 20;
        await RoundAsync(fleet);
        Assert.Single(await fleet.Context.Set<ManualChargingHoldRow>().AsNoTracking().ToArrayAsync(Token));

        await fleet.WriteChargerRosterAsync(Near);
        for (int round = 0; round < 3; round++)
        {
            await RoundAsync(fleet);
        }
        Assert.Null(await ClaimOfAsync(fleet, KeyA));
        Assert.Null(await StationAsync(fleet, Near.StationId));
        Assert.Equal(ChargingAllocationReasons.ManualChargingHoldActive, fleet.ChargingBoard.Verdicts[AgvA].Reason);

        ManualChargingReturnToServiceDecision decision = await ReturnToServiceAsync(fleet, AgvA, KeyA, "00000000-0000-4000-8000-0000000000d1");
        Assert.Equal(ManualChargingReturnToServiceDecision.ReturnedToEligibilityEvaluation, decision.Outcome);
        await RoundAsync(fleet);

        Assert.Empty(await fleet.Context.Set<ManualChargingHoldRow>().AsNoTracking().ToArrayAsync(Token));
        Assert.Equal(VehiclePurposes.Charging, (await ClaimOfAsync(fleet, KeyA))!.Value.Purpose);
        Assert.Equal(KeyA, (await StationAsync(fleet, Near.StationId))!.VehicleKey);
        JsonElement[] states = await PayloadsAsync(fleet, AgvA, "VehicleBusinessStateSnapshot");
        Assert.Equal([true, false], [.. states.Take(2).Select(state => state.GetProperty("manualChargingHold").GetBoolean())]);
        // One stream per vehicle: the hold's two snapshots and the charging journey's that follows never share a revision,
        // and the journey's come after them (the vehicle refuses a revision at or below the one it has adopted).
        await RoundAsync(fleet);
        long[] revisions =
        [
            .. (await PayloadsAsync(fleet, AgvA, "VehicleBusinessStateSnapshot"))
                .Select(state => state.GetProperty("vehicleBusinessStateRevision").GetInt64()),
        ];
        Assert.True(revisions.Length >= 4, string.Join(',', revisions));
        Assert.Equal(revisions.Distinct().Order(), revisions);
        Assert.Equal(
            [null, null, VehicleActivePurposes.Charging, VehicleActivePurposes.Charging],
            (await PayloadsAsync(fleet, AgvA, "VehicleBusinessStateSnapshot")).Take(4)
                .Select(state => state.GetProperty("activePurpose").GetString()));
    }

    /// <summary>
    /// 置上等待那一张车从没收到（它当时不在线），解除那一张排进去时把它退役，不再补发：补发按先后发没确认的行，而车载端把低于已采纳修订号的
    /// 快照当成回退、当场拆会话。车重新连上之后只收到现行的那一张（<c>manualChargingHold=false</c>）。
    /// </summary>
    [Fact]
    public async Task AHoldSnapshotNeverAcknowledgedIsRetiredWhenTheSnapshotLiftingItReplacesIt()
    {
        await using FleetFixture fleet = await FleetAsync(roster: false);
        fleet.Riot.BatteryByVehicle[KeyA] = 20;
        fleet.Peer.Unavailable = AgvA;
        await RoundAsync(fleet);
        ManualChargingHoldRow hold = await fleet.Context.Set<ManualChargingHoldRow>().AsNoTracking().SingleAsync(Token);
        string placed = ChargingAllocator.PlacedMessageId(hold.HoldId);
        string released = ChargingAllocator.ReleasedMessageId(hold.HoldId);

        // Charged by hand, then returned to service -- all while the vehicle's link is still down.
        fleet.Riot.BatteryByVehicle[KeyA] = 90;
        await ReturnToServiceAsync(fleet, AgvA, KeyA, "00000000-0000-4000-8000-0000000000d2");
        await RoundAsync(fleet);
        await RoundAsync(fleet);

        ProtocolOutboxRow[] rows = await fleet.Context.ProtocolOutbox.AsNoTracking()
            .Where(row => row.MessageId == placed || row.MessageId == released).ToArrayAsync(Token);
        Assert.NotNull(rows.Single(row => row.MessageId == placed).FencedAt);
        Assert.Null(rows.Single(row => row.MessageId == released).FencedAt);
        Assert.DoesNotContain(fleet.Peer.Delivered, line => line.MessageId == placed || line.MessageId == released);

        fleet.Peer.Unavailable = null;
        await RoundAsync(fleet);

        Assert.Contains(fleet.Peer.Delivered, line => line.MessageId == released);
        Assert.DoesNotContain(fleet.Peer.Delivered, line => line.MessageId == placed);
        Assert.Equal(
            [true, false],
            (await PayloadsAsync(fleet, AgvA, "VehicleBusinessStateSnapshot"))
                .Select(state => state.GetProperty("manualChargingHold").GetBoolean()));
    }

    /// <summary>
    /// 搬运中的车（持有 <c>TRANSPORT</c> 用途）不置人工充电等待（车载端录入门只看 <c>manualChargingHold</c>，hmi#220；<c>REQ-0281</c>）：
    /// 它在途中掉到强制充电线以下、名册又是空的，这一趟照常做——没有等待行，发给它的每一张业务状态都是 <c>manualChargingHold=false</c>。
    /// </summary>
    [Fact]
    public async Task AVehicleCarryingATransportIsNeverPutOnManualHold()
    {
        await using FleetFixture fleet = await FleetAsync(roster: false);
        fleet.Catalog.Set([FleetFixture.Demand(0, "N1-1", 0)]);
        await RoundAsync(fleet);
        JourneyRuntimeRow transport = await fleet.Context.JourneyRuntimes.AsNoTracking().SingleAsync(Token);
        Assert.NotNull(transport.DemandId);

        fleet.Riot.BatteryByVehicle[KeyA] = 20;
        fleet.Riot.CompleteOrder(transport.PickupUpperId);
        fleet.Riot.VehicleOverrides[KeyA] = seen => seen with { CurrentStationId = 12 };
        for (int round = 0; round < 3; round++)
        {
            await RoundAsync(fleet);
        }

        Assert.Empty(await fleet.Context.Set<ManualChargingHoldRow>().AsNoTracking().ToArrayAsync(Token));
        Assert.Empty(await fleet.Context.Set<ManualChargingHoldRecordRow>().AsNoTracking().ToArrayAsync(Token));
        JsonElement[] states = await PayloadsAsync(fleet, AgvA, "VehicleBusinessStateSnapshot");
        Assert.NotEmpty(states);
        Assert.All(states, state => Assert.False(state.GetProperty("manualChargingHold").GetBoolean()));
        Assert.Equal(VehiclePurposes.Transport, (await ClaimOfAsync(fleet, KeyA))!.Value.Purpose);
    }

    /// <summary>
    /// 人工充电等待中的车不接搬运、不做空闲返回、不被移动——哪怕有人已经给它充过电、电量回到线上：电量回升本身不恢复资格，出口只有
    /// 「充电后返回服务」。搬运那条链答「这辆车在人工充电等待中」，空闲返回答同一个码。
    /// </summary>
    [Fact]
    public async Task AHeldVehicleTakesNoTransportAndNoIdleReturnEvenOnceItsBatteryIsBack()
    {
        await using FleetFixture fleet = await FleetAsync(roster: false);
        fleet.Riot.BatteryByVehicle[KeyA] = 20;
        await RoundAsync(fleet);
        Assert.Single(await fleet.Context.Set<ManualChargingHoldRow>().AsNoTracking().ToArrayAsync(Token));

        fleet.Riot.BatteryByVehicle[KeyA] = 90;
        await fleet.EnableIdleReturnAsync(WaitingPoint);
        AcceptedDemandSnapshot demand = FleetFixture.Demand(0, "N1-1", 0);
        fleet.Catalog.Set([demand]);
        for (int round = 0; round < 3; round++)
        {
            await RoundAsync(fleet);
        }

        Assert.Null(await ClaimOfAsync(fleet, KeyA));
        Assert.Empty(await fleet.Context.JourneyRuntimes.AsNoTracking().ToArrayAsync(Token));
        Assert.Empty(fleet.Riot.Creates);
        Assert.Equal(
            DispatchReasonCodes.VehicleInManualChargingHold,
            (await fleet.Context.JourneyBacklog.AsNoTracking().SingleAsync(row => row.DemandId == demand.DemandId, Token)).ReasonCode);
        Assert.Equal(DispatchReasonCodes.VehicleInManualChargingHold, fleet.IdleReturnBoard.Reasons[AgvA]);
    }

    /// <summary>待充电、在队里等桩的车不被空闲返回选中：它低于强制充电线，空闲返回答「低于强制充电线」，不承诺去等待点。</summary>
    [Fact]
    public async Task AVehicleQueuedForAChargerIsNotSentToAWaitingPoint()
    {
        await using FleetFixture fleet = await FleetAsync(vehicles: 2);
        await fleet.EnableIdleReturnAsync(WaitingPoint);
        fleet.Riot.BatteryByVehicle[KeyA] = 20;
        fleet.Riot.BatteryByVehicle[KeyB] = 30;

        for (int round = 0; round < 3; round++)
        {
            await RoundAsync(fleet);
        }

        Assert.Equal(VehiclePurposes.Charging, (await ClaimOfAsync(fleet, KeyA))!.Value.Purpose);
        Assert.Null(await ClaimOfAsync(fleet, KeyB));
        Assert.Null(await StationAsync(fleet, WaitingPoint.StationId));
    }

    // ---- 登记 ------------------------------------------------------------------------------------------------------------

    /// <summary>宿主把跨轮状态（每辆车上一次的分配结论）注册成单例，并把分配器、占用读取与搬运链上的那条判据注册齐。</summary>
    [Fact]
    public void TheHostRegistersTheAllocationBoardAsASingletonAndTheAllocatorAsRequired()
    {
        Microsoft.Extensions.DependencyInjection.ServiceCollection services = new();
        ControlServer.Host.Composition.ChargingModule.AddCharging(services);
        services.AddDispatchAdmission();

        Assert.Equal(
            Microsoft.Extensions.DependencyInjection.ServiceLifetime.Singleton,
            Assert.Single(services, descriptor => descriptor.ServiceType == typeof(ChargingAllocationBoard)).Lifetime);
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(ChargingAllocator));
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(ChargerOccupancyReader));
        Assert.Single(services, descriptor => descriptor.ImplementationType == typeof(ChargingStandingCriterion));
        Assert.Contains(
            typeof(DispatchRoundRunner).GetConstructors().Single().GetParameters(),
            parameter => parameter.ParameterType == typeof(ChargingAllocator) && !parameter.HasDefaultValue);
    }

    // ---- 夹具 ------------------------------------------------------------------------------------------------------------

    internal static RouteGraphEdgeFact[] Edges() =>
    [
        new(1, 1, 2, 10000, 0, 0, 10000, 0, 1, false),
        new(2, 2, 3, 10000, 10000, 0, 20000, 0, 1, false),
        new(3, 3, 4, 10000, 20000, 0, 30000, 0, 1, false),
        new(4, 4, 5, 10000, 30000, 0, 40000, 0, 1, false),
        new(5, 1, 6, 5000, 0, 0, 0, 5000, 1, false),
        new(6, 6, 1, 5000, 0, 5000, 0, 0, 1, false),
        new(7, 1, 7, 8000, 0, 0, 0, -8000, 1, false),
        new(8, 7, 1, 8000, 0, -8000, 0, 0, 1, false),
        new(9, 2, 1, 10000, 10000, 0, 0, 0, 1, false),
        new(10, 3, 2, 10000, 20000, 0, 10000, 0, 1, false),
        new(11, 2, 6, 11000, 10000, 0, 0, 5000, 1, false),
        new(12, 2, 7, 13000, 10000, 0, 0, -8000, 1, false),
        new(13, 1, 8, 9000, 0, 0, -9000, 0, 1, false),
        new(14, 8, 1, 9000, -9000, 0, 0, 0, 1, false),
        new(15, 6, 2, 11000, 0, 5000, 10000, 0, 1, false),
    ];

    internal static RouteGraphStationFact[] Stations() =>
    [
        new(300, "等待点", 1, 0, 0, 1, 0),
        new(12, "N1-1", 1, 10000, 0, 2, 0),
        new(13, "N1-2_N1-3", 2, 20000, 0, 3, 0),
        new(210, "关卡", 3, 30000, 0, 4, 0),
        new(Near.StationId, Near.StationName, 5, 0, 5000, 6, 0),
        new(Far.StationId, Far.StationName, 7, 0, -8000, 7, 0),
        new(WaitingPoint.StationId, WaitingPoint.StationName, 8, -9000, 0, 8, 0),
    ];

    /// <summary>
    /// 一队车（默认一辆），路网换成带两个充电桩的那张，登记给定的名册（默认只有 211；<paramref name="roster"/> 为假即从没导入过名册），
    /// 目录清空。电量默认 80，要充电的车由用例自己压低。
    /// </summary>
    internal static async Task<FleetFixture> FleetAsync(
        int vehicles = 1,
        ChargerRosterEntry[]? chargers = null,
        bool roster = true,
        DbCommandInterceptor? commands = null)
    {
        FleetFixture fleet = await FleetFixture.CreateAsync(
            configure: options => options.Fleet = options.Fleet[..vehicles], withRouteGraph: true, commands: commands);
        await fleet.ReplaceRouteGraphAsync(Edges(), Stations());
        if (roster)
        {
            await fleet.WriteChargerRosterAsync(chargers ?? [Near]);
        }
        fleet.Catalog.Set([]);
        return fleet;
    }

    /// <summary>一轮，时钟先走一秒；每辆车先说一句话（心跳），会话才算活着。</summary>
    internal static async Task RoundAsync(FleetFixture fleet)
    {
        await fleet.HearFromEveryVehicleAsync();
        await fleet.RunRoundAsync(TimeSpan.FromSeconds(1));
    }

    internal static async Task<(string Purpose, string JourneyId)?> ClaimOfAsync(FleetFixture fleet, string vehicleKey)
    {
        VehiclePurposeClaimRow? claim = await fleet.Context.Set<VehiclePurposeClaimRow>().AsNoTracking()
            .SingleOrDefaultAsync(row => row.VehicleKey == vehicleKey, Token);
        return claim is null ? null : (claim.Purpose, claim.JourneyId);
    }

    internal static Task<StationExclusivityRow?> StationAsync(FleetFixture fleet, int stationId) =>
        fleet.Context.Set<StationExclusivityRow>().AsNoTracking()
            .SingleOrDefaultAsync(row => row.MapId == Map && row.StationId == stationId, Token);

    internal static async Task<(string VehicleKey, string State)?> HolderAsync(FleetFixture fleet, int stationId) =>
        await StationAsync(fleet, stationId) is { } row ? (row.VehicleKey, row.State) : null;

    /// <summary>这辆车未结束的充电承诺的三个身份：周期、旅程、RIoT 单号。预占后不可抢占的断言比的就是它们逐字不变。</summary>
    internal static async Task<(string CycleId, string JourneyId, string UpperId)> CommitmentOfAsync(FleetFixture fleet, string vehicleKey)
    {
        ChargingCycleRow cycle = await fleet.Context.Set<ChargingCycleRow>().AsNoTracking()
            .SingleAsync(row => row.VehicleKey == vehicleKey && row.Phase != ChargingCyclePhases.Ended, Token);
        VehiclePurposeClaimRow claim = await fleet.Context.Set<VehiclePurposeClaimRow>().AsNoTracking()
            .SingleAsync(row => row.VehicleKey == vehicleKey, Token);
        Assert.Equal((VehiclePurposes.Charging, cycle.JourneyId), (claim.Purpose, claim.JourneyId));
        JourneyStopRow stop = await fleet.Context.Set<JourneyStopRow>().AsNoTracking()
            .SingleAsync(row => row.JourneyId == cycle.JourneyId, Token);
        OrderIntentRow intent = await fleet.Context.OrderIntents.AsNoTracking().SingleAsync(row => row.UpperId == stop.UpperId, Token);
        Assert.Equal(cycle.StationId, intent.DestinationStationId);
        return (cycle.CycleId, cycle.JourneyId, intent.UpperId);
    }

    /// <summary>这辆车没有留下充电承诺的任何一半：没有用途占有、没有未结束的周期、没有未完成的旅程与它的停靠、没有订单意图。</summary>
    internal static async Task AssertNothingOfACommitmentAsync(FleetFixture fleet, string vehicleKey)
    {
        Assert.Null(await ClaimOfAsync(fleet, vehicleKey));
        Assert.Empty(await fleet.Context.Set<ChargingCycleRow>().AsNoTracking()
            .Where(row => row.VehicleKey == vehicleKey && row.Phase != ChargingCyclePhases.Ended).ToArrayAsync(Token));
        string[] journeys = await fleet.Context.JourneyRuntimes.AsNoTracking()
            .Where(row => row.VehicleKey == vehicleKey && row.Stage != JourneyRuntimeStage.Completed)
            .Select(row => row.JourneyId).ToArrayAsync(Token);
        Assert.Empty(journeys);
        Assert.Empty(await fleet.Context.OrderIntents.AsNoTracking()
            .Where(row => row.VehicleKey == vehicleKey && row.Purpose == ChargingJourneyShape.IntentPurpose &&
                          row.Status == "PENDING_RECONCILIATION")
            .ToArrayAsync(Token));
        Assert.Empty(await fleet.Context.Set<StationExclusivityRow>().AsNoTracking()
            .Where(row => row.VehicleKey == vehicleKey).ToArrayAsync(Token));
    }

    /// <summary>这辆车一个已经结束的充电周期（上一次充电在这个桩上以 <paramref name="endReason"/> 结束）。</summary>
    internal static async Task EndedCycleAsync(
        FleetFixture fleet, string vehicleKey, int stationId, string endReason, DateTimeOffset endedAt)
    {
        string journeyId = ChargingIdentity.JourneyIdFor(vehicleKey, endedAt.AddMinutes(-1));
        fleet.Context.Set<ChargingCycleRow>().Add(new ChargingCycleRow
        {
            CycleId = ChargingIdentity.CycleIdFor(journeyId),
            VehicleKey = vehicleKey,
            JourneyId = journeyId,
            MapId = Map,
            StationId = stationId,
            ChargerRosterVersion = 1,
            ChargingPolicyVersion = 1,
            WireState = ChargingCycleWireStates.EnRoute,
            Phase = ChargingCyclePhases.Ended,
            AllocatedAt = endedAt.AddMinutes(-1),
            EndedAt = endedAt,
            EndReason = endReason,
            Version = 3,
        });
        await fleet.Context.SaveChangesAsync(Token);
        fleet.Context.ChangeTracker.Clear();
    }

    /// <summary>绕开分配器的资格与选桩，直接在承诺那一刻调存储：就是分配器最后那一步。</summary>
    internal static async Task<ChargingCycleStartOutcome> CommitDirectlyAsync(
        FleetFixture fleet, int vehicleIndex, ChargerRosterEntry charger, DateTimeOffset at)
    {
        ControlServer.Host.Runtime.Fleet.FleetVehicle vehicle = new(
            FleetFixture.AgvIds[vehicleIndex], FleetFixture.VehicleKeys[vehicleIndex], 1);
        string journeyId = ChargingIdentity.JourneyIdFor(vehicle.VehicleKey, at);
        (JourneyRuntimeRow runtime, JourneyStopRow stop, OrderIntent intent) = ChargingJourneyShape.Build(
            journeyId, vehicle, charger, 1, BatteryStates.MandatoryCharge, fleet.Options, at);
        return await ChargingCommitment.TryCommitAsync(
            fleet.Context,
            new ChargingCycleStore(fleet.Context),
            new ChargingCycleStart(
                ChargingIdentity.CycleIdFor(journeyId), vehicle.VehicleKey, journeyId, charger.MapId, charger.StationId, 1, 1, at),
            runtime,
            stop,
            intent,
            Token);
    }

    /// <summary>一次「充电后返回服务」，由维护管理员在车上发起，经存储判定（与车载端入站处理器调的是同一个方法）。</summary>
    internal static async Task<ManualChargingReturnToServiceDecision> ReturnToServiceAsync(
        FleetFixture fleet, string agvId, string vehicleKey, string requestId, string role = "MAINTENANCE_ADMINISTRATOR")
    {
        fleet.Context.ChangeTracker.Clear();
        long generation = await fleet.Context.SessionRecoveries.AsNoTracking()
            .Where(row => row.AgvId == agvId).Select(row => row.SessionGeneration).SingleAsync(Token);
        ManualChargingReturnToServiceDecision decision = await new WireToGateStore(fleet.Context)
            .DecideManualChargingReturnToServiceAsync(
                new ManualChargingReturnToServiceRequest(
                    requestId, agvId, generation, Guid.NewGuid().ToString("D"), new string('a', 64), "operator-1", role,
                    "charged by hand", 85, vehicleKey),
                Token);
        fleet.Context.ChangeTracker.Clear();
        return decision;
    }

    /// <summary>这辆车发件箱里这一类快照的载荷，按修订号排。</summary>
    internal static async Task<JsonElement[]> PayloadsAsync(FleetFixture fleet, string agvId, string messageType)
    {
        string[] rows = await fleet.Context.ProtocolOutbox.AsNoTracking()
            .Where(row => row.MessageType == messageType)
            .Select(row => row.PayloadJson)
            .ToArrayAsync(Token);
        return
        [
            .. rows.Select(json => JsonDocument.Parse(json).RootElement)
                .Where(root => root.GetProperty("agvId").GetString() == agvId)
                .Select(root => root.GetProperty("payload").Clone())
                .OrderBy(payload => payload.TryGetProperty("planRevision", out JsonElement plan) ? plan.GetInt64()
                    : payload.TryGetProperty("vehicleBusinessStateRevision", out JsonElement state) ? state.GetInt64()
                    : 0)
        ];
    }

    /// <summary>第一次被问答一版、之后答另一版的解析器：同一轮里再问一次就会拿到不同的策略。</summary>
    private sealed class FlippingResolver(EffectiveChargingPolicy first, EffectiveChargingPolicy later) : IChargingPolicyResolver
    {
        public int Resolved { get; private set; }

        public void Reset() => Resolved = 0;

        public Task<VehicleChargingPolicyDecision> ResolveForNewDecisionAsync(string vehicleKey, CancellationToken cancellationToken)
        {
            Resolved++;
            return Task.FromResult(new VehicleChargingPolicyDecision(
                vehicleKey, ChargingPolicyCommissioningReasons.Effective, Resolved == 1 ? first : later, null));
        }

        public Task<ChargingPolicyVersion> ReadFrozenAsync(long version, CancellationToken cancellationToken) =>
            Task.FromResult(version == first.Policy.Version ? first.Policy : later.Policy);
    }

    /// <summary>让下一条含 <paramref name="fragment"/> 的写命令失败一次（<see cref="Armed"/> 之后），像进程在那次保存里崩掉：整次保存回滚。</summary>
    internal sealed class FailingInsert(string fragment) : DbCommandInterceptor
    {
        public bool Armed { get; set; }

        public int Fired { get; private set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Trip(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Trip(command);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void Trip(DbCommand command)
        {
            if (Armed && command.CommandText.Contains(fragment, StringComparison.Ordinal))
            {
                Armed = false;
                Fired++;
                throw new InvalidOperationException($"Injected crash at: {fragment}");
            }
        }
    }
}
