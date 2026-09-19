using ControlServer.Application;

namespace ControlServer.Infrastructure.Persistence;

/// <summary>Stub for control-server#161's test commit; the implementation lands in the next commit.</summary>
public sealed class TaskTypeStationActivationStore(
    ControlServerDbContext context,
    ITaskTypeStationBindingStore bindings,
    IGovernanceAuditWriter audit) : ITaskTypeStationActivationStore
{
    private readonly ControlServerDbContext _context = context;
    private readonly ITaskTypeStationBindingStore _bindings = bindings;
    private readonly IGovernanceAuditWriter _audit = audit;

    public Task<TaskTypeStationActivationAttempt> BeginAsync(
        TaskTypeStationActivationStart start,
        Func<TaskTypeStationActivationAttempt, GovernanceAuditEntry> startedAudit,
        CancellationToken cancellationToken) => throw new NotImplementedException();

    public Task CompleteAsync(TaskTypeStationActivationAttempt attempt, DateTimeOffset at, CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public Task<TaskTypeStationActiveReadBack> ReadBackAsync(int mapId, CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public Task<TaskTypeStationActivationAttempt> MarkUnknownAsync(
        TaskTypeStationActivationAttempt attempt,
        DateTimeOffset at,
        CancellationToken cancellationToken) => throw new NotImplementedException();

    public Task<TaskTypeStationActivationAttempt?> ReadOpenAttemptAsync(int mapId, CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public Task<IReadOnlyList<string>> ResolveAsync(
        TaskTypeStationActivationAttempt attempt,
        DateTimeOffset at,
        CancellationToken cancellationToken) => throw new NotImplementedException();

    public Task<IReadOnlyList<TaskTypeStationHold>> ReleaseManualAndCatalogHoldsAsync(
        int mapId,
        string taskType,
        string releasedBy,
        DateTimeOffset at,
        CancellationToken cancellationToken) => throw new NotImplementedException();

    public Task<IReadOnlyList<TaskTypeStationInFlightDemand>> ListInFlightDemandsAsync(
        int mapId,
        CancellationToken cancellationToken) => throw new NotImplementedException();
}
