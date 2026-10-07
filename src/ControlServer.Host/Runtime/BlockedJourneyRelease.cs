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
/// <b>只在五条都成立时放</b>，任何一条不成立就留在 <c>Blocked</c>、什么都不写：
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
/// <item><description>
/// 这趟旅程别的需求没有一条是 <see cref="DemandExecutionStatus.RecoveryRequired"/>（control-server#499 独立审查必修 M-1）。一条需求的
/// 恢复结果没对上（<c>KeepDemandAndJourneyBlockedAsync</c>）时，需求留在 <c>RecoveryRequired</c>、阻塞码改成
/// <c>*_NOT_RECONCILED</c>，而仓位操作一次都不动，所以第 3 条看不见它：交接报了 <c>HANDED_OFF</c> 却有仓位读到 <c>OCCUPIED</c>，
/// 货在不在车上没有结论；扫码前取消的结果在旅程阻塞之后才到，那条需求还挂在本站待装的清单上。这时放出，车会带着没结论的货去做离站核验，
/// 或者向车再要一次那条需求的录入；而且旅程一离开 <c>Blocked</c>，管理员为那条需求开的会话就被拒（<c>RECOVERY_DEMAND_NOT_BLOCKED</c>）。
/// 被终结的这一条自己不算——它在终结之前本来就是 <c>RecoveryRequired</c>。
/// </description></item>
/// <item><description>
/// 阻塞码不是 <c>*_NOT_RECONCILED</c>（cs#499 调度裁定）。第 4 条靠「没对上的需求留在 <c>RecoveryRequired</c>」认出没结论的事实，
/// 而这个标记不一定在：需求已送达或已取消时，<c>KeepDemandAndJourneyBlockedAsync</c> 照写阻塞码、不留标记。例如对一条已卸需求开的纠错
/// （授权不看需求还在不在车上，cs#287）结果没对上，压在另一条的装货恢复之上，阻塞码被覆盖成 <c>LoadCorrectionResult_NOT_RECONCILED</c>，
/// 再补偿另一条时第 2、3、4 条都成立。阻塞码本身不记是哪条需求留下的，分不出这一种与安全的那一种，所以一律不放。
/// 代价：同一条需求「第一次补偿或交接没对上、第二次对上」时阻塞码也是 <c>*_NOT_RECONCILED</c>，多需求旅程照旧停在 <c>Blocked</c>
/// （与修之前相同）。放错碰准入线 1，停住碰准入线 3，宁可停住；源头修好、阻塞码必有标记之后再放宽，见 control-server#505。
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
/// <b>离线收尾（control-server#485）不调这里。</b>这里只服务「车还在、会话结果刚落库」的那一刻：放出之后由引擎接着走，而引擎要车答话。
/// 车永久离线时，只终结阻塞旅程的那一条再调这里，旅程会离开 <c>Blocked</c>、停在 <c>AwaitingLoadResult</c> /
/// <c>ONBOARD_SESSION_NOT_READY</c>，此后离线收尾与异常处置会话都只认 <c>Blocked</c>，剩下的需求就再也碰不到了（独立审查 S-1）。
/// 离线时剩下的需求怎么办由 control-server#485 自己决定，例如一并终结、由 <see cref="PickupStopTermination"/> 收尾。
/// </para>
/// </remarks>
internal static class BlockedJourneyRelease
{
    /// <summary>
    /// <paramref name="endedOperation"/> 刚被结清（终结之前的状态是 <paramref name="statusBeforeEnding"/>）、它的需求已暂存终结时调用。
    /// 返回是否放出；放出时调用方记一条事件（谁的哪个结果、原来的阻塞码）。
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

        if (!await NothingElseUnresolvedAsync(dbContext, runtime, stops, endedOperation, cancellationToken)
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

    /// <summary>
    /// 第 3、4、5 条：除了刚结清的 <paramref name="settled"/> 这一次（与它的需求）之外，旅程里没有别的事还没结论。两条放行路径共用它——
    /// 这里的 <see cref="StageAsync"/>，与修复续行对上之后的 <c>OnboardRecoveryCoordinator.ObserveOperationResultAsync</c>（control-server#506）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 只收这三条，因为第 1、2 条修复续行用不着：授权修复续行要求那一次操作是 <c>RecoveryRequired</c>、车报的未结操作正是它
    /// （<c>ValidateActionPreconditions</c>），开会话要求旅程是 <c>Blocked</c>（<c>ValidateSessionScopeAsync</c>）；而终结那一支的第 2 条
    /// 判的是「终结之前的状态」，修复续行的结果到时操作已被结果改成 <c>Committed</c>，没有对应的量。加给修复续行只会改动它今天放车的其他情形。
    /// </para>
    /// <para>
    /// 修复续行为什么三条都要（#506 读代码与实测）：开会话与授权都不看阻塞码，也不看别的需求。一条需求没对上而待恢复时，对另一条修复续行对上了，
    /// 修之前旅程照样放出、接着发离站核验（第 4 条）；没有标记的 <c>*_NOT_RECONCILED</c>（见上面第 5 条）同样压得在修复续行之下（第 5 条）；
    /// 第 3 条在正常流程里几乎被第 4 条盖住，留着守服务器自己的不变量。
    /// </para>
    /// <para>
    /// <b>出口。</b>不成立时旅程留在 <c>Blocked</c>。另一条需求后来补救成功时，<see cref="StageAsync"/> 也放不出它：那一条的操作早已不是
    /// <c>RecoveryRequired</c>（第 2 条），阻塞码也还是 <c>*_NOT_RECONCILED</c>（第 5 条）。今天只能改库，与 control-server#505 第 1 件同一个形状，
    /// 由 #505 给阻塞码补上标记之后一并放宽。第 5 条还让「同一条需求先补偿没对上、再修复续行对上」从放出变成留在 <c>Blocked</c>，单需求旅程也是
    /// （调度 10-07 裁定接受：放错碰准入线 1，停住碰准入线 3）。
    /// </para>
    /// </remarks>
    internal static async Task<bool> NothingElseUnresolvedAsync(
        ControlServerDbContext dbContext,
        JourneyRuntimeRow runtime,
        JourneyStopCursor stops,
        StationOperationRow settled,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(stops);
        ArgumentNullException.ThrowIfNull(settled);
        if (runtime.BlockReasonCode?.EndsWith("_NOT_RECONCILED", StringComparison.Ordinal) == true)
        {
            return false;
        }

        if (stops.AllDemands.Any(item =>
                item.Demand.DemandId != settled.DemandId &&
                item.Demand.Status == DemandExecutionStatus.RecoveryRequired))
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
                .Where(attempt => attempt != settled.SlotOperationAttemptId)
        ];
        return !await dbContext.StationOperations.AsNoTracking()
            .AnyAsync(
                row => otherAttempts.Contains(row.SlotOperationAttemptId) &&
                       (row.Status == StationOperationStatus.Prepared ||
                        row.Status == StationOperationStatus.RecoveryRequired),
                cancellationToken)
            .ConfigureAwait(false);
    }
}
