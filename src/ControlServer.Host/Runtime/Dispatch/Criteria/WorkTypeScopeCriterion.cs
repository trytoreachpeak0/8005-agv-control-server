using Microsoft.Extensions.Options;

namespace ControlServer.Host.Runtime.Dispatch.Criteria;

/// <summary>
/// Only WIRE_TO_GATE demands, and only when the configuration allows that work type.
/// </summary>
/// <remarks>
/// Both halves are checked, not either: the configured allow-list can be widened without that
/// widening silently pulling a work type this journey family cannot execute into scope.
/// </remarks>
public sealed class WorkTypeScopeCriterion(IOptions<JourneyRuntimeOptions> options)
    : IDispatchAdmissionCriterion
{
    private readonly JourneyRuntimeOptions _options = options.Value;

    public int Order => 20;

    public Task<string> EvaluateAsync(
        DispatchCandidateEvaluation evaluation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        _ = cancellationToken;

        bool allowed =
            _options.AllowedWorkTypes.Contains(evaluation.Candidate.WorkType, StringComparer.Ordinal) &&
            string.Equals(evaluation.Candidate.WorkType, "WIRE_TO_GATE", StringComparison.Ordinal);

        return Task.FromResult(allowed ? DispatchAdmissionChain.Eligible : "OUT_OF_SCOPE_WORK_TYPE");
    }
}
