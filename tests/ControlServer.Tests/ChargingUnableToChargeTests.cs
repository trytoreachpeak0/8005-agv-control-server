using System.Data.Common;
using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Charging;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using static ControlServer.Tests.ChargingAllocationTests;
using static ControlServer.Tests.ChargingExecutionTests;
using FleetFixture = ControlServer.Tests.MultiVehicleExecutionTests.FleetFixture;

namespace ControlServer.Tests;

/// <summary>
/// 充不上（批次9-08，control-server#406）：严格事实自动形成「已确认充不上」（<c>REQ-0174</c>）、一般异常不暂停（<c>REQ-0175</c>）、
/// 不可变幂等的暂停事件（<c>REQ-0177</c>）、原桩重试为 0（<c>REQ-0284</c>）、清桩中车保持原位（用户 09-29 定）、桩上 <c>HANG</c> 不落进急停，
/// 以及「写暂停事件」那一刻的崩溃点、清桩中有人在 RIoT 取消旧单不重建。人工清桩在 <see cref="ManualStationClearanceTests"/>。
/// </summary>
/// <remarks>
/// 夹具同 <see cref="ChargingCycleProgressTests"/>：A 低电，第一轮承诺 211，第二轮建单并确认（<c>EN_ROUTE</c>）；之后由用例让合成 RIoT 把单挂起、
/// 在任务明细里给开始充电的动作写上结果码，并把车放在桩上。
/// </remarks>
[Trait("IntegrationSlice", "FP-IS-13")]
public sealed class ChargingUnableToChargeTests
{
    private const int Map = 25;
    private const string NotCharging = "NO_CHARGE";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string AgvA => FleetFixture.AgvIds[0];
    private static string KeyA => FleetFixture.VehicleKeys[0];

    // ---- 自动形成（REQ-0174）---------------------------------------------------------------------------------------------

    /// <summary>
    /// 全部严格事实在两次相隔不超过证据时效的新鲜观测里都成立：同一次保存里写下一条暂停事件（字段齐全、根因 <c>UNKNOWN</c>）、周期进清桩中
    /// （<c>UNABLE_TO_CHARGE</c>）、车的用途转为 <c>CLEARING_MAINTENANCE</c>、清桩记录开始、旅程写充不上的码；计划留那条 <c>CHARGER</c> 腿，
    /// 业务状态报 <c>UNABLE_TO_CHARGE</c> 与 <c>CLEARING_MAINTENANCE</c>；告警恰好一次。第一次观测只开始观察，那一轮照常写 <c>ORDER_HANG</c>。
    /// </summary>
    [Fact]
    public async Task StrictFactsSeenTwiceConfirmUnableToChargeAndPauseTheChargerInOneSave()
    {
        await using FleetFixture fleet = await FleetAsync();
        JourneyRuntimeRow journey = await FailingAtChargerAsync(fleet);

        await RoundAsync(fleet);
        Assert.Equal(JourneyRuntimeEngine.OrderHangReason, (await ChargingJourneyAsync(fleet, AgvA))!.BlockReasonCode);
        Assert.Empty(await HoldsAsync(fleet));

        await RoundAsync(fleet);

        ChargingStationAllocationHoldRow hold = Assert.Single(await HoldsAsync(fleet));
        ChargingCycleRow cycle = await OpenCycleAsync(fleet);
        Assert.Equal(
            (ChargingStationHoldTriggers.UnableToChargeConfirmed, ChargingHoldRootCauses.Unknown, Map, Near.StationId),
            (hold.Trigger, hold.RootCause, hold.MapId, hold.StationId));
        Assert.Equal(
            (KeyA, cycle.CycleId, journey.PickupUpperId, $"ORDER-{journey.PickupUpperId}", cycle.ChargerRosterVersion),
            (hold.VehicleKey, hold.CycleId, hold.UpperId, hold.OrderId, hold.ChargerRosterVersion));
        Assert.NotNull(hold.ReservationRecordId);
        Assert.NotNull(hold.ArrivedAt);
        Assert.NotNull(hold.FailedAt);
        Assert.NotNull(hold.FinalHangAt);
        Assert.NotNull(hold.ConfirmedAt);
        Assert.Contains("407802", hold.RawActionResultJson, StringComparison.Ordinal);
        Assert.Contains("resultCode=407802", hold.EvidenceReference, StringComparison.Ordinal);
        Assert.NotNull(hold.RawPositionJson);
        Assert.NotNull(hold.RawOrderJson);
        Assert.NotNull(hold.RawBatteryJson);
        Assert.Equal((UnableToChargeFacts.VerifiedRiotBuild, UnableToChargeFacts.VerifiedRiotContract), (hold.RiotBuild, hold.RiotContractVersion));
        Assert.Equal(
            JourneyPlanBuilder.StableGuid(ChargingStationHoldTriggers.UnableToChargeConfirmed + "|" + cycle.CycleId, "charging-station-hold"),
            hold.IdempotencyKey);

        Assert.Equal((ChargingCycleWireStates.UnableToCharge, ChargingCyclePhases.Clearing), (cycle.WireState, cycle.Phase));
        Assert.Equal((VehiclePurposes.ClearingMaintenance, journey.JourneyId), (await ClaimOfAsync(fleet, KeyA))!.Value);
        StationClearanceRow clearance = await fleet.Context.Set<StationClearanceRow>().AsNoTracking()
            .SingleAsync(row => row.CycleId == cycle.CycleId, Token);
        Assert.Null(clearance.CompletedAt);
        Assert.Equal(ChargingExecutionReasons.UnableToChargeClearing, (await ChargingJourneyAsync(fleet, AgvA))!.BlockReasonCode);
        // The CHARGING record is closed for the purpose change, a CLEARING_MAINTENANCE one opened, same journey.
        VehiclePurposeClaimRecordRow[] records = await fleet.Context.Set<VehiclePurposeClaimRecordRow>().AsNoTracking()
            .Where(row => row.VehicleKey == KeyA && row.JourneyId == journey.JourneyId).ToArrayAsync(Token);
        Assert.Contains(records, row => row.Purpose == VehiclePurposes.Charging && row.ReleaseReason == ChargingStationHoldTriggers.UnableToChargeConfirmed);
        Assert.Single(records, row => row.Purpose == VehiclePurposes.ClearingMaintenance && row.ReleasedAt == null);

        JsonElement plan = (await PayloadsAsync(fleet, AgvA, "UpcomingStopPlanSnapshot"))[^1];
        JsonElement leg = Assert.Single(plan.GetProperty("legs").EnumerateArray());
        Assert.Equal(
            (JourneyPlanBuilder.ChargerStopPurpose, Near.StationName),
            (leg.GetProperty("stopPurposeCategory").GetString(), leg.GetProperty("stationId").GetString()));
        JsonElement state = (await PayloadsAsync(fleet, AgvA, "VehicleBusinessStateSnapshot"))[^1];
        Assert.Equal(
            (ChargingCycleWireStates.UnableToCharge, VehicleActivePurposes.ClearingMaintenance),
            (state.GetProperty("chargingCycleState").GetString(), state.GetProperty("activePurpose").GetString()));
        Assert.Contains(fleet.Peer.Delivered, line => line.MessageId == ChargingJourneyShape.ClearingStateMessageId(journey.JourneyId));

        Assert.Single(fleet.EngineLog.Entries, entry => entry.EventId.Id == 2264);
        Assert.Single(fleet.Riot.Creates);
        Assert.Empty(fleet.Riot.OrderCommands);
        Assert.Empty(fleet.Riot.EmergencyCommands);
    }

    public static TheoryData<string> MissingStrictFacts =>
    [
        "identity-order-for-another-charger",
        "reservation-not-this-journey",
        "not-at-the-exact-station",
        "vehicle-still-moving",
        "no-start-charging-action",
        "a-code-other-than-407802",
        "no-code-at-all",
        "407802-but-not-hang",
        "charging-seen-once",
        "order-on-another-vehicle",
        "facts-stale",
        "facts-conflict",
    ];

    /// <summary>
    /// 条文里每一项事实各缺一条（调度 09-29：「HANG 加非 407802 的码」也在里面）：都不形成确认、不暂停桩、车的用途仍是 <c>CHARGING</c>；
    /// 单是 <c>HANG</c> 的，旅程照常写 <c>ORDER_HANG</c>。看十轮。
    /// </summary>
    [Theory]
    [MemberData(nameof(MissingStrictFacts))]
    public async Task AnyStrictFactMissingFormsNoConfirmationAndHoldsNoCharger(string missing)
    {
        await using FleetFixture fleet = await FleetAsync();
        JourneyRuntimeRow journey = await FailingAtChargerAsync(fleet, missing switch
        {
            "a-code-other-than-407802" => 1,
            "no-code-at-all" => null,
            _ => 407802,
        });
        string upperId = journey.PickupUpperId;
        switch (missing)
        {
            case "identity-order-for-another-charger":
                fleet.Riot.PutOrder(fleet.Riot.OrderOf(upperId)! with { DestinationStationId = Far.StationId });
                break;
            case "reservation-not-this-journey":
                await fleet.Context.Set<StationExclusivityRow>()
                    .Where(row => row.StationId == Near.StationId).ExecuteDeleteAsync(Token);
                break;
            case "not-at-the-exact-station":
                fleet.Riot.VehicleOverrides[KeyA] = seen => seen with { CurrentStationId = 300, BatteryState = NotCharging };
                break;
            case "vehicle-still-moving":
                fleet.Riot.VehicleOverrides[KeyA] = seen => seen with { CurrentStationId = Near.StationId, BatteryState = NotCharging, Speed = 200 };
                break;
            case "no-start-charging-action":
                fleet.Riot.MissionOverrides[upperId] = [new RiotOrderMissionFact("move", Map, Near.StationId, 0, 0, 0, null)];
                break;
            case "407802-but-not-hang":
                fleet.Riot.PutOrder(fleet.Riot.OrderOf(upperId)! with { OrderState = RiotOrderState.Executing });
                break;
            case "charging-seen-once":
                fleet.Riot.VehicleOverrides[KeyA] = seen => seen with { CurrentStationId = Near.StationId, BatteryState = "CHARGING" };
                await RoundAsync(fleet);
                // Review P1: CHARGING read once while the order hangs, then never again. Seen is seen for the whole cycle (REQ-0174),
                // so the ten rounds of NO_CHARGE below form nothing.
                fleet.Riot.VehicleOverrides[KeyA] = seen => seen with { CurrentStationId = Near.StationId, BatteryState = NotCharging };
                break;
            case "order-on-another-vehicle":
                // Review P4: RIoT reads the order executed by another vehicle; every other fact holds.
                fleet.Riot.PutOrder(fleet.Riot.OrderOf(upperId)! with { VehicleKey = "SOMEONE-ELSE" });
                break;
            case "facts-stale":
                fleet.Riot.VehicleOverrides[KeyA] = seen => seen with
                {
                    CurrentStationId = Near.StationId,
                    BatteryState = NotCharging,
                    ObservedAt = seen.ObservedAt - fleet.Options.MaximumEvidenceAge - TimeSpan.FromSeconds(1),
                };
                break;
            case "facts-conflict":
                fleet.Riot.MissionOrderStateOverride = RiotOrderState.Executing;
                break;
        }

        for (int round = 0; round < 10; round++)
        {
            await RoundAsync(fleet);
        }

        Assert.Empty(await HoldsAsync(fleet));
        ChargingCycleRow cycle = await OpenCycleAsync(fleet);
        Assert.Equal(ChargingCyclePhases.Active, cycle.Phase);
        Assert.NotEqual(ChargingCycleWireStates.UnableToCharge, cycle.WireState);
        Assert.Equal(VehiclePurposes.Charging, (await ClaimOfAsync(fleet, KeyA))!.Value.Purpose);
        Assert.DoesNotContain(fleet.EngineLog.Entries, entry => entry.EventId.Id == 2264);
        if (fleet.Riot.OrderOf(upperId)!.OrderState == RiotOrderState.Hang)
        {
            Assert.Equal(JourneyRuntimeEngine.OrderHangReason, (await ChargingJourneyAsync(fleet, AgvA))!.BlockReasonCode);
        }
    }

    /// <summary>
    /// 审查第 5 条：严格事实全都成立，但车的用途占有已不是这趟旅程的 <c>CHARGING</c>（这里被改成了别的用途）——用途转换写不成，就不形成确认：
    /// 不暂停桩、不进清桩中，十轮里一条暂停都没有。
    /// </summary>
    [Fact]
    public async Task AClaimThatIsNotThisJourneysChargingOneFormsNoConfirmation()
    {
        await using FleetFixture fleet = await FleetAsync();
        await FailingAtChargerAsync(fleet);
        await fleet.Context.Set<VehiclePurposeClaimRow>()
            .Where(row => row.VehicleKey == KeyA)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Purpose, VehiclePurposes.Transport), Token);

        for (int round = 0; round < 10; round++)
        {
            await RoundAsync(fleet);
        }

        Assert.Empty(await HoldsAsync(fleet));
        Assert.Equal(ChargingCyclePhases.Active, (await OpenCycleAsync(fleet)).Phase);
        Assert.DoesNotContain(fleet.EngineLog.Entries, entry => entry.EventId.Id == 2264);
    }

    public static TheoryData<string, string> UnavailableExits => new()
    {
        { "roster-empty", StationClearanceExit.RosterEmpty },
        { "roster-without-r11-or-r13", StationClearanceExit.RosterEmpty },
        { "no-entry", StationClearanceExit.NoEntry },
    };

    /// <summary>
    /// 审查必修 M1：严格事实全部成立，但人工清桩的出口此刻没人走得通（名单为空、名单里没人持 R-11／R-13、Host 入口没映射也没声明车载端入口）：
    /// 不形成确认、不暂停桩，单照旧是 <c>ORDER_HANG</c>（RIoT 那一侧的出口还在）；告警恰好一次（事件 2271），说出是哪一样。出口恢复之后，同一组事实照常
    /// 形成确认——挡住它的确实是出口。
    /// </summary>
    [Theory]
    [MemberData(nameof(UnavailableExits))]
    public async Task WithNoWayOutForAPersonTheHangStaysOrderHangAndNothingIsPaused(string exit, string reason)
    {
        await using FleetFixture fleet = await FleetAsync();
        string roster = File.ReadAllText(fleet.ClearanceRosterPath);
        switch (exit)
        {
            case "roster-empty":
                File.WriteAllText(fleet.ClearanceRosterPath, """{"operators":[]}""");
                break;
            case "roster-without-r11-or-r13":
                File.WriteAllText(fleet.ClearanceRosterPath, """{"operators":[{"operatorId":"op-plain","roles":["R-04"]}]}""");
                break;
            case "no-entry":
                fleet.ClearanceConfiguration["VehicleFaultRecovery:enabled"] = "false";
                break;
        }
        await FailingAtChargerAsync(fleet);

        for (int round = 0; round < 10; round++)
        {
            await RoundAsync(fleet);
        }

        Assert.Empty(await HoldsAsync(fleet));
        Assert.Equal(ChargingCyclePhases.Active, (await OpenCycleAsync(fleet)).Phase);
        Assert.Equal(VehiclePurposes.Charging, (await ClaimOfAsync(fleet, KeyA))!.Value.Purpose);
        Assert.Equal(JourneyRuntimeEngine.OrderHangReason, (await ChargingJourneyAsync(fleet, AgvA))!.BlockReasonCode);
        EventRecordingLogger<JourneyRuntimeEngine>.Entry warned = Assert.Single(fleet.EngineLog.Entries, entry => entry.EventId.Id == 2271);
        Assert.Contains(reason, warned.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(fleet.EngineLog.Entries, entry => entry.EventId.Id == 2264);

        File.WriteAllText(fleet.ClearanceRosterPath, roster);
        fleet.ClearanceConfiguration["VehicleFaultRecovery:enabled"] = "true";
        await RoundAsync(fleet);
        await RoundAsync(fleet);
        Assert.Single(await HoldsAsync(fleet));
    }

    /// <summary>
    /// 出口判定本身：名单与入口两样各自缺什么报什么；Host 入口没开时，部署方声明了车载端入口（<c>FieldOperatorRoles:OnboardClearanceEntryDeclared</c>）也算有入口。
    /// 启动时不可用告警一次（事件 2272）；没在跑旅程的服务端不看。
    /// </summary>
    [Theory]
    [InlineData(true, true, false, null)]
    [InlineData(true, false, true, null)]
    [InlineData(true, false, false, StationClearanceExit.NoEntry)]
    [InlineData(false, true, false, StationClearanceExit.RosterEmpty)]
    [InlineData(false, false, false, StationClearanceExit.RosterEmpty + "," + StationClearanceExit.NoEntry)]
    public void TheExitNeedsSomeoneOnTheRosterAndAnEntry(bool rosterHasR11, bool hostEntry, bool onboardDeclared, string? expected)
    {
        string path = Path.Combine(Path.GetTempPath(), $"exit-roster-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, rosterHasR11 ? """{"operators":[{"operatorId":"op-r11","roles":["R-11"]}]}""" : """{"operators":[]}""");
        try
        {
            FieldOperatorRoleOptions roles = new() { Path = path, OnboardClearanceEntryDeclared = onboardDeclared };
            Microsoft.Extensions.Configuration.IConfiguration configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["VehicleFaultRecovery:enabled"] = hostEntry ? "true" : "false" })
                .Build();
            StationClearanceExit exit = new(
                new FieldOperatorRoleRoster(Microsoft.Extensions.Options.Options.Create(roles)),
                Microsoft.Extensions.Options.Options.Create(roles),
                configuration);
            Assert.Equal(expected, exit.Unavailable());

            foreach (bool journeysRun in new[] { true, false })
            {
                EventRecordingLogger<StationClearanceExit> log = new();
                ServiceCollection services = new();
                services.AddSingleton(exit);
                services.AddSingleton<ILogger<StationClearanceExit>>(log);
                services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new JourneyRuntimeOptions { Enabled = journeysRun }));
                StationClearanceExit.LogAtStartup(services.BuildServiceProvider());
                Assert.Equal(journeysRun && expected is not null ? 1 : 0, log.Entries.Count(entry => entry.EventId.Id == 2272));
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    public static TheoryData<string> GeneralFaults =>
    [
        "hang-alone",
        "timeout",
        "not-at-the-charger",
        "navigation-unreachable",
        "vehicle-cannot-execute",
        "free-text-failure",
        "order-exception",
        "result-unknown",
    ];

    /// <summary>
    /// <c>REQ-0175</c> 的负向：单独的 <c>HANG</c>、超时（半小时没动静）、未到桩、导航不可达（单 FAILED）、车辆不可执行（离线）、只有自由文本的失败
    /// （没有结果码）、订单异常（FAILED）、结果未知（单读不到）——任何一条单独出现都不形成确认、不暂停桩、没有暂停事件，车的用途仍是 <c>CHARGING</c>
    /// 或已按它原来的分支收尾，绝不是 <c>CLEARING_MAINTENANCE</c>。
    /// </summary>
    [Theory]
    [MemberData(nameof(GeneralFaults))]
    public async Task AGeneralChargingFaultAloneNeverPausesTheCharger(string fault)
    {
        await using FleetFixture fleet = await FleetAsync();
        fleet.Riot.BatteryByVehicle[KeyA] = 20;
        JourneyRuntimeRow journey = await CommittedAndSentAsync(fleet);
        string upperId = journey.PickupUpperId;
        TimeSpan step = TimeSpan.FromSeconds(1);
        switch (fault)
        {
            case "hang-alone":
                fleet.Riot.HangOrder(upperId);
                AtTheCharger(fleet);
                break;
            case "timeout":
                step = TimeSpan.FromMinutes(1);
                AtTheCharger(fleet);
                break;
            case "not-at-the-charger":
                fleet.Riot.FailToCharge(upperId);
                fleet.Riot.VehicleOverrides[KeyA] = seen => seen with { CurrentStationId = 300, BatteryState = NotCharging };
                break;
            case "navigation-unreachable":
            case "order-exception":
                fleet.Riot.PutOrder(fleet.Riot.OrderOf(upperId)! with
                {
                    Kind = RiotOrderObservationKind.Terminal,
                    OrderState = RiotOrderState.Failed,
                });
                AtTheCharger(fleet);
                break;
            case "vehicle-cannot-execute":
                fleet.Riot.FailToCharge(upperId);
                fleet.Riot.VehicleOverrides[KeyA] = seen => seen with
                {
                    CurrentStationId = Near.StationId,
                    BatteryState = NotCharging,
                    Connected = false,
                };
                break;
            case "free-text-failure":
                fleet.Riot.FailToCharge(upperId, resultCode: null);
                AtTheCharger(fleet);
                break;
            case "result-unknown":
                fleet.Riot.PutOrder(new RiotOrderObservation(upperId, RiotOrderObservationKind.Unknown, null));
                AtTheCharger(fleet);
                break;
        }

        for (int round = 0; round < 30; round++)
        {
            await fleet.HearFromEveryVehicleAsync();
            await fleet.RunRoundAsync(step);
        }

        Assert.Empty(await HoldsAsync(fleet));
        Assert.Empty(await fleet.Context.Set<StationClearanceRow>().AsNoTracking().ToArrayAsync(Token));
        Assert.NotEqual(VehiclePurposes.ClearingMaintenance, (await ClaimOfAsync(fleet, KeyA))?.Purpose);
        Assert.DoesNotContain(
            await fleet.Context.Set<ChargingCycleRow>().AsNoTracking().ToArrayAsync(Token),
            row => row.WireState == ChargingCycleWireStates.UnableToCharge || row.Phase == ChargingCyclePhases.Clearing);
        Assert.DoesNotContain(fleet.EngineLog.Entries, entry => entry.EventId.Id == 2264);
    }

    // ---- 暂停事件（REQ-0177）---------------------------------------------------------------------------------------------

    /// <summary>
    /// 同一次充不上被之后每一轮都看到（单一直 <c>HANG</c>、明细一直 407802）：只有一条暂停事件；事件写入之后不可改（改写被守卫拒绝）。
    /// </summary>
    [Fact]
    public async Task OneUnableToChargeSeenRoundAfterRoundIsOneImmutableEvent()
    {
        await using FleetFixture fleet = await FleetAsync();
        await ConfirmedAsync(fleet);

        for (int round = 0; round < 10; round++)
        {
            await RoundAsync(fleet);
        }

        ChargingStationAllocationHoldRow hold = Assert.Single(await HoldsAsync(fleet));
        Assert.Single(fleet.EngineLog.Entries, entry => entry.EventId.Id == 2264);

        ChargingStationAllocationHoldRow tracked = await fleet.Context.Set<ChargingStationAllocationHoldRow>()
            .SingleAsync(row => row.HoldId == hold.HoldId, Token);
        tracked.SiteDisposition = "rewritten";
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => fleet.Context.SaveChangesAsync(Token));
        fleet.Context.ChangeTracker.Clear();
        Assert.Null((await HoldsAsync(fleet)).Single().SiteDisposition);
    }

    // ---- 原桩重试为 0、车保持原位 ----------------------------------------------------------------------------------------

    /// <summary>
    /// 确认之后多轮（半小时、一分钟一轮），而有一条它能接的搬运需求、它的电量还低：没有任何新单（以原桩为目标的、搬运的、空闲返回的），
    /// 没有任何订单命令（没有 <c>CONTINUE_FROM_HANG</c>），也没有急停；它不进充电分配（有用途占有），旅程仍是这一趟充电。
    /// </summary>
    [Fact]
    public async Task AVehicleClearingStaysWhereItIsAndTheChargerIsNeverRetried()
    {
        await using FleetFixture fleet = await FleetAsync();
        JourneyRuntimeRow journey = await ConfirmedAsync(fleet);
        fleet.Catalog.Set([FleetFixture.Demand(0, "N1-1", 0)]);

        for (int minute = 0; minute < 30; minute++)
        {
            await fleet.HearFromEveryVehicleAsync();
            await fleet.RunRoundAsync(TimeSpan.FromMinutes(1));
        }

        Assert.Single(fleet.Riot.Creates);
        Assert.Single(fleet.Riot.CreatedIntents);
        Assert.Empty(fleet.Riot.OrderCommands);
        Assert.Empty(fleet.Riot.EmergencyCommands);
        Assert.Empty(fleet.AcceptedPlans);
        JourneyRuntimeRow open = Assert.Single(await fleet.Context.JourneyRuntimes.AsNoTracking()
            .Where(row => row.AgvId == AgvA && row.Stage != JourneyRuntimeStage.Completed).ToArrayAsync(Token));
        Assert.Equal(journey.JourneyId, open.JourneyId);
        Assert.Equal(ChargingExecutionReasons.UnableToChargeClearing, open.BlockReasonCode);
        Assert.Equal(VehiclePurposes.ClearingMaintenance, (await ClaimOfAsync(fleet, KeyA))!.Value.Purpose);
    }

    /// <summary>
    /// 票面第 9 条的核实：车停在桩上、单 <c>HANG</c>，确认前后都不交故障模型——没有故障事实、没有暂停订单的命令、没有急停，不落进
    /// 「急停之后 RIoT 拒绝继续、解除也拒绝」的环。
    /// </summary>
    [Fact]
    public async Task AnUnableToChargeHangOnTheChargerIsNeverEscalatedToAnEmergencyStop()
    {
        await using FleetFixture fleet = await FleetAsync();
        await ConfirmedAsync(fleet);

        for (int minute = 0; minute < 15; minute++)
        {
            await fleet.HearFromEveryVehicleAsync();
            await fleet.RunRoundAsync(TimeSpan.FromMinutes(1));
        }

        Assert.Empty(fleet.Riot.EmergencyCommands);
        Assert.Empty(fleet.Riot.OrderCommands);
        Assert.Empty(await fleet.Context.VehicleFaultStates.AsNoTracking()
            .Where(row => row.AgvId == AgvA && row.Level != VehicleFaultLevel.None).ToArrayAsync(Token));
    }

    /// <summary>
    /// 清桩中有人在 RIoT 里取消了旧充电单：不重建（cs#404 的判别，充电单没有需求），不建任何单，旅程仍是充不上的码，车仍在清桩中。
    /// </summary>
    [Fact]
    public async Task AnOldOrderCancelledInRiotWhileClearingIsNotRebuilt()
    {
        await using FleetFixture fleet = await FleetAsync();
        JourneyRuntimeRow journey = await ConfirmedAsync(fleet);

        fleet.Riot.CancelOrder(journey.PickupUpperId);
        for (int minute = 0; minute < 10; minute++)
        {
            await fleet.HearFromEveryVehicleAsync();
            await fleet.RunRoundAsync(TimeSpan.FromMinutes(1));
        }

        Assert.Single(fleet.Riot.Creates);
        Assert.Empty(fleet.Riot.OrderCommands);
        Assert.Empty(await fleet.Context.Set<OwnOrderRebuildRow>().AsNoTracking().ToArrayAsync(Token));
        Assert.Equal(ChargingExecutionReasons.UnableToChargeClearing, (await ChargingJourneyAsync(fleet, AgvA))!.BlockReasonCode);
        Assert.Equal(ChargingCyclePhases.Clearing, (await OpenCycleAsync(fleet)).Phase);
        Assert.Equal((KeyA, StationExclusivityStates.Reserved), await HolderAsync(fleet, Near.StationId));
    }

    // ---- 崩溃点 -----------------------------------------------------------------------------------------------------------

    /// <summary>
    /// 「写暂停事件」与「用途转 <c>CLEARING_MAINTENANCE</c>」同一次保存，写事件那一刻崩掉：两样都不留（没有事件、周期仍在途、用途仍是
    /// <c>CHARGING</c>、没有清桩记录）；之后的轮次按同一组事实重新判出同一条，幂等键不变。
    /// </summary>
    [Fact]
    public async Task ACrashWhileWritingTheEventLeavesNeitherHalfAndTheSameEventIsFormedAgain()
    {
        FailOnce crash = new("INSERT INTO \"ChargingStationAllocationHolds\"");
        await using FleetFixture fleet = await FleetAsync(commands: crash);
        JourneyRuntimeRow journey = await FailingAtChargerAsync(fleet);
        await RoundAsync(fleet);

        crash.Armed = true;
        await Assert.ThrowsAnyAsync<Exception>(() => RoundAsync(fleet));
        Assert.Equal(1, crash.Fired);
        Assert.Empty(await HoldsAsync(fleet));
        Assert.Equal(ChargingCyclePhases.Active, (await OpenCycleAsync(fleet)).Phase);
        Assert.Equal(VehiclePurposes.Charging, (await ClaimOfAsync(fleet, KeyA))!.Value.Purpose);
        Assert.Empty(await fleet.Context.Set<StationClearanceRow>().AsNoTracking().ToArrayAsync(Token));

        for (int round = 0; round < 3; round++)
        {
            await RoundAsync(fleet);
        }

        ChargingStationAllocationHoldRow hold = Assert.Single(await HoldsAsync(fleet));
        ChargingCycleRow cycle = await OpenCycleAsync(fleet);
        Assert.Equal(
            JourneyPlanBuilder.StableGuid(ChargingStationHoldTriggers.UnableToChargeConfirmed + "|" + cycle.CycleId, "charging-station-hold"),
            hold.IdempotencyKey);
        Assert.Equal(ChargingCyclePhases.Clearing, cycle.Phase);
        Assert.Equal((VehiclePurposes.ClearingMaintenance, journey.JourneyId), (await ClaimOfAsync(fleet, KeyA))!.Value);
    }

    // ---- 夹具 ------------------------------------------------------------------------------------------------------------

    /// <summary>A 低电、承诺、建单、确认；然后单在桩上执行开始充电、RIoT 返回 <paramref name="resultCode"/> 并挂起，车停在 211 上没在充电。</summary>
    internal static async Task<JourneyRuntimeRow> FailingAtChargerAsync(FleetFixture fleet, int? resultCode = 407802)
    {
        fleet.Riot.BatteryByVehicle[KeyA] = 20;
        JourneyRuntimeRow journey = await CommittedAndSentAsync(fleet);
        fleet.Riot.FailToCharge(journey.PickupUpperId, resultCode);
        AtTheCharger(fleet);
        return journey;
    }

    /// <summary>形成「已确认充不上」：两轮看到同一组严格事实。</summary>
    internal static async Task<JourneyRuntimeRow> ConfirmedAsync(FleetFixture fleet)
    {
        JourneyRuntimeRow journey = await FailingAtChargerAsync(fleet);
        await RoundAsync(fleet);
        await RoundAsync(fleet);
        Assert.Single(await HoldsAsync(fleet));
        Assert.Equal(ChargingCyclePhases.Clearing, (await OpenCycleAsync(fleet)).Phase);
        return journey;
    }

    internal static void AtTheCharger(FleetFixture fleet) =>
        fleet.Riot.VehicleOverrides[KeyA] = seen => seen with { CurrentStationId = Near.StationId, BatteryState = NotCharging };

    internal static async Task<ChargingStationAllocationHoldRow[]> HoldsAsync(FleetFixture fleet) =>
        await fleet.Context.Set<ChargingStationAllocationHoldRow>().AsNoTracking().ToArrayAsync(Token);

    internal static async Task<ChargingCycleRow> OpenCycleAsync(FleetFixture fleet) =>
        await fleet.Context.Set<ChargingCycleRow>().AsNoTracking()
            .SingleAsync(row => row.VehicleKey == KeyA && row.Phase != ChargingCyclePhases.Ended, Token);

    /// <summary>
    /// armed 之后第一条含 <paramref name="prefix"/>（以及 <paramref name="alsoContaining"/>，给了的话）的命令抛异常：那一次保存整个回滚。
    /// </summary>
    internal sealed class FailOnce(string prefix, string? alsoContaining = null) : DbCommandInterceptor
    {
        public bool Armed { get; set; }

        public int Fired { get; private set; }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Fail(command);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Fail(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void Fail(DbCommand command)
        {
            if (!Armed || !command.CommandText.Contains(prefix, StringComparison.Ordinal) ||
                (alsoContaining is not null && !command.CommandText.Contains(alsoContaining, StringComparison.Ordinal)))
            {
                return;
            }
            Armed = false;
            Fired++;
            throw new InvalidOperationException($"Injected crash at: {prefix}");
        }
    }
}
