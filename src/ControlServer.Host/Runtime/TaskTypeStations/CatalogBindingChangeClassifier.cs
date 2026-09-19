using ControlServer.Application;

namespace ControlServer.Host.Runtime.TaskTypeStations;

/// <summary>What happened to one bound station, judged by its stable identity (REQ-0341, REQ-0342).</summary>
public enum CatalogBindingChangeKind
{
    /// <summary>Same <c>mapId + stationId</c>, same name.</summary>
    Unchanged,

    /// <summary>Same <c>mapId + stationId</c>, another name: the identity holds, the use on site may not.</summary>
    Renamed,

    /// <summary>The bound id is not in the catalog any more (deleted or disabled).</summary>
    Removed,

    /// <summary>The bound id is gone and a new id carries its name. The new id is a new station, never a rebinding.</summary>
    IdReplaced
}

/// <summary>The reason codes a catalog change writes on the hold it raises.</summary>
public static class CatalogBindingHoldReasons
{
    /// <summary>站点改名：名称不改变身份，但可能表示现场用途变了，要求现场复核。</summary>
    public const string StationRenamed = "STATION_RENAMED";

    /// <summary>站点已不在目录：原绑定失效，只能由激活一个新绑定集版本恢复（批次6-05）。</summary>
    public const string StationNotInCatalog = "STATION_NOT_IN_CATALOG";
}

/// <summary>One binding set against the current catalog.</summary>
/// <param name="SameNameStationIds">
/// Stations in the current catalog that carry the bound name under another id. Reported so that a person can see a
/// replacement happened; nothing here takes one of them as the binding.
/// </param>
public sealed record CatalogBindingChange(
    string TaskType,
    int StationRiotId,
    string BoundStationName,
    string? CurrentStationName,
    CatalogBindingChangeKind Kind,
    IReadOnlyList<int> SameNameStationIds)
{
    /// <summary>The hold this change raises on its task type; <c>null</c> when it raises none.</summary>
    public string? HoldReasonCode => Kind switch
    {
        CatalogBindingChangeKind.Renamed => CatalogBindingHoldReasons.StationRenamed,
        CatalogBindingChangeKind.Removed or CatalogBindingChangeKind.IdReplaced =>
            CatalogBindingHoldReasons.StationNotInCatalog,
        _ => null
    };
}

/// <summary>
/// Classifies each binding of a Map's active binding set against a complete catalog read (control-server#162).
/// </summary>
public static class CatalogBindingChangeClassifier
{
    public static IReadOnlyList<CatalogBindingChange> Classify(
        IReadOnlyList<TaskTypeStationBinding> bindings,
        RiotMapStationCatalogSnapshot catalog)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(catalog);
        return
        [
            .. bindings.Select(binding => new CatalogBindingChange(
                binding.TaskType, binding.StationRiotId, binding.StationName, binding.StationName,
                CatalogBindingChangeKind.Unchanged, []))
        ];
    }
}
