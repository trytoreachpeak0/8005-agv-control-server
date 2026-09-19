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
    public Task<IFixedTaskStationView> ReadForRoundAsync(
        RiotMapStationCatalogSnapshot map,
        CancellationToken cancellationToken)
    {
        _ = access;
        _ = options;
        throw new NotImplementedException();
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
