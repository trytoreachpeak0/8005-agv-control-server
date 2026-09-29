using ControlServer.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ControlServer.Infrastructure.Persistence;

/// <summary>
/// 地图名基线的存取（control-server#186），每个 <c>mapId</c> 一行。只存取，不判断什么算改名——那是
/// <c>MapRenameHoldConvergence</c> 的事。
/// </summary>
/// <remarks>
/// 写都是单条条件语句，不是先读后存：调用方在自己的写事务里读、判断、写（SQLite <c>BEGIN IMMEDIATE</c>，写者串行），
/// 条件语句只是再多一道——万一有人不在事务里调，也不会覆盖掉别人刚写的。
/// </remarks>
public sealed class MapNameBaselineStore(ControlServerDbContext context, IGovernanceAuditWriter audit) : IMapNameBaselineStore
{
    private readonly ControlServerDbContext _context = context ?? throw new ArgumentNullException(nameof(context));
    private readonly IGovernanceAuditWriter _audit = audit ?? throw new ArgumentNullException(nameof(audit));

    public async Task<MapNameBaseline?> ReadAsync(int mapId, CancellationToken cancellationToken)
    {
        MapNameBaselineRow? row = await _context.Set<MapNameBaselineRow>()
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.MapId == mapId, cancellationToken);
        return row is null ? null : Project(row);
    }

    public async Task<bool> EstablishAsync(int mapId, string name, DateTimeOffset at, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        // INSERT OR IGNORE through the primary key: a second writer that also found no row inserts nothing, and the name
        // that landed first stays the baseline.
        int inserted = await _context.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT OR IGNORE INTO MapNameBaselines (MapId, Name, EstablishedAt) VALUES ({mapId}, {name}, {at})",
            cancellationToken);
        return inserted == 1;
    }

    public async Task<bool> SetPendingAsync(
        int mapId,
        string? pendingName,
        DateTimeOffset? pendingSince,
        CancellationToken cancellationToken)
    {
        int updated = await _context.Set<MapNameBaselineRow>()
            .Where(row => row.MapId == mapId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(row => row.PendingName, pendingName)
                    .SetProperty(row => row.PendingSince, pendingSince),
                cancellationToken);
        return updated == 1;
    }

    public async Task<string?> AcceptAsync(
        int mapId,
        string acceptedName,
        string acceptedByPrefix,
        Func<MapNameBaseline, GovernanceAuditEntry> audit,
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(acceptedName);
        ArgumentException.ThrowIfNullOrWhiteSpace(acceptedByPrefix);
        ArgumentNullException.ThrowIfNull(audit);
        try
        {
            // The acceptance and its audit are one transaction: an accepted name nobody recorded did not happen, and a
            // recorded acceptance of a name that was no longer pending must not stand either.
            await using IDbContextTransaction transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
            MapNameBaselineRow? row = await _context.Set<MapNameBaselineRow>()
                .AsNoTracking()
                .SingleOrDefaultAsync(candidate => candidate.MapId == mapId, cancellationToken);
            if (row is null || !string.Equals(row.PendingName, acceptedName, StringComparison.Ordinal))
            {
                return null;
            }
            string auditId = await _audit.WriteBusinessAsync(audit(Project(row)), at, cancellationToken);
            int updated = await _context.Set<MapNameBaselineRow>()
                .Where(candidate => candidate.MapId == mapId && candidate.PendingName == acceptedName)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(candidate => candidate.Name, acceptedName)
                        .SetProperty(candidate => candidate.PendingName, (string?)null)
                        .SetProperty(candidate => candidate.PendingSince, (DateTimeOffset?)null)
                        .SetProperty(candidate => candidate.AcceptedAt, at)
                        .SetProperty(candidate => candidate.AcceptedBy, acceptedByPrefix + auditId),
                    cancellationToken);
            if (updated != 1)
            {
                // Cannot happen under BEGIN IMMEDIATE, whose read above decided this; if it does, nothing stands.
                await transaction.RollbackAsync(cancellationToken);
                return null;
            }
            await transaction.CommitAsync(cancellationToken);
            return auditId;
        }
        catch
        {
            _context.ChangeTracker.Clear();
            throw;
        }
    }

    private static MapNameBaseline Project(MapNameBaselineRow row) =>
        new(row.MapId, row.Name, row.EstablishedAt, row.PendingName, row.PendingSince, row.AcceptedAt, row.AcceptedBy);
}
