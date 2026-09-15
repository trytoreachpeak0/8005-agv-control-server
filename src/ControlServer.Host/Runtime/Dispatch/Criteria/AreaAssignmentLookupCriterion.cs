using ControlServer.Application;

namespace ControlServer.Host.Runtime.Dispatch.Criteria;

/// <summary>
/// Looks the demand's AREA up in the area assignment table the round read, and records what it found for
/// the criteria behind it. Never blocks.
/// </summary>
/// <remarks>
/// <para>
/// A step in the chain rather than a criterion that decides, placed by control-server#69 so the feature
/// tickets only have to read the result: the whitelist and dispatch zone (#72), slot group selection (#73)
/// and the structural classification (#74). Refusing an AREA the table does not name is #72's decision, and
/// making it here would change who admits what before that ticket does.
/// </para>
/// <para>
/// Runs after <see cref="RequiredMesFactsCriterion"/>, which is what makes reading AREA here safe, and ahead
/// of <see cref="AreaScopeCriterion"/>, the first criterion that decides on AREA.
/// </para>
/// </remarks>
public sealed class AreaAssignmentLookupCriterion : IDispatchAdmissionCriterion
{
    public int Order => 35;

    public Task<string> EvaluateAsync(
        DispatchCandidateEvaluation evaluation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        _ = cancellationToken;

        AreaAssignmentTableVersion? table = evaluation.Round.AreaAssignments;
        evaluation.AreaAssignmentVersion = table?.Version;
        evaluation.AreaAssignment = table?.ByArea.GetValueOrDefault(evaluation.Candidate.LiveMesFields!.Area!);
        evaluation.RequiredSlotPosition = evaluation.AreaAssignment?.SlotPosition;
        return Task.FromResult(DispatchAdmissionChain.Eligible);
    }
}
