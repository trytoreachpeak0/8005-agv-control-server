using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

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

/// <summary>One Map's result of an observation. <see cref="HeldTaskTypes"/> are the holds this observation created.</summary>
public sealed record MapRenameObservation(int MapId, MapRenameObservationKind Kind, IReadOnlyList<string> HeldTaskTypes);

/// <summary>
/// Map level rename detection (control-server#186, REQ-0341): RIoT's Map names against the baseline each Map was first
/// seen under; a different name under the same <c>mapId</c> holds every task type of the Map's active binding set.
/// </summary>
/// <remarks>
/// <para>
/// <b>First sight is not a rename.</b> A Map's first name is recorded and nothing is held; a rename before that first
/// sight cannot be seen, which is the accepted blind spot.
/// </para>
/// <para>
/// <b>A read that tells nothing changes nothing.</b> A failed Map list read throws before anything is touched; a Map the
/// listing leaves out, names blank, or names twice is skipped. None of them holds, and none of them moves the baseline:
/// a Map whose id is gone is the catalog freshness gate's business, which the station catalog read drives.
/// </para>
/// <para>
/// <b>What is held</b> is every task type bound in the Map's active binding set (REQ-0341: "all of the Map's public
/// station bindings"), each under its own <c>CATALOG_CHANGE</c> / <c>MAP_RENAMED</c> hold. An unbound task type cannot
/// be dispatched anyway, and a hold on it could not be released by batch 6-05's release verb, which revalidates the
/// binding. While the rename is pending every observation converges again, so a task type bound in the meantime is held
/// the next time round.
/// </para>
/// <para>
/// <b>What ends it</b> is the field accepting the new name in FieldOps and then releasing the holds; a rename back to the
/// baseline clears the pending name but leaves the holds standing, because a hold is never released automatically
/// (REQ-0340).
/// </para>
/// <para>
/// Nothing here compares with <c>JourneyRuntime:mapIdentity</c>, and nothing uses it as a baseline or a first value.
/// </para>
/// </remarks>
public sealed class MapRenameHoldConvergence(
    ControlServerDbContext dbContext,
    IRiotMapNameCatalog mapNames,
    IMapNameBaselineStore baselines,
    ITaskTypeStationBindingStore bindings,
    ITaskTypeStationHoldStore holds,
    IGovernanceAuditWriter audit,
    GovernanceDeploymentIdentity deployment,
    TimeProvider timeProvider,
    ILogger<MapRenameHoldConvergence> logger)
{
    private readonly ILogger<MapRenameHoldConvergence> _logger = logger;

    private static readonly JsonSerializerOptions DetailOptions = new()
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };

    /// <summary>Reads RIoT's Map list and converges every Map in it. A failed read throws and touches nothing.</summary>
    public async Task<IReadOnlyList<MapRenameObservation>> ObserveAsync(CancellationToken cancellationToken)
    {
        RiotMapNameListing listing = await mapNames.ReadMapNamesAsync(cancellationToken).ConfigureAwait(false);
        DateTimeOffset now = timeProvider.GetUtcNow();
        List<MapRenameObservation> observed = [];
        foreach (IGrouping<int, RiotMapName> map in listing.Maps.GroupBy(map => map.MapId).OrderBy(map => map.Key))
        {
            RiotMapName[] entries = [.. map];
            if (entries.Length != 1 || string.IsNullOrWhiteSpace(entries[0].Name))
            {
                observed.Add(new(map.Key, MapRenameObservationKind.NotObserved, []));
                continue;
            }
            observed.Add(await ConvergeAsync(map.Key, entries[0].Name, now, cancellationToken).ConfigureAwait(false));
        }
        return observed;
    }

    private async Task<MapRenameObservation> ConvergeAsync(
        int mapId,
        string name,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // Decided once outside any transaction, so that the steady state -- same name, or a rename already pending with
        // every bound task type held -- takes no write lock at all; then decided again inside the write transaction,
        // whose reads nothing can overtake before it commits (BEGIN IMMEDIATE), and only that decision is written.
        Plan plan = await PlanAsync(mapId, name, cancellationToken).ConfigureAwait(false);
        if (!plan.Writes)
        {
            return new(mapId, plan.Kind, []);
        }

        await using IDbContextTransaction? transaction = dbContext.Database.CurrentTransaction is null
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false)
            : null;
        plan = await PlanAsync(mapId, name, cancellationToken).ConfigureAwait(false);
        List<string> held = [];
        switch (plan.Kind)
        {
            case MapRenameObservationKind.BaselineEstablished:
                await baselines.EstablishAsync(mapId, name, now, cancellationToken).ConfigureAwait(false);
                break;
            case MapRenameObservationKind.RenamedBack:
                await baselines.SetPendingAsync(mapId, null, null, cancellationToken).ConfigureAwait(false);
                break;
            case MapRenameObservationKind.Renamed:
                MapNameBaseline baseline = plan.Baseline!;
                if (!string.Equals(baseline.PendingName, name, StringComparison.Ordinal))
                {
                    await baselines.SetPendingAsync(mapId, name, now, cancellationToken).ConfigureAwait(false);
                    await audit.WriteBusinessAsync(
                        new GovernanceAuditEntry(
                            MapNameBaselineAuditActions.RenameDetected,
                            GovernedObjectKind.PublicStationBinding,
                            TaskTypeStationGovernance.BindingSetObjectId(mapId),
                            plan.Active?.Version,
                            GovernanceActionOutcome.Succeeded,
                            JsonSerializer.Serialize(
                                new
                                {
                                    mapId,
                                    baselineName = baseline.Name,
                                    previousPendingName = baseline.PendingName,
                                    observedName = name,
                                    boundTaskTypes = plan.BoundTaskTypes
                                },
                                DetailOptions),
                            plan.Active?.SnapshotId),
                        now,
                        cancellationToken).ConfigureAwait(false);
                }
                foreach (string taskType in plan.Unheld)
                {
                    if (await RaiseAsync(plan.Active!, baseline.Name, name, taskType, now, cancellationToken).ConfigureAwait(false))
                    {
                        held.Add(taskType);
                    }
                }
                break;
        }
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        return new(mapId, plan.Kind, held);
    }

    private async Task<bool> RaiseAsync(
        TaskTypeStationBindingSetVersion active,
        string baselineName,
        string observedName,
        string taskType,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        int inFlight = await TaskTypeInFlightDemands.CountAsync(dbContext, active.MapId, taskType, cancellationToken)
            .ConfigureAwait(false);
        string detailJson = JsonSerializer.Serialize(
            new
            {
                mapId = active.MapId,
                taskType,
                source = TaskTypeStationHoldSource.CatalogChange,
                reasonCode = MapNameHoldReasons.MapRenamed,
                classification = "MAP_RENAMED",
                bindingSetVersion = active.Version,
                before = new { mapName = baselineName },
                after = new { mapName = observedName },
                inFlightDemands = inFlight
            },
            DetailOptions);
        TaskTypeStationHoldRaise raised = await holds.RaiseAsync(
            active.MapId,
            taskType,
            TaskTypeStationHoldSource.CatalogChange,
            MapNameHoldReasons.MapRenamed,
            detailJson,
            deployment.Value,
            now,
            cancellationToken).ConfigureAwait(false);
        if (raised.Created)
        {
            await audit.WriteBusinessAsync(
                new GovernanceAuditEntry(
                    CatalogBindingHoldConvergence.HoldRaisedAction,
                    GovernedObjectKind.PublicStationBinding,
                    TaskTypeStationGovernance.BindingSetObjectId(active.MapId),
                    active.Version,
                    GovernanceActionOutcome.Succeeded,
                    detailJson,
                    active.SnapshotId),
                now,
                cancellationToken).ConfigureAwait(false);
        }
        return raised.Created;
    }

    private async Task<Plan> PlanAsync(int mapId, string name, CancellationToken cancellationToken)
    {
        MapNameBaseline? baseline = await baselines.ReadAsync(mapId, cancellationToken).ConfigureAwait(false);
        if (baseline is null)
        {
            return new(MapRenameObservationKind.BaselineEstablished, Writes: true, null, null, [], []);
        }
        if (string.Equals(baseline.Name, name, StringComparison.Ordinal))
        {
            return baseline.PendingName is null
                ? new(MapRenameObservationKind.Unchanged, Writes: false, baseline, null, [], [])
                : new(MapRenameObservationKind.RenamedBack, Writes: true, baseline, null, [], []);
        }

        TaskTypeStationBindingSetVersion? active = await bindings.ReadActiveAsync(mapId, cancellationToken)
            .ConfigureAwait(false);
        string[] bound = active is null
            ? []
            : [.. active.Bindings.Select(binding => binding.TaskType).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        HashSet<string> alreadyHeld = [.. (await holds.ListUnreleasedAsync(mapId, cancellationToken).ConfigureAwait(false))
            .Where(hold => hold.Source == TaskTypeStationHoldSource.CatalogChange
                && hold.ReasonCode == MapNameHoldReasons.MapRenamed)
            .Select(hold => hold.TaskType)];
        string[] unheld = [.. bound.Where(taskType => !alreadyHeld.Contains(taskType))];
        bool writes = !string.Equals(baseline.PendingName, name, StringComparison.Ordinal) || unheld.Length > 0;
        return new(MapRenameObservationKind.Renamed, writes, baseline, active, bound, unheld);
    }

    private sealed record Plan(
        MapRenameObservationKind Kind,
        bool Writes,
        MapNameBaseline? Baseline,
        TaskTypeStationBindingSetVersion? Active,
        IReadOnlyList<string> BoundTaskTypes,
        IReadOnlyList<string> Unheld);
}
