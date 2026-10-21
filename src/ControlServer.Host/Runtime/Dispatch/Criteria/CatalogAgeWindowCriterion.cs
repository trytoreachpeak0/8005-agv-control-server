namespace ControlServer.Host.Runtime.Dispatch.Criteria;

/// <summary>
/// 目录时间窗准入（REQ-0366，CP-0020，control-server#827）：MesIngest 首次读到已超过目录时间窗的未受理候选，以及首次读到时刻缺失的，不派。
/// </summary>
public sealed class CatalogAgeWindowCriterion : IDispatchAdmissionCriterion
{
    public int Order => 13;

    public Task<string> EvaluateAsync(DispatchCandidateEvaluation evaluation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        _ = cancellationToken;
        return Task.FromResult(
            CatalogAgeWindow.Refusal(evaluation.Round, evaluation.Candidate, evaluation.Round.Now)
                ?? DispatchAdmissionChain.Eligible);
    }
}
