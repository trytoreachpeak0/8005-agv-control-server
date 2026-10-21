namespace ControlServer.Host.Runtime.Dispatch.Criteria;

/// <summary>
/// 试验机批号准入（REQ-0368，CP-0023，control-server#828）。
/// </summary>
public sealed class TrialSublotCriterion : IDispatchAdmissionCriterion
{
    public int Order => 42;

    public Task<string> EvaluateAsync(DispatchCandidateEvaluation evaluation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        _ = cancellationToken;
        return Task.FromResult(DispatchAdmissionChain.Eligible);
    }
}
