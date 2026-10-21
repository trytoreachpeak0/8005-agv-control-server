namespace ControlServer.Host.Runtime.Dispatch.Criteria;

/// <summary>
/// 目录时间窗准入（REQ-0366，CP-0020，control-server#827）：MesIngest 首次读到已超过目录时间窗的未受理候选，以及首次读到时刻缺失的，不派。
/// </summary>
/// <remarks>
/// <para>
/// 判法只有 <see cref="CatalogAgeWindow.Refusal"/> 一处，这里按开轮时刻判；受理前重读目录之后由派车轮按受理那一刻再判一次。
/// </para>
/// <para>
/// <b>Order 45：在两道范围判据之后、站点解析之前。</b>
/// </para>
/// <list type="bullet">
/// <item>之后：任务类型不在本部署范围（20）与 AREA 不在分区归属表（40，静默）是配置上的「本线不执行」，与新旧无关。时间窗排在它们前面，
/// 全厂那一大批共晶等不归本线的旧需求就会改报时间窗的码，从静默计数挪进看板积压列表。</item>
/// <item>之前：站点解析（60）及其后是会立结构性阻断的判据。超窗的需求停在这里，就不会为一条有意不派的需求立「请人工搬运」的阻断；
/// 轮末结构性汇总另把这两个码当作静默，清掉它在窗内时立过的阻断。</item>
/// </list>
/// <para>
/// 代价：逐车判据（15～25）在它前面，所以车在充电、有故障时，那辆车对超窗需求报的是车自己的码。积压行上的原因码是这一轮最后一辆判它的车
/// 写下的（<c>DispatchRoundRunner.JudgeForVehicleAsync</c>），车多时可能是某辆车自己的码而不是时间窗的码；结构性汇总看的是全部车的裁决，
/// 只要有一辆走到这里就按时间窗处理（审查 L2）。
/// 序号 30、35 的数据类判据（MES 字段缺失、同批多任务类型）也在它前面，那是数据问题，先报它不误事。
/// </para>
/// </remarks>
public sealed class CatalogAgeWindowCriterion : IDispatchAdmissionCriterion
{
    public int Order => 45;

    public Task<string> EvaluateAsync(DispatchCandidateEvaluation evaluation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        _ = cancellationToken;
        return Task.FromResult(
            CatalogAgeWindow.Refusal(evaluation.Round, evaluation.Candidate, evaluation.Round.Now)
                ?? DispatchAdmissionChain.Eligible);
    }
}
