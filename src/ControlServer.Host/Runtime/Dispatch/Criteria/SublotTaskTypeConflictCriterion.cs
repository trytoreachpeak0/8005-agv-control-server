namespace ControlServer.Host.Runtime.Dispatch.Criteria;

/// <summary>
/// 同一份完整 MES 快照里，一个 Sublot 命中了多于一种任务类型：该 Sublot 的<b>全部</b>候选都挡
/// （<c>REQ-0189</c>；批次7-06，control-server#211）。
/// </summary>
/// <remarks>
/// <para>
/// <b>挡的是整个 Sublot，不是「多出来的那一条」。</b>矛盾的是 MES 给的事实本身：同一批料同时说要走两种工艺，
/// 服务端没有判断哪一种对的依据。挑一条执行等于替 MES 做决定，而那批料已经在车上之后就改不回来了。
/// </para>
/// <para>
/// <b>只影响这一个 Sublot。</b>别的 Sublot 照常判——这是「一辆车承载多条独立需求，各自保留任务类型、端点、状态、
/// 取消、仓位与审计边界」的另一面：边界独立，所以一条的数据出问题不该牵连别的。
/// </para>
/// <para>
/// 批次 6 的 <see cref="AreaEqpUniqueCriterion"/> 管的是另一件事（AREA→EQP 的唯一），它不看任务类型，
/// 所以同一个 Sublot 命中两种任务类型从它那里过得去。
/// </para>
/// </remarks>
public sealed class SublotTaskTypeConflictCriterion : IDispatchAdmissionCriterion
{
    /// <summary>
    /// 紧跟在必需 MES 事实之后：判定只用快照里的 <c>Sublot</c> 与 <c>WorkType</c>，两者齐备就能判，
    /// 而它能省掉后面每一次站点解析与远程读。
    /// </summary>
    public int Order => 35;

    public Task<string> EvaluateAsync(DispatchCandidateEvaluation evaluation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        _ = cancellationToken;

        string sublot = evaluation.Candidate.Sublot;
        // 本轮那一份完整快照，不是历史：矛盾是「这一份里同时出现两种」，而跨快照的变化是普通的需求改写。
        bool conflicted = evaluation.Round.Catalog.Items
            .Where(item => string.Equals(item.Sublot, sublot, StringComparison.Ordinal))
            .Select(item => item.WorkType)
            .Distinct(StringComparer.Ordinal)
            .Count() > 1;
        return Task.FromResult(conflicted
            ? DispatchReasonCodes.SublotTaskTypeConflict
            : DispatchAdmissionChain.Eligible);
    }
}
