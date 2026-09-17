namespace ControlServer.Host.Runtime.Dispatch.Criteria;

/// <summary>
/// Only AREAs the area assignment table names are executed (REQ-0191).
/// </summary>
/// <remarks>
/// <para>
/// The table is the whole whitelist. There is no prefix rule and no guess from the AREA's spelling: the
/// leading N this used to test only held because every AREA executed on map 25 started with N, and it refused
/// a die-attach machine (AREAs starting with T) however that machine was configured.
/// </para>
/// <para>
/// Reads what <see cref="AreaAssignmentLookupCriterion"/> recorded from the version the round read, so the
/// version that admits a demand is the version the demand freezes. With no table imported at all, nothing is
/// in scope.
/// </para>
/// <para>
/// The refusal is silent (<see cref="DispatchReasonCodes.OutOfScopeArea"/>): backlog only.
/// </para>
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
            evaluation.AreaAssignment is not null
                ? DispatchAdmissionChain.Eligible
                : DispatchReasonCodes.OutOfScopeArea);
    }
}
