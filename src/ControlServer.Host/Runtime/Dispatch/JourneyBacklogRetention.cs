using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Host.Runtime.Dispatch;

/// <summary>
/// Deletes backlog rows of demands that left the MesIngest catalog long ago and that nothing else refers to (control-server#881).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why.</b> A demand MES closes before this server takes it keeps its backlog row, marked
/// <see cref="DispatchReasonCodes.DemandLeftCatalog"/>, and nothing ever removed it. On 2026-11-05 the field table held 50,615 rows,
/// 43,400 of them such rows, growing by about 3,000 a day; the dispatch round read the whole table, tracked, twice a round, and
/// every save of the round scanned it. The round no longer reads them (it loads only what this round's catalog lists), so this is
/// about keeping the table from growing without bound -- and the dashboard, which reads every row, quick.
/// </para>
/// <para>
/// <b>What qualifies, every condition at once</b>, at least as conservative as the deletion done by hand on the field database that
/// day: never accepted (<see cref="JourneyBacklogRow.AcceptedAt"/> is null); reason <see cref="DispatchReasonCodes.DemandLeftCatalog"/>;
/// not listed in this round's catalog; no <c>AcceptedDemands</c> row and no <c>JourneyDemands</c> row, removed or not, for the
/// demand; and <see cref="JourneyBacklogRow.LastSeenAt"/> -- the last time the catalog listed it -- strictly longer ago than the
/// retention. The SQL conditions are repeated in the delete itself, so a demand accepted between the read and the delete by
/// another connection keeps its row.
/// </para>
/// <para>
/// <b>What deleting one forgets.</b> If the demand comes back into the catalog, its row is written again as new: its
/// <see cref="JourneyBacklogRow.FirstSeenAt"/> restarts and a starvation escalation already raised for it may be raised again.
/// That is why the retention has a floor (<see cref="JourneyRuntimeOptions.BacklogRetention"/>) and defaults to days.
/// </para>
/// <para>
/// <b>Where it runs.</b> In the dispatch round, before the backlog is loaded, on the round's own context, so a row it deletes is
/// never one the round holds: the round only loads rows of demands this round's catalog lists, and those are kept. The other
/// writers of the backlog -- acceptance in <c>WireToGateStore</c>, release in <c>DemandReleaseService</c> -- only touch a demand
/// that has a membership, which keeps its row, and both treat a missing row as nothing to update. At most once per
/// <see cref="SweepInterval"/>; the last sweep's time outlives the round's scope, hence a singleton.
/// </para>
/// </remarks>
public sealed class JourneyBacklogRetention
{
    /// <summary>How often a round sweeps at most.</summary>
    public static readonly TimeSpan SweepInterval = TimeSpan.FromHours(1);

    /// <summary>Ids deleted per statement, so no single write holds the database long.</summary>
    private const int DeleteBatch = 500;

    private readonly object gate = new();
    private DateTimeOffset? lastSweptAt;

    /// <summary>Deletes every row that qualifies (see the type's remarks); returns how many.</summary>
    public static async Task<int> PurgeAsync(
        ControlServerDbContext dbContext,
        IReadOnlySet<string> listed,
        DateTimeOffset now,
        TimeSpan retention,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(listed);
        // LastSeenAt is a DateTimeOffset, which SQLite cannot compare in the store: the SQL narrows to the dead, unreferenced
        // rows and the age is judged here.
        var dead = await Unreferenced(dbContext)
            .AsNoTracking()
            .Select(row => new { row.DemandId, row.LastSeenAt })
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        string[] expired = [.. dead
            .Where(row => now - row.LastSeenAt > retention && !listed.Contains(row.DemandId))
            .Select(row => row.DemandId)];
        int deleted = 0;
        foreach (string[] batch in expired.Chunk(DeleteBatch))
        {
            deleted += await Unreferenced(dbContext)
                .Where(row => batch.Contains(row.DemandId))
                .ExecuteDeleteAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        return deleted;
    }

    /// <summary>Purges when a sweep is due, and marks it done whether or not it succeeds; null when it was not due.</summary>
    public async Task<int?> SweepIfDueAsync(
        ControlServerDbContext dbContext,
        IReadOnlySet<string> listed,
        DateTimeOffset now,
        TimeSpan retention,
        CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (lastSweptAt is { } last && now - last < SweepInterval)
            {
                return null;
            }
            lastSweptAt = now;
        }
        return await PurgeAsync(dbContext, listed, now, retention, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The store-side conditions: never accepted, left the catalog, no demand row and no membership at all.</summary>
    private static IQueryable<JourneyBacklogRow> Unreferenced(ControlServerDbContext dbContext) =>
        dbContext.JourneyBacklog.Where(row =>
            row.AcceptedAt == null &&
            row.ReasonCode == DispatchReasonCodes.DemandLeftCatalog &&
            !dbContext.AcceptedDemands.Any(demand => demand.DemandId == row.DemandId) &&
            !dbContext.Set<JourneyDemandRow>().Any(membership => membership.DemandId == row.DemandId));
}
