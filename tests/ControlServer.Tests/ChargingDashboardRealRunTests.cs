using System.Text.Json;
using ControlServer.Application;
using ControlServer.Dashboard;
using ControlServer.Host.Dashboard;
using ControlServer.Host.Runtime.Charging;
using ControlServer.Host.Runtime.Fleet;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
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

    /// <summary>
    /// 独立审查 S2（REQ-0269）：RIoT 报这辆车离线（或被禁用）时，它报的电量不是此刻的读数：看板不显示电量与 RIoT 电池状态，batteryState 投影为
    /// <c>UNKNOWN</c>，写「电量拿不到」。分别看离线、被禁用两种，电量各压到低于和高于强制充电线。
    /// </summary>
    [Theory]
    [InlineData(false, true, 62, "CHARGING_NOT_REQUIRED")]
    [InlineData(false, true, 10, null)]
    [InlineData(true, false, 62, "CHARGING_NOT_REQUIRED")]
    public async Task AVehicleRiotReportsOfflineOrDisabledShowsNoBattery(bool connected, bool enabled, int battery, string? voided)
    {
        await using FleetFixture fleet = await FleetAsync();
        fleet.Riot.BatteryByVehicle[KeyA] = battery;
        fleet.Riot.VehicleOverrides[KeyA] = seen => seen with { Connected = connected, Enabled = enabled };
        await RoundAsync(fleet);
        await fleet.HearFromEveryVehicleAsync();

        (JsonDocument vehicles, string html) = await ReadAsync(fleet, "charging-vehicles");
        using (vehicles)
        {
            JsonElement allocation = vehicles.RootElement.GetProperty("vehicles")[0].GetProperty("allocation");
            Assert.Equal(JsonValueKind.Null, allocation.GetProperty("note").ValueKind);
            Assert.Equal(JsonValueKind.Null, allocation.GetProperty("batteryPercent").ValueKind);
            Assert.Equal(JsonValueKind.Null, allocation.GetProperty("riotBatteryState").ValueKind);
            Assert.Equal("UNKNOWN", allocation.GetProperty("batteryState").GetString());
            Assert.Contains("电量拿不到", html, StringComparison.Ordinal);
            Assert.DoesNotContain(battery + "%", html, StringComparison.Ordinal);

            // 增量审查 S2'：按那份电量得出的结论不作数——不当结论显示，原码只在括号里。
            string? reason = allocation.GetProperty("reason").GetString();
            Assert.False(reason is not null && ChargingDashboardDescriptions.BatteryDerivedReasons.Contains(reason), reason);
            Assert.Equal(voided, allocation.GetProperty("voidedReason").GetString());
            if (voided is not null)
            {
                Assert.Null(reason);
                Assert.Equal($"{ChargingDashboardDescriptions.BatteryVerdictVoid}（原结论 {voided}）",
                    allocation.GetProperty("reasonDescription").GetString());
                Assert.Contains($"{ChargingDashboardDescriptions.BatteryVerdictVoid}（原结论 {voided}）", html, StringComparison.Ordinal);
            }
            Assert.DoesNotContain("不需要充电", html, StringComparison.Ordinal);
        }
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

    /// <summary>
    /// 低于强制充电线、又因出发证明有缺口分不到桩（control-server#865，10-23 agv02 一直报 SLOT_1_NOT_LOCKED、电量掉到断电）：门照旧不放，
    /// 但告警卡片有一条 <see cref="ChargingDashboardDescriptions.AlarmBelowMandatoryDepartureNotProven"/>，细节点名是哪一仓。门一好、分到桩，告警消失。
    /// </summary>
    [Fact]
    public async Task AVehicleBelowTheMandatoryLineHeldBackByAnUnlockedSlotIsAlarmedNamingTheSlot()
    {
        await using FleetFixture fleet = await FleetAsync();
        fleet.Riot.BatteryByVehicle[KeyA] = 20;
        await fleet.ReplaceSafetySnapshotAsync(AgvA, unlockedSlot: 3);
        await fleet.HearFromEveryVehicleAsync();
        await RoundAsync(fleet);
        await fleet.HearFromEveryVehicleAsync();

        (JsonDocument alarms, string html) = await ReadAsync(fleet, "charging-alarms");
        using (alarms)
        {
            JsonElement alarm = Assert.Single(
                alarms.RootElement.GetProperty("alarms").EnumerateArray(),
                a => a.GetProperty("code").GetString() == ChargingDashboardDescriptions.AlarmBelowMandatoryDepartureNotProven);
            Assert.Equal(AgvA, alarm.GetProperty("agvId").GetString());
            Assert.Equal(ChargingDashboardDescriptions.SeverityHigh, alarm.GetProperty("severity").GetString());
            string detail = alarm.GetProperty("detail").GetString()!;
            Assert.Contains("3号仓没锁好或开锁输出没复位（SLOT_3_NOT_LOCKED）", detail, StringComparison.Ordinal);
            Assert.Contains("电量 20%", detail, StringComparison.Ordinal);
            Assert.Contains("3号仓没锁好或开锁输出没复位", html, StringComparison.Ordinal);
        }
        Assert.Empty(await fleet.Context.Set<ChargingCycleRow>().AsNoTracking().ToArrayAsync(Token));

        await fleet.ReplaceSafetySnapshotAsync(AgvA);
        await fleet.HearFromEveryVehicleAsync();
        await RoundAsync(fleet);
        await fleet.HearFromEveryVehicleAsync();

        Assert.Single(await fleet.Context.Set<ChargingCycleRow>().AsNoTracking().ToArrayAsync(Token));
        (JsonDocument after, string _) = await ReadAsync(fleet, "charging-alarms");
        using (after)
        {
            Assert.DoesNotContain(
                after.RootElement.GetProperty("alarms").EnumerateArray(),
                a => a.GetProperty("code").GetString() == ChargingDashboardDescriptions.AlarmBelowMandatoryDepartureNotProven);
        }
    }

    /// <summary>
    /// 充电分配停跑（最近一轮已完成的分配超过三个轮询间隔）时，不拿那一轮的结论告警：它说的是分配停下之前的事，与逐车卡片同一个窗口（control-server#865）。
    /// </summary>
    [Fact]
    public async Task AStalePassRaisesNoDepartureAlarm()
    {
        await using FleetFixture fleet = await FleetAsync();
        fleet.Riot.BatteryByVehicle[KeyA] = 20;
        await fleet.ReplaceSafetySnapshotAsync(AgvA, unlockedSlot: 3);
        await fleet.HearFromEveryVehicleAsync();
        await RoundAsync(fleet);
        Assert.Equal(ChargingAllocationReasons.DepartureNotProven, fleet.ChargingBoard.Verdicts[AgvA].Reason);

        fleet.Clock.Advance(fleet.Options.PollInterval * ChargingVehiclesQueryEndpoint.PassLivenessPollIntervals + TimeSpan.FromSeconds(1));
        await fleet.HearFromEveryVehicleAsync();

        (JsonDocument alarms, string _) = await ReadAsync(fleet, "charging-alarms");
        using (alarms)
        {
            Assert.DoesNotContain(
                alarms.RootElement.GetProperty("alarms").EnumerateArray(),
                a => a.GetProperty("code").GetString() == ChargingDashboardDescriptions.AlarmBelowMandatoryDepartureNotProven);
        }
    }

    /// <summary>
    /// 低于强制充电线、RIoT 把它放在两站之间（<c>currentPosition</c> 0）的车（control-server#528，11-06 agv02 排队到断电）：位置未知就分不到桩，
    /// 告警卡片有一条 <see cref="ChargingDashboardDescriptions.AlarmBelowMandatoryPositionUnknown"/>，点名这辆车、写电量，现场该做的是在 RIoT 里
    /// 给车重新定位。重新定位、分到桩之后告警消失。
    /// </summary>
    [Fact]
    public async Task AVehicleBelowTheMandatoryLineAtNoStationIsAlarmedToBeRelocated()
    {
        await using FleetFixture fleet = await FleetAsync();
        fleet.Riot.BatteryByVehicle[KeyA] = 20;
        fleet.Riot.VehicleOverrides[KeyA] = seen => seen with { CurrentStationId = RiotVehicleObservation.NoStation };
        await RoundAsync(fleet);
        await fleet.HearFromEveryVehicleAsync();

        Assert.Equal(ChargingAllocationReasons.VehiclePositionUnknown, fleet.ChargingBoard.Verdicts[AgvA].Reason);
        (JsonDocument alarms, string html) = await ReadAsync(fleet, "charging-alarms");
        using (alarms)
        {
            JsonElement alarm = Assert.Single(
                alarms.RootElement.GetProperty("alarms").EnumerateArray(),
                a => a.GetProperty("code").GetString() == ChargingDashboardDescriptions.AlarmBelowMandatoryPositionUnknown);
            Assert.Equal(AgvA, alarm.GetProperty("agvId").GetString());
            Assert.Equal(ChargingDashboardDescriptions.SeverityHigh, alarm.GetProperty("severity").GetString());
            Assert.Contains("电量 20%", alarm.GetProperty("detail").GetString()!, StringComparison.Ordinal);
            Assert.Contains("重新定位", html, StringComparison.Ordinal);
            Assert.DoesNotContain(
                "调度",
                ChargingDashboardDescriptions.FieldActions[ChargingDashboardDescriptions.AlarmBelowMandatoryPositionUnknown],
                StringComparison.Ordinal);
        }
        Assert.Empty(await fleet.Context.Set<ChargingCycleRow>().AsNoTracking().ToArrayAsync(Token));

        fleet.Riot.VehicleOverrides.Remove(KeyA);
        await RoundAsync(fleet);
        await fleet.HearFromEveryVehicleAsync();

        Assert.Single(await fleet.Context.Set<ChargingCycleRow>().AsNoTracking().ToArrayAsync(Token));
        (JsonDocument after, string _) = await ReadAsync(fleet, "charging-alarms");
        using (after)
        {
            Assert.DoesNotContain(
                after.RootElement.GetProperty("alarms").EnumerateArray(),
                a => a.GetProperty("code").GetString() == ChargingDashboardDescriptions.AlarmBelowMandatoryPositionUnknown);
        }
    }

    /// <summary>高于强制充电线的车在两站之间：它本来就不需要充电，不告警（control-server#528）。</summary>
    [Fact]
    public async Task AVehicleAboveTheMandatoryLineAtNoStationIsNotAlarmed()
    {
        await using FleetFixture fleet = await FleetAsync();
        fleet.Riot.BatteryByVehicle[KeyA] = 90;
        fleet.Riot.VehicleOverrides[KeyA] = seen => seen with { CurrentStationId = RiotVehicleObservation.NoStation };
        await RoundAsync(fleet);
        await fleet.HearFromEveryVehicleAsync();

        Assert.Equal(ChargingAllocationReasons.NotRequired, fleet.ChargingBoard.Verdicts[AgvA].Reason);
        (JsonDocument alarms, string _) = await ReadAsync(fleet, "charging-alarms");
        using (alarms)
        {
            Assert.DoesNotContain(
                alarms.RootElement.GetProperty("alarms").EnumerateArray(),
                a => a.GetProperty("code").GetString() == ChargingDashboardDescriptions.AlarmBelowMandatoryPositionUnknown);
        }
    }

    /// <summary>
    /// 低于强制充电线、车的充电资格暂停着的车（control-server#877，11-04 agv02 挂着中断留下的暂停耗到断电）：分配照旧不给它桩（<c>REQ-0286</c>），
    /// 但告警卡片有一条 <see cref="ChargingDashboardDescriptions.AlarmBelowMandatoryEligibilityHeld"/>，点名这辆车、电量、暂停 id、原因与写入时刻，
    /// 现场该做的是查明原因后经车辆充电资格恢复入口解除。解除、分到桩之后告警消失。
    /// </summary>
    [Fact]
    public async Task AVehicleBelowTheMandatoryLineWithItsChargingEligibilityHeldIsAlarmed()
    {
        await using FleetFixture fleet = await FleetAsync();
        ChargingHoldStore holds = new(fleet.Context);
        DateTimeOffset heldAt = fleet.Clock.GetUtcNow();
        const string holdId = "bc21d8e2-4374-9859-8254-3876ede9a328";
        await holds.RecordVehicleHoldAsync(
            new VehicleChargingEligibilityHold(
                holdId, "cs877-hold", KeyA, null, VehicleChargingEligibilityHoldReasons.InterruptionConfirmed, heldAt, "cs877"),
            Token);
        fleet.Riot.BatteryByVehicle[KeyA] = 20;
        await RoundAsync(fleet);
        await fleet.HearFromEveryVehicleAsync();

        Assert.Equal(ChargingAllocationReasons.VehicleEligibilityHeld, fleet.ChargingBoard.Verdicts[AgvA].Reason);
        (JsonDocument alarms, string html) = await ReadAsync(fleet, "charging-alarms");
        using (alarms)
        {
            JsonElement alarm = Assert.Single(
                alarms.RootElement.GetProperty("alarms").EnumerateArray(),
                a => a.GetProperty("code").GetString() == ChargingDashboardDescriptions.AlarmBelowMandatoryEligibilityHeld);
            Assert.Equal(AgvA, alarm.GetProperty("agvId").GetString());
            Assert.Equal(ChargingDashboardDescriptions.SeverityHigh, alarm.GetProperty("severity").GetString());
            string detail = alarm.GetProperty("detail").GetString()!;
            Assert.Contains("电量 20%", detail, StringComparison.Ordinal);
            Assert.Contains(holdId, detail, StringComparison.Ordinal);
            Assert.Contains(VehicleChargingEligibilityHoldReasons.InterruptionConfirmed, detail, StringComparison.Ordinal);
            Assert.Contains(Host.Runtime.ChargingStationEndpoints.VehicleRecoveryRoute, html, StringComparison.Ordinal);
            Assert.DoesNotContain(
                "调度",
                ChargingDashboardDescriptions.FieldActions[ChargingDashboardDescriptions.AlarmBelowMandatoryEligibilityHeld],
                StringComparison.Ordinal);
        }
        Assert.Empty(await fleet.Context.Set<ChargingCycleRow>().AsNoTracking().ToArrayAsync(Token));

        await holds.RecoverVehicleHoldAsync(
            new ChargingHoldRecovery(Guid.NewGuid().ToString("D"), holdId, "op-r11", "UNSTATED", fleet.Clock.GetUtcNow(), "电池检查"),
            Token);
        // Recovered after the pass that refused it: the card reads the hold closed and does not alarm on the stale verdict.
        (JsonDocument between, string _) = await ReadAsync(fleet, "charging-alarms");
        using (between)
        {
            Assert.DoesNotContain(
                between.RootElement.GetProperty("alarms").EnumerateArray(),
                a => a.GetProperty("code").GetString() == ChargingDashboardDescriptions.AlarmBelowMandatoryEligibilityHeld);
        }
        await RoundAsync(fleet);
        await fleet.HearFromEveryVehicleAsync();

        Assert.Single(await fleet.Context.Set<ChargingCycleRow>().AsNoTracking().ToArrayAsync(Token));
        (JsonDocument after, string _) = await ReadAsync(fleet, "charging-alarms");
        using (after)
        {
            Assert.DoesNotContain(
                after.RootElement.GetProperty("alarms").EnumerateArray(),
                a => a.GetProperty("code").GetString() == ChargingDashboardDescriptions.AlarmBelowMandatoryEligibilityHeld);
        }
    }

    /// <summary>高于强制充电线的车挂着资格暂停：它此刻不需要充电，不告警（control-server#877）；逐车卡片照旧写着这条暂停。</summary>
    [Fact]
    public async Task AVehicleAboveTheMandatoryLineWithItsChargingEligibilityHeldIsNotAlarmed()
    {
        await using FleetFixture fleet = await FleetAsync();
        await new ChargingHoldStore(fleet.Context).RecordVehicleHoldAsync(
            new VehicleChargingEligibilityHold(
                "cs877-above", "cs877-above", KeyA, null, VehicleChargingEligibilityHoldReasons.InterruptionConfirmed,
                fleet.Clock.GetUtcNow(), null),
            Token);
        fleet.Riot.BatteryByVehicle[KeyA] = 90;
        await RoundAsync(fleet);
        await fleet.HearFromEveryVehicleAsync();

        Assert.Equal(ChargingAllocationReasons.NotRequired, fleet.ChargingBoard.Verdicts[AgvA].Reason);
        (JsonDocument alarms, string _) = await ReadAsync(fleet, "charging-alarms");
        using (alarms)
        {
            Assert.DoesNotContain(
                alarms.RootElement.GetProperty("alarms").EnumerateArray(),
                a => a.GetProperty("code").GetString() == ChargingDashboardDescriptions.AlarmBelowMandatoryEligibilityHeld);
        }
    }

    /// <summary>
    /// 两辆车都低于强制充电线、各挂着一条资格暂停（cs#877 审查 S2）：各自一条告警，只写自己那一条暂停的 id。分配那一轮之后 A 的暂停解除了，
    /// A 不再告警，B 的照旧、仍只写 B 的那一条。
    /// </summary>
    [Fact]
    public async Task TwoVehiclesWithTheirEligibilityHeldAreEachAlarmedWithTheirOwnHold()
    {
        await using FleetFixture fleet = await FleetAsync(vehicles: 2);
        string agvB = FleetFixture.AgvIds[1];
        string keyB = FleetFixture.VehicleKeys[1];
        ChargingHoldStore holds = new(fleet.Context);
        DateTimeOffset heldAt = fleet.Clock.GetUtcNow();
        await holds.RecordVehicleHoldAsync(
            new VehicleChargingEligibilityHold(
                "cs877-hold-a", "cs877-hold-a", KeyA, null, VehicleChargingEligibilityHoldReasons.InterruptionConfirmed, heldAt, null),
            Token);
        await holds.RecordVehicleHoldAsync(
            new VehicleChargingEligibilityHold(
                "cs877-hold-b", "cs877-hold-b", keyB, null, VehicleChargingEligibilityHoldReasons.NoProgressConfirmed, heldAt, null),
            Token);
        fleet.Riot.BatteryByVehicle[KeyA] = 20;
        fleet.Riot.BatteryByVehicle[keyB] = 20;
        await RoundAsync(fleet);
        await fleet.HearFromEveryVehicleAsync();

        Assert.Equal(ChargingAllocationReasons.VehicleEligibilityHeld, fleet.ChargingBoard.Verdicts[AgvA].Reason);
        Assert.Equal(ChargingAllocationReasons.VehicleEligibilityHeld, fleet.ChargingBoard.Verdicts[agvB].Reason);
        (JsonDocument both, string _) = await ReadAsync(fleet, "charging-alarms");
        using (both)
        {
            JsonElement[] held =
            [
                .. both.RootElement.GetProperty("alarms").EnumerateArray()
                    .Where(a => a.GetProperty("code").GetString() == ChargingDashboardDescriptions.AlarmBelowMandatoryEligibilityHeld),
            ];
            string detailA = Assert.Single(held, a => a.GetProperty("agvId").GetString() == AgvA).GetProperty("detail").GetString()!;
            string detailB = Assert.Single(held, a => a.GetProperty("agvId").GetString() == agvB).GetProperty("detail").GetString()!;
            Assert.Contains("cs877-hold-a", detailA, StringComparison.Ordinal);
            Assert.DoesNotContain("cs877-hold-b", detailA, StringComparison.Ordinal);
            Assert.Contains("cs877-hold-b", detailB, StringComparison.Ordinal);
            Assert.DoesNotContain("cs877-hold-a", detailB, StringComparison.Ordinal);
        }

        await holds.RecoverVehicleHoldAsync(
            new ChargingHoldRecovery(Guid.NewGuid().ToString("D"), "cs877-hold-a", "op-r11", "UNSTATED", fleet.Clock.GetUtcNow(), "电池检查"),
            Token);
        (JsonDocument after, string _) = await ReadAsync(fleet, "charging-alarms");
        using (after)
        {
            JsonElement[] held =
            [
                .. after.RootElement.GetProperty("alarms").EnumerateArray()
                    .Where(a => a.GetProperty("code").GetString() == ChargingDashboardDescriptions.AlarmBelowMandatoryEligibilityHeld),
            ];
            JsonElement only = Assert.Single(held);
            Assert.Equal(agvB, only.GetProperty("agvId").GetString());
            Assert.DoesNotContain("cs877-hold-a", only.GetProperty("detail").GetString()!, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// 充电分配停跑（最近一轮已完成的分配超过三个轮询间隔）时，暂停还挂着也不拿那一轮的结论告警（cs#877 审查 S2；同 control-server#865 的窗口）。
    /// </summary>
    [Fact]
    public async Task AStalePassRaisesNoEligibilityHeldAlarm()
    {
        await using FleetFixture fleet = await FleetAsync();
        await new ChargingHoldStore(fleet.Context).RecordVehicleHoldAsync(
            new VehicleChargingEligibilityHold(
                "cs877-stale", "cs877-stale", KeyA, null, VehicleChargingEligibilityHoldReasons.InterruptionConfirmed,
                fleet.Clock.GetUtcNow(), null),
            Token);
        fleet.Riot.BatteryByVehicle[KeyA] = 20;
        await fleet.HearFromEveryVehicleAsync();
        await RoundAsync(fleet);
        Assert.Equal(ChargingAllocationReasons.VehicleEligibilityHeld, fleet.ChargingBoard.Verdicts[AgvA].Reason);

        fleet.Clock.Advance(fleet.Options.PollInterval * ChargingVehiclesQueryEndpoint.PassLivenessPollIntervals + TimeSpan.FromSeconds(1));
        await fleet.HearFromEveryVehicleAsync();

        (JsonDocument alarms, string _) = await ReadAsync(fleet, "charging-alarms");
        using (alarms)
        {
            Assert.DoesNotContain(
                alarms.RootElement.GetProperty("alarms").EnumerateArray(),
                a => a.GetProperty("code").GetString() == ChargingDashboardDescriptions.AlarmBelowMandatoryEligibilityHeld);
        }
    }

    /// <summary>高于强制充电线的车仓门没锁：它本来就不需要充电，不告警（control-server#865）。</summary>
    [Fact]
    public async Task AVehicleAboveTheMandatoryLineWithAnUnlockedSlotIsNotAlarmed()
    {
        await using FleetFixture fleet = await FleetAsync();
        fleet.Riot.BatteryByVehicle[KeyA] = 90;
        await fleet.ReplaceSafetySnapshotAsync(AgvA, unlockedSlot: 3);
        await fleet.HearFromEveryVehicleAsync();
        await RoundAsync(fleet);
        await fleet.HearFromEveryVehicleAsync();

        Assert.Equal(ChargingAllocationReasons.NotRequired, fleet.ChargingBoard.Verdicts[AgvA].Reason);
        (JsonDocument alarms, string _) = await ReadAsync(fleet, "charging-alarms");
        using (alarms)
        {
            Assert.DoesNotContain(
                alarms.RootElement.GetProperty("alarms").EnumerateArray(),
                a => a.GetProperty("code").GetString() == ChargingDashboardDescriptions.AlarmBelowMandatoryDepartureNotProven);
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
            "charging-alarms" => new ChargingAlarmsQueryEndpoint(options, fleet.ChargingBoard, fleet.Clock),
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
