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
/// <b>成本那两层搬到车辆侧去了</b>（批次7-06，control-server#211）。它们比的是「这辆车到那个取货站多远」，
/// 而任务侧在还没有车的时候就要把任务排出先后——一个与车有关的量在这一侧无从取值。车辆侧的对应两层是
/// <see cref="PricedVehicleBeforeUnpricedLayer"/> 与 <see cref="MarginalTripCostLayer"/>，比的是边际成本而不是
/// 到取货站的成本（REQ-0206）。
/// </para>
/// <para>
/// 剩下的三层就是本票沿用的任务次序：先见先派，再按需求创建时刻与需求 id 定序。
/// </para>
/// </remarks>
public static class DispatchCandidateOrdering
{
    /// <summary>Every layer, in the order it is asked.</summary>
    public static IReadOnlyList<IDispatchCandidateComparisonLayer> Layers() =>
    [
        new FirstSeenLayer(),
        new DemandCreatedAtLayer(),
        new DemandIdOrdinalLayer(),
    ];

    /// <summary>The ranker over <see cref="Layers"/>: what the host registers.</summary>
    public static LayeredDispatchCandidateRanker Ranker() => new(Layers());
}
