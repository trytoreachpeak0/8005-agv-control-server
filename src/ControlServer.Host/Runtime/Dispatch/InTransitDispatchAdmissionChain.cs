namespace ControlServer.Host.Runtime.Dispatch;

/// <summary>
/// 在途车那条资格链（REQ-0205；批次7-06，control-server#211）：与空闲车那条同样 fail-closed、同样按 <c>Order</c> 排，
/// 判据集合不同。
/// </summary>
/// <remarks>
/// <para>
/// <b>两条链，不是一条链加分支。</b>空闲车那条要求 RIoT 报 <c>IDLE</c>、速度为零、没有在执行的订单——一辆正跑着自己
/// 那趟旅程的车永远不满足，而那正是它该有的样子。把那几条加个「在途就跳过」的分支，会让改动的人很难看出自己动的是
/// 哪一种车；两条链则让每一条判据都只对一种车说话。
/// </para>
/// <para>
/// <b>两条链各自守什么</b>（PR 里那张对照表）：
/// </para>
/// <para>
/// <b>共用</b>——已受理、车辆故障、任务类型范围、车辆任务类型准入、必需 MES 事实、同一 Sublot 任务类型冲突、
/// 区域分配查找与范围、AREA→EQP 唯一、站点解析、分区车辆、包装容量、准入策略漂移、站点任务类型准入、
/// 路网可达性、目录可用性、建单前门、仓位容量（按侧账本）。
/// </para>
/// <para>
/// <b>只在空闲链</b>——<see cref="Criteria.VehicleDynamicFactsCriterion"/>（含 <c>IDLE</c>、速度为零、无订单占用）。
/// </para>
/// <para>
/// <b>只在在途链</b>——<see cref="Criteria.InTransitVehicleFactsCriterion"/>（同样的安全、连接、绑定、Map、新鲜度、
/// 电量，但不问 <c>IDLE</c> 与订单）与 <see cref="Criteria.EnRouteAppendCriterion"/>（追加的四道门）。
/// </para>
/// </remarks>
public sealed class InTransitDispatchAdmissionChain(IReadOnlyList<IDispatchAdmissionCriterion> criteria)
{
    private readonly IDispatchAdmissionCriterion[] _criteria =
        [.. criteria.OrderBy(criterion => criterion.Order)];

    /// <summary>链里每一条判据，按被问到的顺序——PR 与测试用它列出这条链守了什么。</summary>
    public IReadOnlyList<IDispatchAdmissionCriterion> Criteria => _criteria;

    /// <summary>跑完这条链，返回 <see cref="DispatchAdmissionChain.Eligible"/> 或第一条拦下它的理由。</summary>
    public async Task<string> EvaluateAsync(
        DispatchCandidateEvaluation evaluation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        foreach (IDispatchAdmissionCriterion criterion in _criteria)
        {
            string reason = await criterion.EvaluateAsync(evaluation, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(reason, DispatchAdmissionChain.Eligible, StringComparison.Ordinal))
            {
                return reason;
            }
        }

        return DispatchAdmissionChain.Eligible;
    }
}
