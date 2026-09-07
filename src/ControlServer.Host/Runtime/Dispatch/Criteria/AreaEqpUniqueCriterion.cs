using ControlServer.Domain;

namespace ControlServer.Host.Runtime.Dispatch.Criteria;

/// <summary>
/// One AREA must map to exactly one EQP across the whole catalog, and it must be this candidate's.
/// </summary>
/// <remarks>
/// The check looks at every open demand in the round's catalog, not just this candidate: an AREA
/// serving two different EQPs means the pickup station this AREA resolves to is ambiguous, and
/// picking either one would send a vehicle to a machine that is not the one the demand came from.
/// Fail-closed on ambiguity rather than on absence.
/// </remarks>
public sealed class AreaEqpUniqueCriterion : IDispatchAdmissionCriterion
{
    public int Order => 50;

    public Task<string> EvaluateAsync(
        DispatchCandidateEvaluation evaluation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        _ = cancellationToken;

        string area = evaluation.Candidate.LiveMesFields!.Area!;
        string[] areaEqps = evaluation.Round.Catalog.Items
            .Where(item =>
                string.Equals(item.WorkType, "WIRE_TO_GATE", StringComparison.Ordinal) &&
                string.Equals(item.LiveMesFields?.Area, area, StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(item.LiveMesFields?.Eqp))
            .Select(item => item.LiveMesFields!.Eqp!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        bool unique = areaEqps.Length == 1 &&
            string.Equals(areaEqps[0], evaluation.Candidate.LiveMesFields.Eqp, StringComparison.Ordinal);

        return Task.FromResult(unique ? DispatchAdmissionChain.Eligible : "AREA_EQP_NOT_UNIQUE");
    }
}
