using ControlServer.Application;
using Microsoft.Extensions.Options;

namespace ControlServer.Host.Runtime.Dispatch.Criteria;

/// <summary>
/// Resolves the candidate's AREA to a unique pickup station and validates the resulting route.
/// </summary>
/// <remarks>
/// <para>
/// This is the criterion that produces <see cref="DispatchCandidateEvaluation.Route"/>; everything
/// after it depends on that being set. A resolution failure carries the resolver's own reason code
/// through unchanged rather than being flattened into one generic block — the operator needs to
/// know whether the station was missing, ambiguous or out of the Map.
/// </para>
/// <para>
/// The two route validations that follow resolution stay here rather than becoming their own
/// criteria: they are properties of the route this criterion just built, and splitting them would
/// mean a later criterion re-deriving what this one already knows.
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

        try
        {
            RiotMapStation resolvedPickup = stationResolver.ResolveUniquePickup(
                evaluation.Round.Map,
                evaluation.Candidate.LiveMesFields!.Area!);

            ResolvedJourneyRoute route = new(
                _options.DispatchZone,
                MapStationResolver.BuildRouteEvidenceId(
                    evaluation.Round.Map,
                    resolvedPickup,
                    evaluation.Round.Gate,
                    evaluation.Candidate.LiveMesFields.Area!,
                    evaluation.Candidate.LiveMesFields.Eqp!),
                resolvedPickup.StationName,
                resolvedPickup.StationId);
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
