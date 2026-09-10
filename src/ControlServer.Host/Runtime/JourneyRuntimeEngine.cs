using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ControlServer.Host.Runtime;

public sealed class JourneyRuntimeEngine(
    ControlServerDbContext dbContext,
    IMesIngestCatalog catalog,
    ISublotBoxCountReader boxCountReader,
    IPackageCapacityStore packageCapacityStore,
    IRiotVehicleFacts vehicleFacts,
    IRiotMapStationCatalog mapStationCatalog,
    MapStationResolver stationResolver,
    JourneyIntakeCoordinator intakeCoordinator,
    MovementDispatchService movementDispatch,
    WireToGateStore store,
    OnboardJourneyPublisher publisher,
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
    private static readonly Action<ILogger, Exception?> LogMapStationCatalogFailed = LoggerMessage.Define(
        LogLevel.Warning,
        new EventId(2103, nameof(LogMapStationCatalogFailed)),
        "RIoT Map station catalog failed closed; no new journey action was taken.");
    private static readonly Action<ILogger, string, TimeSpan, Exception?> LogStationTimeoutDoorNotClosed =
        LoggerMessage.Define<string, TimeSpan>(
            LogLevel.Warning,
            new EventId(2109, nameof(LogStationTimeoutDoorNotClosed)),
            "Station {StationId} passed its {SublotWaitTimeout} departure deadline with a slot door " +
            "still open. The stop is NOT closed: the vehicle cannot depart with an open door, so it " +
            "keeps waiting for the door to be closed by hand.");
    private static readonly Action<ILogger, string, TimeSpan, Exception?> LogSublotWaitTimedOut =
        LoggerMessage.Define<string, TimeSpan>(
            LogLevel.Information,
            new EventId(2104, nameof(LogSublotWaitTimedOut)),
            "Demand {DemandId} was cancelled: no sublot was entered within {SublotWaitTimeout}.");
    private static readonly Action<ILogger, string, int, string, Exception?> LogAutoChargingStarted =
        LoggerMessage.Define<string, int, string>(
            LogLevel.Information,
            new EventId(2105, nameof(LogAutoChargingStarted)),
            "Charging run {ChargingRunId} dispatched at {BatteryPercent}% to station {ChargerStationId}.");
    private static readonly Action<ILogger, string, int, Exception?> LogAutoChargingCompleted =
        LoggerMessage.Define<string, int>(
            LogLevel.Information,
            new EventId(2106, nameof(LogAutoChargingCompleted)),
            "Charging run {ChargingRunId} released the vehicle at {BatteryPercent}%.");
    private static readonly Action<ILogger, string, Exception?> LogChargerStationUnresolved =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(2107, nameof(LogChargerStationUnresolved)),
            "Charger station could not be resolved on the current map ({ReasonCode}); no charging run was started.");
    private static readonly Action<ILogger, string, string, Exception?> LogSublotRejected =
        LoggerMessage.Define<string, string>(
            LogLevel.Information,
            new EventId(2108, nameof(LogSublotRejected)),
            "Entered sublot for demand {DemandId} was refused: {ReasonCode}.");
    /// <summary>
    /// Where the pickup stops sit in a journey's stop sequence, and where the gate sits.
    /// </summary>
    /// <remarks>
    /// The gate keeps a fixed sequence above every pickup stop so that appending a pickup stop
    /// never has to renumber it -- the sequence is half of the stop row's primary key, and moving it
    /// would mean deleting and re-inserting the stop the vehicle is driving to. The protocol's plan
    /// snapshot is numbered separately, contiguously, from the sorted order, so the wire never shows
    /// the gap this leaves. Eight pickup stops is the vehicle's slot count: a demand occupies at
    /// least one slot, so no journey can load at more stops than that.
    /// </remarks>
    private const int FirstStopSequence = 1;
    private const int MaxPickupStops = 8;
    private const int GateStopSequence = MaxPickupStops + 1;

    private readonly JourneyRuntimeOptions runtimeOptions = options.Value;

    public async Task ExecuteOnceAsync(CancellationToken cancellationToken)
    {
        if (!runtimeOptions.Enabled)
        {
            return;
        }

        RiotMapStationCatalogSnapshot currentMap;
        RiotMapStation gate;
        IReadOnlyList<RiotMapStation> machineStations;
        try
        {
            currentMap = await mapStationCatalog.ReadMapStationsAsync(
                runtimeOptions.MapId, cancellationToken).ConfigureAwait(false);
            gate = stationResolver.RequireFixedStation(
                currentMap, runtimeOptions.GateStationRiotId, runtimeOptions.GateStationId);
            machineStations = stationResolver.ParseAreaNamedMachineStations(currentMap);
        }
        catch (Exception error) when (
            error is HttpRequestException or InvalidDataException or JsonException or StationResolutionException)
        {
            LogMapStationCatalogFailed(logger, error);
            return;
        }

        await store.ApplyAdmissionPolicyAsync(
            new AdmissionPolicyDefinition(
                runtimeOptions.AdmissionPolicyVersion,
                runtimeOptions.AdmissionPolicyDeploymentId,
                machineStations.Select(station => station.StationName)
                    .Distinct(StringComparer.Ordinal)
                    .Select(stationName => new StationTaskTypeAdmission(stationName, "WIRE_TO_GATE"))
                    .ToArray(),
                timeProvider.GetUtcNow()),
            cancellationToken).ConfigureAwait(false);

        JourneyRuntimeRow[] active = await dbContext.JourneyRuntimes
            .Where(row => row.Stage != JourneyRuntimeStage.Completed)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        active = active.OrderBy(row => row.CreatedAt).ToArray();
        if (active.Length > 1)
        {
            throw new BusinessIdentityConflictException("More than one unresolved journey exists for the single-vehicle runtime.");
        }
        if (active.Length == 0)
        {
            string[] unresolvedDemandIds = await dbContext.AcceptedDemands
                .Where(row => row.Status != DemandExecutionStatus.Succeeded &&
                              row.Status != DemandExecutionStatus.Cancelled)
                .Select(row => row.DemandId)
                .ToArrayAsync(cancellationToken).ConfigureAwait(false);
            string[] runtimeDemandIds = await dbContext.JourneyDemands
                .Select(row => row.DemandId)
                .ToArrayAsync(cancellationToken).ConfigureAwait(false);
            string[] orphaned = unresolvedDemandIds.Except(runtimeDemandIds, StringComparer.Ordinal).ToArray();
            if (orphaned.Length > 0)
            {
                throw new BusinessIdentityConflictException(
                    $"Unresolved accepted demand has no production journey runtime: {string.Join(',', orphaned)}.");
            }
            if (await AdvanceAutoChargingAsync(currentMap, cancellationToken).ConfigureAwait(false))
            {
                return;
            }
            await DiscoverAndAcceptAsync(currentMap, gate, cancellationToken).ConfigureAwait(false);
            return;
        }

        await AdvanceAsync(active[0], currentMap, gate, cancellationToken).ConfigureAwait(false);
    }

    private async Task DiscoverAndAcceptAsync(
        RiotMapStationCatalogSnapshot currentMap,
        RiotMapStation gate,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        // Nothing of this journey exists yet, so no slot is reserved and every available one counts.
        CandidateScoring scoring = await ScoreCandidatesAsync(
            currentMap, gate, [], now, cancellationToken).ConfigureAwait(false);
        if (scoring.Onboard is null || scoring.Eligible.Count == 0)
        {
            return;
        }
        if (await dbContext.VehicleDispatchLeases.AnyAsync(
                row => row.VehicleKey == runtimeOptions.VehicleKey && row.ReleasedAt == null,
                cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        EligibleCandidate selected = Preferred(scoring.Eligible);
        long expectedSessionGeneration = scoring.Onboard.SessionGeneration;
        if (!await FinalDynamicFactsReadyAsync(
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
        JourneyExecutionPlan plan = CreatePlan(selected, intakeAt);
        JourneyStopPlan firstStop = plan.Stops.Single(stop => stop.Sequence == FirstStopSequence);
        OrderIntent pickup = new(
            firstStop.MovementLegId,
            selected.Snapshot.DemandId,
            firstStop.UpperId,
            "TO_PICKUP",
            firstStop.StationId,
            intakeAt,
            plan.VehicleKey,
            plan.MapId,
            firstStop.StationRiotId,
            plan.AgvLifecycleGeneration,
            plan.DispatchGeneration);
        JourneyIntakeResult result = await intakeCoordinator.AcceptAndDispatchToPickupAsync(
            selected.Snapshot,
            pickup,
            plan,
            token => FinalDynamicFactsReadyAsync(
                expectedSessionGeneration,
                selected.TargetSlots,
                token),
            cancellationToken).ConfigureAwait(false);
        if (result.IntakeOutcome != DemandIntakeOutcome.Accepted)
        {
            await SetBacklogReasonAsync(
                selected.Snapshot.DemandId,
                BacklogReasonFor(result.IntakeOutcome),
                timeProvider.GetUtcNow(),
                cancellationToken).ConfigureAwait(false);
            return;
        }
        if (result.MovementDispatch?.Outcome != MovementDispatchOutcome.Confirmed)
        {
            JourneyRuntimeRow runtime = await dbContext.JourneyRuntimes
                .SingleAsync(row => row.JourneyId == plan.JourneyId, cancellationToken)
                .ConfigureAwait(false);
            runtime.BlockReasonCode = result.MovementDispatch?.Outcome.ToString() ?? "PICKUP_DISPATCH_NOT_CONFIRMED";
            runtime.UpdatedAt = now;
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The stable order candidates are taken in: oldest in the backlog first, then oldest demand,
    /// then by id. Ties have to break the same way on every poll or the vehicle would be sent to a
    /// different stop each time the catalog is re-read.
    /// </summary>
    private static EligibleCandidate Preferred(IEnumerable<EligibleCandidate> eligible) => eligible
        .OrderBy(item => item.FirstSeenAt)
        .ThenBy(item => item.Snapshot.CreatedAt)
        .ThenBy(item => item.Snapshot.DemandId, StringComparer.Ordinal)
        .First();

    private static string BacklogReasonFor(DemandIntakeOutcome outcome) => outcome switch
    {
        DemandIntakeOutcome.CandidateGone => "FINAL_CATALOG_CANDIDATE_GONE",
        DemandIntakeOutcome.CandidateChanged => "FINAL_CATALOG_DECISION_FACT_CHANGED",
        DemandIntakeOutcome.FinalAdmissionRejected => "FINAL_DYNAMIC_FACTS_NOT_READY",
        _ => throw new InvalidOperationException($"Unsupported intake outcome '{outcome}'.")
    };

    /// <summary>
    /// Scores every demand in the catalog and records the verdict in the backlog, returning the ones
    /// that may be dispatched. <paramref name="reservedSlots"/> is what the current journey has
    /// already promised: Onboard reports slot availability from the session baseline and never takes
    /// a loaded slot out of it, so the free set is the server's own ledger, not the vehicle's.
    /// </summary>
    private async Task<CandidateScoring> ScoreCandidatesAsync(
        RiotMapStationCatalogSnapshot currentMap,
        RiotMapStation gate,
        IReadOnlyCollection<int> reservedSlots,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        DemandCatalogSnapshot snapshot;
        try
        {
            snapshot = await catalog.ReadCatalogAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is HttpRequestException or InvalidDataException or JsonException)
        {
            LogCatalogPollFailed(logger, error);
            return new CandidateScoring([], null, false);
        }

        OnboardFacts? onboard = await ReadOnboardFactsAsync(cancellationToken).ConfigureAwait(false);
        RiotVehicleObservation vehicle = await vehicleFacts.ReadVehicleAsync(
            runtimeOptions.VehicleKey, cancellationToken).ConfigureAwait(false);
        DateTimeOffset dynamicFactsNow = timeProvider.GetUtcNow();
        Dictionary<string, JourneyBacklogRow> backlogByDemandId = await dbContext.JourneyBacklog
            .ToDictionaryAsync(row => row.DemandId, StringComparer.Ordinal, cancellationToken)
            .ConfigureAwait(false);
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
        HashSet<string> acceptedDemandIds = (await dbContext.AcceptedDemands
                .Select(row => row.DemandId)
                .ToArrayAsync(cancellationToken).ConfigureAwait(false))
            .ToHashSet(StringComparer.Ordinal);
        HashSet<string> suppressedKeys = await store
            .ReadSuppressedTransportDemandKeysAsync(cancellationToken).ConfigureAwait(false);
        List<EligibleCandidate> eligible = [];
        // Whether some candidate was refused for capacity alone. That is what "the vehicle is full"
        // means, and it is only knowable here: it takes the candidate's own basket count.
        bool slotStarved = false;
        foreach (AcceptedDemandSnapshot candidate in snapshot.Items)
        {
            string reason = "ELIGIBLE";
            ResolvedJourneyRoute? route = null;
            int expectedBasketCount = 0;
            int? packageCapacity = null;
            int[] targetSlots = [];
            if (acceptedDemandIds.Contains(candidate.DemandId))
            {
                // Ahead of every other gate: an accepted demand can never be taken again, so the
                // route, package, box-count and vehicle reads the gates below perform would be
                // spent on a decision that is already made.
                reason = "DEMAND_ALREADY_ACCEPTED";
            }
            else if (suppressedKeys.Contains(candidate.TransportDemandKey))
            {
                // Keyed on the business identity, so it also catches the fresh DemandId MesIngest
                // allocates when the same demand disappears from its catalog and returns -- which
                // is the only way a cancelled demand can come back at all. It never expires and
                // there is no lifting entry point in this version.
                reason = "TRANSPORT_DEMAND_SUPPRESSED";
            }
            else if (!runtimeOptions.AllowedWorkTypes.Contains(candidate.WorkType, StringComparer.Ordinal) ||
                !string.Equals(candidate.WorkType, "WIRE_TO_GATE", StringComparison.Ordinal))
            {
                reason = "OUT_OF_SCOPE_WORK_TYPE";
            }
            else if (candidate.LiveMesFields is null ||
                     string.IsNullOrWhiteSpace(candidate.LiveMesFields.Area) ||
                     string.IsNullOrWhiteSpace(candidate.LiveMesFields.Eqp) ||
                     string.IsNullOrWhiteSpace(candidate.LiveMesFields.Package))
            {
                reason = "REQUIRED_MES_FACT_MISSING";
            }
            else if (!candidate.LiveMesFields.Area.StartsWith('N'))
            {
                reason = "OUT_OF_SCOPE_AREA";
            }
            else
            {
                string[] areaEqps = snapshot.Items.Where(item =>
                        string.Equals(item.WorkType, "WIRE_TO_GATE", StringComparison.Ordinal) &&
                        string.Equals(item.LiveMesFields?.Area, candidate.LiveMesFields.Area, StringComparison.Ordinal) &&
                        !string.IsNullOrWhiteSpace(item.LiveMesFields?.Eqp))
                    .Select(item => item.LiveMesFields!.Eqp!)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                if (areaEqps.Length != 1 ||
                    !string.Equals(areaEqps[0], candidate.LiveMesFields.Eqp, StringComparison.Ordinal))
                {
                    reason = "AREA_EQP_NOT_UNIQUE";
                }
                else
                {
                    try
                    {
                        RiotMapStation resolvedPickup = stationResolver.ResolveUniquePickup(
                            currentMap, candidate.LiveMesFields.Area);
                        route = new ResolvedJourneyRoute(
                            runtimeOptions.DispatchZone,
                            MapStationResolver.BuildRouteEvidenceId(
                                currentMap,
                                resolvedPickup,
                                gate,
                                candidate.LiveMesFields.Area,
                                candidate.LiveMesFields.Eqp),
                            resolvedPickup.StationName,
                            resolvedPickup.StationId);
                        reason = ValidateStaticRoute(route);
                    }
                    catch (StationResolutionException error)
                    {
                        reason = error.ReasonCode;
                    }
                }
            }

            if (reason == "ELIGIBLE" && route is not null)
            {
                packageCapacity = await packageCapacityStore.ResolveAndTrackAsync(
                    candidate.LiveMesFields!.Package!, now, cancellationToken).ConfigureAwait(false);
                if (packageCapacity is null or <= 0)
                {
                    reason = "PACKAGE_CAPACITY_NOT_UNIQUE";
                }
            }

            if (reason == "ELIGIBLE" && route is not null)
            {
                reason = ValidateDynamicFacts(onboard, vehicle, dynamicFactsNow);
            }
            if (reason == "ELIGIBLE" && route is not null &&
                !await store.IsTaskTypeAllowedAsync(
                    route.PickupStationId, candidate.WorkType, cancellationToken).ConfigureAwait(false))
            {
                reason = "TASK_TYPE_NOT_ALLOWED_AT_STATION";
            }
            if (reason == "ELIGIBLE" && route is not null && onboard is not null)
            {
                int? maxBoxCount;
                try
                {
                    maxBoxCount = await boxCountReader.ReadMaxBoxCountAsync(candidate.Sublot, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception error) when (error is HttpRequestException or InvalidDataException or JsonException)
                {
                    LogBoxCountFailed(logger, candidate.DemandId, error);
                    maxBoxCount = null;
                }
                if (maxBoxCount is null or <= 0)
                {
                    reason = "SUBLOT_BOX_COUNT_UNAVAILABLE";
                }
                else
                {
                    int capacity = packageCapacity
                        ?? throw new InvalidOperationException("Eligible PACKAGE must have a frozen capacity.");
                    expectedBasketCount = checked((maxBoxCount.Value + capacity - 1) / capacity);
                    if (expectedBasketCount is < 1 or > 8)
                    {
                        reason = "EXPECTED_BASKET_COUNT_OUT_OF_RANGE";
                    }
                    else
                    {
                        int[] free = onboard.AvailableSlots
                            .Except(reservedSlots)
                            .Order()
                            .ToArray();
                        if (free.Length < expectedBasketCount)
                        {
                            // ADR-cross-0057's "full": not every slot occupied, but too few free to
                            // take this candidate whole. A demand's baskets go on in one go or not
                            // at all.
                            reason = "SLOT_CAPACITY_TEMPORARILY_UNAVAILABLE";
                            slotStarved = true;
                        }
                        else
                        {
                            targetSlots = free.Take(expectedBasketCount).ToArray();
                        }
                    }
                }
            }

            JourneyBacklogRow backlog = UpsertBacklog(backlogByDemandId, candidate, reason, now);
            if (reason == "ELIGIBLE" && route is not null)
            {
                eligible.Add(new EligibleCandidate(candidate, route, expectedBasketCount, targetSlots, backlog.FirstSeenAt));
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new CandidateScoring(eligible, onboard, slotStarved);
    }

    private async Task AdvanceAsync(
        JourneyRuntimeRow runtime,
        RiotMapStationCatalogSnapshot currentMap,
        RiotMapStation gate,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        JourneyStopRow[] stops = await StopsAsync(runtime, cancellationToken).ConfigureAwait(false);
        JourneyDemandRow[] demands = await DemandsAsync(runtime, cancellationToken).ConfigureAwait(false);
        JourneyStopRow stop = stops.Single(row => row.Sequence == runtime.CurrentStopSequence);
        SessionRecoveryRow? session = await CurrentReadySessionAsync(runtime.AgvId, cancellationToken)
            .ConfigureAwait(false);
        if (session is null)
        {
            // A result that is already durable names the recovery this journey waits on, and it
            // names it from StationOperations alone -- reading it involves no session. Deciding
            // that here, ahead of the readiness gate, is what keeps the gate from closing on
            // exactly the journeys that most need their block named: since 147f02c a refused
            // result moves the session itself to RecoveryRequired, so the vehicle that just failed
            // a load is the one whose session is no longer Ready. L2 run
            // 20260904-recovery-entry-after-announce-001 finished on that deadlock, sitting at
            // AwaitingLoadResult / ONBOARD_SESSION_NOT_READY over a load result the database
            // already held.
            if (await TryBlockOnRecordedRecoveryAsync(runtime, demands, now, cancellationToken)
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
            if (runtime.Stage != JourneyRuntimeStage.Blocked)
            {
                runtime.BlockReasonCode = "ONBOARD_SESSION_NOT_READY";
                runtime.UpdatedAt = now;
                await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            return;
        }
        await publisher.ReplayPendingForSessionAsync(
            runtime.AgvId,
            session.SessionGeneration,
            RuntimeMessageIds(stops, demands),
            cancellationToken).ConfigureAwait(false);

        switch (runtime.Stage)
        {
            case JourneyRuntimeStage.AwaitingPickupArrival:
                if (!await EnsureMovementConfirmedAsync(
                        runtime, stop.UpperId, "PICKUP", cancellationToken).ConfigureAwait(false))
                {
                    return;
                }
                if (!await IsTrustedArrivalAsync(runtime, stop, session, cancellationToken).ConfigureAwait(false))
                {
                    return;
                }
                stop.State = JourneyStopState.Arrived;
                await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                // Everything the catalog offers at this stop goes into the first worklist, rather
                // than one demand now and the rest one batch at a time. ADR-cross-0048 describes a
                // stop's worklist as what is waiting *there*, plural.
                (stops, demands, stop) = await FillJourneyAsync(
                    runtime, stops, stop, demands, currentMap, gate, now, cancellationToken)
                    .ConfigureAwait(false);
                await PublishStopArrivalAsync(runtime, stops, stop, demands, session, now, cancellationToken)
                    .ConfigureAwait(false);
                SetStage(runtime, JourneyRuntimeStage.AwaitingSublot, now, stop);
                break;
            case JourneyRuntimeStage.AwaitingSublot:
                // Refills the station departure deadline after a disconnect voided it
                // (ADR-cross-0055). SetStage only seeds it on the way into this stage, and a
                // reconnect does not re-enter the stage -- the journey was already here. Reaching
                // this line means the session is Ready again, so the handshake and the projection
                // reconciliation the ADR requires are behind us.
                bool deadlineRefilled = stop.SublotWaitStartedAt is null;
                stop.SublotWaitStartedAt ??= now;
                JourneyDemandRow[] pending = UncommandedAt(demands, stop);
                // Waiting for an operator is where the holding limit actually bites: the vehicle
                // stands loaded while nobody scans. Nothing is commanded in this stage, so leaving
                // now interrupts no slot operation.
                if (pending.Length == 0 || HoldingExpired(runtime, now))
                {
                    // Nothing left to load here. Whether that means the next pickup stop or the gate
                    // is one decision, made in one place.
                    await ConcludeLoadingStopAsync(
                        runtime, stops, stop, demands, session, now, cancellationToken).ConfigureAwait(false);
                    break;
                }
                SublotEntry? entry = await FindMatchingSublotAsync(
                    runtime, stop, demands, session, cancellationToken).ConfigureAwait(false);
                if (entry is null)
                {
                    if (await TryTimeOutSublotWaitAsync(
                            runtime, stops, stop, demands, session, now, cancellationToken)
                        .ConfigureAwait(false))
                    {
                        return;
                    }
                    // This is the stage's quiet exit -- nobody scanned yet -- and it is the one
                    // path that reaches a return without saving. A deadline just refilled above
                    // would be lost here and refilled again on every later pass, which is the same
                    // as never expiring.
                    if (runtime.BlockReasonCode is not null || deadlineRefilled)
                    {
                        runtime.UpdatedAt = now;
                        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    }
                    return;
                }
                (ProtocolInboxRow sublot, JourneyDemandRow entered) = entry;
                // BR-013 第 2 节把这次重算的时机写死在「操作员输入 SUBLOT 后」：查 SUBLOT_BOX_COUNT
                // 得到 MAX_BOX_COUNT，结合冻结的 PACKAGE 与已批准的花篮容量对照算出权威数量，算不
                // 出就报错并停止，不分配仓位、不发送开锁指令。受理阶段那次预检拦不住这些——容量
                // 对照表与 MES 的箱数都可能在派车之后变化，而受理时被拦下的需求压根不会进作业清
                // 单，操作员看到的只会是对端本地那句「不在清单里」。
                SublotRejection? rejection = await RevalidateEnteredSublotAsync(
                    runtime, stop, entered, now, cancellationToken).ConfigureAwait(false);
                if (rejection is not null)
                {
                    await publisher.PublishSublotRejectedAsync(
                        StableGuid(sublot.MessageId, "sublot-rejected"),
                        sublot.MessageId,
                        runtime.AgvId,
                        session.SessionGeneration,
                        rejection,
                        cancellationToken).ConfigureAwait(false);
                    // 这条提交判过了，别再判第二次——否则每一轮都要重跑一次远程查询。操作员重扫会
                    // 产生新的 SublotSubmitted，那条会被重新判。
                    entered.ConsumedSublotMessageId = sublot.MessageId;
                    runtime.BlockReasonCode = rejection.ReasonCode;
                    runtime.UpdatedAt = now;
                    await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    LogSublotRejected(logger, entered.DemandId, rejection.ReasonCode, null);
                    return;
                }
                await PublishLoadAsync(runtime, stop, entered, session, sublot.MessageId, cancellationToken)
                    .ConfigureAwait(false);
                entered.ConsumedSublotMessageId = sublot.MessageId;
                entered.LoadCommandedAt = now;
                // The submission is this command's answer. Leaving the command unsettled replayed it
                // into every later session, where the peer refused it as a business id whose content
                // had changed and tore the session down.
                await store.SettleAnsweredCommandAsync(
                    WireToGateStore.SublotRequestId(runtime.JourneyId, stop.Sequence, stop.LoadRound),
                    now,
                    cancellationToken).ConfigureAwait(false);
                SetStage(runtime, JourneyRuntimeStage.AwaitingLoadResult, now, stop);
                break;
            case JourneyRuntimeStage.AwaitingLoadResult:
                if (TerminatedCommandedAt(demands, stop) is not null &&
                    PendingAt(demands, stop).All(row => row.LoadCommandedAt is null))
                {
                    // The demand this stop commanded ended on a terminal path of its own -- an
                    // operator cancellation, a compensation, a fault-cargo handoff -- while the
                    // journey still carries others. That is this batch closing the other way round,
                    // not a broken invariant, and what follows is the same question a completed
                    // batch asks: another round here, the next stop, or the gate. So the stop
                    // finishes through exactly the path a completed batch takes. Nothing was loaded,
                    // so the holding clock is not started here and no command is answered: the
                    // coordinator settled both when it accepted the terminal result.
                    (stops, demands, stop) = await FillJourneyAsync(
                        runtime, stops, stop, demands, currentMap, gate, now, cancellationToken)
                        .ConfigureAwait(false);
                    if (await TryContinueLoadingAtStopAsync(
                            runtime, stop, demands, session, now, cancellationToken).ConfigureAwait(false))
                    {
                        break;
                    }
                    await ConcludeLoadingStopAsync(
                        runtime, stops, stop, demands, session, now, cancellationToken).ConfigureAwait(false);
                    await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    if (runtime.Stage != JourneyRuntimeStage.AwaitingDepartureSafety)
                    {
                        return;
                    }
                    goto case JourneyRuntimeStage.AwaitingDepartureSafety;
                }
                JourneyDemandRow loading = LoadingAt(demands, stop);
                StationOperationRow? load = await dbContext.StationOperations.SingleOrDefaultAsync(
                    row => row.SlotOperationAttemptId == loading.LoadSlotOperationAttemptId,
                    cancellationToken).ConfigureAwait(false);
                if (load?.Status == StationOperationStatus.RecoveryRequired)
                {
                    Block(runtime, "LOAD_RESULT_REQUIRES_RECOVERY", now);
                }
                else if (load?.Status == StationOperationStatus.Failed)
                {
                    // ADR-cross-0058 decision 5. A determinate load failure neither blocks the
                    // journey nor opens a recovery session: the vehicle reported exactly what the
                    // slots look like -- known state, door locked, unlock output reset -- and the
                    // answer was that nobody handed the cargo over. Spelled out rather than left to
                    // the else below, which means something else entirely: no result yet.
                    //
                    // A determinate failure arrives with every door shut, so whatever alarm the open
                    // door raised while the vehicle was still prompting is over -- withdraw it here,
                    // or the stop reads STATION_TIMEOUT_DOOR_NOT_CLOSED against eight locked doors.
                    await ReconcileStationTimeoutDoorNotClosedAsync(
                        runtime, stop, session, now, cancellationToken).ConfigureAwait(false);
                    // And the server ends the demand itself (8005-agv-program#39). This branch used to
                    // return and wait for LoadTaskCancellation, which nothing ever raises: the onboard
                    // offers no cancellation for an attempt whose result is recorded, and neither the
                    // station deadline nor the holding limit is read in this stage. L2 measured the
                    // journey sitting at the stop for good. The vehicle only reports this failure once
                    // the station deadline this server set has expired and one grace round has passed
                    // (#24), so the stop's closing condition already holds when it arrives. With the
                    // demand cancelled the batch has closed the other way round, and the terminal
                    // branch at the top of this case finishes it exactly as it finishes an operator
                    // cancellation: another round here, the next stop, or the gate.
                    await store.CancelDemandAfterDeterminateLoadFailureAsync(
                        loading.DemandId, "CANCELLED_BY_STATION_TIMEOUT", now, cancellationToken)
                        .ConfigureAwait(false);
                    demands = await DemandsAsync(runtime, cancellationToken).ConfigureAwait(false);
                    goto case JourneyRuntimeStage.AwaitingLoadResult;
                }
                else if (load?.Status == StationOperationStatus.Committed)
                {
                    await store.SettleAnsweredCommandAsync(
                        loading.LoadCommandMessageId, now, cancellationToken).ConfigureAwait(false);
                    loading.State = JourneyDemandState.Loaded;
                    loading.LoadedAt = now;
                    // ADR-cross-0057: the holding clock starts at the first LoadBatch that closes
                    // safely, and runs down once for the whole journey. It is not restarted by a
                    // later batch or a later stop -- a vehicle that keeps being given new demands
                    // would otherwise never leave for the gate.
                    runtime.HoldingStartedAt ??= now;
                    await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    // A batch just closed, so the vehicle is safe to take on more -- and whether it
                    // can is exactly what decides between another round here, another stop, and the
                    // gate.
                    (stops, demands, stop) = await FillJourneyAsync(
                        runtime, stops, stop, demands, currentMap, gate, now, cancellationToken)
                        .ConfigureAwait(false);
                    if (await TryContinueLoadingAtStopAsync(
                            runtime, stop, demands, session, now, cancellationToken).ConfigureAwait(false))
                    {
                        break;
                    }
                    await ConcludeLoadingStopAsync(
                        runtime, stops, stop, demands, session, now, cancellationToken).ConfigureAwait(false);
                    await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    if (runtime.Stage != JourneyRuntimeStage.AwaitingDepartureSafety)
                    {
                        return;
                    }
                    // The answer arrives in tens of milliseconds and is stamped with the peer's own
                    // short validity window. Returning here and reading it on the next poll spent
                    // that entire window waiting, so the evidence was always expired by the time it
                    // was judged and the journey never left this stage. Judge it while it is valid.
                    goto case JourneyRuntimeStage.AwaitingDepartureSafety;
                }
                else
                {
                    // No result yet. ADR-cross-0058 decision 4's other half belongs here: the
                    // deadline has passed and a slot door is still open, so the stop cannot close
                    // and nothing about the vehicle says why it is standing there. The vehicle
                    // settles this itself once the door is shut -- it drives the slot to a
                    // determinate failure past the deadline -- but while the door stays open there
                    // is no such moment, and decision 4 wants the vehicle visibly waiting under an
                    // alarm rather than silently stuck. TryTimeOutSublotWaitAsync raises the same
                    // code for the same reason, but it only ever runs in AwaitingSublot, where by
                    // construction no slot operation is underway.
                    await ReconcileStationTimeoutDoorNotClosedAsync(
                        runtime, stop, session, now, cancellationToken).ConfigureAwait(false);
                    return;
                }
                break;
            case JourneyRuntimeStage.AwaitingDepartureSafety:
                JourneyStopRow departingFrom = stop;
                JourneyStopRow target = stops.Single(row =>
                    row.Sequence == (runtime.NextStopSequence ?? throw new InvalidDataException(
                        "A journey awaiting departure safety must name the stop it is leaving for.")));
                SafetyCheckObservation? safety = await AwaitSafeDepartureResultAsync(
                    runtime, departingFrom, session, cancellationToken).ConfigureAwait(false);
                now = timeProvider.GetUtcNow();
                if (safety is null)
                {
                    if (runtime.BlockReasonCode is not null)
                    {
                        runtime.UpdatedAt = now;
                        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    }
                    return;
                }
                OrderIntent departureIntent = StopIntent(
                    runtime, target, demands.OrderBy(row => row.CreatedAt).First().DemandId, now);
                await new WireToGateStore(dbContext).AuthorizeMovementAsync(
                    departureIntent, safety, now, cancellationToken).ConfigureAwait(false);
                MovementDispatchResult dispatch = await movementDispatch.ReconcileOrCreateAsync(
                    target.UpperId, cancellationToken).ConfigureAwait(false);
                departingFrom.ConsumedSafetyResultMessageId = await FindSafetyResultMessageIdAsync(
                    departingFrom.PreDepartureSafetyCheckId, cancellationToken).ConfigureAwait(false);
                await store.SettleAnsweredCommandAsync(
                    departingFrom.PreDepartureSafetyCheckMessageId, now, cancellationToken).ConfigureAwait(false);
                departingFrom.State = JourneyStopState.Completed;
                departingFrom.UpdatedAt = now;
                runtime.CurrentStopSequence = target.Sequence;
                runtime.NextStopSequence = null;
                SetStage(
                    runtime,
                    target.Role == JourneyStopRole.Gate
                        ? JourneyRuntimeStage.AwaitingGateArrival
                        : JourneyRuntimeStage.AwaitingPickupArrival,
                    now,
                    target);
                runtime.BlockReasonCode = dispatch.Outcome == MovementDispatchOutcome.Confirmed
                    ? null
                    : dispatch.Outcome.ToString();
                break;
            case JourneyRuntimeStage.AwaitingGateArrival:
                if (!await EnsureMovementConfirmedAsync(
                        runtime, stop.UpperId, "GATE", cancellationToken).ConfigureAwait(false))
                {
                    return;
                }
                if (!await IsTrustedArrivalAsync(runtime, stop, session, cancellationToken).ConfigureAwait(false))
                {
                    return;
                }
                stop.State = JourneyStopState.Arrived;
                await PublishGateStateAndUnloadAsync(
                    runtime, stops, stop, demands, session, cancellationToken).ConfigureAwait(false);
                SetStage(runtime, JourneyRuntimeStage.AwaitingUnloadResult, now, stop);
                break;
            case JourneyRuntimeStage.AwaitingUnloadResult:
                // The demand whose command went out and has not come off yet. Its state -- not the
                // command flag -- is what moves: the message handler that accepts the peer's result
                // settles the demand to Unloaded, so a null here means the batch closed.
                JourneyDemandRow? unloading = demands.FirstOrDefault(row =>
                    row.State == JourneyDemandState.Loaded && row.UnloadCommandedAt is not null);
                if (unloading is not null)
                {
                    StationOperationRow? unload = await dbContext.StationOperations.SingleOrDefaultAsync(
                        row => row.SlotOperationAttemptId == unloading.UnloadSlotOperationAttemptId,
                        cancellationToken).ConfigureAwait(false);
                    if (unload?.Status == StationOperationStatus.RecoveryRequired)
                    {
                        Block(runtime, "UNLOAD_RESULT_REQUIRES_RECOVERY", now);
                        break;
                    }
                    AcceptedDemandRow demand = await dbContext.AcceptedDemands.SingleAsync(
                        row => row.DemandId == unloading.DemandId, cancellationToken).ConfigureAwait(false);
                    if (unload?.Status != StationOperationStatus.Committed ||
                        demand.Status != DemandExecutionStatus.Succeeded)
                    {
                        return;
                    }
                    // Committed and succeeded, but the demand row still says Loaded: the settlement
                    // has not been applied yet, so there is nothing to conclude on this pass.
                    return;
                }

                JourneyDemandRow? settled = demands.LastOrDefault(row =>
                    row.State == JourneyDemandState.Unloaded && row.UnloadCommandedAt is not null);
                if (settled is not null)
                {
                    await store.SettleAnsweredCommandAsync(
                        settled.UnloadCommandMessageId, now, cancellationToken).ConfigureAwait(false);
                }
                // One demand's cargo comes off at a time: each batch is a slot door opening, and
                // commanding every remaining demand at once would open several at once. The next one
                // is commanded only after the previous batch has closed safely.
                JourneyDemandRow? nextToUnload = demands.FirstOrDefault(row =>
                    row.State == JourneyDemandState.Loaded);
                if (nextToUnload is not null)
                {
                    nextToUnload.UnloadCommandedAt = now;
                    await PublishUnloadAsync(runtime, stop, nextToUnload, session, cancellationToken)
                        .ConfigureAwait(false);
                    SetStage(runtime, JourneyRuntimeStage.AwaitingUnloadResult, now, stop);
                }
                else
                {
                    stop.State = JourneyStopState.Completed;
                    SetStage(runtime, JourneyRuntimeStage.Completed, now, stop);
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

    private string ValidateStaticRoute(ResolvedJourneyRoute route)
    {
        if (!runtimeOptions.AllowedDispatchZones.Contains(route.DispatchZone, StringComparer.Ordinal))
            return "DISPATCH_ZONE_VEHICLE_ADMISSION_MISSING";
        if (string.IsNullOrWhiteSpace(route.RouteEvidenceId))
            return "ROUTE_EVIDENCE_MISSING";
        return "ELIGIBLE";
    }

    private string ValidateDynamicFacts(OnboardFacts? onboard, RiotVehicleObservation vehicle, DateTimeOffset now)
    {
        if (onboard is null) return "ONBOARD_FACTS_NOT_READY";
        if (!onboard.DepartureSafe || !onboard.VehicleStopped || !onboard.AllTargetSlotsLocked ||
            !onboard.AllUnlockOutputsReset || onboard.UnknownPresent)
            return "ONBOARD_DEPARTURE_UNSAFE";
        if (!vehicle.Connected || !vehicle.Enabled) return "RIOT_VEHICLE_NOT_AVAILABLE";
        if (!string.Equals(vehicle.VehicleKey, runtimeOptions.VehicleKey, StringComparison.Ordinal))
            return "RIOT_VEHICLE_BINDING_MISMATCH";
        if (!string.Equals(vehicle.ProcState, "IDLE", StringComparison.Ordinal)) return "RIOT_VEHICLE_NOT_IDLE";
        if (!string.Equals(vehicle.CurrentMap, runtimeOptions.MapIdentity, StringComparison.Ordinal))
            return "RIOT_VEHICLE_MAP_MISMATCH";
        if (vehicle.ObservedAt > now || now - vehicle.ObservedAt > runtimeOptions.MaximumEvidenceAge)
            return "RIOT_VEHICLE_FACT_STALE";
        if (vehicle.BatteryPercent is null || string.IsNullOrWhiteSpace(vehicle.BatteryState))
            return "BATTERY_FACT_UNKNOWN";
        if (vehicle.BatteryPercent < runtimeOptions.MinimumBatteryPercent)
            return "BATTERY_POLICY_NOT_SATISFIED";
        // A vehicle parked on the charger reports CHARGING for as long as it stays plugged in, and
        // the charging errand deliberately leaves it there -- the pad is also where it waits. So
        // refusing every charging vehicle, which cost nothing while nothing ever drove one to the
        // pad, would now strand it there permanently. What the policy protects is a part-charged
        // vehicle being pulled off too early, and that ends at the resume level.
        if (string.Equals(vehicle.BatteryState, "CHARGING", StringComparison.Ordinal) &&
            (!runtimeOptions.AutoChargingEnabled ||
             vehicle.BatteryPercent < runtimeOptions.ChargeResumeBatteryPercent))
            return "BATTERY_POLICY_NOT_SATISFIED";
        if (vehicle.Speed is null || vehicle.Speed != 0) return "RIOT_VEHICLE_NOT_STOPPED";
        if (vehicle.LockStatus is null || vehicle.LockStatus != 0 || !string.IsNullOrWhiteSpace(vehicle.OrderTaskId))
            return "RIOT_VEHICLE_ORDER_OCCUPIED";
        return "ELIGIBLE";
    }

    private async Task<bool> FinalDynamicFactsReadyAsync(
        long expectedSessionGeneration,
        IReadOnlyCollection<int> targetSlots,
        CancellationToken cancellationToken)
    {
        OnboardFacts? onboard = await ReadOnboardFactsAsync(cancellationToken).ConfigureAwait(false);
        if (onboard is null || onboard.SessionGeneration != expectedSessionGeneration ||
            targetSlots.Any(slot => !onboard.AvailableSlots.Contains(slot)))
        {
            return false;
        }

        RiotVehicleObservation vehicle = await vehicleFacts.ReadVehicleAsync(
            runtimeOptions.VehicleKey,
            cancellationToken).ConfigureAwait(false);
        return ValidateDynamicFacts(onboard, vehicle, timeProvider.GetUtcNow()) == "ELIGIBLE";
    }

    private async Task<bool> IsTrustedArrivalAsync(
        JourneyRuntimeRow runtime,
        JourneyStopRow stop,
        SessionRecoveryRow session,
        CancellationToken cancellationToken)
    {
        // Keyed on the leg, not on the demand and purpose it used to be: a journey can visit several
        // pickup stops, so "the TO_PICKUP intent of this demand" no longer names one row.
        OrderIntentRow intent = await dbContext.OrderIntents.SingleAsync(
            row => row.MovementLegId == stop.MovementLegId,
            cancellationToken).ConfigureAwait(false);
        RiotOrderObservation order = await vehicleFacts.ReconcileByUpperIdAsync(intent.UpperId, cancellationToken)
            .ConfigureAwait(false);
        int targetStation = stop.StationRiotId;
        bool exactOrder = order.Kind == RiotOrderObservationKind.Terminal &&
                          order.OrderState == 5 &&
                          !string.IsNullOrWhiteSpace(order.OrderId) &&
                          order.OrderId == intent.OrderId &&
                          order.VehicleKey == runtime.VehicleKey &&
                          order.MapId == runtime.MapId &&
                          order.DestinationStationId == targetStation;
        if (!exactOrder)
        {
            return false;
        }
        RiotVehicleObservation vehicle = await vehicleFacts.ReadVehicleAsync(runtime.VehicleKey, cancellationToken)
            .ConfigureAwait(false);
        OnboardFacts? onboard = await ReadOnboardFactsAsync(cancellationToken).ConfigureAwait(false);
        DateTimeOffset now = timeProvider.GetUtcNow();
        return vehicle.Connected && vehicle.Enabled &&
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
    }

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
            runtime.BlockReasonCode = null;
            runtime.UpdatedAt = timeProvider.GetUtcNow();
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        runtime.BlockReasonCode = $"{legName}_{result.Outcome}";
        runtime.UpdatedAt = timeProvider.GetUtcNow();
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return false;
    }

    /// <summary>
    /// Everything the vehicle is told on arriving at a pickup stop: its business state, the worklist
    /// for this stop, the plan for the rest of the journey, and the first request to enter a sublot.
    /// </summary>
    private async Task PublishStopArrivalAsync(
        JourneyRuntimeRow runtime,
        IReadOnlyList<JourneyStopRow> stops,
        JourneyStopRow stop,
        IReadOnlyList<JourneyDemandRow> demands,
        SessionRecoveryRow session,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        TakeStopRevisions(runtime, stop);
        await publisher.PublishVehicleBusinessStateAsync(
            stop.VehicleBusinessMessageId,
            runtime.AgvId,
            session.SessionGeneration,
            new VehicleBusinessProjection(stop.VehicleBusinessRevision, "READY", false, "SUFFICIENT", []),
            cancellationToken).ConfigureAwait(false);
        await publisher.PublishUpcomingStopPlanAsync(
            stop.PlanMessageId,
            runtime.AgvId,
            session.SessionGeneration,
            Plan(runtime, stops, stop, demands),
            cancellationToken).ConfigureAwait(false);
        await PublishLoadRoundAsync(runtime, stop, demands, session, now, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// One round of loading at a stop: the worklist as it now stands, and the request to enter the
    /// next sublot. A round ends when a load is commanded, so the next demand at the same stop is
    /// asked for under a new revision -- which is also what expires the previous request at the
    /// vehicle (<c>expiresOnRevisionChange</c>).
    /// </summary>
    private async Task PublishLoadRoundAsync(
        JourneyRuntimeRow runtime,
        JourneyStopRow stop,
        IReadOnlyList<JourneyDemandRow> demands,
        SessionRecoveryRow session,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        JourneyDemandRow[] pending = UncommandedAt(demands, stop);
        if (pending.Length == 0)
        {
            return;
        }

        stop.LoadRound++;
        // The station wait has to have started before the worklist that carries its deadline goes
        // out, or the vehicle's first snapshot of this stop would carry a null countdown that fills
        // in one iteration later. On arrival this line is what starts it -- SetStage only seeds the
        // same field after PublishStopArrivalAsync returns -- and every later round runs after
        // TryContinueLoadingAtStopAsync has already reset it, where ??= correctly changes nothing.
        stop.SublotWaitStartedAt ??= now;
        if (stop.LoadRound > 1)
        {
            // The first round publishes at the revision the stop was given on arrival; every later
            // one has to advance it, or the peer refuses a revision whose content changed.
            stop.WorklistRevision = runtime.WorklistRevision++;
        }
        AcceptedDemandRow[] accepted = await AcceptedDemandsForAsync(demands, cancellationToken)
            .ConfigureAwait(false);
        await publisher.PublishCurrentStopWorklistAsync(
            WireToGateStore.WorklistId(runtime.JourneyId, stop.Sequence, stop.LoadRound),
            runtime.AgvId,
            session.SessionGeneration,
            Worklist(runtime, stop, demands, accepted, StationDepartureDeadline(stop)),
            cancellationToken).ConfigureAwait(false);
        await publisher.PublishSublotEntryRequestAsync(
            WireToGateStore.SublotRequestId(runtime.JourneyId, stop.Sequence, stop.LoadRound),
            runtime.AgvId,
            session.SessionGeneration,
            new SublotEntryRequest(
                pending[0].DemandId,
                runtime.OperationSessionId,
                stop.StationId,
                stop.WorklistRevision,
                // FR-001 AC-3 and BR-001 scope entry to the whole dispatch range, not to the demand
                // the request happens to name: "允许 T 的目标站点与操作员当前物理站点不完全相同，
                // 只要 T 在范围内". Every sublot this journey still has to load is offered, wherever
                // its stop is; which one the operator scans decides which demand loads next.
                LoadableSublots(demands, accepted, stop)),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task PublishLoadAsync(
        JourneyRuntimeRow runtime,
        JourneyStopRow stop,
        JourneyDemandRow demand,
        SessionRecoveryRow session,
        string correlationId,
        CancellationToken cancellationToken)
    {
        AcceptedDemandRow accepted = await dbContext.AcceptedDemands.SingleAsync(
            row => row.DemandId == demand.DemandId, cancellationToken).ConfigureAwait(false);
        int[] slots = JsonSerializer.Deserialize<int[]>(demand.TargetSlotsJson) ?? [];
        string hash = BusinessHash(demand.DemandId, accepted.Sublot, "LOAD", slots);
        await publisher.PublishSlotOperationCommandAsync(
            demand.LoadCommandMessageId,
            runtime.AgvId,
            session.SessionGeneration,
            new SlotOperationCommand(
                correlationId,
                demand.DemandId,
                accepted.Sublot,
                runtime.OperationSessionId,
                demand.LoadSlotOperationAttemptId,
                SlotOperationType.Load,
                slots,
                session.ForcedRecoveryGeneration,
                hash),
            cancellationToken,
            stop.StationId,
            accepted.WorkType).ConfigureAwait(false);
    }

    private async Task PublishGateStateAndUnloadAsync(
        JourneyRuntimeRow runtime,
        IReadOnlyList<JourneyStopRow> stops,
        JourneyStopRow stop,
        IReadOnlyList<JourneyDemandRow> demands,
        SessionRecoveryRow session,
        CancellationToken cancellationToken)
    {
        TakeStopRevisions(runtime, stop);
        stop.LoadRound = 1;
        AcceptedDemandRow[] accepted = await AcceptedDemandsForAsync(demands, cancellationToken)
            .ConfigureAwait(false);
        await publisher.PublishVehicleBusinessStateAsync(
            stop.VehicleBusinessMessageId,
            runtime.AgvId,
            session.SessionGeneration,
            new VehicleBusinessProjection(stop.VehicleBusinessRevision, "READY", false, "SUFFICIENT", []),
            cancellationToken).ConfigureAwait(false);
        await publisher.PublishCurrentStopWorklistAsync(
            WireToGateStore.WorklistId(runtime.JourneyId, stop.Sequence, stop.LoadRound),
            runtime.AgvId,
            session.SessionGeneration,
            // The gate stop never waits for a sublot, so it has no station departure deadline;
            // ADR-cross-0058 decision 3 wants that expressed as "no countdown", not as an expired
            // one. ADR-cross-0015 is the reason it can never grow one: unloading has no cancel
            // branch and therefore no operator wait to bound.
            Worklist(runtime, stop, demands, accepted, null),
            cancellationToken).ConfigureAwait(false);
        await publisher.PublishUpcomingStopPlanAsync(
            stop.PlanMessageId,
            runtime.AgvId,
            session.SessionGeneration,
            Plan(runtime, stops, stop, demands),
            cancellationToken).ConfigureAwait(false);
        JourneyDemandRow first = UnloadingAt(demands);
        first.UnloadCommandedAt = timeProvider.GetUtcNow();
        await PublishUnloadAsync(runtime, stop, first, session, cancellationToken).ConfigureAwait(false);
    }

    private async Task PublishUnloadAsync(
        JourneyRuntimeRow runtime,
        JourneyStopRow stop,
        JourneyDemandRow demand,
        SessionRecoveryRow session,
        CancellationToken cancellationToken)
    {
        _ = stop;
        AcceptedDemandRow accepted = await dbContext.AcceptedDemands.SingleAsync(
            row => row.DemandId == demand.DemandId, cancellationToken).ConfigureAwait(false);
        int[] slots = JsonSerializer.Deserialize<int[]>(demand.TargetSlotsJson) ?? [];
        await publisher.PublishSlotOperationCommandAsync(
            demand.UnloadCommandMessageId,
            runtime.AgvId,
            session.SessionGeneration,
            new SlotOperationCommand(
                null,
                demand.DemandId,
                accepted.Sublot,
                runtime.OperationSessionId,
                demand.UnloadSlotOperationAttemptId,
                SlotOperationType.Unload,
                slots,
                session.ForcedRecoveryGeneration,
                BusinessHash(demand.DemandId, accepted.Sublot, "UNLOAD", slots)),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The operator's entry, matched to the demand it names. The sublot decides which demand loads
    /// next, and the candidates are every demand this journey has still to load -- not just the ones
    /// planned for this stop.
    /// </summary>
    /// <remarks>
    /// FR-001 AC-3 is explicit that the target station of what is entered need not be the station
    /// the operator is standing at, as long as it is in the dispatch range, and BR-001 says a range
    /// may span neighbouring stations. So a demand entered here is loaded here: its planned stop
    /// moves to this one, and the stop it was going to be loaded at simply has nothing left to do.
    /// </remarks>
    private async Task<SublotEntry?> FindMatchingSublotAsync(
        JourneyRuntimeRow runtime,
        JourneyStopRow stop,
        IReadOnlyList<JourneyDemandRow> demands,
        SessionRecoveryRow session,
        CancellationToken cancellationToken)
    {
        JourneyDemandRow[] enterable = demands
            .Where(row => row.State == JourneyDemandState.Planned && row.LoadCommandedAt is null)
            .ToArray();
        AcceptedDemandRow[] accepted = await AcceptedDemandsForAsync(enterable, cancellationToken)
            .ConfigureAwait(false);
        ProtocolInboxRow[] rows = await dbContext.ProtocolInbox.AsNoTracking()
            .Where(row => row.MessageType == "SublotSubmitted")
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        rows = rows.OrderBy(row => row.ReceivedAt).ToArray();
        foreach (ProtocolInboxRow row in rows)
        {
            using JsonDocument document = JsonDocument.Parse(row.RequestJson);
            JsonElement root = document.RootElement;
            JsonElement payload = root.GetProperty("payload");
            // 判过一次的提交不再进入匹配：被拒的那条留在 inbox 里，否则每一轮都会重新触发一次
            // 远程重算，也会把同一条拒绝反复发给对端。
            if (enterable.Any(demand => demand.ConsumedSublotMessageId == row.MessageId))
            {
                continue;
            }
            if (RequiredString(root, "agvId") != runtime.AgvId ||
                root.GetProperty("sessionGeneration").GetInt64() != session.SessionGeneration ||
                RequiredString(payload, "operationSessionId") != runtime.OperationSessionId ||
                RequiredString(payload, "stationId") != stop.StationId ||
                payload.GetProperty("worklistRevision").GetInt64() != stop.WorklistRevision)
            {
                runtime.BlockReasonCode = "SUBLOT_SUBMISSION_MISMATCH";
                continue;
            }
            string submittedSublot = RequiredString(payload, "sublot");
            AcceptedDemandRow? demand = accepted.SingleOrDefault(item =>
                string.Equals(item.Sublot, submittedSublot, StringComparison.Ordinal));
            JourneyDemandRow? membership = demand is null
                ? null
                : enterable.SingleOrDefault(item => item.DemandId == demand.DemandId);
            if (demand is null || membership is null)
            {
                runtime.BlockReasonCode = "SUBLOT_SUBMISSION_MISMATCH";
                continue;
            }
            if (!await store.IsTaskTypeAllowedAsync(
                    stop.StationId, demand.WorkType, cancellationToken).ConfigureAwait(false))
            {
                runtime.BlockReasonCode = "TASK_TYPE_NOT_ALLOWED_AT_STATION";
                return null;
            }
            // Entered here, so loaded here. The station admission just checked is this stop's.
            membership.StopSequence = stop.Sequence;
            return new SublotEntry(row, membership);
        }
        return null;
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
    /// <summary>
    /// Recomputes the authoritative basket count for a sublot the operator has just entered, and
    /// returns the refusal to send back when it cannot be established. Returning <c>null</c> means
    /// the load may proceed.
    /// </summary>
    /// <remarks>
    /// BR-013 makes this count authoritative and forbids falling back to a default, a minimum or an
    /// operator-editable value, so every failure here stops the load. The count is compared against
    /// the one frozen at acceptance because the slots were reserved against that number: a demand
    /// whose box count grew between dispatch and entry no longer fits the reservation it was given.
    /// </remarks>
    private async Task<SublotRejection?> RevalidateEnteredSublotAsync(
        JourneyRuntimeRow runtime,
        JourneyStopRow stop,
        JourneyDemandRow entered,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        AcceptedDemandRow demand = await dbContext.AcceptedDemands.SingleAsync(
            row => row.DemandId == entered.DemandId, cancellationToken).ConfigureAwait(false);

        SublotRejection Refuse(string reasonCode, string fieldPath, string displayMessage) => new(
            entered.DemandId,
            runtime.OperationSessionId,
            stop.WorklistRevision,
            reasonCode,
            fieldPath,
            displayMessage);

        if (!await store.IsTaskTypeAllowedAsync(
                stop.StationId, demand.WorkType, cancellationToken).ConfigureAwait(false))
        {
            return Refuse(
                "ACTION_NOT_ALLOWED_IN_STATE",
                "payload.stationId",
                $"本站不允许 {demand.WorkType} 作业，无法在此装货。");
        }

        LiveMesFieldSet? fields = JsonSerializer.Deserialize<LiveMesFieldSet>(
            demand.LiveMesFieldsJson, SerializerOptions);
        if (string.IsNullOrWhiteSpace(fields?.Package))
        {
            return Refuse(
                "EXPECTED_BASKET_COUNT_MISMATCH",
                "payload.sublot",
                $"子批 {demand.Sublot} 没有 PACKAGE 型号，算不出花篮数量，不予开仓。");
        }

        int? capacity = await packageCapacityStore.ResolveAndTrackAsync(
            fields.Package, now, cancellationToken).ConfigureAwait(false);
        if (capacity is null or <= 0)
        {
            return Refuse(
                "EXPECTED_BASKET_COUNT_MISMATCH",
                "payload.sublot",
                $"PACKAGE {fields.Package} 没有已批准的花篮容量对照，算不出花篮数量，不予开仓。");
        }

        int? maxBoxCount;
        try
        {
            maxBoxCount = await boxCountReader.ReadMaxBoxCountAsync(demand.Sublot, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception error) when (
            error is HttpRequestException or InvalidDataException or JsonException)
        {
            LogBoxCountFailed(logger, entered.DemandId, error);
            maxBoxCount = null;
        }
        if (maxBoxCount is null or <= 0)
        {
            return Refuse(
                "EXPECTED_BASKET_COUNT_MISMATCH",
                "payload.sublot",
                $"查不到子批 {demand.Sublot} 的箱数，算不出花篮数量，不予开仓。");
        }

        int expected = checked((maxBoxCount.Value + capacity.Value - 1) / capacity.Value);
        if (expected != entered.ExpectedBasketCount)
        {
            return Refuse(
                "EXPECTED_BASKET_COUNT_MISMATCH",
                "payload.sublot",
                $"子批 {demand.Sublot} 的花篮数量由 {entered.ExpectedBasketCount} 变为 {expected}，" +
                "与本次派车预留的仓位不符，不予开仓。");
        }

        return null;
    }

    private async Task<SafetyCheckObservation?> AwaitSafeDepartureResultAsync(
        JourneyRuntimeRow runtime,
        JourneyStopRow stop,
        SessionRecoveryRow session,
        CancellationToken cancellationToken)
    {
        // Bounded by attempts rather than by the clock: a test clock does not advance on its own,
        // and a wait that never ends is worse than a stage that does not advance.
        TimeSpan step = TimeSpan.FromMilliseconds(50);
        int attempts = Math.Max(1, (int)(runtimeOptions.DepartureSafetyResultWait / step));
        for (int attempt = 0; ; attempt++)
        {
            runtime.BlockReasonCode = null;
            SafetyCheckObservation? safety = await FindSafeDepartureResultAsync(
                runtime, stop, session, timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
            if (safety is not null || runtime.BlockReasonCode is not null || attempt >= attempts)
            {
                return safety;
            }
            await Task.Delay(step, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<SafetyCheckObservation?> FindSafeDepartureResultAsync(
        JourneyRuntimeRow runtime,
        JourneyStopRow stop,
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
            if (RequiredString(payload, "preDepartureSafetyCheckId") != stop.PreDepartureSafetyCheckId)
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
            bool valid = (correlationId == stop.PreDepartureSafetyCheckMessageId ||
                          correlationId == stop.PreDepartureSafetyCheckId) &&
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
                runtime.BlockReasonCode = "PRE_DEPARTURE_SAFETY_NOT_VALID";
                return null;
            }
            if (session.SafetyRevision is not long safetyRevision)
            {
                return null;
            }
            return new SafetyCheckObservation(
                stop.PreDepartureSafetyCheckId,
                safetyRevision,
                true,
                observedAt,
                validUntil);
        }
        return null;
    }

    private async Task<OnboardFacts?> ReadOnboardFactsAsync(CancellationToken cancellationToken)
    {
        SessionRecoveryRow? session = await CurrentReadySessionAsync(runtimeOptions.AgvId, cancellationToken)
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
            "CapabilitySnapshot", runtimeOptions.AgvId, session.SessionGeneration, cancellationToken)
            .ConfigureAwait(false);
        ProtocolInboxRow? safetyRow = await LatestInboxForSessionAsync(
            "SafetyStateSnapshot", runtimeOptions.AgvId, session.SessionGeneration, cancellationToken)
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
            runtimeOptions.AgvId, session.SessionGeneration, safetyRevision, cancellationToken)
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
        // supportsBatchUnlock is deliberately not consulted: protocol-v0.1.1 declares it with no
        // semantics and its own canonical example sets it false, while the real question -- can
        // the vehicle operate this slot set -- is answered against AvailableSlots when the command
        // is actually sent. See docs/defects/20260829-intake-gates-on-unspecified-onboard-facts.md.
        DateTimeOffset now = timeProvider.GetUtcNow();
        DateTimeOffset capabilityAt = capabilityPayload.GetProperty("observedAt").GetDateTimeOffset();
        DateTimeOffset safetyAt = safetySummaryPayload.GetProperty("observedAt").GetDateTimeOffset();
        if (capabilityAt > now || safetyAt > now)
        {
            return null;
        }
        DateTimeOffset? lastInboundAt = await LatestInboundAtForSessionAsync(
            runtimeOptions.AgvId, session.SessionGeneration, cancellationToken).ConfigureAwait(false);
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
        return new OnboardFacts(
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

    private Task<SessionRecoveryRow?> CurrentReadySessionAsync(string agvId, CancellationToken cancellationToken) =>
        dbContext.SessionRecoveries.SingleOrDefaultAsync(
            row => row.AgvId == agvId && row.Readiness == SessionReadiness.Ready,
            cancellationToken);

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
    /// Advances the vehicle's charging errand, and reports whether it owns this iteration.
    /// Returning <c>true</c> keeps discovery from running: while the vehicle is driving to the pad
    /// or still below its resume level, every candidate would be refused for battery anyway, and
    /// the reads that decision costs are all remote.
    /// </summary>
    /// <remarks>
    /// Only ever called with no unresolved journey, so the errand and a demand can never hold the
    /// vehicle at the same time. The vehicle is left standing on the pad when the run completes:
    /// the next demand moves it, and driving it to a separate idle spot would be one more
    /// unattended movement bought for nothing.
    /// </remarks>
    private async Task<bool> AdvanceAutoChargingAsync(
        RiotMapStationCatalogSnapshot currentMap,
        CancellationToken cancellationToken)
    {
        if (!runtimeOptions.AutoChargingEnabled)
        {
            return false;
        }

        // Ordered in memory, not in SQL: SQLite cannot ORDER BY a DateTimeOffset, and the provider
        // says so by throwing at query translation rather than at startup. The set is at most a
        // handful of rows for one vehicle, so the read costs nothing.
        AutoChargingRunRow? run = (await dbContext.AutoChargingRuns
                .Where(row => row.VehicleKey == runtimeOptions.VehicleKey &&
                              row.Stage != AutoChargingStage.Completed)
                .ToArrayAsync(cancellationToken).ConfigureAwait(false))
            .OrderBy(row => row.CreatedAt)
            .FirstOrDefault();
        RiotVehicleObservation vehicle = await vehicleFacts.ReadVehicleAsync(
            runtimeOptions.VehicleKey, cancellationToken).ConfigureAwait(false);
        DateTimeOffset now = timeProvider.GetUtcNow();
        if (run is null)
        {
            return await TryStartAutoChargingAsync(currentMap, vehicle, now, cancellationToken)
                .ConfigureAwait(false);
        }

        switch (run.Stage)
        {
            case AutoChargingStage.AwaitingChargerArrival:
                MovementDispatchResult dispatch = await movementDispatch
                    .ReconcileOrCreateAsync(run.UpperId, cancellationToken).ConfigureAwait(false);
                if (dispatch.Outcome != MovementDispatchOutcome.Confirmed)
                {
                    run.BlockReasonCode = $"CHARGER_{dispatch.Outcome}";
                    run.UpdatedAt = now;
                    await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    return true;
                }
                if (!await IsTrustedChargerArrivalAsync(run, cancellationToken).ConfigureAwait(false))
                {
                    return true;
                }
                run.Stage = AutoChargingStage.Charging;
                run.BlockReasonCode = null;
                run.UpdatedAt = now;
                await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return true;
            case AutoChargingStage.Charging:
                if (vehicle.BatteryPercent is not { } percent)
                {
                    run.BlockReasonCode = "BATTERY_FACT_UNKNOWN";
                    run.UpdatedAt = now;
                    await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    return true;
                }
                if (percent >= runtimeOptions.ChargeResumeBatteryPercent)
                {
                    run.Stage = AutoChargingStage.Completed;
                    run.ReleasedAtBatteryPercent = percent;
                    run.BlockReasonCode = null;
                    run.UpdatedAt = now;
                    await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    LogAutoChargingCompleted(logger, run.ChargingRunId, percent, null);
                    // The vehicle is available again from this instant, so this iteration hands off
                    // to discovery instead of costing a whole poll interval.
                    return false;
                }
                // Standing on the pad without drawing current is the one failure this errand cannot
                // fix by waiting: it means the vehicle never engaged. Naming it is all the runtime
                // can do -- the repair is physical.
                string? notEngaged = string.Equals(vehicle.BatteryState, "CHARGING", StringComparison.Ordinal)
                    ? null
                    : "CHARGER_NOT_ENGAGED";
                if (run.BlockReasonCode != notEngaged)
                {
                    run.BlockReasonCode = notEngaged;
                    run.UpdatedAt = now;
                    await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                }
                return true;
            default:
                return false;
        }
    }

    private async Task<bool> TryStartAutoChargingAsync(
        RiotMapStationCatalogSnapshot currentMap,
        RiotVehicleObservation vehicle,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (vehicle.BatteryPercent is not { } percent ||
            percent >= runtimeOptions.ChargeTriggerBatteryPercent ||
            string.Equals(vehicle.BatteryState, "CHARGING", StringComparison.Ordinal))
        {
            // Already charging means somebody parked it on the pad by hand. Dispatching it to the
            // station it is standing on would add a movement order for no movement.
            return false;
        }
        // Every fact ValidateDynamicFacts checks about the vehicle itself holds here too, minus the
        // battery policy this errand exists to repair: an errand is still an unattended movement.
        if (!vehicle.Connected || !vehicle.Enabled ||
            !string.Equals(vehicle.ProcState, "IDLE", StringComparison.Ordinal) ||
            !string.Equals(vehicle.VehicleKey, runtimeOptions.VehicleKey, StringComparison.Ordinal) ||
            !string.Equals(vehicle.CurrentMap, runtimeOptions.MapIdentity, StringComparison.Ordinal) ||
            vehicle.ObservedAt > now || now - vehicle.ObservedAt > runtimeOptions.MaximumEvidenceAge ||
            vehicle.Speed is null || vehicle.Speed != 0 ||
            vehicle.LockStatus is null || vehicle.LockStatus != 0 ||
            !string.IsNullOrWhiteSpace(vehicle.OrderTaskId))
        {
            return false;
        }
        OnboardFacts? onboard = await ReadOnboardFactsAsync(cancellationToken).ConfigureAwait(false);
        if (onboard is null || !onboard.DepartureSafe || !onboard.VehicleStopped ||
            !onboard.AllTargetSlotsLocked || !onboard.AllUnlockOutputsReset || onboard.UnknownPresent)
        {
            return false;
        }
        if (await dbContext.VehicleDispatchLeases.AnyAsync(
                row => row.VehicleKey == runtimeOptions.VehicleKey && row.ReleasedAt == null,
                cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        RiotMapStation charger;
        try
        {
            charger = stationResolver.RequireFixedStation(
                currentMap, runtimeOptions.ChargerStationRiotId, runtimeOptions.ChargerStationId);
        }
        catch (StationResolutionException error)
        {
            LogChargerStationUnresolved(logger, error.ReasonCode, error);
            return false;
        }

        // The run id is derived rather than random so that a crash between writing the run and
        // dispatching the order cannot leave two runs racing for one vehicle: the same trigger
        // rebuilds the same identity, and both the run row and the order intent refuse a duplicate.
        string chargingRunId = StableGuid(
            $"{runtimeOptions.VehicleKey}|{runtimeOptions.DispatchGeneration}|{now:O}", "charging-run");
        dbContext.AutoChargingRuns.Add(new AutoChargingRunRow
        {
            ChargingRunId = chargingRunId,
            VehicleKey = runtimeOptions.VehicleKey,
            AgvId = runtimeOptions.AgvId,
            Stage = AutoChargingStage.AwaitingChargerArrival,
            ChargerStationId = charger.StationName,
            ChargerStationRiotId = charger.StationId,
            MovementLegId = StableGuid(chargingRunId, "charger-leg"),
            UpperId = $"W2G-CHARGE-{chargingRunId}-{runtimeOptions.DispatchGeneration}",
            TriggeredAtBatteryPercent = percent,
            CreatedAt = now,
            UpdatedAt = now
        });
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        AutoChargingRunRow run = await dbContext.AutoChargingRuns
            .SingleAsync(row => row.ChargingRunId == chargingRunId, cancellationToken).ConfigureAwait(false);
        await store.CreateUnladenMovementIntentAsync(
            new OrderIntent(
                run.MovementLegId,
                // The intent table keys movements by demand, and this leg has no demand. A derived
                // marker keeps it out of every per-demand query rather than borrowing the id of a
                // demand that is not being served.
                $"CHARGE-{chargingRunId}",
                run.UpperId,
                "TO_CHARGER",
                run.ChargerStationId,
                now,
                runtimeOptions.VehicleKey,
                runtimeOptions.MapId,
                run.ChargerStationRiotId,
                runtimeOptions.AgvLifecycleGeneration,
                runtimeOptions.DispatchGeneration),
            cancellationToken).ConfigureAwait(false);
        MovementDispatchResult dispatch = await movementDispatch
            .ReconcileOrCreateAsync(run.UpperId, cancellationToken).ConfigureAwait(false);
        run.BlockReasonCode = dispatch.Outcome == MovementDispatchOutcome.Confirmed
            ? null
            : $"CHARGER_{dispatch.Outcome}";
        run.UpdatedAt = timeProvider.GetUtcNow();
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        LogAutoChargingStarted(logger, chargingRunId, percent, run.ChargerStationId, null);
        return true;
    }

    /// <summary>
    /// The arrival test for a leg with nothing aboard. It asks the same of RIoT as
    /// <see cref="IsTrustedArrivalAsync"/> -- this exact order, terminal and successful, and the
    /// vehicle standing at the target station with no order in hand -- and nothing of the onboard
    /// slots, which no part of this errand touched.
    /// </summary>
    private async Task<bool> IsTrustedChargerArrivalAsync(
        AutoChargingRunRow run,
        CancellationToken cancellationToken)
    {
        OrderIntentRow intent = await dbContext.OrderIntents.SingleAsync(
            row => row.UpperId == run.UpperId, cancellationToken).ConfigureAwait(false);
        RiotOrderObservation order = await vehicleFacts.ReconcileByUpperIdAsync(run.UpperId, cancellationToken)
            .ConfigureAwait(false);
        if (order.Kind != RiotOrderObservationKind.Terminal || order.OrderState != 5 ||
            string.IsNullOrWhiteSpace(order.OrderId) || order.OrderId != intent.OrderId ||
            order.VehicleKey != run.VehicleKey || order.MapId != runtimeOptions.MapId ||
            order.DestinationStationId != run.ChargerStationRiotId)
        {
            return false;
        }
        RiotVehicleObservation vehicle = await vehicleFacts.ReadVehicleAsync(
            run.VehicleKey, cancellationToken).ConfigureAwait(false);
        // Read the clock after the observation, never before. RIoT stamps observedAt as it answers,
        // so a "now" taken before the call is older than the reading it is about to judge, and the
        // ObservedAt > now guard -- which exists to reject a vehicle reporting from the future --
        // fires on every single poll instead. The charger arrival was never trusted and the run sat
        // in AwaitingChargerArrival forever; L2 evidence 20260908-auto-charge-endurance-002.
        // IsTrustedArrivalAsync takes its clock in this order for the same reason.
        DateTimeOffset now = timeProvider.GetUtcNow();
        return vehicle.Connected && vehicle.Enabled &&
               vehicle.ProcState == "IDLE" &&
               vehicle.CurrentMap == runtimeOptions.MapIdentity &&
               vehicle.CurrentStationId == run.ChargerStationRiotId &&
               vehicle.Speed == 0 &&
               vehicle.LockStatus == 0 &&
               string.IsNullOrWhiteSpace(vehicle.OrderTaskId) &&
               vehicle.ObservedAt <= now && now - vehicle.ObservedAt <= runtimeOptions.MaximumEvidenceAge;
    }

    /// <summary>
    /// The journey one selected candidate starts: its first pickup stop, and the gate stop it will
    /// reach once loading ends. Further pickup stops are appended later, as further demands are
    /// taken on, so the gate stop's sequence is not fixed here -- <see cref="GateStopSequence"/>
    /// keeps it last.
    /// </summary>
    private JourneyExecutionPlan CreatePlan(EligibleCandidate candidate, DateTimeOffset now)
    {
        string demandId = candidate.Snapshot.DemandId;
        // The journey's identity is its own, not the demand's (ADR-cross-0057). It is still derived
        // deterministically from the demand that started it, because intake must be replayable: a
        // retried acceptance has to rebuild the same journey or the store refuses it.
        string journeyId = StableGuid(demandId, $"journey-{runtimeOptions.DispatchGeneration}");
        return new JourneyExecutionPlan(
            journeyId,
            runtimeOptions.AgvId,
            runtimeOptions.VehicleKey,
            runtimeOptions.AgvLifecycleGeneration,
            runtimeOptions.MapId,
            runtimeOptions.MapIdentity,
            candidate.Route.DispatchZone,
            runtimeOptions.GateStationId,
            runtimeOptions.GateStationRiotId,
            StableGuid(journeyId, "operation-session"),
            runtimeOptions.DispatchGeneration,
            [
                StopPlan(
                    journeyId,
                    FirstStopSequence,
                    JourneyStopRole.Pickup,
                    candidate.Route.PickupStationId,
                    candidate.Route.PickupStationRiotId,
                    candidate.Route.RouteEvidenceId,
                    "TO_PICKUP"),
                StopPlan(
                    journeyId,
                    GateStopSequence,
                    JourneyStopRole.Gate,
                    runtimeOptions.GateStationId,
                    runtimeOptions.GateStationRiotId,
                    candidate.Route.RouteEvidenceId,
                    "TO_GATE")
            ],
            [new JourneyDemandPlan(
                demandId,
                FirstStopSequence,
                candidate.ExpectedBasketCount,
                candidate.TargetSlots)],
            now);
    }

    private JourneyStopPlan StopPlan(
        string journeyId,
        int sequence,
        string role,
        string stationId,
        int stationRiotId,
        string routeEvidenceId,
        string legType) => new(
            sequence,
            role,
            stationId,
            stationRiotId,
            routeEvidenceId,
            StableGuid(journeyId, $"stop-{sequence}-leg"),
            $"W2G-{journeyId}-S{sequence}-{runtimeOptions.DispatchGeneration}",
            legType);

    /// <summary>
    /// The intent for the leg that takes the vehicle to one stop. <c>DemandId</c> names the demand
    /// that put the stop on the itinerary, for audit only: a leg belongs to the journey, and every
    /// judgement about it -- arrival, reconciliation, authorization -- keys on the movement leg or
    /// the upper id instead.
    /// </summary>
    private static OrderIntent StopIntent(
        JourneyRuntimeRow runtime,
        JourneyStopRow stop,
        string demandId,
        DateTimeOffset now) => new(
        stop.MovementLegId,
        demandId,
        stop.UpperId,
        stop.LegType == "TO_GATE" ? "TO_GATE" : "TO_PICKUP",
        stop.StationId,
        now,
        runtime.VehicleKey,
        runtime.MapId,
        stop.StationRiotId,
        runtime.AgvLifecycleGeneration,
        runtime.DispatchGeneration);

    /// <summary>
    /// The worklist for one stop: the demands still to handle there, in the role this stop plays.
    /// Every publication takes its own revision, because the peer keys a snapshot's identity on its
    /// type and revision. Two stops -- or two rounds at one stop -- publishing at the same revision
    /// made the peer reject the second as a revision whose content had changed, which was right of
    /// it: a revision that does not advance is a promise that the content did not. The connection
    /// died on that rejection, and the command queued behind it was never reached.
    /// </summary>
    private static CurrentStopWorklistProjection Worklist(
        JourneyRuntimeRow runtime,
        JourneyStopRow stop,
        IReadOnlyList<JourneyDemandRow> demands,
        IReadOnlyList<AcceptedDemandRow> accepted,
        DateTimeOffset? stationDepartureDeadlineAt) => new(
            stop.StationId,
            stop.WorklistRevision,
            runtime.OperationSessionId,
            stationDepartureDeadlineAt,
            (stop.Role == JourneyStopRole.Gate
                ? demands.Where(row => row.State == JourneyDemandState.Loaded)
                : PendingAt(demands, stop))
            .Select(row => new
            {
                Membership = row,
                Demand = accepted.Single(item => item.DemandId == row.DemandId)
            })
            .Select(pair => new CurrentStopWorklistItem(
                pair.Demand.DemandId,
                pair.Demand.TransportDemandKey,
                pair.Demand.Sublot,
                pair.Demand.WorkType,
                stop.Role,
                pair.Membership.ExpectedBasketCount))
            .ToArray());

    /// <summary>
    /// When the server stops waiting for an operator at this stop, or null when there is no such
    /// wait to express. Same two conditions <see cref="TryTimeOutSublotWaitAsync"/> enforces, read
    /// the same way round: a disabled timeout and a stop that has not started waiting both mean the
    /// vehicle has nothing to count down, and publishing a deadline the runtime will never act on
    /// would be a countdown that expires into silence.
    /// </summary>
    private DateTimeOffset? StationDepartureDeadline(JourneyStopRow stop) =>
        runtimeOptions.SublotWaitTimeout <= TimeSpan.Zero || stop.SublotWaitStartedAt is not { } startedAt
            ? null
            : startedAt + runtimeOptions.SublotWaitTimeout;

    /// <summary>
    /// The whole itinerary as the vehicle should see it, with the stop it is at marked ARRIVED.
    /// </summary>
    /// <remarks>
    /// The wire sequence is the position in the sorted itinerary, not the stop's own sequence: the
    /// gate keeps a fixed internal sequence above every pickup stop so appending one never renumbers
    /// it, and that leaves a gap the protocol has no reason to see.
    /// </remarks>
    private static UpcomingStopPlanProjection Plan(
        JourneyRuntimeRow runtime,
        IReadOnlyList<JourneyStopRow> stops,
        JourneyStopRow current,
        IReadOnlyList<JourneyDemandRow> demands) => new(
            current.PlanRevision,
            // Null because a journey's plan belongs to the journey, not to any one demand it
            // carries. The field is nullable in the schema for exactly this.
            null,
            stops.Where(row =>
                    row.Sequence == current.Sequence ||
                    row.Role == JourneyStopRole.Gate ||
                    row.State != JourneyStopState.Planned ||
                    UncommandedAt(demands, row).Length > 0)
                .OrderBy(row => row.Sequence)
                .Select((row, index) => new UpcomingMovementLeg(
                    row.MovementLegId,
                    row.LegType,
                    index + 1,
                    row.StationId,
                    runtime.MapIdentity,
                    row.Sequence == current.Sequence ? JourneyStopState.Arrived : row.State))
                .ToArray());

    private static string BusinessHash(string demandId, string sublot, string operation, IEnumerable<int> slots) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{demandId}|{sublot}|{operation}|{string.Join(',', slots)}"))).ToLowerInvariant();

    /// <summary>
    /// Every outbound message this journey owns, so an unacknowledged one can be replayed into the
    /// current session. The per-round worklist and entry request ids are recomputed from the round
    /// counter rather than stored: they are deterministic, and a stop that asked three times has
    /// three of each.
    /// </summary>
    private static HashSet<string> RuntimeMessageIds(
        IReadOnlyList<JourneyStopRow> stops,
        IReadOnlyList<JourneyDemandRow> demands)
    {
        HashSet<string> ids = new(StringComparer.Ordinal);
        foreach (JourneyStopRow stop in stops)
        {
            ids.Add(stop.VehicleBusinessMessageId);
            ids.Add(stop.PlanMessageId);
            ids.Add(stop.PreDepartureSafetyCheckMessageId);
            for (int round = 1; round <= stop.LoadRound; round++)
            {
                ids.Add(WireToGateStore.WorklistId(stop.JourneyId, stop.Sequence, round));
                ids.Add(WireToGateStore.SublotRequestId(stop.JourneyId, stop.Sequence, round));
            }
        }
        foreach (JourneyDemandRow demand in demands)
        {
            ids.Add(demand.LoadCommandMessageId);
            ids.Add(demand.UnloadCommandMessageId);
        }
        return ids;
    }

    private static string StableGuid(string demandId, string purpose)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{demandId}|{purpose}"));
        Span<byte> guidBytes = bytes.AsSpan(0, 16);
        guidBytes[6] = (byte)((guidBytes[6] & 0x0f) | 0x50);
        guidBytes[8] = (byte)((guidBytes[8] & 0x3f) | 0x80);
        return new Guid(guidBytes).ToString("D");
    }

    private static string RequiredString(JsonElement element, string propertyName) =>
        element.GetProperty(propertyName).GetString()
        ?? throw new InvalidDataException($"Protocol field '{propertyName}' is required.");

    /// <summary>
    /// Ends the demand when nobody entered a sublot inside the wait window. A pickup stop can
    /// legitimately have nothing to load, and the journey had no way to say so: it held the vehicle
    /// and the station until an operator drove a recovery by hand. The runtime may end it alone
    /// precisely because no slot operation was ever commanded -- there is no physical state that
    /// only the peer could settle -- and the vehicle is free for the next demand on the next
    /// discovery pass.
    /// </summary>
    private async Task<bool> TryTimeOutSublotWaitAsync(
        JourneyRuntimeRow runtime,
        IReadOnlyList<JourneyStopRow> stops,
        JourneyStopRow stop,
        JourneyDemandRow[] demands,
        SessionRecoveryRow session,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (runtimeOptions.SublotWaitTimeout <= TimeSpan.Zero ||
            stop.SublotWaitStartedAt is not { } startedAt ||
            now - startedAt < runtimeOptions.SublotWaitTimeout)
        {
            return false;
        }

        // ADR-cross-0058 decision 4, the one square the baseline never covered. StopClosureCommit
        // presumes the vehicle can then leave, and ADR-cross-0011/0012 forbid moving with a slot
        // door open -- so a deadline that expires against an open door must not close the stop. It
        // raises an alarm and keeps waiting; when the door is finally shut, the next pass settles
        // against the IO reading of that moment.
        //
        // This square cannot be automated away: ModbusTcpIoModuleClient has no close or lock
        // output at all (0x05 drives only PulseUnlockAsync), so the door is shut by hand. What the
        // software can do is downgrade the human's job from "log in as MAINTENANCE_ADMINISTRATOR
        // and drive a recovery handshake" to "push the door shut".
        if (DoorLeftOpen(session))
        {
            // Only on the edge: this runs every poll interval while the door stays open.
            if (runtime.BlockReasonCode != StationTimeoutDoorNotClosedReason)
            {
                runtime.BlockReasonCode = StationTimeoutDoorNotClosedReason;
                runtime.UpdatedAt = now;
                await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                LogStationTimeoutDoorNotClosed(logger, stop.StationId, runtimeOptions.SublotWaitTimeout, null);
            }
            // Handled: the stop stays open and the journey stays in AwaitingSublot. The stage is
            // deliberately not Blocked -- nothing here needs an administrator, and ADR-cross-0058
            // decision 4 wants the vehicle visibly waiting, not parked in a recovery state.
            return true;
        }

        // ADR-cross-0055 and FR-004 end the *stop*, which terminates every demand still waiting to
        // be loaded there -- plural. A journey carrying demands it has already loaded is not ended
        // by this: those are on board and still bound for the gate.
        foreach (JourneyDemandRow pending in PendingAt(demands, stop))
        {
            if (await store.CancelDemandBeforeLoadAsync(
                    pending.DemandId, "CANCELLED_BY_STATION_TIMEOUT", now, cancellationToken)
                .ConfigureAwait(false))
            {
                LogSublotWaitTimedOut(logger, pending.DemandId, runtimeOptions.SublotWaitTimeout, null);
            }
        }

        JourneyDemandRow[] remaining = await DemandsAsync(runtime, cancellationToken).ConfigureAwait(false);
        if (remaining.All(row =>
                row.State is JourneyDemandState.Cancelled or JourneyDemandState.Unloaded))
        {
            return true;
        }

        // Something is still aboard, so the journey continues -- to the gate, or to a stop where it
        // can still pick more up.
        await ConcludeLoadingStopAsync(
            runtime, stops, stop, remaining, session, now, cancellationToken).ConfigureAwait(false);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// The reason code a stop carries while it is past its departure deadline but cannot be closed
    /// because a slot door is still open. It is an alarm, not a block: the journey stays in
    /// <see cref="JourneyRuntimeStage.AwaitingSublot"/> and settles as soon as the door is shut.
    /// </summary>
    private const string StationTimeoutDoorNotClosedReason = "STATION_TIMEOUT_DOOR_NOT_CLOSED";

    /// <summary>
    /// Raises <see cref="StationTimeoutDoorNotClosedReason"/> while the stop is past its departure
    /// deadline with a slot door still open, and withdraws it once that stops being true. It is the
    /// <see cref="JourneyRuntimeStage.AwaitingLoadResult"/> half of ADR-cross-0058 decision 4;
    /// <see cref="TryTimeOutSublotWaitAsync"/> is the <see cref="JourneyRuntimeStage.AwaitingSublot"/>
    /// half, and the two stages are mutually exclusive -- a slot operation is only ever underway in
    /// this one.
    /// </summary>
    /// <remarks>
    /// Writes on the edge only: this runs every poll interval. The withdrawal is equally narrow --
    /// it clears the field only when this method is what put that code there, so a block reason
    /// written by anything else survives untouched.
    /// </remarks>
    private async Task ReconcileStationTimeoutDoorNotClosedAsync(
        JourneyRuntimeRow runtime,
        JourneyStopRow stop,
        SessionRecoveryRow session,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        string? reason =
            StationDepartureDeadline(stop) is { } deadline &&
            now >= deadline &&
            DoorLeftOpen(session)
                ? StationTimeoutDoorNotClosedReason
                : null;
        if (reason == runtime.BlockReasonCode ||
            (reason is null && runtime.BlockReasonCode != StationTimeoutDoorNotClosedReason))
        {
            return;
        }

        if (reason is not null)
        {
            LogStationTimeoutDoorNotClosed(logger, stop.StationId, runtimeOptions.SublotWaitTimeout, null);
        }
        runtime.BlockReasonCode = reason;
        runtime.UpdatedAt = now;
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Whether the vehicle is currently reporting a slot door that is not closed. The source is the
    /// safety projection the peer already pushes -- <c>LOCK_NOT_CLOSED</c> is the code it reports
    /// for exactly this, and <see cref="WireToGateStore"/> already treats it as one of the two
    /// operation-induced unsafety reasons. An unknown safety picture counts as "not closed": the
    /// server must not close a stop against evidence it cannot read.
    /// </summary>
    private static bool DoorLeftOpen(SessionRecoveryRow session)
    {
        if (session.SafetyUnknownPresent != false)
        {
            return true;
        }
        if (session.SafetyReasonCodesJson is null)
        {
            return false;
        }
        string[] reasonCodes = JsonSerializer.Deserialize<string[]>(session.SafetyReasonCodesJson) ?? [];
        return reasonCodes.Contains("LOCK_NOT_CLOSED", StringComparer.Ordinal);
    }

    private static void SetStage(
        JourneyRuntimeRow runtime,
        JourneyRuntimeStage stage,
        DateTimeOffset now,
        JourneyStopRow stop)
    {
        runtime.Stage = stage;
        runtime.BlockReasonCode = null;
        runtime.UpdatedAt = now;
        stop.UpdatedAt = now;
        if (stage == JourneyRuntimeStage.AwaitingSublot)
        {
            // Per stop, and only the first time this stop starts waiting. ADR-cross-0055 recomputes
            // the station wait after each LoadBatch closes, which is what the reset in
            // TryContinueLoadingAtStopAsync does.
            stop.SublotWaitStartedAt ??= now;
        }
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
        JourneyDemandRow[] demands,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        (string? attemptId, string reason) = runtime.Stage switch
        {
            JourneyRuntimeStage.AwaitingLoadResult => (
                demands.FirstOrDefault(row =>
                    row.StopSequence == runtime.CurrentStopSequence &&
                    row.State == JourneyDemandState.Planned &&
                    row.LoadCommandedAt is not null)?.LoadSlotOperationAttemptId,
                "LOAD_RESULT_REQUIRES_RECOVERY"),
            JourneyRuntimeStage.AwaitingUnloadResult => (
                demands.FirstOrDefault(row =>
                    row.State == JourneyDemandState.Loaded &&
                    row.UnloadCommandedAt is not null)?.UnloadSlotOperationAttemptId,
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

    private static void Block(JourneyRuntimeRow runtime, string reason, DateTimeOffset now)
    {
        runtime.Stage = JourneyRuntimeStage.Blocked;
        runtime.BlockReasonCode = reason;
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

    private async Task<JourneyStopRow[]> StopsAsync(
        JourneyRuntimeRow runtime,
        CancellationToken cancellationToken)
    {
        JourneyStopRow[] rows = await dbContext.JourneyStops
            .Where(row => row.JourneyId == runtime.JourneyId)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        return rows.OrderBy(row => row.Sequence).ToArray();
    }

    /// <summary>
    /// The journey's demands, oldest first. Ordered in memory: the SQLite provider translates an
    /// <c>ORDER BY</c> on a <see cref="DateTimeOffset"/> only at query time and then throws.
    /// </summary>
    private async Task<JourneyDemandRow[]> DemandsAsync(
        JourneyRuntimeRow runtime,
        CancellationToken cancellationToken)
    {
        JourneyDemandRow[] rows = await dbContext.JourneyDemands
            .Where(row => row.JourneyId == runtime.JourneyId)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        return rows.OrderBy(row => row.CreatedAt).ThenBy(row => row.DemandId, StringComparer.Ordinal).ToArray();
    }

    /// <summary>Demands still waiting to be loaded at one stop, oldest first.</summary>
    private static JourneyDemandRow[] PendingAt(
        IReadOnlyList<JourneyDemandRow> demands,
        JourneyStopRow stop) =>
        demands.Where(row =>
                row.StopSequence == stop.Sequence && row.State == JourneyDemandState.Planned)
            .ToArray();

    /// <summary>
    /// Demands at this stop that nobody has been asked to load yet -- what the next entry request
    /// offers, and what an entered sublot is matched against. The one already commanded is excluded:
    /// its door may be open, and a second command for it would be a replay.
    /// </summary>
    private static JourneyDemandRow[] UncommandedAt(
        IReadOnlyList<JourneyDemandRow> demands,
        JourneyStopRow stop) =>
        PendingAt(demands, stop).Where(row => row.LoadCommandedAt is null).ToArray();

    /// <summary>
    /// The demand this stop commanded a load for and which then ended terminally without a load
    /// result -- cancelled by the operator, compensated, or handed off as fault cargo. It is what
    /// tells "the batch closed the other way round" from the broken invariant
    /// <see cref="LoadingAt"/> reports, and it is only ever visible while the journey still carries
    /// other demands: settling the last one moves the journey straight to Completed.
    /// </summary>
    private static JourneyDemandRow? TerminatedCommandedAt(
        IReadOnlyList<JourneyDemandRow> demands,
        JourneyStopRow stop) =>
        demands.FirstOrDefault(row =>
            row.StopSequence == stop.Sequence &&
            row.LoadCommandedAt is not null &&
            row.State == JourneyDemandState.Cancelled);

    /// <summary>
    /// The demand whose load this stop is waiting on: the one whose command went out and whose batch
    /// has not closed. It is not simply the first pending demand -- the operator decides which of
    /// them loads next by what they scan.
    /// </summary>
    private static JourneyDemandRow LoadingAt(
        IReadOnlyList<JourneyDemandRow> demands,
        JourneyStopRow stop) =>
        PendingAt(demands, stop).FirstOrDefault(row => row.LoadCommandedAt is not null)
        ?? throw new InvalidDataException(
            $"Stop {stop.Sequence} awaits a load result with no commanded load there.");

    private static JourneyDemandRow UnloadingAt(IReadOnlyList<JourneyDemandRow> demands) =>
        demands.FirstOrDefault(row => row.State == JourneyDemandState.Loaded)
        ?? throw new InvalidDataException("A journey at the gate carries nothing to unload.");

    private async Task<AcceptedDemandRow[]> AcceptedDemandsForAsync(
        IReadOnlyList<JourneyDemandRow> demands,
        CancellationToken cancellationToken)
    {
        string[] ids = demands.Select(row => row.DemandId).ToArray();
        return await dbContext.AcceptedDemands
            .Where(row => ids.Contains(row.DemandId))
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Every sublot the operator may enter right now: the whole dispatch range still to be loaded,
    /// whichever stop each one belongs to. FR-001 AC-3 makes belonging to the range the criterion,
    /// not proximity to the stop the vehicle is parked at.
    /// </summary>
    private static string[] LoadableSublots(
        IReadOnlyList<JourneyDemandRow> demands,
        IReadOnlyList<AcceptedDemandRow> accepted,
        JourneyStopRow stop) =>
        demands.Where(row => row.State == JourneyDemandState.Planned && row.LoadCommandedAt is null)
            // The set is the whole range; the order puts this stop's own first, because that is what
            // the operator standing here is most likely holding.
            .OrderBy(row => row.StopSequence == stop.Sequence ? 0 : 1)
            .ThenBy(row => row.CreatedAt)
            .Select(row => accepted.Single(item => item.DemandId == row.DemandId).Sublot)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// Gives a stop the snapshot revisions it publishes at, taken from the journey's cursors, and
    /// advances those. Already-numbered stops keep their revisions so that a republish stays
    /// byte-identical -- the peer refuses a revision whose content changed, and a re-numbered
    /// republish is exactly that.
    /// </summary>
    private static void TakeStopRevisions(JourneyRuntimeRow runtime, JourneyStopRow stop)
    {
        if (stop.PlanRevision != 0)
        {
            return;
        }

        stop.VehicleBusinessRevision = runtime.VehicleBusinessRevision++;
        stop.WorklistRevision = runtime.WorklistRevision++;
        stop.PlanRevision = runtime.PlanRevision++;
    }

    /// <summary>
    /// Whether the vehicle keeps loading at this stop after a batch closed. It does when the stop
    /// still has a demand pending and the loading phase has not ended -- and ending it is what
    /// ADR-cross-0057 decides on "full or held too long, whichever comes first".
    /// </summary>
    private async Task<bool> TryContinueLoadingAtStopAsync(
        JourneyRuntimeRow runtime,
        JourneyStopRow stop,
        IReadOnlyList<JourneyDemandRow> demands,
        SessionRecoveryRow session,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // Not gated on the loading phase being closed: a demand already on this journey has its
        // slots reserved and is on the worklist in front of the operator. "Full" means no *further*
        // candidate fits, not that what was promised gets left on the platform. Only the holding
        // limit cuts a stop short, and it terminates what it abandons rather than dropping it.
        if (UncommandedAt(demands, stop).Length == 0 || HoldingExpired(runtime, now))
        {
            return false;
        }

        // ADR-cross-0055: the station wait is recomputed from each closed batch, so the operator gets
        // the whole window again for the next demand at this stop.
        stop.SublotWaitStartedAt = now;
        await PublishLoadRoundAsync(runtime, stop, demands, session, now, cancellationToken)
            .ConfigureAwait(false);
        SetStage(runtime, JourneyRuntimeStage.AwaitingSublot, now, stop);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Why the loading phase should end now, or <c>null</c> when it should not.
    /// </summary>
    /// <remarks>
    /// ADR-cross-0057 gives two reasons, whichever comes first. This is the deadline half: the
    /// holding limit runs from the first batch that closed safely, once, for the whole journey. An
    /// empty vehicle is never held too long -- before cargo is aboard there is no holding risk, and
    /// a stop with nothing to load is ADR-cross-0055's business, not this one's.
    ///
    /// The other half, "full", cannot be decided from the journey alone: it is not every slot
    /// occupied but too few free to take the *next candidate* whole, and what that candidate needs
    /// comes from the catalog. <see cref="TryTakeOnMoreCargoAsync"/> records it when the scoring says
    /// so, and this reads it back off <see cref="JourneyRuntimeRow.LoadingClosedReason"/>.
    /// </remarks>
    private string? CloseLoading(
        JourneyRuntimeRow runtime,
        IReadOnlyList<JourneyDemandRow> demands,
        DateTimeOffset now)
    {
        _ = demands;
        if (runtime.LoadingClosedReason is { } already)
        {
            return already;
        }
        return HoldingExpired(runtime, now) ? "HOLDING_TIMEOUT" : null;
    }

    /// <summary>
    /// Whether the journey has held cargo past the limit. Measured from the first batch that closed
    /// safely, not from acceptance or arrival: before cargo is aboard there is nothing being held.
    /// </summary>
    private bool HoldingExpired(JourneyRuntimeRow runtime, DateTimeOffset now) =>
        runtimeOptions.HoldingTimeout > TimeSpan.Zero &&
        runtime.HoldingStartedAt is { } holdingSince &&
        now - holdingSince >= runtimeOptions.HoldingTimeout;

    /// <summary>
    /// Takes on demands until the vehicle can hold no more, then re-reads the journey. The re-read
    /// is what the caller needs: every acceptance adds rows the arrays it holds do not have.
    /// </summary>
    /// <remarks>
    /// Bounded by the vehicle's slot count rather than left to terminate on its own. It does
    /// terminate -- an accepted demand is never a candidate again and its reservation shrinks the
    /// free set -- but a loop that drives real dispatch should not be relying on that.
    /// </remarks>
    private async Task<(JourneyStopRow[] Stops, JourneyDemandRow[] Demands, JourneyStopRow Stop)> FillJourneyAsync(
        JourneyRuntimeRow runtime,
        JourneyStopRow[] stops,
        JourneyStopRow stop,
        JourneyDemandRow[] demands,
        RiotMapStationCatalogSnapshot currentMap,
        RiotMapStation gate,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        for (int taken = 0; taken < MaxPickupStops; taken++)
        {
            if (!await TryTakeOnMoreCargoAsync(
                    runtime, stops, stop, demands, currentMap, gate, now, cancellationToken)
                .ConfigureAwait(false))
            {
                break;
            }
            stops = await StopsAsync(runtime, cancellationToken).ConfigureAwait(false);
            demands = await DemandsAsync(runtime, cancellationToken).ConfigureAwait(false);
            stop = stops.Single(row => row.Sequence == stop.Sequence);
        }
        return (stops, demands, stop);
    }

    /// <summary>
    /// Takes one more demand onto this journey while it is still loading: at the stop the vehicle is
    /// parked at when the demand's pickup station is that stop, otherwise at a stop appended for it.
    /// Returns whether anything was taken on.
    /// </summary>
    /// <remarks>
    /// This is what makes "full or held too long" a real decision -- ADR-cross-0057's loading phase
    /// ends when the vehicle can take no more or has held cargo too long, and without new candidates
    /// arriving it would always end at the first demand instead. Every gate discovery applies still
    /// applies: the same scoring, the same suppression list, the same re-read of the catalog before
    /// the demand is recorded. What it does not do is take a lease or create a movement intent --
    /// the journey already holds the vehicle, and the leg is authorized on departure.
    /// </remarks>
    private async Task<bool> TryTakeOnMoreCargoAsync(
        JourneyRuntimeRow runtime,
        IReadOnlyList<JourneyStopRow> stops,
        JourneyStopRow stop,
        IReadOnlyList<JourneyDemandRow> demands,
        RiotMapStationCatalogSnapshot currentMap,
        RiotMapStation gate,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (runtime.LoadingClosedReason is not null || CloseLoading(runtime, demands, now) is not null)
        {
            return false;
        }

        int[] reserved = demands
            .Where(row => row.State != JourneyDemandState.Cancelled)
            .SelectMany(row => JsonSerializer.Deserialize<int[]>(row.TargetSlotsJson) ?? [])
            .Distinct()
            .ToArray();
        CandidateScoring scoring = await ScoreCandidatesAsync(
            currentMap, gate, reserved, now, cancellationToken).ConfigureAwait(false);
        if (scoring.Onboard is null || scoring.Eligible.Count == 0)
        {
            if (scoring.SlotStarved)
            {
                // ADR-cross-0057's "full": something is waiting and the vehicle cannot take it
                // whole. The loading phase ends here rather than at the next stop, because the
                // answer will not change until cargo comes off at the gate.
                runtime.LoadingClosedReason ??= "VEHICLE_FULL";
            }
            return false;
        }

        int pickupStops = stops.Count(row => row.Role == JourneyStopRole.Pickup);
        // A candidate at this very stop needs no new stop; one elsewhere does, and the itinerary
        // cannot grow past the vehicle's slot count.
        EligibleCandidate[] reachable = scoring.Eligible
            .Where(candidate =>
                string.Equals(candidate.Route.PickupStationId, stop.StationId, StringComparison.Ordinal) ||
                stops.Any(row =>
                    row.Role == JourneyStopRole.Pickup &&
                    row.State == JourneyStopState.Planned &&
                    string.Equals(row.StationId, candidate.Route.PickupStationId, StringComparison.Ordinal)) ||
                pickupStops < MaxPickupStops)
            .ToArray();
        if (reachable.Length == 0)
        {
            return false;
        }

        EligibleCandidate selected = Preferred(reachable);
        JourneyStopRow? existingStop = stops.FirstOrDefault(row =>
            row.Role == JourneyStopRole.Pickup &&
            string.Equals(row.StationId, selected.Route.PickupStationId, StringComparison.Ordinal) &&
            (row.Sequence == stop.Sequence || row.State == JourneyStopState.Planned));
        JourneyStopPlan? appended = existingStop is null
            ? StopPlan(
                runtime.JourneyId,
                stops.Where(row => row.Role == JourneyStopRole.Pickup).Max(row => row.Sequence) + 1,
                JourneyStopRole.Pickup,
                selected.Route.PickupStationId,
                selected.Route.PickupStationRiotId,
                selected.Route.RouteEvidenceId,
                "TO_PICKUP")
            : null;
        long expectedSessionGeneration = scoring.Onboard.SessionGeneration;
        DemandIntakeOutcome outcome = await intakeCoordinator.JoinJourneyAsync(
            selected.Snapshot,
            runtime.JourneyId,
            appended,
            new JourneyDemandPlan(
                selected.Snapshot.DemandId,
                existingStop?.Sequence ?? appended!.Sequence,
                selected.ExpectedBasketCount,
                selected.TargetSlots),
            token => FinalDynamicFactsReadyAsync(
                expectedSessionGeneration,
                selected.TargetSlots,
                token),
            cancellationToken).ConfigureAwait(false);
        if (outcome != DemandIntakeOutcome.Accepted)
        {
            await SetBacklogReasonAsync(
                selected.Snapshot.DemandId,
                BacklogReasonFor(outcome),
                timeProvider.GetUtcNow(),
                cancellationToken).ConfigureAwait(false);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Ends the vehicle's business at a stop and sends it on: to the gate when loading is over, or
    /// to the next stop that still has something to load. Either way the move needs a pre-departure
    /// safety check, which is what proves every door this stop opened is shut again.
    /// </summary>
    private async Task ConcludeLoadingStopAsync(
        JourneyRuntimeRow runtime,
        IReadOnlyList<JourneyStopRow> stops,
        JourneyStopRow stop,
        IReadOnlyList<JourneyDemandRow> demands,
        SessionRecoveryRow session,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (CloseLoading(runtime, demands, now) is { } reason)
        {
            runtime.LoadingClosedReason ??= reason;
        }
        if (HoldingExpired(runtime, now))
        {
            // The limit is a hard one, so what has not started loading is terminated here rather
            // than carried to the gate unloadable. CANCELLED_BY_STOP_COMPLETE is the registered
            // reason for "loading at this stop is judged finished and the untouched demands end with
            // it" -- UC-002 reaches it by an operator pressing the button, this reaches it by the
            // clock, and the audit subject is the same. Leaving them Planned would also hold the
            // vehicle's lease forever: a journey is settled only when no demand is still open.
            foreach (JourneyDemandRow abandoned in demands
                .Where(row => row.State == JourneyDemandState.Planned && row.LoadCommandedAt is null)
                .ToArray())
            {
                await store.CancelDemandBeforeLoadAsync(
                    abandoned.DemandId, "CANCELLED_BY_STOP_COMPLETE", now, cancellationToken)
                    .ConfigureAwait(false);
            }
            demands = await DemandsAsync(runtime, cancellationToken).ConfigureAwait(false);
        }
        JourneyStopRow? nextPickup = stops.FirstOrDefault(row =>
            row.Role == JourneyStopRole.Pickup &&
            row.Sequence > stop.Sequence &&
            UncommandedAt(demands, row).Length > 0);
        JourneyStopRow target = nextPickup
            ?? stops.Single(row => row.Role == JourneyStopRole.Gate);
        if (target.Role == JourneyStopRole.Gate)
        {
            // Heading for the gate always means loading is over, so the field always names why.
            // Without this it would read null on the ordinary case -- nothing more on offer -- which
            // is indistinguishable from "still loading".
            runtime.LoadingClosedReason ??= "NO_FURTHER_CARGO";
        }
        if (target.Role == JourneyStopRole.Gate && !demands.Any(row =>
                row.State is JourneyDemandState.Planned or JourneyDemandState.Loaded))
        {
            // Nothing aboard and nothing left to load: there is nothing for the gate to receive, so
            // the journey ends here rather than driving an empty vehicle to it.
            stop.State = JourneyStopState.Completed;
            SetStage(runtime, JourneyRuntimeStage.Completed, now, stop);
            return;
        }

        await publisher.PublishPreDepartureSafetyCheckAsync(
            stop.PreDepartureSafetyCheckMessageId,
            runtime.AgvId,
            session.SessionGeneration,
            new PreDepartureSafetyCheckCommand(
                stop.PreDepartureSafetyCheckId,
                demands.OrderBy(row => row.CreatedAt).First().DemandId,
                target.MovementLegId,
                session.SafetyRevision ?? throw new InvalidDataException("Safety revision is required."),
                target.StationId),
            cancellationToken).ConfigureAwait(false);
        runtime.NextStopSequence = target.Sequence;
        SetStage(runtime, JourneyRuntimeStage.AwaitingDepartureSafety, now, stop);
    }

    /// <summary>One operator entry, paired with the demand its sublot names.</summary>
    private sealed record SublotEntry(ProtocolInboxRow Submission, JourneyDemandRow Demand);

    /// <summary>
    /// One pass over the catalog: which candidates may be dispatched, and the Onboard facts they
    /// were judged against. <see cref="Onboard"/> is null when the vehicle told us nothing usable,
    /// which is also why the list is then empty -- the two travel together so a caller cannot act on
    /// a candidate without the facts behind it.
    /// </summary>
    private sealed record CandidateScoring(
        IReadOnlyList<EligibleCandidate> Eligible,
        OnboardFacts? Onboard,
        bool SlotStarved);

    private sealed record EligibleCandidate(
        AcceptedDemandSnapshot Snapshot,
        ResolvedJourneyRoute Route,
        int ExpectedBasketCount,
        int[] TargetSlots,
        DateTimeOffset FirstSeenAt);

    private sealed record ResolvedJourneyRoute(
        string DispatchZone,
        string RouteEvidenceId,
        string PickupStationId,
        int PickupStationRiotId);

    private sealed record OnboardFacts(
        long SessionGeneration,
        int[] AvailableSlots,
        bool DepartureSafe,
        bool VehicleStopped,
        bool AllTargetSlotsLocked,
        bool AllUnlockOutputsReset,
        bool UnknownPresent);
}
