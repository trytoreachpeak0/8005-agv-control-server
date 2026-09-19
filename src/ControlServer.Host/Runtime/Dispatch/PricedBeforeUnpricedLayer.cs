namespace ControlServer.Host.Runtime.Dispatch;

/// <summary>
/// A candidate the route graph priced is taken before one it did not.
/// </summary>
/// <remarks>
/// REQ-0207's second half: <em>"可达性已经确认，但本轮候选的新增行程成本缺失、过期或不可比较时，保留相关车辆、对该比较组
/// 跳过路径成本层并记录原因，继续比较后续层"</em>. Every candidate reaching the ranking already passed the reachability
/// criterion, so an unpriced one is unmeasured, not unreachable: it keeps its place behind the priced ones, and when
/// nothing is priced this layer and the cost layer call every pair equal and the first-seen order decides.
/// </remarks>
public sealed class PricedBeforeUnpricedLayer : IDispatchCandidateComparisonLayer
{
    public int Compare(EligibleDispatchCandidate x, EligibleDispatchCandidate y)
    {
        ArgumentNullException.ThrowIfNull(x);
        ArgumentNullException.ThrowIfNull(y);
        return (x.GraphTraversalCostMm.HasValue, y.GraphTraversalCostMm.HasValue) switch
        {
            (true, false) => -1,
            (false, true) => 1,
            _ => 0,
        };
    }
}
