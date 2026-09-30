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
    ChargingPolicyCommissioningLog commissioningLog,
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
    // Across rounds (review S3 on control-server#403): the criterion is scoped and the host builds it every round, so a set on
    // the instance logged every vehicle again every round -- an Error every two seconds while a bad version stays in effect.
    private readonly ChargingPolicyCommissioningLog _commissioningLog =
        commissioningLog ?? throw new ArgumentNullException(nameof(commissioningLog));

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
            _commissioningLog.Record(vehicleKey ?? string.Empty, null);
            return verdict;
        }
        if (verdict == DispatchReasonCodes.ChargingPolicyEntryNotAboveRescueLine)
        {
            if (_commissioningLog.Record(vehicleKey ?? string.Empty, verdict))
            {
                LogEntryNotAboveRescueLine(_logger, vehicleKey ?? string.Empty, decision.Detail ?? string.Empty, null);
            }
            return verdict;
        }
        if (_commissioningLog.Record(vehicleKey ?? string.Empty, decision.Reason))
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

/// <summary>
/// 每辆车最近一次被投运判据拒绝的原因，跨轮保留（control-server#403 审查 S3）。宿主里是单例：派车每一轮新建判据，状态放在判据上会让
/// 同一条日志每轮再记一次。原因变了才记；车重新合格即复位，下次再被拒时照样记一条。
/// </summary>
public sealed class ChargingPolicyCommissioningLog
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _last = new(StringComparer.Ordinal);

    /// <summary>记下这辆车这一轮的原因（合格为空）；被拒且原因与上一次不同时答真。</summary>
    public bool Record(string vehicleKey, string? reason)
    {
        ArgumentNullException.ThrowIfNull(vehicleKey);
        if (reason is null)
        {
            _last.TryRemove(vehicleKey, out _);
            return false;
        }
        bool changed = !_last.TryGetValue(vehicleKey, out string? before) || before != reason;
        _last[vehicleKey] = reason;
        return changed;
    }
}
