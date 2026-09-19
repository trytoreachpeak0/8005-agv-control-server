using ControlServer.Domain;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Infrastructure.Persistence;

/// <summary>
/// The one answer to "which journey carries this demand" and "which demands does this journey carry" (control-server#207):
/// the demand memberships in force in <c>JourneyDemands</c>, never the journey row's anchor column
/// <see cref="JourneyRuntimeRow.DemandId"/>.
/// </summary>
/// <remarks>
/// <para>
/// Until batch 7 a journey carried exactly one demand, and a dozen places found it by <c>JourneyRuntimes.DemandId</c>. In a
/// single-demand journey the two answers are the same row, which is why moving every one of those reads here changes
/// nothing observable. In a journey of several demands only the membership answers for any demand but the anchor.
/// </para>
/// <para>
/// The queries are composable rather than executed here, so that each caller keeps what it did before: tracked or not,
/// single or any, joined into a larger query. They read the same memberships, under the same "not removed" rule, as
/// <see cref="JourneyMembershipStore.FindJourneyIdByDemandAsync"/>.
/// </para>
/// </remarks>
public static class DemandJourneyLookup
{
    /// <summary>The tag on the "is this the last open demand" read, so a test can find that one command.</summary>
    public const string LastOpenDemandTag = "control-server#207 last open demand of journey";

    /// <summary>The memberships in force: not removed.</summary>
    public static IQueryable<JourneyDemandRow> Memberships(ControlServerDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        return dbContext.Set<JourneyDemandRow>().Where(row => row.RemovedAt == null);
    }

    /// <summary>The journey this demand currently belongs to: none, or one.</summary>
    public static IQueryable<JourneyRuntimeRow> JourneyOf(ControlServerDbContext dbContext, string demandId)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentException.ThrowIfNullOrWhiteSpace(demandId);
        IQueryable<JourneyDemandRow> memberships = Memberships(dbContext);
        return dbContext.JourneyRuntimes.Where(journey => memberships.Any(
            membership => membership.JourneyId == journey.JourneyId && membership.DemandId == demandId));
    }

    /// <summary>The id of the journey this demand currently belongs to: none, or one.</summary>
    public static IQueryable<string> JourneyIdOf(ControlServerDbContext dbContext, string demandId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(demandId);
        return Memberships(dbContext).Where(row => row.DemandId == demandId).Select(row => row.JourneyId);
    }

    /// <summary>The demands this journey currently carries.</summary>
    public static IQueryable<string> DemandIdsOf(ControlServerDbContext dbContext, string journeyId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(journeyId);
        return Memberships(dbContext).Where(row => row.JourneyId == journeyId).Select(row => row.DemandId);
    }

    /// <summary>
    /// Whether <paramref name="demandId"/> is the journey's last open demand: no other demand it carries is still open,
    /// that is neither <see cref="DemandExecutionStatus.Succeeded"/> nor <see cref="DemandExecutionStatus.Cancelled"/>.
    /// </summary>
    /// <remarks>
    /// <b>Read it inside the write transaction whose save acts on the answer.</b> Two demands of one journey can end at the
    /// same moment on two connections -- an unload result on one, a cancellation on the other. Read outside the write lock,
    /// each sees the other still open, neither is last, and the journey is left with every demand ended and its vehicle
    /// still held. <c>BeginTransaction</c> on this store is <c>BEGIN IMMEDIATE</c>, so a read inside it sees every write
    /// committed before it and the second writer waits for the first.
    /// </remarks>
    public static async Task<bool> IsLastOpenDemandAsync(
        ControlServerDbContext dbContext,
        string journeyId,
        string demandId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(journeyId);
        ArgumentException.ThrowIfNullOrWhiteSpace(demandId);
        bool anotherOpen = await Memberships(dbContext)
            .Where(row => row.JourneyId == journeyId && row.DemandId != demandId)
            .Join(dbContext.AcceptedDemands, membership => membership.DemandId, demand => demand.DemandId,
                (membership, demand) => demand.Status)
            .TagWith(LastOpenDemandTag)
            .AnyAsync(
                status => status != DemandExecutionStatus.Succeeded && status != DemandExecutionStatus.Cancelled,
                cancellationToken)
            .ConfigureAwait(false);
        return !anotherOpen;
    }
}
