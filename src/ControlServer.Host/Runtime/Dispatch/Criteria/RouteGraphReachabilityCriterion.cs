using ControlServer.Domain;
using ControlServer.Host.Runtime.RouteGraph;

namespace ControlServer.Host.Runtime.Dispatch.Criteria;

/// <summary>
/// The vehicle must be able to reach the candidate's pickup station, on the route graph.
/// </summary>
/// <remarks>
/// <para>
/// This is REQ-0207's first half, and it is a block, not a ranking penalty: <em>"无法确认车辆到
/// 下一站可达时，该车退出本轮候选"</em>. An unreachable pickup means this vehicle does not take
/// this demand at all — not that it takes it last.
/// </para>
/// <para>
/// <b>There is no weak substitute anywhere in this path.</b> No straight-line distance, no "近似
/// 可达", no falling back to the deterministic tie-break when the graph cannot answer. REQ-0207
/// forbids it, and the reason is not pedantic: a straight line between two stations says nothing
/// about whether a directed edge sequence connects them, and map25 has 403 directed edges of
/// which only 148 have a reverse.
/// </para>
/// <para>
/// A stale snapshot blocks with the engine's own reason rather than a generic "no candidate", so
/// an operator reading the backlog can tell "the graph is stale" from "this station really is
/// unreachable" — the specification asks for exactly that.
/// </para>
/// </remarks>
public sealed class RouteGraphReachabilityCriterion(RouteGraphAccess routeGraph) : IDispatchAdmissionCriterion
{
    /// <summary>Between the station-task-type check and the slot-capacity check.</summary>
    /// <remarks>
    /// After the route exists (it needs the pickup station id) and before the box-count read,
    /// which is a network call this criterion can spare when the station is unreachable anyway.
    /// </remarks>
    public int Order => 95;

    public async Task<string> EvaluateAsync(
        DispatchCandidateEvaluation evaluation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evaluation);

        if (!routeGraph.Enabled)
        {
            // The engine off is a deployment that has not adopted it yet, not a routing verdict.
            // Dispatch behaves exactly as it did before the engine existed.
            return DispatchAdmissionChain.Eligible;
        }

        RouteGraphAvailability availability = await routeGraph
            .ReadAsync(cancellationToken).ConfigureAwait(false);
        if (!availability.IsUsable)
        {
            // Stale means no dispatch this round. The reason is the engine's, so the block is
            // traceable to the engine rather than to a candidate.
            return availability.StaleReason ?? RouteGraphStaleReasons.NeverRefreshed;
        }

        int? origin = evaluation.Vehicle.Vehicle.CurrentStationId;
        if (origin is null)
        {
            // Not knowing where the vehicle is, is not the same as it being anywhere. Fail closed.
            return "ROUTE_GRAPH_VEHICLE_POSITION_UNKNOWN";
        }

        RouteGraphTraversal traversal = availability.Graph!.Traverse(
            origin.Value,
            evaluation.Route!.PickupStationRiotId);

        if (!traversal.Reachable)
        {
            return "ROUTE_GRAPH_PICKUP_UNREACHABLE";
        }

        // Carried for the ranker, which is a separate decision: reachable is a gate, cost is an
        // ordering. REQ-0207 keeps them apart because a missing cost must not evict a vehicle the
        // way a missing reachability does.
        evaluation.GraphTraversalCostMm = traversal.TraversalCostMm;
        return DispatchAdmissionChain.Eligible;
    }
}
