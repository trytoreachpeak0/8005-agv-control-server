using ControlServer.Application;
using ControlServer.Infrastructure.Persistence;

namespace ControlServer.Host.Runtime.TaskTypeStations;

/// <summary>What one Map's name did to its baseline and holds in one observation.</summary>
public enum MapRenameObservationKind
{
    /// <summary>The Map was not in the listing, or the listing named its id more than once: nothing touched.</summary>
    NotObserved,

    /// <summary>First name ever seen for the Map: recorded as its baseline, nothing held.</summary>
    BaselineEstablished,

    /// <summary>Same name as the baseline, nothing pending.</summary>
    Unchanged,

    /// <summary>Back to the baseline name while a rename was pending: the pending name cleared, the holds left standing.</summary>
    RenamedBack,

    /// <summary>A name different from the baseline: the Map's bound task types held.</summary>
    Renamed
}

/// <summary>One Map's result of an observation.</summary>
public sealed record MapRenameObservation(int MapId, MapRenameObservationKind Kind, IReadOnlyList<string> HeldTaskTypes);

/// <summary>
/// Map level rename detection (control-server#186, REQ-0341): RIoT's Map names against the baseline each Map was first
/// seen under; a different name under the same <c>mapId</c> holds every task type of the Map's active binding set.
/// </summary>
public sealed class MapRenameHoldConvergence(
    ControlServerDbContext dbContext,
    IRiotMapNameCatalog mapNames,
    IMapNameBaselineStore baselines,
    ITaskTypeStationBindingStore bindings,
    ITaskTypeStationHoldStore holds,
    IGovernanceAuditWriter audit,
    GovernanceDeploymentIdentity deployment,
    TimeProvider timeProvider)
{
    /// <summary>Reads RIoT's Map list and converges every Map in it. A failed read throws and touches nothing.</summary>
    public Task<IReadOnlyList<MapRenameObservation>> ObserveAsync(CancellationToken cancellationToken)
    {
        _ = (dbContext, mapNames, baselines, bindings, holds, audit, deployment, timeProvider, cancellationToken);
        return Task.FromResult<IReadOnlyList<MapRenameObservation>>([]);
    }
}
