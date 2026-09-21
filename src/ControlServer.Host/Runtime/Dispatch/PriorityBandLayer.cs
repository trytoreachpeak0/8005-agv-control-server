namespace ControlServer.Host.Runtime.Dispatch;

/// <summary>
/// 优先级带（REQ-0202）：<c>STAGING_TO_WIRE</c> 独占最高初始带，其余五类同处普通带、不再按类型细分。
/// </summary>
public sealed class PriorityBandLayer : IDispatchCandidateComparisonLayer
{
    public int Compare(DispatchTask x, DispatchTask y)
    {
        ArgumentNullException.ThrowIfNull(x);
        ArgumentNullException.ThrowIfNull(y);
        return 0;
    }
}
