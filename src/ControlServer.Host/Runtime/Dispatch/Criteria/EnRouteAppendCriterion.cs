using ControlServer.Application;
using ControlServer.Host.Runtime.RouteGraph;

namespace ControlServer.Host.Runtime.Dispatch.Criteria;

/// <summary>
/// 追加的四道门（票面第 3 条，批次7-06，control-server#211）：当前下一站不可改、分区连续、延迟门禁、9 腿 8 项上限。
/// </summary>
/// <remarks>
/// <para>
/// 判定本身在 <see cref="EnRouteAppendPlanner"/>——四道门是同一次插位规划的四种结论，拆成四个判据会把同一次规划算四遍，
/// 而且四个判据对「插在哪」会各有各的答案。这里只负责把轮次的事实喂给它，把结果翻成一个原因码，
/// 并把选中的插入位与边际成本留给排序层。
/// </para>
/// <para>
/// <b>只对在途车问。</b>空闲车没有计划可插，它的「边际成本」是从当前位置走完新计划的全程，由排序层自己算。
/// </para>
/// </remarks>
public sealed class EnRouteAppendCriterion(RouteGraphAccess routeGraph) : IDispatchAdmissionCriterion
{
    /// <summary>在可达性之后、仓位之前：插位规划要用路网，而路网不可用时那条判据已经先拒了。</summary>
    public int Order => 98;

    public async Task<string> EvaluateAsync(
        DispatchCandidateEvaluation evaluation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        if (evaluation.Vehicle.Plan is not { } plan)
        {
            // 空闲车走到这里是链装配错了：这条判据只在在途链上。放行而不是抛，理由与链上别处一致——
            // 它不是这条判据要守的东西，而抛会把一轮派车整个带下去。
            return DispatchAdmissionChain.Eligible;
        }

        if (evaluation.Route is not { } route)
        {
            return DispatchReasonCodes.EnRouteAppendNoInsertionPoint;
        }

        RouteGraphAvailability availability = await routeGraph.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (!availability.IsUsable)
        {
            return availability.StaleReason ?? RouteGraphStaleReasons.NeverRefreshed;
        }

        string zone = route.DispatchZone;
        EnRouteAppendDecision decision = EnRouteAppendPlanner.Plan(
            plan,
            new EnRouteAppendCandidate(
                new EnRouteStop(
                    JourneyIdentity.AppendedPickupStopId(evaluation.Candidate.DemandId),
                    route.PickupStationId,
                    route.PickupStationRiotId,
                    zone,
                    JourneyStopRoles.Pickup),
                new EnRouteStop(
                    JourneyIdentity.AppendedUnloadStopId(evaluation.Candidate.DemandId),
                    route.DropoffStationId,
                    route.DropoffStationRiotId,
                    zone,
                    JourneyStopRoles.Unload),
                zone),
            evaluation.Round.ZoneParameters,
            (from, to) => availability.Graph!.Traverse(from, to) is { Reachable: true } traversal
                ? traversal.TraversalCostMm
                : null);
        if (decision.Placement is not { } placement)
        {
            return decision.RefusalReasonCode
                   ?? throw new InvalidOperationException("A refused append names its reason.");
        }

        evaluation.AppendPlacement = placement;
        // 每次追加决策记下所用的每区参数版本（票面第 3 条）：参数是只追加、不可改的版本表，所以记下版本号
        // 就能在事后原样读回这次决策所依据的那一版。
        evaluation.DispatchZoneParameterVersion = evaluation.Round.ZoneParameters?.Version;
        return DispatchAdmissionChain.Eligible;
    }
}
