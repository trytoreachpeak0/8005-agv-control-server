namespace ControlServer.Host.Runtime.Dispatch;

/// <summary>The last word: the demand ids, compared ordinally.</summary>
/// <remarks>
/// This is what makes the ranking a total order rather than merely a stable one: two demands can share every timestamp
/// and cost, and a dispatch decision that depends on enumeration order is not reproducible from the evidence afterwards.
/// </remarks>
public sealed class DemandIdOrdinalLayer : IDispatchCandidateComparisonLayer
{
    public int Compare(DispatchTask x, DispatchTask y)
    {
        ArgumentNullException.ThrowIfNull(x);
        ArgumentNullException.ThrowIfNull(y);
        return string.CompareOrdinal(x.Snapshot.DemandId, y.Snapshot.DemandId);
    }
}
