using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Commands;
using ControlServer.Host.Runtime.Dispatch;
using ControlServer.Host.Runtime.Faults;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Tests;

/// <summary>
/// control-server#487: one vehicle whose advance throws something other than an unavailable Onboard connection every
/// round no longer stops the round for the vehicles behind it or for the dispatch round.
/// </summary>
public sealed partial class MultiVehicleExecutionTests
{
    /// <summary>
    /// The first vehicle's advance throws every round (its order read); the second vehicle's leg order goes FAILED while it is still moving.
    /// The second vehicle's fault is still recorded, held and escalated to an emergency stop, the dispatch round still runs,
    /// and the failing vehicle is not offered to it.
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-0246")]
    public async Task AVehicleWhoseAdvanceThrowsEveryRoundDoesNotStopTheOthersSupervisionOrTheDispatchRound()
    {
        await using FleetFixture fixture = await FleetFixture.CreateAsync();
        fixture.Riot.MovementState = "MT_FINISHED";
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        string failing = FleetFixture.AgvIds[0];
        string failingOrder = (await fixture.JourneyOfAsync(failing)).PickupUpperId;
        JourneyRuntimeRow stopped = await fixture.JourneyOfAsync(FleetFixture.AgvIds[1]);
        // Not a connection failure and not caught on the way: the shape of control-server#291's every-round throw.
        fixture.Riot.BeforeReconcile = upperId => upperId == failingOrder
            ? throw new InvalidOperationException("Injected: this vehicle's advance throws every round.")
            : Task.CompletedTask;
        fixture.Riot.MovementState = "MT_RUNNING";
        fixture.Riot.FailOrder(stopped.PickupUpperId);
        int outcomesBefore = fixture.RoundOutcomes.Outcomes.Count;

        for (int round = 0; round < 2; round++)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.RunRoundAsync(TimeSpan.FromSeconds(1)));
        }

        VehicleFaultStateRow fault = Assert.Single(
            await fixture.Context.VehicleFaultStates.AsNoTracking().ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal(
            (FleetFixture.AgvIds[1], VehicleFaultEvidence.OrderFailed, true),
            (fault.AgvId, fault.EvidenceCode, fault.EscalatedAt is not null));
        Assert.Contains(
            fixture.Riot.EmergencyCommands,
            command => command.CommandType == RiotCommandTypeNames.TriggerEmergency &&
                       command.DeviceKey == FleetFixture.VehicleKeys[1]);

        Assert.Equal(outcomesBefore + 2, fixture.RoundOutcomes.Outcomes.Count);
        Assert.All(
            fixture.RoundOutcomes.Outcomes.Skip(outcomesBefore),
            outcome => Assert.DoesNotContain(outcome.CompletedVehicles, vehicle => vehicle.AgvId == failing));
        Assert.Equal(
            JourneyRuntimeEngine.AdvanceFailedReason,
            (await fixture.JourneyOfAsync(failing)).BlockReasonCode);
    }
}
