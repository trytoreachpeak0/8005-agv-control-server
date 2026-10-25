using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Charging;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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

    /// <summary>
    /// RIoT 在窗口之内绑了车：从不点名。点名之后才绑：下一轮清掉这个码；之后再排队不绑车时重新告警（清码时撤回这一趟已说过的键）。
    /// </summary>
    [Fact]
    public async Task OnceRiotBindsAVehicleTheCodeIsNeverWrittenOrIsClearedAndNothingElseMoves()
    {
        await using FleetFixture fleet = await FleetAsync();
        fleet.Riot.BatteryByVehicle[KeyA] = 20;
        JourneyRuntimeRow journey = await ChargingExecutionTests.CommittedAndSentAsync(fleet);
        (string CycleId, string JourneyId, string UpperId) before = await CommitmentOfAsync(fleet, KeyA);
        fleet.Riot.QueueOrderUnbound(journey.PickupUpperId);
        await fleet.HearFromEveryVehicleAsync();
        await fleet.RunRoundAsync(fleet.Options.ChargingOrderUndispatchedAlarmAfter - TimeSpan.FromSeconds(10));
        fleet.Riot.BindOrder(journey.PickupUpperId);
        await fleet.HearFromEveryVehicleAsync();
        await fleet.RunRoundAsync(TimeSpan.FromMinutes(1));
        await RoundAsync(fleet);
        Assert.Null((await ChargingExecutionTests.ChargingJourneyAsync(fleet, AgvA))!.BlockReasonCode);
        Assert.DoesNotContain(fleet.EngineLog.Entries, entry => entry.EventId.Id == 2284);

        fleet.Riot.QueueOrderUnbound(journey.PickupUpperId);
        await RoundAsync(fleet);
        Assert.Equal(
            ChargingExecutionReasons.OrderNotDispatched,
            (await ChargingExecutionTests.ChargingJourneyAsync(fleet, AgvA))!.BlockReasonCode);
        fleet.Riot.BindOrder(journey.PickupUpperId);
        await RoundAsync(fleet);
        await RoundAsync(fleet);
        Assert.Null((await ChargingExecutionTests.ChargingJourneyAsync(fleet, AgvA))!.BlockReasonCode);

        fleet.Riot.QueueOrderUnbound(journey.PickupUpperId);
        await RoundAsync(fleet);
        Assert.Equal(2, fleet.EngineLog.Entries.Count(entry => entry.EventId.Id == 2284));
        Assert.Equal(before, await CommitmentOfAsync(fleet, KeyA));
        Assert.Empty(fleet.Riot.OrderCommands);
        Assert.Single(fleet.Riot.Creates);
    }

    /// <summary>
    /// 只认网关读到的「没绑车」：单在排队、但读不出绑没绑（替身不建模、或读数没说）时，过了窗口也不点名。
    /// </summary>
    [Fact]
    public async Task AQueueingOrderWhoseBindingWasNotReadIsNeverNamedUndispatched()
    {
        await using FleetFixture fleet = await FleetAsync();
        fleet.Riot.BatteryByVehicle[KeyA] = 20;
        JourneyRuntimeRow journey = await ChargingExecutionTests.CommittedAndSentAsync(fleet);
        fleet.Riot.PutOrder(fleet.Riot.OrderOf(journey.PickupUpperId)! with
        {
            OrderState = RiotOrderState.Queueing,
            ExecuteVehicleBound = null,
        });

        await fleet.HearFromEveryVehicleAsync();
        await fleet.RunRoundAsync(fleet.Options.ChargingOrderUndispatchedAlarmAfter + TimeSpan.FromMinutes(1));
        await RoundAsync(fleet);

        Assert.NotEqual(
            ChargingExecutionReasons.OrderNotDispatched,
            (await ChargingExecutionTests.ChargingJourneyAsync(fleet, AgvA))!.BlockReasonCode);
        Assert.DoesNotContain(fleet.EngineLog.Entries, entry => entry.EventId.Id == 2284);
    }

    /// <summary>
    /// 窗口从周期落库的确认时刻算：进程在窗口里重启，不从头计时——重启后再过剩下的那一点就点名。
    /// </summary>
    [Fact]
    public async Task ARestartInsideTheWindowDoesNotStartItAgain()
    {
        await using FleetFixture fleet = await FleetAsync();
        fleet.Riot.BatteryByVehicle[KeyA] = 20;
        JourneyRuntimeRow journey = await ChargingExecutionTests.CommittedAndSentAsync(fleet);
        fleet.Riot.QueueOrderUnbound(journey.PickupUpperId);
        await fleet.HearFromEveryVehicleAsync();
        await fleet.RunRoundAsync(fleet.Options.ChargingOrderUndispatchedAlarmAfter - TimeSpan.FromSeconds(20));
        Assert.Null((await ChargingExecutionTests.ChargingJourneyAsync(fleet, AgvA))!.BlockReasonCode);

        await fleet.RecreateEngineAsync();
        await fleet.HearFromEveryVehicleAsync();
        await fleet.RunRoundAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(
            ChargingExecutionReasons.OrderNotDispatched,
            (await ChargingExecutionTests.ChargingJourneyAsync(fleet, AgvA))!.BlockReasonCode);
    }

    /// <summary>
    /// 现场处置（票面第 2 条）：点名之后人工清桩先拒收（旧单还在排队，请先在 RIoT 结束它）；人在 RIoT 上取消这张单，服务端照取消走——
    /// <c>ORDER_ENDED_WITHOUT_ARRIVAL</c>、不重建、不建第二张单；车离线证明不了停稳，预占照旧；这时人工清桩确认，桩当场放开。
    /// </summary>
    [Fact]
    public async Task OnceAPersonCancelsTheUndispatchedOrderInRiotTheChargerIsFreedByAManualClearanceAndNothingIsRebuilt()
    {
        await using FleetFixture fleet = await FleetAsync();
        fleet.Riot.BatteryByVehicle[KeyA] = 20;
        JourneyRuntimeRow journey = await ChargingExecutionTests.CommittedAndSentAsync(fleet);
        fleet.Riot.QueueOrderUnbound(journey.PickupUpperId);
        await fleet.HearFromEveryVehicleAsync();
        await fleet.RunRoundAsync(fleet.Options.ChargingOrderUndispatchedAlarmAfter);
        await RoundAsync(fleet);
        fleet.Riot.VehicleOverrides[KeyA] = seen => seen with { Connected = false, CurrentStationId = 12 };
        await RoundAsync(fleet);
        Assert.Equal(
            ChargingExecutionReasons.OrderNotDispatched,
            (await ChargingExecutionTests.ChargingJourneyAsync(fleet, AgvA))!.BlockReasonCode);

        ManualStationClearanceConfirmation refused =
            await ManualClearance(fleet).DecideAsync(Request("00000000-0000-4000-8000-0000000c8631"), Token);
        Assert.Equal(
            (FieldConfirmationDecision.Rejected, ManualStationClearance.NotAllowedInState, false),
            (refused.Decision.Outcome, refused.Decision.ProblemReasonCode, refused.StationReleased));
        Assert.Contains("end it in RIoT first", refused.Decision.ProblemDisplayMessage!, StringComparison.Ordinal);

        fleet.Riot.CancelOrder(journey.PickupUpperId);
        await RoundAsync(fleet);
        await RoundAsync(fleet);
        Assert.Equal(
            JourneyRuntimeEngine.OrderEndedWithoutArrivalReason,
            (await ChargingExecutionTests.ChargingJourneyAsync(fleet, AgvA))!.BlockReasonCode);
        Assert.Equal((KeyA, StationExclusivityStates.Reserved), await HolderAsync(fleet, Near.StationId));
        Assert.Empty(await fleet.Context.OwnOrderRebuilds.AsNoTracking().ToArrayAsync(Token));

        ManualStationClearanceConfirmation cleared =
            await ManualClearance(fleet).DecideAsync(Request("00000000-0000-4000-8000-0000000c8632"), Token);
        Assert.Equal((FieldConfirmationDecision.Confirmed, true), (cleared.Decision.Outcome, cleared.StationReleased));
        Assert.Null(await StationAsync(fleet, Near.StationId));
        await RoundAsync(fleet);
        await RoundAsync(fleet);
        Assert.Single(fleet.Riot.Creates);
        Assert.Empty(fleet.Riot.OrderCommands);
    }

    internal static ManualStationClearance ManualClearance(FleetFixture fleet) =>
        new(
            fleet.Context,
            new FieldConfirmationRequestStore(fleet.Context),
            new StationClearanceStore(fleet.Context),
            fleet.Riot,
            new FieldOperatorRoleRoster(Options.Create(new FieldOperatorRoleOptions { Path = fleet.ClearanceRosterPath })),
            new GovernanceStore(fleet.Context, new GovernanceDeploymentIdentity("deployment:8005-controlserver@test"), AuditRetentionPolicy.Default),
            Options.Create(fleet.Options),
            fleet.Clock,
            NullLogger<ManualStationClearance>.Instance);

    internal static ManualStationClearanceRequest Request(string confirmationRequestId) =>
        new(
            ManualStationClearanceSources.Onboard, AgvA, KeyA, confirmationRequestId, 1, Guid.NewGuid().ToString("D"),
            $"{Near.StationName}||fleet-r11|STATION_EMPTY", Near.StationName, null, "STATION_EMPTY", "fleet-r11", "BADGE",
            DateTimeOffset.Parse("2026-09-08T06:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
            DateTimeOffset.Parse("2026-09-08T06:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
}
