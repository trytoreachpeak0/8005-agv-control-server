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
            string[] runtimeDemandIds = await dbContext.JourneyRuntimes
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

        await AdvanceAsync(active[0], cancellationToken).ConfigureAwait(false);
    }

    private async Task DiscoverAndAcceptAsync(
        RiotMapStationCatalogSnapshot currentMap,
        RiotMapStation gate,
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
                    else if (onboard.AvailableSlots.Length < expectedBasketCount)
                    {
                        reason = "SLOT_CAPACITY_TEMPORARILY_UNAVAILABLE";
                    }
                    else
                    {
                        targetSlots = onboard.AvailableSlots.Take(expectedBasketCount).ToArray();
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

        if (eligible.Count == 0)
        {
            return;
        }
        if (await dbContext.VehicleDispatchLeases.AnyAsync(
                row => row.VehicleKey == runtimeOptions.VehicleKey && row.ReleasedAt == null,
                cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        EligibleCandidate selected = eligible
            .OrderBy(item => item.FirstSeenAt)
            .ThenBy(item => item.Snapshot.CreatedAt)
            .ThenBy(item => item.Snapshot.DemandId, StringComparer.Ordinal)
            .First();
        long expectedSessionGeneration = onboard?.SessionGeneration
            ?? throw new InvalidOperationException("An eligible candidate requires current Onboard facts.");
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
        OrderIntent pickup = new(
            plan.PickupMovementLegId,
            selected.Snapshot.DemandId,
            plan.PickupUpperId,
            "TO_PICKUP",
            plan.PickupStationId,
            intakeAt,
            plan.VehicleKey,
            plan.MapId,
            plan.PickupStationRiotId,
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
                result.IntakeOutcome switch
                {
                    DemandIntakeOutcome.CandidateGone => "FINAL_CATALOG_CANDIDATE_GONE",
                    DemandIntakeOutcome.CandidateChanged => "FINAL_CATALOG_DECISION_FACT_CHANGED",
                    DemandIntakeOutcome.FinalAdmissionRejected => "FINAL_DYNAMIC_FACTS_NOT_READY",
                    _ => throw new InvalidOperationException(
                        $"Unsupported intake outcome '{result.IntakeOutcome}'.")
                },
                timeProvider.GetUtcNow(),
                cancellationToken).ConfigureAwait(false);
            return;
        }
        if (result.MovementDispatch?.Outcome != MovementDispatchOutcome.Confirmed)
        {
            JourneyRuntimeRow runtime = await dbContext.JourneyRuntimes
                .SingleAsync(row => row.DemandId == selected.Snapshot.DemandId, cancellationToken)
                .ConfigureAwait(false);
            runtime.BlockReasonCode = result.MovementDispatch?.Outcome.ToString() ?? "PICKUP_DISPATCH_NOT_CONFIRMED";
            runtime.UpdatedAt = now;
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task AdvanceAsync(JourneyRuntimeRow runtime, CancellationToken cancellationToken)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
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
            RuntimeMessageIds(runtime),
            cancellationToken).ConfigureAwait(false);

        switch (runtime.Stage)
        {
            case JourneyRuntimeStage.AwaitingPickupArrival:
                if (!await EnsureMovementConfirmedAsync(
                        runtime, runtime.PickupUpperId, "PICKUP", cancellationToken).ConfigureAwait(false))
                {
                    return;
                }
                if (!await IsTrustedArrivalAsync(runtime, "TO_PICKUP", session, cancellationToken).ConfigureAwait(false))
                {
                    return;
                }
                await PublishPickupStateAsync(runtime, session, cancellationToken).ConfigureAwait(false);
                SetStage(runtime, JourneyRuntimeStage.AwaitingSublot, now);
                break;
            case JourneyRuntimeStage.AwaitingSublot:
                ProtocolInboxRow? sublot = await FindMatchingSublotAsync(runtime, session, cancellationToken)
                    .ConfigureAwait(false);
                if (sublot is null)
                {
                    if (await TryTimeOutSublotWaitAsync(runtime, now, cancellationToken).ConfigureAwait(false))
                    {
                        return;
                    }
                    if (runtime.BlockReasonCode is not null)
                    {
                        runtime.UpdatedAt = now;
                        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    }
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
                else if (load?.Status == StationOperationStatus.Committed)
                {
                    await store.SettleAnsweredCommandAsync(
                        runtime.LoadCommandMessageId, now, cancellationToken).ConfigureAwait(false);
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
                }
                else
                {
                    return;
                }
                break;
            case JourneyRuntimeStage.AwaitingDepartureSafety:
                SafetyCheckObservation? safety = await AwaitSafeDepartureResultAsync(
                    runtime, session, cancellationToken).ConfigureAwait(false);
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
                OrderIntent gateIntent = GateIntent(runtime, now);
                await new WireToGateStore(dbContext).AuthorizeMovementAsync(
                    gateIntent, safety, now, cancellationToken).ConfigureAwait(false);
                MovementDispatchResult dispatch = await movementDispatch.ReconcileOrCreateAsync(
                    runtime.GateUpperId, cancellationToken).ConfigureAwait(false);
                runtime.ConsumedSafetyResultMessageId = await FindSafetyResultMessageIdAsync(
                    runtime.PreDepartureSafetyCheckId, cancellationToken).ConfigureAwait(false);
                await store.SettleAnsweredCommandAsync(
                    runtime.PreDepartureSafetyCheckMessageId, now, cancellationToken).ConfigureAwait(false);
                SetStage(runtime, JourneyRuntimeStage.AwaitingGateArrival, now);
                runtime.BlockReasonCode = dispatch.Outcome == MovementDispatchOutcome.Confirmed
                    ? null
                    : dispatch.Outcome.ToString();
                break;
            case JourneyRuntimeStage.AwaitingGateArrival:
                if (!await EnsureMovementConfirmedAsync(
                        runtime, runtime.GateUpperId, "GATE", cancellationToken).ConfigureAwait(false))
                {
                    return;
                }
                if (!await IsTrustedArrivalAsync(runtime, "TO_GATE", session, cancellationToken).ConfigureAwait(false))
                {
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
            new VehicleBusinessProjection(runtime.VehicleBusinessRevision, "READY", false, "SUFFICIENT", []),
            cancellationToken).ConfigureAwait(false);
        await publisher.PublishCurrentStopWorklistAsync(
            runtime.WorklistMessageId,
            runtime.AgvId,
            session.SessionGeneration,
            Worklist(runtime, demand, runtime.PickupStationId, "PICKUP", runtime.WorklistRevision),
            cancellationToken).ConfigureAwait(false);
        await publisher.PublishUpcomingStopPlanAsync(
            runtime.PlanMessageId,
            runtime.AgvId,
            session.SessionGeneration,
            PickupPlan(runtime),
            cancellationToken).ConfigureAwait(false);
        await publisher.PublishSublotEntryRequestAsync(
            runtime.SublotRequestMessageId,
            runtime.AgvId,
            session.SessionGeneration,
            new SublotEntryRequest(
                runtime.DemandId,
                runtime.OperationSessionId,
                runtime.PickupStationId,
                runtime.WorklistRevision,
                demand.Sublot),
            cancellationToken).ConfigureAwait(false);
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
            runtime.PickupStationId,
            demand.WorkType).ConfigureAwait(false);
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
            new VehicleBusinessProjection(runtime.VehicleBusinessRevision + 1, "READY", false, "SUFFICIENT", []),
            cancellationToken).ConfigureAwait(false);
        await publisher.PublishCurrentStopWorklistAsync(
            runtime.GateWorklistMessageId,
            runtime.AgvId,
            session.SessionGeneration,
            Worklist(runtime, demand, runtime.GateStationId, "GATE", runtime.WorklistRevision + 1),
            cancellationToken).ConfigureAwait(false);
        await publisher.PublishUpcomingStopPlanAsync(
            runtime.GatePlanMessageId,
            runtime.AgvId,
            session.SessionGeneration,
            GatePlan(runtime),
            cancellationToken).ConfigureAwait(false);
        int[] slots = JsonSerializer.Deserialize<int[]>(runtime.TargetSlotsJson) ?? [];
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
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<ProtocolInboxRow?> FindMatchingSublotAsync(
        JourneyRuntimeRow runtime,
        SessionRecoveryRow session,
        CancellationToken cancellationToken)
    {
        AcceptedDemandRow demand = await dbContext.AcceptedDemands.SingleAsync(
            row => row.DemandId == runtime.DemandId, cancellationToken).ConfigureAwait(false);
        ProtocolInboxRow[] rows = await dbContext.ProtocolInbox.AsNoTracking()
            .Where(row => row.MessageType == "SublotSubmitted")
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        rows = rows.OrderBy(row => row.ReceivedAt).ToArray();
        foreach (ProtocolInboxRow row in rows)
        {
            using JsonDocument document = JsonDocument.Parse(row.RequestJson);
            JsonElement root = document.RootElement;
            JsonElement payload = root.GetProperty("payload");
            bool matches = RequiredString(root, "agvId") == runtime.AgvId &&
                           root.GetProperty("sessionGeneration").GetInt64() == session.SessionGeneration &&
                           RequiredString(payload, "demandId") == runtime.DemandId &&
                           RequiredString(payload, "operationSessionId") == runtime.OperationSessionId &&
                           RequiredString(payload, "stationId") == runtime.PickupStationId &&
                           payload.GetProperty("worklistRevision").GetInt64() == runtime.WorklistRevision &&
                           RequiredString(payload, "sublot") == demand.Sublot;
            if (matches)
            {
                if (!await store.IsTaskTypeAllowedAsync(
                        runtime.PickupStationId, demand.WorkType, cancellationToken).ConfigureAwait(false))
                {
                    runtime.BlockReasonCode = "TASK_TYPE_NOT_ALLOWED_AT_STATION";
                    return null;
                }
                return row;
            }
            runtime.BlockReasonCode = "SUBLOT_SUBMISSION_MISMATCH";
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
            runtime.BlockReasonCode = null;
            SafetyCheckObservation? safety = await FindSafeDepartureResultAsync(
                runtime, session, timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
            if (safety is not null || runtime.BlockReasonCode is not null || attempt >= attempts)
            {
                return safety;
            }
            await Task.Delay(step, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<SafetyCheckObservation?> FindSafeDepartureResultAsync(
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
                runtime.BlockReasonCode = "PRE_DEPARTURE_SAFETY_NOT_VALID";
                return null;
            }
            if (session.SafetyRevision is not long safetyRevision)
            {
                return null;
            }
            return new SafetyCheckObservation(
                runtime.PreDepartureSafetyCheckId,
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

    private JourneyExecutionPlan CreatePlan(EligibleCandidate candidate, DateTimeOffset now)
    {
        string demandId = candidate.Snapshot.DemandId;
        return new JourneyExecutionPlan(
            runtimeOptions.AgvId,
            runtimeOptions.VehicleKey,
            runtimeOptions.AgvLifecycleGeneration,
            runtimeOptions.MapId,
            runtimeOptions.MapIdentity,
            candidate.Route.DispatchZone,
            candidate.Route.RouteEvidenceId,
            candidate.Route.PickupStationId,
            candidate.Route.PickupStationRiotId,
            runtimeOptions.GateStationId,
            runtimeOptions.GateStationRiotId,
            candidate.ExpectedBasketCount,
            candidate.TargetSlots,
            StableGuid(demandId, "operation-session"),
            StableGuid(demandId, "pickup-leg"),
            $"W2G-{demandId}-PICKUP-{runtimeOptions.DispatchGeneration}",
            StableGuid(demandId, "gate-leg"),
            $"W2G-{demandId}-GATE-{runtimeOptions.DispatchGeneration}",
            runtimeOptions.DispatchGeneration,
            now);
    }

    private static OrderIntent GateIntent(JourneyRuntimeRow runtime, DateTimeOffset now) => new(
        runtime.GateMovementLegId,
        runtime.DemandId,
        runtime.GateUpperId,
        "TO_GATE",
        runtime.GateStationId,
        now,
        runtime.VehicleKey,
        runtime.MapId,
        runtime.GateStationRiotId,
        runtime.AgvLifecycleGeneration,
        runtime.DispatchGeneration);

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
        long revision) => new(
            station,
            revision,
            runtime.OperationSessionId,
            [new CurrentStopWorklistItem(
                demand.DemandId,
                demand.TransportDemandKey,
                demand.Sublot,
                demand.WorkType,
                role,
                runtime.ExpectedBasketCount)]);

    private static UpcomingStopPlanProjection PickupPlan(JourneyRuntimeRow runtime) => new(
        runtime.PlanRevision,
        runtime.DemandId,
        [
            new UpcomingMovementLeg(runtime.PickupMovementLegId, "TO_PICKUP", 1, runtime.PickupStationId, runtime.MapIdentity, "ARRIVED"),
            new UpcomingMovementLeg(runtime.GateMovementLegId, "TO_GATE", 2, runtime.GateStationId, runtime.MapIdentity, "PLANNED")
        ]);

    private static UpcomingStopPlanProjection GatePlan(JourneyRuntimeRow runtime) => new(
        runtime.PlanRevision + 1,
        runtime.DemandId,
        [
            new UpcomingMovementLeg(runtime.PickupMovementLegId, "TO_PICKUP", 1, runtime.PickupStationId, runtime.MapIdentity, "COMPLETED"),
            new UpcomingMovementLeg(runtime.GateMovementLegId, "TO_GATE", 2, runtime.GateStationId, runtime.MapIdentity, "ARRIVED")
        ]);

    private static string BusinessHash(string demandId, string sublot, string operation, IEnumerable<int> slots) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{demandId}|{sublot}|{operation}|{string.Join(',', slots)}"))).ToLowerInvariant();

    private static HashSet<string> RuntimeMessageIds(JourneyRuntimeRow runtime) =>
    [
        runtime.VehicleBusinessMessageId,
        runtime.WorklistMessageId,
        runtime.PlanMessageId,
        runtime.SublotRequestMessageId,
        runtime.LoadCommandMessageId,
        runtime.PreDepartureSafetyCheckMessageId,
        runtime.GateVehicleBusinessMessageId,
        runtime.GateWorklistMessageId,
        runtime.GatePlanMessageId,
        runtime.UnloadCommandMessageId
    ];

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
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (runtimeOptions.SublotWaitTimeout <= TimeSpan.Zero ||
            runtime.SublotWaitStartedAt is not { } startedAt ||
            now - startedAt < runtimeOptions.SublotWaitTimeout)
        {
            return false;
        }

        if (await store.CancelDemandBeforeLoadAsync(
                runtime.DemandId, "CANCELLED_BY_STATION_TIMEOUT", now, cancellationToken)
            .ConfigureAwait(false))
        {
            LogSublotWaitTimedOut(logger, runtime.DemandId, runtimeOptions.SublotWaitTimeout, null);
        }
        return true;
    }

    private static void SetStage(JourneyRuntimeRow runtime, JourneyRuntimeStage stage, DateTimeOffset now)
    {
        runtime.Stage = stage;
        runtime.BlockReasonCode = null;
        runtime.UpdatedAt = now;
        if (stage == JourneyRuntimeStage.AwaitingSublot)
        {
            runtime.SublotWaitStartedAt ??= now;
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
