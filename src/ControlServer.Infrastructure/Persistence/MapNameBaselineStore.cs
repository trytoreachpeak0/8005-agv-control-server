using ControlServer.Application;

namespace ControlServer.Infrastructure.Persistence;

/// <summary>The Map name baselines (control-server#186), one row per <c>mapId</c>.</summary>
public sealed class MapNameBaselineStore(ControlServerDbContext context, IGovernanceAuditWriter audit) : IMapNameBaselineStore
{
    private readonly ControlServerDbContext _context = context ?? throw new ArgumentNullException(nameof(context));
    private readonly IGovernanceAuditWriter _audit = audit ?? throw new ArgumentNullException(nameof(audit));

    public Task<MapNameBaseline?> ReadAsync(int mapId, CancellationToken cancellationToken)
    {
        _ = (_context, _audit, mapId, cancellationToken);
        return Task.FromResult<MapNameBaseline?>(null);
    }

    public Task<bool> EstablishAsync(int mapId, string name, DateTimeOffset at, CancellationToken cancellationToken) =>
        Task.FromResult(false);

    public Task<bool> SetPendingAsync(
        int mapId,
        string? pendingName,
        DateTimeOffset? pendingSince,
        CancellationToken cancellationToken) =>
        Task.FromResult(false);

    public Task<string?> AcceptAsync(
        int mapId,
        string acceptedName,
        string acceptedByPrefix,
        Func<MapNameBaseline, GovernanceAuditEntry> audit,
        DateTimeOffset at,
        CancellationToken cancellationToken) =>
        Task.FromResult<string?>(null);
}
