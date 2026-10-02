using System.Net;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Charging;
using ControlServer.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using static ControlServer.Tests.ChargingAllocationTests;
using static ControlServer.Tests.ChargingExecutionTests;
using FleetFixture = ControlServer.Tests.MultiVehicleExecutionTests.FleetFixture;

namespace ControlServer.Tests;

/// <summary>
/// 充电中断与充电无进展（批次9-09，control-server#407；<c>REQ-0285</c>、<c>REQ-0286</c>、<c>REQ-0287</c>），以及调度追加的 S-d（原桩反复重充）
/// 与 S-e（到桩未证实一类的码按旅程计时）。
/// </summary>
/// <remarks>
/// 夹具同 <see cref="ChargingCycleProgressTests"/>：测试策略的完成阈值 80、稳定期 180 秒、观察窗口 600 秒、最小增量 3，证据最大时效 2 分钟。
/// 隔离要 Host 恢复入口可用，所以多数用例先 <see cref="IsolatingFleetAsync"/>；只告警的那几条用夹具默认（Host 入口不可用）。
/// </remarks>
[Trait("IntegrationSlice", "FP-IS-13")]
public sealed class ChargingInterruptionTests
{
    private const int Map = 25;
    private const string Charging = "CHARGING";
    private const string NotCharging = "NO_CHARGE";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string AgvA => FleetFixture.AgvIds[0];
    private static string KeyA => FleetFixture.VehicleKeys[0];

    // ---- 中断 ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// 充电中、低于完成阈值，连续两次新鲜读到不是 <c>CHARGING</c>：形成中断确认。同一次保存写下桩的分配暂停与车的充电资格暂停（触发来源与原因
    /// <c>INTERRUPTION_CONFIRMED</c>、根因 <c>UNKNOWN</c>），用途转 <c>CLEARING_MAINTENANCE</c>，周期进清桩中（线上 <c>UNABLE_TO_CHARGE</c>），清桩记录开始；
    /// 告警恰好一次。第一次读到只开始观察，什么也不写。不建单、不发命令、桩仍是这一趟的。
    /// </summary>
    [Fact]
    public async Task TwoFreshReadingsNotChargingBelowTheThresholdConfirmAnInterruptionAndPauseBothSidesInOneSave()
    {
        await using FleetFixture fleet = await IsolatingFleetAsync();
        JourneyRuntimeRow journey = await ChargingAsync(fleet, battery: 50);

        AtCharger(fleet, KeyA, 50, NotCharging);
        await LongRoundAsync(fleet, TimeSpan.FromSeconds(30));
        await LongRoundAsync(fleet, TimeSpan.FromSeconds(30));
        Assert.Empty(await StationHoldsAsync(fleet));
        Assert.Empty(await VehicleHoldsAsync(fleet));
        Assert.Equal(ChargingCycleWireStates.Charging, (await OpenCycleAsync(fleet)).WireState);

        await LongRoundAsync(fleet, TimeSpan.FromSeconds(30));

        ChargingCycleRow cycle = await OpenCycleAsync(fleet);
        ChargingStationAllocationHoldRow station = Assert.Single(await StationHoldsAsync(fleet));
        VehicleChargingEligibilityHoldRow vehicle = Assert.Single(await VehicleHoldsAsync(fleet));
        Assert.Equal(
            (ChargingStationHoldTriggers.InterruptionConfirmed, ChargingHoldRootCauses.Unknown, Near.StationId, cycle.CycleId),
            (station.Trigger, station.RootCause, station.StationId, station.CycleId));
        Assert.Equal(
            JourneyPlanBuilder.StableGuid(ChargingStationHoldTriggers.InterruptionConfirmed + "|" + cycle.CycleId, "charging-station-hold"),
            station.IdempotencyKey);
        Assert.Equal(
            (VehicleChargingEligibilityHoldReasons.InterruptionConfirmed, KeyA, cycle.CycleId),
            (vehicle.Reason, vehicle.VehicleKey, vehicle.CycleId));
        Assert.Equal(station.HeldAt, vehicle.HeldAt);
        Assert.Equal((ChargingCyclePhases.Clearing, ChargingCycleWireStates.UnableToCharge), (cycle.Phase, cycle.WireState));
        Assert.Single(await fleet.Context.Set<StationClearanceRow>().AsNoTracking().ToArrayAsync(Token));
        Assert.Equal((VehiclePurposes.ClearingMaintenance, journey.JourneyId), (await ClaimOfAsync(fleet, KeyA))!.Value);
        Assert.Equal(ChargingExecutionReasons.InterruptionClearing, (await ChargingJourneyAsync(fleet, AgvA))!.BlockReasonCode);
        Assert.Equal((KeyA, StationExclusivityStates.Occupied), await HolderAsync(fleet, Near.StationId));
        Assert.Single(fleet.EngineLog.Entries, entry => entry.EventId.Id == 2276);
        Assert.Single(fleet.Riot.Creates);
        Assert.Empty(fleet.Riot.OrderCommands);
        Assert.Empty(fleet.Riot.EmergencyCommands);
    }

    public static TheoryData<string> NoInterruptionShapes =>
        ["one-reading-then-charging", "battery-unreadable", "battery-state-unreadable", "reading-stale", "vehicle-offline", "gap-between-the-two"];

    /// <summary>
    /// 不形成中断的四种（票面测试接缝）：只有一次非 <c>CHARGING</c> 随后又是 <c>CHARGING</c>（反复也不算）；读不到电量、读不到 <c>batteryState</c>、读数过期、
    /// 读不到车（遥测丢失，<c>REQ-0287</c>：只暂停观察）；两次非 <c>CHARGING</c> 之间夹着一次遥测缺口（连续性断了）。三十轮下来：没有暂停、周期仍在充电、
    /// 用途仍是 <c>CHARGING</c>、桩仍占用，不建单、不发命令。
    /// </summary>
    [Theory]
    [MemberData(nameof(NoInterruptionShapes))]
    public async Task AnInterruptionIsNotFormedWithoutTwoContinuousFreshReadingsNotCharging(string shape)
    {
        await using FleetFixture fleet = await IsolatingFleetAsync();
        await ChargingAsync(fleet, battery: 50);

        for (int round = 0; round < 30; round++)
        {
            bool odd = round % 2 == 1;
            fleet.Riot.BatteryByVehicle[KeyA] = 50;
            fleet.Riot.VehicleOverrides[KeyA] = shape switch
            {
                "one-reading-then-charging" => seen => seen with { CurrentStationId = Near.StationId, BatteryState = odd ? Charging : NotCharging },
                "battery-unreadable" => seen => seen with { CurrentStationId = Near.StationId, BatteryState = NotCharging, BatteryPercent = null },
                "battery-state-unreadable" => seen => seen with { CurrentStationId = Near.StationId, BatteryState = null },
                "reading-stale" => seen => seen with
                {
                    CurrentStationId = Near.StationId,
                    BatteryState = NotCharging,
                    ObservedAt = seen.ObservedAt - fleet.Options.MaximumEvidenceAge - TimeSpan.FromSeconds(1),
                },
                "vehicle-offline" => seen => seen with { CurrentStationId = Near.StationId, BatteryState = NotCharging, Connected = false },
                "gap-between-the-two" => seen => odd
                    ? seen with { CurrentStationId = Near.StationId, BatteryState = NotCharging, BatteryPercent = null }
                    : seen with { CurrentStationId = Near.StationId, BatteryState = NotCharging },
                _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, null),
            };
            await LongRoundAsync(fleet, TimeSpan.FromSeconds(10));
        }

        Assert.Empty(await StationHoldsAsync(fleet));
        Assert.Empty(await VehicleHoldsAsync(fleet));
        ChargingCycleRow cycle = await OpenCycleAsync(fleet);
        Assert.Equal((ChargingCyclePhases.Active, ChargingCycleWireStates.Charging), (cycle.Phase, cycle.WireState));
        Assert.Equal(VehiclePurposes.Charging, (await ClaimOfAsync(fleet, KeyA))!.Value.Purpose);
        Assert.Equal((KeyA, StationExclusivityStates.Occupied), await HolderAsync(fleet, Near.StationId));
        Assert.DoesNotContain(fleet.EngineLog.Entries, entry => entry.EventId.Id is 2276 or 2278);
        Assert.Single(fleet.Riot.Creates);
        Assert.Empty(fleet.Riot.OrderCommands);

        if (shape == "gap-between-the-two")
        {
            // The rule, not a blanket "never": two in a row with nothing between them do form it.
            await StopChargingAsync(fleet, 50);
            Assert.Single(await StationHoldsAsync(fleet));
        }
    }

    /// <summary>
    /// 已达完成阈值时停止充电不是中断，是充满（批次9-07）：电量 80 而读到不是 <c>CHARGING</c>，周期 <c>COMPLETE</c>，没有任何暂停。
    /// </summary>
    [Fact]
    public async Task AStopAtTheCompletionThresholdIsCompletionNotAnInterruption()
    {
        await using FleetFixture fleet = await IsolatingFleetAsync();
        await ChargingAsync(fleet, battery: 70);
        AtCharger(fleet, KeyA, 80, NotCharging);
        for (int round = 0; round < 3; round++)
        {
            await RoundAsync(fleet);
        }

        Assert.Equal(ChargingCycleWireStates.Complete, (await CycleAsync(fleet)).WireState);
        Assert.Empty(await StationHoldsAsync(fleet));
        Assert.Empty(await VehicleHoldsAsync(fleet));
    }

    /// <summary>
    /// 并发读改写：中断确认与充满在同一轮——第一次读到不是 <c>CHARGING</c> 时 79%，第二次（本来正好形成中断的那一轮）电量跨到 80%。只有一种结论成立：
    /// 充满优先（周期 <c>COMPLETE</c>，没有暂停）。
    /// </summary>
    [Fact]
    public async Task WhenTheBatteryCrossesTheThresholdInTheRoundAnInterruptionWouldFormCompletionWins()
    {
        await using FleetFixture fleet = await IsolatingFleetAsync();
        await ChargingAsync(fleet, battery: 70);
        AtCharger(fleet, KeyA, 79, Charging);
        await RoundAsync(fleet);
        AtCharger(fleet, KeyA, 79, NotCharging);
        await RoundAsync(fleet);
        Assert.Equal(ChargingCycleWireStates.Charging, (await CycleAsync(fleet)).WireState);

        AtCharger(fleet, KeyA, 80, NotCharging);
        await RoundAsync(fleet);

        Assert.Equal(ChargingCycleWireStates.Complete, (await CycleAsync(fleet)).WireState);
        Assert.Empty(await StationHoldsAsync(fleet));
        Assert.Empty(await VehicleHoldsAsync(fleet));
    }

    /// <summary>
    /// 中断要持续够久（#442 审查 S3a）：每 10 秒读一次「不在充电」，50 秒时还不形成——相隔一个轮询间隔的两次读数可能是 RIoT 的同一份快照；
    /// 从第一次读到起满 60 秒（<c>JourneyRuntime:ChargingInterruptionConfirmAfter</c> 的默认值）的那一次读数形成。
    /// </summary>
    [Fact]
    public async Task AnInterruptionIsConfirmedOnlyOnceTheStopHasLastedTheConfiguredTime()
    {
        await using FleetFixture fleet = await IsolatingFleetAsync();
        await ChargingAsync(fleet, battery: 50);
        Assert.Equal(TimeSpan.FromSeconds(60), fleet.Options.ChargingInterruptionConfirmAfter);
        AtCharger(fleet, KeyA, 50, NotCharging);
        for (int round = 0; round < 6; round++)
        {
            await LongRoundAsync(fleet, TimeSpan.FromSeconds(10));
        }
        Assert.Empty(await StationHoldsAsync(fleet));

        await LongRoundAsync(fleet, TimeSpan.FromSeconds(10));
        Assert.Equal(ChargingStationHoldTriggers.InterruptionConfirmed, Assert.Single(await StationHoldsAsync(fleet)).Trigger);
    }

    /// <summary>
    /// 间隔不够不形成（#442 审查 S3a）：配置改成 120 秒之后，每秒一次「不在充电」读一百一十秒，不形成；读够 120 秒才形成。第一次读到之后又读到一次充电，
    /// 重新计时。
    /// </summary>
    [Fact]
    public async Task ReadingsCloserTogetherThanTheConfiguredTimeFormNoInterruption()
    {
        await using FleetFixture fleet = await IsolatingFleetAsync();
        fleet.Options.ChargingInterruptionConfirmAfter = TimeSpan.FromSeconds(120);
        await fleet.RecreateEngineAsync();
        await ChargingAsync(fleet, battery: 50);

        AtCharger(fleet, KeyA, 50, NotCharging);
        for (int round = 0; round < 60; round++)
        {
            await LongRoundAsync(fleet, TimeSpan.FromSeconds(1));
        }
        AtCharger(fleet, KeyA, 50, Charging);
        await LongRoundAsync(fleet, TimeSpan.FromSeconds(1));
        AtCharger(fleet, KeyA, 50, NotCharging);
        for (int round = 0; round < 110; round++)
        {
            await LongRoundAsync(fleet, TimeSpan.FromSeconds(1));
        }
        Assert.Empty(await StationHoldsAsync(fleet));

        for (int round = 0; round < 12 && (await StationHoldsAsync(fleet)).Length == 0; round++)
        {
            await LongRoundAsync(fleet, TimeSpan.FromSeconds(1));
        }
        Assert.Single(await StationHoldsAsync(fleet));
    }

    // ---- 无进展 ----------------------------------------------------------------------------------------------------------

    /// <summary>
    /// 持续 <c>CHARGING</c>、电量不涨，用会走的钟（每轮 30 秒）：稳定期 180 秒内不开窗；开窗之后起点时刻与电量落库，之后每一个新样本都不改它；窗口满
    /// 600 秒的那一轮形成无进展确认，两侧暂停（<c>NO_PROGRESS_CONFIRMED</c>），在那之前一条暂停都没有。
    /// </summary>
    [Fact]
    public async Task ChargingWithoutGainOverTheWholeWindowConfirmsNoProgressAndTheWindowStartIsNeverMovedBySamples()
    {
        await using FleetFixture fleet = await IsolatingFleetAsync();
        await ChargingAsync(fleet, battery: 50);
        DateTimeOffset chargingSince = (await OpenCycleAsync(fleet)).FirstChargingSeenAt!.Value;

        DateTimeOffset? windowStart = null;
        DateTimeOffset? formedAt = null;
        for (int round = 0; round < 40 && formedAt is null; round++)
        {
            await LongRoundAsync(fleet, TimeSpan.FromSeconds(30));
            if ((await StationHoldsAsync(fleet)).Length > 0)
            {
                formedAt = fleet.Clock.GetUtcNowWithoutTick();
                break;
            }
            ChargingCycleRow cycle = await OpenCycleAsync(fleet);
            if (cycle.ObservationWindowStartedAt is { } start)
            {
                Assert.True(start - chargingSince >= TimeSpan.FromSeconds(180), "no window inside the stabilization period");
                windowStart ??= start;
                Assert.Equal(windowStart, start);
                Assert.Equal(50, cycle.ObservationWindowStartPercent);
            }
        }

        Assert.NotNull(windowStart);
        Assert.NotNull(formedAt);
        Assert.InRange(formedAt.Value - windowStart.Value, TimeSpan.FromSeconds(600), TimeSpan.FromSeconds(600 + 60));
        ChargingStationAllocationHoldRow station = Assert.Single(await StationHoldsAsync(fleet));
        Assert.Equal(ChargingStationHoldTriggers.NoProgressConfirmed, station.Trigger);
        Assert.Equal(VehicleChargingEligibilityHoldReasons.NoProgressConfirmed, Assert.Single(await VehicleHoldsAsync(fleet)).Reason);
        Assert.Equal(ChargingExecutionReasons.NoProgressClearing, (await ChargingJourneyAsync(fleet, AgvA))!.BlockReasonCode);
        Assert.Single(fleet.Riot.Creates);
    }

    /// <summary>
    /// 窗口里电量涨够了最小增量（3%）：不形成，从那一个样本另开一个窗口（起点换成那一刻、那一格电量）；之后不涨的话按新窗口再判。
    /// </summary>
    [Fact]
    public async Task EnoughGainOverTheWindowFormsNothingAndStartsTheNextWindowThere()
    {
        await using FleetFixture fleet = await IsolatingFleetAsync();
        await ChargingAsync(fleet, battery: 50);
        DateTimeOffset firstStart = await RunUntilTheWindowOpensAsync(fleet);

        AtCharger(fleet, KeyA, 53, Charging);
        for (int round = 0; round < 25; round++)
        {
            await LongRoundAsync(fleet, TimeSpan.FromSeconds(30));
        }

        Assert.Empty(await StationHoldsAsync(fleet));
        ChargingCycleRow cycle = await OpenCycleAsync(fleet);
        Assert.Equal(53, cycle.ObservationWindowStartPercent);
        Assert.True(cycle.ObservationWindowStartedAt >= firstStart + TimeSpan.FromSeconds(600));

        for (int round = 0; round < 25 && (await StationHoldsAsync(fleet)).Length == 0; round++)
        {
            await LongRoundAsync(fleet, TimeSpan.FromSeconds(30));
        }
        Assert.Equal(ChargingStationHoldTriggers.NoProgressConfirmed, Assert.Single(await StationHoldsAsync(fleet)).Trigger);
    }

    /// <summary>
    /// 窗口里出现遥测缺口（电量读不到两轮）：窗口作废、重新起算，不顺延——按原窗口本该形成的那一刻不形成；从缺口之后重新开的窗口满了才形成。
    /// </summary>
    [Fact]
    public async Task ATelemetryGapInsideTheWindowStartsTheWindowOver()
    {
        await using FleetFixture fleet = await IsolatingFleetAsync();
        await ChargingAsync(fleet, battery: 50);
        DateTimeOffset firstStart = await RunUntilTheWindowOpensAsync(fleet);
        for (int round = 0; round < 10; round++)
        {
            await LongRoundAsync(fleet, TimeSpan.FromSeconds(30));
        }

        fleet.Riot.VehicleOverrides[KeyA] = seen => seen with { CurrentStationId = Near.StationId, BatteryState = Charging, BatteryPercent = null };
        await LongRoundAsync(fleet, TimeSpan.FromSeconds(30));
        await LongRoundAsync(fleet, TimeSpan.FromSeconds(30));
        Assert.Null((await OpenCycleAsync(fleet)).ObservationWindowStartedAt);

        AtCharger(fleet, KeyA, 50, Charging);
        while (fleet.Clock.GetUtcNowWithoutTick() < firstStart + TimeSpan.FromSeconds(660))
        {
            await LongRoundAsync(fleet, TimeSpan.FromSeconds(30));
        }
        Assert.Empty(await StationHoldsAsync(fleet));
        DateTimeOffset restarted = (await OpenCycleAsync(fleet)).ObservationWindowStartedAt!.Value;
        Assert.True(restarted > firstStart + TimeSpan.FromSeconds(300));

        for (int round = 0; round < 25 && (await StationHoldsAsync(fleet)).Length == 0; round++)
        {
            await LongRoundAsync(fleet, TimeSpan.FromSeconds(30));
        }
        Assert.Single(await StationHoldsAsync(fleet));
        Assert.True(fleet.Clock.GetUtcNowWithoutTick() - restarted >= TimeSpan.FromSeconds(600));
    }

    /// <summary>
    /// 观察参数取周期冻结的那一版（<c>REQ-0282</c>）：周期冻结窗口 600 秒，周期中途激活一版窗口 60 秒的新策略。按新版本该形成的时候不形成，
    /// 按冻结那一版的 600 秒才形成。
    /// </summary>
    [Fact]
    public async Task TheObservationWindowIsTheOneTheCycleFroze()
    {
        await using FleetFixture fleet = await IsolatingFleetAsync();
        SwitchingResolver resolver = new(
            TestChargingPolicies.Effective(TestChargingPolicies.Content, version: 1),
            TestChargingPolicies.Effective(
                TestChargingPolicies.Content with { ProgressStabilizationSeconds = 0, ProgressObservationWindowSeconds = 60 }, version: 2));
        fleet.ChargingPolicy = resolver;
        await fleet.RecreateEngineAsync();
        await ChargingAsync(fleet, battery: 50);
        Assert.Equal(1, (await OpenCycleAsync(fleet)).ChargingPolicyVersion);

        resolver.Switch();
        DateTimeOffset start = await RunUntilTheWindowOpensAsync(fleet);
        while (fleet.Clock.GetUtcNowWithoutTick() < start + TimeSpan.FromSeconds(540))
        {
            await LongRoundAsync(fleet, TimeSpan.FromSeconds(30));
        }
        Assert.Empty(await StationHoldsAsync(fleet));

        for (int round = 0; round < 6 && (await StationHoldsAsync(fleet)).Length == 0; round++)
        {
            await LongRoundAsync(fleet, TimeSpan.FromSeconds(30));
        }
        Assert.Single(await StationHoldsAsync(fleet));
    }

    // ---- 两侧隔离、各自恢复 ----------------------------------------------------------------------------------------------

    /// <summary>
    /// 防「逐桩试错」：中断之后清桩完成、车离开了原桩、电量掉到强制充电线以下，名册里另有一个空闲的桩 221——车的充电资格暂停着，它不被分配任何桩，
    /// 排队并告警（事件 2246，<c>VEHICLE_CHARGING_ELIGIBILITY_HELD</c>）。211 暂停着，221 一直没人占。
    /// </summary>
    [Fact]
    public async Task AVehicleWhoseChargingEligibilityIsPausedIsNotSentToASecondFreeCharger()
    {
        await using FleetFixture fleet = await IsolatingFleetAsync(Near, Far);
        await InterruptedAndClearedAsync(fleet);

        fleet.Riot.BatteryByVehicle[KeyA] = 20;
        for (int round = 0; round < 5; round++)
        {
            await RoundAsync(fleet);
        }

        Assert.Equal(ChargingAllocationReasons.VehicleEligibilityHeld, fleet.ChargingBoard.Verdicts[AgvA].Reason);
        Assert.Null(await HolderAsync(fleet, Far.StationId));
        Assert.Null(await HolderAsync(fleet, Near.StationId));
        Assert.Single(fleet.ChargingLog.Entries, entry => entry.EventId.Id == 2246);
        Assert.Single(fleet.Riot.Creates);
    }

    /// <summary>
    /// 车与桩各自恢复（<c>REQ-0286</c>）：恢复桩（<c>ChargingStationRecoveryConfirmation</c>）不恢复车——车仍然一个桩都分不到；恢复车（本票的 Host 入口）
    /// 不恢复桩——车分到的是 221，211 仍暂停着。
    /// </summary>
    [Fact]
    public async Task TheChargerAndTheVehicleEachRecoverOnTheirOwn()
    {
        const string variable = "CONTROL_SERVER_TEST_CS407_CREDENTIAL";
        Environment.SetEnvironmentVariable(variable, "cs407-secret");
        try
        {
            await using FleetFixture fleet = await IsolatingFleetAsync(Near, Far);
            await InterruptedAndClearedAsync(fleet);
            fleet.Riot.BatteryByVehicle[KeyA] = 20;
            ChargingHoldStore holds = new(fleet.Context);
            IOptions<VehicleFaultRecoveryOptions> options = Options.Create(new VehicleFaultRecoveryOptions { CredentialEnvironmentVariable = variable });

            var charger = await ChargingStationEndpoints.RecoverAsync(
                HttpContextFor("cs407-secret"), new ChargingStationRecoveryHttpRequest(Map, Near.StationId, "op-r11", "桩检查 C-1", null),
                holds, Audit(fleet), options, fleet.Clock, NullLogger<ChargingStationRecoveryHttpRequest>.Instance, Token);
            Assert.Equal(StatusCodes.Status200OK, ((IStatusCodeHttpResult)charger.Result).StatusCode);
            Assert.Single(await holds.ListActiveVehicleHoldsAsync(KeyA, Token));
            for (int round = 0; round < 3; round++)
            {
                await RoundAsync(fleet);
            }
            Assert.Equal(ChargingAllocationReasons.VehicleEligibilityHeld, fleet.ChargingBoard.Verdicts[AgvA].Reason);
            Assert.Null(await HolderAsync(fleet, Near.StationId));
            Assert.Null(await HolderAsync(fleet, Far.StationId));

            // The other way round on a second interruption: the vehicle recovered, the charger not.
            await using FleetFixture second = await IsolatingFleetAsync(Near, Far);
            await InterruptedAndClearedAsync(second);
            second.Riot.BatteryByVehicle[KeyA] = 20;
            ChargingHoldStore secondHolds = new(second.Context);
            var vehicle = await ChargingStationEndpoints.RecoverVehicleAsync(
                HttpContextFor("cs407-secret"), new VehicleChargingEligibilityRecoveryHttpRequest(AgvA, "op-r11", "电池检查 B-1", null),
                secondHolds, new ControlServer.Host.Runtime.Fleet.VehicleRoster(Options.Create(second.Options)), Audit(second), options,
                second.Clock, NullLogger<VehicleChargingEligibilityRecoveryHttpRequest>.Instance, Token);
            Assert.Equal(StatusCodes.Status200OK, ((IStatusCodeHttpResult)vehicle.Result).StatusCode);
            Assert.Single(await secondHolds.ListActiveStationHoldsAsync(Map, Near.StationId, Token));
            await RoundAsync(second);
            Assert.Equal(ChargingAllocationReasons.Committed, second.ChargingBoard.Verdicts[AgvA].Reason);
            Assert.Equal((KeyA, StationExclusivityStates.Reserved), await HolderAsync(second, Far.StationId));
            Assert.Null(await HolderAsync(second, Near.StationId));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    /// <summary>
    /// 车辆充电资格恢复入口（放宽类）：没有凭据 401；不认识的车 404；缺人员或依据、或这辆车没有未恢复的暂停 409；成功时把每一条未恢复的资格暂停各记一条恢复，
    /// 每一次请求（拒绝也算）写一条管理员审计。
    /// </summary>
    [Fact]
    public async Task TheVehicleRecoveryEntryNeedsTheCredentialANamedPersonABasisAndAHold()
    {
        const string variable = "CONTROL_SERVER_TEST_CS407_VEHICLE_CREDENTIAL";
        Environment.SetEnvironmentVariable(variable, "cs407-secret");
        try
        {
            await using FleetFixture fleet = await IsolatingFleetAsync();
            ChargingHoldStore holds = new(fleet.Context);
            IOptions<VehicleFaultRecoveryOptions> options = Options.Create(new VehicleFaultRecoveryOptions { CredentialEnvironmentVariable = variable });
            ControlServer.Host.Runtime.Fleet.VehicleRoster roster = new(Options.Create(fleet.Options));
            Task<Results<Ok<VehicleChargingEligibilityRecoveryResponse>, UnauthorizedHttpResult, ProblemHttpResult>> Recover(
                string? bearer, VehicleChargingEligibilityRecoveryHttpRequest request) =>
                ChargingStationEndpoints.RecoverVehicleAsync(
                    HttpContextFor(bearer), request, holds, roster, Audit(fleet), options, fleet.Clock,
                    NullLogger<VehicleChargingEligibilityRecoveryHttpRequest>.Instance, Token);
            VehicleChargingEligibilityRecoveryHttpRequest good = new(AgvA, "op-r11", "电池检查 B-1", "维护管理员");

            Assert.IsType<UnauthorizedHttpResult>((await Recover(null, good)).Result);
            Assert.Equal(StatusCodes.Status404NotFound, ((IStatusCodeHttpResult)(await Recover("cs407-secret", good with { AgvId = "AGV-NOPE" })).Result).StatusCode);
            Assert.Equal(StatusCodes.Status409Conflict, ((IStatusCodeHttpResult)(await Recover("cs407-secret", good)).Result).StatusCode);

            await ChargingAsync(fleet, battery: 50);
            await StopChargingAsync(fleet, 50);
            Assert.Single(await holds.ListActiveVehicleHoldsAsync(KeyA, Token));
            Assert.Equal(StatusCodes.Status409Conflict, ((IStatusCodeHttpResult)(await Recover("cs407-secret", good with { OperatorId = " " })).Result).StatusCode);
            Assert.Equal(StatusCodes.Status409Conflict, ((IStatusCodeHttpResult)(await Recover("cs407-secret", good with { Basis = null })).Result).StatusCode);

            Ok<VehicleChargingEligibilityRecoveryResponse> ok =
                Assert.IsType<Ok<VehicleChargingEligibilityRecoveryResponse>>((await Recover("cs407-secret", good)).Result);
            Assert.Equal(("RECOVERED", 1), (ok.Value!.Outcome, ok.Value.HoldIds.Count));
            VehicleChargingEligibilityRecoveryRow recovery = Assert.Single(
                await fleet.Context.Set<VehicleChargingEligibilityRecoveryRow>().AsNoTracking().ToArrayAsync(Token));
            Assert.Equal(("op-r11", "电池检查 B-1", "维护管理员"), (recovery.RecoveredBy, recovery.Basis, recovery.RecovererRole));
            Assert.Empty(await holds.ListActiveVehicleHoldsAsync(KeyA, Token));
            Assert.Single(await holds.ListActiveStationHoldsAsync(Map, Near.StationId, Token));
            Assert.Equal(
                5,
                await fleet.Context.Set<AdministratorAuditRecordRow>().AsNoTracking()
                    .CountAsync(row => row.Action == ChargingStationEndpoints.VehicleRecoveryAuditAction, Token));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    // ---- 结果未知、出口不可用 --------------------------------------------------------------------------------------------

    /// <summary>
    /// 证据不完整（票面第 4 条）：读不到车与读到不在充电交替出现十五分钟——不形成任何确认，周期、用途、桩占用都保持，不释放、不改派、不移动。
    /// </summary>
    [Fact]
    public async Task WithIncompleteEvidenceEverythingIsKeptAsItIs()
    {
        await using FleetFixture fleet = await IsolatingFleetAsync();
        JourneyRuntimeRow journey = await ChargingAsync(fleet, battery: 50);
        for (int minute = 0; minute < 15; minute++)
        {
            bool offline = minute % 2 == 0;
            fleet.Riot.VehicleOverrides[KeyA] = seen => seen with
            {
                CurrentStationId = Near.StationId,
                BatteryState = NotCharging,
                Connected = !offline,
            };
            await LongRoundAsync(fleet, TimeSpan.FromMinutes(1));
        }

        Assert.Empty(await StationHoldsAsync(fleet));
        Assert.Empty(await VehicleHoldsAsync(fleet));
        Assert.Equal(ChargingCycleWireStates.Charging, (await OpenCycleAsync(fleet)).WireState);
        Assert.Equal((VehiclePurposes.Charging, journey.JourneyId), (await ClaimOfAsync(fleet, KeyA))!.Value);
        Assert.Equal((KeyA, StationExclusivityStates.Occupied), await HolderAsync(fleet, Near.StationId));
        Assert.Single(fleet.Riot.Creates);
        Assert.Empty(fleet.Riot.OrderCommands);
        Assert.Empty(fleet.Riot.EmergencyCommands);
    }

    public static TheoryData<string> AlarmOnlyShapes => ["interruption", "no-progress"];

    /// <summary>
    /// 隔离的出口不可用（夹具默认：Host 恢入口没开，暂停之后桩与车都回不来；cs#406 M1 的教训）：只告警、不隔离。告警恰好一次（事件 2278，写明原因码
    /// <c>CHARGING_RECOVERY_ENTRY_NOT_OFFERED</c>），旅程写只告警的码；没有暂停、周期仍在充电、用途仍是 <c>CHARGING</c>、桩仍占用，不重启、不换桩、
    /// 不释放、不改派、不移动。之后出口变得可用（重启后），同一组事实照常隔离。
    /// </summary>
    [Theory]
    [MemberData(nameof(AlarmOnlyShapes))]
    public async Task WithoutAWayBackFromAPauseItIsOnlyAlarmedAndNothingMoves(string shape)
    {
        await using FleetFixture fleet = await FleetAsync();
        JourneyRuntimeRow journey = await ChargingAsync(fleet, battery: 50);
        if (shape == "interruption")
        {
            AtCharger(fleet, KeyA, 50, NotCharging);
        }
        for (int round = 0; round < 40; round++)
        {
            await LongRoundAsync(fleet, TimeSpan.FromSeconds(30));
        }

        Assert.Empty(await StationHoldsAsync(fleet));
        Assert.Empty(await VehicleHoldsAsync(fleet));
        EventRecordingLogger<JourneyRuntimeEngine>.Entry said = Assert.Single(fleet.EngineLog.Entries, entry => entry.EventId.Id == 2278);
        Assert.Contains(StationClearanceExit.NoRecoveryEntry, said.Message, StringComparison.Ordinal);
        Assert.Equal(
            shape == "interruption" ? ChargingExecutionReasons.InterruptionNotIsolated : ChargingExecutionReasons.NoProgressNotIsolated,
            (await ChargingJourneyAsync(fleet, AgvA))!.BlockReasonCode);
        ChargingCycleRow cycle = await OpenCycleAsync(fleet);
        Assert.Equal((ChargingCyclePhases.Active, ChargingCycleWireStates.Charging), (cycle.Phase, cycle.WireState));
        Assert.Equal((VehiclePurposes.Charging, journey.JourneyId), (await ClaimOfAsync(fleet, KeyA))!.Value);
        Assert.Equal((KeyA, StationExclusivityStates.Occupied), await HolderAsync(fleet, Near.StationId));
        Assert.Single(fleet.Riot.Creates);
        Assert.Empty(fleet.Riot.OrderCommands);
        Assert.Empty(fleet.Riot.EmergencyCommands);

        fleet.HostRecoveryEntryOffered = true;
        await fleet.RecreateEngineAsync();
        for (int round = 0; round < 3; round++)
        {
            await LongRoundAsync(fleet, TimeSpan.FromSeconds(30));
        }
        Assert.Single(await StationHoldsAsync(fleet));
        Assert.Equal(ChargingCyclePhases.Clearing, (await OpenCycleAsync(fleet)).Phase);
    }

    /// <summary>
    /// 只告警的中断之后车又充上了：只告警的码清掉，不再挂在看板上；再次中断时再告警一次。
    /// </summary>
    [Fact]
    public async Task AnAlarmOnlyInterruptionIsClearedWhenChargingResumesAndSaidAgainOnTheNext()
    {
        await using FleetFixture fleet = await FleetAsync();
        await ChargingAsync(fleet, battery: 50);
        await StopChargingAsync(fleet, 50);
        Assert.Equal(ChargingExecutionReasons.InterruptionNotIsolated, (await ChargingJourneyAsync(fleet, AgvA))!.BlockReasonCode);

        AtCharger(fleet, KeyA, 51, Charging);
        await RoundAsync(fleet);
        await RoundAsync(fleet);
        Assert.Null((await ChargingJourneyAsync(fleet, AgvA))!.BlockReasonCode);

        await StopChargingAsync(fleet, 52);
        Assert.Equal(2, fleet.EngineLog.Entries.Count(entry => entry.EventId.Id == 2278));
    }

    /// <summary>
    /// 只告警的中断有出口（调度 10-02 第 3 点）：有人把车挪开、确认清桩——周期以 <c>CHARGING_CLEARED_BY_OPERATOR</c> 收尾、211 放开、旅程收尾，
    /// 没有任何暂停；这辆车之后又低于强制充电线时照常拿到新的充电承诺。
    /// </summary>
    [Fact]
    public async Task AnAlarmOnlyInterruptionIsLeftByAManualClearanceAndTheVehicleIsCommittedAgainLater()
    {
        await using FleetFixture fleet = await FleetAsync();
        await ChargingAsync(fleet, battery: 50);
        await StopChargingAsync(fleet, 50);
        Assert.Equal(ChargingExecutionReasons.InterruptionNotIsolated, (await ChargingJourneyAsync(fleet, AgvA))!.BlockReasonCode);

        MovedOff(fleet);
        Assert.True((await ClearanceFor(fleet).DecideAsync(ClearanceRequest(), Token)).StationReleased);
        await RoundAsync(fleet);
        Assert.Equal(
            (JourneyRuntimeStage.Completed, ChargingExecutionReasons.ClearedByOperator),
            ((await ChargingJourneyAsync(fleet, AgvA))!.Stage, (await CycleAsync(fleet)).EndReason));
        Assert.Null(await HolderAsync(fleet, Near.StationId));
        Assert.Empty(await StationHoldsAsync(fleet));
        Assert.Empty(await VehicleHoldsAsync(fleet));

        fleet.Riot.BatteryByVehicle[KeyA] = 20;
        await RoundAsync(fleet);
        Assert.Equal(ChargingAllocationReasons.Committed, fleet.ChargingBoard.Verdicts[AgvA].Reason);
        Assert.Equal((KeyA, StationExclusivityStates.Reserved), await HolderAsync(fleet, Near.StationId));
    }

    /// <summary>
    /// 只告警的无进展有出口：车又涨起来、涨到完成阈值——照常 <c>COMPLETE</c>、放开用途，只告警的码不再挂着；桩按充满离桩的三项确认释放，与平常一样。
    /// </summary>
    [Fact]
    public async Task AnAlarmOnlyNoProgressThatGainsAgainCompletesAsUsual()
    {
        await using FleetFixture fleet = await FleetAsync();
        await ChargingAsync(fleet, battery: 50);
        for (int round = 0; round < 40; round++)
        {
            await LongRoundAsync(fleet, TimeSpan.FromSeconds(30));
        }
        Assert.Equal(ChargingExecutionReasons.NoProgressNotIsolated, (await ChargingJourneyAsync(fleet, AgvA))!.BlockReasonCode);

        for (int step = 1; step <= 6; step++)
        {
            AtCharger(fleet, KeyA, 50 + (step * 5), Charging);
            await LongRoundAsync(fleet, TimeSpan.FromSeconds(30));
        }

        Assert.Equal(ChargingCycleWireStates.Complete, (await CycleAsync(fleet)).WireState);
        Assert.Equal(
            (JourneyRuntimeStage.Completed, ChargingExecutionReasons.Completed),
            ((await ChargingJourneyAsync(fleet, AgvA))!.Stage, (await ChargingJourneyAsync(fleet, AgvA))!.BlockReasonCode));
        Assert.Null(await ClaimOfAsync(fleet, KeyA));

        MovedOff(fleet);
        await RoundAsync(fleet);
        Assert.Null(await HolderAsync(fleet, Near.StationId));
    }

    /// <summary>
    /// 只告警的 S-d 有出口：车留在桩上、一直不交桩；有人把车挪开（三项确认都在）——211 按充满离桩释放，下一轮这辆车照常拿到新的充电承诺。
    /// </summary>
    [Fact]
    public async Task AnAlarmOnlySecondRechargeReleasesTheChargerOnceTheVehicleLeavesAndItIsCommittedAgain()
    {
        await using FleetFixture fleet = await FleetAsync();
        await RechargedOnceAndFullAgainAsync(fleet);
        await StopChargingAsync(fleet, 20);
        Assert.Equal(ChargingAllocationReasons.VehicleStillHoldsCharger, fleet.ChargingBoard.Verdicts[AgvA].Reason);

        fleet.Riot.VehicleOverrides[KeyA] = seen => seen with { CurrentStationId = 300, BatteryState = NotCharging };
        await RoundAsync(fleet);
        await RoundAsync(fleet);

        Assert.Equal(ChargingAllocationReasons.Committed, fleet.ChargingBoard.Verdicts[AgvA].Reason);
        Assert.Equal((KeyA, StationExclusivityStates.Reserved), await HolderAsync(fleet, Near.StationId));
        Assert.Empty(await StationHoldsAsync(fleet));
    }

    /// <summary>
    /// 无进展可能在车「仍在充电、只是涨得慢」时形成（调度 10-02 第 2 点）：进了清桩中之后车还报 <c>CHARGING</c>——旅程写
    /// <c>CHARGING_CLEARING_VEHICLE_STILL_CHARGING</c>、告警恰好一次（事件 2280），清桩确认被拒、桩不放；现场结束充电之后码换回
    /// <c>CHARGING_NO_PROGRESS_CONFIRMED</c>，挪车、确认，清桩完成。
    /// </summary>
    [Fact]
    public async Task AVehicleStillChargingWhileClearingIsNamedAndWarnedOnceUntilTheChargingIsEnded()
    {
        await using FleetFixture fleet = await IsolatingFleetAsync();
        await ChargingAsync(fleet, battery: 50);
        for (int round = 0; round < 40 && (await StationHoldsAsync(fleet)).Length == 0; round++)
        {
            // Slow: one percent every five minutes, below the 3 % the window asks for.
            AtCharger(fleet, KeyA, 50 + (round / 10), Charging);
            await LongRoundAsync(fleet, TimeSpan.FromSeconds(30));
        }
        Assert.Equal(ChargingStationHoldTriggers.NoProgressConfirmed, Assert.Single(await StationHoldsAsync(fleet)).Trigger);

        for (int minute = 0; minute < 15; minute++)
        {
            await LongRoundAsync(fleet, TimeSpan.FromMinutes(1));
        }
        Assert.Equal(ChargingExecutionReasons.ClearingVehicleStillCharging, (await ChargingJourneyAsync(fleet, AgvA))!.BlockReasonCode);
        Assert.Single(fleet.EngineLog.Entries, entry => entry.EventId.Id == 2280);
        ManualStationClearanceConfirmation refused = await ClearanceFor(fleet).DecideAsync(ClearanceRequest(), Token);
        Assert.Equal((FieldConfirmationDecision.Rejected, false), (refused.Decision.Outcome, refused.StationReleased));
        Assert.Equal((KeyA, StationExclusivityStates.Occupied), await HolderAsync(fleet, Near.StationId));

        AtCharger(fleet, KeyA, 53, NotCharging);
        await RoundAsync(fleet);
        Assert.Equal(ChargingExecutionReasons.NoProgressClearing, (await ChargingJourneyAsync(fleet, AgvA))!.BlockReasonCode);

        MovedOff(fleet);
        ManualStationClearanceConfirmation confirmed = await ClearanceFor(fleet).DecideAsync(
            ClearanceRequest("00000000-0000-4000-8000-000000000408"), Token);
        Assert.True(confirmed.StationReleased);
        Assert.Single(fleet.EngineLog.Entries, entry => entry.EventId.Id == 2280);
    }

    // ---- 重放、重启、崩溃点、与人工清桩同一刻 ----------------------------------------------------------------------------

    /// <summary>
    /// 同一次中断被之后每一轮看到、进程重启后再跑：两侧各只有一条暂停，告警只有形成那一次；重启后不重新判、不写第二条。
    /// </summary>
    [Fact]
    public async Task OneInterruptionSeenRoundAfterRoundAndAcrossARestartIsOnePauseEachSide()
    {
        await using FleetFixture fleet = await IsolatingFleetAsync();
        await ChargingAsync(fleet, battery: 50);
        await StopChargingAsync(fleet, 50);
        for (int round = 0; round < 6; round++)
        {
            await RoundAsync(fleet);
        }
        await fleet.RecreateEngineAsync();
        for (int round = 0; round < 6; round++)
        {
            await RoundAsync(fleet);
        }

        Assert.Single(await StationHoldsAsync(fleet));
        Assert.Single(await VehicleHoldsAsync(fleet));
        Assert.Single(fleet.EngineLog.Entries, entry => entry.EventId.Id == 2276);
        Assert.Single(await fleet.Context.Set<StationClearanceRow>().AsNoTracking().ToArrayAsync(Token));
    }

    /// <summary>
    /// 崩溃点：「写两种暂停」与「转清桩中」同一次保存，写车的资格暂停那一刻崩掉：三样都不留（没有桩暂停、没有车暂停、周期仍在充电、用途仍是
    /// <c>CHARGING</c>、没有清桩记录）；下一轮按同一组事实重新判出同一条，幂等键不变。
    /// </summary>
    [Fact]
    public async Task ACrashWhileWritingTheVehiclePauseLeavesNothingAndTheSamePausesAreFormedNextRound()
    {
        ChargingUnableToChargeTests.FailOnce crash = new("INSERT INTO \"VehicleChargingEligibilityHolds\"");
        await using FleetFixture fleet = await IsolatingFleetAsync(commands: crash);
        await ChargingAsync(fleet, battery: 50);
        AtCharger(fleet, KeyA, 50, NotCharging);
        await LongRoundAsync(fleet, TimeSpan.FromSeconds(30));
        await LongRoundAsync(fleet, TimeSpan.FromSeconds(30));

        crash.Armed = true;
        await Assert.ThrowsAnyAsync<Exception>(() => LongRoundAsync(fleet, TimeSpan.FromSeconds(30)));
        Assert.Equal(1, crash.Fired);
        Assert.Empty(await StationHoldsAsync(fleet));
        Assert.Empty(await VehicleHoldsAsync(fleet));
        Assert.Equal(ChargingCyclePhases.Active, (await OpenCycleAsync(fleet)).Phase);
        Assert.Equal(VehiclePurposes.Charging, (await ClaimOfAsync(fleet, KeyA))!.Value.Purpose);
        Assert.Empty(await fleet.Context.Set<StationClearanceRow>().AsNoTracking().ToArrayAsync(Token));

        for (int round = 0; round < 3; round++)
        {
            await RoundAsync(fleet);
        }

        ChargingCycleRow cycle = await OpenCycleAsync(fleet);
        string dedup = ChargingStationHoldTriggers.InterruptionConfirmed + "|" + cycle.CycleId;
        Assert.Equal(
            JourneyPlanBuilder.StableGuid(dedup, "charging-station-hold"), Assert.Single(await StationHoldsAsync(fleet)).IdempotencyKey);
        Assert.Equal(
            JourneyPlanBuilder.StableGuid(dedup, "vehicle-charging-eligibility-hold"), Assert.Single(await VehicleHoldsAsync(fleet)).IdempotencyKey);
        Assert.Equal(ChargingCyclePhases.Clearing, cycle.Phase);
    }

    /// <summary>
    /// 中断确认与人工清桩确认同一刻：形成那一轮之后紧接着有人确认清桩（车已挪开），再跑几轮——暂停事件不重复写（两侧各一条），清桩完成一次、桩放一次。
    /// </summary>
    [Fact]
    public async Task AnInterruptionAndAManualClearanceInTheSameMomentWriteEachThingOnce()
    {
        await using FleetFixture fleet = await IsolatingFleetAsync();
        await ChargingAsync(fleet, battery: 50);
        await StopChargingAsync(fleet, 50);
        MovedOff(fleet);
        ManualStationClearanceConfirmation decided = await ClearanceFor(fleet).DecideAsync(ClearanceRequest(), Token);
        Assert.True(decided.StationReleased);
        for (int round = 0; round < 4; round++)
        {
            await RoundAsync(fleet);
        }

        Assert.Single(await StationHoldsAsync(fleet));
        Assert.Single(await VehicleHoldsAsync(fleet));
        Assert.Single(fleet.EngineLog.Entries, entry => entry.EventId.Id == 2276);
        StationClearanceRow clearance = Assert.Single(await fleet.Context.Set<StationClearanceRow>().AsNoTracking().ToArrayAsync(Token));
        Assert.NotNull(clearance.CompletedAt);
        Assert.Single(
            await fleet.Context.Set<StationExclusivityRecordRow>().AsNoTracking()
                .Where(row => row.ReleaseReason == ChargingExecutionReasons.ChargerReleasedOnManualClearance).ToArrayAsync(Token));
        Assert.Equal(JourneyRuntimeStage.Completed, (await ChargingJourneyAsync(fleet, AgvA))!.Stage);
    }

    /// <summary>
    /// 清桩中「等人确认」时看板上的码说的是为什么在清桩：中断之后、没人确认的每一轮都是 <c>CHARGING_INTERRUPTION_CONFIRMED</c>，不被改写成
    /// 「充不上」的 <c>CHARGING_UNABLE_TO_CHARGE</c>；清桩中不建单、不发命令。
    /// </summary>
    [Fact]
    public async Task WhileClearingAfterAnInterruptionTheCodeKeepsSayingWhyAndNothingIsSent()
    {
        await using FleetFixture fleet = await IsolatingFleetAsync();
        await ChargingAsync(fleet, battery: 50);
        AtCharger(fleet, KeyA, 50, NotCharging);
        for (int minute = 0; minute < 20; minute++)
        {
            await LongRoundAsync(fleet, TimeSpan.FromMinutes(1));
        }

        Assert.Equal(ChargingExecutionReasons.InterruptionClearing, (await ChargingJourneyAsync(fleet, AgvA))!.BlockReasonCode);
        Assert.Single(fleet.Riot.Creates);
        Assert.Empty(fleet.Riot.OrderCommands);
        Assert.Empty(fleet.Riot.EmergencyCommands);
        Assert.Equal((KeyA, StationExclusivityStates.Occupied), await HolderAsync(fleet, Near.StationId));
    }

    // ---- S-d：原桩反复重充 -----------------------------------------------------------------------------------------------

    /// <summary>
    /// S-d：充满后一直留在桩上的车，第一次电量掉回强制充电线以下照常原桩重充（cs#405 S6）；充满之后又掉下来、中间没做过别的事——第二次不再交桩、
    /// 不在原桩再充、不分别的桩，只暂停车的充电资格（<c>NO_PROGRESS_CONFIRMED</c>），告警恰好一次（事件 2277）。车留在原地，桩仍是那个充满周期的占用。
    /// </summary>
    [Fact]
    public async Task ASecondRechargeOnTheChargerItNeverLeftIsNoProgressAndPausesTheVehicle()
    {
        await using FleetFixture fleet = await IsolatingFleetAsync(Near, Far);
        await RechargedOnceAndFullAgainAsync(fleet);
        ChargingCycleRow full = await CycleAsync(fleet);

        AtCharger(fleet, KeyA, 20, NotCharging);
        for (int round = 0; round < 4; round++)
        {
            await RoundAsync(fleet);
        }

        Assert.Equal(2, fleet.Riot.Creates.Count);
        ChargingCycleRow still = await fleet.Context.Set<ChargingCycleRow>().AsNoTracking()
            .SingleAsync(row => row.CycleId == full.CycleId, Token);
        Assert.Equal((ChargingCyclePhases.Active, ChargingCycleWireStates.Complete), (still.Phase, still.WireState));
        StationExclusivityRow holder = (await StationAsync(fleet, Near.StationId))!;
        Assert.Equal((KeyA, full.JourneyId), (holder.VehicleKey, holder.JourneyId));
        Assert.Null(await HolderAsync(fleet, Far.StationId));
        VehicleChargingEligibilityHoldRow paused = Assert.Single(await VehicleHoldsAsync(fleet));
        Assert.Equal((VehicleChargingEligibilityHoldReasons.NoProgressConfirmed, full.CycleId), (paused.Reason, paused.CycleId));
        Assert.Single(fleet.ChargingLog.Entries, entry => entry.EventId.Id == 2277);
        Assert.Equal(ChargingAllocationReasons.VehicleEligibilityHeld, fleet.ChargingBoard.Verdicts[AgvA].Reason);
    }

    /// <summary>S-d 在隔离出口不可用时：同样不交桩、不再充、不换桩，只告警一次（写明未隔离），不写暂停。</summary>
    [Fact]
    public async Task ASecondRechargeWithoutAWayBackFromAPauseIsOnlyAlarmedAndNotCharged()
    {
        await using FleetFixture fleet = await FleetAsync(chargers: [Near, Far]);
        await RechargedOnceAndFullAgainAsync(fleet);

        AtCharger(fleet, KeyA, 20, NotCharging);
        for (int round = 0; round < 4; round++)
        {
            await RoundAsync(fleet);
        }

        Assert.Equal(2, fleet.Riot.Creates.Count);
        Assert.Empty(await StationHoldsAsync(fleet));
        Assert.Empty(await VehicleHoldsAsync(fleet));
        EventRecordingLogger<ChargingAllocator>.Entry said = Assert.Single(fleet.ChargingLog.Entries, entry => entry.EventId.Id == 2277);
        Assert.Contains("NOT ISOLATED", said.Message, StringComparison.Ordinal);
        Assert.Equal(ChargingAllocationReasons.VehicleStillHoldsCharger, fleet.ChargingBoard.Verdicts[AgvA].Reason);
        Assert.Null(await HolderAsync(fleet, Far.StationId));
    }

    /// <summary>
    /// S-d 的窗口从资格恢复起重新算：车的资格恢复之后，下一次原桩重充照常进行——有人看过这辆车了，之前那一次不再算。
    /// </summary>
    [Fact]
    public async Task AfterTheVehicleIsRecoveredTheNextRechargeOnTheSameChargerGoesAhead()
    {
        await using FleetFixture fleet = await IsolatingFleetAsync();
        await RechargedOnceAndFullAgainAsync(fleet);
        AtCharger(fleet, KeyA, 20, NotCharging);
        await RoundAsync(fleet);
        ChargingHoldStore holds = new(fleet.Context);
        VehicleChargingEligibilityHoldRow hold = Assert.Single(await VehicleHoldsAsync(fleet));
        await holds.RecoverVehicleHoldAsync(
            new ChargingHoldRecovery("recovery-1", hold.HoldId, "op-r11", "维护管理员", fleet.Clock.GetUtcNowWithoutTick(), "电池已换"), Token);
        Assert.Empty(await StationHoldsAsync(fleet));
        fleet.Context.ChangeTracker.Clear();

        for (int round = 0; round < 3; round++)
        {
            await RoundAsync(fleet);
        }

        Assert.Equal(3, fleet.Riot.Creates.Count);
        Assert.Equal(ChargingCyclePhases.Active, (await OpenCycleAsync(fleet)).Phase);
    }

    /// <summary>
    /// S-d 只暂停车、不暂停桩（#442 审查 S1）：第二次原桩重充之后桩没有分配暂停；A 被挪开、桩按离桩三项确认释放，同一时刻低电的 B 分到的正是这个桩。
    /// A 仍分不到任何桩（资格暂停着）。
    /// </summary>
    [Fact]
    public async Task ARepeatedRechargePausesOnlyTheVehicleAndTheChargerGoesToTheNextVehicle()
    {
        await using FleetFixture fleet = await IsolatingFleetAsync(vehicles: 2);
        string keyB = FleetFixture.VehicleKeys[1];
        fleet.Riot.BatteryByVehicle[keyB] = 80;
        await RechargedOnceAndFullAgainAsync(fleet);
        AtCharger(fleet, KeyA, 20, NotCharging);
        await RoundAsync(fleet);
        await RoundAsync(fleet);
        Assert.Single(await VehicleHoldsAsync(fleet));
        Assert.Empty(await StationHoldsAsync(fleet));

        fleet.Riot.VehicleOverrides[KeyA] = seen => seen with { CurrentStationId = 300, BatteryState = NotCharging };
        fleet.Riot.BatteryByVehicle[keyB] = 20;
        for (int round = 0; round < 4 && (await HolderAsync(fleet, Near.StationId))?.VehicleKey != keyB; round++)
        {
            await RoundAsync(fleet);
        }

        Assert.Equal((keyB, StationExclusivityStates.Reserved), await HolderAsync(fleet, Near.StationId));
        Assert.Equal(ChargingAllocationReasons.VehicleEligibilityHeld, fleet.ChargingBoard.Verdicts[AgvA].Reason);
        Assert.Empty(await StationHoldsAsync(fleet));
    }

    /// <summary>
    /// S-d 的窗口在车离开过桩之后重新算（#442 审查 S1，审查探针 <c>ProbeFirstRechargeAfterTheVehicleLeftTheChargerIsNotASecondRecharge</c> 收作正式用例）：
    /// 第一段在原桩重充过一次；车被挪开、桩按离桩释放，车经正常分配链重新承诺、建单；它回到桩上充满之后掉下来的第一次原桩重充照常进行，不算第二次、
    /// 什么也不暂停。
    /// </summary>
    [Fact]
    public async Task TheFirstRechargeAfterTheVehicleLeftTheChargerIsNotASecondRecharge()
    {
        await using FleetFixture fleet = await IsolatingFleetAsync();
        await RechargedOnceAndFullAgainAsync(fleet);

        fleet.Riot.BatteryByVehicle[KeyA] = 20;
        fleet.Riot.VehicleOverrides[KeyA] = seen => seen with { CurrentStationId = 300, BatteryState = NotCharging };
        for (int round = 0; round < 4 && fleet.Riot.Creates.Count < 3; round++)
        {
            await RoundAsync(fleet);
        }
        Assert.Equal(3, fleet.Riot.Creates.Count);

        JourneyRuntimeRow fresh = (await ChargingJourneyAsync(fleet, AgvA))!;
        fleet.Riot.CompleteOrder(fresh.PickupUpperId);
        AtCharger(fleet, KeyA, 60, Charging);
        await RoundAsync(fleet);
        await RoundAsync(fleet);
        AtCharger(fleet, KeyA, 80, Charging);
        await RoundAsync(fleet);
        Assert.Equal((fresh.JourneyId, ChargingCycleWireStates.Complete), ((await CycleAsync(fleet)).JourneyId, (await CycleAsync(fleet)).WireState));

        AtCharger(fleet, KeyA, 20, NotCharging);
        for (int round = 0; round < 4; round++)
        {
            await RoundAsync(fleet);
        }

        Assert.Empty(await StationHoldsAsync(fleet));
        Assert.Empty(await VehicleHoldsAsync(fleet));
        Assert.DoesNotContain(fleet.ChargingLog.Entries, entry => entry.EventId.Id == 2277);
        Assert.Equal(4, fleet.Riot.Creates.Count);
    }

    /// <summary>
    /// S-d 只看紧挨着的前一个周期（#442 审查 S1）：原桩重充过一次之后，中间夹着一个以失败收尾的充电周期（这里直接写库构造：分配时刻在两者之间、
    /// <c>CHARGING_ORDER_FAILED</c> 收尾、晚于那次重充结束），再到当前这个充满周期——它不是重充接上来的，掉回强充线以下照常原桩重充，什么也不暂停。
    /// 单看「起算点之后有没有过重充」会把它判成第二次。
    /// </summary>
    [Fact]
    public async Task ARechargeLongAgoBehindAnotherCycleDoesNotMakeThisOneASecond()
    {
        await using FleetFixture fleet = await IsolatingFleetAsync();
        await RechargedOnceAndFullAgainAsync(fleet);
        ChargingCycleRow[] cycles = await fleet.Context.Set<ChargingCycleRow>().AsNoTracking()
            .Where(row => row.VehicleKey == KeyA).ToArrayAsync(Token);
        ChargingCycleRow recharged = Assert.Single(cycles, row => row.EndReason == ChargingExecutionReasons.RechargedOnHeldCharger);
        ChargingCycleRow current = Assert.Single(cycles, row => row.Phase != ChargingCyclePhases.Ended);
        fleet.Context.Add(new ChargingCycleRow
        {
            CycleId = "cs407-failed-between",
            VehicleKey = KeyA,
            JourneyId = "charging:cs407-failed-between",
            MapId = current.MapId,
            StationId = current.StationId,
            ChargerRosterVersion = current.ChargerRosterVersion,
            ChargingPolicyVersion = current.ChargingPolicyVersion,
            WireState = ChargingCycleWireStates.NotCharging,
            Phase = ChargingCyclePhases.Ended,
            AllocatedAt = recharged.AllocatedAt + ((current.AllocatedAt - recharged.AllocatedAt) / 2),
            EndedAt = recharged.EndedAt!.Value + TimeSpan.FromMilliseconds(1),
            EndReason = ChargingExecutionReasons.OrderFailed,
        });
        await fleet.Context.SaveChangesAsync(Token);
        fleet.Context.ChangeTracker.Clear();

        AtCharger(fleet, KeyA, 20, NotCharging);
        for (int round = 0; round < 3; round++)
        {
            await RoundAsync(fleet);
        }

        Assert.Empty(await VehicleHoldsAsync(fleet));
        Assert.DoesNotContain(fleet.ChargingLog.Entries, entry => entry.EventId.Id == 2277);
        Assert.Equal(
            ChargingExecutionReasons.RechargedOnHeldCharger,
            (await fleet.Context.Set<ChargingCycleRow>().AsNoTracking().SingleAsync(row => row.CycleId == current.CycleId, Token)).EndReason);
    }

    // ---- S-e：到桩未证实一类的码按旅程计时 -------------------------------------------------------------------------------

    /// <summary>
    /// S-e：单已 <c>SUCCESS</c>，车读数时有时无——一轮读不到（<c>CHARGING_VEHICLE_OBSERVATION_LOST</c>）、一轮读到在别处（<c>CHARGING_ARRIVAL_NOT_PROVEN</c>），
    /// 四十分钟。码来回换，开始时刻不随之重置（仍是第一次缺失的那一刻），过了十分钟窗口升级告警（事件 2261）恰好一次。
    /// </summary>
    [Fact]
    public async Task FlippingBetweenTheEvidenceMissingCodesKeepsOneStartAndWarnsOnce()
    {
        await using FleetFixture fleet = await IsolatingFleetAsync();
        await EnRouteAsync(fleet);
        JourneyRuntimeRow journey = (await ChargingJourneyAsync(fleet, AgvA))!;
        fleet.Riot.CompleteOrder(journey.PickupUpperId);

        DateTimeOffset? since = null;
        for (int minute = 0; minute < 40; minute++)
        {
            bool offline = minute % 2 == 0;
            fleet.Riot.BatteryByVehicle[KeyA] = 20;
            fleet.Riot.VehicleOverrides[KeyA] = seen => seen with { CurrentStationId = 300, BatteryState = NotCharging, Connected = !offline };
            await LongRoundAsync(fleet, TimeSpan.FromMinutes(1));
            JourneyRuntimeRow row = (await ChargingJourneyAsync(fleet, AgvA))!;
            Assert.Equal(
                offline ? ChargingExecutionReasons.VehicleObservationLost : ChargingExecutionReasons.ArrivalNotProven,
                row.BlockReasonCode);
            since ??= row.BlockReasonSince;
            Assert.Equal(since, row.BlockReasonSince);
        }

        Assert.Single(fleet.EngineLog.Entries, entry => entry.EventId.Id == 2261);
        Assert.Equal(ChargingCycleWireStates.EnRoute, (await OpenCycleAsync(fleet)).WireState);
    }

    /// <summary>S-e 的另一半：证据真的回来（到桩成立）之后码清掉；再缺失是新的一次，重新起算，过了窗口再告警一次。</summary>
    [Fact]
    public async Task EvidenceThatComesBackEndsTheEpisodeAndTheNextOneIsTimedAfresh()
    {
        await using FleetFixture fleet = await IsolatingFleetAsync();
        await EnRouteAsync(fleet);
        JourneyRuntimeRow journey = (await ChargingJourneyAsync(fleet, AgvA))!;
        fleet.Riot.CompleteOrder(journey.PickupUpperId);
        fleet.Riot.VehicleOverrides[KeyA] = seen => seen with { CurrentStationId = 300, BatteryState = NotCharging };
        for (int minute = 0; minute < 12; minute++)
        {
            await LongRoundAsync(fleet, TimeSpan.FromMinutes(1));
        }
        Assert.Single(fleet.EngineLog.Entries, entry => entry.EventId.Id == 2261);

        AtCharger(fleet, KeyA, 20, Charging);
        await RoundAsync(fleet);
        Assert.Null((await ChargingJourneyAsync(fleet, AgvA))!.BlockReasonCode);

        fleet.Riot.VehicleOverrides[KeyA] = seen => seen with { CurrentStationId = Near.StationId, BatteryState = Charging, Connected = false };
        for (int minute = 0; minute < 12; minute++)
        {
            await LongRoundAsync(fleet, TimeSpan.FromMinutes(1));
        }
        Assert.Equal(2, fleet.EngineLog.Entries.Count(entry => entry.EventId.Id == 2261));
    }

    /// <summary>
    /// 同一趟旅程的第二段证据缺失照样升级告警（#442 审查 S2，审查探针 <c>ProbeASecondEvidenceEpisodeAfterChargingStartedStillEscalates</c> 收作正式用例）：
    /// 到桩后还没充上时车辆观测丢失十二分钟（2261 一次）；读到充电、开始充电，码清掉；之后电量遥测丢失十二分钟——2262 恰好一次。
    /// </summary>
    [Fact]
    public async Task ASecondEvidenceEpisodeAfterChargingStartedStillEscalates()
    {
        await using FleetFixture fleet = await FleetAsync();
        await EnRouteAsync(fleet);
        JourneyRuntimeRow journey = (await ChargingJourneyAsync(fleet, AgvA))!;
        fleet.Riot.CompleteOrder(journey.PickupUpperId);
        AtCharger(fleet, KeyA, 20, NotCharging);
        await RoundAsync(fleet);
        Assert.NotNull((await OpenCycleAsync(fleet)).ArrivedAt);

        fleet.Riot.VehicleOverrides[KeyA] = seen => seen with { CurrentStationId = Near.StationId, BatteryState = NotCharging, Connected = false };
        for (int minute = 0; minute < 12; minute++)
        {
            await LongRoundAsync(fleet, TimeSpan.FromMinutes(1));
        }
        Assert.Single(fleet.EngineLog.Entries, entry => entry.EventId.Id == 2261);

        AtCharger(fleet, KeyA, 30, Charging);
        await RoundAsync(fleet);
        Assert.Null((await ChargingJourneyAsync(fleet, AgvA))!.BlockReasonCode);
        Assert.Equal(ChargingCycleWireStates.Charging, (await OpenCycleAsync(fleet)).WireState);

        fleet.Riot.VehicleOverrides[KeyA] = seen => seen with { CurrentStationId = Near.StationId, BatteryState = Charging, BatteryPercent = null };
        for (int minute = 0; minute < 12; minute++)
        {
            await LongRoundAsync(fleet, TimeSpan.FromMinutes(1));
        }

        Assert.Equal(ChargingExecutionReasons.BatteryTelemetryLost, (await ChargingJourneyAsync(fleet, AgvA))!.BlockReasonCode);
        Assert.Single(fleet.EngineLog.Entries, entry => entry.EventId.Id == 2262);
    }

    // ---- 夹具 ------------------------------------------------------------------------------------------------------------

    /// <summary>Host 恢复入口可用的一队车：中断与无进展会隔离。</summary>
    private static async Task<FleetFixture> IsolatingFleetAsync(params ChargerRosterEntry[] chargers) =>
        await IsolatingFleetAsync(chargers.Length == 0 ? null : chargers, null);

    private static async Task<FleetFixture> IsolatingFleetAsync(
        ChargerRosterEntry[]? chargers = null,
        Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor? commands = null,
        int vehicles = 1)
    {
        FleetFixture fleet = await FleetAsync(vehicles: vehicles, chargers: chargers, commands: commands);
        fleet.HostRecoveryEntryOffered = true;
        await fleet.RecreateEngineAsync();
        return fleet;
    }

    /// <summary>
    /// 车在桩上不再充电、电量 <paramref name="battery"/>：每轮 30 秒跑三轮——第一次读到只开始观察，60 秒（<c>ChargingInterruptionConfirmAfter</c> 的默认值）
    /// 那一轮形成中断（出口可用时隔离，否则只告警）。
    /// </summary>
    private static async Task StopChargingAsync(FleetFixture fleet, int battery)
    {
        AtCharger(fleet, KeyA, battery, NotCharging);
        for (int round = 0; round < 3; round++)
        {
            await LongRoundAsync(fleet, TimeSpan.FromSeconds(30));
        }
    }

    /// <summary>一轮，时钟走 <paramref name="step"/>；每辆车先说一句话。</summary>
    private static async Task LongRoundAsync(FleetFixture fleet, TimeSpan step)
    {
        await fleet.HearFromEveryVehicleAsync();
        await fleet.RunRoundAsync(step);
    }

    private static async Task EnRouteAsync(FleetFixture fleet)
    {
        fleet.Riot.BatteryByVehicle[KeyA] = 20;
        await CommittedAndSentAsync(fleet);
        Assert.Equal(ChargingCycleWireStates.EnRoute, (await OpenCycleAsync(fleet)).WireState);
    }

    /// <summary>单到 <c>SUCCESS</c>、车停在 211 上报 <c>CHARGING</c>：到桩并开始充电，周期 <c>CHARGING</c>。</summary>
    private static async Task<JourneyRuntimeRow> ChargingAsync(FleetFixture fleet, int battery)
    {
        await EnRouteAsync(fleet);
        JourneyRuntimeRow journey = (await ChargingJourneyAsync(fleet, AgvA))!;
        fleet.Riot.CompleteOrder(journey.PickupUpperId);
        AtCharger(fleet, KeyA, battery, Charging);
        await RoundAsync(fleet);
        Assert.Equal(ChargingCycleWireStates.Charging, (await OpenCycleAsync(fleet)).WireState);
        return journey;
    }

    /// <summary>每轮 30 秒地跑，直到无进展窗口开了；答它的起点。</summary>
    private static async Task<DateTimeOffset> RunUntilTheWindowOpensAsync(FleetFixture fleet)
    {
        for (int round = 0; round < 20; round++)
        {
            await LongRoundAsync(fleet, TimeSpan.FromSeconds(30));
            if ((await OpenCycleAsync(fleet)).ObservationWindowStartedAt is { } start)
            {
                return start;
            }
        }
        throw new InvalidOperationException("The observation window never opened.");
    }

    /// <summary>中断、隔离，车被挪开、人工清桩确认（旧单已 SUCCESS，当场放桩），下一轮旅程收尾。</summary>
    private static async Task InterruptedAndClearedAsync(FleetFixture fleet)
    {
        await ChargingAsync(fleet, battery: 50);
        await StopChargingAsync(fleet, 50);
        Assert.Equal(ChargingCyclePhases.Clearing, (await OpenCycleAsync(fleet)).Phase);
        MovedOff(fleet);
        Assert.True((await ClearanceFor(fleet).DecideAsync(ClearanceRequest(), Token)).StationReleased);
        await RoundAsync(fleet);
        Assert.Equal(JourneyRuntimeStage.Completed, (await ChargingJourneyAsync(fleet, AgvA))!.Stage);
        fleet.ChargingLog.Entries.Clear();
    }

    /// <summary>
    /// 充满、一直留在桩上；掉到强制充电线以下原桩重充一次（cs#405 S6），那一趟也充满。之后车仍停在 211 上，持有第二个周期的占用。
    /// </summary>
    private static async Task RechargedOnceAndFullAgainAsync(FleetFixture fleet)
    {
        await ChargingAsync(fleet, battery: 60);
        AtCharger(fleet, KeyA, 80, Charging);
        await RoundAsync(fleet);
        Assert.Equal(ChargingCycleWireStates.Complete, (await CycleAsync(fleet)).WireState);

        await StopChargingAsync(fleet, 20);
        JourneyRuntimeRow again = (await ChargingJourneyAsync(fleet, AgvA))!;
        Assert.Equal(2, fleet.Riot.Creates.Count);
        fleet.Riot.CompleteOrder(again.PickupUpperId);
        AtCharger(fleet, KeyA, 60, Charging);
        await RoundAsync(fleet);
        await RoundAsync(fleet);
        AtCharger(fleet, KeyA, 80, Charging);
        await RoundAsync(fleet);
        Assert.Equal(ChargingCycleWireStates.Complete, (await CycleAsync(fleet)).WireState);
        Assert.Equal(again.JourneyId, (await CycleAsync(fleet)).JourneyId);
        fleet.ChargingLog.Entries.Clear();
    }

    private static void AtCharger(FleetFixture fleet, string vehicleKey, int battery, string batteryState)
    {
        fleet.Riot.BatteryByVehicle[vehicleKey] = battery;
        fleet.Riot.VehicleOverrides[vehicleKey] = seen => seen with { CurrentStationId = Near.StationId, BatteryState = batteryState };
    }

    /// <summary>有人把车挪开了：RIoT 读到它在 300、没在充电。</summary>
    private static void MovedOff(FleetFixture fleet) =>
        fleet.Riot.VehicleOverrides[KeyA] = seen => seen with { CurrentStationId = 300, BatteryState = NotCharging };

    private static ManualStationClearance ClearanceFor(FleetFixture fleet) =>
        new(
            fleet.Context,
            new FieldConfirmationRequestStore(fleet.Context),
            new StationClearanceStore(fleet.Context),
            fleet.Riot,
            new FieldOperatorRoleRoster(Options.Create(fleet.ClearanceRoles)),
            Audit(fleet),
            Options.Create(fleet.Options),
            fleet.Clock,
            NullLogger<ManualStationClearance>.Instance);

    private static ManualStationClearanceRequest ClearanceRequest(string confirmationRequestId = "00000000-0000-4000-8000-000000000407") =>
        new(
            ManualStationClearanceSources.Onboard, AgvA, KeyA, confirmationRequestId, 1, Guid.NewGuid().ToString("D"),
            $"{Near.StationName}||fleet-r11|STATION_EMPTY", Near.StationName, null, "STATION_EMPTY", "fleet-r11", "BADGE",
            DateTimeOffset.Parse("2026-10-02T06:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
            DateTimeOffset.Parse("2026-10-02T06:00:00Z", System.Globalization.CultureInfo.InvariantCulture));

    private static GovernanceStore Audit(FleetFixture fleet) =>
        new(fleet.Context, new GovernanceDeploymentIdentity("deployment:8005-controlserver@test"), AuditRetentionPolicy.Default);

    private static DefaultHttpContext HttpContextFor(string? bearer)
    {
        DefaultHttpContext context = new();
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.Connection.LocalIpAddress = IPAddress.Loopback;
        if (bearer is not null)
        {
            context.Request.Headers.Authorization = $"Bearer {bearer}";
        }
        return context;
    }

    private static async Task<ChargingStationAllocationHoldRow[]> StationHoldsAsync(FleetFixture fleet) =>
        await fleet.Context.Set<ChargingStationAllocationHoldRow>().AsNoTracking().ToArrayAsync(Token);

    private static async Task<VehicleChargingEligibilityHoldRow[]> VehicleHoldsAsync(FleetFixture fleet) =>
        await fleet.Context.Set<VehicleChargingEligibilityHoldRow>().AsNoTracking().ToArrayAsync(Token);

    private static async Task<ChargingCycleRow> OpenCycleAsync(FleetFixture fleet) =>
        await fleet.Context.Set<ChargingCycleRow>().AsNoTracking()
            .SingleAsync(row => row.VehicleKey == KeyA && row.Phase != ChargingCyclePhases.Ended, Token);

    /// <summary>这辆车最近的那个充电周期（未结束的优先）。</summary>
    private static async Task<ChargingCycleRow> CycleAsync(FleetFixture fleet)
    {
        ChargingCycleRow[] cycles = await fleet.Context.Set<ChargingCycleRow>().AsNoTracking()
            .Where(row => row.VehicleKey == KeyA).ToArrayAsync(Token);
        return cycles.SingleOrDefault(row => row.Phase != ChargingCyclePhases.Ended)
               ?? cycles.OrderByDescending(row => row.AllocatedAt).First();
    }

    /// <summary>先答第一版；<see cref="Switch"/> 之后为新决定答第二版。冻结的版本按号读回。</summary>
    private sealed class SwitchingResolver(EffectiveChargingPolicy first, EffectiveChargingPolicy later) : IChargingPolicyResolver
    {
        private bool _switched;

        public void Switch() => _switched = true;

        public Task<VehicleChargingPolicyDecision> ResolveForNewDecisionAsync(string vehicleKey, CancellationToken cancellationToken) =>
            Task.FromResult(new VehicleChargingPolicyDecision(
                vehicleKey, ChargingPolicyCommissioningReasons.Effective, _switched ? later : first, null));

        public Task<ChargingPolicyVersion> ReadFrozenAsync(long version, CancellationToken cancellationToken) =>
            Task.FromResult(version == first.Policy.Version ? first.Policy : later.Policy);
    }
}
