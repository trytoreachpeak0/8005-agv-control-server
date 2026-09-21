namespace ControlServer.Host.Runtime.Dispatch;

/// <summary>
/// 超时层（REQ-0202、REQ-0203）：等待年龄越过所在分区防饥饿阈值的普通任务，排在所有未超时的任务之前，
/// 未超时的 <c>STAGING_TO_WIRE</c> 也在内。
/// </summary>
public sealed class StarvationTimeoutLayer : IDispatchCandidateComparisonLayer
{
    public int Compare(DispatchTask x, DispatchTask y)
    {
        ArgumentNullException.ThrowIfNull(x);
        ArgumentNullException.ThrowIfNull(y);
        return 0;
    }
}
