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

    /// <summary>
    /// 这趟充电的桩预占行不在了、而单还没发出过：承诺立刻作废（独立审查 M2(a)）——不建那张单，周期结束、用途占有释放、旅程收尾，不再停在
    /// 「请联系开发」上占着用途。车下一轮按正常链重新评估，是一个新的承诺、新的单号。
    /// </summary>
    [Fact]
    public async Task WithoutItsReservationACommitmentNeverSentIsWithdrawnAndTheVehicleIsJudgedAfresh()
    {
        await using FleetFixture fleet = await FleetAsync();
        fleet.Riot.BatteryByVehicle[KeyA] = 20;
        await RoundAsync(fleet);
        (string CycleId, string JourneyId, string UpperId) before = await CommitmentOfAsync(fleet, KeyA);
        await fleet.Context.Set<StationExclusivityRow>().ExecuteDeleteAsync(Token);

        await RoundAsync(fleet);

        Assert.DoesNotContain(fleet.Riot.Creates, create => create.UpperId == before.UpperId);
        JourneyRuntimeRow withdrawn = await fleet.Context.JourneyRuntimes.AsNoTracking()
            .SingleAsync(row => row.JourneyId == before.JourneyId, Token);
        Assert.Equal(
            (JourneyRuntimeStage.Completed, ChargingExecutionReasons.WithdrawnReservationLost),
            (withdrawn.Stage, withdrawn.BlockReasonCode));
        ChargingCycleRow cycle = await fleet.Context.Set<ChargingCycleRow>().AsNoTracking()
            .SingleAsync(row => row.CycleId == before.CycleId, Token);
        Assert.Equal((ChargingCyclePhases.Ended, ChargingExecutionReasons.WithdrawnReservationLost), (cycle.Phase, cycle.EndReason));

        await RoundAsync(fleet);
        await RoundAsync(fleet);

        (string CycleId, string JourneyId, string UpperId) again = await CommitmentOfAsync(fleet, KeyA);
        Assert.NotEqual(before.UpperId, again.UpperId);
        Assert.Equal([again.UpperId], fleet.Riot.Creates.Select(create => create.UpperId));
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
    /// 车载端断线时承诺、确认都发生了：四张快照留在发件箱里，一张也没送到。重连之后，现行的那一对（<c>EN_ROUTE</c>：<c>CHARGER</c> 腿与
    /// <c>CHARGING</c> 状态）按原 <c>messageId</c> 补发；被它取代、从没被确认的 <c>ALLOCATED</c> 那一对已经退役，不再发——补发按先后发，
    /// 车载端把低于已采纳修订号的快照当成回退、当场拆会话。发件箱里每样仍是那几行，不产生第二份。
    /// </summary>
    [Fact]
    public async Task AfterAReconnectTheChargerPlanAndChargingStateAreResentUnderTheirOwnIdsWithoutASecondCopy()
    {
        await using FleetFixture fleet = await FleetAsync();
        fleet.Riot.BatteryByVehicle[KeyA] = 20;
        fleet.Peer.Unavailable = AgvA;
        JourneyRuntimeRow journey = await CommittedAndSentAsync(fleet);
        string[] superseded =
        [
            ChargingJourneyShape.AllocatedPlanMessageId(journey.JourneyId),
            ChargingJourneyShape.AllocatedStateMessageId(journey.JourneyId),
        ];
        string[] current =
        [
            ChargingJourneyShape.EnRoutePlanMessageId(journey.JourneyId),
            ChargingJourneyShape.EnRouteStateMessageId(journey.JourneyId),
        ];
        Assert.DoesNotContain(fleet.Peer.Delivered, line => superseded.Contains(line.MessageId) || current.Contains(line.MessageId));

        fleet.Peer.Unavailable = null;
        await fleet.HearFromEveryVehicleAsync();
        await RoundAsync(fleet);

        Assert.All(current, id => Assert.Contains(fleet.Peer.Delivered, line => line.MessageId == id));
        Assert.DoesNotContain(fleet.Peer.Delivered, line => superseded.Contains(line.MessageId));
        Assert.All(
            await fleet.Context.ProtocolOutbox.AsNoTracking().Where(row => superseded.Contains(row.MessageId)).ToArrayAsync(Token),
            row => Assert.NotNull(row.FencedAt));
        Assert.Equal(2, (await PayloadsAsync(fleet, AgvA, "UpcomingStopPlanSnapshot")).Length);
        Assert.Equal(2, (await PayloadsAsync(fleet, AgvA, "VehicleBusinessStateSnapshot")).Length);
    }

    // ---- 充电单在 RIoT 里被取消（票面第 10 条）---------------------------------------------------------------------------

    /// <summary>
    /// 第一行（<c>ALLOCATED</c>／<c>EN_ROUTE</c>，车还没到桩）：不重建（与空闲返回单同一条路，<c>REQ-0360</c> 的重建承载需求，充电单没有需求）。
    /// 车还可能在动时，预占、周期与用途占有都保持——不许「取消即释放预占」；车证明停稳、没有活动订单之后才按已确认失败收尾：周期结束、用途释放、
    /// 撤下 <c>CHARGING</c>。桩预占这时仍在，下一轮按三项确认（不在充电、车不在桩上、桩可确认空闲）才释放。冷却期内这辆车不被承诺任何充电桩；
    /// 冷却过后它按正常候选链重新排队，是一个新的周期、新的单号。
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

        // Within the cooldown the vehicle is committed to no charger at all; nothing is rebuilt and nothing new is created.
        await RoundAsync(fleet);
        Assert.Null(await ClaimOfAsync(fleet, KeyA));
        Assert.Equal(ChargingAllocationReasons.CooldownAfterFailedCycle, fleet.ChargingBoard.Verdicts[AgvA].Reason);
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
        // Independent review S1: the closing snapshot is sent, not only left in the outbox -- otherwise the vehicle goes on
        // showing "going to charge" with its entry closed until something else happens to be sent to it.
        string closing = Assert.Single(await ClosingStateMessageIdsAsync(fleet, AgvA));
        Assert.Contains(fleet.Peer.Delivered, line => line.MessageId == closing);
    }

    // ---- 出发前复核（独立审查 M1）------------------------------------------------------------------------------------

    public static TheoryData<string, string> CommitmentsThatNoLongerStand => new()
    {
        { "roster-emptied", ChargingExecutionReasons.WithdrawnChargerNoLongerEligible },
        { "another-vehicle-charging-on-it", ChargingExecutionReasons.WithdrawnChargerNoLongerEligible },
        { "charger-allocation-held", ChargingExecutionReasons.WithdrawnChargerNoLongerEligible },
        { "battery-back-above-the-line", ChargingExecutionReasons.WithdrawnNoLongerRequired },
    };

    /// <summary>
    /// 承诺了 211、被没锁好的仓门挡在出发前安全门上的车，等的这段时间里承诺不再成立——名册被置空（关窗）、另一台车停在 211 上充着电、211 被置
    /// 分配暂停、这辆车的电量回到了线上——然后门关好。修复前两轮之内就建出了去 211 的充电单（审查探针 P7）。现在建单之前对这一个桩重跑桩侧判定
    /// 与「还需不需要充电」：不成立就在同一次保存里作废承诺（周期结束、用途与预占释放、旅程收尾），去 211 的单一张也不建，车被告知。
    /// 「单已经发出之后名册被置空 → 继续」的对照是 <c>ChargingAllocationTests.WhenTheRosterIsEmptiedBetweenRoundsCommittedCyclesGoOnAndQueuedVehiclesAreHeld</c>。
    /// </summary>
    [Theory]
    [MemberData(nameof(CommitmentsThatNoLongerStand))]
    public async Task ACommitmentHeldAtTheDepartureGateIsWithdrawnOnceItNoLongerStandsAndNoOrderGoesToThatCharger(
        string what, string expected)
    {
        await using FleetFixture fleet = await FleetAsync(vehicles: 2);
        fleet.Riot.BatteryByVehicle[KeyA] = 20;
        await RoundAsync(fleet);
        (string CycleId, string JourneyId, string UpperId) before = await CommitmentOfAsync(fleet, KeyA);
        await fleet.ReplaceSafetySnapshotAsync(AgvA, unlockedSlot: 3);
        await RoundAsync(fleet);
        Assert.Equal(ChargingExecutionReasons.DepartureNotProven, (await ChargingJourneyAsync(fleet, AgvA))!.BlockReasonCode);
        Assert.Empty(fleet.Riot.Creates);

        switch (what)
        {
            case "roster-emptied":
                await fleet.WriteChargerRosterAsync();
                break;
            case "another-vehicle-charging-on-it":
                fleet.Riot.VehicleOverrides[FleetFixture.VehicleKeys[1]] =
                    seen => seen with { CurrentStationId = Near.StationId, BatteryState = "CHARGING" };
                break;
            case "charger-allocation-held":
                await new ChargingHoldStore(fleet.Context).RecordStationHoldAsync(
                    new ChargingStationAllocationHold(
                        "hold-1", "hold-key-1", ChargingStationHoldTriggers.Maintenance, Map, Near.StationId, 1, null, null, null,
                        null, null, null, null, null, null, null, fleet.Clock.GetUtcNowWithoutTick(), null, null, null, null, null,
                        null, null, null, null, null, null),
                    Token);
                break;
            case "battery-back-above-the-line":
                fleet.Riot.BatteryByVehicle[KeyA] = 95;
                break;
        }
        fleet.Context.ChangeTracker.Clear();

        // The door is locked again. Before the fix the order to 211 was created within two rounds of this.
        await fleet.ReplaceSafetySnapshotAsync(AgvA);
        await RoundAsync(fleet);
        await RoundAsync(fleet);

        Assert.DoesNotContain(
            fleet.Riot.Creates, create => create.VehicleKey == KeyA && create.DestinationStationId == Near.StationId);
        JourneyRuntimeRow withdrawn = await fleet.Context.JourneyRuntimes.AsNoTracking()
            .SingleAsync(row => row.JourneyId == before.JourneyId, Token);
        Assert.Equal((JourneyRuntimeStage.Completed, expected), (withdrawn.Stage, withdrawn.BlockReasonCode));
        ChargingCycleRow cycle = await fleet.Context.Set<ChargingCycleRow>().AsNoTracking()
            .SingleAsync(row => row.CycleId == before.CycleId, Token);
        Assert.Equal((ChargingCyclePhases.Ended, expected), (cycle.Phase, cycle.EndReason));
        // The purpose and the reservation went in that same save, each passage closed under the same reason.
        Assert.Equal(
            expected,
            (await fleet.Context.Set<VehiclePurposeClaimRecordRow>().AsNoTracking()
                .SingleAsync(row => row.JourneyId == before.JourneyId, Token)).ReleaseReason);
        StationExclusivityRecordRow passage = await fleet.Context.Set<StationExclusivityRecordRow>().AsNoTracking()
            .SingleAsync(row => row.JourneyId == before.JourneyId, Token);
        Assert.Equal(expected, passage.ReleaseReason);
        Assert.NotNull(passage.ReleasedAt);
        Assert.NotEqual(before.JourneyId, (await StationAsync(fleet, Near.StationId))?.JourneyId);
        // The vehicle is told: the snapshot taking CHARGING down was staged and reached it.
        string closing = Assert.Single(await ClosingStateMessageIdsAsync(fleet, AgvA));
        Assert.Contains(fleet.Peer.Delivered, line => line.MessageId == closing);
        // A withdrawal is not a failure: nothing counts towards the second strike and there is no cooldown.
        Assert.NotEqual(ChargingAllocationReasons.CooldownAfterFailedCycle, fleet.ChargingBoard.Verdicts[AgvA].Reason);
        Assert.DoesNotContain(
            await fleet.Context.Set<ManualChargingHoldRow>().AsNoTracking().ToArrayAsync(Token),
            hold => hold.Reason == ManualChargingHoldReasons.ChargingRepeatedlyFailed);
        if (what == "roster-emptied")
        {
            // Outside the charging window: no automatic charging, and the vehicle that still needs it waits for a person.
            Assert.Equal(
                (KeyA, ManualChargingHoldReasons.RosterEmpty),
                await fleet.Context.Set<ManualChargingHoldRow>().AsNoTracking()
                    .Select(row => new ValueTuple<string, string>(row.VehicleKey, row.Reason)).SingleAsync(Token));
            Assert.Empty(fleet.Riot.Creates);
        }
    }

    // ---- 承诺的出口（独立审查 M2）------------------------------------------------------------------------------------

    /// <summary>
    /// 出不了门的车不一直占着桩（审查探针 P1b）：安全门持续不过，超过 <c>JourneyRuntime:OwnOrderRebuildDelay</c> 承诺作废，桩让给队里的下一台车。
    /// 宽限之内什么都不动。门还没好的时候这辆车不会被重新承诺（共用的车辆侧判定挡着），所以不会每 30 秒来回一次。
    /// </summary>
    [Theory]
    [InlineData("door-unlocked")]
    [InlineData("session-not-ready")]
    public async Task ACommitmentThatCannotLeaveIsWithdrawnAfterTheGraceAndItsChargerGoesToTheNextVehicle(string gap)
    {
        await using FleetFixture fleet = await FleetAsync(vehicles: 2);
        string keyB = FleetFixture.VehicleKeys[1];
        fleet.Riot.BatteryByVehicle[KeyA] = 20;
        fleet.Riot.BatteryByVehicle[keyB] = 30;
        await RoundAsync(fleet);
        (string CycleId, string JourneyId, string UpperId) before = await CommitmentOfAsync(fleet, KeyA);
        if (gap == "door-unlocked")
        {
            await fleet.ReplaceSafetySnapshotAsync(AgvA, unlockedSlot: 3);
        }
        else
        {
            await fleet.DropSessionAsync(AgvA);
        }

        await RoundAsync(fleet);
        await RoundAsync(fleet);

        // Within the grace: held exactly as it was.
        Assert.Equal(ChargingExecutionReasons.DepartureNotProven, (await ChargingJourneyAsync(fleet, AgvA))!.BlockReasonCode);
        Assert.Equal(before, await CommitmentOfAsync(fleet, KeyA));
        Assert.Equal((KeyA, StationExclusivityStates.Reserved), await HolderAsync(fleet, Near.StationId));

        await fleet.HearFromEveryVehicleAsync();
        await fleet.RunRoundAsync(fleet.Options.OwnOrderRebuildDelay);
        await RoundAsync(fleet);

        JourneyRuntimeRow withdrawn = await fleet.Context.JourneyRuntimes.AsNoTracking()
            .SingleAsync(row => row.JourneyId == before.JourneyId, Token);
        Assert.Equal(
            (JourneyRuntimeStage.Completed, ChargingExecutionReasons.WithdrawnDepartureNotProven),
            (withdrawn.Stage, withdrawn.BlockReasonCode));
        Assert.Null(await ClaimOfAsync(fleet, KeyA));
        Assert.Equal((keyB, StationExclusivityStates.Reserved), await HolderAsync(fleet, Near.StationId));
        Assert.DoesNotContain(fleet.Riot.Creates, create => create.VehicleKey == KeyA);
        Assert.Single(fleet.EngineLog.Entries, entry => entry.EventId.Id == 2254);

        // Still shut: not committed again, round after round -- the allocation refuses it on the same reads as the gate (review
        // S-a), not because B holds the charger now. The single-vehicle form of this is
        // AVehicleTheDepartureGateWouldHoldBackIsNeverCommittedSoItDoesNotLoop.
        for (int round = 0; round < 3; round++)
        {
            await RoundAsync(fleet);
        }
        Assert.Null(await ClaimOfAsync(fleet, KeyA));
        Assert.Single(await fleet.Context.JourneyRuntimes.AsNoTracking().Where(row => row.VehicleKey == KeyA).ToArrayAsync(Token));
    }

    public static TheoryData<string> GapsOnlyTheDepartureGateSaw => ["slot-unlocked", "blocking-fact", "riot-not-stopped"];

    /// <summary>
    /// 出发前安全门比分配侧多判的三样（增量审查 S-a）：会话最新一张安全快照里某个仓位没锁、安全摘要带阻断原因、服务端自己读 RIoT 读不到停稳。
    /// 分配侧原来不判它们，单车时审查探针 PB 跑 400 秒：承诺 9 次、撤回 8 次，给车发了 32 条快照。现在分配经同一组读（<c>NonBusinessDepartureGate</c>）
    /// 先判：一次也不承诺、不撤回、不给车发充电快照，分配结论是 <see cref="ChargingAllocationReasons.DepartureNotProven"/>、细节里是缺的那一项，
    /// 等桩告警（事件 2246）只有一次。门一好，下一轮承诺、再下一轮建单，只建一张。
    /// </summary>
    [Theory]
    [MemberData(nameof(GapsOnlyTheDepartureGateSaw))]
    public async Task AVehicleTheDepartureGateWouldHoldBackIsNeverCommittedSoItDoesNotLoop(string gap)
    {
        await using FleetFixture fleet = await FleetAsync();
        fleet.Riot.BatteryByVehicle[KeyA] = 20;
        string missing;
        switch (gap)
        {
            case "slot-unlocked":
                await fleet.ReplaceSafetySnapshotAsync(AgvA, unlockedSlot: 3);
                missing = "SLOT_3_NOT_LOCKED";
                break;
            case "blocking-fact":
                await fleet.ReplaceSafetySnapshotAsync(AgvA, reasonCodes: ["SLOT_DOOR_SENSOR_FAULT"]);
                missing = "SLOT_DOOR_SENSOR_FAULT";
                break;
            default:
                fleet.Riot.SafetyReasons = ["RIOT_EMERGENCY_NOT_OK"];
                missing = "RIOT_EMERGENCY_NOT_OK";
                break;
        }

        // 400 s, as the probe ran: more than ten times the withdrawal limit.
        for (int round = 0; round < 10; round++)
        {
            await fleet.HearFromEveryVehicleAsync();
            await fleet.RunRoundAsync(TimeSpan.FromSeconds(40));
        }

        Assert.Empty(await fleet.Context.Set<ChargingCycleRow>().AsNoTracking().ToArrayAsync(Token));
        Assert.Empty(await fleet.Context.JourneyRuntimes.AsNoTracking().ToArrayAsync(Token));
        Assert.Null(await ClaimOfAsync(fleet, KeyA));
        Assert.Null(await StationAsync(fleet, Near.StationId));
        Assert.DoesNotContain(
            await PayloadsAsync(fleet, AgvA, "VehicleBusinessStateSnapshot"),
            state => state.GetProperty("activePurpose").ValueKind == JsonValueKind.String &&
                     state.GetProperty("activePurpose").GetString() == VehicleActivePurposes.Charging);
        Assert.DoesNotContain(fleet.EngineLog.Entries, entry => entry.EventId.Id == 2254);
        Assert.Empty(fleet.Riot.Creates);
        (string reason, string detail) = fleet.ChargingBoard.Verdicts[AgvA];
        Assert.Equal(ChargingAllocationReasons.DepartureNotProven, reason);
        Assert.Contains(missing, detail, StringComparison.Ordinal);
        Assert.Single(fleet.ChargingLog.Entries, entry => entry.EventId.Id == 2246);

        if (gap == "riot-not-stopped")
        {
            fleet.Riot.SafetyReasons = [];
        }
        else
        {
            await fleet.ReplaceSafetySnapshotAsync(AgvA);
        }
        await RoundAsync(fleet);
        await RoundAsync(fleet);

        Assert.Single(await fleet.Context.Set<ChargingCycleRow>().AsNoTracking().ToArrayAsync(Token));
        Assert.Single(fleet.Riot.Creates);
        Assert.Null((await ChargingJourneyAsync(fleet, AgvA))!.BlockReasonCode);
    }

    /// <summary>
    /// 撤回不是失败：门好了之后这辆车下一轮就重新排队并取得桩，不进冷却、不计入「两次即停」。
    /// </summary>
    [Fact]
    public async Task AWithdrawnCommitmentIsNotAFailureTheVehicleIsCommittedAgainAsSoonAsItMayLeave()
    {
        await using FleetFixture fleet = await FleetAsync();
        fleet.Riot.BatteryByVehicle[KeyA] = 20;
        await RoundAsync(fleet);
        (string CycleId, string JourneyId, string UpperId) before = await CommitmentOfAsync(fleet, KeyA);
        await fleet.ReplaceSafetySnapshotAsync(AgvA, unlockedSlot: 3);
        await RoundAsync(fleet);
        await fleet.HearFromEveryVehicleAsync();
        await fleet.RunRoundAsync(fleet.Options.OwnOrderRebuildDelay);
        Assert.Null(await ClaimOfAsync(fleet, KeyA));
        Assert.Null(await StationAsync(fleet, Near.StationId));

        await fleet.ReplaceSafetySnapshotAsync(AgvA);
        await RoundAsync(fleet);
        await RoundAsync(fleet);

        (string CycleId, string JourneyId, string UpperId) again = await CommitmentOfAsync(fleet, KeyA);
        Assert.NotEqual(before.CycleId, again.CycleId);
        Assert.Equal([again.UpperId], fleet.Riot.Creates.Select(create => create.UpperId));
        Assert.Empty(await fleet.Context.Set<ManualChargingHoldRow>().AsNoTracking().ToArrayAsync(Token));
    }

    /// <summary>
    /// 建单应答丢了、RIoT 上根本没有这张单（审查探针 P1：修复前 12 小时后承诺原样，211 一直被预占，什么人工动作都解不开）。现在有受治理的出口：
    /// RIoT 对这个单号<b>连续</b>答「查无此单」满 <c>JourneyRuntime:ChargingOrderAbsentAbandonAfter</c>（默认 120 秒；中间读到别的就重新计时），
    /// 车证明停稳、名下没有未完成的单，才放弃这张单、按已确认失败收尾——告警、计入「两次即停」、走与取消相同的冷却；桩预占照旧按三项确认释放。
    /// 任何一项不成立都保持原样。
    /// </summary>
    [Fact]
    public async Task ACreateWhoseAnswerWasLostIsGivenUpOnlyAfterRiotSaidNoSuchOrderLongEnoughWithTheVehicleProvenIdle()
    {
        await using FleetFixture fleet = await FleetAsync();
        Assert.Equal(TimeSpan.FromSeconds(120), fleet.Options.ChargingOrderAbsentAbandonAfter);
        (string CycleId, string JourneyId, string UpperId) before = await ResultUnknownAndAbsentAsync(fleet);

        // 100 s of "no such order": kept.
        await fleet.HearFromEveryVehicleAsync();
        await fleet.RunRoundAsync(TimeSpan.FromSeconds(100));
        Assert.Equal(before, await CommitmentOfAsync(fleet, KeyA));

        // One reading that is not "no such order" -- RIoT answered something it could not classify -- and the count starts again.
        fleet.Riot.PutOrder(new RiotOrderObservation(before.UpperId, RiotOrderObservationKind.Unknown, null));
        await RoundAsync(fleet);
        fleet.Riot.PutOrder(new RiotOrderObservation(before.UpperId, RiotOrderObservationKind.NotFound, null));
        await RoundAsync(fleet);
        await fleet.HearFromEveryVehicleAsync();
        await fleet.RunRoundAsync(TimeSpan.FromSeconds(100));
        Assert.Equal(before, await CommitmentOfAsync(fleet, KeyA));

        // Long enough now, but the vehicle is not proven stopped: kept.
        fleet.Riot.VehicleOverrides[KeyA] = seen => seen with { Speed = 0.3, ProcState = "RUNNING" };
        await fleet.HearFromEveryVehicleAsync();
        await fleet.RunRoundAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(before, await CommitmentOfAsync(fleet, KeyA));
        // Stopped, but RIoT lists an unfinished order for it under another number: kept.
        fleet.Riot.VehicleOverrides.Remove(KeyA);
        fleet.Riot.ForeignOrders.Add((new RiotListedOrder("F-9", "SOMEONE-ELSES", RiotOrderState.Queueing, KeyA, null), 12));
        await RoundAsync(fleet);
        Assert.Equal(before, await CommitmentOfAsync(fleet, KeyA));
        Assert.DoesNotContain(fleet.EngineLog.Entries, entry => entry.EventId.Id == 2255);

        fleet.Riot.ForeignOrders.Clear();
        await RoundAsync(fleet);

        JourneyRuntimeRow ended = await fleet.Context.JourneyRuntimes.AsNoTracking()
            .SingleAsync(row => row.JourneyId == before.JourneyId, Token);
        Assert.Equal(
            (JourneyRuntimeStage.Completed, ChargingExecutionReasons.OrderNeverAppeared), (ended.Stage, ended.BlockReasonCode));
        ChargingCycleRow cycle = await fleet.Context.Set<ChargingCycleRow>().AsNoTracking()
            .SingleAsync(row => row.CycleId == before.CycleId, Token);
        Assert.Equal((ChargingCyclePhases.Ended, ChargingExecutionReasons.OrderNeverAppeared), (cycle.Phase, cycle.EndReason));
        Assert.Null(await ClaimOfAsync(fleet, KeyA));
        var alarm = Assert.Single(fleet.EngineLog.Entries, entry => entry.EventId.Id == 2255);
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Warning, alarm.Level);
        Assert.Contains(before.UpperId, alarm.Message, StringComparison.Ordinal);
        Assert.Single(fleet.Riot.Creates);
        Assert.Empty(await fleet.Context.OwnOrderRebuilds.AsNoTracking().ToArrayAsync(Token));

        // A confirmed failure like a cancellation: the reservation goes on the three confirmations, then the cooldown.
        await RoundAsync(fleet);
        Assert.Null(await StationAsync(fleet, Near.StationId));
        await RoundAsync(fleet);
        Assert.Equal(ChargingAllocationReasons.CooldownAfterFailedCycle, fleet.ChargingBoard.Verdicts[AgvA].Reason);
        await fleet.HearFromEveryVehicleAsync();
        await fleet.RunRoundAsync(fleet.Options.OwnOrderRebuildDelay);
        Assert.NotEqual(before.UpperId, (await CommitmentOfAsync(fleet, KeyA)).UpperId);
    }

    /// <summary>
    /// 被放弃的那张单事后才在 RIoT 冒出来、跑到这辆车上：那趟旅程已经收尾，没有任何东西在看着它。外来单监督器把它当成要取消的单——记下、
    /// 这辆车不接新活、取消只发一次、告警——哪怕本部署没有被授权取消<b>别人</b>的单（这张是自己的）。还在进行的充电单不受影响：它是本服务端
    /// 自己的在途单，监督器不碰。
    /// </summary>
    [Fact]
    public async Task AnAbandonedChargeOrderThatTurnsUpRunningOnTheVehicleIsCancelledOnceAndAlarmed()
    {
        await using FleetFixture fleet = await FleetAsync();
        EventRecordingLogger<ControlServer.Host.Runtime.ForeignOrders.ForeignRunningOrderSupervisor> log = new();
        ControlServer.Host.Runtime.ForeignOrders.ForeignRunningOrderSupervisor Supervisor() => SupervisorOf(fleet, log);

        (string CycleId, string JourneyId, string UpperId) before = await ResultUnknownAndAbsentAsync(fleet);
        // While the commitment stands, the same order running would be this server's own: left alone.
        fleet.Riot.PutOrder(new RiotOrderObservation(
            before.UpperId, RiotOrderObservationKind.Active, "ORDER-LATE", RiotOrderState.Executing, KeyA, Map, Near.StationId));
        await Supervisor().SuperviseAsync(Token);
        Assert.Empty(await fleet.Context.ForeignRiotOrders.AsNoTracking().ToArrayAsync(Token));
        Assert.Empty(fleet.Riot.OrderCommands);

        // Back to "no such order", long enough: given up.
        fleet.Riot.PutOrder(new RiotOrderObservation(before.UpperId, RiotOrderObservationKind.NotFound, null));
        fleet.Context.ChangeTracker.Clear();
        await RoundAsync(fleet);
        await fleet.HearFromEveryVehicleAsync();
        await fleet.RunRoundAsync(fleet.Options.ChargingOrderAbsentAbandonAfter);
        Assert.Equal(
            ChargingExecutionReasons.OrderNeverAppeared,
            (await fleet.Context.Set<ChargingCycleRow>().AsNoTracking().SingleAsync(row => row.CycleId == before.CycleId, Token)).EndReason);

        // And now RIoT shows it running on the vehicle after all.
        fleet.Riot.PutOrder(new RiotOrderObservation(
            before.UpperId, RiotOrderObservationKind.Active, "ORDER-LATE", RiotOrderState.Executing, KeyA, Map, Near.StationId));
        fleet.Context.ChangeTracker.Clear();
        await Supervisor().SuperviseAsync(Token);
        fleet.Context.ChangeTracker.Clear();
        await Supervisor().SuperviseAsync(Token);
        fleet.Context.ChangeTracker.Clear();

        ForeignRiotOrderRow row = await fleet.Context.ForeignRiotOrders.AsNoTracking().SingleAsync(Token);
        Assert.Equal(
            ("ORDER-LATE", before.UpperId, AgvA, AbandonedChargeOrders.OwnershipBasis),
            (row.RiotOrderId, row.UpperId, row.AgvId, row.OwnershipBasis));
        Assert.Equal([(RiotCommandTypeNames.CancelOrder, "ORDER-LATE")], fleet.Riot.OrderCommands);
        Assert.Contains(
            AgvA, await ControlServer.Host.Runtime.ForeignOrders.ForeignRunningOrders.HeldAgvIdsAsync(fleet.Context, Token));
        // Its own event, not 2180's "not created by this server" (review S-b): there is nobody outside to look for.
        Assert.Contains(log.Entries, entry => entry.EventId.Id == 2188 && entry.Message.Contains("ORDER-LATE", StringComparison.Ordinal));
        Assert.DoesNotContain(log.Entries, entry => entry.EventId.Id == 2180);
    }

    /// <summary>
    /// 被放弃的那张单事后冒出来时还在 RIoT 里<b>排队</b>（增量审查 S-b）：监督器原来只看在我们车上运行的单，要等 RIoT 先把它派给车、车开起来，
    /// 最长跑一轮才取消。现在放弃之后 <c>JourneyRuntime:OwnOrderRebuildRepeatWindow</c> 之内按 <c>upperId</c> 追读，排队中的也当场取消——取消前重读、
    /// 只发一次；挡的是为它建单的那辆车。看板上的说明是「本服务端自己建了又放弃的充电单」，不让现场去找外部建单人。过了窗口的不追（在跑的仍由
    /// 常规认定兜住）。
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnAbandonedChargeOrderThatTurnsUpStillQueueingIsCancelledWithinTheWindow(bool pastTheWindow)
    {
        await using FleetFixture fleet = await FleetAsync();
        fleet.Riot.AnswersAbsentAsRealRiot = true;
        EventRecordingLogger<ControlServer.Host.Runtime.ForeignOrders.ForeignRunningOrderSupervisor> log = new();
        (string CycleId, string JourneyId, string UpperId) before = await ResultUnknownAndAbsentAsync(fleet);
        await fleet.HearFromEveryVehicleAsync();
        await fleet.RunRoundAsync(fleet.Options.ChargingOrderAbsentAbandonAfter);
        Assert.Equal(
            ChargingExecutionReasons.OrderNeverAppeared,
            (await fleet.Context.Set<ChargingCycleRow>().AsNoTracking().SingleAsync(row => row.CycleId == before.CycleId, Token)).EndReason);
        if (pastTheWindow)
        {
            fleet.Clock.Advance(fleet.Options.OwnOrderRebuildRepeatWindow + TimeSpan.FromSeconds(1));
        }

        // RIoT now lists it, queueing, with no vehicle running it yet.
        fleet.Riot.PutOrder(new RiotOrderObservation(
            before.UpperId, RiotOrderObservationKind.Active, "ORDER-LATE", RiotOrderState.Queueing, null, Map, Near.StationId));
        fleet.Context.ChangeTracker.Clear();
        await SupervisorOf(fleet, log).SuperviseAsync(Token);
        fleet.Context.ChangeTracker.Clear();
        await SupervisorOf(fleet, log).SuperviseAsync(Token);
        fleet.Context.ChangeTracker.Clear();

        if (pastTheWindow)
        {
            Assert.Empty(await fleet.Context.ForeignRiotOrders.AsNoTracking().ToArrayAsync(Token));
            Assert.Empty(fleet.Riot.OrderCommands);
            return;
        }

        ForeignRiotOrderRow row = await fleet.Context.ForeignRiotOrders.AsNoTracking().SingleAsync(Token);
        Assert.Equal(
            ("ORDER-LATE", before.UpperId, AgvA, AbandonedChargeOrders.OwnershipBasis, (int?)RiotOrderState.Queueing),
            (row.RiotOrderId, row.UpperId, row.AgvId, row.OwnershipBasis, row.OrderStateAtDetection));
        Assert.Equal([(RiotCommandTypeNames.CancelOrder, "ORDER-LATE")], fleet.Riot.OrderCommands);
        Assert.Contains(
            AgvA, await ControlServer.Host.Runtime.ForeignOrders.ForeignRunningOrders.HeldAgvIdsAsync(fleet.Context, Token));
        Assert.Single(log.Entries, entry => entry.EventId.Id == 2188);
        Assert.DoesNotContain(log.Entries, entry => entry.EventId.Id == 2180);

        // Still listed after the one cancel had time to take: handed to a person, never cancelled again, and the card says
        // whose order it is.
        fleet.Clock.Advance(ControlServer.Host.Runtime.ForeignOrders.ForeignRunningOrderSupervisor.CancelSettleTime);
        await SupervisorOf(fleet, log).SuperviseAsync(Token);
        fleet.Context.ChangeTracker.Clear();
        Assert.Single(fleet.Riot.OrderCommands);
        Assert.Equal(
            ForeignRiotOrderStates.StillRunningAfterCancel,
            (await fleet.Context.ForeignRiotOrders.AsNoTracking().SingleAsync(Token)).State);
        object fact = await new ForeignRunningOrdersQueryEndpoint().ReadAsync(fleet.Context, Token);
        using JsonDocument card = JsonDocument.Parse(JsonSerializer.Serialize(fact));
        string description = Assert.Single(card.RootElement.EnumerateArray()).GetProperty("reasonDescription").GetString()!;
        Assert.Equal(
            ForeignRunningOrdersQueryEndpoint.AbandonedChargeOrderDescriptions[
                ControlServer.Host.Runtime.ForeignOrders.ForeignRunningOrders.StillRunningAfterCancelReason],
            description);
        Assert.DoesNotContain("不是本服务端建的", description, StringComparison.Ordinal);
    }

    /// <summary>
    /// 被放弃的那张单事后出现在<b>不是我们的车</b>上执行（第二轮审查 S-1，探针 P4）：绝不对别的车发订单命令，也不挡我们的车——不取消、
    /// 不认下为「在处理中」，只记一行不挡车的记录、打一条 Warning（事件 2189，写明单号与执行车）让人去看；之后每一轮不再重复告警。
    /// </summary>
    [Fact]
    public async Task AnAbandonedChargeOrderExecutingOnAVehicleNotOursIsNeverCancelledAndHoldsNothing()
    {
        await using FleetFixture fleet = await FleetAsync();
        fleet.Riot.AnswersAbsentAsRealRiot = true;
        EventRecordingLogger<ControlServer.Host.Runtime.ForeignOrders.ForeignRunningOrderSupervisor> log = new();
        (string CycleId, string JourneyId, string UpperId) before = await ResultUnknownAndAbsentAsync(fleet);
        await fleet.HearFromEveryVehicleAsync();
        await fleet.RunRoundAsync(fleet.Options.ChargingOrderAbsentAbandonAfter);
        Assert.Equal(
            ChargingExecutionReasons.OrderNeverAppeared,
            (await fleet.Context.Set<ChargingCycleRow>().AsNoTracking().SingleAsync(row => row.CycleId == before.CycleId, Token)).EndReason);

        fleet.Riot.PutOrder(new RiotOrderObservation(
            before.UpperId, RiotOrderObservationKind.Active, "ORDER-LATE", RiotOrderState.Executing, "NOT-OURS-KEY-9", Map,
            Near.StationId));
        for (int round = 0; round < 3; round++)
        {
            fleet.Context.ChangeTracker.Clear();
            await SupervisorOf(fleet, log).SuperviseAsync(Token);
        }
        fleet.Context.ChangeTracker.Clear();

        Assert.Empty(fleet.Riot.OrderCommands);
        Assert.Empty(await ControlServer.Host.Runtime.ForeignOrders.ForeignRunningOrders.HeldAgvIdsAsync(fleet.Context, Token));
        Assert.Equal(
            ForeignRiotOrderStates.LeftVehicle,
            (await fleet.Context.ForeignRiotOrders.AsNoTracking().SingleAsync(Token)).State);
        var warning = Assert.Single(log.Entries, entry => entry.EventId.Id == 2189);
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Warning, warning.Level);
        Assert.Contains("ORDER-LATE", warning.Message, StringComparison.Ordinal);
        Assert.Contains("NOT-OURS-KEY-9", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(log.Entries, entry => entry.EventId.Id is 2180 or 2188);
    }

    /// <summary>
    /// 追读窗口过了之后才冒出来、还在排队、已指定给本车的被放弃单（第二轮审查 L-1）：不认它的话，本车会因为名下有未完成的单一直接不到新活，
    /// 直到 RIoT 把它派到车上跑起来才被认出——车会先动一小段。指定给我们车的那一种不限时，按被放弃的单认下并取消一次。
    /// </summary>
    [Fact]
    public async Task PastTheWindowAQueueingAbandonedChargeOrderAppointedToOurVehicleIsStillCancelled()
    {
        await using FleetFixture fleet = await FleetAsync();
        fleet.Riot.AnswersAbsentAsRealRiot = true;
        EventRecordingLogger<ControlServer.Host.Runtime.ForeignOrders.ForeignRunningOrderSupervisor> log = new();
        (string CycleId, string JourneyId, string UpperId) before = await ResultUnknownAndAbsentAsync(fleet);
        await fleet.HearFromEveryVehicleAsync();
        await fleet.RunRoundAsync(fleet.Options.ChargingOrderAbsentAbandonAfter);
        fleet.Clock.Advance(fleet.Options.OwnOrderRebuildRepeatWindow + TimeSpan.FromSeconds(1));

        // Queueing, appointed to our vehicle (the double lists the vehicle key as both keys), not yet running.
        fleet.Riot.PutOrder(new RiotOrderObservation(
            before.UpperId, RiotOrderObservationKind.Active, "ORDER-LATE", RiotOrderState.Queueing, KeyA, Map, Near.StationId));
        fleet.Context.ChangeTracker.Clear();
        await SupervisorOf(fleet, log).SuperviseAsync(Token);
        fleet.Context.ChangeTracker.Clear();

        ForeignRiotOrderRow row = await fleet.Context.ForeignRiotOrders.AsNoTracking().SingleAsync(Token);
        Assert.Equal(
            ("ORDER-LATE", AgvA, AbandonedChargeOrders.OwnershipBasis, (int?)RiotOrderState.Queueing),
            (row.RiotOrderId, row.AgvId, row.OwnershipBasis, row.OrderStateAtDetection));
        Assert.Equal([(RiotCommandTypeNames.CancelOrder, "ORDER-LATE")], fleet.Riot.OrderCommands);
        Assert.Single(log.Entries, entry => entry.EventId.Id == 2188);
    }

    /// <summary>
    /// 放弃计时按真实 RIoT 的答法算（增量审查 M-A）：真实 RIoT 对它没有的单不回 404，回 HTTP 200、业务码 0、不带 result，网关归为
    /// <c>AbsentAtObservation</c> 的 Unknown。只认 404 时，审查探针 PA 里 20 分钟车停稳、名下没单，一次也没放弃。这里用真实形态：满时限恰好放弃
    /// 一次；中间来一次<b>不精确</b>的 Unknown（带失败类别）就不算「查无此单」、重新计时。
    /// </summary>
    [Fact]
    public async Task RealRiotsNoSuchOrderAnswerCountsTowardsGivingUpAndAnInexactUnknownStartsTheCountAgain()
    {
        await using FleetFixture fleet = await FleetAsync();
        fleet.Riot.AnswersAbsentAsRealRiot = true;
        (string CycleId, string JourneyId, string UpperId) before = await ResultUnknownAndAbsentAsync(fleet);
        // The double answers the real form -- not a 404 the engine would have taken before.
        RiotOrderObservation answer = await fleet.Riot.ReconcileByUpperIdAsync(before.UpperId, Token);
        Assert.Equal(RiotOrderObservationKind.Unknown, answer.Kind);
        Assert.True(answer.IsExactAbsentAtObservation(before.UpperId));

        await fleet.HearFromEveryVehicleAsync();
        await fleet.RunRoundAsync(TimeSpan.FromSeconds(100));
        Assert.Equal(before, await CommitmentOfAsync(fleet, KeyA));

        // An unknown that is not the exact absent read -- the same classification, but with a failure category -- breaks the run.
        RiotOrderObservation real = fleet.Riot.RealRiotAbsent(before.UpperId);
        RiotOrderObservation inexact = real with { Receipt = real.Receipt! with { FailureCategory = "TRANSPORT_FAILURE" } };
        Assert.False(inexact.IsExactAbsentAtObservation(before.UpperId));
        fleet.Riot.PutOrder(inexact);
        await RoundAsync(fleet);
        fleet.Riot.PutOrder(new RiotOrderObservation(before.UpperId, RiotOrderObservationKind.NotFound, null));
        await RoundAsync(fleet);
        await fleet.HearFromEveryVehicleAsync();
        await fleet.RunRoundAsync(TimeSpan.FromSeconds(100));
        Assert.Equal(before, await CommitmentOfAsync(fleet, KeyA));
        Assert.DoesNotContain(fleet.EngineLog.Entries, entry => entry.EventId.Id == 2255);

        await fleet.HearFromEveryVehicleAsync();
        await fleet.RunRoundAsync(TimeSpan.FromSeconds(30));
        await RoundAsync(fleet);

        ChargingCycleRow cycle = await fleet.Context.Set<ChargingCycleRow>().AsNoTracking()
            .SingleAsync(row => row.CycleId == before.CycleId, Token);
        Assert.Equal((ChargingCyclePhases.Ended, ChargingExecutionReasons.OrderNeverAppeared), (cycle.Phase, cycle.EndReason));
        Assert.Null(await ClaimOfAsync(fleet, KeyA));
        Assert.Single(fleet.EngineLog.Entries, entry => entry.EventId.Id == 2255);
        Assert.Single(fleet.Riot.Creates);
    }

    private static ControlServer.Host.Runtime.ForeignOrders.ForeignRunningOrderSupervisor SupervisorOf(
        FleetFixture fleet,
        EventRecordingLogger<ControlServer.Host.Runtime.ForeignOrders.ForeignRunningOrderSupervisor> log)
    {
        // The foreign-order cancel gate stays closed, as it ships: a charge order this server gave up is its own to cancel.
        ControlServer.Host.Runtime.ForeignOrders.RiotForeignOrderCancelOptions gate = new();
        Assert.False(gate.Enabled);
        return new(
            fleet.Context,
            fleet.Riot,
            fleet.Riot,
            new RiotOrderCommandAuditStore(fleet.Context),
            new ControlServer.Host.Runtime.Fleet.VehicleRoster(Microsoft.Extensions.Options.Options.Create(fleet.Options)),
            Microsoft.Extensions.Options.Options.Create(fleet.Options),
            Microsoft.Extensions.Options.Options.Create(gate),
            fleet.Clock,
            log);
    }

    /// <summary>
    /// 「只告警一次」的键在事情走别的路了结后要丢掉（增量审查低项）：旅程已收尾的「证明不了停稳」、桩预占已不在的「放不掉」，每一轮开头的扫描
    /// 清掉；还开着的旅程、还在的预占，键留着——不然同一件事会每轮告警一次。
    /// </summary>
    [Fact]
    public async Task TheSaidOnceKeysOfSettledJourneysAndReleasedReservationsAreForgottenAndTheOthersKept()
    {
        // Two vehicles: the dispatch round -- and with it the sweep -- runs only while some vehicle is free or under way, and A
        // is neither while it is on its way to charge. B stays above its line.
        await using FleetFixture fleet = await FleetAsync(vehicles: 2);
        fleet.Riot.BatteryByVehicle[KeyA] = 20;
        await RoundAsync(fleet);
        (string CycleId, string JourneyId, string UpperId) open = await CommitmentOfAsync(fleet, KeyA);
        ChargingAllocationBoard board = fleet.ChargingBoard;
        Assert.True(board.FirstTime(ChargingAllocationBoard.EndNotProvenKey(open.JourneyId)));
        Assert.True(board.FirstTime(ChargingAllocationBoard.ReservationStuckKey(open.JourneyId)));
        Assert.True(board.FirstTime(ChargingAllocationBoard.EndNotProvenKey("charging:closed-elsewhere")));
        Assert.True(board.FirstTime(ChargingAllocationBoard.ReservationStuckKey("charging:released-elsewhere")));
        Assert.True(board.FirstTime("some-other-key"));

        await RoundAsync(fleet);

        Assert.False(board.FirstTime(ChargingAllocationBoard.EndNotProvenKey(open.JourneyId)));
        Assert.False(board.FirstTime(ChargingAllocationBoard.ReservationStuckKey(open.JourneyId)));
        Assert.True(board.FirstTime(ChargingAllocationBoard.EndNotProvenKey("charging:closed-elsewhere")));
        Assert.True(board.FirstTime(ChargingAllocationBoard.ReservationStuckKey("charging:released-elsewhere")));
        Assert.False(board.FirstTime("some-other-key"));
    }

    /// <summary>
    /// 充电单被取消之后车一直证明不了停稳（被关机、拖走、读不到）：预占、周期与用途照旧保持——不凭时间放；但超过
    /// <c>JourneyRuntime:OwnOrderRebuildRepeatWindow</c> 告警一次（事件 2256，带车号与桩号），不让它无声地占着唯一的桩。人工清桩归 control-server#406。
    /// </summary>
    [Fact]
    public async Task ACancelledChargingWhoseVehicleIsNeverProvenStoppedIsAlarmedOncePastTheWindowAndKeepsEverything()
    {
        await using FleetFixture fleet = await FleetAsync();
        fleet.Riot.BatteryByVehicle[KeyA] = 20;
        JourneyRuntimeRow journey = await CommittedAndSentAsync(fleet);
        (string CycleId, string JourneyId, string UpperId) before = await CommitmentOfAsync(fleet, KeyA);
        fleet.Riot.CancelOrder(journey.PickupUpperId);
        fleet.Riot.VehicleOverrides[KeyA] = seen => seen with { Connected = false, CurrentStationId = 12 };
        await RoundAsync(fleet);
        await RoundAsync(fleet);
        Assert.DoesNotContain(fleet.EngineLog.Entries, entry => entry.EventId.Id == 2256);

        await fleet.HearFromEveryVehicleAsync();
        await fleet.RunRoundAsync(fleet.Options.OwnOrderRebuildRepeatWindow + TimeSpan.FromMinutes(1));
        await RoundAsync(fleet);
        await RoundAsync(fleet);

        var alarm = Assert.Single(fleet.EngineLog.Entries, entry => entry.EventId.Id == 2256);
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Warning, alarm.Level);
        Assert.Contains(KeyA, alarm.Message, StringComparison.Ordinal);
        Assert.Contains(Near.StationId.ToString(System.Globalization.CultureInfo.InvariantCulture), alarm.Message, StringComparison.Ordinal);
        Assert.Equal(before, await CommitmentOfAsync(fleet, KeyA));
        Assert.Equal((KeyA, StationExclusivityStates.Reserved), await HolderAsync(fleet, Near.StationId));
    }

    /// <summary>
    /// 周期已按失败收尾、而桩的预占一直放不掉（三项确认拿不到：这里是车读出来就停在桩上）：同样不凭时间放，超过窗口告警一次（事件 2247，
    /// 带车、桩与缺的是哪一项）。
    /// </summary>
    [Fact]
    public async Task AReservationLeftByAFailedCycleThatCannotBeReleasedIsAlarmedOncePastTheWindow()
    {
        await using FleetFixture fleet = await FleetAsync();
        fleet.Riot.BatteryByVehicle[KeyA] = 20;
        JourneyRuntimeRow journey = await CommittedAndSentAsync(fleet);
        fleet.Riot.VehicleOverrides[KeyA] = seen => seen with { CurrentStationId = Near.StationId };
        fleet.Riot.CancelOrder(journey.PickupUpperId);
        await RoundAsync(fleet);
        await RoundAsync(fleet);
        Assert.Equal(
            ChargingExecutionReasons.OrderEnded,
            (await fleet.Context.Set<ChargingCycleRow>().AsNoTracking().SingleAsync(Token)).EndReason);
        Assert.DoesNotContain(fleet.ChargingLog.Entries, entry => entry.EventId.Id == 2247);

        await fleet.HearFromEveryVehicleAsync();
        await fleet.RunRoundAsync(fleet.Options.OwnOrderRebuildRepeatWindow + TimeSpan.FromMinutes(1));
        await RoundAsync(fleet);
        await RoundAsync(fleet);

        var alarm = Assert.Single(fleet.ChargingLog.Entries, entry => entry.EventId.Id == 2247);
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Warning, alarm.Level);
        Assert.Contains(KeyA, alarm.Message, StringComparison.Ordinal);
        Assert.Contains("standing on the charger", alarm.Message, StringComparison.Ordinal);
        Assert.Equal((KeyA, StationExclusivityStates.Reserved), await HolderAsync(fleet, Near.StationId));
    }

    // ---- 按车冷却（独立审查 M3）--------------------------------------------------------------------------------------

    /// <summary>
    /// 名册里有 211 与 221。去 211 的充电单被人在 RIoT 里取消（审查探针 P2：修复前 3 秒后就建出了去 221 的单）。冷却是这辆车的，不是某个桩的：
    /// <c>JourneyRuntime:OwnOrderRebuildDelay</c> 之内不给它承诺任何充电桩，一张单也不建；过了冷却它重新排队。
    /// </summary>
    [Fact]
    public async Task AVehicleWhoseChargingOrderWasJustCancelledIsSentToNoOtherChargerDuringTheCooldown()
    {
        await using FleetFixture fleet = await FleetAsync(chargers: [Near, Far]);
        fleet.Riot.BatteryByVehicle[KeyA] = 20;
        JourneyRuntimeRow first = await CommittedAndSentAsync(fleet);
        Assert.Equal(Near.StationId, Assert.Single(fleet.Riot.Creates).DestinationStationId);
        fleet.Riot.CancelOrder(first.PickupUpperId);

        for (int round = 0; round < 6; round++)
        {
            await RoundAsync(fleet);
            Assert.Single(fleet.Riot.Creates);
            Assert.Null(await StationAsync(fleet, Far.StationId));
        }

        Assert.Equal(
            ChargingExecutionReasons.OrderEnded,
            (await fleet.Context.Set<ChargingCycleRow>().AsNoTracking().SingleAsync(Token)).EndReason);
        Assert.Null(await ClaimOfAsync(fleet, KeyA));
        Assert.Equal(ChargingAllocationReasons.CooldownAfterFailedCycle, fleet.ChargingBoard.Verdicts[AgvA].Reason);

        await fleet.HearFromEveryVehicleAsync();
        await fleet.RunRoundAsync(fleet.Options.OwnOrderRebuildDelay);
        await RoundAsync(fleet);

        Assert.Equal(VehiclePurposes.Charging, (await ClaimOfAsync(fleet, KeyA))!.Value.Purpose);
        Assert.Equal(2, fleet.Riot.Creates.Count);
    }

    /// <summary>
    /// 取消的间隔略大于 <c>JourneyRuntime:OwnOrderRebuildRepeatWindow</c>（审查探针 P2b）：按定下的口径——窗口内两次才停——这些失败不会累计成
    /// 人工充电等待，但每一次取消之后这辆车都先冷却，冷却期内哪个桩都不去。（每一轮都要一个人隔十多分钟去取消一次；窗口内第二次即停由
    /// <see cref="ASecondEndedChargingOrderWithinTheWindowPutsTheVehicleOnManualHold"/> 守。）
    /// </summary>
    [Fact]
    public async Task OrdersCancelledMoreThanTheWindowApartNeverAddUpToAHoldButEachOneStillCoolsTheVehicleDown()
    {
        await using FleetFixture fleet = await FleetAsync(chargers: [Near, Far]);
        fleet.Riot.BatteryByVehicle[KeyA] = 20;
        JourneyRuntimeRow journey = await CommittedAndSentAsync(fleet);

        for (int cancelled = 1; cancelled <= 3; cancelled++)
        {
            fleet.Riot.CancelOrder(journey.PickupUpperId);
            for (int round = 0; round < 4; round++)
            {
                await RoundAsync(fleet);
                Assert.Equal(cancelled, fleet.Riot.Creates.Count);
            }
            Assert.Equal(ChargingAllocationReasons.CooldownAfterFailedCycle, fleet.ChargingBoard.Verdicts[AgvA].Reason);

            // More than the window later (the vehicle's session is heard from again first: the long step aged it out).
            await fleet.RunRoundAsync(fleet.Options.OwnOrderRebuildRepeatWindow + TimeSpan.FromMinutes(1));
            await RoundAsync(fleet);
            await RoundAsync(fleet);
            journey = (await ChargingJourneyAsync(fleet, AgvA))!;
            Assert.Equal(JourneyRuntimeStage.AwaitingPickupArrival, journey.Stage);
            Assert.Equal(cancelled + 1, fleet.Riot.Creates.Count);
        }

        Assert.Empty(await fleet.Context.Set<ManualChargingHoldRow>().AsNoTracking().ToArrayAsync(Token));
    }

    // ---- 看板说明 --------------------------------------------------------------------------------------------------------

    /// <summary>充电旅程会写在旅程行上的每一个码，在看板的阻断卡片上都有中文说明（新码进看板说明，不留英文码给现场猜）。</summary>
    [Fact]
    public void EveryCodeAChargingJourneyWritesHasAChineseDescriptionOnTheBlockedJourneysCard()
    {
        // Every constant, read by reflection (review low item): a code added later needs a description without anyone
        // remembering to list it. Two constants are not codes a journey row carries: the prefix of the leg outcome codes, and
        // the reason a charger reservation is released for, which goes on the station exclusivity record.
        string[] notOnAJourneyRow = [ChargingExecutionReasons.LegName, ChargingExecutionReasons.ReservationReleasedAfterEndedCycle];
        string[] constants =
        [
            .. typeof(ChargingExecutionReasons)
                .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                .Where(field => field.IsLiteral && field.FieldType == typeof(string))
                .Select(field => (string)field.GetRawConstantValue()!)
                .Except(notOnAJourneyRow),
        ];
        string[] codes = [.. constants, .. ChargingExecutionReasons.LegOutcomeCodes];

        Assert.Contains(ChargingExecutionReasons.OrderNeverAppeared, constants);
        Assert.Contains(ChargingExecutionReasons.WithdrawnDepartureNotProven, constants);
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

    /// <summary>
    /// 这辆车发件箱里「撤下 CHARGING」的业务状态快照的消息 id：没有现行用途、不在充电周期里、也不是人工充电等待的那一张。
    /// </summary>
    private static async Task<string[]> ClosingStateMessageIdsAsync(FleetFixture fleet, string agvId)
    {
        var rows = await fleet.Context.ProtocolOutbox.AsNoTracking()
            .Where(row => row.MessageType == "VehicleBusinessStateSnapshot")
            .Select(row => new { row.MessageId, row.PayloadJson })
            .ToArrayAsync(Token);
        return
        [
            .. rows.Where(row =>
                {
                    using JsonDocument envelope = JsonDocument.Parse(row.PayloadJson);
                    JsonElement payload = envelope.RootElement.GetProperty("payload");
                    return envelope.RootElement.GetProperty("agvId").GetString() == agvId &&
                           payload.GetProperty("activePurpose").ValueKind == JsonValueKind.Null &&
                           payload.GetProperty("chargingCycleState").GetString() == ChargingCycleWireStates.NotCharging &&
                           !payload.GetProperty("manualChargingHold").GetBoolean();
                })
                .Select(row => row.MessageId),
        ];
    }

    /// <summary>
    /// 承诺、过安全门、建单发出去而应答丢了（结果未知），RIoT 上按这个单号查无此单：答那份承诺的三个身份。旅程停在 <c>CHARGER_ResultUnknown</c>。
    /// </summary>
    private static async Task<(string CycleId, string JourneyId, string UpperId)> ResultUnknownAndAbsentAsync(FleetFixture fleet)
    {
        fleet.Riot.BatteryByVehicle[KeyA] = 20;
        fleet.Riot.CreateAnswer = intent => new RiotOrderObservation(intent.UpperId, RiotOrderObservationKind.Unknown, null);
        await RoundAsync(fleet);
        await RoundAsync(fleet);
        fleet.Riot.CreateAnswer = null;
        Assert.Single(fleet.Riot.Creates);
        Assert.Equal(
            ChargingExecutionReasons.LegOutcomeCode(MovementDispatchOutcome.ResultUnknown),
            (await ChargingJourneyAsync(fleet, AgvA))!.BlockReasonCode);
        return await CommitmentOfAsync(fleet, KeyA);
    }

    private static async Task<JourneyRuntimeRow?> ChargingJourneyAsync(FleetFixture fleet, string agvId)
    {
        JourneyRuntimeRow[] rows = await fleet.Context.JourneyRuntimes.AsNoTracking()
            .Where(row => row.AgvId == agvId && row.JourneyId.StartsWith(ChargingIdentity.JourneyIdPrefix))
            .ToArrayAsync(Token);
        return rows.OrderByDescending(row => row.CreatedAt).FirstOrDefault();
    }
}
