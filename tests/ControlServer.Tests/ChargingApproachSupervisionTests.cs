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
/// 去桩这一段的监看（control-server#863）：充电单确认之后、到桩之前，RIoT 一直不把单派给车（<c>QUEUEING</c>、没有执行车），或车在途中失联。
/// 2026-10-22 agv03 9% 分到唯一的桩 211，单在 RIoT 挂了三个小时没派出，看板看不出、人工清桩也接不住。两种都只点名、告警、看板标超时，
/// 不取消、不放桩、不动车（<c>REQ-0165</c>、<c>REQ-0168</c>、<c>REQ-0169</c>、<c>REQ-0287</c>）；人在 RIoT 上取消之后由人工清桩放桩。
/// </summary>
public sealed class ChargingApproachSupervisionTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string AgvA => FleetFixture.AgvIds[0];
    private static string KeyA => FleetFixture.VehicleKeys[0];

    /// <summary>
    /// 单确认之后 RIoT 一直排队、不绑车：窗口之内什么也不写；过了 <c>JourneyRuntime:ChargingOrderUndispatchedAlarmAfter</c> 写
    /// <see cref="ChargingExecutionReasons.OrderNotDispatched"/>、告警一次（事件 2284，带车、单号与桩）。承诺、预占、周期照旧，不发任何订单命令，
    /// 不建第二张单。
    /// </summary>
    [Fact]
    public async Task AChargeOrderRiotNeverBindsIsNamedAndAlarmedOncePastTheWindowAndNothingIsCancelledOrReleased()
    {
        await using FleetFixture fleet = await FleetAsync();
        fleet.Riot.BatteryByVehicle[KeyA] = 20;
        JourneyRuntimeRow journey = await ChargingExecutionTests.CommittedAndSentAsync(fleet);
        (string CycleId, string JourneyId, string UpperId) before = await CommitmentOfAsync(fleet, KeyA);
        fleet.Riot.QueueOrderUnbound(journey.PickupUpperId);

        await RoundAsync(fleet);
        await RoundAsync(fleet);
        Assert.Null((await ChargingExecutionTests.ChargingJourneyAsync(fleet, AgvA))!.BlockReasonCode);
        Assert.DoesNotContain(fleet.EngineLog.Entries, entry => entry.EventId.Id == 2284);

        await fleet.HearFromEveryVehicleAsync();
        await fleet.RunRoundAsync(fleet.Options.ChargingOrderUndispatchedAlarmAfter);
        await RoundAsync(fleet);
        await RoundAsync(fleet);

        Assert.Equal(
            ChargingExecutionReasons.OrderNotDispatched,
            (await ChargingExecutionTests.ChargingJourneyAsync(fleet, AgvA))!.BlockReasonCode);
        var alarm = Assert.Single(fleet.EngineLog.Entries, entry => entry.EventId.Id == 2284);
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Warning, alarm.Level);
        Assert.Contains(KeyA, alarm.Message, StringComparison.Ordinal);
        Assert.Contains(journey.PickupUpperId, alarm.Message, StringComparison.Ordinal);
        Assert.Contains(Near.StationId.ToString(System.Globalization.CultureInfo.InvariantCulture), alarm.Message, StringComparison.Ordinal);
        Assert.Equal(before, await CommitmentOfAsync(fleet, KeyA));
        Assert.Equal((KeyA, StationExclusivityStates.Reserved), await HolderAsync(fleet, Near.StationId));
        Assert.Empty(fleet.Riot.OrderCommands);
        Assert.Single(fleet.Riot.Creates);
        Assert.Empty(await fleet.Context.OwnOrderRebuilds.AsNoTracking().ToArrayAsync(Token));
    }
}
