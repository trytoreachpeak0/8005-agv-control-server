using ControlServer.Domain;

namespace ControlServer.Host.Runtime.Dispatch;

/// <summary>
/// 派车轮里的一条任务：目录里的一条需求，连同它第一次被看见的时刻。
/// </summary>
/// <remarks>
/// 批次7-06（control-server#211）把轮次翻成任务优先（REQ-0200）之后，任务侧要在<b>还没有车</b>的时候就把候选排出先后
/// ——所以排序的对象不能再是「某辆车的一条合格候选」。<c>FirstSeenAt</c> 取积压行，没有积压行的用本轮的钟：
/// 一条刚出现在目录里的需求就是此刻第一次被看见。
/// </remarks>
public sealed record DispatchTask(AcceptedDemandSnapshot Snapshot, DateTimeOffset FirstSeenAt)
{
    /// <summary>
    /// 这条任务在本轮的等待处境（批次7-09，control-server#214），由派车轮按本轮读一次的分区归属表与每区参数算出；
    /// 没算过的任务（空）不在超时层。
    /// </summary>
    public TaskStarvationStanding? Starvation { get; init; }
}

/// <summary>
/// 任务侧排序的一层：比较两条任务，只回答一个问题，别的都算平手。
/// </summary>
/// <remarks>
/// 一层分不出高下就返回 0，下一层接着判。批次7-09（control-server#214）的优先级带、超时层与等待年龄加在这一侧，
/// 各是一个文件加 <see cref="DispatchCandidateOrdering.Layers"/> 一行；本票的成本层与带内层加在<b>车辆侧</b>
/// （<see cref="IDispatchVehicleComparisonLayer"/>），两侧不相交。
/// </remarks>
public interface IDispatchCandidateComparisonLayer
{
    /// <summary><paramref name="x"/> 先派为负，<paramref name="y"/> 先派为正，分不出为 0。</summary>
    int Compare(DispatchTask x, DispatchTask y);
}

/// <summary>按这几层把任务排出先后。</summary>
/// <remarks>
/// 在批次7-06 之前它叫「选下一个候选」，选的是某辆车该接哪一条；翻成任务优先之后，它要给出<b>整张表</b>的次序，
/// 因为每一条都要依次问一遍有没有车接得了。完全分不出高下的两条按列表原序，与原来那个稳定的 <c>OrderBy</c> 一致。
/// </remarks>
public sealed class LayeredDispatchCandidateRanker(IReadOnlyList<IDispatchCandidateComparisonLayer> layers)
    : IDispatchCandidateRanker
{
    private readonly IDispatchCandidateComparisonLayer[] _layers = [.. layers];

    public IReadOnlyList<DispatchTask> Order(IReadOnlyList<DispatchTask> tasks)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        List<DispatchTask> ordered = [.. tasks];
        // 稳定排序：分不出高下的两条保持原序。List.Sort 不稳定，所以带上原下标当最后一道分辨。
        int[] positions = [.. Enumerable.Range(0, ordered.Count)];
        DispatchTask[] source = [.. ordered];
        Array.Sort(positions, (left, right) =>
        {
            int order = Compare(source[left], source[right]);
            return order != 0 ? order : left.CompareTo(right);
        });
        return [.. positions.Select(index => source[index])];
    }

    private int Compare(DispatchTask x, DispatchTask y)
    {
        foreach (IDispatchCandidateComparisonLayer layer in _layers)
        {
            int order = layer.Compare(x, y);
            if (order != 0)
            {
                return order;
            }
        }

        return 0;
    }
}
