namespace ControlServer.Host.Runtime.Dispatch;

/// <summary>
/// Of two priced candidates, the one the vehicle reaches more cheaply is taken first.
/// </summary>
/// <remarks>
/// <para>
/// A pair with either side unpriced is a tie here; <see cref="PricedBeforeUnpricedLayer"/> has already decided it.
/// Same cost is common on a grid-like Map, and falls through to the deterministic layers below.
/// </para>
/// <para>
/// <b>The value compared is not a RouteCost.</b> It is the self-built graph's traversal cost, and it is never logged,
/// persisted or compared under that name. RIoT's RouteCost is a different fact answering a different question, and the
/// pre-create gate is where that one belongs.
/// </para>
/// </remarks>
public sealed class GraphTraversalCostLayer : IDispatchCandidateComparisonLayer
{
    public int Compare(EligibleDispatchCandidate x, EligibleDispatchCandidate y)
    {
        ArgumentNullException.ThrowIfNull(x);
        ArgumentNullException.ThrowIfNull(y);
        return x.GraphTraversalCostMm is long xCost && y.GraphTraversalCostMm is long yCost
            ? xCost.CompareTo(yCost)
            : 0;
    }
}
