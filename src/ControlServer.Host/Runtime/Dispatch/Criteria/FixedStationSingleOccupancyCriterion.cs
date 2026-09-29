using ControlServer.Application;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Host.Runtime.Dispatch.Criteria;

/// <summary>
/// 固定公共站点单车位（<c>REQ-0204</c> 修订，批次8-20，control-server#391）：一个公共站点同时只由一台已到达车占用或一台已承诺车预占，
/// 别的车不承接下一站为该点的新任务。
/// </summary>
/// <remarks>
/// <para>
/// <b>「下一站」是让站的那个定义</b>（<c>StationYield.NextStop</c>），不另造一套：车在路上时是它正驶向的那一个，站在停靠上时是序位上
/// 紧接着的那一个。于是这条候选改变下一站只有两种样子——空闲车接一趟新的（下一站是新旅程的取货站），或者站在停靠上的在途车把新停靠
/// 插在紧接着的位置（途中追加门禁不许改当前停靠，REQ-0196，所以在路上的车改不了它的下一站）。下一站没被这条候选改变时一律放行：
/// 本票只管新任务，已经承诺的不因本票被取消或改派。
/// </para>
/// <para>
/// <b>只看这条候选自己的公共站点</b>（它任务类型的 <c>FixedTaskStation</c>）。候选能改成下一站的只有它自己带来的两个停靠，
/// 其中是公共站点的只可能是它的固定站。所以 <c>WIRE_TO_GATE</c>（公共站点是关卡，在卸货端、排在取货之后）实际上从不被这里挡——
/// 关卡什么时候成为一辆车的下一站是推进决定的，不是派车。
/// </para>
/// <para>
/// <b>读是当下读，不是轮次开头读一次。</b>一轮里前面的受理刚预占的站，后面的候选要看得见，否则同轮两车会被判给同一个站。但读不定输赢：
/// 读过之后才被占的那一刻由受理事务里的主键决定（<c>FixedStationExclusivity</c>），这里只是不让明知会输的候选占掉一个出价。
/// </para>
/// <para>
/// <b>排在追加的四道门与装货阶段之后（99）、仓位之前（100）。</b>在追加门之后，因为在途车的下一站要看它选中的插入位；与装货阶段同序、
/// 注册在它后面（<see cref="DispatchAdmissionCriteria.InTransit"/>），是为了装货阶段已结束的车仍报 <c>LOADING_PHASE_CLOSED</c>——那才是
/// 这辆车接不了的原因；在仓位之前，理由同 <see cref="LoadingPhaseOpenCriterion"/>：一条因为站被占而不会接的候选，不该让这辆车的哪一侧被判满。
/// </para>
/// </remarks>
public sealed class FixedStationSingleOccupancyCriterion(ControlServerDbContext dbContext) : IDispatchAdmissionCriterion
{
    public int Order => 99;

    public async Task<string> EvaluateAsync(DispatchCandidateEvaluation evaluation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        if (evaluation.Route is not { } route || route.FixedStation.Station is not { } fixedStation)
        {
            // No route means an earlier criterion refused it; a route without a fixed station cannot be built.
            return DispatchAdmissionChain.Eligible;
        }

        if (NewNextStopStation(evaluation, route) is not int nextStation || nextStation != fixedStation.StationId)
        {
            return DispatchAdmissionChain.Eligible;
        }

        int mapId = evaluation.Round.Map.MapId;
        StationExclusivityRow? held = await dbContext.Set<StationExclusivityRow>().AsNoTracking()
            .SingleOrDefaultAsync(row => row.MapId == mapId && row.StationId == nextStation, cancellationToken)
            .ConfigureAwait(false);
        if (held is null)
        {
            return DispatchAdmissionChain.Eligible;
        }

        // 同一个 (MapId, StationId) 不会既是等待点又是公共站点：批次8-17（control-server#388）的导入已拒绝，这里不再校验那条，只断言。
        // 抛而不是挡：挡住会把一个配置缺陷变成一条看起来普通的积压理由。
        if (held.StationKind != StationExclusivityKinds.FixedTaskStation)
        {
            throw new InvalidOperationException(FormattableString.Invariant(
                $"Station {mapId}/{nextStation} is the fixed task station of task type {route.FixedStation.TaskType} and is held as {held.StationKind}."));
        }

        // 这辆车自己占着（上一趟留下的在点占用，或者本来就是它的预占）：它不是「别的车」。
        if (string.Equals(held.VehicleKey, evaluation.Vehicle.VehicleKey, StringComparison.Ordinal))
        {
            return DispatchAdmissionChain.Eligible;
        }

        return held.State == StationExclusivityStates.Occupied
            ? DispatchReasonCodes.FixedTaskStationOccupiedByOtherVehicle
            : DispatchReasonCodes.FixedTaskStationReservedByOtherVehicle;
    }

    /// <summary>
    /// 这条候选让这辆车的下一站变成了哪个站；下一站没被它改变时为空。
    /// </summary>
    internal static int? NewNextStopStation(DispatchCandidateEvaluation evaluation, ResolvedJourneyRoute route)
    {
        if (evaluation.Vehicle.Plan is not { } plan)
        {
            // 空闲车：新旅程的第一个停靠就是它的取货站，车还没到——下一站就是它。
            return route.PickupStationRiotId;
        }

        if (!plan.StandsAtCurrentStop || evaluation.AppendPlacement is not { } placement)
        {
            return null;
        }

        string key = evaluation.Round.DerivationKeyOf(evaluation.Candidate.DemandId);
        return NextAfterCurrent(
            plan,
            placement,
            new Dictionary<string, int>(StringComparer.Ordinal)
            {
                // The stops the append would open, under the ids EnRouteAppendCriterion planned them with. A merged one
                // is already in the plan under its own id and its own station.
                [JourneyIdentity.AppendedPickupStopId(key)] = route.PickupStationRiotId,
                [JourneyIdentity.AppendedUnloadStopId(key)] = route.DropoffStationRiotId,
            });
    }

    // The stop right after the current one once the candidate's stops are in, when that is not the one that was there before.
    private static int? NextAfterCurrent(
        EnRouteVehiclePlan plan, EnRouteAppendPlacement placement, IReadOnlyDictionary<string, int> openedStops)
    {
        Dictionary<string, int> stationByStop = plan.Stops.ToDictionary(
            stop => stop.StopId, stop => stop.StationRiotId, StringComparer.Ordinal);
        foreach ((string stopId, int station) in openedStops)
        {
            stationByStop.TryAdd(stopId, station);
        }

        string current = plan.Stops[plan.CurrentNextStopIndex].StopId;
        string? before = plan.Stops.Count > plan.CurrentNextStopIndex + 1
            ? plan.Stops[plan.CurrentNextStopIndex + 1].StopId
            : null;
        string[] after = [.. placement.Resequenced
            .Where(sequenced => stationByStop.ContainsKey(sequenced.StopId)
                                && plan.TrailingRemovedStopIds?.Contains(sequenced.StopId) != true)
            .OrderBy(sequenced => sequenced.Sequence)
            .Select(sequenced => sequenced.StopId)];
        int at = Array.IndexOf(after, current);
        string? next = at >= 0 && at + 1 < after.Length ? after[at + 1] : null;
        return next is null || string.Equals(next, before, StringComparison.Ordinal) ? null : stationByStop[next];
    }
}
