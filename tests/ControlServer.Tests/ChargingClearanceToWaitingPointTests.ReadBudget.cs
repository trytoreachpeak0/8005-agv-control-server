using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Charging;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using static ControlServer.Tests.ChargingAllocationTests;
using static ControlServer.Tests.ChargingExecutionTests;
using FleetFixture = ControlServer.Tests.MultiVehicleExecutionTests.FleetFixture;

namespace ControlServer.Tests;

/// <summary>
/// control-server#799 审查 M-1：清桩移动里几处读车被预算（<c>JourneyRuntime:RiotVehicleReadBudget</c>）截断时走哪一支。预算 200 毫秒，替身挂 60 秒、
/// 挂满判失败，所以去掉对应那一处的预算，用例就红。
/// </summary>
public sealed partial class ChargingClearanceToWaitingPointTests
{
    /// <summary>
    /// 开往等待点途中人工确认已记下、清桩还没完成（确认那一刻旧单又被读成 <c>HANG</c>），之后旧单取消：完成前复核读车（<c>ReadVehicleOrNullAsync</c>）
    /// 超时，按读不到处理——不算「车还在原桩上」，按人的确认完成清桩、放桩。钉住现行行为（调度 11-18 接受）：要改成扣住，先改这一条。
    /// </summary>
    [Fact]
    public async Task AConfirmationRecordedOnTheWayCompletesWhenTheRecheckReadIsCutOffAtItsBudget()
    {
        await using FleetFixture fleet = await ClearanceFleetAsync();
        JourneyRuntimeRow journey = await OldOrderEndedAsync(fleet);
        await RoundAsync(fleet);
        await RoundAsync(fleet);
        Assert.Single(await ClearanceStopsAsync(fleet, journey));
        fleet.Riot.VehicleOverrides[KeyA] = seen => seen with { CurrentStationId = 300, Speed = 300, ProcState = "EXECUTING", OrderTaskId = "T" };
        fleet.Riot.HangOrder(journey.PickupUpperId);

        ManualStationClearanceConfirmation decision = await ManualClearance(fleet).DecideAsync(Request("00000000-0000-4000-8000-0000000c4799"), Token);
        Assert.Equal((FieldConfirmationDecision.Confirmed, false), (decision.Decision.Outcome, decision.StationReleased));
        Assert.Null((await ClearanceAsync(fleet)).CompletedAt);

        fleet.Riot.CancelOrder(journey.PickupUpperId);
        RiotVehicleReadBudgetTests.HangVehicleReads(fleet, KeyA);
        await RoundAsync(fleet);
        fleet.Riot.HangOn = null;

        Assert.NotNull((await ClearanceAsync(fleet)).CompletedAt);
        Assert.Null(await HolderAsync(fleet, Near.StationId));
    }

    /// <summary>
    /// 清桩移动单在 RIoT 里被取消、车其实已停稳：停稳证明的两次读（读车、安全读）任一超时，就证明不了停稳，保持一切——等待点 300 仍预占、码是
    /// <see cref="ChargingExecutionReasons.ClearanceMoveHeld"/>，不放预占。读数回来的那一轮照常放（<see cref="ClearanceMoveReleaseReasons.Ended"/>）。
    /// </summary>
    /// <remarks>
    /// 车站在哪按证明停稳那一次读数判，不再读第二次（审查 M-4）：第二次读超时会被当成「不在点上」而放预占。
    /// </remarks>
    [Theory]
    [InlineData("vehicle-read-hangs")]
    [InlineData("safety-read-hangs")]
    public async Task AMoveEndedInRiotWhoseStopProofReadIsCutOffKeepsEverything(string hang)
    {
        await using FleetFixture fleet = await ClearanceFleetAsync(points: [Point214, Point300]);
        JourneyRuntimeRow journey = await OldOrderEndedAsync(fleet);
        await RoundAsync(fleet);
        await RoundAsync(fleet);
        string upperId = ClearanceMoveShape.UpperIdFor(journey.JourneyId, 1);
        fleet.Riot.CancelOrder(upperId);
        fleet.Riot.VehicleOverrides[KeyA] = seen => seen with { CurrentStationId = 12, BatteryState = "NO_CHARGE" };
        if (hang == "vehicle-read-hangs")
        {
            RiotVehicleReadBudgetTests.HangVehicleReads(fleet, KeyA);
        }
        else
        {
            fleet.Options.RiotVehicleReadBudget = RiotVehicleReadBudgetTests.ShortReadBudget;
            fleet.Riot.HangSafetyOn = KeyA;
        }

        for (int round = 0; round < 3; round++)
        {
            await RoundAsync(fleet);
        }

        Assert.Equal((KeyA, StationExclusivityStates.Reserved), await HolderAsync(fleet, 300));
        Assert.Equal(ChargingExecutionReasons.ClearanceMoveHeld, (await ChargingJourneyAsync(fleet, AgvA))!.BlockReasonCode);
        Assert.Empty(await fleet.Context.Set<StationExclusivityRecordRow>().AsNoTracking()
            .Where(row => row.StationId == 300 && row.ReleaseReason == ClearanceMoveReleaseReasons.Ended).ToArrayAsync(Token));

        fleet.Riot.HangOn = null;
        fleet.Riot.HangSafetyOn = null;
        await RoundAsync(fleet);
        Assert.Null(await StationAsync(fleet, 300));
        Assert.Equal(ChargingExecutionReasons.ClearanceMoveEnded, (await ChargingJourneyAsync(fleet, AgvA))!.BlockReasonCode);
    }

    /// <summary>
    /// 审查 M-4 的回归看守（增量审查 L-b）：清桩移动单在 RIoT 里被取消，车已停稳在等待点 300 上。车在不在点上，按证明停稳那一次读数判——在点上，
    /// 保持一切，不放 300 的预占。证明停稳的两次读照常答；之后再经 <c>ReadVehicleOrNullAsync</c> 读的车一律挂住、被预算截断。
    /// </summary>
    /// <remarks>
    /// 把 <c>JudgeClearanceMoveEndedInRiotAsync</c> 改回第二次读车：那一次读被截断、答空，被当成「不在点上」，结束移动、放掉车正停着的 300——这条红。
    /// 只挂经 <c>ReadVehicleOrNullAsync</c> 的读（看调用栈），不按第几次计数：同一轮里这辆车被读几次、谁先谁后，随推进与派车轮而变。
    /// </remarks>
    [Fact]
    public async Task AMoveEndedInRiotIsJudgedOnTheReadingThatProvedItStopped()
    {
        await using FleetFixture fleet = await ClearanceFleetAsync(points: [Point214, Point300]);
        JourneyRuntimeRow journey = await OldOrderEndedAsync(fleet);
        await RoundAsync(fleet);
        await RoundAsync(fleet);
        Assert.Equal((KeyA, StationExclusivityStates.Reserved), await HolderAsync(fleet, 300));
        fleet.Riot.CancelOrder(ClearanceMoveShape.UpperIdFor(journey.JourneyId, 1));
        StandOn(fleet, 300);
        fleet.Options.RiotVehicleReadBudget = RiotVehicleReadBudgetTests.ShortReadBudget;
        fleet.Riot.HangWhen = key =>
            key == KeyA &&
            new System.Diagnostics.StackTrace().ToString().Contains("ReadVehicleOrNullAsync", StringComparison.Ordinal);

        for (int round = 0; round < 3; round++)
        {
            await RoundAsync(fleet);
        }
        fleet.Riot.HangWhen = null;

        Assert.Equal((KeyA, StationExclusivityStates.Reserved), await HolderAsync(fleet, 300));
        Assert.Empty(await fleet.Context.Set<StationExclusivityRecordRow>().AsNoTracking()
            .Where(row => row.StationId == 300 && row.ReleaseReason == ClearanceMoveReleaseReasons.Ended).ToArrayAsync(Token));
        Assert.NotEqual(ChargingExecutionReasons.ClearanceMoveEnded, (await ChargingJourneyAsync(fleet, AgvA))!.BlockReasonCode);
    }
}
