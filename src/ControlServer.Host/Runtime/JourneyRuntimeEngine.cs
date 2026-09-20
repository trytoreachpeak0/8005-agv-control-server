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
    IRiotVehicleFacts vehicleFacts,
    IRiotMapStationCatalog mapStationCatalog,
    MapStationResolver stationResolver,
    IFixedTaskStationResolver fixedStationResolver,
    TaskTypeStationAccess taskTypeStations,
    CatalogBindingHoldConvergence catalogBindingHolds,
    MovementDispatchService movementDispatch,
    WireToGateStore store,
    OnboardJourneyPublisher publisher,
    ISublotBoxCountReader boxCountReader,
    IPackageCapacityStore packageCapacityStore,
    CatalogAvailabilityAccess catalogAvailability,
    ICatalogAvailabilityStore catalogStore,
    PreCreateGate createGate,
    VehicleRoster roster,
    VehicleDispatchPolicyAccess dispatchPolicy,
    IVehicleMotionFacts motionFacts,
    CheckpointWaitLedger checkpointWaits,
    VehicleFaultCoordinator faults,
    DispatchRoundRunner dispatchRound,
    OnboardDispatchFactsReader onboardFacts,
    IOptions<JourneyRuntimeOptions> options,
    TimeProvider timeProvider,
    ILogger<JourneyRuntimeEngine> logger)
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
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
    private static readonly Action<ILogger, string, string, Exception?> LogCheckpointWaitExceeded =
        LoggerMessage.Define<string, string>(
            LogLevel.Warning,
            new EventId(2105, nameof(LogCheckpointWaitExceeded)),
            "Vehicle {AgvId} has been holding at a traffic checkpoint past its budget on journey " +
            "{DemandId}; it is not arriving on its own.");
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

    /// <summary>The code a stop held at its AREA machine for the station's admission carries while it waits.</summary>
    private const string AreaEndAdmissionHeldReason = "TASK_TYPE_NOT_ALLOWED_AT_STATION";

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
        FleetVehicle[] underWay = roster.Vehicles
            .Where(vehicle => busy.Contains(vehicle.AgvId))
            .ToArray();
        // 一辆车都没有才退（批次7-06，control-server#211）。在这之前判的是「没有空闲车」——那时在途车走一条
        // 一律拒绝的占位路径，问它等于白问，所以提前退出是对的。本票让在途车与空闲车在同一张候选表上竞争
        // （REQ-0205），「全车队都在途」于是成了一种正常的、有活可派的局面：单车现场里它甚至是常态——
        // 车一接单就不再空闲，此后到卸完货为止的每一条新需求都只能靠追加接。按空闲车判会让这些需求一条都
        // 看不见，而这正是同区追加那条 L2 场景第一次跑出来的样子。
        if (free.Length == 0 && underWay.Length == 0)
        {
            return;
        }

        // The orphan check guards intake, so it runs when this round is about to take work on --
        // which with one vehicle is exactly when it ran before, since the single vehicle being
        // free is the same statement as no journey being active.
        string[] unresolvedDemandIds = await DemandJourneyLookup.OpenDemands(dbContext)
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

        await dispatchRound.RunAsync(
                currentMap, fixedStations, free, underWay, admissionPolicyDrifted, cancellationToken)
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
        // 本轮推进读到的停靠与归属。每一个要发出去的 id 都从这里取，取货与关卡两段不再各读各的列。
        JourneyStopCursor stops = await JourneyStopCursor.LoadAsync(dbContext, runtime, cancellationToken)
            .ConfigureAwait(false);
        await publisher.ReplayPendingForSessionAsync(
            runtime.AgvId,
            session.SessionGeneration,
            RuntimeMessageIds(runtime, stops),
            cancellationToken).ConfigureAwait(false);
        // 计划整体替换下发（ADR-cross-0053）：途中追加会在序列中间插进新停靠，而车上手里那张计划是插入之前的。
        // 补发在重放之后：重放先把没确认的旧行发完，新的那一版才不会被它顶回去。
        await RefreshUpcomingStopPlanAsync(runtime, stops, session, cancellationToken).ConfigureAwait(false);

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
                await PublishPickupDispatchPlanOnceAsync(runtime, stops, session, cancellationToken).ConfigureAwait(false);
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
                await PublishPickupStateAsync(runtime, stops, session, cancellationToken).ConfigureAwait(false);
                SetStage(runtime, JourneyRuntimeStage.AwaitingSublot, now);
                break;
            case JourneyRuntimeStage.AwaitingSublot:
                // Refills the station departure wait a disconnect voided (ADR-cross-0055). A reconnect
                // does not re-enter this stage, so the arrival's seed cannot be what refills it; and
                // this line runs behind the readiness gate, which is "after the recovery handshake and
                // the projection reconciliation".
                bool waitRefilled = runtime.StationDepartureWaitStartedAt is null;
                runtime.StationDepartureWaitStartedAt ??= now;
                ProtocolInboxRow? sublot = await FindMatchingSublotAsync(runtime, stops, session, cancellationToken)
                    .ConfigureAwait(false);
                // An operator cancelling before any entry (ADR-cross-0046; control-server#83) holds the stop
                // until the vehicle reports: no load starts and the deadline does not end it. Read after the
                // inbox, so an entry seen here cannot have been persisted before the cancellation it loses to.
                //
                // 按「这个停靠上任一条需求」问（批次7-06）：取消要车证明那一排仓位是空的，而那是一次整排的证明，
                // 不是对某一条需求的证明——所以一条需求的取消开着，这个停靠上的每一条都等着。
                if (await OpenCancellationAtCurrentStopAsync(stops, cancellationToken).ConfigureAwait(false))
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
                if (await RevalidateEnteredSublotAsync(runtime, stops, session, sublot, now, cancellationToken)
                        .ConfigureAwait(false) is not { } entered)
                {
                    return;
                }
                await PublishLoadAsync(runtime, stops, entered, session, sublot.MessageId, cancellationToken)
                    .ConfigureAwait(false);
                runtime.ConsumedSublotMessageId = sublot.MessageId;
                // 「这个停靠此刻在装哪一条」从这里起是落库的状态（批次7-06）。写在发完命令之后、同一次保存里：
                // 命令与状态要么都在，要么都不在，否则重启之后会对着一条没有命令的需求等结果。
                (await TrackedMembershipAsync(runtime, entered.Demand.DemandId, cancellationToken)
                    .ConfigureAwait(false)).Status = JourneyDemandStatuses.Loading;
                // The submission is this command's answer. Leaving the command unsettled replayed it
                // into every later session, where the peer refused it as a business id whose content
                // had changed and tore the session down.
                await store.SettleAnsweredCommandAsync(
                    stops.CurrentSublotRequestMessageId(runtime.WorklistRevision), now, cancellationToken).ConfigureAwait(false);
                SetStage(runtime, JourneyRuntimeStage.AwaitingLoadResult, now);
                break;
            case JourneyRuntimeStage.AwaitingLoadResult:
                // 等的是「此刻在装的那一条」的 attempt（批次7-06）。批次7-03 查旅程行上锚需求的那一个，
                // 而命令是按被录入那条发出去的——两者分岔的样子就是「发了 A 查 B」，旅程停在这里而且不报错。
                JourneyStopDemand loading = stops.LoadingAtCurrentStop
                    ?? throw new InvalidDataException(
                        $"Journey {runtime.JourneyId} waits for a load result with no demand loading at its stop.");
                StationOperationRow? load = await dbContext.StationOperations.SingleOrDefaultAsync(
                    row => row.SlotOperationAttemptId == loading.Membership.LoadSlotOperationAttemptId,
                    cancellationToken).ConfigureAwait(false);
                if (load?.Status == StationOperationStatus.RecoveryRequired)
                {
                    Block(runtime, "LOAD_RESULT_REQUIRES_RECOVERY", now);
                }
                else if (load?.Status == StationOperationStatus.Failed)
                {
                    await TrySettleDeterminateLoadFailureAsync(
                            runtime, stops, loading, load, session, now, cancellationToken)
                        .ConfigureAwait(false);
                    return;
                }
                else if (load?.Status == StationOperationStatus.Committed)
                {
                    await store.SettleAnsweredCommandAsync(
                        loading.Membership.LoadCommandMessageId, now, cancellationToken).ConfigureAwait(false);
                    (await TrackedMembershipAsync(runtime, loading.Demand.DemandId, cancellationToken)
                        .ConfigureAwait(false)).Status = JourneyDemandStatuses.Loaded;
                    await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    // 这个停靠上还有没装的需求，就回到等录入等下一条（同一站逐条串行，规格第 22 节补记）：重新加载
                    // 游标，因为刚写下的状态决定了清单该列谁、修订号该走到几。
                    stops = await JourneyStopCursor.LoadAsync(dbContext, runtime, cancellationToken)
                        .ConfigureAwait(false);
                    if (stops.OutstandingAtCurrentStop.Count > 0)
                    {
                        await PublishStopWorklistAsync(
                            runtime,
                            stops,
                            session,
                            StationDepartureDeadline(runtime, runtimeOptions.StationDepartureWaitTimeout),
                            cancellationToken).ConfigureAwait(false);
                        await PublishEntryRequestAsync(runtime, stops, session, cancellationToken)
                            .ConfigureAwait(false);
                        SetStage(runtime, JourneyRuntimeStage.AwaitingSublot, now);
                        break;
                    }
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
                    if (ReconcileStationTimeoutDoorNotClosed(runtime, stops.Current.StationId, session, now) ||
                        loadWaitRefilled)
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
                // 核验的两个 id 取当前停靠。命令里的 demandId 取「第一条受理的需求」（锚需求），即使它已经在更早的
                // 停靠卸完了：协议只放得下一个，而离站安全本来就是一次整车判断，不是对某一条需求的判断
                // （票面第 10 条）。腿与目标站取「即将出发的那一段」，也就是下一个停靠。
                await publisher.PublishPreDepartureSafetyCheckAsync(
                    DepartureCheckMessageId(stops.Current),
                    runtime.AgvId,
                    session.SessionGeneration,
                    new PreDepartureSafetyCheckCommand(
                        DepartureCheckId(stops.Current),
                        runtime.DemandId,
                        NextStopAfterCurrent(stops).MovementLegId,
                        session.SafetyRevision ?? throw new InvalidDataException("Safety revision is required."),
                        NextStopAfterCurrent(stops).StationId),
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
                    runtime, stops, session, cancellationToken).ConfigureAwait(false);
                now = timeProvider.GetUtcNow();
                if (safety is null)
                {
                    if (!reissuedDepartureCheck &&
                        await ReissueExpiredDepartureCheckAsync(runtime, stops, session, now, cancellationToken)
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
                JourneyStopRow departedFrom = stops.Current;
                JourneyStopRow nextStop = NextStopAfterCurrent(stops);
                CreateGateOutcome gateLeg = await GateLegAsync(runtime, nextStop, currentMap, cancellationToken)
                    .ConfigureAwait(false);
                if (!gateLeg.IsAllowed)
                {
                    runtime.SetBlockReason(gateLeg.BlockReason, timeProvider.GetUtcNow());
                    runtime.UpdatedAt = timeProvider.GetUtcNow();
                    await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    return;
                }

                OrderIntent gateIntent = JourneyPlanBuilder.LegIntent(runtime, nextStop, now);
                await new WireToGateStore(dbContext).AuthorizeMovementAsync(
                    gateIntent, safety, now, cancellationToken).ConfigureAwait(false);
                MovementDispatchResult dispatch = await movementDispatch.ReconcileOrCreateAsync(
                    nextStop.UpperId, cancellationToken).ConfigureAwait(false);
                runtime.ConsumedSafetyResultMessageId = await FindSafetyResultMessageIdAsync(
                    DepartureCheckId(departedFrom), cancellationToken).ConfigureAwait(false);
                await store.SettleAnsweredCommandAsync(
                    DepartureCheckMessageId(departedFrom), now, cancellationToken).ConfigureAwait(false);
                // 车开走了，这个停靠就完成了：当前停靠指针从此是落库的状态（批次7-06），下一轮加载游标读到的是下一个。
                (await TrackedStopAsync(departedFrom.StopId, cancellationToken).ConfigureAwait(false)).Status =
                    JourneyStopStatuses.Completed;
                SetStage(
                    runtime,
                    nextStop.StopRole == JourneyStopRoles.Pickup
                        ? JourneyRuntimeStage.AwaitingPickupArrival
                        : JourneyRuntimeStage.AwaitingGateArrival,
                    now);
                runtime.SetBlockReason(
                    dispatch.Outcome == MovementDispatchOutcome.Confirmed ? null : dispatch.Outcome.ToString(),
                    now);
                break;
            case JourneyRuntimeStage.AwaitingGateArrival:
                // control-server#228: judged first, every round, before anything below can return. The arrival check
                // below returns early whenever it cannot trust the arrival -- an onboard that stopped heartbeating on a
                // session still Ready does that every round -- and ObserveOrderFailureAsync may write another code; neither
                // is the admission returning, so neither stops or restarts this count. Escalating returns at once, so the
                // order failure check never runs in the same round and cannot write its code over the escalation, and a
                // Blocked journey is not observed for arrival again.
                if (await EscalateAreaEndAdmissionRevokedPastTimeoutAsync(runtime, stops, now, cancellationToken)
                        .ConfigureAwait(false))
                {
                    await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    return;
                }
                if (!await EnsureMovementConfirmedAsync(
                        runtime, stops.Current.UpperId, "GATE", cancellationToken).ConfigureAwait(false))
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
                if (!await UnloadAdmittedAsync(runtime, stops, cancellationToken).ConfigureAwait(false))
                {
                    // The first hold stores when the wait began, in this same save as the hold itself, so a crash leaves
                    // both or neither. The block is written from that start too: back from another code -- a failed
                    // order -- the hold names the whole wait, not the part since the last code change.
                    DateTimeOffset revokedSince = runtime.HoldForAreaEndAdmission(now);
                    runtime.SetBlockReason(AreaEndAdmissionHeldReason, revokedSince);
                    runtime.UpdatedAt = now;
                    await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    return;
                }
                await PublishGateStateAndUnloadAsync(runtime, stops, session, cancellationToken).ConfigureAwait(false);
                // Admitted again: the one place the wait's start is cleared.
                runtime.ReleaseAreaEndAdmissionHold();
                SetStage(runtime, JourneyRuntimeStage.AwaitingUnloadResult, now);
                break;
            case JourneyRuntimeStage.AwaitingUnloadResult:
                // 等的是本停靠此刻该卸的那一条（批次7-06）。卸货不需要一个「正在卸」的状态：卸是服务端自己按归属先后
                // 发的，一条卸完才发下一条，所以「此刻在卸的」恒等于「第一条还没卸的已装需求」。
                JourneyStopDemand unloading = stops.NextToUnloadAtCurrentStop
                    ?? throw new InvalidDataException(
                        $"Journey {runtime.JourneyId} waits for an unload result with nothing left to unload.");
                StationOperationRow? unload = await dbContext.StationOperations.SingleOrDefaultAsync(
                    row => row.SlotOperationAttemptId == unloading.Membership.UnloadSlotOperationAttemptId,
                    cancellationToken).ConfigureAwait(false);
                if (unload?.Status == StationOperationStatus.RecoveryRequired)
                {
                    Block(runtime, "UNLOAD_RESULT_REQUIRES_RECOVERY", now);
                }
                else
                {
                    AcceptedDemandRow demand = await dbContext.AcceptedDemands.SingleAsync(
                        row => row.DemandId == unloading.Demand.DemandId, cancellationToken).ConfigureAwait(false);
                    if (unload?.Status != StationOperationStatus.Committed ||
                        demand.Status != DemandExecutionStatus.Succeeded)
                    {
                        return;
                    }
                    await store.SettleAnsweredCommandAsync(
                        unloading.Membership.UnloadCommandMessageId, now, cancellationToken).ConfigureAwait(false);
                    (await TrackedMembershipAsync(runtime, unloading.Demand.DemandId, cancellationToken)
                        .ConfigureAwait(false)).Status = JourneyDemandStatuses.Unloaded;
                    await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    stops = await JourneyStopCursor.LoadAsync(dbContext, runtime, cancellationToken)
                        .ConfigureAwait(false);
                    if (stops.OutstandingAtCurrentStop.Count > 0)
                    {
                        // 本停靠还有没卸的：发这一版清单与下一条卸货命令，留在等结果。
                        await PublishStopWorklistAsync(
                            runtime, stops, session, stationDepartureDeadlineAt: null, cancellationToken)
                            .ConfigureAwait(false);
                        await PublishUnloadCommandAsync(runtime, stops, session, cancellationToken)
                            .ConfigureAwait(false);
                        break;
                    }

                    (await TrackedStopAsync(stops.Current.StopId, cancellationToken).ConfigureAwait(false)).Status =
                        JourneyStopStatuses.Completed;
                    await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    stops = await JourneyStopCursor.LoadAsync(dbContext, runtime, cancellationToken)
                        .ConfigureAwait(false);
                    if (stops.OpenStops.Count > 0)
                    {
                        // 还有停靠没走完（多停靠计划）：下一轮从新的当前停靠接着推。
                        SetStage(
                            runtime,
                            stops.Current.StopRole == JourneyStopRoles.Pickup
                                ? JourneyRuntimeStage.AwaitingPickupArrival
                                : JourneyRuntimeStage.AwaitingGateArrival,
                            now);
                        break;
                    }

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

    /// <summary>
    /// 车到了当前停靠：把这个停靠的车辆业务状态、清单、计划与录入请求发出去。
    /// </summary>
    /// <remarks>
    /// 四条消息的 id、作业会话与站点全部取自停靠行；清单项与录入请求的期待子批取这个停靠上还没终结的需求
    /// （单需求时就是今天那一条）。方法名里的 Pickup 留着，因为今天只有取货停靠会走到这里——问录入是取货才有的事。
    /// </remarks>
    private async Task PublishPickupStateAsync(
        JourneyRuntimeRow runtime,
        JourneyStopCursor stops,
        SessionRecoveryRow session,
        CancellationToken cancellationToken)
    {
        JourneyStopRow stop = stops.Current;
        await publisher.PublishVehicleBusinessStateAsync(
            stop.VehicleBusinessMessageId,
            runtime.AgvId,
            session.SessionGeneration,
            TransportBusinessState(
                StopRevision(runtime.VehicleBusinessRevision, stop),
                LoadingPhase(runtime.Stage, loadBatchClosed: false)),
            cancellationToken).ConfigureAwait(false);
        // ADR-cross-0055: the station departure wait starts at the arrival. Seeded ahead of the
        // worklist, whose save carries it, because the worklist is where the vehicle is told the
        // deadline -- a first snapshot sent before the seed would tell it there is none.
        runtime.StationDepartureWaitStartedAt ??= timeProvider.GetUtcNow();
        await PublishStopWorklistAsync(
            runtime,
            stops,
            session,
            StationDepartureDeadline(runtime, runtimeOptions.StationDepartureWaitTimeout),
            cancellationToken).ConfigureAwait(false);
        await RetireSupersededSnapshotAsync(PickupDispatchPlanMessageId(runtime), cancellationToken)
            .ConfigureAwait(false);
        await publisher.PublishUpcomingStopPlanAsync(
            stop.PlanMessageId,
            runtime.AgvId,
            session.SessionGeneration,
            JourneyPlanBuilder.Plan(
                runtime, stops.Stops, stop, arrivedAtCurrent: true,
                PlanRevisionAt(runtime.PlanRevision, stop, arrivedAtStop: true)),
            cancellationToken).ConfigureAwait(false);
        await PublishEntryRequestAsync(runtime, stops, session, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 当前停靠此刻这一版的清单（批次7-06，control-server#211）。到站时发第一版，此后每做完一条需求再发一版。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>清单与录入请求载的是同一个集合</b>——「本停靠还没做完的需求」——修订号取 <see cref="WorklistRevisionAt"/>。
    /// 集合少一条，号就加一，旧的那一版随 <c>expiresOnRevisionChange</c> 在车上作废。两条报文分成两个方法只是为了
    /// 保住线序（到站那一次，计划夹在它们中间），它们仍在同一次保存里落库。
    /// </para>
    /// <para>
    /// <b>旧的那一版在发件箱里也要退役。</b>重放按未确认的行补发，旧清单一旦被补发，车载端会把它当成修订号回退
    /// （<c>SNAPSHOT_REVISION_REGRESSION</c>）而断会话；旧录入请求被补发，则是一条内容已变的业务 id。
    /// </para>
    /// <para>
    /// 一条待做的需求都没有时什么也不发：那不是空清单该不该发的问题，<c>expectedSublots</c> 带 <c>minItems: 1</c>，
    /// 空集合发出去就是一条违反 schema 的报文。本停靠做完了，调用方接着往下一个停靠推。
    /// </para>
    /// </remarks>
    private async Task PublishStopWorklistAsync(
        JourneyRuntimeRow runtime,
        JourneyStopCursor stops,
        SessionRecoveryRow session,
        DateTimeOffset? stationDepartureDeadlineAt,
        CancellationToken cancellationToken)
    {
        JourneyStopRow stop = stops.Current;
        IReadOnlyList<JourneyStopDemand> outstanding = stops.OutstandingAtCurrentStop;
        if (outstanding.Count == 0)
        {
            return;
        }

        long revision = stops.WorklistRevisionAt(runtime.WorklistRevision, stop);
        long baseRevision = stops.FirstWorklistRevisionAt(runtime.WorklistRevision, stop);
        if (revision > baseRevision)
        {
            // 上一版：同一个停靠，偏移少一。
            await RetireSupersededSnapshotAsync(
                stops.WorklistMessageIdAt(runtime.WorklistRevision, stop, revision - 1), cancellationToken)
                .ConfigureAwait(false);
        }

        await publisher.PublishCurrentStopWorklistAsync(
            stops.WorklistMessageIdAt(runtime.WorklistRevision, stop, revision),
            runtime.AgvId,
            session.SessionGeneration,
            Worklist(stop, outstanding, revision, stationDepartureDeadlineAt),
            cancellationToken).ConfigureAwait(false);
        await AdvanceWorklistRevisionCounterAsync(runtime.AgvId, revision, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>取货停靠此刻这一版的录入请求，期待子批就是清单那一版列的那些。</summary>
    private async Task PublishEntryRequestAsync(
        JourneyRuntimeRow runtime,
        JourneyStopCursor stops,
        SessionRecoveryRow session,
        CancellationToken cancellationToken)
    {
        JourneyStopRow stop = stops.Current;
        IReadOnlyList<JourneyStopDemand> outstanding = stops.OutstandingAtCurrentStop;
        if (outstanding.Count == 0)
        {
            return;
        }

        long revision = stops.WorklistRevisionAt(runtime.WorklistRevision, stop);
        long baseRevision = stops.FirstWorklistRevisionAt(runtime.WorklistRevision, stop);
        if (revision > baseRevision)
        {
            await RetireSupersededSnapshotAsync(
                stops.SublotRequestMessageIdAt(runtime.WorklistRevision, stop, revision - 1), cancellationToken)
                .ConfigureAwait(false);
        }

        await publisher.PublishSublotEntryRequestAsync(
            stops.SublotRequestMessageIdAt(runtime.WorklistRevision, stop, revision),
            runtime.AgvId,
            session.SessionGeneration,
            new SublotEntryRequest(
                stop.OperationSessionId,
                stop.StationId,
                revision,
                [.. outstanding.Select(item => item.Demand.Sublot)]),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 保证下一趟旅程的清单基准高于本趟已经用过的任何一个号。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 受理时按车计数器被推进到这趟的基准，下一趟的基准是它加上每趟的预留量
    /// （<see cref="WireToGateStore.RevisionsPerJourney"/>）——那个预留量是照「一趟两个停靠、各发一版」定的。多需求
    /// 或多停靠的旅程会发得更多，于是本趟的号可能越过下一趟的基准，而车载端只按消息类型记修订号，一次回退就是
    /// <c>SNAPSHOT_REVISION_REGRESSION</c> 断会话。
    /// </para>
    /// <para>
    /// <b>单需求旅程一次也不会推进它</b>：那时最大的号正好落在预留里，条件不成立。所以这条只在多需求下起作用，
    /// 单需求的修订号流逐字不变——<c>WirePin</c> 的「同一辆车跑两趟」那条钉着它。
    /// </para>
    /// </remarks>
    private async Task AdvanceWorklistRevisionCounterAsync(
        string agvId,
        long revision,
        CancellationToken cancellationToken)
    {
        long required = revision - WireToGateStore.RevisionsPerJourney + 1;
        VehicleSnapshotRevisionRow? counter = await dbContext.Set<VehicleSnapshotRevisionRow>()
            .SingleOrDefaultAsync(row => row.AgvId == agvId, cancellationToken).ConfigureAwait(false);
        if (counter is not null && counter.WorklistRevision < required)
        {
            counter.WorklistRevision = required;
        }
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
        JourneyStopCursor stops,
        SessionRecoveryRow session,
        CancellationToken cancellationToken)
    {
        string messageId = PickupDispatchPlanMessageId(runtime);
        if (await dbContext.ProtocolOutbox.AsNoTracking()
                .AnyAsync(row => row.MessageId == messageId, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        // 车还在路上，所以当前停靠这条腿是 ACTIVE 而不是 ARRIVED。
        await publisher.PublishUpcomingStopPlanAsync(
            messageId,
            runtime.AgvId,
            session.SessionGeneration,
            JourneyPlanBuilder.Plan(
                runtime, stops.Stops, stops.Current, arrivedAtCurrent: false,
                PlanRevisionAt(runtime.PlanRevision, stops.Current, arrivedAtStop: false)),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 车上那张计划与此刻的停靠序列不一致时，发一张新的（ADR-cross-0053，批次7-06，control-server#211）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>判据是内容，不是某个计数器。</b>途中追加在序列中间插进两个停靠，而当前停靠的序位没变——按序位算出来的
    /// 计划修订号于是一字未动，靠它看不出该重发。所以这里把「现在应该是什么样」与「发件箱里最后发出去的那一张」
    /// 逐条比：腿的身份、序位、站点、状态，任一条不同就换一版。
    /// </para>
    /// <para>
    /// <b>到站那一次不走这里。</b>到站本来就要发一张新的计划，序位一到就换号；这里管的是两次到站<b>之间</b>计划被
    /// 改写的那种情形，今天只有途中追加一种。没有可比的那一张（这个停靠还没发过计划）时什么也不做。
    /// </para>
    /// </remarks>
    private async Task RefreshUpcomingStopPlanAsync(
        JourneyRuntimeRow runtime,
        JourneyStopCursor stops,
        SessionRecoveryRow session,
        CancellationToken cancellationToken)
    {
        if (runtime.Stage is JourneyRuntimeStage.Completed or JourneyRuntimeStage.Blocked)
        {
            return;
        }

        // 只有被追加过的旅程才需要整体重发，而追加必然带来第二条需求。单需求旅程——今天现场跑的全部——
        // 因此一步都不进这一段：既省掉每个 tick 读一次发件箱，也让这次改动在单需求那条路上完全不执行。
        if (stops.AllDemands.Count <= 1)
        {
            return;
        }

        JourneyStopRow stop = stops.Current;
        bool arrived = runtime.Stage is not
            (JourneyRuntimeStage.AwaitingPickupArrival or JourneyRuntimeStage.AwaitingGateArrival);
        if (await LastSentPlanAsync(runtime.AgvId, cancellationToken).ConfigureAwait(false) is not { } last)
        {
            return;
        }

        UpcomingStopPlanProjection current = JourneyPlanBuilder.Plan(
            runtime, stops.Stops, stop, arrived, last.Revision);
        if (PlanOnTheWireMatches(last.PayloadJson, current))
        {
            return;
        }

        long revision = last.Revision + 1;
        await RetireSupersededSnapshotAsync(last.MessageId, cancellationToken).ConfigureAwait(false);
        await publisher.PublishUpcomingStopPlanAsync(
            JourneyPlanBuilder.StableGuid($"{stop.StopId}|{revision}", "plan"),
            runtime.AgvId,
            session.SessionGeneration,
            JourneyPlanBuilder.Plan(runtime, stops.Stops, stop, arrived, revision),
            cancellationToken).ConfigureAwait(false);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>这辆车最后收到的那一版计划：发件箱里最新的一条 <c>UpcomingStopPlanSnapshot</c>。</summary>
    /// <remarks>
    /// <para>
    /// <b>从发件箱读，而不是把消息 id 推算出来。</b>一张计划的第一版由三个地方发出，各有各的 id 来源——
    /// 派往取货站那一版按锚需求算（<c>PickupDispatchPlanMessageId</c>），到站那两版按停靠行上的
    /// <c>PlanMessageId</c>。推算要把这三种来源在这里再写一遍，而写漏一种的表现是「最后一版找不到，于是不重发」：
    /// 静默地什么都不做，正是最难发现的那种错。实际写漏过一次，同区追加那条 L2 场景跑出来才看见。
    /// </para>
    /// <para>
    /// 按载荷里的 <c>agvId</c> 筛，因为发件箱行本身没有这一列。整表读回来在内存里筛看着粗，但这个方法只在
    /// 被追加过的旅程上调用（调用处第一道判断），而那在今天的现场是零。
    /// </para>
    /// </remarks>
    private async Task<(long Revision, string MessageId, string PayloadJson)?> LastSentPlanAsync(
        string agvId,
        CancellationToken cancellationToken)
    {
        ProtocolOutboxRow[] rows = await dbContext.ProtocolOutbox.AsNoTracking()
            .Where(row => row.MessageType == "UpcomingStopPlanSnapshot")
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        (long Revision, string MessageId, string PayloadJson)? last = null;
        DateTimeOffset latest = DateTimeOffset.MinValue;
        foreach (ProtocolOutboxRow row in rows)
        {
            using JsonDocument document = JsonDocument.Parse(row.PayloadJson);
            JsonElement envelope = document.RootElement;
            if (RequiredString(envelope, "agvId") != agvId)
            {
                continue;
            }

            if (row.CreatedAt < latest ||
                (row.CreatedAt == latest && last is { } seen &&
                 string.CompareOrdinal(row.MessageId, seen.MessageId) <= 0))
            {
                continue;
            }

            latest = row.CreatedAt;
            last = (
                envelope.GetProperty("payload").GetProperty("planRevision").GetInt64(),
                row.MessageId,
                row.PayloadJson);
        }

        return last;
    }

    /// <summary>车上那一版的腿，与现在应该发的那一份，是不是同一串。</summary>
    private static bool PlanOnTheWireMatches(string payloadJson, UpcomingStopPlanProjection current)
    {
        using JsonDocument document = JsonDocument.Parse(payloadJson);
        JsonElement legs = document.RootElement.GetProperty("payload").GetProperty("legs");
        if (legs.GetArrayLength() != current.Legs.Count)
        {
            return false;
        }

        int index = 0;
        foreach (JsonElement leg in legs.EnumerateArray())
        {
            UpcomingMovementLeg expected = current.Legs[index++];
            if (RequiredString(leg, "movementLegId") != expected.MovementLegId ||
                leg.GetProperty("sequence").GetInt32() != expected.Sequence ||
                RequiredString(leg, "stationId") != expected.StationId ||
                RequiredString(leg, "state") != expected.State)
            {
                return false;
            }
        }

        return true;
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

    /// <summary>
    /// 对录入所答复的那条需求下装货命令。attempt id、命令 id 与目标仓位取<b>它自己</b>在本旅程的归属行，作业会话与
    /// 站点取当前停靠。
    /// </summary>
    /// <remarks>
    /// <paramref name="entered"/> 是操作员扫出来的那一条，不是锚需求（批次7-06，control-server#211）。批次7-03 取锚需求，
    /// 是因为那时录入范围被一句守卫退回了锚需求；守卫删掉之后再取锚需求，就是「操作员扫第二条、车上收到第一条的仓位」。
    /// </remarks>
    private async Task PublishLoadAsync(
        JourneyRuntimeRow runtime,
        JourneyStopCursor stops,
        JourneyStopDemand entered,
        SessionRecoveryRow session,
        string correlationId,
        CancellationToken cancellationToken)
    {
        JourneyStopRow stop = stops.Current;
        int[] slots = JsonSerializer.Deserialize<int[]>(entered.Membership.TargetSlotsJson) ?? [];
        string hash = BusinessHash(entered.Demand.DemandId, entered.Demand.Sublot, "LOAD", slots);
        // The load carries the admission only where the AREA machine is the pickup (WIRE_TO_GATE); STAGING_TO_WIRE's
        // is carried by the unload at the machine (I6 overturned, scope specification 21.2 item 2).
        bool admission = await store.AreaEndOperationAsync(
                entered.Demand.DemandId, entered.Demand.WorkType, cancellationToken)
            .ConfigureAwait(false) == SlotOperationType.Load;
        await publisher.PublishSlotOperationCommandAsync(
            entered.Membership.LoadCommandMessageId,
            runtime.AgvId,
            session.SessionGeneration,
            new SlotOperationCommand(
                correlationId,
                entered.Demand.DemandId,
                entered.Demand.Sublot,
                stop.OperationSessionId,
                entered.Membership.LoadSlotOperationAttemptId,
                SlotOperationType.Load,
                slots,
                session.ForcedRecoveryGeneration,
                hash),
            cancellationToken,
            admission ? stop.StationId : null,
            admission ? entered.Demand.WorkType : null).ConfigureAwait(false);
    }

    /// <summary>
    /// 车到了卸货停靠：发这个停靠的三条快照，再对本停靠该卸的第一条需求下卸货命令。
    /// </summary>
    /// <remarks>
    /// 清单与卸货命令都随本停靠的进度走（批次7-06，control-server#211）：清单列的是还没卸的，命令发给
    /// <see cref="JourneyStopCursor.NextToUnloadAtCurrentStop"/>。单需求下这两者都只有那一条，与之前逐字相同。
    /// </remarks>
    private async Task PublishGateStateAndUnloadAsync(
        JourneyRuntimeRow runtime,
        JourneyStopCursor stops,
        SessionRecoveryRow session,
        CancellationToken cancellationToken)
    {
        JourneyStopRow stop = stops.Current;
        await publisher.PublishVehicleBusinessStateAsync(
            stop.VehicleBusinessMessageId,
            runtime.AgvId,
            session.SessionGeneration,
            TransportBusinessState(
                StopRevision(runtime.VehicleBusinessRevision, stop),
                LoadingPhase(runtime.Stage, loadBatchClosed: true)),
            cancellationToken).ConfigureAwait(false);
        // The drop-off stop has no departure wait: ADR-cross-0055's wait is the pickup's.
        await PublishStopWorklistAsync(
            runtime, stops, session, stationDepartureDeadlineAt: null, cancellationToken).ConfigureAwait(false);
        await publisher.PublishUpcomingStopPlanAsync(
            stop.PlanMessageId,
            runtime.AgvId,
            session.SessionGeneration,
            JourneyPlanBuilder.Plan(
                runtime, stops.Stops, stop, arrivedAtCurrent: true,
                PlanRevisionAt(runtime.PlanRevision, stop, arrivedAtStop: true)),
            cancellationToken).ConfigureAwait(false);
        await PublishUnloadCommandAsync(runtime, stops, session, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 对本停靠该卸的那一条需求下卸货命令。一条卸完再下一条——同一站的几条需求逐条串行，一次只开一排仓门
    /// （规格第 22 节补记；一次只开一个仓门的现场口径见 8005-agv-program#111）。
    /// </summary>
    private async Task PublishUnloadCommandAsync(
        JourneyRuntimeRow runtime,
        JourneyStopCursor stops,
        SessionRecoveryRow session,
        CancellationToken cancellationToken)
    {
        JourneyStopRow stop = stops.Current;
        if (stops.NextToUnloadAtCurrentStop is not { } next)
        {
            return;
        }

        int[] slots = JsonSerializer.Deserialize<int[]>(next.Membership.TargetSlotsJson) ?? [];
        // The unload carries the admission where the AREA machine is the drop-off (STAGING_TO_WIRE).
        bool admission = await store.AreaEndOperationAsync(
                next.Demand.DemandId, next.Demand.WorkType, cancellationToken)
            .ConfigureAwait(false) == SlotOperationType.Unload;
        await publisher.PublishSlotOperationCommandAsync(
            next.Membership.UnloadCommandMessageId,
            runtime.AgvId,
            session.SessionGeneration,
            new SlotOperationCommand(
                null,
                next.Demand.DemandId,
                next.Demand.Sublot,
                stop.OperationSessionId,
                next.Membership.UnloadSlotOperationAttemptId,
                SlotOperationType.Unload,
                slots,
                session.ForcedRecoveryGeneration,
                BusinessHash(next.Demand.DemandId, next.Demand.Sublot, "UNLOAD", slots)),
            cancellationToken,
            admission ? stop.StationId : null,
            admission ? next.Demand.WorkType : null).ConfigureAwait(false);
    }

    /// <summary>
    /// Whether the unload may be commanded as far as station admission goes. Only a journey whose AREA machine is the
    /// drop-off (STAGING_TO_WIRE) is asked, and only until its unload is prepared: from then on the decision frozen with
    /// the unload is what stands (ADR-cross-0050/0051), so a restart that re-enters the arrival with the unload already
    /// prepared goes on to replay it rather than stopping here on the current policy.
    /// </summary>
    private async Task<bool> UnloadAdmittedAsync(
        JourneyRuntimeRow runtime,
        JourneyStopCursor stops,
        CancellationToken cancellationToken)
    {
        // 问的是本停靠此刻该卸的那一条（批次7-06）：准入是「这个站允许这条需求的任务类型吗」，而一站几条需求的
        // 任务类型未必相同。没有该卸的就没有可问的，放行——调用方接着往下判。
        if (stops.NextToUnloadAtCurrentStop is not { } next)
        {
            return true;
        }

        string workType = next.Demand.WorkType;
        if (await store.AreaEndOperationAsync(next.Demand.DemandId, workType, cancellationToken).ConfigureAwait(false)
                != SlotOperationType.Unload ||
            await UnloadPreparedAsync(stops, cancellationToken).ConfigureAwait(false))
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
        JourneyStopCursor stops,
        SessionRecoveryRow session,
        CancellationToken cancellationToken)
    {
        // 预筛用当前停靠的作业会话（control-server#208）。下面 AnswersTheStop 读的仍是旅程行上的同源副本：
        // 那个判定被 LoadCancellationBeforeSublot 与取消那条入站链共用，本票不碰那条链，所以它随多停靠一起搬（批次7-06）。
        string operationSessionId = stops.Current.OperationSessionId;
        StopEntryAddress address = stops.EntryAddressOfCurrentStop(runtime.WorklistRevision);
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
                                   LoadCancellationBeforeSublot.AnswersTheStop(root, address);
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
        // 已经被一条装货命令答复过的录入不再读第二遍（批次7-06，control-server#211）。一个停靠上可以连着录入几条需求，
        // 所以「消费过的」是一个集合；旅程行上那个单数的 ConsumedSublotMessageId 只记得住最后一条，第二次录入会
        // 重新读到第一条、把已经装完的那条需求再判一次，结果是一条拒收而不是下一条装货命令。
        //
        // 证据取发件箱里那条装货命令的 correlationId，与 RefusedSubmissionIdsAsync 同构：那是「这条录入被答复过」
        // 唯一的持久记录，重启之后仍在，而内存里的任何标记都不在。
        HashSet<string> consumed = await ConsumedSubmissionIdsAsync(
            [.. answers.Select(row => row.MessageId)], cancellationToken).ConfigureAwait(false);
        return answers.Find(row => !refused.Contains(row.MessageId) && !consumed.Contains(row.MessageId));
    }

    /// <summary>这些录入里，哪些已经被一条装货命令答复过。</summary>
    private async Task<HashSet<string>> ConsumedSubmissionIdsAsync(
        IReadOnlyCollection<string> submissionIds,
        CancellationToken cancellationToken)
    {
        HashSet<string> consumed = new(StringComparer.Ordinal);
        foreach (string submissionId in submissionIds)
        {
            // 子串只是数据库能用的预筛，判定仍由下面的解析做——同 RefusedSubmissionIdsAsync 的理由。
            string[] stored = await dbContext.ProtocolOutbox.AsNoTracking()
                .Where(row => row.MessageType == "SlotOperationCommand" && row.PayloadJson.Contains(submissionId))
                .Select(row => row.PayloadJson)
                .ToArrayAsync(cancellationToken).ConfigureAwait(false);
            foreach (string payload in stored)
            {
                using JsonDocument document = JsonDocument.Parse(payload);
                if (document.RootElement.TryGetProperty("correlationId", out JsonElement correlationId) &&
                    string.Equals(correlationId.GetString(), submissionId, StringComparison.Ordinal))
                {
                    consumed.Add(submissionId);
                    break;
                }
            }
        }

        return consumed;
    }

    /// <summary>
    /// Judges the submission that answers this stop's open entry request, answering it when it may not
    /// load. Returns the demand the entry resolved to, or null when nothing may be loaded.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The demand is resolved here, from the sublot, inside the dispatch scope.</b> Protocol 2.0.0 took
    /// the demand off <c>SublotSubmitted</c>: which demand a sublot belongs to is the server's to work
    /// out, and the scope it works it out in is the demands this journey was dispatched for — not the
    /// catalogue, which holds demands this vehicle was never given. FR-001 AC-3 scopes entry to the whole
    /// stop, so the scope is the set of demands still outstanding there, and <b>the demand it resolves to is
    /// the one that gets loaded</b> (control-server#211): every step below — the basket count it is compared
    /// against, the slots the command opens, the attempt its result is looked up by — is that demand's own.
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
    private async Task<JourneyStopDemand?> RevalidateEnteredSublotAsync(
        JourneyRuntimeRow runtime,
        JourneyStopCursor stops,
        SessionRecoveryRow session,
        ProtocolInboxRow submission,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        using JsonDocument document = JsonDocument.Parse(submission.RequestJson);
        string enteredSublot = RequiredString(document.RootElement.GetProperty("payload"), "sublot");
        // 派车范围：这个停靠上还没装完的需求（control-server#208 放宽成集合，control-server#211 让它真的生效）。
        // 取「还没装完」而不是「还没终结」，是因为刚装完的那一条不该再被扫一次——它已经不在清单上了。
        JourneyStopDemand? member = stops.OutstandingAtCurrentStop.SingleOrDefault(
            item => string.Equals(item.Demand.Sublot, enteredSublot, StringComparison.Ordinal));
        if (member is null)
        {
            await RefuseAsync(
                runtime,
                stops,
                session,
                submission,
                demandId: null,
                enteredSublot,
                ServerReasonCodes.SublotNotInDispatchScope,
                $"子批 {enteredSublot} 不属于本次派车范围，不予开仓。",
                now,
                cancellationToken).ConfigureAwait(false);
            return null;
        }

        AcceptedDemandRow demand = member.Demand;

        // At the AREA machine station, not necessarily this one: STAGING_TO_WIRE loads at a staging station and is
        // admitted at the machine it unloads at (control-server#163). Asked here too, before anything is loaded for a
        // machine that would refuse it.
        if (!await store.IsTaskTypeAllowedAtAreaEndAsync(runtime, demand.WorkType, cancellationToken)
                .ConfigureAwait(false))
        {
            runtime.SetBlockReason(AreaEndAdmissionHeldReason, now);
            runtime.UpdatedAt = now;
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        LiveMesFieldSet? fields = JsonSerializer.Deserialize<LiveMesFieldSet>(
            demand.LiveMesFieldsJson, SerializerOptions);
        if (string.IsNullOrWhiteSpace(fields?.Package))
        {
            await RefuseAsync(
                runtime,
                stops,
                session,
                submission,
                demand.DemandId,
                enteredSublot,
                ServerReasonCodes.PackageCapacityUnresolved,
                $"子批 {enteredSublot} 没有 PACKAGE 型号，算不出花篮数量，不予开仓。",
                now,
                cancellationToken).ConfigureAwait(false);
            return null;
        }

        int? capacity = await packageCapacityStore.ResolveAndTrackAsync(fields.Package, now, cancellationToken)
            .ConfigureAwait(false);
        if (!AuthoritativeBasketCount.PackageCapacityIsUsable(capacity))
        {
            await RefuseAsync(
                runtime,
                stops,
                session,
                submission,
                demand.DemandId,
                enteredSublot,
                ServerReasonCodes.PackageCapacityUnresolved,
                $"PACKAGE {fields.Package} 没有已批准的花篮容量对照，算不出花篮数量，不予开仓。",
                now,
                cancellationToken).ConfigureAwait(false);
            return null;
        }

        int? maxBoxCount;
        try
        {
            maxBoxCount = await boxCountReader.ReadMaxBoxCountAsync(demand.Sublot, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception error) when (error is HttpRequestException or InvalidDataException or JsonException)
        {
            LogBoxCountFailed(logger, demand.DemandId, error);
            maxBoxCount = null;
        }

        if (!AuthoritativeBasketCount.BoxCountIsUsable(maxBoxCount))
        {
            await RefuseAsync(
                runtime,
                stops,
                session,
                submission,
                demand.DemandId,
                enteredSublot,
                ServerReasonCodes.SublotBoxCountUnavailable,
                $"查不到子批 {enteredSublot} 的箱数，算不出花篮数量，不予开仓。",
                now,
                cancellationToken).ConfigureAwait(false);
            return null;
        }

        // Both inputs were checked usable just above, so a count is computable.
        int recomputed = AuthoritativeBasketCount.Compute(maxBoxCount, capacity)
            ?? throw new InvalidOperationException("Both inputs are usable, so a count is computable.");
        // 比的是「被录入那一条」自己冻结的花篮数（批次7-06）：仓位是按它预留的，而一站几条需求各有各的数。
        // 批次7-03 比的是旅程行上的锚需求那一份，单需求下两者恒等。
        if (recomputed != member.Membership.ExpectedBasketCount)
        {
            await RefuseAsync(
                runtime,
                stops,
                session,
                submission,
                demand.DemandId,
                enteredSublot,
                ServerReasonCodes.ExpectedBasketCountMismatch,
                $"子批 {enteredSublot} 的花篮数量由 {member.Membership.ExpectedBasketCount} 变为 {recomputed}，"
                + "与本次派车预留的仓位不符，不予开仓。",
                now,
                cancellationToken).ConfigureAwait(false);
            return null;
        }

        return member;
    }

    /// <summary>
    /// Answers one entry with a <c>SublotRejected</c> carrying the reason, and leaves the journey waiting
    /// for the next one.
    /// </summary>
    /// <remarks>
    /// The refusal is keyed on the submission it answers, which is the only correlation the operator's
    /// screen can attach the reason to, and its own id is derived from that submission rather than
    /// counted, so two refusals of two submissions are two messages instead of one rewritten identity.
    /// The submission is deliberately not recorded as consumed: the stop is still the operator's to
    /// rescan, to cancel, or to run out at its deadline.
    /// </remarks>
    private async Task RefuseAsync(
        JourneyRuntimeRow runtime,
        JourneyStopCursor stops,
        SessionRecoveryRow session,
        ProtocolInboxRow submission,
        string? demandId,
        string enteredSublot,
        string reasonCode,
        string displayMessage,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // 作业会话与修订号取「当前停靠此刻这一版」（批次7-06）：拒收告诉操作员「你扫的那一版清单是第几号」，
        // 而清单每装完一条就升一版，旅程行上那个受理时的值只在第一版上对得上。
        await publisher.PublishSublotRejectedAsync(
            JourneyPlanBuilder.StableGuid(submission.MessageId, "sublot-rejected"),
            submission.MessageId,
            runtime.AgvId,
            session.SessionGeneration,
            new SublotRejection(
                demandId,
                stops.Current.OperationSessionId,
                new WireProblem(reasonCode, "payload.sublot", displayMessage),
                stops.WorklistRevisionAt(runtime.WorklistRevision, stops.Current),
                enteredSublot),
            cancellationToken).ConfigureAwait(false);
        runtime.UpdatedAt = now;
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        LogSublotRejected(logger, demandId ?? runtime.DemandId, reasonCode, null);
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
        JourneyStopCursor stops,
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
                runtime,
                stops, session, now, cancellationToken).ConfigureAwait(false);
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
        JourneyStopCursor stops,
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
            if (RequiredString(payload, "preDepartureSafetyCheckId") != DepartureCheckId(stops.Current))
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
            bool valid = (correlationId == DepartureCheckMessageId(stops.Current) ||
                          correlationId == DepartureCheckId(stops.Current)) &&
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
                DepartureCheckId(stops.Current),
                safetyRevision,
                true,
                observedAt,
                validUntil), false);
        }
        return (null, false);
    }

    /// <summary>One vehicle's Onboard-side facts; see <see cref="OnboardDispatchFactsReader.ReadOnboardFactsAsync"/>.</summary>
    private Task<OnboardDispatchFacts?> ReadOnboardFactsAsync(string agvId, CancellationToken cancellationToken) =>
        onboardFacts.ReadOnboardFactsAsync(agvId, cancellationToken);

    /// <summary>
    /// The vehicle's Ready session row, untracked; see <see cref="OnboardDispatchFactsReader.CurrentReadySessionAsync"/>.
    /// </summary>
    private Task<SessionRecoveryRow?> CurrentReadySessionAsync(string agvId, CancellationToken cancellationToken) =>
        onboardFacts.CurrentReadySessionAsync(agvId, cancellationToken);

    /// <summary>
    /// When this session generation was last heard from; see
    /// <see cref="OnboardDispatchFactsReader.LatestInboundAtForSessionAsync"/>.
    /// </summary>
    private Task<DateTimeOffset?> LatestInboundAtForSessionAsync(
        string agvId,
        long generation,
        CancellationToken cancellationToken) =>
        onboardFacts.LatestInboundAtForSessionAsync(agvId, generation, cancellationToken);

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
        JourneyStopRow targetStop,
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
        // 兜底取「这一段腿要去的那个停靠」（批次7-06），不是旅程行上写死的关卡站：多停靠计划里后者只是其中一个。
        int stationId = dropoff?.StationId ?? targetStop.StationRiotId;
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
    /// <remarks>
    /// <para>
    /// 站点、作业会话与项的角色全部由停靠行给出，项本身是这个停靠上还没终结的需求（批次7-03，control-server#208）。
    /// </para>
    /// <para>
    /// 协议顶层只有一个 <c>operationSessionId</c>（规格第 22 节补记），项至多 8 条。今天一个停靠一条需求，
    /// 所以这里恒为一项，与原来写死的那一项逐字相同。
    /// </para>
    /// </remarks>
    private static CurrentStopWorklistProjection Worklist(
        JourneyStopRow stop,
        IReadOnlyList<JourneyStopDemand> demands,
        long revision,
        DateTimeOffset? stationDepartureDeadlineAt) => new(
            stop.StationId,
            revision,
            stop.OperationSessionId,
            stationDepartureDeadlineAt,
            [.. demands.Select(item => new CurrentStopWorklistItem(
                item.Demand.DemandId,
                item.Demand.TransportDemandKey,
                item.Demand.Sublot,
                item.Demand.WorkType,
                // 协议这一栏说的是「在这个停靠上对这条需求做什么」：取货停靠装货，卸货停靠卸货。
                stop.StopRole == JourneyStopRoles.Pickup ? "PICKUP" : "DROPOFF",
                item.Membership.ExpectedBasketCount))]);

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

    /// <summary>
    /// 车辆业务状态这条流在这个停靠上发的是第几号：每个停靠各发一张，所以是基准加序位。
    /// </summary>
    /// <remarks>
    /// 在批次7-03 之前这个偏移写死在两个发布方法里——取货那段发基准，关卡那段发基准 +1。写死的 +1 就是「这趟只有两个
    /// 停靠」这个假设本身。单需求两停靠下这里算出来的还是基准与基准 +1。
    /// <b>清单不走这条算式</b>（批次7-06）：一个停靠上的清单会发不止一版，见 <see cref="WorklistRevisionAt"/>。
    /// </remarks>
    private static long StopRevision(long journeyBase, JourneyStopRow stop) =>
        journeyBase + stop.Sequence - 1;

    /// <summary>
    /// 计划流在这一刻发的是第几号。它比车辆业务状态那条多一张：派车时先发一张「车还在路上」的计划
    /// （CV-DEMAND-ACCEPT-TO-PICKUP），之后每到一个停靠再发一张。所以号数就是「到站几次」——派车时零次，
    /// 取货到站一次，卸货到站两次。
    /// </summary>
    private static long PlanRevisionAt(long journeyBase, JourneyStopRow stop, bool arrivedAtStop) =>
        journeyBase + (arrivedAtStop ? stop.Sequence : stop.Sequence - 1);

    /// <summary>
    /// 一个停靠的离站核验身份。今天只有取货停靠有（卸货停靠是旅程的终点，没有「离开之前」可言），所以取不到就是
    /// 在一个不该问离站安全的停靠上问了。
    /// </summary>
    private static string DepartureCheckId(JourneyStopRow stop) =>
        stop.DepartureSafetyCheckId
        ?? throw new InvalidDataException($"Stop '{stop.StopId}' has no pre-departure safety check id.");

    private static string DepartureCheckMessageId(JourneyStopRow stop) =>
        stop.DepartureSafetyCheckMessageId
        ?? throw new InvalidDataException($"Stop '{stop.StopId}' has no pre-departure safety check message id.");

    private static string PickupDispatchPlanMessageId(JourneyRuntimeRow runtime) =>
        JourneyPlanBuilder.StableGuid(runtime.DemandId, "pickup-dispatch-plan");

    /// <summary>
    /// 车离开当前停靠之后要去的那一个：序位上紧接着的、还开着的停靠（批次7-06，control-server#211）。
    /// </summary>
    /// <remarks>
    /// 取不到就是在最后一个停靠上问了「下一段腿去哪」——那一刻旅程该结束而不是再开一张订单，所以响亮地停下。
    /// </remarks>
    private static JourneyStopRow NextStopAfterCurrent(JourneyStopCursor stops) =>
        stops.OpenStops.FirstOrDefault(stop => stop.Sequence > stops.Current.Sequence)
        ?? throw new InvalidDataException(
            $"Stop {stops.Current.StopId} is the last one; there is no next leg to depart on.");

    /// <summary>
    /// 这个停靠上有没有一条还没结的扫码前取消。
    /// </summary>
    /// <remarks>
    /// 按<b>停靠</b>问而不是按需求（批次7-06）：取消要车证明那一排仓位是空的，那是一次整排的证明，所以一条需求的取消
    /// 开着，这个停靠上的每一条都等着——既不开仓，也不让站点期限结束这个停靠。
    /// </remarks>
    private async Task<bool> OpenCancellationAtCurrentStopAsync(
        JourneyStopCursor stops,
        CancellationToken cancellationToken)
    {
        foreach (JourneyStopDemand item in stops.CurrentStopDemands)
        {
            if (await LoadCancellationBeforeSublot
                    .HasOpenCancellationAsync(dbContext, item.Demand.DemandId, cancellationToken)
                    .ConfigureAwait(false))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>被跟踪的那一份归属行，用来写状态；游标读出来的是 <c>AsNoTracking</c> 的。</summary>
    private Task<JourneyDemandRow> TrackedMembershipAsync(
        JourneyRuntimeRow runtime,
        string demandId,
        CancellationToken cancellationToken) =>
        dbContext.Set<JourneyDemandRow>()
            .SingleAsync(row => row.JourneyId == runtime.JourneyId && row.DemandId == demandId, cancellationToken);

    /// <summary>被跟踪的那一份停靠行，用来写状态。</summary>
    private Task<JourneyStopRow> TrackedStopAsync(string stopId, CancellationToken cancellationToken) =>
        dbContext.Set<JourneyStopRow>().SingleAsync(row => row.StopId == stopId, cancellationToken);

    /// <summary>
    /// 这趟旅程有权补发的消息，按停靠与归属枚举出来。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 在批次7-03 之前这里是写死的 11 个 id。单需求两个停靠下枚举出来的仍是那 11 个：每个停靠贡献车辆业务状态、清单、
    /// 计划三条，取货停靠另有录入请求与离站核验两条，每条需求贡献装、卸两条命令，再加上派车时那张现算的计划。
    /// </para>
    /// <para>
    /// <b>这个集合少一个就是一条报文再也不补发</b>，车载端会一直等一个不会来的东西；多一个或换了 id，车载端会当成
    /// 没见过的新消息。<c>Batch7StopDrivenAdvanceWireParityTests</c> 的重连那条用例钉的就是它。
    /// </para>
    /// <para>
    /// 离站核验过期重发会换一对新 id，写回停靠行（见 <see cref="ReissueExpiredDepartureCheckAsync"/>），所以这里读停靠行
    /// 读到的就是当前那一对。归属取 <see cref="JourneyStopCursor.AllDemands"/> 而不是未终结的那一份：一条需求终结
    /// 不会让它的装卸命令变成别人的消息。
    /// </para>
    /// </remarks>
    private static HashSet<string> RuntimeMessageIds(JourneyRuntimeRow runtime, JourneyStopCursor stops)
    {
        HashSet<string> ids = [PickupDispatchPlanMessageId(runtime)];
        foreach (JourneyStopRow stop in stops.Stops)
        {
            ids.Add(stop.VehicleBusinessMessageId);
            ids.Add(stop.PlanMessageId);
            // 清单与录入请求在一个停靠上可能发不止一版（批次7-06）：挂几条需求就有几版，每一版一个 id。全部枚举出来
            // ——少一个就是一条报文再也不补发，而这个集合的每一项在发件箱里不一定有行，多出来的项不会让任何东西发出去。
            // 计划在一个停靠上也可能发不止一版（途中追加改写了序列）。重发版的 id 只与停靠和修订号有关，
            // 枚举它们不需要知道第一版是从哪个发布点来的。
            for (long revision = 1; revision <= EnRouteAppendPlanner.MaximumLegs * 2; revision++)
            {
                ids.Add(JourneyPlanBuilder.StableGuid($"{stop.StopId}|{revision}", "plan"));
            }

            long baseRevision = stops.FirstWorklistRevisionAt(runtime.WorklistRevision, stop);
            for (long offset = 0; offset < stops.WorklistVersionsOf(stop); offset++)
            {
                ids.Add(stops.WorklistMessageIdAt(runtime.WorklistRevision, stop, baseRevision + offset));
                if (stop.SublotRequestMessageId is not null)
                {
                    ids.Add(stops.SublotRequestMessageIdAt(runtime.WorklistRevision, stop, baseRevision + offset));
                }
            }
            if (stop.DepartureSafetyCheckMessageId is { } departureCheck)
            {
                ids.Add(departureCheck);
            }
        }
        foreach (JourneyStopDemand demand in stops.AllDemands)
        {
            ids.Add(demand.Membership.LoadCommandMessageId);
            ids.Add(demand.Membership.UnloadCommandMessageId);
        }

        return ids;
    }

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
        // attempt 取「此刻在做的那一条」的（批次7-06）：装的那条由录入决定，卸的那条由归属先后决定。
        JourneyStopCursor stops = await JourneyStopCursor.LoadAsync(dbContext, runtime, cancellationToken)
            .ConfigureAwait(false);
        (string? attemptId, string reason) = runtime.Stage switch
        {
            JourneyRuntimeStage.AwaitingLoadResult =>
                (stops.LoadingAtCurrentStop?.Membership.LoadSlotOperationAttemptId, "LOAD_RESULT_REQUIRES_RECOVERY"),
            JourneyRuntimeStage.AwaitingUnloadResult =>
                (stops.NextToUnloadAtCurrentStop?.Membership.UnloadSlotOperationAttemptId,
                    "UNLOAD_RESULT_REQUIRES_RECOVERY"),
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
        string.Equals(runtime.BlockReasonCode, AreaEndAdmissionHeldReason, StringComparison.Ordinal);

    private static void Block(JourneyRuntimeRow runtime, string reason, DateTimeOffset now)
    {
        runtime.Stage = JourneyRuntimeStage.Blocked;
        runtime.SetBlockReason(reason, now);
        runtime.UpdatedAt = now;
    }

    /// <summary>
    /// Hands a stop held at its AREA machine to a person once the wait for the station's admission has run past
    /// <see cref="JourneyRuntimeOptions.AreaEndAdmissionRevokedTimeout"/>, counted from
    /// <see cref="JourneyRuntimeRow.AreaEndAdmissionRevokedSince"/> (control-server#228). Returns whether it did; the caller
    /// saves.
    /// </summary>
    /// <remarks>
    /// control-server#198, decided by the user on 2026-09-19: the vehicle is loaded and nobody is told while it waits, so
    /// past the threshold the journey is Blocked under <see cref="AreaEndAdmissionRevokedTimeoutReason"/>. Only the journey
    /// changes: the order that brought the vehicle here is not touched, and no new one is created. Whatever code the
    /// journey carries this round -- the hold, a failed order, a checkpoint wait, none -- the block is the escalated hold,
    /// and it starts where the wait started, which is what the dashboard's escalation ladder is measured from. The start
    /// stays on the row: it is cleared only by the admission returning.
    /// <para>
    /// A prepared unload is the admission having returned: it is frozen with the unload, and the stage save that follows
    /// is what clears the start. A restart between the two leaves the start on a row still at AwaitingGateArrival, and
    /// escalating it would hand the journey to a person with an unload command already out; the arrival path below goes
    /// on under the frozen admission instead, and clears it.
    /// </para>
    /// </remarks>
    private async Task<bool> EscalateAreaEndAdmissionRevokedPastTimeoutAsync(
        JourneyRuntimeRow runtime,
        JourneyStopCursor stops,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (runtime.AreaEndAdmissionRevokedSince is not DateTimeOffset revokedSince ||
            now - revokedSince < runtimeOptions.AreaEndAdmissionRevokedTimeout ||
            await UnloadPreparedAsync(stops, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }
        runtime.Stage = JourneyRuntimeStage.Blocked;
        runtime.SetBlockReason(AreaEndAdmissionHeldReason, revokedSince);
        runtime.EscalateBlockReason(AreaEndAdmissionRevokedTimeoutReason);
        runtime.UpdatedAt = now;
        LogAreaEndAdmissionRevokedTimeout(
            logger,
            runtime.AgvId,
            stops.Current.StationId,
            runtime.DemandId,
            runtimeOptions.AreaEndAdmissionRevokedTimeout,
            null);
        return true;
    }

    /// <summary>
    /// Whether this journey's unload at the AREA machine has already been prepared: the attempt has a
    /// <c>StationOperations</c> row, which is written with the admission frozen on it (ADR-cross-0050/0051).
    /// </summary>
    private Task<bool> UnloadPreparedAsync(JourneyStopCursor stops, CancellationToken cancellationToken) =>
        stops.NextToUnloadAtCurrentStop is { } next
            ? dbContext.StationOperations.AsNoTracking()
                .AnyAsync(row => row.SlotOperationAttemptId == next.Membership.UnloadSlotOperationAttemptId,
                    cancellationToken)
            : Task.FromResult(false);

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
        string stationId,
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
                logger, runtime.AgvId, runtime.DemandId, stationId, deadline, null);
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
        JourneyStopCursor stops,
        JourneyStopDemand loading,
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
        if (await DeterminateLoadFailureTerminalReasonAsync(loading, cancellationToken).ConfigureAwait(false)
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
                .SingleAsync(row => row.DemandId == loading.Demand.DemandId, cancellationToken).ConfigureAwait(false);
            await dbContext.Entry(demand).ReloadAsync(cancellationToken).ConfigureAwait(false);
            // The one definition the sublot deadline and the cancellation's authorization use too. A cancellation
            // that did not reconcile is not "open", but it has already put the demand in RecoveryRequired and
            // the journey in Blocked, which the checks below refuse on their own.
            bool cancellationOpen = await LoadCancellationBeforeSublot
                .HasOpenCancellationAsync(dbContext, loading.Demand.DemandId, cancellationToken)
                .ConfigureAwait(false);
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
                .SingleOrDefaultAsync(
                    row => row.MessageId == loading.Membership.LoadCommandMessageId, cancellationToken)
                .ConfigureAwait(false);
            if (loadCommand is not null)
            {
                loadCommand.AcknowledgedAt ??= now;
            }
            // 终结的是「装失败的那一条」（批次7-06）。旅程还带着别的需求时，第二步（关闭旅程）不跑，下一轮回到
            // 当前停靠继续推进。
            await new PickupStopTermination(dbContext).StageAsync(
                runtime, stops.CurrentSublotRequestMessageId(runtime.WorklistRevision), loading.Demand.DemandId, terminalReason, now,
                cancellationToken).ConfigureAwait(false);
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        checkpointWaits.Clear(runtime.VehicleKey);
        LogDeterminateLoadFailureSettled(
            logger, runtime.AgvId, loading.Demand.DemandId, terminalReason, null);
        return true;
    }

    /// <summary>
    /// The terminal reason the live result of this journey's load names, read from the result as the vehicle
    /// sent it.
    /// </summary>
    private async Task<string?> DeterminateLoadFailureTerminalReasonAsync(
        JourneyStopDemand loading,
        CancellationToken cancellationToken)
    {
        string attemptId = loading.Membership.LoadSlotOperationAttemptId;
        string? resultId = await dbContext.OperationResults.AsNoTracking()
            .Where(row => row.SlotOperationAttemptId == attemptId &&
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
        JourneyStopCursor stops,
        SessionRecoveryRow session,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (session.DepartureSafe != true || session.SafetyRevision is not long currentRevision)
        {
            return false;
        }

        ProtocolOutboxRow? check = await dbContext.ProtocolOutbox
            .SingleOrDefaultAsync(row => row.MessageId == DepartureCheckMessageId(stops.Current), cancellationToken)
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
                if (RequiredString(payload, "preDepartureSafetyCheckId") != DepartureCheckId(stops.Current))
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
        // 换一对新身份，派生算法照旧（control-server#208 只改「从哪里读」）。写三处：库里的停靠行是权威，旅程行那两列
        // 跟着写，本轮 cursor 手上那个快照实例也跟着改——这一轮重发完会原地再判一次答复，判的必须是新身份。
        string reissuedCheckId = JourneyPlanBuilder.StableGuid(
            DepartureCheckId(stops.Current), "reissued-after-expiry");
        string reissuedMessageId = JourneyPlanBuilder.StableGuid(
            DepartureCheckMessageId(stops.Current), "reissued-after-expiry");
        JourneyStopRow trackedStop = await dbContext.Set<JourneyStopRow>()
            .SingleAsync(row => row.StopId == stops.Current.StopId, cancellationToken).ConfigureAwait(false);
        trackedStop.DepartureSafetyCheckId = reissuedCheckId;
        trackedStop.DepartureSafetyCheckMessageId = reissuedMessageId;
        stops.Current.DepartureSafetyCheckId = reissuedCheckId;
        stops.Current.DepartureSafetyCheckMessageId = reissuedMessageId;
        // 旅程行那两列今天是重发后的权威，本票把权威搬到停靠行，但仍然照旧写它：库里落下的状态因此一字未变，
        // 而票面之外的读者（如果还有）也读不到过期的值。它们在 批次7-06 随多停靠退役。
        runtime.PreDepartureSafetyCheckId = reissuedCheckId;
        runtime.PreDepartureSafetyCheckMessageId = reissuedMessageId;
        await publisher.PublishPreDepartureSafetyCheckAsync(
            reissuedMessageId,
            runtime.AgvId,
            session.SessionGeneration,
            new PreDepartureSafetyCheckCommand(
                reissuedCheckId,
                runtime.DemandId,
                NextStopAfterCurrent(stops).MovementLegId,
                currentRevision,
                NextStopAfterCurrent(stops).StationId),
            cancellationToken).ConfigureAwait(false);
        runtime.SetBlockReason("PREDEPARTURE_CHECK_EXPIRED", now);
        runtime.UpdatedAt = now;
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }



}
