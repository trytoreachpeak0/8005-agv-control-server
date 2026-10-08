using ControlServer.Application;
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
/// <b>放到哪里。</b>与修复续行（<c>OnboardRecoveryCoordinator.ObserveOperationResultAsync</c>）同一个做法（<see cref="ReleaseStage"/>）：按车此刻所在的
/// 当前停靠是取货还是卸货，回到 <see cref="JourneyRuntimeStage.AwaitingLoadResult"/> 或 <see cref="JourneyRuntimeStage.AwaitingUnloadResult"/>，
/// 阻塞码清掉。control-server#505 之前按那一次操作是装还是卸；第 2 条保证那一次就在当前停靠上，两者相同。#505 放出的「标记」那一支（第 2 条）
/// 终结的可以是前一站装上的需求，那时只有当前停靠说得出车在哪里。
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
/// <para>
/// 或者（control-server#505）：阻塞码是 <c>*_NOT_RECONCILED</c>，而被终结的需求在终结之前正是 <see cref="DemandExecutionStatus.RecoveryRequired"/>
/// ——它就是那个标记。<c>*_NOT_RECONCILED</c> 来自一次恢复结果没对上（<c>KeepDemandAndJourneyBlockedAsync</c>），那时仓位操作一次都不动，
/// 所以那条需求的操作可以早已是 <see cref="StationOperationStatus.Committed"/>：纠错没对上、交接第一次没对上的都是这样，第二次交接对上了，
/// 上面那一支永远不成立，修之前只能改库。阻塞旅程的别的原因由第 3、4 条看着。
/// </para>
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
/// 阻塞码不是不可放行的那一族（<see cref="IsUnreleasable"/>）。第 4 条靠「没对上的需求留在 <c>RecoveryRequired</c>」认出没结论的事实。
/// cs#499 时这个标记不一定在：需求已送达或已取消时，<c>KeepDemandAndJourneyBlockedAsync</c> 照写 <c>*_NOT_RECONCILED</c>、不留标记，所以那时
/// 第 5 条对 <c>*_NOT_RECONCILED</c> 一律不放，代价是「同一条需求第一次没对上、第二次对上」也卡在 <c>Blocked</c>，只能改库。
/// control-server#505 堵了源头：打不上标记的（需求已终结）改写 <see cref="OnEndedDemandSuffix"/>，而且不被后来的码覆盖；纠错只授权给还在车上的需求，
/// 已终结的需求开不了会话。于是普通 <c>*_NOT_RECONCILED</c> 必有一条 <c>RecoveryRequired</c> 的需求，交给第 4 条判；那个标记只能经终结、
/// 修复续行提交或卸货提交清掉，三者都带着车载端一份新的、对上的仓位账。升级前写下的普通 <c>*_NOT_RECONCILED</c> 不满足这个前提，
/// 由一次性数据迁移改成 <see cref="BeforeUpgradeSuffix"/>。不可放行的那一族（两个后缀）两条放行路径都不放，正式出口归 control-server#485。
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
    /// <paramref name="endedOperation"/> 刚被结清（终结之前的状态是 <paramref name="statusBeforeEnding"/>）、它的需求已暂存终结
    /// （终结之前是 <paramref name="demandStatusBeforeEnding"/>）时调用。返回是否放出；放出时调用方记一条事件（谁的哪个结果、原来的阻塞码）。
    /// </summary>
    public static async Task<bool> StageAsync(
        ControlServerDbContext dbContext,
        JourneyRuntimeRow runtime,
        StationOperationRow? endedOperation,
        StationOperationStatus? statusBeforeEnding,
        DemandExecutionStatus? demandStatusBeforeEnding,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(runtime);
        if (runtime.Stage != JourneyRuntimeStage.Blocked || endedOperation is null)
        {
            return false;
        }

        // 第 2 条的「标记」那一支（control-server#505）：没对上的结果留下的阻塞，终结的正是带着标记的那条需求。
        bool carriedTheMark = IsMarkedNotReconciled(runtime.BlockReasonCode) &&
                              demandStatusBeforeEnding == DemandExecutionStatus.RecoveryRequired;
        if (!carriedTheMark && statusBeforeEnding != StationOperationStatus.RecoveryRequired)
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
        if (!blockedOnIt && !carriedTheMark)
        {
            return false;
        }

        if (!await NothingElseUnresolvedAsync(dbContext, runtime, stops, endedOperation, cancellationToken)
                .ConfigureAwait(false))
        {
            return false;
        }

        runtime.Stage = ReleaseStage(stops);
        runtime.SetBlockReason(null, now);
        runtime.UpdatedAt = now;
        return true;
    }

    /// <summary>普通的「恢复结果没对上」阻塞码的后缀：<c>&lt;messageType&gt;_NOT_RECONCILED</c>，那条需求留在 <c>RecoveryRequired</c>。</summary>
    internal const string NotReconciledSuffix = "_NOT_RECONCILED";

    /// <summary>
    /// 结果没对上、而它的需求已送达或已取消、打不上 <c>RecoveryRequired</c> 标记时写的阻塞码后缀（control-server#505）：
    /// <c>&lt;messageType&gt;_NOT_RECONCILED_ON_ENDED_DEMAND</c>。两条放行路径都不放它，也不被后来的阻塞码覆盖；只能人工处理。
    /// </summary>
    internal const string OnEndedDemandSuffix = NotReconciledSuffix + "_ON_ENDED_DEMAND";

    /// <summary>
    /// 升级到 control-server#505 那一刻还停在 <c>Blocked</c> 的普通 <c>*_NOT_RECONCILED</c>，由一次性数据迁移
    /// （<c>UnreleasableNotReconciledBlocksBeforeUpgrade</c>）改成的后缀。那时的代码不保证每个这样的码都有标记，分不出哪一行有，
    /// 所以与 <see cref="OnEndedDemandSuffix"/> 一样不放、不覆盖。升级前这些行本来就被第 5 条一律挡着，现场行为不变。
    /// </summary>
    internal const string BeforeUpgradeSuffix = NotReconciledSuffix + "_BEFORE_UPGRADE";

    /// <summary>这个阻塞码是不是不可放行、也不可覆盖的那一族（<see cref="OnEndedDemandSuffix"/> 与 <see cref="BeforeUpgradeSuffix"/>）。</summary>
    internal static bool IsUnreleasable(string? blockReasonCode) =>
        blockReasonCode is not null &&
        (blockReasonCode.EndsWith(OnEndedDemandSuffix, StringComparison.Ordinal) ||
         blockReasonCode.EndsWith(BeforeUpgradeSuffix, StringComparison.Ordinal));

    /// <summary>这个阻塞码是不是带着标记的「结果没对上」（不可放行的那一族不以 <see cref="NotReconciledSuffix"/> 结尾，两者不相交）。</summary>
    internal static bool IsMarkedNotReconciled(string? blockReasonCode) =>
        blockReasonCode?.EndsWith(NotReconciledSuffix, StringComparison.Ordinal) == true;

    /// <summary>
    /// 放出时回到的阶段：车此刻所在的当前停靠是卸货停靠就等卸货结果，否则等装货结果（control-server#505）。两条放行路径共用。
    /// </summary>
    internal static JourneyRuntimeStage ReleaseStage(JourneyStopCursor stops)
    {
        ArgumentNullException.ThrowIfNull(stops);
        return stops.Current.StopRole == JourneyStopRoles.Unload
            ? JourneyRuntimeStage.AwaitingUnloadResult
            : JourneyRuntimeStage.AwaitingLoadResult;
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
    /// <b>出口。</b>不成立时旅程留在 <c>Blocked</c>。另一条需求后来补救成功时，<see cref="StageAsync"/> 经第 2 条的「标记」那一支放出它
    /// （control-server#505）；「同一条需求先补偿没对上、再修复续行对上」也放出（#506 时第 5 条挡着它，#505 放宽）。
    /// 不可放行的那一族（<see cref="IsUnreleasable"/>）两条路径都不放。
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
        if (IsUnreleasable(runtime.BlockReasonCode))
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
