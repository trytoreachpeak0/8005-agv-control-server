using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Charging;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using static ControlServer.Tests.ChargingAllocationTests;
using FleetFixture = ControlServer.Tests.MultiVehicleExecutionTests.FleetFixture;

namespace ControlServer.Tests;

/// <summary>
/// 充电单在车到桩之前在 RIoT 结束（control-server#882）。2026-11-06 01:12 agv01 15% 预占 211、下了充电单，RIoT 一直没把单派给车
/// （<c>executeVehicleKey="--"</c>、<c>executingIndex=-1</c>、三段 <c>missionState=0</c>），03:27 单被取消。服务端判
/// <c>ORDER_ENDED_WITHOUT_ARRIVAL</c> 之后只等「车证明停稳」才收尾，而 agv01 的运动读数是 <c>MT_NA</c>、系统与定位 <c>ERROR</c>，证明永远不来：
/// 211 一直 <c>RESERVED</c>、周期 <c>ACTIVE/EN_ROUTE</c>、用途 <c>CHARGING</c> 挂到 09:11 人工清桩，agv02 分不到桩耗到断电。
/// </summary>
public sealed class ChargingOrderEndedBeforeArrivalTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string AgvA => FleetFixture.AgvIds[0];
    private static string KeyA => FleetFixture.VehicleKeys[0];
    private static string KeyB => FleetFixture.VehicleKeys[1];

    /// <summary>周期以「单没派给车就结束」收尾时写的码（周期结束原因、旅程收尾码、用途与桩预占的释放原因）。</summary>
    private const string EndedUndispatched = "CHARGING_ORDER_ENDED_UNDISPATCHED";

    /// <summary>
    /// 11-06 的形状：单确认之后 RIoT 一直排队、没有绑执行车，然后被取消（或删除）；车此刻证明不了停稳（运动读数不明，或离线）。RIoT 从没让任何车跑这张单，
    /// 车没有因为它动过、也没有被它带到桩上：这一轮就按已确认失败收尾，桩预占同一次保存里放掉（与「单从没发出过」的撤回同一个理由），不重建、不建第二张单、
    /// 不发任何订单命令或急停。放掉之后，排队的另一辆低电车下一轮就分到这个桩。
    /// </summary>
    [Theory]
    [InlineData("cancelled", "motion-unknown")]
    [InlineData("cancelled", "offline")]
    [InlineData("deleted", "motion-unknown")]
    public async Task AChargeOrderEndedBeforeRiotEverBoundAVehicleEndsTheCycleAndFreesTheChargerAtOnce(string ending, string vehicle)
    {
        await using FleetFixture fleet = await FleetAsync(vehicles: 2);
        fleet.Riot.BatteryByVehicle[KeyA] = 15;
        JourneyRuntimeRow journey = await ChargingExecutionTests.CommittedAndSentAsync(fleet);
        (string CycleId, string JourneyId, string UpperId) before = await CommitmentOfAsync(fleet, KeyA);
        fleet.Riot.QueueOrderUnbound(journey.PickupUpperId);
        await RoundAsync(fleet);

        if (ending == "cancelled")
        {
            fleet.Riot.CancelOrder(journey.PickupUpperId);
        }
        else
        {
            fleet.Riot.DeleteOrder(journey.PickupUpperId);
        }
        // Pushed away to be charged by hand: off every station, and RIoT cannot say it is stopped.
        fleet.Riot.BatteryByVehicle[KeyA] = 90;
        if (vehicle == "offline")
        {
            fleet.Riot.VehicleOverrides[KeyA] = seen => seen with { Connected = false, CurrentStationId = 0 };
        }
        else
        {
            fleet.Riot.VehicleOverrides[KeyA] = seen => seen with { CurrentStationId = 0, BatteryState = "NO_CHARGE" };
            fleet.Riot.SafetyReasonsByVehicle[KeyA] = ["RIOT_CONTROL_NOT_OK", "RIOT_LOCATION_NOT_OK"];
        }

        await RoundAsync(fleet);
        DateTimeOffset firstRoundAfterTheEnd = fleet.Clock.GetUtcNowWithoutTick();
        // 11-06 it stayed like this from 03:27 to 09:11: six hours later nothing may still be held.
        await fleet.HearFromEveryVehicleAsync();
        await fleet.RunRoundAsync(TimeSpan.FromHours(6));
        await RoundAsync(fleet);

        JourneyRuntimeRow ended = await fleet.Context.JourneyRuntimes.AsNoTracking()
            .SingleAsync(row => row.JourneyId == journey.JourneyId, Token);
        Assert.Equal((JourneyRuntimeStage.Completed, EndedUndispatched), (ended.Stage, ended.BlockReasonCode));
        ChargingCycleRow cycle = await fleet.Context.Set<ChargingCycleRow>().AsNoTracking()
            .SingleAsync(row => row.CycleId == before.CycleId, Token);
        Assert.Equal((ChargingCyclePhases.Ended, EndedUndispatched), (cycle.Phase, cycle.EndReason));
        Assert.True(cycle.EndedAt <= firstRoundAfterTheEnd, $"ended at {cycle.EndedAt:O}, not in the round that read the end");
        Assert.Null(await ClaimOfAsync(fleet, KeyA));
        Assert.Null(await StationAsync(fleet, Near.StationId));
        StationExclusivityRecordRow passage = await fleet.Context.Set<StationExclusivityRecordRow>().AsNoTracking()
            .SingleAsync(row => row.JourneyId == journey.JourneyId, Token);
        Assert.Equal(EndedUndispatched, passage.ReleaseReason);
        Assert.Equal(cycle.EndedAt, passage.ReleasedAt);
        Assert.Empty(await fleet.Context.OwnOrderRebuilds.AsNoTracking().ToArrayAsync(Token));
        Assert.Empty(fleet.Riot.OrderCommands);
        Assert.Empty(fleet.Riot.EmergencyCommands);
        Assert.Single(fleet.Riot.Creates, create => create.VehicleKey == KeyA);

        // agv02 below the mandatory line: the charger is its to take.
        fleet.Riot.BatteryByVehicle[KeyB] = 30;
        await RoundAsync(fleet);
        await RoundAsync(fleet);
        Assert.Equal((KeyB, StationExclusivityStates.Reserved), await HolderAsync(fleet, Near.StationId));
        Assert.Equal(VehiclePurposes.Charging, (await ClaimOfAsync(fleet, KeyB))?.Purpose);
    }

    /// <summary>
    /// 单派给过车（RIoT 绑了执行车）之后被取消，车证明不了停稳：车可能还在路上、可能已在桩上，预占、周期与用途照旧保持（这一段不变）。
    /// 但不能静默：过了 <c>JourneyRuntime:OwnOrderRebuildRepeatWindow</c> 告警一次（事件 2256），点名车、桩与周期。
    /// </summary>
    [Fact]
    public async Task AChargeOrderEndedAfterRiotBoundTheVehicleKeepsTheChargerAndAlarmsWithVehicleChargerAndCyclePastTheWindow()
    {
        await using FleetFixture fleet = await FleetAsync();
        fleet.Riot.BatteryByVehicle[KeyA] = 15;
        JourneyRuntimeRow journey = await ChargingExecutionTests.CommittedAndSentAsync(fleet);
        (string CycleId, string JourneyId, string UpperId) before = await CommitmentOfAsync(fleet, KeyA);
        fleet.Riot.BindOrder(journey.PickupUpperId);
        await RoundAsync(fleet);
        fleet.Riot.CancelOrder(journey.PickupUpperId);
        fleet.Riot.VehicleOverrides[KeyA] = seen => seen with { CurrentStationId = 0, BatteryState = "NO_CHARGE" };
        fleet.Riot.SafetyReasonsByVehicle[KeyA] = ["RIOT_CONTROL_NOT_OK"];

        await RoundAsync(fleet);
        await RoundAsync(fleet);
        Assert.Equal(
            JourneyRuntimeEngine.OrderEndedWithoutArrivalReason,
            (await ChargingExecutionTests.ChargingJourneyAsync(fleet, AgvA))!.BlockReasonCode);
        Assert.DoesNotContain(fleet.EngineLog.Entries, entry => entry.EventId.Id == 2256);

        await fleet.HearFromEveryVehicleAsync();
        await fleet.RunRoundAsync(fleet.Options.OwnOrderRebuildRepeatWindow + TimeSpan.FromMinutes(1));
        await RoundAsync(fleet);
        await RoundAsync(fleet);

        var alarm = Assert.Single(fleet.EngineLog.Entries, entry => entry.EventId.Id == 2256);
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Warning, alarm.Level);
        Assert.Contains(KeyA, alarm.Message, StringComparison.Ordinal);
        Assert.Contains(Near.StationId.ToString(System.Globalization.CultureInfo.InvariantCulture), alarm.Message, StringComparison.Ordinal);
        Assert.Contains(before.CycleId, alarm.Message, StringComparison.Ordinal);
        Assert.Equal(before, await CommitmentOfAsync(fleet, KeyA));
        Assert.Equal((KeyA, StationExclusivityStates.Reserved), await HolderAsync(fleet, Near.StationId));
        Assert.Empty(fleet.Riot.OrderCommands);
        Assert.Single(fleet.Riot.Creates);
    }

    /// <summary>
    /// 读不出绑没绑（替身不建模、或读数没说）的取消不算「没派给车」：照旧等车证明停稳，预占、周期、用途都保持。
    /// </summary>
    [Fact]
    public async Task ACancelWhoseBindingWasNotReadIsNotTakenAsUndispatched()
    {
        await using FleetFixture fleet = await FleetAsync();
        fleet.Riot.BatteryByVehicle[KeyA] = 15;
        JourneyRuntimeRow journey = await ChargingExecutionTests.CommittedAndSentAsync(fleet);
        (string CycleId, string JourneyId, string UpperId) before = await CommitmentOfAsync(fleet, KeyA);
        fleet.Riot.PutOrder(fleet.Riot.OrderOf(journey.PickupUpperId)! with
        {
            Kind = RiotOrderObservationKind.Terminal,
            OrderState = RiotOrderState.Cancelled,
            ExecuteVehicleBound = null,
        });
        fleet.Riot.SafetyReasonsByVehicle[KeyA] = ["RIOT_CONTROL_NOT_OK"];

        await RoundAsync(fleet);
        await RoundAsync(fleet);

        Assert.Equal(
            JourneyRuntimeEngine.OrderEndedWithoutArrivalReason,
            (await ChargingExecutionTests.ChargingJourneyAsync(fleet, AgvA))!.BlockReasonCode);
        Assert.Equal(before, await CommitmentOfAsync(fleet, KeyA));
        Assert.Equal((KeyA, StationExclusivityStates.Reserved), await HolderAsync(fleet, Near.StationId));
    }
}
