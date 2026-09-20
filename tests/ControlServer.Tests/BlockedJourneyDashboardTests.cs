using ControlServer.Application;
using System.Text.Json;
using ControlServer.Dashboard;
using ControlServer.Domain;
using ControlServer.Host.Dashboard;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace ControlServer.Tests;

/// <summary>
/// 旅程阻断带开始时间上看板（control-server#80，program#55）：开始时间的四条规则、两道升级线的边界、只读查询端点的字段，
/// 以及自注册的卡片。
/// </summary>
/// <remarks>
/// 库由迁移建出，所以这里读到的 <c>BlockReasonSince</c> 就是批次 5 那个迁移加的那一列。
/// </remarks>
public sealed class BlockedJourneyDashboardTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 17, 8, 0, 0, TimeSpan.Zero);

    // --- 开始时间：挂上即记、同码持续不变、换码重记、清掉即清 ---------------------------------------------------

    [Fact]
    public void TheStartIsRecordedWhenACodeFirstAppearsAndKeptWhileTheSameCodeIsWrittenAgain()
    {
        JourneyRuntimeRow runtime = Runtime("D-1", "AGV-01");
        Assert.Null(runtime.BlockReasonSince);

        runtime.SetBlockReason("LOAD_RESULT_REQUIRES_RECOVERY", Now);
        Assert.Equal("LOAD_RESULT_REQUIRES_RECOVERY", runtime.BlockReasonCode);
        Assert.Equal(Now, runtime.BlockReasonSince);

        runtime.SetBlockReason("LOAD_RESULT_REQUIRES_RECOVERY", Now.AddMinutes(12));
        runtime.UpdatedAt = Now.AddMinutes(12);
        Assert.Equal(Now, runtime.BlockReasonSince);
    }

    [Fact]
    public void ADifferentCodeStartsTheClockOverAndClearingTheCodeClearsTheStart()
    {
        JourneyRuntimeRow runtime = Runtime("D-1", "AGV-01");
        runtime.SetBlockReason("VEHICLE_WAITING_AT_CHECKPOINT", Now);

        runtime.SetBlockReason("VEHICLE_CHECKPOINT_WAIT_EXCEEDED", Now.AddMinutes(6));
        Assert.Equal("VEHICLE_CHECKPOINT_WAIT_EXCEEDED", runtime.BlockReasonCode);
        Assert.Equal(Now.AddMinutes(6), runtime.BlockReasonSince);

        runtime.SetBlockReason(null, Now.AddMinutes(7));
        Assert.Null(runtime.BlockReasonCode);
        Assert.Null(runtime.BlockReasonSince);

        // 清掉之后同一个码再来，是一次新的阻断，从它自己挂上的时刻算。
        runtime.SetBlockReason("VEHICLE_CHECKPOINT_WAIT_EXCEEDED", Now.AddMinutes(9));
        Assert.Equal(Now.AddMinutes(9), runtime.BlockReasonSince);
    }

    [Fact]
    public async Task TheStartSurvivesTheDatabaseAndALaterWriteOfTheSameCodeFromAFreshContext()
    {
        await using DashboardDatabase database = await DashboardDatabase.CreateAsync();
        JourneyRuntimeRow runtime = Runtime("D-1", "AGV-01");
        runtime.SetBlockReason("ONBOARD_SESSION_NOT_READY", Now);
        database.Context.JourneyRuntimes.Add(runtime);
        await database.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        await using ControlServerDbContext later = database.NewContext();
        JourneyRuntimeRow reloaded = await later.JourneyRuntimes.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(Now, reloaded.BlockReasonSince);
        reloaded.SetBlockReason("ONBOARD_SESSION_NOT_READY", Now.AddMinutes(20));
        reloaded.UpdatedAt = Now.AddMinutes(20);
        await later.SaveChangesAsync(TestContext.Current.CancellationToken);

        await using ControlServerDbContext third = database.NewContext();
        JourneyRuntimeRow read = await third.JourneyRuntimes.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(Now.AddMinutes(20), read.UpdatedAt);
        Assert.Equal(Now, read.BlockReasonSince);
    }

    // --- 分档：10 分钟、30 分钟两侧，安全证据不全直接最高档 --------------------------------------------------------

    [Theory]
    [InlineData(0, "Operator")]
    [InlineData(599, "Operator")]
    [InlineData(600, "ShiftLeader")]
    [InlineData(601, "ShiftLeader")]
    [InlineData(1799, "ShiftLeader")]
    [InlineData(1800, "MaintenanceAdministrator")]
    [InlineData(1801, "MaintenanceAdministrator")]
    public void TheTwoLinesAreTenAndThirtyMinutesAndReachingALineCountsAsPastIt(int seconds, string expected)
    {
        BlockedJourneyEscalationLevel level = BlockedJourneyEscalationOptions.Default.Classify(
            TimeSpan.FromSeconds(seconds), carriesSessionSafety: false, safetyUnknownPresent: null);

        Assert.Equal(expected, level.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData(true)]
    public void IncompleteSafetyEvidenceGoesStraightToTheTopWithoutWaiting(bool? safetyUnknownPresent)
    {
        Assert.Equal(
            BlockedJourneyEscalationLevel.MaintenanceAdministrator,
            BlockedJourneyEscalationOptions.Default.Classify(
                TimeSpan.Zero, carriesSessionSafety: true, safetyUnknownPresent));
    }

    [Fact]
    public void CompleteSafetyEvidenceWaitsForTheLinesLikeAnyOtherBlock()
    {
        BlockedJourneyEscalationOptions lines = BlockedJourneyEscalationOptions.Default;

        Assert.Equal(
            BlockedJourneyEscalationLevel.Operator,
            lines.Classify(TimeSpan.FromSeconds(599), carriesSessionSafety: true, safetyUnknownPresent: false));
        Assert.Equal(
            BlockedJourneyEscalationLevel.ShiftLeader,
            lines.Classify(TimeSpan.FromSeconds(600), carriesSessionSafety: true, safetyUnknownPresent: false));
    }

    // --- 由自己的在途移动单解释的「未知」（control-server#139）-------------------------------------------------------

    [Fact]
    public void AnUnknownOnlyTheVehicleSideReportsWhileThisServersOwnMoveOrderIsInFlightIsExplained()
    {
        Assert.True(OwnMovementOrderExplanation.Explains(
            "ONBOARD_SESSION_NOT_READY",
            "DEPARTURE_SAFETY_NOT_READY",
            """["ACTION_NOT_ALLOWED_IN_STATE","VEHICLE_NOT_READY"]""",
            safetyUnknownPresent: true,
            ownMovementOrderInFlight: true));
        Assert.True(OwnMovementOrderExplanation.Explains(
            "ONBOARD_SESSION_NOT_READY",
            "DEPARTURE_SAFETY_NOT_READY",
            """["VEHICLE_NOT_READY"]""",
            safetyUnknownPresent: true,
            ownMovementOrderInFlight: true));
    }

    [Theory]
    // 阻断不是会话未就绪。
    [InlineData("VEHICLE_WAITING_AT_CHECKPOINT", "DEPARTURE_SAFETY_NOT_READY", """["VEHICLE_NOT_READY"]""", true, true)]
    // 会话降级的原因不是离站安全。
    [InlineData("ONBOARD_SESSION_NOT_READY", "PENDING_FACT_RECONCILIATION_REQUIRED", """["VEHICLE_NOT_READY"]""", true, true)]
    [InlineData("ONBOARD_SESSION_NOT_READY", null, """["VEHICLE_NOT_READY"]""", true, true)]
    // 安全原因里有仓位侧的码：未知可能来自仓位，不只来自车。
    [InlineData("ONBOARD_SESSION_NOT_READY", "DEPARTURE_SAFETY_NOT_READY", """["SLOT_STATE_UNKNOWN","VEHICLE_NOT_READY"]""", true, true)]
    [InlineData("ONBOARD_SESSION_NOT_READY", "DEPARTURE_SAFETY_NOT_READY", """["IO_FACT_UNKNOWN","VEHICLE_NOT_READY"]""", true, true)]
    // 没有车辆侧的码，未知就说不上来自车辆信号。
    [InlineData("ONBOARD_SESSION_NOT_READY", "DEPARTURE_SAFETY_NOT_READY", """["ACTION_NOT_ALLOWED_IN_STATE"]""", true, true)]
    [InlineData("ONBOARD_SESSION_NOT_READY", "DEPARTURE_SAFETY_NOT_READY", "[]", true, true)]
    // 原因码没报或读不懂。
    [InlineData("ONBOARD_SESSION_NOT_READY", "DEPARTURE_SAFETY_NOT_READY", null, true, true)]
    [InlineData("ONBOARD_SESSION_NOT_READY", "DEPARTURE_SAFETY_NOT_READY", "not json", true, true)]
    [InlineData("ONBOARD_SESSION_NOT_READY", "DEPARTURE_SAFETY_NOT_READY", """{"code":"VEHICLE_NOT_READY"}""", true, true)]
    // 没有未知项，就没有要解释的东西；没有会话行（null）不算「没有未知」。
    [InlineData("ONBOARD_SESSION_NOT_READY", "DEPARTURE_SAFETY_NOT_READY", """["VEHICLE_NOT_READY"]""", false, true)]
    [InlineData("ONBOARD_SESSION_NOT_READY", "DEPARTURE_SAFETY_NOT_READY", """["VEHICLE_NOT_READY"]""", null, true)]
    // 服务端没有自己的在途移动单。
    [InlineData("ONBOARD_SESSION_NOT_READY", "DEPARTURE_SAFETY_NOT_READY", """["VEHICLE_NOT_READY"]""", true, false)]
    public void EveryOtherShapeOfIncompleteEvidenceIsNotExplained(
        string blockReasonCode,
        string? sessionReasonCode,
        string? safetyReasonCodesJson,
        bool? safetyUnknownPresent,
        bool ownMovementOrderInFlight)
    {
        Assert.False(OwnMovementOrderExplanation.Explains(
            blockReasonCode, sessionReasonCode, safetyReasonCodesJson, safetyUnknownPresent, ownMovementOrderInFlight));
    }

    [Theory]
    [InlineData(0, "Operator")]
    [InlineData(599, "Operator")]
    [InlineData(600, "ShiftLeader")]
    [InlineData(1799, "ShiftLeader")]
    [InlineData(1800, "MaintenanceAdministrator")]
    public void AnUnknownExplainedByTheOwnOrderClimbsTheLadderLikeAnyOtherBlock(int seconds, string expected)
    {
        Assert.Equal(
            expected,
            BlockedJourneyEscalationOptions.Default.Classify(
                TimeSpan.FromSeconds(seconds),
                carriesSessionSafety: true,
                safetyUnknownPresent: true,
                unknownExplainedByOwnOrder: true).ToString());
    }

    [Fact]
    public void AnExplainedUnknownWhoseStartWasNeverRecordedStillGoesToTheTop()
    {
        Assert.Equal(
            BlockedJourneyEscalationLevel.MaintenanceAdministrator,
            BlockedJourneyEscalationOptions.Default.Classify(
                null, carriesSessionSafety: true, safetyUnknownPresent: true, unknownExplainedByOwnOrder: true));
    }

    [Fact]
    public void ABlockWhoseStartWasNeverRecordedIsNotShownAsYoung()
    {
        Assert.Equal(
            BlockedJourneyEscalationLevel.MaintenanceAdministrator,
            BlockedJourneyEscalationOptions.Default.Classify(null, carriesSessionSafety: false, safetyUnknownPresent: null));
    }

    [Fact]
    public void TheLinesComeFromTheirOwnSettingsFileWhichShipsTheProcedureDefaults()
    {
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, BlockedJourneyEscalationOptions.FileName)));
        Assert.Equal(BlockedJourneyEscalationOptions.Default, BlockedJourneyEscalationOptions.Load(AppContext.BaseDirectory));

        string appSettings = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "ControlServer.Host", "appsettings.json"));
        Assert.DoesNotContain(BlockedJourneyEscalationOptions.SectionName, appSettings, StringComparison.Ordinal);

        BlockedJourneyEscalationOptions configured = BlockedJourneyEscalationOptions.From(Section(("shiftLeaderAfter", "00:05:00"), ("maintenanceAdministratorAfter", "00:45:00")));
        Assert.Equal(TimeSpan.FromMinutes(5), configured.ShiftLeaderAfter);
        Assert.Equal(TimeSpan.FromMinutes(45), configured.MaintenanceAdministratorAfter);
        Assert.Equal(
            BlockedJourneyEscalationLevel.ShiftLeader,
            configured.Classify(TimeSpan.FromMinutes(44), carriesSessionSafety: false, safetyUnknownPresent: null));
    }

    [Theory]
    [InlineData("00:00:00", "00:30:00")]
    [InlineData("00:30:00", "00:10:00")]
    [InlineData("00:10:00", "00:10:00")]
    [InlineData("ten minutes", "00:30:00")]
    public void LinesThatCannotBeAnEscalationAreRefused(string shiftLeaderAfter, string maintenanceAdministratorAfter)
    {
        Assert.Throws<InvalidDataException>(() => BlockedJourneyEscalationOptions.From(
            Section(("shiftLeaderAfter", shiftLeaderAfter), ("maintenanceAdministratorAfter", maintenanceAdministratorAfter))));
    }

    // --- 查询端点：只读、绑 127.0.0.1、字段齐全 ------------------------------------------------------------------

    [Fact]
    public async Task TheEndpointListsEveryBlockedJourneyWithItsVehicleStationCodeStartAndElapsedTime()
    {
        await using DashboardDatabase database = await DashboardDatabase.CreateAsync();
        JourneyRuntimeRow atPickup = Runtime("D-PICKUP", "AGV-01");
        atPickup.Stage = JourneyRuntimeStage.Blocked;
        atPickup.SetBlockReason("LOAD_RESULT_REQUIRES_RECOVERY", Now.AddMinutes(-31));
        JourneyRuntimeRow atGate = Runtime("D-GATE", "AGV-02");
        atGate.Stage = JourneyRuntimeStage.Blocked;
        atGate.SetBlockReason("UNLOAD_RESULT_REQUIRES_RECOVERY", Now.AddMinutes(-12));
        JourneyRuntimeRow waiting = Runtime("D-CHECKPOINT", "AGV-03");
        waiting.Stage = JourneyRuntimeStage.AwaitingGateArrival;
        waiting.SetBlockReason("VEHICLE_WAITING_AT_CHECKPOINT", Now.AddSeconds(-30));
        JourneyRuntimeRow moving = Runtime("D-MOVING", "AGV-04");
        moving.Stage = JourneyRuntimeStage.AwaitingPickupArrival;
        JourneyRuntimeRow ended = Runtime("D-ENDED", "AGV-05");
        ended.Stage = JourneyRuntimeStage.Completed;
        ended.SetBlockReason("CANCELLED_BY_OPERATOR", Now.AddHours(-3));
        database.Context.JourneyRuntimes.AddRange(atPickup, atGate, waiting, moving, ended);
        // 去关卡的移动意图只在出发授权时建：有它，停摆的旅程就停在关卡这一段。
        database.Context.OrderIntents.Add(GateIntent(atGate));
        await database.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        using JsonDocument fact = await ReadAsync(database);

        Assert.Equal(600, fact.RootElement.GetProperty("shiftLeaderAfterSeconds").GetInt64());
        Assert.Equal(1800, fact.RootElement.GetProperty("maintenanceAdministratorAfterSeconds").GetInt64());
        JsonElement[] journeys = [.. fact.RootElement.GetProperty("journeys").EnumerateArray()];
        Assert.Equal(
            ["D-PICKUP", "D-GATE", "D-CHECKPOINT"],
            journeys.Select(journey => journey.GetProperty("demandId").GetString()));

        JsonElement pickup = journeys[0];
        Assert.Equal("AGV-01", pickup.GetProperty("agvId").GetString());
        Assert.Equal("Blocked", pickup.GetProperty("stage").GetString());
        Assert.Equal("PICKUP-1", pickup.GetProperty("stationId").GetString());
        Assert.Equal("LOAD_RESULT_REQUIRES_RECOVERY", pickup.GetProperty("blockReasonCode").GetString());
        Assert.Equal(Now.AddMinutes(-31), pickup.GetProperty("blockReasonSince").GetDateTimeOffset());
        Assert.Equal(31 * 60, pickup.GetProperty("blockedSeconds").GetInt64());
        Assert.Equal("MaintenanceAdministrator", pickup.GetProperty("escalationLevel").GetString());
        Assert.Equal(JsonValueKind.Null, pickup.GetProperty("session").ValueKind);

        JsonElement gate = journeys[1];
        Assert.Equal("GATE-1", gate.GetProperty("stationId").GetString());
        Assert.Equal("ShiftLeader", gate.GetProperty("escalationLevel").GetString());

        JsonElement checkpoint = journeys[2];
        Assert.Equal("GATE-1", checkpoint.GetProperty("stationId").GetString());
        Assert.Equal(30, checkpoint.GetProperty("blockedSeconds").GetInt64());
        Assert.Equal("Operator", checkpoint.GetProperty("escalationLevel").GetString());
    }

    [Fact]
    public async Task ASessionNotReadyBlockCarriesTheSessionReasonAndBothSafetyFields()
    {
        await using DashboardDatabase database = await DashboardDatabase.CreateAsync();
        JourneyRuntimeRow unknownSafety = Runtime("D-UNKNOWN", "AGV-01");
        unknownSafety.SetBlockReason("ONBOARD_SESSION_NOT_READY", Now.AddMinutes(-1));
        JourneyRuntimeRow knownSafety = Runtime("D-KNOWN", "AGV-02");
        knownSafety.SetBlockReason("ONBOARD_SESSION_NOT_READY", Now.AddMinutes(-2));
        JourneyRuntimeRow noSession = Runtime("D-NO-SESSION", "AGV-03");
        noSession.SetBlockReason("ONBOARD_SESSION_NOT_READY", Now.AddMinutes(-3));
        database.Context.JourneyRuntimes.AddRange(unknownSafety, knownSafety, noSession);
        database.Context.SessionRecoveries.AddRange(
            Session("AGV-01", "DEPARTURE_SAFETY_NOT_READY", """["IO_FACT_UNKNOWN"]""", safetyUnknownPresent: true),
            Session("AGV-02", "DEPARTURE_SAFETY_NOT_READY", """["UNLOCK_OUTPUT_NOT_RESET"]""", safetyUnknownPresent: false));
        await database.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        using JsonDocument fact = await ReadAsync(database);
        Dictionary<string, JsonElement> byDemand = fact.RootElement.GetProperty("journeys").EnumerateArray()
            .ToDictionary(journey => journey.GetProperty("demandId").GetString()!, StringComparer.Ordinal);

        JsonElement unknown = byDemand["D-UNKNOWN"];
        JsonElement unknownSession = unknown.GetProperty("session");
        Assert.True(unknownSession.GetProperty("present").GetBoolean());
        Assert.Equal("DEPARTURE_SAFETY_NOT_READY", unknownSession.GetProperty("reasonCode").GetString());
        Assert.Equal("""["IO_FACT_UNKNOWN"]""", unknownSession.GetProperty("safetyReasonCodesJson").GetString());
        Assert.True(unknownSession.GetProperty("safetyUnknownPresent").GetBoolean());
        // 挂上才一分钟，但安全证据不全：不等时长。
        Assert.Equal("MaintenanceAdministrator", unknown.GetProperty("escalationLevel").GetString());

        JsonElement known = byDemand["D-KNOWN"];
        Assert.False(known.GetProperty("session").GetProperty("safetyUnknownPresent").GetBoolean());
        Assert.Equal("""["UNLOCK_OUTPUT_NOT_RESET"]""", known.GetProperty("session").GetProperty("safetyReasonCodesJson").GetString());
        Assert.Equal("Operator", known.GetProperty("escalationLevel").GetString());

        JsonElement missing = byDemand["D-NO-SESSION"];
        Assert.False(missing.GetProperty("session").GetProperty("present").GetBoolean());
        Assert.Equal(JsonValueKind.Null, missing.GetProperty("session").GetProperty("safetyUnknownPresent").ValueKind);
        Assert.Equal("MaintenanceAdministrator", missing.GetProperty("escalationLevel").GetString());
    }

    /// <summary>
    /// control-server#198：停在 AREA 机台等准入恢复的旅程，车载端掉线时引擎保留 <c>TASK_TYPE_NOT_ALLOWED_AT_STATION</c>
    /// （计时从第一次停住起算，不因掉线归零）。看板照样把它当会话未就绪看：带出会话原因与两个安全字段，安全证据未知或
    /// 会话行缺失就直接最高档，不等时长；阻断码与开始时间原样给出。会话就绪时不带会话、按时长走，与改前一样。
    /// </summary>
    [Fact]
    public async Task AStopHeldForAdmissionAtTheMachineWithTheSessionDownIsJudgedAsASessionNotReadyBlock()
    {
        await using DashboardDatabase database = await DashboardDatabase.CreateAsync();
        JourneyRuntimeRow unknownSafety = HeldForAdmission("D-HELD-UNKNOWN", "AGV-01");
        JourneyRuntimeRow noSession = HeldForAdmission("D-HELD-NO-SESSION", "AGV-02");
        JourneyRuntimeRow sessionUp = HeldForAdmission("D-HELD-READY", "AGV-03");
        database.Context.JourneyRuntimes.AddRange(unknownSafety, noSession, sessionUp);
        SessionRecoveryRow ready = Session("AGV-03", "READY", "[]", safetyUnknownPresent: false);
        ready.Readiness = SessionReadiness.Ready;
        database.Context.SessionRecoveries.AddRange(
            Session("AGV-01", "DEPARTURE_SAFETY_NOT_READY", """["IO_FACT_UNKNOWN"]""", safetyUnknownPresent: true),
            ready);
        await database.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        using JsonDocument fact = await ReadAsync(database);
        ZeroChangePin.AssertTextMatches(fact.RootElement.GetRawText(), "dashboard-blocked-journeys-admission-session-down");
        Dictionary<string, JsonElement> byDemand = fact.RootElement.GetProperty("journeys").EnumerateArray()
            .ToDictionary(journey => journey.GetProperty("demandId").GetString()!, StringComparer.Ordinal);

        JsonElement unknown = byDemand["D-HELD-UNKNOWN"];
        Assert.Equal(
            ("TASK_TYPE_NOT_ALLOWED_AT_STATION", 120L, "MaintenanceAdministrator"),
            (unknown.GetProperty("blockReasonCode").GetString(), unknown.GetProperty("blockedSeconds").GetInt64(),
                unknown.GetProperty("escalationLevel").GetString()));
        Assert.Equal(JsonValueKind.Object, unknown.GetProperty("session").ValueKind);
        JsonElement unknownSession = unknown.GetProperty("session");
        Assert.True(unknownSession.GetProperty("present").GetBoolean());
        Assert.Equal("DEPARTURE_SAFETY_NOT_READY", unknownSession.GetProperty("reasonCode").GetString());
        Assert.Equal("""["IO_FACT_UNKNOWN"]""", unknownSession.GetProperty("safetyReasonCodesJson").GetString());
        Assert.True(unknownSession.GetProperty("safetyUnknownPresent").GetBoolean());

        JsonElement missing = byDemand["D-HELD-NO-SESSION"];
        Assert.Equal(JsonValueKind.Object, missing.GetProperty("session").ValueKind);
        Assert.False(missing.GetProperty("session").GetProperty("present").GetBoolean());
        Assert.Equal("MaintenanceAdministrator", missing.GetProperty("escalationLevel").GetString());

        JsonElement up = byDemand["D-HELD-READY"];
        Assert.Equal(JsonValueKind.Null, up.GetProperty("session").ValueKind);
        Assert.Equal("Operator", up.GetProperty("escalationLevel").GetString());
    }

    private static JourneyRuntimeRow HeldForAdmission(string demandId, string agvId)
    {
        JourneyRuntimeRow runtime = Runtime(demandId, agvId);
        runtime.Stage = JourneyRuntimeStage.AwaitingGateArrival;
        runtime.SetBlockReason("TASK_TYPE_NOT_ALLOWED_AT_STATION", Now.AddMinutes(-2));
        return runtime;
    }

    [Fact]
    public async Task AVehicleOnItsOwnMoveOrderIsNotSentToMaintenanceButEveryOtherUnknownStillIs()
    {
        await using DashboardDatabase database = await DashboardDatabase.CreateAsync();
        const string VehicleOnly = """["ACTION_NOT_ALLOWED_IN_STATE","VEHICLE_NOT_READY"]""";

        // 去关卡途中，自己的单已在 RIoT 上建成：刚挂两分钟，归操作员。
        JourneyRuntimeRow toGate = Runtime("D-TO-GATE", "AGV-01");
        toGate.Stage = JourneyRuntimeStage.AwaitingGateArrival;
        toGate.SetBlockReason("ONBOARD_SESSION_NOT_READY", Now.AddMinutes(-2));
        // 去取货站途中挂了 12 分钟：照阶梯归班组长。
        JourneyRuntimeRow toPickup = Runtime("D-TO-PICKUP", "AGV-02");
        toPickup.Stage = JourneyRuntimeStage.AwaitingPickupArrival;
        toPickup.SetBlockReason("ONBOARD_SESSION_NOT_READY", Now.AddMinutes(-12));
        // 还在等离站安全，去关卡的单还没建：没有在途单可以解释。
        JourneyRuntimeRow departing = Runtime("D-DEPARTING", "AGV-03");
        departing.Stage = JourneyRuntimeStage.AwaitingDepartureSafety;
        departing.SetBlockReason("ONBOARD_SESSION_NOT_READY", Now.AddMinutes(-1));
        // 单还在对账，没确认建成。
        JourneyRuntimeRow unconfirmed = Runtime("D-UNCONFIRMED", "AGV-04");
        unconfirmed.Stage = JourneyRuntimeStage.AwaitingGateArrival;
        unconfirmed.SetBlockReason("ONBOARD_SESSION_NOT_READY", Now.AddMinutes(-1));
        // 单派给的不是这辆车。
        JourneyRuntimeRow otherVehicle = Runtime("D-OTHER-VEHICLE", "AGV-05");
        otherVehicle.Stage = JourneyRuntimeStage.AwaitingGateArrival;
        otherVehicle.SetBlockReason("ONBOARD_SESSION_NOT_READY", Now.AddMinutes(-1));
        // 车挂着故障事实（例如这张单已被 RIoT 报 FAILED）。
        JourneyRuntimeRow faulted = Runtime("D-FAULTED", "AGV-06");
        faulted.Stage = JourneyRuntimeStage.AwaitingPickupArrival;
        faulted.SetBlockReason("ONBOARD_SESSION_NOT_READY", Now.AddMinutes(-1));
        // 在途单都在，但安全原因里有仓位侧的码。
        JourneyRuntimeRow slotSide = Runtime("D-SLOT-SIDE", "AGV-07");
        slotSide.Stage = JourneyRuntimeStage.AwaitingGateArrival;
        slotSide.SetBlockReason("ONBOARD_SESSION_NOT_READY", Now.AddMinutes(-1));
        database.Context.JourneyRuntimes.AddRange(toGate, toPickup, departing, unconfirmed, otherVehicle, faulted, slotSide);

        database.Context.OrderIntents.AddRange(
            ConfirmedIntent(toGate, "TO_GATE"),
            ConfirmedIntent(toPickup, "TO_PICKUP"),
            GateIntent(unconfirmed),
            ConfirmedIntent(otherVehicle, "TO_GATE", vehicleKey: "KEY-SOMEONE-ELSE"),
            ConfirmedIntent(faulted, "TO_PICKUP"),
            ConfirmedIntent(slotSide, "TO_GATE"));
        database.Context.VehicleFaultStates.Add(new VehicleFaultStateRow
        {
            AgvId = "AGV-06",
            Level = VehicleFaultLevel.SuspectedBlocked,
            FaultGeneration = 1,
            EvidenceCode = "ORDER_FAILED",
            EnteredAt = Now.AddMinutes(-1)
        });
        foreach (string agvId in new[] { "AGV-01", "AGV-02", "AGV-03", "AGV-04", "AGV-05", "AGV-06" })
        {
            database.Context.SessionRecoveries.Add(Session(agvId, "DEPARTURE_SAFETY_NOT_READY", VehicleOnly, safetyUnknownPresent: true));
        }
        database.Context.SessionRecoveries.Add(Session(
            "AGV-07", "DEPARTURE_SAFETY_NOT_READY", """["SLOT_STATE_UNKNOWN","VEHICLE_NOT_READY"]""", safetyUnknownPresent: true));
        await database.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        using JsonDocument fact = await ReadAsync(database);
        ZeroChangePin.AssertTextMatches(fact.RootElement.GetRawText(), "dashboard-blocked-journeys-own-move-order");
        Dictionary<string, JsonElement> byDemand = fact.RootElement.GetProperty("journeys").EnumerateArray()
            .ToDictionary(journey => journey.GetProperty("demandId").GetString()!, StringComparer.Ordinal);

        Assert.Equal("Operator", byDemand["D-TO-GATE"].GetProperty("escalationLevel").GetString());
        Assert.Equal("OWN_MOVEMENT_ORDER_IN_FLIGHT", byDemand["D-TO-GATE"].GetProperty("unknownExplainedBy").GetString());
        Assert.Equal("ShiftLeader", byDemand["D-TO-PICKUP"].GetProperty("escalationLevel").GetString());
        Assert.Equal("OWN_MOVEMENT_ORDER_IN_FLIGHT", byDemand["D-TO-PICKUP"].GetProperty("unknownExplainedBy").GetString());
        foreach (string demandId in new[] { "D-DEPARTING", "D-UNCONFIRMED", "D-OTHER-VEHICLE", "D-FAULTED", "D-SLOT-SIDE" })
        {
            Assert.Equal("MaintenanceAdministrator", byDemand[demandId].GetProperty("escalationLevel").GetString());
            Assert.Equal(JsonValueKind.Null, byDemand[demandId].GetProperty("unknownExplainedBy").ValueKind);
        }
    }

    [Fact]
    public async Task ABlockAlreadyHeldWhenTheColumnWasAddedSaysItsStartIsUnknown()
    {
        await using DashboardDatabase database = await DashboardDatabase.CreateAsync();
        database.Context.JourneyRuntimes.Add(Runtime("D-OLD", "AGV-01"));
        await database.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        await database.ExecuteAsync(
            "UPDATE JourneyRuntimes SET Stage = 'Blocked', BlockReasonCode = 'LOAD_RESULT_REQUIRES_RECOVERY' WHERE DemandId = 'D-OLD'");

        using JsonDocument fact = await ReadAsync(database);

        JsonElement old = Assert.Single(fact.RootElement.GetProperty("journeys").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, old.GetProperty("blockReasonSince").ValueKind);
        Assert.Equal(JsonValueKind.Null, old.GetProperty("blockedSeconds").ValueKind);
        Assert.Equal("MaintenanceAdministrator", old.GetProperty("escalationLevel").GetString());
        string html = new BlockedJourneyCard().RenderFact(fact.RootElement);
        Assert.Contains("开始时间没有记录", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadingTheEndpointChangesNothing()
    {
        await using DashboardDatabase database = await DashboardDatabase.CreateAsync();
        JourneyRuntimeRow runtime = Runtime("D-1", "AGV-01");
        runtime.SetBlockReason("ONBOARD_SESSION_NOT_READY", Now.AddMinutes(-40));
        database.Context.JourneyRuntimes.Add(runtime);
        database.Context.SessionRecoveries.Add(Session("AGV-01", "HANDSHAKE_INCOMPLETE", null, safetyUnknownPresent: null));
        await database.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        string before = await database.DumpAsync();

        await using ControlServerDbContext reading = database.NewContext();
        await new BlockedJourneysQueryEndpoint(BlockedJourneyEscalationOptions.Default, new FixedClock(Now))
            .ReadAsync(reading, TestContext.Current.CancellationToken);

        Assert.Empty(reading.ChangeTracker.Entries());
        Assert.Equal(before, await database.DumpAsync());
    }

    [Fact]
    public void TheEndpointIsAGetUnderTheDashboardPrefixOnTheLoopbackOnlyHealthBinding()
    {
        DashboardQueryEndpointCatalog endpoints =
            DashboardQueryEndpointCatalog.Discover(typeof(BlockedJourneysQueryEndpoint).Assembly);
        IDashboardQueryEndpoint endpoint =
            Assert.Single(endpoints.Endpoints, candidate => candidate is BlockedJourneysQueryEndpoint);
        Assert.Equal("/api/dashboard/blocked-journeys", endpoint.Path);

        // 看板端点只由 DashboardQueryModule 挂，那里只有 MapGet；它挂在 Health:url 那个绑定上。
        string root = RepositoryRoot();
        string module = File.ReadAllText(Path.Combine(root, "src", "ControlServer.Host", "Composition", "DashboardQueryModule.cs"));
        Assert.Contains("app.MapGet(", module, StringComparison.Ordinal);
        foreach (string write in new[] { "MapPost", "MapPut", "MapDelete", "MapPatch", "MapMethods" })
        {
            Assert.DoesNotContain(write, module, StringComparison.Ordinal);
        }
        string program = File.ReadAllText(Path.Combine(root, "src", "ControlServer.Host", "Program.cs"));
        Assert.Contains("builder.Configuration[\"Health:url\"] ?? \"http://127.0.0.1:58007\"", program, StringComparison.Ordinal);
        using JsonDocument appSettings = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(root, "src", "ControlServer.Host", "appsettings.json")));
        Uri health = new(appSettings.RootElement.GetProperty("Health").GetProperty("url").GetString()!);
        Assert.Equal("127.0.0.1", health.Host);
    }

    // --- 卡片 -------------------------------------------------------------------------------------------------

    [Fact]
    public void TheCardRegistersItselfOnTheFleetViewAgainstTheEndpoint()
    {
        IDashboardCard card = Assert.Single(DashboardCardCatalog.Discovered.Cards, candidate => candidate is BlockedJourneyCard);
        Assert.Equal(DashboardView.Fleet, card.View);
        Assert.Equal(new BlockedJourneysQueryEndpoint(BlockedJourneyEscalationOptions.Default, TimeProvider.System).Path, card.SourcePath);
    }

    [Fact]
    public async Task TheCardColoursEachRowByTheLevelTheServerDecidedAndShowsTheSessionDetails()
    {
        await using DashboardDatabase database = await DashboardDatabase.CreateAsync();
        JourneyRuntimeRow young = Runtime("D-YOUNG", "AGV-01");
        young.SetBlockReason("SUBLOT_SUBMISSION_MISMATCH", Now.AddMinutes(-9));
        JourneyRuntimeRow shift = Runtime("D-SHIFT", "AGV-02");
        shift.SetBlockReason("LOAD_CORRECTION_IN_PROGRESS", Now.AddMinutes(-10));
        JourneyRuntimeRow session = Runtime("D-SESSION", "AGV-03");
        session.SetBlockReason("ONBOARD_SESSION_NOT_READY", Now.AddSeconds(-5));
        database.Context.JourneyRuntimes.AddRange(young, shift, session);
        database.Context.SessionRecoveries.Add(
            Session("AGV-03", "DEPARTURE_SAFETY_NOT_READY", """["IO_FACT_UNKNOWN"]""", safetyUnknownPresent: true));
        await database.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        using JsonDocument fact = await ReadAsync(database);
        string html = new BlockedJourneyCard().RenderFact(fact.RootElement);

        Assert.Contains("满 10 分钟 转班组长，满 30 分钟 转维护管理员", html, StringComparison.Ordinal);
        AssertRowContains(html, "AGV-01", ["escalation-operator", "操作员", "9 分钟", "SUBLOT_SUBMISSION_MISMATCH", "PICKUP-1"]);
        AssertRowContains(html, "AGV-02", ["escalation-shift-leader", "班组长", "10 分钟"]);
        AssertRowContains(html, "AGV-03",
            ["escalation-maintenance-administrator", "维护管理员", "5 秒", "DEPARTURE_SAFETY_NOT_READY", "IO_FACT_UNKNOWN", "安全证据有未知项：是"]);
    }

    [Fact]
    public async Task TheCardSaysWhyAnUnknownOnTheVehiclesOwnOrderWasNotSentToMaintenance()
    {
        await using DashboardDatabase database = await DashboardDatabase.CreateAsync();
        JourneyRuntimeRow moving = Runtime("D-MOVING", "AGV-01");
        moving.Stage = JourneyRuntimeStage.AwaitingGateArrival;
        moving.SetBlockReason("ONBOARD_SESSION_NOT_READY", Now.AddMinutes(-3));
        JourneyRuntimeRow unexplained = Runtime("D-UNEXPLAINED", "AGV-02");
        unexplained.Stage = JourneyRuntimeStage.AwaitingDepartureSafety;
        unexplained.SetBlockReason("ONBOARD_SESSION_NOT_READY", Now.AddMinutes(-3));
        database.Context.JourneyRuntimes.AddRange(moving, unexplained);
        database.Context.OrderIntents.Add(ConfirmedIntent(moving, "TO_GATE"));
        database.Context.SessionRecoveries.AddRange(
            Session("AGV-01", "DEPARTURE_SAFETY_NOT_READY", """["VEHICLE_NOT_READY"]""", safetyUnknownPresent: true),
            Session("AGV-02", "DEPARTURE_SAFETY_NOT_READY", """["VEHICLE_NOT_READY"]""", safetyUnknownPresent: true));
        await database.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        using JsonDocument fact = await ReadAsync(database);
        string html = new BlockedJourneyCard().RenderFact(fact.RootElement);

        AssertRowContains(html, "AGV-01", ["escalation-operator", "操作员", "行驶中（由在途运单解释）", "安全证据有未知项：是"]);
        AssertRowContains(html, "AGV-02", ["escalation-maintenance-administrator", "维护管理员"]);
        int row = html.IndexOf("<td>AGV-02</td>", StringComparison.Ordinal);
        Assert.DoesNotContain("由在途运单解释", html[row..html.IndexOf("</tr>", row, StringComparison.Ordinal)], StringComparison.Ordinal);
    }

    [Fact]
    public void WithNothingBlockedTheCardSaysSo()
    {
        using JsonDocument fact = JsonDocument.Parse(
            """{"shiftLeaderAfterSeconds":600,"maintenanceAdministratorAfterSeconds":1800,"journeys":[]}""");

        string html = new BlockedJourneyCard().RenderFact(fact.RootElement);

        Assert.Contains("<p>无被阻断的旅程</p>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<table>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void ALevelTheCardDoesNotKnowIsShownAtTheTopRatherThanGuessedLower()
    {
        using JsonDocument fact = JsonDocument.Parse(
            """{"shiftLeaderAfterSeconds":600,"maintenanceAdministratorAfterSeconds":1800,"journeys":[{"agvId":"AGV-01","stationId":"S","blockReasonCode":"X","blockReasonSince":null,"blockedSeconds":null,"escalationLevel":"SomethingNew","session":null}]}""");

        string html = new BlockedJourneyCard().RenderFact(fact.RootElement);

        Assert.Contains("escalation-maintenance-administrator", html, StringComparison.Ordinal);
        Assert.Contains("SomethingNew", html, StringComparison.Ordinal);
    }

    // --- helpers ----------------------------------------------------------------------------------------------

    /// <summary>
    /// Asserts on one vehicle's table row only, so an assertion cannot pass by reading a neighbouring row's colour.
    /// </summary>
    private static void AssertRowContains(string html, string agvId, string[] expected)
    {
        int start = html.IndexOf($"<td>{agvId}</td>", StringComparison.Ordinal);
        Assert.True(start >= 0, html);
        int rowStart = html.LastIndexOf("<tr", start, StringComparison.Ordinal);
        int rowEnd = html.IndexOf("</tr>", start, StringComparison.Ordinal);
        string row = System.Net.WebUtility.HtmlDecode(html[rowStart..rowEnd]);
        foreach (string text in expected)
        {
            Assert.Contains(text, row, StringComparison.Ordinal);
        }
    }

    private static async Task<JsonDocument> ReadAsync(DashboardDatabase database)
    {
        await using ControlServerDbContext reading = database.NewContext();
        object result = await new BlockedJourneysQueryEndpoint(BlockedJourneyEscalationOptions.Default, new FixedClock(Now))
            .ReadAsync(reading, TestContext.Current.CancellationToken);
        return JsonDocument.Parse(JsonSerializer.Serialize(result));
    }

    private static IConfiguration Section(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(pair =>
                new KeyValuePair<string, string?>(pair.Key, pair.Value)))
            .Build();

    private static SessionRecoveryRow Session(
        string agvId, string reasonCode, string? safetyReasonCodesJson, bool? safetyUnknownPresent) => new()
        {
            AgvId = agvId,
            ProtocolCommit = "c",
            ManifestSha256 = "m",
            ProfileId = "p",
            ProtocolVersion = 2,
            Readiness = SessionReadiness.RecoveryRequired,
            ReasonCode = reasonCode,
            SafetyReasonCodesJson = safetyReasonCodesJson,
            SafetyUnknownPresent = safetyUnknownPresent,
            UpdatedAt = Now
        };

    private static OrderIntentRow GateIntent(JourneyRuntimeRow runtime) => new()
    {
        MovementLegId = runtime.GateMovementLegId,
        DemandId = runtime.DemandId,
        UpperId = runtime.GateUpperId,
        Purpose = "TO_GATE",
        TargetStationId = runtime.GateStationId,
        CreatedAt = Now
    };

    /// <summary>A move order this server created for the journey and RIoT accepted, bound to <paramref name="vehicleKey"/>.</summary>
    private static OrderIntentRow ConfirmedIntent(JourneyRuntimeRow runtime, string purpose, string? vehicleKey = null) => new()
    {
        MovementLegId = purpose == "TO_GATE" ? runtime.GateMovementLegId : runtime.PickupMovementLegId,
        DemandId = runtime.DemandId,
        UpperId = purpose == "TO_GATE" ? runtime.GateUpperId : runtime.PickupUpperId,
        Purpose = purpose,
        TargetStationId = purpose == "TO_GATE" ? runtime.GateStationId : runtime.PickupStationId,
        VehicleKey = vehicleKey ?? runtime.VehicleKey,
        MapId = runtime.MapId,
        CreatedAt = Now.AddMinutes(-20),
        Status = "CONFIRMED",
        OrderId = "ORDER-" + purpose + "-" + runtime.DemandId
    };

    private static JourneyRuntimeRow Runtime(string demandId, string agvId) => new()
    {
        JourneyId = JourneyIdentity.ForAnchorDemand(demandId),
        DemandId = demandId,
        Stage = JourneyRuntimeStage.AwaitingSublot,
        AgvId = agvId,
        VehicleKey = "KEY-" + agvId,
        AgvLifecycleGeneration = 1,
        MapId = 25,
        MapIdentity = "map-25",
        DispatchZone = "MAP-25-WIRE_TO_GATE",
        RouteEvidenceId = "route-" + demandId,
        PickupStationId = "PICKUP-1",
        PickupStationRiotId = 11,
        GateStationId = "GATE-1",
        GateStationRiotId = 22,
        ExpectedBasketCount = 2,
        TargetSlotsJson = "[1,2]",
        OperationSessionId = "session-" + demandId,
        PickupMovementLegId = "pickup-leg-" + demandId,
        PickupUpperId = "UPPER-PICKUP-" + demandId,
        GateMovementLegId = "gate-leg-" + demandId,
        GateUpperId = "UPPER-GATE-" + demandId,
        DispatchGeneration = 1,
        VehicleBusinessRevision = 1,
        WorklistRevision = 1,
        PlanRevision = 1,
        VehicleBusinessMessageId = "vb-" + demandId,
        WorklistMessageId = "wl-" + demandId,
        PlanMessageId = "plan-" + demandId,
        SublotRequestMessageId = "sublot-" + demandId,
        LoadCommandMessageId = "load-" + demandId,
        LoadSlotOperationAttemptId = "load-attempt-" + demandId,
        PreDepartureSafetyCheckMessageId = "check-message-" + demandId,
        PreDepartureSafetyCheckId = "check-" + demandId,
        GateVehicleBusinessMessageId = "gate-vb-" + demandId,
        GateWorklistMessageId = "gate-wl-" + demandId,
        GatePlanMessageId = "gate-plan-" + demandId,
        UnloadCommandMessageId = "unload-" + demandId,
        UnloadSlotOperationAttemptId = "unload-attempt-" + demandId,
        CreatedAt = Now.AddHours(-1),
        UpdatedAt = Now
    };

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ControlServer.sln")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class DashboardDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private DashboardDatabase(SqliteConnection connection, ControlServerDbContext context)
        {
            _connection = connection;
            Context = context;
        }

        public ControlServerDbContext Context { get; }

        public static async Task<DashboardDatabase> CreateAsync()
        {
            SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            DashboardDatabase database = new(connection, new ControlServerDbContext(Options(connection)));
            await database.Context.Database.MigrateAsync(TestContext.Current.CancellationToken);
            return database;
        }

        public ControlServerDbContext NewContext() => new(Options(_connection));

        public async Task ExecuteAsync(string sql)
        {
            await using SqliteCommand command = _connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        public async Task<string> DumpAsync()
        {
            List<string> rows = [];
            foreach (string table in new[] { "JourneyRuntimes", "SessionRecoveries", "OrderIntents" })
            {
                await using SqliteCommand command = _connection.CreateCommand();
                command.CommandText = $"SELECT * FROM \"{table}\"";
                await using SqliteDataReader reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
                while (await reader.ReadAsync(TestContext.Current.CancellationToken))
                {
                    object[] values = new object[reader.FieldCount];
                    reader.GetValues(values);
                    rows.Add(table + ":" + string.Join("|", values));
                }
            }
            return string.Join(Environment.NewLine, rows);
        }

        private static DbContextOptions<ControlServerDbContext> Options(SqliteConnection connection) =>
            new DbContextOptionsBuilder<ControlServerDbContext>().UseSqlite(connection).Options;

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
