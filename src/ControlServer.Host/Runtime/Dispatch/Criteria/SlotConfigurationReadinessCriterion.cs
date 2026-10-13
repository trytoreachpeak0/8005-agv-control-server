using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ControlServer.Host.Runtime.Dispatch.Criteria;

/// <summary>
/// A vehicle whose slot configuration is not confirmed in this database takes no transport (control-server#686;
/// REQ-0259, REQ-0263): its IO bindings incomplete, its slots never verified one by one, a verification incomplete or
/// negative, a maintenance or hardware change not yet re-verified, or its vehicle model unresolved.
/// </summary>
/// <remarks>
/// <para>
/// <b>Only while <c>Governance:slotConfigurationReadinessGate</c> is <c>Enforcing</c>.</b> Off, the criterion answers eligible
/// without reading anything, which is today's admission set. Which mode a deployment runs is not a hand-written switch: the
/// v2 installer derives it from the installed definition's <c>productionCutover.phase</c> (production: Enforcing; the trial,
/// on simulated slot IO, and every other definition: Off). The product default stays Off for the rigs, G3 and the spare-car
/// test instances.
/// </para>
/// <para>
/// <b>Computed, never read from the readiness row.</b> <see cref="SlotConfigurationReadinessGate.EvaluateVehicleAsync"/>
/// judges the facts as they are now; the row is only the latest conclusion, and on a new database it does not exist, which
/// has to mean "not confirmed". A model that cannot be resolved is not confirmed either.
/// </para>
/// <para>
/// <b>Order 19 -- after the charging criteria (17, 18), before anything about the demand.</b> Like them the verdict is about
/// the vehicle, so nothing more expensive runs first. Transport only: the en-route chain is derived from this one and gets it
/// without a line of its own, while the idle return and the charging allocation do not read the dispatch chain -- they carry
/// no cargo. One Warning per vehicle and readiness reason, across rounds (<see cref="SlotConfigurationReadinessLog"/>).
/// </para>
/// </remarks>
public sealed class SlotConfigurationReadinessCriterion(
    SlotConfigurationGateSwitch gateSwitch,
    SlotConfigurationReadinessGate? readiness,
    SlotConfigurationReadinessLog readinessLog,
    ILogger<SlotConfigurationReadinessCriterion>? logger = null) : IDispatchAdmissionCriterion
{
    private static readonly Action<ILogger, string, string, string, Exception?> LogNotConfirmed =
        LoggerMessage.Define<string, string, string>(
            LogLevel.Warning,
            new EventId(1, nameof(LogNotConfirmed)),
            "Vehicle {AgvId} takes no transport: its slot configuration is not confirmed in this database ({Reason}; {Detail}). "
            + "Verify every slot on site and record it with ControlServer.FieldOps verify (REQ-0263); the next round takes it.");

    private readonly SlotConfigurationGateSwitch _gateSwitch = gateSwitch ?? throw new ArgumentNullException(nameof(gateSwitch));

    // Enforcing without a source of verdicts would be a gate that is on and always lets vehicles through.
    private readonly SlotConfigurationReadinessGate? _readiness =
        readiness ?? ((gateSwitch?.IsEnforcing ?? false) ? throw new ArgumentNullException(nameof(readiness)) : null);

    private readonly SlotConfigurationReadinessLog _readinessLog = readinessLog ?? throw new ArgumentNullException(nameof(readinessLog));
    private readonly ILogger _logger = logger ?? NullLogger<SlotConfigurationReadinessCriterion>.Instance;

    public int Order => 19;

    public async Task<string> EvaluateAsync(DispatchCandidateEvaluation evaluation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        if (!_gateSwitch.IsEnforcing)
        {
            return DispatchAdmissionChain.Eligible;
        }

        string agvId = evaluation.Vehicle.AgvId ?? string.Empty;
        SlotConfigurationReadinessVerdict? verdict = string.IsNullOrWhiteSpace(agvId)
            ? null
            : await _readiness!.EvaluateVehicleAsync(agvId, cancellationToken).ConfigureAwait(false);
        if (verdict is { Ready: true })
        {
            _readinessLog.Record(agvId, null);
            return DispatchAdmissionChain.Eligible;
        }

        string reason = verdict?.ReasonCode ?? DispatchReasonCodes.VehicleSlotModelUnresolved;
        if (_readinessLog.Record(agvId, reason))
        {
            LogNotConfirmed(_logger, agvId, reason, Detail(verdict), null);
        }
        return DispatchReasonCodes.SlotConfigurationNotConfirmed;
    }

    private static string Detail(SlotConfigurationReadinessVerdict? verdict) =>
        verdict is null
            ? "no active slot configuration and no published IO binding names a vehicle model with slots"
            : string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"model {verdict.SlotModelVersionId}; slots missing a binding [{string.Join(',', verdict.SlotsMissingBinding)}], "
                + $"missing a verification [{string.Join(',', verdict.SlotsMissingVerification)}], "
                + $"verified negative [{string.Join(',', verdict.SlotsVerifiedNegative)}]");
}

/// <summary>
/// 每辆车最近一次被仓位就绪判据拒绝的原因，跨轮保留（control-server#686，形状照 <see cref="ChargingPolicyCommissioningLog"/>）。
/// 宿主里是单例：派车每一轮新建判据，状态放在判据上会让同一条日志每轮再记一次。原因变了才记；车重新合格即复位。
/// </summary>
public sealed class SlotConfigurationReadinessLog
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _last = new(StringComparer.Ordinal);

    /// <summary>记下这辆车这一轮的原因（合格为空）；被拒且原因与上一次不同时答真。</summary>
    public bool Record(string agvId, string? reason)
    {
        ArgumentNullException.ThrowIfNull(agvId);
        if (reason is null)
        {
            _last.TryRemove(agvId, out _);
            return false;
        }
        bool changed = !_last.TryGetValue(agvId, out string? before) || before != reason;
        _last[agvId] = reason;
        return changed;
    }
}
