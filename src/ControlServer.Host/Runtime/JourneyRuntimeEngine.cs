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
        List<EligibleCandidate> eligible = [];
        foreach (AcceptedDemandSnapshot candidate in snapshot.Items)
        {
            string reason = "ELIGIBLE";
            ResolvedJourneyRoute? route = null;
            int expectedBasketCount = 0;
            int? packageCapacity = null;
            int[] targetSlots = [];
            if (!runtimeOptions.AllowedWorkTypes.Contains(candidate.WorkType, StringComparer.Ordinal) ||
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
            runtime.BlockReasonCode = "ONBOARD_SESSION_NOT_READY";
            runtime.UpdatedAt = now;
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
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
                    if (runtime.BlockReasonCode is not null)
                    {
                        runtime.UpdatedAt = now;
                        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    }
                    return;
                }
                await PublishLoadAsync(runtime, session, sublot.MessageId, cancellationToken).ConfigureAwait(false);
                runtime.ConsumedSublotMessageId = sublot.MessageId;
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
                }
                else
                {
                    return;
                }
                break;
            case JourneyRuntimeStage.AwaitingDepartureSafety:
                SafetyCheckObservation? safety = await FindSafeDepartureResultAsync(runtime, session, now, cancellationToken)
                    .ConfigureAwait(false);
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
        if (string.Equals(vehicle.BatteryState, "CHARGING", StringComparison.Ordinal) ||
            vehicle.BatteryPercent < runtimeOptions.MinimumBatteryPercent)
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
            Worklist(runtime, demand, runtime.PickupStationId, "PICKUP"),
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
            Worklist(runtime, demand, runtime.GateStationId, "GATE"),
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
            bool valid = RequiredString(root, "correlationId") == runtime.PreDepartureSafetyCheckMessageId &&
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
        ProtocolInboxRow? capability = await LatestInboxForSessionAsync(
            "CapabilitySnapshot", runtimeOptions.AgvId, session.SessionGeneration, cancellationToken)
            .ConfigureAwait(false);
        ProtocolInboxRow? safetyRow = await LatestInboxForSessionAsync(
            "SafetyStateSnapshot", runtimeOptions.AgvId, session.SessionGeneration, cancellationToken)
            .ConfigureAwait(false);
        if (capability is null || safetyRow is null)
        {
            return null;
        }
        using JsonDocument capabilityDocument = JsonDocument.Parse(capability.RequestJson);
        using JsonDocument safetyDocument = JsonDocument.Parse(safetyRow.RequestJson);
        JsonElement capabilityPayload = capabilityDocument.RootElement.GetProperty("payload");
        JsonElement safetyPayload = safetyDocument.RootElement.GetProperty("payload");
        // The two snapshots are session-scoped facts, not polled evidence: the protocol states no
        // cadence for them and Onboard sends each once per session, so ageing them out would cap
        // every session's admission window at MaximumEvidenceAge. What must still be bounded is
        // session *liveness* -- a dead peer leaves a Ready row and its last snapshots behind, and
        // CurrentReadySessionAsync has no liveness component of its own. So the age limit applies
        // to the last thing we heard from this session generation (Heartbeat arrives periodically,
        // and any inbound message counts, which also covers the window before the first one).
        // Content stays current through the session: both snapshots are read for this exact
        // SessionGeneration, a new generation supersedes them, and an unsafe SafetyStateChanged
        // fail-closes the session out of Ready. MaximumEvidenceAge still separately governs the
        // RIoT vehicle observation in ValidateDynamicFacts, which genuinely is polled.
        //
        // supportsBatchUnlock is deliberately not consulted: protocol-v0.1.1 declares it with no
        // semantics and its own canonical example sets it false, while the real question -- can
        // the vehicle operate this slot set -- is answered against AvailableSlots when the command
        // is actually sent. See docs/defects/20260829-intake-gates-on-unspecified-onboard-facts.md.
        DateTimeOffset now = timeProvider.GetUtcNow();
        DateTimeOffset capabilityAt = capabilityPayload.GetProperty("observedAt").GetDateTimeOffset();
        DateTimeOffset safetyAt = safetyPayload.GetProperty("observedAt").GetDateTimeOffset();
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
        JsonElement safety = safetyPayload.GetProperty("safety");
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

    private static CurrentStopWorklistProjection Worklist(
        JourneyRuntimeRow runtime,
        AcceptedDemandRow demand,
        string station,
        string role) => new(
            station,
            runtime.WorklistRevision,
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

    private static void SetStage(JourneyRuntimeRow runtime, JourneyRuntimeStage stage, DateTimeOffset now)
    {
        runtime.Stage = stage;
        runtime.BlockReasonCode = null;
        runtime.UpdatedAt = now;
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
