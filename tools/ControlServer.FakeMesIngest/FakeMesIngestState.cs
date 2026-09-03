namespace ControlServer.FakeMesIngest;

/// <summary>
/// One demand as the externally readable catalog reports it. Field names and shapes are the ones
/// <c>HttpMesIngestCatalog</c> parses; the control server validates the catalog's internal identity
/// hard (sorted unique demand ids, count matching items, an ETag derived from the body), so this
/// double must build those rather than hand-wave them.
/// </summary>
public sealed record FakeDemand
{
    /// <summary>
    /// MesIngest reports demand ids unhyphenated. Keeping that here is deliberate: the control
    /// server normalises them on the way in, and a double that emitted the canonical spelling
    /// would quietly stop covering the code that does it.
    /// </summary>
    public required string DemandId { get; init; }
    public required string SeriesId { get; init; }
    public string WorkType { get; init; } = "WIRE_TO_GATE";
    public required string Sublot { get; init; }
    public int Generation { get; init; } = 1;
    public long DemandRevision { get; init; } = 1;
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset ValueObservedAt { get; init; }
    public required string ValuePollTraceId { get; init; }
    public required string ValueProjectionCommitId { get; init; }
    public required string Area { get; init; }
    public required string Eqp { get; init; }
    public string? Step { get; init; }
    public DateTimeOffset? MesSourceDate { get; init; }
    public required string Package { get; init; }

    /// <summary>Box count this sublot answers on SUBLOT_BOX_COUNT. Must be positive.</summary>
    public int MaxBoxCount { get; init; } = 4;
}

public sealed record FakeMesIngestState
{
    public required string HistoryEpoch { get; init; }
    public required long CatalogRevision { get; init; }
    public required IReadOnlyList<FakeDemand> Demands { get; init; }

    /// <summary>
    /// When true, contract discovery answers a version the control server does not accept. The
    /// catalog read is gated on discovery, so this is how a scenario proves the runtime fails
    /// closed on a MesIngest contract change rather than reading an unverified catalog.
    /// </summary>
    public bool BreakContract { get; init; }
}
