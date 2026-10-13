using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.ForeignOrders;
using ControlServer.Host.Runtime.IdleReturn;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using static ControlServer.Tests.WaitingPointArrivalSettlementTestKit;
using FleetFixture = ControlServer.Tests.MultiVehicleExecutionTests.FleetFixture;

namespace ControlServer.Tests;

// control-server#611: an idle return whose create went out with its result unknown -- RIoT answered HTTP 200 with business
// code 00002 on 2026-10-11, twice -- and that RIoT then never shows, used to hold the vehicle for ever.
public sealed partial class IdleReturnExecutionTests
{
    /// <summary>
    /// 现场的形态（cs#566 续场二 E 段）：建单回 HTTP 200、业务码 <c>00002</c>，网关判结果未知；之后每次对账都是真实 RIoT 的「查无此单」
    /// （HTTP 200、业务码 0、不带 result）。修前旅程停在 <c>WAITING_POINT_ResultUnknown</c> 永远不动。现在：不到 120 秒保持；中间读到别的重新计时；
    /// 车在动、名下有别的未完成单都保持；全部成立时放弃、按已确认失败收尾，只建过一张单；冷却过后下一次承诺排除原点、换新单号。
    /// </summary>
    [Fact]
    public async Task ACreateAnsweredWithBusinessCode00002IsGivenUpOnceRiotSaidNoSuchOrderLongEnoughWithTheVehicleProvenIdle()
    {
        await using FleetFixture fleet = await FleetAsync(points: [Near, Far]);
        Assert.Equal(TimeSpan.FromSeconds(120), fleet.Options.ChargingOrderAbsentAbandonAfter);
        JourneyRuntimeRow journey = await CreateFailedAsync(fleet, BusinessFailure00002);
        // The double answers what real RIoT answers, through the gateway -- not the 404 only the doubles ever gave.
        RiotOrderObservation answer = await fleet.Riot.ReconcileByUpperIdAsync(journey.PickupUpperId, Token);
        Assert.True(answer.Kind == RiotOrderObservationKind.Unknown && answer.IsExactAbsentAtObservation(journey.PickupUpperId));

        // 100 s of "no such order": kept.
        await fleet.HearFromEveryVehicleAsync();
        await fleet.RunRoundAsync(TimeSpan.FromSeconds(100));
        await AssertStillHeldAsync(fleet, journey);

        // One answer that is not exactly "no such order" starts the count again.
        RiotOrderObservation real = fleet.Riot.RealRiotAbsent(journey.PickupUpperId);
        fleet.Riot.PutOrder(real with { Receipt = real.Receipt! with { FailureCategory = "TRANSPORT_FAILURE" } });
        await RoundAsync(fleet);
        fleet.Riot.PutOrder(fleet.Riot.RealRiotAbsent(journey.PickupUpperId));
        await RoundAsync(fleet);
        await fleet.HearFromEveryVehicleAsync();
        await fleet.RunRoundAsync(TimeSpan.FromSeconds(100));
        await AssertStillHeldAsync(fleet, journey);

        // Long enough now, but the vehicle is not proven stopped: kept.
        Override(fleet, seen => seen with { Speed = 0.3, ProcState = "RUNNING" });
        await fleet.HearFromEveryVehicleAsync();
        await fleet.RunRoundAsync(TimeSpan.FromSeconds(30));
        await AssertStillHeldAsync(fleet, journey);
        // Stopped, but RIoT lists an unfinished order for it under another number: kept.
        fleet.Riot.VehicleOverrides.Remove(KeyA);
        fleet.Riot.ForeignOrders.Add((new RiotListedOrder("F-9", "SOMEONE-ELSES", RiotOrderState.Queueing, KeyA, null), 12));
        await RoundAsync(fleet);
        await AssertStillHeldAsync(fleet, journey);
        Assert.DoesNotContain(fleet.EngineLog.Entries, entry => entry.EventId.Id == 2232);

        fleet.Riot.ForeignOrders.Clear();
        await RoundAsync(fleet);

        JourneyRuntimeRow ended = await fleet.Context.JourneyRuntimes.AsNoTracking()
            .SingleAsync(row => row.JourneyId == journey.JourneyId, Token);
        Assert.Equal(
            (JourneyRuntimeStage.Completed, IdleReturnExecutionReasons.OrderNeverAppeared), (ended.Stage, ended.BlockReasonCode));
        Assert.Null(await ClaimOfAsync(fleet, KeyA));
        VehiclePurposeClaimRecordRow record = await fleet.Context.Set<VehiclePurposeClaimRecordRow>().AsNoTracking()
            .SingleAsync(row => row.JourneyId == journey.JourneyId, Token);
        Assert.Equal(IdleReturnExecutionReasons.OrderNeverAppeared, record.ReleaseReason);
        var alarm = Assert.Single(fleet.EngineLog.Entries, entry => entry.EventId.Id == 2232);
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Warning, alarm.Level);
        Assert.Contains(journey.PickupUpperId, alarm.Message, StringComparison.Ordinal);
        Assert.Single(fleet.Riot.Creates);
        Assert.Empty(fleet.Riot.OrderCommands);
        Assert.Empty(await fleet.Context.OwnOrderRebuilds.AsNoTracking().ToArrayAsync(Token));
        // The vehicle never left its spot: whatever the onboard was last told carries no waiting point leg and no IDLE_RETURN.
        JsonElement[] plans = await PayloadsAsync(fleet, AgvA, "UpcomingStopPlanSnapshot");
        Assert.True(plans.Length == 0 || !plans.Last().GetProperty("legs").EnumerateArray().Any());
        JsonElement[] states = await PayloadsAsync(fleet, AgvA, "VehicleBusinessStateSnapshot");
        Assert.True(states.Length == 0 || states.Last().GetProperty("activePurpose").ValueKind == JsonValueKind.Null);

        // A confirmed failure like a cancellation: the reservation goes on departure evidence, then the cooldown, then the
        // next commitment leaves the failed point out and carries a new upperId. The old one is never sent again.
        await RoundAsync(fleet);
        Assert.Null(await StationAsync(fleet, Near.StationId));
        Assert.Equal(IdleReturnReasons.CooldownAfterEndedOrder, fleet.IdleReturnBoard.Reasons[AgvA]);
        await fleet.HearFromEveryVehicleAsync();
        await fleet.RunRoundAsync(fleet.Options.OwnOrderRebuildDelay);
        await RoundAsync(fleet);
        JourneyRuntimeRow next = (await IdleJourneyAsync(fleet, AgvA))!;
        Assert.NotEqual(journey.JourneyId, next.JourneyId);
        Assert.Equal(Far.StationId, next.PickupStationRiotId);
        Assert.Equal(2, fleet.Riot.Creates.Count);
        Assert.Equal(1, fleet.Riot.Creates.Count(create => create.UpperId == journey.PickupUpperId));
    }

    /// <summary>
    /// 原因（cs#616 的 upperId 超长）一直在时，每一次建单都回 <c>00002</c>：放弃一次、冷却、换点再放弃一次，窗口内第二次就停止自动空闲返回，
    /// 不会每两分钟循环一次。车照样能接搬运。
    /// </summary>
    [Fact]
    public async Task AFailureThatRepeatsStopsAutomaticIdleReturnsAfterTheSecondNeverAppearedOrder()
    {
        await using FleetFixture fleet = await FleetAsync(points: [Near, Far]);
        fleet.Riot.AnswersAbsentAsRealRiot = true;
        fleet.Riot.CreateAnswer = BusinessFailure00002;
        for (int lap = 0; lap < 2; lap++)
        {
            await RoundAsync(fleet);
            await RoundAsync(fleet);
            await fleet.HearFromEveryVehicleAsync();
            await fleet.RunRoundAsync(fleet.Options.ChargingOrderAbsentAbandonAfter);
            await RoundAsync(fleet);
            Assert.Equal(IdleReturnExecutionReasons.OrderNeverAppeared, (await IdleJourneyAsync(fleet, AgvA))!.BlockReasonCode);
            await RoundAsync(fleet);
            await fleet.HearFromEveryVehicleAsync();
            await fleet.RunRoundAsync(fleet.Options.OwnOrderRebuildDelay);
        }

        await fleet.HearFromEveryVehicleAsync();
        await fleet.RunRoundAsync(fleet.Options.OwnOrderRebuildRepeatWindow + TimeSpan.FromMinutes(1));
        await RoundAsync(fleet);
        Assert.Null(await ClaimOfAsync(fleet, KeyA));
        Assert.Equal(IdleReturnReasons.StoppedAfterRepeatedEndedOrders, fleet.IdleReturnBoard.Reasons[AgvA]);
        Assert.Equal([Near.StationId, Far.StationId], [.. fleet.Riot.Creates.Select(create => create.DestinationStationId)]);

        fleet.Riot.CreateAnswer = null;
        fleet.Catalog.Set([FleetFixture.Demand(0, "N1-1", 0)]);
        await RoundAsync(fleet);
        Assert.Contains(fleet.Riot.Creates, create => create.VehicleKey == KeyA && create.DestinationStationId == 12);
    }

    /// <summary>
    /// 超时（应答丢了）同样先进「结果未知」、不当场判失败；窗口之内 RIoT 读到那张单在，就按同一个单号确认下去，之后不会因为等久了被放弃。
    /// </summary>
    [Fact]
    public async Task ATimedOutCreateStaysUnknownAndAnOrderThatAppearsWithinTheWindowIsTakenUpNotGivenUp()
    {
        await using FleetFixture fleet = await FleetAsync();
        JourneyRuntimeRow journey = await CreateFailedAsync(fleet, Timeout);

        await fleet.HearFromEveryVehicleAsync();
        await fleet.RunRoundAsync(TimeSpan.FromSeconds(90));
        await AssertStillHeldAsync(fleet, journey);

        fleet.Riot.PutOrder(new RiotOrderObservation(
            journey.PickupUpperId, RiotOrderObservationKind.Active, "ORDER-LATE", RiotOrderState.Executing, KeyA, Map, Near.StationId));
        await RoundAsync(fleet);
        OrderIntentRow intent = await fleet.Context.OrderIntents.AsNoTracking().SingleAsync(Token);
        Assert.Equal(("CONFIRMED", "ORDER-LATE"), (intent.Status, intent.OrderId));

        // Past the window, the order running: the idle return goes on, nothing is given up.
        await fleet.HearFromEveryVehicleAsync();
        await fleet.RunRoundAsync(fleet.Options.ChargingOrderAbsentAbandonAfter + TimeSpan.FromSeconds(30));
        await RoundAsync(fleet);
        JourneyRuntimeRow running = (await IdleJourneyAsync(fleet, AgvA))!;
        Assert.Equal((JourneyRuntimeStage.AwaitingPickupArrival, null), (running.Stage, running.BlockReasonCode));
        Assert.Equal((VehiclePurposes.IdleReturn, journey.JourneyId), await ClaimOfAsync(fleet, KeyA));
        Assert.Single(fleet.Riot.Creates);
        Assert.DoesNotContain(fleet.EngineLog.Entries, entry => entry.EventId.Id == 2232);
    }

    /// <summary>
    /// 点被人工释放时建单结果未知（审查 S1 那条路）：原来保持 <c>IDLE_RETURN_WAITING_POINT_LOST_ORDER_IN_FLIGHT</c> 直到改库。现在同一组证据成立后
    /// 同样放弃；不建第二张单。
    /// </summary>
    [Fact]
    public async Task AWaitingPointLostWhileTheCreateResultIsUnknownIsGivenUpOnTheSameEvidence()
    {
        await using FleetFixture fleet = await FleetAsync(points: [Near, Far]);
        JourneyRuntimeRow journey = await CreateFailedAsync(fleet, BusinessFailure00002);
        await ReleaseByHandAsync(fleet, Near.StationId);
        await RoundAsync(fleet);
        Assert.Equal(
            IdleReturnExecutionReasons.WaitingPointLostOrderInFlight, (await IdleJourneyAsync(fleet, AgvA))!.BlockReasonCode);

        await fleet.HearFromEveryVehicleAsync();
        await fleet.RunRoundAsync(fleet.Options.ChargingOrderAbsentAbandonAfter);
        await RoundAsync(fleet);

        JourneyRuntimeRow ended = await fleet.Context.JourneyRuntimes.AsNoTracking()
            .SingleAsync(row => row.JourneyId == journey.JourneyId, Token);
        Assert.Equal(
            (JourneyRuntimeStage.Completed, IdleReturnExecutionReasons.OrderNeverAppeared), (ended.Stage, ended.BlockReasonCode));
        Assert.Null(await ClaimOfAsync(fleet, KeyA));
        Assert.Single(fleet.Riot.Creates);
    }

    /// <summary>
    /// 被放弃的那张单事后才在 RIoT 上跑到这辆车上：那趟空闲返回已经收尾，没人看着它。外来单监督器认作「归属证明不了」——挡住这辆车不接新活、
    /// 告警交人（事件 2201）——不取消：<c>REQ-0148</c> 情形二只授权取消被放弃的充电单。放弃之前同一张单在跑，是本服务端自己的在途单，监督器不碰。
    /// </summary>
    [Fact]
    public async Task AnAbandonedIdleReturnOrderThatTurnsUpRunningHoldsTheVehicleForAPersonAndIsNeverCancelled()
    {
        await using FleetFixture fleet = await FleetAsync();
        EventRecordingLogger<ForeignRunningOrderSupervisor> log = new();
        JourneyRuntimeRow journey = await CreateFailedAsync(fleet, BusinessFailure00002);
        RiotOrderObservation running = new(
            journey.PickupUpperId, RiotOrderObservationKind.Active, "ORDER-LATE", RiotOrderState.Executing, KeyA, Map, Near.StationId);

        // While the idle return is held, the order running would be this server's own: left alone.
        fleet.Riot.PutOrder(running);
        await SupervisorOf(fleet, log).SuperviseAsync(Token);
        fleet.Context.ChangeTracker.Clear();
        Assert.Empty(await fleet.Context.ForeignRiotOrders.AsNoTracking().ToArrayAsync(Token));

        fleet.Riot.PutOrder(fleet.Riot.RealRiotAbsent(journey.PickupUpperId));
        await RoundAsync(fleet);
        await fleet.HearFromEveryVehicleAsync();
        await fleet.RunRoundAsync(fleet.Options.ChargingOrderAbsentAbandonAfter);
        await RoundAsync(fleet);
        Assert.Equal(IdleReturnExecutionReasons.OrderNeverAppeared, (await IdleJourneyAsync(fleet, AgvA))!.BlockReasonCode);

        fleet.Riot.PutOrder(running);
        fleet.Context.ChangeTracker.Clear();
        await SupervisorOf(fleet, log).SuperviseAsync(Token);
        fleet.Context.ChangeTracker.Clear();
        await SupervisorOf(fleet, log).SuperviseAsync(Token);
        fleet.Context.ChangeTracker.Clear();

        ForeignRiotOrderRow row = await fleet.Context.ForeignRiotOrders.AsNoTracking().SingleAsync(Token);
        Assert.Equal(
            ("ORDER-LATE", journey.PickupUpperId, AgvA, ForeignRiotOrderOwnership.Unproven, AbandonedIdleReturnOrders.OwnershipBasis),
            (row.RiotOrderId, row.UpperId, row.AgvId, row.Ownership, row.OwnershipBasis));
        Assert.Empty(fleet.Riot.OrderCommands);
        Assert.Contains(AgvA, await ForeignRunningOrders.HeldAgvIdsAsync(fleet.Context, Token));
        Assert.DoesNotContain(log.Entries, entry => entry.EventId.Id == 2181);
        Assert.Contains(log.Entries, entry => entry.EventId.Id == 2201 && entry.Message.Contains("ORDER-LATE", StringComparison.Ordinal));
    }

    // ---- 人工出口 ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// 自动放弃凑不齐证据时（这里：每一轮都读不全未完成订单清单）旅程一直保持；过了重复窗口，R-11 经等待点到点人工收尾入口说车不在点上，
    /// 服务端核过 RIoT 精确查无此单、车停稳没单之后按已确认失败收尾，写成功的管理员审计；不建第二张单。
    /// </summary>
    [Fact]
    public async Task WhenTheEvidenceNeverCompletesAPersonClosesTheUnknownCreateThroughTheArrivalSettlement()
    {
        await using FleetFixture fleet = await FleetAsync();
        JourneyRuntimeRow journey = await CreateFailedAsync(fleet, BusinessFailure00002);
        fleet.Riot.ForeignOrders.Add((new RiotListedOrder("F-9", "SOMEONE-ELSES", RiotOrderState.Queueing, KeyA, null), 12));
        await fleet.HearFromEveryVehicleAsync();
        await fleet.RunRoundAsync(fleet.Options.OwnOrderRebuildRepeatWindow + TimeSpan.FromSeconds(1));
        await RoundAsync(fleet);
        await AssertStillHeldAsync(fleet, journey);

        WaitingPointArrivalSettlementResult result = await SettleAsync(
            fleet, SettlementRequest(AgvA, KeyA, journey.JourneyId, Near.StationId, WaitingPointArrivalVerdicts.NotAtWaitingPoint));

        Assert.Equal((true, IdleReturnExecutionReasons.NeverAppearedByOperator), (result.Settled, result.Ending));
        JourneyRuntimeRow closed = await fleet.Context.JourneyRuntimes.AsNoTracking()
            .SingleAsync(row => row.JourneyId == journey.JourneyId, Token);
        Assert.Equal(
            (JourneyRuntimeStage.Completed, IdleReturnExecutionReasons.NeverAppearedByOperator), (closed.Stage, closed.BlockReasonCode));
        Assert.Null(await ClaimOfAsync(fleet, KeyA));
        Assert.Equal((KeyA, StationExclusivityStates.Reserved), await HolderAsync(fleet, Near.StationId));
        AdministratorAuditRecordRow audit = Assert.Single(await AuditsAsync(fleet));
        Assert.Equal(GovernanceActionOutcome.Succeeded, audit.Outcome);
        using (JsonDocument detail = JsonDocument.Parse(audit.DetailJson!))
        {
            Assert.True(detail.RootElement.GetProperty("riot").GetProperty("ExactAbsent").GetBoolean());
        }
        Assert.Single(fleet.Riot.Creates);
    }

    /// <summary>
    /// 第二次复现那样：人在 RIoT 上把车手动开到了等待点。说车在点上：按到点收尾，预占转占用、用途释放，车回到可派。
    /// </summary>
    [Fact]
    public async Task AVehicleDrivenToThePointByHandIsSettledAtTheWaitingPoint()
    {
        await using FleetFixture fleet = await FleetAsync();
        JourneyRuntimeRow journey = await CreateFailedAsync(fleet, BusinessFailure00002);
        MoveTo(fleet, KeyA, Near.StationId);
        fleet.Clock.Advance(fleet.Options.OwnOrderRebuildRepeatWindow + TimeSpan.FromSeconds(1));

        WaitingPointArrivalSettlementResult result = await SettleAsync(
            fleet, SettlementRequest(AgvA, KeyA, journey.JourneyId, Near.StationId, WaitingPointArrivalVerdicts.AtWaitingPoint));

        Assert.Equal((true, IdleReturnExecutionReasons.NeverAppearedAtWaitingPointByOperator), (result.Settled, result.Ending));
        Assert.Null(await ClaimOfAsync(fleet, KeyA));
        Assert.Equal((KeyA, StationExclusivityStates.Occupied), await HolderAsync(fleet, Near.StationId));
        Assert.Single(fleet.Riot.Creates);
    }

    public static TheoryData<string, string> NeverAppearedSettlementPremises => new()
    {
        { "order-present", WaitingPointArrivalSettlement.OrderNotAbsent },
        { "order-unclassified", WaitingPointArrivalSettlement.OrderNotAbsent },
        { "intent-has-order-id", WaitingPointArrivalSettlement.OrderNotAbsent },
        { "order-unreadable", WaitingPointArrivalSettlement.OrderUnreadable },
        { "too-early", WaitingPointArrivalSettlement.TooEarly },
        { "vehicle-offline", WaitingPointArrivalSettlement.VehicleOffline },
        { "vehicle-moving", WaitingPointArrivalSettlement.VehicleNotProvenStopped },
    };

    /// <summary>
    /// 结果未知那一种的前提各缺一样：单在 RIoT 上（或回答不是精确的查无此单）、读不到单、没到时长、车离线、车在动——拒绝，答那一条的码，什么也不动。
    /// 这个入口不会让一张其实建成了的单失去看管。
    /// </summary>
    [Theory]
    [MemberData(nameof(NeverAppearedSettlementPremises))]
    public async Task EachUnmetPremiseRefusesTheUnknownCreateSettlementAndChangesNothing(string premise, string code)
    {
        await using FleetFixture fleet = await FleetAsync();
        JourneyRuntimeRow journey = await CreateFailedAsync(fleet, BusinessFailure00002);
        fleet.Clock.Advance(fleet.Options.OwnOrderRebuildRepeatWindow + TimeSpan.FromSeconds(1));
        switch (premise)
        {
            case "order-present":
                fleet.Riot.PutOrder(new RiotOrderObservation(
                    journey.PickupUpperId, RiotOrderObservationKind.Active, "ORDER-LATE", RiotOrderState.Queueing, KeyA, Map,
                    Near.StationId));
                break;
            case "order-unclassified":
                RiotOrderObservation real = fleet.Riot.RealRiotAbsent(journey.PickupUpperId);
                fleet.Riot.PutOrder(real with { Receipt = real.Receipt! with { FailureCategory = "TRANSPORT_FAILURE" } });
                break;
            case "intent-has-order-id":
                // RIoT answers exactly "no such order", but the server did learn an order id for this intent once (review X5): an
                // order that existed is not one that never appeared, so the entry must not close it.
                OrderIntentRow stored = await fleet.Context.OrderIntents.SingleAsync(row => row.UpperId == journey.PickupUpperId, Token);
                stored.OrderId = "ORDER-ONCE-SEEN";
                await fleet.Context.SaveChangesAsync(Token);
                fleet.Context.ChangeTracker.Clear();
                Assert.True((await fleet.Riot.ReconcileByUpperIdAsync(journey.PickupUpperId, Token))
                    .IsExactAbsentAtObservation(journey.PickupUpperId));
                break;
            case "order-unreadable":
                fleet.Riot.BeforeReconcile = _ => throw new HttpRequestException("RIoT did not answer.");
                break;
            case "too-early":
                await SetJourneyAsync(fleet, journey.JourneyId, row =>
                {
                    row.SetBlockReason(null, fleet.Clock.GetUtcNowWithoutTick());
                    row.SetBlockReason(
                        IdleReturnExecutionReasons.LegOutcomeCode(MovementDispatchOutcome.ResultUnknown), fleet.Clock.GetUtcNowWithoutTick());
                });
                break;
            case "vehicle-offline": Override(fleet, seen => seen with { Connected = false }); break;
            case "vehicle-moving": Override(fleet, seen => seen with { Speed = 0.3 }); break;
            default: throw new ArgumentOutOfRangeException(nameof(premise), premise, null);
        }

        WaitingPointArrivalSettlementResult result = await SettleAsync(
            fleet, SettlementRequest(AgvA, KeyA, journey.JourneyId, Near.StationId, WaitingPointArrivalVerdicts.NotAtWaitingPoint));

        Assert.False(result.Settled);
        Assert.Contains(code, result.Codes);
        await AssertStillHeldAsync(fleet, journey);
        Assert.Equal(GovernanceActionOutcome.Failed, Assert.Single(await AuditsAsync(fleet)).Outcome);
    }

    // ---- helpers ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// 网关把 HTTP 200、业务码 00002 的建单应答读成的样子（<c>HttpRiotMovementGateway.CreateAsync</c> 的 <c>FailureReceipt</c>；
    /// <c>HttpRiotFailureDetailTests</c> 钉住网关真的这样读）。
    /// </summary>
    private static RiotOrderObservation BusinessFailure00002(OrderIntent intent) => new(
        intent.UpperId,
        RiotOrderObservationKind.Unknown,
        null,
        Receipt: new RiotOrderCallReceipt(
            "CREATE", "SdkFailure", DateTimeOffset.UnixEpoch, HttpStatusCode: 200, BusinessCode: "00002",
            FailureCategory: "RIOT_API_FAILURE"));

    /// <summary>网关把一次超时的建单读成的样子。</summary>
    private static RiotOrderObservation Timeout(OrderIntent intent) => new(
        intent.UpperId,
        RiotOrderObservationKind.Unknown,
        null,
        Receipt: new RiotOrderCallReceipt("CREATE", "SdkFailure", DateTimeOffset.UnixEpoch, FailureCategory: "TIMEOUT"));

    /// <summary>
    /// 承诺、物化、过安全门、建单——应答是 <paramref name="answer"/>；之后 RIoT 按真实形态答查无此单。答那一趟旅程，它停在
    /// <c>WAITING_POINT_ResultUnknown</c>。
    /// </summary>
    private static async Task<JourneyRuntimeRow> CreateFailedAsync(FleetFixture fleet, Func<OrderIntent, RiotOrderObservation> answer)
    {
        fleet.Riot.AnswersAbsentAsRealRiot = true;
        fleet.Riot.CreateAnswer = answer;
        await RoundAsync(fleet);
        await RoundAsync(fleet);
        fleet.Riot.CreateAnswer = null;
        JourneyRuntimeRow journey = (await IdleJourneyAsync(fleet, AgvA))!;
        Assert.Equal(IdleReturnExecutionReasons.LegOutcomeCode(MovementDispatchOutcome.ResultUnknown), journey.BlockReasonCode);
        Assert.Equal("RESULT_UNKNOWN", (await fleet.Context.OrderIntents.AsNoTracking()
            .SingleAsync(row => row.UpperId == journey.PickupUpperId, Token)).Status);
        Assert.Single(fleet.Riot.Creates);
        return journey;
    }

    private static async Task AssertStillHeldAsync(FleetFixture fleet, JourneyRuntimeRow journey)
    {
        JourneyRuntimeRow now = await fleet.Context.JourneyRuntimes.AsNoTracking()
            .SingleAsync(row => row.JourneyId == journey.JourneyId, Token);
        Assert.Equal(
            (JourneyRuntimeStage.AwaitingPickupArrival, IdleReturnExecutionReasons.LegOutcomeCode(MovementDispatchOutcome.ResultUnknown)),
            (now.Stage, now.BlockReasonCode));
        Assert.Equal((VehiclePurposes.IdleReturn, journey.JourneyId), await ClaimOfAsync(fleet, KeyA));
        Assert.Equal((KeyA, StationExclusivityStates.Reserved), await HolderAsync(fleet, Near.StationId));
        Assert.Single(fleet.Riot.Creates);
    }

    private static ForeignRunningOrderSupervisor SupervisorOf(
        FleetFixture fleet, EventRecordingLogger<ForeignRunningOrderSupervisor> log) =>
        new(
            fleet.Context,
            fleet.Riot,
            fleet.Riot,
            new RiotOrderCommandAuditStore(fleet.Context),
            new ControlServer.Host.Runtime.Fleet.VehicleRoster(Microsoft.Extensions.Options.Options.Create(fleet.Options)),
            Microsoft.Extensions.Options.Options.Create(fleet.Options),
            Microsoft.Extensions.Options.Options.Create(new RiotForeignOrderCancelOptions()),
            fleet.Clock,
            log);
}
