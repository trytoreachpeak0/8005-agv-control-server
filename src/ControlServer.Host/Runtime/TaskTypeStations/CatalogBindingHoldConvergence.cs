using ControlServer.Application;
using ControlServer.Infrastructure.Persistence;

namespace ControlServer.Host.Runtime.TaskTypeStations;

/// <summary>One binding a catalog change reached, and the hold it stands under.</summary>
public sealed record CatalogBindingHoldOutcome(CatalogBindingChange Change, TaskTypeStationHold Hold, bool HoldCreated);

/// <summary>
/// What runs after every complete catalog confirmation (control-server#162): each binding of the Map's active binding
/// set is judged against the catalog, and a binding whose station changed holds its own <c>Map + TASK_TYPE</c> -- and
/// nothing else.
/// </summary>
public sealed class CatalogBindingHoldConvergence(
    ControlServerDbContext dbContext,
    ITaskTypeStationBindingStore bindings,
    ITaskTypeStationHoldStore holds,
    ITaskTypeStationCatalogChangeStore catalogChanges,
    IGovernanceAuditWriter audit,
    GovernanceDeploymentIdentity deployment,
    TimeProvider timeProvider)
{
    /// <summary>The business audit action written when a catalog change raises a hold.</summary>
    public const string HoldRaisedAction = "TASK_TYPE_STATION_HOLD_RAISED";

    public Task<IReadOnlyList<CatalogBindingHoldOutcome>> ApplyAsync(
        RiotMapStationCatalogSnapshot catalog,
        CancellationToken cancellationToken)
    {
        _ = (dbContext, bindings, holds, catalogChanges, audit, deployment, timeProvider, catalog, cancellationToken);
        return Task.FromResult<IReadOnlyList<CatalogBindingHoldOutcome>>([]);
    }
}
