namespace ControlServer.Host.Runtime.Dispatch.Criteria;

/// <summary>
/// Only AREAs this journey family serves, identified by the leading N.
/// </summary>
/// <remarks>
/// Runs after <see cref="RequiredMesFactsCriterion"/>, which is what makes reading AREA here safe.
/// </remarks>
public sealed class AreaScopeCriterion : IDispatchAdmissionCriterion
{
    public int Order => 40;

    public Task<string> EvaluateAsync(
        DispatchCandidateEvaluation evaluation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        _ = cancellationToken;

        return Task.FromResult(
            evaluation.Candidate.LiveMesFields!.Area!.StartsWith('N')
                ? DispatchAdmissionChain.Eligible
                : "OUT_OF_SCOPE_AREA");
    }
}
