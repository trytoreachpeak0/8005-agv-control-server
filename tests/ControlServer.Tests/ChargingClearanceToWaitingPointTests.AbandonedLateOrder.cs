using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime.Charging;
using ControlServer.Host.Runtime.ForeignOrders;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using static ControlServer.Tests.ChargingAllocationTests;
using static ControlServer.Tests.ChargingExecutionTests;
using static ControlServer.Tests.ChargingUnableToChargeTests;
using FleetFixture = ControlServer.Tests.MultiVehicleExecutionTests.FleetFixture;

namespace ControlServer.Tests;

// control-server#611: the late order of a clearance move given up as never having appeared, a gap open since control-server#409.
public sealed partial class ChargingClearanceToWaitingPointTests
{
    /// <summary>
    /// 清桩移动单结果未知、RIoT 一直查无此单，被放弃（预占以 <see cref="ClearanceMoveReleaseReasons.NeverAppeared"/> 放掉、停靠移除）；之后那张单才在
    /// RIoT 上跑到这辆车上。修前监督器查到订单意图就当本服务端自有单不管，没有任何东西看着它。现在认作「归属证明不了」：挡车不接新活、告警交人
    /// （事件 2181），不取消——<c>REQ-0148</c> 情形二只授权取消被放弃的充电单。放弃之前同一张单在跑，是自有在途单，监督器不碰。
    /// </summary>
    [Fact]
    public async Task AnAbandonedClearanceMoveThatTurnsUpRunningHoldsTheVehicleForAPersonAndIsNeverCancelled()
    {
        await using FleetFixture fleet = await ClearanceFleetAsync(points: [Point214, Point300]);
        EventRecordingLogger<ForeignRunningOrderSupervisor> log = new();
        JourneyRuntimeRow journey = await OldOrderEndedAsync(fleet);
        await RoundAsync(fleet);
        fleet.Riot.AnswersAbsentAsRealRiot = true;
        fleet.Riot.CreateAnswer = intent => fleet.Riot.RealRiotAbsent(intent.UpperId);
        await RoundAsync(fleet);
        fleet.Riot.CreateAnswer = null;
        string upperId = ClearanceMoveShape.UpperIdFor(journey.JourneyId, 1);
        RiotOrderObservation running = new(
            upperId, RiotOrderObservationKind.Active, "ORDER-CLR-LATE", RiotOrderState.Executing, KeyA, Map, 300);

        // While the move is held, the order running would be this server's own: left alone.
        fleet.Riot.PutOrder(running);
        await ClearanceSupervisorOf(fleet, log).SuperviseAsync(Token);
        fleet.Context.ChangeTracker.Clear();
        Assert.Empty(await fleet.Context.ForeignRiotOrders.AsNoTracking().ToArrayAsync(Token));

        fleet.Riot.PutOrder(fleet.Riot.RealRiotAbsent(upperId));
        for (int round = 0; round < 6; round++)
        {
            await fleet.HearFromEveryVehicleAsync();
            await fleet.RunRoundAsync(TimeSpan.FromSeconds(30));
        }
        Assert.Single(await fleet.Context.Set<StationExclusivityRecordRow>().AsNoTracking()
            .Where(row => row.StationId == 300 && row.ReleaseReason == ClearanceMoveReleaseReasons.NeverAppeared).ToArrayAsync(Token));

        fleet.Riot.PutOrder(running);
        fleet.Context.ChangeTracker.Clear();
        await ClearanceSupervisorOf(fleet, log).SuperviseAsync(Token);
        fleet.Context.ChangeTracker.Clear();
        await ClearanceSupervisorOf(fleet, log).SuperviseAsync(Token);
        fleet.Context.ChangeTracker.Clear();

        ForeignRiotOrderRow row = await fleet.Context.ForeignRiotOrders.AsNoTracking()
            .SingleAsync(item => item.RiotOrderId == "ORDER-CLR-LATE", Token);
        Assert.Equal(
            (upperId, AgvA, ForeignRiotOrderOwnership.Unproven, AbandonedClearanceMoves.OwnershipBasis),
            (row.UpperId, row.AgvId, row.Ownership, row.OwnershipBasis));
        Assert.DoesNotContain(fleet.Riot.OrderCommands, command => command.OrderId == "ORDER-CLR-LATE");
        Assert.Contains(AgvA, await ForeignRunningOrders.HeldAgvIdsAsync(fleet.Context, Token));
        Assert.Contains(
            log.Entries, entry => entry.EventId.Id == 2181 && entry.Message.Contains("ORDER-CLR-LATE", StringComparison.Ordinal));
    }

    private static ForeignRunningOrderSupervisor ClearanceSupervisorOf(
        FleetFixture fleet, EventRecordingLogger<ForeignRunningOrderSupervisor> log) =>
        new(
            fleet.Context,
            fleet.Riot,
            fleet.Riot,
            new RiotOrderCommandAuditStore(fleet.Context),
            new ControlServer.Host.Runtime.Fleet.VehicleRoster(Microsoft.Extensions.Options.Options.Create(fleet.Options)),
            Microsoft.Extensions.Options.Options.Create(fleet.Options),
            Microsoft.Extensions.Options.Options.Create(new RiotForeignOrderCancelOptions()),
            fleet.Clock,
            log);
}
