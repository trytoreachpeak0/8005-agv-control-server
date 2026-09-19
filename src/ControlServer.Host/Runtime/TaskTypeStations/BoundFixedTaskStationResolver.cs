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
        TaskTypeHolds holds = await access.ReadHoldsAsync(_mapId, cancellationToken).ConfigureAwait(false);
        return new BoundFixedTaskStationView(map, rules, bindingSet, holds);
    }
}

/// <summary>One round's rules and bindings, answered per task type.</summary>
public sealed class BoundFixedTaskStationView(
    RiotMapStationCatalogSnapshot map,
    TaskTypeStationRuleVersion? rules,
    TaskTypeStationBindingSetVersion? bindingSet,
    TaskTypeHolds holds) : IFixedTaskStationView
{
    public FixedTaskStationResolution Resolve(string taskType)
    {
        ArgumentNullException.ThrowIfNull(taskType);

        // No rule, no fixed end: the task type is outside what this server knows how to judge at all. The end is
        // meaningless on this refusal and only filled because a resolution always carries one.
        if (rules is not { } ruleVersion ||
            ruleVersion.Rules.SingleOrDefault(item => item.TaskType == taskType) is not { } rule)
        {
            return FixedTaskStationResolution.Refused(
                taskType, FixedStationEnd.Destination, DispatchReasonCodes.OutOfScopeWorkType, rules?.Version);
        }

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

        if (holds.Holds(taskType))
        {
            return FixedTaskStationResolution.Refused(
                taskType, end, DispatchReasonCodes.TaskTypeHeld, ruleVersion.Version, bindingSet.Version);
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

    /// <summary>
    /// Every hold in force on the Map, whatever raised it: an unreleased hold row (an operator or a catalog change,
    /// REQ-0340, REQ-0342), or an activation whose outcome is unknown, which holds the whole Map (REQ-0347).
    /// </summary>
    public async Task<TaskTypeHolds> ReadHoldsAsync(int mapId, CancellationToken cancellationToken)
    {
        TaskTypeStationActivePointer? pointer = await Bindings.ReadActivePointerAsync(mapId, cancellationToken)
            .ConfigureAwait(false);
        IReadOnlyList<TaskTypeStationHold> held = await Holds.ListUnreleasedAsync(mapId, cancellationToken)
            .ConfigureAwait(false);
        return new TaskTypeHolds(
            string.Equals(pointer?.State, TaskTypeStationActivationState.ActivationUnknown, StringComparison.Ordinal),
            held.Select(hold => hold.TaskType).ToHashSet(StringComparer.Ordinal));
    }
}

/// <summary>The holds in force on one Map.</summary>
/// <param name="WholeMap">An activation's outcome is unknown, so every task type of the Map is held.</param>
/// <param name="TaskTypes">The task types an unreleased hold names.</param>
public sealed record TaskTypeHolds(bool WholeMap, IReadOnlySet<string> TaskTypes)
{
    public bool Holds(string taskType) => WholeMap || TaskTypes.Contains(taskType);
}
