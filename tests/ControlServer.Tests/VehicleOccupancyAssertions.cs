using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Tests;

/// <summary>
/// The occupancy invariant: a vehicle has an open claim record exactly when it has a purpose claim, for the same journey
/// (batch 7, control-server#206, between the lease and the claim; since batch 8-16, control-server#387, between the claim
/// and its record, which replaced the lease).
/// </summary>
/// <remarks>
/// Called after every way a journey ends. A release site that forgets the claim does not fail where it forgets it; it
/// fails the next acceptance on that vehicle, which is where these assertions put it back. A release that removes the
/// claim but leaves its record open is the other half: nothing arbitrates on the record, but the history would say the
/// vehicle is still held.
/// </remarks>
internal static class VehicleOccupancyAssertions
{
    internal static async Task AssertOpenClaimRecordsAndPurposeClaimsMatchAsync(ControlServerDbContext context)
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string[] records = await context.Set<VehiclePurposeClaimRecordRow>().AsNoTracking()
            .Where(record => record.ReleasedAt == null)
            .Select(record => record.VehicleKey + " " + record.JourneyId)
            .ToArrayAsync(cancellationToken);
        string[] claims = await context.Set<VehiclePurposeClaimRow>().AsNoTracking()
            .Select(claim => claim.VehicleKey + " " + claim.JourneyId)
            .ToArrayAsync(cancellationToken);
        Assert.Equal(records.Order(StringComparer.Ordinal), claims.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// The journey no longer holds a vehicle, and its history says when and why it gave it back: exactly one record of
    /// that journey, closed at <paramref name="releasedAt"/> for <paramref name="reason"/> (control-server#387).
    /// </summary>
    internal static async Task AssertReleasedWithHistoryAsync(
        ControlServerDbContext context, string journeyId, DateTimeOffset releasedAt, string reason)
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Assert.False(await context.Set<VehiclePurposeClaimRow>().AsNoTracking()
            .AnyAsync(claim => claim.JourneyId == journeyId, cancellationToken));
        VehiclePurposeClaimRecordRow record = Assert.Single(await context.Set<VehiclePurposeClaimRecordRow>().AsNoTracking()
            .Where(row => row.JourneyId == journeyId)
            .ToArrayAsync(cancellationToken));
        Assert.Equal((releasedAt, reason), (record.ReleasedAt, record.ReleaseReason));
    }
}
