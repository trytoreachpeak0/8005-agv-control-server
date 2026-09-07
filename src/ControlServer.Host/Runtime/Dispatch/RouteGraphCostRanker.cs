namespace ControlServer.Host.Runtime.Dispatch;

/// <summary>
/// Picks the eligible candidate this vehicle can reach most cheaply, falling back to first-seen
/// when the graph could not price a comparison.
/// </summary>
/// <remarks>
/// <para>
/// This is REQ-0207's second half, and it is deliberately gentler than the first: <em>"可达性已经
/// 确认，但本轮候选的新增行程成本缺失、过期或不可比较时，保留相关车辆、对该比较组跳过路径成本
/// 层并记录原因，继续比较后续层"</em>. A missing cost drops the cost layer for that comparison —
/// it does not drop the candidate, and it does not drop the round.
/// </para>
/// <para>
/// So: candidates that have a traversal cost are ordered by it; candidates that do not keep their
/// place in the deterministic first-seen order and sort after the priced ones. Every candidate
/// reaching this point already passed the reachability criterion, so "no cost" here means the
/// engine was off or the cost was not carried — never that the station cannot be reached.
/// </para>
/// <para>
/// <b>The value being ordered is not a RouteCost.</b> It is the self-built graph's traversal cost,
/// and it is never logged, persisted or compared under that name. RIoT's RouteCost is a different
/// fact answering a different question, and the pre-create gate is where that one belongs.
/// </para>
/// </remarks>
public sealed class RouteGraphCostRanker : IDispatchCandidateRanker
{
    private static readonly FirstSeenDispatchCandidateRanker FirstSeen = new();

    public EligibleDispatchCandidate SelectNext(IReadOnlyList<EligibleDispatchCandidate> eligible)
    {
        ArgumentNullException.ThrowIfNull(eligible);
        if (eligible.Count == 0)
        {
            throw new ArgumentException("The ranker is only called with at least one candidate.", nameof(eligible));
        }

        List<EligibleDispatchCandidate> priced =
            eligible.Where(candidate => candidate.GraphTraversalCostMm.HasValue).ToList();
        if (priced.Count == 0)
        {
            // Nothing priced: the cost layer contributes nothing and the round falls through to
            // the layer below it, exactly as REQ-0207 describes.
            return FirstSeen.SelectNext(eligible);
        }

        return priced
            .OrderBy(candidate => candidate.GraphTraversalCostMm!.Value)
            // Same cost is common on a grid-like Map, and a tie broken by enumeration order is not
            // reproducible from the evidence. The deterministic tie-break carries on below.
            .ThenBy(candidate => candidate.FirstSeenAt)
            .ThenBy(candidate => candidate.Snapshot.CreatedAt)
            .ThenBy(candidate => candidate.Snapshot.DemandId, StringComparer.Ordinal)
            .First();
    }
}
