using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Charging;
using ControlServer.Host.Runtime.Dispatch;
using ControlServer.Host.Runtime.Dispatch.Criteria;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using static ControlServer.Tests.ChargingAllocationTests;
using static ControlServer.Tests.ChargingExecutionTests;
using FleetFixture = ControlServer.Tests.MultiVehicleExecutionTests.FleetFixture;

namespace ControlServer.Tests;

/// <summary>
/// 到桩之后的充电周期（批次9-07，control-server#405）：到桩、充电中、充满、离桩与释放，失联三分，两个崩溃点、两种并发交错、重连补发，
/// 以及 <c>CV-AUTOMATIC-CHARGING-CYCLE</c> 的两条同名具名测试。夹具与路网同 <see cref="ChargingAllocationTests"/>：第一轮分配承诺，
/// 第二轮建单并确认（<c>EN_ROUTE</c>），之后由用例把 RIoT 上的单推到 <c>SUCCESS</c>、把车放到桩上。
/// </summary>
/// <remarks>
/// 整个类挂 <c>FP-IS-13</c>：这里的每一条都是服务端 <c>CHARGING_CLAIMS_THE_VEHICLE</c> 那一半的行为；带 <c>CV-AUTOMATIC-CHARGING-CYCLE</c>
/// 的两条是向量的同名具名测试。<c>FP-IS-13</c> 还有两条向量归批次9-08、9-12，所以它不进已实施集合。
/// </remarks>
[Trait("IntegrationSlice", "FP-IS-13")]
public sealed class ChargingCycleProgressTests
{
    private const string Vector = "CV-AUTOMATIC-CHARGING-CYCLE";
    private const int Map = 25;
    private const string Charging = "CHARGING";
    private const string NotCharging = "NO_CHARGE";
    private static readonly WaitingPointEntry WaitingPoint = new(Map, 214, "等待点214", true, []);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string AgvA => FleetFixture.AgvIds[0];
    private static string KeyA => FleetFixture.VehicleKeys[0];
    private static string AgvB => FleetFixture.AgvIds[1];
    private static string KeyB => FleetFixture.VehicleKeys[1];

    // ---- 到桩 ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// 到桩证据全满足（订单终态 <c>SUCCESS</c>、订单号、车、图、目标桩一致；车在线、空闲、当前图加当前站是这个桩、停稳、读数新鲜）：
    /// <c>CHARGER</c> 预占转占用，周期记下到桩时刻，计划里那条腿报 <c>ARRIVED</c>。读不到 <c>CHARGING</c> 之前周期仍是 <c>EN_ROUTE</c>；
    /// 不建任何单、不发任何命令。
    /// </summary>
    [Fact]
    public async Task WithEveryProofAtTheChargerTheReservationBecomesAnOccupancyAndTheLegArrives()
    {
        await using FleetFixture fleet = await FleetAsync();
        JourneyRuntimeRow journey = await EnRouteAsync(fleet);

        fleet.Riot.CompleteOrder(journey.PickupUpperId);
        AtCharger(fleet, KeyA, 40, NotCharging);
        await RoundAsync(fleet);

        Assert.Equal((KeyA, StationExclusivityStates.Occupied), await HolderAsync(fleet, Near.StationId));
        ChargingCycleRow cycle = await CycleAsync(fleet, KeyA);
        Assert.Equal(ChargingCycleWireStates.EnRoute, cycle.WireState);
        Assert.NotNull(cycle.ArrivedAt);
        Assert.Null(cycle.FirstChargingSeenAt);
        JsonElement plan = (await PayloadsAsync(fleet, AgvA, "UpcomingStopPlanSnapshot"))[^1];
        JsonElement leg = Assert.Single(plan.GetProperty("legs").EnumerateArray());
        Assert.Equal(
            (JourneyPlanBuilder.ChargerStopPurpose, "ARRIVED"),
            (leg.GetProperty("stopPurposeCategory").GetString(), leg.GetProperty("state").GetString()));
        Assert.Contains(
            fleet.Peer.Delivered, line => line.MessageId == ChargingJourneyShape.ArrivedPlanMessageId(journey.JourneyId));
        Assert.Single(fleet.Riot.Creates);
        Assert.Empty(fleet.Riot.OrderCommands);
        Assert.Equal(VehiclePurposes.Charging, (await ClaimOfAsync(fleet, KeyA))!.Value.Purpose);
    }

    public static TheoryData<string> MissingArrivalProofs =>
        ["order-not-terminal", "order-for-another-station", "vehicle-elsewhere", "vehicle-moving", "vehicle-reading-stale", "vehicle-busy-with-a-task"];

    /// <summary>
    /// 到桩证据缺任何一样，周期保持 <c>EN_ROUTE</c>、预占不转占用；等再久也不按超时推进（半小时、一轮一分钟），也不建单不发命令。
    /// </summary>
    [Theory]
    [MemberData(nameof(MissingArrivalProofs))]
    public async Task AMissingArrivalProofKeepsTheCycleEnRouteHoweverLongItWaits(string missing)
    {
        await using FleetFixture fleet = await FleetAsync();
        JourneyRuntimeRow journey = await EnRouteAsync(fleet);
        if (missing != "order-not-terminal")
        {
            fleet.Riot.CompleteOrder(journey.PickupUpperId);
        }
        if (missing == "order-for-another-station")
        {
            fleet.Riot.PutOrder(fleet.Riot.OrderOf(journey.PickupUpperId)! with { DestinationStationId = Far.StationId });
        }
        fleet.Riot.BatteryByVehicle[KeyA] = 40;
        fleet.Riot.VehicleOverrides[KeyA] = seen => missing switch
        {
            "vehicle-elsewhere" => seen with { CurrentStationId = 300, BatteryState = Charging },
            "vehicle-moving" => seen with { CurrentStationId = Near.StationId, BatteryState = Charging, Speed = 300, ProcState = "RUNNING" },
            "vehicle-reading-stale" => seen with
            {
                CurrentStationId = Near.StationId,
                BatteryState = Charging,
                ObservedAt = seen.ObservedAt - fleet.Options.MaximumEvidenceAge - TimeSpan.FromSeconds(1),
            },
            "vehicle-busy-with-a-task" => seen with { CurrentStationId = Near.StationId, BatteryState = Charging, OrderTaskId = "TASK-9" },
            _ => seen with { CurrentStationId = Near.StationId, BatteryState = Charging },
        };

        for (int minute = 0; minute < 30; minute++)
        {
            await fleet.HearFromEveryVehicleAsync();
            await fleet.RunRoundAsync(TimeSpan.FromMinutes(1));
        }

        Assert.Equal((KeyA, StationExclusivityStates.Reserved), await HolderAsync(fleet, Near.StationId));
        ChargingCycleRow cycle = await CycleAsync(fleet, KeyA);
        Assert.Equal((ChargingCycleWireStates.EnRoute, (DateTimeOffset?)null), (cycle.WireState, cycle.ArrivedAt));
        Assert.Equal(JourneyRuntimeStage.AwaitingPickupArrival, (await ChargingJourneyAsync(fleet, AgvA))!.Stage);
        Assert.Equal(VehiclePurposes.Charging, (await ClaimOfAsync(fleet, KeyA))!.Value.Purpose);
        Assert.Single(fleet.Riot.Creates);
        Assert.Empty(fleet.Riot.OrderCommands);
        Assert.Empty(fleet.Riot.EmergencyCommands);
    }

    // ---- 开始充电 ----------------------------------------------------------------------------------------------------------

    /// <summary>
    /// 到桩之后读到新鲜的 <c>batteryState=CHARGING</c> 才算开始：周期进 <c>CHARGING</c>、记下第一次看到充电的时刻；业务状态报
    /// <c>CHARGING</c>、<c>activePurpose</c> 仍是 <c>CHARGING</c>。在那之前（到桩、还没充上）周期是 <c>EN_ROUTE</c>。
    /// </summary>
    [Fact]
    public async Task OnlyAFreshChargingReadingAfterArrivalStartsTheCharging()
    {
        await using FleetFixture fleet = await FleetAsync();
        JourneyRuntimeRow journey = await EnRouteAsync(fleet);
        fleet.Riot.CompleteOrder(journey.PickupUpperId);
        AtCharger(fleet, KeyA, 40, NotCharging);
        await RoundAsync(fleet);
        await RoundAsync(fleet);
        Assert.Equal(ChargingCycleWireStates.EnRoute, (await CycleAsync(fleet, KeyA)).WireState);

        // A CHARGING reading that is no longer fresh is not the start: arrived, the cycle stays EN_ROUTE.
        fleet.Riot.VehicleOverrides[KeyA] = seen => seen with
        {
            CurrentStationId = Near.StationId,
            BatteryState = Charging,
            ObservedAt = seen.ObservedAt - fleet.Options.MaximumEvidenceAge - TimeSpan.FromSeconds(1),
        };
        await RoundAsync(fleet);
        await RoundAsync(fleet);
        Assert.Equal(
            (ChargingCycleWireStates.EnRoute, (DateTimeOffset?)null),
            ((await CycleAsync(fleet, KeyA)).WireState, (await CycleAsync(fleet, KeyA)).FirstChargingSeenAt));

        AtCharger(fleet, KeyA, 40, Charging);
        await RoundAsync(fleet);

        ChargingCycleRow cycle = await CycleAsync(fleet, KeyA);
        Assert.Equal(ChargingCycleWireStates.Charging, cycle.WireState);
        Assert.NotNull(cycle.FirstChargingSeenAt);
        JsonElement state = (await PayloadsAsync(fleet, AgvA, "VehicleBusinessStateSnapshot"))[^1];
        Assert.Equal(
            (ChargingCycleWireStates.Charging, VehicleActivePurposes.Charging),
            (state.GetProperty("chargingCycleState").GetString(), state.GetProperty("activePurpose").GetString()));
        Assert.Contains(
            fleet.Peer.Delivered, line => line.MessageId == ChargingJourneyShape.ChargingStateMessageId(journey.JourneyId));
        Assert.Equal(VehiclePurposes.Charging, (await ClaimOfAsync(fleet, KeyA))!.Value.Purpose);
        Assert.Equal((KeyA, StationExclusivityStates.Occupied), await HolderAsync(fleet, Near.StationId));
    }

    /// <summary>
    /// 到桩但一直读不到 <c>CHARGING</c>（MVP 的 <c>CHARGER_NOT_ENGAGED</c> 形状）不是充不上的确认（<c>REQ-0175</c> 的负向）：宽限之后旅程写
    /// <c>CHARGER_NOT_ENGAGED</c>、告警恰好一次，周期、用途、桩占用都保持；不暂停桩、不暂停车的充电资格、不建单不发命令，交人处理。
    /// </summary>
    [Fact]
    public async Task ArrivedButNeverChargingIsNamedAndWarnedOnceAndNoChargerIsHeld()
    {
        await using FleetFixture fleet = await FleetAsync();
        JourneyRuntimeRow journey = await EnRouteAsync(fleet);
        fleet.Riot.CompleteOrder(journey.PickupUpperId);
        AtCharger(fleet, KeyA, 40, NotCharging);

        for (int round = 0; round < 3; round++)
        {
            await RoundAsync(fleet);
        }
        Assert.Null((await ChargingJourneyAsync(fleet, AgvA))!.BlockReasonCode);

        for (int minute = 0; minute < 20; minute++)
        {
            await fleet.HearFromEveryVehicleAsync();
            await fleet.RunRoundAsync(TimeSpan.FromMinutes(1));
        }

        JourneyRuntimeRow held = (await ChargingJourneyAsync(fleet, AgvA))!;
        Assert.Equal(
            (JourneyRuntimeStage.AwaitingPickupArrival, ChargingExecutionReasons.ChargerNotEngaged),
            (held.Stage, held.BlockReasonCode));
        Assert.Single(fleet.EngineLog.Entries, entry => entry.EventId.Id == 2259);
        Assert.Equal(ChargingCycleWireStates.EnRoute, (await CycleAsync(fleet, KeyA)).WireState);
        Assert.Equal((KeyA, StationExclusivityStates.Occupied), await HolderAsync(fleet, Near.StationId));
        Assert.Equal(VehiclePurposes.Charging, (await ClaimOfAsync(fleet, KeyA))!.Value.Purpose);
        Assert.Empty(await fleet.Context.Set<ChargingStationAllocationHoldRow>().AsNoTracking().ToArrayAsync(Token));
        Assert.Empty(await fleet.Context.Set<VehicleChargingEligibilityHoldRow>().AsNoTracking().ToArrayAsync(Token));
        Assert.Single(fleet.Riot.Creates);
        Assert.Empty(fleet.Riot.OrderCommands);

        // Once it does charge, the code is cleared and the cycle goes on.
        AtCharger(fleet, KeyA, 40, Charging);
        await RoundAsync(fleet);
        Assert.Null((await ChargingJourneyAsync(fleet, AgvA))!.BlockReasonCode);
        Assert.Equal(ChargingCycleWireStates.Charging, (await CycleAsync(fleet, KeyA)).WireState);
    }

    // ---- 充满 ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// 电量（新鲜、连续的样本）达到本周期快照里的完成阈值：周期 <c>COMPLETE</c>，同一次保存放开 <c>CHARGING</c> 用途、旅程收尾；桩仍是这辆车的占用。
    /// 低于阈值一格不算。线上：业务状态 <c>chargingCycleState=COMPLETE</c>、<c>activePurpose</c> 为空，计划里留那条 <c>ARRIVED</c> 的
    /// <c>CHARGER</c> 腿（车载端据此仍显示「已充满，在充电桩待命」、不出录入）。本服务端不为离桩建任何单、不发任何命令。
    /// </summary>
    [Fact]
    public async Task AtTheCompletionThresholdTheCycleCompletesAndReleasesThePurposeInOneSaveWhileTheChargerStaysOccupied()
    {
        await using FleetFixture fleet = await FleetAsync();
        JourneyRuntimeRow journey = await ChargingAsync(fleet, battery: 60);
        AtCharger(fleet, KeyA, 79, Charging);
        await RoundAsync(fleet);
        await RoundAsync(fleet);
        Assert.Equal(ChargingCycleWireStates.Charging, (await CycleAsync(fleet, KeyA)).WireState);

        AtCharger(fleet, KeyA, 80, Charging);
        await RoundAsync(fleet);

        ChargingCycleRow cycle = await CycleAsync(fleet, KeyA);
        Assert.Equal((ChargingCycleWireStates.Complete, ChargingCyclePhases.Active), (cycle.WireState, cycle.Phase));
        Assert.NotNull(cycle.CompletedAt);
        Assert.Null(await ClaimOfAsync(fleet, KeyA));
        VehiclePurposeClaimRecordRow released = await fleet.Context.Set<VehiclePurposeClaimRecordRow>().AsNoTracking()
            .SingleAsync(row => row.JourneyId == journey.JourneyId, Token);
        Assert.Equal(ChargingExecutionReasons.Completed, released.ReleaseReason);
        Assert.Equal(cycle.CompletedAt, released.ReleasedAt);
        JourneyRuntimeRow closed = (await ChargingJourneyAsync(fleet, AgvA))!;
        Assert.Equal((JourneyRuntimeStage.Completed, ChargingExecutionReasons.Completed), (closed.Stage, closed.BlockReasonCode));
        Assert.Equal((KeyA, StationExclusivityStates.Occupied), await HolderAsync(fleet, Near.StationId));

        JsonElement state = (await PayloadsAsync(fleet, AgvA, "VehicleBusinessStateSnapshot"))[^1];
        Assert.Equal(ChargingCycleWireStates.Complete, state.GetProperty("chargingCycleState").GetString());
        Assert.Equal(JsonValueKind.Null, state.GetProperty("activePurpose").ValueKind);
        JsonElement plan = (await PayloadsAsync(fleet, AgvA, "UpcomingStopPlanSnapshot"))[^1];
        JsonElement leg = Assert.Single(plan.GetProperty("legs").EnumerateArray());
        Assert.Equal(
            (JourneyPlanBuilder.ChargerStopPurpose, "ARRIVED"),
            (leg.GetProperty("stopPurposeCategory").GetString(), leg.GetProperty("state").GetString()));
        Assert.Single(fleet.Riot.Creates);
        Assert.Empty(fleet.Riot.OrderCommands);
        Assert.Single(fleet.EngineLog.Entries, entry => entry.EventId.Id == 2260);

        // Full and plugged in, with nothing to do: it stays on the charger. Nothing is created to take it off.
        for (int round = 0; round < 5; round++)
        {
            await RoundAsync(fleet);
        }
        Assert.Single(fleet.Riot.Creates);
        Assert.Equal((KeyA, StationExclusivityStates.Occupied), await HolderAsync(fleet, Near.StationId));
    }

    /// <summary>
    /// 判充满用的是这个周期冻结的策略版本（<c>REQ-0282</c>），不是此刻生效的那一版：周期进行中激活了一版阈值不同的新策略，85% 的车按周期那一版判。
    /// </summary>
    [Theory]
    [InlineData(80, 95, true)]
    [InlineData(95, 80, false)]
    public async Task TheCompletionThresholdIsTheOneTheCycleFroze(int frozen, int activatedMidCycle, bool completes)
    {
        await using FleetFixture fleet = await FleetAsync();
        SwitchingResolver resolver = new(
            TestChargingPolicies.Effective(TestChargingPolicies.Content with { ChargingCompletionThresholdPercent = frozen }, version: 1),
            TestChargingPolicies.Effective(TestChargingPolicies.Content with { ChargingCompletionThresholdPercent = activatedMidCycle }, version: 2));
        fleet.ChargingPolicy = resolver;
        await fleet.RecreateEngineAsync();
        await ChargingAsync(fleet, battery: 60);
        Assert.Equal(1, (await CycleAsync(fleet, KeyA)).ChargingPolicyVersion);

        resolver.Switch();
        AtCharger(fleet, KeyA, 85, Charging);
        await RoundAsync(fleet);
        await RoundAsync(fleet);

        Assert.Equal(
            completes ? ChargingCycleWireStates.Complete : ChargingCycleWireStates.Charging,
            (await CycleAsync(fleet, KeyA)).WireState);
    }

    /// <summary>
    /// 重启之后，已 <c>COMPLETE</c> 的周期不再被判一次充满，用途也不再放一次：引擎重建（进程重启）后再跑几轮，周期行、用途记录与
    /// <c>COMPLETE</c> 的业务状态快照都只有原来那一份。
    /// </summary>
    [Fact]
    public async Task AfterARestartACompletedCycleIsNeitherCompletedAgainNorItsPurposeReleasedTwice()
    {
        await using FleetFixture fleet = await FleetAsync();
        JourneyRuntimeRow journey = await CompletedAsync(fleet);
        ChargingCycleRow before = await CycleAsync(fleet, KeyA);

        await fleet.RecreateEngineAsync();
        for (int round = 0; round < 3; round++)
        {
            await RoundAsync(fleet);
        }

        ChargingCycleRow after = await CycleAsync(fleet, KeyA);
        Assert.Equal((before.Version, before.CompletedAt, before.WireState), (after.Version, after.CompletedAt, after.WireState));
        Assert.Single(
            await fleet.Context.Set<VehiclePurposeClaimRecordRow>().AsNoTracking()
                .Where(row => row.JourneyId == journey.JourneyId).ToArrayAsync(Token));
        Assert.Single(
            (await PayloadsAsync(fleet, AgvA, "VehicleBusinessStateSnapshot")),
            state => state.GetProperty("chargingCycleState").GetString() == ChargingCycleWireStates.Complete);
    }

    // ---- 充满前不派，充满后能派 --------------------------------------------------------------------------------------------

    /// <summary>
    /// 充满之前（<c>CHARGING</c> 且低于完成阈值）：一条正好合适的搬运需求不派给它、也不问它追加；空闲返回不评估它（<c>NEVER_DISPATCH_DURING_CHARGING</c>）。
    /// </summary>
    [Fact]
    public async Task BelowTheCompletionThresholdAChargingVehicleTakesNoTransportNoAppendAndNoIdleReturn()
    {
        await using FleetFixture fleet = await FleetAsync();
        await ChargingAsync(fleet, battery: 60);
        await fleet.EnableIdleReturnAsync(WaitingPoint);
        await fleet.AllowEnRouteAppendAsync(1_000_000);
        fleet.Catalog.Set([FleetFixture.Demand(0, "N1-1", 0)]);

        for (int round = 0; round < 4; round++)
        {
            await RoundAsync(fleet);
        }

        Assert.Equal(VehiclePurposes.Charging, (await ClaimOfAsync(fleet, KeyA))!.Value.Purpose);
        Assert.Empty(await fleet.Context.Set<JourneyDemandRow>().AsNoTracking().ToArrayAsync(Token));
        Assert.Single(await fleet.Context.JourneyRuntimes.AsNoTracking().ToArrayAsync(Token));
        Assert.Null(await StationAsync(fleet, WaitingPoint.StationId));
        Assert.Single(fleet.Riot.Creates);
        Assert.Equal(ChargingCycleWireStates.Charging, (await CycleAsync(fleet, KeyA)).WireState);
    }

    /// <summary>
    /// 已 <c>COMPLETE</c>、仍插在桩上报 <c>CHARGING</c> 的车能被派搬运（红证据：把判据改回「一律拒绝 <c>CHARGING</c>」，这条变红）。
    /// 下达的新计划里没有充电桩腿，车到取货站当前腿就是业务腿（车载端据此不把它判为充电停靠、能录入）。
    /// </summary>
    [Fact]
    public async Task ACompletedVehicleStillReportingChargingTakesATransportWhosePlanHasNoChargerLeg()
    {
        await using FleetFixture fleet = await FleetAsync();
        await CompletedAsync(fleet);
        fleet.Catalog.Set([FleetFixture.Demand(0, "N1-1", 0)]);

        for (int round = 0; round < 3; round++)
        {
            await RoundAsync(fleet);
        }

        JourneyRuntimeRow transport = await fleet.Context.JourneyRuntimes.AsNoTracking()
            .SingleAsync(row => row.AgvId == AgvA && !row.JourneyId.StartsWith(ChargingIdentity.JourneyIdPrefix), Token);
        Assert.Equal(VehiclePurposes.Transport, (await ClaimOfAsync(fleet, KeyA))!.Value.Purpose);
        Assert.Contains(fleet.Riot.Creates, create => create.VehicleKey == KeyA && create.DestinationStationId == 12);
        JsonElement plan = (await PayloadsAsync(fleet, AgvA, "UpcomingStopPlanSnapshot"))[^1];
        Assert.All(
            plan.GetProperty("legs").EnumerateArray(),
            leg => Assert.NotEqual(JourneyPlanBuilder.ChargerStopPurpose, leg.GetProperty("stopPurposeCategory").GetString()));
        Assert.Equal("BUSINESS", plan.GetProperty("legs")[0].GetProperty("stopPurposeCategory").GetString());

        // Taken off the charger and at the pickup, the entry opens: the journey waits for the sublot, and the plan the
        // vehicle is sent has its business leg ARRIVED at the pickup N1-1 (RIoT station 12) -- nothing of the charging cycle stands in the way.
        fleet.Riot.CompleteOrder(transport.PickupUpperId);
        fleet.Riot.BatteryByVehicle[KeyA] = 80;
        fleet.Riot.VehicleOverrides[KeyA] = seen => seen with { CurrentStationId = 12, BatteryState = NotCharging };
        for (int round = 0; round < 3; round++)
        {
            await fleet.HearFromEveryVehicleAsync();
            await RoundAsync(fleet);
        }

        Assert.Equal(
            JourneyRuntimeStage.AwaitingSublot,
            (await fleet.Context.JourneyRuntimes.AsNoTracking()
                .SingleAsync(row => row.JourneyId == transport.JourneyId, Token)).Stage);
        JsonElement atPickup = (await PayloadsAsync(fleet, AgvA, "UpcomingStopPlanSnapshot"))[^1].GetProperty("legs")[0];
        Assert.Equal(
            ("BUSINESS", "ARRIVED", "N1-1"),
            (atPickup.GetProperty("stopPurposeCategory").GetString(), atPickup.GetProperty("state").GetString(),
                atPickup.GetProperty("stationId").GetString()));
    }

    /// <summary>已 <c>COMPLETE</c>、仍报 <c>CHARGING</c>、没有搬运可接的车，空闲返回照常选中它（#389、#390），去等待点就是离桩。</summary>
    [Fact]
    public async Task ACompletedVehicleStillReportingChargingIsSentToAWaitingPointWhenIdle()
    {
        await using FleetFixture fleet = await FleetAsync();
        await CompletedAsync(fleet);
        await fleet.EnableIdleReturnAsync(WaitingPoint);

        for (int round = 0; round < 3; round++)
        {
            await RoundAsync(fleet);
        }

        Assert.Equal(VehiclePurposes.IdleReturn, (await ClaimOfAsync(fleet, KeyA))!.Value.Purpose);
        Assert.Equal(KeyA, (await StationAsync(fleet, WaitingPoint.StationId))!.VehicleKey);
        Assert.Equal((KeyA, StationExclusivityStates.Occupied), await HolderAsync(fleet, Near.StationId));
    }

    /// <summary>
    /// 电量判据那一支的放开只认「本周期已 <c>COMPLETE</c>」：报 <c>CHARGING</c> 的车，周期没完成照旧 <c>BATTERY_POLICY_NOT_SATISFIED</c>，完成了才放行。
    /// </summary>
    [Theory]
    [InlineData(false, VehicleDynamicFactsCriterion.BatteryPolicyNotSatisfiedReason)]
    [InlineData(true, DispatchAdmissionChain.Eligible)]
    public void OnlyACompletedCycleLetsAChargingVehicleTakeWork(bool complete, string expected)
    {
        RiotVehicleObservation charging = new(
            KeyA, true, true, "IDLE", "MAP", Near.StationId, 90, Charging, 0, DateTimeOffset.UnixEpoch, 0, null);

        Assert.Equal(expected, BatteryEligibility.Judge(charging, TestChargingPolicies.Battery(), 1, 10, complete));
        Assert.Equal(complete, BatteryEligibility.ChargingVehicleMayTakeWork(charging, complete));
    }

    // ---- 离桩与释放（REQ-0173）-------------------------------------------------------------------------------------------

    /// <summary>
    /// 下达下一单那一刻桩仍是占用（「下达即释放」的缺陷版本用它取红）；车离开桩、不再报 <c>CHARGING</c>、桩可确认空闲，三项都确认那一轮才释放，
    /// 释放写进独占经过；周期收尾、回 <c>NOT_CHARGING</c>，记下离桩与释放时刻。
    /// </summary>
    [Fact]
    public async Task TheChargerIsStillOccupiedWhenTheNextOrderGoesOutAndIsReleasedOnceAllThreeAreConfirmed()
    {
        await using FleetFixture fleet = await FleetAsync();
        JourneyRuntimeRow charging = await CompletedAsync(fleet);
        fleet.Catalog.Set([FleetFixture.Demand(0, "N1-1", 0)]);
        for (int round = 0; round < 3; round++)
        {
            await RoundAsync(fleet);
        }
        Assert.Contains(fleet.Riot.Creates, create => create.VehicleKey == KeyA && create.DestinationStationId == 12);
        Assert.Equal((KeyA, StationExclusivityStates.Occupied), await HolderAsync(fleet, Near.StationId));
        await RoundAsync(fleet);
        Assert.Equal((KeyA, StationExclusivityStates.Occupied), await HolderAsync(fleet, Near.StationId));

        fleet.Riot.VehicleOverrides[KeyA] = seen => seen with { CurrentStationId = 12, BatteryState = NotCharging };
        await RoundAsync(fleet);

        Assert.Null(await StationAsync(fleet, Near.StationId));
        StationExclusivityRecordRow record = await fleet.Context.Set<StationExclusivityRecordRow>().AsNoTracking()
            .SingleAsync(row => row.JourneyId == charging.JourneyId, Token);
        Assert.Equal(ChargingExecutionReasons.ChargerReleasedOnDeparture, record.ReleaseReason);
        ChargingCycleRow cycle = await fleet.Context.Set<ChargingCycleRow>().AsNoTracking().SingleAsync(Token);
        Assert.Equal(
            (ChargingCyclePhases.Ended, ChargingCycleWireStates.NotCharging, ChargingExecutionReasons.Departed),
            (cycle.Phase, cycle.WireState, cycle.EndReason));
        Assert.NotNull(cycle.DepartedAt);
        Assert.Equal(record.ReleasedAt, cycle.ReleasedAt);
        Assert.Single(fleet.ChargingLog.Entries, entry => entry.EventId.Id == 2248);
    }

    public static TheoryData<string> MissingDepartureConfirmations => ["still-charging", "still-on-the-charger", "charger-not-free", "vehicle-unreadable"];

    /// <summary>三项确认缺任何一项都不释放（车读不到也不算离开）；补上那一项后才释放。</summary>
    [Theory]
    [MemberData(nameof(MissingDepartureConfirmations))]
    public async Task WithAnyOfTheThreeConfirmationsMissingTheChargerStaysOccupied(string missing)
    {
        await using FleetFixture fleet = await FleetAsync();
        await CompletedAsync(fleet);
        fleet.Catalog.Set([FleetFixture.Demand(0, "N1-1", 0)]);
        for (int round = 0; round < 3; round++)
        {
            await RoundAsync(fleet);
        }

        fleet.Riot.VehicleOverrides[KeyA] = seen => missing switch
        {
            "still-charging" => seen with { CurrentStationId = 12, BatteryState = Charging },
            "still-on-the-charger" => seen with { CurrentStationId = Near.StationId, BatteryState = NotCharging },
            _ => seen with { CurrentStationId = 12, BatteryState = NotCharging },
        };
        if (missing == "charger-not-free")
        {
            // RIoT's unfinished order listing cannot be read whole: whether an order is heading for the charger is unknown, so
            // the charger is not confirmed free (the same occupancy reading as the allocation).
            fleet.Riot.OrderListingIncomplete = true;
        }
        if (missing == "vehicle-unreadable")
        {
            fleet.Riot.FailOn = KeyA;
        }
        for (int round = 0; round < 4; round++)
        {
            await RoundAsync(fleet);
        }

        Assert.Equal((KeyA, StationExclusivityStates.Occupied), await HolderAsync(fleet, Near.StationId));
        Assert.Equal(ChargingCycleWireStates.Complete, (await CycleAsync(fleet, KeyA)).WireState);

        fleet.Riot.FailOn = null;
        fleet.Riot.OrderListingIncomplete = false;
        fleet.Riot.VehicleOverrides[KeyA] = seen => seen with { CurrentStationId = 12, BatteryState = NotCharging };
        await RoundAsync(fleet);

        Assert.Null(await StationAsync(fleet, Near.StationId));
    }

    // ---- 失联三分（REQ-0287）-------------------------------------------------------------------------------------------

    /// <summary>
    /// 车载端会话失效：只阻断车辆新业务，周期照样经 RIoT 观察、不结束、不释放；桩占用与用途都在，没有任何命令发出。
    /// </summary>
    [Fact]
    public async Task WhenTheOnboardSessionIsLostTheCycleIsStillObservedThroughRiotAndNothingIsReleased()
    {
        await using FleetFixture fleet = await FleetAsync();
        JourneyRuntimeRow journey = await ChargingAsync(fleet, battery: 60);
        fleet.Peer.Unavailable = AgvA;

        // No heartbeat any more: the session is dead from the server's side.
        for (int minute = 0; minute < 30; minute++)
        {
            await fleet.RunRoundAsync(TimeSpan.FromMinutes(1));
        }

        ChargingCycleRow cycle = await CycleAsync(fleet, KeyA);
        Assert.Equal((ChargingCycleWireStates.Charging, ChargingCyclePhases.Active), (cycle.WireState, cycle.Phase));
        Assert.Equal(VehiclePurposes.Charging, (await ClaimOfAsync(fleet, KeyA))!.Value.Purpose);
        Assert.Equal((KeyA, StationExclusivityStates.Occupied), await HolderAsync(fleet, Near.StationId));
        Assert.Equal(JourneyRuntimeStage.AwaitingPickupArrival, (await ChargingJourneyAsync(fleet, AgvA))!.Stage);
        Assert.Single(fleet.Riot.Creates);
        Assert.Empty(fleet.Riot.OrderCommands);
        Assert.Empty(fleet.Riot.EmergencyCommands);

        // Observed through RIoT all along: once full, it completes whatever the session.
        AtCharger(fleet, KeyA, 80, Charging);
        await fleet.RunRoundAsync(TimeSpan.FromSeconds(1));
        await fleet.RunRoundAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(ChargingCycleWireStates.Complete, (await CycleAsync(fleet, KeyA)).WireState);
        Assert.Equal(journey.JourneyId, (await ChargingJourneyAsync(fleet, AgvA))!.JourneyId);
    }

    /// <summary>
    /// 真车载端在本服务端自己的单在途时整段未就绪（<c>RecoveryRequired</c>、<c>DEPARTURE_SAFETY_NOT_READY</c>，control-server#314）；合成车载端永远报安全，
    /// 看不见这一格。到桩、开始充电、充满都只看 RIoT 的证据，所以会话在这一整段都是这个状态时照样走到 <c>COMPLETE</c>，快照照样发到车上。
    /// </summary>
    [Fact]
    public async Task ArrivalChargingAndCompletionGoAheadWhileTheSessionIsNotReadyBecauseOfOurOwnOrder()
    {
        await using FleetFixture fleet = await FleetAsync();
        JourneyRuntimeRow journey = await EnRouteAsync(fleet);
        await DropSessionOnOwnOrderAsync(fleet);
        fleet.Riot.CompleteOrder(journey.PickupUpperId);
        AtCharger(fleet, KeyA, 60, Charging);

        await RoundAsync(fleet);
        Assert.Equal((KeyA, StationExclusivityStates.Occupied), await HolderAsync(fleet, Near.StationId));
        Assert.Equal(ChargingCycleWireStates.Charging, (await CycleAsync(fleet, KeyA)).WireState);

        AtCharger(fleet, KeyA, 80, Charging);
        await RoundAsync(fleet);

        Assert.Equal(ChargingCycleWireStates.Complete, (await CycleAsync(fleet, KeyA)).WireState);
        Assert.Null(await ClaimOfAsync(fleet, KeyA));
        Assert.Equal(
            SessionReadiness.RecoveryRequired,
            (await fleet.Context.SessionRecoveries.AsNoTracking().SingleAsync(row => row.AgvId == AgvA, Token)).Readiness);
        Assert.Contains(fleet.Peer.Delivered, line => line.MessageId == JourneyClosure.SnapshotMessageIds(journey.JourneyId)[2]);
    }

    public static TheoryData<string> VehicleObservationLosses => ["read-fails", "disconnected"];

    /// <summary>
    /// RIoT 车辆观测丢失（读不到、或报离线）：按「车可能仍在桩上」处理——桩占用、用途、周期都保持，不发停止、重启或移动命令；旅程写
    /// <c>CHARGING_VEHICLE_OBSERVATION_LOST</c>，持续超过窗口只升级告警一次。观测恢复后码清掉、周期接着走。
    /// </summary>
    [Theory]
    [MemberData(nameof(VehicleObservationLosses))]
    public async Task WhenRiotLosesTheVehicleEverythingIsKeptAndOnlyAnAlarmIsRaised(string loss)
    {
        await using FleetFixture fleet = await FleetAsync();
        await ChargingAsync(fleet, battery: 60);
        if (loss == "read-fails")
        {
            fleet.Riot.FailOn = KeyA;
        }
        else
        {
            fleet.Riot.VehicleOverrides[KeyA] = seen => seen with { CurrentStationId = Near.StationId, BatteryState = Charging, Connected = false };
        }

        for (int minute = 0; minute < 30; minute++)
        {
            await fleet.HearFromEveryVehicleAsync();
            await fleet.RunRoundAsync(TimeSpan.FromMinutes(1));
        }

        Assert.Equal(ChargingExecutionReasons.VehicleObservationLost, (await ChargingJourneyAsync(fleet, AgvA))!.BlockReasonCode);
        Assert.Single(fleet.EngineLog.Entries, entry => entry.EventId.Id == 2261);
        ChargingCycleRow cycle = await CycleAsync(fleet, KeyA);
        Assert.Equal((ChargingCycleWireStates.Charging, ChargingCyclePhases.Active), (cycle.WireState, cycle.Phase));
        Assert.Equal(VehiclePurposes.Charging, (await ClaimOfAsync(fleet, KeyA))!.Value.Purpose);
        Assert.Equal((KeyA, StationExclusivityStates.Occupied), await HolderAsync(fleet, Near.StationId));
        Assert.Single(fleet.Riot.Creates);
        Assert.Empty(fleet.Riot.OrderCommands);
        Assert.Empty(fleet.Riot.EmergencyCommands);

        fleet.Riot.FailOn = null;
        AtCharger(fleet, KeyA, 60, Charging);
        await RoundAsync(fleet);
        Assert.Null((await ChargingJourneyAsync(fleet, AgvA))!.BlockReasonCode);
    }

    /// <summary>
    /// 电量遥测丢失（读不到电量、或读数过期）：暂停完成判断，持续超过窗口只升级告警一次；遥测恢复前的最后读数不当作仍然成立——过期的 90% 不判充满，
    /// 恢复后的第一个新鲜样本也只是重新开始观察，连续的第二个才判。
    /// </summary>
    [Fact]
    public async Task WhenBatteryTelemetryIsLostCompletionWaitsAndNoReadingFromBeforeTheGapIsTakenAsStillTrue()
    {
        await using FleetFixture fleet = await FleetAsync();
        await ChargingAsync(fleet, battery: 60);
        fleet.Riot.VehicleOverrides[KeyA] = seen => seen with
        {
            CurrentStationId = Near.StationId,
            BatteryState = Charging,
            BatteryPercent = 90,
            ObservedAt = seen.ObservedAt - fleet.Options.MaximumEvidenceAge - TimeSpan.FromSeconds(1),
        };

        for (int minute = 0; minute < 15; minute++)
        {
            await fleet.HearFromEveryVehicleAsync();
            await fleet.RunRoundAsync(TimeSpan.FromMinutes(1));
        }

        Assert.Equal(ChargingCycleWireStates.Charging, (await CycleAsync(fleet, KeyA)).WireState);
        Assert.Equal(ChargingExecutionReasons.BatteryTelemetryLost, (await ChargingJourneyAsync(fleet, AgvA))!.BlockReasonCode);
        Assert.Single(fleet.EngineLog.Entries, entry => entry.EventId.Id == 2262);

        AtCharger(fleet, KeyA, 90, Charging);
        await RoundAsync(fleet);
        Assert.Equal(ChargingCycleWireStates.Charging, (await CycleAsync(fleet, KeyA)).WireState);
        Assert.Null((await ChargingJourneyAsync(fleet, AgvA))!.BlockReasonCode);

        await RoundAsync(fleet);
        Assert.Equal(ChargingCycleWireStates.Complete, (await CycleAsync(fleet, KeyA)).WireState);
        Assert.Empty(fleet.Riot.OrderCommands);
    }

    // ---- 并发交错 ----------------------------------------------------------------------------------------------------------

    /// <summary>
    /// 桩释放与另一辆车对同一桩的预占在同一轮：B 在 A 离桩之前一直拿不到；A 的三项确认满足那一轮，B 在释放提交之后才取得预占——经过里 A 的释放时刻
    /// 不晚于 B 的预占时刻，任何时刻这个桩只有一行。
    /// </summary>
    [Fact]
    public async Task WhenTheReleaseAndAnotherVehiclesReservationMeetInOneRoundTheOtherGetsItOnlyAfterTheRelease()
    {
        await using FleetFixture fleet = await FleetAsync(vehicles: 2);
        JourneyRuntimeRow charging = await CompletedAsync(fleet);
        // B falls below its line while A, full, still holds the only charger: B queues and takes no transport.
        fleet.Riot.BatteryByVehicle[KeyB] = 20;
        await RoundAsync(fleet);
        fleet.Catalog.Set([FleetFixture.Demand(0, "N1-1", 0)]);
        for (int round = 0; round < 3; round++)
        {
            await RoundAsync(fleet);
        }
        Assert.Equal(VehiclePurposes.Transport, (await ClaimOfAsync(fleet, KeyA))!.Value.Purpose);
        Assert.Equal((KeyA, StationExclusivityStates.Occupied), await HolderAsync(fleet, Near.StationId));
        Assert.Null(await ClaimOfAsync(fleet, KeyB));

        fleet.Riot.VehicleOverrides[KeyA] = seen => seen with { CurrentStationId = 13, BatteryState = NotCharging };
        await RoundAsync(fleet);

        Assert.Equal((KeyB, StationExclusivityStates.Reserved), await HolderAsync(fleet, Near.StationId));
        StationExclusivityRecordRow[] records = await fleet.Context.Set<StationExclusivityRecordRow>().AsNoTracking()
            .Where(row => row.StationId == Near.StationId).ToArrayAsync(Token);
        StationExclusivityRecordRow a = Assert.Single(records, row => row.JourneyId == charging.JourneyId);
        StationExclusivityRecordRow b = Assert.Single(records, row => row.VehicleKey == KeyB);
        Assert.True(a.ReleasedAt <= b.ReservedAt, $"A released at {a.ReleasedAt}, B reserved at {b.ReservedAt}.");
    }

    /// <summary>
    /// 「放开用途」与「另一用途认领这辆车」在同一轮：充满那一轮里车还算忙（这一轮开头读的是在推进的旅程），那一轮结束时它一份用途也没有；
    /// 新需求下一轮才派给它。用途经过里恰好两份：<c>CHARGING</c> 已释放、<c>TRANSPORT</c> 在持有，从没有两份同时在持有。
    /// </summary>
    [Fact]
    public async Task ThePurposeReleasedAtCompletionIsNeverHeldTwice()
    {
        await using FleetFixture fleet = await FleetAsync();
        JourneyRuntimeRow journey = await ChargingAsync(fleet, battery: 60);
        fleet.Catalog.Set([FleetFixture.Demand(0, "N1-1", 0)]);
        AtCharger(fleet, KeyA, 80, Charging);

        await RoundAsync(fleet);
        Assert.Equal(ChargingCycleWireStates.Complete, (await CycleAsync(fleet, KeyA)).WireState);
        Assert.Null(await ClaimOfAsync(fleet, KeyA));

        await RoundAsync(fleet);
        Assert.Equal(VehiclePurposes.Transport, (await ClaimOfAsync(fleet, KeyA))!.Value.Purpose);
        VehiclePurposeClaimRecordRow[] records = await fleet.Context.Set<VehiclePurposeClaimRecordRow>().AsNoTracking()
            .Where(row => row.VehicleKey == KeyA).ToArrayAsync(Token);
        Assert.Equal(2, records.Length);
        VehiclePurposeClaimRecordRow charged = Assert.Single(records, row => row.JourneyId == journey.JourneyId);
        VehiclePurposeClaimRecordRow transport = Assert.Single(records, row => row.Purpose == VehiclePurposes.Transport);
        Assert.Equal(ChargingExecutionReasons.Completed, charged.ReleaseReason);
        Assert.Null(transport.ReleasedAt);
    }

    // ---- 崩溃点 ----------------------------------------------------------------------------------------------------------

    /// <summary>
    /// 「转 <c>COMPLETE</c>」与「放开用途」同一次保存：保存里崩掉，周期仍是 <c>CHARGING</c>、用途仍在、旅程仍开着——不留「用途已放、周期仍充电」
    /// 或反过来；下一轮整笔做成，恰好一次。
    /// </summary>
    [Fact]
    public async Task ACrashInTheCompletionSaveLeavesNeitherHalf()
    {
        FailingInsert crash = new("DELETE FROM \"VehiclePurposeClaims\"");
        await using FleetFixture fleet = await FleetAsync(commands: crash);
        await ChargingAsync(fleet, battery: 60);
        AtCharger(fleet, KeyA, 80, Charging);
        crash.Armed = true;

        await Record.ExceptionAsync(() => RoundAsync(fleet));

        Assert.Equal(1, crash.Fired);
        fleet.Context.ChangeTracker.Clear();
        Assert.Equal(ChargingCycleWireStates.Charging, (await CycleAsync(fleet, KeyA)).WireState);
        Assert.Equal(VehiclePurposes.Charging, (await ClaimOfAsync(fleet, KeyA))!.Value.Purpose);
        Assert.Equal(JourneyRuntimeStage.AwaitingPickupArrival, (await ChargingJourneyAsync(fleet, AgvA))!.Stage);

        await fleet.RecreateEngineAsync();
        await RoundAsync(fleet);
        await RoundAsync(fleet);

        Assert.Equal(ChargingCycleWireStates.Complete, (await CycleAsync(fleet, KeyA)).WireState);
        Assert.Null(await ClaimOfAsync(fleet, KeyA));
        Assert.Equal(JourneyRuntimeStage.Completed, (await ChargingJourneyAsync(fleet, AgvA))!.Stage);
    }

    /// <summary>
    /// 「写释放经过」与「删独占行」（以及周期收尾）同一个事务：中间崩掉，桩仍被占着、周期仍是 <c>COMPLETE</c>——不留「桩已空而周期未收尾」；
    /// 下一轮整笔释放。
    /// </summary>
    [Fact]
    public async Task ACrashBetweenTheReleaseAndTheCycleEndLeavesNoEmptyChargerWithAnOpenCycle()
    {
        FailingInsert crash = new("UPDATE \"ChargingCycles\"");
        await using FleetFixture fleet = await FleetAsync(commands: crash);
        await CompletedAsync(fleet);
        fleet.Catalog.Set([FleetFixture.Demand(0, "N1-1", 0)]);
        for (int round = 0; round < 3; round++)
        {
            await RoundAsync(fleet);
        }
        fleet.Riot.VehicleOverrides[KeyA] = seen => seen with { CurrentStationId = 12, BatteryState = NotCharging };
        crash.Armed = true;

        await Record.ExceptionAsync(() => RoundAsync(fleet));

        Assert.Equal(1, crash.Fired);
        fleet.Context.ChangeTracker.Clear();
        Assert.Equal((KeyA, StationExclusivityStates.Occupied), await HolderAsync(fleet, Near.StationId));
        ChargingCycleRow cycle = await fleet.Context.Set<ChargingCycleRow>().AsNoTracking().SingleAsync(Token);
        Assert.Equal((ChargingCycleWireStates.Complete, ChargingCyclePhases.Active), (cycle.WireState, cycle.Phase));

        await RoundAsync(fleet);

        Assert.Null(await StationAsync(fleet, Near.StationId));
        Assert.Equal(ChargingCyclePhases.Ended, (await fleet.Context.Set<ChargingCycleRow>().AsNoTracking().SingleAsync(Token)).Phase);
    }

    // ---- 重连补发 ----------------------------------------------------------------------------------------------------------

    /// <summary>
    /// 车载端断着的时候到桩并开始充电：重连之后按原 id 补发到桩的计划与 <c>CHARGING</c> 的业务状态，各一份；被它们取代、还没被确认的
    /// <c>EN_ROUTE</c> 那一对退役、不补发（低于车已采纳的修订号会拆会话）。发件箱里没有第二份。
    /// </summary>
    [Fact]
    public async Task AfterAReconnectTheArrivedAndChargingSnapshotsAreResentUnderTheirOwnIdsWithoutASecondCopy()
    {
        await using FleetFixture fleet = await FleetAsync();
        fleet.Riot.BatteryByVehicle[KeyA] = 20;
        fleet.Peer.Unavailable = AgvA;
        JourneyRuntimeRow journey = await CommittedAndSentAsync(fleet);
        fleet.Riot.CompleteOrder(journey.PickupUpperId);
        AtCharger(fleet, KeyA, 60, Charging);
        await RoundAsync(fleet);
        await RoundAsync(fleet);
        Assert.Equal(ChargingCycleWireStates.Charging, (await CycleAsync(fleet, KeyA)).WireState);
        string[] current = [ChargingJourneyShape.ArrivedPlanMessageId(journey.JourneyId), ChargingJourneyShape.ChargingStateMessageId(journey.JourneyId)];
        string[] superseded = [ChargingJourneyShape.EnRoutePlanMessageId(journey.JourneyId), ChargingJourneyShape.EnRouteStateMessageId(journey.JourneyId)];
        int plans = (await PayloadsAsync(fleet, AgvA, "UpcomingStopPlanSnapshot")).Length;
        int states = (await PayloadsAsync(fleet, AgvA, "VehicleBusinessStateSnapshot")).Length;

        fleet.Peer.Unavailable = null;
        await RoundAsync(fleet);
        await RoundAsync(fleet);

        Assert.All(current, id => Assert.Contains(fleet.Peer.Delivered, line => line.MessageId == id));
        Assert.DoesNotContain(fleet.Peer.Delivered, line => superseded.Contains(line.MessageId));
        Assert.Equal(plans, (await PayloadsAsync(fleet, AgvA, "UpcomingStopPlanSnapshot")).Length);
        Assert.Equal(states, (await PayloadsAsync(fleet, AgvA, "VehicleBusinessStateSnapshot")).Length);
    }

    /// <summary>
    /// 车载端断着的时候充满：<c>COMPLETE</c> 的那一对是这趟旅程的收尾快照，重连之后由收尾补发（<see cref="JourneyClosure.ReplayIdsAsync"/>）送到；
    /// 被它取代的到桩计划与 <c>CHARGING</c> 业务状态不在补发之列，也没被确认的那几张已退役。
    /// </summary>
    [Fact]
    public async Task ACompletionTheVehicleMissedIsResentAsTheJourneysClosureAndNothingItSupersedes()
    {
        await using FleetFixture fleet = await FleetAsync();
        fleet.Riot.BatteryByVehicle[KeyA] = 20;
        fleet.Peer.Unavailable = AgvA;
        JourneyRuntimeRow journey = await CommittedAndSentAsync(fleet);
        fleet.Riot.CompleteOrder(journey.PickupUpperId);
        AtCharger(fleet, KeyA, 60, Charging);
        await RoundAsync(fleet);
        AtCharger(fleet, KeyA, 80, Charging);
        await RoundAsync(fleet);
        Assert.Equal(ChargingCycleWireStates.Complete, (await CycleAsync(fleet, KeyA)).WireState);

        IReadOnlyList<string> closure = JourneyClosure.SnapshotMessageIds(journey.JourneyId);
        Assert.Equal([closure[1], closure[2]], await JourneyClosure.ReplayIdsAsync(fleet.Context, AgvA, Token));
        string[] superseded = [ChargingJourneyShape.ArrivedPlanMessageId(journey.JourneyId), ChargingJourneyShape.ChargingStateMessageId(journey.JourneyId)];
        Assert.All(
            await fleet.Context.ProtocolOutbox.AsNoTracking().Where(row => superseded.Contains(row.MessageId)).ToArrayAsync(Token),
            row => Assert.NotNull(row.FencedAt));
    }

    // ---- 协议向量的同名具名测试 -------------------------------------------------------------------------------------------

    /// <summary>
    /// <c>CV-AUTOMATIC-CHARGING-CYCLE</c> 的 <c>CLAIM_VEHICLE_FOR_CHARGING_PURPOSE</c>：真实的分配、建单、到桩与充电——从承诺到充满，
    /// 这辆车一直只持有一份 <c>CHARGING</c> 用途（同一个旅程、同一条用途记录），桩从预占到占用一直是它的；车载端收到的每一对快照都是先计划、后业务状态，
    /// 充电中的业务状态 <c>activePurpose=CHARGING</c>。充电单只建一次（<c>duplicate-riot-order</c>）。
    /// </summary>
    [Fact]
    [Trait("ProtocolVector", Vector)]
    public async Task CvAutomaticChargingCycleClaimsTheVehicleForChargingPurpose()
    {
        await using FleetFixture fleet = await FleetAsync();
        fleet.Riot.BatteryByVehicle[KeyA] = 20;
        await RoundAsync(fleet);
        (string Purpose, string JourneyId) claimed = (await ClaimOfAsync(fleet, KeyA))!.Value;
        Assert.Equal(VehiclePurposes.Charging, claimed.Purpose);
        await RoundAsync(fleet);
        JourneyRuntimeRow journey = (await ChargingJourneyAsync(fleet, AgvA))!;
        Assert.Equal(claimed.JourneyId, journey.JourneyId);

        fleet.Riot.CompleteOrder(journey.PickupUpperId);
        AtCharger(fleet, KeyA, 50, Charging);
        for (int round = 0; round < 3; round++)
        {
            await RoundAsync(fleet);
            Assert.Equal(claimed, (await ClaimOfAsync(fleet, KeyA))!.Value);
        }

        Assert.Equal(ChargingCycleWireStates.Charging, (await CycleAsync(fleet, KeyA)).WireState);
        Assert.Equal((KeyA, StationExclusivityStates.Occupied), await HolderAsync(fleet, Near.StationId));
        Assert.Single(
            await fleet.Context.Set<VehiclePurposeClaimRecordRow>().AsNoTracking().Where(row => row.VehicleKey == KeyA).ToArrayAsync(Token));
        Assert.Single(fleet.Riot.Creates, create => create.UpperId == journey.PickupUpperId);
        string[] lines = [.. fleet.Peer.Delivered
            .Where(line => line.AgvId == AgvA && line.MessageType is "UpcomingStopPlanSnapshot" or "VehicleBusinessStateSnapshot")
            .Select(line => line.MessageType)];
        Assert.Equal("UpcomingStopPlanSnapshot", lines[0]);
        JsonElement state = (await PayloadsAsync(fleet, AgvA, "VehicleBusinessStateSnapshot"))[^1];
        Assert.Equal(
            (VehicleActivePurposes.Charging, ChargingCycleWireStates.Charging),
            (state.GetProperty("activePurpose").GetString(), state.GetProperty("chargingCycleState").GetString()));
    }

    /// <summary>
    /// <c>CV-AUTOMATIC-CHARGING-CYCLE</c> 的 <c>NEVER_DISPATCH_DURING_CHARGING</c>：真实的分配、建单、到桩与充电；充电中一条正好合适的需求等着，
    /// 一直不派给它，也没有它的第二张单；充满（<c>COMPLETE</c>）之后下一轮才派给它，桩在下达那一刻仍是它的占用。没有一张「结果未知」被当成成功
    /// （<c>unknown-as-success</c>）：对账答不上的那几轮周期停在原地。
    /// </summary>
    [Fact]
    [Trait("ProtocolVector", Vector)]
    public async Task CvAutomaticChargingCycleNeverDispatchesDuringCharging()
    {
        await using FleetFixture fleet = await FleetAsync();
        JourneyRuntimeRow journey = await EnRouteAsync(fleet);
        fleet.Riot.PutOrder(new RiotOrderObservation(journey.PickupUpperId, RiotOrderObservationKind.Unknown, null));
        AtCharger(fleet, KeyA, 50, Charging);
        await RoundAsync(fleet);
        await RoundAsync(fleet);
        Assert.Equal(ChargingCycleWireStates.EnRoute, (await CycleAsync(fleet, KeyA)).WireState);

        fleet.Riot.PutOrder(new RiotOrderObservation(
            journey.PickupUpperId, RiotOrderObservationKind.Terminal, $"ORDER-{journey.PickupUpperId}", RiotOrderState.Success,
            KeyA, Map, Near.StationId));
        AcceptedDemandSnapshot demand = FleetFixture.Demand(0, "N1-1", 0);
        fleet.Catalog.Set([demand]);
        for (int round = 0; round < 4; round++)
        {
            await RoundAsync(fleet);
            Assert.Equal(VehiclePurposes.Charging, (await ClaimOfAsync(fleet, KeyA))!.Value.Purpose);
        }
        Assert.Equal(ChargingCycleWireStates.Charging, (await CycleAsync(fleet, KeyA)).WireState);
        Assert.Empty(await fleet.Context.Set<JourneyDemandRow>().AsNoTracking().ToArrayAsync(Token));
        Assert.Single(fleet.Riot.Creates);

        AtCharger(fleet, KeyA, 80, Charging);
        await RoundAsync(fleet);
        await RoundAsync(fleet);
        await RoundAsync(fleet);

        Assert.Equal(VehiclePurposes.Transport, (await ClaimOfAsync(fleet, KeyA))!.Value.Purpose);
        Assert.Contains(fleet.Riot.Creates, create => create.VehicleKey == KeyA && create.DestinationStationId == 12);
        Assert.Equal((KeyA, StationExclusivityStates.Occupied), await HolderAsync(fleet, Near.StationId));
    }

    // ---- 独立审查 M1：单 SUCCESS 之后不靠每一轮再读到 SUCCESS ----------------------------------------------------------------

    /// <summary>
    /// 到桩一经确认（单 <c>SUCCESS</c> 加车辆那一半），之后判开始充电、判充满只看车与电量，不再读那张单：单此后被删、或对 RIoT 答「查无此单」
    /// （真实形态 HTTP 200 不带 result，以及替身的 404），周期照样充满、放开用途，充满的车照样接活（独立审查 M1 第 1 条）。
    /// </summary>
    [Theory]
    [InlineData("real-absent")]
    [InlineData("not-found-404")]
    [InlineData("deleted")]
    public async Task OnceArrivedTheChargeOrderGoingMissingNeitherStopsTheCompletionNorKeepsTheVehicleFromWork(string shape)
    {
        await using FleetFixture fleet = await FleetAsync();
        JourneyRuntimeRow journey = await ChargingAsync(fleet, battery: 60);
        OrderGone(fleet, journey.PickupUpperId, shape);

        for (int minute = 0; minute < 15; minute++)
        {
            await fleet.HearFromEveryVehicleAsync();
            await fleet.RunRoundAsync(TimeSpan.FromMinutes(1));
        }
        ChargingCycleRow charging = await CycleAsync(fleet, KeyA);
        Assert.Equal((ChargingCyclePhases.Active, ChargingCycleWireStates.Charging), (charging.Phase, charging.WireState));
        Assert.Null((await ChargingJourneyAsync(fleet, AgvA))!.BlockReasonCode);
        Assert.Equal(VehiclePurposes.Charging, (await ClaimOfAsync(fleet, KeyA))!.Value.Purpose);
        Assert.Equal((KeyA, StationExclusivityStates.Occupied), await HolderAsync(fleet, Near.StationId));

        AtCharger(fleet, KeyA, 80, Charging);
        await RoundAsync(fleet);
        ChargingCycleRow full = await CycleAsync(fleet, KeyA);
        Assert.Equal((ChargingCyclePhases.Active, ChargingCycleWireStates.Complete), (full.Phase, full.WireState));
        Assert.Null(await ClaimOfAsync(fleet, KeyA));
        Assert.Equal(
            (JourneyRuntimeStage.Completed, ChargingExecutionReasons.Completed),
            ((await ChargingJourneyAsync(fleet, AgvA))!.Stage, (await ChargingJourneyAsync(fleet, AgvA))!.BlockReasonCode));

        fleet.Catalog.Set([FleetFixture.Demand(0, "N1-1", 0)]);
        for (int round = 0; round < 3; round++)
        {
            await RoundAsync(fleet);
        }
        Assert.Equal(VehiclePurposes.Transport, (await ClaimOfAsync(fleet, KeyA))!.Value.Purpose);
        Assert.Contains(fleet.Riot.Creates, create => create.VehicleKey == KeyA && create.DestinationStationId == 12);
        Assert.Single(fleet.Riot.Creates, create => create.DestinationStationId == Near.StationId);
    }

    /// <summary>
    /// 还没来得及判到桩，充电单就被删了或查无此单，而车停稳在这个桩上、新鲜地读到 <c>CHARGING</c>：按「到桩、单据丢失」处理——预占转占用、
    /// 周期进 <c>CHARGING</c>，告警恰好一次，之后按电量判充满（独立审查 M1 第 3 条）。不建单、不发命令。
    /// </summary>
    [Theory]
    [InlineData("real-absent")]
    [InlineData("deleted")]
    public async Task AChargeOrderGoneBeforeArrivalWasJudgedWhileTheVehicleChargesOnTheChargerIsTakenAsArrived(string shape)
    {
        await using FleetFixture fleet = await FleetAsync();
        JourneyRuntimeRow journey = await EnRouteAsync(fleet);
        OrderGone(fleet, journey.PickupUpperId, shape);
        AtCharger(fleet, KeyA, 40, Charging);

        for (int round = 0; round < 4; round++)
        {
            await RoundAsync(fleet);
        }

        ChargingCycleRow cycle = await CycleAsync(fleet, KeyA);
        Assert.Equal((ChargingCyclePhases.Active, ChargingCycleWireStates.Charging), (cycle.Phase, cycle.WireState));
        Assert.NotNull(cycle.ArrivedAt);
        Assert.Equal((KeyA, StationExclusivityStates.Occupied), await HolderAsync(fleet, Near.StationId));
        Assert.Single(fleet.EngineLog.Entries, entry => entry.EventId.Id == 2263);

        AtCharger(fleet, KeyA, 80, Charging);
        await RoundAsync(fleet);
        Assert.Equal(ChargingCycleWireStates.Complete, (await CycleAsync(fleet, KeyA)).WireState);
        Assert.Single(fleet.Riot.Creates);
        Assert.Empty(fleet.Riot.OrderCommands);
    }

    /// <summary>
    /// 单 <c>SUCCESS</c>，车辆那一半却一直不成立（读不到当前站、读到停在 212）：写 <c>CHARGING_ARRIVAL_NOT_PROVEN</c>，周期仍 <c>EN_ROUTE</c>、预占仍是预占，
    /// 不按超时推进；超过 <c>JourneyRuntime:OwnOrderRebuildRepeatWindow</c> 告警恰好一次（独立审查 M1 第 2 条）。车停到桩上之后照常到桩、码清掉。
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData(212)]
    public async Task AnOrderThatSucceededWithTheVehicleNeverProvenAtTheChargerIsNamedAndWarnedOnceAndNothingMoves(int? station)
    {
        await using FleetFixture fleet = await FleetAsync();
        JourneyRuntimeRow journey = await EnRouteAsync(fleet);
        fleet.Riot.CompleteOrder(journey.PickupUpperId);
        fleet.Riot.BatteryByVehicle[KeyA] = 40;
        fleet.Riot.VehicleOverrides[KeyA] = seen => seen with { CurrentStationId = station, BatteryState = Charging };

        for (int minute = 0; minute < 30; minute++)
        {
            await fleet.HearFromEveryVehicleAsync();
            await fleet.RunRoundAsync(TimeSpan.FromMinutes(1));
        }

        JourneyRuntimeRow held = (await ChargingJourneyAsync(fleet, AgvA))!;
        Assert.Equal(
            (JourneyRuntimeStage.AwaitingPickupArrival, ChargingExecutionReasons.ArrivalNotProven),
            (held.Stage, held.BlockReasonCode));
        Assert.Single(
            fleet.EngineLog.Entries,
            entry => entry.EventId.Id == 2261 && entry.Message.Contains(ChargingExecutionReasons.ArrivalNotProven, StringComparison.Ordinal));
        ChargingCycleRow cycle = await CycleAsync(fleet, KeyA);
        Assert.Equal((ChargingCycleWireStates.EnRoute, (DateTimeOffset?)null), (cycle.WireState, cycle.ArrivedAt));
        Assert.Equal((KeyA, StationExclusivityStates.Reserved), await HolderAsync(fleet, Near.StationId));
        Assert.Single(fleet.Riot.Creates);
        Assert.Empty(fleet.Riot.OrderCommands);
        Assert.Empty(fleet.Riot.EmergencyCommands);

        AtCharger(fleet, KeyA, 40, Charging);
        await RoundAsync(fleet);
        Assert.Null((await ChargingJourneyAsync(fleet, AgvA))!.BlockReasonCode);
        Assert.Equal((KeyA, StationExclusivityStates.Occupied), await HolderAsync(fleet, Near.StationId));
    }

    /// <summary>
    /// 已确认的充电单在到桩之前对 RIoT 答「查无此单」（真实形态），车也不在桩上充电：写 <c>CHARGING_ORDER_NOT_FOUND</c>，用途、预占、周期全保持，
    /// 不建单不发命令；超时告警恰好一次（独立审查 M1 第 2 条）。
    /// </summary>
    [Theory]
    [InlineData("elsewhere")]
    [InlineData("on-the-charger-not-charging")]
    public async Task AConfirmedChargeOrderAbsentBeforeArrivalIsNamedAndWarnedOnceAndEverythingIsKept(string where)
    {
        await using FleetFixture fleet = await FleetAsync();
        JourneyRuntimeRow journey = await EnRouteAsync(fleet);
        OrderGone(fleet, journey.PickupUpperId, "real-absent");
        fleet.Riot.BatteryByVehicle[KeyA] = 40;
        fleet.Riot.VehicleOverrides[KeyA] = seen => seen with
        {
            CurrentStationId = where == "elsewhere" ? 300 : Near.StationId,
            BatteryState = NotCharging,
        };

        for (int minute = 0; minute < 20; minute++)
        {
            await fleet.HearFromEveryVehicleAsync();
            await fleet.RunRoundAsync(TimeSpan.FromMinutes(1));
        }

        JourneyRuntimeRow held = (await ChargingJourneyAsync(fleet, AgvA))!;
        Assert.Equal(
            (JourneyRuntimeStage.AwaitingPickupArrival, ChargingExecutionReasons.OrderNotFound),
            (held.Stage, held.BlockReasonCode));
        Assert.Single(
            fleet.EngineLog.Entries,
            entry => entry.EventId.Id == 2261 && entry.Message.Contains(ChargingExecutionReasons.OrderNotFound, StringComparison.Ordinal));
        ChargingCycleRow cycle = await CycleAsync(fleet, KeyA);
        Assert.Equal((ChargingCyclePhases.Active, ChargingCycleWireStates.EnRoute), (cycle.Phase, cycle.WireState));
        Assert.Equal((KeyA, StationExclusivityStates.Reserved), await HolderAsync(fleet, Near.StationId));
        Assert.Equal(VehiclePurposes.Charging, (await ClaimOfAsync(fleet, KeyA))!.Value.Purpose);
        Assert.Single(fleet.Riot.Creates);
        Assert.Empty(fleet.Riot.OrderCommands);
        Assert.Empty(fleet.Riot.EmergencyCommands);
    }

    // ---- 独立审查 S6、S1、S3 -------------------------------------------------------------------------------------------------

    /// <summary>
    /// 充满之后一直留在桩上、没在充电的车，电量掉到强制充电线以下：这一轮以 <c>CHARGING_RECHARGED_ON_HELD_CHARGER</c> 收尾、桩以
    /// <c>CHARGER_RELEASED_FOR_RECHARGE</c> 释放，同一轮按正常分配链在原桩上重新充电（独立审查 S6）。原先它被「仍持有桩」与「需强制充电」同时挡住，没有出口。
    /// </summary>
    [Fact]
    public async Task AFullVehicleLeftOnItsChargerThatFallsBelowTheMandatoryLineChargesAgainOnTheSameCharger()
    {
        await using FleetFixture fleet = await FleetAsync();
        JourneyRuntimeRow first = await CompletedAsync(fleet);
        AtCharger(fleet, KeyA, 80, NotCharging);
        for (int round = 0; round < 3; round++)
        {
            await RoundAsync(fleet);
        }
        Assert.Equal((KeyA, StationExclusivityStates.Occupied), await HolderAsync(fleet, Near.StationId));

        AtCharger(fleet, KeyA, 20, NotCharging);
        for (int round = 0; round < 3; round++)
        {
            await RoundAsync(fleet);
        }

        ChargingCycleRow[] cycles = await fleet.Context.Set<ChargingCycleRow>().AsNoTracking()
            .Where(row => row.VehicleKey == KeyA).ToArrayAsync(Token);
        ChargingCycleRow old = Assert.Single(cycles, row => row.JourneyId == first.JourneyId);
        Assert.Equal((ChargingCyclePhases.Ended, ChargingExecutionReasons.RechargedOnHeldCharger), (old.Phase, old.EndReason));
        ChargingCycleRow again = Assert.Single(cycles, row => row.Phase != ChargingCyclePhases.Ended);
        Assert.Equal((Near.StationId, true), (again.StationId, again.JourneyId != first.JourneyId));
        StationExclusivityRow holder = await fleet.Context.Set<StationExclusivityRow>().AsNoTracking()
            .SingleAsync(row => row.StationId == Near.StationId, Token);
        Assert.Equal((KeyA, again.JourneyId), (holder.VehicleKey, holder.JourneyId));
        StationExclusivityRecordRow released = await fleet.Context.Set<StationExclusivityRecordRow>().AsNoTracking()
            .SingleAsync(row => row.JourneyId == first.JourneyId, Token);
        Assert.Equal(ChargingExecutionReasons.ChargerReleasedForRecharge, released.ReleaseReason);
        Assert.Equal(2, fleet.Riot.Creates.Count(create => create.VehicleKey == KeyA && create.DestinationStationId == Near.StationId));
    }

    /// <summary>
    /// 充满的车一直不离桩（停在桩上、不接活）：桩一直是它的占用，不按超时释放；充满超过 <c>JourneyRuntime:OwnOrderRebuildRepeatWindow</c> 告警恰好一次，
    /// 写明桩、车与缺哪一项确认（独立审查 S1，同失败周期的事件 2247）。
    /// </summary>
    [Fact]
    public async Task AFullVehicleThatNeverLeavesItsChargerIsWarnedOnceAndKeepsIt()
    {
        await using FleetFixture fleet = await FleetAsync();
        await CompletedAsync(fleet);
        AtCharger(fleet, KeyA, 80, NotCharging);

        for (int minute = 0; minute < 25; minute++)
        {
            await fleet.HearFromEveryVehicleAsync();
            await fleet.RunRoundAsync(TimeSpan.FromMinutes(1));
        }

        var warning = Assert.Single(fleet.ChargingLog.Entries, entry => entry.EventId.Id == 2249);
        Assert.Contains(KeyA, warning.Message, StringComparison.Ordinal);
        Assert.Contains("standing on the charger", warning.Message, StringComparison.Ordinal);
        Assert.Equal((KeyA, StationExclusivityStates.Occupied), await HolderAsync(fleet, Near.StationId));
        Assert.Equal(ChargingCycleWireStates.Complete, (await CycleAsync(fleet, KeyA)).WireState);
    }

    /// <summary>
    /// 「充满的车仍报 CHARGING 也能接活」只认周期 <c>COMPLETE</c>：周期还在 <c>CHARGING</c> 时，哪怕承载它的旅程与用途已经不在了，车也不接活
    /// （独立审查 S3：变异「不看 WireState」原先全绿，充电中不派单只靠旅程还开着那一层）。
    /// </summary>
    [Fact]
    public async Task ACycleStillChargingKeepsTheVehicleFromWorkEvenWithItsJourneyAndPurposeGone()
    {
        await using FleetFixture fleet = await FleetAsync();
        JourneyRuntimeRow journey = await ChargingAsync(fleet, battery: 60);
        JourneyRuntimeRow row = await fleet.Context.JourneyRuntimes.SingleAsync(item => item.JourneyId == journey.JourneyId, Token);
        row.Stage = JourneyRuntimeStage.Completed;
        await fleet.Context.SaveChangesAsync(Token);
        await fleet.Context.Set<VehiclePurposeClaimRow>().Where(item => item.VehicleKey == KeyA).ExecuteDeleteAsync(Token);
        fleet.Context.ChangeTracker.Clear();
        AtCharger(fleet, KeyA, 90, Charging);

        fleet.Catalog.Set([FleetFixture.Demand(0, "N1-1", 0)]);
        for (int round = 0; round < 3; round++)
        {
            await RoundAsync(fleet);
        }

        Assert.Equal(ChargingCycleWireStates.Charging, (await CycleAsync(fleet, KeyA)).WireState);
        Assert.Null(await ClaimOfAsync(fleet, KeyA));
        Assert.DoesNotContain(fleet.Riot.Creates, create => create.DestinationStationId == 12);
    }

    // ---- 夹具 ------------------------------------------------------------------------------------------------------------

    /// <summary>A 低电、承诺、建单、确认：周期 <c>EN_ROUTE</c>。</summary>
    private static async Task<JourneyRuntimeRow> EnRouteAsync(FleetFixture fleet)
    {
        fleet.Riot.BatteryByVehicle[KeyA] = 20;
        JourneyRuntimeRow journey = await CommittedAndSentAsync(fleet);
        Assert.Equal(ChargingCycleWireStates.EnRoute, (await CycleAsync(fleet, KeyA)).WireState);
        return journey;
    }

    /// <summary>单到 <c>SUCCESS</c>、车停在 211 上报 <c>CHARGING</c>：到桩并开始充电，周期 <c>CHARGING</c>。</summary>
    private static async Task<JourneyRuntimeRow> ChargingAsync(FleetFixture fleet, int battery)
    {
        JourneyRuntimeRow journey = await EnRouteAsync(fleet);
        fleet.Riot.CompleteOrder(journey.PickupUpperId);
        AtCharger(fleet, KeyA, battery, Charging);
        await RoundAsync(fleet);
        Assert.Equal(ChargingCycleWireStates.Charging, (await CycleAsync(fleet, KeyA)).WireState);
        return journey;
    }

    /// <summary>充到完成阈值：周期 <c>COMPLETE</c>，车仍插在桩上报 <c>CHARGING</c>。</summary>
    private static async Task<JourneyRuntimeRow> CompletedAsync(FleetFixture fleet)
    {
        JourneyRuntimeRow journey = await ChargingAsync(fleet, battery: 60);
        AtCharger(fleet, KeyA, 80, Charging);
        await RoundAsync(fleet);
        Assert.Equal(ChargingCycleWireStates.Complete, (await CycleAsync(fleet, KeyA)).WireState);
        return journey;
    }

    /// <summary>真车载端开着本服务端的单时的会话（形状同 <c>PickupDispatchPlanPastOwnOrderTests.DropSessionOnOwnOrderAsync</c>）。</summary>
    private static async Task DropSessionOnOwnOrderAsync(FleetFixture fleet)
    {
        SessionRecoveryRow session = await fleet.Context.SessionRecoveries.SingleAsync(row => row.AgvId == AgvA, Token);
        session.Readiness = SessionReadiness.RecoveryRequired;
        session.ReasonCode = "DEPARTURE_SAFETY_NOT_READY";
        session.DepartureSafe = false;
        session.SafetyReasonCodesJson = """["VEHICLE_NOT_READY"]""";
        session.SafetyUnknownPresent = true;
        session.UpdatedAt = fleet.Clock.GetUtcNowWithoutTick();
        await fleet.Context.SaveChangesAsync(Token);
        fleet.Context.ChangeTracker.Clear();
    }

    /// <summary>这张单在 RIoT 上不见了：真实形态的「查无此单」（HTTP 200 不带 result）、替身的 404，或终态 <c>DELETED</c>。</summary>
    private static void OrderGone(FleetFixture fleet, string upperId, string shape)
    {
        switch (shape)
        {
            case "real-absent":
                fleet.Riot.AnswersAbsentAsRealRiot = true;
                fleet.Riot.PutOrder(new RiotOrderObservation(upperId, RiotOrderObservationKind.NotFound, null));
                break;
            case "not-found-404":
                fleet.Riot.PutOrder(new RiotOrderObservation(upperId, RiotOrderObservationKind.NotFound, null));
                break;
            case "deleted":
                fleet.Riot.PutOrder(new RiotOrderObservation(
                    upperId, RiotOrderObservationKind.Terminal, $"ORDER-{upperId}", RiotOrderState.Deleted, KeyA, Map, Near.StationId));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(shape), shape, null);
        }
    }

    private static void AtCharger(FleetFixture fleet, string vehicleKey, int battery, string batteryState)
    {
        fleet.Riot.BatteryByVehicle[vehicleKey] = battery;
        fleet.Riot.VehicleOverrides[vehicleKey] = seen => seen with { CurrentStationId = Near.StationId, BatteryState = batteryState };
    }

    /// <summary>这辆车最近的那个充电周期（未结束的优先）。</summary>
    private static async Task<ChargingCycleRow> CycleAsync(FleetFixture fleet, string vehicleKey)
    {
        ChargingCycleRow[] cycles = await fleet.Context.Set<ChargingCycleRow>().AsNoTracking()
            .Where(row => row.VehicleKey == vehicleKey).ToArrayAsync(Token);
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
