namespace ControlServer.Host.Runtime.Dispatch;

/// <summary>
/// 优先级带（REQ-0202）：<c>STAGING_TO_WIRE</c> 独占最高初始带，其余五类同处普通带、不再按类型细分。
/// </summary>
/// <remarks>
/// 只看任务类型，不看处境：超时与否由排在它前面的 <see cref="StarvationTimeoutLayer"/> 决定，两层各答一个问题。
/// </remarks>
public sealed class PriorityBandLayer : IDispatchCandidateComparisonLayer
{
    public int Compare(DispatchTask x, DispatchTask y)
    {
        ArgumentNullException.ThrowIfNull(x);
        ArgumentNullException.ThrowIfNull(y);
        return TaskStarvation.InTopBand(y.Snapshot).CompareTo(TaskStarvation.InTopBand(x.Snapshot));
    }
}
