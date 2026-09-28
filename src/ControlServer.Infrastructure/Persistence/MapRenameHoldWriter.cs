using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using ControlServer.Application;
using ControlServer.Domain;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Infrastructure.Persistence;

/// <summary>
/// How many demands of one task type on one Map are under way right now: a journey not completed, whose demand is of that
/// task type. What a hold's audit records as "in flight and frozen on this task type". Here rather than in the Host so that
/// the FieldOps side (the activation store) records it the same way (control-server#186); the Host's
/// <c>TaskTypeInFlightDemands</c> forwards to it.
/// </summary>
public static class TaskTypeInFlightDemandCount
{
    public static Task<int> CountAsync(
        ControlServerDbContext dbContext,
        int mapId,
        string taskType,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        // Each demand a journey carries, through the demand memberships (control-server#207): in a single-demand journey
        // that is the journey row's own demand, as before.
        return dbContext.JourneyRuntimes.AsNoTracking()
            .Where(journey => journey.MapId == mapId && journey.Stage != JourneyRuntimeStage.Completed)
            .Join(
                DemandJourneyLookup.Memberships(dbContext).AsNoTracking(),
                journey => journey.JourneyId,
                membership => membership.JourneyId,
                (journey, membership) => membership.DemandId)
            .Join(
                dbContext.AcceptedDemands.AsNoTracking().Where(demand => demand.WorkType == taskType),
                demandId => demandId,
                demand => demand.DemandId,
                (demandId, demand) => demandId)
            .CountAsync(cancellationToken);
    }
}

/// <summary>
/// The one place a Map rename hold is written (control-server#186), whoever writes it: the engine when it reads a new name,
/// the activation's second step and the startup preset when they make a version active under a rename nobody accepted.
/// One detail format, one audit action, so the dashboard, the audit export and the field read them the same way whichever
/// path raised them.
/// </summary>
/// <remarks>Runs inside the caller's write transaction; it opens none of its own.</remarks>
public static class MapRenameHoldWriter
{
    /// <summary>The engine read a name different from the baseline.</summary>
    public const string ByEngineObservation = "ENGINE_OBSERVATION";

    /// <summary>A FieldOps activation made a version active while a rename was pending.</summary>
    public const string ByActivation = "ACTIVATION_UNDER_PENDING_RENAME";

    /// <summary>The startup preset became a Map's first version while a rename was pending.</summary>
    public const string ByStartupPreset = "STARTUP_PRESET_UNDER_PENDING_RENAME";

    private static readonly JsonSerializerOptions DetailOptions = new()
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };

    /// <summary>
    /// Holds one task type of <paramref name="version"/> under <c>CATALOG_CHANGE</c> / <c>MAP_RENAMED</c>, and writes its
    /// audit when the hold is new. Returns whether it is new: the same hold standing already is the answer, not a second row.
    /// </summary>
    public static async Task<bool> RaiseAsync(
        ControlServerDbContext context,
        ITaskTypeStationHoldStore holds,
        IGovernanceAuditWriter audit,
        TaskTypeStationBindingSetVersion version,
        string taskType,
        string baselineName,
        string observedName,
        string raisedBy,
        string raisedThrough,
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(holds);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(version);
        int inFlight = await TaskTypeInFlightDemandCount.CountAsync(context, version.MapId, taskType, cancellationToken)
            .ConfigureAwait(false);
        string detailJson = JsonSerializer.Serialize(
            new
            {
                mapId = version.MapId,
                taskType,
                source = TaskTypeStationHoldSource.CatalogChange,
                reasonCode = MapNameHoldReasons.MapRenamed,
                classification = "MAP_RENAMED",
                bindingSetVersion = version.Version,
                before = new { mapName = baselineName },
                after = new { mapName = observedName },
                raisedThrough,
                inFlightDemands = inFlight
            },
            DetailOptions);
        TaskTypeStationHoldRaise raised = await holds.RaiseAsync(
            version.MapId,
            taskType,
            TaskTypeStationHoldSource.CatalogChange,
            MapNameHoldReasons.MapRenamed,
            detailJson,
            raisedBy,
            at,
            cancellationToken).ConfigureAwait(false);
        if (raised.Created)
        {
            await audit.WriteBusinessAsync(
                new GovernanceAuditEntry(
                    TaskTypeStationHoldAuditActions.Raised,
                    GovernedObjectKind.PublicStationBinding,
                    TaskTypeStationGovernance.BindingSetObjectId(version.MapId),
                    version.Version,
                    GovernanceActionOutcome.Succeeded,
                    detailJson,
                    version.SnapshotId),
                at,
                cancellationToken).ConfigureAwait(false);
        }
        return raised.Created;
    }

    /// <summary>
    /// When the Map of <paramref name="version"/> carries a rename nobody accepted, holds every task type the version binds
    /// that is not held for the rename already. Called in the transaction that makes the version active, so the version and
    /// its holds come into force together (PR #378 review: activation, then the startup preset).
    /// </summary>
    public static async Task<IReadOnlyList<string>> HoldUnderPendingRenameAsync(
        ControlServerDbContext context,
        ITaskTypeStationHoldStore holds,
        IGovernanceAuditWriter audit,
        TaskTypeStationBindingSetVersion version,
        string raisedBy,
        string raisedThrough,
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(version);
        MapNameBaselineRow? mapName = await context.Set<MapNameBaselineRow>().AsNoTracking()
            .SingleOrDefaultAsync(row => row.MapId == version.MapId, cancellationToken).ConfigureAwait(false);
        if (mapName?.PendingName is not { } pendingName)
        {
            return [];
        }
        HashSet<string> alreadyHeld = [.. await context.Set<TaskTypeStationHoldRow>().AsNoTracking()
            .Where(row => row.MapId == version.MapId
                && row.Source == TaskTypeStationHoldSource.CatalogChange
                && row.ReasonCode == MapNameHoldReasons.MapRenamed
                && row.ReleasedAt == null)
            .Select(row => row.TaskType)
            .ToListAsync(cancellationToken).ConfigureAwait(false)];
        List<string> held = [];
        foreach (string taskType in version.Bindings.Select(binding => binding.TaskType)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Where(taskType => !alreadyHeld.Contains(taskType)))
        {
            if (await RaiseAsync(context, holds, audit, version, taskType, mapName.Name, pendingName, raisedBy, raisedThrough, at,
                    cancellationToken).ConfigureAwait(false))
            {
                held.Add(taskType);
            }
        }
        return held;
    }
}
