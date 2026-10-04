using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Charging;
using ControlServer.Host.Runtime.Commands;
using ControlServer.Host.Runtime.Faults;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using static ControlServer.Tests.ChargingAllocationTests;
using static ControlServer.Tests.ChargingExecutionTests;
using static ControlServer.Tests.ChargingUnableToChargeTests;
using FleetFixture = ControlServer.Tests.MultiVehicleExecutionTests.FleetFixture;

namespace ControlServer.Tests;

// control-server#462: a manual clearance confirmed while the vehicle drives to its waiting point used to release the
// vehicle's CLEARING_MAINTENANCE purpose there and then, while the journey stayed open until the move converged. The purpose
// now stays with the journey until then; and an unclosed journey on its own still keeps the vehicle from every path that
// commits one.
public sealed partial class ChargingClearanceToWaitingPointTests
{
    /// <summary>
    /// 开往等待点途中人工清桩确认：清桩完成、桩当场放开，但用途 <c>CLEARING_MAINTENANCE</c> 跟着这趟还在开的旅程留着，直到移动收敛——到点、
    /// 或单被人在 RIoT 里结束且车证明停稳之后旅程收尾——才放（cs#462）。其间每一轮它都在：派车与充电分配写入侧防重复承诺的那道主键一直立着。
    /// </summary>
    [Theory]
    [InlineData("arrival")]
    [InlineData("endedInRiot")]
    public async Task AManualConfirmationOnTheWayKeepsThePurposeUntilTheMoveConverges(string exit)
    {
        await using FleetFixture fleet = await ClearanceFleetAsync();
        JourneyRuntimeRow journey = await OldOrderEndedAsync(fleet);
        await RoundAsync(fleet);
        await RoundAsync(fleet);
        string upperId = ClearanceMoveShape.UpperIdFor(journey.JourneyId, 1);
        fleet.Riot.VehicleOverrides[KeyA] = seen => seen with { CurrentStationId = 300, Speed = 300, ProcState = "EXECUTING", OrderTaskId = "T" };

        ManualStationClearanceConfirmation decision = await ManualClearance(fleet).DecideAsync(Request("00000000-0000-4000-8000-0000000c4622"), Token);

        Assert.Equal((FieldConfirmationDecision.Confirmed, true), (decision.Decision.Outcome, decision.StationReleased));
        Assert.Null(await HolderAsync(fleet, Near.StationId));
        for (int round = 0; round < 3; round++)
        {
            Assert.Equal((VehiclePurposes.ClearingMaintenance, journey.JourneyId), await ClaimOfAsync(fleet, KeyA));
            await RoundAsync(fleet);
        }
        Assert.Equal((VehiclePurposes.ClearingMaintenance, journey.JourneyId), await ClaimOfAsync(fleet, KeyA));

        if (exit == "arrival")
        {
            fleet.Riot.CompleteOrder(upperId);
            StandOn(fleet, 214);
        }
        else
        {
            fleet.Riot.CancelOrder(upperId);
            fleet.Riot.VehicleOverrides[KeyA] = seen => seen with { CurrentStationId = 12, BatteryState = "NO_CHARGE" };
        }
        for (int round = 0; round < 3; round++)
        {
            await RoundAsync(fleet);
        }

        Assert.Equal(JourneyRuntimeStage.Completed, (await fleet.Context.JourneyRuntimes.AsNoTracking()
            .SingleAsync(row => row.JourneyId == journey.JourneyId, Token)).Stage);
        Assert.Null(await ClaimOfAsync(fleet, KeyA));
    }

    /// <summary>
    /// 人工清桩确认之后，这次移动不到点就结束的另外三个出口（cs#462 独立审查 S-1、S-2）：单 FAILED、人清除故障；建单发出过、RIoT 一直查无此单、
    /// 凭证据放弃；单从没发出、建单前复核撤回。每一个都不抛（周期已被人工结束，没有「回到清桩中」的快照可发），旅程随后收尾、用途在收尾时放。
    /// 撤回那一个不建单。
    /// </summary>
    [Theory]
    [InlineData("failedThenFaultCleared")]
    [InlineData("neverAppearedAbandoned")]
    [InlineData("withdrawnBeforeSent")]
    public async Task EveryOtherWayAMoveEndsAfterAManualClearanceClosesTheJourneyAndReleasesThePurpose(string exit)
    {
        await using FleetFixture fleet = await ClearanceFleetAsync();
        JourneyRuntimeRow journey = await OldOrderEndedAsync(fleet);
        string upperId = ClearanceMoveShape.UpperIdFor(journey.JourneyId, 1);
        await RoundAsync(fleet);
        switch (exit)
        {
            case "failedThenFaultCleared":
                await RoundAsync(fleet);
                fleet.Riot.VehicleOverrides[KeyA] = seen => seen with { CurrentStationId = 300, Speed = 300, ProcState = "EXECUTING", OrderTaskId = "T" };
                await ConfirmOnTheWayAsync(fleet, journey, "00000000-0000-4000-8000-0000000c4624");
                fleet.Riot.MovementState = "MT_FINISHED";
                fleet.Riot.FailOrder(upperId);
                fleet.Riot.VehicleOverrides[KeyA] = seen => seen with { CurrentStationId = 12, BatteryState = "NO_CHARGE" };
                await RoundAsync(fleet);
                await RoundAsync(fleet);
                Assert.Equal(VehicleFaultEvidence.OrderFailed, (await ChargingJourneyAsync(fleet, AgvA))!.BlockReasonCode);
                fleet.Context.ChangeTracker.Clear();
                VehicleFaultRecoveryDecision recovery = await fleet.CreateFaultRecovery().RecoverAsync(
                    new VehicleFaultRecoveryRequest(
                        new EmergencyStopSubject(AgvA, KeyA), VehicleFaultRecoveryAction.ClearFault, "operator-1", true, null),
                    Token);
                fleet.Context.ChangeTracker.Clear();
                Assert.Equal(
                    (VehicleFaultRecoveryOutcome.Cleared, VehicleFaultRecoveryDispositions.ClearanceMoveEnded),
                    (recovery.Outcome, recovery.Disposition));
                break;
            case "neverAppearedAbandoned":
                fleet.Riot.AnswersAbsentAsRealRiot = true;
                fleet.Riot.CreateAnswer = intent => fleet.Riot.RealRiotAbsent(intent.UpperId);
                await RoundAsync(fleet);
                fleet.Riot.CreateAnswer = null;
                fleet.Riot.VehicleOverrides[KeyA] = seen => seen with { CurrentStationId = 12, BatteryState = "NO_CHARGE" };
                await ConfirmOnTheWayAsync(fleet, journey, "00000000-0000-4000-8000-0000000c4625");
                for (int round = 0; round < 8; round++)
                {
                    await fleet.HearFromEveryVehicleAsync();
                    await fleet.RunRoundAsync(TimeSpan.FromSeconds(30));
                }
                Assert.Single(await fleet.Context.Set<StationExclusivityRecordRow>().AsNoTracking()
                    .Where(row => row.StationId == 214 && row.ReleaseReason == ClearanceMoveReleaseReasons.NeverAppeared).ToArrayAsync(Token));
                break;
            case "withdrawnBeforeSent":
                fleet.Riot.VehicleOverrides[KeyA] = seen => seen with { CurrentStationId = 12, BatteryState = "NO_CHARGE" };
                await ConfirmOnTheWayAsync(fleet, journey, "00000000-0000-4000-8000-0000000c4626");
                break;
        }

        for (int round = 0; round < 3; round++)
        {
            await RoundAsync(fleet);
        }

        Assert.Equal(JourneyRuntimeStage.Completed, (await fleet.Context.JourneyRuntimes.AsNoTracking()
            .SingleAsync(row => row.JourneyId == journey.JourneyId, Token)).Stage);
        Assert.Null(await ClaimOfAsync(fleet, KeyA));
        Assert.Null(await StationAsync(fleet, 214));
        if (exit == "withdrawnBeforeSent")
        {
            Assert.DoesNotContain(fleet.Riot.Creates, create => create.UpperId == upperId);
        }
    }

    /// <summary>移动已承诺（等待点腿还没结束）时人工清桩确认：清桩完成、桩放开，用途仍跟着旅程（cs#462）。</summary>
    private static async Task ConfirmOnTheWayAsync(FleetFixture fleet, JourneyRuntimeRow journey, string confirmationRequestId)
    {
        ManualStationClearanceConfirmation decision = await ManualClearance(fleet).DecideAsync(Request(confirmationRequestId), Token);
        Assert.Equal((FieldConfirmationDecision.Confirmed, true), (decision.Decision.Outcome, decision.StationReleased));
        Assert.Single(await ClearanceStopsAsync(fleet, journey), stop => stop.Status != JourneyStopStatuses.Removed);
        Assert.Equal((VehiclePurposes.ClearingMaintenance, journey.JourneyId), await ClaimOfAsync(fleet, KeyA));
    }

    /// <summary>
    /// 建单结果未知、其间人工清桩确认（清桩完成、周期结束），之后对账才确认了那张移动单：发给车载端的途中业务状态带的是这辆车此刻的用途
    /// <c>CLEARING_MAINTENANCE</c>，不是 <c>null</c>——车在开，车载端按用途把它写成「清桩：正在前往等待点」，而不是按等待点腿猜成空闲返回（cs#462）。
    /// </summary>
    [Fact]
    public async Task TheMoveSnapshotsConfirmedAfterAManualClearanceStillCarryTheVehiclesPurpose()
    {
        await using FleetFixture fleet = await ClearanceFleetAsync();
        JourneyRuntimeRow journey = await OldOrderEndedAsync(fleet);
        await RoundAsync(fleet);
        string upperId = ClearanceMoveShape.UpperIdFor(journey.JourneyId, 1);
        fleet.Riot.CreateAnswer = intent => new RiotOrderObservation(intent.UpperId, RiotOrderObservationKind.Unknown, null);
        await RoundAsync(fleet);
        Assert.Equal(ChargingExecutionReasons.ClearanceMoveNotConfirmed, (await ChargingJourneyAsync(fleet, AgvA))!.BlockReasonCode);

        fleet.Riot.VehicleOverrides[KeyA] = seen => seen with { CurrentStationId = 300, Speed = 300, ProcState = "EXECUTING", OrderTaskId = "T" };
        ManualStationClearanceConfirmation decision = await ManualClearance(fleet).DecideAsync(Request("00000000-0000-4000-8000-0000000c4623"), Token);
        Assert.Equal((FieldConfirmationDecision.Confirmed, true), (decision.Decision.Outcome, decision.StationReleased));

        fleet.Riot.CreateAnswer = null;
        fleet.Riot.PutOrder(new RiotOrderObservation(
            upperId, RiotOrderObservationKind.Active, $"ORDER-{upperId}", OrderState: 3, VehicleKey: KeyA, MapId: Map, DestinationStationId: 214));
        await RoundAsync(fleet);

        Assert.Equal("CONFIRMED", (await fleet.Context.OrderIntents.AsNoTracking().SingleAsync(row => row.UpperId == upperId, Token)).Status);
        ProtocolOutboxRow state = await fleet.Context.ProtocolOutbox.AsNoTracking()
            .SingleAsync(row => row.MessageId == ClearanceMoveShape.StateMessageId(journey.JourneyId, 1), Token);
        using JsonDocument line = JsonDocument.Parse(state.PayloadJson);
        Assert.Equal(VehicleActivePurposes.ClearingMaintenance, line.RootElement.GetProperty("payload").GetProperty("activePurpose").GetString());
    }

    /// <summary>
    /// 一趟没收尾的旅程本身就把车挡在每一条承诺路径之外，不靠用途占有（cs#462 的护栏）。车正开往等待点时人工清桩确认了，然后把它的用途强行放掉——
    /// cs#462 之前人工确认就是这么放的；修好之后挡着它的是两样，这里拆掉用途那一样，只留旅程。RIoT 把它读成一辆再空闲不过的车（停着、没有任务号、
    /// 不在任何等待点上），再把派车、途中追加、充电分配、空闲返回各自最想承诺它的条件摆出来。三轮里没有任何一条承诺它：没有第二趟旅程、没有新的
    /// 用途占有、没有新的周期、没有挂上需求、RIoT 上没有新单；之后移动照常收敛、旅程收尾。
    /// </summary>
    /// <remarks>
    /// 派车与充电分配的写入侧只靠用途占有的主键防重复承诺，所以在那两条路上，引擎把有未收尾旅程的车算作 busy 是用途之外唯一的一道；它坏了的后果
    /// 不止是错派：车多出一趟旅程，下一轮整轮抛「More than one unresolved journey」，全车队停摆。空闲返回与途中追加另各有自己的第二道。
    /// </remarks>
    [Theory]
    [InlineData("dispatch")]
    [InlineData("append")]
    [InlineData("charging")]
    [InlineData("idleReturn")]
    public async Task AVehicleWhosePurposeWasReleasedOnTheWayIsCommittedByNothingUntilItsJourneyCloses(string path)
    {
        // A second charger, free and not held: the first one stays held after a clearance, so it alone would refuse the charging
        // path for its own reason.
        await using FleetFixture fleet = await ClearanceFleetAsync(chargers: [Near, Far]);
        JourneyRuntimeRow journey = await ConfirmedWhileDrivingAsync(fleet);
        // Charged enough to take work and to go back to a waiting point; the charging path lowers it again.
        fleet.Riot.BatteryByVehicle[KeyA] = 80;
        switch (path)
        {
            case "dispatch":
                fleet.Catalog.Set([FleetFixture.Demand(0, "N1-1", 0)]);
                break;
            case "append":
                await fleet.AllowEnRouteAppendAsync(1_000_000);
                fleet.Catalog.Set([FleetFixture.Demand(0, "N1-1", 0)]);
                break;
            case "charging":
                fleet.Riot.BatteryByVehicle[KeyA] = 20;
                break;
            case "idleReturn":
                await fleet.EnableIdleReturnAsync(Point214, Point300);
                break;
        }

        for (int round = 0; round < 3; round++)
        {
            await RoundAsync(fleet);
        }

        JourneyRuntimeRow only = Assert.Single(await fleet.Context.JourneyRuntimes.AsNoTracking()
            .Where(row => row.AgvId == AgvA).ToArrayAsync(Token));
        Assert.Equal(journey.JourneyId, only.JourneyId);
        Assert.NotEqual(JourneyRuntimeStage.Completed, only.Stage);
        Assert.Null(await ClaimOfAsync(fleet, KeyA));
        Assert.Single(await fleet.Context.Set<ChargingCycleRow>().AsNoTracking().Where(row => row.VehicleKey == KeyA).ToArrayAsync(Token));
        Assert.Empty(await fleet.Context.Set<JourneyDemandRow>().AsNoTracking().Where(row => row.JourneyId == journey.JourneyId).ToArrayAsync(Token));
        Assert.Equal((KeyA, StationExclusivityStates.Reserved), await HolderAsync(fleet, 214));
        Assert.Equal(2, fleet.Riot.Creates.Count);

        fleet.Riot.CompleteOrder(ClearanceMoveShape.UpperIdFor(journey.JourneyId, 1));
        StandOn(fleet, 214);
        await RoundAsync(fleet);
        Assert.Equal(JourneyRuntimeStage.Completed, (await fleet.Context.JourneyRuntimes.AsNoTracking()
            .SingleAsync(row => row.JourneyId == journey.JourneyId, Token)).Stage);
        Assert.Equal((KeyA, StationExclusivityStates.Occupied), await HolderAsync(fleet, 214));
    }

    /// <summary>
    /// 开往等待点途中人工确认：清桩完成、桩放开，旅程仍开着、等待点仍预占；再把用途强行放掉（cs#462 之前确认那一刻就是这个样子）。然后车在 RIoT
    /// 里读成停着、没有任务号、不在桩上也不在任何等待点上——除了这趟没收尾的旅程，再没有别的东西说它被占着。
    /// </summary>
    private static async Task<JourneyRuntimeRow> ConfirmedWhileDrivingAsync(FleetFixture fleet)
    {
        JourneyRuntimeRow journey = await OldOrderEndedAsync(fleet);
        await RoundAsync(fleet);
        await RoundAsync(fleet);
        fleet.Riot.VehicleOverrides[KeyA] = seen => seen with { CurrentStationId = 300, Speed = 300, ProcState = "EXECUTING", OrderTaskId = "T" };
        ManualStationClearanceConfirmation decision = await ManualClearance(fleet).DecideAsync(Request("00000000-0000-4000-8000-0000000c4621"), Token);
        Assert.Equal((FieldConfirmationDecision.Confirmed, true), (decision.Decision.Outcome, decision.StationReleased));
        Assert.NotEqual(JourneyRuntimeStage.Completed, (await fleet.Context.JourneyRuntimes.AsNoTracking()
            .SingleAsync(row => row.JourneyId == journey.JourneyId, Token)).Stage);
        Assert.Equal((KeyA, StationExclusivityStates.Reserved), await HolderAsync(fleet, 214));

        await JourneyPurposeClaimRelease.StageAsync(
            fleet.Context, journey.JourneyId, fleet.Clock.GetUtcNow(), ChargingExecutionReasons.UnableToChargeCleared, Token);
        await fleet.Context.SaveChangesAsync(Token);
        fleet.Context.ChangeTracker.Clear();
        Assert.Null(await ClaimOfAsync(fleet, KeyA));

        fleet.Riot.VehicleOverrides[KeyA] = seen => seen with { CurrentStationId = 12, BatteryState = "NO_CHARGE" };
        return journey;
    }
}
