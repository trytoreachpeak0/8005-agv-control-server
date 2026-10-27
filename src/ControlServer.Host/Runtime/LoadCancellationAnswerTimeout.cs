using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ControlServer.Host.Runtime;

/// <summary>
/// 装货中取消授权之后，车一直不答（control-server#837，用户 2026-10-27 选定的方案 a）：等答复超过
/// <see cref="JourneyRuntimeOptions.LoadCancellationAnswerTimeout"/>，服务端收口这次取消、把它取消的装货标为待恢复、旅程挂起，人工的出口就都开了。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么要有。</b>取消授权之后，取消工作流停在 <see cref="RecoveryWorkflowState.AwaitingResult"/> 等车报 <c>LoadCancellationResult</c>，此前没有超时。
/// 这期间人工判仓位故障被拒（取消正在进行），异常恢复会话要旅程先挂起才开，而旅程还在等装货结果；装货自己报确定性失败，引擎也因为取消还开着不终结需求。
/// 车载端不答——断网、程序崩溃，或 onboard-hmi#392 修好之前按钮按不了——这辆车就只能改库。车载端已经修了按钮，这里是服务端的兜底。
/// </para>
/// <para>
/// <b>做什么。</b>取消工作流收口为 <see cref="RecoveryWorkflowState.RecoveryRequired"/>，结论 <see cref="RecoveryWorkflowOutcomes.LoadCancellationAnswerTimedOut"/>。
/// 它取消的装货还是 <see cref="StationOperationStatus.Prepared"/> 时标为 <see cref="StationOperationStatus.RecoveryRequired"/>（与没对上的取消结果同一个判法，
/// control-server#697），旅程在等装货结果时挂起为 <see cref="BlockReason"/>。需求不动：它的去留由人工恢复的结果决定（补偿、交接终结它），与
/// <c>LOAD_RESULT_REQUIRES_RECOVERY</c> 一样，恢复结果落库时由 <see cref="BlockedJourneyRelease"/> 按「这次装货原来待恢复」放行旅程上别的需求。
/// 装货已经有了结论（例如这期间装货自己报了 COMPLETED，旅程已往下走）的，只收口取消，什么都不改。
/// </para>
/// <para>
/// <b>计时从落库的授权时刻起算</b>（取消工作流的 <c>CreatedAt</c>，服务端的钟），重启不重新计。判在引擎就绪门之前：车载端不答时会话常常不就绪（真车载端
/// 挂着本服务端的在途单时整段未就绪），判在门后就永远到不了。
/// </para>
/// <para>
/// <b>与车的答复只有一个先落库。</b>判定在写事务里重读工作流，答复也在收件箱的写事务里处理，库的写事务互斥：答复先到的，这里看到它已不在
/// <see cref="RecoveryWorkflowState.AwaitingResult"/>，什么都不做；这里先到的，迟到的答复由 <c>OnboardRecoveryCoordinator</c> 认出这个结论，只记证据与日志。
/// </para>
/// </remarks>
internal static class LoadCancellationAnswerTimeout
{
    /// <summary>旅程挂起的阻塞码：取消装货等车答复超时，这次装货待人工恢复。</summary>
    public const string BlockReason = "LOAD_CANCELLATION_UNANSWERED";

    private static readonly Action<ILogger, string, string, string, string, TimeSpan, string, Exception?> LogTimedOut =
        LoggerMessage.Define<string, string, string, string, TimeSpan, string>(
            LogLevel.Warning,
            new EventId(2340, nameof(LogTimedOut)),
            "Load cancellation {CancellationId} on {AgvId} (demand {DemandId}, attempt {AttemptId}) had no answer from the vehicle "
            + "within {Timeout} of its authorization; closed as LOAD_CANCELLATION_ANSWER_TIMED_OUT, {Effect}.");

    /// <summary>
    /// 收口这辆车上属于 <paramref name="runtime"/> 的、授权早于 <paramref name="now"/> 减 <paramref name="timeout"/> 还没答复的装货中取消。自己开写事务并保存
    /// （调用方已有事务就加入它）。返回是否收口了至少一条。
    /// </summary>
    public static async Task<bool> TryExpireAsync(
        ControlServerDbContext dbContext,
        JourneyRuntimeRow runtime,
        DateTimeOffset now,
        TimeSpan timeout,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(logger);
        // CreatedAt is compared here, not in SQL: SQLite does not order DateTimeOffset, and a vehicle has one or two such rows.
        var waiting = await dbContext.RecoveryWorkflows.AsNoTracking()
            .Where(row => row.AgvId == runtime.AgvId && row.WorkflowType == LoadCancellationBeforeSublot.WorkflowType &&
                          row.State == RecoveryWorkflowState.AwaitingResult && row.SlotOperationAttemptId != null &&
                          row.DemandId != null)
            .Select(row => new { row.WorkflowId, row.DemandId, row.CreatedAt })
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        string[] due = [.. waiting.Where(row => now - row.CreatedAt >= timeout).Select(row => row.WorkflowId)];
        if (due.Length == 0)
        {
            return false;
        }

        List<(RecoveryWorkflowRow Workflow, string Effect)> expired = [];
        IDbContextTransaction? transaction = dbContext.Database.CurrentTransaction is null
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false)
            : null;
        try
        {
            await dbContext.Entry(runtime).ReloadAsync(cancellationToken).ConfigureAwait(false);
            foreach (string workflowId in due)
            {
                RecoveryWorkflowRow workflow = await dbContext.RecoveryWorkflows
                    .SingleAsync(row => row.WorkflowId == workflowId, cancellationToken).ConfigureAwait(false);
                await dbContext.Entry(workflow).ReloadAsync(cancellationToken).ConfigureAwait(false);
                // Answered between the read above and this transaction: the answer settled it.
                if (workflow.State != RecoveryWorkflowState.AwaitingResult ||
                    !await DemandJourneyLookup.JourneyOf(dbContext, workflow.DemandId!)
                        .AnyAsync(row => row.JourneyId == runtime.JourneyId, cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }
                workflow.State = RecoveryWorkflowState.RecoveryRequired;
                workflow.Outcome = RecoveryWorkflowOutcomes.LoadCancellationAnswerTimedOut;
                workflow.UpdatedAt = now;
                StationOperationRow? load = await dbContext.StationOperations
                    .SingleOrDefaultAsync(row => row.SlotOperationAttemptId == workflow.SlotOperationAttemptId, cancellationToken)
                    .ConfigureAwait(false);
                if (load is not null)
                {
                    await dbContext.Entry(load).ReloadAsync(cancellationToken).ConfigureAwait(false);
                }
                string effect;
                if (load is { OperationType: SlotOperationType.Load, Status: StationOperationStatus.Prepared } &&
                    load.DemandId == workflow.DemandId)
                {
                    load.Status = StationOperationStatus.RecoveryRequired;
                    if (runtime.Stage == JourneyRuntimeStage.AwaitingLoadResult)
                    {
                        runtime.Stage = JourneyRuntimeStage.Blocked;
                        runtime.SetBlockReason(BlockReason, now);
                        runtime.UpdatedAt = now;
                        effect = $"the load held for recovery and journey {runtime.JourneyId} blocked under {BlockReason}";
                    }
                    else
                    {
                        effect = $"the load held for recovery, journey {runtime.JourneyId} left at {runtime.Stage}/{runtime.BlockReasonCode}";
                    }
                }
                else
                {
                    effect = $"the load already {load?.Status.ToString() ?? "missing"}, nothing else changed";
                }
                expired.Add((workflow, effect));
            }
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync().ConfigureAwait(false);
            }
        }
        // Logged once the change is committed, so the record never names a closing the database does not hold.
        foreach ((RecoveryWorkflowRow workflow, string effect) in expired)
        {
            LogTimedOut(
                logger, workflow.WorkflowId, workflow.AgvId, workflow.DemandId!, workflow.SlotOperationAttemptId!, timeout, effect,
                null);
        }
        return expired.Count > 0;
    }
}
