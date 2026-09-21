using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime.Dispatch;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Host.Runtime;

/// <summary>
/// 重排要用的两样东西：每区参数的当前版本与两站之间的计划路径代价（批次7-10，control-server#215）。
/// </summary>
/// <remarks>
/// 调用方在进写事务之前就读好：路网读的是快照表，放进事务里只会让事务更长。
/// </remarks>
public sealed record PlanRevisionRouting(
    DispatchZoneParameterTableVersion? ZoneParameters,
    Func<int, int, long?> Cost);

/// <summary>
/// 把 <see cref="JourneyPlanRevision"/> 的结论落到被跟踪的停靠行上：删掉的标 <see cref="JourneyStopStatuses.Removed"/>，
/// 其余写新序位（批次7-10，control-server#215）。
/// </summary>
/// <remarks>
/// <para>
/// <b>只暂存，不保存</b>，与 <see cref="PickupStopTermination"/> 同一个理由：终结一条需求与删掉它留下的空停靠必须一起提交，
/// 中间崩掉会留下一趟「需求已取消、车还要去它的站」的旅程。
/// </para>
/// <para>
/// <b>读的是被跟踪的行</b>，所以看得见调用方同一次改动里已经暂存、还没保存的事实——终结刚把归属标成
/// <see cref="JourneyDemandStatuses.Terminated"/>、需求标成已取消，这里读到的就是那个值（EF 对已跟踪的实体不用库里的值覆盖）。
/// 用 <c>AsNoTracking</c> 读会看到终结之前的样子，那条需求就还有剩余作业，它的停靠一个也删不掉。「已结束」有两条独立来源——
/// 归属的 <see cref="JourneyDemandStatuses.Terminated"/> 与需求的 <see cref="DemandExecutionStatus.Cancelled"/>——任一条读得到就够；
/// 两条都读成库里的旧值时，<c>Batch7ThreeStopJourneyTests.EndingOneOfTwoDemandsRemovesTheStopsItLeavesEmptyAndWithdrawsThemFromThePlan</c> 会红。
/// </para>
/// <para>
/// <b>不带 <paramref name="routing"/> 时只删不换。</b>删只需要停靠与归属，每条终结路径都做得到；换要路网与每区参数，
/// 只有拿得到它们的调用方才做。车看见的计划由引擎的计划重发（<c>RefreshUpcomingStopPlanAsync</c>）按线上内容比出来、
/// 换号重发，这里不发任何报文。
/// </para>
/// </remarks>
public static class JourneyPlanRevisionStage
{
    public static async Task<PlanRevisionResult> StageAsync(
        ControlServerDbContext dbContext,
        string journeyId,
        IReadOnlyCollection<string> leavingDemandIds,
        bool currentStopMayGo,
        PlanRevisionRouting? routing,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentException.ThrowIfNullOrWhiteSpace(journeyId);
        ArgumentNullException.ThrowIfNull(leavingDemandIds);

        List<JourneyStopRow> stops = await dbContext.Set<JourneyStopRow>()
            .Where(row => row.JourneyId == journeyId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        List<JourneyDemandRow> memberships = await dbContext.Set<JourneyDemandRow>()
            .Where(row => row.JourneyId == journeyId && row.RemovedAt == null)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        string[] demandIds = [.. memberships.Select(row => row.DemandId)];
        Dictionary<string, AcceptedDemandRow> demands = await dbContext.AcceptedDemands
            .Where(row => demandIds.Contains(row.DemandId))
            .ToDictionaryAsync(row => row.DemandId, StringComparer.Ordinal, cancellationToken).ConfigureAwait(false);

        PlanRevisionInput input = new(
            [.. stops.Select(row => new PlanRevisionStop(
                row.StopId,
                row.StopRole,
                row.StationRiotId,
                row.DispatchZone,
                row.Sequence,
                Done: row.Status is JourneyStopStatuses.Completed or JourneyStopStatuses.Removed))],
            [.. memberships.Select(row => new PlanRevisionDemand(
                row.DemandId,
                row.PickupStopId,
                row.UnloadStopId,
                StateOf(row, demands.GetValueOrDefault(row.DemandId), leavingDemandIds)))],
            currentStopMayGo);

        PlanRevisionResult result = JourneyPlanRevision.Revise(
            input,
            routing?.ZoneParameters,
            routing?.Cost ?? NoCost);
        if (routing is null)
        {
            // 没有路网就没有重排可言。纯函数在代价算不出时保持原序并给出理由，那个理由在这里不成立——不是算不出，是没去算。
            result = result with { Reordered = false, OrderKeptBecause = null };
        }

        HashSet<string> removed = new(result.RemovedStopIds, StringComparer.Ordinal);
        Dictionary<string, int> sequences = result.Sequences.ToDictionary(
            item => item.StopId, item => item.Sequence, StringComparer.Ordinal);
        foreach (JourneyStopRow stop in stops)
        {
            if (removed.Contains(stop.StopId))
            {
                stop.Status = JourneyStopStatuses.Removed;
            }

            // 删掉的停靠也按修订给的序位写：它们排到所有开放的之后，不写就留着夹在开放停靠之间的旧号（审查 M1）。
            if (sequences.TryGetValue(stop.StopId, out int sequence) && stop.Sequence != sequence)
            {
                stop.Sequence = sequence;
            }
        }

        return result;
    }

    /// <summary>只删不换时交给纯函数的代价：一段都算不出，于是它不会换序。</summary>
    private static long? NoCost(int from, int to) => null;

    private static PlanRevisionDemandState StateOf(
        JourneyDemandRow membership,
        AcceptedDemandRow? demand,
        IReadOnlyCollection<string> leavingDemandIds)
    {
        if (leavingDemandIds.Contains(membership.DemandId, StringComparer.Ordinal))
        {
            if (membership.Status == JourneyDemandStatuses.Loaded)
            {
                // REQ-0328 只释放未取货的需求；已装车的货不能从计划里丢掉（REQ-0197）。走到这里是调用方的判定错了。
                throw new InvalidOperationException(
                    $"Demand '{membership.DemandId}' is loaded and cannot leave its journey's plan.");
            }

            return PlanRevisionDemandState.Leaving;
        }

        if (membership.Status is JourneyDemandStatuses.Terminated or JourneyDemandStatuses.Unloaded ||
            demand?.Status is DemandExecutionStatus.Cancelled or DemandExecutionStatus.Succeeded)
        {
            return PlanRevisionDemandState.Ended;
        }

        return membership.Status == JourneyDemandStatuses.Loaded
            ? PlanRevisionDemandState.Loaded
            : PlanRevisionDemandState.PendingLoad;
    }
}
