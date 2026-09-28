namespace ControlServer.Application;

/// <summary>One Map as RIoT's Map list reports it: its stable id and its display name (control-server#186).</summary>
public sealed record RiotMapName(int MapId, string Name);

/// <summary>
/// RIoT's Map list read in one call, without any Map's <c>mapJson</c>
/// (<c>GET /api/imap/v1/mapInfo/getALLMapInfoExcludeMapJson</c>, verified on the real RIoT on 2026-09-28: the name of
/// map 26 there is byte for byte the one <c>mapInfo/26</c> reports).
/// </summary>
public sealed record RiotMapNameListing(DateTimeOffset ObservedAt, IReadOnlyList<RiotMapName> Maps);

/// <summary>Reads every Map's name from RIoT. A failed read throws; it never returns a partial or empty guess.</summary>
public interface IRiotMapNameCatalog
{
    Task<RiotMapNameListing> ReadMapNamesAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The name a Map was first seen under, per <c>mapId</c> (REQ-0341: a Map renamed under the same <c>mapId</c> holds all of
/// its public station bindings for review). <see cref="PendingName"/> is a different name read since, not yet accepted on
/// site; while it is set, the Map's task types stay held.
/// </summary>
/// <remarks>
/// Deliberately unrelated to <c>JourneyRuntime:mapIdentity</c>: that one is compared with the vehicles' reported
/// <c>CurrentMap</c> and nothing else, and on site the two are not even the same literal (「老厂前线new」 against RIoT's
/// 「老厂前线new_wk」 for map 26). Neither is ever the other's baseline or initial value.
/// </remarks>
public sealed record MapNameBaseline(
    int MapId,
    string Name,
    DateTimeOffset EstablishedAt,
    string? PendingName,
    DateTimeOffset? PendingSince,
    DateTimeOffset? AcceptedAt,
    string? AcceptedBy);

/// <summary>
/// Storage of the Map name baselines. Only stores; when a name counts as a rename is the convergence's business. Every
/// write is meant to run inside the caller's transaction, whose reads decided it (SQLite <c>BEGIN IMMEDIATE</c>: writers
/// are serial, so a decision read inside the transaction cannot be overtaken before it commits).
/// </summary>
public interface IMapNameBaselineStore
{
    Task<MapNameBaseline?> ReadAsync(int mapId, CancellationToken cancellationToken);

    /// <summary>Records the first name seen for a Map. Returns <c>false</c>, writing nothing, when it already has one.</summary>
    Task<bool> EstablishAsync(int mapId, string name, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>Sets or clears the pending name. Returns <c>false</c> when the Map has no baseline.</summary>
    Task<bool> SetPendingAsync(
        int mapId,
        string? pendingName,
        DateTimeOffset? pendingSince,
        CancellationToken cancellationToken);

    /// <summary>
    /// Makes the pending name the baseline, in one transaction with its audit, and only if the pending name is still
    /// exactly <paramref name="acceptedName"/>. Returns the audit record id, or <c>null</c> -- nothing written, not even
    /// the audit -- when it is not.
    /// </summary>
    Task<string?> AcceptAsync(
        int mapId,
        string acceptedName,
        string acceptedByPrefix,
        Func<MapNameBaseline, GovernanceAuditEntry> audit,
        DateTimeOffset at,
        CancellationToken cancellationToken);
}

/// <summary>The hold reason a Map level rename raises, under source <c>CATALOG_CHANGE</c>.</summary>
public static class MapNameHoldReasons
{
    public const string MapRenamed = "MAP_RENAMED";
}

/// <summary>Business audit actions of the Map name baseline.</summary>
public static class MapNameBaselineAuditActions
{
    /// <summary>A name different from the baseline was read for the first time (or changed again while pending).</summary>
    public const string RenameDetected = "MAP_RENAME_DETECTED";

    public const string Accepted = "MAP_NAME_BASELINE_ACCEPTED";

    public const string AcceptRejected = "MAP_NAME_BASELINE_ACCEPT_REJECTED";
}

/// <summary>Why accepting a new Map name, or releasing a hold under an unaccepted rename, is refused.</summary>
public static class MapNameBaselineReasonCodes
{
    public const string BaselineMissing = "MAP_NAME_BASELINE_MISSING";

    public const string NoPendingRename = "MAP_RENAME_NOT_PENDING";

    public const string NameMismatch = "MAP_RENAME_NAME_MISMATCH";

    /// <summary>A hold release asked while the Map still carries a rename nobody accepted.</summary>
    public const string RenameNotAccepted = "MAP_RENAME_NOT_ACCEPTED";
}
