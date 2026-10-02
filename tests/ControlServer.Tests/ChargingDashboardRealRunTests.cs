using System.Text.Json;
using ControlServer.Application;
using ControlServer.Dashboard;
using ControlServer.Host.Dashboard;
using ControlServer.Host.Runtime.Charging;
using ControlServer.Host.Runtime.Fleet;
using Microsoft.Extensions.Options;
using static ControlServer.Tests.ChargingAllocationTests;
using FleetFixture = ControlServer.Tests.MultiVehicleExecutionTests.FleetFixture;

namespace ControlServer.Tests;

/// <summary>
/// 充电看板（批次9-10，control-server#408）读引擎真实跑出来的状态，不是手种的库行：名册为空退化到人工充电等待、已确认充不上进清桩中、
/// 充满之后一直停在桩上超过告警窗口。三种都在充电执行的车队夹具里按真实轮次走到，再读四个数据面、交给卡片渲染。
/// </summary>
/// <remarks>夹具挂着 <see cref="ChargingDashboardCodeRecorder"/>：这几条用例写下的每个码同样要有说明。</remarks>
public sealed class ChargingDashboardRealRunTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string AgvA => FleetFixture.AgvIds[0];

    private static string KeyA => FleetFixture.VehicleKeys[0];

    /// <summary>名册置空、车电量 20：引擎把它放进人工充电等待；充电桩卡片醒目写名册为空并列出这辆车，告警卡片有名册为空与人工充电等待两条。</summary>
    [Fact]
    public async Task AnEmptyRosterTheEngineDegradedToManualChargingIsShownWithTheWaitingVehicle()
    {
        await using FleetFixture fleet = await FleetAsync(roster: true, chargers: []);
        fleet.Riot.BatteryByVehicle[KeyA] = 20;
        for (int round = 0; round < 4; round++)
        {
            await RoundAsync(fleet);
        }
        await fleet.HearFromEveryVehicleAsync();

        (JsonDocument chargers, string chargerHtml) = await ReadAsync(fleet, "chargers");
        using (chargers)
        {
            Assert.True(chargers.RootElement.GetProperty("rosterEmpty").GetBoolean());
            Assert.Contains(ChargersQueryEndpoint.RosterEmptyBanner, chargerHtml, StringComparison.Ordinal);
            Assert.Contains($"正在等人工充电的车：{AgvA}（ROSTER_EMPTY，自 ", chargerHtml, StringComparison.Ordinal);
        }

        (JsonDocument holds, string holdHtml) = await ReadAsync(fleet, "charging-holds");
        using (holds)
        {
            Assert.Contains("ROSTER_EMPTY：名册为空", holdHtml, StringComparison.Ordinal);
            Assert.Contains(ChargingDashboardDescriptions.ManualHoldReleaseHint, holdHtml, StringComparison.Ordinal);
        }

        (JsonDocument alarms, string _) = await ReadAsync(fleet, "charging-alarms");
        using (alarms)
        {
            Assert.Equal(
                [ChargingDashboardDescriptions.AlarmRosterEmpty + "@", ChargingDashboardDescriptions.AlarmManualChargingHold + "@" + AgvA],
                alarms.RootElement.GetProperty("alarms").EnumerateArray()
                    .Select(a => a.GetProperty("code").GetString() + "@" + a.GetProperty("agvId").GetString())
                    .Order(StringComparer.Ordinal));
        }
    }

    /// <summary>
    /// 已确认充不上：充电桩卡片上这个桩是「清桩中」、带着充不上的分配暂停，持有者是清桩中的充电旅程；暂停卡片列出清桩中的车与它此刻的码；
    /// 告警卡片有充不上的暂停与清桩中两条；逐车卡片的用途是 CLEARING_MAINTENANCE。
    /// </summary>
    [Fact]
    public async Task AConfirmedFailureToChargeIsShownAsClearingWithItsPauseOnEveryCard()
    {
        await using FleetFixture fleet = await FleetAsync();
        await ChargingUnableToChargeTests.ConfirmedAsync(fleet);
        await fleet.HearFromEveryVehicleAsync();

        (JsonDocument chargers, string chargerHtml) = await ReadAsync(fleet, "chargers");
        using (chargers)
        {
            JsonElement charger = chargers.RootElement.GetProperty("chargers").EnumerateArray()
                .Single(c => c.GetProperty("stationId").GetInt32() == Near.StationId);
            Assert.Equal(ChargingDashboardDescriptions.StageClearing, charger.GetProperty("stage").GetString());
            Assert.Equal("清桩中的充电旅程", charger.GetProperty("holding").GetProperty("holderKind").GetString());
            Assert.Equal(ChargingStationHoldTriggers.UnableToChargeConfirmed,
                charger.GetProperty("allocationHolds")[0].GetProperty("trigger").GetString());
            Assert.Equal(ChargingExecutionReasons.UnableToChargeClearing, charger.GetProperty("journeyCode").GetString());
            Assert.Contains("CLEARING：清桩中", chargerHtml, StringComparison.Ordinal);
            Assert.Contains("UNABLE_TO_CHARGE_CONFIRMED：充不上", chargerHtml, StringComparison.Ordinal);
        }

        (JsonDocument holds, string holdHtml) = await ReadAsync(fleet, "charging-holds");
        using (holds)
        {
            Assert.Equal(AgvA, Assert.Single(holds.RootElement.GetProperty("clearing").EnumerateArray()).GetProperty("agvId").GetString());
            Assert.Contains("CHARGING_UNABLE_TO_CHARGE：已确认充不上", holdHtml, StringComparison.Ordinal);
            Assert.Contains("还没有人工确认", holdHtml, StringComparison.Ordinal);
        }

        (JsonDocument alarms, string _) = await ReadAsync(fleet, "charging-alarms");
        using (alarms)
        {
            Assert.Equal(
                [ChargingExecutionReasons.UnableToChargeClearing, ChargingStationHoldTriggers.UnableToChargeConfirmed],
                alarms.RootElement.GetProperty("alarms").EnumerateArray().Select(a => a.GetProperty("code").GetString()!)
                    .Order(StringComparer.Ordinal));
        }

        (JsonDocument vehicles, string _) = await ReadAsync(fleet, "charging-vehicles");
        using (vehicles)
        {
            JsonElement vehicle = vehicles.RootElement.GetProperty("vehicles")[0];
            Assert.Equal(
                (VehiclePurposes.ClearingMaintenance, "CLEARING_MAINTENANCE", ChargingCycleWireStates.UnableToCharge),
                (vehicle.GetProperty("purpose").GetString(), vehicle.GetProperty("holderKind").GetString(),
                    vehicle.GetProperty("chargingCycleState").GetString()));
        }
    }

    /// <summary>
    /// 充满之后一直停在桩上（没接到活、仍报在充电）：充电桩卡片是「充满待离桩」、自充满时刻起算；超过告警窗口（10 分钟）后标出来并指到事件 2249，
    /// 告警卡片多出一条。充满的那趟旅程已收尾，阻断卡片上看不到它——这正是桩独占视图要补的那一块。
    /// </summary>
    [Fact]
    public async Task AFullVehicleLeftOnItsChargerIsShownAwaitingDepartureAndFlaggedPastTheWindow()
    {
        await using FleetFixture fleet = await FleetAsync();
        await ChargingCycleProgressTests.ChargingAsync(fleet, battery: 60);
        ChargingCycleProgressTests.AtCharger(fleet, KeyA, 80, "CHARGING");
        await RoundAsync(fleet);

        (JsonDocument justFull, string _) = await ReadAsync(fleet, "chargers");
        using (justFull)
        {
            JsonElement charger = justFull.RootElement.GetProperty("chargers")[0];
            Assert.Equal(ChargingDashboardDescriptions.StageCompleteAwaitingDeparture, charger.GetProperty("stage").GetString());
            Assert.False(charger.GetProperty("overdue").GetBoolean());
        }

        for (int minute = 0; minute < 11; minute++)
        {
            await fleet.HearFromEveryVehicleAsync();
            await fleet.RunRoundAsync(TimeSpan.FromMinutes(1));
        }
        await fleet.HearFromEveryVehicleAsync();

        (JsonDocument chargers, string html) = await ReadAsync(fleet, "chargers");
        using (chargers)
        {
            JsonElement charger = chargers.RootElement.GetProperty("chargers")[0];
            Assert.Equal(ChargingDashboardDescriptions.StageCompleteAwaitingDeparture, charger.GetProperty("stage").GetString());
            Assert.True(charger.GetProperty("overdue").GetBoolean());
            Assert.Contains("COMPLETE_AWAITING_DEPARTURE：充满待离桩", html, StringComparison.Ordinal);
            Assert.Contains("事件 2249", html, StringComparison.Ordinal);
        }

        (JsonDocument alarms, string _) = await ReadAsync(fleet, "charging-alarms");
        using (alarms)
        {
            Assert.Contains(alarms.RootElement.GetProperty("alarms").EnumerateArray(),
                a => a.GetProperty("code").GetString() == ChargingDashboardDescriptions.StageCompleteAwaitingDeparture);
        }
    }

    // ---- 电量与排队原因：分配板最近一轮已完成的分配（调度 10-02，照 cs#392 的 M1～M6）----

    private static string AgvB => FleetFixture.AgvIds[1];

    private static string KeyB => FleetFixture.VehicleKeys[1];

    /// <summary>一轮分配交到的车：电量、RIoT 电池状态、观测时刻、batteryState 投影与结论都是那一轮的。</summary>
    [Fact]
    public async Task AVehicleTheRoundEvaluatedShowsItsBatteryAndReasonFromThatRound()
    {
        await using FleetFixture fleet = await FleetAsync();
        fleet.Riot.BatteryByVehicle[KeyA] = 77;
        await RoundAsync(fleet);
        await fleet.HearFromEveryVehicleAsync();

        JsonElement allocation = await AllocationAsync(fleet, AgvA);
        Assert.Equal(JsonValueKind.Null, allocation.GetProperty("note").ValueKind);
        Assert.Equal(77, allocation.GetProperty("batteryPercent").GetInt32());
        Assert.Equal(ChargingAllocationReasons.NotRequired, allocation.GetProperty("reason").GetString());
        Assert.NotEqual(JsonValueKind.Null, allocation.GetProperty("batteryObservedAt").ValueKind);
        Assert.Equal("SUFFICIENT", allocation.GetProperty("batteryState").GetString());
    }

    /// <summary>上一轮评估过、这一轮读 RIoT 失败没交到的车：写没评估，不显示它上一轮的电量与结论；同一轮里别的车照常。</summary>
    [Fact]
    public async Task AVehicleWhoseRiotReadFailsThisRoundReadsAsNotEvaluatedRatherThanItsPreviousBattery()
    {
        await using FleetFixture fleet = await FleetAsync(vehicles: 2);
        fleet.Riot.BatteryByVehicle[KeyA] = 66;
        fleet.Riot.BatteryByVehicle[KeyB] = 88;
        await RoundAsync(fleet);
        Assert.Equal(66, (await AllocationAsync(fleet, AgvA)).GetProperty("batteryPercent").GetInt32());

        fleet.Riot.FailOn = KeyA;
        await RoundAsync(fleet);
        await fleet.HearFromEveryVehicleAsync();

        JsonElement lost = await AllocationAsync(fleet, AgvA);
        Assert.Equal(ChargingDashboardDescriptions.NotEvaluatedThisPass, lost.GetProperty("note").GetString());
        Assert.Equal(JsonValueKind.Null, lost.GetProperty("batteryPercent").ValueKind);
        Assert.Equal(JsonValueKind.Null, lost.GetProperty("reason").ValueKind);
        Assert.Equal(88, (await AllocationAsync(fleet, AgvB)).GetProperty("batteryPercent").GetInt32());
    }

    /// <summary>候选为空的一轮（唯一的车读 RIoT 失败）也算走完一轮：写「本轮没有评估这辆车」，不是「还没完成过一轮」。</summary>
    [Fact]
    public async Task APassWithNoCandidatesStillCompletesAndLeavesTheVehicleNotEvaluated()
    {
        await using FleetFixture fleet = await FleetAsync();
        fleet.Riot.FailOn = KeyA;
        await RoundAsync(fleet);
        await fleet.HearFromEveryVehicleAsync();

        Assert.NotNull(fleet.ChargingBoard.LatestCompletedPass);
        Assert.Equal(ChargingDashboardDescriptions.NotEvaluatedThisPass, (await AllocationAsync(fleet, AgvA)).GetProperty("note").GetString());
    }

    /// <summary>派车轮停了（时钟走过 3 个轮询间隔而没有新的一轮走完）：电量与结论写没评估；用途与周期这些库里的事实照常。</summary>
    [Fact]
    public async Task OnceNoPassCompletesWithinTheWindowTheBatteryIsNotShownAndTheCycleStillIs()
    {
        await using FleetFixture fleet = await FleetAsync();
        await ChargingCycleProgressTests.ChargingAsync(fleet, battery: 60);
        fleet.Clock.Advance(fleet.Options.PollInterval * ChargingVehiclesQueryEndpoint.PassLivenessPollIntervals + TimeSpan.FromSeconds(1));
        await fleet.HearFromEveryVehicleAsync();

        (JsonDocument vehicles, string _) = await ReadAsync(fleet, "charging-vehicles");
        using (vehicles)
        {
            JsonElement vehicle = vehicles.RootElement.GetProperty("vehicles")[0];
            Assert.StartsWith("本轮没有评估这辆车：最近 ", vehicle.GetProperty("allocation").GetProperty("note").GetString(), StringComparison.Ordinal);
            Assert.Equal(JsonValueKind.Null, vehicle.GetProperty("allocation").GetProperty("batteryPercent").ValueKind);
            Assert.Equal(ChargingCycleWireStates.Charging, vehicle.GetProperty("chargingCycleState").GetString());
        }
    }

    private static async Task<JsonElement> AllocationAsync(FleetFixture fleet, string agvId)
    {
        (JsonDocument vehicles, string _) = await ReadAsync(fleet, "charging-vehicles");
        using (vehicles)
        {
            return vehicles.RootElement.GetProperty("vehicles").EnumerateArray()
                .Single(v => v.GetProperty("agvId").GetString() == agvId).GetProperty("allocation").Clone();
        }
    }

    private static async Task<(JsonDocument Fact, string Html)> ReadAsync(FleetFixture fleet, string path)
    {
        IOptions<Host.Runtime.JourneyRuntimeOptions> options = Options.Create(fleet.Options);
        IDashboardCard card = DashboardCardCatalog.Discovered.Cards
            .Single(candidate => candidate.SourcePath == DashboardPaths.QueryPrefix + path);
        IDashboardQueryEndpoint endpoint = path switch
        {
            "chargers" => new ChargersQueryEndpoint(options, fleet.Clock),
            "charging-vehicles" => new ChargingVehiclesQueryEndpoint(options, fleet.ChargingBoard, fleet.Clock),
            "charging-holds" => new ChargingHoldsQueryEndpoint(new VehicleRoster(options), fleet.Clock),
            "charging-alarms" => new ChargingAlarmsQueryEndpoint(options, fleet.Clock),
            _ => throw new InvalidOperationException(path),
        };
        fleet.Context.ChangeTracker.Clear();
        object rows = await endpoint.ReadAsync(fleet.Context, Token);
        JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(rows));
        string html = card.RenderFact(document.RootElement);
        Assert.DoesNotContain("<form", html, StringComparison.OrdinalIgnoreCase);
        return (document, html);
    }
}
