namespace ControlServer.Host.Runtime.Dispatch;

/// <summary>Of two candidates first seen together, the demand MES created earlier is taken first.</summary>
public sealed class DemandCreatedAtLayer : IDispatchCandidateComparisonLayer
{
    public int Compare(EligibleDispatchCandidate x, EligibleDispatchCandidate y)
    {
        ArgumentNullException.ThrowIfNull(x);
        ArgumentNullException.ThrowIfNull(y);
        return x.Snapshot.CreatedAt.CompareTo(y.Snapshot.CreatedAt);
    }
}
