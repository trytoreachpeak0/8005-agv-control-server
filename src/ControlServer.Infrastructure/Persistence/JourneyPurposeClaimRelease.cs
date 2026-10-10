using ControlServer.Application;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Infrastructure.Persistence;

/// <summary>
/// Stages the release of a journey's vehicle purpose claim, with its record closed, without saving (control-server#207;
/// the lease that used to go with it was retired in batch 8-16, control-server#387).
/// </summary>
/// <remarks>
/// <para>
/// <b>The claim belongs to the journey, so it is released when the journey's last open demand ends</b>, not when any one
/// of its demands does. In a single-demand journey the demand that ends is always the last, so the release happens at
/// the same moment, in the same save, as before. In a journey of several demands, ending one leaves the vehicle held for
/// the others.
/// </para>
/// <para>
/// "The last" is read with <see cref="DemandJourneyLookup.IsLastOpenDemandAsync"/>, and the caller must be inside the
/// write transaction whose save carries the release -- the reason is there.
/// </para>
/// <para>
/// Every ending of a journey reaches one of these two: the successful unload through
/// <c>WireToGateStore.CompleteDemandAfterUnloadAsync</c>, the unload result through
/// <c>WireToGateStore.ApplyOperationResultAsync</c>, and every ending of a pickup stop through <c>PickupStopTermination</c>.
/// Missing one is not a leak that shows up later: the next acceptance on the vehicle hits the claim's key and is refused.
/// A journey that no longer holds a claim (released already) is left as it is.
/// </para>
/// </remarks>
public static class JourneyPurposeClaimRelease
{
    /// <summary>Releases the claim <paramref name="journeyId"/> holds at <paramref name="releasedAt"/>, if it holds one.</summary>
    public static async Task StageAsync(
        ControlServerDbContext dbContext,
        string journeyId,
        DateTimeOffset releasedAt,
        string releaseReason,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentException.ThrowIfNullOrWhiteSpace(journeyId);
        await VehiclePurposeClaimWrites
            .StageReleaseAsync(dbContext, vehicleKey: null, journeyId, releasedAt, releaseReason, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// For a demand that has just ended: releases its journey's claim if it was the journey's last open demand. A demand
    /// accepted without a journey holds a claim under its own anchor journey id, which it releases as it always did.
    /// </summary>
    public static async Task StageIfLastOpenDemandAsync(
        ControlServerDbContext dbContext,
        string demandId,
        DateTimeOffset releasedAt,
        string releaseReason,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentException.ThrowIfNullOrWhiteSpace(demandId);
        string? journeyId = await DemandJourneyLookup.JourneyIdOf(dbContext, demandId)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (journeyId is null)
        {
            await StageAsync(dbContext, JourneyIdentity.ForAnchorDemand(demandId), releasedAt, releaseReason, cancellationToken)
                .ConfigureAwait(false);
            return;
        }
        if (await DemandJourneyLookup.IsLastOpenDemandAsync(dbContext, journeyId, demandId, cancellationToken)
                .ConfigureAwait(false))
        {
            await StageAsync(dbContext, journeyId, releasedAt, releaseReason, cancellationToken).ConfigureAwait(false);
        }
    }
}
