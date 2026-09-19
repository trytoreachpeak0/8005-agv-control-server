using ControlServer.Application;
using Microsoft.Extensions.Options;

namespace ControlServer.Host.Runtime.Dispatch.Criteria;

/// <summary>
/// Resolves the candidate's task type to its fixed station and its AREA to a unique station, has the
/// plan builder turn the two into a route, and validates the route.
/// </summary>
/// <remarks>
/// <para>
/// This is the criterion that produces <see cref="DispatchCandidateEvaluation.Route"/>; everything
/// after it depends on that being set. A resolution failure carries the resolver's own reason code
/// through unchanged rather than being flattened into one generic block — the operator needs to
/// know whether the station was missing, ambiguous or out of the Map.
/// </para>
/// <para>
/// The fixed station comes from the round's view, asked for this candidate's task type. A task type
/// the view refuses is refused here under the view's own reason and touches no other candidate
/// (REQ-0335). Which end of the route the fixed station is, and so which station is the pickup, is the
/// plan builder's decision, not this criterion's.
/// </para>
/// <para>
/// The two route validations that follow resolution stay here rather than becoming their own
/// criteria: they are properties of the route this criterion just built, and splitting them would
/// mean a later criterion re-deriving what this one already knows.
/// </para>
/// <para>
/// The route's dispatch zone is the one the area assignment table gives the demand's AREA (REQ-0191), not
/// the single <c>JourneyRuntime:dispatchZone</c> this server is configured with — that one still names the
/// zone a single-vehicle deployment serves, and nothing else. So two candidates of one round can fall in two
/// zones, and <see cref="DispatchZoneVehicleCriterion"/> judges each against its own zone's vehicles.
/// </para>
/// </remarks>
public sealed class StationResolutionCriterion(
    MapStationResolver stationResolver,
    IOptions<JourneyRuntimeOptions> options) : IDispatchAdmissionCriterion
{
    private readonly JourneyRuntimeOptions _options = options.Value;

    public int Order => 60;

    public Task<string> EvaluateAsync(
        DispatchCandidateEvaluation evaluation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        _ = cancellationToken;

        // AreaScopeCriterion has already refused an AREA the table does not name. Refusing it again here,
        // under the same silent reason, keeps this criterion fail-closed when it is run on its own.
        if (evaluation.AreaAssignment is not { } assignment)
        {
            return Task.FromResult(DispatchReasonCodes.OutOfScopeArea);
        }

        FixedTaskStationResolution fixedStation =
            evaluation.Round.FixedStations.Resolve(evaluation.Candidate.WorkType);
        evaluation.FixedStation = fixedStation;
        if (fixedStation.RefusalReasonCode is { } refusal)
        {
            return Task.FromResult(refusal);
        }

        try
        {
            RiotMapStation areaStation = stationResolver.ResolveUniquePickup(
                evaluation.Round.Map,
                evaluation.Candidate.LiveMesFields!.Area!);

            JourneyRouteDecision decision = JourneyPlanBuilder.ResolveRoute(
                evaluation.Round.Map,
                assignment.DispatchZone,
                areaStation,
                fixedStation,
                evaluation.Candidate.LiveMesFields.Area!,
                evaluation.Candidate.LiveMesFields.Eqp!);
            if (decision.Route is not { } route)
            {
                return Task.FromResult(decision.RefusalReasonCode!);
            }
            evaluation.Route = route;

            if (!_options.AllowedDispatchZones.Contains(route.DispatchZone, StringComparer.Ordinal))
            {
                return Task.FromResult("DISPATCH_ZONE_VEHICLE_ADMISSION_MISSING");
            }

            if (string.IsNullOrWhiteSpace(route.RouteEvidenceId))
            {
                return Task.FromResult("ROUTE_EVIDENCE_MISSING");
            }

            return Task.FromResult(DispatchAdmissionChain.Eligible);
        }
        catch (StationResolutionException error)
        {
            return Task.FromResult(error.ReasonCode);
        }
    }
}
