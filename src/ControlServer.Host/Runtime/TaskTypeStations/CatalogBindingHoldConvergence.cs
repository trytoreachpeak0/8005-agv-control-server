using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime.CreateGate;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ControlServer.Host.Runtime.TaskTypeStations;

/// <summary>One binding a catalog change reached, and the hold it stands under.</summary>
public sealed record CatalogBindingHoldOutcome(CatalogBindingChange Change, TaskTypeStationHold Hold, bool HoldCreated);

/// <summary>
/// What runs after every complete catalog confirmation (control-server#162): each binding of the Map's active binding
/// set is judged against the catalog, and a binding whose station changed holds its own <c>Map + TASK_TYPE</c> -- and
/// nothing else.
/// </summary>
/// <remarks>
/// <para>
/// <b>The effect is confined to what is necessary</b> (REQ-0345): one station's change holds the one task type bound to
/// it. Other task types on the Map and other Maps carry on; a station no binding names -- the area-named machine
/// stations included -- raises nothing here, and keeps going through the admission policy drift handling it always had.
/// </para>
/// <para>
/// <b>Nothing is rewritten</b>: the new catalog becomes the current fact, but binding content, requirement set, rules and
/// the area assignment table stay exactly as they were; no station is guessed for a task type, and a vanished station is
/// never mapped onto another one. Orders already created in RIoT, and journeys under way, are not touched -- whether a
/// leg that is not yet created may be created is the pre-create check's business (batch 6-04), which reads the hold.
/// </para>
/// <para>
/// A hold, its catalog change record and its audit land in one transaction, so a crash between them cannot leave a
/// hold that never got its audit: the next round would find the hold standing and write none.
/// </para>
/// </remarks>
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

    /// <summary>Risk class of a rename: the identity holds, the use on site needs a review.</summary>
    public const string SiteReviewRequired = "SITE_REVIEW_REQUIRED";

    /// <summary>Risk class of a station gone from the catalog: the binding is void until a new binding set version.</summary>
    public const string BindingInvalidated = "BINDING_INVALIDATED";

    private static readonly JsonSerializerOptions DetailOptions = new()
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };

    public async Task<IReadOnlyList<CatalogBindingHoldOutcome>> ApplyAsync(
        RiotMapStationCatalogSnapshot catalog,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        TaskTypeStationBindingSetVersion? active = await bindings.ReadActiveAsync(catalog.MapId, cancellationToken)
            .ConfigureAwait(false);
        if (active is null)
        {
            return [];
        }

        CatalogBindingChange[] changed =
        [
            .. CatalogBindingChangeClassifier.Classify(active.Bindings, catalog)
                .Where(change => change.HoldReasonCode is not null)
        ];
        if (changed.Length == 0)
        {
            return [];
        }

        long catalogRevision = CatalogAvailabilityAccess.RevisionOf(catalog.ContentSha256);
        DateTimeOffset now = timeProvider.GetUtcNow();
        List<CatalogBindingHoldOutcome> outcomes = [];
        foreach (CatalogBindingChange change in changed)
        {
            outcomes.Add(await ConvergeAsync(active, catalog.MapId, catalogRevision, change, now, cancellationToken)
                .ConfigureAwait(false));
        }
        return outcomes;
    }

    private async Task<CatalogBindingHoldOutcome> ConvergeAsync(
        TaskTypeStationBindingSetVersion active,
        int mapId,
        long catalogRevision,
        CatalogBindingChange change,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        int inFlight = await TaskTypeInFlightDemands.CountAsync(dbContext, mapId, change.TaskType, cancellationToken)
            .ConfigureAwait(false);
        string classification = ChangeKindName(change.Kind);
        string detailJson = JsonSerializer.Serialize(
            new
            {
                mapId,
                taskType = change.TaskType,
                source = TaskTypeStationHoldSource.CatalogChange,
                reasonCode = change.HoldReasonCode,
                classification,
                catalogRevision,
                bindingSetVersion = active.Version,
                before = new { stationRiotId = change.StationRiotId, stationName = change.BoundStationName },
                after = change.CurrentStationName is null
                    ? null
                    : new { stationRiotId = change.StationRiotId, stationName = change.CurrentStationName },
                sameNameStationIds = change.SameNameStationIds,
                inFlightDemands = inFlight
            },
            DetailOptions);

        await using IDbContextTransaction? transaction = dbContext.Database.CurrentTransaction is null
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false)
            : null;
        TaskTypeStationHoldRaise raised = await holds.RaiseAsync(
            mapId,
            change.TaskType,
            TaskTypeStationHoldSource.CatalogChange,
            change.HoldReasonCode!,
            detailJson,
            deployment.Value,
            now,
            cancellationToken).ConfigureAwait(false);
        await catalogChanges.RecordAsync(
            new TaskTypeStationCatalogChange(
                Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
                mapId,
                change.StationRiotId,
                change.BoundStationName,
                change.CurrentStationName,
                classification,
                change.Kind == CatalogBindingChangeKind.Renamed ? SiteReviewRequired : BindingInvalidated,
                catalogRevision,
                now,
                [change.TaskType],
                raised.Hold.HoldId),
            cancellationToken).ConfigureAwait(false);
        if (raised.Created)
        {
            await audit.WriteBusinessAsync(
                new GovernanceAuditEntry(
                    HoldRaisedAction,
                    GovernedObjectKind.PublicStationBinding,
                    TaskTypeStationGovernance.BindingSetObjectId(mapId),
                    active.Version,
                    GovernanceActionOutcome.Succeeded,
                    detailJson,
                    active.SnapshotId),
                now,
                cancellationToken).ConfigureAwait(false);
        }
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        return new CatalogBindingHoldOutcome(change, raised.Hold, raised.Created);
    }

    private static string ChangeKindName(CatalogBindingChangeKind kind) => kind switch
    {
        CatalogBindingChangeKind.Renamed => "RENAMED",
        CatalogBindingChangeKind.Removed => "REMOVED",
        CatalogBindingChangeKind.IdReplaced => "ID_REPLACED",
        _ => "UNCHANGED"
    };
}

/// <summary>
/// How many demands of one task type on one Map are under way right now: a journey not completed, whose demand is of
/// that task type. What a hold's audit records as "in flight and frozen on this task type".
/// </summary>
public static class TaskTypeInFlightDemands
{
    public static Task<int> CountAsync(
        ControlServerDbContext dbContext,
        int mapId,
        string taskType,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        return dbContext.JourneyRuntimes.AsNoTracking()
            .Where(journey => journey.MapId == mapId && journey.Stage != JourneyRuntimeStage.Completed)
            .Join(
                dbContext.AcceptedDemands.AsNoTracking().Where(demand => demand.WorkType == taskType),
                journey => journey.DemandId,
                demand => demand.DemandId,
                (journey, demand) => journey.DemandId)
            .CountAsync(cancellationToken);
    }
}
