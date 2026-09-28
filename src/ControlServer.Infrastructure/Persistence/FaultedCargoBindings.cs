using ControlServer.Application;
using ControlServer.Domain;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Infrastructure.Persistence;

/// <summary>
/// When a faulted vehicle's cargo binding (<see cref="FaultedVehicleCargoRow"/>, REQ-0238) has done its work
/// (control-server#376). Staged into the caller's context; the caller saves.
/// </summary>
/// <remarks>
/// <para>
/// <b>A binding stands for the cargo of one journey, not of one demand.</b> The engine binds under the journey's anchor demand
/// (<c>JourneyRuntimeEngine.InFlightFaultContextAsync</c>), which on a journey of several demands need not be the one on board.
/// So a binding is let go only once nothing of its journey is left on the vehicle -- releasing it when the demand it names is
/// unloaded would drop the protection of another demand's cargo still on board.
/// </para>
/// <para>
/// <b>Before control-server#376</b> a binding was released only by a confirmed resumption, a rebuild confirmed on a clearance that
/// judged the vehicle loaded, and a handoff in the exception recovery session. A binding that outlived its journey -- control-server#335's
/// door fault on an order then cancelled in RIoT, or a rebuilt order cancelled before it was confirmed -- was taken for the cargo of the
/// vehicle's next fault, because the fault coordinator binds once and leaves a live binding alone. An empty vehicle was then cleared
/// as loaded and its rebuild waited for ever on slots it had none of; a loaded one could not resume its own order.
/// </para>
/// </remarks>
public static class FaultedCargoBindings
{
    /// <summary>The journey's last demand on board was unloaded: nothing of it is left on the vehicle.</summary>
    public const string UnloadedWithNothingLeftOnBoardReason = "UNLOADED_WITH_NOTHING_LEFT_ON_BOARD";

    /// <summary>
    /// The binding names a demand of a journey that has closed and is not part of the vehicle's current journey. Every way a
    /// journey closes leaves nothing of it on board as this server records it -- the last demand unloaded, handed off, its load
    /// cancelled or compensated empty, or ended before it was loaded -- so the binding is not the current journey's cargo.
    /// </summary>
    public const string NotCargoOfTheCurrentJourneyReason = "NOT_CARGO_OF_THE_VEHICLES_CURRENT_JOURNEY";

    /// <summary>
    /// Whether a demand in this status may be on the vehicle: anything but "still to load" and the two endings. Loading counts --
    /// a load in flight is cargo whose state is unknown, and REQ-0238 counts unknown as loaded.
    /// </summary>
    public static bool MayBeOnBoard(string status) =>
        status is not (JourneyDemandStatuses.PendingLoad or JourneyDemandStatuses.Unloaded or JourneyDemandStatuses.Terminated);

    /// <summary>
    /// Releases this vehicle's live bindings that name a demand of <paramref name="journeyId"/>, when no demand of that journey is
    /// left on board. Statuses changed in the caller's context and not yet saved count: the query returns the tracked rows.
    /// </summary>
    /// <returns>The bindings released, staged.</returns>
    public static async Task<IReadOnlyList<FaultedVehicleCargoRow>> StageReleaseWhenNothingLeftOnBoardAsync(
        ControlServerDbContext dbContext,
        string journeyId,
        string agvId,
        string reason,
        DateTimeOffset releasedAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        List<JourneyDemandRow> memberships = await dbContext.Set<JourneyDemandRow>()
            .Where(row => row.JourneyId == journeyId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        // Judged on the tracked rows, in memory: the unload or the termination that brought the caller here is not saved yet.
        if (memberships.Any(row => row.RemovedAt == null && MayBeOnBoard(row.Status)))
        {
            return [];
        }

        HashSet<string> demands = memberships.Select(row => row.DemandId).ToHashSet(StringComparer.Ordinal);
        return await StageReleaseAsync(
            dbContext, agvId, binding => demands.Contains(binding.DemandId), reason, releasedAt, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Releases this vehicle's live bindings that belong to a journey other than its current one (see
    /// <see cref="NotCargoOfTheCurrentJourneyReason"/>): the demand a binding names is not an active member of a journey of this
    /// vehicle that is still open, and a journey it belonged to has closed. A binding with no such evidence -- its demand in no
    /// journey at all, or still in an open journey elsewhere -- is kept: without evidence the cargo may be there.
    /// </summary>
    /// <returns>The bindings released, staged.</returns>
    public static async Task<IReadOnlyList<FaultedVehicleCargoRow>> StageReleaseOfOtherJourneysCargoAsync(
        ControlServerDbContext dbContext,
        string agvId,
        DateTimeOffset releasedAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        string[] named = await dbContext.FaultedVehicleCargo
            .Where(row => row.AgvId == agvId && row.ReleasedAt == null)
            .Select(row => row.DemandId)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        if (named.Length == 0)
        {
            return [];
        }

        var journeys = await (
                from membership in dbContext.Set<JourneyDemandRow>()
                join journey in dbContext.JourneyRuntimes on membership.JourneyId equals journey.JourneyId
                where named.Contains(membership.DemandId)
                select new { membership.DemandId, membership.RemovedAt, journey.AgvId, journey.Stage })
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        HashSet<string> current = journeys
            .Where(row => row.AgvId == agvId && row.Stage != JourneyRuntimeStage.Completed && row.RemovedAt == null)
            .Select(row => row.DemandId)
            .ToHashSet(StringComparer.Ordinal);
        HashSet<string> closed = journeys
            .Where(row => row.Stage == JourneyRuntimeStage.Completed)
            .Select(row => row.DemandId)
            .ToHashSet(StringComparer.Ordinal);
        HashSet<string> stillOpenElsewhere = journeys
            .Where(row => row.Stage != JourneyRuntimeStage.Completed && row.RemovedAt == null)
            .Select(row => row.DemandId)
            .ToHashSet(StringComparer.Ordinal);

        return await StageReleaseAsync(
            dbContext,
            agvId,
            binding => !current.Contains(binding.DemandId) && closed.Contains(binding.DemandId) &&
                       !stillOpenElsewhere.Contains(binding.DemandId),
            NotCargoOfTheCurrentJourneyReason,
            releasedAt,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<FaultedVehicleCargoRow>> StageReleaseAsync(
        ControlServerDbContext dbContext,
        string agvId,
        Func<FaultedVehicleCargoRow, bool> releases,
        string reason,
        DateTimeOffset releasedAt,
        CancellationToken cancellationToken)
    {
        List<FaultedVehicleCargoRow> released = [];
        foreach (FaultedVehicleCargoRow binding in await dbContext.FaultedVehicleCargo
                     .Where(row => row.AgvId == agvId && row.ReleasedAt == null)
                     .ToArrayAsync(cancellationToken).ConfigureAwait(false))
        {
            if (releases(binding))
            {
                binding.ReleasedAt = releasedAt;
                binding.ReleasedReason = reason;
                released.Add(binding);
            }
        }

        return released;
    }
}
