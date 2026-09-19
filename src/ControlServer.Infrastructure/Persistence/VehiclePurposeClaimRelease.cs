using Microsoft.EntityFrameworkCore;

namespace ControlServer.Infrastructure.Persistence;

/// <summary>
/// Stages the release of the purpose claim that goes with a lease being released, without saving.
/// </summary>
/// <remarks>
/// <para>
/// Every site that releases a <see cref="VehicleDispatchLeaseRow"/> calls this in the same unsaved change (batch 7,
/// control-server#206): the successful unload through <c>WireToGateStore.CompleteDemandAfterUnloadAsync</c>, the unload
/// result through <c>WireToGateStore.ApplyOperationResultAsync</c>, and every ending of a pickup stop through
/// <c>PickupStopTermination</c>. Missing one is not a leak that shows up later: the next acceptance on the vehicle hits the
/// claim's key and is refused.
/// </para>
/// <para>
/// Only the claim this journey holds is removed. A claim some other journey holds is not this lease's to release.
/// </para>
/// </remarks>
public static class VehiclePurposeClaimRelease
{
    public static async Task StageAsync(
        ControlServerDbContext dbContext,
        VehicleDispatchLeaseRow lease,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(lease);

        VehiclePurposeClaimRow? claim = await dbContext.Set<VehiclePurposeClaimRow>()
            .SingleOrDefaultAsync(
                row => row.VehicleKey == lease.VehicleKey && row.JourneyId == lease.JourneyId,
                cancellationToken)
            .ConfigureAwait(false);
        if (claim is not null)
        {
            dbContext.Set<VehiclePurposeClaimRow>().Remove(claim);
        }
    }
}
