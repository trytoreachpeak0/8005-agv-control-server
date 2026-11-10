using ControlServer.Infrastructure.Persistence;

namespace ControlServer.Host.Runtime.Dispatch;

/// <summary>
/// Deletes backlog rows of demands that left the MesIngest catalog long ago and that nothing else refers to (control-server#881).
/// </summary>
/// <remarks>Skeleton on the test commit; the implementation commit fills it in.</remarks>
public sealed class JourneyBacklogRetention
{
    /// <summary>How often a round sweeps at most.</summary>
    public static readonly TimeSpan SweepInterval = TimeSpan.FromHours(1);

    /// <summary>Deletes every row that qualifies; returns how many.</summary>
    public static Task<int> PurgeAsync(
        ControlServerDbContext dbContext,
        IReadOnlySet<string> listed,
        DateTimeOffset now,
        TimeSpan retention,
        CancellationToken cancellationToken) => Task.FromResult(0);

    /// <summary>Purges when a sweep is due; null when it was not.</summary>
    public Task<int?> SweepIfDueAsync(
        ControlServerDbContext dbContext,
        IReadOnlySet<string> listed,
        DateTimeOffset now,
        TimeSpan retention,
        CancellationToken cancellationToken) => Task.FromResult<int?>(null);
}
