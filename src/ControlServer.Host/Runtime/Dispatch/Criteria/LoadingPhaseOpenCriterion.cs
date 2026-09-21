namespace ControlServer.Host.Runtime.Dispatch.Criteria;

/// <summary>
/// 装货阶段已经结束的在途车不再接追加（批次7-07，control-server#212；REQ-0354 末句「持货超时或让站之后不再接受新的待装
/// Demand」，以及装满之后离开最后一个装货停靠）。
/// </summary>
/// <remarks>
/// <para>
/// <b>只在在途链上。</b>空闲车没有旅程，也就没有装货阶段。
/// </para>
/// <para>
/// <b>排在追加的四道门之后（98）、仓位之前（100）</b>，两边各有一条理由：
/// </para>
/// <list type="bullet">
/// <item>在四道门之后，是为了不改既有的原因码。分区禁止途中追加的旅程装完就是 <c>CLOSED</c>／
/// <c>PLANNED_LOADING_COMPLETE</c>，排在前面的话，这些车对每条候选报的都会从 <c>EN_ROUTE_APPEND_NOT_CONFIGURED</c>
/// 变成本条——而前者才是现场能据此行动的那一个（去配参数），也是 <c>en-route-append-not-configured</c> 场景断言的那一个。</item>
/// <item>在仓位之前，是为了守住 <see cref="SlotGroupFullnessBoard"/> 读的那个前提：一条候选拿到「本车货物占侧」，
/// 就说明它排在前面的每一条都过了。一辆装货阶段已经结束的车，不该有任何一侧因为一条它反正不会接的候选被判满。</item>
/// </list>
/// <para>
/// 与 <c>WireToGateStore.StageAndCommitAppendAsync</c> 事务里那一处判的是两个时刻：这里是轮次开始时读到的计划，
/// 那里是写入那一刻的旅程行。
/// </para>
/// </remarks>
public sealed class LoadingPhaseOpenCriterion : IDispatchAdmissionCriterion
{
    public int Order => 99;

    public Task<string> EvaluateAsync(DispatchCandidateEvaluation evaluation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        _ = cancellationToken;
        return Task.FromResult(evaluation.Vehicle.Plan is { LoadingPhaseClosed: true }
            ? DispatchReasonCodes.LoadingPhaseClosed
            : DispatchAdmissionChain.Eligible);
    }
}
