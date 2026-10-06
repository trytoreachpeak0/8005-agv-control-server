using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Host.Runtime;

/// <summary>
/// 旅程阻塞在某一次仓位操作的恢复上，而那一次操作所属的需求被终结、旅程还带着别的需求时，把旅程放回等那一次结果的阶段
/// （control-server#499）。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么要放。</b>装货或卸货结果要恢复时，引擎把旅程置为 <c>Blocked</c>（<c>LOAD_RESULT_REQUIRES_RECOVERY</c> /
/// <c>UNLOAD_RESULT_REQUIRES_RECOVERY</c>），此后每轮直接返回。异常处置会话里的装货补偿、故障货物交接与强制机械取出经
/// <see cref="PickupStopTermination"/> 终结这一条；旅程只带它一条时随之收尾，多带别的需求时不收尾——修之前阶段就一直停在
/// <c>Blocked</c>，车上剩下的货永远不卸，只能改库。
/// </para>
/// <para>
/// <b>放到哪里。</b>与修复续行（<c>OnboardRecoveryCoordinator.ObserveOperationResultAsync</c>）同一个做法：按那一次操作是装还是卸，
/// 回到 <see cref="JourneyRuntimeStage.AwaitingLoadResult"/> 或 <see cref="JourneyRuntimeStage.AwaitingUnloadResult"/>，阻塞码清掉。
/// 接下来怎么走由引擎在那两个阶段里按落库的状态判，这里不另写一套：取货停靠是 control-server#291 的续跑——本站还有待装的回等录入、
/// 发清单与录入请求，没有就进离站段；卸货停靠是同一票的卸货续跑，本站一条在下了卸货命令之后被终结也算已有进展（本票放宽），
/// 本站还有没卸的就发它的卸货命令，没有就离站或收尾。
/// </para>
/// <para>
/// <b>只在三条都成立时放</b>，任何一条不成立就留在 <c>Blocked</c>、什么都不写：
/// </para>
/// <list type="number">
/// <item><description>
/// 旅程此刻是 <c>Blocked</c>。在途取消（<c>LoadCancellationResult</c>）终结时旅程在等装货结果，那一支由引擎自己续上，不经这里。
/// </description></item>
/// <item><description>
/// 被结清的正是旅程阻塞在其上的那一次：终结之前它是 <see cref="StationOperationStatus.RecoveryRequired"/>，而且是当前停靠上这条需求的
/// 装货（取货停靠）或卸货（卸货停靠）操作。会话也可以开在同一趟旅程里另一条已装上的需求上，把它交接掉——那一条的操作早已结清，
/// 阻塞旅程的那一次仍没收敛，不放。
/// </description></item>
/// <item><description>
/// 这趟旅程别的仓位操作没有一次还没收敛（<see cref="StationOperationStatus.Prepared"/> 或 <c>RecoveryRequired</c>）。同一站逐条串行，
/// 正常流程下不会有，这是准入线第 1 条在放行这一侧的那半：有一次操作没收敛，就不放车去做下一件事。
/// </description></item>
/// </list>
/// <para>
/// <b>门。</b>这里从不直接放车离站。离站只有一条路：引擎发离站核验，车答 <c>SAFE</c>、所有目标仓位锁闭、开锁输出复位、没有未知仓位，
/// 而且会话就绪——强制机械取出之后会话一直不就绪，直到这次强制恢复的 <c>HardwareRecoveryRecord</c> 到达（REQ-0242，
/// <c>WireToGateStore.DecideReadinessAsync</c>）。准入线第 1 条的另一半由那条路守着。
/// </para>
/// <para>
/// <b>只暂存，不保存</b>，与终结、结果同一次保存（调用方那一次）。
/// </para>
/// <para>
/// <b>给 control-server#485 的用法。</b>车永久离线时由服务端收尾阻塞旅程，如果那里只终结阻塞旅程的那一条、留下别的需求，
/// 终结之后调这里，与会话结果走同一个出口；不要另写一个离开 <c>Blocked</c> 的分支。整趟旅程都终结的，<see cref="PickupStopTermination"/>
/// 已经收尾，这里因阶段已是 <c>Completed</c> 而什么都不做。
/// </para>
/// </remarks>
internal static class BlockedJourneyRelease
{
    /// <summary>
    /// <paramref name="endedOperation"/> 刚被结清（终结之前的状态是 <paramref name="statusBeforeEnding"/>）、它的需求已暂存终结时调用。
    /// 返回是否放出。
    /// </summary>
    public static async Task<bool> StageAsync(
        ControlServerDbContext dbContext,
        JourneyRuntimeRow runtime,
        StationOperationRow? endedOperation,
        StationOperationStatus? statusBeforeEnding,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(runtime);
        if (runtime.Stage != JourneyRuntimeStage.Blocked ||
            endedOperation is null ||
            statusBeforeEnding != StationOperationStatus.RecoveryRequired)
        {
            return false;
        }

        JourneyStopCursor stops = await JourneyStopCursor
            .LoadIncludingUnsavedChangesAsync(dbContext, runtime, cancellationToken).ConfigureAwait(false);
        if (stops.OpenStops.Count == 0)
        {
            return false;
        }
        JourneyStopRow current = stops.Current;
        JourneyDemandRow? ended = stops.AllDemands
            .SingleOrDefault(item => item.Demand.DemandId == endedOperation.DemandId)?.Membership;
        bool blockedOnIt = ended is not null && (endedOperation.OperationType == SlotOperationType.Load
            ? ended.PickupStopId == current.StopId &&
              ended.LoadSlotOperationAttemptId == endedOperation.SlotOperationAttemptId
            : ended.UnloadStopId == current.StopId &&
              ended.UnloadSlotOperationAttemptId == endedOperation.SlotOperationAttemptId);
        if (!blockedOnIt)
        {
            return false;
        }

        string[] otherAttempts =
        [
            .. stops.AllDemands
                .SelectMany(item => new[]
                {
                    item.Membership.LoadSlotOperationAttemptId,
                    item.Membership.UnloadSlotOperationAttemptId
                })
                .Where(attempt => attempt != endedOperation.SlotOperationAttemptId)
        ];
        if (await dbContext.StationOperations.AsNoTracking()
                .AnyAsync(
                    row => otherAttempts.Contains(row.SlotOperationAttemptId) &&
                           (row.Status == StationOperationStatus.Prepared ||
                            row.Status == StationOperationStatus.RecoveryRequired),
                    cancellationToken)
                .ConfigureAwait(false))
        {
            return false;
        }

        runtime.Stage = endedOperation.OperationType == SlotOperationType.Load
            ? JourneyRuntimeStage.AwaitingLoadResult
            : JourneyRuntimeStage.AwaitingUnloadResult;
        runtime.SetBlockReason(null, now);
        runtime.UpdatedAt = now;
        return true;
    }
}
