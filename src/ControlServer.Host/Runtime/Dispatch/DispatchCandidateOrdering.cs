namespace ControlServer.Host.Runtime.Dispatch;

/// <summary>
/// The dispatch ranking's registry -- the one place its comparison layers are listed, in the order they are asked.
/// </summary>
/// <remarks>
/// <para>
/// <b>A new layer adds one file and one line here.</b> Batch 7 puts two changes into the ranking: control-server#211
/// replaces the cost layer's trip-to-pickup cost with the marginal cost of the added trip, and control-server#214 adds
/// the priority band and waiting age ahead of it. Written as one chain of <c>OrderBy</c> calls, as it was until
/// control-server#209, both would have edited the same expression.
/// </para>
/// <para>
/// The order below is the order of <c>RouteGraphCostRanker</c> over <c>FirstSeenDispatchCandidateRanker</c>, which it
/// replaced: priced candidates first, then by cost, then first seen, then created, then demand id.
/// </para>
/// </remarks>
public static class DispatchCandidateOrdering
{
    /// <summary>Every layer, in the order it is asked.</summary>
    public static IReadOnlyList<IDispatchCandidateComparisonLayer> Layers() =>
    [
        new PricedBeforeUnpricedLayer(),
        new GraphTraversalCostLayer(),
        new FirstSeenLayer(),
        new DemandCreatedAtLayer(),
        new DemandIdOrdinalLayer(),
    ];

    /// <summary>The ranker over <see cref="Layers"/>: what the host registers.</summary>
    public static LayeredDispatchCandidateRanker Ranker() => new(Layers());
}
