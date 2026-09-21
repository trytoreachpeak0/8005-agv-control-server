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
        return Key(x).CompareTo(Key(y));
    }

    // 不知道建单时刻的（MesIngest 没给，审查低 3）按零岁算，排在同带里所有知道的之后，而不是凭 0001-01-01 排到最前。
    private static DateTimeOffset Key(DispatchTask task)
    {
        return TaskStarvation.HasLocalCreation(task.Snapshot) ? task.Snapshot.CreatedAt : DateTimeOffset.MaxValue;
    }
}
