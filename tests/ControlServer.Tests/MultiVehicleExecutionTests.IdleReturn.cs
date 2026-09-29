using ControlServer.Application;
using ControlServer.Host.Runtime.Dispatch;
using ControlServer.Host.Runtime.IdleReturn;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Tests;

/// <summary>
/// 空闲返回在派车轮里（批次8-18，control-server#389）：只评估任务优先派车剩下的空闲车；承诺之后搬运不抢它。
/// </summary>
/// <remarks>
/// 夹具的路网里站 300「等待点」就是车停着的地方（节点 1），这里把它登记成唯一的等待点：第一辆被评估的车拿到它，后面的车一个点都不剩。
/// </remarks>
public sealed partial class MultiVehicleExecutionTests
{
    private static readonly WaitingPointEntry FleetWaitingPoint = new(25, 300, "等待点", true, []);

    /// <summary>
    /// 一轮里接了单的车不做空闲返回；剩下的空闲车按名册次序评估，第一辆拿到唯一的等待点，第三辆一个点都不剩。
    /// </summary>
    [Fact]
    public async Task OnlyTheIdleVehiclesTheTransportPassLeftOverAreCommittedToAnIdleReturn()
    {
        await using FleetFixture fixture = await FleetFixture.CreateAsync(withRouteGraph: true);
        await fixture.EnableIdleReturnAsync(FleetWaitingPoint);
        fixture.Catalog.Set([FleetFixture.Demand(0, "N1-1", 0)]);

        await fixture.RunRoundAsync();

        JourneyRuntimeRow journey = Assert.Single(
            await fixture.Context.JourneyRuntimes.AsNoTracking().ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal(FleetFixture.AgvIds[0], journey.AgvId);
        Dictionary<string, (string Purpose, string JourneyId)> claims = await fixture.Context.Set<VehiclePurposeClaimRow>()
            .AsNoTracking()
            .ToDictionaryAsync(row => row.VehicleKey, row => (row.Purpose, row.JourneyId), TestContext.Current.CancellationToken);
        Assert.Equal(VehiclePurposes.Transport, claims[FleetFixture.VehicleKeys[0]].Purpose);
        Assert.Equal(VehiclePurposes.IdleReturn, claims[FleetFixture.VehicleKeys[1]].Purpose);
        Assert.False(claims.ContainsKey(FleetFixture.VehicleKeys[2]));
        StationExclusivityRow station = Assert.Single(
            await fixture.Context.Set<StationExclusivityRow>().AsNoTracking().ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal(
            (300, FleetFixture.VehicleKeys[1], claims[FleetFixture.VehicleKeys[1]].JourneyId, StationExclusivityStates.Reserved),
            (station.StationId, station.VehicleKey, station.JourneyId, station.State));
        // 承诺不建单：这一轮 RIoT 上只有接单那辆车的取货单。
        Assert.All(fixture.Riot.Creates, create => Assert.Equal(FleetFixture.VehicleKeys[0], create.VehicleKey));
    }

    /// <summary>
    /// 承诺之后，同一辆车在下一轮、再下一轮都不接一条正好适合它的搬运，也不被当成在途车问追加；需求留在积压里，原因是它已承诺空闲返回。
    /// 承诺本身原样留着：不取消、不换点。
    /// </summary>
    /// <remarks>
    /// 车队裁成一辆：有别的车时需求会被别的车接走，积压上的原因就不是这一条了。
    /// </remarks>
    [Fact]
    public async Task AVehicleCommittedToAnIdleReturnTakesNoLaterTransportAndTheDemandWaitsWithThatReason()
    {
        await using FleetFixture fixture = await FleetFixture.CreateAsync(
            configure: options => options.Fleet = options.Fleet[..1], withRouteGraph: true);
        await fixture.AllowEnRouteAppendAsync(1_000_000);
        await fixture.EnableIdleReturnAsync(FleetWaitingPoint);
        // 第一轮没有需求（夹具默认给每辆车一条，这里清掉）：没有合法搬运用途，才轮到空闲返回。
        fixture.Catalog.Set([]);
        await fixture.RunRoundAsync();
        VehiclePurposeClaimRow committed = Assert.Single(
            await fixture.Context.Set<VehiclePurposeClaimRow>().AsNoTracking().ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal(VehiclePurposes.IdleReturn, committed.Purpose);

        fixture.Catalog.Set([FleetFixture.Demand(0, "N1-1", 0)]);
        await fixture.RunRoundAsync(TimeSpan.FromSeconds(1));
        await fixture.RunRoundAsync(TimeSpan.FromSeconds(1));

        Assert.Empty(await fixture.Context.JourneyRuntimes.AsNoTracking().ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Empty(fixture.Riot.Creates);
        JourneyBacklogRow backlog = Assert.Single(
            await fixture.Context.JourneyBacklog.AsNoTracking().ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal(DispatchReasonCodes.VehicleCommittedToIdleReturn, backlog.ReasonCode);
        Assert.Null(backlog.AcceptedAt);
        // 两轮都问到了它、都是这一条：它作为空闲车被问（没有在途计划），没有被当成可追加的在途车。
        DispatchRoundOutcome[] lastTwo = [.. fixture.RoundOutcomes.Outcomes.TakeLast(2)];
        Assert.All(lastTwo, outcome =>
        {
            DispatchVehicleOutcome vehicle = Assert.Single(outcome.CompletedVehicles);
            DispatchCandidateVerdict verdict = Assert.Single(vehicle.Verdicts);
            Assert.Equal(DispatchReasonCodes.VehicleCommittedToIdleReturn, verdict.ReasonCode);
            Assert.Null(verdict.Evaluation.Vehicle.Plan);
        });
        VehiclePurposeClaimRow still = Assert.Single(
            await fixture.Context.Set<VehiclePurposeClaimRow>().AsNoTracking().ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal((committed.Purpose, committed.JourneyId, committed.ClaimedAt), (still.Purpose, still.JourneyId, still.ClaimedAt));
        StationExclusivityRow station = Assert.Single(
            await fixture.Context.Set<StationExclusivityRow>().AsNoTracking().ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal((300, committed.JourneyId), (station.StationId, station.JourneyId));
    }
}
