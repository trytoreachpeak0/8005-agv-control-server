using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime.Commands;
using ControlServer.Host.Runtime.Faults;
using ControlServer.Host.Runtime.IdleReturn;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using FleetFixture = ControlServer.Tests.MultiVehicleExecutionTests.FleetFixture;

namespace ControlServer.Tests;

// control-server#447: the idle return's waiting-point move ends SUCCESS in RIoT, and the vehicle half of the arrival proof is
// never satisfied.
public sealed partial class IdleReturnExecutionTests
{
    public static TheoryData<string> ArrivalNeverProven => ["other-map", "other-station"];

    /// <summary>
    /// 复现 control-server#447（空闲返回那一条）：RIoT 报单 <c>SUCCESS</c>，车停稳、没单，但到点证据的车辆那一半始终不满足——当前图与旅程的图对不上
    /// （数据本身不一致），或车停在了别的站。两个小时之后：旅程仍在途、用途占有与等待点预占仍在，旅程上没有任何码；#419 的人工释放以
    /// <c>HOLDER_JOURNEY_STILL_BOUND</c> 拒绝，故障人工恢复入口没有故障可清而拒绝。今天没有受治理的出口。
    /// </summary>
    [Theory]
    [MemberData(nameof(ArrivalNeverProven))]
    public async Task AnIdleReturnWhoseArrivalIsNeverProvenAfterSuccessHasNoGovernedExitToday(string why)
    {
        await using FleetFixture fleet = await FleetAsync();
        JourneyRuntimeRow journey = await CommittedAndSentAsync(fleet);
        fleet.Riot.CompleteOrder(journey.PickupUpperId);
        fleet.Riot.VehicleOverrides[KeyA] = why switch
        {
            "other-map" => seen => seen with { CurrentStationId = Near.StationId, CurrentMap = "map:not-the-journeys" },
            _ => seen => seen with { CurrentStationId = 12 },
        };

        await RoundAsync(fleet);
        fleet.Clock.Advance(TimeSpan.FromHours(2));
        await RoundAsync(fleet);
        await RoundAsync(fleet);

        JourneyRuntimeRow stuck = (await IdleJourneyAsync(fleet, AgvA))!;
        Assert.Equal((JourneyRuntimeStage.AwaitingPickupArrival, (string?)null), (stuck.Stage, stuck.BlockReasonCode));
        Assert.Equal((VehiclePurposes.IdleReturn, journey.JourneyId), await ClaimOfAsync(fleet, KeyA));
        Assert.Equal((KeyA, StationExclusivityStates.Reserved), await HolderAsync(fleet, Near.StationId));

        // #419's manual release: refused because the journey still has the point as an unfinished stop.
        fleet.Context.ChangeTracker.Clear();
        StationExclusivityManualReleaseResult release = await StationExclusivityManualRelease.ReleaseAsync(
            fleet.Context,
            new GovernanceStore(fleet.Context, new GovernanceDeploymentIdentity("deployment:8005-controlserver@test"), AuditRetentionPolicy.Default),
            fleet.Riot,
            new StationExclusivityManualReleaseRequest(Map, Near.StationId, KeyA, "OP-7", "到点证明不了", "SITE-447", "班长"),
            fleet.Clock.GetUtcNowWithoutTick(),
            Token);
        fleet.Context.ChangeTracker.Clear();
        Assert.False(release.Released);
        Assert.Contains(StationExclusivityManualRelease.HolderJourneyStillBound, release.Codes);

        // The fault recovery entry: there is no fault to clear.
        VehicleFaultRecoveryDecision recovery = await fleet.CreateFaultRecovery().RecoverAsync(
            new VehicleFaultRecoveryRequest(
                new EmergencyStopSubject(AgvA, KeyA), VehicleFaultRecoveryAction.ClearFault, "operator-1", true, null),
            Token);
        fleet.Context.ChangeTracker.Clear();
        Assert.Equal(VehicleFaultRecoveryOutcome.Refused, recovery.Outcome);

        await RoundAsync(fleet);
        Assert.Equal(JourneyRuntimeStage.AwaitingPickupArrival, (await IdleJourneyAsync(fleet, AgvA))!.Stage);
        Assert.Equal((VehiclePurposes.IdleReturn, journey.JourneyId), await ClaimOfAsync(fleet, KeyA));
        Assert.Equal((KeyA, StationExclusivityStates.Reserved), await HolderAsync(fleet, Near.StationId));
    }
}
