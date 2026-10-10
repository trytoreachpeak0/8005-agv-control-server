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
/// <b>批次7-09（control-server#214）在前面加了超时层与优先级带，并把建单时刻挪到首次看到之前。</b>次序是 REQ-0202 的原话：
/// 超时层高于所有未超时的带，<c>STAGING_TO_WIRE</c> 独占最高初始带，同层按等待年龄从长到短。等待年龄从本地首次创建
/// TransportDemand 起算，那是 MesIngest 目录项的 <c>CreatedAt</c>，所以建单时刻那一层就是等待年龄层；首次看到退为平手键，
/// 理由见 <see cref="TaskStarvation"/>。
/// </para>
/// </remarks>
public static class DispatchCandidateOrdering
{
    /// <summary>Every layer, in the order it is asked.</summary>
    public static IReadOnlyList<IDispatchCandidateComparisonLayer> Layers() =>
    [
        new StarvationTimeoutLayer(),
        new PriorityBandLayer(),
        new DemandCreatedAtLayer(),
        new FirstSeenLayer(),
        new DemandIdOrdinalLayer(),
    ];

    /// <summary>The ranker over <see cref="Layers"/>: what the host registers.</summary>
    public static LayeredDispatchCandidateRanker Ranker() => new(Layers());
}
