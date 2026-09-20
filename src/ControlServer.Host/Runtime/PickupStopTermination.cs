using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Host.Runtime;

/// <summary>
/// Ends a demand at its pickup stop without the cargo leaving it: the demand is terminated, and when it
/// was the journey's last open demand the journey closes under the reason the caller names and the
/// vehicle stops being held by it.
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
/// <b>Two steps since control-server#207: ending the demand, and closing the journey.</b> Ending the demand
/// touches that demand alone. Closing the journey -- Completed with the reason, the departure wait cleared,
/// the unanswered entry request settled, and the lease, the order occupancy and the purpose claim released
/// together -- happens only when the journey carries no other open demand. A journey still carrying one is
/// left as it is, every occupancy held and every other demand untouched. With one demand per journey, as
/// every journey the runtime creates today, the demand ended is always the last, so both steps run, in the
/// same unsaved change, exactly as the one step did. "The last" is read inside the caller's write
/// transaction (<see cref="DemandJourneyLookup.IsLastOpenDemandAsync"/>); every caller already holds one.
/// </para>
/// <para>
/// <b>It does not decide whether the stop may end.</b> Whether the deadline has passed, whether a slot
/// operation was commanded, whether a door is open: those belong to the caller, because each caller
/// answers them differently.
/// </para>
/// </remarks>
public sealed class PickupStopTermination(ControlServerDbContext dbContext)
{
    /// <summary>Ends the demand the journey row names, its anchor.</summary>
    public Task StageAsync(
        JourneyRuntimeRow runtime,
        string reasonCode,
        DateTimeOffset endedAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        return StageAsync(runtime, runtime.DemandId, reasonCode, endedAt, cancellationToken);
    }

    /// <summary>
    /// Ends <paramref name="demandId"/>, a demand <paramref name="runtime"/> carries, and closes the journey if it was
    /// the last open one.
    /// </summary>
    public async Task StageAsync(
        JourneyRuntimeRow runtime,
        string demandId,
        string reasonCode,
        DateTimeOffset endedAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentException.ThrowIfNullOrWhiteSpace(demandId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reasonCode);

        await StageDemandTerminationAsync(demandId, cancellationToken).ConfigureAwait(false);
        if (await DemandJourneyLookup.IsLastOpenDemandAsync(dbContext, runtime.JourneyId, demandId, cancellationToken)
                .ConfigureAwait(false))
        {
            await StageJourneyClosureAsync(runtime, reasonCode, endedAt, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>The first step: this demand, and nothing else, is terminated.</summary>
    public async Task StageDemandTerminationAsync(string demandId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(demandId);
        AcceptedDemandRow demand = await dbContext.AcceptedDemands
            .SingleAsync(row => row.DemandId == demandId, cancellationToken).ConfigureAwait(false);
        if (demand.Status == DemandExecutionStatus.Succeeded)
        {
            throw new BusinessIdentityConflictException(
                "A completed demand cannot be terminated at its pickup stop.");
        }
        demand.Status = DemandExecutionStatus.Cancelled;
    }

    /// <summary>
    /// The second step, for a journey that carries no open demand any more: it completes under
    /// <paramref name="reasonCode"/>, and the lease, the order occupancy and the purpose claim are released together.
    /// </summary>
    public async Task StageJourneyClosureAsync(
        JourneyRuntimeRow runtime,
        string reasonCode,
        DateTimeOffset endedAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentException.ThrowIfNullOrWhiteSpace(reasonCode);

        await JourneyLeaseRelease.StageAsync(dbContext, runtime.JourneyId, endedAt, cancellationToken)
            .ConfigureAwait(false);
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
