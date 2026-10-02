using System.Text.Json;
using ControlServer.Application;
using ControlServer.Dashboard;
using ControlServer.Domain;
using ControlServer.Host.Dashboard;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Charging;
using ControlServer.Host.Runtime.Faults;
using ControlServer.Host.Runtime.Fleet;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ControlServer.Tests;

/// <summary>
/// 批次9-10（control-server#408）的四张充电卡片：逐车充电状态、充电桩、充电暂停与等待、充电告警。每张卡片的数据面在迁移建出的库上读种好的行，
/// 再交给卡片渲染，断言字段与写出来的字。
/// </summary>
/// <remarks>
/// 名册五辆车：<c>AGV-01</c>～<c>AGV-04</c> 听得到，<c>AGV-05</c> 的会话行在、却六秒内没说过话——它是失联车，逐车卡片只把它列进
/// <c>unavailableVehicles</c>；它被持有的桩、它的人工充电等待是服务端的记录，照样出现并标出失联。
/// </remarks>
public sealed class ChargingDashboardTests
{
    private const int MapId = 26;

    private static readonly string[] StaleWords =
    [
        "已过期", "过期", "陈旧", "上次更新", "上一次更新", "最后更新",
        "stale", "expired", "lastUpdated", "LastUpdated", "last updated",
    ];

    // ---------------- 充电桩 ----------------

    /// <summary>
    /// 桩的每一种阶段各一个：空闲、预占、占用、充满待离桩（超过告警窗口）、清桩中（人工确认已记下、旧单还没终结）、失败待放桩（还在窗口内），
    /// 外加一个不在当前名册里仍被失联车持有的桩。每一行写明谁占着、哪个阶段、自何时、为什么还没放。
    /// </summary>
    [Fact]
    public async Task EveryChargerShowsWhoHoldsItInWhichStageSinceWhenAndWhyItIsNotReleased()
    {
        await using ChargingDatabase database = await ChargingDatabase.CreateAsync();
        DateTimeOffset now = database.Now;
        await database.SeedAsync(context =>
        {
            AddRoster(context, 1, now.AddDays(-2), "张三", [211, 218]);
            AddRoster(context, 2, now.AddDays(-1), "李四", [211, 212, 213, 214, 215, 216], changeNote: "去掉 218");
            AddCharging(context, "K-01", 212, ChargingCyclePhases.Active, ChargingCycleWireStates.Allocated,
                StationExclusivityStates.Reserved, now.AddMinutes(-1));
            AddCharging(context, "K-02", 213, ChargingCyclePhases.Active, ChargingCycleWireStates.Charging,
                StationExclusivityStates.Occupied, now.AddMinutes(-20));
            ChargingCycleRow full = AddCharging(context, "K-03", 214, ChargingCyclePhases.Active, ChargingCycleWireStates.Complete,
                StationExclusivityStates.Occupied, now.AddHours(-1));
            full.CompletedAt = now.AddMinutes(-15);
            ChargingCycleRow clearing = AddCharging(context, "K-04", 215, ChargingCyclePhases.Clearing,
                ChargingCycleWireStates.UnableToCharge, StationExclusivityStates.Occupied, now.AddMinutes(-30),
                purpose: VehiclePurposes.ClearingMaintenance, code: ChargingExecutionReasons.ClearedOldOrderUnsettled);
            context.Add(Clearance(clearing, now.AddMinutes(-25), confirmedAt: now.AddMinutes(-5), completedAt: null));
            AddStationHold(context, 215, ChargingStationHoldTriggers.UnableToChargeConfirmed, now.AddMinutes(-25), "K-04", clearing.CycleId);
            ChargingCycleRow failed = AddCharging(context, "K-01", 216, ChargingCyclePhases.Ended, ChargingCycleWireStates.NotCharging,
                StationExclusivityStates.Reserved, now.AddMinutes(-3), purpose: null, open: false);
            failed.EndReason = ChargingExecutionReasons.OrderEnded;
            failed.EndedAt = now.AddMinutes(-2);
            AddCharging(context, "K-05", 218, ChargingCyclePhases.Active, ChargingCycleWireStates.Charging,
                StationExclusivityStates.Occupied, now.AddMinutes(-40), rosterVersion: 1);
        });

        (JsonDocument fact, string html) = await ReadAsync(database, "chargers");
        using (fact)
        {
            JsonElement root = fact.RootElement;
            Assert.False(root.GetProperty("rosterEmpty").GetBoolean());
            Assert.Equal(JsonValueKind.Null, root.GetProperty("rosterEmptyBanner").ValueKind);
            JsonElement roster = root.GetProperty("roster");
            Assert.Equal(("ACTIVE", 2L, "李四", 6), (roster.GetProperty("state").GetString(), roster.GetProperty("version").GetInt64(),
                roster.GetProperty("approvedBy").GetString(), roster.GetProperty("chargerCount").GetInt32()));
            Assert.Equal([2L, 1L], root.GetProperty("rosterHistory").EnumerateArray().Select(v => v.GetProperty("version").GetInt64()));

            Dictionary<int, JsonElement> chargers = root.GetProperty("chargers").EnumerateArray()
                .ToDictionary(c => c.GetProperty("stationId").GetInt32());
            Assert.Equal([211, 212, 213, 214, 215, 216, 218], chargers.Keys);
            Assert.Equal(ChargingDashboardDescriptions.StageFree, Stage(chargers[211]));
            Assert.Equal(ChargingDashboardDescriptions.StageReserved, Stage(chargers[212]));
            Assert.Equal(ChargingDashboardDescriptions.StageOccupied, Stage(chargers[213]));
            Assert.Equal(ChargingDashboardDescriptions.StageCompleteAwaitingDeparture, Stage(chargers[214]));
            Assert.Equal(ChargingDashboardDescriptions.StageClearing, Stage(chargers[215]));
            Assert.Equal(ChargingDashboardDescriptions.StageFailedCycleAwaitingRelease, Stage(chargers[216]));
            Assert.Equal(ChargingDashboardDescriptions.StageOccupied, Stage(chargers[218]));

            // 充满待离桩从充满的时刻算起，超过 10 分钟告警窗口就标出来，指到事件 2249；失败待放桩还在窗口里，不标。
            Assert.Equal(now.AddMinutes(-15), chargers[214].GetProperty("stageSince").GetDateTimeOffset());
            Assert.True(chargers[214].GetProperty("overdue").GetBoolean());
            Assert.Contains("2249", chargers[214].GetProperty("overdueDescription").GetString(), StringComparison.Ordinal);
            Assert.False(chargers[216].GetProperty("overdue").GetBoolean());
            Assert.Equal(now.AddMinutes(-2), chargers[216].GetProperty("stageSince").GetDateTimeOffset());
            Assert.Equal(ChargingExecutionReasons.OrderEnded, chargers[216].GetProperty("cycle").GetProperty("endReason").GetString());

            // 状态说明按种类取：充电桩的「占用」不写「离点证据」（cs#392 审查对齐第 1 条）。
            JsonElement occupied = chargers[213].GetProperty("holding");
            Assert.Equal(StationExclusivityKinds.Charger, occupied.GetProperty("stationKind").GetString());
            Assert.Equal(DashboardDescriptions.ChargerStates[StationExclusivityStates.Occupied],
                occupied.GetProperty("statusDescription").GetString());
            Assert.DoesNotContain("离点证据", occupied.GetProperty("statusDescription").GetString(), StringComparison.Ordinal);

            // 清桩中：持有者认得 CLEARING_MAINTENANCE（cs#392 审查对齐第 3 条）；人工确认时刻与清桩完成分开写。
            JsonElement clearing = chargers[215];
            Assert.Equal("清桩中的充电旅程", clearing.GetProperty("holding").GetProperty("holderKind").GetString());
            Assert.Equal(ChargingExecutionReasons.ClearedOldOrderUnsettled, clearing.GetProperty("journeyCode").GetString());
            Assert.Equal(now.AddMinutes(-5), clearing.GetProperty("clearance").GetProperty("confirmedAt").GetDateTimeOffset());
            Assert.Equal(JsonValueKind.Null, clearing.GetProperty("clearance").GetProperty("completedAt").ValueKind);
            Assert.True(clearing.GetProperty("allocationHeld").GetBoolean());

            // 不在当前名册里仍被持有：照样列出、写明，持有车失联时只标失联。
            JsonElement orphan = chargers[218];
            Assert.Equal(ChargersQueryEndpoint.NotInCurrentRosterStillHeld, orphan.GetProperty("rosterNote").GetString());
            Assert.False(orphan.GetProperty("holding").GetProperty("holderInContact").GetBoolean());

            string full = RowWith(html, "<td>214</td>");
            Assert.Contains("COMPLETE_AWAITING_DEPARTURE：充满待离桩", full, StringComparison.Ordinal);
            Assert.Contains("事件 2249", full, StringComparison.Ordinal);
            string clearingRow = RowWith(html, "<td>215</td>");
            Assert.Contains("CLEARING：清桩中", clearingRow, StringComparison.Ordinal);
            Assert.Contains("CHARGING_CLEARED_OLD_ORDER_UNSETTLED：人工清桩确认已记下", clearingRow, StringComparison.Ordinal);
            Assert.Contains("人工确认已记下于", clearingRow, StringComparison.Ordinal);
            Assert.Contains("清桩还没完成", clearingRow, StringComparison.Ordinal);
            Assert.Contains("UNABLE_TO_CHARGE_CONFIRMED：充不上", clearingRow, StringComparison.Ordinal);
            Assert.Contains("清桩中的充电旅程", clearingRow, StringComparison.Ordinal);
            Assert.Contains("FAILED_CYCLE_AWAITING_RELEASE：失败待放桩", RowWith(html, "<td>216</td>"), StringComparison.Ordinal);
            Assert.Contains("CHARGING_ORDER_ENDED：", RowWith(html, "<td>216</td>"), StringComparison.Ordinal);
            Assert.Contains("AGV-05（车辆失联", RowWith(html, "<td>218</td>"), StringComparison.Ordinal);
            Assert.Contains("FREE：空闲", RowWith(html, "<td>211</td>"), StringComparison.Ordinal);
            Assert.Contains(ChargingStationEndpoints.ClearanceRoute, html, StringComparison.Ordinal);
            AssertCleanHtml(html);
        }
    }

    /// <summary>
    /// 四种暂停来源各一次：充不上与维修暂停在名册里的桩上，中断与无进展的暂停在已不在名册里的桩上（暂停不随名册消失）；已解除的暂停不列。
    /// 每一条写出 REQ-0177 显示得下的那几项，全文在数据面里。
    /// </summary>
    [Fact]
    public async Task EveryPauseSourceIsShownOnItsChargerAndARecoveredPauseIsNot()
    {
        await using ChargingDatabase database = await ChargingDatabase.CreateAsync();
        DateTimeOffset now = database.Now;
        await database.SeedAsync(context =>
        {
            AddRoster(context, 1, now.AddDays(-1), "张三", [211, 212, 213]);
            AddStationHold(context, 211, ChargingStationHoldTriggers.UnableToChargeConfirmed, now.AddMinutes(-9), "K-01", "cycle-1");
            ChargingStationAllocationHoldRow maintenance =
                AddStationHold(context, 212, ChargingStationHoldTriggers.Maintenance, now.AddMinutes(-8), null, null);
            maintenance.ConfirmedByPersonId = "P-007";
            maintenance.ConfirmedByRole = "R-11";
            maintenance.SiteDisposition = "等厂家";
            AddStationHold(context, 221, ChargingStationHoldTriggers.InterruptionConfirmed, now.AddMinutes(-7), "K-02", "cycle-2");
            AddStationHold(context, 222, ChargingStationHoldTriggers.NoProgressConfirmed, now.AddMinutes(-6), "K-03", "cycle-3");
            ChargingStationAllocationHoldRow recovered =
                AddStationHold(context, 213, ChargingStationHoldTriggers.Maintenance, now.AddMinutes(-60), null, null);
            context.Add(new ChargingStationRecoveryRow
            {
                RecoveryId = "R-1",
                HoldId = recovered.HoldId,
                RecoveredBy = "P-007",
                RecovererRole = "R-11",
                RecoveredAt = now.AddMinutes(-30),
                Basis = "修好了",
            });
        });

        (JsonDocument fact, string html) = await ReadAsync(database, "chargers");
        using (fact)
        {
            Dictionary<int, JsonElement> chargers = fact.RootElement.GetProperty("chargers").EnumerateArray()
                .ToDictionary(c => c.GetProperty("stationId").GetInt32());
            Assert.Equal([211, 212, 213, 221, 222], chargers.Keys);
            Assert.Equal(ChargingStationHoldTriggers.UnableToChargeConfirmed, Trigger(chargers[211]));
            Assert.Equal(ChargingStationHoldTriggers.Maintenance, Trigger(chargers[212]));
            Assert.False(chargers[213].GetProperty("allocationHeld").GetBoolean());
            Assert.Equal(ChargingStationHoldTriggers.InterruptionConfirmed, Trigger(chargers[221]));
            Assert.Equal(ChargingStationHoldTriggers.NoProgressConfirmed, Trigger(chargers[222]));
            Assert.Equal(ChargersQueryEndpoint.NotInCurrentRosterOnHold, chargers[221].GetProperty("rosterNote").GetString());
            JsonElement hold = chargers[212].GetProperty("allocationHolds")[0];
            Assert.Equal("P-007", hold.GetProperty("confirmedByPersonId").GetString());
            Assert.Equal("UNKNOWN", hold.GetProperty("full").GetProperty("rootCause").GetString());

            Assert.Contains("UNABLE_TO_CHARGE_CONFIRMED：充不上", RowWith(html, "<td>211</td>"), StringComparison.Ordinal);
            string maintenanceRow = RowWith(html, "<td>212</td>");
            Assert.Contains("MAINTENANCE：维修", maintenanceRow, StringComparison.Ordinal);
            Assert.Contains("确认人 P-007（R-11）", maintenanceRow, StringComparison.Ordinal);
            Assert.Contains("现场处置 等厂家", maintenanceRow, StringComparison.Ordinal);
            Assert.Contains("全部字段见数据面 /api/dashboard/chargers", maintenanceRow, StringComparison.Ordinal);
            Assert.Contains("<td>没有暂停</td>", RowWith(html, "<td>213</td>"), StringComparison.Ordinal);
            Assert.Contains("INTERRUPTION_CONFIRMED：充电中断", RowWith(html, "<td>221</td>"), StringComparison.Ordinal);
            Assert.Contains("NO_PROGRESS_CONFIRMED：充电无进展", RowWith(html, "<td>222</td>"), StringComparison.Ordinal);
            AssertCleanHtml(html);
        }
    }

    /// <summary>
    /// 名册为空（有版本、零个桩）与从没导入过：整张卡片顶上醒目写明「名册为空：自动充电已停，需要充电的车等人工充电」，并列出正在等人工充电的车
    /// （两种原因各一辆，其中一辆失联也照列）。告警卡片同时有一条名册为空的告警。
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnEmptyRosterIsShownProminentlyWithTheVehiclesWaitingForManualCharging(bool imported)
    {
        await using ChargingDatabase database = await ChargingDatabase.CreateAsync();
        DateTimeOffset now = database.Now;
        await database.SeedAsync(context =>
        {
            if (imported)
            {
                AddRoster(context, 1, now.AddDays(-1), "张三", [211]);
                AddRoster(context, 2, now.AddMinutes(-30), "李四", [], changeNote: "211 并行期窗口外置空");
            }
            AddManualHold(context, "K-02", ManualChargingHoldReasons.RosterEmpty, now.AddMinutes(-20));
            AddManualHold(context, "K-05", ManualChargingHoldReasons.ChargingRepeatedlyFailed, now.AddMinutes(-10));
        });

        (JsonDocument fact, string html) = await ReadAsync(database, "chargers");
        using (fact)
        {
            JsonElement root = fact.RootElement;
            Assert.True(root.GetProperty("rosterEmpty").GetBoolean());
            Assert.Equal(ChargersQueryEndpoint.RosterEmptyBanner, root.GetProperty("rosterEmptyBanner").GetString());
            Assert.Equal(imported ? "EMPTY" : "NEVER_IMPORTED", root.GetProperty("roster").GetProperty("state").GetString());
            Assert.Equal(["AGV-02", "AGV-05"],
                root.GetProperty("manualChargingHoldVehicles").EnumerateArray().Select(v => v.GetProperty("agvId").GetString()));
            Assert.Empty(root.GetProperty("chargers").EnumerateArray());

            Assert.StartsWith(
                "<section class=\"charger-roster-empty alarm\" style=\"border:2px solid #c00;background:#f8d7da;color:#900\">"
                + "<p style=\"font-size:1.4em;font-weight:bold\">名册为空：自动充电已停，需要充电的车等人工充电</p>",
                html, StringComparison.Ordinal);
            Assert.Contains("正在等人工充电的车：AGV-02（ROSTER_EMPTY，自 ", html, StringComparison.Ordinal);
            Assert.Contains("AGV-05（CHARGING_REPEATEDLY_FAILED，自 ", html, StringComparison.Ordinal);
            AssertCleanHtml(html);
        }

        (JsonDocument alarms, string alarmHtml) = await ReadAsync(database, "charging-alarms");
        using (alarms)
        {
            JsonElement roster = alarms.RootElement.GetProperty("alarms").EnumerateArray()
                .Single(a => a.GetProperty("code").GetString() is ChargingDashboardDescriptions.AlarmRosterEmpty
                    or ChargingDashboardDescriptions.AlarmRosterNeverImported);
            Assert.Equal(imported ? ChargingDashboardDescriptions.AlarmRosterEmpty : ChargingDashboardDescriptions.AlarmRosterNeverImported,
                roster.GetProperty("code").GetString());
            Assert.Equal(ChargingDashboardDescriptions.SeverityHigh, roster.GetProperty("severity").GetString());
            Assert.Equal("正在等人工充电的车：AGV-02、AGV-05", roster.GetProperty("detail").GetString());
            Assert.Contains("人工充电", roster.GetProperty("fieldAction").GetString(), StringComparison.Ordinal);
            AssertCleanHtml(alarmHtml);
        }
    }

    /// <summary>名册为空时没有车在等：横幅照样在，写明此刻没有车在等人工充电。</summary>
    [Fact]
    public async Task AnEmptyRosterWithNobodyWaitingStillSaysSo()
    {
        await using ChargingDatabase database = await ChargingDatabase.CreateAsync();
        await database.SeedAsync(context => AddRoster(context, 1, database.Now.AddDays(-1), "张三", []));

        (JsonDocument fact, string html) = await ReadAsync(database, "chargers");
        using (fact)
        {
            Assert.Contains(ChargersQueryEndpoint.RosterEmptyBanner, html, StringComparison.Ordinal);
            Assert.Contains("此刻没有车在等人工充电", html, StringComparison.Ordinal);
        }
    }

    // ---------------- 逐车充电状态 ----------------

    /// <summary>
    /// 每台车的用途、chargingCycleState、阶段、目标桩、名册与策略版本、旅程上的码（含建单结果未知），人工充电等待；服务启动以来还没完成过一轮
    /// 充电分配时，电量、batteryState 与排队原因写没评估（不摆任何值）；失联车只进失联列表，不出现它失联前的周期。
    /// </summary>
    [Fact]
    public async Task EachVehicleShowsItsPurposeCycleTargetAndVersionsAndBatteryIsNotEvaluatedBeforeAnyPass()
    {
        await using ChargingDatabase database = await ChargingDatabase.CreateAsync();
        DateTimeOffset now = database.Now;
        await database.SeedAsync(context =>
        {
            AddRoster(context, 3, now.AddDays(-1), "张三", [211, 212, 213]);
            AddCharging(context, "K-02", 211, ChargingCyclePhases.Active, ChargingCycleWireStates.Allocated,
                StationExclusivityStates.Reserved, now.AddMinutes(-2),
                code: ChargingExecutionReasons.LegOutcomeCode(MovementDispatchOutcome.ResultUnknown), rosterVersion: 3, policyVersion: 7);
            AddCharging(context, "K-03", 212, ChargingCyclePhases.Clearing, ChargingCycleWireStates.UnableToCharge,
                StationExclusivityStates.Occupied, now.AddMinutes(-30), purpose: VehiclePurposes.ClearingMaintenance,
                code: ChargingExecutionReasons.UnableToChargeClearing, rosterVersion: 3);
            AddManualHold(context, "K-04", ManualChargingHoldReasons.RosterEmpty, now.AddMinutes(-20));
            AddCharging(context, "K-05", 213, ChargingCyclePhases.Active, ChargingCycleWireStates.Charging,
                StationExclusivityStates.Occupied, now.AddMinutes(-40), rosterVersion: 3);
        });

        (JsonDocument fact, string html) = await ReadAsync(database, "charging-vehicles");
        using (fact)
        {
            JsonElement root = fact.RootElement;
            JsonElement[] vehicles = [.. root.GetProperty("vehicles").EnumerateArray()];
            Assert.Equal(["AGV-01", "AGV-02", "AGV-03", "AGV-04"], vehicles.Select(v => v.GetProperty("agvId").GetString()));
            Assert.Equal(ChargingCycleWireStates.NotCharging, vehicles[0].GetProperty("chargingCycleState").GetString());
            Assert.Equal(JsonValueKind.Null, vehicles[0].GetProperty("cycle").ValueKind);

            JsonElement allocated = vehicles[1];
            Assert.Equal(ChargingCycleWireStates.Allocated, allocated.GetProperty("chargingCycleState").GetString());
            Assert.Equal((211, "CHG-211", 3L, 7L), (allocated.GetProperty("cycle").GetProperty("targetStationId").GetInt32(),
                allocated.GetProperty("cycle").GetProperty("targetStationName").GetString(),
                allocated.GetProperty("cycle").GetProperty("chargerRosterVersion").GetInt64(),
                allocated.GetProperty("cycle").GetProperty("chargingPolicyVersion").GetInt64()));
            Assert.Equal("CHARGER_ResultUnknown", allocated.GetProperty("journeyCode").GetString());

            JsonElement clearing = vehicles[2];
            Assert.Equal(VehiclePurposes.ClearingMaintenance, clearing.GetProperty("purpose").GetString());
            Assert.Equal("CLEARING_MAINTENANCE", clearing.GetProperty("holderKind").GetString());
            Assert.Equal(ChargingCyclePhases.Clearing, clearing.GetProperty("cycle").GetProperty("phase").GetString());
            Assert.Equal(ManualChargingHoldReasons.RosterEmpty,
                vehicles[3].GetProperty("manualChargingHold").GetProperty("reason").GetString());
            Assert.Equal(["agvId", "reason"],
                root.GetProperty("unavailableVehicles")[0].EnumerateObject().Select(p => p.Name));
            Assert.Equal("AGV-05", root.GetProperty("unavailableVehicles")[0].GetProperty("agvId").GetString());
            // 没评估时电量这一格只有那一句话：数据面里不存在可以误读成现状的数。
            JsonElement allocation = vehicles[1].GetProperty("allocation");
            Assert.Equal(ChargingDashboardDescriptions.NoPassCompletedYet, allocation.GetProperty("note").GetString());
            Assert.Equal(JsonValueKind.Null, allocation.GetProperty("batteryPercent").ValueKind);
            Assert.Equal(JsonValueKind.Null, allocation.GetProperty("reason").ValueKind);

            string allocatedRow = RowOf(html, "AGV-02");
            Assert.Equal(ChargingDashboardDescriptions.NoPassCompletedYet, Cell(allocatedRow, 1));
            Assert.Equal("", Cell(allocatedRow, 2));
            Assert.Contains("ALLOCATED：已分配", Cell(allocatedRow, 4), StringComparison.Ordinal);
            Assert.Contains("26/211 CHG-211", Cell(allocatedRow, 6), StringComparison.Ordinal);
            Assert.Equal("充电桩名册 v3", Cell(allocatedRow, 7));
            Assert.Equal("充电策略 v7", Cell(allocatedRow, 8));
            Assert.Contains("CHARGER_ResultUnknown：开往充电桩的单发给 RIoT 之后结果未知", Cell(allocatedRow, 9), StringComparison.Ordinal);
            Assert.Equal(ChargingDashboardDescriptions.NoPassCompletedYet, Cell(allocatedRow, 10));
            Assert.Contains("CLEARING_MAINTENANCE（清桩或维护", RowOf(html, "AGV-03"), StringComparison.Ordinal);
            Assert.Contains("清桩中的充电旅程", RowOf(html, "AGV-03"), StringComparison.Ordinal);
            Assert.Contains("CHARGING_UNABLE_TO_CHARGE：已确认充不上", RowOf(html, "AGV-03"), StringComparison.Ordinal);
            Assert.Contains("ROSTER_EMPTY：名册为空", RowOf(html, "AGV-04"), StringComparison.Ordinal);
            AssertOnlyLost(html);
            AssertCleanHtml(html);
        }
    }

    /// <summary>
    /// 电量、batteryState 与排队原因只读最近一轮已完成的分配：后开的一轮还没走完时看到的是上一轮完整的值；车不在那一轮里写没评估，
    /// 不拿它更早的值。
    /// </summary>
    [Fact]
    public async Task BatteryAndQueueReasonComeOnlyFromTheLatestCompletedPass()
    {
        await using ChargingDatabase database = await ChargingDatabase.CreateAsync();
        // 时效窗口按真实时钟判：建库迁移要几秒，所以这里取板子写入那一刻的时钟，不用建库时的 Now。
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ChargingAllocationBoard board = new();
        long first = board.BeginPass(now.AddSeconds(-2));
        board.RecordObservation("AGV-02", 18, "NO_CHARGE", "MANDATORY_CHARGE", now.AddSeconds(-2));
        board.Record("AGV-02", ChargingAllocationReasons.NoChargerAvailable, "211=CHARGER_RESERVED_OR_OCCUPIED");
        board.RecordObservation("AGV-03", 64, "NO_CHARGE", "SUFFICIENT", now.AddSeconds(-2));
        board.Record("AGV-03", ChargingAllocationReasons.NotRequired, "");
        board.EndPass(first, now.AddSeconds(-1));
        board.BeginPass(now);
        board.RecordObservation("AGV-02", 99, "CHARGING", "SUFFICIENT", now);
        board.Record("AGV-02", ChargingAllocationReasons.Committed, "half-finished pass");
        board.Record("AGV-01", ChargingAllocationReasons.NotRequired, "");

        (JsonDocument fact, string html) = await ReadAsync(database, "charging-vehicles", board);
        using (fact)
        {
            Dictionary<string, JsonElement> allocation = fact.RootElement.GetProperty("vehicles").EnumerateArray()
                .ToDictionary(v => v.GetProperty("agvId").GetString()!, v => v.GetProperty("allocation"));
            Assert.Equal((18, "MANDATORY_CHARGE", ChargingAllocationReasons.NoChargerAvailable),
                (allocation["AGV-02"].GetProperty("batteryPercent").GetInt32(), allocation["AGV-02"].GetProperty("batteryState").GetString(),
                    allocation["AGV-02"].GetProperty("reason").GetString()));
            Assert.Equal(now.AddSeconds(-2), allocation["AGV-02"].GetProperty("batteryObservedAt").GetDateTimeOffset());
            Assert.Equal(ChargingDashboardDescriptions.NotEvaluatedThisPass, allocation["AGV-01"].GetProperty("note").GetString());
            Assert.Equal(JsonValueKind.Null, allocation["AGV-01"].GetProperty("reason").ValueKind);

            string row = RowOf(html, "AGV-02");
            Assert.StartsWith("18%（RIoT 读数，观测于 ", Cell(row, 1), StringComparison.Ordinal);
            Assert.Contains("RIoT 电池状态 NO_CHARGE", Cell(row, 1), StringComparison.Ordinal);
            Assert.StartsWith("MANDATORY_CHARGE：强制充电", Cell(row, 2), StringComparison.Ordinal);
            Assert.StartsWith("CHARGING_NO_CHARGER_AVAILABLE：无合格桩", Cell(row, 10), StringComparison.Ordinal);
            Assert.Contains("细节：211=CHARGER_RESERVED_OR_OCCUPIED", Cell(row, 10), StringComparison.Ordinal);
            Assert.StartsWith("64%", Cell(RowOf(html, "AGV-03"), 1), StringComparison.Ordinal);
            Assert.Equal(ChargingDashboardDescriptions.NotEvaluatedThisPass, Cell(RowOf(html, "AGV-01"), 1));
            AssertCleanHtml(html);
        }
    }

    /// <summary>
    /// 最近一轮走完已超过 3 个轮询间隔（按实际配置）：分配没在跑，电量与排队原因都写没评估并列全原因，不显示更早的值；窗口内照常显示。
    /// 用途与周期这些库里的事实不受影响。
    /// </summary>
    [Theory]
    [InlineData(5, true)]
    [InlineData(7, false)]
    public async Task APassOlderThanThreeConfiguredPollIntervalsIsNotShown(int secondsAgo, bool shown)
    {
        await using ChargingDatabase database = await ChargingDatabase.CreateAsync();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await database.SeedAsync(context =>
        {
            AddRoster(context, 2, now.AddDays(-1), "张三", [211]);
            AddCharging(context, "K-02", 211, ChargingCyclePhases.Active, ChargingCycleWireStates.Charging,
                StationExclusivityStates.Occupied, now.AddMinutes(-3));
        });
        ChargingAllocationBoard board = new();
        long pass = board.BeginPass(now.AddSeconds(-secondsAgo - 1));
        board.RecordObservation("AGV-01", 55, "NO_CHARGE", "SUFFICIENT", now.AddSeconds(-secondsAgo - 1));
        board.Record("AGV-01", ChargingAllocationReasons.NotRequired, "");
        board.EndPass(pass, now.AddSeconds(-secondsAgo));
        // 轮询间隔 2 秒：窗口 6 秒。
        IOptions<JourneyRuntimeOptions> options = Options.Create(new JourneyRuntimeOptions
        {
            MapId = MapId,
            PollInterval = TimeSpan.FromSeconds(2),
            Fleet = FleetOptions.Value.Fleet,
        });

        (JsonDocument fact, string html) = await ReadAsync(database, "charging-vehicles", board, options);
        using (fact)
        {
            Dictionary<string, JsonElement> vehicles = fact.RootElement.GetProperty("vehicles").EnumerateArray()
                .ToDictionary(v => v.GetProperty("agvId").GetString()!);
            JsonElement allocation = vehicles["AGV-01"].GetProperty("allocation");
            if (shown)
            {
                Assert.Equal(55, allocation.GetProperty("batteryPercent").GetInt32());
                Assert.Equal(JsonValueKind.Null, allocation.GetProperty("note").ValueKind);
            }
            else
            {
                Assert.Equal(ChargingDashboardDescriptions.PassNotRunning(TimeSpan.FromSeconds(6)), allocation.GetProperty("note").GetString());
                Assert.Equal(JsonValueKind.Null, allocation.GetProperty("batteryPercent").ValueKind);
                Assert.Equal(JsonValueKind.Null, allocation.GetProperty("reason").ValueKind);
                Assert.Contains("最近 6 秒内没有完成过一轮充电分配", Cell(RowOf(html, "AGV-01"), 1), StringComparison.Ordinal);
                foreach (string cause in new[] { "全车队没有空闲车", "MesIngest 读不到", "引擎这一轮出错", "一轮跑得太慢" })
                {
                    Assert.Contains(cause, Cell(RowOf(html, "AGV-01"), 10), StringComparison.Ordinal);
                }
                Assert.DoesNotContain("55%", html, StringComparison.Ordinal);
            }
            Assert.Equal(ChargingCycleWireStates.Charging, vehicles["AGV-02"].GetProperty("chargingCycleState").GetString());
        }
    }

    /// <summary>窗口是 3 个轮询间隔，与空闲返回结论那一格同一个数（cs#392）；改它要改这条用例。</summary>
    [Fact]
    public void TheLivenessWindowIsThreePollIntervalsLikeTheIdleReturnVerdicts()
    {
        Assert.Equal(3, ChargingVehiclesQueryEndpoint.PassLivenessPollIntervals);
        Assert.Equal(IdleReturnsQueryEndpoint.PassLivenessPollIntervals, ChargingVehiclesQueryEndpoint.PassLivenessPollIntervals);
    }

    /// <summary>两轮重叠时，先开的一轮凭旧令牌提交不了后一轮的半截暂存；后一轮自己走完才发布。</summary>
    [Fact]
    public void AnOverlappedPassCannotPublishTheNextPassesHalfFinishedStaging()
    {
        ChargingAllocationBoard board = new();
        DateTimeOffset at = DateTimeOffset.UnixEpoch;
        long first = board.BeginPass(at);
        long second = board.BeginPass(at.AddSeconds(1));
        board.Record("AGV-01", ChargingAllocationReasons.NotRequired, "");
        board.EndPass(first, at.AddSeconds(2));
        Assert.Null(board.LatestCompletedPass);
        board.EndPass(second, at.AddSeconds(3));
        Assert.Equal(ChargingAllocationReasons.NotRequired, board.LatestCompletedPass!.Verdicts["AGV-01"].Reason);
    }

    /// <summary>阻断卡片上，充电旅程行的共用码用充电口径（不写「重建」「需求」）；同一个码在搬运行上照旧是搬运口径。</summary>
    [Fact]
    public async Task TheBlockedJourneyCardDescribesASharedCodeOnAChargingRowInChargingTerms()
    {
        await using ChargingDatabase database = await ChargingDatabase.CreateAsync();
        DateTimeOffset now = database.Now;
        await database.SeedAsync(context =>
        {
            AddRoster(context, 2, now.AddDays(-1), "张三", [211]);
            AddCharging(context, "K-01", 211, ChargingCyclePhases.Active, ChargingCycleWireStates.EnRoute,
                StationExclusivityStates.Reserved, now.AddMinutes(-3), code: JourneyRuntimeEngine.OrderHangReason);
            JourneyRuntimeRow transport = WaitingJourneyBatteryWatchTests.Runtime("D-HANG", JourneyRuntimeStage.AwaitingGateArrival, now);
            transport.SetBlockReason(JourneyRuntimeEngine.OrderHangReason, now);
            context.Add(transport);
        });

        await using ControlServerDbContext context = database.NewContext();
        object rows = await new BlockedJourneysQueryEndpoint().ReadAsync(context, TestContext.Current.CancellationToken);
        using JsonDocument fact = JsonDocument.Parse(JsonSerializer.Serialize(rows));
        JsonElement[] journeys = [.. fact.RootElement.GetProperty("journeys").EnumerateArray()];
        string charging = journeys.Single(row => row.GetProperty("agvId").GetString() == "AGV-01")
            .GetProperty("blockReasonDescription").GetString()!;
        Assert.Equal(ChargingDashboardDescriptions.SharedCodesOnChargingJourneys[JourneyRuntimeEngine.OrderHangReason], charging);
        Assert.DoesNotContain("重建", charging, StringComparison.Ordinal);
        Assert.DoesNotContain("需求", charging, StringComparison.Ordinal);
        Assert.Equal(
            BlockedJourneysQueryEndpoint.Descriptions[JourneyRuntimeEngine.OrderHangReason],
            journeys.Single(row => row.GetProperty("agvId").GetString() == "AGV-D-HANG").GetProperty("blockReasonDescription").GetString());

        string html = new BlockedJourneyCard().RenderFact(fact.RootElement);
        Assert.Contains("开往充电桩（或在桩上）的充电单在 RIoT 上挂起", RowWith(html, "<td>AGV-01</td>"), StringComparison.Ordinal);
    }

    /// <summary>
    /// 独立审查 S1：清桩中的旧单被人在 RIoT 里恢复时（车可能移动），充电桩卡片的阶段格写「车可能移动，先联系现场」，整行与整张卡片里都没有
    /// 「保持原位」；旧单没被恢复的清桩中只写服务端自己的行为（不建单、不动车）。
    /// </summary>
    [Fact]
    public async Task AClearingChargerWhoseOldOrderWasResumedSaysTheVehicleMayMoveAndNeverThatItStaysPut()
    {
        await using ChargingDatabase database = await ChargingDatabase.CreateAsync();
        DateTimeOffset now = database.Now;
        await database.SeedAsync(context =>
        {
            AddRoster(context, 2, now.AddDays(-1), "张三", [211, 212]);
            AddCharging(context, "K-01", 211, ChargingCyclePhases.Clearing, ChargingCycleWireStates.UnableToCharge,
                StationExclusivityStates.Occupied, now.AddMinutes(-20), purpose: VehiclePurposes.ClearingMaintenance,
                code: ChargingExecutionReasons.OldOrderResumedWhileClearing);
            AddCharging(context, "K-02", 212, ChargingCyclePhases.Clearing, ChargingCycleWireStates.UnableToCharge,
                StationExclusivityStates.Occupied, now.AddMinutes(-20), purpose: VehiclePurposes.ClearingMaintenance,
                code: ChargingExecutionReasons.UnableToChargeClearing);
        });

        (JsonDocument fact, string html) = await ReadAsync(database, "chargers");
        using (fact)
        {
            Dictionary<int, JsonElement> chargers = fact.RootElement.GetProperty("chargers").EnumerateArray()
                .ToDictionary(c => c.GetProperty("stationId").GetInt32());
            Assert.Equal(ChargingDashboardDescriptions.ClearingVehicleMayMove, chargers[211].GetProperty("stageDescription").GetString());
            Assert.Equal(ChargingDashboardDescriptions.ChargerStages[ChargingDashboardDescriptions.StageClearing],
                chargers[212].GetProperty("stageDescription").GetString());
            string resumed = RowWith(html, "<td>211</td>");
            Assert.Contains("CLEARING：车可能移动，先联系现场", resumed, StringComparison.Ordinal);
            Assert.DoesNotContain("保持原位", resumed, StringComparison.Ordinal);
            Assert.Contains("服务端不为它建单、不动车", RowWith(html, "<td>212</td>"), StringComparison.Ordinal);
            Assert.DoesNotContain("保持原位", html, StringComparison.Ordinal);
        }

        (JsonDocument vehicles, string vehicleHtml) = await ReadAsync(database, "charging-vehicles");
        using (vehicles)
        {
            Assert.DoesNotContain("保持原位", vehicleHtml, StringComparison.Ordinal);
        }
    }

    /// <summary>独立审查 S3：一轮的完成时刻在此刻之后（时钟回拨），与过期的一轮同样不可用：电量与排队原因写没评估，不显示那一轮的值。</summary>
    [Fact]
    public async Task APassCompletedAfterNowIsNotShownBecauseTheClockWentBack()
    {
        await using ChargingDatabase database = await ChargingDatabase.CreateAsync();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ChargingAllocationBoard board = new();
        long pass = board.BeginPass(now.AddSeconds(29));
        board.RecordObservation("AGV-01", 55, "NO_CHARGE", "SUFFICIENT", now.AddSeconds(29));
        board.Record("AGV-01", ChargingAllocationReasons.NotRequired, "");
        board.EndPass(pass, now.AddSeconds(30));

        (JsonDocument fact, string html) = await ReadAsync(database, "charging-vehicles", board);
        using (fact)
        {
            JsonElement allocation = fact.RootElement.GetProperty("vehicles").EnumerateArray()
                .Single(v => v.GetProperty("agvId").GetString() == "AGV-01").GetProperty("allocation");
            Assert.StartsWith("本轮没有评估这辆车：最近 ", allocation.GetProperty("note").GetString(), StringComparison.Ordinal);
            Assert.Equal(JsonValueKind.Null, allocation.GetProperty("batteryPercent").ValueKind);
            Assert.DoesNotContain("55%", html, StringComparison.Ordinal);
        }
    }

    // ---------------- 充电暂停与等待 ----------------

    /// <summary>
    /// 人工充电等待两种原因各一条（失联车照列、标出失联）并写明解除方式；清桩中两辆（一辆人工确认已记下、一辆还没有）；最近的清桩记录里
    /// 人工确认时刻与清桩完成时刻分开写；车辆充电资格暂停写明本版本未实施，库里有行时照列。
    /// </summary>
    [Fact]
    public async Task ManualHoldsClearingVehiclesClearanceRecordsAndEligibilityHoldsAreListed()
    {
        await using ChargingDatabase database = await ChargingDatabase.CreateAsync();
        DateTimeOffset now = database.Now;
        await database.SeedAsync(context =>
        {
            AddRoster(context, 1, now.AddDays(-1), "张三", [211, 212, 213]);
            AddManualHold(context, "K-01", ManualChargingHoldReasons.RosterEmpty, now.AddMinutes(-20));
            AddManualHold(context, "K-05", ManualChargingHoldReasons.ChargingRepeatedlyFailed, now.AddMinutes(-10));
            ChargingCycleRow confirmed = AddCharging(context, "K-02", 211, ChargingCyclePhases.Clearing,
                ChargingCycleWireStates.UnableToCharge, StationExclusivityStates.Occupied, now.AddMinutes(-30),
                purpose: VehiclePurposes.ClearingMaintenance, code: ChargingExecutionReasons.OldOrderResumedWhileClearing);
            context.Add(Clearance(confirmed, now.AddMinutes(-28), confirmedAt: now.AddMinutes(-4), completedAt: null));
            ChargingCycleRow waiting = AddCharging(context, "K-03", 212, ChargingCyclePhases.Clearing,
                ChargingCycleWireStates.UnableToCharge, StationExclusivityStates.Occupied, now.AddMinutes(-12),
                purpose: VehiclePurposes.ClearingMaintenance, code: ChargingExecutionReasons.UnableToChargeClearing);
            context.Add(Clearance(waiting, now.AddMinutes(-12), confirmedAt: null, completedAt: null));
            ChargingCycleRow done = Cycle("K-04", 213, ChargingCyclePhases.Ended, ChargingCycleWireStates.NotCharging, now.AddHours(-2));
            done.EndReason = ChargingExecutionReasons.UnableToChargeCleared;
            context.Add(done);
            StationClearanceRow record = Clearance(done, now.AddHours(-2), confirmedAt: now.AddMinutes(-70), completedAt: now.AddMinutes(-65));
            record.Proof = StationClearanceProofs.ManualConfirmation;
            record.VehicleFinalPosition = "挪到 300 号点旁";
            record.OldOrderDisposition = "CANCELLED_IN_RIOT";
            record.AssistantsJson = "[\"P-009\"]";
            record.ClearedCondition = "VEHICLE_MOVED_AND_CHARGER_VACANT";
            context.Add(record);
            context.Add(new VehicleChargingEligibilityHoldRow
            {
                HoldId = "VH-1",
                IdempotencyKey = "VH-1",
                VehicleKey = "K-04",
                Reason = VehicleChargingEligibilityHoldReasons.NoProgressConfirmed,
                HeldAt = now.AddMinutes(-50),
            });
        });

        (JsonDocument fact, string html) = await ReadAsync(database, "charging-holds");
        using (fact)
        {
            JsonElement root = fact.RootElement;
            JsonElement[] manual = [.. root.GetProperty("manualChargingHolds").EnumerateArray()];
            Assert.Equal(["AGV-01", "AGV-05"], manual.Select(m => m.GetProperty("agvId").GetString()));
            Assert.False(manual[1].GetProperty("vehicleInContact").GetBoolean());
            JsonElement[] clearing = [.. root.GetProperty("clearing").EnumerateArray()];
            Assert.Equal(["AGV-02", "AGV-03"], clearing.Select(c => c.GetProperty("agvId").GetString()));
            JsonElement recent = root.GetProperty("recentClearances")[0];
            Assert.NotEqual(recent.GetProperty("confirmedAt").GetDateTimeOffset(), recent.GetProperty("completedAt").GetDateTimeOffset());
            Assert.Equal(ChargingDashboardDescriptions.InterruptionNotImplemented, root.GetProperty("eligibilityHoldsNote").GetString());
            Assert.Single(root.GetProperty("eligibilityHolds").EnumerateArray());

            string lostHold = RowWith(html, "<td>AGV-05（车辆失联");
            Assert.Contains("CHARGING_REPEATEDLY_FAILED：反复失败", lostHold, StringComparison.Ordinal);
            Assert.Contains(ChargingDashboardDescriptions.ManualHoldReleaseHint, lostHold, StringComparison.Ordinal);
            Assert.Contains("ROSTER_EMPTY：名册为空", RowOf(html, "AGV-01"), StringComparison.Ordinal);
            string resumed = RowOf(html, "AGV-02");
            Assert.Contains("CHARGING_OLD_ORDER_RESUMED_WHILE_CLEARING：现场注意车辆可能移动", resumed, StringComparison.Ordinal);
            Assert.Contains("人工确认已记下于", resumed, StringComparison.Ordinal);
            // 独立审查 S1：旧单被恢复时，同一行不能再有「车不动」一类的话，「现场要做的」改为先联系现场。
            Assert.Contains(ChargingDashboardDescriptions.ClearingVehicleMayMove, resumed, StringComparison.Ordinal);
            Assert.DoesNotContain("保持原位", resumed, StringComparison.Ordinal);
            Assert.Contains("还没有人工确认", RowOf(html, "AGV-03"), StringComparison.Ordinal);
            Assert.Contains(ChargingDashboardDescriptions.ClearingGuidance, RowOf(html, "AGV-03"), StringComparison.Ordinal);
            Assert.DoesNotContain("保持原位", html, StringComparison.Ordinal);
            string recordRow = RowWith(html, "<td>AGV-04</td><td>26/213</td>");
            Assert.Contains("MANUAL_CONFIRMATION：人工确认", recordRow, StringComparison.Ordinal);
            Assert.Contains("挪到 300 号点旁", recordRow, StringComparison.Ordinal);
            Assert.Contains("P-009", recordRow, StringComparison.Ordinal);
            Assert.Contains("NO_PROGRESS_CONFIRMED：充电无进展", RowWith(html, "<td>AGV-04</td><td>NO_PROGRESS"), StringComparison.Ordinal);
            Assert.Contains("本版本未实施", html, StringComparison.Ordinal);
            AssertCleanHtml(html);
        }
    }

    /// <summary>什么都没有时每一节写明「没有」，不留空表。</summary>
    [Fact]
    public async Task EmptySectionsSayThereIsNothing()
    {
        await using ChargingDatabase database = await ChargingDatabase.CreateAsync();
        (JsonDocument fact, string html) = await ReadAsync(database, "charging-holds");
        using (fact)
        {
            Assert.Contains("没有车在人工充电等待中", html, StringComparison.Ordinal);
            Assert.Contains("没有车在清桩中", html, StringComparison.Ordinal);
            Assert.Contains("还没有完成过清桩", html, StringComparison.Ordinal);
        }
        (JsonDocument alarms, string alarmHtml) = await ReadAsync(database, "charging-alarms");
        using (alarms)
        {
            // 从没导入过名册本身就是一条告警。
            Assert.Equal([ChargingDashboardDescriptions.AlarmRosterNeverImported],
                alarms.RootElement.GetProperty("alarms").EnumerateArray().Select(a => a.GetProperty("code").GetString()));
            Assert.Contains(ChargingDashboardDescriptions.NoChargerAvailableNote, alarmHtml, StringComparison.Ordinal);
        }
    }

    // ---------------- 充电告警 ----------------

    /// <summary>
    /// 告警按严重程度排：清桩中旧单被恢复（车可能移动）在最前；充不上、人工充电等待、RIoT 观测丢失、会话失联为高；到桩不充电、电量遥测丢失、
    /// 充满超过窗口没离桩为中。每条都有码的中文说明与「现场该做什么」。
    /// </summary>
    [Fact]
    public async Task AlarmsAreOrderedBySeverityAndEachSaysWhatToDoOnSite()
    {
        await using ChargingDatabase database = await ChargingDatabase.CreateAsync();
        DateTimeOffset now = database.Now;
        await database.SeedAsync(context =>
        {
            AddRoster(context, 1, now.AddDays(-1), "张三", [211, 212, 213, 214, 215]);
            AddCharging(context, "K-01", 211, ChargingCyclePhases.Active, ChargingCycleWireStates.EnRoute,
                StationExclusivityStates.Occupied, now.AddMinutes(-30), code: ChargingExecutionReasons.ChargerNotEngaged);
            AddCharging(context, "K-02", 212, ChargingCyclePhases.Active, ChargingCycleWireStates.Charging,
                StationExclusivityStates.Occupied, now.AddMinutes(-30), code: ChargingExecutionReasons.VehicleObservationLost);
            ChargingCycleRow clearing = AddCharging(context, "K-03", 213, ChargingCyclePhases.Clearing,
                ChargingCycleWireStates.UnableToCharge, StationExclusivityStates.Occupied, now.AddMinutes(-30),
                purpose: VehiclePurposes.ClearingMaintenance, code: ChargingExecutionReasons.OldOrderResumedWhileClearing);
            AddStationHold(context, 213, ChargingStationHoldTriggers.UnableToChargeConfirmed, now.AddMinutes(-29), "K-03", clearing.CycleId);
            ChargingCycleRow full = AddCharging(context, "K-04", 214, ChargingCyclePhases.Active, ChargingCycleWireStates.Complete,
                StationExclusivityStates.Occupied, now.AddHours(-1), purpose: null);
            full.CompletedAt = now.AddMinutes(-12);
            AddCharging(context, "K-05", 215, ChargingCyclePhases.Active, ChargingCycleWireStates.Charging,
                StationExclusivityStates.Occupied, now.AddMinutes(-30), code: ChargingExecutionReasons.BatteryTelemetryLost);
            AddManualHold(context, "K-01", ManualChargingHoldReasons.ChargingRepeatedlyFailed, now.AddMinutes(-5));
        });

        (JsonDocument fact, string html) = await ReadAsync(database, "charging-alarms");
        using (fact)
        {
            JsonElement[] alarms = [.. fact.RootElement.GetProperty("alarms").EnumerateArray()];
            Assert.Equal(ChargingExecutionReasons.OldOrderResumedWhileClearing, alarms[0].GetProperty("code").GetString());
            Assert.Equal(ChargingDashboardDescriptions.SeverityCritical, alarms[0].GetProperty("severity").GetString());
            string[] severities = [.. alarms.Select(a => a.GetProperty("severity").GetString()!)];
            Assert.Equal(severities.OrderBy(ChargingDashboardDescriptions.SeverityRank), severities);

            Dictionary<string, string> severityByCode = alarms.ToDictionary(
                a => a.GetProperty("code").GetString() + "@" + a.GetProperty("agvId").GetString(),
                a => a.GetProperty("severity").GetString()!);
            Assert.Equal(new Dictionary<string, string>
            {
                [ChargingExecutionReasons.OldOrderResumedWhileClearing + "@AGV-03"] = ChargingDashboardDescriptions.SeverityCritical,
                [ChargingStationHoldTriggers.UnableToChargeConfirmed + "@AGV-03"] = ChargingDashboardDescriptions.SeverityHigh,
                [ChargingDashboardDescriptions.AlarmManualChargingHold + "@AGV-01"] = ChargingDashboardDescriptions.SeverityHigh,
                [ChargingExecutionReasons.VehicleObservationLost + "@AGV-02"] = ChargingDashboardDescriptions.SeverityHigh,
                [JourneyRuntimeEngine.OnboardSessionLostReason + "@AGV-05"] = ChargingDashboardDescriptions.SeverityHigh,
                [ChargingExecutionReasons.ChargerNotEngaged + "@AGV-01"] = ChargingDashboardDescriptions.SeverityMedium,
                [ChargingExecutionReasons.BatteryTelemetryLost + "@AGV-05"] = ChargingDashboardDescriptions.SeverityMedium,
                [ChargingDashboardDescriptions.StageCompleteAwaitingDeparture + "@AGV-04"] = ChargingDashboardDescriptions.SeverityMedium,
            }, severityByCode);
            foreach (JsonElement alarm in alarms)
            {
                string code = alarm.GetProperty("code").GetString()!;
                Assert.DoesNotContain("请报开发", alarm.GetProperty("codeDescription").GetString(), StringComparison.Ordinal);
                Assert.True(ChargingDashboardDescriptions.FieldActions.ContainsKey(code), $"No field action for alarm {code}.");
            }

            string critical = RowWith(html, "<tr class=\"charging-alarm-critical\"");
            Assert.Contains("style=\"background:#c00;color:#fff;font-weight:bold\"", critical, StringComparison.Ordinal);
            Assert.Contains("CHARGING_OLD_ORDER_RESUMED_WHILE_CLEARING：现场注意车辆可能移动", critical, StringComparison.Ordinal);
            Assert.Contains("立刻联系现场人员注意车辆可能移动", critical, StringComparison.Ordinal);
            Assert.Contains("ONBOARD_SESSION_LOST：车载端会话失联", html, StringComparison.Ordinal);
            Assert.Contains("服务端仍为它记着：充电周期、充电桩", html, StringComparison.Ordinal);
            Assert.Contains("CHARGING_VEHICLE_OBSERVATION_LOST：车在充电桩上", html, StringComparison.Ordinal);
            Assert.Contains("CHARGING_BATTERY_TELEMETRY_LOST：充电中读不到新鲜的电量", html, StringComparison.Ordinal);
            Assert.Contains("CHARGER_NOT_ENGAGED：车已到充电桩并停稳", html, StringComparison.Ordinal);
            Assert.Contains("缺哪一项见服务端日志事件 2249", html, StringComparison.Ordinal);
            Assert.Contains(ChargingDashboardDescriptions.InterruptionNotImplemented, html, StringComparison.Ordinal);
            AssertCleanHtml(html);
        }
    }

    // ---------------- 说明与共用读法 ----------------

    /// <summary>每个取值集合的每个值都有中文说明（新加一个而忘了说明就红）；真实跑出来的码另由 <see cref="ChargingDashboardCodeRecorder"/> 核。</summary>
    [Fact]
    public void EveryChargingValueHasAChineseDescription()
    {
        Assert.All(ChargingCycleWireStates.All, state => Assert.True(ChargingDashboardDescriptions.CycleStates.ContainsKey(state), state));
        Assert.All(ChargingCyclePhases.All, phase => Assert.True(ChargingDashboardDescriptions.CyclePhases.ContainsKey(phase), phase));
        Assert.All(ChargingStationHoldTriggers.All, trigger =>
        {
            Assert.True(ChargingDashboardDescriptions.HoldTriggers.ContainsKey(trigger), trigger);
            Assert.True(ChargingDashboardDescriptions.FieldActions.ContainsKey(trigger), trigger);
        });
        Assert.All(VehicleChargingEligibilityHoldReasons.All,
            reason => Assert.True(ChargingDashboardDescriptions.EligibilityHoldReasons.ContainsKey(reason), reason));
        Assert.All(
            typeof(ChargingAllocationReasons).GetFields().Where(field => field.IsLiteral).Select(field => (string)field.GetRawConstantValue()!),
            reason => Assert.NotNull(ChargingDashboardDescriptions.DescribeAllocationReason(reason)));
        Assert.All(
            typeof(Host.Runtime.Dispatch.BatteryStates).GetFields().Where(field => field.IsLiteral).Select(field => (string)field.GetRawConstantValue()!),
            state => Assert.True(ChargingDashboardDescriptions.BatteryStateProjections.ContainsKey(state), state));
        Assert.All(StationClearanceProofs.All, proof => Assert.True(ChargingDashboardDescriptions.ClearanceProofs.ContainsKey(proof), proof));
        Assert.All(
            typeof(ManualChargingHoldReasons).GetFields().Where(field => field.IsLiteral).Select(field => (string)field.GetRawConstantValue()!),
            reason => Assert.True(ChargingDashboardDescriptions.ManualHoldReasons.ContainsKey(reason), reason));
        Assert.All(StationExclusivityStates.All, state => Assert.NotNull(DashboardDescriptions.StationState(StationExclusivityKinds.Charger, state)));
        Assert.All(ChargingDashboardDescriptions.ChargerStages.Keys.Where(stage => stage is
                ChargingDashboardDescriptions.StageCompleteAwaitingDeparture or ChargingDashboardDescriptions.StageFailedCycleAwaitingRelease),
            stage => Assert.True(ChargingDashboardDescriptions.FieldActions.ContainsKey(stage), stage));
        Assert.All(ChargingDashboardDescriptions.OwnAlarmCodes.Keys,
            code => Assert.True(ChargingDashboardDescriptions.FieldActions.ContainsKey(code), code));
        Assert.All(ChargingDashboardDescriptions.SharedCodesOnChargingJourneys, pair =>
        {
            Assert.True(ChargingDashboardDescriptions.FieldActions.ContainsKey(pair.Key), pair.Key);
            // 充电口径：充电单没有需求，被取消后不重建（与搬运口径不同）。
            Assert.DoesNotContain("需求", pair.Value, StringComparison.Ordinal);
            Assert.DoesNotContain("重建", pair.Value, StringComparison.Ordinal);
            Assert.NotEqual(BlockedJourneysQueryEndpoint.Descriptions.GetValueOrDefault(pair.Key), pair.Value);
        });
    }

    /// <summary>持有者种类认得 CLEARING_MAINTENANCE：用途转为清桩维护时，持有者仍是原来那趟充电旅程（cs#392 审查对齐第 3 条）。</summary>
    [Fact]
    public void TheHolderKindRecognisesAClearingMaintenancePurpose()
    {
        string journeyId = ChargingIdentity.JourneyIdPrefix + "K-01:20261002T000000000Z";
        Assert.Equal("CHARGING_JOURNEY", VehiclePurposeFacts.HolderKind(journeyId, VehiclePurposes.Charging));
        Assert.Equal("CLEARING_MAINTENANCE", VehiclePurposeFacts.HolderKind(journeyId, VehiclePurposes.ClearingMaintenance));
        Assert.Equal("清桩中的充电旅程", VehiclePurposeFacts.HolderKindDescription(journeyId, VehiclePurposes.ClearingMaintenance));
        Assert.Equal("CHARGING_JOURNEY", VehiclePurposeFacts.HolderKind(journeyId));
    }

    /// <summary>站点状态说明按种类取：等待点与公共站点仍是「离点证据」那一句，充电桩是三项确认那一句。</summary>
    [Fact]
    public void StationStateDescriptionsDependOnTheStationKind()
    {
        Assert.Contains("离点证据", DashboardDescriptions.StationState(StationExclusivityKinds.WaitingPoint, StationExclusivityStates.Occupied),
            StringComparison.Ordinal);
        Assert.Contains("离点证据", DashboardDescriptions.StationState(StationExclusivityKinds.FixedTaskStation, StationExclusivityStates.Occupied),
            StringComparison.Ordinal);
        Assert.Contains("三项都确认了才放", DashboardDescriptions.StationState(StationExclusivityKinds.Charger, StationExclusivityStates.Occupied),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// 三个数据面共用的那一份合并：登记里的站点按登记顺序、配上各自的独占，不在登记里却仍被持有的跟在后面（cs#392 审查对齐第 2 条）。
    /// </summary>
    [Fact]
    public void TheSharedMergeListsRegisteredStationsThenStationsStillHeldOutsideTheRegistration()
    {
        StationExclusivityRow Held(int stationId) => new()
        {
            MapId = MapId,
            StationId = stationId,
            StationKind = StationExclusivityKinds.Charger,
            State = StationExclusivityStates.Reserved,
            VehicleKey = "K-01",
            JourneyId = "j-" + stationId,
            RecordId = "r-" + stationId,
        };
        IReadOnlyList<(string? Entry, StationExclusivityRow? Holding)> merged = StationHoldings.MergeWithHeld(
            ["b", "a"], name => (MapId, name == "a" ? 211 : 212), [Held(213), Held(211)]);
        Assert.Equal(
            [("b", (int?)null), ("a", 211), (null, 213)],
            merged.Select(item => (item.Entry, item.Holding?.StationId)));
    }

    // ---------------- 辅助 ----------------

    private static string Stage(JsonElement charger) => charger.GetProperty("stage").GetString()!;

    private static string Trigger(JsonElement charger) =>
        charger.GetProperty("allocationHolds")[0].GetProperty("trigger").GetString()!;

    private static void AddRoster(
        ControlServerDbContext context, long version, DateTimeOffset loadedAt, string approvedBy, int[] stations, string? changeNote = null)
    {
        context.Add(new ChargerRosterVersionRow
        {
            Version = version,
            ContentSha256 = new string((char)('a' + version), 64),
            LoadedAt = loadedAt,
            Source = ChargerRosterSources.GovernedImport,
            ApprovedBy = approvedBy,
            ApprovalBasis = "现场勘查记录 " + version,
            ChangeNote = changeNote,
        });
        foreach (int station in stations)
        {
            context.Add(new ChargerRosterEntryRow { Version = version, MapId = MapId, StationId = station, StationName = "CHG-" + station });
        }
    }

    private static ChargingCycleRow Cycle(string vehicleKey, int stationId, string phase, string wireState, DateTimeOffset allocatedAt,
        long rosterVersion = 2, long policyVersion = 1)
    {
        string journeyId = ChargingIdentity.JourneyIdPrefix + vehicleKey + ":" + stationId;
        return new ChargingCycleRow
        {
            CycleId = "cycle:" + vehicleKey + ":" + stationId,
            VehicleKey = vehicleKey,
            JourneyId = journeyId,
            MapId = MapId,
            StationId = stationId,
            ChargerRosterVersion = rosterVersion,
            ChargingPolicyVersion = policyVersion,
            WireState = wireState,
            Phase = phase,
            AllocatedAt = allocatedAt,
        };
    }

    /// <summary>一次充电在库里的样子：周期、桩独占与经过、（未结束时）用途占有与充电旅程行。</summary>
    private static ChargingCycleRow AddCharging(
        ControlServerDbContext context,
        string vehicleKey,
        int stationId,
        string phase,
        string wireState,
        string exclusivityState,
        DateTimeOffset since,
        string? purpose = VehiclePurposes.Charging,
        string? code = null,
        bool open = true,
        long rosterVersion = 2,
        long policyVersion = 1)
    {
        ChargingCycleRow cycle = Cycle(vehicleKey, stationId, phase, wireState, since, rosterVersion, policyVersion);
        context.Add(cycle);
        string recordId = Guid.NewGuid().ToString("D");
        context.Add(new StationExclusivityRow
        {
            MapId = MapId,
            StationId = stationId,
            StationKind = StationExclusivityKinds.Charger,
            State = exclusivityState,
            VehicleKey = vehicleKey,
            JourneyId = cycle.JourneyId,
            StateSince = since,
            RecordId = recordId,
            ChargerRosterVersion = rosterVersion,
        });
        context.Add(new StationExclusivityRecordRow
        {
            RecordId = recordId,
            MapId = MapId,
            StationId = stationId,
            StationKind = StationExclusivityKinds.Charger,
            VehicleKey = vehicleKey,
            JourneyId = cycle.JourneyId,
            ReservedAt = since,
            ChargerRosterVersion = rosterVersion,
        });
        if (purpose is not null)
        {
            context.Add(new VehiclePurposeClaimRow { VehicleKey = vehicleKey, Purpose = purpose, JourneyId = cycle.JourneyId, ClaimedAt = since });
        }
        if (open)
        {
            JourneyRuntimeRow runtime = ChargingJourneyShape.Build(
                cycle.JourneyId,
                new FleetVehicle("AGV-" + vehicleKey[2..], vehicleKey, 1),
                new ChargerRosterEntry(MapId, stationId, "CHG-" + stationId, null, null, []),
                policyVersion,
                "MANDATORY_CHARGE",
                new JourneyRuntimeOptions { MapId = MapId },
                since).Runtime;
            runtime.SetBlockReason(code, since.AddMinutes(1));
            context.Add(runtime);
        }
        return cycle;
    }

    private static StationClearanceRow Clearance(
        ChargingCycleRow cycle, DateTimeOffset startedAt, DateTimeOffset? confirmedAt, DateTimeOffset? completedAt) => new()
        {
            ClearanceId = "clearance:" + cycle.CycleId,
            CycleId = cycle.CycleId,
            VehicleKey = cycle.VehicleKey,
            MapId = cycle.MapId,
            StationId = cycle.StationId,
            StartedAt = startedAt,
            ConfirmedAt = confirmedAt,
            ConfirmedBy = confirmedAt is null ? null : "P-001",
            ConfirmedByRole = confirmedAt is null ? null : "R-13",
            CompletedAt = completedAt,
            AssistantsJson = "[]",
        };

    private static ChargingStationAllocationHoldRow AddStationHold(
        ControlServerDbContext context, int stationId, string trigger, DateTimeOffset heldAt, string? vehicleKey, string? cycleId)
    {
        string holdId = $"hold:{trigger}:{stationId}:{heldAt.ToUnixTimeSeconds()}";
        ChargingStationAllocationHoldRow hold = new()
        {
            HoldId = holdId,
            IdempotencyKey = holdId,
            Trigger = trigger,
            RootCause = ChargingHoldRootCauses.Unknown,
            MapId = MapId,
            StationId = stationId,
            VehicleKey = vehicleKey,
            CycleId = cycleId,
            HeldAt = heldAt,
        };
        context.Add(hold);
        return hold;
    }

    private static void AddManualHold(ControlServerDbContext context, string vehicleKey, string reason, DateTimeOffset since) =>
        context.Add(new ManualChargingHoldRow { VehicleKey = vehicleKey, HoldId = "manual:" + vehicleKey, Reason = reason, Since = since });

    private static async Task<(JsonDocument Fact, string Html)> ReadAsync(
        ChargingDatabase database, string path, ChargingAllocationBoard? board = null, IOptions<JourneyRuntimeOptions>? options = null)
    {
        IDashboardCard card = DashboardCardCatalog.Discovered.Cards
            .Single(candidate => candidate.SourcePath == DashboardPaths.QueryPrefix + path);
        IDashboardQueryEndpoint endpoint = DashboardQueryEndpointCatalog
            .Discover(typeof(FleetSessionsQueryEndpoint).Assembly)
            .Endpoints.Single(candidate => candidate.Path == card.SourcePath);
        // 换成测试的名册：同一个类型的内部构造，读法与宿主一样。
        endpoint = endpoint switch
        {
            ChargersQueryEndpoint => new ChargersQueryEndpoint(FleetOptions, TimeProvider.System),
            ChargingVehiclesQueryEndpoint => new ChargingVehiclesQueryEndpoint(
                options ?? FleetOptions, board ?? new ChargingAllocationBoard(), TimeProvider.System),
            ChargingHoldsQueryEndpoint => new ChargingHoldsQueryEndpoint(new VehicleRoster(FleetOptions), TimeProvider.System),
            ChargingAlarmsQueryEndpoint => new ChargingAlarmsQueryEndpoint(FleetOptions, TimeProvider.System),
            _ => throw new InvalidOperationException(path),
        };
        await using ControlServerDbContext context = database.NewContext();
        object rows = await endpoint.ReadAsync(context, TestContext.Current.CancellationToken);
        JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(rows));
        return (document, card.RenderFact(document.RootElement));
    }

    private static readonly IOptions<JourneyRuntimeOptions> FleetOptions = Options.Create(new JourneyRuntimeOptions
    {
        MapId = MapId,
        Fleet =
        [
            .. Enumerable.Range(1, 5).Select(n => new FleetVehicleOptions
            {
                AgvId = $"AGV-0{n}",
                VehicleKey = $"K-0{n}",
                AgvLifecycleGeneration = 1,
            }),
        ],
    });

    /// <summary>AGV-05 是唯一的失联车：逐车卡片上它只在失联列表里，没有任何一行以它开头。</summary>
    private static void AssertOnlyLost(string html)
    {
        Assert.DoesNotContain("<tr><td>AGV-05</td>", html, StringComparison.Ordinal);
        Assert.Contains($"AGV-05：{VehicleAlarmProjection.LinkDownReason}", html, StringComparison.Ordinal);
    }

    private static void AssertCleanHtml(string html)
    {
        Assert.DoesNotContain("<form", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<button", html, StringComparison.OrdinalIgnoreCase);
        foreach (string word in StaleWords)
        {
            Assert.DoesNotContain(word, html, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>一行里第 <paramref name="index"/> 个单元格（从 0 数）的内容。</summary>
    private static string Cell(string row, int index) =>
        row.Split("<td>")[index + 1].Split("</td>")[0];

    private static string RowOf(string html, string agvId) => RowWith(html, "<tr><td>" + agvId + "</td>");

    private static string RowWith(string html, string marker)
    {
        int start = html.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, "No row with " + marker + ".");
        start = html.LastIndexOf("<tr", start + "<tr".Length - 1, StringComparison.Ordinal);
        int end = html.IndexOf("</tr>", start, StringComparison.Ordinal);
        return html[start..end];
    }

    /// <summary>迁移建出的库；AGV-01..04 六秒内说过话，AGV-05 的会话行在、最后一条消息却在存活时限之外。</summary>
    private sealed class ChargingDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private ChargingDatabase(SqliteConnection connection, DateTimeOffset now)
        {
            _connection = connection;
            Now = now;
        }

        /// <summary>种行用的「此刻」：会话存活按真实时钟判，所以种的时刻也跟着真实时钟走，精确到秒。</summary>
        public DateTimeOffset Now { get; }

        public static async Task<ChargingDatabase> CreateAsync()
        {
            SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            DateTimeOffset now = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            ChargingDatabase database = new(connection, now);
            await using ControlServerDbContext context = database.NewContext();
            await context.Database.MigrateAsync(TestContext.Current.CancellationToken);
            foreach (int n in Enumerable.Range(1, 5))
            {
                string agvId = $"AGV-0{n}";
                context.SessionRecoveries.Add(new SessionRecoveryRow
                {
                    AgvId = agvId,
                    SessionGeneration = 3,
                    ProtocolCommit = "c",
                    ManifestSha256 = "m",
                    ProfileId = "AGV_FULL_PRODUCT",
                    ProtocolVersion = 2,
                    Readiness = SessionReadiness.Ready,
                    ReasonCode = "READY",
                    UpdatedAt = now,
                });
                context.ProtocolInbox.Add(new ProtocolInboxRow
                {
                    MessageId = Guid.NewGuid().ToString("D"),
                    MessageType = "Heartbeat",
                    RequestJson = JsonSerializer.Serialize(new { messageType = "Heartbeat", agvId, sessionGeneration = 3 }),
                    ContentHash = new string('0', 64),
                    FirstResponseJson = "{}",
                    ReceivedAt = DateTimeOffset.UtcNow - (n == 5 ? SessionLiveness.Timeout + TimeSpan.FromSeconds(5) : TimeSpan.FromSeconds(1)),
                });
            }
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
            return database;
        }

        public ControlServerDbContext NewContext() =>
            new(new DbContextOptionsBuilder<ControlServerDbContext>().UseSqlite(_connection).Options);

        public async Task SeedAsync(Action<ControlServerDbContext> seed)
        {
            await using ControlServerDbContext context = NewContext();
            seed(context);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        public async ValueTask DisposeAsync() => await _connection.DisposeAsync();
    }
}
