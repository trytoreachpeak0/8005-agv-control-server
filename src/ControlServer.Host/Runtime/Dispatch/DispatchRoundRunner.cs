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
/// the engine and every store of the round are, and that is load-bearing: a vehicle whose segment ends early clears
/// the change tracker here, and that has to be the tracker every later save of the round goes through.
/// </para>
/// <para>
/// <b>One vehicle's trouble is its own</b> (control-server#231): a segment that runs out its budget or throws is
/// dropped, and the round carries on with the vehicles behind it and still reports at its end.
/// </para>
/// <para>
/// It logs under <see cref="JourneyRuntimeEngine"/>'s category, with the event ids it had there (2101, 2104, 2106)
/// plus three of control-server#231's own — 2123 a segment that threw, 2124 a segment that broke one of this
/// server's invariants, 2125 an in-transit vehicle that could not be asked — so a log filter or an alert written
/// against the engine still sees the round.
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
    IInTransitDispatchQualification inTransitQualification,
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
    private static readonly Action<ILogger, string, string, Exception?> LogVehicleRoundFailed =
        LoggerMessage.Define<string, string>(
            LogLevel.Warning,
            new EventId(2123, nameof(LogVehicleRoundFailed)),
            "Vehicle {AgvId} could not be served this round: {ExceptionType}. The round moved on to the " +
            "remaining vehicles.");
    private static readonly Action<ILogger, string, string, Exception?> LogVehicleRoundFaulted =
        LoggerMessage.Define<string, string>(
            LogLevel.Error,
            new EventId(2124, nameof(LogVehicleRoundFaulted)),
            "Vehicle {AgvId} broke one of this server's own invariants and was skipped: {ExceptionType}. " +
            "The round moved on, but this is a defect rather than an unreachable peer -- it will not fix " +
            "itself next round.");
    private static readonly Action<ILogger, string, string, Exception?> LogInTransitQualificationFailed =
        LoggerMessage.Define<string, string>(
            LogLevel.Warning,
            new EventId(2125, nameof(LogInTransitQualificationFailed)),
            "Asking whether vehicle {AgvId} may take an appended demand failed: {ExceptionType}. The round " +
            "left it under way and went on to its end.");

    private readonly JourneyRuntimeOptions runtimeOptions = options.Value;

    /// <summary>
    /// Whether this is one of this server's own invariants being broken rather than a peer being unreachable.
    /// </summary>
    /// <remarks>
    /// Both of these mean a defect here -- a demand bound to content that does not match it, a plan frozen with
    /// two of its three versions (REQ-0305). Isolating the vehicle keeps the fleet moving, which is what
    /// control-server#231 is for, but it must not also make the defect look like weather: these are logged at
    /// Error under their own event id, because a peer that cannot be reached is fixed by the next round and this
    /// is not.
    /// </remarks>
    private static bool IsInvariantBreach(Exception error) =>
        error is BusinessIdentityConflictException or JourneyPlanFreezeIncompleteException;

    /// <summary>Runs one round for the free vehicles, and asks the in-transit path about the ones under way.</summary>
    /// <param name="currentMap">The Map this round was read against.</param>
    /// <param name="fixedStations">This round's fixed stations.</param>
    /// <param name="vehicles">The idle vehicles, in roster order: each goes down the admission chain.</param>
    /// <param name="vehiclesUnderWay">
    /// The vehicles already carrying a journey. They are never judged by the admission chain; each is asked of
    /// <see cref="IInTransitDispatchQualification"/> once the idle vehicles are served, and for now always refused.
    /// </param>
    /// <param name="admissionPolicyDrifted">Whether this round's admission policy refused to rebind.</param>
    /// <param name="cancellationToken">Ends the round on shutdown.</param>
    public async Task RunAsync(
        RiotMapStationCatalogSnapshot currentMap,
        IFixedTaskStationView fixedStations,
        IReadOnlyList<FleetVehicle> vehicles,
        IReadOnlyList<FleetVehicle> vehiclesUnderWay,
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
            List<string> claimedThisSegment = [];
            try
            {
                await DispatchForVehicleAsync(
                    round, vehicle, acceptedDemandIds, claimedThisSegment, backlogByDemandId, verdicts, now,
                    linked.Token)
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
                // The same withdrawal as the catch below, through the same method (control-server#239): a budget
                // that fires between a claim and its acceptance leaves the round carrying a demand nothing took,
                // and the round-end hook would clear that demand's structural block on the strength of it. Which
                // claims were made good on is read from the database there, not from the way the segment ended --
                // a budget can just as well fire the moment after the acceptance committed.
                await DropWhatTheSegmentStagedAsync(
                    backlogByDemandId, claimedThisSegment, acceptedDemandIds, cancellationToken)
                    .ConfigureAwait(false);
            }
            // A vehicle whose own reads fail -- an unreachable RIoT, an Onboard fact that cannot be read, an
            // evidence write that ran out its own five-second timeout -- is skipped exactly as a
            // budget-exhausted one is (control-server#231). Before this, the first vehicle to throw ended the
            // round: every vehicle behind it went unserved and the round-end hook below was never called, so
            // one vehicle failing every round starved the whole fleet.
            //
            // The filter is the host's token rather than the exception's type. A cancellation that is not the
            // host shutting down is one of this vehicle's own deadlines -- the budget above, or a timeout a
            // store started for one write -- and belongs to that vehicle, not to the round. Matching on
            // "anything that is not an OperationCanceledException" would let every one of those internal
            // timeouts end the round, which is the failure this ticket exists to remove.
            //
            // The vehicle is not added to completedVehicles either, and that is the same rule rather than a
            // second one: a segment that did not run to its end says nothing about that vehicle, so the
            // round-end hook must not read its absence as "this vehicle refused". The structural dispatch
            // block turns "every vehicle on the roster refused" into a fleet-wide alarm, and a vehicle that
            // was merely unreadable this round has refused nothing.
            catch (Exception error) when (!cancellationToken.IsCancellationRequested)
            {
                if (IsInvariantBreach(error))
                {
                    LogVehicleRoundFaulted(logger, vehicle.AgvId, error.GetType().Name, error);
                }
                else
                {
                    LogVehicleRoundFailed(logger, vehicle.AgvId, error.GetType().Name, error);
                }

                await DropWhatTheSegmentStagedAsync(
                    backlogByDemandId, claimedThisSegment, acceptedDemandIds, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        // The second path (control-server#209): a vehicle under way is asked whether it may take an appended demand,
        // never judged by the chain above. Nothing opens it yet, and a refusal leaves no trace -- no backlog row, no
        // verdict, nothing for the round-end hook below. Where these vehicles sit against the idle ones once they can
        // qualify (REQ-0205 has them compete for the same demand) is control-server#211's to decide.
        foreach (FleetVehicle vehicle in vehiclesUnderWay)
        {
            bool qualifies;
            try
            {
                qualifies = await inTransitQualification
                    .QualifiesAsync(round, vehicle, cancellationToken).ConfigureAwait(false);
            }
            // The same isolation as the idle vehicles above, and it matters here for a reason of position: this
            // loop sits between them and the round-end hook, so today -- with a path that only ever refuses --
            // nothing can throw, but the path control-server#211 puts here reads, and a read that failed would
            // take the hook down with it after every idle vehicle had already been served.
            //
            // The clearing below is dead weight today: nothing in this loop writes, so there is nothing staged
            // to drop. It is here because control-server#211's path will write, and a version of this catch
            // that only logs would then carry one vehicle's abandoned changes into the round-end hook.
            catch (Exception error) when (!cancellationToken.IsCancellationRequested)
            {
                LogInTransitQualificationFailed(logger, vehicle.AgvId, error.GetType().Name, error);
                await DropWhatTheSegmentStagedAsync(backlogByDemandId, cancellationToken).ConfigureAwait(false);
                continue;
            }

            // Outside the catch on purpose: this is the round refusing to ignore an answer it cannot act on,
            // not a vehicle failing, and swallowing it would be the silence it exists to prevent.
            if (qualifies)
            {
                throw new NotSupportedException(
                    $"Vehicle {vehicle.AgvId} qualified for an appended demand, which this round cannot yet dispatch " +
                    "(control-server#211).");
            }
        }

        // After every vehicle, a budget-exhausted one included. Whether any vehicle at all could take a demand
        // is only answerable across the fleet; JourneyBacklog, overwritten vehicle by vehicle, cannot say.
        await roundOutcomes.RecordAsync(new DispatchRoundOutcome(round, completedVehicles), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Drops whatever the segment that just ended had staged, and reads the backlog back as the database holds it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Shared by the two ways a segment can end early -- its budget running out and it throwing -- because the
    /// hazard is one hazard. What the abandoned segment staged is not that vehicle's decision any more and must not
    /// be written under the next vehicle's <c>SaveChanges</c>: stopping the work is not what keeps one vehicle's
    /// trouble off the others, clearing the tracker they all share is.
    /// </para>
    /// <para>
    /// <b>This overload is for a path that claims nothing</b> -- today only the in-transit qualification below,
    /// which asks one question and writes nothing. The empty claim list is what leaves the round's accepted set
    /// alone: the overload below only ever removes what that list names.
    /// </para>
    /// <para>
    /// It passes a set of its own rather than one shared static empty one. The overload below removes from what it
    /// is handed, so a static would be mutable state shared across every instance of this class -- safe only for
    /// as long as nobody hands this path a claim, which is the kind of thing control-server#211 finds by breaking
    /// it.
    /// </para>
    /// </remarks>
    private Task DropWhatTheSegmentStagedAsync(
        Dictionary<string, JourneyBacklogRow> backlogByDemandId,
        CancellationToken cancellationToken) =>
        DropWhatTheSegmentStagedAsync(
            backlogByDemandId, [], new HashSet<string>(StringComparer.Ordinal), cancellationToken);

    /// <inheritdoc cref="DropWhatTheSegmentStagedAsync(Dictionary{string, JourneyBacklogRow}, CancellationToken)"/>
    /// <remarks>
    /// <para>
    /// <b>It also takes back a claim the segment never made good on.</b> A vehicle claims its pick in the round's
    /// live accepted set before calling intake, so that the vehicles behind it stop considering that demand
    /// whatever intake then reports. When the segment ends early instead of reporting -- throwing, or its budget
    /// running out -- that claim can be a lie: the round would carry a demand it never accepted, and the round-end
    /// hook clears a structural dispatch block for every demand the round says was accepted -- so a block standing
    /// against a demand nothing took would be cleared, and raised again as new the next round.
    /// </para>
    /// <para>
    /// Whether it was made good on is decided by the database rather than by how the segment ended: the tracker is
    /// cleared first, so this reads what the acceptance transaction actually committed. A demand whose row is
    /// there was accepted, whatever threw or expired afterwards, and its claim stands.
    /// </para>
    /// </remarks>
    private async Task DropWhatTheSegmentStagedAsync(
        Dictionary<string, JourneyBacklogRow> backlogByDemandId,
        IReadOnlyList<string> claimedThisSegment,
        HashSet<string> acceptedDemandIds,
        CancellationToken cancellationToken)
    {
        dbContext.ChangeTracker.Clear();
        foreach (string demandId in claimedThisSegment)
        {
            bool accepted = await dbContext.AcceptedDemands
                .AnyAsync(row => row.DemandId == demandId, cancellationToken).ConfigureAwait(false);
            if (!accepted)
            {
                acceptedDemandIds.Remove(demandId);
            }
        }

        backlogByDemandId.Clear();
        foreach (JourneyBacklogRow row in await dbContext.JourneyBacklog
                     .ToArrayAsync(cancellationToken).ConfigureAwait(false))
        {
            backlogByDemandId[row.DemandId] = row;
        }
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
        List<string> claimedThisSegment,
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
        // Written down as well, so that a segment which throws instead of reporting can have the claim taken
        // back -- an unaccepted demand the round believes was accepted would have its structural block cleared.
        claimedDemandIds.Add(selected.Snapshot.DemandId);
        claimedThisSegment.Add(selected.Snapshot.DemandId);
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
