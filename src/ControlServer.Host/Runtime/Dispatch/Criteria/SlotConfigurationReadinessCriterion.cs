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
public sealed class SlotConfigurationReadinessCriterion(
    SlotConfigurationGateSwitch gateSwitch,
    SlotConfigurationReadinessGate? readiness,
    SlotConfigurationReadinessLog readinessLog,
    ILogger<SlotConfigurationReadinessCriterion>? logger = null) : IDispatchAdmissionCriterion
{
    private readonly SlotConfigurationGateSwitch _gateSwitch = gateSwitch ?? throw new ArgumentNullException(nameof(gateSwitch));
    private readonly SlotConfigurationReadinessGate? _readiness = readiness;
    private readonly SlotConfigurationReadinessLog _readinessLog = readinessLog ?? throw new ArgumentNullException(nameof(readinessLog));
    private readonly ILogger _logger = logger ?? NullLogger<SlotConfigurationReadinessCriterion>.Instance;

    public int Order => 19;

    public Task<string> EvaluateAsync(DispatchCandidateEvaluation evaluation, CancellationToken cancellationToken) =>
        Task.FromResult(DispatchAdmissionChain.Eligible);
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
