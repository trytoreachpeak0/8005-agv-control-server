using ControlServer.Application;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ControlServer.Host.Runtime.Dispatch.Criteria;

/// <summary>
/// A vehicle without an approved, activated <c>ChargingPolicyVersion</c> that covers it takes no new work
/// (control-server#400; REQ-0282; specification 8.6: a hard block, judged vehicle by vehicle).
/// </summary>
/// <remarks>
/// <para>
/// <b>Per vehicle, never the whole server.</b> The server starts and the other vehicles keep working; refusing to start
/// would also stop the staged G3 runners, which run with the journey runtime off and dispatch nothing. The en-route chain
/// is derived from this one and gets the criterion without a line of its own.
/// </para>
/// <para>
/// <b>Fail-closed.</b> The decision comes from <see cref="IChargingPolicyResolver"/>, the same judgement the idle return
/// and the charging allocation read; a policy that cannot be read is a policy that is not there.
/// </para>
/// <para>
/// <b>Order 17 -- right behind the fault block and the idle return commitment (16).</b> Like it, the verdict is about the vehicle and not the demand, so nothing
/// more expensive runs first. One log line per vehicle and reason per chain instance, which the host builds per round.
/// </para>
/// </remarks>
public sealed class ChargingPolicyCommissioningCriterion(
    IChargingPolicyResolver resolver,
    IOptions<JourneyRuntimeOptions> options,
    ILogger<ChargingPolicyCommissioningCriterion>? logger = null) : IDispatchAdmissionCriterion
{
    // control-server#403: a version in effect whose entry threshold is not above the rescue line is unusable. Error, not
    // Warning: every vehicle stops taking work until someone activates a corrected version.
    private static readonly Action<ILogger, string, string, Exception?> LogEntryNotAboveRescueLine =
        LoggerMessage.Define<string, string>(
            LogLevel.Error,
            new EventId(2, nameof(LogEntryNotAboveRescueLine)),
            "Vehicle {VehicleKey} takes no new work: CHARGING_POLICY_ENTRY_NOT_ABOVE_RESCUE_LINE ({Detail}). Activate, with " +
            "ControlServer.FieldOps, a charging policy whose MandatoryChargeEntryThreshold is above the rescue line; no database " +
            "edit and no restart are needed. Journeys under way finish as planned.");

    private readonly int _rescueBatteryPercent =
        (options ?? throw new ArgumentNullException(nameof(options))).Value.WaitingJourneyRescueBatteryPercent;

    private static readonly Action<ILogger, string, string, string, Exception?> LogNotCommissioned =
        LoggerMessage.Define<string, string, string>(
            LogLevel.Warning,
            new EventId(1, nameof(LogNotCommissioned)),
            "Vehicle {VehicleKey} takes no new work: {Reason} ({Detail}). Import, approve and activate a charging policy "
            + "that covers it (REQ-0282).");

    private readonly IChargingPolicyResolver _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
    private readonly ILogger _logger = logger ?? NullLogger<ChargingPolicyCommissioningCriterion>.Instance;
    private readonly HashSet<(string VehicleKey, string Reason)> _logged = [];

    public int Order => 17;

    public async Task<string> EvaluateAsync(
        DispatchCandidateEvaluation evaluation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evaluation);

        string vehicleKey = evaluation.Vehicle.VehicleKey;
        // One definition with the idle return (control-server#400 after #389): VehicleNewPurposeReadiness.
        (string verdict, VehicleChargingPolicyDecision decision) = await VehicleNewPurposeReadiness
            .CommissioningVerdictAsync(_resolver, vehicleKey, _rescueBatteryPercent, cancellationToken).ConfigureAwait(false);
        if (verdict == DispatchAdmissionChain.Eligible)
        {
            return verdict;
        }
        if (verdict == DispatchReasonCodes.ChargingPolicyEntryNotAboveRescueLine)
        {
            if (_logged.Add((vehicleKey, verdict)))
            {
                LogEntryNotAboveRescueLine(_logger, vehicleKey ?? string.Empty, decision.Detail ?? string.Empty, null);
            }
            return verdict;
        }
        if (_logged.Add((vehicleKey, decision.Reason)))
        {
            LogNotCommissioned(
                _logger,
                vehicleKey ?? string.Empty,
                decision.Reason,
                decision.Detail ?? "no approved, activated policy version covers this vehicle",
                null);
        }
        return verdict;
    }
}
