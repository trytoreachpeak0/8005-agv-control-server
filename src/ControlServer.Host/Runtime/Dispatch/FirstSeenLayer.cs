namespace ControlServer.Host.Runtime.Dispatch;

/// <summary>
/// Of two demands created at the same instant, the one this server has known about longest is taken first. A tie-break
/// only since control-server#214: see <see cref="TaskStarvation"/> for why the waiting age does not run from it.
/// </summary>
public sealed class FirstSeenLayer : IDispatchCandidateComparisonLayer
{
    public int Compare(DispatchTask x, DispatchTask y)
    {
        ArgumentNullException.ThrowIfNull(x);
        ArgumentNullException.ThrowIfNull(y);
        return x.FirstSeenAt.CompareTo(y.FirstSeenAt);
    }
}
