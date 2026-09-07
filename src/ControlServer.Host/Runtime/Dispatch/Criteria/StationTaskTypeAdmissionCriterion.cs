using ControlServer.Infrastructure.Persistence;

namespace ControlServer.Host.Runtime.Dispatch.Criteria;

/// <summary>
/// The resolved pickup station must admit this work type under the current admission policy.
/// </summary>
/// <remarks>
/// Station-scoped admission, distinct from the vehicle-scoped admission ticket 09 adds: this one
/// asks whether the work may happen at that station at all, not whether this vehicle may take it.
/// Both are fail-closed and both must pass.
/// </remarks>
public sealed class StationTaskTypeAdmissionCriterion(WireToGateStore store) : IDispatchAdmissionCriterion
{
    public int Order => 90;

    public async Task<string> EvaluateAsync(
        DispatchCandidateEvaluation evaluation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evaluation);

        bool allowed = await store.IsTaskTypeAllowedAsync(
            evaluation.Route!.PickupStationId,
            evaluation.Candidate.WorkType,
            cancellationToken).ConfigureAwait(false);

        return allowed ? DispatchAdmissionChain.Eligible : "TASK_TYPE_NOT_ALLOWED_AT_STATION";
    }
}
