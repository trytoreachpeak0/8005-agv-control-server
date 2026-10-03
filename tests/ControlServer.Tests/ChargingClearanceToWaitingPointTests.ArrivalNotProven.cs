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
using static ControlServer.Tests.WaitingPointArrivalSettlementTestKit;
using FleetFixture = ControlServer.Tests.MultiVehicleExecutionTests.FleetFixture;

namespace ControlServer.Tests;

// control-server#447: the clearance move to a waiting point ends SUCCESS in RIoT, and the vehicle half of the arrival proof is
// never satisfied.
public sealed partial class ChargingClearanceToWaitingPointTests
{
    public static TheoryData<string> ArrivalNeverProven => ["other-map", "other-station"];

    /// <summary>
    /// control-server#447 第 1 步的复现（清桩开往等待点那一条），修复之后的样子：清桩移动单 RIoT 报 <c>SUCCESS</c>，车停稳、没单，但到点证据的车辆那一半
    /// 始终不满足。码是 <c>CHARGING_CLEARANCE_ARRIVAL_NOT_PROVEN</c>，告警只一次；两个小时也不按时间放车放点放桩；#419 的人工释放与故障恢复入口仍然拒绝。
    /// 出口是到点人工收尾，见下面两条。
    /// </summary>
    [Theory]
    [MemberData(nameof(ArrivalNeverProven))]
    public async Task AClearanceMoveWhoseArrivalIsNeverProvenIsHeldAndNeverReleasedByTime(string why)
    {
        await using FleetFixture fleet = await ClearanceFleetAsync();
        JourneyRuntimeRow journey = await MoveSucceededWithoutArrivalAsync(fleet, why);

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

        fleet.Context.ChangeTracker.Clear();
        StationExclusivityManualReleaseResult release = await StationExclusivityManualRelease.ReleaseAsync(
            fleet.Context,
            new GovernanceStore(fleet.Context, new GovernanceDeploymentIdentity("deployment:8005-controlserver@test"), AuditRetentionPolicy.Default),
            fleet.Riot,
            new StationExclusivityManualReleaseRequest(Map, 214, KeyA, "OP-7", "到点证明不了", "SITE-447", "班长"),
            fleet.Clock.GetUtcNowWithoutTick(),
            Token);
        fleet.Context.ChangeTracker.Clear();
        Assert.Contains(StationExclusivityManualRelease.HolderJourneyStillBound, release.Codes);
        VehicleFaultRecoveryDecision recovery = await fleet.CreateFaultRecovery().RecoverAsync(
            new VehicleFaultRecoveryRequest(
                new EmergencyStopSubject(AgvA, KeyA), VehicleFaultRecoveryAction.ClearFault, "operator-1", true, null),
            Token);
        fleet.Context.ChangeTracker.Clear();
        Assert.Equal(VehicleFaultRecoveryOutcome.Refused, recovery.Outcome);
    }

    /// <summary>
    /// 人说车就在等待点上（地图对不上的那种）：清桩还没完成时拒绝（<c>ARRIVAL_SETTLEMENT_CLEARANCE_STILL_OPEN</c>，清桩的完成要 R-11／R-13 的那一次确认，
    /// 这里不替它做），留一条失败审计。人工清桩确认之后再办：与引擎到点完成的后一半同一套——等待点转占用、两个停靠完成、被取代的途中快照退役、旅程以清桩
    /// 收尾码收尾，收尾计划留那条 <c>ARRIVED</c> 的等待点腿、业务状态撤下用途；成功审计一条。
    /// </summary>
    [Fact]
    public async Task AtTheWaitingPointClosesTheChargingJourneyOnceAPersonHasClearedTheCharger()
    {
        await using FleetFixture fleet = await ClearanceFleetAsync();
        JourneyRuntimeRow journey = await MoveSucceededWithoutArrivalAsync(fleet, "other-map");
        fleet.Clock.Advance(fleet.Options.OwnOrderRebuildRepeatWindow + TimeSpan.FromSeconds(1));
        WaitingPointArrivalSettlementRequest request = SettlementRequest(AgvA, KeyA, journey.JourneyId, 214, WaitingPointArrivalVerdicts.AtWaitingPoint);

        WaitingPointArrivalSettlementResult early = await SettleAsync(fleet, request);
        Assert.Equal([WaitingPointArrivalSettlement.ClearanceStillOpen], early.Codes);
        Assert.Equal((KeyA, StationExclusivityStates.Reserved), await HolderAsync(fleet, 214));

        ManualStationClearanceConfirmation cleared =
            await ManualClearance(fleet).DecideAsync(Request("00000000-0000-4000-8000-0000000c4471"), Token);
        Assert.Equal((FieldConfirmationDecision.Confirmed, true), (cleared.Decision.Outcome, cleared.StationReleased));
        fleet.Context.ChangeTracker.Clear();

        WaitingPointArrivalSettlementResult result = await SettleAsync(fleet, request);

        Assert.Equal((true, ChargingExecutionReasons.UnableToChargeCleared), (result.Settled, result.Ending));
        JourneyRuntimeRow closed = await fleet.Context.JourneyRuntimes.AsNoTracking().SingleAsync(row => row.JourneyId == journey.JourneyId, Token);
        Assert.Equal((JourneyRuntimeStage.Completed, ChargingExecutionReasons.UnableToChargeCleared), (closed.Stage, closed.BlockReasonCode));
        Assert.Equal((KeyA, StationExclusivityStates.Occupied), await HolderAsync(fleet, 214));
        Assert.All(
            await fleet.Context.Set<JourneyStopRow>().AsNoTracking().Where(row => row.JourneyId == journey.JourneyId).ToArrayAsync(Token),
            stop => Assert.Equal(JourneyStopStatuses.Completed, stop.Status));
        Assert.Null(await ClaimOfAsync(fleet, KeyA));
        string[] moveSnapshots = [.. ClearanceMoveShape.SnapshotIds(journey.JourneyId, 1)];
        Assert.Empty(await fleet.Context.ProtocolOutbox.AsNoTracking()
            .Where(row => moveSnapshots.Contains(row.MessageId) && row.AcknowledgedAt == null && row.FencedAt == null)
            .ToArrayAsync(Token));
        JsonElement arrived = Assert.Single((await PayloadsAsync(fleet, AgvA, "UpcomingStopPlanSnapshot"))[^1].GetProperty("legs").EnumerateArray());
        Assert.Equal((JourneyPlanBuilder.WaitingPointStopPurpose, "ARRIVED"),
            (arrived.GetProperty("stopPurposeCategory").GetString(), arrived.GetProperty("state").GetString()));
        Assert.Equal(JsonValueKind.Null, (await PayloadsAsync(fleet, AgvA, "VehicleBusinessStateSnapshot"))[^1].GetProperty("activePurpose").ValueKind);
        Assert.Equal(
            [GovernanceActionOutcome.Failed, GovernanceActionOutcome.Succeeded],
            (await AuditsAsync(fleet)).Select(row => row.Outcome));
        Assert.Equal(2, fleet.Riot.Creates.Count);
        Assert.Empty(fleet.Riot.OrderCommands);

        await RoundAsync(fleet);
        Assert.Equal((KeyA, StationExclusivityStates.Occupied), await HolderAsync(fleet, 214));
    }

    /// <summary>
    /// 人说车不在等待点上（停在别的站的那种）：与「单被人结束、车证明停稳」同一套——这次移动结束，等待点预占当场放（原因 <c>Ended</c>），停靠回到原桩，
    /// 码是被人结束的码，这个周期不再自动出发；之后人工清桩照常完成并收尾。
    /// </summary>
    [Fact]
    public async Task NotAtTheWaitingPointEndsTheMoveAndOnlyAManualClearanceIsLeft()
    {
        await using FleetFixture fleet = await ClearanceFleetAsync();
        JourneyRuntimeRow journey = await MoveSucceededWithoutArrivalAsync(fleet, "other-station");
        fleet.Clock.Advance(fleet.Options.OwnOrderRebuildRepeatWindow + TimeSpan.FromSeconds(1));

        WaitingPointArrivalSettlementResult result = await SettleAsync(
            fleet, SettlementRequest(AgvA, KeyA, journey.JourneyId, 214, WaitingPointArrivalVerdicts.NotAtWaitingPoint));

        Assert.Equal((true, ChargingExecutionReasons.ClearanceMoveEnded), (result.Settled, result.Ending));
        Assert.Null(await StationAsync(fleet, 214));
        Assert.Single(await fleet.Context.Set<StationExclusivityRecordRow>().AsNoTracking()
            .Where(row => row.StationId == 214 && row.ReleaseReason == ClearanceMoveReleaseReasons.Ended).ToArrayAsync(Token));
        JourneyStopRow move = Assert.Single(await ClearanceStopsAsync(fleet, journey));
        Assert.Equal(JourneyStopStatuses.Removed, move.Status);
        Assert.Equal(ChargingExecutionReasons.ClearanceMoveEnded, (await ChargingJourneyAsync(fleet, AgvA))!.BlockReasonCode);
        Assert.Equal(GovernanceActionOutcome.Succeeded, Assert.Single(await AuditsAsync(fleet)).Outcome);

        for (int round = 0; round < 5; round++)
        {
            await RoundAsync(fleet);
        }
        // The point is free again and this clearing does not set off for it, nor for any other, by itself.
        Assert.Equal(2, fleet.Riot.Creates.Count);
        Assert.Null(await StationAsync(fleet, 214));

        ManualStationClearanceConfirmation cleared =
            await ManualClearance(fleet).DecideAsync(Request("00000000-0000-4000-8000-0000000c4472"), Token);
        Assert.Equal((FieldConfirmationDecision.Confirmed, true), (cleared.Decision.Outcome, cleared.StationReleased));
        await RoundAsync(fleet);
        Assert.Equal(JourneyRuntimeStage.Completed, (await fleet.Context.JourneyRuntimes.AsNoTracking()
            .SingleAsync(row => row.JourneyId == journey.JourneyId, Token)).Stage);
    }

    /// <summary>
    /// 先人工清桩、再办「车不在点上」（独立审查 S2，探针 P3）：清桩已完成，这次移动结束、等待点当场释放，码是清桩收尾码；旅程下一轮自己收尾，
    /// 不再建单。那一轮里旅程还开着、带着 <c>CHARGING_UNABLE_TO_CHARGE_CLEARED</c>，所以它要有现场说明（夹具释放时的看板守卫核）。
    /// </summary>
    [Fact]
    public async Task NotAtTheWaitingPointAfterAManualClearanceClosesTheJourneyNextRound()
    {
        await using FleetFixture fleet = await ClearanceFleetAsync();
        JourneyRuntimeRow journey = await MoveSucceededWithoutArrivalAsync(fleet, "other-station");
        fleet.Clock.Advance(fleet.Options.OwnOrderRebuildRepeatWindow + TimeSpan.FromSeconds(1));
        ManualStationClearanceConfirmation cleared =
            await ManualClearance(fleet).DecideAsync(Request("00000000-0000-4000-8000-0000000c4479"), Token);
        Assert.Equal(FieldConfirmationDecision.Confirmed, cleared.Decision.Outcome);
        fleet.Context.ChangeTracker.Clear();

        WaitingPointArrivalSettlementResult result = await SettleAsync(
            fleet, SettlementRequest(AgvA, KeyA, journey.JourneyId, 214, WaitingPointArrivalVerdicts.NotAtWaitingPoint));

        Assert.True(result.Settled, string.Join(",", result.Codes));
        Assert.Equal(ChargingExecutionReasons.UnableToChargeCleared, result.Ending);
        Assert.Null(await StationAsync(fleet, 214));
        for (int round = 0; round < 5; round++)
        {
            await RoundAsync(fleet);
        }
        JourneyRuntimeRow after = await fleet.Context.JourneyRuntimes.AsNoTracking().SingleAsync(row => row.JourneyId == journey.JourneyId, Token);
        Assert.Equal(JourneyRuntimeStage.Completed, after.Stage);
        Assert.Equal(2, fleet.Riot.Creates.Count);
        Assert.Null(await StationAsync(fleet, 214));
        Assert.Null(await ClaimOfAsync(fleet, KeyA));
    }

    /// <summary>
    /// 清桩那一支的前提用的是它自己的码与它自己的那一次移动：还不到时限、单还在走、车离线、这次移动已不在进行，都拒绝，什么也不动，留一条失败审计。
    /// </summary>
    [Theory]
    [InlineData("too-early", WaitingPointArrivalSettlement.TooEarly)]
    [InlineData("order-not-terminal", WaitingPointArrivalSettlement.OrderNotExactSuccess)]
    [InlineData("vehicle-offline", WaitingPointArrivalSettlement.VehicleOffline)]
    [InlineData("move-not-in-progress", WaitingPointArrivalSettlement.NotAWaitingPointMove)]
    public async Task EachUnmetPremiseRefusesTheClearanceSettlementAndChangesNothing(string premise, string code)
    {
        await using FleetFixture fleet = await ClearanceFleetAsync();
        JourneyRuntimeRow journey = await MoveSucceededWithoutArrivalAsync(fleet, "other-station");
        string upperId = ClearanceMoveShape.UpperIdFor(journey.JourneyId, 1);
        if (premise != "too-early")
        {
            fleet.Clock.Advance(fleet.Options.OwnOrderRebuildRepeatWindow + TimeSpan.FromSeconds(1));
        }
        switch (premise)
        {
            case "order-not-terminal":
                fleet.Riot.PutOrder(fleet.Riot.OrderOf(upperId)! with { Kind = RiotOrderObservationKind.Active, OrderState = RiotOrderState.Executing });
                break;
            case "vehicle-offline":
                fleet.Riot.VehicleOverrides[KeyA] = seen => seen with { CurrentStationId = 12, BatteryState = "NO_CHARGE", Connected = false };
                break;
            case "move-not-in-progress":
                await using (ControlServerDbContext other = fleet.NewContext())
                {
                    JourneyStopRow stop = await other.Set<JourneyStopRow>().SingleAsync(row => row.UpperId == upperId, Token);
                    stop.Status = JourneyStopStatuses.Removed;
                    await other.SaveChangesAsync(Token);
                }
                break;
        }

        WaitingPointArrivalSettlementResult result = await SettleAsync(
            fleet, SettlementRequest(AgvA, KeyA, journey.JourneyId, 214, WaitingPointArrivalVerdicts.NotAtWaitingPoint));

        Assert.False(result.Settled);
        Assert.Contains(code, result.Codes);
        Assert.Equal((KeyA, StationExclusivityStates.Reserved), await HolderAsync(fleet, 214));
        Assert.NotEqual(JourneyRuntimeStage.Completed, (await ChargingJourneyAsync(fleet, AgvA))!.Stage);
        Assert.Equal(GovernanceActionOutcome.Failed, Assert.Single(await AuditsAsync(fleet)).Outcome);
    }

    /// <summary>旧单结束、清桩移动建单确认、RIoT 报移动单 SUCCESS，车停稳没单但到点证据的车辆那一半不满足；跑一轮，引擎写上到点证明不了的码。</summary>
    private static async Task<JourneyRuntimeRow> MoveSucceededWithoutArrivalAsync(FleetFixture fleet, string why)
    {
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
        Assert.Equal(ChargingExecutionReasons.ClearanceArrivalNotProven, (await ChargingJourneyAsync(fleet, AgvA))!.BlockReasonCode);
        return journey;
    }
}
