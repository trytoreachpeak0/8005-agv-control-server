using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime.Charging;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using static ControlServer.Tests.ChargingAllocationTests;
using static ControlServer.Tests.ChargingExecutionTests;
using static ControlServer.Tests.ChargingUnableToChargeTests;
using FleetFixture = ControlServer.Tests.MultiVehicleExecutionTests.FleetFixture;

namespace ControlServer.Tests;

// control-server#462: a manual clearance confirmed while the vehicle drives to its waiting point releases the vehicle's
// CLEARING_MAINTENANCE purpose there and then (ChargerClearanceRelease), while the journey stays open until the move
// converges. These cases put every path that commits a vehicle in front of that vehicle and show none takes it.
public sealed partial class ChargingClearanceToWaitingPointTests
{
    /// <summary>
    /// 车正开往等待点时人工清桩确认了：用途已放、旅程未收尾、等待点仍预占。然后 RIoT 把它读成一辆再空闲不过的车——停着、没有任务号、低电、
    /// 不在任何等待点上——再把派车、途中追加、充电分配、空闲返回各自最想承诺它的条件摆出来。三轮里没有任何一条承诺它：没有第二趟旅程、
    /// 没有新的用途占有、没有新的周期、没有挂上需求、RIoT 上没有新单；之后移动照常收敛、旅程收尾。
    /// </summary>
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
    /// 开往等待点途中人工确认：清桩完成、桩放开、用途放开，旅程仍开着、等待点仍预占（cs#462 要回答的那个中间态）。然后车在 RIoT 里读成停着、
    /// 没有任务号、不在桩上也不在任何等待点上——除了这趟没收尾的旅程，再没有别的东西说它被占着。
    /// </summary>
    private static async Task<JourneyRuntimeRow> ConfirmedWhileDrivingAsync(FleetFixture fleet)
    {
        JourneyRuntimeRow journey = await OldOrderEndedAsync(fleet);
        await RoundAsync(fleet);
        await RoundAsync(fleet);
        fleet.Riot.VehicleOverrides[KeyA] = seen => seen with { CurrentStationId = 300, Speed = 300, ProcState = "EXECUTING", OrderTaskId = "T" };
        ManualStationClearanceConfirmation decision = await ManualClearance(fleet).DecideAsync(Request("00000000-0000-4000-8000-0000000c4621"), Token);
        Assert.Equal((FieldConfirmationDecision.Confirmed, true), (decision.Decision.Outcome, decision.StationReleased));

        Assert.Null(await ClaimOfAsync(fleet, KeyA));
        Assert.NotEqual(JourneyRuntimeStage.Completed, (await fleet.Context.JourneyRuntimes.AsNoTracking()
            .SingleAsync(row => row.JourneyId == journey.JourneyId, Token)).Stage);
        Assert.Equal((KeyA, StationExclusivityStates.Reserved), await HolderAsync(fleet, 214));

        fleet.Riot.VehicleOverrides[KeyA] = seen => seen with { CurrentStationId = 12, BatteryState = "NO_CHARGE" };
        return journey;
    }
}
