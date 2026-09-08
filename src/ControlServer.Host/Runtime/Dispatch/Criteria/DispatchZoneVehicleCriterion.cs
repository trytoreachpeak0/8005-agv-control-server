namespace ControlServer.Host.Runtime.Dispatch.Criteria;

/// <summary>
/// The zone this candidate's route falls in must be served by this vehicle.
/// </summary>
/// <remarks>
/// <para>
/// B2's zone→vehicle hard set. The relation is stated one way round on purpose: a zone names the
/// vehicles that serve it, so a zone nobody was configured for has no vehicles rather than all of
/// them, and adding a vehicle to a zone is a deliberate edit rather than the absence of one.
/// </para>
/// <para>
/// <b>Order 65 — immediately after station resolution.</b> The zone is a property of the route,
/// which <see cref="StationResolutionCriterion"/> at order 60 is what produces; nothing before it
/// knows which zone the candidate would be served in. It runs ahead of the package, dynamic-fact
/// and slot work below, all of which is wasted on a vehicle that does not serve the zone.
/// </para>
/// <para>
/// The two refusals are separate for the same reason as in
/// <see cref="VehicleTaskTypeAdmissionCriterion"/>: "this zone is not configured" is an operator's
/// omission and "this vehicle does not serve it" is the policy working.
/// </para>
/// </remarks>
public sealed class DispatchZoneVehicleCriterion : IDispatchAdmissionCriterion
{
    /// <summary>No vehicle at all is configured for the route's dispatch zone.</summary>
    public const string ZoneNotConfiguredReason = "DISPATCH_ZONE_HAS_NO_VEHICLES";

    /// <summary>The zone is configured, and this vehicle is not one of the ones that serve it.</summary>
    public const string VehicleNotInZoneReason = "VEHICLE_NOT_ADMITTED_IN_ZONE";

    public int Order => 65;

    public Task<string> EvaluateAsync(
        DispatchCandidateEvaluation evaluation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        _ = cancellationToken;

        string zone = evaluation.Route!.DispatchZone;
        if (!evaluation.Round.Policy.ZoneVehicles.TryGetValue(zone, out IReadOnlySet<string>? agvIds))
        {
            return Task.FromResult(ZoneNotConfiguredReason);
        }

        return Task.FromResult(agvIds.Contains(evaluation.Vehicle.AgvId)
            ? DispatchAdmissionChain.Eligible
            : VehicleNotInZoneReason);
    }
}
