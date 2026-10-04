using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Charging;
using ControlServer.Host.Runtime.Commands;
using ControlServer.Host.Runtime.Faults;
using ControlServer.Host.Runtime.IdleReturn;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using static ControlServer.Tests.ChargingAllocationTests;
using static ControlServer.Tests.ChargingExecutionTests;
using static ControlServer.Tests.ChargingUnableToChargeTests;
using FleetFixture = ControlServer.Tests.MultiVehicleExecutionTests.FleetFixture;

namespace ControlServer.Tests;

/// <summary>
/// 清桩开往等待点（批次9-11，control-server#409）：清桩中的车在旧充电单确认终结之后，与空闲返回共用同一个等待点集合原子预占一个点并开过去，
/// 到点即完成清桩并释放桩（<c>REQ-0178</c>、<c>REQ-0179</c> 的系统证明）；无合格点原地排队告警、不猜别的站；开关
/// <c>JourneyRuntime:ClearanceToWaitingPointEnabled</c> 默认关，关着与 control-server#406 逐字相同。
/// </summary>
/// <remarks>
/// 夹具同 <see cref="ChargingUnableToChargeTests"/>：A 低电，承诺 211、建单、确认、在桩上充不上并形成「已确认充不上」（清桩中）。等待点 214 在路网节点 8，
/// 300 在节点 1（车队默认停的地方），从 211（节点 6）都可达；212 不在登记里。用例自己把旧单在 RIoT 里结束（取消开关默认关，这正是现场的出口）。
/// </remarks>
[Trait("IntegrationSlice", "FP-IS-13")]
public sealed partial class ChargingClearanceToWaitingPointTests
{
    private const int Map = 25;

    private static readonly WaitingPointEntry Point214 = new(Map, 214, "等待点214", true, []);
    private static readonly WaitingPointEntry Point300 = new(Map, 300, "等待点", true, []);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string AgvA => FleetFixture.AgvIds[0];
    private static string KeyA => FleetFixture.VehicleKeys[0];
    private static string KeyB => FleetFixture.VehicleKeys[1];

    // ---- 开关 ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// 开关关着（默认）：旧单结束之后车照旧原地等人工清桩——十轮里没有第二张单、没有任何等待点预占、没有清桩移动的停靠，码是充不上的等人码；
    /// 人工清桩照常完成并收尾。开关打开的那一种是下一条用例。
    /// </summary>
    [Fact]
    public async Task SwitchedOffTheClearingVehicleStaysWhereItIsExactlyAsBefore()
    {
        await using FleetFixture fleet = await ClearanceFleetAsync(enabled: false);
        Assert.False(new JourneyRuntimeOptions().ClearanceToWaitingPointEnabled);
        JourneyRuntimeRow journey = await OldOrderEndedAsync(fleet);

        for (int round = 0; round < 10; round++)
        {
            await RoundAsync(fleet);
        }

        Assert.Single(fleet.Riot.Creates);
        Assert.Null(await StationAsync(fleet, Point214.StationId));
        Assert.Empty(await ClearanceStopsAsync(fleet, journey));
        Assert.Equal(ChargingExecutionReasons.UnableToChargeClearing, (await ChargingJourneyAsync(fleet, AgvA))!.BlockReasonCode);
        Assert.DoesNotContain(fleet.EngineLog.Entries, entry => entry.EventId.Id is >= 2300 and <= 2306);

        ChargingUnableToChargeTests.AtTheCharger(fleet);
        fleet.Riot.VehicleOverrides[KeyA] = seen => seen with { CurrentStationId = 300, BatteryState = "NO_CHARGE" };
        ManualStationClearanceConfirmation decision = await ManualClearance(fleet).DecideAsync(Request("00000000-0000-4000-8000-0000000c4090"), Token);
        Assert.Equal((FieldConfirmationDecision.Confirmed, true), (decision.Decision.Outcome, decision.StationReleased));
    }

    // ---- 开过去、到点即完成（REQ-0179 系统证明）--------------------------------------------------------------------------

    /// <summary>
    /// 开关打开：旧单结束之后的那一轮原子预占 214（停靠、意图、预占同一次保存，还没建单）；下一轮过安全门、建单（单段 move、目标 214、单号
    /// <c>…-CLR1</c>）、发途中快照（原桩腿 <c>COMPLETED</c>、等待点腿 <c>ACTIVE</c>、仍是 <c>CLEARING_MAINTENANCE</c>）。在路上的每一轮桩的独占都还在
    /// （另等第二个事实，不读一次就断言）；到点证据满足的那一轮，同一次保存里：清桩完成（证明 <c>ARRIVED_AT_WAITING_POINT</c>、等待点 214）、周期结束、
    /// 桩独占释放、用途放开、214 转占用、旅程收尾（计划留 <c>ARRIVED</c> 的等待点腿、撤下用途）。桩的暂停仍在，没有恢复记录。
    /// </summary>
    [Fact]
    public async Task TheClearingVehicleDrivesToAWaitingPointAndArrivingCompletesTheClearanceInOneSave()
    {
        await using FleetFixture fleet = await ClearanceFleetAsync();
        JourneyRuntimeRow journey = await OldOrderEndedAsync(fleet);

        await RoundAsync(fleet);

        JourneyStopRow move = Assert.Single(await ClearanceStopsAsync(fleet, journey));
        Assert.Equal((JourneyStopRoles.WaitingPoint, 214, ClearanceMoveShape.UpperIdFor(journey.JourneyId, 1)),
            (move.StopRole, move.StationRiotId, move.UpperId));
        StationExclusivityRow reserved = (await StationAsync(fleet, 214))!;
        Assert.Equal((KeyA, journey.JourneyId, StationExclusivityStates.Reserved), (reserved.VehicleKey, reserved.JourneyId, reserved.State));
        Assert.Single(fleet.Riot.Creates);
        Assert.Equal(ChargingExecutionReasons.ClearanceToWaitingPoint, (await ChargingJourneyAsync(fleet, AgvA))!.BlockReasonCode);
        Assert.Single(fleet.EngineLog.Entries, entry => entry.EventId.Id == 2300);

        await RoundAsync(fleet);

        Assert.Equal(2, fleet.Riot.Creates.Count);
        Assert.Equal((KeyA, move.UpperId, 214), fleet.Riot.Creates[^1]);
        Assert.Equal(OrderShapes.SingleMove, fleet.Riot.CreatedIntents[^1].OrderShape);
        JsonElement plan = (await PayloadsAsync(fleet, AgvA, "UpcomingStopPlanSnapshot"))[^1];
        string[] legs = [.. plan.GetProperty("legs").EnumerateArray()
            .Select(leg => leg.GetProperty("stopPurposeCategory").GetString() + ":" + leg.GetProperty("state").GetString())];
        Assert.Equal([JourneyPlanBuilder.ChargerStopPurpose + ":COMPLETED", JourneyPlanBuilder.WaitingPointStopPurpose + ":ACTIVE"], legs);
        JsonElement state = (await PayloadsAsync(fleet, AgvA, "VehicleBusinessStateSnapshot"))[^1];
        Assert.Equal(VehicleActivePurposes.ClearingMaintenance, state.GetProperty("activePurpose").GetString());

        // On the way: the charger stays this journey's every round until the arrival is proven.
        fleet.Riot.VehicleOverrides[KeyA] = seen => seen with { CurrentStationId = 300, Speed = 300, ProcState = "EXECUTING", OrderTaskId = "T" };
        for (int round = 0; round < 5; round++)
        {
            await RoundAsync(fleet);
            Assert.Equal((KeyA, StationExclusivityStates.Reserved), await HolderAsync(fleet, Near.StationId));
            Assert.Null((await ClearanceAsync(fleet)).CompletedAt);
        }
        // The order says SUCCESS but the vehicle is not proven standing on 214 yet: still nothing completes.
        fleet.Riot.CompleteOrder(move.UpperId);
        await RoundAsync(fleet);
        Assert.Equal((KeyA, StationExclusivityStates.Reserved), await HolderAsync(fleet, Near.StationId));
        Assert.Equal(ChargingExecutionReasons.ClearanceArrivalNotProven, (await ChargingJourneyAsync(fleet, AgvA))!.BlockReasonCode);

        StandOn(fleet, 214);
        await RoundAsync(fleet);

        StationClearanceRow clearance = await ClearanceAsync(fleet);
        Assert.NotNull(clearance.CompletedAt);
        Assert.Equal((StationClearanceProofs.ArrivedAtWaitingPoint, Map, 214, "CANCELLED"),
            (clearance.Proof, clearance.WaitingPointMapId, clearance.WaitingPointStationId, clearance.OldOrderDisposition));
        Assert.Null(clearance.ConfirmedBy);
        ChargingCycleRow cycle = await fleet.Context.Set<ChargingCycleRow>().AsNoTracking().SingleAsync(Token);
        Assert.Equal((ChargingCyclePhases.Ended, ChargingExecutionReasons.UnableToChargeClearedAtWaitingPoint), (cycle.Phase, cycle.EndReason));
        Assert.Null(await HolderAsync(fleet, Near.StationId));
        Assert.Single(await fleet.Context.Set<StationExclusivityRecordRow>().AsNoTracking()
            .Where(row => row.StationId == Near.StationId && row.ReleaseReason == ChargingExecutionReasons.ChargerReleasedOnClearanceAtWaitingPoint)
            .ToArrayAsync(Token));
        Assert.Equal((KeyA, StationExclusivityStates.Occupied), await HolderAsync(fleet, 214));
        Assert.Null(await ClaimOfAsync(fleet, KeyA));
        JourneyRuntimeRow closed = await fleet.Context.JourneyRuntimes.AsNoTracking().SingleAsync(row => row.JourneyId == journey.JourneyId, Token);
        Assert.Equal((JourneyRuntimeStage.Completed, ChargingExecutionReasons.UnableToChargeClearedAtWaitingPoint), (closed.Stage, closed.BlockReasonCode));
        // The charger's hold stays: only a recovery confirmation lifts it.
        Assert.Single(await HoldsAsync(fleet));
        Assert.Empty(await fleet.Context.Set<ChargingStationRecoveryRow>().AsNoTracking().ToArrayAsync(Token));
        JsonElement closingPlan = (await PayloadsAsync(fleet, AgvA, "UpcomingStopPlanSnapshot"))[^1];
        JsonElement arrived = Assert.Single(closingPlan.GetProperty("legs").EnumerateArray());
        Assert.Equal((JourneyPlanBuilder.WaitingPointStopPurpose, "ARRIVED"),
            (arrived.GetProperty("stopPurposeCategory").GetString(), arrived.GetProperty("state").GetString()));
        Assert.Equal(JsonValueKind.Null, (await PayloadsAsync(fleet, AgvA, "VehicleBusinessStateSnapshot"))[^1].GetProperty("activePurpose").ValueKind);
        Assert.Single(fleet.EngineLog.Entries, entry => entry.EventId.Id == 2305);
        Assert.Equal(2, fleet.Riot.Creates.Count);
        Assert.Empty(fleet.Riot.OrderCommands);
        Assert.Empty(fleet.Riot.EmergencyCommands);

        // REQ-0180: the vehicle is not held afterwards; its occupancy of 214 goes once it is seen elsewhere.
        fleet.Riot.VehicleOverrides[KeyA] = seen => seen with { CurrentStationId = 12, BatteryState = "NO_CHARGE" };
        await RoundAsync(fleet);
        Assert.Null(await StationAsync(fleet, 214));
    }

    // ---- 出发条件 --------------------------------------------------------------------------------------------------------

    public static TheoryData<string> DepartureNotAllowed =>
    [
        "old-order-still-hang",
        "old-order-result-unknown",
        "emergency-stop",
        "fault-in-effect",
        "vehicle-off-the-charger",
    ];

    /// <summary>
    /// 出发条件各缺一条，十轮里都不出发：旧单仍 <c>HANG</c>（没确认取消）、旧单读不到（结果未知，继续对账）、车急停、车有故障、车不在原桩上（被挪走）——
    /// 没有等待点预占、没有清桩移动的停靠与订单意图、没有第二张单。
    /// </summary>
    [Theory]
    [MemberData(nameof(DepartureNotAllowed))]
    public async Task TheClearingVehicleDoesNotSetOffWhileAConditionIsMissing(string missing)
    {
        await using FleetFixture fleet = await ClearanceFleetAsync();
        JourneyRuntimeRow journey = await ConfirmedAsync(fleet);
        string expected = ChargingExecutionReasons.UnableToChargeClearing;
        switch (missing)
        {
            case "old-order-still-hang":
                break;
            case "old-order-result-unknown":
                fleet.Riot.PutOrder(new RiotOrderObservation(journey.PickupUpperId, RiotOrderObservationKind.Unknown, null));
                break;
            case "emergency-stop":
                fleet.Riot.CancelOrder(journey.PickupUpperId);
                fleet.Riot.SafetyReasons = ["RIOT_EMERGENCY_NOT_OK"];
                expected = ChargingExecutionReasons.ClearanceDepartureNotProven;
                break;
            case "fault-in-effect":
                fleet.Riot.CancelOrder(journey.PickupUpperId);
                fleet.Context.VehicleFaultStates.Add(new VehicleFaultStateRow
                {
                    AgvId = AgvA,
                    Level = VehicleFaultLevel.SuspectedBlocked,
                    FaultGeneration = 1,
                    EvidenceCode = "RIOT_ALARM",
                    EnteredAt = fleet.Clock.GetUtcNow(),
                });
                await fleet.Context.SaveChangesAsync(Token);
                expected = ChargingExecutionReasons.ClearanceDepartureNotProven;
                break;
            case "vehicle-off-the-charger":
                fleet.Riot.CancelOrder(journey.PickupUpperId);
                fleet.Riot.VehicleOverrides[KeyA] = seen => seen with { CurrentStationId = 300, BatteryState = "NO_CHARGE" };
                expected = ChargingExecutionReasons.ClearanceVehicleOffCharger;
                break;
        }

        for (int round = 0; round < 10; round++)
        {
            await RoundAsync(fleet);
        }

        Assert.Single(fleet.Riot.Creates);
        Assert.Null(await StationAsync(fleet, Point214.StationId));
        Assert.Empty(await ClearanceStopsAsync(fleet, journey));
        Assert.Empty(await fleet.Context.OrderIntents.AsNoTracking()
            .Where(row => row.Purpose == ClearanceMoveShape.IntentPurpose).ToArrayAsync(Token));
        Assert.Equal(expected, (await ChargingJourneyAsync(fleet, AgvA))!.BlockReasonCode);
    }

    /// <summary>
    /// 读数时刻晚于判定时刻（合成 L2 第一轮抓到的缺陷）：真实进程里时间在读车期间照走，RIoT 读数的时刻会落在读之前取的「此刻」之后。夹具时钟默认静止，
    /// 所以这里让它每被读一次就走 1 毫秒。「车静止停在桩上」要求读数不晚于此刻，判定用的此刻必须在读车之后取，否则车永远判不新鲜、永远不出发。
    /// </summary>
    [Fact]
    public async Task AReadingStampedAfterTheMomentItIsJudgedAtStillCountsAsFresh()
    {
        await using FleetFixture fleet = await ClearanceFleetAsync();
        JourneyRuntimeRow journey = await OldOrderEndedAsync(fleet);
        fleet.Clock.Tick = TimeSpan.FromMilliseconds(1);

        await RoundAsync(fleet);

        Assert.Equal(ChargingExecutionReasons.ClearanceToWaitingPoint, (await ChargingJourneyAsync(fleet, AgvA))!.BlockReasonCode);
        Assert.Equal((KeyA, journey.JourneyId), ((await StationAsync(fleet, 214))!.VehicleKey, (await StationAsync(fleet, 214))!.JourneyId));
    }

    /// <summary>
    /// 出发前提来回抖（这里是急停一会儿有一会儿没有）：承诺之后建单前复核不过就撤回（从没发出过，预占当场放）；撤回之后隔
    /// <c>JourneyRuntime:OwnOrderRebuildDelay</c> 才再承诺——不会每隔一轮就承诺、撤回一次，留下一串从没发出过的意图。
    /// </summary>
    [Fact]
    public async Task AFlappingPremiseDoesNotCommitAndWithdrawEveryOtherRound()
    {
        await using FleetFixture fleet = await ClearanceFleetAsync();
        JourneyRuntimeRow journey = await OldOrderEndedAsync(fleet);
        await RoundAsync(fleet);
        Assert.Single(await ClearanceStopsAsync(fleet, journey));

        fleet.Riot.SafetyReasons = ["RIOT_EMERGENCY_NOT_OK"];
        await RoundAsync(fleet);
        Assert.Null(await StationAsync(fleet, 214));
        Assert.Equal(ChargingExecutionReasons.ClearanceDepartureNotProven, (await ChargingJourneyAsync(fleet, AgvA))!.BlockReasonCode);

        fleet.Riot.SafetyReasons = [];
        for (int round = 0; round < 10; round++)
        {
            await RoundAsync(fleet);
        }
        Assert.Single(await ClearanceStopsAsync(fleet, journey));
        Assert.Null(await StationAsync(fleet, 214));
        Assert.Single(fleet.Riot.Creates);

        await fleet.HearFromEveryVehicleAsync();
        await fleet.RunRoundAsync(fleet.Options.OwnOrderRebuildDelay);
        Assert.Equal(2, (await ClearanceStopsAsync(fleet, journey)).Length);
        Assert.Equal((KeyA, journey.JourneyId), ((await StationAsync(fleet, 214))!.VehicleKey, (await StationAsync(fleet, 214))!.JourneyId));
    }

    public static TheoryData<string> PremisesLostBeforeTheCreate =>
    [
        "manual-confirmation-recorded",
        "vehicle-moved-off-the-charger",
        "old-order-not-ended-any-more",
        "switched-off",
    ];

    /// <summary>
    /// 承诺之后、建单之前，前提丢了一样（#446 审查 M-1、S-1、S-2）：人工清桩确认已记下而还没完成（有人正在桩旁处理这辆车）、车被挪离原桩、
    /// 旧单又读成没终结、开关被关掉——建单前复核按承诺时同一组前提再判，撤回：不建单，等待点预占当场放（经过上写撤回），停靠移除。
    /// </summary>
    [Theory]
    [MemberData(nameof(PremisesLostBeforeTheCreate))]
    public async Task APremiseLostBetweenTheCommitmentAndTheCreateWithdrawsItWithoutAnOrder(string lost)
    {
        await using FleetFixture fleet = await ClearanceFleetAsync();
        JourneyRuntimeRow journey = await OldOrderEndedAsync(fleet);
        await RoundAsync(fleet);
        Assert.Equal(KeyA, (await StationAsync(fleet, 214))?.VehicleKey);
        Assert.Single(fleet.Riot.Creates);
        switch (lost)
        {
            case "manual-confirmation-recorded":
                // The probe of the review: the confirmation is recorded, the vehicle still reads on the charger.
                Assert.Equal(1, await fleet.Context.Set<StationClearanceRow>()
                    .Where(row => row.CompletedAt == null)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(row => row.ConfirmedAt, fleet.Clock.GetUtcNow())
                        .SetProperty(row => row.ConfirmedBy, "fleet-r11"), Token));
                break;
            case "vehicle-moved-off-the-charger":
                fleet.Riot.VehicleOverrides[KeyA] = seen => seen with { CurrentStationId = 300, BatteryState = "NO_CHARGE" };
                break;
            case "old-order-not-ended-any-more":
                fleet.Riot.PutOrder(fleet.Riot.OrderOf(journey.PickupUpperId)! with
                {
                    Kind = RiotOrderObservationKind.Active,
                    OrderState = RiotOrderState.Hang,
                });
                break;
            case "switched-off":
                fleet.Options.ClearanceToWaitingPointEnabled = false;
                break;
        }

        await RoundAsync(fleet);

        Assert.Single(fleet.Riot.Creates);
        Assert.Null(await StationAsync(fleet, 214));
        Assert.Single(await fleet.Context.Set<StationExclusivityRecordRow>().AsNoTracking()
            .Where(row => row.StationId == 214 && row.ReleaseReason == ClearanceMoveReleaseReasons.Withdrawn).ToArrayAsync(Token));
        Assert.Equal(JourneyStopStatuses.Removed, Assert.Single(await ClearanceStopsAsync(fleet, journey)).Status);
        Assert.Empty(fleet.Riot.OrderCommands);
    }

    // ---- 选点：共用集合、无合格点、不猜站 --------------------------------------------------------------------------------

    /// <summary>
    /// 同一个等待点集合：空闲返回的 B 刚预占了 214，清桩的 A 只能拿剩下的 300——不另立清桩专用点，也不与 B 抢。
    /// </summary>
    [Fact]
    public async Task TheClearingVehicleTakesWhatAnIdleReturnLeftOfTheSameWaitingPoints()
    {
        await using FleetFixture fleet = await ClearanceFleetAsync(vehicles: 2, points: [Point214, Point300]);
        JourneyRuntimeRow journey = await ConfirmedAsync(fleet);
        Assert.Equal(VehiclePurposeAcquisitionOutcome.Acquired, await IdleReturnCommitAsync(fleet, KeyB, 214));
        fleet.Riot.CancelOrder(journey.PickupUpperId);

        await RoundAsync(fleet);

        JourneyStopRow move = Assert.Single(await ClearanceStopsAsync(fleet, journey));
        Assert.Equal(300, move.StationRiotId);
        Assert.Equal(KeyB, (await StationAsync(fleet, 214))!.VehicleKey);
        Assert.Equal((KeyA, journey.JourneyId), ((await StationAsync(fleet, 300))!.VehicleKey, (await StationAsync(fleet, 300))!.JourneyId));
    }

    /// <summary>
    /// 无合格点：唯一登记的 214 已被空闲返回预占。原地排队、写 <see cref="ChargingExecutionReasons.ClearanceNoWaitingPoint"/>、告警恰好一次（事件 2301，十轮），
    /// 没有任何订单意图、停靠或预占；不退到桩旁的 212 或任何登记外的站。点空出来之后照常出发。
    /// </summary>
    [Fact]
    public async Task WithNoEligibleWaitingPointTheVehicleQueuesWhereItIsAndNoOtherStationIsGuessed()
    {
        await using FleetFixture fleet = await ClearanceFleetAsync(vehicles: 2);
        JourneyRuntimeRow journey = await ConfirmedAsync(fleet);
        Assert.Equal(VehiclePurposeAcquisitionOutcome.Acquired, await IdleReturnCommitAsync(fleet, KeyB, 214));
        fleet.Riot.CancelOrder(journey.PickupUpperId);

        for (int round = 0; round < 10; round++)
        {
            await RoundAsync(fleet);
            Assert.Equal(KeyB, (await StationAsync(fleet, 214))?.VehicleKey);
        }

        Assert.Empty(await ClearanceStopsAsync(fleet, journey));
        Assert.Empty(await fleet.Context.OrderIntents.AsNoTracking()
            .Where(row => row.VehicleKey == KeyA && row.Purpose == ClearanceMoveShape.IntentPurpose).ToArrayAsync(Token));
        Assert.DoesNotContain(fleet.Riot.Creates, create => create.VehicleKey == KeyA && create.UpperId != journey.PickupUpperId);
        Assert.Null(await StationAsync(fleet, 212));
        Assert.Equal(ChargingExecutionReasons.ClearanceNoWaitingPoint, (await ChargingJourneyAsync(fleet, AgvA))!.BlockReasonCode);
        Assert.Single(fleet.EngineLog.Entries, entry => entry.EventId.Id == 2301);
    }

    // ---- 并发：争最后一个点 ----------------------------------------------------------------------------------------------

    /// <summary>
    /// 清桩车与空闲返回车同一刻争最后一个等待点，绕开选点的预读直接在承诺这一刻构造：先到的那一方拿到，后到的被主键拒绝、整笔回滚——
    /// 没有停靠、没有订单意图、原桩停靠的状态不变；两种先后各一次。
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AClearingVehicleAndAnIdleReturnRacingForTheLastPointLeaveExactlyOneWinner(bool idleReturnFirst)
    {
        await using FleetFixture fleet = await ClearanceFleetAsync(vehicles: 2);
        JourneyRuntimeRow journey = await ConfirmedAsync(fleet);
        JourneyStopRow charger = await fleet.Context.Set<JourneyStopRow>()
            .SingleAsync(row => row.JourneyId == journey.JourneyId && row.StopRole == JourneyStopRoles.Charger, Token);
        string chargerStatus = charger.Status;
        (JourneyStopRow stop, OrderIntent intent) = ClearanceMoveShape.Build(journey, charger, 1, 214, Point214.StationName, fleet.Clock.GetUtcNow());

        if (idleReturnFirst)
        {
            Assert.Equal(VehiclePurposeAcquisitionOutcome.Acquired, await IdleReturnCommitAsync(fleet, KeyB, 214));
            Assert.Equal(
                ClearanceWaitingPointCommitOutcome.StationHeld,
                await ClearanceWaitingPointCommitment.TryCommitAsync(
                    fleet.Context, KeyA, journey.JourneyId, Map, 214, 1, stop, intent, charger, fleet.Clock.GetUtcNow(), Token));
            fleet.Context.ChangeTracker.Clear();
            Assert.Equal(KeyB, (await StationAsync(fleet, 214))!.VehicleKey);
            Assert.Empty(await ClearanceStopsAsync(fleet, journey));
            Assert.Empty(await fleet.Context.OrderIntents.AsNoTracking().Where(row => row.UpperId == stop.UpperId).ToArrayAsync(Token));
            Assert.Equal(chargerStatus, (await fleet.Context.Set<JourneyStopRow>().AsNoTracking()
                .SingleAsync(row => row.StopId == charger.StopId, Token)).Status);
        }
        else
        {
            Assert.Equal(
                ClearanceWaitingPointCommitOutcome.Committed,
                await ClearanceWaitingPointCommitment.TryCommitAsync(
                    fleet.Context, KeyA, journey.JourneyId, Map, 214, 1, stop, intent, charger, fleet.Clock.GetUtcNow(), Token));
            fleet.Context.ChangeTracker.Clear();
            Assert.Equal(VehiclePurposeAcquisitionOutcome.StationHeld, await IdleReturnCommitAsync(fleet, KeyB, 214));
            Assert.Equal(KeyA, (await StationAsync(fleet, 214))!.VehicleKey);
            Assert.Null(await ClaimOfAsync(fleet, KeyB));
        }
    }

    /// <summary>用途占有已不是这趟旅程的 <c>CLEARING_MAINTENANCE</c>（人工清桩在这一刻放开了它）：承诺什么也不写。</summary>
    [Fact]
    public async Task ACommitmentWhoseClaimIsGoneWritesNothing()
    {
        await using FleetFixture fleet = await ClearanceFleetAsync();
        JourneyRuntimeRow journey = await ConfirmedAsync(fleet);
        await fleet.Context.Set<VehiclePurposeClaimRow>().Where(row => row.VehicleKey == KeyA).ExecuteDeleteAsync(Token);
        JourneyStopRow charger = await fleet.Context.Set<JourneyStopRow>()
            .SingleAsync(row => row.JourneyId == journey.JourneyId && row.StopRole == JourneyStopRoles.Charger, Token);
        (JourneyStopRow stop, OrderIntent intent) = ClearanceMoveShape.Build(journey, charger, 1, 214, Point214.StationName, fleet.Clock.GetUtcNow());

        Assert.Equal(
            ClearanceWaitingPointCommitOutcome.ClaimNotHeld,
            await ClearanceWaitingPointCommitment.TryCommitAsync(
                fleet.Context, KeyA, journey.JourneyId, Map, 214, 1, stop, intent, charger, fleet.Clock.GetUtcNow(), Token));
        fleet.Context.ChangeTracker.Clear();
        Assert.Null(await StationAsync(fleet, 214));
        Assert.Empty(await ClearanceStopsAsync(fleet, journey));
    }

    // ---- 人工清桩与系统到点：两种次序 ------------------------------------------------------------------------------------

    /// <summary>
    /// 车正开往等待点时有人按了人工清桩确认：清桩由人工证明完成（只此一次）、桩当场释放；移动单<b>不取消</b>，车照常开完，到点那一轮只把 214 转为占用、
    /// 旅程收尾——不再写清桩记录、不再放桩。清桩记录一条、桩的释放经过一条，没有订单命令。
    /// </summary>
    [Fact]
    public async Task AManualConfirmationWhileDrivingCompletesTheClearanceOnceAndTheVehicleDrivesOn()
    {
        await using FleetFixture fleet = await ClearanceFleetAsync();
        JourneyRuntimeRow journey = await OldOrderEndedAsync(fleet);
        await RoundAsync(fleet);
        await RoundAsync(fleet);
        string upperId = ClearanceMoveShape.UpperIdFor(journey.JourneyId, 1);
        fleet.Riot.VehicleOverrides[KeyA] = seen => seen with { CurrentStationId = 300, Speed = 300, ProcState = "EXECUTING", OrderTaskId = "T" };

        ManualStationClearanceConfirmation decision = await ManualClearance(fleet).DecideAsync(Request("00000000-0000-4000-8000-0000000c4091"), Token);

        Assert.Equal((FieldConfirmationDecision.Confirmed, true), (decision.Decision.Outcome, decision.StationReleased));
        Assert.Equal(StationClearanceProofs.ManualConfirmation, (await ClearanceAsync(fleet)).Proof);
        Assert.Null(await HolderAsync(fleet, Near.StationId));
        for (int round = 0; round < 3; round++)
        {
            await RoundAsync(fleet);
            Assert.Equal((KeyA, StationExclusivityStates.Reserved), await HolderAsync(fleet, 214));
            Assert.NotEqual(JourneyRuntimeStage.Completed, (await fleet.Context.JourneyRuntimes.AsNoTracking()
                .SingleAsync(row => row.JourneyId == journey.JourneyId, Token)).Stage);
        }

        fleet.Riot.CompleteOrder(upperId);
        StandOn(fleet, 214);
        await RoundAsync(fleet);

        StationClearanceRow clearance = Assert.Single(await fleet.Context.Set<StationClearanceRow>().AsNoTracking().ToArrayAsync(Token));
        Assert.Equal(StationClearanceProofs.ManualConfirmation, clearance.Proof);
        Assert.Equal((KeyA, StationExclusivityStates.Occupied), await HolderAsync(fleet, 214));
        JourneyRuntimeRow closed = await fleet.Context.JourneyRuntimes.AsNoTracking().SingleAsync(row => row.JourneyId == journey.JourneyId, Token);
        Assert.Equal((JourneyRuntimeStage.Completed, ChargingExecutionReasons.UnableToChargeCleared), (closed.Stage, closed.BlockReasonCode));
        Assert.Single(await ChargerReleasesAsync(fleet));
        Assert.Empty(fleet.Riot.OrderCommands);
        Assert.Equal(2, fleet.Riot.Creates.Count);
    }

    /// <summary>
    /// 系统先到：车到点、清桩由系统证明完成之后，有人又按了人工清桩确认——答已确认、桩已释放，但什么也不再写：清桩记录仍是一条、证明仍是到点、
    /// 确认人那几列为空，桩的释放经过仍是一条。
    /// </summary>
    [Fact]
    public async Task AManualConfirmationAfterTheArrivalRecordsNothingTwice()
    {
        await using FleetFixture fleet = await ClearanceFleetAsync();
        JourneyRuntimeRow journey = await ArrivedAndClearedAsync(fleet);

        ManualStationClearanceConfirmation decision = await ManualClearance(fleet).DecideAsync(Request("00000000-0000-4000-8000-0000000c4092"), Token);

        Assert.Equal(FieldConfirmationDecision.Confirmed, decision.Decision.Outcome);
        StationClearanceRow clearance = Assert.Single(await fleet.Context.Set<StationClearanceRow>().AsNoTracking().ToArrayAsync(Token));
        Assert.Equal((StationClearanceProofs.ArrivedAtWaitingPoint, (string?)null), (clearance.Proof, clearance.ConfirmedBy));
        Assert.Single(await ChargerReleasesAsync(fleet));
        Assert.Equal((KeyA, StationExclusivityStates.Occupied), await HolderAsync(fleet, 214));
        _ = journey;
    }

    // ---- 重连补发 --------------------------------------------------------------------------------------------------------

    /// <summary>
    /// 车载端在建单前后断线：途中那一对快照（等待点腿 <c>ACTIVE</c>、<c>CLEARING_MAINTENANCE</c>）留在发件箱里没送到。重连之后按原 <c>messageId</c> 补发，
    /// 发件箱里每张仍只有一行，不产生第二份；被它取代的清桩中快照不再补发。
    /// </summary>
    [Fact]
    public async Task AfterAReconnectTheClearanceMoveSnapshotsAreResentUnderTheirOwnIdsWithoutASecondCopy()
    {
        await using FleetFixture fleet = await ClearanceFleetAsync();
        JourneyRuntimeRow journey = await OldOrderEndedAsync(fleet);
        await RoundAsync(fleet);
        fleet.Peer.Unavailable = AgvA;
        await RoundAsync(fleet);
        string[] ids =
        [
            ClearanceMoveShape.PlanMessageId(journey.JourneyId, 1),
            ClearanceMoveShape.StateMessageId(journey.JourneyId, 1),
        ];
        Assert.DoesNotContain(fleet.Peer.Delivered, line => ids.Contains(line.MessageId));
        Assert.Equal(2, fleet.Riot.Creates.Count);

        fleet.Peer.Unavailable = null;
        await RoundAsync(fleet);
        await RoundAsync(fleet);

        Assert.All(ids, id => Assert.Contains(fleet.Peer.Delivered, line => line.MessageId == id));
        Assert.All(ids, id => Assert.Single(fleet.Context.ProtocolOutbox.AsNoTracking().Where(row => row.MessageId == id)));
        Assert.Equal(2, fleet.Riot.Creates.Count);
    }

    // ---- 崩溃点 ----------------------------------------------------------------------------------------------------------

    /// <summary>
    /// 到点完成那一个事务的最后一步（等待点转占用）崩掉：什么也不留——清桩没完成、桩仍是这一趟的独占、214 仍是预占、用途仍是
    /// <c>CLEARING_MAINTENANCE</c>、旅程没收尾；不会留下「桩已空而清桩未完成」。下一轮按同一组事实完成，桩的释放经过只有一条。
    /// </summary>
    [Fact]
    public async Task ACrashInsideTheArrivalSaveLeavesNoHalfAndTheNextRoundCompletes()
    {
        ChargingUnableToChargeTests.FailOnce crash = new("UPDATE \"StationExclusivities\"");
        await using FleetFixture fleet = await ClearanceFleetAsync(commands: crash);
        JourneyRuntimeRow journey = await OldOrderEndedAsync(fleet);
        await RoundAsync(fleet);
        await RoundAsync(fleet);
        fleet.Riot.CompleteOrder(ClearanceMoveShape.UpperIdFor(journey.JourneyId, 1));
        StandOn(fleet, 214);

        crash.Armed = true;
        await Assert.ThrowsAnyAsync<Exception>(() => RoundAsync(fleet));
        Assert.Equal(1, crash.Fired);
        // A round's scope is thrown away with its failure in the host; the fixture shares one context, so it forgets here.
        fleet.Context.ChangeTracker.Clear();

        Assert.Null((await ClearanceAsync(fleet)).CompletedAt);
        Assert.Equal((KeyA, StationExclusivityStates.Reserved), await HolderAsync(fleet, Near.StationId));
        Assert.Equal((KeyA, StationExclusivityStates.Reserved), await HolderAsync(fleet, 214));
        Assert.Equal(VehiclePurposes.ClearingMaintenance, (await ClaimOfAsync(fleet, KeyA))!.Value.Purpose);
        Assert.Equal(ChargingCyclePhases.Clearing, (await OpenCycleAsync(fleet)).Phase);
        Assert.Empty(await ChargerReleasesAsync(fleet));

        await RoundAsync(fleet);

        Assert.NotNull((await ClearanceAsync(fleet)).CompletedAt);
        Assert.Null(await HolderAsync(fleet, Near.StationId));
        Assert.Equal((KeyA, StationExclusivityStates.Occupied), await HolderAsync(fleet, 214));
        Assert.Single(await ChargerReleasesAsync(fleet));
    }

    /// <summary>
    /// 建单前后崩溃：建单发出、应答丢了（结果未知）——之后几轮车、目标点、用途、预占全部保持，不换号、不换点、不重复建单；RIoT 上那张单其实在，
    /// 下一次对账按同一个 <c>upperId</c> 确认它。全程只有一张清桩移动单。
    /// </summary>
    [Fact]
    public async Task AnUnknownCreateResultKeepsEverythingAndNeverMakesASecondOrderOrAnotherId()
    {
        await using FleetFixture fleet = await ClearanceFleetAsync(points: [Point214, Point300]);
        JourneyRuntimeRow journey = await OldOrderEndedAsync(fleet);
        await RoundAsync(fleet);
        string upperId = ClearanceMoveShape.UpperIdFor(journey.JourneyId, 1);
        fleet.Riot.CreateAnswer = intent => new RiotOrderObservation(intent.UpperId, RiotOrderObservationKind.Unknown, null);

        // 300 is the cheaper of the two from 211 (5 m against 14 m): the commitment took it, and nothing ever moves it to 214.
        for (int round = 0; round < 5; round++)
        {
            await RoundAsync(fleet);
            Assert.Equal((KeyA, StationExclusivityStates.Reserved), await HolderAsync(fleet, 300));
            Assert.Null(await StationAsync(fleet, 214));
        }
        Assert.Equal(ChargingExecutionReasons.ClearanceMoveNotConfirmed, (await ChargingJourneyAsync(fleet, AgvA))!.BlockReasonCode);
        Assert.Single(fleet.Riot.Creates, create => create.UpperId == upperId);
        Assert.Single(await ClearanceStopsAsync(fleet, journey));

        fleet.Riot.CreateAnswer = null;
        fleet.Riot.PutOrder(new RiotOrderObservation(
            upperId, RiotOrderObservationKind.Active, $"ORDER-{upperId}", OrderState: 3, VehicleKey: KeyA, MapId: Map, DestinationStationId: 300));
        await RoundAsync(fleet);

        Assert.Equal("CONFIRMED", (await fleet.Context.OrderIntents.AsNoTracking().SingleAsync(row => row.UpperId == upperId, Token)).Status);
        Assert.Equal(ChargingExecutionReasons.ClearanceToWaitingPoint, (await ChargingJourneyAsync(fleet, AgvA))!.BlockReasonCode);
        Assert.Single(fleet.Riot.Creates, create => create.UpperId == upperId);
        Assert.Equal(2, fleet.Riot.Creates.Count);
    }

    // ---- 失败分流（REQ-0296）---------------------------------------------------------------------------------------------

    /// <summary>
    /// 结果未知之后 RIoT 一直按真实 RIoT 的样子答查无此单（HTTP 200、无 result），车证明停稳、名下没有未完成的单：满窗口之前一切保持；满窗口之后按「证明没单没动」
    /// 放预占（经过上写 <see cref="ClearanceMoveReleaseReasons.NeverAppeared"/>）、回到清桩中，重评不选原失败点——第一次去的是更近的 300，下一次去的是 214。
    /// </summary>
    [Fact]
    public async Task AMoveThatNeverAppearsIsAbandonedOnEvidenceAndTheNextAttemptSkipsItsPoint()
    {
        await using FleetFixture fleet = await ClearanceFleetAsync(points: [Point214, Point300]);
        JourneyRuntimeRow journey = await OldOrderEndedAsync(fleet);
        await RoundAsync(fleet);
        fleet.Riot.AnswersAbsentAsRealRiot = true;
        fleet.Riot.CreateAnswer = intent => fleet.Riot.RealRiotAbsent(intent.UpperId);

        await RoundAsync(fleet);
        fleet.Riot.CreateAnswer = null;
        Assert.Equal((KeyA, StationExclusivityStates.Reserved), await HolderAsync(fleet, 300));

        for (int round = 0; round < 3; round++)
        {
            await fleet.HearFromEveryVehicleAsync();
            await fleet.RunRoundAsync(TimeSpan.FromSeconds(30));
        }
        Assert.Equal((KeyA, StationExclusivityStates.Reserved), await HolderAsync(fleet, 300));

        for (int round = 0; round < 4; round++)
        {
            await fleet.HearFromEveryVehicleAsync();
            await fleet.RunRoundAsync(TimeSpan.FromSeconds(30));
        }

        Assert.Null(await StationAsync(fleet, 300));
        Assert.Single(await fleet.Context.Set<StationExclusivityRecordRow>().AsNoTracking()
            .Where(row => row.StationId == 300 && row.ReleaseReason == ClearanceMoveReleaseReasons.NeverAppeared).ToArrayAsync(Token));
        JourneyStopRow[] moves = await ClearanceStopsAsync(fleet, journey);
        Assert.Contains(moves, stop => stop.StationRiotId == 300 && stop.Status == JourneyStopStatuses.Removed);
        Assert.Contains(moves, stop => stop.StationRiotId == 214 && stop.UpperId == ClearanceMoveShape.UpperIdFor(journey.JourneyId, 2));
        Assert.Equal((KeyA, journey.JourneyId), ((await StationAsync(fleet, 214))!.VehicleKey, (await StationAsync(fleet, 214))!.JourneyId));
        Assert.Equal(ChargingCyclePhases.Clearing, (await OpenCycleAsync(fleet)).Phase);
    }

    /// <summary>
    /// 清桩移动单被人在 RIoT 里取消：车还可能在动时保持一切（预占、用途、桩）；车证明停稳之后放预占（<see cref="ClearanceMoveReleaseReasons.Ended"/>），
    /// 不重建、这个周期不再自动出发（十轮里没有第三张单），写 <see cref="ChargingExecutionReasons.ClearanceMoveEnded"/>、告警恰好一次；人工清桩照常完成并收尾。
    /// 本服务端一条订单命令都没发。
    /// </summary>
    [Fact]
    public async Task AMoveCancelledInRiotIsNotRebuiltAndOnlyAManualClearanceIsLeft()
    {
        await using FleetFixture fleet = await ClearanceFleetAsync(points: [Point214, Point300]);
        JourneyRuntimeRow journey = await OldOrderEndedAsync(fleet);
        await RoundAsync(fleet);
        await RoundAsync(fleet);
        string upperId = ClearanceMoveShape.UpperIdFor(journey.JourneyId, 1);
        fleet.Riot.VehicleOverrides[KeyA] = seen => seen with { CurrentStationId = 12, Speed = 300, ProcState = "EXECUTING", OrderTaskId = "T" };
        fleet.Riot.CancelOrder(upperId);

        await RoundAsync(fleet);
        Assert.Equal((KeyA, StationExclusivityStates.Reserved), await HolderAsync(fleet, 300));
        Assert.Equal(ChargingExecutionReasons.ClearanceMoveHeld, (await ChargingJourneyAsync(fleet, AgvA))!.BlockReasonCode);

        fleet.Riot.VehicleOverrides[KeyA] = seen => seen with { CurrentStationId = 12, BatteryState = "NO_CHARGE" };
        for (int round = 0; round < 10; round++)
        {
            await RoundAsync(fleet);
        }

        Assert.Null(await StationAsync(fleet, 214));
        Assert.Null(await StationAsync(fleet, 300));
        Assert.Single(await fleet.Context.Set<StationExclusivityRecordRow>().AsNoTracking()
            .Where(row => row.StationId == 300 && row.ReleaseReason == ClearanceMoveReleaseReasons.Ended).ToArrayAsync(Token));
        Assert.Equal(2, fleet.Riot.Creates.Count);
        Assert.Empty(fleet.Riot.OrderCommands);
        Assert.Empty(await fleet.Context.Set<OwnOrderRebuildRow>().AsNoTracking().ToArrayAsync(Token));
        Assert.Equal(ChargingExecutionReasons.ClearanceMoveEnded, (await ChargingJourneyAsync(fleet, AgvA))!.BlockReasonCode);
        Assert.Single(fleet.EngineLog.Entries, entry => entry.EventId.Id == 2304);
        Assert.Equal((KeyA, StationExclusivityStates.Reserved), await HolderAsync(fleet, Near.StationId));

        ManualStationClearanceConfirmation decision = await ManualClearance(fleet).DecideAsync(Request("00000000-0000-4000-8000-0000000c4093"), Token);
        Assert.Equal((FieldConfirmationDecision.Confirmed, true), (decision.Decision.Outcome, decision.StationReleased));
        await RoundAsync(fleet);
        Assert.Equal(JourneyRuntimeStage.Completed, (await fleet.Context.JourneyRuntimes.AsNoTracking()
            .SingleAsync(row => row.JourneyId == journey.JourneyId, Token)).Stage);
    }

    /// <summary>
    /// 清桩移动单 FAILED：交故障模型（疑似故障、码），预占、用途、桩都保持；人工清除故障之后只结束这次移动——放预占、周期仍在清桩中、不再自动出发，
    /// 而不是把周期按订单失败结束（那样清桩记录就永远完不成）。之后人工清桩照常完成清桩记录并收尾。
    /// </summary>
    [Fact]
    public async Task AFailedMoveClearedByAPersonEndsOnlyTheMoveAndTheClearanceStillCompletes()
    {
        await using FleetFixture fleet = await ClearanceFleetAsync();
        JourneyRuntimeRow journey = await OldOrderEndedAsync(fleet);
        await RoundAsync(fleet);
        await RoundAsync(fleet);
        string upperId = ClearanceMoveShape.UpperIdFor(journey.JourneyId, 1);
        fleet.Riot.MovementState = "MT_FINISHED";
        fleet.Riot.FailOrder(upperId);

        await RoundAsync(fleet);
        await RoundAsync(fleet);
        Assert.Equal(VehicleFaultEvidence.OrderFailed, (await ChargingJourneyAsync(fleet, AgvA))!.BlockReasonCode);
        Assert.Equal((KeyA, StationExclusivityStates.Reserved), await HolderAsync(fleet, 214));

        fleet.Context.ChangeTracker.Clear();
        VehicleFaultRecoveryDecision recovery = await fleet.CreateFaultRecovery().RecoverAsync(
            new VehicleFaultRecoveryRequest(
                new EmergencyStopSubject(AgvA, KeyA), VehicleFaultRecoveryAction.ClearFault, "operator-1", true, null),
            Token);
        fleet.Context.ChangeTracker.Clear();

        Assert.Equal(
            (VehicleFaultRecoveryOutcome.Cleared, VehicleFaultRecoveryDispositions.ClearanceMoveEnded),
            (recovery.Outcome, recovery.Disposition));
        Assert.Null(await StationAsync(fleet, 214));
        Assert.Equal(ChargingCyclePhases.Clearing, (await OpenCycleAsync(fleet)).Phase);
        Assert.Equal(VehiclePurposes.ClearingMaintenance, (await ClaimOfAsync(fleet, KeyA))!.Value.Purpose);
        Assert.Equal((KeyA, StationExclusivityStates.Reserved), await HolderAsync(fleet, Near.StationId));
        for (int round = 0; round < 5; round++)
        {
            await RoundAsync(fleet);
        }
        Assert.Equal(2, fleet.Riot.Creates.Count);
        Assert.Equal(ChargingExecutionReasons.ClearanceMoveEnded, (await ChargingJourneyAsync(fleet, AgvA))!.BlockReasonCode);

        fleet.Riot.VehicleOverrides[KeyA] = seen => seen with { CurrentStationId = 300, BatteryState = "NO_CHARGE" };
        ManualStationClearanceConfirmation decision = await ManualClearance(fleet).DecideAsync(Request("00000000-0000-4000-8000-0000000c4094"), Token);
        Assert.Equal((FieldConfirmationDecision.Confirmed, true), (decision.Decision.Outcome, decision.StationReleased));
        StationClearanceRow clearance = await ClearanceAsync(fleet);
        Assert.Equal(StationClearanceProofs.ManualConfirmation, clearance.Proof);
        Assert.NotNull(clearance.CompletedAt);
        await RoundAsync(fleet);
        Assert.Equal(JourneyRuntimeStage.Completed, (await fleet.Context.JourneyRuntimes.AsNoTracking()
            .SingleAsync(row => row.JourneyId == journey.JourneyId, Token)).Stage);
    }

    // ---- 夹具 ------------------------------------------------------------------------------------------------------------

    private static async Task<FleetFixture> ClearanceFleetAsync(
        int vehicles = 1,
        bool enabled = true,
        DbCommandInterceptor? commands = null,
        WaitingPointEntry[]? points = null)
    {
        FleetFixture fleet = await FleetAsync(vehicles, commands: commands);
        fleet.Options.ClearanceToWaitingPointEnabled = enabled;
        points ??= [Point214];
        await new WaitingPointRegistry(fleet.Context, JourneyRuntimeWorkerTestKit.CreateGovernedPublisher(fleet.Context))
            .WriteVersionAsync(points, fleet.Clock.GetUtcNow(), Token);
        fleet.Riot.ExtraStations.AddRange(points.Select(point => new RiotMapStation(point.StationId, point.StationName)));
        return fleet;
    }

    /// <summary>形成「已确认充不上」，然后有人在 RIoT 里结束了旧充电单（取消开关默认关，这是现场的出口）。车仍停在 211 上。</summary>
    private static async Task<JourneyRuntimeRow> OldOrderEndedAsync(FleetFixture fleet)
    {
        JourneyRuntimeRow journey = await ConfirmedAsync(fleet);
        fleet.Riot.CancelOrder(journey.PickupUpperId);
        return journey;
    }

    /// <summary>开过去、到点、清桩由系统证明完成。</summary>
    private static async Task<JourneyRuntimeRow> ArrivedAndClearedAsync(FleetFixture fleet)
    {
        JourneyRuntimeRow journey = await OldOrderEndedAsync(fleet);
        await RoundAsync(fleet);
        await RoundAsync(fleet);
        fleet.Riot.CompleteOrder(ClearanceMoveShape.UpperIdFor(journey.JourneyId, 1));
        StandOn(fleet, 214);
        await RoundAsync(fleet);
        Assert.Equal(StationClearanceProofs.ArrivedAtWaitingPoint, (await ClearanceAsync(fleet)).Proof);
        return journey;
    }

    private static void StandOn(FleetFixture fleet, int stationId) =>
        fleet.Riot.VehicleOverrides[KeyA] = seen => seen with { CurrentStationId = stationId, BatteryState = "NO_CHARGE" };

    private static async Task<JourneyStopRow[]> ClearanceStopsAsync(FleetFixture fleet, JourneyRuntimeRow journey) =>
        await fleet.Context.Set<JourneyStopRow>().AsNoTracking()
            .Where(row => row.JourneyId == journey.JourneyId && row.StopRole == JourneyStopRoles.WaitingPoint)
            .ToArrayAsync(Token);

    private static async Task<StationClearanceRow> ClearanceAsync(FleetFixture fleet) =>
        await fleet.Context.Set<StationClearanceRow>().AsNoTracking().SingleAsync(Token);

    private static async Task<StationExclusivityRecordRow[]> ChargerReleasesAsync(FleetFixture fleet) =>
        await fleet.Context.Set<StationExclusivityRecordRow>().AsNoTracking()
            .Where(row => row.StationId == Near.StationId && row.ReleasedAt != null && row.StationKind == StationExclusivityKinds.Charger)
            .ToArrayAsync(Token);

    /// <summary>另一辆车的空闲返回承诺（用途与等待点预占一次保存），与评估器在派车轮末尾做的同一个原子承诺。</summary>
    private static async Task<VehiclePurposeAcquisitionOutcome> IdleReturnCommitAsync(FleetFixture fleet, string vehicleKey, int stationId)
    {
        DateTimeOffset now = fleet.Clock.GetUtcNow();
        VehiclePurposeAcquisitionOutcome outcome = await IdleReturnEvaluator.TryCommitAsync(
            new VehiclePurposeLedgerStore(fleet.Context), vehicleKey, IdleReturnIdentity.JourneyIdFor(vehicleKey, now), Map, stationId, 1,
            now, Token);
        fleet.Context.ChangeTracker.Clear();
        return outcome;
    }

    private static ManualStationClearance ManualClearance(FleetFixture fleet) =>
        new(
            fleet.Context,
            new FieldConfirmationRequestStore(fleet.Context),
            new StationClearanceStore(fleet.Context),
            fleet.Riot,
            new FieldOperatorRoleRoster(Options.Create(new FieldOperatorRoleOptions { Path = fleet.ClearanceRosterPath })),
            new GovernanceStore(fleet.Context, new GovernanceDeploymentIdentity("deployment:8005-controlserver@test"), AuditRetentionPolicy.Default),
            Options.Create(fleet.Options),
            fleet.Clock,
            NullLogger<ManualStationClearance>.Instance);

    private static ManualStationClearanceRequest Request(string confirmationRequestId) =>
        new(
            ManualStationClearanceSources.Onboard, AgvA, KeyA, confirmationRequestId, 1, Guid.NewGuid().ToString("D"),
            $"{Near.StationName}||fleet-r11|STATION_EMPTY", Near.StationName, null, "STATION_EMPTY", "fleet-r11", "BADGE",
            DateTimeOffset.Parse("2026-09-08T06:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
            DateTimeOffset.Parse("2026-09-08T06:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
}
