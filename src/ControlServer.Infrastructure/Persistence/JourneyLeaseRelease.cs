using Microsoft.EntityFrameworkCore;

namespace ControlServer.Infrastructure.Persistence;

/// <summary>
/// Stages the release of a journey's lease, and with it the purpose claim, without saving (control-server#207).
/// </summary>
/// <remarks>
/// <para>
/// <b>A lease belongs to the journey, so it is released when the journey's last open demand ends</b>, not when any one
/// of its demands does. In a single-demand journey the demand that ends is always the last, so the release happens at
/// the same moment, in the same save, as before. In a journey of several demands, ending one leaves the vehicle held for
/// the others.
/// </para>
/// <para>
/// "The last" is read with <see cref="DemandJourneyLookup.IsLastOpenDemandAsync"/>, and the caller must be inside the
/// write transaction whose save carries the release -- the reason is there.
/// </para>
/// </remarks>
public static class JourneyLeaseRelease
{
    /// <summary>Releases the journey's lease at <paramref name="releasedAt"/> unless already released, and its claim.</summary>
    public static async Task StageAsync(
        ControlServerDbContext dbContext,
        string journeyId,
        DateTimeOffset releasedAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentException.ThrowIfNullOrWhiteSpace(journeyId);
        VehicleDispatchLeaseRow lease = await dbContext.VehicleDispatchLeases
            .SingleAsync(row => row.JourneyId == journeyId, cancellationToken)
            .ConfigureAwait(false);
        await StageAsync(dbContext, lease, releasedAt, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// For a demand that has just ended: releases its journey's lease and claim if it was the journey's last open demand.
    /// A demand accepted without a journey holds a lease of its own, which it releases as it always did.
    /// </summary>
    public static async Task StageIfLastOpenDemandAsync(
        ControlServerDbContext dbContext,
        string demandId,
        DateTimeOffset releasedAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentException.ThrowIfNullOrWhiteSpace(demandId);
        string? journeyId = await DemandJourneyLookup.JourneyIdOf(dbContext, demandId)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (journeyId is null)
        {
            VehicleDispatchLeaseRow own = await dbContext.VehicleDispatchLeases
                .SingleAsync(row => row.DemandId == demandId, cancellationToken)
                .ConfigureAwait(false);
            await StageAsync(dbContext, own, releasedAt, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (await DemandJourneyLookup.IsLastOpenDemandAsync(dbContext, journeyId, demandId, cancellationToken)
                .ConfigureAwait(false))
        {
            await StageAsync(dbContext, journeyId, releasedAt, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task StageAsync(
        ControlServerDbContext dbContext,
        VehicleDispatchLeaseRow lease,
        DateTimeOffset releasedAt,
        CancellationToken cancellationToken)
    {
        lease.ReleasedAt ??= releasedAt;
        await VehiclePurposeClaimRelease.StageAsync(dbContext, lease, cancellationToken).ConfigureAwait(false);
    }
}
