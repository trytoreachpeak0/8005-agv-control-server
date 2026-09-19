using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Tests;

/// <summary>
/// The batch 7 occupancy invariant (control-server#206): a vehicle has an unreleased lease exactly when it has a purpose
/// claim, for the same journey.
/// </summary>
/// <remarks>
/// Called after every way a journey ends. A release site that forgets the claim does not fail where it forgets it; it
/// fails the next acceptance on that vehicle, which is where these assertions put it back.
/// </remarks>
internal static class VehicleOccupancyAssertions
{
    internal static async Task AssertActiveLeasesAndPurposeClaimsMatchAsync(ControlServerDbContext context)
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string[] leases = await context.VehicleDispatchLeases.AsNoTracking()
            .Where(lease => lease.ReleasedAt == null)
            .Select(lease => lease.VehicleKey + " " + lease.JourneyId)
            .ToArrayAsync(cancellationToken);
        string[] claims = await context.Set<VehiclePurposeClaimRow>().AsNoTracking()
            .Select(claim => claim.VehicleKey + " " + claim.JourneyId)
            .ToArrayAsync(cancellationToken);
        Assert.Equal(leases.Order(StringComparer.Ordinal), claims.Order(StringComparer.Ordinal));
    }
}
