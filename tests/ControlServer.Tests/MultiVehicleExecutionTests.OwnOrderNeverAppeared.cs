using ControlServer.Application;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Tests;

// control-server#673, P8 of #680's mutation round: the transitional code of a leg taken as never created keeps appends off.
public sealed partial class MultiVehicleExecutionTests
{
    /// <summary>
    /// 取货腿建单回 00002、RIoT 一直查无此单，判作没建成（<c>OWN_ORDER_NEVER_APPEARED</c>），延迟还没到、重建还没开始：这辆车停着、当前腿没有单，
    /// 不接途中追加（「停住的车不接追加」，用户 09-22 定）——第二条需求不进这趟旅程。
    /// </summary>
    /// <remarks>
    /// 这一格只靠旅程码挡：派车轮次按 <see cref="Host.Runtime.JourneyRuntimeEngine.IsStalledOrderReason"/> 判，不看重建记录（释放服务两样都看）。
    /// 变异把新码移出那一族时（#680 的 P8），这一条红。判别力同 <see cref="AJourneyWhoseOrderHangsTakesNoAppendedDemand"/>：
    /// <see cref="AnInTransitVehicleTakesAnAppendedDemandIntoItsExistingJourney"/> 是同一个夹具、同样两条需求下追加进来的那一面。
    /// </remarks>
    [Fact]
    [Trait("Requirement", "REQ-0360")]
    public async Task AVehicleWhoseLegWasTakenAsNeverCreatedTakesNoAppendedDemandBeforeTheRebuild()
    {
        await using FleetFixture fixture = await FleetFixture.CreateAsync(
            configure: options => options.Fleet = options.Fleet[..1], withRouteGraph: true);
        await fixture.AllowEnRouteAppendAsync(1_000_000);
        fixture.Riot.AnswersAbsentAsRealRiot = true;
        fixture.Riot.CreateAnswer = intent => new RiotOrderObservation(
            intent.UpperId,
            RiotOrderObservationKind.Unknown,
            null,
            Receipt: new RiotOrderCallReceipt(
                "CREATE", "SdkFailure", fixture.Clock.GetUtcNowWithoutTick(), HttpStatusCode: 200, BusinessCode: "00002",
                FailureCategory: "RIOT_API_FAILURE"));
        fixture.Catalog.Set([FleetFixture.Demand(0, "N1-1", 0)]);
        await fixture.RunRoundAsync();
        JourneyRuntimeRow journey = await fixture.JourneyOfAsync(FleetFixture.AgvIds[0]);
        await fixture.HearFromEveryVehicleAsync();
        await fixture.RunRoundAsync(TimeSpan.FromSeconds(1));
        Assert.Equal("PICKUP_ResultUnknown", (await fixture.JourneyOfAsync(FleetFixture.AgvIds[0])).BlockReasonCode);
        await fixture.HearFromEveryVehicleAsync();
        await fixture.RunRoundAsync(TimeSpan.FromSeconds(130));
        Assert.Equal("OWN_ORDER_NEVER_APPEARED", (await fixture.JourneyOfAsync(FleetFixture.AgvIds[0])).BlockReasonCode);

        fixture.Catalog.Set([FleetFixture.Demand(0, "N1-1", 0), FleetFixture.Demand(1, "N1-2", 1)]);
        await fixture.HearFromEveryVehicleAsync();
        await fixture.RunRoundAsync(TimeSpan.FromSeconds(1));

        JourneyRuntimeRow only = Assert.Single(
            await fixture.Context.JourneyRuntimes.AsNoTracking().ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal((journey.JourneyId, "OWN_ORDER_NEVER_APPEARED"), (only.JourneyId, only.BlockReasonCode));
        JourneyDemandRow membership = Assert.Single(
            await fixture.Context.Set<JourneyDemandRow>().AsNoTracking()
                .ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal(FleetFixture.Demand(0, "N1-1", 0).DemandId, membership.DemandId);
        Assert.Single(fixture.Riot.Creates);
    }
}
