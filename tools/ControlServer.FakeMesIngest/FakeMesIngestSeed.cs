namespace ControlServer.FakeMesIngest;

/// <summary>
/// The safe initial state every round starts from. Empty by design: a scenario says which demands
/// exist, and a double that shipped a demand nobody asked for would make "no demand was accepted"
/// impossible to assert.
/// </summary>
public sealed class FakeMesIngestSeed
{
    /// <summary>
    /// Fixed rather than random so a re-run produces the same catalog identity and an evidence
    /// diff between two runs shows behaviour, not fresh GUIDs.
    /// </summary>
    public string HistoryEpoch { get; set; } = "8f14e45f-ea4e-4c1b-9a2b-000000000001";

    public FakeMesIngestState BuildInitialState() => new()
    {
        HistoryEpoch = HistoryEpoch,
        CatalogRevision = 1,
        Demands = []
    };
}
