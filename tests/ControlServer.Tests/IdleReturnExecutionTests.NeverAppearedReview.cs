using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Dashboard;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.ForeignOrders;
using ControlServer.Host.Runtime.IdleReturn;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using static ControlServer.Tests.WaitingPointArrivalSettlementTestKit;
using FleetFixture = ControlServer.Tests.MultiVehicleExecutionTests.FleetFixture;

namespace ControlServer.Tests;

// control-server#611, the first review of PR #675: M1, M2, M3, M4, S1, S2.
public sealed partial class IdleReturnExecutionTests
{
    /// <summary>
    /// 审查 M2：第二次复现的样子——人在 RIoT 上把车手动开到了要去的等待点，单从没出现过。放弃之后车就停在点上：预占不当场放（离点清扫凭离点证据放），
    /// 别的车预占不到它；评估器答车占着站点；不建第二张单。看板上这辆车的「上一趟」显示收尾码与已确认失败那一支。
    /// </summary>
    [Fact]
    public async Task GivenUpWithTheVehicleStandingOnItsPointTheReservationStaysForTheDepartureSweep()
    {
        await using FleetFixture fleet = await FleetAsync();
        JourneyRuntimeRow journey = await CreateFailedAsync(fleet, BusinessFailure00002);
        MoveTo(fleet, KeyA, Near.StationId);

        await fleet.HearFromEveryVehicleAsync();
        await fleet.RunRoundAsync(fleet.Options.ChargingOrderAbsentAbandonAfter);
        await RoundAsync(fleet);
        await RoundAsync(fleet);

        JourneyRuntimeRow ended = await fleet.Context.JourneyRuntimes.AsNoTracking()
            .SingleAsync(row => row.JourneyId == journey.JourneyId, Token);
        Assert.Equal(
            (JourneyRuntimeStage.Completed, IdleReturnExecutionReasons.OrderNeverAppeared), (ended.Stage, ended.BlockReasonCode));
        Assert.Equal((KeyA, StationExclusivityStates.Reserved), await HolderAsync(fleet, Near.StationId));
        Assert.Equal(journey.JourneyId, (await StationAsync(fleet, Near.StationId))!.JourneyId);
        Assert.Equal(IdleReturnReasons.VehicleHoldsStation, fleet.IdleReturnBoard.Reasons[AgvA]);
        Assert.Single(fleet.Riot.Creates);
        // The plan follows the facts (contract item 2): standing on the point, the onboard is left with that one waiting point
        // leg, ARRIVED -- not an empty plan, which is what a vehicle that never left gets. This is the only thing
        // stillAtWaitingPoint decides; the reservation stays either way (mutation V06 survived on the reservation alone).
        JsonElement leg = Assert.Single(
            (await PayloadsAsync(fleet, AgvA, "UpcomingStopPlanSnapshot")).Last().GetProperty("legs").EnumerateArray());
        Assert.Equal(("WAITING_POINT", "ARRIVED"), (leg.GetProperty("stopPurposeCategory").GetString(), leg.GetProperty("state").GetString()));

        (JsonElement vehicle, _) = await DashboardRowAsync(fleet, AgvA);
        JsonElement last = vehicle.GetProperty("lastEnded");
        Assert.Equal(IdleReturnExecutionReasons.OrderNeverAppeared, last.GetProperty("reasonCode").GetString());
        Assert.False(string.IsNullOrWhiteSpace(last.GetProperty("reasonDescription").GetString()));
        Assert.Equal("CONFIRMED_FAILURE", last.GetProperty("failureBranch").GetString());
    }

    /// <summary>
    /// 审查 M3：出口与建单为什么失败无关，是有意的——超时（应答丢了）之后 RIoT 连续 120 秒精确查无此单、车停稳没单，同样放弃。
    /// 「回超时时仍走未知」指的是当场不判失败（见 <see cref="ATimedOutCreateStaysUnknownAndAnOrderThatAppearsWithinTheWindowIsTakenUpNotGivenUp"/>），
    /// 不是永远保持。
    /// </summary>
    [Fact]
    public async Task ATimedOutCreateWhoseOrderNeverAppearsIsGivenUpOnTheSameEvidence()
    {
        await using FleetFixture fleet = await FleetAsync();
        JourneyRuntimeRow journey = await CreateFailedAsync(fleet, Timeout);

        await fleet.HearFromEveryVehicleAsync();
        await fleet.RunRoundAsync(TimeSpan.FromSeconds(100));
        await AssertStillHeldAsync(fleet, journey);
        await fleet.HearFromEveryVehicleAsync();
        await fleet.RunRoundAsync(TimeSpan.FromSeconds(30));
        await RoundAsync(fleet);

        Assert.Equal(
            IdleReturnExecutionReasons.OrderNeverAppeared,
            (await fleet.Context.JourneyRuntimes.AsNoTracking().SingleAsync(row => row.JourneyId == journey.JourneyId, Token))
                .BlockReasonCode);
        Assert.Null(await ClaimOfAsync(fleet, KeyA));
        Assert.Single(fleet.Riot.Creates);
    }

    /// <summary>
    /// 审查 S2：点被释放那条路上读到单在，计时要清零，与主路径同一个约定。查无此单 100 秒 → 单出现一次 → 又查无此单：之后要再连续满 120 秒才放弃，
    /// 不能沿用最早的起点。
    /// </summary>
    [Fact]
    public async Task OnTheLostPointPathAnOrderSeenOnceStartsTheCountAgain()
    {
        await using FleetFixture fleet = await FleetAsync(points: [Near, Far]);
        JourneyRuntimeRow journey = await CreateFailedAsync(fleet, BusinessFailure00002);
        await ReleaseByHandAsync(fleet, Near.StationId);
        await fleet.HearFromEveryVehicleAsync();
        await fleet.RunRoundAsync(TimeSpan.FromSeconds(100));

        fleet.Riot.PutOrder(new RiotOrderObservation(
            journey.PickupUpperId, RiotOrderObservationKind.Active, "ORDER-BRIEF", RiotOrderState.Queueing, KeyA, Map, Near.StationId));
        await RoundAsync(fleet);
        fleet.Riot.PutOrder(fleet.Riot.RealRiotAbsent(journey.PickupUpperId));
        // The count starts again at this round's reading; without the reset it would have run from the first reading, 200 s ago.
        await RoundAsync(fleet);
        await fleet.HearFromEveryVehicleAsync();
        await fleet.RunRoundAsync(TimeSpan.FromSeconds(100));
        Assert.Equal(
            (JourneyRuntimeStage.AwaitingPickupArrival, IdleReturnExecutionReasons.WaitingPointLostOrderInFlight),
            ((await IdleJourneyAsync(fleet, AgvA))!.Stage, (await IdleJourneyAsync(fleet, AgvA))!.BlockReasonCode));

        await fleet.HearFromEveryVehicleAsync();
        await fleet.RunRoundAsync(TimeSpan.FromSeconds(30));
        await RoundAsync(fleet);
        Assert.Equal(IdleReturnExecutionReasons.OrderNeverAppeared, (await IdleJourneyAsync(fleet, AgvA))!.BlockReasonCode);
        Assert.Single(fleet.Riot.Creates);
    }

    /// <summary>
    /// 审查 M4：点被释放、建单结果未知、自动放弃凑不齐证据（车上一直挂着别人的排队单）——人工出口照收，只收「不在点上」；说「在点上」拒绝。
    /// </summary>
    [Fact]
    public async Task APersonClosesTheLostPointUnknownCreateAsNotAtThePointOnly()
    {
        await using FleetFixture fleet = await FleetAsync(points: [Near, Far]);
        JourneyRuntimeRow journey = await CreateFailedAsync(fleet, BusinessFailure00002);
        await ReleaseByHandAsync(fleet, Near.StationId);
        fleet.Riot.ForeignOrders.Add((new RiotListedOrder("F-9", "SOMEONE-ELSES", RiotOrderState.Queueing, KeyA, null), 12));
        // The round that names the lost point; the repeat window counts from it.
        await RoundAsync(fleet);
        await fleet.HearFromEveryVehicleAsync();
        await fleet.RunRoundAsync(fleet.Options.OwnOrderRebuildRepeatWindow + TimeSpan.FromSeconds(1));
        await RoundAsync(fleet);
        Assert.Equal(
            IdleReturnExecutionReasons.WaitingPointLostOrderInFlight, (await IdleJourneyAsync(fleet, AgvA))!.BlockReasonCode);

        WaitingPointArrivalSettlementResult refused = await SettleAsync(
            fleet, SettlementRequest(AgvA, KeyA, journey.JourneyId, Near.StationId, WaitingPointArrivalVerdicts.AtWaitingPoint));
        Assert.False(refused.Settled);
        Assert.True(refused.Codes.Contains(WaitingPointArrivalSettlement.PointLostVerdictMustBeNotAt), string.Join(",", refused.Codes));

        WaitingPointArrivalSettlementResult result = await SettleAsync(
            fleet, SettlementRequest(AgvA, KeyA, journey.JourneyId, Near.StationId, WaitingPointArrivalVerdicts.NotAtWaitingPoint));
        Assert.Equal((true, IdleReturnExecutionReasons.NeverAppearedByOperator), (result.Settled, result.Ending));
        Assert.Equal(JourneyRuntimeStage.Completed, (await IdleJourneyAsync(fleet, AgvA))!.Stage);
        Assert.Null(await ClaimOfAsync(fleet, KeyA));
        Assert.Single(fleet.Riot.Creates);
    }

    /// <summary>
    /// 审查 M1：每一种「本服务端自己建了又放弃的单」的依据（<c>OWN_*_ABANDONED</c>）都有自己的看板说明，不落进叫现场去找外部建单人的通用说明；
    /// 空闲返回与清桩移动这两种会走到的「归属证明不了」与「回查不到终结」都有，并且说清不用找外部建单人。
    /// </summary>
    [Fact]
    public void EveryOwnAbandonedOrderBasisHasItsOwnDashboardDescription()
    {
        string[] bases =
        [
            .. typeof(ForeignRunningOrderSupervisor).Assembly.GetTypes()
                .SelectMany(type => type.GetFields(
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static))
                .Where(field => field.IsLiteral && field.FieldType == typeof(string))
                .Select(field => (string)field.GetRawConstantValue()!)
                .Where(value => value.StartsWith("OWN_", StringComparison.Ordinal) && value.EndsWith("_ABANDONED", StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal),
        ];
        // control-server#673 added the fourth: a demand leg's order taken as never created.
        Assert.Equal(4, bases.Length);
        foreach (string basis in bases)
        {
            Assert.True(ForeignRunningOrdersQueryEndpoint.OwnAbandonedOrderDescriptions.ContainsKey(basis), basis);
        }
        foreach (string basis in new[]
                 {
                     AbandonedIdleReturnOrders.OwnershipBasis, ControlServer.Host.Runtime.Charging.AbandonedClearanceMoves.OwnershipBasis,
                     AbandonedDemandLegOrders.OwnershipBasis,
                 })
        {
            foreach (string reason in new[] { ForeignRunningOrders.OwnershipUnprovenReason, ForeignRunningOrders.UnsettledReason })
            {
                string? said = ForeignRunningOrdersQueryEndpoint.DescriptionFor(basis, reason);
                Assert.NotNull(said);
                Assert.DoesNotContain("谁建的", said!, StringComparison.Ordinal);
                Assert.Contains("本服务端自己", said, StringComparison.Ordinal);
            }
        }
        // Anything else still reads the general wording.
        Assert.Equal(
            ForeignRunningOrdersQueryEndpoint.Descriptions[ForeignRunningOrders.OwnershipUnprovenReason],
            ForeignRunningOrdersQueryEndpoint.DescriptionFor("UPPER_ID_SHAPED_AS_OWN_WITHOUT_INTENT", ForeignRunningOrders.OwnershipUnprovenReason));
    }

    /// <summary>
    /// 审查 S1：放弃窗口加冷却不短于重复窗口时，两次放弃永远落在窗口外，「两次即停」永远不触发——启动校验拒绝这种配置；默认值（120 秒 + 30 秒 &lt; 10 分钟）放行。
    /// </summary>
    [Fact]
    public void AnAbandonWindowThatOutlastsTheRepeatWindowIsRefusedAtStartup()
    {
        const string failure =
            "ChargingOrderAbsentAbandonAfter plus OwnOrderRebuildDelay must be shorter than OwnOrderRebuildRepeatWindow, " +
            "or a repeatedly failing create is never stopped.";
        JourneyRuntimeOptionsValidator validator = new(new ConfigurationBuilder().Build());
        JourneyRuntimeOptions options = JourneyRuntimeOptionsTests.ValidEnabledOptions();
        Assert.DoesNotContain(failure, validator.Validate(null, options).Failures ?? [], StringComparer.Ordinal);

        options.ChargingOrderAbsentAbandonAfter = options.OwnOrderRebuildRepeatWindow - options.OwnOrderRebuildDelay;
        Assert.Contains(failure, validator.Validate(null, options).Failures ?? [], StringComparer.Ordinal);
        options.ChargingOrderAbsentAbandonAfter -= TimeSpan.FromSeconds(1);
        Assert.DoesNotContain(failure, validator.Validate(null, options).Failures ?? [], StringComparer.Ordinal);
    }
}
