using ControlServer.Host.Runtime.CreateGate;

namespace ControlServer.Host.Runtime.Dispatch.Criteria;

/// <summary>
/// RIoT must agree this vehicle can reach the pickup station before the demand is taken.
/// </summary>
/// <remarks>
/// <para>
/// Order 96: after the route exists and after the route graph has had its say (95), so the gate can
/// put both sources side by side; before the slot-capacity read (100), because a station this
/// vehicle cannot reach is not worth a box-count call.
/// </para>
/// <para>
/// This is the ticket's <c>getRouteCostsBy</c> gate, and it is a block rather than a preference:
/// REQ-0305 lets a task exist only against endpoints that passed RouteCost at the moment it was
/// created. When it and the route graph disagree, the demand is blocked and the disagreement is
/// alarmed — see <see cref="PreCreateGate"/> for why neither source wins.
/// </para>
/// <para>
/// The vehicle named in the audit row and the log is the one this candidate is evaluated against, not the single-vehicle
/// default <c>JourneyRuntimeOptions.AgvId</c>, which every multi-vehicle audit row used to carry (control-server#751).
/// </para>
/// </remarks>
public sealed class PreCreateGateCriterion(PreCreateGate gate) : IDispatchAdmissionCriterion
{
    public int Order => 96;

    public async Task<string> EvaluateAsync(
        DispatchCandidateEvaluation evaluation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evaluation);

        CreateGateOutcome outcome = await gate.EvaluateAsync(
            new CreateGateRequest(
                evaluation.Candidate.DemandId,
                evaluation.Candidate.TransportDemandKey,
                evaluation.Vehicle.AgvId,
                evaluation.Vehicle.VehicleKey,
                evaluation.Round.Map.MapId,
                evaluation.Route!.PickupStationRiotId,
                evaluation.GraphTraversalCostMm),
            cancellationToken).ConfigureAwait(false);

        return outcome.IsAllowed
            ? DispatchAdmissionChain.Eligible
            : outcome.BlockReason!;
    }
}
