using ControlServer.Domain;
using ControlServer.Host.Runtime.IdleReturn;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using FleetFixture = ControlServer.Tests.MultiVehicleExecutionTests.FleetFixture;

namespace ControlServer.Tests;

// control-server#673 review M1: a demand leg's order taken as never created leaves its old intent RESULT_UNKNOWN for good.
public sealed partial class IdleReturnExecutionTests
{
    /// <summary>
    /// 审查 M1（探针 P3）：判作没建成的需求腿旧意图永远停在 <c>RESULT_UNKNOWN</c>、没有单号。它有一条「从没出现过」来源的重建记录，就不再挡这辆车的
    /// 空闲返回——修之前这辆车走完那趟搬运之后再也不空闲返回，只能删意图行解开。没有那条记录的同样一条意图照旧挡（既有用例
    /// <see cref="OnlyTheSettledIdleReturnLegIsLetThroughTheUnknownOrderCheck"/>）。
    /// </summary>
    [Fact]
    public async Task ADemandLegTakenAsNeverCreatedDoesNotKeepTheVehicleFromIdleReturn()
    {
        await using FleetFixture fleet = await FleetAsync(points: [Near, Far]);
        JourneyRuntimeRow journey = await CommittedAndSentAsync(fleet);
        fleet.Riot.CancelOrder(journey.PickupUpperId);
        await RoundAsync(fleet);
        Assert.Equal(JourneyRuntimeStage.Completed, (await IdleJourneyAsync(fleet, AgvA))!.Stage);

        fleet.Context.OrderIntents.Add(new OrderIntentRow
        {
            MovementLegId = "never-created-leg",
            UpperId = "W2G-NEVER-CREATED-PICKUP",
            Purpose = "TO_PICKUP",
            TargetStationId = "N1-1",
            VehicleKey = KeyA,
            MapId = Map,
            DestinationStationId = 12,
            CreatedAt = fleet.Clock.GetUtcNowWithoutTick(),
            Status = "RESULT_UNKNOWN",
            CreateAttemptCount = 1,
        });
        fleet.Context.OwnOrderRebuilds.Add(new OwnOrderRebuildRow
        {
            RebuildId = "never-created-rebuild",
            JourneyId = "journey:never-created",
            DemandId = "never-created-demand",
            AgvId = AgvA,
            VehicleKey = KeyA,
            StopId = "journey:never-created|PICKUP",
            Source = OwnOrderRebuildSources.OrderNeverAppeared,
            EndedUpperId = "W2G-NEVER-CREATED-PICKUP",
            IncidentAt = fleet.Clock.GetUtcNowWithoutTick(),
            RecordedAt = fleet.Clock.GetUtcNowWithoutTick(),
            DueAt = fleet.Clock.GetUtcNowWithoutTick(),
            NewUpperId = "W2G-NEVER-CREATED-PICKUP-R1",
            NewMovementLegId = "never-created-leg-r1",
            State = OwnOrderRebuildStates.Rebuilt,
        });
        await fleet.Context.SaveChangesAsync(Token);
        fleet.Context.ChangeTracker.Clear();

        await fleet.HearFromEveryVehicleAsync();
        await fleet.RunRoundAsync(fleet.Options.OwnOrderRebuildDelay);
        await RoundAsync(fleet);

        Assert.Equal(IdleReturnReasons.Committed, fleet.IdleReturnBoard.Reasons[AgvA]);
        Assert.NotNull(await ClaimOfAsync(fleet, KeyA));
    }
}
