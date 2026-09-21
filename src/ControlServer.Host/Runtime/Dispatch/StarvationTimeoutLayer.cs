namespace ControlServer.Host.Runtime.Dispatch;

/// <summary>
/// 超时层（REQ-0202、REQ-0203）：等待年龄越过所在分区防饥饿阈值的普通任务，排在所有未超时的任务之前，
/// 未超时的 <c>STAGING_TO_WIRE</c> 也在内。
/// </summary>
/// <remarks>
/// <para>
/// 超时任务之间这一层分不出高下，交给后面的等待年龄层——「超时任务之间仍按本地等待时长排序」，不按超出阈值多少。
/// 是否超时由派车轮在排序前按本轮那一版参数算好（<see cref="DispatchTask.Starvation"/>），这一层只读结论：
/// 排进超时层与告警读的是同一个判断（<see cref="TaskStarvation.Assess"/>）。
/// </para>
/// <para>
/// <b>它不绕过任何门禁</b>：排序只决定先问哪条任务，每条任务照样逐车走完整条判据链，判据没全过的不会被派。
/// </para>
/// </remarks>
public sealed class StarvationTimeoutLayer : IDispatchCandidateComparisonLayer
{
    public int Compare(DispatchTask x, DispatchTask y)
    {
        ArgumentNullException.ThrowIfNull(x);
        ArgumentNullException.ThrowIfNull(y);
        return Overdue(y).CompareTo(Overdue(x));
    }

    private static bool Overdue(DispatchTask task) => task.Starvation?.Overdue == true;
}
