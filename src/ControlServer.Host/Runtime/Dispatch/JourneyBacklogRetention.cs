using System.Globalization;
using System.Text.Json;
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
/// demand; and <see cref="JourneyBacklogRow.LastSeenAt"/> strictly longer ago than the retention. On a row carrying that reason
/// <see cref="JourneyBacklogRow.LastSeenAt"/> is when the round marked it, that is when the demand left the catalog
/// (control-server#881 review S1): before the mark it is the last time a vehicle judged the demand, which can be days earlier.
/// A row marked before this change keeps its older value, so on the first sweep after the deploy such a row may go up to the
/// gap between its last judgement and its leaving early. The SQL conditions are repeated in the delete itself, so a demand
/// accepted between the read and the delete by another connection keeps its row.
/// </para>
/// <para>
/// <b>A row whose <see cref="JourneyBacklogRow.LastSeenAt"/> cannot be read is kept</b> and reported back
/// (<see cref="JourneyBacklogPurge.Unreadable"/>), rather than failing the whole sweep every hour (review L4).
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

    /// <summary>Deletes every row that qualifies (see the type's remarks); returns how many, and which it could not judge.</summary>
    public static async Task<JourneyBacklogPurge> PurgeAsync(
        ControlServerDbContext dbContext,
        IReadOnlySet<string> listed,
        DateTimeOffset now,
        TimeSpan retention,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(listed);
        // LastSeenAt is a DateTimeOffset, which SQLite cannot compare in the store: the SQL narrows to the dead, unreferenced
        // rows and the age is judged here, from the stored text, one row at a time.
        string[] dead = await Unreferenced(dbContext)
            .AsNoTracking()
            .Select(row => row.DemandId)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        List<string> expired = [];
        List<string> unreadable = [];
        foreach (string[] batch in dead.Where(id => !listed.Contains(id)).Chunk(DeleteBatch))
        {
            StoredLastSeen[] stored = await dbContext.Database
                .SqlQueryRaw<StoredLastSeen>(
                    "SELECT \"DemandId\", CAST(\"LastSeenAt\" AS TEXT) AS \"LastSeenAt\" FROM \"JourneyBacklog\" " +
                    "WHERE \"DemandId\" IN (SELECT \"value\" FROM json_each({0}))",
                    JsonSerializer.Serialize(batch))
                .ToArrayAsync(cancellationToken)
                .ConfigureAwait(false);
            foreach (StoredLastSeen row in stored)
            {
                if (!DateTimeOffset.TryParse(row.LastSeenAt, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset lastSeenAt))
                {
                    unreadable.Add(row.DemandId);
                }
                else if (now - lastSeenAt > retention)
                {
                    expired.Add(row.DemandId);
                }
            }
        }
        int deleted = 0;
        foreach (string[] batch in expired.Chunk(DeleteBatch))
        {
            deleted += await Unreferenced(dbContext)
                .Where(row => batch.Contains(row.DemandId))
                .ExecuteDeleteAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        return new JourneyBacklogPurge(deleted, unreadable);
    }

    /// <summary>Purges when a sweep is due, and marks it done whether or not it succeeds; null when it was not due.</summary>
    public async Task<JourneyBacklogPurge?> SweepIfDueAsync(
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

    /// <summary>One row's id and its <c>LastSeenAt</c> as stored.</summary>
    private sealed class StoredLastSeen
    {
        public string DemandId { get; set; } = "";

        public string? LastSeenAt { get; set; }
    }
}

/// <summary>What one purge did: how many rows it deleted, and the dead rows it kept because their LastSeenAt could not be read.</summary>
public sealed record JourneyBacklogPurge(int Deleted, IReadOnlyList<string> Unreadable);
