using ControlServer.Application;
using ControlServer.Domain;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Infrastructure.Persistence;

/// <summary>
/// 让站（REQ-0355，规格第 5.1 节第 8 条，批次7-08，control-server#213）：别的车被承诺以某站为下一停靠的那一刻，
/// 停在该站持货等单的车结束等单。
/// </summary>
/// <remarks>
/// <para>
/// <b>这里只写触发，不改装货阶段。</b>触发是旅程行上的两列（<c>YieldTriggeredAt</c>、<c>YieldTriggeredByVehicleKey</c>，
/// control-server#206 建的），由承诺那一方的事务写——受理、追加（<c>WireToGateStore</c>）与离站（推进段）三处，与承诺本身
/// 同一次保存。被让的车下一次判装货阶段时读到它，走到 <c>CLOSED/WAITING_STATION_YIELD</c>（<c>LoadingPhaseMachine</c>）。
/// 分两步是因为承诺方的事务里不该替另一辆车发快照：快照的修订号、会话代数都是那辆车自己的推进段管的。
/// </para>
/// <para>
/// <b>只写一次，不撤回。</b>已经有值的不覆盖：第一个让它让站的是谁，就记谁。触发方之后换序、移除、改派，
/// 这两列都不动——让站不可撤回（票面第 5 条）。
/// </para>
/// <para>
/// <b>站按 <c>StationRiotId</c> 认</b>，与途中追加判「同一个站」（<c>EnRouteAppendPlanner</c>）是同一个判法。
/// </para>
/// </remarks>
public static class StationYield
{
    /// <summary>
    /// 车此刻站在当前停靠上的阶段：到站之后、为离站向 RIoT 请求移动之前。离站安全核验已发出（<c>AwaitingDepartureSafety</c>）
    /// 仍算没离开——「离开」是请求移动的那一刻（批次7-07 的定义）。
    /// </summary>
    public static bool StandsAtCurrentStop(JourneyRuntimeStage stage) =>
        stage is JourneyRuntimeStage.AwaitingSublot
            or JourneyRuntimeStage.AwaitingLoadResult
            or JourneyRuntimeStage.AwaitingStationDeparture
            or JourneyRuntimeStage.AwaitingDepartureSafety
            or JourneyRuntimeStage.AwaitingUnloadResult;

    /// <summary>持货等单的两个状态：<c>CARGO_HOLDING_WAIT</c>，或 <c>VEHICLE_FULL</c> 之后尚未离开。</summary>
    public static bool IsHolding(string? loadingPhaseState) =>
        loadingPhaseState is LoadingPhaseStates.CargoHoldingWait or LoadingPhaseStates.VehicleFull;

    /// <summary>
    /// 这趟旅程的「下一停靠」：车在路上时是它正开往的那一个（当前停靠），车站在停靠上时是序位上紧接着的、还开着的那一个。
    /// 没有就为空（最后一个停靠上，或旅程已阻断、已完成——那时说不出车在哪）。
    /// </summary>
    /// <param name="stops">这趟旅程的停靠，任意顺序；已完成、已移除的会被跳过。</param>
    public static JourneyStopRow? NextStop(JourneyRuntimeStage stage, IEnumerable<JourneyStopRow> stops)
    {
        ArgumentNullException.ThrowIfNull(stops);
        if (stage is JourneyRuntimeStage.Blocked or JourneyRuntimeStage.Completed)
        {
            return null;
        }

        JourneyStopRow[] open = [.. stops.Where(IsOpen).OrderBy(stop => stop.Sequence)];
        if (open.Length == 0)
        {
            return null;
        }

        return StandsAtCurrentStop(stage) ? open.ElementAtOrDefault(1) : open[0];
    }

    /// <summary>
    /// 这辆车此刻是不是停在 <paramref name="stationRiotId"/> 这个站、在一个取货停靠上持货等单——也就是会被让站的那种车。
    /// </summary>
    public static bool HoldsAtStation(JourneyRuntimeRow runtime, IEnumerable<JourneyStopRow> stops, int stationRiotId)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(stops);
        if (!IsHolding(runtime.LoadingPhaseState) || !StandsAtCurrentStop(runtime.Stage))
        {
            return false;
        }

        JourneyStopRow? current = stops.Where(IsOpen).OrderBy(stop => stop.Sequence).FirstOrDefault();
        return current is not null &&
               current.StopRole == JourneyStopRoles.Pickup &&
               current.StationRiotId == stationRiotId;
    }

    /// <summary>
    /// <paramref name="triggeringVehicleKey"/> 刚被承诺以 <paramref name="stationRiotId"/> 为下一停靠：把停在那个站持货等单的
    /// 别的车标记为让站。只暂存在变更跟踪器里，由调用方那次承诺的保存一起提交——要么都在，要么都不在。
    /// </summary>
    /// <returns>这一次新标记的旅程；已经标记过的不算。</returns>
    public static async Task<IReadOnlyList<string>> StageTriggerAsync(
        ControlServerDbContext dbContext,
        string triggeringVehicleKey,
        int stationRiotId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentException.ThrowIfNullOrWhiteSpace(triggeringVehicleKey);

        // 跟踪查询：同一个上下文里已经加载、改过的那一行按身份解析拿回来的是同一个实例，看到的是它此刻的值。
        JourneyRuntimeRow[] candidates = await dbContext.JourneyRuntimes
            .Where(row => row.Stage != JourneyRuntimeStage.Completed &&
                          row.Stage != JourneyRuntimeStage.Blocked &&
                          row.VehicleKey != triggeringVehicleKey &&
                          row.YieldTriggeredAt == null &&
                          (row.LoadingPhaseState == LoadingPhaseStates.CargoHoldingWait ||
                           row.LoadingPhaseState == LoadingPhaseStates.VehicleFull))
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);

        List<string> triggered = [];
        foreach (JourneyRuntimeRow runtime in candidates.OrderBy(row => row.CreatedAt))
        {
            // 查询在数据库上筛，这里按实例此刻的值再判一次：同一个上下文里未保存的改动（例如本轮刚关闭的装货阶段）以它为准。
            if (runtime.YieldTriggeredAt is not null ||
                string.Equals(runtime.VehicleKey, triggeringVehicleKey, StringComparison.Ordinal))
            {
                continue;
            }

            IReadOnlyList<JourneyStopRow> stops = await StopsOfAsync(dbContext, runtime.JourneyId, cancellationToken)
                .ConfigureAwait(false);
            if (!HoldsAtStation(runtime, stops, stationRiotId))
            {
                continue;
            }

            runtime.YieldTriggeredAt = now;
            runtime.YieldTriggeredByVehicleKey = triggeringVehicleKey;
            runtime.UpdatedAt = now;
            triggered.Add(runtime.JourneyId);
        }

        return triggered;
    }

    /// <summary>
    /// 别的哪辆车此刻以 <paramref name="stationRiotId"/> 为下一停靠；没有为空。有几辆取最早受理的那一辆。
    /// </summary>
    /// <remarks>
    /// 给被让的那一方用：它在别的车被承诺<b>之后</b>才进入持货等单时，承诺那一刻它还不是等单的车，承诺方的事务不会标记它，
    /// 而那辆车照样要来。不补这一处，它会一直停在别的车要来的站上。
    /// </remarks>
    public static async Task<string?> VehicleHeadingToAsync(
        ControlServerDbContext dbContext,
        string selfVehicleKey,
        int stationRiotId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        JourneyRuntimeRow[] others = await dbContext.JourneyRuntimes
            .Where(row => row.Stage != JourneyRuntimeStage.Completed &&
                          row.Stage != JourneyRuntimeStage.Blocked &&
                          row.VehicleKey != selfVehicleKey)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        foreach (JourneyRuntimeRow other in others.OrderBy(row => row.CreatedAt))
        {
            IReadOnlyList<JourneyStopRow> stops = await StopsOfAsync(dbContext, other.JourneyId, cancellationToken)
                .ConfigureAwait(false);
            if (NextStop(other.Stage, stops)?.StationRiotId == stationRiotId)
            {
                return other.VehicleKey;
            }
        }

        return null;
    }

    /// <summary>
    /// 一趟旅程的停靠：库里的与本上下文里新加、还没保存的并在一起。受理与追加的事务里新停靠还在变更跟踪器里，
    /// 只查数据库会把它们漏掉——与 <c>WireToGateStore.ApplyResequencingAsync</c> 同一个理由。
    /// </summary>
    public static async Task<IReadOnlyList<JourneyStopRow>> StopsOfAsync(
        ControlServerDbContext dbContext,
        string journeyId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        JourneyStopRow[] stored = await dbContext.Set<JourneyStopRow>()
            .Where(row => row.JourneyId == journeyId)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        return
        [
            .. stored
                .Concat(dbContext.Set<JourneyStopRow>().Local.Where(row => row.JourneyId == journeyId))
                .DistinctBy(row => row.StopId, StringComparer.Ordinal)
        ];
    }

    private static bool IsOpen(JourneyStopRow stop) =>
        stop.Status is not (JourneyStopStatuses.Completed or JourneyStopStatuses.Removed);
}
