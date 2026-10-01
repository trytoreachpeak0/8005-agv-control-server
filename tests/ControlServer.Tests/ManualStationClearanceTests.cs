using System.Net;
using System.Text;
using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Charging;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using static ControlServer.Tests.ChargingAllocationTests;
using static ControlServer.Tests.ChargingExecutionTests;
using static ControlServer.Tests.ChargingUnableToChargeTests;
using FleetFixture = ControlServer.Tests.MultiVehicleExecutionTests.FleetFixture;

namespace ControlServer.Tests;

/// <summary>
/// 人工清桩确认（批次9-08，control-server#406；<c>REQ-0179</c>、<c>REQ-0178</c>、<c>REQ-0180</c>）：R-11／R-13 名单、四种拒绝、确认的两种
/// （旧单已终结与未收敛）、释放之后桩仍暂停、重放与换号重提、同一次清桩的第二个确认号、两种并发交错、释放与收尾之间的崩溃点、清桩之后的车，
/// cs#404 留给本票的两个保持状态的出口，三个 Host 入口，以及 <c>CV-MANUAL-STATION-CLEARANCE</c> 的两条同名具名测试（经真实的消息处理与存储）。
/// </summary>
[Trait("IntegrationSlice", "FP-IS-13")]
public sealed class ManualStationClearanceTests : IDisposable
{
    private const string Vector = "CV-MANUAL-STATION-CLEARANCE";
    private const int Map = 25;

    private readonly string _roster = Path.Combine(Path.GetTempPath(), $"cs406-roster-{Guid.NewGuid():N}.json");

    public ManualStationClearanceTests()
    {
        File.WriteAllText(_roster, """
            {"operators":[
              {"operatorId":"op-r11","roles":["R-11"]},
              {"operatorId":"op-r13","roles":["R-13"]},
              {"operatorId":"op-plain","roles":["R-02"]}
            ]}
            """);
    }

    public void Dispose() => File.Delete(_roster);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string AgvA => FleetFixture.AgvIds[0];
    private static string KeyA => FleetFixture.VehicleKeys[0];
    private static string AgvB => FleetFixture.AgvIds[1];
    private static string KeyB => FleetFixture.VehicleKeys[1];

    // ---- 名单 -------------------------------------------------------------------------------------------------------------

    public static TheoryData<string, string?> RosterCases => new()
    {
        { "op-plain", null },
        { "op-unknown", null },
        { "op-r11", "R-11" },
        { "op-r13", "R-13" },
    };

    /// <summary>名单按 <c>operatorId</c> 查：R-11 与 R-13 有清桩权限，普通操作员与名单外的人没有。</summary>
    [Theory]
    [MemberData(nameof(RosterCases))]
    public void TheRosterGrantsTheClearanceOnlyToR11AndR13(string operatorId, string? granted)
    {
        FieldOperatorRoleRoster roster = new(Options.Create(new FieldOperatorRoleOptions { Path = _roster }));
        Assert.Equal(granted, roster.GrantedRole(operatorId, FieldOperatorRoleRoster.StationClearanceRoles));
    }

    public static TheoryData<string> EmptyRosters => ["no-path", "missing-file", "unreadable", "no-operators"];

    /// <summary>没配名单、文件不在、读不出、名单为空：谁都没有权限，不是谁都有。</summary>
    [Theory]
    [MemberData(nameof(EmptyRosters))]
    public void AnEmptyOrUnreadableRosterGrantsNobody(string shape)
    {
        string path = Path.Combine(Path.GetTempPath(), $"cs406-empty-{Guid.NewGuid():N}.json");
        if (shape == "unreadable") File.WriteAllText(path, "{not json");
        if (shape == "no-operators") File.WriteAllText(path, """{"operators":[]}""");
        try
        {
            FieldOperatorRoleRoster roster = new(Options.Create(new FieldOperatorRoleOptions { Path = shape == "no-path" ? null : path }));
            Assert.Null(roster.GrantedRole("op-r11", FieldOperatorRoleRoster.StationClearanceRoles));
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---- 拒绝 -------------------------------------------------------------------------------------------------------------

    public static TheoryData<string, string> Refusals => new()
    {
        { "plain-operator", ManualStationClearance.NotAuthorized },
        { "empty-roster", ManualStationClearance.NotAuthorized },
        { "another-station", ManualStationClearance.StationMismatch },
        { "public-station-function", ManualStationClearance.StationMismatch },
        { "riot-reads-it-still-on-the-charger", ManualStationClearance.NotAllowedInState },
        { "riot-reads-it-charging", ManualStationClearance.NotAllowedInState },
    };

    /// <summary>
    /// 权限不足（普通操作员、名单为空）、站点不符、<c>publicStationFunction</c> 非空、与 RIoT 新鲜事实冲突（车仍读在桩上、或在充电）：一律
    /// <c>REJECTED</c>、<c>stationReleased=false</c>，清桩记录不完成，桩仍是这一趟的，用途仍是 <c>CLEARING_MAINTENANCE</c>。
    /// </summary>
    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task EachRefusalReleasesNothing(string refusal, string reasonCode)
    {
        await using FleetFixture fleet = await FleetAsync();
        JourneyRuntimeRow journey = await ConfirmedAsync(fleet);
        fleet.Riot.CancelOrder(journey.PickupUpperId);
        MovedOff(fleet);
        if (refusal == "riot-reads-it-still-on-the-charger") AtTheCharger(fleet);
        if (refusal == "riot-reads-it-charging")
        {
            fleet.Riot.VehicleOverrides[KeyA] = seen => seen with { CurrentStationId = 300, BatteryState = "CHARGING" };
        }

        ManualStationClearanceConfirmation decision = await Clearance(fleet, rosterPath: refusal == "empty-roster" ? null : _roster)
            .DecideAsync(
                Request(
                    "00000000-0000-4000-8000-000000000a01",
                    operatorId: refusal == "plain-operator" ? "op-plain" : "op-r11",
                    station: refusal == "another-station" ? Far.StationName : Near.StationName,
                    publicFunction: refusal == "public-station-function" ? "ELEVATOR" : null),
                Token);

        Assert.Equal((FieldConfirmationDecision.Rejected, reasonCode, false),
            (decision.Decision.Outcome, decision.Decision.ProblemReasonCode, decision.StationReleased));
        await AssertNothingReleasedAsync(fleet);
    }

    // ---- 确认 -------------------------------------------------------------------------------------------------------------

    public static TheoryData<string> SettledOldOrders => ["cancelled", "deleted", "real-absent", "not-found-404"];

    /// <summary>
    /// 旧单已终结（取消、删除、真实 RIoT 的查无此单——HTTP 200 不带 result、替身的 404）时确认：同一个事务里清桩记录写全（确认人、角色、时刻、
    /// 车辆最终位置、旧单处置、<c>clearedCondition</c>、确认号），桩的独占释放（原因写进经过）、周期以 <c>CHARGING_UNABLE_TO_CHARGE_CLEARED</c> 结束、
    /// 用途放开，<c>stationReleased=true</c>。<b>暂停仍在</b>：另一辆低电的车分不到这个桩（<c>CHARGER_ALLOCATION_HELD</c>）。下一轮旅程收尾。
    /// </summary>
    [Theory]
    [MemberData(nameof(SettledOldOrders))]
    public async Task ConfirmedWithTheOldOrderEndedReleasesTheChargerButNotItsHold(string shape)
    {
        await using FleetFixture fleet = await FleetAsync(vehicles: 2);
        JourneyRuntimeRow journey = await ConfirmedAsync(fleet);
        EndOldOrder(fleet, journey.PickupUpperId, shape);
        MovedOff(fleet);

        ManualStationClearanceConfirmation decision = await Clearance(fleet)
            .DecideAsync(Request("00000000-0000-4000-8000-000000000a02", operatorId: "op-r13"), Token);

        Assert.Equal((FieldConfirmationDecision.Confirmed, (string?)null, true),
            (decision.Decision.Outcome, decision.Decision.ProblemReasonCode, decision.StationReleased));
        StationClearanceRow clearance = await ClearanceRowAsync(fleet);
        Assert.Equal(
            (StationClearanceProofs.ManualConfirmation, "op-r13", "R-13", $"{fleet.Options.MapIdentity}/300", "STATION_EMPTY",
                "00000000-0000-4000-8000-000000000a02"),
            (clearance.Proof, clearance.ConfirmedBy, clearance.ConfirmedByRole, clearance.VehicleFinalPosition, clearance.ClearedCondition,
                clearance.ConfirmationRequestId));
        Assert.Equal(shape is "real-absent" or "not-found-404" ? "ABSENT" : shape.ToUpperInvariant(), clearance.OldOrderDisposition);
        Assert.NotNull(clearance.CompletedAt);
        Assert.Null(await HolderAsync(fleet, Near.StationId));
        Assert.Equal(
            ChargingExecutionReasons.ChargerReleasedOnManualClearance,
            (await fleet.Context.Set<StationExclusivityRecordRow>().AsNoTracking()
                .SingleAsync(row => row.StationId == Near.StationId && row.JourneyId == journey.JourneyId, Token)).ReleaseReason);
        ChargingCycleRow cycle = await fleet.Context.Set<ChargingCycleRow>().AsNoTracking()
            .SingleAsync(row => row.JourneyId == journey.JourneyId, Token);
        Assert.Equal((ChargingCyclePhases.Ended, ChargingExecutionReasons.UnableToChargeCleared), (cycle.Phase, cycle.EndReason));
        Assert.Null(await ClaimOfAsync(fleet, KeyA));
        Assert.Single(await HoldsAsync(fleet));
        Assert.Empty(await fleet.Context.Set<ChargingStationRecoveryRow>().AsNoTracking().ToArrayAsync(Token));

        fleet.Riot.BatteryByVehicle[KeyB] = 15;
        await RoundAsync(fleet);
        JourneyRuntimeRow closed = await fleet.Context.JourneyRuntimes.AsNoTracking()
            .SingleAsync(row => row.JourneyId == journey.JourneyId, Token);
        Assert.Equal((JourneyRuntimeStage.Completed, ChargingExecutionReasons.UnableToChargeCleared), (closed.Stage, closed.BlockReasonCode));
        JsonElement state = (await PayloadsAsync(fleet, AgvA, "VehicleBusinessStateSnapshot"))[^1];
        Assert.Equal(JsonValueKind.Null, state.GetProperty("activePurpose").ValueKind);
        Assert.Null(await HolderAsync(fleet, Near.StationId));
        Assert.Equal(ChargingAllocationReasons.NoChargerAvailable, fleet.ChargingBoard.Verdicts[AgvB].Reason);
        Assert.Contains(ChargingAllocationReasons.ChargerAllocationHeld, fleet.ChargingBoard.Verdicts[AgvB].Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// 取消开关关着（收紧之后的样子）、旧单仍 <c>HANG</c> 时确认：记下确认、<c>stationReleased=false</c>，桩与用途都不放，一条订单命令都不发；
    /// 之后每一轮写「旧单未收敛」的码、超过十分钟告警恰好一次；有人在 RIoT 里把旧单取消之后的那一轮才释放并收尾——释放只有一次，之后桩仍暂停。
    /// </summary>
    [Fact]
    public async Task ConfirmedWhileTheOldOrderHangsReleasesOnlyInTheRoundItEnds()
    {
        await using FleetFixture fleet = await FleetAsync();
        fleet.Options.UnableToChargeOldOrderCancelEnabled = false;
        JourneyRuntimeRow journey = await ConfirmedAsync(fleet);
        MovedOff(fleet);

        ManualStationClearanceConfirmation decision = await Clearance(fleet)
            .DecideAsync(Request("00000000-0000-4000-8000-000000000a03"), Token);

        Assert.Equal((FieldConfirmationDecision.Confirmed, false), (decision.Decision.Outcome, decision.StationReleased));
        Assert.Equal("HANG", (await ClearanceRowAsync(fleet)).OldOrderDisposition);
        Assert.Equal((KeyA, StationExclusivityStates.Reserved), await HolderAsync(fleet, Near.StationId));
        Assert.Equal(VehiclePurposes.ClearingMaintenance, (await ClaimOfAsync(fleet, KeyA))!.Value.Purpose);

        for (int minute = 0; minute < 15; minute++)
        {
            await fleet.HearFromEveryVehicleAsync();
            await fleet.RunRoundAsync(TimeSpan.FromMinutes(1));
            Assert.Equal((KeyA, StationExclusivityStates.Reserved), await HolderAsync(fleet, Near.StationId));
        }
        Assert.Equal(ChargingExecutionReasons.ClearedOldOrderUnsettled, (await ChargingJourneyAsync(fleet, AgvA))!.BlockReasonCode);
        Assert.Single(fleet.EngineLog.Entries, entry => entry.EventId.Id == 2265);
        Assert.Empty(fleet.Riot.OrderCommands);

        fleet.Riot.CancelOrder(journey.PickupUpperId);
        await RoundAsync(fleet);

        Assert.Null(await HolderAsync(fleet, Near.StationId));
        Assert.Null(await ClaimOfAsync(fleet, KeyA));
        JourneyRuntimeRow closed = await fleet.Context.JourneyRuntimes.AsNoTracking()
            .SingleAsync(row => row.JourneyId == journey.JourneyId, Token);
        Assert.Equal((JourneyRuntimeStage.Completed, ChargingExecutionReasons.UnableToChargeCleared), (closed.Stage, closed.BlockReasonCode));
        Assert.Single(fleet.EngineLog.Entries, entry => entry.EventId.Id == 2267);
        Assert.Single(await fleet.Context.Set<StationExclusivityRecordRow>().AsNoTracking()
            .Where(row => row.ReleaseReason == ChargingExecutionReasons.ChargerReleasedOnManualClearance).ToArrayAsync(Token));
        Assert.Single(await HoldsAsync(fleet));
    }

    /// <summary>
    /// 取消开关（<c>UnableToChargeOldOrderCancelEnabled</c>）按默认值、即打开（<c>REQ-0148</c> 情形一，基线 <c>v1.9.0</c>；上一条用例钉住关着时一条订单命令都没有）：
    /// 清桩确认之前什么也不发；确认之后、旧单是 <c>SUSPENDED</c> 时不发；旧单 <c>HANG</c> 但 RIoT 读到执行车是别的车、或读不到执行车时不发；
    /// 旧单 <c>HANG</c> 且由本车执行时，恰好对这个周期自己的那张单发一次 <c>CMD_ORDER_CANCEL</c>。RIoT 没把它变成取消（替身照旧答 HANG）：告警一次（事件 2270）、
    /// 之后十五轮不重发、桩一直留着；取消不替代对账，读到旧单终结的那一轮才放桩。
    /// </summary>
    [Fact]
    public async Task WithTheCancelSwitchOnTheOldOrderIsCancelledOnceAndTheChargerStillWaitsForItToEnd()
    {
        await using FleetFixture fleet = await FleetAsync();
        JourneyRuntimeRow journey = await ConfirmedAsync(fleet);
        MovedOff(fleet);
        for (int minute = 0; minute < 3; minute++)
        {
            await fleet.HearFromEveryVehicleAsync();
            await fleet.RunRoundAsync(TimeSpan.FromMinutes(1));
        }
        Assert.Empty(fleet.Riot.OrderCommands);

        ManualStationClearanceConfirmation decision = await Clearance(fleet)
            .DecideAsync(Request("00000000-0000-4000-8000-000000000a0c"), Token);
        Assert.False(decision.StationReleased);

        // Unsettled but not HANG: the switch cancels nothing.
        fleet.Riot.SuspendOrder(journey.PickupUpperId);
        for (int minute = 0; minute < 3; minute++)
        {
            await fleet.HearFromEveryVehicleAsync();
            await fleet.RunRoundAsync(TimeSpan.FromMinutes(1));
        }
        Assert.Empty(fleet.Riot.OrderCommands);

        // HANG, but RIoT reads it executed by another vehicle, or by none: not proven ours to cancel (REQ-0148 case 1).
        fleet.Riot.HangOrder(journey.PickupUpperId);
        foreach (string? executor in new[] { "AGV-NOT-OURS", null })
        {
            fleet.Riot.ExecuteOrderOn(journey.PickupUpperId, executor);
            for (int minute = 0; minute < 2; minute++)
            {
                await fleet.HearFromEveryVehicleAsync();
                await fleet.RunRoundAsync(TimeSpan.FromMinutes(1));
            }
            Assert.Empty(fleet.Riot.OrderCommands);
        }

        fleet.Riot.ExecuteOrderOn(journey.PickupUpperId, KeyA);
        for (int minute = 0; minute < 15; minute++)
        {
            await fleet.HearFromEveryVehicleAsync();
            await fleet.RunRoundAsync(TimeSpan.FromMinutes(1));
            Assert.Equal((KeyA, StationExclusivityStates.Reserved), await HolderAsync(fleet, Near.StationId));
        }
        (string Command, string OrderId) cancel = Assert.Single(fleet.Riot.OrderCommands);
        RiotOrderObservation hanging = await fleet.Riot.ReconcileByUpperIdAsync(journey.PickupUpperId, Token);
        Assert.Equal((RiotCommandTypeNames.For(RiotOrderCommandKind.Cancel), hanging.OrderId), (cancel.Command, cancel.OrderId));
        Assert.Single(fleet.EngineLog.Entries, entry => entry.EventId.Id == 2270);

        fleet.Riot.CancelOrder(journey.PickupUpperId);
        await RoundAsync(fleet);

        Assert.Null(await HolderAsync(fleet, Near.StationId));
        Assert.Null(await ClaimOfAsync(fleet, KeyA));
        Assert.Single(fleet.Riot.OrderCommands);
    }

    // ---- 重放、换号重提、第二个确认号 ------------------------------------------------------------------------------------

    /// <summary>
    /// 同一个确认号再来（同一份载荷）：拿回存下的那一份判定，<c>stationReleased</c> 不重算——即使之后旧单已经终结、桩已经放了（车载端按确认号记载荷哈希，
    /// 两份不同就断会话）；不产生第二条清桩记录。同一个确认号、不同载荷：按内容冲突拒收。
    /// </summary>
    [Fact]
    public async Task TheSameConfirmationNumberAgainGetsTheStoredDecisionAndOtherContentIsAConflict()
    {
        await using FleetFixture fleet = await FleetAsync();
        JourneyRuntimeRow journey = await ConfirmedAsync(fleet);
        MovedOff(fleet);
        const string id = "00000000-0000-4000-8000-000000000a04";
        ManualStationClearanceConfirmation first = await Clearance(fleet).DecideAsync(Request(id), Token);
        Assert.False(first.StationReleased);

        fleet.Riot.CancelOrder(journey.PickupUpperId);
        await RoundAsync(fleet);
        Assert.Null(await HolderAsync(fleet, Near.StationId));

        ManualStationClearanceConfirmation again = await Clearance(fleet).DecideAsync(Request(id), Token);
        Assert.Equal(
            (first.Decision.Outcome, first.Decision.DecidedAt, false),
            (again.Decision.Outcome, again.Decision.DecidedAt, again.StationReleased));
        Assert.Single(await fleet.Context.Set<StationClearanceRow>().AsNoTracking().ToArrayAsync(Token));

        await Assert.ThrowsAsync<FieldConfirmationContentConflictException>(
            () => Clearance(fleet).DecideAsync(Request(id, operatorId: "op-r13"), Token));
    }

    /// <summary>
    /// 同一次清桩来了第二个确认号（车载端重启后再按）：答 <c>CONFIRMED</c>，不写第二次清桩记录、不放第二次；旧单此时已终结而桩还没放的，
    /// 由这一次放（与引擎同一个释放，只有一方生效）。
    /// </summary>
    [Fact]
    public async Task ASecondConfirmationNumberForTheSameClearanceRecordsAndReleasesNothingTwice()
    {
        await using FleetFixture fleet = await FleetAsync();
        JourneyRuntimeRow journey = await ConfirmedAsync(fleet);
        MovedOff(fleet);
        ManualStationClearanceConfirmation first = await Clearance(fleet)
            .DecideAsync(Request("00000000-0000-4000-8000-000000000a05"), Token);
        Assert.False(first.StationReleased);
        DateTimeOffset completedAt = (await ClearanceRowAsync(fleet)).CompletedAt!.Value;

        ManualStationClearanceConfirmation second = await Clearance(fleet)
            .DecideAsync(Request("00000000-0000-4000-8000-000000000a06", operatorId: "op-r13"), Token);
        Assert.Equal((FieldConfirmationDecision.Confirmed, false), (second.Decision.Outcome, second.StationReleased));

        fleet.Riot.CancelOrder(journey.PickupUpperId);
        ManualStationClearanceConfirmation third = await Clearance(fleet)
            .DecideAsync(Request("00000000-0000-4000-8000-000000000a07"), Token);
        Assert.Equal((FieldConfirmationDecision.Confirmed, true), (third.Decision.Outcome, third.StationReleased));

        StationClearanceRow clearance = await ClearanceRowAsync(fleet);
        Assert.Equal(("op-r11", completedAt), (clearance.ConfirmedBy, clearance.CompletedAt!.Value));
        Assert.Null(await HolderAsync(fleet, Near.StationId));
        Assert.Single(await fleet.Context.Set<StationExclusivityRecordRow>().AsNoTracking()
            .Where(row => row.ReleaseReason == ChargingExecutionReasons.ChargerReleasedOnManualClearance).ToArrayAsync(Token));

        await RoundAsync(fleet);
        Assert.Equal(JourneyRuntimeStage.Completed, (await fleet.Context.JourneyRuntimes.AsNoTracking()
            .SingleAsync(row => row.JourneyId == journey.JourneyId, Token)).Stage);
    }

    // ---- 并发交错 ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// 车载端与 Host 几乎同时确认同一次清桩：第一个在读完库、读 RIoT 的那一刻，第二个（另一个上下文）整笔做完。只有一条清桩记录完成、只释放一次；
    /// 第一个整个事务回滚、重判，答 <c>CONFIRMED</c> 与「已放」。
    /// </summary>
    [Fact]
    public async Task TwoConfirmationsAtOnceMakeOneRecordAndOneRelease()
    {
        await using FleetFixture fleet = await FleetAsync();
        JourneyRuntimeRow journey = await ConfirmedAsync(fleet);
        fleet.Riot.CancelOrder(journey.PickupUpperId);
        MovedOff(fleet);

        await using ControlServerDbContext other = fleet.NewContext();
        bool raced = false;
        InterleavingRiot riot = new(fleet.Riot, async () =>
        {
            if (raced) return;
            raced = true;
            ManualStationClearanceConfirmation host = await Clearance(fleet, other).DecideAsync(
                Request("00000000-0000-4000-8000-000000000a08", source: ManualStationClearanceSources.Host, operatorId: "op-r13"),
                Token);
            Assert.True(host.StationReleased);
        });

        ManualStationClearanceConfirmation onboard = await Clearance(fleet, riot: riot)
            .DecideAsync(Request("00000000-0000-4000-8000-000000000a09"), Token);

        Assert.True(raced);
        Assert.Equal((FieldConfirmationDecision.Confirmed, true), (onboard.Decision.Outcome, onboard.StationReleased));
        StationClearanceRow clearance = await ClearanceRowAsync(fleet);
        Assert.Equal("op-r13", clearance.ConfirmedBy);
        Assert.Single(await fleet.Context.Set<StationExclusivityRecordRow>().AsNoTracking()
            .Where(row => row.ReleaseReason == ChargingExecutionReasons.ChargerReleasedOnManualClearance).ToArrayAsync(Token));
        Assert.Equal(2, await fleet.Context.Set<ManualStationClearanceConfirmationRow>().AsNoTracking().CountAsync(Token));
    }

    /// <summary>
    /// 清桩确认与「旧单对账到终态」在同一轮：引擎读到清桩已完成、正去读旧单的那一刻，第二个确认号（另一个上下文）到来并放了桩；引擎随后那一次
    /// 释放写到 0 行、什么也不写，照常收尾。只释放一次、不漏放。
    /// </summary>
    [Fact]
    public async Task AConfirmationAndTheEnginesReleaseInTheSameRoundReleaseOnce()
    {
        await using FleetFixture fleet = await FleetAsync();
        JourneyRuntimeRow journey = await ConfirmedAsync(fleet);
        MovedOff(fleet);
        Assert.False((await Clearance(fleet).DecideAsync(Request("00000000-0000-4000-8000-000000000a10"), Token)).StationReleased);
        fleet.Riot.CancelOrder(journey.PickupUpperId);

        await using ControlServerDbContext other = fleet.NewContext();
        bool raced = false;
        fleet.Riot.BeforeReconcile = async upperId =>
        {
            if (raced || upperId != journey.PickupUpperId) return;
            raced = true;
            ManualStationClearanceConfirmation second = await Clearance(fleet, other)
                .DecideAsync(Request("00000000-0000-4000-8000-000000000a11"), Token);
            Assert.True(second.StationReleased);
        };
        await RoundAsync(fleet);
        fleet.Riot.BeforeReconcile = null;

        Assert.True(raced);
        Assert.Null(await HolderAsync(fleet, Near.StationId));
        Assert.Single(await fleet.Context.Set<StationExclusivityRecordRow>().AsNoTracking()
            .Where(row => row.ReleaseReason == ChargingExecutionReasons.ChargerReleasedOnManualClearance).ToArrayAsync(Token));
        Assert.DoesNotContain(fleet.EngineLog.Entries, entry => entry.EventId.Id == 2267);
        Assert.Equal(JourneyRuntimeStage.Completed, (await fleet.Context.JourneyRuntimes.AsNoTracking()
            .SingleAsync(row => row.JourneyId == journey.JourneyId, Token)).Stage);
    }

    // ---- 崩溃点 -----------------------------------------------------------------------------------------------------------

    /// <summary>
    /// 「写清桩记录」与「释放独占」之间崩掉（删独占行那一刻）：整个事务回滚，没有「记录说已清桩、桩仍被占」——清桩记录没完成、判定也没记下；
    /// 同一个确认号再来照常判成、放成。
    /// </summary>
    [Fact]
    public async Task ACrashBetweenTheRecordAndTheReleaseLeavesNeitherAndTheSameNumberSucceedsAfter()
    {
        FailOnce crash = new("DELETE FROM \"StationExclusivities\"");
        await using FleetFixture fleet = await FleetAsync(commands: crash);
        JourneyRuntimeRow journey = await ConfirmedAsync(fleet);
        fleet.Riot.CancelOrder(journey.PickupUpperId);
        MovedOff(fleet);
        const string id = "00000000-0000-4000-8000-000000000a12";

        crash.Armed = true;
        await Assert.ThrowsAnyAsync<Exception>(() => Clearance(fleet).DecideAsync(Request(id), Token));
        Assert.Equal(1, crash.Fired);
        fleet.Context.ChangeTracker.Clear();
        Assert.Null((await ClearanceRowAsync(fleet)).CompletedAt);
        Assert.Empty(await fleet.Context.Set<ManualStationClearanceConfirmationRow>().AsNoTracking().ToArrayAsync(Token));
        Assert.Equal((KeyA, StationExclusivityStates.Reserved), await HolderAsync(fleet, Near.StationId));

        ManualStationClearanceConfirmation decision = await Clearance(fleet).DecideAsync(Request(id), Token);
        Assert.True(decision.StationReleased);
        Assert.NotNull((await ClearanceRowAsync(fleet)).CompletedAt);
        Assert.Null(await HolderAsync(fleet, Near.StationId));
    }

    /// <summary>
    /// 引擎那一轮释放之后、旅程收尾之前崩掉（写旅程行那一刻）：释放已经提交，旅程还开着；下一轮读到由清桩结束的周期，照样收尾、发收尾快照。
    /// </summary>
    [Fact]
    public async Task ACrashBetweenTheEnginesReleaseAndTheClosureIsClosedNextRound()
    {
        // The closure's save, the one that writes the journey's stage (the guard's version bumps write no stage).
        FailOnce crash = new("UPDATE \"JourneyRuntimes\"", "\"Stage\" = ");
        await using FleetFixture fleet = await FleetAsync(commands: crash);
        JourneyRuntimeRow journey = await ConfirmedAsync(fleet);
        MovedOff(fleet);
        Assert.False((await Clearance(fleet).DecideAsync(Request("00000000-0000-4000-8000-000000000a13"), Token)).StationReleased);
        await RoundAsync(fleet);
        fleet.Riot.CancelOrder(journey.PickupUpperId);

        crash.Armed = true;
        await Assert.ThrowsAnyAsync<Exception>(() => RoundAsync(fleet));
        Assert.Equal(1, crash.Fired);
        Assert.Null(await HolderAsync(fleet, Near.StationId));
        Assert.NotEqual(JourneyRuntimeStage.Completed, (await fleet.Context.JourneyRuntimes.AsNoTracking()
            .SingleAsync(row => row.JourneyId == journey.JourneyId, Token)).Stage);

        await RoundAsync(fleet);
        JourneyRuntimeRow closed = await fleet.Context.JourneyRuntimes.AsNoTracking()
            .SingleAsync(row => row.JourneyId == journey.JourneyId, Token);
        Assert.Equal((JourneyRuntimeStage.Completed, ChargingExecutionReasons.UnableToChargeCleared), (closed.Stage, closed.BlockReasonCode));
        Assert.Null(await ClaimOfAsync(fleet, KeyA));
    }

    // ---- 清桩之后的车（REQ-0180）----------------------------------------------------------------------------------------

    /// <summary>
    /// 清桩收尾之后车仍需充电、唯一的桩暂停着：留在统一电量队列里，以 <c>CHARGER_ALLOCATION_HELD</c> 排队并告警「无合格桩」恰好一次（事件 2246），
    /// 名册非空、不进人工充电等待；不被派回原桩。
    /// </summary>
    [Fact]
    public async Task AfterTheClearanceTheOnlyChargerPausedQueuesTheVehicleWithAWarningAndNoManualHold()
    {
        await using FleetFixture fleet = await FleetAsync();
        await ClearedAndClosedAsync(fleet);

        for (int round = 0; round < 5; round++)
        {
            await RoundAsync(fleet);
        }

        Assert.Equal(ChargingAllocationReasons.NoChargerAvailable, fleet.ChargingBoard.Verdicts[AgvA].Reason);
        Assert.Contains(ChargingAllocationReasons.ChargerAllocationHeld, fleet.ChargingBoard.Verdicts[AgvA].Detail, StringComparison.Ordinal);
        Assert.Single(fleet.ChargingLog.Entries, entry => entry.EventId.Id == 2246);
        Assert.Empty(await fleet.Context.Set<ManualChargingHoldRow>().AsNoTracking().ToArrayAsync(Token));
        Assert.Single(fleet.Riot.Creates);
    }

    /// <summary>
    /// 清桩收尾之后还有别的合格桩：车回到统一电量队列、按正常链分到另一个桩（不是原桩，原桩暂停着）；清桩不是「已确认失败」，不进冷却。
    /// </summary>
    [Fact]
    public async Task AfterTheClearanceAnotherChargerTakesTheVehicleWithoutACooldown()
    {
        await using FleetFixture fleet = await FleetAsync(chargers: [Near, Far]);
        await ClearedAndClosedAsync(fleet);

        await RoundAsync(fleet);

        Assert.Equal(ChargingAllocationReasons.Committed, fleet.ChargingBoard.Verdicts[AgvA].Reason);
        Assert.Equal((KeyA, StationExclusivityStates.Reserved), await HolderAsync(fleet, Far.StationId));
        Assert.Null(await HolderAsync(fleet, Near.StationId));
    }

    /// <summary>
    /// 清桩收尾之后车的位置读不到（断电移车、拖走）：常规派车检查把它挡在门外，不分配任何东西，直到读到为止（<c>REQ-0180</c> 的第二种处置）。
    /// </summary>
    [Fact]
    public async Task AfterTheClearanceAVehicleWhosePositionIsUnknownIsNotDispatched()
    {
        await using FleetFixture fleet = await FleetAsync(chargers: [Near, Far]);
        await ClearedAndClosedAsync(fleet);
        fleet.Riot.VehicleOverrides[KeyA] = seen => seen with { CurrentStationId = null, BatteryState = "NO_CHARGE" };

        for (int round = 0; round < 5; round++)
        {
            await RoundAsync(fleet);
        }

        Assert.NotEqual(ChargingAllocationReasons.Committed, fleet.ChargingBoard.Verdicts[AgvA].Reason);
        Assert.Null(await HolderAsync(fleet, Far.StationId));
        Assert.Single(fleet.Riot.Creates);
    }

    // ---- cs#404 留下的两个保持状态的出口 ---------------------------------------------------------------------------------

    /// <summary>
    /// 充电单被取消之后车离线、一直证明不了停稳（cs#404 审查留给本票的第二格）：用途与桩一直被占着。Host 人工清桩是它的出口——确认之后周期以
    /// <c>CHARGING_CLEARED_BY_OPERATOR</c> 结束、桩与用途放开，下一轮旅程收尾；不暂停桩。
    /// </summary>
    [Fact]
    public async Task AnOfflineVehicleWhoseChargeOrderWasCancelledIsReleasedByAManualClearance()
    {
        await using FleetFixture fleet = await FleetAsync();
        fleet.Riot.BatteryByVehicle[KeyA] = 20;
        JourneyRuntimeRow journey = await CommittedAndSentAsync(fleet);
        fleet.Riot.CancelOrder(journey.PickupUpperId);
        fleet.Riot.VehicleOverrides[KeyA] = seen => seen with { Connected = false, CurrentStationId = null };
        for (int round = 0; round < 3; round++)
        {
            await RoundAsync(fleet);
        }
        Assert.Equal(JourneyRuntimeEngine.OrderEndedWithoutArrivalReason, (await ChargingJourneyAsync(fleet, AgvA))!.BlockReasonCode);
        Assert.Equal((KeyA, StationExclusivityStates.Reserved), await HolderAsync(fleet, Near.StationId));

        ManualStationClearanceConfirmation decision = await Clearance(fleet).DecideAsync(
            Request("00000000-0000-4000-8000-000000000a14", source: ManualStationClearanceSources.Host), Token);

        Assert.Equal((FieldConfirmationDecision.Confirmed, true), (decision.Decision.Outcome, decision.StationReleased));
        Assert.Equal(ManualStationClearance.PositionUnknown, (await ClearanceRowAsync(fleet)).VehicleFinalPosition);
        Assert.Null(await HolderAsync(fleet, Near.StationId));
        Assert.Null(await ClaimOfAsync(fleet, KeyA));
        Assert.Empty(await HoldsAsync(fleet));
        await RoundAsync(fleet);
        JourneyRuntimeRow closed = await fleet.Context.JourneyRuntimes.AsNoTracking()
            .SingleAsync(row => row.JourneyId == journey.JourneyId, Token);
        Assert.Equal((JourneyRuntimeStage.Completed, ChargingExecutionReasons.ClearedByOperator), (closed.Stage, closed.BlockReasonCode));
    }

    /// <summary>
    /// 桩被人放掉、而那一趟充电还在（cs#404 审查留给本票的第一格，<c>CHARGING_RESERVATION_LOST_AT_ARRIVAL</c>）：人工清桩之后周期结束、用途放开、
    /// 旅程收尾，不会变成新的卡死点；别人的桩不碰。
    /// </summary>
    [Fact]
    public async Task AJourneyWhoseChargerWasReleasedByHandIsClosedByAManualClearance()
    {
        await using FleetFixture fleet = await FleetAsync();
        fleet.Riot.BatteryByVehicle[KeyA] = 20;
        JourneyRuntimeRow journey = await CommittedAndSentAsync(fleet);
        await fleet.Context.Set<StationExclusivityRow>().Where(row => row.StationId == Near.StationId).ExecuteDeleteAsync(Token);
        fleet.Riot.CompleteOrder(journey.PickupUpperId);
        AtTheCharger(fleet);
        await RoundAsync(fleet);
        Assert.Equal(ChargingExecutionReasons.ReservationLostAtArrival, (await ChargingJourneyAsync(fleet, AgvA))!.BlockReasonCode);
        MovedOff(fleet);

        ManualStationClearanceConfirmation decision = await Clearance(fleet).DecideAsync(
            Request("00000000-0000-4000-8000-000000000a15", source: ManualStationClearanceSources.Host), Token);

        Assert.Equal(FieldConfirmationDecision.Confirmed, decision.Decision.Outcome);
        Assert.Null(await ClaimOfAsync(fleet, KeyA));
        await RoundAsync(fleet);
        Assert.Equal(JourneyRuntimeStage.Completed, (await fleet.Context.JourneyRuntimes.AsNoTracking()
            .SingleAsync(row => row.JourneyId == journey.JourneyId, Token)).Stage);
    }

    /// <summary>
    /// 失败周期留下的预占放不掉（车读不到，事件 2247 的那一格）：人工清桩把那一行放掉，周期不再动。
    /// </summary>
    [Fact]
    public async Task AReservationAFailedCycleLeftIsReleasedByAManualClearance()
    {
        await using FleetFixture fleet = await FleetAsync();
        fleet.Riot.BatteryByVehicle[KeyA] = 20;
        JourneyRuntimeRow journey = await CommittedAndSentAsync(fleet);
        fleet.Riot.CancelOrder(journey.PickupUpperId);
        // Proven stopped off the charger, but its battery state does not read: the cycle ends as a confirmed failure and the
        // sweep cannot confirm that charging has stopped, so the reservation stays. Then the vehicle drops off RIoT altogether.
        fleet.Riot.VehicleOverrides[KeyA] = seen => seen with { CurrentStationId = 300, BatteryState = null };
        await RoundAsync(fleet);
        await RoundAsync(fleet);
        Assert.Equal(ChargingExecutionReasons.OrderEnded, (await fleet.Context.Set<ChargingCycleRow>().AsNoTracking()
            .SingleAsync(row => row.JourneyId == journey.JourneyId, Token)).EndReason);
        fleet.Riot.VehicleOverrides[KeyA] = seen => seen with { Connected = false, CurrentStationId = null };
        await RoundAsync(fleet);
        Assert.Equal((KeyA, StationExclusivityStates.Reserved), await HolderAsync(fleet, Near.StationId));

        ManualStationClearanceConfirmation decision = await Clearance(fleet).DecideAsync(
            Request("00000000-0000-4000-8000-000000000a16", source: ManualStationClearanceSources.Host), Token);

        Assert.Equal((FieldConfirmationDecision.Confirmed, true), (decision.Decision.Outcome, decision.StationReleased));
        Assert.Null(await HolderAsync(fleet, Near.StationId));
        Assert.Equal(ChargingExecutionReasons.OrderEnded, (await fleet.Context.Set<ChargingCycleRow>().AsNoTracking()
            .SingleAsync(row => row.JourneyId == journey.JourneyId, Token)).EndReason);
    }

    /// <summary>车在正常推进（去桩途中，没有任何保持码）：人工清桩不接，答 <c>ACTION_NOT_ALLOWED_IN_STATE</c>，什么也不放。</summary>
    [Fact]
    public async Task AVehicleChargingNormallyIsNotClearedByHand()
    {
        await using FleetFixture fleet = await FleetAsync();
        fleet.Riot.BatteryByVehicle[KeyA] = 20;
        await CommittedAndSentAsync(fleet);
        MovedOff(fleet);

        ManualStationClearanceConfirmation decision = await Clearance(fleet).DecideAsync(
            Request("00000000-0000-4000-8000-000000000a17", source: ManualStationClearanceSources.Host), Token);

        Assert.Equal((FieldConfirmationDecision.Rejected, ManualStationClearance.NotAllowedInState),
            (decision.Decision.Outcome, decision.Decision.ProblemReasonCode));
        Assert.Equal((KeyA, StationExclusivityStates.Reserved), await HolderAsync(fleet, Near.StationId));
    }

    // ---- CV-MANUAL-STATION-CLEARANCE（经真实的消息处理与存储）--------------------------------------------------------------

    /// <summary>
    /// <c>CV-MANUAL-STATION-CLEARANCE</c>「确认才释放」：车载端发 <c>ManualStationClearanceConfirmationRequested</c>，服务端经入站处理器与存储判定，
    /// 回 <c>ManualStationClearanceConfirmationResult</c>（<c>CONFIRMED</c>、<c>problem</c> 为空、旧单已终结时 <c>stationReleased=true</c>），桩的独占已放。
    /// 车载端重提（同一确认号、同一份载荷、新的 <c>messageId</c>）拿回逐字节相同的载荷。
    /// </summary>
    [Fact]
    [Trait("ProtocolVector", Vector)]
    public async Task CvManualStationClearanceReleasesTheChargerOnlyOnAConfirmedClearance()
    {
        await using FleetFixture fleet = await FleetAsync();
        JourneyRuntimeRow journey = await ConfirmedAsync(fleet);
        fleet.Riot.CancelOrder(journey.PickupUpperId);
        MovedOff(fleet);
        (OnboardMessageProcessor processor, OnboardConnectionState state) = await ProcessorAsync(fleet);
        const string id = "00000000-0000-4000-8000-000000000b01";

        JsonElement result = await SendAsync(processor, state, id, "00000000-0000-4000-8000-000000000b02", "op-r11", Near.StationName);

        Assert.Equal(id, result.GetProperty("confirmationRequestId").GetString());
        Assert.Equal("CONFIRMED", result.GetProperty("outcome").GetString());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("problem").ValueKind);
        Assert.True(result.GetProperty("stationReleased").GetBoolean());
        Assert.Null(await HolderAsync(fleet, Near.StationId));
        Assert.Equal(id, (await ClearanceRowAsync(fleet)).ConfirmationRequestId);

        JsonElement resubmitted = await SendAsync(processor, state, id, "00000000-0000-4000-8000-000000000b03", "op-r11", Near.StationName);
        Assert.Equal(result.GetRawText(), resubmitted.GetRawText());
    }

    public static TheoryData<string> VectorRefusals => ["ordinary-operator", "identity-conflict", "station-mismatch"];

    /// <summary>
    /// <c>CV-MANUAL-STATION-CLEARANCE</c>「拒绝不释放」：普通操作员、与系统事实冲突（RIoT 读到车仍在桩上）、站点不符，各一种输入。回
    /// <c>REJECTED</c>、带协议登记过的 <c>problem.reasonCode</c>、<c>stationReleased=false</c>，桩的独占不动。
    /// </summary>
    [Theory]
    [MemberData(nameof(VectorRefusals))]
    [Trait("ProtocolVector", Vector)]
    public async Task CvManualStationClearanceRefusesWithoutReleasing(string input)
    {
        await using FleetFixture fleet = await FleetAsync();
        JourneyRuntimeRow journey = await ConfirmedAsync(fleet);
        fleet.Riot.CancelOrder(journey.PickupUpperId);
        if (input != "identity-conflict") MovedOff(fleet);
        (OnboardMessageProcessor processor, OnboardConnectionState state) = await ProcessorAsync(fleet);

        JsonElement result = await SendAsync(
            processor, state, "00000000-0000-4000-8000-000000000b04", "00000000-0000-4000-8000-000000000b05",
            input == "ordinary-operator" ? "op-plain" : "op-r11",
            input == "station-mismatch" ? Far.StationName : Near.StationName);

        Assert.Equal("REJECTED", result.GetProperty("outcome").GetString());
        Assert.False(result.GetProperty("stationReleased").GetBoolean());
        Assert.Contains(
            result.GetProperty("problem").GetProperty("reasonCode").GetString(),
            new[] { ManualStationClearance.NotAuthorized, ManualStationClearance.StationMismatch, ManualStationClearance.NotAllowedInState });
        Assert.Equal((KeyA, StationExclusivityStates.Reserved), await HolderAsync(fleet, Near.StationId));
        Assert.Null((await ClearanceRowAsync(fleet)).CompletedAt);
    }

    // ---- Host 入口 --------------------------------------------------------------------------------------------------------

    /// <summary>
    /// 维修暂停（收紧）：只收本机来源——别处来的 403、不读请求体；名册里的桩写一条 <c>MAINTENANCE</c> 暂停（201），再来一次答已有的那一条（200）；
    /// 名册外的桩 422。每一次都写管理员审计。
    /// </summary>
    [Fact]
    public async Task TheMaintenanceHoldEntryTakesOnlyLocalRequestsAndHoldsOnce()
    {
        await using FleetFixture fleet = await FleetAsync();
        ChargingHoldStore holds = new(fleet.Context);
        GovernanceStore audit = Audit(fleet.Context);
        FixedRoster roster = new([Near]);

        IResult remote = await ChargingStationEndpoints.HoldAsync(
            HttpContextFor(new { mapId = Map, stationId = Near.StationId, reason = "检修" }, IPAddress.Parse("10.0.0.9")),
            roster, holds, audit, new GovernanceDeploymentIdentity("deployment:test"), fleet.Clock,
            NullLogger<ChargingStationHoldHttpRequest>.Instance, Token);
        Assert.Equal(StatusCodes.Status403Forbidden, ((IStatusCodeHttpResult)remote).StatusCode);

        IResult first = await HoldAsync(new { mapId = Map, stationId = Near.StationId, reason = "检修", claimedRole = "维护管理员" });
        IResult again = await HoldAsync(new { mapId = Map, stationId = Near.StationId, reason = "检修" });
        IResult outside = await HoldAsync(new { mapId = Map, stationId = 999, reason = "检修" });

        Assert.Equal(StatusCodes.Status201Created, ((IStatusCodeHttpResult)first).StatusCode);
        Assert.Equal(StatusCodes.Status200OK, ((IStatusCodeHttpResult)again).StatusCode);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, ((IStatusCodeHttpResult)outside).StatusCode);
        ChargingStationAllocationHoldRow hold = Assert.Single(await HoldsAsync(fleet));
        Assert.Equal((ChargingStationHoldTriggers.Maintenance, "检修", "维护管理员"), (hold.Trigger, hold.SiteDisposition, hold.ConfirmedByRole));
        Assert.Equal(4, await fleet.Context.Set<AdministratorAuditRecordRow>().AsNoTracking()
            .CountAsync(row => row.Action == ChargingStationEndpoints.HoldAuditAction, Token));

        async Task<IResult> HoldAsync(object body) => await ChargingStationEndpoints.HoldAsync(
            HttpContextFor(body, IPAddress.Loopback), roster, holds, audit, new GovernanceDeploymentIdentity("deployment:test"),
            fleet.Clock, NullLogger<ChargingStationHoldHttpRequest>.Instance, Token);
    }

    /// <summary>
    /// 恢复确认（放宽）：没有凭据 401；这个桩没有未恢复的暂停 409；有时把每一条未恢复的暂停（维修、充不上）各记一条恢复，桩回到分配候选，名册不改。
    /// </summary>
    [Fact]
    public async Task TheRecoveryEntryClosesEveryHoldOfTheChargerWithTheCredential()
    {
        const string variable = "CONTROL_SERVER_TEST_CS406_CREDENTIAL";
        Environment.SetEnvironmentVariable(variable, "cs406-secret");
        try
        {
            await using FleetFixture fleet = await FleetAsync(vehicles: 2);
            JourneyRuntimeRow journey = await ConfirmedAsync(fleet);
            ChargingHoldStore holds = new(fleet.Context);
            IOptions<VehicleFaultRecoveryOptions> options = Options.Create(new VehicleFaultRecoveryOptions { CredentialEnvironmentVariable = variable });
            ChargingStationRecoveryHttpRequest request = new(Map, Near.StationId, "op-r11", "维修单 M-1", "维护管理员");

            var unauthenticated = await ChargingStationEndpoints.RecoverAsync(
                HttpContextFor(null, IPAddress.Loopback), request, holds, Audit(fleet.Context), options, fleet.Clock,
                NullLogger<ChargingStationRecoveryHttpRequest>.Instance, Token);
            Assert.IsType<UnauthorizedHttpResult>(unauthenticated.Result);
            var nothing = await ChargingStationEndpoints.RecoverAsync(
                HttpContextFor(null, IPAddress.Loopback, "cs406-secret"), request with { StationId = Far.StationId }, holds,
                Audit(fleet.Context), options, fleet.Clock, NullLogger<ChargingStationRecoveryHttpRequest>.Instance, Token);
            Assert.Equal(StatusCodes.Status409Conflict, ((IStatusCodeHttpResult)nothing.Result).StatusCode);

            var recovered = await ChargingStationEndpoints.RecoverAsync(
                HttpContextFor(null, IPAddress.Loopback, "cs406-secret"), request, holds, Audit(fleet.Context), options, fleet.Clock,
                NullLogger<ChargingStationRecoveryHttpRequest>.Instance, Token);

            Assert.Equal(StatusCodes.Status200OK, ((IStatusCodeHttpResult)recovered.Result).StatusCode);
            ChargingStationRecoveryRow recovery = Assert.Single(await fleet.Context.Set<ChargingStationRecoveryRow>().AsNoTracking().ToArrayAsync(Token));
            Assert.Equal(("op-r11", "维修单 M-1"), (recovery.RecoveredBy, recovery.Basis));
            Assert.Empty(await holds.ListActiveStationHoldsAsync(Map, Near.StationId, Token));
            _ = journey;
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    /// <summary>Host 人工清桩（放宽）：没有凭据 401；有凭据与 R-11 的人时走同一个判定，答 <c>CONFIRMED</c> 并放桩。</summary>
    [Fact]
    public async Task TheClearanceEntryDecidesThroughTheSameDecisionWithTheCredential()
    {
        const string variable = "CONTROL_SERVER_TEST_CS406_CLEARANCE_CREDENTIAL";
        Environment.SetEnvironmentVariable(variable, "cs406-secret");
        try
        {
            await using FleetFixture fleet = await FleetAsync();
            JourneyRuntimeRow journey = await ConfirmedAsync(fleet);
            fleet.Riot.CancelOrder(journey.PickupUpperId);
            MovedOff(fleet);
            IOptions<VehicleFaultRecoveryOptions> options = Options.Create(new VehicleFaultRecoveryOptions { CredentialEnvironmentVariable = variable });
            ChargingStationClearanceHttpRequest request = new(AgvA, Near.StationName, "op-r11", null);
            ControlServer.Host.Runtime.Fleet.VehicleRoster roster = new(Options.Create(fleet.Options));

            var unauthenticated = await ChargingStationEndpoints.ClearAsync(
                HttpContextFor(null, IPAddress.Loopback), request, Clearance(fleet), roster, options, fleet.Clock, Token);
            Assert.IsType<UnauthorizedHttpResult>(unauthenticated.Result);

            var confirmed = await ChargingStationEndpoints.ClearAsync(
                HttpContextFor(null, IPAddress.Loopback, "cs406-secret"), request, Clearance(fleet), roster, options, fleet.Clock, Token);

            Ok<ChargingStationClearanceResponse> ok = Assert.IsType<Ok<ChargingStationClearanceResponse>>(confirmed.Result);
            Assert.Equal((FieldConfirmationDecision.Confirmed, true), (ok.Value!.Outcome, ok.Value.StationReleased));
            Assert.Null(await HolderAsync(fleet, Near.StationId));
            Assert.Single(await fleet.Context.Set<AdministratorAuditRecordRow>().AsNoTracking()
                .Where(row => row.Action == ManualStationClearance.AuditAction).ToArrayAsync(Token));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    // ---- 夹具 ------------------------------------------------------------------------------------------------------------

    private ManualStationClearance Clearance(
        FleetFixture fleet, ControlServerDbContext? context = null, IRiotVehicleFacts? riot = null, string? rosterPath = "default")
    {
        ControlServerDbContext db = context ?? fleet.Context;
        return new ManualStationClearance(
            db,
            new FieldConfirmationRequestStore(db),
            new StationClearanceStore(db),
            riot ?? fleet.Riot,
            new FieldOperatorRoleRoster(Options.Create(new FieldOperatorRoleOptions { Path = rosterPath == "default" ? _roster : rosterPath })),
            Audit(db),
            Options.Create(fleet.Options),
            fleet.Clock,
            NullLogger<ManualStationClearance>.Instance);
    }

    private static GovernanceStore Audit(ControlServerDbContext context) =>
        new(context, new GovernanceDeploymentIdentity("deployment:8005-controlserver@test"), AuditRetentionPolicy.Default);

    private static ManualStationClearanceRequest Request(
        string confirmationRequestId,
        string operatorId = "op-r11",
        string? station = null,
        string? publicFunction = null,
        string source = ManualStationClearanceSources.Onboard)
    {
        station ??= Near.StationName;
        return new ManualStationClearanceRequest(
            source, AgvA, KeyA, confirmationRequestId, 1, Guid.NewGuid().ToString("D"),
            $"{station}|{publicFunction}|{operatorId}|STATION_EMPTY", station, publicFunction, "STATION_EMPTY", operatorId, "BADGE",
            DateTimeOffset.Parse("2026-09-08T06:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
            DateTimeOffset.Parse("2026-09-08T06:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>有人把车挪开了：RIoT 读到它在 300、没在充电。</summary>
    private static void MovedOff(FleetFixture fleet) =>
        fleet.Riot.VehicleOverrides[KeyA] = seen => seen with { CurrentStationId = 300, BatteryState = "NO_CHARGE" };

    private static void EndOldOrder(FleetFixture fleet, string upperId, string shape)
    {
        switch (shape)
        {
            case "cancelled": fleet.Riot.CancelOrder(upperId); break;
            case "deleted": fleet.Riot.DeleteOrder(upperId); break;
            case "real-absent":
                fleet.Riot.AnswersAbsentAsRealRiot = true;
                fleet.Riot.PutOrder(new RiotOrderObservation(upperId, RiotOrderObservationKind.NotFound, null));
                break;
            case "not-found-404":
                fleet.Riot.PutOrder(new RiotOrderObservation(upperId, RiotOrderObservationKind.NotFound, null));
                break;
        }
    }

    /// <summary>充不上、旧单已取消、车已挪开、人工清桩确认、下一轮旅程收尾。车仍低电。</summary>
    private async Task ClearedAndClosedAsync(FleetFixture fleet)
    {
        JourneyRuntimeRow journey = await ConfirmedAsync(fleet);
        fleet.Riot.CancelOrder(journey.PickupUpperId);
        MovedOff(fleet);
        Assert.True((await Clearance(fleet).DecideAsync(Request("00000000-0000-4000-8000-000000000c01"), Token)).StationReleased);
        await RoundAsync(fleet);
        Assert.Equal(JourneyRuntimeStage.Completed, (await fleet.Context.JourneyRuntimes.AsNoTracking()
            .SingleAsync(row => row.JourneyId == journey.JourneyId, Token)).Stage);
        fleet.ChargingLog.Entries.Clear();
    }

    private static async Task<StationClearanceRow> ClearanceRowAsync(FleetFixture fleet) =>
        await fleet.Context.Set<StationClearanceRow>().AsNoTracking().SingleAsync(Token);

    private static async Task AssertNothingReleasedAsync(FleetFixture fleet)
    {
        Assert.Null((await ClearanceRowAsync(fleet)).CompletedAt);
        Assert.Equal((KeyA, StationExclusivityStates.Reserved), await HolderAsync(fleet, Near.StationId));
        Assert.Equal(VehiclePurposes.ClearingMaintenance, (await ClaimOfAsync(fleet, KeyA))!.Value.Purpose);
        Assert.Equal(ChargingCyclePhases.Clearing, (await OpenCycleAsync(fleet)).Phase);
    }

    private async Task<(OnboardMessageProcessor Processor, OnboardConnectionState State)> ProcessorAsync(FleetFixture fleet)
    {
        long generation = await fleet.Context.SessionRecoveries.AsNoTracking()
            .Where(row => row.AgvId == AgvA).Select(row => row.SessionGeneration).SingleAsync(Token);
        OnboardMessageProcessor processor = TestOnboardProcessorFactory.Create(
            fleet.Context, new WireToGateStore(fleet.Context), fleet.Clock, new ConfigurationBuilder().Build(),
            runtimeOptions: fleet.Options, stationClearance: Clearance(fleet));
        OnboardConnectionState state = new()
        {
            AgvId = AgvA,
            SessionGeneration = generation,
            HandshakeCompleted = true,
            Readiness = SessionReadiness.Ready,
        };
        return (processor, state);
    }

    /// <summary>车载端发一次清桩确认（hmi#221 的形状：操作员号、<c>SESSION</c>、<c>STATION_EMPTY</c>、<c>publicStationFunction</c> 为空），答 Result 的载荷。</summary>
    private static async Task<JsonElement> SendAsync(
        OnboardMessageProcessor processor, OnboardConnectionState state, string confirmationRequestId, string messageId,
        string operatorId, string stationId)
    {
        string line = JsonSerializer.Serialize(new
        {
            protocolVersion = ProtocolCandidateIdentity.ProtocolVersion,
            profileId = ProtocolCandidateIdentity.ProfileId,
            protocolReleaseVersion = ProtocolCandidateIdentity.ReleaseVersion,
            protocolReleaseManifestSha256 = ProtocolCandidateIdentity.ManifestSha256,
            messageType = "ManualStationClearanceConfirmationRequested",
            messageId,
            correlationId = (string?)null,
            agvId = state.AgvId,
            sessionGeneration = state.SessionGeneration,
            sentAt = "2026-09-08T06:00:00Z",
            payload = new
            {
                confirmationRequestId,
                stationId,
                publicStationFunction = (string?)null,
                clearedCondition = "STATION_EMPTY",
                @operator = new { operatorId, verificationMethod = "SESSION", verifiedAt = "2026-09-08T05:59:00Z" },
                observedAt = "2026-09-08T06:00:00Z",
            },
        });
        string response = await processor.ProcessAsync(line, state, Token);
        using JsonDocument document = JsonDocument.Parse(response.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0]);
        Assert.Equal("ManualStationClearanceConfirmationResult", document.RootElement.GetProperty("messageType").GetString());
        Assert.Equal(messageId, document.RootElement.GetProperty("correlationId").GetString());
        return document.RootElement.GetProperty("payload").Clone();
    }

    private static DefaultHttpContext HttpContextFor(object? body, IPAddress remote, string? bearer = null)
    {
        DefaultHttpContext context = new();
        context.Connection.RemoteIpAddress = remote;
        context.Connection.LocalIpAddress = IPAddress.Loopback;
        if (bearer is not null)
        {
            context.Request.Headers.Authorization = $"Bearer {bearer}";
        }
        if (body is not null)
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(body);
            context.Request.Body = new MemoryStream(bytes);
            context.Request.ContentLength = bytes.Length;
            context.Request.ContentType = "application/json";
        }
        return context;
    }

    /// <summary>名册的一个固定版本。</summary>
    private sealed class FixedRoster(ChargerRosterEntry[] chargers) : IChargerRoster
    {
        private readonly ChargerRosterVersion _version = new(
            1, "sha", null, DateTimeOffset.UnixEpoch, ChargerRosterSources.GovernedImport,
            new ChargerRosterApproval("tester", "test", null), chargers);

        public Task<ChargerRosterVersion> WriteVersionAsync(
            IReadOnlyList<ChargerRosterEntry> chargers, ChargerRosterApproval approval, DateTimeOffset loadedAt,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ChargerRosterVersion?> ReadCurrentAsync(CancellationToken cancellationToken) =>
            Task.FromResult<ChargerRosterVersion?>(_version);

        public Task<ChargerRosterVersion?> ReadVersionAsync(long version, CancellationToken cancellationToken) =>
            Task.FromResult<ChargerRosterVersion?>(_version);
    }

    /// <summary>读车那一刻先让另一件事做完：判定读完库、读 RIoT 的那一刻，另一个确认整笔提交（并发交错的第一个时刻）。</summary>
    private sealed class InterleavingRiot(IRiotVehicleFacts inner, Func<Task> meanwhile) : IRiotVehicleFacts
    {
        public async Task<RiotVehicleObservation> ReadVehicleAsync(string vehicleKey, CancellationToken cancellationToken)
        {
            await meanwhile();
            return await inner.ReadVehicleAsync(vehicleKey, cancellationToken);
        }

        public Task<RiotOrderObservation> ReconcileByUpperIdAsync(string upperId, CancellationToken cancellationToken) =>
            inner.ReconcileByUpperIdAsync(upperId, cancellationToken);

        public Task<RiotOrderObservation> CreateAsync(OrderIntent intent, CancellationToken cancellationToken) =>
            inner.CreateAsync(intent, cancellationToken);
    }
}
