using ControlServer.Application;
using ControlServer.Host.Runtime.Dispatch;
using Microsoft.Extensions.Options;

namespace ControlServer.Host.Runtime.TaskTypeStations;

/// <summary>
/// The task types this build can execute end to end. Everything else that clears its binding is refused as
/// <see cref="DispatchReasonCodes.TaskTypeNotYetExecutable"/> (control-server#160).
/// </summary>
public static class ExecutableTaskTypes
{
    public static IReadOnlySet<string> All { get; } =
        new HashSet<string>(StringComparer.Ordinal) { TransportTaskTypes.WireToGate };

    public static bool Contains(string taskType) => All.Contains(taskType);
}

/// <summary>
/// The fixed station of every task type, taken from the rules and the Map's active binding set version
/// (scope specification 5.3, REQ-0334, REQ-0335).
/// </summary>
public sealed class BoundFixedTaskStationResolver(
    TaskTypeStationAccess access,
    IOptions<JourneyRuntimeOptions> options) : IFixedTaskStationResolver
{
    private readonly int _mapId = options.Value.MapId;

    public async Task<IFixedTaskStationView> ReadForRoundAsync(
        RiotMapStationCatalogSnapshot map,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(map);

        TaskTypeStationBindingSetVersion? bindingSet = await access.Bindings
            .ReadActiveAsync(_mapId, cancellationToken).ConfigureAwait(false);
        TaskTypeStationRuleVersion? rules = bindingSet is null
            ? await access.Rules.ReadCurrentAsync(cancellationToken).ConfigureAwait(false)
            : await access.Rules.ReadVersionAsync(bindingSet.RuleVersion, cancellationToken).ConfigureAwait(false);
        return new BoundFixedTaskStationView(map, rules, bindingSet);
    }
}

/// <summary>One round's rules and bindings, answered per task type.</summary>
public sealed class BoundFixedTaskStationView(
    RiotMapStationCatalogSnapshot map,
    TaskTypeStationRuleVersion? rules,
    TaskTypeStationBindingSetVersion? bindingSet) : IFixedTaskStationView
{
    public FixedTaskStationResolution Resolve(string taskType)
    {
        TaskTypeStationRuleVersion ruleVersion = rules!;
        TaskTypeStationRule rule = ruleVersion.Rules.Single(item => item.TaskType == taskType);
        FixedStationEnd end = rule.FixedEnd == TaskTypeFixedEnd.Origin ? FixedStationEnd.Origin : FixedStationEnd.Destination;
        TaskTypeStationBinding? binding = bindingSet is not null &&
            bindingSet.RequiredTaskTypes.Contains(taskType, StringComparer.Ordinal)
                ? bindingSet.Bindings.SingleOrDefault(item => item.TaskType == taskType)
                : null;
        if (binding is null)
        {
            return FixedTaskStationResolution.Refused(
                taskType, end, DispatchReasonCodes.TaskTypeBindingMissing, ruleVersion.Version, bindingSet?.Version);
        }

        // The catalog half of #159's validator, asked every round rather than at startup (specification 21.2 item 1):
        // the site Map is shared with production, so a station edited elsewhere stops only the task type bound to it.
        // The Map passed in was just read whole, which is what a fresh catalog is.
        IReadOnlyList<TaskTypeStationViolation> violations =
            TaskTypeStationConfigurationValidator.EvaluateCatalog(bindingSet!.MapId, [binding], map, catalogFresh: true);
        if (violations.Count > 0)
        {
            return FixedTaskStationResolution.Refused(
                taskType, end, violations[0].ReasonCode, ruleVersion.Version, bindingSet.Version);
        }

        RiotMapStation station = map.Stations.Single(item => item.StationId == binding.StationRiotId);
        return FixedTaskStationResolution.Resolved(taskType, end, station, ruleVersion.Version, bindingSet!.Version);
    }
}

/// <summary>What the runtime reads of the batch 6 task type station tables.</summary>
public sealed class TaskTypeStationAccess(
    ITaskTypeStationRuleStore rules,
    ITaskTypeStationBindingStore bindings,
    ITaskTypeStationHoldStore holds,
    IDemandTaskTypeStationFreeze freezes)
{
    public ITaskTypeStationRuleStore Rules { get; } = rules;

    public ITaskTypeStationBindingStore Bindings { get; } = bindings;

    public ITaskTypeStationHoldStore Holds { get; } = holds;

    public IDemandTaskTypeStationFreeze Freezes { get; } = freezes;
}
