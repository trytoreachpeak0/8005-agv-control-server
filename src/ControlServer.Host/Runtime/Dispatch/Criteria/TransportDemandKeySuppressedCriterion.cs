using ControlServer.Application;

namespace ControlServer.Host.Runtime.Dispatch.Criteria;

/// <summary>
/// 候选的业务键已被本地取消抑制，永远不再成为候选（REQ-0155、REQ-0156、REQ-0211；批次7-05，control-server#210）。
/// </summary>
/// <remarks>
/// <para>
/// <b>挡的是键，不是 DemandId。</b>本地取消只终结那一条 <c>DemandId</c>，而 MesIngest 在需求仍可见、或 GONE 后再现时会给同一个
/// 业务键发新的 <c>DemandId</c>。REQ-0155 要的是那个键不再被执行，不管它这次叫什么。同一 SUBLOT 的另一任务类型是另一个键，
/// 不受影响——键本身就是 <c>sublot|workType</c>。
/// </para>
/// <para>
/// <b>Order 11：紧跟 <see cref="AlreadyAcceptedCriterion"/>，在一切车辆判据之前。</b>它是需求本身的结论，与车无关，
/// 所以任何一辆车问到它都得到同一个答案；排在前面也省掉后面的读。被取消的那一条自己（同一个 <c>DemandId</c>）仍在目录里时，
/// 第 10 道先给它 <c>DEMAND_ALREADY_ACCEPTED</c>，既有场景的期待不变。
/// </para>
/// <para>
/// <b>每次都读库，不读轮次事实。</b>抑制由终结路径与终态同一次保存写下，派车轮之外随时可能发生；读 <c>DispatchRoundFacts</c>
/// 里的快照就会漏掉本轮开始之后写下的那一条。
/// </para>
/// </remarks>
public sealed class TransportDemandKeySuppressedCriterion(ITransportDemandSuppressionStore suppressions)
    : IDispatchAdmissionCriterion
{
    public const string Reason = DispatchReasonCodes.TransportDemandKeySuppressed;

    public int Order => 11;

    public async Task<string> EvaluateAsync(DispatchCandidateEvaluation evaluation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        TransportDemandSuppression? suppression = await suppressions
            .ReadAsync(evaluation.Candidate.TransportDemandKey, cancellationToken).ConfigureAwait(false);
        return suppression is null ? DispatchAdmissionChain.Eligible : Reason;
    }
}
