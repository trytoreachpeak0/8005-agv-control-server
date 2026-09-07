namespace ControlServer.Host.Runtime.Dispatch.Criteria;

/// <summary>
/// AREA, EQP and PACKAGE must all be present before anything downstream can resolve a route.
/// </summary>
/// <remarks>
/// Blank counts as missing, not as an empty value to be carried forward: every later criterion
/// reads these as identifiers, and an empty AREA would resolve to whatever a station lookup makes
/// of an empty string.
/// </remarks>
public sealed class RequiredMesFactsCriterion : IDispatchAdmissionCriterion
{
    public int Order => 30;

    public Task<string> EvaluateAsync(
        DispatchCandidateEvaluation evaluation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        _ = cancellationToken;

        bool missing =
            evaluation.Candidate.LiveMesFields is null ||
            string.IsNullOrWhiteSpace(evaluation.Candidate.LiveMesFields.Area) ||
            string.IsNullOrWhiteSpace(evaluation.Candidate.LiveMesFields.Eqp) ||
            string.IsNullOrWhiteSpace(evaluation.Candidate.LiveMesFields.Package);

        return Task.FromResult(missing ? "REQUIRED_MES_FACT_MISSING" : DispatchAdmissionChain.Eligible);
    }
}
