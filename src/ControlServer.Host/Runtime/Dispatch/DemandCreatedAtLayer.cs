namespace ControlServer.Host.Runtime.Dispatch;

/// <summary>
/// The waiting age (REQ-0201): the demand created locally earlier -- MesIngest's <c>CreatedAt</c>, when it created the
/// TransportDemand, not MES's own dates -- is taken first. Ahead of <see cref="FirstSeenLayer"/> since control-server#214.
/// </summary>
public sealed class DemandCreatedAtLayer : IDispatchCandidateComparisonLayer
{
    public int Compare(DispatchTask x, DispatchTask y)
    {
        ArgumentNullException.ThrowIfNull(x);
        ArgumentNullException.ThrowIfNull(y);
        return x.Snapshot.CreatedAt.CompareTo(y.Snapshot.CreatedAt);
    }
}
