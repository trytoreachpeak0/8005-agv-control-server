using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Host.Runtime;

/// <summary>
/// 待送点取货停靠的判别（control-server#709，program#184 第一波各票共用）：取货停靠，而且站点就是这个停靠上某条 STAGING_TO_WIRE 需求
/// 冻结的绑定所指的固定起点（REQ-0334、REQ-0344）。
/// </summary>
/// <remarks>
/// <para>
/// <b>按停靠判，不按车判。</b>用户 10-14 定：agv01、agv03 的机台取货保持现状，规则只对 STAGING_TO_WIRE 在待送点的取货生效。同一辆车、同一趟
/// 旅程上的机台取货停靠，答的永远是否。
/// </para>
/// <para>
/// <b>站点取需求冻结的那一版绑定，不取此刻的绑定。</b>需求受理时冻结了规则与绑定集版本，之后的版本变化不改变这条需求（REQ-0344）；
/// 这里与 <see cref="WireToGateStore.AreaEndOperationAsync"/> 读同一份冻结。没有冻结的需求（受理时没有按版本解析出固定站）
/// 答否：认不出是待送点的停靠就按今天的规则走，宁可多一个倒计时，也不在一个说不清的站上拆掉兜底。
/// </para>
/// <para>
/// <b>开关关着时一次库都不读</b>（<see cref="StagingDepartureRulesOptions.Enabled"/>）：调用方先看开关，关着就不进这里。
/// </para>
/// </remarks>
internal static class StagingPickupStops
{
    /// <summary>这趟旅程里哪些停靠是待送点取货停靠，按停靠 id。</summary>
    public static async Task<IReadOnlySet<string>> RecognizeAsync(
        ControlServerDbContext dbContext,
        JourneyStopCursor stops,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(stops);
        HashSet<string> staging = new(StringComparer.Ordinal);
        foreach (JourneyStopRow stop in stops.Stops)
        {
            if (await IsStagingPickupAsync(dbContext, stop, EverAtStop(stops, stop), cancellationToken).ConfigureAwait(false))
            {
                staging.Add(stop.StopId);
            }
        }
        return staging;
    }

    /// <summary>
    /// 这趟旅程此刻的当前停靠是不是待送点取货停靠；开关关着、旅程没有开着的停靠时答否。给推进段之外读期限的地方用（装货结果的回执、看板）。
    /// </summary>
    public static async Task<bool> CurrentStopIsStagingAsync(
        ControlServerDbContext dbContext,
        JourneyRuntimeRow runtime,
        StagingDepartureRulesOptions rules,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(rules);
        if (!rules.Enabled || runtime.Stage == JourneyRuntimeStage.Completed)
        {
            return false;
        }
        JourneyStopCursor stops = await JourneyStopCursor.LoadAsync(dbContext, runtime, cancellationToken).ConfigureAwait(false);
        if (!stops.OpenStops.Any())
        {
            return false;
        }
        JourneyStopRow current = stops.Current;
        return await IsStagingPickupAsync(dbContext, current, EverAtStop(stops, current), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 认停靠用的归属：此刻挂着的，<b>加上已被移除的</b>（control-server#904）。
    /// </summary>
    /// <remarks>
    /// 停靠是不是待送点，是这个停靠落库时就定了的事实（为哪条需求、在哪一站开），不随它上面的需求被放回改派而变。只看未移除的归属时，
    /// 同站追加单独开的那个停靠上唯一一条在装货中取消、放回（control-server#713）之后，这个停靠就认不出了：引擎此前在同一个实例里跨轮
    /// 记着认过的停靠，测试夹具复用一个引擎所以看不出；生产里引擎每轮取自新的 scope，取消之后的那一轮重新认，认成机台取货，
    /// #713 的续跑接不住，每轮抛（11-19 agv02 在 217）。
    /// <para>
    /// 按已移除需求的冻结认站，<b>只在它还没被别的旅程再受理时与它挂着时同一个答案</b>。放回待派的需求被再派受理时，
    /// <c>DemandTaskTypeStationFreezeStore.ThawForRedispatchAsync</c> 删掉旧冻结、按受理那一刻的版本重冻（<c>FreezeAsync</c> 已有才返回已有的）。
    /// 所以有一条窄路径认不出：车还停在这个清空的待送点停靠上，那条需求已被派给别的车，而这期间导入的新绑定集把 STAGING_TO_WIRE 的固定站改了址——
    /// 重冻的站不再等于这个停靠的站，这里认成机台取货。窄在：同一趟旅程不会再把它追加回来、固定站被占时别的车接不了它
    /// （单占用），而且要恰好撞上换绑定。control-server#906 起这条路不再卡死：引擎等装货结果时只要本停靠上有「已移除且仍是 LOADING」的归属，
    /// 就按装货途中被结束续跑，不经过这里。剩下的只是之后的离站等待按机台取货的站点期限走、不持货等单——那个停靠上已经一条需求都没有，
    /// 两样都是到期带着车上已有的货离站，只是期限不同（站点期限通常短于持货期限，车少等一些追加）。
    /// </para>
    /// </remarks>
    private static IReadOnlyList<JourneyStopDemand> EverAtStop(JourneyStopCursor stops, JourneyStopRow stop) =>
        [.. stops.AllAtStop(stop), .. stops.LeftAtStop(stop)];

    private static async Task<bool> IsStagingPickupAsync(
        ControlServerDbContext dbContext,
        JourneyStopRow stop,
        IReadOnlyList<JourneyStopDemand> demandsAtStop,
        CancellationToken cancellationToken)
    {
        if (stop.StopRole != JourneyStopRoles.Pickup)
        {
            return false;
        }
        DemandTaskTypeStationFreezeStore freezes = new(dbContext);
        foreach (JourneyStopDemand item in demandsAtStop.Where(item =>
                     string.Equals(item.Demand.WorkType, TransportTaskTypes.StagingToWire, StringComparison.Ordinal)))
        {
            if (await freezes.ReadAsync(item.Demand.DemandId, cancellationToken).ConfigureAwait(false) is not { } freeze)
            {
                continue;
            }
            int? bound = await dbContext.Set<TaskTypeStationBindingRow>().AsNoTracking()
                .Where(row => row.MapId == freeze.MapId &&
                              row.Version == freeze.BindingSetVersion &&
                              row.TaskType == TransportTaskTypes.StagingToWire)
                .Select(row => (int?)row.StationRiotId)
                .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (bound == stop.StationRiotId)
            {
                return true;
            }
        }
        return false;
    }
}
