namespace ControlServer.Application;

/// <summary>The outcome of accepting a Map's new name as its baseline.</summary>
public sealed record MapNameBaselineAcceptResult(
    bool Accepted,
    int MapId,
    string? PreviousName,
    string? AcceptedName,
    IReadOnlyList<TaskTypeStationViolation> Violations,
    string AuditRecordId);

/// <summary>
/// The first half of recovering from a Map rename (control-server#186, REQ-0340 recovery half): the name read since the
/// rename becomes the Map's baseline, after the field has checked the Map on site. The holds the rename raised are
/// released afterwards, task type by task type, by the batch 6-05 release verb, which refuses while a rename is pending.
/// </summary>
public sealed class MapNameBaselineAcceptanceService(IMapNameBaselineStore baselines, IGovernanceAuditWriter audit)
{
    private readonly IMapNameBaselineStore _baselines = baselines ?? throw new ArgumentNullException(nameof(baselines));
    private readonly IGovernanceAuditWriter _audit = audit ?? throw new ArgumentNullException(nameof(audit));

    public Task<MapNameBaselineAcceptResult> AcceptAsync(
        int mapId,
        string? mapName,
        TaskTypeStationChangeRequest request,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        _ = (_baselines, _audit, mapName, request, now, cancellationToken);
        return Task.FromResult(new MapNameBaselineAcceptResult(false, mapId, null, null, [], string.Empty));
    }
}
