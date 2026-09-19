using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using ControlServer.Host.Runtime.Dispatch;
using ControlServer.Host.Runtime.Dispatch.Criteria;
using ControlServer.Host.Runtime.Commands;
using ControlServer.Host.Runtime.CreateGate;
using ControlServer.Host.Runtime.Faults;
using ControlServer.Host.Runtime.Fleet;
using ControlServer.Host.Runtime.TaskTypeStations;

namespace ControlServer.Host.Runtime;

public sealed class JourneyRuntimeEngine(
    ControlServerDbContext dbContext,
    IMesIngestCatalog catalog,
    IRiotVehicleFacts vehicleFacts,
    IRiotMapStationCatalog mapStationCatalog,
    MapStationResolver stationResolver,
    IFixedTaskStationResolver fixedStationResolver,
    TaskTypeStationAccess taskTypeStations,
    CatalogBindingHoldConvergence catalogBindingHolds,
    JourneyIntakeCoordinator intakeCoordinator,
    MovementDispatchService movementDispatch,
    WireToGateStore store,
    OnboardJourneyPublisher publisher,
    ISublotBoxCountReader boxCountReader,
    IPackageCapacityStore packageCapacityStore,
    DispatchAdmissionChain admissionChain,
    IDispatchCandidateRanker candidateRanker,
    CatalogAvailabilityAccess catalogAvailability,
    ICatalogAvailabilityStore catalogStore,
    PreCreateGate createGate,
    VehicleRoster roster,
    VehicleDispatchPolicyAccess dispatchPolicy,
    IVehicleMotionFacts motionFacts,
    CheckpointWaitLedger checkpointWaits,
    VehicleFaultCoordinator faults,
    IAreaAssignmentStore areaAssignments,
    IVehicleSlotPositionReader slotPositions,
    IDispatchRoundOutcomeSink roundOutcomes,
    IOptions<JourneyRuntimeOptions> options,
    TimeProvider timeProvider,
    ILogger<JourneyRuntimeEngine> logger)
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private static readonly Action<ILogger, Exception?> LogCatalogPollFailed = LoggerMessage.Define(
        LogLevel.Warning,
        new EventId(2101, nameof(LogCatalogPollFailed)),
        "MesIngest catalog polling failed closed; no journey was accepted.");
    private static readonly Action<ILogger, string, Exception?> LogBoxCountFailed = LoggerMessage.Define<string>(
        LogLevel.Warning,
        new EventId(2102, nameof(LogBoxCountFailed)),
        "SUBLOT_BOX_COUNT failed closed for demand {DemandId}.");
    private static readonly Action<ILogger, string, string, Exception?> LogSublotRejected =
        LoggerMessage.Define<string, string>(
            LogLevel.Information,
            new EventId(2108, nameof(LogSublotRejected)),
            "The sublot entered for demand {DemandId} was refused: {ReasonCode}.");
    private static readonly Action<ILogger, Exception?> LogMapStationCatalogFailed = LoggerMessage.Define(
        LogLevel.Warning,
        new EventId(2103, nameof(LogMapStationCatalogFailed)),
        "RIoT Map station catalog failed closed; no new journey action was taken.");
    private static readonly Action<ILogger, string, int, Exception?> LogVehicleRoundBudgetExhausted =
        LoggerMessage.Define<string, int>(
            LogLevel.Warning,
            new EventId(2104, nameof(LogVehicleRoundBudgetExhausted)),
            "Vehicle {AgvId} exhausted its {BudgetMilliseconds} ms dispatch budget; the round moved on " +
            "to the remaining vehicles.");
    private static readonly Action<ILogger, string, string, Exception?> LogCheckpointWaitExceeded =
        LoggerMessage.Define<string, string>(
            LogLevel.Warning,
            new EventId(2105, nameof(LogCheckpointWaitExceeded)),
            "Vehicle {AgvId} has been holding at a traffic checkpoint past its budget on journey " +
            "{DemandId}; it is not arriving on its own.");
    private static readonly Action<ILogger, string, string, Exception?> LogVehicleOccupancyConflict =
        LoggerMessage.Define<string, string>(
            LogLevel.Error,
            new EventId(2106, nameof(LogVehicleOccupancyConflict)),
            "Vehicle {AgvId} already holds an in-flight order; the claim for {UpperId} was refused by " +
            "the occupancy index.");
    private static readonly Action<ILogger, string, string, string, Exception?> LogOrderFailedSymptom =
        LoggerMessage.Define<string, string, string>(
            LogLevel.Warning,
            new EventId(2107, nameof(LogOrderFailedSymptom)),
            "RIoT reports order {UpperId} FAILED on vehicle {AgvId}; journey {DemandId} recorded the " +
            "symptom with the fault model and stopped advancing on its own.");
    private static readonly Action<ILogger, string, string, DateTimeOffset, Exception?> LogStationDeadlineEndedStop =
        LoggerMessage.Define<string, string, DateTimeOffset>(
            LogLevel.Warning,
            new EventId(2108, nameof(LogStationDeadlineEndedStop)),
            "Nobody entered a sublot at vehicle {AgvId}'s pickup before the station departure deadline " +
            "{Deadline}; demand {DemandId} ended as CANCELLED_BY_STATION_TIMEOUT and the vehicle was released.");
    private static readonly Action<ILogger, string, string, string, DateTimeOffset, Exception?> LogStationTimeoutDoorNotClosed =
        LoggerMessage.Define<string, string, string, DateTimeOffset>(
            LogLevel.Warning,
            new EventId(2109, nameof(LogStationTimeoutDoorNotClosed)),
            "Vehicle {AgvId} is still loading demand {DemandId} at station {StationId} past its station " +
            "departure deadline {Deadline} with a slot door not closed; the stop keeps waiting under " +
            "STATION_TIMEOUT_DOOR_NOT_CLOSED.");
    private static readonly Action<ILogger, string, string, string, Exception?> LogDeterminateLoadFailureSettled =
        LoggerMessage.Define<string, string, string>(
            LogLevel.Warning,
            new EventId(2110, nameof(LogDeterminateLoadFailureSettled)),
            "Vehicle {AgvId} reported a determinate load failure after the station departure deadline; " +
            "demand {DemandId} ended as {TerminalReason} and the vehicle was released.");
    private static readonly Action<ILogger, long, int, string, string, Exception?> LogAdmissionPolicyDrift =
        LoggerMessage.Define<long, int, string, string>(
            LogLevel.Warning,
            new EventId(2116, nameof(LogAdmissionPolicyDrift)),
            "Admission policy version {AdmissionPolicyVersion} is bound to other area-named stations than " +
            "map {MapId} now carries (added: {AddedStations}; removed: {RemovedStations}). Journeys already " +
            "under way continue on the bound policy; no further demand is taken on until " +
            "admissionPolicyVersion is raised.");

    private static readonly Action<ILogger, string, string, string, TimeSpan, Exception?> LogAreaEndAdmissionRevokedTimeout =
        LoggerMessage.Define<string, string, string, TimeSpan>(
            LogLevel.Warning,
            new EventId(2117, nameof(LogAreaEndAdmissionRevokedTimeout)),
            "Vehicle {AgvId} has waited at AREA machine station {StationId} with demand {DemandId} on board for longer " +
            "than {Timeout} for the station to admit its task type again; the journey is blocked for manual recovery.");

    private static readonly Action<ILogger, int, Exception?> LogCatalogBindingHoldConvergenceFailed =
        LoggerMessage.Define<int>(
            LogLevel.Warning,
            new EventId(2118, nameof(LogCatalogBindingHoldConvergenceFailed)),
            "The catalog change convergence for map {MapId} failed; this round goes on without it and the next " +
            "complete catalog confirmation converges again.");

    /// <summary>
    /// The terminal reason of a demand whose pickup stop ran out its station departure deadline
    /// (ADR-cross-0055).
    /// </summary>
    public const string StationTimeoutCancellationReason = "CANCELLED_BY_STATION_TIMEOUT";

    /// <summary>
    /// The load stop has run out its station departure deadline with a slot door still not closed
    /// (ADR-cross-0058 decision 4). The stop does not end; the duty moves to someone shutting the door.
    /// </summary>
    public const string StationTimeoutDoorNotClosedReason = "STATION_TIMEOUT_DOOR_NOT_CLOSED";

    /// <summary>
    /// A loaded journey at its AREA machine station waited longer than
    /// <see cref="JourneyRuntimeOptions.AreaEndAdmissionRevokedTimeout"/> for the station to admit its task type again
    /// (control-server#198). The journey is <see cref="JourneyRuntimeStage.Blocked"/>: the goods on the vehicle are an
    /// administrator's to dispose of through a recovery session.
    /// </summary>
    public const string AreaEndAdmissionRevokedTimeoutReason = "TASK_TYPE_NOT_ALLOWED_AT_STATION_TIMEOUT";

    /// <summary>The journey is not arriving because the vehicle is holding at a traffic checkpoint.</summary>
    public const string CheckpointWaitReason = "VEHICLE_WAITING_AT_CHECKPOINT";

    /// <summary>The checkpoint wait has lasted longer than the configured budget.</summary>
    public const string CheckpointWaitExceededReason = "VEHICLE_CHECKPOINT_WAIT_EXCEEDED";

    private readonly JourneyRuntimeOptions runtimeOptions = options.Value;

    private readonly TaskTypeStationAccess _taskTypeStations = taskTypeStations;

    /// <summary>
    /// Runs the catalog change convergence without letting its failure end the round (control-server#201, review D of
    /// #162). A busy database or a constraint conflict here used to escape <see cref="ExecuteOnceAsync"/>, so the
    /// admission policy and every journey under way sat out the round.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Carrying on opens nothing: a demand bound to a renamed or removed station is already refused this round by the
    /// fixed station view, which judges every binding against this very catalog before it looks at holds. The hold
    /// arriving a round late only delays what it adds on top -- staying in force once the station is back.
    /// </para>
    /// <para>
    /// The convergence's transaction rolls back on its own, but what it touched stays tracked on the shared context:
    /// a pending entry the round's next save would write after all (a hold without its change record or its audit),
    /// or an entry it already saved inside the rolled-back transaction, tracked as Unchanged for a row the database no
    /// longer has. So every entry the attempt began tracking is detached, and every pending one too; nothing else in
    /// the round leaves one pending at this point, because every step before this one saved. A shutdown cancellation
    /// still ends the round.
    /// </para>
    /// </remarks>
    private async Task ConvergeCatalogBindingHoldsAsync(
        RiotMapStationCatalogSnapshot currentMap,
        CancellationToken cancellationToken)
    {
        HashSet<object> trackedBefore = dbContext.ChangeTracker.Entries()
            .Select(entry => entry.Entity)
            .ToHashSet(ReferenceEqualityComparer.Instance);
        try
        {
            await catalogBindingHolds.ApplyAsync(currentMap, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            foreach (EntityEntry left in dbContext.ChangeTracker.Entries()
                .Where(entry => !trackedBefore.Contains(entry.Entity)
                    || entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
                .ToArray())
            {
                left.State = EntityState.Detached;
            }
            LogCatalogBindingHoldConvergenceFailed(logger, currentMap.MapId, error);
        }
    }

    public async Task ExecuteOnceAsync(CancellationToken cancellationToken)
    {
        if (!runtimeOptions.Enabled)
        {
            return;
        }

        RiotMapStationCatalogSnapshot currentMap;
        IFixedTaskStationView fixedStations;
        IReadOnlyList<RiotMapStation> machineStations;
        try
        {
            currentMap = await mapStationCatalog.ReadMapStationsAsync(
                runtimeOptions.MapId, cancellationToken).ConfigureAwait(false);
            // Once per round, so that every candidate is judged against the same rules and bindings. A fixed
            // station missing from the Map is not a catalog failure (control-server#160, REQ-0342): the view
            // refuses the task type bound to it and nothing else, so journeys under way and every other task type
            // go on. Only a resolver that cannot use the Map at all throws StationResolutionException.
            fixedStations = await fixedStationResolver.ReadForRoundAsync(currentMap, cancellationToken)
                .ConfigureAwait(false);
            machineStations = stationResolver.ParseAreaNamedMachineStations(currentMap);
        }
        catch (Exception error) when (
            error is HttpRequestException or InvalidDataException or JsonException or StationResolutionException)
        {
            LogMapStationCatalogFailed(logger, error);
            // REQ-0302/REQ-0308: a failed attempt is a catalog-level state, and explicitly not a
            // confirmation -- it must not extend the freshness window. Whether it blocks depends
            // on how long ago the last real confirmation was, which the availability check decides.
            await catalogAvailability.RecordFailureAsync(
                runtimeOptions.MapId,
                error is StationResolutionException
                    ? MapStationCatalogState.CandidateInvalid
                    : MapStationCatalogState.RefreshFailed,
                error is StationResolutionException resolution
                    ? resolution.ReasonCode
                    : "CATALOG_REFRESH_FAILED",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        // Read whole and machine stations parsed: this is what a complete confirmation is, and the only thing
        // freshness is measured from. Whether some task type's fixed station is in it does not enter into it
        // (REQ-0302); that is the task type's own admission question.
        await catalogAvailability.RecordConfirmationAsync(currentMap, cancellationToken)
            .ConfigureAwait(false);
        // control-server#162: a bound station renamed or gone holds its own Map + TASK_TYPE, read from the next round on.
        await ConvergeCatalogBindingHoldsAsync(currentMap, cancellationToken).ConfigureAwait(false);

        // The policy is re-derived from the live map every iteration, and map 25 is shared: RIoT's
        // other users add, rename and remove stations on it. Any such edit to an area-named station
        // arrives here as a different set under the version this deployment names, and the store
        // refuses to rebind it. That refusal used to escape this method, so every iteration failed
        // before AdvanceAsync -- a loaded vehicle already bound for the gate was never given its
        // unload until someone raised the version and restarted. ADR-cross-0050 and 0051 confine a
        // policy change to what is not yet committed: the bound policy stays in force for every
        // journey under way, and only taking on a further demand waits for the version, which
        // AdmissionPolicyDriftCriterion enforces for every vehicle this round serves. One operation of
        // a journey under way is not committed yet and is asked against the current admissions: the
        // unload of a journey whose AREA machine is the drop-off (STAGING_TO_WIRE), until it is
        // prepared. A machine that no longer admits it holds the loaded vehicle there, and past
        // JourneyRuntimeOptions.AreaEndAdmissionRevokedTimeout the journey is blocked for manual
        // recovery rather than left waiting unseen (control-server#198).
        string[] liveStationNames = machineStations.Select(station => station.StationName)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        bool admissionPolicyDrifted = false;
        try
        {
            // Each area-named station is paired with every task type this build can execute, whichever end of the
            // route the station is (control-server#163): the admission follows the AREA machine end, which is the
            // pickup for WIRE_TO_GATE and the drop-off for STAGING_TO_WIRE. Adding STAGING_TO_WIRE changed the
            // relations and their hash, so the shipped admissionPolicyVersion moved past 1 with it.
            string[] seededTaskTypes = await AdmissionSeedTaskTypesAsync(cancellationToken).ConfigureAwait(false);
            await store.ApplyAdmissionPolicyAsync(
                new AdmissionPolicyDefinition(
                    runtimeOptions.AdmissionPolicyVersion,
                    runtimeOptions.AdmissionPolicyDeploymentId,
                    liveStationNames
                        .SelectMany(stationName => seededTaskTypes
                            .Select(taskType => new StationTaskTypeAdmission(stationName, taskType)))
                        .ToArray(),
                    timeProvider.GetUtcNow()),
                cancellationToken).ConfigureAwait(false);
        }
        catch (BusinessIdentityConflictException error)
        {
            admissionPolicyDrifted = true;
            string[] boundStationNames = await dbContext.StationTaskTypeAdmissions.AsNoTracking()
                .Select(row => row.StationId)
                .ToArrayAsync(cancellationToken).ConfigureAwait(false);
            LogAdmissionPolicyDrift(
                logger,
                runtimeOptions.AdmissionPolicyVersion,
                runtimeOptions.MapId,
                Describe(liveStationNames.Except(boundStationNames, StringComparer.Ordinal)),
                Describe(boundStationNames.Except(liveStationNames, StringComparer.Ordinal)),
                error);

            static string Describe(IEnumerable<string> stationNames) =>
                string.Join(", ", stationNames.Order(StringComparer.Ordinal)) is { Length: > 0 } names
                    ? names
                    : "none";
        }

        JourneyRuntimeRow[] active = await dbContext.JourneyRuntimes
            .Where(row => row.Stage != JourneyRuntimeStage.Completed)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        active = active.OrderBy(row => row.CreatedAt).ToArray();
        // The one-journey invariant is per vehicle, not per server. It was written as a global
        // check while there was one vehicle, and those are the same statement then; with a fleet
        // they are not, and reading it globally would refuse to run the fleet at all.
        string[] doubleBooked = active
            .GroupBy(row => row.AgvId, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        if (doubleBooked.Length > 0)
        {
            throw new BusinessIdentityConflictException(
                $"More than one unresolved journey exists for vehicle: {string.Join(',', doubleBooked)}.");
        }

        // Vehicles are advanced before free ones are served, and a journey created this round is
        // not advanced until the next one. With one vehicle that is exactly the old shape -- either
        // it had a journey and only advanced, or it had none and only discovered.
        foreach (JourneyRuntimeRow runtime in active)
        {
            await AdvanceAsync(runtime, currentMap, cancellationToken).ConfigureAwait(false);
        }

        HashSet<string> busy = active.Select(row => row.AgvId).ToHashSet(StringComparer.Ordinal);
        FleetVehicle[] free = roster.Vehicles
            .Where(vehicle => !busy.Contains(vehicle.AgvId))
            .ToArray();
        if (free.Length == 0)
        {
            return;
        }

        // The orphan check guards intake, so it runs when this round is about to take work on --
        // which with one vehicle is exactly when it ran before, since the single vehicle being
        // free is the same statement as no journey being active.
        string[] unresolvedDemandIds = await dbContext.AcceptedDemands
            .Where(row => row.Status != DemandExecutionStatus.Succeeded &&
                          row.Status != DemandExecutionStatus.Cancelled)
            .Select(row => row.DemandId)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        // A demand is carried by a journey when a membership in force says so (control-server#207), not when a journey
        // row names it: a demand added to a journey has no row of its own. A journey whose demands have all ended is no
        // orphan either -- none of them is unresolved -- and the next round closes it.
        string[] memberDemandIds = await DemandJourneyLookup.Memberships(dbContext)
            .Select(row => row.DemandId)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        string[] orphaned = unresolvedDemandIds.Except(memberDemandIds, StringComparer.Ordinal).ToArray();
        if (orphaned.Length > 0)
        {
            throw new BusinessIdentityConflictException(
                $"Unresolved accepted demand has no production journey runtime: {string.Join(',', orphaned)}.");
        }

        await DiscoverAndAcceptAsync(currentMap, fixedStations, free, admissionPolicyDrifted, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The task types the station admission seed carries: those this build can execute, at either fixed end. The seed
    /// is per area-named machine station, and a task type is admitted at its AREA end whether that is the pickup
    /// (fixed end at the destination) or the drop-off (fixed end at the origin); control-server#160 seeded only the
    /// former, which was all it could execute.
    /// </summary>
    /// <remarks>
    /// Read from the rules the Map's active binding set was built on -- the same version the resolver judges the round
    /// by -- not the latest one: a rule version written ahead of its binding set must not change the seed, which the
    /// store would take for a policy drift and stop every acceptance on.
    /// </remarks>
    private async Task<string[]> AdmissionSeedTaskTypesAsync(CancellationToken cancellationToken)
    {
        TaskTypeStationBindingSetVersion? bindingSet = await _taskTypeStations.Bindings
            .ReadActiveAsync(runtimeOptions.MapId, cancellationToken).ConfigureAwait(false);
        TaskTypeStationRuleVersion? rules = bindingSet is null
            ? await _taskTypeStations.Rules.ReadCurrentAsync(cancellationToken).ConfigureAwait(false)
            : await _taskTypeStations.Rules.ReadVersionAsync(bindingSet.RuleVersion, cancellationToken)
                .ConfigureAwait(false);
        return
        [
            .. (rules?.Rules ?? [])
                .Where(rule => ExecutableTaskTypes.Contains(rule.TaskType))
                .Select(rule => rule.TaskType)
                .Order(StringComparer.Ordinal)
        ];
    }

    private async Task DiscoverAndAcceptAsync(
        RiotMapStationCatalogSnapshot currentMap,
        IFixedTaskStationView fixedStations,
        IReadOnlyList<FleetVehicle> vehicles,
        bool admissionPolicyDrifted,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        DemandCatalogSnapshot snapshot;
        try
        {
            snapshot = await catalog.ReadCatalogAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is HttpRequestException or InvalidDataException or JsonException)
        {
            LogCatalogPollFailed(logger, error);
            return;
        }

        Dictionary<string, JourneyBacklogRow> backlogByDemandId = await dbContext.JourneyBacklog
            .ToDictionaryAsync(row => row.DemandId, StringComparer.Ordinal, cancellationToken)
            .ConfigureAwait(false);
        await MarkBacklogLeftCatalogAsync(backlogByDemandId, snapshot, cancellationToken).ConfigureAwait(false);
        // The MesIngest catalog is MES's own list of open transport demands, and a journey of ours
        // reaching Completed does not take the demand out of it. Discovery is only reached once no
        // unresolved journey remains, so the demand that just finished was scored as a fresh
        // candidate again and, being the oldest thing in the backlog, was selected ahead of every
        // other one. Intake then met its own AcceptedDemands row and the store refused the replay
        // -- correctly, because the pickup intent this rebuilds carries the current clock as its
        // CreatedAt and no longer matches the persisted one. That refusal failed the whole
        // iteration closed, so no *other* eligible demand could be accepted for as long as the
        // finished demand stayed in the catalog. A demand this server has accepted is bound to its
        // one journey permanently; it is never a candidate again, whatever stage that journey
        // reached.
        // Kept as a live set rather than a snapshot: a demand taken by an earlier vehicle in this
        // same round has to stop being a candidate for the later ones. See DispatchRoundFacts.
        HashSet<string> acceptedDemandIds = (await dbContext.AcceptedDemands
                .Select(row => row.DemandId)
                .ToArrayAsync(cancellationToken).ConfigureAwait(false))
            .ToHashSet(StringComparer.Ordinal);

        VehicleDispatchPolicy policy = await dispatchPolicy.EnsureCurrentAsync(cancellationToken)
            .ConfigureAwait(false);
        // Read once with the policy and for the same reason: every candidate in the round is judged against
        // one version of the table, and that is the version a demand freezes.
        AreaAssignmentTableVersion? areaAssignmentTable = await areaAssignments
            .ReadCurrentAsync(cancellationToken).ConfigureAwait(false);
        DispatchRoundFacts round = new(
            snapshot, currentMap, fixedStations, acceptedDemandIds, now, policy, areaAssignmentTable,
            admissionPolicyDrifted);
        List<DispatchVehicleOutcome> completedVehicles = [];

        // One worker, vehicles in series -- not one worker per vehicle. Serial iteration is what
        // keeps a round's snapshot fresh: two workers would each decide against their own read of
        // the same catalog and could accept the same demand twice.
        //
        // Each vehicle gets its own budget rather than the round getting one. The failure this
        // guards against is a single vehicle's reads hanging -- an unreachable peer, a RIoT call
        // that never answers -- and a round-level budget would let that one vehicle consume the
        // whole round and starve every vehicle behind it. A vehicle that runs out is skipped for
        // this round only; nothing about it is remembered, so it is served again next round.
        foreach (FleetVehicle vehicle in vehicles)
        {
            TimeSpan budget = RoundBudget(policy, vehicle.AgvId);
            using CancellationTokenSource expiry = new(budget, timeProvider);
            using CancellationTokenSource linked =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, expiry.Token);
            List<DispatchCandidateVerdict> verdicts = [];
            try
            {
                await DispatchForVehicleAsync(
                    round, vehicle, acceptedDemandIds, backlogByDemandId, verdicts, now, linked.Token)
                    .ConfigureAwait(false);
                // Only once the segment has run to its end. A vehicle its budget cuts off below has not
                // finished deciding, so what it judged so far says nothing about that vehicle.
                completedVehicles.Add(new DispatchVehicleOutcome(vehicle.AgvId, vehicle.VehicleKey, verdicts));
            }
            catch (OperationCanceledException) when (expiry.IsCancellationRequested &&
                                                     !cancellationToken.IsCancellationRequested)
            {
                LogVehicleRoundBudgetExhausted(
                    logger, vehicle.AgvId, (int)budget.TotalMilliseconds, null);
                // Whatever the abandoned segment had staged is not this vehicle's decision any
                // more and must not be written under the next vehicle's SaveChanges. Dropping it
                // is what actually keeps one vehicle's timeout from reaching the others -- the
                // budget only stops the work, the tracker is what would have carried it across.
                dbContext.ChangeTracker.Clear();
                backlogByDemandId.Clear();
                foreach (JourneyBacklogRow row in await dbContext.JourneyBacklog
                             .ToArrayAsync(cancellationToken).ConfigureAwait(false))
                {
                    backlogByDemandId[row.DemandId] = row;
                }
            }
        }

        // After every vehicle, a budget-exhausted one included. Whether any vehicle at all could take a demand
        // is only answerable across the fleet; JourneyBacklog, overwritten vehicle by vehicle, cannot say.
        await roundOutcomes.RecordAsync(new DispatchRoundOutcome(round, completedVehicles), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// How long this vehicle's segment of the round may take.
    /// </summary>
    /// <remarks>
    /// A vehicle the policy does not mention still gets the default budget rather than none. It
    /// cannot be dispatched — the task-type criterion refuses an unconfigured vehicle — but the
    /// point of running it anyway is that refusal reaching the backlog under its own reason code.
    /// A zero budget would replace that diagnosis with a timeout every round.
    /// </remarks>
    private static TimeSpan RoundBudget(VehicleDispatchPolicy policy, string agvId)
    {
        VehicleDispatchProfile? profile = policy.Vehicles
            .FirstOrDefault(vehicle => string.Equals(vehicle.AgvId, agvId, StringComparison.Ordinal));
        int milliseconds = profile?.RoundTimeoutMilliseconds ?? 0;
        return TimeSpan.FromMilliseconds(milliseconds > 0
            ? milliseconds
            : new FleetVehicleOptions().RoundTimeoutMilliseconds);
    }

    /// <summary>Scores every candidate for one vehicle and dispatches at most one of them.</summary>
    private async Task DispatchForVehicleAsync(
        DispatchRoundFacts round,
        FleetVehicle fleetVehicle,
        HashSet<string> claimedDemandIds,
        Dictionary<string, JourneyBacklogRow> backlogByDemandId,
        List<DispatchCandidateVerdict> verdicts,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        string vehicleKey = fleetVehicle.VehicleKey;
        // The server's own record of which slot is in which group, once per vehicle per round. Not the
        // vehicle's report (program#70 decision 4), which is why the onboard slot facts below stay as they are.
        VehicleSlotPositions? vehicleSlotPositions = await slotPositions
            .ReadAsync(fleetVehicle.AgvId, cancellationToken).ConfigureAwait(false);
        // Onboard facts are read for this vehicle's own agvId. Reading them off the single
        // configured one, as this did while there was one vehicle, would have judged every
        // vehicle in the fleet against the first vehicle's session -- the exact cross-talk the
        // per-vehicle iteration exists to prevent.
        OnboardDispatchFacts? onboard = await ReadOnboardFactsAsync(fleetVehicle.AgvId, cancellationToken)
            .ConfigureAwait(false);
        RiotVehicleObservation vehicle = await vehicleFacts
            .ReadVehicleAsync(vehicleKey, cancellationToken).ConfigureAwait(false);
        DispatchVehicleFacts vehicleForRound = new(
            vehicleKey, fleetVehicle.AgvId, onboard, vehicle, timeProvider.GetUtcNow(), vehicleSlotPositions);

        List<EligibleDispatchCandidate> eligible = [];
        foreach (AcceptedDemandSnapshot candidate in round.Catalog.Items)
        {
            DispatchCandidateEvaluation evaluation = new(candidate, round, vehicleForRound);
            string reason = await admissionChain
                .EvaluateAsync(evaluation, cancellationToken).ConfigureAwait(false);

            verdicts.Add(new DispatchCandidateVerdict(evaluation, reason));
            JourneyBacklogRow backlog = UpsertBacklog(backlogByDemandId, candidate, reason, now);
            if (string.Equals(reason, DispatchAdmissionChain.Eligible, StringComparison.Ordinal) &&
                evaluation.Route is not null)
            {
                eligible.Add(new EligibleDispatchCandidate(
                    candidate,
                    evaluation.Route,
                    evaluation.ExpectedBasketCount,
                    evaluation.TargetSlots,
                    backlog.FirstSeenAt,
                    evaluation.GraphTraversalCostMm,
                    evaluation.CatalogRevision,
                    evaluation.AreaAssignmentVersion,
                    evaluation.RequiredSlotPosition));
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        if (eligible.Count == 0)
        {
            return;
        }
        if (await dbContext.VehicleDispatchLeases.AnyAsync(
                row => row.VehicleKey == vehicleKey && row.ReleasedAt == null,
                cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        EligibleDispatchCandidate selected = candidateRanker.SelectNext(eligible);
        long expectedSessionGeneration = onboard?.SessionGeneration
            ?? throw new InvalidOperationException("An eligible candidate requires current Onboard facts.");
        if (!await FinalDynamicFactsReadyAsync(
                fleetVehicle,
                expectedSessionGeneration,
                selected.TargetSlots,
                cancellationToken).ConfigureAwait(false))
        {
            await SetBacklogReasonAsync(
                selected.Snapshot.DemandId,
                "FINAL_DYNAMIC_FACTS_NOT_READY",
                timeProvider.GetUtcNow(),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        DateTimeOffset intakeAt = timeProvider.GetUtcNow();
        // REQ-0305: both endpoints are frozen from the snapshot that was fresh when the demand was taken -- by the
        // store, inside the transaction that accepts it (control-server#160). Frozen here, ahead of the intake, an
        // acceptance refused at the last moment left them behind, and after a rebinding the next attempt was
        // refused a rewrite and failed every round for every task type.
        JourneyExecutionPlan plan = new JourneyPlanBuilder(runtimeOptions).CreatePlan(fleetVehicle, selected, intakeAt);
        OrderIntent pickup = JourneyPlanBuilder.PickupIntent(plan, selected.Snapshot.DemandId, intakeAt);
        // Taken before the call rather than after it: a candidate this vehicle is committing to
        // must stop being a candidate for the vehicles behind it in this round whatever the intake
        // then reports, because every refusal below leaves the demand bound to this attempt.
        claimedDemandIds.Add(selected.Snapshot.DemandId);
        JourneyIntakeResult result = await intakeCoordinator.AcceptAndDispatchToPickupAsync(
            selected.Snapshot,
            pickup,
            plan,
            token => FinalDynamicFactsReadyAsync(
                fleetVehicle,
                expectedSessionGeneration,
                selected.TargetSlots,
                token),
            cancellationToken).ConfigureAwait(false);
        if (result.IntakeOutcome != DemandIntakeOutcome.Accepted)
        {
            await SetBacklogReasonAsync(
                selected.Snapshot.DemandId,
                result.IntakeOutcome switch
                {
                    DemandIntakeOutcome.CandidateGone => "FINAL_CATALOG_CANDIDATE_GONE",
                    DemandIntakeOutcome.CandidateChanged => "FINAL_CATALOG_DECISION_FACT_CHANGED",
                    DemandIntakeOutcome.FinalAdmissionRejected => "FINAL_DYNAMIC_FACTS_NOT_READY",
                    DemandIntakeOutcome.JourneyPlanIncomplete => "FINAL_JOURNEY_PLAN_INCOMPLETE",
                    _ => throw new InvalidOperationException(
                        $"Unsupported intake outcome '{result.IntakeOutcome}'.")
                },
                timeProvider.GetUtcNow(),
                cancellationToken).ConfigureAwait(false);
            return;
        }
        // The vehicle is now carrying this journey's first order, and that is what the occupancy
        // claim records. It is decided by the filtered unique index on OrderIntents rather than by
        // a read, so it is the constraint the lease check above cannot be: the lease is checked
        // before the round decides and the database enforces this one at the moment of writing. A
        // refusal here therefore means a second writer got in between, and it is reported rather
        // than worked around -- the journey exists, so the operator has to see which vehicle is
        // double-booked.
        if (!await dispatchPolicy.TryClaimVehicleOccupancyAsync(plan.PickupUpperId, cancellationToken)
                .ConfigureAwait(false))
        {
            LogVehicleOccupancyConflict(logger, fleetVehicle.AgvId, plan.PickupUpperId, null);
            JourneyRuntimeRow conflicted = await dbContext.JourneyRuntimes
                .SingleAsync(row => row.DemandId == selected.Snapshot.DemandId, cancellationToken)
                .ConfigureAwait(false);
            Block(conflicted, "VEHICLE_OCCUPANCY_CONFLICT", timeProvider.GetUtcNow());
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (result.MovementDispatch?.Outcome != MovementDispatchOutcome.Confirmed)
        {
            JourneyRuntimeRow runtime = await dbContext.JourneyRuntimes
                .SingleAsync(row => row.DemandId == selected.Snapshot.DemandId, cancellationToken)
                .ConfigureAwait(false);
            runtime.SetBlockReason(
                result.MovementDispatch?.Outcome.ToString() ?? "PICKUP_DISPATCH_NOT_CONFIRMED", now);
            runtime.UpdatedAt = now;
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task AdvanceAsync(
        JourneyRuntimeRow runtime,
        RiotMapStationCatalogSnapshot currentMap,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        SessionRecoveryRow? session = await CurrentReadySessionAsync(runtime.AgvId, cancellationToken)
            .ConfigureAwait(false);
        if (session is null)
        {
            // ADR-cross-0055: the station departure wait is about this session's chance to scan, and a
            // journey behind a closed readiness gate has none -- the runtime advances no stop there, so
            // an entry would not be acted on either. Left running, the clock would end the stop on the
            // first iteration after readiness returned. Voided here and refilled behind the gate, the
            // same way a reconnect's is. A disconnect that leaves the session Ready is caught instead by
            // the liveness check in TryEndStopAtStationDeadlineAsync.
            bool waitVoided = runtime.StationDepartureWaitStartedAt is not null;
            runtime.StationDepartureWaitStartedAt = null;

            // A result that is already durable names the recovery this journey waits on, and it
            // names it from StationOperations alone -- reading it involves no session. Deciding
            // that here, ahead of the readiness gate, is what keeps the gate from closing on
            // exactly the journeys that most need their block named: since 147f02c a refused
            // result moves the session itself to RecoveryRequired, so the vehicle that just failed
            // a load is the one whose session is no longer Ready. L2 run
            // 20260904-recovery-entry-after-announce-001 finished on that deadlock, sitting at
            // AwaitingLoadResult / ONBOARD_SESSION_NOT_READY over a load result the database
            // already held.
            if (await TryBlockOnRecordedRecoveryAsync(runtime, now, cancellationToken)
                .ConfigureAwait(false))
            {
                return;
            }

            // A Blocked journey is already waiting on the recovery its block reason names, and the
            // vehicle is normally powered down for exactly that repair -- so losing the session is
            // the ordinary case there, not news. Overwriting the reason threw away the only record
            // of which recovery is outstanding, and nothing rebuilds it: the field left a journey
            // reading "Blocked / ONBOARD_SESSION_NOT_READY", which names neither. Session
            // readiness carries its own row and its own reason code.
            // A stop held at its AREA machine for the station's admission is still held for that while the session is
            // down, and the reason is what control-server#198 counts the wait from: overwritten here, every reconnect
            // started the count again, and a link dropping more often than the threshold kept the loaded vehicle waiting
            // for ever.
            if (runtime.Stage != JourneyRuntimeStage.Blocked && !IsHeldForAreaEndAdmission(runtime))
            {
                runtime.SetBlockReason("ONBOARD_SESSION_NOT_READY", now);
                runtime.UpdatedAt = now;
                await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            else if (waitVoided)
            {
                runtime.UpdatedAt = now;
                await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            return;
        }
        await publisher.ReplayPendingForSessionAsync(
            runtime.AgvId,
            session.SessionGeneration,
            RuntimeMessageIds(runtime),
            cancellationToken).ConfigureAwait(false);

        // Set when this iteration has already asked a pre-departure check again, so the judgment that
        // follows neither asks a third time nor forgets why the vehicle is still waiting.
        bool reissuedDepartureCheck = false;
        switch (runtime.Stage)
        {
            case JourneyRuntimeStage.AwaitingPickupArrival:
                if (!await EnsureMovementConfirmedAsync(
                        runtime, runtime.PickupUpperId, "PICKUP", cancellationToken).ConfigureAwait(false))
                {
                    return;
                }
                await PublishPickupDispatchPlanOnceAsync(runtime, session, cancellationToken).ConfigureAwait(false);
                ArrivalCheck pickupArrival = await CheckArrivalAsync(
                    runtime, "TO_PICKUP", session, cancellationToken).ConfigureAwait(false);
                if (!pickupArrival.Trusted)
                {
                    if (await ObserveOrderFailureAsync(runtime, pickupArrival, cancellationToken)
                            .ConfigureAwait(false))
                    {
                        return;
                    }
                    await NameCheckpointWaitAsync(runtime, cancellationToken).ConfigureAwait(false);
                    return;
                }
                await PublishPickupStateAsync(runtime, session, cancellationToken).ConfigureAwait(false);
                SetStage(runtime, JourneyRuntimeStage.AwaitingSublot, now);
                break;
            case JourneyRuntimeStage.AwaitingSublot:
                // Refills the station departure wait a disconnect voided (ADR-cross-0055). A reconnect
                // does not re-enter this stage, so the arrival's seed cannot be what refills it; and
                // this line runs behind the readiness gate, which is "after the recovery handshake and
                // the projection reconciliation".
                bool waitRefilled = runtime.StationDepartureWaitStartedAt is null;
                runtime.StationDepartureWaitStartedAt ??= now;
                ProtocolInboxRow? sublot = await FindMatchingSublotAsync(runtime, session, cancellationToken)
                    .ConfigureAwait(false);
                // An operator cancelling before any entry (ADR-cross-0046; control-server#83) holds the stop
                // until the vehicle reports: no load starts and the deadline does not end it. Read after the
                // inbox, so an entry seen here cannot have been persisted before the cancellation it loses to.
                if (await LoadCancellationBeforeSublot.HasOpenCancellationAsync(
                        dbContext, runtime.DemandId, cancellationToken).ConfigureAwait(false))
                {
                    // What the entry read concluded does not stand while the cancellation decides the stop, so this
                    // iteration's unsaved change to the block is undone -- code and start time both, back to what the
                    // row holds in the store. This is only for undoing an unsaved change: every new block is written
                    // through SetBlockReason, and writing the old code back through it here would restart a block
                    // that never ended.
                    EntityEntry<JourneyRuntimeRow> tracked = dbContext.Entry(runtime);
                    tracked.Property(row => row.BlockReasonCode).CurrentValue =
                        tracked.Property(row => row.BlockReasonCode).OriginalValue;
                    tracked.Property(row => row.BlockReasonSince).CurrentValue =
                        tracked.Property(row => row.BlockReasonSince).OriginalValue;
                    if (waitRefilled)
                    {
                        runtime.UpdatedAt = now;
                        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    }
                    return;
                }
                if (sublot is null)
                {
                    if (await TryEndStopAtStationDeadlineAsync(runtime, session, now, cancellationToken)
                            .ConfigureAwait(false))
                    {
                        return;
                    }
                    // The one quiet exit of this stage. A wait refilled above and not saved here would be
                    // refilled again on every later iteration, which is the same as never running out.
                    if (runtime.BlockReasonCode is not null || waitRefilled)
                    {
                        runtime.UpdatedAt = now;
                        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    }
                    return;
                }
                // The entry answers the request open at this stop. Whether it may load is judged now,
                // after the cancellation has had its chance to decide the stop: an entry that loses to
                // one is neither loaded nor answered. A refusal is answered here, which is the point of
                // BR-013's "give the operator the real reason", and leaves the stop exactly where it was.
                if (!await RevalidateEnteredSublotAsync(runtime, session, sublot, now, cancellationToken)
                        .ConfigureAwait(false))
                {
                    return;
                }
                await PublishLoadAsync(runtime, session, sublot.MessageId, cancellationToken).ConfigureAwait(false);
                runtime.ConsumedSublotMessageId = sublot.MessageId;
                // The submission is this command's answer. Leaving the command unsettled replayed it
                // into every later session, where the peer refused it as a business id whose content
                // had changed and tore the session down.
                await store.SettleAnsweredCommandAsync(
                    runtime.SublotRequestMessageId, now, cancellationToken).ConfigureAwait(false);
                SetStage(runtime, JourneyRuntimeStage.AwaitingLoadResult, now);
                break;
            case JourneyRuntimeStage.AwaitingLoadResult:
                StationOperationRow? load = await dbContext.StationOperations.SingleOrDefaultAsync(
                    row => row.SlotOperationAttemptId == runtime.LoadSlotOperationAttemptId,
                    cancellationToken).ConfigureAwait(false);
                if (load?.Status == StationOperationStatus.RecoveryRequired)
                {
                    Block(runtime, "LOAD_RESULT_REQUIRES_RECOVERY", now);
                }
                else if (load?.Status == StationOperationStatus.Failed)
                {
                    await TrySettleDeterminateLoadFailureAsync(runtime, load, session, now, cancellationToken)
                        .ConfigureAwait(false);
                    return;
                }
                else if (load?.Status == StationOperationStatus.Committed)
                {
                    await store.SettleAnsweredCommandAsync(
                        runtime.LoadCommandMessageId, now, cancellationToken).ConfigureAwait(false);
                    // REQ-0237 / ADR-cross-0054: the load is committed and the vehicle is still at the
                    // pickup, which is the only time an ordinary mis-placement may be corrected. Asking
                    // for departure safety in this same iteration, as this server did until 2026-09-13,
                    // left no such time at all. Not saved here: with the wait off the next case departs
                    // at once and saves once, as before.
                    runtime.StationDepartureWaitStartedAt = now;
                    SetStage(runtime, JourneyRuntimeStage.AwaitingStationDeparture, now);
                    goto case JourneyRuntimeStage.AwaitingStationDeparture;
                }
                else
                {
                    // No result yet. Refills the station departure wait a disconnect voided (ADR-cross-0055),
                    // the way AwaitingSublot does: this stage is not re-entered on a reconnect either, and
                    // without it a stop loading across a reconnect would have no deadline to raise its
                    // alarm by.
                    bool loadWaitRefilled = runtime.StationDepartureWaitStartedAt is null;
                    runtime.StationDepartureWaitStartedAt ??= now;
                    if (ReconcileStationTimeoutDoorNotClosed(runtime, session, now) || loadWaitRefilled)
                    {
                        runtime.UpdatedAt = now;
                        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    }
                    return;
                }
                break;
            case JourneyRuntimeStage.AwaitingStationDeparture:
                if (!await StationDepartureWaitIsOverAsync(runtime, now, cancellationToken).ConfigureAwait(false))
                {
                    await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    return;
                }
                runtime.StationDepartureWaitStartedAt = null;
                await publisher.PublishPreDepartureSafetyCheckAsync(
                    runtime.PreDepartureSafetyCheckMessageId,
                    runtime.AgvId,
                    session.SessionGeneration,
                    new PreDepartureSafetyCheckCommand(
                        runtime.PreDepartureSafetyCheckId,
                        runtime.DemandId,
                        runtime.GateMovementLegId,
                        session.SafetyRevision ?? throw new InvalidDataException("Safety revision is required."),
                        runtime.GateStationId),
                    cancellationToken).ConfigureAwait(false);
                SetStage(runtime, JourneyRuntimeStage.AwaitingDepartureSafety, now);
                await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                // The answer arrives in tens of milliseconds and is stamped with the peer's own
                // short validity window. Returning here and reading it on the next poll spent
                // that entire window waiting, so the evidence was always expired by the time it
                // was judged and the journey never left this stage. Judge it while it is valid.
                goto case JourneyRuntimeStage.AwaitingDepartureSafety;
            case JourneyRuntimeStage.AwaitingDepartureSafety:
                SafetyCheckObservation? safety = await AwaitSafeDepartureResultAsync(
                    runtime, session, cancellationToken).ConfigureAwait(false);
                now = timeProvider.GetUtcNow();
                if (safety is null)
                {
                    if (!reissuedDepartureCheck &&
                        await ReissueExpiredDepartureCheckAsync(runtime, session, now, cancellationToken)
                            .ConfigureAwait(false))
                    {
                        reissuedDepartureCheck = true;
                        // The new check is answered within its peer's short window, so it is judged
                        // now, the way the first check is judged in the iteration that asks it.
                        goto case JourneyRuntimeStage.AwaitingDepartureSafety;
                    }
                    if (reissuedDepartureCheck && runtime.BlockReasonCode is null)
                    {
                        runtime.SetBlockReason("PREDEPARTURE_CHECK_EXPIRED", now);
                    }
                    if (runtime.BlockReasonCode is not null)
                    {
                        runtime.UpdatedAt = now;
                        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    }
                    return;
                }
                // REQ-0305: every RIoT move order that does not exist yet is gated again, against
                // the frozen endpoint and the catalog as it stands now. The gate leg is a second
                // order created minutes after the demand was taken, and nothing about the first
                // order's gate says the vehicle can still reach the gate station from where it now
                // is.
                CreateGateOutcome gateLeg = await GateLegAsync(runtime, currentMap, cancellationToken)
                    .ConfigureAwait(false);
                if (!gateLeg.IsAllowed)
                {
                    runtime.SetBlockReason(gateLeg.BlockReason, timeProvider.GetUtcNow());
                    runtime.UpdatedAt = timeProvider.GetUtcNow();
                    await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    return;
                }

                OrderIntent gateIntent = JourneyPlanBuilder.GateIntent(runtime, now);
                await new WireToGateStore(dbContext).AuthorizeMovementAsync(
                    gateIntent, safety, now, cancellationToken).ConfigureAwait(false);
                MovementDispatchResult dispatch = await movementDispatch.ReconcileOrCreateAsync(
                    runtime.GateUpperId, cancellationToken).ConfigureAwait(false);
                runtime.ConsumedSafetyResultMessageId = await FindSafetyResultMessageIdAsync(
                    runtime.PreDepartureSafetyCheckId, cancellationToken).ConfigureAwait(false);
                await store.SettleAnsweredCommandAsync(
                    runtime.PreDepartureSafetyCheckMessageId, now, cancellationToken).ConfigureAwait(false);
                SetStage(runtime, JourneyRuntimeStage.AwaitingGateArrival, now);
                runtime.SetBlockReason(
                    dispatch.Outcome == MovementDispatchOutcome.Confirmed ? null : dispatch.Outcome.ToString(),
                    now);
                break;
            case JourneyRuntimeStage.AwaitingGateArrival:
                if (!await EnsureMovementConfirmedAsync(
                        runtime, runtime.GateUpperId, "GATE", cancellationToken).ConfigureAwait(false))
                {
                    return;
                }
                ArrivalCheck gateArrival = await CheckArrivalAsync(
                    runtime, "TO_GATE", session, cancellationToken).ConfigureAwait(false);
                if (!gateArrival.Trusted)
                {
                    if (await ObserveOrderFailureAsync(runtime, gateArrival, cancellationToken)
                            .ConfigureAwait(false))
                    {
                        return;
                    }
                    await NameCheckpointWaitAsync(runtime, cancellationToken).ConfigureAwait(false);
                    return;
                }
                if (!await UnloadAdmittedAsync(runtime, cancellationToken).ConfigureAwait(false))
                {
                    // Writing the code it already holds keeps BlockReasonSince, so the wait is counted from the first
                    // round that held the stop here; admitted again, the stage moves on and SetStage clears both.
                    runtime.SetBlockReason("TASK_TYPE_NOT_ALLOWED_AT_STATION", now);
                    runtime.UpdatedAt = now;
                    if (runtime.BlockReasonSince is DateTimeOffset heldSince &&
                        now - heldSince >= runtimeOptions.AreaEndAdmissionRevokedTimeout)
                    {
                        // control-server#198, decided by the user on 2026-09-19: the vehicle is loaded and nobody is told
                        // while it waits, so past the threshold it is handed to a person. Only the journey changes: the
                        // order that brought the vehicle here is not touched, and no new one is created. The block keeps
                        // the first hold's start, which the dashboard's escalation ladder is measured from.
                        runtime.Stage = JourneyRuntimeStage.Blocked;
                        runtime.EscalateBlockReason(AreaEndAdmissionRevokedTimeoutReason);
                        LogAreaEndAdmissionRevokedTimeout(
                            logger,
                            runtime.AgvId,
                            runtime.GateStationId,
                            runtime.DemandId,
                            runtimeOptions.AreaEndAdmissionRevokedTimeout,
                            null);
                    }
                    await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    return;
                }
                await PublishGateStateAndUnloadAsync(runtime, session, cancellationToken).ConfigureAwait(false);
                SetStage(runtime, JourneyRuntimeStage.AwaitingUnloadResult, now);
                break;
            case JourneyRuntimeStage.AwaitingUnloadResult:
                StationOperationRow? unload = await dbContext.StationOperations.SingleOrDefaultAsync(
                    row => row.SlotOperationAttemptId == runtime.UnloadSlotOperationAttemptId,
                    cancellationToken).ConfigureAwait(false);
                if (unload?.Status == StationOperationStatus.RecoveryRequired)
                {
                    Block(runtime, "UNLOAD_RESULT_REQUIRES_RECOVERY", now);
                }
                else
                {
                    AcceptedDemandRow demand = await dbContext.AcceptedDemands.SingleAsync(
                        row => row.DemandId == runtime.DemandId, cancellationToken).ConfigureAwait(false);
                    if (unload?.Status != StationOperationStatus.Committed ||
                        demand.Status != DemandExecutionStatus.Succeeded)
                    {
                        return;
                    }
                    await store.SettleAnsweredCommandAsync(
                        runtime.UnloadCommandMessageId, now, cancellationToken).ConfigureAwait(false);
                    SetStage(runtime, JourneyRuntimeStage.Completed, now);
                    checkpointWaits.Clear(runtime.VehicleKey);
                    // The journey is over, so the vehicle stops being occupied by it. Released
                    // here rather than at the gate arrival because the claim covers the whole
                    // journey, not one leg -- a vehicle that has arrived but not unloaded is still
                    // executing this demand and must not be given another. It runs after the stage
                    // is set so that the store's save commits both, and a crash between them
                    // cannot leave a completed journey still holding its vehicle.
                    await dispatchPolicy.ReleaseVehicleOccupancyAsync(
                        runtime.PickupUpperId, cancellationToken).ConfigureAwait(false);
                }
                break;
            case JourneyRuntimeStage.Blocked:
            case JourneyRuntimeStage.Completed:
                return;
            default:
                throw new InvalidDataException($"Journey stage '{runtime.Stage}' is not supported.");
        }

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }



    /// <summary>
    /// Re-reads the dynamic facts immediately before intake and re-runs the same verdict the
    /// admission chain reached.
    /// </summary>
    /// <remarks>
    /// It calls <see cref="VehicleDynamicFactsCriterion.Evaluate"/> rather than repeating its
    /// clauses, so this check and the chain's cannot drift apart. The session generation and slot
    /// checks sit on top of it: they are what makes this a re-check of *this* decision rather than
    /// a fresh one.
    /// </remarks>
    private async Task<bool> FinalDynamicFactsReadyAsync(
        FleetVehicle fleetVehicle,
        long expectedSessionGeneration,
        IReadOnlyCollection<int> targetSlots,
        CancellationToken cancellationToken)
    {
        OnboardDispatchFacts? onboard = await ReadOnboardFactsAsync(fleetVehicle.AgvId, cancellationToken)
            .ConfigureAwait(false);
        if (onboard is null || onboard.SessionGeneration != expectedSessionGeneration ||
            targetSlots.Any(slot => !onboard.AvailableSlots.Contains(slot)))
        {
            return false;
        }

        RiotVehicleObservation vehicle = await vehicleFacts.ReadVehicleAsync(
            fleetVehicle.VehicleKey,
            cancellationToken).ConfigureAwait(false);
        DispatchVehicleFacts facts = new(
            fleetVehicle.VehicleKey, fleetVehicle.AgvId, onboard, vehicle, timeProvider.GetUtcNow());
        return string.Equals(
            VehicleDynamicFactsCriterion.Evaluate(facts, runtimeOptions),
            DispatchAdmissionChain.Eligible,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// One arrival evaluation, with the order reading it was decided from.
    /// </summary>
    /// <remarks>
    /// The observation is carried out rather than discarded because a journey that did not arrive
    /// still has to say why, and the order's own state is the first thing that answers it. Reading
    /// it a second time for that would be a second RIoT call per poll and, worse, a second answer:
    /// the two reads could disagree, and then "not arrived" and "the order failed" would be judged
    /// against different facts.
    /// </remarks>
    private sealed record ArrivalCheck(
        bool Trusted,
        string Purpose,
        OrderIntentRow Intent,
        RiotOrderObservation Order);

    private async Task<ArrivalCheck> CheckArrivalAsync(
        JourneyRuntimeRow runtime,
        string purpose,
        SessionRecoveryRow session,
        CancellationToken cancellationToken)
    {
        OrderIntentRow intent = await dbContext.OrderIntents.SingleAsync(
            row => row.DemandId == runtime.DemandId && row.Purpose == purpose,
            cancellationToken).ConfigureAwait(false);
        RiotOrderObservation order = await vehicleFacts.ReconcileByUpperIdAsync(intent.UpperId, cancellationToken)
            .ConfigureAwait(false);
        int targetStation = purpose == "TO_PICKUP" ? runtime.PickupStationRiotId : runtime.GateStationRiotId;
        bool exactOrder = order.Kind == RiotOrderObservationKind.Terminal &&
                          order.OrderState == RiotOrderState.Success &&
                          !string.IsNullOrWhiteSpace(order.OrderId) &&
                          order.OrderId == intent.OrderId &&
                          order.VehicleKey == runtime.VehicleKey &&
                          order.MapId == runtime.MapId &&
                          order.DestinationStationId == targetStation;
        if (!exactOrder)
        {
            return new ArrivalCheck(false, purpose, intent, order);
        }
        RiotVehicleObservation vehicle = await vehicleFacts.ReadVehicleAsync(runtime.VehicleKey, cancellationToken)
            .ConfigureAwait(false);
        OnboardDispatchFacts? onboard = await ReadOnboardFactsAsync(runtime.AgvId, cancellationToken)
            .ConfigureAwait(false);
        DateTimeOffset now = timeProvider.GetUtcNow();
        bool trusted = vehicle.Connected && vehicle.Enabled &&
               vehicle.ProcState == "IDLE" &&
               vehicle.CurrentMap == runtime.MapIdentity &&
               vehicle.CurrentStationId == targetStation &&
               vehicle.Speed == 0 &&
               vehicle.LockStatus == 0 &&
               string.IsNullOrWhiteSpace(vehicle.OrderTaskId) &&
               vehicle.ObservedAt <= now && now - vehicle.ObservedAt <= runtimeOptions.MaximumEvidenceAge &&
               onboard is not null && onboard.SessionGeneration == session.SessionGeneration &&
               onboard.VehicleStopped && onboard.AllTargetSlotsLocked && onboard.AllUnlockOutputsReset &&
               !onboard.UnknownPresent;
        return new ArrivalCheck(trusted, purpose, intent, order);
    }

    /// <summary>
    /// REQ-0232's one symptom this loop can establish for itself: RIoT reports the move order this
    /// journey has in flight as FAILED.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this symptom and not the other four.</b> REQ-0232 names five - offline, comms lost,
    /// navigation failed, one order FAILED, elapsed time - and the fault model treats all five
    /// alike once they arrive. What separates this one is that the loop can establish it without
    /// guessing: a terminal FAILED on an order this server created is RIoT's own statement about
    /// this project's own task, and it arrives with the two things the model needs before it can
    /// protect anything - a vehicle with an order in flight, so <c>OrderHold</c> has a target, and
    /// an order <c>ResumeAsync</c> can later be asked about, so the fault has a clearing path.
    /// A vehicle merely unreachable has neither, which is why ticket 09 declined to route
    /// <c>VEHICLE_OFFLINE</c> here: it would record a fault nothing could clear.
    /// </para>
    /// <para>
    /// <b>The stage is deliberately not moved to Blocked.</b> The fault fact is what blocks new
    /// dispatch, and it does so from the moment it is recorded. Leaving the journey in its arrival
    /// stage keeps this loop evaluating, which is the only thing that advances REQ-0247's stop
    /// proof - a window of samples needs the loop to keep sampling - and keeps the escalation live
    /// if the vehicle turns out to be moving after all. What changes is the reason the journey
    /// names for standing still, in the same shape a checkpoint wait names its own.
    /// </para>
    /// <para>
    /// Cargo is claimed only on the gate leg. On the way to the pickup the vehicle is empty and
    /// that is known, which is REQ-0238's one case for not binding; past a committed load it is
    /// carrying this demand's product and the binding has to hold.
    /// </para>
    /// </remarks>
    private async Task<bool> ObserveOrderFailureAsync(
        JourneyRuntimeRow runtime,
        ArrivalCheck arrival,
        CancellationToken cancellationToken)
    {
        if (arrival.Order.Kind != RiotOrderObservationKind.Terminal ||
            arrival.Order.OrderState != RiotOrderState.Failed ||
            arrival.Intent.OrderId is not string orderId)
        {
            return false;
        }

        string transportDemandKey = await dbContext.AcceptedDemands
            .Where(row => row.DemandId == runtime.DemandId)
            .Select(row => row.TransportDemandKey)
            .SingleAsync(cancellationToken).ConfigureAwait(false);
        FaultedVehicleCargoFacts? cargo = arrival.Purpose == "TO_GATE"
            ? new FaultedVehicleCargoFacts(
                runtime.DemandId,
                runtime.GateMovementLegId,
                transportDemandKey,
                LoadingWitnessed: true,
                CargoStateKnown: true)
            : null;

        await faults.ObserveAsync(
            new EmergencyStopSubject(runtime.AgvId, runtime.VehicleKey),
            VehicleFaultEvidence.OrderFailed,
            new FaultedVehicleContext(
                new RiotOrderCommandTarget(runtime.AgvId, arrival.Intent.UpperId, orderId),
                cargo),
            cancellationToken).ConfigureAwait(false);

        LogOrderFailedSymptom(logger, arrival.Intent.UpperId, runtime.AgvId, runtime.DemandId, null);
        checkpointWaits.Clear(runtime.VehicleKey);
        if (!string.Equals(runtime.BlockReasonCode, VehicleFaultEvidence.OrderFailed, StringComparison.Ordinal))
        {
            runtime.SetBlockReason(VehicleFaultEvidence.OrderFailed, timeProvider.GetUtcNow());
            runtime.UpdatedAt = timeProvider.GetUtcNow();
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        return true;
    }

    /// <summary>
    /// Says why a journey that has not arrived is not arriving, when RIoT's answer is that the
    /// vehicle is holding at a traffic checkpoint.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is B2's <c>MT_WAIT_FOR_CHECKPOINT</c> handling, and it deliberately stops short of
    /// the motion model.</b> The state stays
    /// <see cref="VehicleMotionReading.Unknown"/> — a vehicle waiting for its turn at a checkpoint
    /// has not been shown to have stopped rolling, and adding the state to the gateway's
    /// not-moving list would weaken REQ-0247's stop proof to buy a nicer word here. What this adds
    /// is the other half of "recognised": the reason the journey is standing still is named
    /// instead of being left blank, which with one vehicle almost never happened and with several
    /// is the ordinary case.
    /// </para>
    /// <para>
    /// <b>The disposition is bounded waiting, not escalation.</b> Below the budget the wait is
    /// traffic working as designed and the journey simply says so. Past it, the reason changes and
    /// the wait is logged for a person, because a vehicle that never gets its turn will not fix
    /// itself. It is deliberately not routed into the two-level fault model: that path proves a
    /// stop before it decides, an unknown reading satisfies its escalation condition by
    /// construction, and every long checkpoint wait would therefore end in an emergency stop.
    /// </para>
    /// <para>
    /// A vehicle that is not waiting has its wait forgotten, so the next wait is measured from its
    /// own beginning, and any block reason this wrote is cleared rather than left to describe a
    /// situation that has passed.
    /// </para>
    /// </remarks>
    private async Task NameCheckpointWaitAsync(JourneyRuntimeRow runtime, CancellationToken cancellationToken)
    {
        VehicleMotionSample sample = await motionFacts
            .SampleMotionAsync(runtime.VehicleKey, cancellationToken).ConfigureAwait(false);
        bool waiting = string.Equals(
            sample.MovementState, RiotMovementStates.WaitForCheckpoint, StringComparison.Ordinal);
        if (!waiting)
        {
            checkpointWaits.Clear(runtime.VehicleKey);
            if (IsCheckpointReason(runtime.BlockReasonCode))
            {
                runtime.SetBlockReason(null, timeProvider.GetUtcNow());
                runtime.UpdatedAt = timeProvider.GetUtcNow();
                await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            return;
        }

        TimeSpan waited = checkpointWaits.Observe(runtime.VehicleKey, sample.ObservedAt);
        bool exceeded = waited > runtimeOptions.CheckpointWaitBudget;
        string reason = exceeded ? CheckpointWaitExceededReason : CheckpointWaitReason;
        if (exceeded)
        {
            LogCheckpointWaitExceeded(logger, runtime.AgvId, runtime.DemandId, null);
        }
        if (string.Equals(runtime.BlockReasonCode, reason, StringComparison.Ordinal))
        {
            return;
        }

        runtime.SetBlockReason(reason, timeProvider.GetUtcNow());
        runtime.UpdatedAt = timeProvider.GetUtcNow();
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static bool IsCheckpointReason(string? reason) =>
        string.Equals(reason, CheckpointWaitReason, StringComparison.Ordinal) ||
        string.Equals(reason, CheckpointWaitExceededReason, StringComparison.Ordinal);

    private async Task<bool> EnsureMovementConfirmedAsync(
        JourneyRuntimeRow runtime,
        string upperId,
        string legName,
        CancellationToken cancellationToken)
    {
        OrderIntentRow intent = await dbContext.OrderIntents.SingleAsync(
            row => row.UpperId == upperId,
            cancellationToken).ConfigureAwait(false);
        if (intent.Status == "CONFIRMED" && intent.OrderId is not null)
        {
            return true;
        }
        MovementDispatchResult result = await movementDispatch.ReconcileOrCreateAsync(
            upperId, cancellationToken).ConfigureAwait(false);
        if (result.Outcome == MovementDispatchOutcome.Confirmed)
        {
            runtime.SetBlockReason(null, timeProvider.GetUtcNow());
            runtime.UpdatedAt = timeProvider.GetUtcNow();
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        runtime.SetBlockReason($"{legName}_{result.Outcome}", timeProvider.GetUtcNow());
        runtime.UpdatedAt = timeProvider.GetUtcNow();
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return false;
    }

    private async Task PublishPickupStateAsync(
        JourneyRuntimeRow runtime,
        SessionRecoveryRow session,
        CancellationToken cancellationToken)
    {
        AcceptedDemandRow demand = await dbContext.AcceptedDemands.SingleAsync(
            row => row.DemandId == runtime.DemandId, cancellationToken).ConfigureAwait(false);
        await publisher.PublishVehicleBusinessStateAsync(
            runtime.VehicleBusinessMessageId,
            runtime.AgvId,
            session.SessionGeneration,
            TransportBusinessState(runtime.VehicleBusinessRevision, LoadingPhase(runtime.Stage, loadBatchClosed: false)),
            cancellationToken).ConfigureAwait(false);
        // ADR-cross-0055: the station departure wait starts at the arrival. Seeded ahead of the
        // worklist, whose save carries it, because the worklist is where the vehicle is told the
        // deadline -- a first snapshot sent before the seed would tell it there is none.
        runtime.StationDepartureWaitStartedAt ??= timeProvider.GetUtcNow();
        await publisher.PublishCurrentStopWorklistAsync(
            runtime.WorklistMessageId,
            runtime.AgvId,
            session.SessionGeneration,
            Worklist(
                runtime,
                demand,
                runtime.PickupStationId,
                "PICKUP",
                runtime.WorklistRevision,
                StationDepartureDeadline(runtime, runtimeOptions.StationDepartureWaitTimeout)),
            cancellationToken).ConfigureAwait(false);
        await RetireSupersededSnapshotAsync(PickupDispatchPlanMessageId(runtime), cancellationToken)
            .ConfigureAwait(false);
        await publisher.PublishUpcomingStopPlanAsync(
            runtime.PlanMessageId,
            runtime.AgvId,
            session.SessionGeneration,
            JourneyPlanBuilder.PickupPlan(runtime),
            cancellationToken).ConfigureAwait(false);
        // One demand per journey, so the dispatch scope is this demand's sublot (protocol 2.0.0 item 2).
        await publisher.PublishSublotEntryRequestAsync(
            runtime.SublotRequestMessageId,
            runtime.AgvId,
            session.SessionGeneration,
            new SublotEntryRequest(
                runtime.OperationSessionId,
                runtime.PickupStationId,
                runtime.WorklistRevision,
                [demand.Sublot]),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// CV-DEMAND-ACCEPT-TO-PICKUP's first snapshot: the plan as it stands once the TO_PICKUP order
    /// exists and before the vehicle is at the pickup -- the pickup leg ACTIVE, the drop-off leg
    /// PLANNED.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Published once, then left to the replay.</b> Every iteration of this stage passes through
    /// here, and publishing sends whatever is unacknowledged, so calling the publisher each time
    /// would put the same line on the wire once per poll. The replay at the top of
    /// <see cref="AdvanceAsync"/> already re-sends an unacknowledged row, which is what a lost
    /// acknowledgement needs.
    /// </para>
    /// <para>
    /// <b>Not before the order is confirmed.</b> The vector's checkpoints put the TO_PICKUP intent
    /// ahead of the RIoT call and the projection revision ahead of the send; a plan naming a leg
    /// nothing has dispatched would project a journey that is not committed.
    /// </para>
    /// <para>
    /// Until 2026-09-13 the server sent no plan before the arrival. Neither G2 noticed: this side's
    /// test compared the outbox as a sorted set of types, and the onboard side's is written against
    /// a fake server that sends the vector's order.
    /// </para>
    /// </remarks>
    private async Task PublishPickupDispatchPlanOnceAsync(
        JourneyRuntimeRow runtime,
        SessionRecoveryRow session,
        CancellationToken cancellationToken)
    {
        string messageId = PickupDispatchPlanMessageId(runtime);
        if (await dbContext.ProtocolOutbox.AsNoTracking()
                .AnyAsync(row => row.MessageId == messageId, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        await publisher.PublishUpcomingStopPlanAsync(
            messageId,
            runtime.AgvId,
            session.SessionGeneration,
            JourneyPlanBuilder.PickupDispatchPlan(runtime),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Retires a snapshot that the next revision of its stream supersedes, if it is still waiting
    /// for an acknowledgement.
    /// </summary>
    /// <remarks>
    /// The replay sends unacknowledged rows oldest first, and the vehicle refuses a snapshot below the
    /// revision it has adopted as SNAPSHOT_REVISION_REGRESSION, tearing the session down. The plan
    /// sent before the arrival is the one row that can fall behind like that without the journey
    /// having needed a message from the vehicle in between, so the pickup retires it before
    /// publishing the plan that replaces it. A snapshot states what is current, so nothing the
    /// vehicle needs is lost: it adopts the higher revision directly.
    /// </remarks>
    private async Task RetireSupersededSnapshotAsync(string messageId, CancellationToken cancellationToken)
    {
        ProtocolOutboxRow? row = await dbContext.ProtocolOutbox
            .SingleOrDefaultAsync(
                item => item.MessageId == messageId && item.AcknowledgedAt == null && item.FencedAt == null,
                cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
        {
            return;
        }

        row.FencedAt = timeProvider.GetUtcNow();
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task PublishLoadAsync(
        JourneyRuntimeRow runtime,
        SessionRecoveryRow session,
        string correlationId,
        CancellationToken cancellationToken)
    {
        AcceptedDemandRow demand = await dbContext.AcceptedDemands.SingleAsync(
            row => row.DemandId == runtime.DemandId, cancellationToken).ConfigureAwait(false);
        int[] slots = JsonSerializer.Deserialize<int[]>(runtime.TargetSlotsJson) ?? [];
        string hash = BusinessHash(runtime.DemandId, demand.Sublot, "LOAD", slots);
        // The load carries the admission only where the AREA machine is the pickup (WIRE_TO_GATE); STAGING_TO_WIRE's
        // is carried by the unload at the machine (I6 overturned, scope specification 21.2 item 2).
        bool admission = await store.AreaEndOperationAsync(runtime.DemandId, demand.WorkType, cancellationToken)
            .ConfigureAwait(false) == SlotOperationType.Load;
        await publisher.PublishSlotOperationCommandAsync(
            runtime.LoadCommandMessageId,
            runtime.AgvId,
            session.SessionGeneration,
            new SlotOperationCommand(
                correlationId,
                runtime.DemandId,
                demand.Sublot,
                runtime.OperationSessionId,
                runtime.LoadSlotOperationAttemptId,
                SlotOperationType.Load,
                slots,
                session.ForcedRecoveryGeneration,
                hash),
            cancellationToken,
            admission ? runtime.PickupStationId : null,
            admission ? demand.WorkType : null).ConfigureAwait(false);
    }

    private async Task PublishGateStateAndUnloadAsync(
        JourneyRuntimeRow runtime,
        SessionRecoveryRow session,
        CancellationToken cancellationToken)
    {
        AcceptedDemandRow demand = await dbContext.AcceptedDemands.SingleAsync(
            row => row.DemandId == runtime.DemandId, cancellationToken).ConfigureAwait(false);
        await publisher.PublishVehicleBusinessStateAsync(
            runtime.GateVehicleBusinessMessageId,
            runtime.AgvId,
            session.SessionGeneration,
            TransportBusinessState(runtime.VehicleBusinessRevision + 1, LoadingPhase(runtime.Stage, loadBatchClosed: true)),
            cancellationToken).ConfigureAwait(false);
        // The drop-off stop has no departure wait: ADR-cross-0055's wait is the pickup's.
        await publisher.PublishCurrentStopWorklistAsync(
            runtime.GateWorklistMessageId,
            runtime.AgvId,
            session.SessionGeneration,
            Worklist(
                runtime,
                demand,
                runtime.GateStationId,
                "DROPOFF",
                runtime.WorklistRevision + 1,
                stationDepartureDeadlineAt: null),
            cancellationToken).ConfigureAwait(false);
        await publisher.PublishUpcomingStopPlanAsync(
            runtime.GatePlanMessageId,
            runtime.AgvId,
            session.SessionGeneration,
            JourneyPlanBuilder.GatePlan(runtime),
            cancellationToken).ConfigureAwait(false);
        int[] slots = JsonSerializer.Deserialize<int[]>(runtime.TargetSlotsJson) ?? [];
        // The unload carries the admission where the AREA machine is the drop-off (STAGING_TO_WIRE).
        bool admission = await store.AreaEndOperationAsync(runtime.DemandId, demand.WorkType, cancellationToken)
            .ConfigureAwait(false) == SlotOperationType.Unload;
        await publisher.PublishSlotOperationCommandAsync(
            runtime.UnloadCommandMessageId,
            runtime.AgvId,
            session.SessionGeneration,
            new SlotOperationCommand(
                null,
                runtime.DemandId,
                demand.Sublot,
                runtime.OperationSessionId,
                runtime.UnloadSlotOperationAttemptId,
                SlotOperationType.Unload,
                slots,
                session.ForcedRecoveryGeneration,
                BusinessHash(runtime.DemandId, demand.Sublot, "UNLOAD", slots)),
            cancellationToken,
            admission ? runtime.GateStationId : null,
            admission ? demand.WorkType : null).ConfigureAwait(false);
    }

    /// <summary>
    /// Whether the unload may be commanded as far as station admission goes. Only a journey whose AREA machine is the
    /// drop-off (STAGING_TO_WIRE) is asked, and only until its unload is prepared: from then on the decision frozen with
    /// the unload is what stands (ADR-cross-0050/0051), so a restart that re-enters the arrival with the unload already
    /// prepared goes on to replay it rather than stopping here on the current policy.
    /// </summary>
    private async Task<bool> UnloadAdmittedAsync(JourneyRuntimeRow runtime, CancellationToken cancellationToken)
    {
        string workType = await dbContext.AcceptedDemands.AsNoTracking()
            .Where(row => row.DemandId == runtime.DemandId)
            .Select(row => row.WorkType)
            .SingleAsync(cancellationToken).ConfigureAwait(false);
        if (await store.AreaEndOperationAsync(runtime.DemandId, workType, cancellationToken).ConfigureAwait(false)
                != SlotOperationType.Unload ||
            await dbContext.StationOperations.AsNoTracking()
                .AnyAsync(row => row.SlotOperationAttemptId == runtime.UnloadSlotOperationAttemptId, cancellationToken)
                .ConfigureAwait(false))
        {
            return true;
        }
        return await store.IsTaskTypeAllowedAtAreaEndAsync(runtime, workType, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The submission answering the entry request this stop has open, or null when none does.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A submission answers one entry request, and its address says which.</b> The vehicle, the
    /// session generation, the operation session, the station and the worklist revision are the address.
    /// A submission whose address is not this stop's cannot be an answer to this request, so it is
    /// skipped rather than judged. Judging the whole inbox is what this method used to do, and it raised
    /// <c>SUBLOT_SUBMISSION_MISMATCH</c> within one poll of every arrival with nobody having scanned
    /// anything: the row that raised it was a finished journey's, another vehicle's, or this journey's
    /// own earlier round. Nothing here writes a block reason.
    /// </para>
    /// <para>
    /// <b>The read is narrowed in the store as well as here.</b> The operation session is written into
    /// the submission's own JSON, so a substring is a filter the database can apply and the inbox's
    /// history is not read back and parsed on every poll. It is a pre-filter only: the parsed comparison
    /// below is what decides, and a pre-filter that decided anything would be a bug rather than a
    /// shortcut.
    /// </para>
    /// <para>
    /// <b>A submission already refused is not read again.</b> The stored <c>SublotRejected</c> is the
    /// record that this entry was judged (<see cref="LoadCancellationBeforeSublot.RefusedSubmissionIdsAsync"/>);
    /// without skipping it every poll would re-run the remote reads and re-send the same refusal. Only
    /// the rows that answer this stop are asked about, so that lookup is bounded by this stop's entries
    /// rather than by every refusal the server has ever written.
    /// </para>
    /// <para>
    /// <b>Nothing here decides about the load.</b> Which demand the sublot belongs to, whether the station
    /// admits that demand's work type, and whether BR-013 still holds are judged by
    /// <see cref="RevalidateEnteredSublotAsync"/>, which the caller reaches only after it has read the
    /// open cancellation — so an entry that loses to a cancellation is neither loaded nor answered.
    /// </para>
    /// </remarks>
    private async Task<ProtocolInboxRow?> FindMatchingSublotAsync(
        JourneyRuntimeRow runtime,
        SessionRecoveryRow session,
        CancellationToken cancellationToken)
    {
        string operationSessionId = runtime.OperationSessionId;
        ProtocolInboxRow[] rows = await dbContext.ProtocolInbox.AsNoTracking()
            .Where(row => row.MessageType == "SublotSubmitted" && row.RequestJson.Contains(operationSessionId))
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        List<ProtocolInboxRow> answers = [];
        foreach (ProtocolInboxRow row in rows.OrderBy(row => row.ReceivedAt))
        {
            using JsonDocument document = JsonDocument.Parse(row.RequestJson);
            JsonElement root = document.RootElement;
            // The same address the cancellation before a sublot reads, plus the generation: the runtime
            // acts only on an answer of the session it is serving, while the cancellation refuses on an
            // entry of any generation (control-server#116 review).
            bool answersThisStop = root.GetProperty("sessionGeneration").GetInt64() == session.SessionGeneration &&
                                   LoadCancellationBeforeSublot.AnswersTheStop(root, runtime);
            if (answersThisStop)
            {
                answers.Add(row);
            }
        }

        // Asked only about the submissions that answer this stop: the refusal lookup is then bounded by
        // this stop's entries, not by every refusal the server has ever written.
        HashSet<string> refused = await LoadCancellationBeforeSublot
            .RefusedSubmissionIdsAsync(dbContext, [.. answers.Select(row => row.MessageId)], cancellationToken)
            .ConfigureAwait(false);
        return answers.Find(row => !refused.Contains(row.MessageId));
    }

    /// <summary>
    /// Judges the submission that answers this stop's open entry request, answering it when it may not
    /// load. Returns whether the load may proceed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The demand is resolved here, from the sublot, inside the dispatch scope.</b> Protocol 2.0.0 took
    /// the demand off <c>SublotSubmitted</c>: which demand a sublot belongs to is the server's to work
    /// out, and the scope it works it out in is the demands this journey was dispatched for — not the
    /// catalogue, which holds demands this vehicle was never given. One demand per journey makes that
    /// scope one demand's sublot; it is written as the set it becomes once a journey carries a stop
    /// sequence, because FR-001 AC-3 scopes entry to the whole range.
    /// </para>
    /// <para>
    /// <b>BR-013 section 2 makes the recomputed count authoritative after the entry.</b> The count was
    /// computed once at acceptance against the capacity table and the MES box count as they stood then;
    /// both move while the vehicle drives, and the slots were reserved against the number frozen on the
    /// journey row. A count that cannot be established, or that comes out different, stops the load: no
    /// slot is allocated and nothing is unlocked.
    /// </para>
    /// <para>
    /// <b>The one refusal that is not a message.</b> A station whose task-type admission does not allow
    /// the demand's work type is a fact about the station, not about the entry: the journey blocks under
    /// <c>TASK_TYPE_NOT_ALLOWED_AT_STATION</c> and nothing is sent, which is the carve-out
    /// <see cref="LoadCancellationBeforeSublot"/> still carries on the cancellation side.
    /// </para>
    /// <para>
    /// A refusal is saved with the journey left where it was. The stop stays the operator's: the entry is
    /// not recorded as consumed, so a rescan is judged afresh, the station deadline of
    /// <c>control-server#79</c> still runs, and a cancellation before any sublot is still available.
    /// </para>
    /// </remarks>
    private async Task<bool> RevalidateEnteredSublotAsync(
        JourneyRuntimeRow runtime,
        SessionRecoveryRow session,
        ProtocolInboxRow submission,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        using JsonDocument document = JsonDocument.Parse(submission.RequestJson);
        string enteredSublot = RequiredString(document.RootElement.GetProperty("payload"), "sublot");
        // The dispatch scope: the demands this journey was sent for.
        AcceptedDemandRow[] scope = await dbContext.AcceptedDemands.AsNoTracking()
            .Where(row => row.DemandId == runtime.DemandId)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        AcceptedDemandRow? demand = scope.SingleOrDefault(
            row => string.Equals(row.Sublot, enteredSublot, StringComparison.Ordinal));
        if (demand is null)
        {
            return await RefuseAsync(
                runtime,
                session,
                submission,
                demandId: null,
                enteredSublot,
                ServerReasonCodes.SublotNotInDispatchScope,
                $"子批 {enteredSublot} 不属于本次派车范围，不予开仓。",
                now,
                cancellationToken).ConfigureAwait(false);
        }

        // At the AREA machine station, not necessarily this one: STAGING_TO_WIRE loads at a staging station and is
        // admitted at the machine it unloads at (control-server#163). Asked here too, before anything is loaded for a
        // machine that would refuse it.
        if (!await store.IsTaskTypeAllowedAtAreaEndAsync(runtime, demand.WorkType, cancellationToken)
                .ConfigureAwait(false))
        {
            runtime.SetBlockReason("TASK_TYPE_NOT_ALLOWED_AT_STATION", now);
            runtime.UpdatedAt = now;
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        LiveMesFieldSet? fields = JsonSerializer.Deserialize<LiveMesFieldSet>(
            demand.LiveMesFieldsJson, SerializerOptions);
        if (string.IsNullOrWhiteSpace(fields?.Package))
        {
            return await RefuseAsync(
                runtime,
                session,
                submission,
                demand.DemandId,
                enteredSublot,
                ServerReasonCodes.PackageCapacityUnresolved,
                $"子批 {enteredSublot} 没有 PACKAGE 型号，算不出花篮数量，不予开仓。",
                now,
                cancellationToken).ConfigureAwait(false);
        }

        int? capacity = await packageCapacityStore.ResolveAndTrackAsync(fields.Package, now, cancellationToken)
            .ConfigureAwait(false);
        if (!AuthoritativeBasketCount.PackageCapacityIsUsable(capacity))
        {
            return await RefuseAsync(
                runtime,
                session,
                submission,
                demand.DemandId,
                enteredSublot,
                ServerReasonCodes.PackageCapacityUnresolved,
                $"PACKAGE {fields.Package} 没有已批准的花篮容量对照，算不出花篮数量，不予开仓。",
                now,
                cancellationToken).ConfigureAwait(false);
        }

        int? maxBoxCount;
        try
        {
            maxBoxCount = await boxCountReader.ReadMaxBoxCountAsync(demand.Sublot, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception error) when (error is HttpRequestException or InvalidDataException or JsonException)
        {
            LogBoxCountFailed(logger, runtime.DemandId, error);
            maxBoxCount = null;
        }

        if (!AuthoritativeBasketCount.BoxCountIsUsable(maxBoxCount))
        {
            return await RefuseAsync(
                runtime,
                session,
                submission,
                demand.DemandId,
                enteredSublot,
                ServerReasonCodes.SublotBoxCountUnavailable,
                $"查不到子批 {enteredSublot} 的箱数，算不出花篮数量，不予开仓。",
                now,
                cancellationToken).ConfigureAwait(false);
        }

        // Both inputs were checked usable just above, so a count is computable.
        int recomputed = AuthoritativeBasketCount.Compute(maxBoxCount, capacity)
            ?? throw new InvalidOperationException("Both inputs are usable, so a count is computable.");
        if (recomputed != runtime.ExpectedBasketCount)
        {
            return await RefuseAsync(
                runtime,
                session,
                submission,
                demand.DemandId,
                enteredSublot,
                ServerReasonCodes.ExpectedBasketCountMismatch,
                $"子批 {enteredSublot} 的花篮数量由 {runtime.ExpectedBasketCount} 变为 {recomputed}，"
                + "与本次派车预留的仓位不符，不予开仓。",
                now,
                cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    /// <summary>
    /// Answers one entry with a <c>SublotRejected</c> carrying the reason, and leaves the journey waiting
    /// for the next one. Always returns <see langword="false"/> — the caller's "do not load".
    /// </summary>
    /// <remarks>
    /// The refusal is keyed on the submission it answers, which is the only correlation the operator's
    /// screen can attach the reason to, and its own id is derived from that submission rather than
    /// counted, so two refusals of two submissions are two messages instead of one rewritten identity.
    /// The submission is deliberately not recorded as consumed: the stop is still the operator's to
    /// rescan, to cancel, or to run out at its deadline.
    /// </remarks>
    private async Task<bool> RefuseAsync(
        JourneyRuntimeRow runtime,
        SessionRecoveryRow session,
        ProtocolInboxRow submission,
        string? demandId,
        string enteredSublot,
        string reasonCode,
        string displayMessage,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await publisher.PublishSublotRejectedAsync(
            JourneyPlanBuilder.StableGuid(submission.MessageId, "sublot-rejected"),
            submission.MessageId,
            runtime.AgvId,
            session.SessionGeneration,
            new SublotRejection(
                demandId,
                runtime.OperationSessionId,
                new WireProblem(reasonCode, "payload.sublot", displayMessage),
                runtime.WorklistRevision,
                enteredSublot),
            cancellationToken).ConfigureAwait(false);
        runtime.UpdatedAt = now;
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        LogSublotRejected(logger, runtime.DemandId, reasonCode, null);
        return false;
    }

    /// <summary>
    /// Waits briefly for the peer's answer to the pre-departure safety check, then judges it.
    /// </summary>
    /// <remarks>
    /// The peer stamps its answer with a validity window of its own choosing, and the protocol puts
    /// no floor under that window. Its window is shorter than one poll interval, so reading the
    /// answer on the next iteration always found it expired and the journey stopped at this stage
    /// with PRE_DEPARTURE_SAFETY_NOT_VALID. Waiting here consumes the evidence while it is still
    /// valid rather than asking the peer for a longer window, which would only make the evidence
    /// staler. An answer that is present but not safe stops the wait immediately -- this shortens
    /// the gap between asking and judging, and relaxes nothing.
    /// </remarks>
    private async Task<SafetyCheckObservation?> AwaitSafeDepartureResultAsync(
        JourneyRuntimeRow runtime,
        SessionRecoveryRow session,
        CancellationToken cancellationToken)
    {
        // Bounded by attempts rather than by the clock: a test clock does not advance on its own,
        // and a wait that never ends is worse than a stage that does not advance.
        TimeSpan step = TimeSpan.FromMilliseconds(50);
        int attempts = Math.Max(1, (int)(runtimeOptions.DepartureSafetyResultWait / step));
        for (int attempt = 0; ; attempt++)
        {
            DateTimeOffset now = timeProvider.GetUtcNow();
            (SafetyCheckObservation? safety, bool invalid) = await FindSafeDepartureResultAsync(
                runtime, session, now, cancellationToken).ConfigureAwait(false);
            if (safety is not null || invalid || attempt >= attempts)
            {
                // Written once, as the wait ends. Clearing the code before every attempt and writing it
                // back, as this did before control-server#80, would restart BlockReasonSince on every
                // poll of a journey that stays PRE_DEPARTURE_SAFETY_NOT_VALID.
                runtime.SetBlockReason(invalid ? "PRE_DEPARTURE_SAFETY_NOT_VALID" : null, now);
                return safety;
            }
            await Task.Delay(step, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<(SafetyCheckObservation? Safety, bool Invalid)> FindSafeDepartureResultAsync(
        JourneyRuntimeRow runtime,
        SessionRecoveryRow session,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ProtocolInboxRow[] rows = await dbContext.ProtocolInbox.AsNoTracking()
            .Where(row => row.MessageType == "PreDepartureSafetyCheckResult")
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        rows = rows.OrderBy(row => row.ReceivedAt).ToArray();
        foreach (ProtocolInboxRow row in rows)
        {
            using JsonDocument document = JsonDocument.Parse(row.RequestJson);
            JsonElement root = document.RootElement;
            JsonElement payload = root.GetProperty("payload");
            if (RequiredString(payload, "preDepartureSafetyCheckId") != runtime.PreDepartureSafetyCheckId)
                continue;
            JsonElement safety = payload.GetProperty("safety");
            DateTimeOffset observedAt = payload.GetProperty("observedAt").GetDateTimeOffset();
            DateTimeOffset validUntil = payload.GetProperty("validUntil").GetDateTimeOffset();
            // The protocol requires a correlationId here but never says what it correlates to, and
            // its own valid example points it at neither the request's messageId nor the check id.
            // The peer correlates by the check id; we demanded the request's messageId, so a
            // perfectly good SAFE answer was refused and no departure could ever be authorized.
            // Both readings are accepted. This loses no identity: the check id already had to match
            // above, and it is unique to this journey's leg, so the correlationId was only ever a
            // second name for a fact already proven.
            string correlationId = RequiredString(root, "correlationId");
            bool valid = (correlationId == runtime.PreDepartureSafetyCheckMessageId ||
                          correlationId == runtime.PreDepartureSafetyCheckId) &&
                         RequiredString(root, "agvId") == runtime.AgvId &&
                         root.GetProperty("sessionGeneration").GetInt64() == session.SessionGeneration &&
                         RequiredString(payload, "outcome") == "SAFE" &&
                         payload.GetProperty("safetyStateVersion").GetInt64() == session.SafetyRevision &&
                         safety.GetProperty("departureSafe").GetBoolean() &&
                         safety.GetProperty("vehicleStopped").GetBoolean() &&
                         safety.GetProperty("allTargetSlotsLocked").GetBoolean() &&
                         safety.GetProperty("allUnlockOutputsReset").GetBoolean() &&
                         !safety.GetProperty("unknownPresent").GetBoolean() &&
                         observedAt <= now && validUntil >= now &&
                         now - observedAt <= runtimeOptions.MaximumEvidenceAge;
            if (!valid)
            {
                return (null, true);
            }
            if (session.SafetyRevision is not long safetyRevision)
            {
                return (null, false);
            }
            return (new SafetyCheckObservation(
                runtime.PreDepartureSafetyCheckId,
                safetyRevision,
                true,
                observedAt,
                validUntil), false);
        }
        return (null, false);
    }

    /// <summary>
    /// One vehicle's Onboard-side facts, read against that vehicle's own session.
    /// </summary>
    /// <remarks>
    /// The <c>agvId</c> is a parameter rather than the configured one because every read below is
    /// session-scoped: the session row, the two snapshots and the last inbound message all belong
    /// to one vehicle's session generation. Reading them for the configured vehicle while deciding
    /// for another would admit a vehicle on a different vehicle's safety evidence.
    /// </remarks>
    private async Task<OnboardDispatchFacts?> ReadOnboardFactsAsync(
        string agvId,
        CancellationToken cancellationToken)
    {
        SessionRecoveryRow? session = await CurrentReadySessionAsync(agvId, cancellationToken)
            .ConfigureAwait(false);
        if (session is null)
        {
            return null;
        }
        if (session.SafetyRevision is not long safetyRevision)
        {
            return null;
        }
        ProtocolInboxRow? capability = await LatestInboxForSessionAsync(
            "CapabilitySnapshot", agvId, session.SessionGeneration, cancellationToken)
            .ConfigureAwait(false);
        // The session's baseline SafetyStateSnapshot, not its latest: slot availability is the session baseline
        // (see below), and since control-server#142 a session can carry later snapshots -- the vehicle's answer
        // when the dashboard asks for an overdue slot's readings, which reads a slot mid-operation as occupied
        // or unlocked. Their safety summary still counts, through LatestSafetySummaryForSessionAsync.
        ProtocolInboxRow? safetyRow = await BaselineSafetySnapshotForSessionAsync(
            agvId, session.SessionGeneration, cancellationToken)
            .ConfigureAwait(false);
        // The snapshot is sent once per session; every later change arrives as SafetyStateChanged
        // (ADR-cross-0033), which carries the same safety summary and no slotStates. Reading the
        // summary from the snapshot alone froze it at whatever was true when the session was
        // established: a session opened while the vehicle was moving reported vehicleStopped false
        // for its whole life, so the vehicle could stop at the pickup station and neither be
        // admitted nor have its arrival trusted -- while SessionRecoveries, which SafetyStateChanged
        // does update, correctly showed Ready. The reverse is worse: a session opened at rest went
        // on reporting the vehicle stopped after Onboard said it had moved.
        ProtocolInboxRow? safetySummaryRow = await LatestSafetySummaryForSessionAsync(
            agvId, session.SessionGeneration, safetyRevision, cancellationToken)
            .ConfigureAwait(false);
        if (capability is null || safetyRow is null || safetySummaryRow is null)
        {
            return null;
        }
        using JsonDocument capabilityDocument = JsonDocument.Parse(capability.RequestJson);
        using JsonDocument safetyDocument = JsonDocument.Parse(safetyRow.RequestJson);
        using JsonDocument safetySummaryDocument = JsonDocument.Parse(safetySummaryRow.RequestJson);
        JsonElement capabilityPayload = capabilityDocument.RootElement.GetProperty("payload");
        JsonElement safetyPayload = safetyDocument.RootElement.GetProperty("payload");
        JsonElement safetySummaryPayload = safetySummaryDocument.RootElement.GetProperty("payload");
        // The two snapshots are session-scoped facts, not polled evidence: the protocol states no
        // cadence for them and Onboard sends each once per session, so ageing them out would cap
        // every session's admission window at MaximumEvidenceAge. What must still be bounded is
        // session *liveness* -- a dead peer leaves a Ready row and its last snapshots behind, and
        // CurrentReadySessionAsync has no liveness component of its own. So the age limit applies
        // to the last thing we heard from this session generation (Heartbeat arrives periodically,
        // and any inbound message counts, which also covers the window before the first one).
        // Content stays current through the session: everything read here is scoped to this exact
        // SessionGeneration, a new generation supersedes it, and the safety summary is taken from
        // the message carrying the session's current safetyStateVersion rather than from the
        // session-start snapshot. MaximumEvidenceAge still separately governs the RIoT vehicle
        // observation in ValidateDynamicFacts, which genuinely is polled.
        //
        // supportsBatchUnlock is deliberately not consulted: the protocol declares it with no
        // semantics -- a bare boolean in CapabilitySnapshot, unchanged from protocol-v0.1.1 through
        // the v2 candidate -- and its own canonical example sets it false, while the real question,
        // can the vehicle operate this slot set, is answered against AvailableSlots when the command
        // is actually sent. See docs/defects/20260829-intake-gates-on-unspecified-onboard-facts.md.
        DateTimeOffset now = timeProvider.GetUtcNow();
        DateTimeOffset capabilityAt = capabilityPayload.GetProperty("observedAt").GetDateTimeOffset();
        DateTimeOffset safetyAt = safetySummaryPayload.GetProperty("observedAt").GetDateTimeOffset();
        if (capabilityAt > now || safetyAt > now)
        {
            return null;
        }
        DateTimeOffset? lastInboundAt = await LatestInboundAtForSessionAsync(
            agvId, session.SessionGeneration, cancellationToken).ConfigureAwait(false);
        if (lastInboundAt is null || lastInboundAt > now ||
            now - lastInboundAt.Value > runtimeOptions.MaximumEvidenceAge)
        {
            return null;
        }
        Dictionary<int, JsonElement> safetySlots = safetyPayload.GetProperty("slotStates")
            .EnumerateArray().ToDictionary(item => item.GetProperty("slotNo").GetInt32());
        int[] available = capabilityPayload.GetProperty("slotStates").EnumerateArray()
            .Where(item => SlotAvailable(item, safetySlots))
            .Select(item => item.GetProperty("slotNo").GetInt32())
            .Order()
            .ToArray();
        // slotStates live only on the snapshot; SafetyStateChanged names the slots it affects but
        // not their new state. Availability therefore stays on the session baseline, which is what
        // the slot reservation ledger is built against -- a load and its unload each "affect" the
        // slots they touch, and treating that as lost availability would strand every slot the
        // first journey used for the rest of the session.
        JsonElement safety = safetySummaryPayload.GetProperty("safety");
        return new OnboardDispatchFacts(
            session.SessionGeneration,
            available,
            safety.GetProperty("departureSafe").GetBoolean(),
            safety.GetProperty("vehicleStopped").GetBoolean(),
            safety.GetProperty("allTargetSlotsLocked").GetBoolean(),
            safety.GetProperty("allUnlockOutputsReset").GetBoolean(),
            safety.GetProperty("unknownPresent").GetBoolean());
    }

    private static bool SlotAvailable(JsonElement capability, Dictionary<int, JsonElement> safetySlots)
    {
        int slot = capability.GetProperty("slotNo").GetInt32();
        if (!safetySlots.TryGetValue(slot, out JsonElement safety)) return false;
        return RequiredString(capability, "operability") == "OPERABLE" &&
               RequiredString(capability, "administrativeAvailability") == "ENABLED" &&
               RequiredString(capability, "physicalState") == "EMPTY" &&
               RequiredString(capability, "lockState") == "LOCKED" &&
               RequiredString(capability, "unlockOutputState") == "RESET" &&
               RequiredString(safety, "physicalState") == "EMPTY" &&
               RequiredString(safety, "lockState") == "LOCKED" &&
               RequiredString(safety, "unlockOutputState") == "RESET";
    }

    private async Task<ProtocolInboxRow?> LatestInboxForSessionAsync(
        string messageType,
        string agvId,
        long generation,
        CancellationToken cancellationToken)
    {
        ProtocolInboxRow[] rows = await dbContext.ProtocolInbox.AsNoTracking()
            .Where(row => row.MessageType == messageType)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        rows = rows.OrderByDescending(row => row.ReceivedAt).ToArray();
        return rows.FirstOrDefault(row =>
        {
            using JsonDocument document = JsonDocument.Parse(row.RequestJson);
            JsonElement root = document.RootElement;
            return RequiredString(root, "agvId") == agvId &&
                   root.GetProperty("sessionGeneration").GetInt64() == generation;
        });
    }

    /// <summary>
    /// The session's baseline SafetyStateSnapshot: the one with this generation's lowest
    /// <c>safetyStateVersion</c>, which is the handshake's. Ordered by version rather than receive time
    /// for the reason LatestSafetySummaryForSessionAsync is: Onboard allocates the version under one lock,
    /// so it orders the vehicle's safety facts, while the server's receive clock can be stepped back
    /// between the handshake snapshot and a later answer to SafetyStateSnapshotRequested (control-server#142).
    /// </summary>
    private async Task<ProtocolInboxRow?> BaselineSafetySnapshotForSessionAsync(
        string agvId,
        long generation,
        CancellationToken cancellationToken)
    {
        ProtocolInboxRow[] rows = await dbContext.ProtocolInbox.AsNoTracking()
            .Where(row => row.MessageType == "SafetyStateSnapshot")
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        ProtocolInboxRow? baseline = null;
        long baselineVersion = long.MaxValue;
        foreach (ProtocolInboxRow row in rows)
        {
            using JsonDocument document = JsonDocument.Parse(row.RequestJson);
            JsonElement root = document.RootElement;
            if (RequiredString(root, "agvId") != agvId ||
                root.GetProperty("sessionGeneration").GetInt64() != generation)
            {
                continue;
            }
            long version = root.GetProperty("payload").GetProperty("safetyStateVersion").GetInt64();
            if (version < baselineVersion)
            {
                baseline = row;
                baselineVersion = version;
            }
        }
        return baseline;
    }

    /// <summary>
    /// The abstract safety summary Onboard currently stands behind, taken from whichever message
    /// carries the session's own <c>safetyStateVersion</c>. SafetyStateSnapshot and
    /// SafetyStateChanged both carry that summary and both advance the revision on the session row
    /// in the same transaction that stores the envelope, so matching on the revision picks the
    /// newest one without trusting either peer clock or receive order. No match means the session
    /// row and the inbox disagree, which proves nothing and fails closed.
    /// </summary>
    private async Task<ProtocolInboxRow?> LatestSafetySummaryForSessionAsync(
        string agvId,
        long generation,
        long safetyRevision,
        CancellationToken cancellationToken)
    {
        ProtocolInboxRow[] rows = await dbContext.ProtocolInbox.AsNoTracking()
            .Where(row => row.MessageType == "SafetyStateSnapshot" || row.MessageType == "SafetyStateChanged")
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        rows = rows.OrderByDescending(row => row.ReceivedAt).ToArray();
        return rows.FirstOrDefault(row =>
        {
            using JsonDocument document = JsonDocument.Parse(row.RequestJson);
            JsonElement root = document.RootElement;
            return RequiredString(root, "agvId") == agvId &&
                   root.GetProperty("sessionGeneration").GetInt64() == generation &&
                   root.GetProperty("payload").GetProperty("safetyStateVersion").GetInt64() == safetyRevision;
        });
    }

    /// <summary>
    /// Server-observed liveness of one session generation: when any inbound message from it was
    /// last received. Uses the receive time rather than a payload timestamp so a stopped or
    /// misconfigured peer clock cannot make a dead session look alive.
    /// </summary>
    private async Task<DateTimeOffset?> LatestInboundAtForSessionAsync(
        string agvId,
        long generation,
        CancellationToken cancellationToken)
    {
        ProtocolInboxRow[] rows = await dbContext.ProtocolInbox.AsNoTracking()
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        DateTimeOffset? latest = null;
        foreach (ProtocolInboxRow row in rows)
        {
            using JsonDocument document = JsonDocument.Parse(row.RequestJson);
            JsonElement root = document.RootElement;
            // This scans every inbound message, so unlike the per-type readers it meets envelopes
            // that carry no generation yet (or none at all). Those prove nothing about this
            // session's liveness and are skipped rather than throwing.
            if (!root.TryGetProperty("agvId", out JsonElement agv) ||
                agv.ValueKind != JsonValueKind.String ||
                agv.GetString() != agvId ||
                !root.TryGetProperty("sessionGeneration", out JsonElement sessionGeneration) ||
                sessionGeneration.ValueKind != JsonValueKind.Number ||
                sessionGeneration.GetInt64() != generation)
            {
                continue;
            }
            if (latest is null || row.ReceivedAt > latest.Value)
            {
                latest = row.ReceivedAt;
            }
        }
        return latest;
    }

    /// <summary>
    /// The vehicle's session row as the database holds it right now.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>AsNoTracking is the point of this method, not a performance note.</b> The engine only
    /// ever reads this row -- Onboard's transport owns every write to it, on its own scope and its
    /// own <see cref="ControlServerDbContext"/>. A tracking query returns the instance the change
    /// tracker already holds and leaves its values alone, so the first read in an iteration pinned
    /// the row for the whole iteration: a SafetyStateChanged that landed while the iteration was
    /// running was invisible to every later read of it, however long the iteration then ran
    /// against RIoT.
    /// </para>
    /// <para>
    /// That is not merely stale, it is unsafe, because
    /// <see cref="ReadOnboardFactsAsync"/> pins the safety summary to
    /// <c>SafetyRevision</c>: a stale revision does not fail to match, it matches the *previous*
    /// message, which is still in the inbox and still says whatever was true before. The arrival
    /// check then read a vehicle Onboard had already reported moving as stopped, and trusted an
    /// arrival that had not happened. See
    /// docs/defects/20260916-arrival-trusted-on-a-session-row-pinned-for-one-iteration.md.
    /// </para>
    /// </remarks>
    private Task<SessionRecoveryRow?> CurrentReadySessionAsync(string agvId, CancellationToken cancellationToken) =>
        dbContext.SessionRecoveries.AsNoTracking().SingleOrDefaultAsync(
            row => row.AgvId == agvId && row.Readiness == SessionReadiness.Ready,
            cancellationToken);

    /// <summary>
    /// Marks every unaccepted backlog row whose demand the catalog no longer lists, so it stops reading as waiting.
    /// </summary>
    /// <remarks>
    /// Rows are only ever written for demands in the catalog, so without this a demand MES closed before this
    /// server took it kept its last reason forever. Saved here, ahead of the vehicle loop, because a vehicle that
    /// runs out its budget clears the change tracker. <c>LastSeenAt</c> is left alone: it stays the last time the
    /// demand was in the catalog. See <see cref="DispatchReasonCodes.DemandLeftCatalog"/>.
    /// </remarks>
    private async Task MarkBacklogLeftCatalogAsync(
        Dictionary<string, JourneyBacklogRow> backlogByDemandId,
        DemandCatalogSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        HashSet<string> listed = snapshot.Items.Select(item => item.DemandId).ToHashSet(StringComparer.Ordinal);
        bool marked = false;
        foreach (JourneyBacklogRow row in backlogByDemandId.Values)
        {
            if (row.AcceptedAt is null &&
                !listed.Contains(row.DemandId) &&
                !string.Equals(row.ReasonCode, DispatchReasonCodes.DemandLeftCatalog, StringComparison.Ordinal))
            {
                row.ReasonCode = DispatchReasonCodes.DemandLeftCatalog;
                marked = true;
            }
        }
        if (marked)
        {
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private JourneyBacklogRow UpsertBacklog(
        Dictionary<string, JourneyBacklogRow> backlogByDemandId,
        AcceptedDemandSnapshot candidate,
        string reason,
        DateTimeOffset now)
    {
        string fingerprint = DecisionFingerprint(candidate);
        if (!backlogByDemandId.TryGetValue(candidate.DemandId, out JourneyBacklogRow? row))
        {
            row = new JourneyBacklogRow
            {
                DemandId = candidate.DemandId,
                TransportDemandKey = candidate.TransportDemandKey,
                FirstSeenAt = now,
                DemandCreatedAt = candidate.CreatedAt,
                DecisionFingerprint = fingerprint,
                ReasonCode = reason,
                LastSeenAt = now
            };
            dbContext.JourneyBacklog.Add(row);
            backlogByDemandId.Add(candidate.DemandId, row);
        }
        else
        {
            bool decisionFactsChanged = row.TransportDemandKey != candidate.TransportDemandKey ||
                                        row.DecisionFingerprint != fingerprint;
            row.TransportDemandKey = candidate.TransportDemandKey;
            row.DemandCreatedAt = candidate.CreatedAt;
            row.DecisionFingerprint = fingerprint;
            row.ReasonCode = decisionFactsChanged ? "DEMAND_DECISION_FACT_CHANGED" : reason;
            row.LastSeenAt = now;
        }
        return row;
    }

    private static string DecisionFingerprint(AcceptedDemandSnapshot candidate)
    {
        byte[] content = JsonSerializer.SerializeToUtf8Bytes(new
        {
            candidate.DemandId,
            candidate.HistoryEpoch,
            candidate.SeriesId,
            candidate.TransportDemandKey,
            candidate.WorkType,
            candidate.Sublot,
            candidate.Generation,
            candidate.DemandRevision,
            candidate.CreatedAt,
            candidate.ValueObservedAt,
            candidate.ValuePollTraceId,
            candidate.ValueProjectionCommitId,
            candidate.LiveMesFields
        }, SerializerOptions);
        return Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
    }

    private async Task SetBacklogReasonAsync(
        string demandId,
        string reason,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        JourneyBacklogRow row = await dbContext.JourneyBacklog.SingleAsync(
            item => item.DemandId == demandId, cancellationToken).ConfigureAwait(false);
        row.ReasonCode = reason;
        row.LastSeenAt = now;
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Re-runs the pre-create gate for the gate-bound move order, against the endpoint this demand
    /// froze rather than a freshly resolved one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// REQ-0305 asks for exactly three things before a move order that does not exist yet:
    /// the catalog must be usable, the frozen <c>mapId + stationId</c> must still be in it, and
    /// RouteCost must pass. REQ-0345 adds a fourth, asked before RIoT is: the demand's frozen
    /// <c>Map + TASK_TYPE</c> must not be held. When any of them fails the new action is blocked and the reason
    /// recorded precisely — never resolved to another station, another Map, or a similar name.
    /// </para>
    /// <para>
    /// The route graph is not consulted here, so this leg has one evidence source rather than two.
    /// That is not a weaker gate, it is a narrower question: the graph's contribution at selection
    /// time was to compare candidate stations against each other, and there is nothing to compare
    /// here — the destination was fixed when the demand was taken.
    /// </para>
    /// </remarks>
    private async Task<CreateGateOutcome> GateLegAsync(
        JourneyRuntimeRow runtime,
        RiotMapStationCatalogSnapshot currentMap,
        CancellationToken cancellationToken)
    {
        CatalogAvailability availability = await catalogAvailability
            .ReadAsync(runtime.MapId, cancellationToken).ConfigureAwait(false);
        if (!availability.IsUsable)
        {
            return new CreateGateOutcome(
                CreateGateVerdict.BlockedCatalogNotFresh, availability.BlockReason, null);
        }

        // REQ-0344's last sentence and REQ-0345: a move order that does not exist yet waits while the demand's frozen
        // Map + TASK_TYPE is held, whatever raised the hold. An order already created is not touched -- this is only
        // ever asked before the gate leg is created. A journey accepted before control-server#160 froze no versions;
        // it is judged as the WIRE_TO_GATE on the runtime's Map it was, the way the dropoff below falls back.
        DemandTaskTypeStationFreeze? frozenVersions = await _taskTypeStations.Freezes
            .ReadAsync(runtime.DemandId, cancellationToken).ConfigureAwait(false);
        string taskType = frozenVersions is null
            ? TransportTaskTypes.WireToGate
            : await dbContext.AcceptedDemands
                .Where(row => row.DemandId == runtime.DemandId)
                .Select(row => row.WorkType)
                .SingleAsync(cancellationToken)
                .ConfigureAwait(false);
        TaskTypeHolds holds = await _taskTypeStations
            .ReadHoldsAsync(frozenVersions?.MapId ?? runtime.MapId, cancellationToken).ConfigureAwait(false);
        if (holds.Holds(taskType))
        {
            return new CreateGateOutcome(CreateGateVerdict.BlockedTaskTypeHeld, DispatchReasonCodes.TaskTypeHeld, null);
        }

        IReadOnlyList<FrozenStationFact> frozen = await catalogStore
            .ReadFrozenStationsAsync(runtime.DemandId, cancellationToken).ConfigureAwait(false);
        FrozenStationFact? dropoff = frozen
            .FirstOrDefault(station => station.Role == FrozenStationRole.Dropoff);

        // A journey created before this gate existed has no frozen row. Falling back to the
        // runtime's own gate station keeps that journey moving under the same check rather than
        // blocking it on a record it never had a chance to write.
        int mapId = dropoff?.MapId ?? runtime.MapId;
        int stationId = dropoff?.StationId ?? runtime.GateStationRiotId;
        string transportDemandKey = await dbContext.AcceptedDemands
            .Where(row => row.DemandId == runtime.DemandId)
            .Select(row => row.TransportDemandKey)
            .SingleAsync(cancellationToken)
            .ConfigureAwait(false);

        return await createGate.EvaluateAsync(
            new CreateGateRequest(
                runtime.DemandId,
                transportDemandKey,
                runtime.AgvId,
                runtime.VehicleKey,
                mapId,
                stationId,
                GraphTraversalCostMm: null,
                TargetStationInCurrentCatalog: currentMap.MapId == mapId &&
                    currentMap.Stations.Any(station => station.StationId == stationId)),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The worklist for one stop. The revision is explicit because the gate stop is a different
    /// worklist from the pickup stop -- different station, different role -- and the peer keys a
    /// snapshot's identity on its type and revision. Publishing both at the same revision made the
    /// peer reject the second as a revision whose content had changed, which was right of it: a
    /// revision that does not advance is a promise that the content did not. The connection died on
    /// that rejection, and the unload command queued behind it was never reached, so the gate stage
    /// could not start. The sibling projections at this stop already advance the same way.
    /// </summary>
    private static CurrentStopWorklistProjection Worklist(
        JourneyRuntimeRow runtime,
        AcceptedDemandRow demand,
        string station,
        string role,
        long revision,
        DateTimeOffset? stationDepartureDeadlineAt) => new(
            station,
            revision,
            runtime.OperationSessionId,
            stationDepartureDeadlineAt,
            [new CurrentStopWorklistItem(
                demand.DemandId,
                demand.TransportDemandKey,
                demand.Sublot,
                demand.WorkType,
                role,
                runtime.ExpectedBasketCount)]);

    // Likewise the only activePurpose this runtime can be in. CHARGING is batch 8, IDLE_RETURN is
    // batch 5, CLEARING_MAINTENANCE is deferred; a vehicle running this worker is carrying a demand.
    private const string TransportPurpose = VehicleActivePurposes.Transport;

    // 8005-agv-program#94's semantic table: v2 has no automatic charging today (scope specification
    // 5.5), so this server holds no charger reservation, no charging order and no charging cycle.
    // "Not in a charging cycle" is a fact it knows, not a guess; UNKNOWN would report a missing
    // feature as a lost observation. Nor is MANDATORY_CHARGE sent before batch 9.
    private const string NotInAChargingCycle = "NOT_CHARGING";

    private static VehicleBusinessProjection TransportBusinessState(long revision, LoadingPhaseProjection loadingPhase) =>
        new(revision, "READY", TransportPurpose, false, "SUFFICIENT", NotInAChargingCycle, loadingPhase, []);

    /// <summary>
    /// The loading phase a transport journey reports at a given stage -- the one place that mapping is
    /// made, never null for a journey that exists.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The semantics are <c>8005-agv-program</c> commit <c>db5a1d14</c> (<c>8005-agv-program#94</c>):
    /// <c>LOADING</c> until the pickup's load batch closes safely, then <c>CLOSED</c> with
    /// <c>PLANNED_LOADING_COMPLETE</c> until the journey ends. With one demand per journey there is no
    /// cargo holding wait, so the holding deadline is always null.
    /// </para>
    /// <para>
    /// The stage alone decides it everywhere but <see cref="JourneyRuntimeStage.Blocked"/> and
    /// <see cref="JourneyRuntimeStage.Completed"/>, which a journey reaches from either side of the
    /// load; there <paramref name="loadBatchClosed"/> decides. <see cref="JourneyRuntimeStage.AwaitingStationDeparture"/>
    /// is already closed: the load has committed, and a correction there handles what was already
    /// loaded rather than admitting another demand.
    /// </para>
    /// <para>
    /// <b>Sent at the two points this runtime already publishes the snapshot</b>: the pickup arrival
    /// (<c>LOADING</c>) and the drop-off arrival (<c>CLOSED</c>). #94 left open whether to publish once
    /// more when the vehicle leaves the pickup, and this server does not: an extra snapshot shifts the
    /// revision stream the synthetic peer and the G3 runners assert against, for a value nothing acts on
    /// before batch 7 gives the phase its display.
    /// </para>
    /// </remarks>
    public static LoadingPhaseProjection LoadingPhase(JourneyRuntimeStage stage, bool loadBatchClosed) => stage switch
    {
        JourneyRuntimeStage.AwaitingPickupArrival or
        JourneyRuntimeStage.AwaitingSublot or
        JourneyRuntimeStage.AwaitingLoadResult => LoadingPhaseProjection.Loading,
        JourneyRuntimeStage.AwaitingStationDeparture or
        JourneyRuntimeStage.AwaitingDepartureSafety or
        JourneyRuntimeStage.AwaitingGateArrival or
        JourneyRuntimeStage.AwaitingUnloadResult => LoadingPhaseProjection.PlannedLoadingComplete,
        JourneyRuntimeStage.Blocked or
        JourneyRuntimeStage.Completed => loadBatchClosed
            ? LoadingPhaseProjection.PlannedLoadingComplete
            : LoadingPhaseProjection.Loading,
        _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, "Journey stage has no loading phase mapping.")
    };

    private static string BusinessHash(string demandId, string sublot, string operation, IEnumerable<int> slots) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{demandId}|{sublot}|{operation}|{string.Join(',', slots)}"))).ToLowerInvariant();

    // Derived rather than stored: the row predates this snapshot, and a deterministic id from the
    // demand is what the stored ones are anyway (WireToGateStore.ToRuntimeRow), without a migration.
    private static string PickupDispatchPlanMessageId(JourneyRuntimeRow runtime) =>
        JourneyPlanBuilder.StableGuid(runtime.DemandId, "pickup-dispatch-plan");

    private static HashSet<string> RuntimeMessageIds(JourneyRuntimeRow runtime) =>
    [
        runtime.VehicleBusinessMessageId,
        runtime.WorklistMessageId,
        PickupDispatchPlanMessageId(runtime),
        runtime.PlanMessageId,
        runtime.SublotRequestMessageId,
        runtime.LoadCommandMessageId,
        runtime.PreDepartureSafetyCheckMessageId,
        runtime.GateVehicleBusinessMessageId,
        runtime.GateWorklistMessageId,
        runtime.GatePlanMessageId,
        runtime.UnloadCommandMessageId
    ];

    private static string RequiredString(JsonElement element, string propertyName) =>
        element.GetProperty(propertyName).GetString()
        ?? throw new InvalidDataException($"Protocol field '{propertyName}' is required.");

    private static void SetStage(JourneyRuntimeRow runtime, JourneyRuntimeStage stage, DateTimeOffset now)
    {
        runtime.Stage = stage;
        runtime.SetBlockReason(null, now);
        runtime.UpdatedAt = now;
    }

    /// <summary>
    /// The two block transitions that need nothing from the peer: a station operation the server
    /// has already judged <see cref="StationOperationStatus.RecoveryRequired"/> blocks the journey
    /// under the reason code naming which recovery is outstanding. Every other transition in
    /// <c>AdvanceAsync</c> publishes to the vehicle or reads its revisions, so those stay behind
    /// the readiness gate.
    /// </summary>
    private async Task<bool> TryBlockOnRecordedRecoveryAsync(
        JourneyRuntimeRow runtime,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        (string? attemptId, string reason) = runtime.Stage switch
        {
            JourneyRuntimeStage.AwaitingLoadResult =>
                (runtime.LoadSlotOperationAttemptId, "LOAD_RESULT_REQUIRES_RECOVERY"),
            JourneyRuntimeStage.AwaitingUnloadResult =>
                (runtime.UnloadSlotOperationAttemptId, "UNLOAD_RESULT_REQUIRES_RECOVERY"),
            _ => (null, string.Empty)
        };
        if (attemptId is null)
        {
            return false;
        }

        StationOperationRow? operation = await dbContext.StationOperations.SingleOrDefaultAsync(
            row => row.SlotOperationAttemptId == attemptId,
            cancellationToken).ConfigureAwait(false);
        if (operation?.Status != StationOperationStatus.RecoveryRequired)
        {
            return false;
        }

        Block(runtime, reason, now);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static bool IsHeldForAreaEndAdmission(JourneyRuntimeRow runtime) =>
        runtime.Stage == JourneyRuntimeStage.AwaitingGateArrival &&
        string.Equals(runtime.BlockReasonCode, "TASK_TYPE_NOT_ALLOWED_AT_STATION", StringComparison.Ordinal);

    private static void Block(JourneyRuntimeRow runtime, string reason, DateTimeOffset now)
    {
        runtime.Stage = JourneyRuntimeStage.Blocked;
        runtime.SetBlockReason(reason, now);
        runtime.UpdatedAt = now;
    }

    private async Task<string?> FindSafetyResultMessageIdAsync(
        string checkId,
        CancellationToken cancellationToken)
    {
        ProtocolInboxRow[] rows = await dbContext.ProtocolInbox.AsNoTracking()
            .Where(row => row.MessageType == "PreDepartureSafetyCheckResult")
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        return rows.FirstOrDefault(row =>
        {
            using JsonDocument document = JsonDocument.Parse(row.RequestJson);
            return RequiredString(document.RootElement.GetProperty("payload"), "preDepartureSafetyCheckId") == checkId;
        })?.MessageId;
    }

    /// <summary>
    /// Whether the station departure wait (ADR-cross-0055) has run out for this journey.
    /// </summary>
    /// <remarks>
    /// An open load correction holds the vehicle whatever the clock says, and one that has closed
    /// since the wait began starts the wait over from its full length (ADR-cross-0054) -- measured
    /// from when this server recorded it closed, not from the peer's clock. Updates the runtime's
    /// wait start and block reason; the caller saves.
    /// </remarks>
    private async Task<bool> StationDepartureWaitIsOverAsync(
        JourneyRuntimeRow runtime,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        RecoveryWorkflowRow[] corrections = await dbContext.RecoveryWorkflows.AsNoTracking()
            .Where(row => row.DemandId == runtime.DemandId && row.WorkflowType == "LOAD_CORRECTION")
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        if (corrections.Any(row => row.State is not
                (RecoveryWorkflowState.Reconciled or RecoveryWorkflowState.HistoricalOnly)))
        {
            if (runtime.BlockReasonCode != "LOAD_CORRECTION_IN_PROGRESS")
            {
                runtime.SetBlockReason("LOAD_CORRECTION_IN_PROGRESS", now);
                runtime.UpdatedAt = now;
            }
            return false;
        }

        DateTimeOffset startedAt = runtime.StationDepartureWaitStartedAt ?? now;
        foreach (RecoveryWorkflowRow closed in corrections.Where(row => row.State == RecoveryWorkflowState.Reconciled))
        {
            if (closed.UpdatedAt > startedAt) startedAt = closed.UpdatedAt;
        }
        if (runtime.StationDepartureWaitStartedAt != startedAt || runtime.BlockReasonCode is not null)
        {
            runtime.StationDepartureWaitStartedAt = startedAt;
            runtime.SetBlockReason(null, now);
            runtime.UpdatedAt = now;
        }
        // No deadline here means the wait is off: the vehicle leaves in the iteration its load commits.
        return StationDepartureDeadline(runtime, runtimeOptions.StationDepartureWaitTimeout) is not { } deadline ||
               now >= deadline;
    }

    /// <summary>
    /// When this journey's station departure wait (ADR-cross-0055) runs out, or null when there is no
    /// such wait to express: the timeout is off, or the wait has not started.
    /// </summary>
    /// <remarks>
    /// The single source of the deadline. The runtime judges the pickup timeout and the departure from
    /// it, and the worklist sends it to the vehicle as <c>stationDepartureDeadlineAt</c>
    /// (control-server#84), so the two can never disagree about when the stop ends. A null is not a
    /// deadline of "now": publishing one the runtime never acts on would be a countdown that expires
    /// into silence.
    /// </remarks>
    public static DateTimeOffset? StationDepartureDeadline(
        JourneyRuntimeRow runtime,
        TimeSpan stationDepartureWaitTimeout) =>
        stationDepartureWaitTimeout <= TimeSpan.Zero || runtime.StationDepartureWaitStartedAt is not { } startedAt
            ? null
            : startedAt + stationDepartureWaitTimeout;

    /// <summary>
    /// Ends the pickup stop once its station departure deadline has passed with no sublot entered
    /// (ADR-cross-0055; ADR-cross-0058 decision 7 redone for the one-demand journey). Called only from
    /// <see cref="JourneyRuntimeStage.AwaitingSublot"/> in an iteration that found no matching entry.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Until batch 5 that stage had no deadline and no exit: an unscanned stop was asked again on
    /// every poll and held the pickup until someone edited the database. The runtime may end it alone
    /// because no slot operation has been commanded in this stage -- a load command moves the journey
    /// to AwaitingLoadResult in the iteration that sends it -- so there is no physical state that only
    /// the peer could settle.
    /// </para>
    /// <para>
    /// <b>Exclusive with starting the load.</b> Both are decided by this one iteration for this one
    /// journey: an entry already durable when the iteration reads the inbox starts the load and this
    /// is never reached; once this has committed the journey is Completed and a later entry is never
    /// read. Whichever is persisted first wins (ADR-cross-0055).
    /// </para>
    /// <para>
    /// The demand stays out of dispatch because its DemandId is already accepted
    /// (AlreadyAcceptedCriterion). Suppressing the business key permanently, so that a new DemandId
    /// for the same sublot is not dispatched either, is REQ-0155/0156/0211 in batch 7.
    /// </para>
    /// </remarks>
    private async Task<bool> TryEndStopAtStationDeadlineAsync(
        JourneyRuntimeRow runtime,
        SessionRecoveryRow session,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (StationDepartureDeadline(runtime, runtimeOptions.StationDepartureWaitTimeout) is not { } deadline ||
            now < deadline)
        {
            return false;
        }
        // ADR-cross-0058 decision 4: ending the stop lets the vehicle be sent on, and it must not move
        // with a door open (ADR-cross-0011/0012). So the stop keeps waiting past its deadline until the
        // door is shut, then ends on that iteration's evidence. The alarm is raised only in
        // AwaitingLoadResult (ReconcileStationTimeoutDoorNotClosed): here no slot has been commanded, so an
        // open door is nothing our command explains and the session leaves Ready first
        // (ADR-cross-0058 Verification, decision 4).
        if (SlotDoorsNotProvenClosed(session))
        {
            return false;
        }
        // ADR-cross-0055 voids the countdown for a disconnect, and FR-031 AC-9 forbids cancelling or
        // departing while the vehicle is off air. A dropped link does not move the session off Ready by
        // itself -- nothing calls RecordConnectionLossAsync -- so readiness alone would let the stop end
        // against evidence the vehicle sent before it vanished, cancelling a demand nobody could have
        // scanned. Liveness is the measure the dispatch facts already use: the last inbound of this
        // generation, within MaximumEvidenceAge. Coming back on the same generation is not a reconnect,
        // so a deadline that passed meanwhile still stands; a real reconnect voids it at the handshake.
        DateTimeOffset? lastInboundAt = await LatestInboundAtForSessionAsync(
            runtime.AgvId, session.SessionGeneration, cancellationToken).ConfigureAwait(false);
        if (lastInboundAt is null || now - lastInboundAt.Value > runtimeOptions.MaximumEvidenceAge)
        {
            return false;
        }

        // Decided again under the write lock (control-server#83). The operator's cancellation before a sublot
        // is authorized on another connection's scope, and everything this iteration read about the stop was
        // read without a lock: a cancellation authorized -- or authorized and already settled -- since then
        // would otherwise lose its stop to this one, and its result would find a demand ended for a reason
        // that is not the operator's. BeginTransaction on this store is BEGIN IMMEDIATE, so the reads below
        // see every write committed before it, and the authorization, running inside the inbox's own write
        // transaction, sees this one once it commits.
        await using IDbContextTransaction? transaction = dbContext.Database.CurrentTransaction is null
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false)
            : null;
        JourneyRuntimeStage? stageNow = await dbContext.JourneyRuntimes.AsNoTracking()
            .Where(row => row.DemandId == runtime.DemandId)
            .Select(row => (JourneyRuntimeStage?)row.Stage)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (stageNow != JourneyRuntimeStage.AwaitingSublot ||
            await LoadCancellationBeforeSublot.HasOpenCancellationAsync(dbContext, runtime.DemandId, cancellationToken)
                .ConfigureAwait(false))
        {
            return false;
        }
        await new PickupStopTermination(dbContext)
            .StageAsync(runtime, StationTimeoutCancellationReason, now, cancellationToken).ConfigureAwait(false);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        checkpointWaits.Clear(runtime.VehicleKey);
        LogStationDeadlineEndedStop(logger, runtime.AgvId, runtime.DemandId, deadline, null);
        return true;
    }

    /// <summary>
    /// Raises <see cref="StationTimeoutDoorNotClosedReason"/> while a load stop is past its station departure
    /// deadline with no result and a slot door not proven closed, and withdraws it once that stops being
    /// true (ADR-cross-0058 decision 4). Returns whether the block reason changed; the caller saves.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The stage stays <see cref="JourneyRuntimeStage.AwaitingLoadResult"/> and the journey is not Blocked:
    /// nothing is wrong with the operation, and ending the stop would let the vehicle be sent away with a
    /// door open (ADR-cross-0011/0012). The door is someone's to shut, so the alarm moves the duty to a
    /// person rather than ending anything (8005-agv-program#55). Once the door is shut the alarm is
    /// withdrawn, and the result that then arrives settles the stop as the vehicle reports it.
    /// </para>
    /// <para>
    /// The door evidence is <see cref="SlotDoorsNotProvenClosed"/>, the reading that holds the AwaitingSublot
    /// deadline back, and never RIoT's stop proof. Written on the edge only, since this runs every poll; the
    /// withdrawal clears only this code, so a reason written by anything else survives. Its start time is
    /// the block reason's own (control-server#80).
    /// </para>
    /// </remarks>
    private bool ReconcileStationTimeoutDoorNotClosed(
        JourneyRuntimeRow runtime,
        SessionRecoveryRow session,
        DateTimeOffset now)
    {
        if (StationDepartureDeadline(runtime, runtimeOptions.StationDepartureWaitTimeout) is { } deadline &&
            now >= deadline &&
            SlotDoorsNotProvenClosed(session))
        {
            if (runtime.BlockReasonCode == StationTimeoutDoorNotClosedReason)
            {
                return false;
            }
            runtime.SetBlockReason(StationTimeoutDoorNotClosedReason, now);
            LogStationTimeoutDoorNotClosed(
                logger, runtime.AgvId, runtime.DemandId, runtime.PickupStationId, deadline, null);
            return true;
        }
        if (runtime.BlockReasonCode != StationTimeoutDoorNotClosedReason)
        {
            return false;
        }
        runtime.SetBlockReason(null, now);
        return true;
    }

    /// <summary>
    /// Ends the demand of a load the store judged <see cref="StationOperationStatus.Failed"/>: a determinate
    /// failure received after the stop's station departure deadline (ADR-cross-0058 decision 5).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Defensive path: the v2 onboard never produces it; the protocol allows another onboard version to
    /// send it.</b> After its deadline the v2 onboard reopens a closed, empty slot rather than failing it
    /// (8005-agv-program#55), so this runs only for a peer that does report <c>FAILED</c> with
    /// <c>OPERATOR_TIMEOUT</c>. L1 and the synthetic peer are its whole proof.
    /// </para>
    /// <para>
    /// The server may close the stop alone because such a failure only follows the deadline, so the
    /// condition for closing it already holds when it arrives; one that arrived earlier never became
    /// Failed (<see cref="DeterminateLoadFailure"/>). The demand ends under the terminal reason its failure
    /// reason names -- <c>OPERATOR_TIMEOUT</c> ends it <c>CANCELLED_BY_STATION_TIMEOUT</c> -- the unanswered
    /// load command is settled, and the vehicle is released through the same tail as the sublot deadline
    /// (<see cref="PickupStopTermination"/>). The operation stays Failed, as the record of the load.
    /// </para>
    /// <para>
    /// <b>Re-checked under the write lock.</b> The inbound processor handles every vehicle message inside a
    /// write transaction, the cancellation of an in-flight load included, and a Failed load whose demand is
    /// still Accepted is one that cancellation authorizes. So the decision is taken again inside one here:
    /// the stage, the operation, the demand and the absence of an open load cancellation are read afresh,
    /// and whichever writer commits first decides the demand. The one that loses changes nothing, rather
    /// than rewriting a terminal reason already recorded.
    /// </para>
    /// <para>
    /// Liveness is the station deadline's measure (FR-031 AC-9): nothing is ended against evidence from a
    /// vehicle that has since gone off air.
    /// </para>
    /// </remarks>
    private async Task<bool> TrySettleDeterminateLoadFailureAsync(
        JourneyRuntimeRow runtime,
        StationOperationRow load,
        SessionRecoveryRow session,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        DateTimeOffset? lastInboundAt = await LatestInboundAtForSessionAsync(
            runtime.AgvId, session.SessionGeneration, cancellationToken).ConfigureAwait(false);
        if (lastInboundAt is null || now - lastInboundAt.Value > runtimeOptions.MaximumEvidenceAge)
        {
            return false;
        }
        // The store makes a load Failed only for a result whose reason has a terminal state, and records
        // that result in the same transaction; a null here would be a store defect, left standing rather
        // than guessed at.
        if (await DeterminateLoadFailureTerminalReasonAsync(runtime, cancellationToken).ConfigureAwait(false)
            is not { } terminalReason)
        {
            return false;
        }

        await using (IDbContextTransaction transaction = await dbContext.Database
                         .BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
        {
            await dbContext.Entry(runtime).ReloadAsync(cancellationToken).ConfigureAwait(false);
            await dbContext.Entry(load).ReloadAsync(cancellationToken).ConfigureAwait(false);
            AcceptedDemandRow demand = await dbContext.AcceptedDemands
                .SingleAsync(row => row.DemandId == runtime.DemandId, cancellationToken).ConfigureAwait(false);
            await dbContext.Entry(demand).ReloadAsync(cancellationToken).ConfigureAwait(false);
            // The one definition the sublot deadline and the cancellation's authorization use too. A cancellation
            // that did not reconcile is not "open", but it has already put the demand in RecoveryRequired and
            // the journey in Blocked, which the checks below refuse on their own.
            bool cancellationOpen = await LoadCancellationBeforeSublot
                .HasOpenCancellationAsync(dbContext, runtime.DemandId, cancellationToken).ConfigureAwait(false);
            if (runtime.Stage != JourneyRuntimeStage.AwaitingLoadResult ||
                load.Status != StationOperationStatus.Failed ||
                demand.Status != DemandExecutionStatus.Accepted ||
                cancellationOpen)
            {
                return false;
            }

            // The failure is the load command's answer. Left unsettled it would be replayed into every later
            // session, where the peer refuses it as a business id whose content changed.
            ProtocolOutboxRow? loadCommand = await dbContext.ProtocolOutbox
                .SingleOrDefaultAsync(row => row.MessageId == runtime.LoadCommandMessageId, cancellationToken)
                .ConfigureAwait(false);
            if (loadCommand is not null)
            {
                loadCommand.AcknowledgedAt ??= now;
            }
            await new PickupStopTermination(dbContext)
                .StageAsync(runtime, terminalReason, now, cancellationToken).ConfigureAwait(false);
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        checkpointWaits.Clear(runtime.VehicleKey);
        LogDeterminateLoadFailureSettled(logger, runtime.AgvId, runtime.DemandId, terminalReason, null);
        return true;
    }

    /// <summary>
    /// The terminal reason the live result of this journey's load names, read from the result as the vehicle
    /// sent it.
    /// </summary>
    private async Task<string?> DeterminateLoadFailureTerminalReasonAsync(
        JourneyRuntimeRow runtime,
        CancellationToken cancellationToken)
    {
        string? resultId = await dbContext.OperationResults.AsNoTracking()
            .Where(row => row.SlotOperationAttemptId == runtime.LoadSlotOperationAttemptId &&
                          row.SupersededByResultId == null &&
                          !row.HistoricalOnly)
            .Select(row => row.ResultId)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        ProtocolInboxRow? result = resultId is null
            ? null
            : await dbContext.ProtocolInbox.AsNoTracking()
                .SingleOrDefaultAsync(row => row.MessageId == resultId, cancellationToken).ConfigureAwait(false);
        if (result is null)
        {
            return null;
        }
        using JsonDocument document = JsonDocument.Parse(result.RequestJson);
        return DeterminateLoadFailure.TerminalReason(SlotOutcomeReport.FromSlotResults(
            document.RootElement.GetProperty("payload").GetProperty("slotResults")));
    }

    /// <summary>
    /// Whether the vehicle's current safety evidence fails to show every slot door shut: it reports a
    /// door not closed (<c>LOCK_NOT_CLOSED</c>), or something unknown, or the server holds no reading
    /// at all. The server must not end a stop against evidence it cannot read.
    /// </summary>
    private static bool SlotDoorsNotProvenClosed(SessionRecoveryRow session)
    {
        if (session.SafetyUnknownPresent != false || session.SafetyReasonCodesJson is null)
        {
            return true;
        }
        string[] reasonCodes = JsonSerializer.Deserialize<string[]>(session.SafetyReasonCodesJson) ?? [];
        return reasonCodes.Contains("LOCK_NOT_CLOSED", StringComparer.Ordinal);
    }

    /// <summary>
    /// CV-PREDEPARTURE-SAFETY-EXPIRES: retires the current pre-departure check once it no longer
    /// speaks for the vehicle's safety, and asks again under a new identity.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A check expires when the safety state has moved past the version it asked about -- whether or
    /// not it was answered -- or when its answer's own window has closed. Either way the answer
    /// cannot authorize a departure, and before 2026-09-13 nothing replaced it: one check id per
    /// journey, judged invalid on every later poll, so the vehicle waited at the pickup for ever.
    /// </para>
    /// <para>
    /// Not asked again while the vehicle is unsafe: that check could only be answered UNSAFE, and it
    /// would be asked again on every poll. The next SafetyStateChanged that makes it safe moves the
    /// version on and expires the check then. The new identity is derived from the old one, so a
    /// restart mid-way derives the same ids and needs no column to remember a counter.
    /// </para>
    /// </remarks>
    private async Task<bool> ReissueExpiredDepartureCheckAsync(
        JourneyRuntimeRow runtime,
        SessionRecoveryRow session,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (session.DepartureSafe != true || session.SafetyRevision is not long currentRevision)
        {
            return false;
        }

        ProtocolOutboxRow? check = await dbContext.ProtocolOutbox
            .SingleOrDefaultAsync(row => row.MessageId == runtime.PreDepartureSafetyCheckMessageId, cancellationToken)
            .ConfigureAwait(false);
        if (check is null)
        {
            return false;
        }

        long askedRevision;
        using (JsonDocument document = JsonDocument.Parse(check.PayloadJson))
        {
            askedRevision = document.RootElement.GetProperty("payload")
                .GetProperty("expectedSafetyStateVersion").GetInt64();
        }

        bool expired = askedRevision != currentRevision;
        if (!expired)
        {
            ProtocolInboxRow[] answers = await dbContext.ProtocolInbox.AsNoTracking()
                .Where(row => row.MessageType == "PreDepartureSafetyCheckResult")
                .ToArrayAsync(cancellationToken).ConfigureAwait(false);
            foreach (ProtocolInboxRow answer in answers)
            {
                using JsonDocument document = JsonDocument.Parse(answer.RequestJson);
                JsonElement payload = document.RootElement.GetProperty("payload");
                if (RequiredString(payload, "preDepartureSafetyCheckId") != runtime.PreDepartureSafetyCheckId)
                    continue;
                // A version change expires the check at once. A window that merely closed while the
                // version stayed put waits out the evidence age first: that answer lapses every couple
                // of seconds while a departure is held for another reason (the create gate refusing
                // the gate leg), and asking again each time would fill the outbox with checks.
                expired |= payload.GetProperty("safetyStateVersion").GetInt64() != currentRevision ||
                           now - payload.GetProperty("validUntil").GetDateTimeOffset() >= runtimeOptions.MaximumEvidenceAge;
            }
        }
        if (!expired)
        {
            return false;
        }

        if (check.AcknowledgedAt is null && check.FencedAt is null)
        {
            check.FencedAt = now;
        }
        runtime.PreDepartureSafetyCheckId =
            JourneyPlanBuilder.StableGuid(runtime.PreDepartureSafetyCheckId, "reissued-after-expiry");
        runtime.PreDepartureSafetyCheckMessageId =
            JourneyPlanBuilder.StableGuid(runtime.PreDepartureSafetyCheckMessageId, "reissued-after-expiry");
        await publisher.PublishPreDepartureSafetyCheckAsync(
            runtime.PreDepartureSafetyCheckMessageId,
            runtime.AgvId,
            session.SessionGeneration,
            new PreDepartureSafetyCheckCommand(
                runtime.PreDepartureSafetyCheckId,
                runtime.DemandId,
                runtime.GateMovementLegId,
                currentRevision,
                runtime.GateStationId),
            cancellationToken).ConfigureAwait(false);
        runtime.SetBlockReason("PREDEPARTURE_CHECK_EXPIRED", now);
        runtime.UpdatedAt = now;
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }



}
