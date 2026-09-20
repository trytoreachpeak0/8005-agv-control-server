namespace ControlServer.Host.Runtime.Dispatch;

/// <summary>The candidate this server has known about longest is taken first.</summary>
public sealed class FirstSeenLayer : IDispatchCandidateComparisonLayer
{
    public int Compare(DispatchTask x, DispatchTask y)
    {
        ArgumentNullException.ThrowIfNull(x);
        ArgumentNullException.ThrowIfNull(y);
        return x.FirstSeenAt.CompareTo(y.FirstSeenAt);
    }
}
