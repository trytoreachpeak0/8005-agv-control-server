using Microsoft.Extensions.Options;

namespace ControlServer.Host.Runtime.Dispatch.Criteria;

/// <summary>
/// The vehicle must currently be safe, available, idle, on the right Map, freshly observed,
/// adequately charged, stopped and unoccupied.
/// </summary>
/// <remarks>
/// <para>
/// Every clause here is a positive requirement, and each one blocks with its own reason code: an
/// operator reading the backlog needs to know which fact stopped the round, and "vehicle not
/// ready" would not tell them.
/// </para>
/// <para>
/// The binding check compares RIoT's answer against the vehicle this round segment is deciding
/// for, which comes from <see cref="DispatchVehicleFacts.VehicleKey"/> rather than from a
/// single-vehicle configuration entry. With one configured vehicle those are the same string,
/// which is why N=1 behaviour is unchanged; with several, this is what keeps one vehicle's
/// observation from admitting another.
/// </para>
/// </remarks>
public sealed class VehicleDynamicFactsCriterion(IOptions<JourneyRuntimeOptions> options)
    : IDispatchAdmissionCriterion
{
    private readonly JourneyRuntimeOptions _options = options.Value;

    public int Order => 80;

    public Task<string> EvaluateAsync(
        DispatchCandidateEvaluation evaluation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        _ = cancellationToken;

        return Task.FromResult(Evaluate(evaluation.Vehicle, _options));
    }

    /// <summary>
    /// The same verdict, callable outside the chain. The final pre-intake re-check needs exactly
    /// this decision against freshly read facts, and it must not be able to drift from the chain's.
    /// </summary>
    public static string Evaluate(DispatchVehicleFacts facts, JourneyRuntimeOptions options)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(options);

        if (facts.Onboard is null)
        {
            return "ONBOARD_FACTS_NOT_READY";
        }

        if (!facts.Onboard.DepartureSafe || !facts.Onboard.VehicleStopped ||
            !facts.Onboard.AllTargetSlotsLocked || !facts.Onboard.AllUnlockOutputsReset ||
            facts.Onboard.UnknownPresent)
        {
            return "ONBOARD_DEPARTURE_UNSAFE";
        }

        if (!facts.Vehicle.Connected || !facts.Vehicle.Enabled)
        {
            return "RIOT_VEHICLE_NOT_AVAILABLE";
        }

        if (!string.Equals(facts.Vehicle.VehicleKey, facts.VehicleKey, StringComparison.Ordinal))
        {
            return "RIOT_VEHICLE_BINDING_MISMATCH";
        }

        if (!string.Equals(facts.Vehicle.ProcState, "IDLE", StringComparison.Ordinal))
        {
            return "RIOT_VEHICLE_NOT_IDLE";
        }

        if (!string.Equals(facts.Vehicle.CurrentMap, options.MapIdentity, StringComparison.Ordinal))
        {
            return "RIOT_VEHICLE_MAP_MISMATCH";
        }

        // A future timestamp is as unusable as an old one: it means the two clocks disagree, and
        // an age computed from disagreeing clocks proves nothing about freshness.
        if (facts.Vehicle.ObservedAt > facts.ObservedAt ||
            facts.ObservedAt - facts.Vehicle.ObservedAt > options.MaximumEvidenceAge)
        {
            return "RIOT_VEHICLE_FACT_STALE";
        }

        if (facts.Vehicle.BatteryPercent is null || string.IsNullOrWhiteSpace(facts.Vehicle.BatteryState))
        {
            return "BATTERY_FACT_UNKNOWN";
        }

        if (string.Equals(facts.Vehicle.BatteryState, "CHARGING", StringComparison.Ordinal) ||
            facts.Vehicle.BatteryPercent < options.MinimumBatteryPercent)
        {
            return "BATTERY_POLICY_NOT_SATISFIED";
        }

        if (facts.Vehicle.Speed is null || facts.Vehicle.Speed != 0)
        {
            return "RIOT_VEHICLE_NOT_STOPPED";
        }

        if (facts.Vehicle.LockStatus is null || facts.Vehicle.LockStatus != 0 ||
            !string.IsNullOrWhiteSpace(facts.Vehicle.OrderTaskId))
        {
            return "RIOT_VEHICLE_ORDER_OCCUPIED";
        }

        return DispatchAdmissionChain.Eligible;
    }
}
