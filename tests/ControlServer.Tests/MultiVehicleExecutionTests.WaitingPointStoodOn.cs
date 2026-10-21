using ControlServer.Application;
using ControlServer.Host.Runtime.IdleReturn;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Tests;

/// <summary>
/// 空闲返回选点看全车队（control-server#798），走整条派车轮：停在等待点上的车不是这一轮的空闲车——一张外来订单占着它，或它在跑一趟旅程——
/// 派车轮不把它交给评估器，#796 的「另一辆车站在点上」因此看不见它。点上没有占用行时，别的车被派进来。
/// </summary>
public sealed partial class MultiVehicleExecutionTests
{
    [Theory]
    [InlineData("foreign-order")]
    [InlineData("journey")]
    public async Task AVehicleOutsideTheIdleFleetStandingOnTheWaitingPointKeepsTheIdleReturnOffIt(string why)
    {
        await using FleetFixture fixture = await FleetFixture.CreateAsync(
            configure: options => options.Fleet = options.Fleet[..2], withRouteGraph: true);
        string standing = FleetFixture.VehicleKeys[0];
        string evaluated = FleetFixture.AgvIds[1];
        if (why == "journey")
        {
            // The first vehicle in the roster takes the demand; the idle return is switched on only afterwards, so the second
            // vehicle is not committed in that same round while the first still stands at 300.
            fixture.Catalog.Set([FleetFixture.Demand(0, "N1-1", 0)]);
            await fixture.RunRoundAsync();
            Assert.Equal(FleetFixture.AgvIds[0], (await fixture.JourneyOfAsync(FleetFixture.AgvIds[0])).AgvId);
        }
        else
        {
            fixture.Context.ForeignRiotOrders.Add(new ForeignRiotOrderRow
            {
                RiotOrderId = "order-foreign-0798",
                UpperId = "MES-FIELD-0798",
                AgvId = FleetFixture.AgvIds[0],
                DeviceKey = standing,
                Ownership = ForeignRiotOrderOwnership.Foreign,
                OwnershipBasis = "TEST",
                State = ForeignRiotOrderStates.StillRunningAfterCancel,
                DetectedAt = DateTimeOffset.UnixEpoch,
                LastSeenRunningAt = DateTimeOffset.UnixEpoch,
                UpdatedAt = DateTimeOffset.UnixEpoch,
            });
            await fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await EnableIdleReturnAtFleetWaitingPointAsync(fixture);
        fixture.Riot.VehicleOverrides[standing] = seen => seen with { CurrentStationId = FleetWaitingPointStation };

        await fixture.RunRoundAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(IdleReturnReasons.NoWaitingPointAvailable, fixture.IdleReturnBoard.Reasons[evaluated]);
        Assert.Equal(
            $"{FleetWaitingPointStation}={IdleReturnReasons.PointStoodOn}",
            fixture.IdleReturnBoard.LatestCompletedPass!.Verdicts[evaluated].Detail);
        Assert.Empty(await fixture.Context.Set<StationExclusivityRow>().AsNoTracking()
            .Where(row => row.StationId == FleetWaitingPointStation)
            .ToArrayAsync(TestContext.Current.CancellationToken));
    }
}
