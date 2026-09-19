using ControlServer.Domain;

namespace ControlServer.Application;

/// <summary>Stub for control-server#161's test commit; the implementation lands in the next commit.</summary>
public sealed class TaskTypeStationActivationService(
    ITaskTypeStationRuleStore rules,
    ITaskTypeStationBindingStore bindings,
    ITaskTypeStationActivationStore activations,
    ICatalogAvailabilityStore catalogStates,
    IGovernanceAuditWriter audit)
{
    private readonly ITaskTypeStationRuleStore _rules = rules;
    private readonly ITaskTypeStationBindingStore _bindings = bindings;
    private readonly ITaskTypeStationActivationStore _activations = activations;
    private readonly ICatalogAvailabilityStore _catalogStates = catalogStates;
    private readonly IGovernanceAuditWriter _audit = audit;

    public Task<TaskTypeStationActivationResult> ActivateAsync(
        TaskTypeStationCandidate candidate,
        RiotMapStationCatalogSnapshot? catalog,
        TaskTypeStationChangeRequest request,
        bool dryRun,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public Task<TaskTypeStationActivationResult> RollbackAsync(
        int mapId,
        long version,
        RiotMapStationCatalogSnapshot? catalog,
        TaskTypeStationChangeRequest request,
        bool dryRun,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public Task<TaskTypeStationReconciliationResult> ReconcileAsync(
        int mapId,
        TaskTypeStationChangeRequest request,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public Task<TaskTypeStationHoldReleaseResult> ReleaseHoldAsync(
        int mapId,
        string taskType,
        string? siteVerificationRef,
        RiotMapStationCatalogSnapshot? catalog,
        TaskTypeStationChangeRequest request,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public static string ConclusionName(TaskTypeStationReconciliationConclusion conclusion) =>
        throw new NotImplementedException();
}
