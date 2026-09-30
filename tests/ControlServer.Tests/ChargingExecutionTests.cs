using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Dashboard;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Charging;
using ControlServer.Host.Runtime.Dispatch;
using ControlServer.Host.Runtime.Faults;
using ControlServer.Host.Runtime.Commands;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using static ControlServer.Tests.ChargingAllocationTests;
using FleetFixture = ControlServer.Tests.MultiVehicleExecutionTests.FleetFixture;

namespace ControlServer.Tests;

/// <summary>
/// 充电承诺之后到去桩途中（批次9-06，control-server#404）：计划与投影、出发前安全门、建单与对账、结果未知、三个崩溃点、重连补发、
/// 充电单被取消（不重建）、途中 <c>HANG</c>、急停与故障。到桩之后的充电、充满、离桩与释放在批次9-07。夹具与路网同
/// <see cref="ChargingAllocationTests"/>：第一轮分配承诺，第二轮引擎推进（发 <c>ALLOCATED</c>、过安全门、建单、确认后发 <c>EN_ROUTE</c>）。
/// </summary>
public sealed class ChargingExecutionTests
{
    private const int Map = 25;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string AgvA => FleetFixture.AgvIds[0];
    private static string KeyA => FleetFixture.VehicleKeys[0];

    // ---- 计划与投影 --------------------------------------------------------------------------------------------------

    /// <summary>
    /// 已承诺的充电在线上是一个 <c>CHARGER</c> 停靠：计划一条 <c>stopPurposeCategory=CHARGER</c>、<c>legType</c> 与 <c>demandId</c> 为空的腿，
    /// 业务状态 <c>activePurpose=CHARGING</c>、没有装货阶段、<c>manualChargingHold=false</c>、<c>batteryState</c> 取承诺时的投影。
    /// 先是 <c>ALLOCATED</c>（腿 <c>PLANNED</c>：还没有出发），RIoT 确认建单之后才是 <c>EN_ROUTE</c>（腿 <c>ACTIVE</c>）。没有清单、录入请求
    /// 与装卸命令。消息 id 是 <c>StableUuid(journeyId|用途)</c>。
    /// </summary>
    [Fact]
    public async Task ACommittedChargingIsPublishedAsOneChargerLegAllocatedThenEnRoute()
    {
        await using FleetFixture fleet = await FleetAsync();
        fleet.Riot.BatteryByVehicle[KeyA] = 20;

        JourneyRuntimeRow journey = await CommittedAndSentAsync(fleet);

        JsonElement[] plans = await PayloadsAsync(fleet, AgvA, "UpcomingStopPlanSnapshot");
        Assert.Equal(["PLANNED", "ACTIVE"], [.. plans.Select(plan => Assert.Single(plan.GetProperty("legs").EnumerateArray()).GetProperty("state").GetString())]);
        foreach (JsonElement plan in plans)
        {
            JsonElement leg = Assert.Single(plan.GetProperty("legs").EnumerateArray());
            Assert.Equal(
                (JourneyPlanBuilder.ChargerStopPurpose, Near.StationName),
                (leg.GetProperty("stopPurposeCategory").GetString(), leg.GetProperty("stationId").GetString()));
            Assert.Equal(JsonValueKind.Null, leg.GetProperty("legType").ValueKind);
            Assert.Equal(JsonValueKind.Null, leg.GetProperty("demandId").ValueKind);
        }
        JsonElement[] states = await PayloadsAsync(fleet, AgvA, "VehicleBusinessStateSnapshot");
        Assert.Equal(
            [ChargingCycleWireStates.Allocated, ChargingCycleWireStates.EnRoute],
            [.. states.Select(state => state.GetProperty("chargingCycleState").GetString())]);
        foreach (JsonElement state in states)
        {
            Assert.Equal(VehicleActivePurposes.Charging, state.GetProperty("activePurpose").GetString());
            Assert.Equal(JsonValueKind.Null, state.GetProperty("loadingPhase").ValueKind);
            Assert.False(state.GetProperty("manualChargingHold").GetBoolean());
            Assert.Equal(BatteryStates.MandatoryCharge, state.GetProperty("batteryState").GetString());
        }
        Assert.Empty(await PayloadsAsync(fleet, AgvA, "CurrentStopWorklistSnapshot"));
        Assert.Empty(await PayloadsAsync(fleet, AgvA, "SublotEntryRequested"));
        Assert.Empty(await PayloadsAsync(fleet, AgvA, "SlotOperationCommand"));
        string[] ids = await fleet.Context.ProtocolOutbox.AsNoTracking()
            .Where(row => row.MessageType == "UpcomingStopPlanSnapshot" || row.MessageType == "VehicleBusinessStateSnapshot")
            .Select(row => row.MessageId).ToArrayAsync(Token);
        Assert.Equal(
            new[]
            {
                ChargingJourneyShape.AllocatedPlanMessageId(journey.JourneyId),
                ChargingJourneyShape.AllocatedStateMessageId(journey.JourneyId),
                ChargingJourneyShape.EnRoutePlanMessageId(journey.JourneyId),
                ChargingJourneyShape.EnRouteStateMessageId(journey.JourneyId),
            }.Order(StringComparer.Ordinal),
            ids.Order(StringComparer.Ordinal));

        // The order: created once, a charge order (move + act 78) to the reserved charger, under the cycle's stable upperId.
        OrderIntent created = Assert.Single(fleet.Riot.CreatedIntents);
        Assert.Equal(
            (KeyA, ChargingIdentity.UpperIdFor(journey.JourneyId), Near.StationId, OrderShapes.Charge),
            (created.VehicleKey, created.UpperId, created.DestinationStationId, created.OrderShape));
        ChargingCycleRow cycle = await fleet.Context.Set<ChargingCycleRow>().AsNoTracking().SingleAsync(Token);
        Assert.Equal((ChargingCycleWireStates.EnRoute, created.UpperId), (cycle.WireState, cycle.UpperId));
        Assert.NotNull(cycle.OrderConfirmedAt);
    }

    /// <summary>
    /// 建一次单；之后在途的每一轮只对账，不再建。途中来了一条正好适合它的搬运，它不接、也不被问追加（<c>REQ-0290</c>：已开始的用途不被新用途抢）。
    /// </summary>
    [Fact]
    public async Task AChargingJourneyCreatesItsOrderOnceAndTakesNoTransportOnTheWay()
    {
        await using FleetFixture fleet = await FleetAsync();
        fleet.Riot.BatteryByVehicle[KeyA] = 20;
        JourneyRuntimeRow journey = await CommittedAndSentAsync(fleet);
        await fleet.AllowEnRouteAppendAsync(1_000_000);
        fleet.Catalog.Set([FleetFixture.Demand(0, "N1-1", 0)]);

        for (int round = 0; round < 3; round++)
        {
            await RoundAsync(fleet);
        }

        Assert.Equal([journey.JourneyId], await fleet.Context.JourneyRuntimes.AsNoTracking().Select(row => row.JourneyId).ToArrayAsync(Token));
        Assert.Empty(await fleet.Context.Set<JourneyDemandRow>().AsNoTracking().ToArrayAsync(Token));
        Assert.Single(fleet.Riot.Creates);
        Assert.Equal(JourneyRuntimeStage.AwaitingPickupArrival, (await ChargingJourneyAsync(fleet, AgvA))!.Stage);
    }

    // ---- 建单结果未知（REQ-0283）----------------------------------------------------------------------------------------

    /// <summary>
    /// 建单结果未知：车辆、充电意图、目标桩、预占、周期五样全保持；不换单号、不重复建单、不释放；周期停在 <c>ALLOCATED</c>，线上不报
    /// <c>EN_ROUTE</c>。RIoT 之后读到那张单在，就按同一个单号确认下去，这时才报 <c>EN_ROUTE</c>。
    /// </summary>
    [Fact]
    public async Task AnUnknownCreateResultKeepsAllFiveAndNeverReportsEnRoute()
    {
        await using FleetFixture fleet = await FleetAsync();
        fleet.Riot.BatteryByVehicle[KeyA] = 20;
        fleet.Riot.CreateAnswer = intent => new RiotOrderObservation(intent.UpperId, RiotOrderObservationKind.Unknown, null);
        await RoundAsync(fleet);
        await RoundAsync(fleet);
        fleet.Riot.CreateAnswer = null;
        (string CycleId, string JourneyId, string UpperId) before = await CommitmentOfAsync(fleet, KeyA);
        Assert.Equal(
            ChargingExecutionReasons.LegOutcomeCode(MovementDispatchOutcome.ResultUnknown),
            (await ChargingJourneyAsync(fleet, AgvA))!.BlockReasonCode);

        // The battery goes on falling meanwhile: nothing about the commitment changes for it (REQ-0169).
        fleet.Riot.BatteryByVehicle[KeyA] = 16;
        for (int round = 0; round < 3; round++)
        {
            await RoundAsync(fleet);
        }

        Assert.Single(fleet.Riot.Creates);
        Assert.Equal(before, await CommitmentOfAsync(fleet, KeyA));
        Assert.Equal((KeyA, StationExclusivityStates.Reserved), await HolderAsync(fleet, Near.StationId));
        ChargingCycleRow cycle = await fleet.Context.Set<ChargingCycleRow>().AsNoTracking().SingleAsync(Token);
        Assert.Equal((ChargingCycleWireStates.Allocated, ChargingCyclePhases.Active), (cycle.WireState, cycle.Phase));
        Assert.Equal(
            [ChargingCycleWireStates.Allocated],
            [.. (await PayloadsAsync(fleet, AgvA, "VehicleBusinessStateSnapshot")).Select(state => state.GetProperty("chargingCycleState").GetString())]);
        Assert.Equal(
            ["PLANNED"],
            [.. (await PayloadsAsync(fleet, AgvA, "UpcomingStopPlanSnapshot"))
                .Select(plan => Assert.Single(plan.GetProperty("legs").EnumerateArray()).GetProperty("state").GetString())]);

        fleet.Riot.PutOrder(new RiotOrderObservation(
            before.UpperId, RiotOrderObservationKind.Active, "ORDER-LATE", RiotOrderState.Executing, KeyA, Map, Near.StationId));
        await RoundAsync(fleet);

        OrderIntentRow intent = await fleet.Context.OrderIntents.AsNoTracking().SingleAsync(Token);
        Assert.Equal(("CONFIRMED", "ORDER-LATE", before.UpperId), (intent.Status, intent.OrderId, intent.UpperId));
        Assert.Single(fleet.Riot.Creates);
        Assert.Null((await ChargingJourneyAsync(fleet, AgvA))!.BlockReasonCode);
        Assert.Equal(
            ChargingCycleWireStates.EnRoute,
            (await fleet.Context.Set<ChargingCycleRow>().AsNoTracking().SingleAsync(Token)).WireState);
    }

    // ---- 出发前安全门 ----------------------------------------------------------------------------------------------------

    public static TheoryData<string> UnsafeDepartures => ["slot-unlocked", "blocking-fact", "summary-not-locked", "session-not-ready"];

    /// <summary>
    /// 去充电是非业务移动，v2 发不了它的 <c>PreDepartureSafetyCheck</c>，建单之前由服务端自己核同一组事实（与空闲返回同一道门）：仓门没锁、
    /// 摘要带阻断事实、摘要说仓位没锁、会话不就绪——都不建单、写 <c>CHARGING_DEPARTURE_NOT_PROVEN</c>，承诺、预占与周期保持；安全状态恢复后
    /// 下一轮建单，只建一张。看板的阻断卡片上是中文说明。
    /// </summary>
    [Theory]
    [MemberData(nameof(UnsafeDepartures))]
    public async Task NoChargingOrderIsCreatedUntilTheDepartureSafetyGateIsMet(string gap)
    {
        await using FleetFixture fleet = await FleetAsync();
        fleet.Riot.BatteryByVehicle[KeyA] = 20;
        await RoundAsync(fleet);
        (string CycleId, string JourneyId, string UpperId) before = await CommitmentOfAsync(fleet, KeyA);
        switch (gap)
        {
            case "slot-unlocked":
                await fleet.ReplaceSafetySnapshotAsync(AgvA, unlockedSlot: 3);
                break;
            case "blocking-fact":
                await fleet.ReplaceSafetySnapshotAsync(AgvA, reasonCodes: ["SLOT_DOOR_SENSOR_FAULT"]);
                break;
            case "summary-not-locked":
                await fleet.ReplaceSafetySnapshotAsync(AgvA, allTargetSlotsLocked: false);
                break;
            case "session-not-ready":
                await fleet.DropSessionAsync(AgvA);
                break;
        }

        await RoundAsync(fleet);
        await RoundAsync(fleet);

        Assert.Empty(fleet.Riot.Creates);
        JourneyRuntimeRow waiting = (await ChargingJourneyAsync(fleet, AgvA))!;
        Assert.Equal(ChargingExecutionReasons.DepartureNotProven, waiting.BlockReasonCode);
        Assert.Equal(before, await CommitmentOfAsync(fleet, KeyA));
        Assert.Equal((KeyA, StationExclusivityStates.Reserved), await HolderAsync(fleet, Near.StationId));
        object fact = await new BlockedJourneysQueryEndpoint(BlockedJourneyEscalationOptions.Default, fleet.Clock)
            .ReadAsync(fleet.Context, Token);
        using (JsonDocument card = JsonDocument.Parse(JsonSerializer.Serialize(fact)))
        {
            JsonElement row = Assert.Single(
                card.RootElement.GetProperty("journeys").EnumerateArray(),
                item => item.GetProperty("agvId").GetString() == AgvA);
            Assert.Equal(
                (ChargingExecutionReasons.DepartureNotProven,
                 BlockedJourneysQueryEndpoint.Descriptions[ChargingExecutionReasons.DepartureNotProven]),
                (row.GetProperty("blockReasonCode").GetString(), row.GetProperty("blockReasonDescription").GetString()));
        }

        if (gap == "session-not-ready")
        {
            return;
        }
        await fleet.ReplaceSafetySnapshotAsync(AgvA);
        await RoundAsync(fleet);
        await RoundAsync(fleet);

        Assert.Single(fleet.Riot.Creates);
        Assert.Null((await ChargingJourneyAsync(fleet, AgvA))!.BlockReasonCode);
        Assert.Equal(before, await CommitmentOfAsync(fleet, KeyA));
    }

    /// <summary>
    /// 承诺之后、建单之前，这辆车的充电策略被收回或读坏了（共用的投运判定不再放行）：不出发，与空闲返回同一条——策略坏了时充电也一样不动。
    /// </summary>
    [Fact]
    public async Task ACommittedChargingDoesNotSetOffOnceItsVehicleIsNoLongerCommissioned()
    {
        await using FleetFixture fleet = await FleetAsync();
        fleet.Riot.BatteryByVehicle[KeyA] = 12;
        await RoundAsync(fleet);
        Assert.NotNull(await ClaimOfAsync(fleet, KeyA));

        fleet.ChargingPolicy = TestChargingPolicies.AllApprovedAt(15);
        await fleet.RecreateEngineAsync();
        await RoundAsync(fleet);
        await RoundAsync(fleet);

        Assert.Empty(fleet.Riot.Creates);
        Assert.Equal(ChargingExecutionReasons.DepartureNotProven, (await ChargingJourneyAsync(fleet, AgvA))!.BlockReasonCode);
    }

    /// <summary>这趟充电的桩预占行不在了、而单还没发出过：不建单，写明原因等人查，用途占有与周期不动。</summary>
    [Fact]
    public async Task WithoutItsReservationAChargingJourneyCreatesNoOrder()
    {
        await using FleetFixture fleet = await FleetAsync();
        fleet.Riot.BatteryByVehicle[KeyA] = 20;
        await RoundAsync(fleet);
        await fleet.Context.Set<StationExclusivityRow>().ExecuteDeleteAsync(Token);

        await RoundAsync(fleet);
        await RoundAsync(fleet);

        Assert.Empty(fleet.Riot.Creates);
        Assert.Equal(ChargingExecutionReasons.ReservationNotHeld, (await ChargingJourneyAsync(fleet, AgvA))!.BlockReasonCode);
        Assert.Equal(VehiclePurposes.Charging, (await ClaimOfAsync(fleet, KeyA))!.Value.Purpose);
    }

    // ---- 崩溃点 --------------------------------------------------------------------------------------------------------

    /// <summary>
    /// 承诺已提交、RIoT 建单之前进程崩溃：重启后（新的引擎实例、同一个库）按同一个 <c>upperId</c> 对账、建一次，周期与旅程还是那一个。
    /// </summary>
    [Fact]
    public async Task ACrashBetweenTheCommitmentAndTheCreateIsMadeGoodOnceUnderTheSameUpperId()
    {
        await using FleetFixture fleet = await FleetAsync();
        fleet.Riot.BatteryByVehicle[KeyA] = 20;
        await RoundAsync(fleet);
        (string CycleId, string JourneyId, string UpperId) before = await CommitmentOfAsync(fleet, KeyA);
        Assert.Empty(fleet.Riot.Creates);

        await fleet.RecreateEngineAsync();
        await RoundAsync(fleet);
        await RoundAsync(fleet);

        Assert.Equal(before.UpperId, Assert.Single(fleet.Riot.Creates).UpperId);
        Assert.Equal(before, await CommitmentOfAsync(fleet, KeyA));
        Assert.Single(await fleet.Context.Set<ChargingCycleRow>().AsNoTracking().ToArrayAsync(Token));
        Assert.Single(await fleet.Context.JourneyRuntimes.AsNoTracking().ToArrayAsync(Token));
    }

    /// <summary>
    /// RIoT 已建单、本地还没记下结果时崩溃：重启后按 <c>upperId</c> 对账认回这张单，不建第二张，<c>upperId</c> 不变，周期进入 <c>EN_ROUTE</c>。
    /// </summary>
    [Fact]
    public async Task AnOrderCreatedButNotRecordedIsRecognisedByItsUpperIdAndNeverCreatedAgain()
    {
        await using FleetFixture fleet = await FleetAsync();
        fleet.Riot.BatteryByVehicle[KeyA] = 20;
        await RoundAsync(fleet);
        (string CycleId, string JourneyId, string UpperId) before = await CommitmentOfAsync(fleet, KeyA);
        // RIoT takes the order, and the process dies before the answer is written down.
        fleet.Riot.CreateAnswer = intent =>
        {
            fleet.Riot.PutOrder(new RiotOrderObservation(
                intent.UpperId, RiotOrderObservationKind.Active, "ORDER-UNRECORDED", RiotOrderState.Executing, intent.VehicleKey,
                intent.MapId, intent.DestinationStationId));
            throw new InvalidOperationException("Injected crash after RIoT accepted the create.");
        };
        await fleet.HearFromEveryVehicleAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => fleet.RunRoundAsync(TimeSpan.FromSeconds(1)));
        fleet.Riot.CreateAnswer = null;

        await fleet.RecreateEngineAsync();
        await RoundAsync(fleet);
        await RoundAsync(fleet);

        Assert.Single(fleet.Riot.Creates);
        Assert.Equal(before, await CommitmentOfAsync(fleet, KeyA));
        OrderIntentRow intent = await fleet.Context.OrderIntents.AsNoTracking().SingleAsync(Token);
        Assert.Equal(("CONFIRMED", "ORDER-UNRECORDED"), (intent.Status, intent.OrderId));
        Assert.Equal(
            ChargingCycleWireStates.EnRoute,
            (await fleet.Context.Set<ChargingCycleRow>().AsNoTracking().SingleAsync(Token)).WireState);
    }

    // ---- 重放与重连 ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// 车载端断线时承诺、确认都发生了：四张快照留在发件箱里；重连之后按原 <c>messageId</c> 补发，发件箱里每样仍是那几行，不产生第二份。
    /// </summary>
    [Fact]
    public async Task AfterAReconnectTheChargerPlanAndChargingStateAreResentUnderTheirOwnIdsWithoutASecondCopy()
    {
        await using FleetFixture fleet = await FleetAsync();
        fleet.Riot.BatteryByVehicle[KeyA] = 20;
        fleet.Peer.Unavailable = AgvA;
        JourneyRuntimeRow journey = await CommittedAndSentAsync(fleet);
        string[] ids =
        [
            ChargingJourneyShape.AllocatedPlanMessageId(journey.JourneyId),
            ChargingJourneyShape.AllocatedStateMessageId(journey.JourneyId),
            ChargingJourneyShape.EnRoutePlanMessageId(journey.JourneyId),
            ChargingJourneyShape.EnRouteStateMessageId(journey.JourneyId),
        ];
        Assert.DoesNotContain(fleet.Peer.Delivered, line => ids.Contains(line.MessageId));

        fleet.Peer.Unavailable = null;
        await fleet.HearFromEveryVehicleAsync();
        await RoundAsync(fleet);

        Assert.All(ids, id => Assert.Contains(fleet.Peer.Delivered, line => line.MessageId == id));
        Assert.Equal(2, (await PayloadsAsync(fleet, AgvA, "UpcomingStopPlanSnapshot")).Length);
        Assert.Equal(2, (await PayloadsAsync(fleet, AgvA, "VehicleBusinessStateSnapshot")).Length);
    }

    // ---- 充电单在 RIoT 里被取消（票面第 10 条）---------------------------------------------------------------------------

    /// <summary>
    /// 第一行（<c>ALLOCATED</c>／<c>EN_ROUTE</c>，车还没到桩）：不重建（与空闲返回单同一条路，<c>REQ-0360</c> 的重建承载需求，充电单没有需求）。
    /// 车还可能在动时，预占、周期与用途占有都保持——不许「取消即释放预占」；车证明停稳、没有活动订单之后才按已确认失败收尾：周期结束、用途释放、
    /// 撤下 <c>CHARGING</c>。桩预占这时仍在，下一轮按三项确认（不在充电、车不在桩上、桩可确认空闲）才释放。冷却期内这个桩对这辆车是「刚失败」，
    /// 不再分给它；冷却过后它按正常候选链重新排队，是一个新的周期、新的单号。
    /// </summary>
    [Fact]
    public async Task AChargingOrderCancelledOnTheWayIsNeverRebuiltAndEndsOnlyOnceTheVehicleIsProvenStopped()
    {
        await using FleetFixture fleet = await FleetAsync();
        fleet.Riot.BatteryByVehicle[KeyA] = 20;
        JourneyRuntimeRow journey = await CommittedAndSentAsync(fleet);
        (string CycleId, string JourneyId, string UpperId) before = await CommitmentOfAsync(fleet, KeyA);
        fleet.Riot.CancelOrder(journey.PickupUpperId);
        fleet.Riot.VehicleOverrides[KeyA] = seen => seen with { CurrentStationId = 12, Speed = 0.4, ProcState = "RUNNING" };

        await RoundAsync(fleet);
        await RoundAsync(fleet);

        Assert.Equal(before, await CommitmentOfAsync(fleet, KeyA));
        Assert.Equal((KeyA, StationExclusivityStates.Reserved), await HolderAsync(fleet, Near.StationId));
        Assert.Equal(JourneyRuntimeEngine.OrderEndedWithoutArrivalReason, (await ChargingJourneyAsync(fleet, AgvA))!.BlockReasonCode);
        Assert.Empty(await fleet.Context.OwnOrderRebuilds.AsNoTracking().ToArrayAsync(Token));

        fleet.Riot.VehicleOverrides[KeyA] = seen => seen with { CurrentStationId = 12 };
        await RoundAsync(fleet);

        JourneyRuntimeRow ended = await fleet.Context.JourneyRuntimes.AsNoTracking().SingleAsync(row => row.JourneyId == journey.JourneyId, Token);
        Assert.Equal((JourneyRuntimeStage.Completed, ChargingExecutionReasons.OrderEnded), (ended.Stage, ended.BlockReasonCode));
        ChargingCycleRow cycle = await fleet.Context.Set<ChargingCycleRow>().AsNoTracking().SingleAsync(Token);
        Assert.Equal((ChargingCyclePhases.Ended, ChargingExecutionReasons.OrderEnded), (cycle.Phase, cycle.EndReason));
        Assert.NotNull(cycle.EndedAt);
        VehiclePurposeClaimRecordRow record = await fleet.Context.Set<VehiclePurposeClaimRecordRow>().AsNoTracking()
            .SingleAsync(row => row.JourneyId == journey.JourneyId, Token);
        Assert.Equal(ChargingExecutionReasons.OrderEnded, record.ReleaseReason);
        JsonElement lastState = (await PayloadsAsync(fleet, AgvA, "VehicleBusinessStateSnapshot")).Last();
        Assert.Equal(JsonValueKind.Null, lastState.GetProperty("activePurpose").ValueKind);
        Assert.Equal(ChargingCycleWireStates.NotCharging, lastState.GetProperty("chargingCycleState").GetString());
        Assert.Empty((await PayloadsAsync(fleet, AgvA, "UpcomingStopPlanSnapshot")).Last().GetProperty("legs").EnumerateArray());

        // The reservation is released on the three confirmations, in the next round's sweep -- never by the cancellation.
        await RoundAsync(fleet);
        Assert.Null(await StationAsync(fleet, Near.StationId));
        StationExclusivityRecordRow passage = await fleet.Context.Set<StationExclusivityRecordRow>().AsNoTracking()
            .SingleAsync(row => row.JourneyId == journey.JourneyId, Token);
        Assert.Equal(ChargingExecutionReasons.ReservationReleasedAfterEndedCycle, passage.ReleaseReason);

        // Within the cooldown the charger is "just failed" for this vehicle; nothing is rebuilt and nothing new is created.
        await RoundAsync(fleet);
        Assert.Null(await ClaimOfAsync(fleet, KeyA));
        Assert.Contains(
            $"{Near.StationId}={ChargingAllocationReasons.ChargerFailedJustNow}",
            fleet.ChargingBoard.Verdicts[AgvA].Detail, StringComparison.Ordinal);
        Assert.Single(fleet.Riot.Creates);
        Assert.Empty(await fleet.Context.OwnOrderRebuilds.AsNoTracking().ToArrayAsync(Token));

        // Past it, the vehicle is queued and allocated afresh: another cycle, another journey, another upperId.
        await fleet.HearFromEveryVehicleAsync();
        await fleet.RunRoundAsync(fleet.Options.OwnOrderRebuildDelay);
        (string CycleId, string JourneyId, string UpperId) again = await CommitmentOfAsync(fleet, KeyA);
        Assert.NotEqual(before.CycleId, again.CycleId);
        Assert.NotEqual(before.UpperId, again.UpperId);
    }

    /// <summary>
    /// 第二行（车已到桩、充电动作之后单进 <c>HANG</c>，人在 RIoT 里把它取消）：一律不重建——那次取消是旧单的预期终结，重建等于向原桩再发一次
    /// 充电动作。车停在桩上，预占不放（三项确认里「原车离开」不成立）；这辆车自己还占着桩，冷却过后也不再被分配、不建第二张单。
    /// </summary>
    [Fact]
    public async Task AChargingOrderCancelledAtTheChargerIsNotRebuiltAndNothingIsSentToThatChargerAgain()
    {
        await using FleetFixture fleet = await FleetAsync();
        fleet.Riot.BatteryByVehicle[KeyA] = 20;
        JourneyRuntimeRow journey = await CommittedAndSentAsync(fleet);
        fleet.Riot.VehicleOverrides[KeyA] = seen => seen with { CurrentStationId = Near.StationId };
        fleet.Riot.HangOrder(journey.PickupUpperId);
        await RoundAsync(fleet);
        Assert.Equal(JourneyRuntimeEngine.OrderHangReason, (await ChargingJourneyAsync(fleet, AgvA))!.BlockReasonCode);

        fleet.Riot.CancelOrder(journey.PickupUpperId);
        await RoundAsync(fleet);
        await RoundAsync(fleet);
        await fleet.HearFromEveryVehicleAsync();
        await fleet.RunRoundAsync(fleet.Options.OwnOrderRebuildDelay);
        await RoundAsync(fleet);

        JourneyRuntimeRow ended = await fleet.Context.JourneyRuntimes.AsNoTracking().SingleAsync(row => row.JourneyId == journey.JourneyId, Token);
        Assert.Equal((JourneyRuntimeStage.Completed, ChargingExecutionReasons.OrderEnded), (ended.Stage, ended.BlockReasonCode));
        Assert.Empty(await fleet.Context.OwnOrderRebuilds.AsNoTracking().ToArrayAsync(Token));
        Assert.Single(fleet.Riot.Creates);
        Assert.Null(await ClaimOfAsync(fleet, KeyA));
        Assert.Equal((KeyA, StationExclusivityStates.Reserved), await HolderAsync(fleet, Near.StationId));
        Assert.Equal(ChargingAllocationReasons.VehicleStillHoldsCharger, fleet.ChargingBoard.Verdicts[AgvA].Reason);
        Assert.Single(await fleet.Context.JourneyRuntimes.AsNoTracking().ToArrayAsync(Token));
    }

    /// <summary>第三行（单已是终态 <c>SUCCESS</c>，充电已接上）：没有可取消的单；本票到此为止，周期、用途与预占原样留给批次9-07 接着做。</summary>
    [Fact]
    public async Task AChargingOrderThatSucceededIsLeftForTheRestOfTheCycle()
    {
        await using FleetFixture fleet = await FleetAsync();
        fleet.Riot.BatteryByVehicle[KeyA] = 20;
        JourneyRuntimeRow journey = await CommittedAndSentAsync(fleet);
        (string CycleId, string JourneyId, string UpperId) before = await CommitmentOfAsync(fleet, KeyA);
        fleet.Riot.CompleteOrder(journey.PickupUpperId);
        fleet.Riot.VehicleOverrides[KeyA] = seen => seen with { CurrentStationId = Near.StationId, BatteryState = "CHARGING" };

        for (int round = 0; round < 3; round++)
        {
            await RoundAsync(fleet);
        }

        Assert.Equal(before, await CommitmentOfAsync(fleet, KeyA));
        JourneyRuntimeRow still = (await ChargingJourneyAsync(fleet, AgvA))!;
        Assert.Equal((JourneyRuntimeStage.AwaitingPickupArrival, (string?)null), (still.Stage, still.BlockReasonCode));
        Assert.Equal((KeyA, StationExclusivityStates.Reserved), await HolderAsync(fleet, Near.StationId));
        Assert.Single(fleet.Riot.Creates);
        Assert.Empty(await fleet.Context.OwnOrderRebuilds.AsNoTracking().ToArrayAsync(Token));
    }

    /// <summary>
    /// 被取消之后的第二道护栏（与搬运自建单「再次出问题即停」对等）：冷却过后重新分配的那一次又被取消——窗口内第二次，不再自动分配，
    /// 这辆车进入人工充电等待（原因「充电反复失败」）并告警，由人处理；不建第三张单。
    /// </summary>
    [Fact]
    public async Task ASecondEndedChargingOrderWithinTheWindowPutsTheVehicleOnManualHold()
    {
        await using FleetFixture fleet = await FleetAsync();
        fleet.Riot.BatteryByVehicle[KeyA] = 20;
        JourneyRuntimeRow first = await CommittedAndSentAsync(fleet);
        fleet.Riot.CancelOrder(first.PickupUpperId);
        await RoundAsync(fleet);
        await RoundAsync(fleet);
        await fleet.HearFromEveryVehicleAsync();
        await fleet.RunRoundAsync(fleet.Options.OwnOrderRebuildDelay);
        await RoundAsync(fleet);
        JourneyRuntimeRow second = (await ChargingJourneyAsync(fleet, AgvA))!;
        Assert.NotEqual(first.JourneyId, second.JourneyId);
        Assert.Equal(2, fleet.Riot.Creates.Count);

        fleet.Riot.CancelOrder(second.PickupUpperId);
        await RoundAsync(fleet);
        await RoundAsync(fleet);
        await fleet.HearFromEveryVehicleAsync();
        await fleet.RunRoundAsync(fleet.Options.OwnOrderRebuildDelay);
        await RoundAsync(fleet);

        ManualChargingHoldRow hold = await fleet.Context.Set<ManualChargingHoldRow>().AsNoTracking().SingleAsync(Token);
        Assert.Equal((KeyA, ManualChargingHoldReasons.ChargingRepeatedlyFailed), (hold.VehicleKey, hold.Reason));
        Assert.Single(fleet.ChargingLog.Entries, entry => entry.EventId.Id == 2242);
        Assert.Null(await ClaimOfAsync(fleet, KeyA));
        Assert.Equal(2, fleet.Riot.Creates.Count);
        Assert.Empty(await fleet.Context.OwnOrderRebuilds.AsNoTracking().ToArrayAsync(Token));
    }

    // ---- 途中 HANG、急停与故障 ------------------------------------------------------------------------------------------

    /// <summary>
    /// 途中急停或切手动：RIoT 把单挂起（<c>HANG</c>），车停在半路。与搬运旅程受同样的监看——旅程保持在途、写 <c>ORDER_HANG</c>；这不是「充不上」
    /// （<c>REQ-0175</c>）：不暂停桩、不暂停车的充电资格、不释放任何东西、不重建。RIoT 继续之后原因码清掉，照旧在途。
    /// </summary>
    [Fact]
    public async Task AnEmergencyStopOnTheWayIsNamedOrderHangAndPausesNoCharger()
    {
        await using FleetFixture fleet = await FleetAsync();
        fleet.Riot.BatteryByVehicle[KeyA] = 20;
        JourneyRuntimeRow journey = await CommittedAndSentAsync(fleet);
        (string CycleId, string JourneyId, string UpperId) before = await CommitmentOfAsync(fleet, KeyA);
        fleet.Riot.HangOrder(journey.PickupUpperId);
        fleet.Riot.VehicleOverrides[KeyA] = seen => seen with { CurrentStationId = 12, ProcState = "EMERGENCY" };

        await RoundAsync(fleet);
        fleet.Clock.Advance(TimeSpan.FromMinutes(30));
        await RoundAsync(fleet);

        Assert.Equal(JourneyRuntimeEngine.OrderHangReason, (await ChargingJourneyAsync(fleet, AgvA))!.BlockReasonCode);
        Assert.Equal(before, await CommitmentOfAsync(fleet, KeyA));
        Assert.Equal((KeyA, StationExclusivityStates.Reserved), await HolderAsync(fleet, Near.StationId));
        Assert.Empty(await fleet.Context.Set<ChargingStationAllocationHoldRow>().AsNoTracking().ToArrayAsync(Token));
        Assert.Empty(await fleet.Context.Set<VehicleChargingEligibilityHoldRow>().AsNoTracking().ToArrayAsync(Token));
        Assert.Empty(await fleet.Context.OwnOrderRebuilds.AsNoTracking().ToArrayAsync(Token));
        Assert.Single(fleet.Riot.Creates);

        fleet.Riot.PutOrder(fleet.Riot.OrderOf(journey.PickupUpperId)! with { OrderState = RiotOrderState.Executing });
        fleet.Riot.VehicleOverrides.Remove(KeyA);
        await RoundAsync(fleet);

        Assert.Null((await ChargingJourneyAsync(fleet, AgvA))!.BlockReasonCode);
        Assert.Equal(before, await CommitmentOfAsync(fleet, KeyA));
    }

    /// <summary>
    /// 途中单 FAILED：与搬运一样交故障模型（疑似故障、按住、原因码），承诺、预占与周期都不放、不重建；人工清除故障之后按已确认失败收尾——
    /// 周期结束、用途占有释放、撤下 <c>CHARGING</c>，不建第二张单。
    /// </summary>
    [Fact]
    public async Task AFailedChargingOrderIsSupervisedLikeATransportLegAndEndsOnlyWhenAPersonClearsTheFault()
    {
        await using FleetFixture fleet = await FleetAsync();
        fleet.Riot.BatteryByVehicle[KeyA] = 20;
        JourneyRuntimeRow journey = await CommittedAndSentAsync(fleet);
        (string CycleId, string JourneyId, string UpperId) before = await CommitmentOfAsync(fleet, KeyA);
        fleet.Riot.MovementState = "MT_FINISHED";
        fleet.Riot.FailOrder(journey.PickupUpperId);

        await RoundAsync(fleet);
        await RoundAsync(fleet);

        Assert.Equal(VehicleFaultEvidence.OrderFailed, (await ChargingJourneyAsync(fleet, AgvA))!.BlockReasonCode);
        Assert.True(await fleet.Context.VehicleFaultStates.AsNoTracking()
            .AnyAsync(row => row.AgvId == AgvA && row.Level != VehicleFaultLevel.None, Token));
        Assert.Equal(before, await CommitmentOfAsync(fleet, KeyA));
        Assert.Equal((KeyA, StationExclusivityStates.Reserved), await HolderAsync(fleet, Near.StationId));

        fleet.Context.ChangeTracker.Clear();
        VehicleFaultRecoveryDecision decision = await fleet.CreateFaultRecovery().RecoverAsync(
            new VehicleFaultRecoveryRequest(
                new EmergencyStopSubject(AgvA, KeyA), VehicleFaultRecoveryAction.ClearFault, "operator-1", true, null),
            Token);
        fleet.Context.ChangeTracker.Clear();

        Assert.Equal(
            (VehicleFaultRecoveryOutcome.Cleared, VehicleFaultRecoveryDispositions.ChargingEnded),
            (decision.Outcome, decision.Disposition));
        JourneyRuntimeRow ended = await fleet.Context.JourneyRuntimes.AsNoTracking()
            .SingleAsync(row => row.JourneyId == journey.JourneyId, Token);
        Assert.Equal((JourneyRuntimeStage.Completed, ChargingExecutionReasons.OrderFailed), (ended.Stage, ended.BlockReasonCode));
        ChargingCycleRow cycle = await fleet.Context.Set<ChargingCycleRow>().AsNoTracking().SingleAsync(Token);
        Assert.Equal((ChargingCyclePhases.Ended, ChargingExecutionReasons.OrderFailed), (cycle.Phase, cycle.EndReason));
        Assert.Null(await ClaimOfAsync(fleet, KeyA));
        Assert.Empty(await fleet.Context.OwnOrderRebuilds.AsNoTracking().ToArrayAsync(Token));
        Assert.Equal(
            JsonValueKind.Null,
            (await PayloadsAsync(fleet, AgvA, "VehicleBusinessStateSnapshot")).Last().GetProperty("activePurpose").ValueKind);
        Assert.Single(fleet.Riot.Creates);
    }

    // ---- 看板说明 --------------------------------------------------------------------------------------------------------

    /// <summary>充电旅程会写在旅程行上的每一个码，在看板的阻断卡片上都有中文说明（新码进看板说明，不留英文码给现场猜）。</summary>
    [Fact]
    public void EveryCodeAChargingJourneyWritesHasAChineseDescriptionOnTheBlockedJourneysCard()
    {
        string[] codes = [.. ChargingExecutionReasons.All, .. ChargingExecutionReasons.LegOutcomeCodes];

        Assert.NotEmpty(ChargingExecutionReasons.LegOutcomeCodes);
        Assert.All(codes, code =>
        {
            Assert.True(BlockedJourneysQueryEndpoint.Descriptions.TryGetValue(code, out string? description), code);
            Assert.Contains(description!, character => character >= 0x4e00 && character <= 0x9fff);
        });
    }

    // ---- 夹具 ------------------------------------------------------------------------------------------------------------

    /// <summary>第一轮分配承诺，第二轮过安全门、建单并确认：答那一趟充电的旅程行。</summary>
    private static async Task<JourneyRuntimeRow> CommittedAndSentAsync(FleetFixture fleet)
    {
        await RoundAsync(fleet);
        await RoundAsync(fleet);
        JourneyRuntimeRow journey = (await ChargingJourneyAsync(fleet, AgvA))!;
        Assert.NotNull(journey);
        Assert.Equal("CONFIRMED", (await fleet.Context.OrderIntents.AsNoTracking()
            .SingleAsync(row => row.UpperId == journey.PickupUpperId, Token)).Status);
        return journey;
    }

    private static async Task<JourneyRuntimeRow?> ChargingJourneyAsync(FleetFixture fleet, string agvId)
    {
        JourneyRuntimeRow[] rows = await fleet.Context.JourneyRuntimes.AsNoTracking()
            .Where(row => row.AgvId == agvId && row.JourneyId.StartsWith(ChargingIdentity.JourneyIdPrefix))
            .ToArrayAsync(Token);
        return rows.OrderByDescending(row => row.CreatedAt).FirstOrDefault();
    }
}
