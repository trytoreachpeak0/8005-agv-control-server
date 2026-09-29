using ControlServer.Application;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Infrastructure.Persistence;

/// <summary>
/// The one place a vehicle purpose claim is written and released (batch 8-16, control-server#387). It stages without
/// saving: <see cref="VehiclePurposeLedgerStore"/> saves right after, while the acceptance in <c>WireToGateStore</c> and
/// the journey release in <see cref="JourneyPurposeClaimRelease"/> stage it inside a larger save of their own.
/// </summary>
/// <remarks>
/// <para>
/// The claim row and its record go together: a claim is taken with an open record, and released with every open record of
/// that journey on that vehicle closed, in the same save. So no record stays open once its claim is gone -- the state that
/// would otherwise only ever be cleared by editing the database, once something reads the records.
/// </para>
/// <para>
/// Who holds a vehicle is decided by the claims' key alone. Nothing here reads first.
/// </para>
/// </remarks>
internal static class VehiclePurposeClaimWrites
{
    /// <summary>The claim row and its open record, for the caller to add to the save that decides the claim.</summary>
    internal static object[] NewRows(VehiclePurposeClaim claim)
    {
        ArgumentNullException.ThrowIfNull(claim);
        return
        [
            new VehiclePurposeClaimRow
            {
                VehicleKey = claim.VehicleKey,
                Purpose = claim.Purpose,
                JourneyId = claim.JourneyId,
                ClaimedAt = claim.ClaimedAt
            },
            new VehiclePurposeClaimRecordRow
            {
                RecordId = Guid.NewGuid().ToString("D"),
                VehicleKey = claim.VehicleKey,
                Purpose = claim.Purpose,
                JourneyId = claim.JourneyId,
                AcquiredAt = claim.ClaimedAt
            }
        ];
    }

    /// <summary>
    /// Stages the release of the claim <paramref name="journeyId"/> holds -- on <paramref name="vehicleKey"/> when given,
    /// on whichever vehicle otherwise -- and closes its open records. Returns false, staging nothing, when it holds none.
    /// </summary>
    internal static async Task<bool> StageReleaseAsync(
        ControlServerDbContext context,
        string? vehicleKey,
        string journeyId,
        DateTimeOffset releasedAt,
        string releaseReason,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(journeyId);
        ArgumentException.ThrowIfNullOrWhiteSpace(releaseReason);

        VehiclePurposeClaimRow? claim = await context.Set<VehiclePurposeClaimRow>()
            .SingleOrDefaultAsync(
                row => row.JourneyId == journeyId && (vehicleKey == null || row.VehicleKey == vehicleKey),
                cancellationToken)
            .ConfigureAwait(false);
        if (claim is null)
        {
            return false;
        }
        context.Set<VehiclePurposeClaimRow>().Remove(claim);
        // Records are evidence, not an arbiter, so nothing guarantees at most one is open: every open one of this journey
        // on this vehicle is closed.
        foreach (VehiclePurposeClaimRecordRow record in await context.Set<VehiclePurposeClaimRecordRow>()
                     .Where(row => row.VehicleKey == claim.VehicleKey && row.JourneyId == journeyId && row.ReleasedAt == null)
                     .ToListAsync(cancellationToken)
                     .ConfigureAwait(false))
        {
            record.ReleasedAt = releasedAt;
            record.ReleaseReason = releaseReason;
        }
        return true;
    }
}
