using System.Reflection;
using System.Text.Json;
using ControlServer.Application;
using ControlServer.Dashboard;
using ControlServer.Domain;
using ControlServer.Host.Dashboard;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Faults;
using ControlServer.Host.Runtime.Fleet;
using ControlServer.Host.Runtime.IdleReturn;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ControlServer.Tests;

/// <summary>
/// 批次8-21（control-server#392）的四张卡片：车辆用途、等待点、公共站点占用、空闲返回。每张卡片的数据面在迁移建出的库上读种好的行，
/// 再交给卡片渲染，断言字段与写出来的字。
/// </summary>
/// <remarks>
/// 名册三辆车之外再配两辆：<c>AGV-01</c>～<c>AGV-04</c> 听得到，<c>AGV-05</c> 的会话行在、却六秒内没说过话——它是失联车，
/// 每张卡片都只能把它列进 <c>unavailableVehicles</c>，不出现它失联前的用途、步骤或站点。
/// </remarks>
public sealed class VehiclePurposeDashboardTests
{
    private const int MapId = 26;
    private static readonly DateTimeOffset At = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);

    private static readonly string[] StaleWords =
    [
        "已过期", "过期", "陈旧", "上次更新", "上一次更新", "最后更新",
        "stale", "expired", "lastUpdated", "LastUpdated", "last updated",
    ];

    // ---------------- 车辆用途 ----------------

    /// <summary>每个用途取值各一辆车、外加一辆没有用途的：用途、持有者种类、持有旅程与取得时刻照库里写；失联车只进失联列表。</summary>
    [Fact]
    public async Task EachVehicleShowsItsPurposeHolderAndClaimTimeAndALostVehicleShowsNoPurpose()
    {
        await using DashboardDatabase database = await DashboardDatabase.CreateAsync();
        await database.SeedAsync(context =>
        {
            context.Add(Claim("K-01", VehiclePurposes.Transport, JourneyIdentity.ForAnchorDemand("D-1")));
            context.Add(Claim("K-02", VehiclePurposes.Charging, ChargingIdentity.JourneyIdPrefix + "K-02:1"));
            context.Add(Claim("K-03", VehiclePurposes.IdleReturn, IdleReturnIdentity.JourneyIdFor("K-03", At)));
            context.Add(Claim("K-05", VehiclePurposes.IdleReturn, IdleReturnIdentity.JourneyIdFor("K-05", At)));
        });

        (JsonDocument fact, string html) = await ReadAsync(database, "vehicle-purposes");
        using (fact)
        {
            JsonElement[] vehicles = [.. fact.RootElement.GetProperty("vehicles").EnumerateArray()];
            Assert.Equal(["AGV-01", "AGV-02", "AGV-03", "AGV-04"], vehicles.Select(v => v.GetProperty("agvId").GetString()));
            Assert.Equal("TRANSPORT", vehicles[0].GetProperty("purpose").GetString());
            Assert.Equal("JOURNEY", vehicles[0].GetProperty("holderKind").GetString());
            Assert.Equal(At, vehicles[0].GetProperty("claimedAt").GetDateTimeOffset());
            Assert.Equal("CHARGING", vehicles[1].GetProperty("purpose").GetString());
            Assert.Equal("CHARGING_JOURNEY", vehicles[1].GetProperty("holderKind").GetString());
            Assert.Equal("IDLE_RETURN", vehicles[2].GetProperty("purpose").GetString());
            Assert.Equal("IDLE_RETURN_RECORD", vehicles[2].GetProperty("holderKind").GetString());
            Assert.Equal(IdleReturnIdentity.JourneyIdFor("K-03", At), vehicles[2].GetProperty("holderJourneyId").GetString());
            Assert.Equal(JsonValueKind.Null, vehicles[3].GetProperty("purpose").ValueKind);
            AssertOnlyLost(fact.RootElement, html);

            Assert.Contains("TRANSPORT（搬运", RowOf(html, "AGV-01"), StringComparison.Ordinal);
            Assert.Contains("CHARGING（充电", RowOf(html, "AGV-02"), StringComparison.Ordinal);
            Assert.Contains("充电旅程 charging:K-02:1", RowOf(html, "AGV-02"), StringComparison.Ordinal);
            Assert.Contains("IDLE_RETURN（空闲返回", RowOf(html, "AGV-03"), StringComparison.Ordinal);
            Assert.Contains("空闲返回记录 idle-return:K-03:", RowOf(html, "AGV-03"), StringComparison.Ordinal);
            Assert.Contains("<td>无（没有任何用途占着这辆车）</td>", RowOf(html, "AGV-04"), StringComparison.Ordinal);
        }
    }

    /// <summary>用途的每个取值都有中文说明：批次 9 加了 CHARGING 与 CLEARING_MAINTENANCE，以后再加一个而忘了说明就红。</summary>
    [Fact]
    public void EveryVehiclePurposeHasAChineseDescription()
    {
        foreach (string purpose in VehiclePurposes.All)
        {
            Assert.True(DashboardDescriptions.Purposes.ContainsKey(purpose), $"No description for purpose {purpose}.");
        }
        foreach (string state in StationExclusivityStates.All)
        {
            Assert.True(DashboardDescriptions.StationStates.ContainsKey(state), $"No description for station state {state}.");
        }
        foreach (string kind in StationExclusivityKinds.All)
        {
            Assert.True(DashboardDescriptions.StationKinds.ContainsKey(kind), $"No description for station kind {kind}.");
        }
    }

    // ---------------- 等待点 ----------------

    /// <summary>
    /// 当前登记版本；空闲、预占、占用、停用各一个点；停用却仍被占用的、已从登记里删掉却仍被预占的照样列出并写明（REQ-0297）。
    /// 持有车辆失联时独占行照给，只标出失联，不写车此刻在不在点上。
    /// </summary>
    [Fact]
    public async Task EveryWaitingPointShowsItsStateHolderSinceAndVersionAndDisabledOrRemovedPointsStillHeldAreListed()
    {
        await using DashboardDatabase database = await DashboardDatabase.CreateAsync();
        await database.SeedAsync(context =>
        {
            AddWaitingPoints(context, version: 1, (216, true));
            AddWaitingPoints(context, version: 2, (211, true), (212, true), (213, true), (214, false), (215, false));
            context.Add(new WaitingPointVehicleScopeRow { Version = 2, MapId = MapId, StationId = 212, VehicleKey = "K-01" });
            context.AddRange(Holding(StationExclusivityKinds.WaitingPoint, 212, "K-01", StationExclusivityStates.Reserved, waitingPointVersion: 2));
            context.AddRange(Holding(StationExclusivityKinds.WaitingPoint, 213, "K-02", StationExclusivityStates.Occupied, waitingPointVersion: 2));
            context.AddRange(Holding(StationExclusivityKinds.WaitingPoint, 215, "K-05", StationExclusivityStates.Occupied, waitingPointVersion: 1));
            context.AddRange(Holding(StationExclusivityKinds.WaitingPoint, 216, "K-04", StationExclusivityStates.Reserved, waitingPointVersion: 1));
        });

        (JsonDocument fact, string html) = await ReadAsync(database, "waiting-points");
        using (fact)
        {
            JsonElement root = fact.RootElement;
            Assert.Equal(2, root.GetProperty("registration").GetProperty("version").GetInt64());
            JsonElement[] points = [.. root.GetProperty("points").EnumerateArray()];
            Assert.Equal([211, 212, 213, 214, 215, 216], points.Select(p => p.GetProperty("stationId").GetInt32()));

            JsonElement free = points[0];
            Assert.Equal("FREE", free.GetProperty("holding").GetProperty("status").GetString());
            Assert.Empty(free.GetProperty("vehicleScope").EnumerateArray());

            JsonElement reserved = points[1];
            Assert.Equal("RESERVED", reserved.GetProperty("holding").GetProperty("status").GetString());
            Assert.Equal("AGV-01", reserved.GetProperty("holding").GetProperty("holderAgvId").GetString());
            Assert.Equal(At, reserved.GetProperty("holding").GetProperty("stateSince").GetDateTimeOffset());
            Assert.Equal(2, reserved.GetProperty("holding").GetProperty("waitingPointVersion").GetInt64());
            Assert.Equal(["K-01"], reserved.GetProperty("vehicleScope").EnumerateArray().Select(v => v.GetString()));

            Assert.Equal("OCCUPIED", points[2].GetProperty("holding").GetProperty("status").GetString());
            Assert.True(points[3].GetProperty("disabled").GetBoolean());
            Assert.Equal("FREE", points[3].GetProperty("holding").GetProperty("status").GetString());

            JsonElement disabledHeld = points[4];
            Assert.Equal(WaitingPointsQueryEndpoint.DisabledStillHeld, disabledHeld.GetProperty("registrationNote").GetString());
            Assert.False(disabledHeld.GetProperty("disabled").GetBoolean());
            Assert.Equal("OCCUPIED", disabledHeld.GetProperty("holding").GetProperty("status").GetString());
            Assert.False(disabledHeld.GetProperty("holding").GetProperty("holderInContact").GetBoolean());

            JsonElement removedHeld = points[5];
            Assert.Equal(
                WaitingPointsQueryEndpoint.NotInCurrentRegistrationStillHeld, removedHeld.GetProperty("registrationNote").GetString());
            Assert.False(removedHeld.GetProperty("inCurrentRegistration").GetBoolean());
            Assert.Equal(1, removedHeld.GetProperty("holding").GetProperty("waitingPointVersion").GetInt64());

            Assert.Contains("当前登记版本 v2", html, StringComparison.Ordinal);
            Assert.Contains("RESERVED（预占", RowWith(html, "<td>212</td>"), StringComparison.Ordinal);
            Assert.Contains("等待点登记 v2", RowWith(html, "<td>212</td>"), StringComparison.Ordinal);
            Assert.Contains("OCCUPIED（占用", RowWith(html, "<td>213</td>"), StringComparison.Ordinal);
            Assert.Contains("停用：不接新的空闲返回", RowWith(html, "<td>214</td>"), StringComparison.Ordinal);
            Assert.Contains("在当前登记里已停用，但仍被", RowWith(html, "<td>215</td>"), StringComparison.Ordinal);
            Assert.Contains("AGV-05（车辆失联", RowWith(html, "<td>215</td>"), StringComparison.Ordinal);
            Assert.Contains("已不在当前登记里", RowWith(html, "<td>216</td>"), StringComparison.Ordinal);
            AssertCleanHtml(html);
        }
    }

    // ---------------- 公共站点 ----------------

    /// <summary>生效绑定集里的每个公共站点一行，与等待点同一种显示；已不在生效绑定集里却仍被占用的照样列出。</summary>
    [Fact]
    public async Task EveryFixedTaskStationShowsItsHoldingAndAStationStillHeldOutsideTheActiveBindingIsListed()
    {
        await using DashboardDatabase database = await DashboardDatabase.CreateAsync();
        await database.SeedAsync(context =>
        {
            context.Add(new TaskTypeStationActiveBindingSetRow { MapId = MapId, ActiveVersion = 3, State = "ACTIVE", UpdatedAt = At });
            context.Add(Binding(version: 2, "LOAD", 309));
            context.Add(Binding(version: 3, "LOAD", 301));
            context.Add(Binding(version: 3, "UNLOAD", 302));
            context.AddRange(Holding(StationExclusivityKinds.FixedTaskStation, 302, "K-01", StationExclusivityStates.Reserved, null));
            context.AddRange(Holding(StationExclusivityKinds.FixedTaskStation, 309, "K-02", StationExclusivityStates.Occupied, null));
        });

        (JsonDocument fact, string html) = await ReadAsync(database, "fixed-task-stations");
        using (fact)
        {
            JsonElement[] stations = [.. fact.RootElement.GetProperty("stations").EnumerateArray()];
            Assert.Equal([301, 302, 309], stations.Select(s => s.GetProperty("stationId").GetInt32()));
            Assert.Equal("FREE", stations[0].GetProperty("holding").GetProperty("status").GetString());
            Assert.Equal(["LOAD"], stations[0].GetProperty("taskTypes").EnumerateArray().Select(t => t.GetString()));
            Assert.Equal("RESERVED", stations[1].GetProperty("holding").GetProperty("status").GetString());
            Assert.Equal("AGV-01", stations[1].GetProperty("holding").GetProperty("holderAgvId").GetString());
            Assert.Equal(JsonValueKind.Null, stations[1].GetProperty("holding").GetProperty("waitingPointVersion").ValueKind);
            Assert.Equal(
                FixedTaskStationsQueryEndpoint.NotInActiveBindingStillHeld, stations[2].GetProperty("registrationNote").GetString());
            Assert.Equal("OCCUPIED", stations[2].GetProperty("holding").GetProperty("status").GetString());

            Assert.Contains("生效绑定集 v3", RowWith(html, "<td>301</td>"), StringComparison.Ordinal);
            Assert.Contains("RESERVED（预占", RowWith(html, "<td>302</td>"), StringComparison.Ordinal);
            Assert.Contains("已不在本图生效的任务类型绑定集里", RowWith(html, "<td>309</td>"), StringComparison.Ordinal);
            AssertCleanHtml(html);
        }
    }

    // ---------------- 空闲返回 ----------------

    /// <summary>
    /// 空闲返回的每一步各一条：在种好的库上读数据面得到这一步，卡片写出步骤码与中文说明；原因码配中文说明。
    /// 「建单结果未知」与「失败待人工」两种（FAILED／UNKNOWN 分支）在这里各有一条。
    /// </summary>
    [Theory]
    [InlineData(IdleReturnsQueryEndpoint.StepNone, null)]
    [InlineData(IdleReturnsQueryEndpoint.StepOtherPurpose, null)]
    [InlineData(IdleReturnsQueryEndpoint.StepCommitted, null)]
    [InlineData(IdleReturnsQueryEndpoint.StepCommitted, IdleReturnExecutionReasons.DepartureNotProven)]
    [InlineData(IdleReturnsQueryEndpoint.StepCreateResultUnknown, "WAITING_POINT_ResultUnknown")]
    [InlineData(IdleReturnsQueryEndpoint.StepEnRoute, null)]
    [InlineData(IdleReturnsQueryEndpoint.StepEnRoute, JourneyRuntimeEngine.CheckpointWaitReason)]
    [InlineData(IdleReturnsQueryEndpoint.StepEnRoute, JourneyRuntimeEngine.CheckpointWaitExceededReason)]
    [InlineData(IdleReturnsQueryEndpoint.StepArrivalNotProven, IdleReturnExecutionReasons.ArrivalNotProven)]
    [InlineData(IdleReturnsQueryEndpoint.StepOrderStalled, JourneyRuntimeEngine.OrderHangReason)]
    [InlineData(IdleReturnsQueryEndpoint.StepHeldAwaitingStop, IdleReturnExecutionReasons.OrderEndedStopNotProven)]
    [InlineData(IdleReturnsQueryEndpoint.StepFailedAwaitingManual, VehicleFaultEvidence.OrderFailed)]
    [InlineData(IdleReturnsQueryEndpoint.StepAdvanceFailed, JourneyRuntimeEngine.AdvanceFailedReason)]
    [InlineData(IdleReturnsQueryEndpoint.StepAtPoint, null)]
    public async Task EachIdleReturnStepIsReadFromTheDatabaseAndRenderedWithItsChineseDescription(string step, string? code)
    {
        await using DashboardDatabase database = await DashboardDatabase.CreateAsync();
        string journeyId = IdleReturnIdentity.JourneyIdFor("K-01", At);
        await database.SeedAsync(context => SeedIdleReturnStep(context, step, code, journeyId));

        (JsonDocument fact, string html) = await ReadAsync(database, "idle-returns");
        using (fact)
        {
            JsonElement vehicle = fact.RootElement.GetProperty("vehicles").EnumerateArray()
                .Single(v => v.GetProperty("agvId").GetString() == "AGV-01");
            Assert.Equal(step, vehicle.GetProperty("step").GetString());
            string row = RowOf(html, "AGV-01");
            Assert.Contains($"{step}（{IdleReturnsQueryEndpoint.StepDescriptions[step]}）", row, StringComparison.Ordinal);
            if (code is not null)
            {
                Assert.Equal(code, vehicle.GetProperty("reasonCode").GetString());
                string description = IdleReturnCodeDescriptions.DescribeJourneyCode(code)!;
                Assert.Contains($"{code}：{System.Net.WebUtility.HtmlEncode(description)}", row, StringComparison.Ordinal);
            }
            bool holds = step is IdleReturnsQueryEndpoint.StepCreateResultUnknown or IdleReturnsQueryEndpoint.StepOrderStalled
                or IdleReturnsQueryEndpoint.StepHeldAwaitingStop or IdleReturnsQueryEndpoint.StepFailedAwaitingManual
                or IdleReturnsQueryEndpoint.StepArrivalNotProven;
            Assert.Equal(
                holds ? IdleReturnsQueryEndpoint.BranchHoldAndReconcile : null,
                vehicle.GetProperty("failureBranch").GetString());
            if (step == IdleReturnsQueryEndpoint.StepAtPoint)
            {
                // The step cell and the holding say "occupied until departure evidence", never "空闲" (idle) about the vehicle.
                string stepCell = Cell(row, 1);
                Assert.Contains("服务端记录这个等待点被它占用", stepCell, StringComparison.Ordinal);
                Assert.Contains("等待点占用直到离点证据满足", stepCell, StringComparison.Ordinal);
                Assert.DoesNotContain("空闲", stepCell, StringComparison.Ordinal);
                Assert.DoesNotContain("空闲", Cell(row, 2), StringComparison.Ordinal);
            }
            AssertOnlyLost(fact.RootElement, html);
            AssertCleanHtml(html);
        }
    }

    /// <summary>
    /// 最近一趟旅程是一次已确认失败的空闲返回（单被取消、车停稳）：给出收尾码、中文说明、收尾时刻与 REQ-0296 的已确认失败一支；
    /// 之后若又做过一趟搬运，它就只是历史，不再给。
    /// </summary>
    [Fact]
    public async Task TheLatestIdleReturnEndingIsShownWithItsBranchUntilTheVehicleDoesOtherWork()
    {
        await using DashboardDatabase database = await DashboardDatabase.CreateAsync();
        string ended = IdleReturnIdentity.JourneyIdFor("K-01", At);
        string endedOnOther = IdleReturnIdentity.JourneyIdFor("K-02", At);
        await database.SeedAsync(context =>
        {
            AddIdleReturnJourney(context, "K-01", ended, IdleReturnExecutionReasons.OrderEnded, completed: true, intentStatus: "CONFIRMED");
            context.Add(ReleasedClaimRecord("K-01", ended, At.AddMinutes(2)));
            AddIdleReturnJourney(context, "K-02", endedOnOther, IdleReturnExecutionReasons.OrderEnded, completed: true, intentStatus: "CONFIRMED");
            JourneyRuntimeRow transport = WaitingJourneyBatteryWatchTests.Runtime("D-LATER", JourneyRuntimeStage.Completed, At.AddHours(2));
            transport.AgvId = "AGV-02";
            transport.VehicleKey = "K-02";
            context.Add(transport);
        });

        (JsonDocument fact, string html) = await ReadAsync(database, "idle-returns");
        using (fact)
        {
            JsonElement[] vehicles = [.. fact.RootElement.GetProperty("vehicles").EnumerateArray()];
            JsonElement lastEnded = vehicles[0].GetProperty("lastEnded");
            Assert.Equal(IdleReturnExecutionReasons.OrderEnded, lastEnded.GetProperty("reasonCode").GetString());
            Assert.Equal(At.AddMinutes(2), lastEnded.GetProperty("endedAt").GetDateTimeOffset());
            Assert.Equal(IdleReturnsQueryEndpoint.BranchConfirmedFailure, lastEnded.GetProperty("failureBranch").GetString());
            Assert.Equal(IdleReturnsQueryEndpoint.StepNone, vehicles[0].GetProperty("step").GetString());
            Assert.Contains("REQ-0296 已确认失败一支", RowOf(html, "AGV-01"), StringComparison.Ordinal);
            Assert.Equal(JsonValueKind.Null, vehicles[1].GetProperty("lastEnded").ValueKind);
        }
    }

    /// <summary>
    /// 评估结论一格：服务启动以来还没完成过一轮评估时写「本轮没有评估」（不留白、不摆旧码）；完成一轮之后，在其中的车写码与中文说明，
    /// 不在其中的车写「本轮没有评估」；一轮正在进行时读到的仍是上一轮完整的结论，不会一轮刚开始所有车都变成没评估。
    /// </summary>
    [Fact]
    public async Task TheVerdictCellReadsOnlyTheLatestCompletedPass()
    {
        await using DashboardDatabase database = await DashboardDatabase.CreateAsync();
        IdleReturnVerdictBoard board = new();

        (JsonDocument fresh, string freshHtml) = await ReadAsync(database, "idle-returns", board);
        using (fresh)
        {
            Assert.All(fresh.RootElement.GetProperty("vehicles").EnumerateArray(), vehicle =>
                Assert.Equal(IdleReturnsQueryEndpoint.NoPassCompletedYet, vehicle.GetProperty("verdict").GetProperty("note").GetString()));
            Assert.Contains(IdleReturnsQueryEndpoint.NoPassCompletedYet, RowOf(freshHtml, "AGV-01"), StringComparison.Ordinal);
        }

        long pass1 = board.BeginPass(At);
        board.Record("AGV-01", IdleReturnReasons.CooldownAfterEndedOrder, "");
        board.Record("AGV-02", IdleReturnReasons.NoWaitingPointAvailable, "214=WAITING_POINT_RESERVED_OR_OCCUPIED");
        board.EndPass(pass1, DateTimeOffset.UtcNow);
        // The next pass has begun and recorded AGV-01 anew, but has not finished: the card still reads the completed one.
        long pass2 = board.BeginPass(At.AddSeconds(2));
        board.Record("AGV-01", IdleReturnReasons.StoppedAfterRepeatedEndedOrders, "");

        (JsonDocument fact, string html) = await ReadAsync(database, "idle-returns", board);
        using (fact)
        {
            JsonElement[] vehicles = [.. fact.RootElement.GetProperty("vehicles").EnumerateArray()];
            JsonElement cooling = vehicles[0].GetProperty("verdict");
            Assert.True(cooling.GetProperty("evaluated").GetBoolean());
            Assert.Equal(IdleReturnReasons.CooldownAfterEndedOrder, cooling.GetProperty("reasonCode").GetString());
            Assert.Equal(At, cooling.GetProperty("passStartedAt").GetDateTimeOffset());
            Assert.Contains(
                $"{IdleReturnReasons.CooldownAfterEndedOrder}：{IdleReturnCodeDescriptions.VerdictCodes[IdleReturnReasons.CooldownAfterEndedOrder]}",
                RowOf(html, "AGV-01"),
                StringComparison.Ordinal);
            Assert.DoesNotContain(IdleReturnReasons.StoppedAfterRepeatedEndedOrders, html, StringComparison.Ordinal);
            Assert.Contains("细节：214=WAITING_POINT_RESERVED_OR_OCCUPIED", RowOf(html, "AGV-02"), StringComparison.Ordinal);
            JsonElement absent = vehicles[2].GetProperty("verdict");
            Assert.False(absent.GetProperty("evaluated").GetBoolean());
            Assert.Equal(IdleReturnsQueryEndpoint.NotEvaluatedThisPass, absent.GetProperty("note").GetString());
            Assert.Contains(IdleReturnsQueryEndpoint.NotEvaluatedThisPass, RowOf(html, "AGV-03"), StringComparison.Ordinal);
            AssertOnlyLost(fact.RootElement, html);
        }

        board.EndPass(pass2, DateTimeOffset.UtcNow);
        (JsonDocument next, string nextHtml) = await ReadAsync(database, "idle-returns", board);
        using (next)
        {
            Assert.Contains(IdleReturnReasons.StoppedAfterRepeatedEndedOrders, RowOf(nextHtml, "AGV-01"), StringComparison.Ordinal);
            Assert.Contains(IdleReturnsQueryEndpoint.NotEvaluatedThisPass, RowOf(nextHtml, "AGV-02"), StringComparison.Ordinal);
        }
    }

    /// <summary>一轮的完成时刻在此刻之后（时钟回拨）：与过期的一轮同样不可用，结论那一格写没评估（control-server#408 独立审查 S3）。</summary>
    [Fact]
    public async Task APassCompletedAfterNowIsNotShownBecauseTheClockWentBack()
    {
        await using DashboardDatabase database = await DashboardDatabase.CreateAsync();
        IdleReturnVerdictBoard board = new();
        long pass = board.BeginPass(DateTimeOffset.UtcNow.AddSeconds(29));
        board.Record("AGV-01", IdleReturnReasons.Committed, "j");
        board.EndPass(pass, DateTimeOffset.UtcNow.AddSeconds(30));

        (JsonDocument fact, string _) = await ReadAsync(database, "idle-returns", board);
        using (fact)
        {
            JsonElement verdict = fact.RootElement.GetProperty("vehicles").EnumerateArray()
                .Single(vehicle => vehicle.GetProperty("agvId").GetString() == "AGV-01").GetProperty("verdict");
            Assert.False(verdict.GetProperty("evaluated").GetBoolean());
            Assert.Equal(IdleReturnsQueryEndpoint.PassNotRunning(TimeSpan.FromSeconds(6)), verdict.GetProperty("note").GetString());
        }
    }

    /// <summary>
    /// 时效窗口（调度 10-01 定的方案 A）：最近一轮已完成的评估走完已超过 3 个轮询间隔，评估没在跑——全车队没有空闲车时派车轮不跑、
    /// 评估或引擎这一轮抛了异常（开了一轮却没走完）——每辆车的结论一格都写没评估与原因，不写那一轮的码。用途、等待点、公共站点三张卡片
    /// 读的是落库事实，不受这个窗口影响，照常给值。
    /// </summary>
    [Fact]
    public async Task APassOlderThanThreePollIntervalsIsNotShownAndTheOtherCardsStillShowTheirFacts()
    {
        await using DashboardDatabase database = await DashboardDatabase.CreateAsync();
        await database.SeedAsync(context =>
        {
            AddWaitingPoints(context, version: 1, (212, true));
            context.Add(new TaskTypeStationActiveBindingSetRow { MapId = MapId, ActiveVersion = 1, State = "ACTIVE", UpdatedAt = At });
            context.Add(Binding(version: 1, "LOAD", 301));
            context.AddRange(Holding(StationExclusivityKinds.FixedTaskStation, 301, "K-02", StationExclusivityStates.Occupied, null));
            SeedIdleReturnStep(context, IdleReturnsQueryEndpoint.StepEnRoute, null, IdleReturnIdentity.JourneyIdFor("K-01", At));
        });
        IdleReturnVerdictBoard board = new();
        long pass3 = board.BeginPass(DateTimeOffset.UtcNow.AddSeconds(-31));
        board.Record("AGV-01", IdleReturnReasons.Committed, "j");
        board.Record("AGV-02", IdleReturnReasons.CooldownAfterEndedOrder, "");
        board.EndPass(pass3, DateTimeOffset.UtcNow.AddSeconds(-30));
        // A pass that began and then threw before it finished: it publishes nothing.
        _ = board.BeginPass(DateTimeOffset.UtcNow.AddSeconds(-20));
        board.Record("AGV-02", IdleReturnReasons.EvaluationFailed, "InvalidOperationException");

        string expected = IdleReturnsQueryEndpoint.PassNotRunning(TimeSpan.FromSeconds(6));
        (JsonDocument fact, string html) = await ReadAsync(database, "idle-returns", board);
        using (fact)
        {
            JsonElement[] vehicles = [.. fact.RootElement.GetProperty("vehicles").EnumerateArray()];
            Assert.All(vehicles, vehicle =>
            {
                Assert.False(vehicle.GetProperty("verdict").GetProperty("evaluated").GetBoolean());
                Assert.Equal(expected, vehicle.GetProperty("verdict").GetProperty("note").GetString());
            });
            Assert.Contains("最近 6 秒内没有完成过一轮空闲返回评估", RowOf(html, "AGV-01"), StringComparison.Ordinal);
            Assert.DoesNotContain(IdleReturnReasons.Committed, html, StringComparison.Ordinal);
            Assert.DoesNotContain(IdleReturnReasons.CooldownAfterEndedOrder, html, StringComparison.Ordinal);
            Assert.DoesNotContain(IdleReturnReasons.EvaluationFailed, html, StringComparison.Ordinal);
            // The step is a stored fact: still shown.
            Assert.Equal(IdleReturnsQueryEndpoint.StepEnRoute, vehicles[0].GetProperty("step").GetString());
        }

        (JsonDocument purposes, _) = await ReadAsync(database, "vehicle-purposes");
        using (purposes)
        {
            Assert.Equal("IDLE_RETURN", purposes.RootElement.GetProperty("vehicles")[0].GetProperty("purpose").GetString());
        }
        (JsonDocument points, _) = await ReadAsync(database, "waiting-points");
        using (points)
        {
            Assert.Equal(
                "RESERVED", points.RootElement.GetProperty("points")[0].GetProperty("holding").GetProperty("status").GetString());
        }
        (JsonDocument stations, _) = await ReadAsync(database, "fixed-task-stations");
        using (stations)
        {
            Assert.Equal(
                "OCCUPIED", stations.RootElement.GetProperty("stations")[0].GetProperty("holding").GetProperty("status").GetString());
        }
    }

    /// <summary>
    /// 窗口按实际配置的 <c>JourneyRuntime:PollInterval</c> 算、倍数是 3：轮询 5 秒时 10 秒前走完的一轮还在窗口内（15 秒），照写结论；
    /// 同一轮在默认 2 秒的配置下已在窗口外（6 秒）。
    /// </summary>
    [Fact]
    public async Task TheLivenessWindowIsThreeOfTheConfiguredPollIntervals()
    {
        Assert.Equal(3, IdleReturnsQueryEndpoint.PassLivenessPollIntervals);
        Assert.Equal(TimeSpan.FromSeconds(2), FleetOptions.Value.PollInterval);
        await using DashboardDatabase database = await DashboardDatabase.CreateAsync();
        IdleReturnVerdictBoard board = new();
        long pass5 = board.BeginPass(DateTimeOffset.UtcNow.AddSeconds(-11));
        board.Record("AGV-01", IdleReturnReasons.CooldownAfterEndedOrder, "");
        board.EndPass(pass5, DateTimeOffset.UtcNow.AddSeconds(-10));

        IOptions<JourneyRuntimeOptions> slow = Options.Create(new JourneyRuntimeOptions
        {
            PollInterval = TimeSpan.FromSeconds(5),
            Fleet = FleetOptions.Value.Fleet,
        });
        foreach ((IOptions<JourneyRuntimeOptions> options, bool shown) in new[] { (slow, true), (FleetOptions, false) })
        {
            await using ControlServerDbContext context = database.NewContext();
            object rows = await new IdleReturnsQueryEndpoint(options, board, TimeProvider.System)
                .ReadAsync(context, TestContext.Current.CancellationToken);
            using JsonDocument fact = JsonDocument.Parse(JsonSerializer.Serialize(rows));
            JsonElement verdict = fact.RootElement.GetProperty("vehicles")[0].GetProperty("verdict");
            Assert.Equal(shown, verdict.GetProperty("evaluated").GetBoolean());
            if (!shown)
            {
                Assert.Equal(IdleReturnsQueryEndpoint.PassNotRunning(TimeSpan.FromSeconds(6)), verdict.GetProperty("note").GetString());
            }
        }
    }

    /// <summary>结论板的「变了才记」不随轮次改变：同一辆车下一轮同一结论答假，结论变了答真。</summary>
    [Fact]
    public void TheVerdictBoardStillReportsAChangeOnlyWhenTheVerdictChangesAcrossPasses()
    {
        IdleReturnVerdictBoard board = new();
        long pass6 = board.BeginPass(At);
        Assert.True(board.Record("AGV-01", IdleReturnReasons.Disabled, ""));
        board.EndPass(pass6, At);
        long pass7 = board.BeginPass(At.AddSeconds(2));
        Assert.False(board.Record("AGV-01", IdleReturnReasons.Disabled, ""));
        Assert.True(board.Record("AGV-01", IdleReturnReasons.Committed, "j"));
        board.EndPass(pass7, At.AddSeconds(2));
        // Outside any pass (a caller that never began one) the change detection still works and nothing is published.
        Assert.False(board.Record("AGV-01", IdleReturnReasons.Committed, "j"));
        Assert.Equal(2, board.LatestCompletedPass!.Number);
    }

    /// <summary>
    /// 两轮重叠（审查低优先级项）：先开的一轮凭它的令牌提交时，后开的一轮已经接管了暂存，先开的那一轮什么也不发布；后开的一轮
    /// 凭自己的令牌提交，发布的是它自己的结论。
    /// </summary>
    [Fact]
    public void AnOverlappedPassCannotPublishTheNextPassesHalfFinishedStaging()
    {
        IdleReturnVerdictBoard board = new();
        long first = board.BeginPass(At);
        board.Record("AGV-01", IdleReturnReasons.Disabled, "");
        long second = board.BeginPass(At.AddSeconds(1));
        board.Record("AGV-02", IdleReturnReasons.Committed, "j");

        board.EndPass(first, At.AddSeconds(2));
        Assert.Null(board.LatestCompletedPass);

        board.EndPass(second, At.AddSeconds(3));
        IdleReturnBoardPass pass = board.LatestCompletedPass!;
        Assert.Equal(["AGV-02"], pass.Verdicts.Keys);
        Assert.Equal(At.AddSeconds(3), pass.CompletedAt);
    }

    /// <summary>服务端没给 <c>holding</c> 的一行补齐表头那五格，不错列（审查 S3）。</summary>
    [Fact]
    public void AStationRowWithoutAHoldingStillFillsTheFiveHoldingCells()
    {
        using JsonDocument fact = JsonDocument.Parse(
            """{"registration":null,"points":[{"mapId":26,"stationId":211,"stationName":"WP-211","vehicleScope":[]}],"unavailableVehicles":[]}""");
        string html = new WaitingPointCard().RenderFact(fact.RootElement);
        string row = RowWith(html, "<td>211</td>");
        Assert.Equal(10, row.Split("<td>").Length - 1);
        Assert.Contains("（服务端未提供该字段）", row, StringComparison.Ordinal);
        int headers = html.Split("<th>").Length - 1;
        Assert.Equal(headers, row.Split("<td>").Length - 1);
    }

    // ---------------- 原因码说明 ----------------

    /// <summary>
    /// 空闲返回旅程上可能出现的每一个码都有空闲返回口径的说明（调度 09-30，cs#390 增量审查 L-d）：空闲返回自己的码、建单结果码、
    /// 以及复用的共用码。共用码的说明不许照搬搬运口径——没有需求，也不重建。
    /// </summary>
    [Fact]
    public void EveryCodeAnIdleReturnJourneyCanCarryHasAnIdleReturnDescription()
    {
        foreach (string code in IdleReturnCodeDescriptions.AllJourneyCodes)
        {
            Assert.False(
                string.IsNullOrWhiteSpace(IdleReturnCodeDescriptions.DescribeJourneyCode(code)),
                $"No idle return description for {code}.");
        }
        foreach ((string code, string description) in IdleReturnCodeDescriptions.SharedCodesOnIdleReturnJourneys)
        {
            Assert.DoesNotContain("需求", description, StringComparison.Ordinal);
            Assert.DoesNotContain("重建运单", description, StringComparison.Ordinal);
            if (BlockedJourneysQueryEndpoint.Descriptions.TryGetValue(code, out string? transport))
            {
                Assert.NotEqual(transport, description);
            }
        }
    }

    /// <summary>
    /// 开着的空闲返回旅程上每一个码 → 期望的步骤（独立审查 S2）。逐码写死期望值：码进错了组（例如从「失败待人工」里漏掉
    /// <c>HELD_ORDER_RESUMED_WITHOUT_CONTINUE</c>），它会落到意图状态的兜底（单已确认就是「在途」），这里就红。
    /// 每行带上这个码在现实里出现时意图的状态。
    /// </summary>
    public static TheoryData<string, string, string> CodeToStep { get; } = new()
    {
        { JourneyRuntimeEngine.OrderHangReason, "CONFIRMED", IdleReturnsQueryEndpoint.StepOrderStalled },
        { JourneyRuntimeEngine.OrderStateUnrecognizedReason, "CONFIRMED", IdleReturnsQueryEndpoint.StepOrderStalled },
        { VehicleFaultEvidence.OrderFailed, "CONFIRMED", IdleReturnsQueryEndpoint.StepFailedAwaitingManual },
        { VehicleFaultEvidence.DoorNotProvenLocked, "CONFIRMED", IdleReturnsQueryEndpoint.StepFailedAwaitingManual },
        { JourneyRuntimeEngine.HeldOrderResumedWithoutContinueReason, "CONFIRMED", IdleReturnsQueryEndpoint.StepFailedAwaitingManual },
        { JourneyRuntimeEngine.CheckpointWaitReason, "CONFIRMED", IdleReturnsQueryEndpoint.StepEnRoute },
        { JourneyRuntimeEngine.CheckpointWaitExceededReason, "CONFIRMED", IdleReturnsQueryEndpoint.StepEnRoute },
        { JourneyRuntimeEngine.AdvanceFailedReason, "CONFIRMED", IdleReturnsQueryEndpoint.StepAdvanceFailed },
        { IdleReturnExecutionReasons.DepartureNotProven, "PENDING_RECONCILIATION", IdleReturnsQueryEndpoint.StepCommitted },
        { IdleReturnExecutionReasons.WaitingPointLostOrderInFlight, "CONFIRMED", IdleReturnsQueryEndpoint.StepHeldAwaitingStop },
        { IdleReturnExecutionReasons.OrderEndedStopNotProven, "CONFIRMED", IdleReturnsQueryEndpoint.StepHeldAwaitingStop },
        { IdleReturnExecutionReasons.ArrivalNotProven, "CONFIRMED", IdleReturnsQueryEndpoint.StepArrivalNotProven },
        { "WAITING_POINT_ResultUnknown", "PENDING_RECONCILIATION", IdleReturnsQueryEndpoint.StepCreateResultUnknown },
        { "WAITING_POINT_CreateDispatchDisabled", "PENDING_RECONCILIATION", IdleReturnsQueryEndpoint.StepCommitted },
        { "WAITING_POINT_UnsupportedOrderShape", "PENDING_RECONCILIATION", IdleReturnsQueryEndpoint.StepCommitted },
    };

    [Theory]
    [MemberData(nameof(CodeToStep))]
    public void EachCodeOnAnOpenIdleReturnJourneyMapsToItsStep(string code, string intentStatus, string expected)
    {
        JourneyRuntimeRow journey = IdleReturnRuntime("K-01", IdleReturnIdentity.JourneyIdFor("K-01", At));
        journey.SetBlockReason(code, At);
        Assert.Equal(
            expected,
            IdleReturnsQueryEndpoint.Classify(
                null, journey, intentStatus, intentStatus == "CONFIRMED" ? "ORDER-1" : null, null));
    }

    /// <summary>
    /// 对照表盖住开着的空闲返回旅程上可能出现的每一个码（保持类、建单结果码、共用码）：新加一个而没进表就红。收尾码写在已完成的
    /// 旅程上，不走步骤分类。
    /// </summary>
    [Fact]
    public void TheCodeToStepTableCoversEveryCodeAnOpenIdleReturnJourneyCanCarry()
    {
        HashSet<string> table = [.. CodeToStep.Select(row => row.Data.Item1)];
        string[] inFlight =
        [
            IdleReturnExecutionReasons.DepartureNotProven,
            IdleReturnExecutionReasons.WaitingPointLostOrderInFlight,
            IdleReturnExecutionReasons.OrderEndedStopNotProven,
            IdleReturnExecutionReasons.ArrivalNotProven,
            .. IdleReturnExecutionReasons.LegOutcomeCodes,
            .. IdleReturnCodeDescriptions.SharedCodesOnIdleReturnJourneys.Keys,
        ];
        Assert.DoesNotContain(inFlight, code => !table.Contains(code));
    }

    /// <summary>
    /// 检查点等待与等待超时是两件事，现场要一眼分得开（调度 10-01、审查 MJ）：期望值写成字面量，不从被测的说明表里取。
    /// </summary>
    [Fact]
    public void TheCheckpointWaitAndTheCheckpointWaitExceededReadDifferently()
    {
        string wait = IdleReturnCodeDescriptions.DescribeJourneyCode("VEHICLE_WAITING_AT_CHECKPOINT")!;
        string exceeded = IdleReturnCodeDescriptions.DescribeJourneyCode("VEHICLE_CHECKPOINT_WAIT_EXCEEDED")!;
        Assert.Equal("开往等待点的车停在 RIoT 的检查点前等放行：这是 RIoT 的交通管制，服务端不干预，放行后自动继续", wait);
        Assert.Equal(
            "开往等待点的车在 RIoT 检查点前等放行，已超过服务端的等待时限：服务端只告警，不急停、不另选等待点。"
            + "请到 RIoT 查看交通管制为什么一直不放行",
            exceeded);
    }

    /// <summary>
    /// 阻断卡片上，空闲返回行的共用码用空闲返回口径的说明（调度 09-30，cs#390 增量审查 L-d）；同一个码在搬运行上照旧是搬运口径。
    /// </summary>
    [Fact]
    public async Task TheBlockedJourneyCardDescribesASharedCodeOnAnIdleReturnRowInIdleReturnTerms()
    {
        await using DashboardDatabase database = await DashboardDatabase.CreateAsync();
        await database.SeedAsync(context =>
        {
            AddIdleReturnJourney(
                context, "K-01", IdleReturnIdentity.JourneyIdFor("K-01", At), JourneyRuntimeEngine.OrderHangReason, completed: false, "CONFIRMED");
            JourneyRuntimeRow transport = WaitingJourneyBatteryWatchTests.Runtime("D-HANG", JourneyRuntimeStage.AwaitingGateArrival, At);
            transport.SetBlockReason(JourneyRuntimeEngine.OrderHangReason, At);
            context.Add(transport);
        });

        await using ControlServerDbContext context = database.NewContext();
        object rows = await new BlockedJourneysQueryEndpoint().ReadAsync(context, TestContext.Current.CancellationToken);
        using JsonDocument fact = JsonDocument.Parse(JsonSerializer.Serialize(rows));
        JsonElement[] journeys = [.. fact.RootElement.GetProperty("journeys").EnumerateArray()];
        JsonElement idle = journeys.Single(row => row.GetProperty("agvId").GetString() == "AGV-01");
        JsonElement carrying = journeys.Single(row => row.GetProperty("agvId").GetString() == "AGV-D-HANG");
        Assert.Equal(
            IdleReturnCodeDescriptions.SharedCodesOnIdleReturnJourneys[JourneyRuntimeEngine.OrderHangReason],
            idle.GetProperty("blockReasonDescription").GetString());
        Assert.Equal(
            BlockedJourneysQueryEndpoint.Descriptions[JourneyRuntimeEngine.OrderHangReason],
            carrying.GetProperty("blockReasonDescription").GetString());

        IDashboardCard card = DashboardCardCatalog.Discovered.Cards.Single(candidate => candidate.CardId == new BlockedJourneyCard().CardId);
        string html = card.RenderFact(fact.RootElement);
        Assert.Contains("开往等待点的空闲返回单在 RIoT 上挂起", RowWith(html, "<td>AGV-01</td>"), StringComparison.Ordinal);
        Assert.DoesNotContain("同一条需求", RowWith(html, "<td>AGV-01</td>"), StringComparison.Ordinal);
    }

    /// <summary>评估器的每一个结论码（<see cref="IdleReturnReasons"/> 的公开常量，反射扫）都有中文说明。</summary>
    [Fact]
    public void EveryIdleReturnVerdictCodeHasAChineseDescription()
    {
        string[] codes =
        [
            .. typeof(IdleReturnReasons).GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(field => field.IsLiteral && field.FieldType == typeof(string))
                .Select(field => (string)field.GetRawConstantValue()!),
        ];
        Assert.Contains(IdleReturnReasons.CooldownAfterEndedOrder, codes);
        Assert.Contains(IdleReturnReasons.StoppedAfterRepeatedEndedOrders, codes);
        foreach (string code in codes)
        {
            Assert.False(string.IsNullOrWhiteSpace(IdleReturnCodeDescriptions.DescribeVerdict(code)), $"No description for {code}.");
        }
    }

    // ---------------- helpers ----------------

    private static void SeedIdleReturnStep(ControlServerDbContext context, string step, string? code, string journeyId)
    {
        switch (step)
        {
            case IdleReturnsQueryEndpoint.StepNone:
                return;
            case IdleReturnsQueryEndpoint.StepOtherPurpose:
                context.Add(Claim("K-01", VehiclePurposes.Transport, JourneyIdentity.ForAnchorDemand("D-1")));
                return;
            case IdleReturnsQueryEndpoint.StepAtPoint:
                context.AddRange(Holding(StationExclusivityKinds.WaitingPoint, 212, "K-01", StationExclusivityStates.Occupied, 2));
                return;
            case IdleReturnsQueryEndpoint.StepCommitted when code is null:
                // 承诺已成、还没物化：只有用途占有与预占，没有旅程行。
                context.Add(Claim("K-01", VehiclePurposes.IdleReturn, journeyId));
                context.AddRange(Holding(StationExclusivityKinds.WaitingPoint, 212, "K-01", StationExclusivityStates.Reserved, 2, journeyId));
                return;
        }

        context.Add(Claim("K-01", VehiclePurposes.IdleReturn, journeyId));
        context.AddRange(Holding(StationExclusivityKinds.WaitingPoint, 212, "K-01", StationExclusivityStates.Reserved, 2, journeyId));
        string intentStatus = step switch
        {
            IdleReturnsQueryEndpoint.StepCommitted => "PENDING_RECONCILIATION",
            IdleReturnsQueryEndpoint.StepCreateResultUnknown => "RESULT_UNKNOWN",
            _ => "CONFIRMED",
        };
        AddIdleReturnJourney(context, "K-01", journeyId, code, completed: false, intentStatus);
    }

    private static void AddIdleReturnJourney(
        ControlServerDbContext context, string vehicleKey, string journeyId, string? code, bool completed, string intentStatus)
    {
        JourneyRuntimeRow runtime = IdleReturnRuntime(vehicleKey, journeyId);
        runtime.SetBlockReason(code, At.AddMinutes(1));
        if (completed)
        {
            runtime.Stage = JourneyRuntimeStage.Completed;
        }
        context.Add(runtime);
        context.Add(new OrderIntentRow
        {
            MovementLegId = runtime.PickupMovementLegId!,
            UpperId = runtime.PickupUpperId!,
            Purpose = IdleReturnJourneyShape.IntentPurpose,
            TargetStationId = runtime.PickupStationId!,
            VehicleKey = vehicleKey,
            MapId = MapId,
            DestinationStationId = runtime.PickupStationRiotId,
            CreatedAt = At,
            Status = intentStatus,
            OrderId = intentStatus == "CONFIRMED" ? "RIOT-ORDER-1" : null,
        });
    }

    private static JourneyRuntimeRow IdleReturnRuntime(string vehicleKey, string journeyId)
    {
        string agvId = "AGV-" + vehicleKey[2..];
        JourneyRuntimeOptions options = new() { MapId = MapId };
        return IdleReturnJourneyShape.Build(journeyId, new FleetVehicle(agvId, vehicleKey, 1), 212, "WP-212", options, At).Runtime;
    }

    private static VehiclePurposeClaimRow Claim(string vehicleKey, string purpose, string journeyId) => new()
    {
        VehicleKey = vehicleKey,
        Purpose = purpose,
        JourneyId = journeyId,
        ClaimedAt = At,
    };

    private static VehiclePurposeClaimRecordRow ReleasedClaimRecord(string vehicleKey, string journeyId, DateTimeOffset releasedAt) => new()
    {
        RecordId = Guid.NewGuid().ToString("D"),
        VehicleKey = vehicleKey,
        Purpose = VehiclePurposes.IdleReturn,
        JourneyId = journeyId,
        AcquiredAt = At,
        ReleasedAt = releasedAt,
        ReleaseReason = IdleReturnExecutionReasons.OrderEnded,
    };

    private static object[] Holding(
        string kind, int stationId, string vehicleKey, string state, long? waitingPointVersion, string? journeyId = null)
    {
        string recordId = Guid.NewGuid().ToString("D");
        string holder = journeyId ?? IdleReturnIdentity.JourneyIdFor(vehicleKey, At.AddSeconds(-stationId));
        return
        [
            new StationExclusivityRow
            {
                MapId = MapId,
                StationId = stationId,
                StationKind = kind,
                State = state,
                VehicleKey = vehicleKey,
                JourneyId = holder,
                StateSince = At,
                WaitingPointVersion = waitingPointVersion,
                RecordId = recordId,
            },
            new StationExclusivityRecordRow
            {
                RecordId = recordId,
                MapId = MapId,
                StationId = stationId,
                StationKind = kind,
                VehicleKey = vehicleKey,
                JourneyId = holder,
                WaitingPointVersion = waitingPointVersion,
                ReservedAt = At,
                OccupiedAt = state == StationExclusivityStates.Occupied ? At : null,
            },
        ];
    }

    private static void AddWaitingPoints(ControlServerDbContext context, long version, params (int StationId, bool Enabled)[] points)
    {
        context.Add(new WaitingPointVersionRow
        {
            Version = version,
            ContentSha256 = new string((char)('a' + version), 64),
            LoadedAt = At.AddDays(version - 3),
            Source = WaitingPointSources.GovernedImport,
        });
        foreach ((int stationId, bool enabled) in points)
        {
            context.Add(new WaitingPointRow
            {
                Version = version,
                MapId = MapId,
                StationId = stationId,
                StationName = $"WP-{stationId}",
                Enabled = enabled,
            });
        }
    }

    private static TaskTypeStationBindingRow Binding(long version, string taskType, int stationId) => new()
    {
        MapId = MapId,
        Version = version,
        TaskType = taskType,
        StationRiotId = stationId,
        StationName = $"ST-{stationId}",
        SiteVerificationRef = "site-check",
    };

    private static async Task<(JsonDocument Fact, string Html)> ReadAsync(
        DashboardDatabase database, string path, IdleReturnVerdictBoard? board = null)
    {
        IDashboardCard card = DashboardCardCatalog.Discovered.Cards
            .Single(candidate => candidate.SourcePath == DashboardPaths.QueryPrefix + path);
        IDashboardQueryEndpoint endpoint = DashboardQueryEndpointCatalog
            .Discover(typeof(FleetSessionsQueryEndpoint).Assembly)
            .Endpoints.Single(candidate => candidate.Path == card.SourcePath);
        // 换成测试的名册：同一个类型的内部构造，读法与宿主一样。
        endpoint = endpoint switch
        {
            VehiclePurposesQueryEndpoint => new VehiclePurposesQueryEndpoint(Roster, TimeProvider.System),
            WaitingPointsQueryEndpoint => new WaitingPointsQueryEndpoint(Roster, TimeProvider.System),
            FixedTaskStationsQueryEndpoint => new FixedTaskStationsQueryEndpoint(Roster, TimeProvider.System),
            IdleReturnsQueryEndpoint => new IdleReturnsQueryEndpoint(FleetOptions, board ?? new IdleReturnVerdictBoard(), TimeProvider.System),
            _ => throw new InvalidOperationException(path),
        };
        await using ControlServerDbContext context = database.NewContext();
        object rows = await endpoint.ReadAsync(context, TestContext.Current.CancellationToken);
        JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(rows));
        return (document, card.RenderFact(document.RootElement));
    }

    private static VehicleRoster Roster => new(FleetOptions);

    private static readonly IOptions<JourneyRuntimeOptions> FleetOptions = Options.Create(new JourneyRuntimeOptions
    {
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

    /// <summary>AGV-05 是唯一的失联车：它只在失联列表里，任何一行都不以它开头。</summary>
    private static void AssertOnlyLost(JsonElement root, string html)
    {
        JsonElement[] lost = [.. root.GetProperty("unavailableVehicles").EnumerateArray()];
        Assert.Equal(["AGV-05"], lost.Select(v => v.GetProperty("agvId").GetString()));
        Assert.Equal(VehicleAlarmProjection.LinkDownReason, lost[0].GetProperty("reason").GetString());
        Assert.Equal(["agvId", "reason"], lost[0].EnumerateObject().Select(p => p.Name));
        Assert.DoesNotContain("<tr><td>AGV-05</td>", html, StringComparison.Ordinal);
        Assert.Contains($"AGV-05：{VehicleAlarmProjection.LinkDownReason}", html, StringComparison.Ordinal);
    }

    private static void AssertCleanHtml(string html)
    {
        Assert.DoesNotContain("<form", html, StringComparison.OrdinalIgnoreCase);
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
        // Rows may carry attributes (<tr class=...>), so the row start is the last "<tr" at or before the marker.
        start = html.LastIndexOf("<tr", start + "<tr".Length - 1, StringComparison.Ordinal);
        int end = html.IndexOf("</tr>", start, StringComparison.Ordinal);
        return html[start..end];
    }

    private sealed class DashboardDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private DashboardDatabase(SqliteConnection connection) => _connection = connection;

        public static async Task<DashboardDatabase> CreateAsync()
        {
            SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            DashboardDatabase database = new(connection);
            await using ControlServerDbContext context = database.NewContext();
            await context.Database.MigrateAsync(TestContext.Current.CancellationToken);
            // AGV-01..04 说过话；AGV-05 的会话行在，最后一条消息却在六秒存活时限之外。
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
                    UpdatedAt = At,
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
