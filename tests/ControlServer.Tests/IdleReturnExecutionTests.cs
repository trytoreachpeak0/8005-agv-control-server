using System.Data.Common;
using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Dashboard;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Faults;
using ControlServer.Host.Runtime.Commands;
using ControlServer.Host.Runtime.IdleReturn;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using FleetFixture = ControlServer.Tests.MultiVehicleExecutionTests.FleetFixture;

namespace ControlServer.Tests;

/// <summary>
/// 空闲返回的执行（批次8-19，control-server#390）：承诺之后的物化、出发前安全门、建单与对账、到点证据、收敛、离点释放与失败分流，
/// 在引擎的真实轮次里跑（派车轮末尾承诺，下一轮物化与建单），RIoT 与车载端用车队夹具的替身。
/// </summary>
/// <remarks>
/// <para>
/// <b>路网</b>：车队夹具默认的单向链（300 → 12 → 13 → 210）之外，加两个等待点 214、215，从车位 300 两个方向都连着（代价 5000、8000 毫米），
/// 车默认停在 300，所以 214 是最近的等待点。
/// </para>
/// <para>
/// 整个类挂 <c>FP-IS-12</c>：这里的每一条都是服务端 <c>CLAIM_AND_RELEASE_WAITING_POINT</c> 那一半的行为；两条带
/// <c>CV-WAITING-POINT-IDLE-RETURN</c> 的是向量的同名具名测试。
/// </para>
/// </remarks>
[Trait("IntegrationSlice", "FP-IS-12")]
public sealed class IdleReturnExecutionTests
{
    private const string Vector = "CV-WAITING-POINT-IDLE-RETURN";
    private const int Map = 25;
    private const int Home = 300;
    private static readonly WaitingPointEntry Near = new(Map, 214, "等待点214", true, []);
    private static readonly WaitingPointEntry Far = new(Map, 215, "等待点215", true, []);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string AgvA => FleetFixture.AgvIds[0];
    private static string KeyA => FleetFixture.VehicleKeys[0];
    private static string AgvB => FleetFixture.AgvIds[1];
    private static string KeyB => FleetFixture.VehicleKeys[1];

    // ---- 计划与投影 --------------------------------------------------------------------------------------------------

    /// <summary>
    /// 单确认之后发出一张 <c>WAITING_POINT</c> 腿的计划（<c>legType</c>、<c>demandId</c> 为空、<c>ACTIVE</c>）与 <c>IDLE_RETURN</c> 的业务状态
    /// （没有装货阶段），没有清单、没有录入请求；消息 id 是 <c>StableUuid(journeyId|用途)</c>。旅程行没有需求，订单意图是单段移动。
    /// </summary>
    [Fact]
    public async Task AConfirmedIdleReturnPublishesOneWaitingPointLegAndIdleReturnWithNoWorklistOrEntryRequest()
    {
        await using FleetFixture fleet = await FleetAsync();

        JourneyRuntimeRow journey = await CommittedAndSentAsync(fleet);

        Assert.Null(journey.DemandId);
        Assert.Equal((Near.StationId, Near.StationName), (journey.PickupStationRiotId, journey.PickupStationId));
        OrderIntentRow intent = await fleet.Context.OrderIntents.AsNoTracking().SingleAsync(Token);
        Assert.Equal(
            (null, IdleReturnJourneyShape.IntentPurpose, OrderShapes.SingleMove, "CONFIRMED", Near.StationId),
            (intent.DemandId, intent.Purpose, intent.OrderShape, intent.Status, intent.DestinationStationId));
        Assert.Equal(IdleReturnJourneyShape.UpperIdFor(journey.JourneyId), intent.UpperId);

        JsonElement plan = Assert.Single(await PayloadsAsync(fleet, AgvA, "UpcomingStopPlanSnapshot"));
        JsonElement leg = Assert.Single(plan.GetProperty("legs").EnumerateArray());
        Assert.Equal("WAITING_POINT", leg.GetProperty("stopPurposeCategory").GetString());
        Assert.Equal(JsonValueKind.Null, leg.GetProperty("legType").ValueKind);
        Assert.Equal(JsonValueKind.Null, leg.GetProperty("demandId").ValueKind);
        Assert.Equal(("ACTIVE", Near.StationName), (leg.GetProperty("state").GetString(), leg.GetProperty("stationId").GetString()));
        JsonElement state = Assert.Single(await PayloadsAsync(fleet, AgvA, "VehicleBusinessStateSnapshot"));
        Assert.Equal(VehicleActivePurposes.IdleReturn, state.GetProperty("activePurpose").GetString());
        Assert.Equal(JsonValueKind.Null, state.GetProperty("loadingPhase").ValueKind);
        Assert.Empty(await PayloadsAsync(fleet, AgvA, "CurrentStopWorklistSnapshot"));
        Assert.Empty(await PayloadsAsync(fleet, AgvA, "SublotEntryRequested"));
        Assert.Equal(
            [JourneyPlanBuilder.StableGuid(journey.JourneyId, "waiting-point-plan"),
             JourneyPlanBuilder.StableGuid(journey.JourneyId, "waiting-point-vehicle-state")],
            [journey.PlanMessageId, journey.VehicleBusinessMessageId]);
    }

    // ---- 建单 --------------------------------------------------------------------------------------------------------

    /// <summary>
    /// 建一次单；之后在途的每一轮只对账，不再建。途中来了一条正好适合它的搬运，它不接、也不被问追加（<c>REQ-0290</c>：已开始的用途不被新用途抢）。
    /// </summary>
    [Fact]
    public async Task AnIdleReturnCreatesItsOrderOnceAndLaterRoundsOnlyReconcileIt()
    {
        await using FleetFixture fleet = await FleetAsync();
        JourneyRuntimeRow journey = await CommittedAndSentAsync(fleet);
        await fleet.AllowEnRouteAppendAsync(1_000_000);
        fleet.Catalog.Set([FleetFixture.Demand(0, "N1-1", 0)]);

        for (int round = 0; round < 3; round++)
        {
            await RoundAsync(fleet);
        }

        Assert.Equal([journey.JourneyId], await fleet.Context.JourneyRuntimes.AsNoTracking().Select(row => row.JourneyId).ToArrayAsync(Token));
        Assert.Empty(await fleet.Context.Set<JourneyDemandRow>().AsNoTracking().ToArrayAsync(Token));
        var create = Assert.Single(fleet.Riot.Creates);
        Assert.Equal((KeyA, journey.PickupUpperId, Near.StationId), (create.VehicleKey, create.UpperId, create.DestinationStationId));
        Assert.Equal(JourneyRuntimeStage.AwaitingPickupArrival, (await IdleJourneyAsync(fleet, AgvA))!.Stage);
    }

    /// <summary>
    /// 结果未知（<c>REQ-0294</c>）：车、目标点、用途占有、等待点预占都保持，单号不换、不重复建、不发「已出发」；RIoT 之后读到那张单在，就按
    /// 同一个单号确认下去。
    /// </summary>
    [Fact]
    public async Task AResultUnknownKeepsTheVehicleThePointThePurposeAndTheReservationAndNeverCreatesTwice()
    {
        await using FleetFixture fleet = await FleetAsync();
        fleet.Riot.CreateAnswer = intent => new RiotOrderObservation(intent.UpperId, RiotOrderObservationKind.Unknown, null);
        await RoundAsync(fleet);
        await RoundAsync(fleet);
        fleet.Riot.CreateAnswer = null;
        JourneyRuntimeRow journey = (await IdleJourneyAsync(fleet, AgvA))!;
        Assert.Equal($"{IdleReturnExecutionReasons.LegName}_{MovementDispatchOutcome.ResultUnknown}", journey.BlockReasonCode);

        for (int round = 0; round < 3; round++)
        {
            await RoundAsync(fleet);
        }

        Assert.Single(fleet.Riot.Creates);
        Assert.Equal((VehiclePurposes.IdleReturn, journey.JourneyId), await ClaimOfAsync(fleet, KeyA));
        StationExclusivityRow reserved = (await StationAsync(fleet, Near.StationId))!;
        Assert.Equal((StationExclusivityStates.Reserved, KeyA, journey.JourneyId), (reserved.State, reserved.VehicleKey, reserved.JourneyId));
        Assert.Equal(journey.PickupUpperId, (await fleet.Context.OrderIntents.AsNoTracking().SingleAsync(Token)).UpperId);
        Assert.Empty(await PayloadsAsync(fleet, AgvA, "UpcomingStopPlanSnapshot"));
        Assert.Empty(await PayloadsAsync(fleet, AgvA, "VehicleBusinessStateSnapshot"));

        fleet.Riot.PutOrder(new RiotOrderObservation(
            journey.PickupUpperId, RiotOrderObservationKind.Active, "ORDER-LATE", RiotOrderState.Executing, KeyA, Map, Near.StationId));
        await RoundAsync(fleet);

        OrderIntentRow intent = await fleet.Context.OrderIntents.AsNoTracking().SingleAsync(Token);
        Assert.Equal(("CONFIRMED", "ORDER-LATE"), (intent.Status, intent.OrderId));
        Assert.Single(fleet.Riot.Creates);
        Assert.Null((await IdleJourneyAsync(fleet, AgvA))!.BlockReasonCode);
    }

    // ---- 到点 --------------------------------------------------------------------------------------------------------

    public static TheoryData<string> MissingArrivalEvidence => ["order-not-ended", "other-station", "moving", "stale", "order-elsewhere"];

    /// <summary>
    /// 到点证据的每一样缺了都保持在途（<c>REQ-0295</c>）：单没到终态、车当前站不是等待点、车在动、车辆读数过期、单的目标不是预占的点；
    /// 等两个小时也不放车、不放点。
    /// </summary>
    [Theory]
    [MemberData(nameof(MissingArrivalEvidence))]
    public async Task AnyMissingArrivalEvidenceKeepsTheIdleReturnInTransitHoweverLongItWaits(string missing)
    {
        await using FleetFixture fleet = await FleetAsync();
        JourneyRuntimeRow journey = await CommittedAndSentAsync(fleet);
        RiotOrderObservation order = fleet.Riot.OrderOf(journey.PickupUpperId)!;
        switch (missing)
        {
            case "order-not-ended":
                MoveTo(fleet, KeyA, Near.StationId);
                break;
            case "other-station":
                fleet.Riot.CompleteOrder(journey.PickupUpperId);
                MoveTo(fleet, KeyA, 12);
                break;
            case "moving":
                fleet.Riot.CompleteOrder(journey.PickupUpperId);
                MoveTo(fleet, KeyA, Near.StationId, speed: 0.4);
                break;
            case "stale":
                fleet.Riot.CompleteOrder(journey.PickupUpperId);
                DateTimeOffset readLongAgo = fleet.Clock.GetUtcNowWithoutTick().AddMinutes(-10);
                fleet.Riot.VehicleOverrides[KeyA] = seen => seen with { CurrentStationId = Near.StationId, ObservedAt = readLongAgo };
                break;
            case "order-elsewhere":
                fleet.Riot.PutOrder(order with
                {
                    Kind = RiotOrderObservationKind.Terminal, OrderState = RiotOrderState.Success, DestinationStationId = Far.StationId,
                });
                MoveTo(fleet, KeyA, Near.StationId);
                break;
        }

        await RoundAsync(fleet);
        fleet.Clock.Advance(TimeSpan.FromHours(2));
        await RoundAsync(fleet);
        await RoundAsync(fleet);

        Assert.Equal(JourneyRuntimeStage.AwaitingPickupArrival, (await IdleJourneyAsync(fleet, AgvA))!.Stage);
        Assert.Equal((VehiclePurposes.IdleReturn, journey.JourneyId), await ClaimOfAsync(fleet, KeyA));
        Assert.Equal(StationExclusivityStates.Reserved, (await StationAsync(fleet, Near.StationId))!.State);
    }

    // ---- 收敛与离点 ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// 到点证据全满足：预占转占用、用途占有释放、旅程收尾，同一轮；收尾快照撤下 <c>IDLE_RETURN</c>（hmi#217 契约第 1 条），计划仍是那一条
    /// <c>ARRIVED</c> 的等待点腿（契约第 2 条：车还停在点上）。车回到可选择：下一条需求派给它。
    /// </summary>
    [Fact]
    public async Task OnArrivalTheReservationBecomesAnOccupancyThePurposeIsReleasedAndTheVehicleCanBeChosenAgain()
    {
        await using FleetFixture fleet = await FleetAsync();
        JourneyRuntimeRow journey = await CommittedAndSentAsync(fleet);

        await ArriveAsync(fleet, journey, KeyA, Near.StationId);

        JourneyRuntimeRow closed = (await IdleJourneyAsync(fleet, AgvA))!;
        Assert.Equal((JourneyRuntimeStage.Completed, (string?)null), (closed.Stage, closed.BlockReasonCode));
        Assert.Null(await ClaimOfAsync(fleet, KeyA));
        VehiclePurposeClaimRecordRow record = await fleet.Context.Set<VehiclePurposeClaimRecordRow>().AsNoTracking()
            .SingleAsync(row => row.JourneyId == journey.JourneyId, Token);
        Assert.Equal(IdleReturnExecutionReasons.ConvergedAtWaitingPoint, record.ReleaseReason);
        StationExclusivityRow occupied = (await StationAsync(fleet, Near.StationId))!;
        Assert.Equal((StationExclusivityStates.Occupied, KeyA, journey.JourneyId), (occupied.State, occupied.VehicleKey, occupied.JourneyId));

        JsonElement lastState = (await PayloadsAsync(fleet, AgvA, "VehicleBusinessStateSnapshot")).Last();
        Assert.Equal(JsonValueKind.Null, lastState.GetProperty("activePurpose").ValueKind);
        JsonElement lastPlan = (await PayloadsAsync(fleet, AgvA, "UpcomingStopPlanSnapshot")).Last();
        JsonElement leg = Assert.Single(lastPlan.GetProperty("legs").EnumerateArray());
        Assert.Equal(("WAITING_POINT", "ARRIVED"), (leg.GetProperty("stopPurposeCategory").GetString(), leg.GetProperty("state").GetString()));

        fleet.Catalog.Set([FleetFixture.Demand(0, "N1-1", 0)]);
        await RoundAsync(fleet);
        JourneyRuntimeRow transport = await fleet.Context.JourneyRuntimes.AsNoTracking()
            .SingleAsync(row => row.AgvId == AgvA && row.DemandId != null, Token);
        Assert.Equal(JourneyRuntimeStage.AwaitingPickupArrival, transport.Stage);
    }

    /// <summary>
    /// 离点（<c>REQ-0293</c>）：搬运单下达那一刻车还在点上，占用不放（「下达离点订单即释放」的缺陷版本在这一格变红）；RIoT 报出车到了别的站，
    /// 下一轮开头的离点清扫才放，原因是离点证据。之后搬运的计划整体替换，等待点腿不再出现（hmi#217 契约第 2 条）。
    /// </summary>
    [Fact]
    public async Task TheOccupancyStaysWhenTheDepartingOrderIsIssuedAndIsReleasedOnlyOnDepartureEvidence()
    {
        await using FleetFixture fleet = await FleetAsync();
        JourneyRuntimeRow journey = await CommittedAndSentAsync(fleet);
        await ArriveAsync(fleet, journey, KeyA, Near.StationId);

        fleet.Catalog.Set([FleetFixture.Demand(0, "N1-1", 0)]);
        await RoundAsync(fleet);
        Assert.Contains(fleet.Riot.Creates, create => create.VehicleKey == KeyA && create.DestinationStationId == 12);
        await RoundAsync(fleet);
        Assert.Equal(StationExclusivityStates.Occupied, (await StationAsync(fleet, Near.StationId))!.State);

        MoveTo(fleet, KeyA, 12, speed: 0);
        await RoundAsync(fleet);

        Assert.Null(await StationAsync(fleet, Near.StationId));
        StationExclusivityRecordRow passage = await fleet.Context.Set<StationExclusivityRecordRow>().AsNoTracking()
            .SingleAsync(row => row.JourneyId == journey.JourneyId, Token);
        Assert.Equal(FixedStationExclusivity.ReleasedOnDepartureEvidence, passage.ReleaseReason);
        Assert.NotNull(passage.ReservedAt);
        Assert.NotNull(passage.OccupiedAt);
        JsonElement transportPlan = (await PayloadsAsync(fleet, AgvA, "UpcomingStopPlanSnapshot")).Last();
        Assert.DoesNotContain(
            transportPlan.GetProperty("legs").EnumerateArray(),
            leg => leg.GetProperty("stopPurposeCategory").GetString() == "WAITING_POINT");
        // hmi#217 contract, item 1: nothing sent after the convergence says IDLE_RETURN any more.
        Assert.Equal(
            JsonValueKind.Null,
            (await PayloadsAsync(fleet, AgvA, "VehicleBusinessStateSnapshot")).Last().GetProperty("activePurpose").ValueKind);

        // The entry that follows still opens (the L1 cell of the hmi#217 contracts): the transport arrives at the pickup station
        // and the onboard is asked for a sublot there, as for any vehicle that never went to a waiting point.
        JourneyRuntimeRow pickup = await fleet.Context.JourneyRuntimes.AsNoTracking()
            .SingleAsync(row => row.AgvId == AgvA && row.DemandId != null, Token);
        fleet.Riot.CompleteOrder(pickup.PickupUpperId);
        await RoundAsync(fleet);
        await RoundAsync(fleet);
        Assert.Equal(
            JourneyRuntimeStage.AwaitingSublot,
            (await fleet.Context.JourneyRuntimes.AsNoTracking().SingleAsync(row => row.JourneyId == pickup.JourneyId, Token)).Stage);
        Assert.NotEmpty(await PayloadsAsync(fleet, AgvA, "SublotEntryRequested"));
    }

    // ---- 失败分流 ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// <c>REQ-0296</c> 第一支：首次建单之前重新核验失败（等待点被停用），单从没发过——用途占有与预占同一次保存释放，不建单，车回到重新评估；
    /// 重评选别的合格点。
    /// </summary>
    [Fact]
    public async Task APointDisabledBeforeTheFirstCreateReleasesTheCommitmentAndTheVehicleIsJudgedAgain()
    {
        await using FleetFixture fleet = await FleetAsync(points: [Near, Far]);
        await RoundAsync(fleet);
        string committed = (await ClaimOfAsync(fleet, KeyA))!.Value.JourneyId;
        await RegisterAsync(fleet, Near with { Enabled = false }, Far);

        await RoundAsync(fleet);

        JourneyRuntimeRow ended = await fleet.Context.JourneyRuntimes.AsNoTracking().SingleAsync(row => row.JourneyId == committed, Token);
        Assert.Equal((JourneyRuntimeStage.Completed, IdleReturnExecutionReasons.WaitingPointNoLongerEligible), (ended.Stage, ended.BlockReasonCode));
        Assert.Empty(fleet.Riot.Creates);
        Assert.Null(await StationAsync(fleet, Near.StationId));
        Assert.Null(await ClaimOfAsync(fleet, KeyA));

        // The vehicle was busy with that journey when this round began; the next round judges it afresh.
        await RoundAsync(fleet);

        (string Purpose, string JourneyId)? again = await ClaimOfAsync(fleet, KeyA);
        Assert.NotNull(again);
        Assert.NotEqual(committed, again.Value.JourneyId);
        Assert.Equal(again.Value.JourneyId, (await StationAsync(fleet, Far.StationId))!.JourneyId);
    }

    public static TheoryData<string> HeldFailures =>
        ["order-running", "cancelled-while-moving", "cancelled-motion-not-proven", "hang", "riot-unreadable"];

    /// <summary>
    /// <c>REQ-0296</c> 第二支：单存在、单被取消而车还在动、导航 <c>HANG</c>、RIoT 读不到（监听丢失）——都保持原承诺与独占，不盲选别的点、
    /// 不建第二张单，并写下原因码。
    /// </summary>
    [Theory]
    [MemberData(nameof(HeldFailures))]
    public async Task WhileAnOrderMayExistOrTheVehicleMayMoveTheCommitmentAndTheReservationAreKept(string failure)
    {
        await using FleetFixture fleet = await FleetAsync(points: [Near, Far]);
        JourneyRuntimeRow journey = await CommittedAndSentAsync(fleet);
        switch (failure)
        {
            case "order-running":
                break;
            case "cancelled-while-moving":
                fleet.Riot.CancelOrder(journey.PickupUpperId);
                MoveTo(fleet, KeyA, 12, speed: 0.6, procState: "RUNNING");
                break;
            case "cancelled-motion-not-proven":
                // Every vehicle reading says stopped with no order; only RIoT's motion safety read does not say Stopped
                // (review S2, MA). The ending needs both.
                fleet.Riot.CancelOrder(journey.PickupUpperId);
                MoveTo(fleet, KeyA, 12, speed: 0);
                fleet.Riot.SafetyReasons = ["MOTION_NOT_PROVEN_STOPPED"];
                break;
            case "hang":
                fleet.Riot.HangOrder(journey.PickupUpperId);
                break;
            case "riot-unreadable":
                fleet.Riot.PutOrder(new RiotOrderObservation(journey.PickupUpperId, RiotOrderObservationKind.Unknown, null));
                break;
        }

        for (int round = 0; round < 3; round++)
        {
            await RoundAsync(fleet);
        }

        Assert.Equal((VehiclePurposes.IdleReturn, journey.JourneyId), await ClaimOfAsync(fleet, KeyA));
        Assert.Equal(journey.JourneyId, (await StationAsync(fleet, Near.StationId))!.JourneyId);
        Assert.Null(await StationAsync(fleet, Far.StationId));
        Assert.Single(fleet.Riot.Creates);
        JourneyRuntimeRow held = (await IdleJourneyAsync(fleet, AgvA))!;
        Assert.Equal(JourneyRuntimeStage.AwaitingPickupArrival, held.Stage);
        string? expected = failure switch
        {
            "cancelled-while-moving" or "cancelled-motion-not-proven" => IdleReturnExecutionReasons.OrderEndedStopNotProven,
            "hang" => JourneyRuntimeEngine.OrderHangReason,
            _ => null,
        };
        Assert.Equal(expected, held.BlockReasonCode);
    }

    /// <summary>
    /// 票面第 7 条（开工核实定的路）：空闲返回单在 RIoT 被取消，不按 <c>REQ-0360</c> 同车重建（它没有需求可承载），按 <c>REQ-0296</c>：车证明停稳、
    /// 没有活动订单之后才是已确认失败——用途占有释放，预占等车不在点上由离点清扫放，下一次承诺排除原失败点、选别的点；没有重建记录。
    /// 下一次承诺要等 <c>OwnOrderRebuildDelay</c> 的冷却过去（审查问题 2，与搬运自建单被取消的第一道护栏对等）：冷却里答冷却码、不承诺。
    /// </summary>
    [Fact]
    public async Task AnIdleReturnOrderCancelledInRiotEndsOnceTheVehicleIsProvenStoppedAndTheNextCommitmentLeavesThatPointOut()
    {
        await using FleetFixture fleet = await FleetAsync(points: [Near, Far]);
        JourneyRuntimeRow journey = await CommittedAndSentAsync(fleet);
        fleet.Riot.CancelOrder(journey.PickupUpperId);

        await RoundAsync(fleet);

        JourneyRuntimeRow ended = await fleet.Context.JourneyRuntimes.AsNoTracking()
            .SingleAsync(row => row.JourneyId == journey.JourneyId, Token);
        Assert.Equal((JourneyRuntimeStage.Completed, IdleReturnExecutionReasons.OrderEnded), (ended.Stage, ended.BlockReasonCode));
        Assert.Empty(await fleet.Context.OwnOrderRebuilds.AsNoTracking().ToArrayAsync(Token));
        Assert.Null(await ClaimOfAsync(fleet, KeyA));
        // Still reserved: the vehicle has not been seen elsewhere since the ending was saved. The next round's sweep frees it.
        Assert.Equal(journey.JourneyId, (await StationAsync(fleet, Near.StationId))!.JourneyId);

        await RoundAsync(fleet);

        // Within the delay: nothing is committed, though Far is free and the vehicle is idle. Without the guard this round
        // committed it to Far at once, straight over the person's cancellation.
        Assert.Null(await ClaimOfAsync(fleet, KeyA));
        Assert.Null(await StationAsync(fleet, Near.StationId));
        Assert.Equal(IdleReturnReasons.CooldownAfterEndedOrder, fleet.IdleReturnBoard.Reasons[AgvA]);
        await fleet.HearFromEveryVehicleAsync();
        await fleet.RunRoundAsync(fleet.Options.OwnOrderRebuildDelay - TimeSpan.FromSeconds(5));
        Assert.Null(await ClaimOfAsync(fleet, KeyA));
        Assert.Equal(IdleReturnReasons.CooldownAfterEndedOrder, fleet.IdleReturnBoard.Reasons[AgvA]);

        await fleet.HearFromEveryVehicleAsync();
        await fleet.RunRoundAsync(TimeSpan.FromSeconds(5));

        (string Purpose, string JourneyId)? next = await ClaimOfAsync(fleet, KeyA);
        Assert.NotNull(next);
        Assert.NotEqual(journey.JourneyId, next.Value.JourneyId);
        Assert.Null(await StationAsync(fleet, Near.StationId));
        Assert.Equal(next.Value.JourneyId, (await StationAsync(fleet, Far.StationId))!.JourneyId);
        Assert.Equal(
            IdleReturnReasons.Committed,
            fleet.IdleReturnBoard.Reasons[AgvA]);
    }

    /// <summary>只有原失败点的时候，重评不选它：结论是没有可用等待点，细节里写着那个点上一次失败了。</summary>
    [Fact]
    public async Task WithOnlyTheFailedPointLeftTheVehicleIsNotSentBackToIt()
    {
        await using FleetFixture fleet = await FleetAsync();
        JourneyRuntimeRow journey = await CommittedAndSentAsync(fleet);
        fleet.Riot.DeleteOrder(journey.PickupUpperId);

        await RoundAsync(fleet);
        await RoundAsync(fleet);
        await fleet.HearFromEveryVehicleAsync();
        await fleet.RunRoundAsync(fleet.Options.OwnOrderRebuildDelay);

        Assert.Null(await ClaimOfAsync(fleet, KeyA));
        Assert.Null(await StationAsync(fleet, Near.StationId));
        // Near is the vehicle's only point and it is free again: without the exclusion it would be committed to it once more.
        Assert.Equal(IdleReturnReasons.NoWaitingPointAvailable, fleet.IdleReturnBoard.Reasons[AgvA]);
    }

    /// <summary>
    /// 审查问题 2 的第二道护栏（与搬运的 <c>REQ-0361</c> 对等）：冷却过后承诺去了别的点，那张单又在窗口内被取消——最近两趟都是被取消的
    /// 空闲返回，不再自动承诺，告警等人；窗口过去也不自动解除。这辆车照样能接搬运，做了别的旅程之后才解除。
    /// </summary>
    [Fact]
    public async Task ASecondCancelledIdleReturnWithinTheRepeatWindowStopsAutomaticIdleReturnsUntilTheVehicleDoesOtherWork()
    {
        await using FleetFixture fleet = await FleetAsync(points: [Near, Far]);
        JourneyRuntimeRow first = await CommittedAndSentAsync(fleet);
        fleet.Riot.CancelOrder(first.PickupUpperId);
        await RoundAsync(fleet);
        await RoundAsync(fleet);
        await fleet.HearFromEveryVehicleAsync();
        await fleet.RunRoundAsync(fleet.Options.OwnOrderRebuildDelay);
        await RoundAsync(fleet);
        JourneyRuntimeRow second = (await IdleJourneyAsync(fleet, AgvA))!;
        Assert.NotEqual(first.JourneyId, second.JourneyId);
        Assert.Equal("CONFIRMED", (await fleet.Context.OrderIntents.AsNoTracking()
            .SingleAsync(row => row.UpperId == second.PickupUpperId, Token)).Status);

        fleet.Riot.CancelOrder(second.PickupUpperId);
        await RoundAsync(fleet);
        Assert.Equal(
            IdleReturnExecutionReasons.OrderEnded,
            (await fleet.Context.JourneyRuntimes.AsNoTracking().SingleAsync(row => row.JourneyId == second.JourneyId, Token))
                .BlockReasonCode);

        // Past the delay and past the repeat window: still nothing. Near is free again (the failed point of the first, but the
        // exclusion only looks at the latest), so without the guard the vehicle would be sent there.
        await RoundAsync(fleet);
        await fleet.HearFromEveryVehicleAsync();
        await fleet.RunRoundAsync(fleet.Options.OwnOrderRebuildRepeatWindow + TimeSpan.FromMinutes(1));
        await RoundAsync(fleet);
        Assert.Null(await ClaimOfAsync(fleet, KeyA));
        Assert.Equal(IdleReturnReasons.StoppedAfterRepeatedEndedOrders, fleet.IdleReturnBoard.Reasons[AgvA]);
        // Increment review R1: the warning is the only signal on site (the stop is not on the dashboard), exactly once, and a new
        // engine -- a new round's scope in the host -- does not say it again.
        Assert.Single(fleet.IdleReturnLog.Entries, entry => entry.EventId.Id == 2227);
        await fleet.RecreateEngineAsync();
        await RoundAsync(fleet);
        await RoundAsync(fleet);
        var warning = Assert.Single(fleet.IdleReturnLog.Entries, entry => entry.EventId.Id == 2227);
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Warning, warning.Level);
        Assert.Contains(AgvA, warning.Message, StringComparison.Ordinal);
        Assert.Equal(2, (await fleet.Context.JourneyRuntimes.AsNoTracking()
            .Where(row => row.AgvId == AgvA).ToArrayAsync(Token)).Length);
        // Review M1 (b): the reviewer's probe saw 214, 215, 214 -- back to the first point after the second cancellation.
        Assert.Equal([Near.StationId, Far.StationId], [.. fleet.Riot.Creates.Select(create => create.DestinationStationId)]);

        // Transport is not held back: a demand is taken by this vehicle, and that journey is now its latest, which lifts the stop.
        fleet.Catalog.Set([FleetFixture.Demand(0, "N1-1", 0)]);
        await RoundAsync(fleet);
        Assert.Contains(fleet.Riot.Creates, create => create.VehicleKey == KeyA && create.DestinationStationId == 12);
    }

    /// <summary>
    /// 增量审查 L-a（审查员探针的顺序）：取消 → 第二趟在出发之前点被人释放、以 <c>IDLE_RETURN_WAITING_POINT_LOST</c> 收尾（不算失败）→
    /// 第三趟再被取消。夹在中间的收尾不打断计数：窗口里已确认失败两次，停止自动空闲返回。按「最近两趟」数时这里只冷却，84 秒建了 4 张单。
    /// </summary>
    [Fact]
    public async Task AnEndingThatIsNotAFailureBetweenTwoCancellationsDoesNotBreakTheStop()
    {
        await using FleetFixture fleet = await FleetAsync(points: [Near, Far]);
        JourneyRuntimeRow first = await CommittedAndSentAsync(fleet);
        fleet.Riot.CancelOrder(first.PickupUpperId);
        await RoundAsync(fleet);
        await RoundAsync(fleet);

        // The second: committed once the delay is over, held at the departure gate, its point released by hand before any
        // order was created -- it ends as WAITING_POINT_LOST, which is not a failure.
        await fleet.HearFromEveryVehicleAsync();
        await fleet.RunRoundAsync(fleet.Options.OwnOrderRebuildDelay);
        (string Purpose, string JourneyId)? second = await ClaimOfAsync(fleet, KeyA);
        Assert.NotNull(second);
        await fleet.ReplaceSafetySnapshotAsync(AgvA, unlockedSlot: 3);
        await RoundAsync(fleet);
        StationExclusivityRow secondPoint = await fleet.Context.Set<StationExclusivityRow>().AsNoTracking()
            .SingleAsync(row => row.JourneyId == second.Value.JourneyId, Token);
        await ReleaseByHandAsync(fleet, secondPoint.StationId);
        await fleet.ReplaceSafetySnapshotAsync(AgvA);
        await RoundAsync(fleet);
        Assert.Equal(
            IdleReturnExecutionReasons.WaitingPointLost,
            (await fleet.Context.JourneyRuntimes.AsNoTracking().SingleAsync(row => row.JourneyId == second.Value.JourneyId, Token))
                .BlockReasonCode);
        Assert.Single(fleet.Riot.Creates);

        // The third: committed right away (the latest journey is not a failure), sent, then cancelled too.
        for (int round = 0; round < 3 && fleet.Riot.Creates.Count < 2; round++)
        {
            await RoundAsync(fleet);
        }
        JourneyRuntimeRow third = (await IdleJourneyAsync(fleet, AgvA))!;
        Assert.NotEqual(second.Value.JourneyId, third.JourneyId);
        Assert.Equal(2, fleet.Riot.Creates.Count);
        fleet.Riot.CancelOrder(third.PickupUpperId);
        await RoundAsync(fleet);
        Assert.Equal(
            IdleReturnExecutionReasons.OrderEnded,
            (await fleet.Context.JourneyRuntimes.AsNoTracking().SingleAsync(row => row.JourneyId == third.JourneyId, Token))
                .BlockReasonCode);

        await RoundAsync(fleet);
        await fleet.HearFromEveryVehicleAsync();
        await fleet.RunRoundAsync(fleet.Options.OwnOrderRebuildDelay);
        await RoundAsync(fleet);
        Assert.Null(await ClaimOfAsync(fleet, KeyA));
        Assert.Equal(IdleReturnReasons.StoppedAfterRepeatedEndedOrders, fleet.IdleReturnBoard.Reasons[AgvA]);
        Assert.Equal(2, fleet.Riot.Creates.Count);
    }

    /// <summary>
    /// 审查 M1 (a)：FAILED 后由人清除故障，与单被取消走同一套护栏——冷却内不承诺（有别的合格点也不），冷却过后承诺别的点；那一趟又 FAILED、
    /// 又被人清除，就停止自动空闲返回，不回到第一个点。
    /// </summary>
    [Fact]
    public async Task AfterAPersonClearsAFailedIdleReturnTheSameCooldownAndStopApply()
    {
        await using FleetFixture fleet = await FleetAsync(points: [Near, Far]);
        fleet.Riot.MovementState = "MT_FINISHED";

        async Task FailAndClearAsync(JourneyRuntimeRow journey)
        {
            fleet.Riot.FailOrder(journey.PickupUpperId);
            await RoundAsync(fleet);
            await RoundAsync(fleet);
            fleet.Context.ChangeTracker.Clear();
            VehicleFaultRecoveryDecision decision = await fleet.CreateFaultRecovery().RecoverAsync(
                new VehicleFaultRecoveryRequest(
                    new EmergencyStopSubject(AgvA, KeyA), VehicleFaultRecoveryAction.ClearFault, "operator-1", true, null),
                Token);
            fleet.Context.ChangeTracker.Clear();
            Assert.Equal(VehicleFaultRecoveryDispositions.IdleReturnEnded, decision.Disposition);
        }

        JourneyRuntimeRow first = await CommittedAndSentAsync(fleet);
        await FailAndClearAsync(first);
        await RoundAsync(fleet);
        await RoundAsync(fleet);
        Assert.Null(await ClaimOfAsync(fleet, KeyA));
        Assert.Equal(IdleReturnReasons.CooldownAfterEndedOrder, fleet.IdleReturnBoard.Reasons[AgvA]);
        Assert.Single(fleet.Riot.Creates);

        await fleet.HearFromEveryVehicleAsync();
        await fleet.RunRoundAsync(fleet.Options.OwnOrderRebuildDelay);
        await RoundAsync(fleet);
        JourneyRuntimeRow second = (await IdleJourneyAsync(fleet, AgvA))!;
        Assert.NotEqual(first.JourneyId, second.JourneyId);
        Assert.Equal(Far.StationId, second.PickupStationRiotId);

        await FailAndClearAsync(second);
        await RoundAsync(fleet);
        await fleet.HearFromEveryVehicleAsync();
        await fleet.RunRoundAsync(fleet.Options.OwnOrderRebuildDelay);
        await RoundAsync(fleet);
        Assert.Null(await ClaimOfAsync(fleet, KeyA));
        Assert.Equal(IdleReturnReasons.StoppedAfterRepeatedEndedOrders, fleet.IdleReturnBoard.Reasons[AgvA]);
        Assert.Equal([Near.StationId, Far.StationId], [.. fleet.Riot.Creates.Select(create => create.DestinationStationId)]);
    }

    /// <summary>
    /// 审查 M1 (c)：冷却期间车停在原处。停在出发的地方：预占在收尾那一轮还在，下一轮离点清扫按离点证据放掉，冷却里不承诺、不建第二张单。
    /// 停在要去的等待点上（单被取消时车已经到了）：计划留着那条到达的腿，预占留给离点清扫、车不走就不放；冷却里、冷却过后都不再承诺
    /// （它已经在一个等待点上），被派走、离开之后才放。
    /// </summary>
    [Theory]
    [InlineData("at-origin")]
    [InlineData("at-the-waiting-point")]
    public async Task DuringTheCooldownTheVehicleStaysPutAndTheReservationIsLeftToTheDepartureSweep(string position)
    {
        await using FleetFixture fleet = await FleetAsync(points: [Near, Far]);
        JourneyRuntimeRow journey = await CommittedAndSentAsync(fleet);
        if (position == "at-the-waiting-point")
        {
            MoveTo(fleet, KeyA, Near.StationId);
        }
        fleet.Riot.CancelOrder(journey.PickupUpperId);
        await RoundAsync(fleet);

        Assert.Equal(
            IdleReturnExecutionReasons.OrderEnded,
            (await fleet.Context.JourneyRuntimes.AsNoTracking().SingleAsync(row => row.JourneyId == journey.JourneyId, Token))
                .BlockReasonCode);
        Assert.Null(await ClaimOfAsync(fleet, KeyA));
        Assert.Equal(journey.JourneyId, (await StationAsync(fleet, Near.StationId))!.JourneyId);

        await RoundAsync(fleet);
        Assert.Null(await ClaimOfAsync(fleet, KeyA));
        Assert.Single(fleet.Riot.Creates);
        if (position == "at-origin")
        {
            Assert.Null(await StationAsync(fleet, Near.StationId));
            Assert.Equal(IdleReturnReasons.CooldownAfterEndedOrder, fleet.IdleReturnBoard.Reasons[AgvA]);
            return;
        }

        Assert.Equal(journey.JourneyId, (await StationAsync(fleet, Near.StationId))!.JourneyId);
        JsonElement plan = (await PayloadsAsync(fleet, AgvA, "UpcomingStopPlanSnapshot")).Last();
        Assert.Equal("ARRIVED", Assert.Single(plan.GetProperty("legs").EnumerateArray()).GetProperty("state").GetString());
        await fleet.HearFromEveryVehicleAsync();
        await fleet.RunRoundAsync(fleet.Options.OwnOrderRebuildDelay);
        await RoundAsync(fleet);
        Assert.Null(await ClaimOfAsync(fleet, KeyA));
        Assert.Equal(IdleReturnReasons.VehicleHoldsStation, fleet.IdleReturnBoard.Reasons[AgvA]);
        Assert.Single(fleet.Riot.Creates);

        fleet.Catalog.Set([FleetFixture.Demand(0, "N1-1", 0)]);
        await RoundAsync(fleet);
        Assert.Contains(fleet.Riot.Creates, create => create.VehicleKey == KeyA && create.DestinationStationId == 12);
        MoveTo(fleet, KeyA, 12);
        await RoundAsync(fleet);
        Assert.Null(await StationAsync(fleet, Near.StationId));
    }

    /// <summary>
    /// 审查 S1：建单结果未知（请求已发出、没有回音）时等待点被人工释放，RIoT 读到查无此单——那不是终结：保持承诺与用途，写点丢失待停的码，
    /// 不收尾、不换点再建（与主路径对结果未知的单只对账、不再建同一个判法）。
    /// </summary>
    [Fact]
    public async Task AWaitingPointLostWhileTheCreateResultIsUnknownHoldsInsteadOfEnding()
    {
        await using FleetFixture fleet = await FleetAsync(points: [Near, Far]);
        fleet.Riot.CreateAnswer = intent => new RiotOrderObservation(intent.UpperId, RiotOrderObservationKind.Unknown, null);
        await RoundAsync(fleet);
        await RoundAsync(fleet);
        fleet.Riot.CreateAnswer = null;
        JourneyRuntimeRow journey = (await IdleJourneyAsync(fleet, AgvA))!;
        Assert.Equal(IdleReturnExecutionReasons.LegOutcomeCode(MovementDispatchOutcome.ResultUnknown), journey.BlockReasonCode);

        await ReleaseByHandAsync(fleet, Near.StationId);
        for (int round = 0; round < 3; round++)
        {
            await RoundAsync(fleet);
        }

        JourneyRuntimeRow held = (await IdleJourneyAsync(fleet, AgvA))!;
        Assert.Equal(journey.JourneyId, held.JourneyId);
        Assert.Equal(
            (JourneyRuntimeStage.AwaitingPickupArrival, IdleReturnExecutionReasons.WaitingPointLostOrderInFlight),
            (held.Stage, held.BlockReasonCode));
        Assert.Equal((VehiclePurposes.IdleReturn, journey.JourneyId), await ClaimOfAsync(fleet, KeyA));
        Assert.Single(fleet.Riot.Creates);
    }

    /// <summary>
    /// 审查 S2（MF）：「本服务端有结果未知的单就不承诺」只放行已了结的空闲返回留下的那张。一张不属于已完成空闲返回的结果未知的单挡住承诺；
    /// 拿掉它之后，已完成空闲返回那张停在 <c>TERMINAL_RECONCILIATION_REQUIRED</c> 的单不挡。
    /// </summary>
    [Fact]
    public async Task OnlyTheSettledIdleReturnLegIsLetThroughTheUnknownOrderCheck()
    {
        await using FleetFixture fleet = await FleetAsync(points: [Near, Far]);
        JourneyRuntimeRow journey = await CommittedAndSentAsync(fleet);
        fleet.Riot.CancelOrder(journey.PickupUpperId);
        await RoundAsync(fleet);
        Assert.Equal(JourneyRuntimeStage.Completed, (await IdleJourneyAsync(fleet, AgvA))!.Stage);

        // The ended leg's intent as a cancellation before its create was confirmed leaves it (the engine's
        // TerminalReconciliationRequired branch), and another leg of this vehicle whose create result is unknown.
        OrderIntentRow settled = await fleet.Context.OrderIntents.SingleAsync(row => row.UpperId == journey.PickupUpperId, Token);
        settled.Status = "TERMINAL_RECONCILIATION_REQUIRED";
        fleet.Context.OrderIntents.Add(new OrderIntentRow
        {
            MovementLegId = "unsettled-leg",
            UpperId = "W2G-UNSETTLED-LEG",
            Purpose = "TO_PICKUP",
            TargetStationId = "N1-1",
            VehicleKey = KeyA,
            MapId = Map,
            DestinationStationId = 12,
            CreatedAt = fleet.Clock.GetUtcNowWithoutTick(),
            Status = "RESULT_UNKNOWN",
        });
        await fleet.Context.SaveChangesAsync(Token);
        fleet.Context.ChangeTracker.Clear();

        await fleet.HearFromEveryVehicleAsync();
        await fleet.RunRoundAsync(fleet.Options.OwnOrderRebuildDelay);
        await RoundAsync(fleet);
        Assert.Null(await ClaimOfAsync(fleet, KeyA));
        Assert.Equal(IdleReturnReasons.OwnOrderResultUnknown, fleet.IdleReturnBoard.Reasons[AgvA]);

        fleet.Context.OrderIntents.Remove(await fleet.Context.OrderIntents.SingleAsync(row => row.MovementLegId == "unsettled-leg", Token));
        await fleet.Context.SaveChangesAsync(Token);
        fleet.Context.ChangeTracker.Clear();
        await RoundAsync(fleet);

        Assert.Equal(IdleReturnReasons.Committed, fleet.IdleReturnBoard.Reasons[AgvA]);
        Assert.NotNull(await ClaimOfAsync(fleet, KeyA));
    }

    /// <summary>
    /// 审查 L4：投运策略在承诺之后被收回（或读坏），已承诺、还没建单的空闲返回在出发安全门那一格被挡住，不建单；策略恢复后照常出发。
    /// </summary>
    [Fact]
    public async Task ACommittedIdleReturnDoesNotSetOffOnceItsVehicleIsNoLongerCommissioned()
    {
        await using FleetFixture fleet = await FleetAsync();
        await RoundAsync(fleet);
        Assert.NotNull(await ClaimOfAsync(fleet, KeyA));
        IChargingPolicyResolver approved = fleet.ChargingPolicy;
        fleet.ChargingPolicy = TestChargingPolicies.None;
        await fleet.RecreateEngineAsync();

        await RoundAsync(fleet);
        await RoundAsync(fleet);

        Assert.Empty(fleet.Riot.Creates);
        Assert.Equal(IdleReturnExecutionReasons.DepartureNotProven, (await IdleJourneyAsync(fleet, AgvA))!.BlockReasonCode);

        fleet.ChargingPolicy = approved;
        await fleet.RecreateEngineAsync();
        await RoundAsync(fleet);
        Assert.Single(fleet.Riot.Creates);
    }

    /// <summary>
    /// 审查 L3：承诺每一轮都物化不了，每轮一条 Warning（2222）之外，连续到第 <see cref="IdleReturnMaterializationFailures.EscalateAfter"/>
    /// 轮升级一条 Error（2228），只一次；之前没有。
    /// </summary>
    [Fact]
    public async Task AMaterializationThatKeepsFailingIsEscalatedOnceAfterItsRoundLimit()
    {
        FailingInsert crash = new("INSERT INTO \"JourneyRuntimes\"");
        await using FleetFixture fleet = await FleetAsync(commands: crash);
        await RoundAsync(fleet);
        Assert.NotNull(await ClaimOfAsync(fleet, KeyA));

        int EscalationsLogged() => fleet.EngineLog.Entries.Count(entry => entry.EventId.Id == 2228);
        for (int round = 1; round <= IdleReturnMaterializationFailures.EscalateAfter + 2; round++)
        {
            crash.Armed = true;
            await RoundAsync(fleet);
            Assert.Equal(round >= IdleReturnMaterializationFailures.EscalateAfter ? 1 : 0, EscalationsLogged());
        }

        Assert.Null(await IdleJourneyAsync(fleet, AgvA));
        var escalation = Assert.Single(fleet.EngineLog.Entries, entry => entry.EventId.Id == 2228);
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Error, escalation.Level);
        Assert.True(fleet.EngineLog.Entries.Count(entry => entry.EventId.Id == 2222) >= IdleReturnMaterializationFailures.EscalateAfter);
    }

    // ---- 急停与故障 ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// 途中单 FAILED：与搬运一样交故障模型（疑似故障、按住、原因码），承诺与独占都不放、不重建；人工清除故障之后按已确认失败收尾——
    /// 用途占有释放、撤下 <c>IDLE_RETURN</c>，不建第二张单。
    /// </summary>
    [Fact]
    public async Task AFailedIdleReturnOrderIsSupervisedLikeATransportLegAndEndsOnlyWhenAPersonClearsTheFault()
    {
        await using FleetFixture fleet = await FleetAsync();
        JourneyRuntimeRow journey = await CommittedAndSentAsync(fleet);
        fleet.Riot.MovementState = "MT_FINISHED";
        fleet.Riot.FailOrder(journey.PickupUpperId);

        await RoundAsync(fleet);
        await RoundAsync(fleet);

        Assert.Equal(VehicleFaultEvidence.OrderFailed, (await IdleJourneyAsync(fleet, AgvA))!.BlockReasonCode);
        Assert.True(await fleet.Context.VehicleFaultStates.AsNoTracking()
            .AnyAsync(row => row.AgvId == AgvA && row.Level != VehicleFaultLevel.None, Token));
        Assert.Equal((VehiclePurposes.IdleReturn, journey.JourneyId), await ClaimOfAsync(fleet, KeyA));
        Assert.Equal(journey.JourneyId, (await StationAsync(fleet, Near.StationId))!.JourneyId);

        fleet.Context.ChangeTracker.Clear();
        VehicleFaultRecoveryDecision decision = await fleet.CreateFaultRecovery().RecoverAsync(
            new VehicleFaultRecoveryRequest(
                new EmergencyStopSubject(AgvA, KeyA), VehicleFaultRecoveryAction.ClearFault, "operator-1", true, null),
            Token);
        fleet.Context.ChangeTracker.Clear();

        Assert.Equal(
            (VehicleFaultRecoveryOutcome.Cleared, VehicleFaultRecoveryDispositions.IdleReturnEnded),
            (decision.Outcome, decision.Disposition));
        JourneyRuntimeRow ended = await fleet.Context.JourneyRuntimes.AsNoTracking()
            .SingleAsync(row => row.JourneyId == journey.JourneyId, Token);
        Assert.Equal((JourneyRuntimeStage.Completed, IdleReturnExecutionReasons.OrderFailed), (ended.Stage, ended.BlockReasonCode));
        Assert.Null(await ClaimOfAsync(fleet, KeyA));
        Assert.Empty(await fleet.Context.OwnOrderRebuilds.AsNoTracking().ToArrayAsync(Token));
        Assert.Equal(
            JsonValueKind.Null,
            (await PayloadsAsync(fleet, AgvA, "VehicleBusinessStateSnapshot")).Last().GetProperty("activePurpose").ValueKind);
        Assert.Single(fleet.Riot.Creates);
    }

    /// <summary>
    /// 途中急停或切手动：RIoT 把单挂起（<c>HANG</c>），车停在半路。旅程保持在途、写 <c>ORDER_HANG</c>，承诺与独占都不放，也不按到点收敛；
    /// RIoT 继续之后照常到点收敛。
    /// </summary>
    [Fact]
    public async Task AnEmergencyStopOnTheWayHoldsTheIdleReturnUntilTheOrderGoesOnAndThenItConverges()
    {
        await using FleetFixture fleet = await FleetAsync();
        JourneyRuntimeRow journey = await CommittedAndSentAsync(fleet);
        fleet.Riot.HangOrder(journey.PickupUpperId);
        MoveTo(fleet, KeyA, 12, speed: 0, procState: "EMERGENCY");

        await RoundAsync(fleet);
        fleet.Clock.Advance(TimeSpan.FromMinutes(30));
        await RoundAsync(fleet);

        Assert.Equal(JourneyRuntimeEngine.OrderHangReason, (await IdleJourneyAsync(fleet, AgvA))!.BlockReasonCode);
        Assert.Equal((VehiclePurposes.IdleReturn, journey.JourneyId), await ClaimOfAsync(fleet, KeyA));
        Assert.Equal(StationExclusivityStates.Reserved, (await StationAsync(fleet, Near.StationId))!.State);

        await ArriveAsync(fleet, journey, KeyA, Near.StationId);

        Assert.Equal(JourneyRuntimeStage.Completed, (await IdleJourneyAsync(fleet, AgvA))!.Stage);
        Assert.Equal(StationExclusivityStates.Occupied, (await StationAsync(fleet, Near.StationId))!.State);
    }

    // ---- 出发前安全门 ----------------------------------------------------------------------------------------------------

    public static TheoryData<string> UnsafeDepartures => ["slot-unlocked", "blocking-fact", "summary-not-locked", "session-not-ready"];

    /// <summary>
    /// 非业务移动的出发前安全门（调度 2026-09-29）：仓门没锁（快照里一个仓位 <c>UNLOCKED</c>）、摘要带阻断事实、摘要说仓位没锁、会话不就绪——
    /// 都不建单、写 <see cref="IdleReturnExecutionReasons.DepartureNotProven"/>，承诺与预占保持；安全状态恢复后下一轮建单，只建一张。
    /// </summary>
    [Theory]
    [MemberData(nameof(UnsafeDepartures))]
    public async Task NoOrderIsCreatedUntilTheDepartureSafetyGateIsMet(string gap)
    {
        await using FleetFixture fleet = await FleetAsync();
        // Committed first: the commitment's own readiness check (shared with dispatch) already refuses a vehicle whose
        // safety summary says it may not depart, and this is about the gate between the commitment and the create.
        await RoundAsync(fleet);
        Assert.NotNull(await ClaimOfAsync(fleet, KeyA));
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
        JourneyRuntimeRow waiting = (await IdleJourneyAsync(fleet, AgvA))!;
        Assert.Equal(IdleReturnExecutionReasons.DepartureNotProven, waiting.BlockReasonCode);
        Assert.Equal((VehiclePurposes.IdleReturn, waiting.JourneyId), await ClaimOfAsync(fleet, KeyA));
        Assert.Equal(StationExclusivityStates.Reserved, (await StationAsync(fleet, Near.StationId))!.State);

        // What the dashboard's blocked-journeys card shows for this journey: the Chinese description, read through the card's own
        // endpoint from the row the engine wrote -- not only "the dictionary has a key" (coordinator, control-server#390).
        object fact = await new BlockedJourneysQueryEndpoint(BlockedJourneyEscalationOptions.Default, fleet.Clock)
            .ReadAsync(fleet.Context, Token);
        using (JsonDocument card = JsonDocument.Parse(JsonSerializer.Serialize(fact)))
        {
            JsonElement row = Assert.Single(
                card.RootElement.GetProperty("journeys").EnumerateArray(),
                item => item.GetProperty("agvId").GetString() == AgvA);
            Assert.Equal(
                (IdleReturnExecutionReasons.DepartureNotProven,
                 BlockedJourneysQueryEndpoint.Descriptions[IdleReturnExecutionReasons.DepartureNotProven]),
                (row.GetProperty("blockReasonCode").GetString(), row.GetProperty("blockReasonDescription").GetString()));
        }

        if (gap == "session-not-ready")
        {
            return;
        }
        await fleet.ReplaceSafetySnapshotAsync(AgvA);
        await RoundAsync(fleet);

        Assert.Single(fleet.Riot.Creates);
        Assert.Null((await IdleJourneyAsync(fleet, AgvA))!.BlockReasonCode);
    }

    /// <summary>
    /// 本票会写进 <c>BlockReasonCode</c> 的每一个码在阻断旅程卡片上都有中文说明（调度 2026-09-30：cs#392 暂不开工，不能让原码裸露）。
    /// 码从两处现取，不抄清单：<see cref="IdleReturnExecutionReasons"/> 的每个字符串常量，与 <see cref="MovementDispatchOutcome"/> 的每个取值
    /// 拼成的建单结果码——以后加一个常量或一个取值而忘了说明，这里就红。不是阻断码的两个常量点名排除，理由写在旁边。
    /// </summary>
    [Fact]
    public void EveryCodeAnIdleReturnWritesOnItsJourneyHasAChineseDescriptionOnTheBlockedJourneysCard()
    {
        string[] notWrittenOnAJourney =
        [
            IdleReturnExecutionReasons.LegName, // a prefix, never a code by itself
            IdleReturnExecutionReasons.ConvergedAtWaitingPoint, // a purpose release reason: a converged journey closes with no code
        ];
        string[] constants =
        [
            .. typeof(IdleReturnExecutionReasons)
                .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                .Where(field => field.IsLiteral && field.FieldType == typeof(string))
                .Select(field => (string)field.GetRawConstantValue()!)
                .Where(code => !notWrittenOnAJourney.Contains(code)),
        ];
        // Confirmed clears the code; TerminalReconciliationRequired is judged as an ended order and writes none (the engine's
        // AdvanceIdleReturnAsync). Every other outcome is written as it is.
        string[] outcomes =
        [
            .. Enum.GetValues<MovementDispatchOutcome>()
                .Where(outcome => outcome is not (MovementDispatchOutcome.Confirmed or MovementDispatchOutcome.TerminalReconciliationRequired))
                .Select(IdleReturnExecutionReasons.LegOutcomeCode),
        ];
        Assert.Contains(IdleReturnExecutionReasons.DepartureNotProven, constants);
        Assert.Contains("WAITING_POINT_ResultUnknown", outcomes);
        Assert.Equal([], [.. constants.Concat(outcomes).Where(code => !BlockedJourneysQueryEndpoint.Descriptions.ContainsKey(code))]);
    }

    // ---- 并发、崩溃点与重连 ------------------------------------------------------------------------------------------------

    /// <summary>
    /// 离点释放与另一辆车对同一个点的预占在同一轮：另一辆只在释放提交之后拿到它——释放在轮次开头，承诺在派车轮末尾；释放之前它的直接承诺被主键拒绝。
    /// </summary>
    [Fact]
    public async Task AnotherVehicleGetsTheWaitingPointOnlyAfterItsDepartureReleaseIsCommitted()
    {
        await using FleetFixture fleet = await FleetAsync(vehicles: 2);
        MoveTo(fleet, KeyB, 210);
        JourneyRuntimeRow journey = await CommittedAndSentAsync(fleet);
        await ArriveAsync(fleet, journey, KeyA, Near.StationId);
        Assert.Equal(IdleReturnReasons.NoWaitingPointAvailable, fleet.IdleReturnBoard.Reasons[AgvB]);

        VehiclePurposeAcquisitionOutcome early = await IdleReturnEvaluator.TryCommitAsync(
            new VehiclePurposeLedgerStore(fleet.Context), KeyB, IdleReturnIdentity.JourneyIdFor(KeyB, fleet.Clock.GetUtcNowWithoutTick()),
            Map, Near.StationId, 1, fleet.Clock.GetUtcNowWithoutTick(), Token);
        fleet.Context.ChangeTracker.Clear();
        Assert.Equal(VehiclePurposeAcquisitionOutcome.StationHeld, early);
        Assert.Null(await ClaimOfAsync(fleet, KeyB));

        // A is sent off on a transport, so it is busy when the point frees up and B is the only idle vehicle left.
        fleet.Catalog.Set([FleetFixture.Demand(0, "N1-1", 0)]);
        await RoundAsync(fleet);
        Assert.Contains(fleet.Riot.Creates, create => create.VehicleKey == KeyA && create.DestinationStationId == 12);
        MoveTo(fleet, KeyA, 12);
        await RoundAsync(fleet);

        StationExclusivityRow taken = (await StationAsync(fleet, Near.StationId))!;
        Assert.Equal((KeyB, StationExclusivityStates.Reserved), (taken.VehicleKey, taken.State));
        StationExclusivityRecordRow[] passages = [.. (await fleet.Context.Set<StationExclusivityRecordRow>().AsNoTracking()
                .Where(row => row.StationId == Near.StationId).ToArrayAsync(Token))
            .OrderBy(row => row.ReservedAt ?? row.OccupiedAt)];
        Assert.Equal([KeyA, KeyB], passages.Select(row => row.VehicleKey));
        Assert.True(passages[0].ReleasedAt <= passages[1].ReservedAt);
    }

    /// <summary>
    /// 崩在承诺之后、物化之前：物化那一次保存失败（注入），旅程行、停靠与意图一行都没留下，承诺还在；「重启」之后下一轮补建恰好一次、只建一张单。
    /// </summary>
    [Fact]
    public async Task ACrashBetweenTheCommitmentAndItsMaterializationIsMadeGoodExactlyOnce()
    {
        FailingInsert crash = new("INSERT INTO \"JourneyRuntimes\"");
        await using FleetFixture fleet = await FleetAsync(commands: crash);
        await RoundAsync(fleet);
        string committed = (await ClaimOfAsync(fleet, KeyA))!.Value.JourneyId;

        crash.Armed = true;
        await RoundAsync(fleet);
        Assert.Equal(1, crash.Fired);
        Assert.Empty(await fleet.Context.JourneyRuntimes.AsNoTracking().ToArrayAsync(Token));
        Assert.Empty(await fleet.Context.OrderIntents.AsNoTracking().ToArrayAsync(Token));
        Assert.Equal(committed, (await ClaimOfAsync(fleet, KeyA))!.Value.JourneyId);

        await fleet.RecreateEngineAsync();
        await RoundAsync(fleet);
        await RoundAsync(fleet);

        JourneyRuntimeRow journey = Assert.Single(await fleet.Context.JourneyRuntimes.AsNoTracking().ToArrayAsync(Token));
        Assert.Equal(committed, journey.JourneyId);
        Assert.Single(await fleet.Context.OrderIntents.AsNoTracking().ToArrayAsync(Token));
        Assert.Single(await fleet.Context.Set<JourneyStopRow>().AsNoTracking().ToArrayAsync(Token));
        Assert.Single(fleet.Riot.Creates);
    }

    /// <summary>
    /// 崩在收敛那一次保存：预占转占用、用途释放、旅程收尾在同一次保存里，注入让它失败——三样都没变（不会留下「用途已放、独占仍是预占」），
    /// 下一轮照常收敛。
    /// </summary>
    [Fact]
    public async Task ACrashInTheConvergingSaveLeavesNeitherThePurposeReleasedNorTheReservationHalfConverted()
    {
        FailingInsert crash = new("DELETE FROM \"VehiclePurposeClaims\"");
        await using FleetFixture fleet = await FleetAsync(commands: crash);
        JourneyRuntimeRow journey = await CommittedAndSentAsync(fleet);
        fleet.Riot.CompleteOrder(journey.PickupUpperId);
        MoveTo(fleet, KeyA, Near.StationId);

        crash.Armed = true;
        await Assert.ThrowsAsync<DbUpdateException>(() => RoundAsync(fleet));
        Assert.Equal(1, crash.Fired);

        Assert.Equal((VehiclePurposes.IdleReturn, journey.JourneyId), await ClaimOfAsync(fleet, KeyA));
        Assert.Equal(StationExclusivityStates.Reserved, (await StationAsync(fleet, Near.StationId))!.State);
        Assert.NotEqual(JourneyRuntimeStage.Completed, (await IdleJourneyAsync(fleet, AgvA))!.Stage);

        await RoundAsync(fleet);

        Assert.Null(await ClaimOfAsync(fleet, KeyA));
        Assert.Equal(StationExclusivityStates.Occupied, (await StationAsync(fleet, Near.StationId))!.State);
        Assert.Equal(JourneyRuntimeStage.Completed, (await IdleJourneyAsync(fleet, AgvA))!.Stage);
    }

    /// <summary>
    /// 车载端断线时单确认了：两张途中快照留在发件箱里；重连之后按原 messageId 补发，发件箱里每样仍只有一行，不产生第二份。
    /// </summary>
    [Fact]
    public async Task AfterAReconnectTheWaitingPointPlanAndIdleReturnAreResentUnderTheirOwnIdsWithoutASecondCopy()
    {
        await using FleetFixture fleet = await FleetAsync();
        fleet.Peer.Unavailable = AgvA;
        JourneyRuntimeRow journey = await CommittedAndSentAsync(fleet);
        Assert.DoesNotContain(fleet.Peer.Delivered, line => line.MessageId == journey.PlanMessageId);

        fleet.Peer.Unavailable = null;
        await fleet.HearFromEveryVehicleAsync();
        await RoundAsync(fleet);

        Assert.Contains(fleet.Peer.Delivered, line => line.MessageId == journey.PlanMessageId);
        Assert.Contains(fleet.Peer.Delivered, line => line.MessageId == journey.VehicleBusinessMessageId);
        Assert.Single(await PayloadsAsync(fleet, AgvA, "UpcomingStopPlanSnapshot"));
        Assert.Single(await PayloadsAsync(fleet, AgvA, "VehicleBusinessStateSnapshot"));
    }

    // ---- 孤儿承诺与等待点被人工释放 ----------------------------------------------------------------------------------------

    /// <summary>
    /// 物化不出旅程的承诺不锁死车（准入线第 3 条）：等待点在两轮之间从登记里删掉，承诺整个释放；本票合入前留下的存量承诺同样被接住——
    /// 点还合格的照常物化执行，点不在了的释放。
    /// </summary>
    [Fact]
    public async Task ACommitmentThatCannotBecomeAJourneyIsReleasedIncludingOnesLeftBeforeThisTicket()
    {
        await using FleetFixture fleet = await FleetAsync(vehicles: 2, points: [Near, Far]);
        DateTimeOffset before = fleet.Clock.GetUtcNowWithoutTick().AddDays(-1);
        string leftA = IdleReturnIdentity.JourneyIdFor(KeyA, before);
        string leftB = IdleReturnIdentity.JourneyIdFor(KeyB, before);
        Assert.Equal(
            VehiclePurposeAcquisitionOutcome.Acquired,
            await IdleReturnEvaluator.TryCommitAsync(new VehiclePurposeLedgerStore(fleet.Context), KeyA, leftA, Map, Near.StationId, 1, before, Token));
        Assert.Equal(
            VehiclePurposeAcquisitionOutcome.Acquired,
            await IdleReturnEvaluator.TryCommitAsync(new VehiclePurposeLedgerStore(fleet.Context), KeyB, leftB, Map, Far.StationId, 1, before, Token));
        fleet.Context.ChangeTracker.Clear();
        await RegisterAsync(fleet, Near);

        await RoundAsync(fleet);

        Assert.Null(await ClaimOfAsync(fleet, KeyB));
        Assert.Null(await StationAsync(fleet, Far.StationId));
        Assert.Equal(
            IdleReturnExecutionReasons.CommitmentOrphaned,
            (await fleet.Context.Set<VehiclePurposeClaimRecordRow>().AsNoTracking().SingleAsync(row => row.JourneyId == leftB, Token))
            .ReleaseReason);
        Assert.Equal(leftA, (await IdleJourneyAsync(fleet, AgvA))!.JourneyId);
        Assert.Contains(fleet.Riot.Creates, create => create.VehicleKey == KeyA && create.DestinationStationId == Near.StationId);
    }

    /// <summary>
    /// 等待点独占在建单之前被人工释放（control-server#419）：承诺作废、用途占有释放、原因码，车不出发。
    /// </summary>
    [Fact]
    public async Task AWaitingPointReleasedByHandBeforeTheCreateVoidsTheCommitmentAndTheVehicleDoesNotSetOff()
    {
        await using FleetFixture fleet = await FleetAsync();
        await fleet.ReplaceSafetySnapshotAsync(AgvA, unlockedSlot: 1);
        await RoundAsync(fleet);
        await RoundAsync(fleet);
        JourneyRuntimeRow journey = (await IdleJourneyAsync(fleet, AgvA))!;
        await ReleaseByHandAsync(fleet, Near.StationId);

        await RoundAsync(fleet);

        JourneyRuntimeRow ended = await fleet.Context.JourneyRuntimes.AsNoTracking()
            .SingleAsync(row => row.JourneyId == journey.JourneyId, Token);
        Assert.Equal((JourneyRuntimeStage.Completed, IdleReturnExecutionReasons.WaitingPointLost), (ended.Stage, ended.BlockReasonCode));
        Assert.Null(await ClaimOfAsync(fleet, KeyA));
        Assert.Empty(fleet.Riot.Creates);
    }

    /// <summary>
    /// 在途时等待点被人工释放、之后被别的车预占：车不得继续开往那里——对这张单发一次取消（只发一次），承诺与用途占有保持到 RIoT 报单终结、车证明停稳，
    /// 然后作废收尾；别的车的预占原样不动。
    /// </summary>
    [Fact]
    public async Task AWaitingPointTakenByAnotherVehicleWhileOnTheWayCancelsTheOrderAndEndsOnceTheVehicleIsStopped()
    {
        await using FleetFixture fleet = await FleetAsync(vehicles: 2);
        MoveTo(fleet, KeyB, 12);
        JourneyRuntimeRow journey = await CommittedAndSentAsync(fleet);
        await ReleaseByHandAsync(fleet, Near.StationId);
        string other = IdleReturnIdentity.JourneyIdFor(KeyB, fleet.Clock.GetUtcNowWithoutTick());
        Assert.Equal(
            VehiclePurposeAcquisitionOutcome.Acquired,
            await IdleReturnEvaluator.TryCommitAsync(
                new VehiclePurposeLedgerStore(fleet.Context), KeyB, other, Map, Near.StationId, 1, fleet.Clock.GetUtcNowWithoutTick(), Token));
        fleet.Context.ChangeTracker.Clear();
        MoveTo(fleet, KeyA, 12, speed: 0.5, procState: "RUNNING");

        await RoundAsync(fleet);
        await RoundAsync(fleet);

        Assert.Single(fleet.Riot.OrderCommands, command => command.CommandType == RiotCommandTypeNames.For(RiotOrderCommandKind.Cancel));
        Assert.Equal((VehiclePurposes.IdleReturn, journey.JourneyId), await ClaimOfAsync(fleet, KeyA));
        Assert.Equal(IdleReturnExecutionReasons.WaitingPointLostOrderInFlight, (await IdleJourneyAsync(fleet, AgvA))!.BlockReasonCode);

        fleet.Riot.CancelOrder(journey.PickupUpperId);
        MoveTo(fleet, KeyA, 12);
        await RoundAsync(fleet);

        JourneyRuntimeRow ended = await fleet.Context.JourneyRuntimes.AsNoTracking()
            .SingleAsync(row => row.JourneyId == journey.JourneyId, Token);
        Assert.Equal((JourneyRuntimeStage.Completed, IdleReturnExecutionReasons.WaitingPointLost), (ended.Stage, ended.BlockReasonCode));
        Assert.NotEqual(journey.JourneyId, (await ClaimOfAsync(fleet, KeyA))?.JourneyId);
        Assert.Equal(other, (await StationAsync(fleet, Near.StationId))!.JourneyId);
    }

    /// <summary>
    /// 到点那一刻等待点独占已不在或属于别的车（control-server#419 之后被别车预占）：不转占用，按承诺作废收尾、释放用途占有、给原因码；别的车的那一行不动。
    /// </summary>
    [Fact]
    public async Task AtArrivalAWaitingPointNowHeldByAnotherVehicleIsNotTakenOverAndTheCommitmentIsVoided()
    {
        await using FleetFixture fleet = await FleetAsync(vehicles: 2);
        MoveTo(fleet, KeyB, 12);
        JourneyRuntimeRow journey = await CommittedAndSentAsync(fleet);
        await ReleaseByHandAsync(fleet, Near.StationId);
        string other = IdleReturnIdentity.JourneyIdFor(KeyB, fleet.Clock.GetUtcNowWithoutTick());
        await IdleReturnEvaluator.TryCommitAsync(
            new VehiclePurposeLedgerStore(fleet.Context), KeyB, other, Map, Near.StationId, 1, fleet.Clock.GetUtcNowWithoutTick(), Token);
        fleet.Context.ChangeTracker.Clear();
        fleet.Riot.CompleteOrder(journey.PickupUpperId);
        MoveTo(fleet, KeyA, Near.StationId);

        await RoundAsync(fleet);

        JourneyRuntimeRow ended = await fleet.Context.JourneyRuntimes.AsNoTracking()
            .SingleAsync(row => row.JourneyId == journey.JourneyId, Token);
        Assert.Equal(
            (JourneyRuntimeStage.Completed, IdleReturnExecutionReasons.WaitingPointLostAtArrival), (ended.Stage, ended.BlockReasonCode));
        StationExclusivityRow held = (await StationAsync(fleet, Near.StationId))!;
        Assert.Equal((other, StationExclusivityStates.Reserved), (held.JourneyId, held.State));
    }

    // ---- 协议向量的同名具名测试 -------------------------------------------------------------------------------------------

    /// <summary>
    /// <c>CV-WAITING-POINT-IDLE-RETURN</c> 的 <c>CLAIM_WAITING_POINT_EXCLUSIVELY</c>：两辆空闲车、一个等待点，真实的承诺、建单、到点——
    /// 一辆车从预占到占用连续独占它，另一辆在整个过程中既拿不到它、也没有为它建单。
    /// </summary>
    [Fact]
    [Trait("ProtocolVector", Vector)]
    public async Task CvWaitingPointIdleReturnClaimsTheWaitingPointExclusively()
    {
        await using FleetFixture fleet = await FleetAsync(vehicles: 2);
        MoveTo(fleet, KeyB, 12);
        JourneyRuntimeRow journey = await CommittedAndSentAsync(fleet);
        Assert.Equal(StationExclusivityStates.Reserved, (await StationAsync(fleet, Near.StationId))!.State);
        Assert.Null(await ClaimOfAsync(fleet, KeyB));

        await ArriveAsync(fleet, journey, KeyA, Near.StationId);
        await RoundAsync(fleet);

        StationExclusivityRow occupied = (await StationAsync(fleet, Near.StationId))!;
        Assert.Equal((KeyA, StationExclusivityStates.Occupied), (occupied.VehicleKey, occupied.State));
        Assert.Single(
            await fleet.Context.Set<StationExclusivityRecordRow>().AsNoTracking()
                .Where(row => row.StationId == Near.StationId).ToArrayAsync(Token));
        Assert.All(fleet.Riot.Creates, create => Assert.Equal(KeyA, create.VehicleKey));
        Assert.Equal(IdleReturnReasons.NoWaitingPointAvailable, fleet.IdleReturnBoard.Reasons[AgvB]);
    }

    /// <summary>
    /// <c>CV-WAITING-POINT-IDLE-RETURN</c> 的 <c>RELEASE_ON_DEPARTURE_EVIDENCE</c>：收敛在点上的车被派走，离点订单下达时占用仍在，车到了别的站
    /// 才释放，原因是离点证据；释放之后另一辆空闲车才承诺到这个点。
    /// </summary>
    [Fact]
    [Trait("ProtocolVector", Vector)]
    public async Task CvWaitingPointIdleReturnReleasesTheWaitingPointOnDepartureEvidence()
    {
        await using FleetFixture fleet = await FleetAsync(vehicles: 2);
        MoveTo(fleet, KeyB, 210);
        JourneyRuntimeRow journey = await CommittedAndSentAsync(fleet);
        await ArriveAsync(fleet, journey, KeyA, Near.StationId);

        fleet.Catalog.Set([FleetFixture.Demand(0, "N1-1", 0)]);
        await RoundAsync(fleet);
        Assert.Contains(fleet.Riot.Creates, create => create.VehicleKey == KeyA && create.DestinationStationId == 12);
        Assert.Equal((KeyA, StationExclusivityStates.Occupied), await HolderAsync(fleet, Near.StationId));
        await RoundAsync(fleet);
        Assert.Equal((KeyA, StationExclusivityStates.Occupied), await HolderAsync(fleet, Near.StationId));

        MoveTo(fleet, KeyA, 12);
        await RoundAsync(fleet);

        StationExclusivityRecordRow first = await fleet.Context.Set<StationExclusivityRecordRow>().AsNoTracking()
            .SingleAsync(row => row.JourneyId == journey.JourneyId, Token);
        Assert.Equal(FixedStationExclusivity.ReleasedOnDepartureEvidence, first.ReleaseReason);
        Assert.Equal((KeyB, StationExclusivityStates.Reserved), await HolderAsync(fleet, Near.StationId));
    }

    // ---- 公共站点持有车 ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// 持 <c>FIXED_TASK_STATION</c> 的车（卸完货、没有下一单，停在单车位的公共站上）被承诺空闲返回：出发之前那一行不放，车开离那个站之后
    /// 由同一个离点清扫凭离点证据放。
    /// </summary>
    [Fact]
    public async Task AVehicleHoldingAFixedTaskStationLeavesItOnDepartureEvidenceWhenItReturnsIdle()
    {
        await using FleetFixture fleet = await FleetAsync();
        MoveTo(fleet, KeyA, 210);
        const string gateJourney = "journey:finished-at-the-gate";
        await FixedStationExclusivity.StageReserveAsync(fleet.Context, Map, 210, KeyA, gateJourney, fleet.Clock.GetUtcNowWithoutTick(), Token);
        await fleet.Context.SaveChangesAsync(Token);
        fleet.Context.ChangeTracker.Clear();

        await RoundAsync(fleet);
        await RoundAsync(fleet);
        Assert.NotNull(await IdleJourneyAsync(fleet, AgvA));
        Assert.Equal(KeyA, (await StationAsync(fleet, 210))!.VehicleKey);

        MoveTo(fleet, KeyA, 13, speed: 0.5, procState: "RUNNING");
        await RoundAsync(fleet);

        Assert.Null(await StationAsync(fleet, 210));
        Assert.Equal(StationExclusivityStates.Reserved, (await StationAsync(fleet, Near.StationId))!.State);
    }

    // ---- 夹具 ------------------------------------------------------------------------------------------------------------

    private static RouteGraphEdgeFact[] Edges() =>
    [
        new(1, 1, 2, 10000, 0, 0, 10000, 0, 1, false),
        new(2, 2, 3, 10000, 10000, 0, 20000, 0, 1, false),
        new(3, 3, 4, 10000, 20000, 0, 30000, 0, 1, false),
        new(4, 4, 5, 10000, 30000, 0, 40000, 0, 1, false),
        new(5, 1, 6, 5000, 0, 0, 0, 5000, 1, false),
        new(6, 6, 1, 5000, 0, 5000, 0, 0, 1, false),
        new(7, 1, 7, 8000, 0, 0, 0, -8000, 1, false),
        new(8, 7, 1, 8000, 0, -8000, 0, 0, 1, false),
        new(9, 2, 1, 10000, 10000, 0, 0, 0, 1, false),
        new(10, 3, 2, 10000, 20000, 0, 10000, 0, 1, false),
        new(11, 2, 6, 11000, 10000, 0, 0, 5000, 1, false),
        new(12, 2, 7, 13000, 10000, 0, 0, -8000, 1, false),
        new(13, 4, 3, 10000, 30000, 0, 20000, 0, 1, false),
        new(14, 4, 6, 7000, 30000, 0, 0, 5000, 1, false),
    ];

    private static RouteGraphStationFact[] Stations() =>
    [
        new(Home, "等待点", 1, 0, 0, 1, 0),
        new(12, "N1-1", 1, 10000, 0, 2, 0),
        new(13, "N1-2_N1-3", 2, 20000, 0, 3, 0),
        new(210, "关卡", 3, 30000, 0, 4, 0),
        new(Near.StationId, Near.StationName, 5, 0, 5000, 6, 0),
        new(Far.StationId, Far.StationName, 7, 0, -8000, 7, 0),
    ];

    /// <summary>
    /// 一队车（默认一辆），路网换成带两个等待点的那张，登记给定的等待点并打开空闲返回，目录清空——没有搬运，才轮到空闲返回。
    /// </summary>
    private static async Task<FleetFixture> FleetAsync(
        int vehicles = 1,
        WaitingPointEntry[]? points = null,
        DbCommandInterceptor? commands = null)
    {
        FleetFixture fleet = await FleetFixture.CreateAsync(
            configure: options => options.Fleet = options.Fleet[..vehicles], withRouteGraph: true, commands: commands);
        await fleet.ReplaceRouteGraphAsync(Edges(), Stations());
        await fleet.EnableIdleReturnAsync(points ?? [Near]);
        fleet.Catalog.Set([]);
        return fleet;
    }

    private static async Task RegisterAsync(FleetFixture fleet, params WaitingPointEntry[] points)
    {
        await new WaitingPointRegistry(fleet.Context, JourneyRuntimeWorkerTestKit.CreateGovernedPublisher(fleet.Context))
            .WriteVersionAsync(points, fleet.Clock.GetUtcNowWithoutTick(), Token);
        fleet.Context.ChangeTracker.Clear();
    }

    /// <summary>一轮，时钟先走一秒；每辆车先说一句话（心跳），会话才算活着。</summary>
    private static async Task RoundAsync(FleetFixture fleet)
    {
        await fleet.HearFromEveryVehicleAsync();
        await fleet.RunRoundAsync(TimeSpan.FromSeconds(1));
    }

    /// <summary>第一轮承诺，第二轮物化、过安全门、建单并确认：答那一趟空闲返回的旅程行。</summary>
    private static async Task<JourneyRuntimeRow> CommittedAndSentAsync(FleetFixture fleet)
    {
        await RoundAsync(fleet);
        await RoundAsync(fleet);
        JourneyRuntimeRow journey = (await IdleJourneyAsync(fleet, AgvA))!;
        Assert.NotNull(journey);
        Assert.Equal("CONFIRMED", (await fleet.Context.OrderIntents.AsNoTracking()
            .SingleAsync(row => row.UpperId == journey.PickupUpperId, Token)).Status);
        return journey;
    }

    /// <summary>RIoT 报单成功、车停在点上，跑一轮。</summary>
    private static async Task ArriveAsync(FleetFixture fleet, JourneyRuntimeRow journey, string vehicleKey, int stationId)
    {
        fleet.Riot.CompleteOrder(journey.PickupUpperId);
        MoveTo(fleet, vehicleKey, stationId);
        await RoundAsync(fleet);
    }

    private static void MoveTo(
        FleetFixture fleet, string vehicleKey, int stationId, double speed = 0, string procState = "IDLE") =>
        fleet.Riot.VehicleOverrides[vehicleKey] = seen => seen with
        {
            CurrentStationId = stationId, Speed = speed, ProcState = procState,
        };

    private static async Task<JourneyRuntimeRow?> IdleJourneyAsync(FleetFixture fleet, string agvId)
    {
        JourneyRuntimeRow[] rows = await fleet.Context.JourneyRuntimes.AsNoTracking()
            .Where(row => row.AgvId == agvId && row.JourneyId.StartsWith(IdleReturnIdentity.JourneyIdPrefix))
            .ToArrayAsync(Token);
        return rows.OrderByDescending(row => row.CreatedAt).FirstOrDefault();
    }

    private static async Task<(string Purpose, string JourneyId)?> ClaimOfAsync(FleetFixture fleet, string vehicleKey)
    {
        VehiclePurposeClaimRow? claim = await fleet.Context.Set<VehiclePurposeClaimRow>().AsNoTracking()
            .SingleOrDefaultAsync(row => row.VehicleKey == vehicleKey, Token);
        return claim is null ? null : (claim.Purpose, claim.JourneyId);
    }

    private static Task<StationExclusivityRow?> StationAsync(FleetFixture fleet, int stationId) =>
        fleet.Context.Set<StationExclusivityRow>().AsNoTracking()
            .SingleOrDefaultAsync(row => row.MapId == Map && row.StationId == stationId, Token);

    private static async Task<(string VehicleKey, string State)?> HolderAsync(FleetFixture fleet, int stationId) =>
        await StationAsync(fleet, stationId) is { } row ? (row.VehicleKey, row.State) : null;

    /// <summary>control-server#419 的人工释放那一次删行：删掉站点独占，关它的经过；不动用途占有。</summary>
    private static async Task ReleaseByHandAsync(FleetFixture fleet, int stationId)
    {
        StationExclusivityRow row = (await StationAsync(fleet, stationId))!;
        Assert.True(await FixedStationExclusivity.ReleaseAsReadAsync(
            fleet.Context, row, fleet.Clock.GetUtcNowWithoutTick(), "RELEASED_BY_HAND", Token));
        fleet.Context.ChangeTracker.Clear();
    }

    /// <summary>这辆车发件箱里这一类快照的载荷，按修订号排。</summary>
    private static async Task<JsonElement[]> PayloadsAsync(FleetFixture fleet, string agvId, string messageType)
    {
        string[] rows = await fleet.Context.ProtocolOutbox.AsNoTracking()
            .Where(row => row.MessageType == messageType)
            .Select(row => row.PayloadJson)
            .ToArrayAsync(Token);
        return
        [
            .. rows.Select(json => JsonDocument.Parse(json).RootElement)
                .Where(root => root.GetProperty("agvId").GetString() == agvId)
                .Select(root => root.GetProperty("payload").Clone())
                .OrderBy(payload => payload.TryGetProperty("planRevision", out JsonElement plan) ? plan.GetInt64()
                    : payload.TryGetProperty("vehicleBusinessStateRevision", out JsonElement state) ? state.GetInt64()
                    : 0)
        ];
    }

    /// <summary>
    /// 让下一条以 <paramref name="prefix"/> 开头的写命令失败一次（<see cref="Armed"/> 之后），像进程在那次保存里崩掉：整次保存回滚。
    /// </summary>
    private sealed class FailingInsert(string prefix) : DbCommandInterceptor
    {
        public bool Armed { get; set; }

        public int Fired { get; private set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Trip(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Trip(command);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void Trip(DbCommand command)
        {
            if (Armed && command.CommandText.Contains(prefix, StringComparison.Ordinal))
            {
                Armed = false;
                Fired++;
                throw new InvalidOperationException($"Injected crash at: {prefix}");
            }
        }
    }
}
