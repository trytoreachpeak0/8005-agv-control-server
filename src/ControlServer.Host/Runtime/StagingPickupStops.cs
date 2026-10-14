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
            if (await IsStagingPickupAsync(dbContext, stop, stops.AllAtStop(stop), cancellationToken).ConfigureAwait(false))
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
        return await IsStagingPickupAsync(dbContext, current, stops.AllAtStop(current), cancellationToken).ConfigureAwait(false);
    }

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
