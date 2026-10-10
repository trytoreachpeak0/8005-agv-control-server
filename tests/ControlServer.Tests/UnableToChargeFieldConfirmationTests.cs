using System.Data.Common;
using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Charging;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using static ControlServer.Tests.ChargingAllocationTests;
using static ControlServer.Tests.ChargingExecutionTests;
using static ControlServer.Tests.ChargingUnableToChargeTests;
using FleetFixture = ControlServer.Tests.MultiVehicleExecutionTests.FleetFixture;

namespace ControlServer.Tests;

/// <summary>
/// 现场确认充不上（批次9-12，control-server#410；<c>REQ-0176</c>、<c>REQ-0177</c>）：只有 R-11 能确认、普通操作员只报告；身份冲突与 RIoT 读不到一律拒绝；
/// <c>observedCondition</c> 四值；<c>chargingPolicyDecision</c> 三种只由服务端事实定；确认后与系统确认同一条暂停与清桩路、与系统确认竞争只一条事件；
/// 重放、内容冲突、崩溃点；以及 <c>CV-UNABLE-TO-CHARGE-FIELD-CONFIRMATION</c> 的同名具名测试（经真实的消息处理与存储）。
/// </summary>
/// <remarks>
/// 夹具同 <see cref="ChargingUnableToChargeTests"/>：A 电量 20，承诺 211、建单、确认；单在桩上挂起，开始充电的动作带一个<b>没验证过</b>的结果码（1），
/// 所以系统事实不完整、引擎不自动形成确认——正是现场确认要接的情形。车载端的生效策略两道线都是 40。
/// </remarks>
[Trait("IntegrationSlice", "FP-IS-13")]
public sealed class UnableToChargeFieldConfirmationTests : IDisposable
{
    private const string Vector = "CV-UNABLE-TO-CHARGE-FIELD-CONFIRMATION";
    private const int Map = 25;

    private readonly string _roster = Path.Combine(Path.GetTempPath(), $"cs410-roster-{Guid.NewGuid():N}.json");

    public UnableToChargeFieldConfirmationTests()
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

    // ---- 谁能确认（REQ-0176）-----------------------------------------------------------------------------------------------

    public static TheoryData<string, string?> Confirmers => new()
    {
        { "op-r11", null },
        { "op-r13", UnableToChargeFieldConfirmations.NotAuthorized },
        { "op-plain", UnableToChargeFieldConfirmations.NotAuthorized },
        { "op-unknown", UnableToChargeFieldConfirmations.NotAuthorized },
    };

    /// <summary>
    /// 只有 R-11 能确认（R-13 是调度管理员，不是维护权限）。别人的请求只作报告：判定与观察记下（确认请求表一行、管理员审计一条、告警 2291），
    /// <c>REJECTED</c>、<c>chargingPolicyDecision</c> 为空，桩不暂停、周期不动、用途不动。
    /// </summary>
    [Theory]
    [MemberData(nameof(Confirmers))]
    public async Task OnlyR11ConfirmsAndAnyoneElseOnlyReports(string operatorId, string? reasonCode)
    {
        await using FleetFixture fleet = await FleetAsync();
        await UnconfirmedAtChargerAsync(fleet);
        EventRecordingLogger<UnableToChargeFieldConfirmations> log = new();

        UnableToChargeFieldConfirmation decision = await Decider(fleet, logger: log)
            .DecideAsync(Request("00000000-0000-4000-8000-000000000d01", operatorId: operatorId), Token);

        Assert.Equal(reasonCode, decision.Decision.ProblemReasonCode);
        UnableToChargeFieldConfirmationRow recorded = Assert.Single(
            await fleet.Context.Set<UnableToChargeFieldConfirmationRow>().AsNoTracking().ToArrayAsync(Token));
        Assert.Equal(("CONNECTION_FAILED", operatorId), (recorded.ObservedCondition, recorded.OperatorId));
        Assert.Single(await fleet.Context.Set<AdministratorAuditRecordRow>().AsNoTracking()
            .Where(row => row.Action == UnableToChargeFieldConfirmations.AuditAction).ToArrayAsync(Token));
        if (reasonCode is null)
        {
            Assert.Equal(FieldConfirmationDecision.Confirmed, decision.Decision.Outcome);
            Assert.NotNull(decision.ChargingPolicyDecision);
            Assert.Single(await HoldsAsync(fleet));
            Assert.Single(log.Entries, entry => entry.EventId.Id == 2290);
        }
        else
        {
            Assert.Equal((FieldConfirmationDecision.Rejected, (string?)null), (decision.Decision.Outcome, decision.ChargingPolicyDecision));
            Assert.Equal("payload.operator.operatorId", decision.Decision.ProblemFieldPath);
            await AssertNothingPausedAsync(fleet);
            Assert.Single(log.Entries, entry => entry.EventId.Id == 2291);
        }
    }

    // ---- 身份冲突与读不到（人工不能覆盖）-------------------------------------------------------------------------------------

    public static TheoryData<string, string> IdentityConflicts => new()
    {
        { "no-cycle", UnableToChargeFieldConfirmations.NotAllowedInState },
        { "another-charger", UnableToChargeFieldConfirmations.StationMismatch },
        { "not-on-the-charger", UnableToChargeFieldConfirmations.NotAllowedInState },
        { "cycle-complete", UnableToChargeFieldConfirmations.NotAllowedInState },
        { "clearing-after-an-interruption", UnableToChargeFieldConfirmations.NotAllowedInState },
        { "charging-seen-this-cycle", UnableToChargeFieldConfirmations.NotAllowedInState },
        { "riot-reads-it-charging", UnableToChargeFieldConfirmations.NotAllowedInState },
        { "riot-unreadable", UnableToChargeFieldConfirmations.NotAllowedInState },
        { "riot-reading-stale", UnableToChargeFieldConfirmations.NotAllowedInState },
        { "exit-unavailable", UnableToChargeFieldConfirmations.NotAllowedInState },
    };

    /// <summary>
    /// 判定那一刻重核身份：没有周期、目标桩不是请求里的桩、RIoT 新鲜读到车不在这个桩上、周期已 <c>COMPLETE</c>、周期已因中断进了清桩中、本周期读到过充电、
    /// 此刻在充电、RIoT 读不到车（<c>FAILED</c>／<c>UNKNOWN</c>）、读数不新鲜、确认之后没人能清桩：R-11 确认也一律 <c>REJECTED</c>，判定仍记下，
    /// 什么都不暂停（暂停事件为零，周期与用途不变）。
    /// </summary>
    [Theory]
    [MemberData(nameof(IdentityConflicts))]
    public async Task AnIdentityConflictOrAnUnknownIsRejectedAndNotOverriddenByHand(string conflict, string reasonCode)
    {
        await using FleetFixture fleet = await FleetAsync();
        IRiotVehicleFacts riot = fleet.Riot;
        bool exitDeclared = true;
        if (conflict != "no-cycle")
        {
            await UnconfirmedAtChargerAsync(fleet);
        }
        switch (conflict)
        {
            case "not-on-the-charger":
                fleet.Riot.VehicleOverrides[KeyA] = seen => seen with { CurrentStationId = 300, BatteryState = "NO_CHARGE" };
                break;
            case "cycle-complete":
                await SetCycleAsync(fleet, wireState: ChargingCycleWireStates.Complete);
                break;
            case "clearing-after-an-interruption":
                await SetCycleAsync(fleet, phase: ChargingCyclePhases.Clearing);
                break;
            case "charging-seen-this-cycle":
                await SetCycleAsync(fleet, firstChargingSeenAt: fleet.Clock.GetUtcNow());
                break;
            case "riot-reads-it-charging":
                fleet.Riot.VehicleOverrides[KeyA] = seen => seen with { CurrentStationId = Near.StationId, BatteryState = "CHARGING" };
                break;
            case "riot-unreadable":
                riot = new UnreadableVehicleRiot(fleet.Riot);
                break;
            case "riot-reading-stale":
                DateTimeOffset old = fleet.Clock.GetUtcNow() - TimeSpan.FromMinutes(5);
                fleet.Riot.VehicleOverrides[KeyA] = seen => seen with
                {
                    CurrentStationId = Near.StationId,
                    BatteryState = "NO_CHARGE",
                    ObservedAt = old,
                };
                break;
            case "exit-unavailable":
                exitDeclared = false;
                break;
        }

        UnableToChargeFieldConfirmation decision = await Decider(fleet, riot: riot, exitDeclared: exitDeclared)
            .DecideAsync(
                Request("00000000-0000-4000-8000-000000000d02", station: conflict == "another-charger" ? Far.StationName : Near.StationName),
                Token);

        Assert.Equal((FieldConfirmationDecision.Rejected, reasonCode, (string?)null),
            (decision.Decision.Outcome, decision.Decision.ProblemReasonCode, decision.ChargingPolicyDecision));
        Assert.Single(await fleet.Context.Set<UnableToChargeFieldConfirmationRow>().AsNoTracking().ToArrayAsync(Token));
        Assert.Empty(await HoldsAsync(fleet));
        Assert.Empty(await fleet.Context.Set<StationClearanceRow>().AsNoTracking().ToArrayAsync(Token));
        if (conflict is not ("no-cycle" or "clearing-after-an-interruption"))
        {
            await AssertNothingPausedAsync(fleet);
        }
    }

    public static TheoryData<int> OrderStatesThatCanMoveTheVehicle =>
        [RiotOrderState.Queueing, RiotOrderState.Executing, RiotOrderState.QueuePriority];

    /// <summary>
    /// 审查 S1：旧单被人在 RIoT 里继续（排队、执行、队列优先——会让车动的状态），车即使此刻停在桩上也不是「充不上停在那里」：<c>REJECTED</c>
    /// （<c>ACTION_NOT_ALLOWED_IN_STATE</c>），什么都不暂停，引擎之后也不会报「清桩中旧单被继续」（2273）。与人工清桩拒收的是同一个判断。
    /// </summary>
    [Theory]
    [MemberData(nameof(OrderStatesThatCanMoveTheVehicle))]
    public async Task AnOldOrderLetGoOnInRiotIsNotConfirmedAsUnableToCharge(int state)
    {
        await using FleetFixture fleet = await FleetAsync();
        JourneyRuntimeRow journey = await UnconfirmedAtChargerAsync(fleet);
        fleet.Riot.PutOrder(fleet.Riot.OrderOf(journey.PickupUpperId)! with { OrderState = state });

        UnableToChargeFieldConfirmation decision = await Decider(fleet)
            .DecideAsync(Request("00000000-0000-4000-8000-000000000d30"), Token);

        Assert.Equal((FieldConfirmationDecision.Rejected, UnableToChargeFieldConfirmations.NotAllowedInState, (string?)null),
            (decision.Decision.Outcome, decision.Decision.ProblemReasonCode, decision.ChargingPolicyDecision));
        await AssertNothingPausedAsync(fleet);
        await RoundAsync(fleet);
        Assert.DoesNotContain(fleet.EngineLog.Entries, entry => entry.EventId.Id == 2273);
    }

    /// <summary>
    /// 增量审查：旧单此刻读不到（RIoT 读单出错）——它可能正被人在 RIoT 里继续，与 S1 同一个局面：<c>REJECTED</c>（<c>ACTION_NOT_ALLOWED_IN_STATE</c>），
    /// 什么都不暂停。读不到也拒绝。
    /// </summary>
    [Fact]
    public async Task AnUnreadableOldOrderIsNotConfirmedAsUnableToCharge()
    {
        await using FleetFixture fleet = await FleetAsync();
        await UnconfirmedAtChargerAsync(fleet);

        UnableToChargeFieldConfirmation decision = await Decider(fleet, riot: new UnreadableOrderRiot(fleet.Riot))
            .DecideAsync(Request("00000000-0000-4000-8000-000000000d33"), Token);

        Assert.Equal((FieldConfirmationDecision.Rejected, UnableToChargeFieldConfirmations.NotAllowedInState, (string?)null),
            (decision.Decision.Outcome, decision.Decision.ProblemReasonCode, decision.ChargingPolicyDecision));
        await AssertNothingPausedAsync(fleet);
    }

    // ---- observedCondition 四值 -----------------------------------------------------------------------------------------

    public static TheoryData<string, bool> Conditions => new()
    {
        { "CONNECTION_FAILED", true },
        { "CHARGER_FAULT", true },
        { "CHARGER_UNREACHABLE", false },
        { "CHARGER_OCCUPIED", false },
    };

    /// <summary>
    /// 只有「桩在、试过、没建立」可确认：<c>CONNECTION_FAILED</c>、<c>CHARGER_FAULT</c>。<c>CHARGER_UNREACHABLE</c>、<c>CHARGER_OCCUPIED</c> 不是充不上
    /// （<c>REQ-0175</c>）：记下观察并告警，<c>REJECTED</c>（<c>payload.observedCondition</c>）、<c>chargingPolicyDecision</c> 为空，不暂停。
    /// </summary>
    [Theory]
    [MemberData(nameof(Conditions))]
    public async Task OnlyATriedAndNotEstablishedChargeIsConfirmed(string condition, bool confirmable)
    {
        await using FleetFixture fleet = await FleetAsync();
        await UnconfirmedAtChargerAsync(fleet);

        UnableToChargeFieldConfirmation decision = await Decider(fleet)
            .DecideAsync(Request("00000000-0000-4000-8000-000000000d03", condition: condition), Token);

        if (confirmable)
        {
            Assert.Equal(FieldConfirmationDecision.Confirmed, decision.Decision.Outcome);
            Assert.Equal(condition, Assert.Single(await HoldsAsync(fleet)).SiteDisposition);
        }
        else
        {
            Assert.Equal(
                (FieldConfirmationDecision.Rejected, UnableToChargeFieldConfirmations.NotAllowedInState, "payload.observedCondition",
                    (string?)null),
                (decision.Decision.Outcome, decision.Decision.ProblemReasonCode, decision.Decision.ProblemFieldPath,
                    decision.ChargingPolicyDecision));
            Assert.Equal(condition, (await fleet.Context.Set<UnableToChargeFieldConfirmationRow>().AsNoTracking().SingleAsync(Token))
                .ObservedCondition);
            await AssertNothingPausedAsync(fleet);
        }
    }

    // ---- chargingPolicyDecision 三种（DECIDE_CHARGING_POLICY_CENTRALLY）------------------------------------------------------

    public static TheoryData<string, string> Policies => new()
    {
        { "another-charger-in-the-roster", ChargingPolicyDecisions.ReassignCharger },
        { "the-other-charger-is-paused-battery-can-wait", ChargingPolicyDecisions.RetryLater },
        { "only-this-charger-battery-can-wait", ChargingPolicyDecisions.RetryLater },
        { "only-this-charger-battery-cannot-wait", ChargingPolicyDecisions.ManualChargingHold },
        { "another-charger-outside-the-vehicle-scope-battery-cannot-wait", ChargingPolicyDecisions.ManualChargingHold },
    };

    /// <summary>
    /// 名册里还有别的桩可给这辆车（同地图、车辆范围含它、没有未恢复的分配暂停）→ <c>REASSIGN_CHARGER</c>；没有，电量不低于这个周期策略的最低余量（40）→
    /// <c>RETRY_LATER</c>；没有、电量低于它 → <c>MANUAL_CHARGING_HOLD</c>，同一事务置服务端人工充电等待（原因与名册为空区分）。别的两种不置等待。
    /// </summary>
    [Theory]
    [MemberData(nameof(Policies))]
    public async Task TheChargingPolicyDecisionComesFromTheServersFactsAlone(string facts, string expected)
    {
        ChargerRosterEntry[] chargers = facts switch
        {
            "another-charger-in-the-roster" or "the-other-charger-is-paused-battery-can-wait" => [Near, Far],
            "another-charger-outside-the-vehicle-scope-battery-cannot-wait" => [Near, Far with { VehicleScope = ["agv-someone-else"] }],
            _ => [Near],
        };
        await using FleetFixture fleet = await FleetAsync(chargers: chargers);
        await UnconfirmedAtChargerAsync(fleet);
        fleet.Riot.BatteryByVehicle[KeyA] = facts.EndsWith("battery-can-wait", StringComparison.Ordinal) ? 50 : 20;
        if (facts == "the-other-charger-is-paused-battery-can-wait")
        {
            await new ChargingHoldStore(fleet.Context).RecordStationHoldAsync(MaintenanceHold(Far.StationId, fleet.Clock.GetUtcNow()), Token);
        }

        UnableToChargeFieldConfirmation decision = await Decider(fleet)
            .DecideAsync(Request("00000000-0000-4000-8000-000000000d04"), Token);

        Assert.Equal((FieldConfirmationDecision.Confirmed, expected), (decision.Decision.Outcome, decision.ChargingPolicyDecision));
        ManualChargingHold? hold = await new ManualChargingHoldStore(fleet.Context).ReadAsync(KeyA, Token);
        Assert.Equal(
            expected == ChargingPolicyDecisions.ManualChargingHold ? ManualChargingHoldReasons.UnableToChargeLowBattery : null,
            hold?.Reason);
    }

    /// <summary>
    /// 电量取自判定车在桩上的那<b>同一份</b>新鲜 RIoT 读数（调度 10-02 对齐）：RIoT 第一次答 20%、之后答 60%，判定只读一次车，所以按 20% 给
    /// <c>MANUAL_CHARGING_HOLD</c>。另读一次就会拿到 60% 而给 <c>RETRY_LATER</c>。
    /// </summary>
    [Fact]
    public async Task TheBatteryJudgedIsTheSameReadingThatPlacedTheVehicleOnTheCharger()
    {
        await using FleetFixture fleet = await FleetAsync();
        await UnconfirmedAtChargerAsync(fleet);
        BatteryThenRisingRiot riot = new(fleet.Riot, first: 20, later: 60);

        UnableToChargeFieldConfirmation decision = await Decider(fleet, riot: riot)
            .DecideAsync(Request("00000000-0000-4000-8000-000000000d20"), Token);

        Assert.Equal((FieldConfirmationDecision.Confirmed, ChargingPolicyDecisions.ManualChargingHold, 1),
            (decision.Decision.Outcome, decision.ChargingPolicyDecision, riot.VehicleReads));
        Assert.Contains("\"BatteryPercent\":20", Assert.Single(await HoldsAsync(fleet)).RawBatteryJson, StringComparison.Ordinal);
    }

    // ---- 确认之后：与系统确认同一条路 ------------------------------------------------------------------------------------------

    /// <summary>
    /// 确认后与系统确认写同样的行、同样的键：一条暂停事件（触发来源「已确认充不上」、根因 <c>UNKNOWN</c>、幂等键 <c>StableUuid(UNABLE_TO_CHARGE_CONFIRMED|周期)</c>），
    /// 另记确认人、角色 R-11、认证时刻、现场处置，证据引用标明「现场确认」；周期 <c>UNABLE_TO_CHARGE</c>／清桩中，用途 <c>CLEARING_MAINTENANCE</c>，清桩记录开始，
    /// 旅程写充不上的码。下一轮引擎（清桩分支）发清桩中的计划与业务状态快照，不建单、不发命令、不急停（原桩重试为 0、车保持原位）；暂停事件仍只一条。
    /// 有人挪车、旧单结束、人工清桩确认之后照 control-server#406 收尾。
    /// </summary>
    [Fact]
    public async Task AConfirmationTakesTheSamePauseAndClearingPathAsTheSystemOne()
    {
        await using FleetFixture fleet = await FleetAsync();
        JourneyRuntimeRow journey = await UnconfirmedAtChargerAsync(fleet);
        int creates = fleet.Riot.Creates.Count;
        DateTimeOffset verifiedAt = DateTimeOffset.Parse("2026-09-08T05:59:00Z", System.Globalization.CultureInfo.InvariantCulture);

        UnableToChargeFieldConfirmation decision = await Decider(fleet)
            .DecideAsync(Request("00000000-0000-4000-8000-000000000d05", condition: "CHARGER_FAULT"), Token);

        Assert.Equal(FieldConfirmationDecision.Confirmed, decision.Decision.Outcome);
        ChargingStationAllocationHoldRow hold = Assert.Single(await HoldsAsync(fleet));
        ChargingCycleRow cycle = await OpenCycleAsync(fleet);
        Assert.Equal(
            (ChargingStationHoldTriggers.UnableToChargeConfirmed, ChargingHoldRootCauses.Unknown, Map, Near.StationId, KeyA, cycle.CycleId),
            (hold.Trigger, hold.RootCause, hold.MapId, hold.StationId, hold.VehicleKey, hold.CycleId));
        Assert.Equal(
            (JourneyPlanBuilder.StableGuid(cycle.CycleId, "unable-to-charge-hold"),
                JourneyPlanBuilder.StableGuid(ChargingStationHoldTriggers.UnableToChargeConfirmed + "|" + cycle.CycleId, "charging-station-hold")),
            (hold.HoldId, hold.IdempotencyKey));
        Assert.Equal(("op-r11", "R-11", verifiedAt, "CHARGER_FAULT"),
            (hold.ConfirmedByPersonId, hold.ConfirmedByRole, hold.ConfirmedAuthenticatedAt!.Value, hold.SiteDisposition));
        Assert.StartsWith(UnableToChargeFieldConfirmations.EvidencePrefix, hold.EvidenceReference, StringComparison.Ordinal);
        Assert.NotNull(hold.ReservationRecordId);
        Assert.NotNull(hold.RawPositionJson);
        Assert.Equal((ChargingCycleWireStates.UnableToCharge, ChargingCyclePhases.Clearing), (cycle.WireState, cycle.Phase));
        Assert.Equal((VehiclePurposes.ClearingMaintenance, journey.JourneyId), (await ClaimOfAsync(fleet, KeyA))!.Value);
        Assert.Null((await fleet.Context.Set<StationClearanceRow>().AsNoTracking().SingleAsync(Token)).CompletedAt);
        Assert.Equal(ChargingExecutionReasons.UnableToChargeClearing, (await ChargingJourneyAsync(fleet, AgvA))!.BlockReasonCode);

        await RoundAsync(fleet);
        await RoundAsync(fleet);

        JsonElement state = (await PayloadsAsync(fleet, AgvA, "VehicleBusinessStateSnapshot"))[^1];
        Assert.Equal(
            (ChargingCycleWireStates.UnableToCharge, VehicleActivePurposes.ClearingMaintenance),
            (state.GetProperty("chargingCycleState").GetString(), state.GetProperty("activePurpose").GetString()));
        Assert.Contains(fleet.Peer.Delivered, line => line.MessageId == ChargingJourneyShape.ClearingStateMessageId(journey.JourneyId));
        Assert.Single(await HoldsAsync(fleet));
        Assert.Equal(creates, fleet.Riot.Creates.Count);
        Assert.Empty(fleet.Riot.OrderCommands);
        Assert.Empty(fleet.Riot.EmergencyCommands);
        Assert.Equal((KeyA, StationExclusivityStates.Reserved), await HolderAsync(fleet, Near.StationId));
        Assert.DoesNotContain(fleet.EngineLog.Entries, entry => entry.EventId.Id == 2264);

        fleet.Riot.CancelOrder(journey.PickupUpperId);
        fleet.Riot.VehicleOverrides[KeyA] = seen => seen with { CurrentStationId = 300, BatteryState = "NO_CHARGE" };
        ManualStationClearanceConfirmation cleared = await StationClearance(fleet)
            .DecideAsync(ClearanceRequest("00000000-0000-4000-8000-000000000d06"), Token);
        Assert.True(cleared.StationReleased);
        await RoundAsync(fleet);
        Assert.Equal((JourneyRuntimeStage.Completed, ChargingExecutionReasons.UnableToChargeCleared),
            ((await fleet.Context.JourneyRuntimes.AsNoTracking().SingleAsync(row => row.JourneyId == journey.JourneyId, Token)).Stage,
                (await fleet.Context.JourneyRuntimes.AsNoTracking().SingleAsync(row => row.JourneyId == journey.JourneyId, Token)).BlockReasonCode));
        Assert.Single(await HoldsAsync(fleet));
    }

    /// <summary>
    /// 清桩中的形状与系统确认逐列相同（cs#409 接的正是这个状态，调度 10-02 对齐）：周期、清桩记录、用途与它的经过、旅程的码、暂停事件里系统与现场共有的那些列。
    /// 只有确认人、角色、认证时刻、现场处置、证据与原始读数这几列是现场确认另记的。
    /// </summary>
    [Fact]
    public async Task TheClearingStateAFieldConfirmationWritesIsTheOneTheSystemWritesColumnForColumn()
    {
        string system;
        await using (FleetFixture fleet = await FleetAsync())
        {
            await ConfirmedAsync(fleet);
            system = await ClearingShapeAsync(fleet);
        }

        string field;
        await using (FleetFixture fleet = await FleetAsync())
        {
            await UnconfirmedAtChargerAsync(fleet);
            Assert.Equal(FieldConfirmationDecision.Confirmed, (await Decider(fleet)
                .DecideAsync(Request("00000000-0000-4000-8000-000000000d21"), Token)).Decision.Outcome);
            field = await ClearingShapeAsync(fleet);
        }

        Assert.Equal(system, field);
    }

    /// <summary>
    /// 系统已自动确认了这一次充不上，现场确认又到：不写第二条事件（那一条仍是系统的，没有确认人），答 <c>CONFIRMED</c>。第二个确认号（车载端重启后再按）
    /// 拿到与第一个相同的 <c>chargingPolicyDecision</c>，即使此刻的事实已经变了（电量回升），也不再置一次人工充电等待。
    /// </summary>
    [Fact]
    public async Task AFieldConfirmationAfterTheSystemOneWritesNoSecondEventAndKeepsTheDecision()
    {
        await using FleetFixture fleet = await FleetAsync();
        await ConfirmedAsync(fleet);
        ChargingStationAllocationHoldRow system = Assert.Single(await HoldsAsync(fleet));

        UnableToChargeFieldConfirmation first = await Decider(fleet)
            .DecideAsync(Request("00000000-0000-4000-8000-000000000d07"), Token);
        fleet.Riot.BatteryByVehicle[KeyA] = 60;
        await new ManualChargingHoldStore(fleet.Context).ReleaseAsync(KeyA, "REQ-RTS-1", fleet.Clock.GetUtcNow(), Token);
        UnableToChargeFieldConfirmation second = await Decider(fleet)
            .DecideAsync(Request("00000000-0000-4000-8000-000000000d08"), Token);

        Assert.Equal((FieldConfirmationDecision.Confirmed, ChargingPolicyDecisions.ManualChargingHold),
            (first.Decision.Outcome, first.ChargingPolicyDecision));
        Assert.Equal((FieldConfirmationDecision.Confirmed, first.ChargingPolicyDecision),
            (second.Decision.Outcome, second.ChargingPolicyDecision));
        ChargingStationAllocationHoldRow only = Assert.Single(await HoldsAsync(fleet));
        Assert.Equal((system.HoldId, (string?)null), (only.HoldId, only.ConfirmedByPersonId));
        Assert.Null(await new ManualChargingHoldStore(fleet.Context).ReadAsync(KeyA, Token));
    }

    public static TheoryData<string> ReadingsNotOnTheCharger => ["stale-thirty-minutes", "fresh-but-moved-off"];

    /// <summary>
    /// 审查 S2：系统已确认、本周期还没有现场确认，而此刻的读数不新鲜（30 分钟前）或车已不在桩上：仍答 <c>CONFIRMED</c>（这一次充不上已经确认过），
    /// 但不拿这份读数的电量（20）去置人工充电等待——给 <c>RETRY_LATER</c>，没有等待。
    /// </summary>
    [Theory]
    [MemberData(nameof(ReadingsNotOnTheCharger))]
    public async Task AfterTheSystemOneAReadingNotOnTheChargerPlacesNoManualHold(string reading)
    {
        await using FleetFixture fleet = await FleetAsync();
        await ConfirmedAsync(fleet);
        fleet.Riot.BatteryByVehicle[KeyA] = 20;
        DateTimeOffset old = fleet.Clock.GetUtcNow() - TimeSpan.FromMinutes(30);
        fleet.Riot.VehicleOverrides[KeyA] = reading == "stale-thirty-minutes"
            ? seen => seen with { CurrentStationId = Near.StationId, BatteryState = "NO_CHARGE", ObservedAt = old }
            : seen => seen with { CurrentStationId = 300, BatteryState = "NO_CHARGE" };

        UnableToChargeFieldConfirmation decision = await Decider(fleet)
            .DecideAsync(Request("00000000-0000-4000-8000-000000000d31"), Token);

        Assert.Equal((FieldConfirmationDecision.Confirmed, ChargingPolicyDecisions.RetryLater),
            (decision.Decision.Outcome, decision.ChargingPolicyDecision));
        Assert.Null(await new ManualChargingHoldStore(fleet.Context).ReadAsync(KeyA, Token));
        Assert.Single(await HoldsAsync(fleet));
    }

    // ---- 并发读改写 --------------------------------------------------------------------------------------------------------

    /// <summary>
    /// 现场确认与系统自动确认在同一轮：现场确认读完车与库、正读旧单的那一刻，引擎（另一个上下文的那一轮）按严格事实形成了确认。现场确认的周期按旧版本改写到 0 行，
    /// 整个尝试回滚、重判，读到系统那一条，答 <c>CONFIRMED</c>。只一条暂停事件（系统的）、一个决定。
    /// </summary>
    [Fact]
    public async Task AFieldConfirmationRacingTheSystemOneMakesOneEventAndOneDecision()
    {
        await using FleetFixture fleet = await FleetAsync();
        await FailingAtChargerAsync(fleet);
        await RoundAsync(fleet);
        Assert.Empty(await HoldsAsync(fleet));

        bool raced = false;
        InterleavingRiot riot = new(fleet.Riot, async () =>
        {
            if (raced) return;
            raced = true;
            await RoundAsync(fleet);
            Assert.Single(await HoldsAsync(fleet));
        });
        await using ControlServerDbContext own = fleet.NewContext();

        UnableToChargeFieldConfirmation decision = await Decider(fleet, own, riot)
            .DecideAsync(Request("00000000-0000-4000-8000-000000000d09"), Token);

        Assert.True(raced);
        Assert.Equal(FieldConfirmationDecision.Confirmed, decision.Decision.Outcome);
        Assert.NotNull(decision.ChargingPolicyDecision);
        ChargingStationAllocationHoldRow only = Assert.Single(await HoldsAsync(fleet));
        Assert.Null(only.ConfirmedByPersonId);
        Assert.Single(await fleet.Context.Set<UnableToChargeFieldConfirmationRow>().AsNoTracking().ToArrayAsync(Token));
        Assert.Single(await fleet.Context.Set<StationClearanceRow>().AsNoTracking().ToArrayAsync(Token));
    }

    /// <summary>
    /// 审查 S3：读周期与读用途之间（同一个判定里的两次读库），引擎那一轮按严格事实形成了确认。判定读到「周期还是 ACTIVE、用途已是 CLEARING_MAINTENANCE」，
    /// 前后不一，按这份事实会拒绝（用途不是这一趟的 <c>CHARGING</c>）；拒绝落盘之前再核周期版本，发现变了，整次重判，读到系统那一条，答 <c>CONFIRMED</c>。
    /// 只一条暂停事件（系统的）、一行判定。
    /// </summary>
    [Fact]
    public async Task AConfirmationFormedBetweenReadingTheCycleAndTheClaimIsJudgedAgain()
    {
        await using FleetFixture fleet = await FleetAsync();
        await FailingAtChargerAsync(fleet);
        await RoundAsync(fleet);
        Assert.Empty(await HoldsAsync(fleet));

        BeforeFirstCommand interleave = new("\"VehiclePurposeClaims\"", async () =>
        {
            await RoundAsync(fleet);
            Assert.Single(await HoldsAsync(fleet));
        });
        await using ControlServerDbContext own = new(new DbContextOptionsBuilder<ControlServerDbContext>()
            .UseSqlite(fleet.Context.Database.GetDbConnection()).AddInterceptors(interleave).Options);

        UnableToChargeFieldConfirmation decision = await Decider(fleet, own)
            .DecideAsync(Request("00000000-0000-4000-8000-000000000d32"), Token);

        Assert.True(interleave.Fired);
        Assert.Equal(FieldConfirmationDecision.Confirmed, decision.Decision.Outcome);
        ChargingStationAllocationHoldRow only = Assert.Single(await HoldsAsync(fleet));
        Assert.Null(only.ConfirmedByPersonId);
        Assert.Single(await fleet.Context.Set<UnableToChargeFieldConfirmationRow>().AsNoTracking().ToArrayAsync(Token));
    }

    // ---- 重放、重连、内容冲突、崩溃点（经真实的消息处理） ----------------------------------------------------------------------------

    /// <summary>
    /// <c>Result</c> 发出后断线、重连：同一行（同一个 <c>messageId</c>）在新会话里重发，拿回逐字节相同的那一行——同一个 <c>Result</c> 的 <c>messageId</c>；
    /// 换了 <c>messageId</c> 与会话代次、载荷不变的重提，拿回相同的载荷。都不重判、不写第二条事件。同一个确认号、不同载荷：按内容冲突拒收。
    /// </summary>
    [Fact]
    public async Task AReplayGetsTheSameResultAndOtherContentIsAConflict()
    {
        await using FleetFixture fleet = await FleetAsync();
        await UnconfirmedAtChargerAsync(fleet);
        (OnboardMessageProcessor processor, OnboardConnectionState state) = await ProcessorAsync(fleet);
        const string id = "00000000-0000-4000-8000-000000000d10";
        string line = RequestLine(state, id, "00000000-0000-4000-8000-000000000d11", "op-r11", Near.StationName, "CONNECTION_FAILED");

        string first = await processor.ProcessAsync(line, state, Token);
        fleet.Riot.BatteryByVehicle[KeyA] = 60;

        (OnboardMessageProcessor reconnected, OnboardConnectionState newSession) = await ProcessorAsync(fleet);
        string replayed = await reconnected.ProcessAsync(line, newSession, Token);
        Assert.Equal(FirstLine(first), FirstLine(replayed));

        string resubmitted = await reconnected.ProcessAsync(
            RequestLine(newSession, id, "00000000-0000-4000-8000-000000000d12", "op-r11", Near.StationName, "CONNECTION_FAILED"),
            newSession, Token);
        Assert.Equal(PayloadOf(first).GetRawText(), PayloadOf(resubmitted).GetRawText());
        Assert.Single(await HoldsAsync(fleet));
        Assert.Single(await fleet.Context.Set<UnableToChargeFieldConfirmationRow>().AsNoTracking().ToArrayAsync(Token));

        // control-server#478: refused with a ProtocolProblem on a connection that stays, no longer an exception.
        string conflicting =
            RequestLine(newSession, id, "00000000-0000-4000-8000-000000000d13", "op-r11", Near.StationName, "CHARGER_FAULT");
        ProtocolProblemAssert.RefusedLine(
            await reconnected.ProcessAsync(conflicting, newSession, Token), "BUSINESS_ID_CONTENT_CONFLICT", conflicting);
    }

    /// <summary>
    /// 「写确认记录与暂停事件」与「发结果」之间崩溃：判定与暂停事件已经落盘、结果在收件箱里。重启后（新上下文、新处理器）同一行再来，结果照已落盘的发出，
    /// 不重新判一次、不写第二条——即使此刻的事实已经变了（车被挪开）。在落盘之前崩溃（写确认请求表那一刻）：整笔回滚，什么都没有；同一行再来照常判成。
    /// </summary>
    [Fact]
    public async Task ACrashAfterTheDecisionSendsTheStoredResultAndACrashBeforeItLeavesNothing()
    {
        FailOnce crash = new("INSERT INTO \"UnableToChargeFieldConfirmations\"");
        await using FleetFixture fleet = await FleetAsync(commands: crash);
        await UnconfirmedAtChargerAsync(fleet);
        (OnboardMessageProcessor processor, OnboardConnectionState state) = await ProcessorAsync(fleet);
        string line = RequestLine(
            state, "00000000-0000-4000-8000-000000000d14", "00000000-0000-4000-8000-000000000d15", "op-r11", Near.StationName,
            "CONNECTION_FAILED");

        crash.Armed = true;
        await Assert.ThrowsAnyAsync<Exception>(() => processor.ProcessAsync(line, state, Token));
        Assert.Equal(1, crash.Fired);
        fleet.Context.ChangeTracker.Clear();
        Assert.Empty(await HoldsAsync(fleet));
        Assert.Empty(await fleet.Context.Set<UnableToChargeFieldConfirmationRow>().AsNoTracking().ToArrayAsync(Token));
        Assert.Equal(ChargingCyclePhases.Active, (await OpenCycleAsync(fleet)).Phase);

        string decided = await processor.ProcessAsync(line, state, Token);
        Assert.Equal("CONFIRMED", PayloadOf(decided).GetProperty("outcome").GetString());

        // The send never happened; the server restarts.
        fleet.Riot.VehicleOverrides[KeyA] = seen => seen with { CurrentStationId = 300, BatteryState = "NO_CHARGE" };
        await using ControlServerDbContext restarted = fleet.NewContext();
        (OnboardMessageProcessor afterRestart, OnboardConnectionState session) = await ProcessorAsync(fleet, restarted);
        string resent = await afterRestart.ProcessAsync(line, session, Token);

        Assert.Equal(FirstLine(decided), FirstLine(resent));
        Assert.Single(await HoldsAsync(fleet));
        Assert.Single(await fleet.Context.Set<UnableToChargeFieldConfirmationRow>().AsNoTracking().ToArrayAsync(Token));
    }

    // ---- CV-UNABLE-TO-CHARGE-FIELD-CONFIRMATION（经真实的消息处理与存储）------------------------------------------------------

    /// <summary>
    /// <c>DECIDE_CHARGING_POLICY_CENTRALLY</c>：车载端发 <c>UnableToChargeFieldConfirmationRequested</c>，服务端经入站处理器与存储回
    /// <c>UnableToChargeFieldConfirmationResult</c>（<c>CONFIRMED</c>、<c>problem</c> 为空），<c>chargingPolicyDecision</c> 由服务端给出：同一份事实、车载端报的
    /// 情形不同（<c>CONNECTION_FAILED</c> 与 <c>CHARGER_FAULT</c>）决定相同；事实变了（名册多一个桩）决定跟着变。下一轮发 <c>VehicleBusinessStateSnapshot</c>。
    /// </summary>
    [Fact]
    [Trait("ProtocolVector", Vector)]
    public async Task CvUnableToChargeFieldConfirmationDecidesTheChargingPolicyCentrally()
    {
        List<string> decisions = [];
        foreach ((ChargerRosterEntry[] chargers, string condition) in new[]
                 {
                     (new[] { Near }, "CONNECTION_FAILED"),
                     (new[] { Near }, "CHARGER_FAULT"),
                     (new[] { Near, Far }, "CONNECTION_FAILED"),
                 })
        {
            await using FleetFixture fleet = await FleetAsync(chargers: chargers);
            JourneyRuntimeRow journey = await UnconfirmedAtChargerAsync(fleet);
            (OnboardMessageProcessor processor, OnboardConnectionState state) = await ProcessorAsync(fleet);
            const string id = "00000000-0000-4000-8000-000000000e01";

            string response = await processor.ProcessAsync(
                RequestLine(state, id, "00000000-0000-4000-8000-000000000e02", "op-r11", Near.StationName, condition), state, Token);

            using JsonDocument envelope = JsonDocument.Parse(FirstLine(response));
            Assert.Equal("UnableToChargeFieldConfirmationResult", envelope.RootElement.GetProperty("messageType").GetString());
            Assert.Equal("00000000-0000-4000-8000-000000000e02", envelope.RootElement.GetProperty("correlationId").GetString());
            JsonElement result = envelope.RootElement.GetProperty("payload");
            Assert.Equal((id, "CONFIRMED", JsonValueKind.Null),
                (result.GetProperty("confirmationRequestId").GetString(), result.GetProperty("outcome").GetString(),
                    result.GetProperty("problem").ValueKind));
            decisions.Add(result.GetProperty("chargingPolicyDecision").GetString()!);

            await RoundAsync(fleet);
            Assert.Contains(fleet.Peer.Delivered, line => line.MessageId == ChargingJourneyShape.ClearingStateMessageId(journey.JourneyId));
        }

        Assert.Equal(
            [ChargingPolicyDecisions.ManualChargingHold, ChargingPolicyDecisions.ManualChargingHold, ChargingPolicyDecisions.ReassignCharger],
            decisions);
    }

    /// <summary>
    /// <c>RECORD_FIELD_OBSERVATION</c>：普通操作员报告、或报的不是充不上（<c>CHARGER_OCCUPIED</c>），服务端回 <c>REJECTED</c>（协议登记过的原因码、
    /// <c>chargingPolicyDecision</c> 为空），而这次报告照样被记下：确认请求表一行（谁、什么情形、何时）、管理员审计一条。桩不暂停。
    /// </summary>
    [Theory]
    [InlineData("op-plain", "CONNECTION_FAILED", UnableToChargeFieldConfirmations.NotAuthorized)]
    [InlineData("op-r11", "CHARGER_OCCUPIED", UnableToChargeFieldConfirmations.NotAllowedInState)]
    [Trait("ProtocolVector", Vector)]
    public async Task CvUnableToChargeFieldConfirmationRecordsTheFieldObservationOfARejectedReport(
        string operatorId, string condition, string reasonCode)
    {
        await using FleetFixture fleet = await FleetAsync();
        await UnconfirmedAtChargerAsync(fleet);
        (OnboardMessageProcessor processor, OnboardConnectionState state) = await ProcessorAsync(fleet);
        const string id = "00000000-0000-4000-8000-000000000e03";

        JsonElement result = PayloadOf(await processor.ProcessAsync(
            RequestLine(state, id, "00000000-0000-4000-8000-000000000e04", operatorId, Near.StationName, condition), state, Token));

        Assert.Equal(("REJECTED", reasonCode, JsonValueKind.Null),
            (result.GetProperty("outcome").GetString(), result.GetProperty("problem").GetProperty("reasonCode").GetString(),
                result.GetProperty("chargingPolicyDecision").ValueKind));
        UnableToChargeFieldConfirmationRow recorded = await fleet.Context.Set<UnableToChargeFieldConfirmationRow>().AsNoTracking()
            .SingleAsync(Token);
        Assert.Equal((id, operatorId, condition, Near.StationName, FieldConfirmationDecision.Rejected),
            (recorded.ConfirmationRequestId, recorded.OperatorId, recorded.ObservedCondition, recorded.ChargerStationId, recorded.Outcome));
        Assert.Single(await fleet.Context.Set<AdministratorAuditRecordRow>().AsNoTracking()
            .Where(row => row.Action == UnableToChargeFieldConfirmations.AuditAction).ToArrayAsync(Token));
        await AssertNothingPausedAsync(fleet);
    }

    // ---- 夹具 ------------------------------------------------------------------------------------------------------------

    /// <summary>A 在桩上、单挂起，开始充电的动作结果码不是验证过的那一个：引擎两轮都不形成确认（系统事实不完整）。</summary>
    private static async Task<JourneyRuntimeRow> UnconfirmedAtChargerAsync(FleetFixture fleet)
    {
        JourneyRuntimeRow journey = await FailingAtChargerAsync(fleet, resultCode: 1);
        await RoundAsync(fleet);
        await RoundAsync(fleet);
        Assert.Empty(await HoldsAsync(fleet));
        Assert.Equal(ChargingCyclePhases.Active, (await OpenCycleAsync(fleet)).Phase);
        return journey;
    }

    /// <summary>清桩中那一刻写下的行，按列写成一行文字；以周期为基准的 id 换成「它们是不是按周期推出来的」，别的 id 只看有没有。</summary>
    private static async Task<string> ClearingShapeAsync(FleetFixture fleet)
    {
        ChargingCycleRow cycle = await OpenCycleAsync(fleet);
        StationClearanceRow clearance = await fleet.Context.Set<StationClearanceRow>().AsNoTracking().SingleAsync(Token);
        ChargingStationAllocationHoldRow hold = Assert.Single(await HoldsAsync(fleet));
        JourneyRuntimeRow journey = await fleet.Context.JourneyRuntimes.AsNoTracking()
            .SingleAsync(row => row.JourneyId == cycle.JourneyId, Token);
        (string Purpose, string JourneyId) claim = (await ClaimOfAsync(fleet, KeyA))!.Value;
        string[] records =
        [
            .. (await fleet.Context.Set<VehiclePurposeClaimRecordRow>().AsNoTracking()
                    .Where(row => row.VehicleKey == KeyA && row.JourneyId == cycle.JourneyId).ToArrayAsync(Token))
                .Select(row => $"{row.Purpose}:{row.ReleaseReason ?? "open"}:{row.ReleasedAt is null}")
                .Order(StringComparer.Ordinal),
        ];
        return string.Join(" | ",
            $"cycle {cycle.WireState} {cycle.Phase} {cycle.EndReason ?? "-"} {cycle.EndedAt is null} {cycle.FirstChargingSeenAt is null}",
            $"clearance {clearance.ClearanceId == JourneyPlanBuilder.StableGuid(cycle.CycleId, "station-clearance")} {clearance.CycleId == cycle.CycleId} " +
            $"{clearance.VehicleKey} {clearance.MapId}/{clearance.StationId} {clearance.CompletedAt is null} {clearance.ConfirmedAt is null} " +
            $"{clearance.Proof ?? "-"} {clearance.AssistantsJson}",
            $"claim {claim.Purpose} {claim.JourneyId == cycle.JourneyId} [{string.Join(",", records)}]",
            $"journey {journey.Stage} {journey.BlockReasonCode}",
            $"hold {hold.HoldId == JourneyPlanBuilder.StableGuid(cycle.CycleId, "unable-to-charge-hold")} " +
            $"{hold.IdempotencyKey == JourneyPlanBuilder.StableGuid(ChargingStationHoldTriggers.UnableToChargeConfirmed + "|" + cycle.CycleId, "charging-station-hold")} " +
            $"{hold.Trigger} {hold.RootCause} {hold.MapId}/{hold.StationId} {hold.ChargerRosterVersion == cycle.ChargerRosterVersion} " +
            $"{hold.VehicleKey} {hold.CycleId == cycle.CycleId} {hold.UpperId == journey.PickupUpperId} {hold.OrderId} " +
            $"{hold.ReservationRecordId is not null} {hold.ChargingStartedAt is null} {hold.FailedAt is not null} {hold.FinalHangAt is not null} " +
            $"{hold.ConfirmedAt is not null} {hold.HeldAt == hold.ConfirmedAt}");
    }

    private static async Task AssertNothingPausedAsync(FleetFixture fleet)
    {
        Assert.Empty(await HoldsAsync(fleet));
        ChargingCycleRow cycle = await OpenCycleAsync(fleet);
        Assert.Equal(ChargingCyclePhases.Active, cycle.Phase);
        Assert.NotEqual(ChargingCycleWireStates.UnableToCharge, cycle.WireState);
        Assert.Equal(VehiclePurposes.Charging, (await ClaimOfAsync(fleet, KeyA))!.Value.Purpose);
        Assert.Null(await new ManualChargingHoldStore(fleet.Context).ReadAsync(KeyA, Token));
    }

    private static async Task SetCycleAsync(
        FleetFixture fleet, string? wireState = null, string? phase = null, DateTimeOffset? firstChargingSeenAt = null)
    {
        ChargingCycleRow cycle = await fleet.Context.Set<ChargingCycleRow>()
            .SingleAsync(row => row.VehicleKey == KeyA && row.Phase != ChargingCyclePhases.Ended, Token);
        cycle.WireState = wireState ?? cycle.WireState;
        cycle.Phase = phase ?? cycle.Phase;
        cycle.FirstChargingSeenAt = firstChargingSeenAt ?? cycle.FirstChargingSeenAt;
        cycle.Version++;
        await fleet.Context.SaveChangesAsync(Token);
        fleet.Context.ChangeTracker.Clear();
    }

    private static ChargingStationAllocationHold MaintenanceHold(int stationId, DateTimeOffset at) => new(
        "MAINT-" + stationId, "maint-" + stationId, ChargingStationHoldTriggers.Maintenance, Map, stationId, null, null, null, null,
        null, null, null, null, null, null, null, at, null, null, null, null, null, null, null, null, "维护管理员", null, "检修");

    private UnableToChargeFieldConfirmations Decider(
        FleetFixture fleet,
        ControlServerDbContext? context = null,
        IRiotVehicleFacts? riot = null,
        bool exitDeclared = true,
        EventRecordingLogger<UnableToChargeFieldConfirmations>? logger = null)
    {
        ControlServerDbContext db = context ?? fleet.Context;
        FieldOperatorRoleOptions roles = new() { Path = _roster, OnboardClearanceEntryDeclared = exitDeclared };
        FieldOperatorRoleRoster roster = new(Options.Create(roles));
        return new UnableToChargeFieldConfirmations(
            db,
            new FieldConfirmationRequestStore(db),
            riot ?? fleet.Riot,
            roster,
            new ChargerRosterStore(db, new GovernedConfigurationPublisher(Audit(db), Audit(db))),
            new ChargingHoldStore(db),
            new ManualChargingHoldStore(db),
            fleet.ChargingPolicy,
            Audit(db),
            Options.Create(fleet.Options),
            fleet.Clock,
            (Microsoft.Extensions.Logging.ILogger<UnableToChargeFieldConfirmations>?)logger
                ?? NullLogger<UnableToChargeFieldConfirmations>.Instance,
            new StationClearanceExit(
                roster,
                Options.Create(roles),
                new ConfigurationBuilder().Build(),
                Options.Create(new VehicleFaultRecoveryOptions { CredentialEnvironmentVariable = "W2G_TEST_CS410_UNSET" })));
    }

    private ManualStationClearance StationClearance(FleetFixture fleet) => new(
        fleet.Context,
        new FieldConfirmationRequestStore(fleet.Context),
        new StationClearanceStore(fleet.Context),
        fleet.Riot,
        new FieldOperatorRoleRoster(Options.Create(new FieldOperatorRoleOptions { Path = _roster })),
        Audit(fleet.Context),
        Options.Create(fleet.Options),
        fleet.Clock,
        NullLogger<ManualStationClearance>.Instance);

    private static ManualStationClearanceRequest ClearanceRequest(string confirmationRequestId) => new(
        ManualStationClearanceSources.Onboard, AgvA, KeyA, confirmationRequestId, 1, Guid.NewGuid().ToString("D"),
        "clearance|" + confirmationRequestId, Near.StationName, null, "STATION_EMPTY", "op-r11", "BADGE",
        DateTimeOffset.Parse("2026-09-08T06:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
        DateTimeOffset.Parse("2026-09-08T06:00:00Z", System.Globalization.CultureInfo.InvariantCulture));

    private static GovernanceStore Audit(ControlServerDbContext context) =>
        new(context, new GovernanceDeploymentIdentity("deployment:8005-controlserver@test"), AuditRetentionPolicy.Default);

    private static UnableToChargeFieldConfirmationRequest Request(
        string confirmationRequestId,
        string operatorId = "op-r11",
        string? station = null,
        string condition = "CONNECTION_FAILED")
    {
        station ??= Near.StationName;
        return new UnableToChargeFieldConfirmationRequest(
            AgvA, KeyA, confirmationRequestId, 1, Guid.NewGuid().ToString("D"), $"{station}|{condition}|{operatorId}", station,
            condition, operatorId, "BADGE",
            DateTimeOffset.Parse("2026-09-08T05:59:00Z", System.Globalization.CultureInfo.InvariantCulture),
            DateTimeOffset.Parse("2026-09-08T06:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
    }

    private async Task<(OnboardMessageProcessor Processor, OnboardConnectionState State)> ProcessorAsync(
        FleetFixture fleet, ControlServerDbContext? context = null)
    {
        ControlServerDbContext db = context ?? fleet.Context;
        long generation = await db.SessionRecoveries.AsNoTracking()
            .Where(row => row.AgvId == AgvA).Select(row => row.SessionGeneration).SingleAsync(Token);
        OnboardMessageProcessor processor = TestOnboardProcessorFactory.Create(
            db, new WireToGateStore(db), fleet.Clock, new ConfigurationBuilder().Build(),
            runtimeOptions: fleet.Options, fieldConfirmations: Decider(fleet, db));
        OnboardConnectionState state = new()
        {
            AgvId = AgvA,
            SessionGeneration = generation,
            HandshakeCompleted = true,
            Readiness = SessionReadiness.Ready,
        };
        return (processor, state);
    }

    /// <summary>车载端发一次现场确认（hmi#222 的形状：操作员号、<c>SESSION</c>），载荷逐字节由参数决定。</summary>
    private static string RequestLine(
        OnboardConnectionState state, string confirmationRequestId, string messageId, string operatorId, string chargerStationId,
        string observedCondition) =>
        JsonSerializer.Serialize(new
        {
            protocolVersion = ProtocolCandidateIdentity.ProtocolVersion,
            profileId = ProtocolCandidateIdentity.ProfileId,
            protocolReleaseVersion = ProtocolCandidateIdentity.ReleaseVersion,
            protocolReleaseManifestSha256 = ProtocolCandidateIdentity.ManifestSha256,
            messageType = "UnableToChargeFieldConfirmationRequested",
            messageId,
            correlationId = (string?)null,
            agvId = state.AgvId,
            sessionGeneration = state.SessionGeneration,
            sentAt = "2026-09-08T06:00:00Z",
            payload = new
            {
                confirmationRequestId,
                chargerStationId,
                observedCondition,
                @operator = new { operatorId, verificationMethod = "SESSION", verifiedAt = "2026-09-08T05:59:00Z" },
                observedAt = "2026-09-08T06:00:00Z",
            },
        });

    private static string FirstLine(string response) => response.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0];

    private static JsonElement PayloadOf(string response)
    {
        using JsonDocument document = JsonDocument.Parse(FirstLine(response));
        Assert.Equal("UnableToChargeFieldConfirmationResult", document.RootElement.GetProperty("messageType").GetString());
        return document.RootElement.GetProperty("payload").Clone();
    }

    /// <summary>
    /// 读旧单那一刻先让另一件事做完（并发交错的那一刻）：判定此时已读完车与库，还没开始写。
    /// </summary>
    private sealed class InterleavingRiot(IRiotVehicleFacts inner, Func<Task> meanwhile) : IRiotVehicleFacts
    {
        public Task<RiotVehicleObservation> ReadVehicleAsync(string vehicleKey, CancellationToken cancellationToken) =>
            inner.ReadVehicleAsync(vehicleKey, cancellationToken);

        public async Task<RiotOrderObservation> ReconcileByUpperIdAsync(string upperId, CancellationToken cancellationToken)
        {
            await meanwhile();
            return await inner.ReconcileByUpperIdAsync(upperId, cancellationToken);
        }

        public Task<RiotOrderObservation> CreateAsync(OrderIntent intent, CancellationToken cancellationToken) =>
            inner.CreateAsync(intent, cancellationToken);
    }

    /// <summary>车的电量第一次读是 <paramref name="first"/>，之后每次都是 <paramref name="later"/>；记下读了几次。</summary>
    private sealed class BatteryThenRisingRiot(MultiVehicleExecutionTests.FleetRiot inner, int first, int later) : IRiotVehicleFacts
    {
        public int VehicleReads { get; private set; }

        public async Task<RiotVehicleObservation> ReadVehicleAsync(string vehicleKey, CancellationToken cancellationToken)
        {
            RiotVehicleObservation seen = await inner.ReadVehicleAsync(vehicleKey, cancellationToken);
            VehicleReads++;
            return seen with { BatteryPercent = VehicleReads == 1 ? first : later };
        }

        public Task<RiotOrderObservation> ReconcileByUpperIdAsync(string upperId, CancellationToken cancellationToken) =>
            inner.ReconcileByUpperIdAsync(upperId, cancellationToken);

        public Task<RiotOrderObservation> CreateAsync(OrderIntent intent, CancellationToken cancellationToken) =>
            inner.CreateAsync(intent, cancellationToken);
    }

    /// <summary>这个上下文第一次执行含 <paramref name="contains"/> 的查询之前，先让另一件事做完；只一次。</summary>
    private sealed class BeforeFirstCommand(string contains, Func<Task> meanwhile) : DbCommandInterceptor
    {
        public bool Fired { get; private set; }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (!Fired && command.CommandText.Contains(contains, StringComparison.Ordinal))
            {
                Fired = true;
                await meanwhile();
            }
            return await base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    /// <summary>RIoT 读不到旧单（网关报错）；读车照常。</summary>
    private sealed class UnreadableOrderRiot(IRiotVehicleFacts inner) : IRiotVehicleFacts
    {
        public Task<RiotVehicleObservation> ReadVehicleAsync(string vehicleKey, CancellationToken cancellationToken) =>
            inner.ReadVehicleAsync(vehicleKey, cancellationToken);

        public Task<RiotOrderObservation> ReconcileByUpperIdAsync(string upperId, CancellationToken cancellationToken) =>
            throw new HttpRequestException("RIoT gateway unavailable (test)");

        public Task<RiotOrderObservation> CreateAsync(OrderIntent intent, CancellationToken cancellationToken) =>
            inner.CreateAsync(intent, cancellationToken);
    }

    /// <summary>RIoT 读不到车（网关报错）。</summary>
    private sealed class UnreadableVehicleRiot(IRiotVehicleFacts inner) : IRiotVehicleFacts
    {
        public Task<RiotVehicleObservation> ReadVehicleAsync(string vehicleKey, CancellationToken cancellationToken) =>
            throw new HttpRequestException("RIoT gateway unavailable (test)");

        public Task<RiotOrderObservation> ReconcileByUpperIdAsync(string upperId, CancellationToken cancellationToken) =>
            inner.ReconcileByUpperIdAsync(upperId, cancellationToken);

        public Task<RiotOrderObservation> CreateAsync(OrderIntent intent, CancellationToken cancellationToken) =>
            inner.CreateAsync(intent, cancellationToken);
    }
}
