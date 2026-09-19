using ControlServer.Infrastructure.Persistence;

namespace ControlServer.Host.Runtime.Dispatch.Criteria;

/// <summary>
/// The route's AREA machine station must admit this work type under the current admission policy.
/// </summary>
/// <remarks>
/// <para>
/// Station-scoped admission, distinct from the vehicle-scoped admission ticket 09 adds: this one
/// asks whether the work may happen at that station at all, not whether this vehicle may take it.
/// Both are fail-closed and both must pass.
/// </para>
/// <para>
/// <b>The AREA end, not the pickup</b> (control-server#160, overturning I6's criterion side). The admission
/// policy is seeded per area-named machine station, and which end of the route that station is depends on the
/// task type's fixed end: the pickup when the fixed station is the destination (WIRE_TO_GATE, so nothing changes
/// for it), the dropoff when the fixed station is the origin (STAGING_TO_WIRE). control-server#163 relies on this
/// rather than touching the criterion.
/// </para>
/// </remarks>
public sealed class StationTaskTypeAdmissionCriterion(WireToGateStore store) : IDispatchAdmissionCriterion
{
    public int Order => 90;

    public async Task<string> EvaluateAsync(
        DispatchCandidateEvaluation evaluation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evaluation);

        ResolvedJourneyRoute route = evaluation.Route!;
        string areaStation = route.FixedStation.FixedEnd == FixedStationEnd.Origin
            ? route.DropoffStationId
            : route.PickupStationId;
        bool allowed = await store.IsTaskTypeAllowedAsync(
            areaStation,
            evaluation.Candidate.WorkType,
            cancellationToken).ConfigureAwait(false);

        return allowed ? DispatchAdmissionChain.Eligible : "TASK_TYPE_NOT_ALLOWED_AT_STATION";
    }
}
