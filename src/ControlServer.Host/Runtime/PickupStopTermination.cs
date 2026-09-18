using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Host.Runtime;

/// <summary>
/// Ends a journey's demand at its pickup stop without the cargo leaving it: the demand is terminated
/// under the reason the caller names, the vehicle stops being held by it, and the journey completes.
/// </summary>
/// <remarks>
/// <para>
/// <b>The tail every way of ending a pickup stop like this is meant to share.</b> The station
/// departure deadline (ADR-cross-0055, ADR-cross-0058 decision 7) was the first caller. The
/// cancellation before a sublot is entered (ADR-cross-0046, control-server#83) is the second: its
/// ALL_EMPTY result ends the stop through here as <c>CANCELLED_BY_OPERATOR</c>. The settlement of a
/// determinate load failure (control-server#81) ends the stop the same way and differs only in why, so
/// it reuses this rather than release the vehicle a third slightly different way.
/// </para>
/// <para>
/// <b>The endings of a commanded slot operation use it too.</b> Since control-server#131,
/// <c>OnboardRecoveryCoordinator.ApplyCurrentResultAsync</c> ends a demand whose slot operation was
/// commanded -- a cancellation in flight (<c>CANCELLED_BY_OPERATOR</c>), a compensation
/// (<c>CANCELLED_BY_LOAD_COMPENSATION</c>), a fault cargo handoff (<c>TERMINATED_BY_FAULT_CARGO_HANDOFF</c>)
/// -- through here as well, and since control-server#137 a forced mechanical recovery, whose named handoff
/// ends the demand under that same reason. Until then it wrote nearly the same facts by hand, minus the vehicle occupancy,
/// and the pickup order held the vehicle against every later claim. What this still deliberately knows
/// nothing about is the commanded operation itself: the coordinator cancels it, and settles the recovery
/// command it answered, in the same unsaved change. A fault cargo handoff can happen at the gate as well as
/// at the pickup; the tail is the same there, because the occupancy was claimed on the pickup order for the
/// whole journey. It is now the only code that sets a demand <c>Cancelled</c>.
/// </para>
/// <para>
/// <b>It stages the changes and does not save.</b> Every fact here has to commit together with the
/// caller's own: a cancelled demand whose vehicle is still leased, or a free vehicle whose journey is
/// still open, is exactly what a crash between two saves would leave behind. The caller saves once.
/// That is also why the vehicle occupancy is released on the tracked order intent rather than through
/// <see cref="Fleet.VehicleDispatchPolicyAccess"/>, whose release saves on its own.
/// </para>
/// <para>
/// <b>It does not decide whether the stop may end.</b> Whether the deadline has passed, whether a slot
/// operation was commanded, whether a door is open: those belong to the caller, because each caller
/// answers them differently.
/// </para>
/// </remarks>
public sealed class PickupStopTermination(ControlServerDbContext dbContext)
{
    public async Task StageAsync(
        JourneyRuntimeRow runtime,
        string reasonCode,
        DateTimeOffset endedAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentException.ThrowIfNullOrWhiteSpace(reasonCode);

        AcceptedDemandRow demand = await dbContext.AcceptedDemands
            .SingleAsync(row => row.DemandId == runtime.DemandId, cancellationToken).ConfigureAwait(false);
        if (demand.Status == DemandExecutionStatus.Succeeded)
        {
            throw new BusinessIdentityConflictException(
                "A completed demand cannot be terminated at its pickup stop.");
        }
        demand.Status = DemandExecutionStatus.Cancelled;

        VehicleDispatchLeaseRow lease = await dbContext.VehicleDispatchLeases
            .SingleAsync(row => row.DemandId == runtime.DemandId, cancellationToken).ConfigureAwait(false);
        lease.ReleasedAt ??= endedAt;
        OrderIntentRow pickup = await dbContext.OrderIntents
            .SingleAsync(row => row.UpperId == runtime.PickupUpperId, cancellationToken).ConfigureAwait(false);
        pickup.VehicleOccupancyReleasedAt ??= endedAt;

        // Nobody is going to answer the entry request now. Left unsettled it is replayed into every
        // later session, where the peer refuses it as a business id whose content changed and tears
        // the session down -- the same failure the answered request is settled for.
        ProtocolOutboxRow? entryRequest = await dbContext.ProtocolOutbox
            .SingleOrDefaultAsync(row => row.MessageId == runtime.SublotRequestMessageId, cancellationToken)
            .ConfigureAwait(false);
        if (entryRequest is not null)
        {
            entryRequest.AcknowledgedAt ??= endedAt;
        }

        runtime.Stage = JourneyRuntimeStage.Completed;
        runtime.SetBlockReason(reasonCode, endedAt);
        runtime.StationDepartureWaitStartedAt = null;
        runtime.UpdatedAt = endedAt;
    }
}
