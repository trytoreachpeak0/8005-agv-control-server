using ControlServer.Application;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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
/// <b>Order 16 -- right behind the fault block.</b> Like it, the verdict is about the vehicle and not the demand, so nothing
/// more expensive runs first. One log line per vehicle and reason per chain instance, which the host builds per round.
/// </para>
/// </remarks>
public sealed class ChargingPolicyCommissioningCriterion(
    IChargingPolicyResolver resolver,
    ILogger<ChargingPolicyCommissioningCriterion>? logger = null) : IDispatchAdmissionCriterion
{
    private static readonly Action<ILogger, string, string, string, Exception?> LogNotCommissioned =
        LoggerMessage.Define<string, string, string>(
            LogLevel.Warning,
            new EventId(1, nameof(LogNotCommissioned)),
            "Vehicle {VehicleKey} takes no new work: {Reason} ({Detail}). Import, approve and activate a charging policy "
            + "that covers it (REQ-0282).");

    private readonly IChargingPolicyResolver _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
    private readonly ILogger _logger = logger ?? NullLogger<ChargingPolicyCommissioningCriterion>.Instance;
    private readonly HashSet<(string VehicleKey, string Reason)> _logged = [];

    public int Order => 16;

    public async Task<string> EvaluateAsync(
        DispatchCandidateEvaluation evaluation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evaluation);

        string vehicleKey = evaluation.Vehicle.VehicleKey;
        if (string.IsNullOrWhiteSpace(vehicleKey))
        {
            return DispatchReasonCodes.ChargingPolicyNotApproved;
        }
        VehicleChargingPolicyDecision decision =
            await _resolver.ResolveForNewDecisionAsync(vehicleKey, cancellationToken).ConfigureAwait(false);
        if (decision.Commissioned)
        {
            return DispatchAdmissionChain.Eligible;
        }
        if (_logged.Add((vehicleKey, decision.Reason)))
        {
            LogNotCommissioned(
                _logger,
                vehicleKey,
                decision.Reason,
                decision.Detail ?? "no approved, activated policy version covers this vehicle",
                null);
        }
        return DispatchReasonCodes.ChargingPolicyNotApproved;
    }
}
