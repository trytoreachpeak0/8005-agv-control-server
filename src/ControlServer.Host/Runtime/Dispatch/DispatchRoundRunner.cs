using System.Security.Cryptography;
using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime.Dispatch.Criteria;
using ControlServer.Host.Runtime.Fleet;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ControlServer.Host.Runtime.Dispatch;

/// <summary>
/// One dispatch round: reads the catalog, judges every candidate for every free vehicle in series, and takes on at
/// most one demand per vehicle (control-server#209 moved it here, unchanged, out of <see cref="JourneyRuntimeEngine"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>It shares the engine's scope.</b> Registered scoped, it is handed the same <see cref="ControlServerDbContext"/>
/// the engine and every store of the round are, and that is load-bearing: a vehicle that runs out its budget clears
/// the change tracker here, and that has to be the tracker every later save of the round goes through.
/// </para>
/// <para>
/// It logs under <see cref="JourneyRuntimeEngine"/>'s category, with the event ids it had there (2101, 2104, 2106), so
/// a log filter or an alert written against the engine still sees the round.
/// </para>
/// </remarks>
public sealed class DispatchRoundRunner(
    ControlServerDbContext dbContext,
    IMesIngestCatalog catalog,
    IRiotVehicleFacts vehicleFacts,
    JourneyIntakeCoordinator intakeCoordinator,
    DispatchAdmissionChain admissionChain,
    IDispatchCandidateRanker candidateRanker,
    VehicleDispatchPolicyAccess dispatchPolicy,
    IAreaAssignmentStore areaAssignments,
    IVehicleSlotPositionReader slotPositions,
    IDispatchRoundOutcomeSink roundOutcomes,
    OnboardDispatchFactsReader onboardFacts,
    IOptions<JourneyRuntimeOptions> options,
    TimeProvider timeProvider,
    ILogger<JourneyRuntimeEngine> logger)
{
    // The backlog's decision fingerprint is a hash over this serialisation, so it is the engine's setting exactly: a
    // different one would read every backlog row written before the move as a changed demand.
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private static readonly Action<ILogger, Exception?> LogCatalogPollFailed = LoggerMessage.Define(
        LogLevel.Warning,
        new EventId(2101, nameof(LogCatalogPollFailed)),
        "MesIngest catalog polling failed closed; no journey was accepted.");
    private static readonly Action<ILogger, string, int, Exception?> LogVehicleRoundBudgetExhausted =
        LoggerMessage.Define<string, int>(
            LogLevel.Warning,
            new EventId(2104, nameof(LogVehicleRoundBudgetExhausted)),
            "Vehicle {AgvId} exhausted its {BudgetMilliseconds} ms dispatch budget; the round moved on " +
            "to the remaining vehicles.");
    private static readonly Action<ILogger, string, string, Exception?> LogVehicleOccupancyConflict =
        LoggerMessage.Define<string, string>(
            LogLevel.Error,
            new EventId(2106, nameof(LogVehicleOccupancyConflict)),
            "Vehicle {AgvId} already holds an in-flight order; the claim for {UpperId} was refused by " +
            "the occupancy index.");

    private readonly JourneyRuntimeOptions runtimeOptions = options.Value;

    public async Task RunAsync(
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
        OnboardDispatchFacts? onboard = await onboardFacts.ReadOnboardFactsAsync(fleetVehicle.AgvId, cancellationToken)
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
        OnboardDispatchFacts? onboard = await onboardFacts.ReadOnboardFactsAsync(fleetVehicle.AgvId, cancellationToken)
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
    /// Blocks the journey the occupancy claim was refused for. The same three assignments as the engine's own
    /// <c>Block</c>, which the advance side keeps: a journey blocked here is advanced there, and both read it alike.
    /// </summary>
    private static void Block(JourneyRuntimeRow runtime, string reason, DateTimeOffset now)
    {
        runtime.Stage = JourneyRuntimeStage.Blocked;
        runtime.SetBlockReason(reason, now);
        runtime.UpdatedAt = now;
    }
}
