namespace ControlServer.Host.Runtime.Dispatch.Criteria;

/// <summary>
/// 试验机批号准入（REQ-0368，CP-0023，control-server#828）：SUBLOT 带配置的试验机标记的未受理候选不派、不受理。
/// </summary>
/// <remarks>
/// <para>
/// 判法只有 <see cref="TrialSublot.Refusal"/> 一处。
/// </para>
/// <para>
/// <b>Order 42：在两道范围判据之后、目录时间窗（45）之前。</b>
/// </para>
/// <list type="bullet">
/// <item>之后：任务类型不在本部署范围（20）与 AREA 不在分区归属表（40，静默）是配置上的「本线不执行」，不归本线的试验批照旧静默计数，
/// 不挪进看板积压列表。</item>
/// <item>之前：条文定先于目录时间窗判，既是试验批又超窗的候选报试验批的码。站点解析（60）及其后会立结构性阻断，试验批停在这里，
/// 不会为它立「请人工搬运」的阻断；轮末结构性汇总另把它当作静默，清掉规则打开前立过的阻断。</item>
/// </list>
/// <para>
/// 代价同 <see cref="CatalogAgeWindowCriterion"/>：逐车判据（15～25）与数据类判据（30、35）在它前面，车多时积压行上的码可能是某辆车自己的码；
/// 结构性汇总看的是全部车的裁决，只要有一辆走到这里就按试验批处理。
/// </para>
/// </remarks>
public sealed class TrialSublotCriterion : IDispatchAdmissionCriterion
{
    public int Order => 42;

    public Task<string> EvaluateAsync(DispatchCandidateEvaluation evaluation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        _ = cancellationToken;
        return Task.FromResult(TrialSublot.Refusal(evaluation.Round, evaluation.Candidate) ?? DispatchAdmissionChain.Eligible);
    }
}
