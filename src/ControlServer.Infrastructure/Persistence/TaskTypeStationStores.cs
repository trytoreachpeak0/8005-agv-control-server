using ControlServer.Application;

namespace ControlServer.Infrastructure.Persistence;

public sealed class TaskTypeStationRuleStore(
    ControlServerDbContext context,
    GovernedConfigurationPublisher publisher) : ITaskTypeStationRuleStore
{
    private readonly ControlServerDbContext _context = context;
    private readonly GovernedConfigurationPublisher _publisher = publisher;

    public Task<TaskTypeStationRuleVersion?> ReadCurrentAsync(CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public Task<TaskTypeStationRuleVersion?> ReadVersionAsync(long version, CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public Task<TaskTypeStationVersionWrite<TaskTypeStationRuleVersion>> WriteVersionAsync(
        IReadOnlyList<TaskTypeStationRule> rules,
        string source,
        DateTimeOffset loadedAt,
        CancellationToken cancellationToken) =>
        throw new NotImplementedException();
}

public sealed class TaskTypeStationBindingStore(
    ControlServerDbContext context,
    GovernedConfigurationPublisher publisher) : ITaskTypeStationBindingStore
{
    private readonly ControlServerDbContext _context = context;
    private readonly GovernedConfigurationPublisher _publisher = publisher;

    public Task<TaskTypeStationBindingSetVersion?> ReadActiveAsync(int mapId, CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public Task<TaskTypeStationBindingSetVersion?> ReadLatestAsync(int mapId, CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public Task<TaskTypeStationBindingSetVersion?> ReadVersionAsync(
        int mapId,
        long version,
        CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public Task<TaskTypeStationVersionWrite<TaskTypeStationBindingSetVersion>> WriteVersionAsync(
        int mapId,
        long ruleVersion,
        IReadOnlyList<string> requiredTaskTypes,
        IReadOnlyList<TaskTypeStationBinding> bindings,
        long? catalogRevision,
        string source,
        DateTimeOffset loadedAt,
        CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public Task<TaskTypeStationActivePointer?> ReadActivePointerAsync(int mapId, CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public Task<TaskTypeStationActivePointer> SetActiveAsync(
        int mapId,
        long version,
        DateTimeOffset at,
        CancellationToken cancellationToken) =>
        throw new NotImplementedException();
}

public sealed class TaskTypeStationHoldStore(ControlServerDbContext context) : ITaskTypeStationHoldStore
{
    private readonly ControlServerDbContext _context = context;

    public Task<TaskTypeStationHold> RaiseAsync(
        int mapId,
        string taskType,
        string source,
        string reasonCode,
        string detailJson,
        string raisedBy,
        DateTimeOffset raisedAt,
        CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public Task<bool> ReleaseAsync(
        string holdId,
        string releasedBy,
        DateTimeOffset releasedAt,
        CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public Task<IReadOnlyList<TaskTypeStationHold>> ListUnreleasedAsync(int mapId, CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public Task<bool> IsHeldAsync(int mapId, string taskType, CancellationToken cancellationToken) =>
        throw new NotImplementedException();
}

public sealed class TaskTypeStationCatalogChangeStore(ControlServerDbContext context)
    : ITaskTypeStationCatalogChangeStore
{
    private readonly ControlServerDbContext _context = context;

    public Task<TaskTypeStationCatalogChange> RecordAsync(
        TaskTypeStationCatalogChange change,
        CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public Task<IReadOnlyList<TaskTypeStationCatalogChange>> ListAsync(int mapId, CancellationToken cancellationToken) =>
        throw new NotImplementedException();
}

public sealed class DemandTaskTypeStationFreezeStore(ControlServerDbContext context) : IDemandTaskTypeStationFreeze
{
    private readonly ControlServerDbContext _context = context;

    public Task<DemandTaskTypeStationFreeze> FreezeAsync(
        string demandId,
        long ruleVersion,
        int mapId,
        long bindingSetVersion,
        DateTimeOffset frozenAt,
        CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public Task<DemandTaskTypeStationFreeze?> ReadAsync(string demandId, CancellationToken cancellationToken) =>
        throw new NotImplementedException();
}
