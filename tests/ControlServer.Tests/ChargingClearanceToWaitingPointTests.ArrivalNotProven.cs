using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime.Charging;
using ControlServer.Host.Runtime.Commands;
using ControlServer.Host.Runtime.Faults;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using static ControlServer.Tests.ChargingAllocationTests;
using static ControlServer.Tests.ChargingExecutionTests;
using FleetFixture = ControlServer.Tests.MultiVehicleExecutionTests.FleetFixture;

namespace ControlServer.Tests;

// control-server#447: the clearance move to a waiting point ends SUCCESS in RIoT, and the vehicle half of the arrival proof is
// never satisfied.
public sealed partial class ChargingClearanceToWaitingPointTests
{
    public static TheoryData<string> ArrivalNeverProven => ["other-map", "other-station"];

    /// <summary>
    /// 复现 control-server#447（清桩开往等待点那一条）：清桩移动单 RIoT 报 <c>SUCCESS</c>，车停稳、没单，但到点证据的车辆那一半始终不满足。两个小时之后：
    /// 旅程没收尾，码是 <c>CHARGING_CLEARANCE_ARRIVAL_NOT_PROVEN</c>、告警只一次（这一半今天已有），等待点预占、用途占有、桩都还在；#419 的人工释放以
    /// <c>HOLDER_JOURNEY_STILL_BOUND</c> 拒绝，故障人工恢复入口无故障可清而拒绝；人工清桩完成了清桩、放了桩、放了清桩用途，但旅程照样收不了尾，等待点仍是预占，新需求也不派给它。
    /// 今天没有受治理的出口。
    /// </summary>
    [Theory]
    [MemberData(nameof(ArrivalNeverProven))]
    public async Task AClearanceMoveWhoseArrivalIsNeverProvenAfterSuccessHasNoGovernedExitToday(string why)
    {
        await using FleetFixture fleet = await ClearanceFleetAsync();
        JourneyRuntimeRow journey = await OldOrderEndedAsync(fleet);
        await RoundAsync(fleet);
        await RoundAsync(fleet);
        fleet.Riot.CompleteOrder(ClearanceMoveShape.UpperIdFor(journey.JourneyId, 1));
        fleet.Riot.VehicleOverrides[KeyA] = why switch
        {
            "other-map" => seen => seen with { CurrentStationId = 214, CurrentMap = "map:not-the-journeys", BatteryState = "NO_CHARGE" },
            _ => seen => seen with { CurrentStationId = 12, BatteryState = "NO_CHARGE" },
        };

        await RoundAsync(fleet);
        fleet.Clock.Advance(TimeSpan.FromHours(2));
        await RoundAsync(fleet);
        await RoundAsync(fleet);

        JourneyRuntimeRow stuck = (await ChargingJourneyAsync(fleet, AgvA))!;
        Assert.NotEqual(JourneyRuntimeStage.Completed, stuck.Stage);
        Assert.Equal(ChargingExecutionReasons.ClearanceArrivalNotProven, stuck.BlockReasonCode);
        Assert.Single(fleet.EngineLog.Entries, entry => entry.EventId.Id == 2303 && entry.Message.Contains("ARRIVAL_NOT_PROVEN", StringComparison.Ordinal));
        Assert.Equal((KeyA, StationExclusivityStates.Reserved), await HolderAsync(fleet, 214));
        Assert.Equal(journey.JourneyId, (await ClaimOfAsync(fleet, KeyA))?.JourneyId);
        Assert.Equal(KeyA, (await HolderAsync(fleet, Near.StationId))?.VehicleKey);

        // #419's manual release of the waiting point: refused, the journey still has it as an unfinished stop.
        fleet.Context.ChangeTracker.Clear();
        StationExclusivityManualReleaseResult release = await StationExclusivityManualRelease.ReleaseAsync(
            fleet.Context,
            new GovernanceStore(fleet.Context, new GovernanceDeploymentIdentity("deployment:8005-controlserver@test"), AuditRetentionPolicy.Default),
            fleet.Riot,
            new StationExclusivityManualReleaseRequest(Map, 214, KeyA, "OP-7", "到点证明不了", "SITE-447", "班长"),
            fleet.Clock.GetUtcNowWithoutTick(),
            Token);
        fleet.Context.ChangeTracker.Clear();
        Assert.False(release.Released);
        Assert.Contains(StationExclusivityManualRelease.HolderJourneyStillBound, release.Codes);

        // The fault recovery entry: no fault to clear.
        VehicleFaultRecoveryDecision recovery = await fleet.CreateFaultRecovery().RecoverAsync(
            new VehicleFaultRecoveryRequest(
                new EmergencyStopSubject(AgvA, KeyA), VehicleFaultRecoveryAction.ClearFault, "operator-1", true, null),
            Token);
        fleet.Context.ChangeTracker.Clear();
        Assert.Equal(VehicleFaultRecoveryOutcome.Refused, recovery.Outcome);

        // A manual station clearance completes the clearing and frees the charger, and closes nothing else.
        ManualStationClearanceConfirmation cleared =
            await ManualClearance(fleet).DecideAsync(Request("00000000-0000-4000-8000-0000000c4471"), Token);
        Assert.Equal((FieldConfirmationDecision.Confirmed, true), (cleared.Decision.Outcome, cleared.StationReleased));
        fleet.Clock.Advance(TimeSpan.FromHours(1));
        await RoundAsync(fleet);
        await RoundAsync(fleet);

        Assert.NotEqual(
            JourneyRuntimeStage.Completed,
            (await fleet.Context.JourneyRuntimes.AsNoTracking().SingleAsync(row => row.JourneyId == journey.JourneyId, Token)).Stage);
        // The clearing purpose went with the manual clearance; the journey did not. A demand is not given to it either.
        Assert.Null(await ClaimOfAsync(fleet, KeyA));
        fleet.Catalog.Set([FleetFixture.Demand(0, "N1-1", 0)]);
        await RoundAsync(fleet);
        await RoundAsync(fleet);
        Assert.Equal(
            [journey.JourneyId],
            await fleet.Context.JourneyRuntimes.AsNoTracking().Where(row => row.AgvId == AgvA).Select(row => row.JourneyId).ToArrayAsync(Token));
        Assert.Equal((KeyA, StationExclusivityStates.Reserved), await HolderAsync(fleet, 214));
    }
}
