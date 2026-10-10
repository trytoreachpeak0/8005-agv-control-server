using ControlServer.Application;
using ControlServer.Host.Runtime.RouteGraph;

namespace ControlServer.Host.Runtime;

/// <summary>
/// 计划修订换序要用的路网与每区参数，读一次交给 <see cref="PickupStopTermination"/> 与释放改派服务（批次7-10，control-server#215）。
/// </summary>
/// <remarks>
/// <para>
/// 代价取法与途中追加（<c>EnRouteAppendCriterion</c>）一致：路网快照里两站之间可达就取遍历代价（毫米），否则算不出。
/// <b>路网不可用时返回空</b>，调用方于是只删不换——与「任何一段代价算不出就不换」同一口径，删却照样发生：删不需要代价。
/// </para>
/// <para>
/// 每区参数读当前版本，与派车轮次读的是同一张表。轮次在一轮开头读一次、这里在终结或释放那一刻读，两者可能差一个版本；
/// 修订判的是「此刻换序会不会让谁超限」，用此刻的版本是对的。
/// </para>
/// </remarks>
public sealed class PlanRevisionRoutingSource(RouteGraphAccess routeGraph, IDispatchZoneParameterStore zoneParameters)
{
    public async Task<PlanRevisionRouting?> ReadAsync(CancellationToken cancellationToken)
    {
        RouteGraphAvailability availability = await routeGraph.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (!availability.IsUsable)
        {
            return null;
        }

        DispatchZoneParameterTableVersion? zones = await zoneParameters.ReadCurrentAsync(cancellationToken)
            .ConfigureAwait(false);
        return new PlanRevisionRouting(
            zones,
            (from, to) => availability.Graph!.Traverse(from, to) is { Reachable: true } traversal
                ? traversal.TraversalCostMm
                : null);
    }
}
