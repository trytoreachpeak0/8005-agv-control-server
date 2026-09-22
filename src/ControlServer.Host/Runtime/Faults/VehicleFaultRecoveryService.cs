using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime.Commands;
using ControlServer.Host.Runtime.Release;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ControlServer.Host.Runtime.Faults;

/// <summary>What a person asks the server to do about a faulted vehicle (control-server#299).</summary>
public enum VehicleFaultRecoveryAction
{
    /// <summary>The order that raised the fault has ended; clear the fault and let the vehicle work again.</summary>
    ClearFault,

    /// <summary>The order was held (PAUSED 7); continue it on the same vehicle (REQ-0239, first half).</summary>
    ResumeHeldOrder,

    /// <summary>
    /// Rebuild the ended order for the same vehicle and the same demand (control-server#318). Reserved: the criteria are
    /// judged and reported, and the answer is always that the rebuild is not available yet.
    /// </summary>
    ConfirmRebuild,
}

/// <summary>A person's request about one explicitly named vehicle.</summary>
/// <param name="OperatorId">Who asks. Recorded, and required: an unnamed request has nobody to record.</param>
/// <param name="FaultRemedied">The person confirms the cause of the fault has been removed on site.</param>
/// <param name="Note">Free text, recorded verbatim. Optional.</param>
public sealed record VehicleFaultRecoveryRequest(
    EmergencyStopSubject Subject,
    VehicleFaultRecoveryAction Action,
    string? OperatorId,
    bool FaultRemedied,
    string? Note);

/// <summary>How a request ended.</summary>
public enum VehicleFaultRecoveryOutcome
{
    /// <summary>The fault is cleared and the journey disposed of; see the disposition.</summary>
    Cleared,

    /// <summary>The held order was continued and the fault cleared.</summary>
    Resumed,

    /// <summary>The same clearance had already been made; nothing was done again.</summary>
    AlreadyCleared,

    /// <summary>Refused; the reasons name every criterion that is not met.</summary>
    Refused,

    /// <summary>The action is reserved and not implemented yet; the reasons still name every criterion not met.</summary>
    NotAvailable,
}

/// <summary>What was done with the vehicle's journey when its fault was cleared.</summary>
public static class VehicleFaultRecoveryDispositions
{
    /// <summary>No journey was waiting on the order, or nothing was cleared, so there was nothing to dispose of.</summary>
    public const string None = "NONE";

    /// <summary>Nothing was loaded: every demand still to load was released for redispatch and the journey closed.</summary>
    public const string Released = "RELEASED_FOR_REDISPATCH";

    /// <summary>Cargo may be on board: its binding stays, and the journey waits for a person (or #318's rebuild).</summary>
    public const string HeldForPerson = "HELD_FOR_PERSON";
}

/// <summary>The answer to one request.</summary>
public sealed record VehicleFaultRecoveryDecision(
    VehicleFaultRecoveryOutcome Outcome,
    IReadOnlyList<string> Reasons,
    string Disposition,
    long? FaultGeneration);

/// <summary>
/// The person's way out of a vehicle fault (control-server#299).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it has to exist.</b> The one symptom that raises a fault today is an in-flight order RIoT reports FAILED, and
/// until this the one path that cleared a fault was <see cref="VehicleFaultCoordinator.ResumeAsync"/>, which needs the
/// order HELD. A FAILED order never is, so the vehicle kept its fault for good: no new work, its demand never
/// redispatched, and REQ-0167's automatic release of a latch never earned. The only way out was editing the database.
/// </para>
/// <para>
/// <b>The server judges every criterion itself and believes nothing in the request</b> (the user's decision of
/// 2026-09-22, option F-a). What the person supplies is who they are and that the cause has been removed on site;
/// everything else is read here: the fault is in effect, the order that raised it has ended (FAILED, CANCELLED or
/// DELETED -- read, and an order that cannot be read has not ended), RIoT holds no unfinished order for the vehicle,
/// and no latch is engaged and no stop of this server's is still open. <b>Clearing never releases a latch</b>: a
/// latched vehicle is released first, by a person, through REQ-0356's entry point. Every unmet criterion is named,
/// never only the first, for the reason REQ-0356's refusal names them all.
/// </para>
/// <para>
/// <b>Clearing the fault is not enough on its own, for two reasons found reading the code, and both decide the shape
/// here.</b> The journey still waits in its arrival stage on the FAILED order, so the engine's next round would observe
/// that order again and record a new fault. And the release service releases a demand only while its vehicle is not
/// eligible for it -- a cleared fault takes the one trigger away, so nothing would ever release the demand either. So the
/// journey is disposed of in the same transaction as the fault is cleared: with nothing on board, every demand still
/// to load is released for redispatch (control-server#215's rows, written the same way) and the journey closes, which
/// frees the vehicle; with cargo possibly on board the cargo binding stays, nothing is released, and the journey goes to
/// <see cref="JourneyRuntimeStage.Blocked"/> under <see cref="CargoOnBoardReason"/> for a person -- REQ-0328 releases
/// only what has not been picked up, and rebuilding the order on the same vehicle is control-server#318.
/// </para>
/// <para>
/// <b>One transaction, so a crash leaves nothing half done.</b> The fault store saves as it goes; inside the
/// transaction its save commits nothing until the whole of the clearance does. A request cut short leaves the database
/// as it was before, and the same request made again does all of it.
/// </para>
/// <para>
/// <b>Serialised with the runtime loop</b> through <see cref="JourneyMutationGate"/>; see there for the interleaving that
/// would otherwise record a fresh fault on a vehicle whose journey was just closed.
/// </para>
/// <para>
/// <b>A second request for a clearance already made does nothing</b> and says so: the fault fact records the operator
/// clearance in <c>ClearedReason</c>, and a vehicle whose fault was cleared that way is answered
/// <see cref="VehicleFaultRecoveryOutcome.AlreadyCleared"/> without a read of RIoT or a write -- above all without
/// touching the journey the vehicle may have taken since.
/// </para>
/// <para>
/// <b>A held order is continued, not cleared</b>: <see cref="VehicleFaultRecoveryAction.ResumeHeldOrder"/> is REQ-0239's
/// first half, and goes to <see cref="VehicleFaultCoordinator.ResumeAsync"/>, which clears the fault only on a confirmed
/// continue. It is refused while a latch is engaged: under a latch RIoT refuses the continue (100021, Round27), and a
/// continued order is exactly what REQ-0356's release and the automatic release both refuse to release under -- a loop
/// with no way out on this server.
/// </para>
/// <para>
/// <b>The operator's identity</b> goes on the fault fact's <c>ClearedReason</c> (and, for a resume, on the continue's
/// audit row) and into event 9203 with the outcome and every reason. It stands in for REQ-0253's personal administrator
/// account until the server has logins, the arrangement the user accepted for REQ-0356 on 2026-09-15.
/// </para>
/// </remarks>
public sealed class VehicleFaultRecoveryService(
    ControlServerDbContext dbContext,
    IVehicleFaultStore faults,
    IRiotVehicleEmergencyFacts emergencyFacts,
    IRiotVehicleOrderFacts orderFacts,
    IRiotMovementGateway movement,
    EmergencyStopSupervisor emergencyStop,
    VehicleFaultCoordinator coordinator,
    VehicleMotionLedger ledger,
    JourneyMutationGate gate,
    TimeProvider timeProvider,
    ILogger<VehicleFaultRecoveryService> logger,
    TimeSpan? gateTimeout = null)
{
    /// <summary>The block a journey carries when its vehicle's fault was cleared with cargo possibly on board.</summary>
    public const string CargoOnBoardReason = "VEHICLE_FAULT_CLEARED_CARGO_ON_BOARD";

    /// <summary>The prefix of <c>ClearedReason</c> for a fault a person cleared here; the operator id follows the colon.</summary>
    public const string ClearedByOperatorReason = "FAULT_CLEARED_BY_OPERATOR";

    /// <summary>The prefix of the reason a held order is continued under; the operator id follows the colon.</summary>
    public const string ResumedByOperatorReason = "FAULT_RESUMED_BY_OPERATOR";

    /// <summary>
    /// How long a request waits for the runtime round in progress before it gives up, answering
    /// <c>FAULT_RECOVERY_RUNTIME_BUSY</c> (HTTP 503) and changing nothing.
    /// </summary>
    /// <remarks>
    /// A round is normally well under the poll interval, so thirty seconds is a round that is stuck, not a busy one. The
    /// wait cannot deadlock on this server's own account: nothing a round calls takes the gate, and nothing the recovery
    /// calls while holding it does either -- <c>VehicleFaultRecoveryTests.OnlyTheRuntimeRoundAndTheRecoveryTakeTheGate</c>
    /// fails the day a third holder appears, which is the day to look again.
    /// </remarks>
    public static readonly TimeSpan DefaultGateTimeout = TimeSpan.FromSeconds(30);

    private readonly TimeSpan gateWait = gateTimeout ?? DefaultGateTimeout;

    private static readonly Action<ILogger, string, string, string, string, string, string, Exception?> LogRequest =
        LoggerMessage.Define<string, string, string, string, string, string>(
            LogLevel.Warning,
            new EventId(9203, "VehicleFaultRecoveryRequest"),
            "Fault recovery {Action} on {AgvId} by {OperatorId} -> {Outcome} ({Disposition}): {ReasonsAndNote}.");

    public async Task<VehicleFaultRecoveryDecision> RecoverAsync(
        VehicleFaultRecoveryRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Subject);

        using IDisposable? round = await gate.TryEnterAsync(gateWait, cancellationToken).ConfigureAwait(false);
        if (round is null)
        {
            return Record(request, Refused(["FAULT_RECOVERY_RUNTIME_BUSY"], null));
        }

        dbContext.ChangeTracker.Clear();
        VehicleFaultRecoveryDecision decision = request.Action switch
        {
            VehicleFaultRecoveryAction.ClearFault => await ClearAsync(request, cancellationToken).ConfigureAwait(false),
            VehicleFaultRecoveryAction.ResumeHeldOrder => await ResumeAsync(request, cancellationToken).ConfigureAwait(false),
            VehicleFaultRecoveryAction.ConfirmRebuild => await JudgeRebuildAsync(request, cancellationToken).ConfigureAwait(false),
            _ => Refused(["FAULT_RECOVERY_ACTION_UNKNOWN"], null),
        };
        return Record(request, decision);
    }

    private async Task<VehicleFaultRecoveryDecision> ClearAsync(
        VehicleFaultRecoveryRequest request,
        CancellationToken cancellationToken)
    {
        EmergencyStopSubject subject = request.Subject;
        VehicleFaultFact? fault = await faults.ReadAsync(subject.AgvId, cancellationToken).ConfigureAwait(false);
        if (fault is { Level: VehicleFaultLevel.None } &&
            fault.ClearedReason?.StartsWith(ClearedByOperatorReason + ":", StringComparison.Ordinal) == true)
        {
            return new VehicleFaultRecoveryDecision(
                VehicleFaultRecoveryOutcome.AlreadyCleared, [], VehicleFaultRecoveryDispositions.None, fault.FaultGeneration);
        }

        Situation situation = await ReadSituationAsync(subject, cancellationToken).ConfigureAwait(false);
        List<string> reasons = [.. PersonReasons(request, askRemedy: true)];
        if (!InEffect(fault))
        {
            reasons.Add("FAULT_RECOVERY_FAULT_NOT_IN_EFFECT");
        }

        reasons.AddRange(EmergencyReasons(situation));
        reasons.AddRange(VehicleOrderReasons(situation));
        reasons.AddRange(CurrentOrderReasons(situation));
        if (reasons.Count > 0)
        {
            return Refused(reasons, fault?.FaultGeneration);
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        await using IDbContextTransaction transaction =
            await dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        string disposition = await DisposeOfTheJourneyAsync(subject.AgvId, situation.Journey, now, cancellationToken)
            .ConfigureAwait(false);
        await faults.ClearAsync(
            subject.AgvId,
            fault!.FaultGeneration,
            $"{ClearedByOperatorReason}:{request.OperatorId}",
            now,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        // A new episode starts from an empty window, as after a resumption.
        ledger.Forget(subject.DeviceKey);
        return new VehicleFaultRecoveryDecision(VehicleFaultRecoveryOutcome.Cleared, [], disposition, fault.FaultGeneration);
    }

    private async Task<VehicleFaultRecoveryDecision> ResumeAsync(
        VehicleFaultRecoveryRequest request,
        CancellationToken cancellationToken)
    {
        EmergencyStopSubject subject = request.Subject;
        VehicleFaultFact? fault = await faults.ReadAsync(subject.AgvId, cancellationToken).ConfigureAwait(false);
        FaultedCargoBinding? cargo = await faults.ReadLiveCargoAsync(subject.AgvId, cancellationToken).ConfigureAwait(false);
        Situation situation = await ReadSituationAsync(subject, cancellationToken).ConfigureAwait(false);

        List<string> reasons = [.. PersonReasons(request, askRemedy: true)];
        reasons.AddRange(EmergencyReasons(situation));
        VehicleFaultResumption? resumption = null;
        if (situation.Journey is not JourneyRuntimeRow journey || situation.Intent is not { OrderId: string orderId } intent ||
            situation.Order is not RiotOrderObservation order)
        {
            reasons.Add("FAULT_RECOVERY_CURRENT_ORDER_UNKNOWN");
        }
        else
        {
            string transportDemandKey = await dbContext.AcceptedDemands.AsNoTracking()
                .Where(row => row.DemandId == journey.DemandId)
                .Select(row => row.TransportDemandKey)
                .SingleAsync(cancellationToken).ConfigureAwait(false);
            resumption = new VehicleFaultResumption(
                new RiotOrderCommandTarget(subject.AgvId, intent.UpperId, orderId),
                journey.DemandId,
                transportDemandKey,
                $"{ResumedByOperatorReason}:{request.OperatorId}");
            reasons.AddRange(VehicleFaultCoordinator.ResumeRefusals(subject, resumption, fault, cargo, order));
        }

        if (reasons.Count > 0)
        {
            return Refused(reasons, fault?.FaultGeneration);
        }

        VehicleFaultResumeDecision resumed = await coordinator.ResumeAsync(subject, resumption!, cancellationToken)
            .ConfigureAwait(false);
        return resumed.Resumed
            ? new VehicleFaultRecoveryDecision(
                VehicleFaultRecoveryOutcome.Resumed, [], VehicleFaultRecoveryDispositions.None, fault?.FaultGeneration)
            : Refused(resumed.Refusals, fault?.FaultGeneration);
    }

    /// <summary>
    /// control-server#318's confirmation, reserved: judged against the same criteria and answered with every one unmet,
    /// and never carried out -- the rebuild itself is that ticket's.
    /// </summary>
    private async Task<VehicleFaultRecoveryDecision> JudgeRebuildAsync(
        VehicleFaultRecoveryRequest request,
        CancellationToken cancellationToken)
    {
        EmergencyStopSubject subject = request.Subject;
        VehicleFaultFact? fault = await faults.ReadAsync(subject.AgvId, cancellationToken).ConfigureAwait(false);
        Situation situation = await ReadSituationAsync(subject, cancellationToken).ConfigureAwait(false);

        List<string> reasons = [.. PersonReasons(request, askRemedy: false)];
        if (InEffect(fault))
        {
            reasons.Add("FAULT_RECOVERY_FAULT_STILL_IN_EFFECT");
        }

        reasons.AddRange(EmergencyReasons(situation));
        reasons.AddRange(VehicleOrderReasons(situation));
        reasons.AddRange(CurrentOrderReasons(situation));
        reasons.Add("FAULT_RECOVERY_REBUILD_NOT_AVAILABLE");
        return new VehicleFaultRecoveryDecision(
            VehicleFaultRecoveryOutcome.NotAvailable, reasons, VehicleFaultRecoveryDispositions.None, fault?.FaultGeneration);
    }

    /// <summary>What RIoT and this server's own tables say about the vehicle right now, read once per request.</summary>
    private async Task<Situation> ReadSituationAsync(EmergencyStopSubject subject, CancellationToken cancellationToken)
    {
        RiotVehicleEmergencyObservation emergency = await emergencyFacts
            .ReadEmergencyStateAsync(subject.DeviceKey, cancellationToken).ConfigureAwait(false);
        // A release REQ-0356 issued, whose read-back still saw the latch, has taken effect once the latch reads OK; left
        // unsettled it would read as a stop still open, and the person who has just released the vehicle would be told
        // to release it again.
        await emergencyStop.SettleReleaseTakenEffectAsync(subject, emergency, cancellationToken).ConfigureAwait(false);
        bool stopOpen = await emergencyStop.HasOpenEpisodeAsync(subject, cancellationToken).ConfigureAwait(false);
        RiotVehicleOrderObservation orders = await orderFacts
            .ReadUnfinishedOrdersAsync(subject.DeviceKey, cancellationToken).ConfigureAwait(false);

        JourneyRuntimeRow? journey = await dbContext.JourneyRuntimes.AsNoTracking()
            .SingleOrDefaultAsync(
                row => row.AgvId == subject.AgvId && row.Stage != JourneyRuntimeStage.Completed,
                cancellationToken).ConfigureAwait(false);
        if (journey is null || !WaitsOnAnOrder(journey))
        {
            // No order of this server's is in flight on the vehicle: nothing to have ended, and nothing to dispose of.
            return new Situation(emergency, stopOpen, orders, journey, Intent: null, Order: null, InFlight: false);
        }

        JourneyStopCursor stops = await JourneyStopCursor.LoadAsync(dbContext, journey, cancellationToken).ConfigureAwait(false);
        OrderIntentRow? intent = await dbContext.OrderIntents.AsNoTracking()
            .SingleOrDefaultAsync(row => row.MovementLegId == stops.Current.MovementLegId, cancellationToken)
            .ConfigureAwait(false);
        RiotOrderObservation? order = null;
        if (intent is { Status: "CONFIRMED", OrderId: not null })
        {
            try
            {
                order = await movement.ReconcileByUpperIdAsync(intent.UpperId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is HttpRequestException or InvalidDataException or TaskCanceledException &&
                                          !cancellationToken.IsCancellationRequested)
            {
                order = null;
            }
        }

        return new Situation(emergency, stopOpen, orders, journey, intent, order, InFlight: true);
    }

    /// <summary>
    /// What is done with the journey that waited on the ended order, staged and saved inside the caller's transaction.
    /// </summary>
    private async Task<string> DisposeOfTheJourneyAsync(
        string agvId,
        JourneyRuntimeRow? read,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (read is null || !WaitsOnAnOrder(read))
        {
            return VehicleFaultRecoveryDispositions.None;
        }

        JourneyRuntimeRow runtime = await dbContext.JourneyRuntimes
            .SingleAsync(row => row.JourneyId == read.JourneyId, cancellationToken).ConfigureAwait(false);
        List<JourneyDemandRow> memberships = await dbContext.Set<JourneyDemandRow>()
            .Where(row => row.JourneyId == runtime.JourneyId && row.RemovedAt == null)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        bool cargoBound = await faults.ReadLiveCargoAsync(agvId, cancellationToken).ConfigureAwait(false) is not null;

        // Anything other than "still to load" or "already ended" may be on the vehicle: loading, loaded, or a status this
        // was not written for. All of those keep the demand with the vehicle. The direction is deliberate -- a demand
        // released while its product is on board is a product nobody is looking for.
        bool mayCarry = cargoBound || memberships.Any(row => row.Status is not
            (JourneyDemandStatuses.PendingLoad or JourneyDemandStatuses.Unloaded or JourneyDemandStatuses.Terminated));
        if (mayCarry)
        {
            runtime.Stage = JourneyRuntimeStage.Blocked;
            runtime.SetBlockReason(CargoOnBoardReason, now);
            runtime.UpdatedAt = now;
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return VehicleFaultRecoveryDispositions.HeldForPerson;
        }

        JourneyDemandRow[] waiting = [.. memberships.Where(row => row.Status == JourneyDemandStatuses.PendingLoad)];
        await JourneyPlanRevisionStage.StageAsync(
            dbContext, runtime.JourneyId, [.. waiting.Select(row => row.DemandId)], currentStopMayGo: true, routing: null,
            cancellationToken).ConfigureAwait(false);
        await new PickupStopTermination(dbContext)
            .StageJourneyClosureAsync(runtime, DemandReleaseReasons.Released, now, cancellationToken)
            .ConfigureAwait(false);
        foreach (JourneyDemandRow membership in waiting)
        {
            await DemandReleaseService.StageReleasedMembershipAsync(dbContext, membership, now, cancellationToken)
                .ConfigureAwait(false);
        }

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return VehicleFaultRecoveryDispositions.Released;
    }

    /// <summary>The two stages in which the journey waits on a move order in flight -- the only ones a FAILED order stops.</summary>
    private static bool WaitsOnAnOrder(JourneyRuntimeRow journey) =>
        journey.Stage is JourneyRuntimeStage.AwaitingPickupArrival or JourneyRuntimeStage.AwaitingGateArrival;

    private static IEnumerable<string> PersonReasons(VehicleFaultRecoveryRequest request, bool askRemedy)
    {
        if (string.IsNullOrWhiteSpace(request.OperatorId))
        {
            yield return "FAULT_RECOVERY_OPERATOR_UNIDENTIFIED";
        }

        if (askRemedy && !request.FaultRemedied)
        {
            yield return "FAULT_RECOVERY_REMEDY_NOT_CONFIRMED";
        }
    }

    /// <summary>No latch, and no stop of this server's still open. Clearing never releases a latch (REQ-0356 does).</summary>
    private static IEnumerable<string> EmergencyReasons(Situation situation)
    {
        if (!situation.Emergency.IsKnown)
        {
            yield return "FAULT_RECOVERY_EMERGENCY_STATE_UNKNOWN";
        }
        else if (situation.Emergency.IsLatched)
        {
            yield return "FAULT_RECOVERY_EMERGENCY_LATCHED";
        }
        else if (situation.StopOpen)
        {
            yield return "FAULT_RECOVERY_EMERGENCY_STOP_OPEN";
        }
    }

    /// <summary>RIoT holds no unfinished order for the vehicle -- REQ-0356's reading of the same question.</summary>
    private static IEnumerable<string> VehicleOrderReasons(Situation situation)
    {
        if (!situation.Orders.IsKnown)
        {
            yield return "FAULT_RECOVERY_VEHICLE_ORDERS_UNKNOWN";
        }
        else if (situation.Orders.HasUnfinishedOrder == true)
        {
            yield return "FAULT_RECOVERY_VEHICLE_ORDER_NOT_FINISHED";
        }
    }

    /// <summary>
    /// The order the journey waits on has ended without arriving: FAILED, CANCELLED or DELETED, read as terminal. SUCCESS
    /// is an arrival, not an ending, and everything else -- running, held, hanging, SUSPENDED -- has not ended. An order
    /// that could not be read, or that RIoT does not know, has not been shown to have ended either.
    /// </summary>
    private static IEnumerable<string> CurrentOrderReasons(Situation situation)
    {
        if (!situation.InFlight)
        {
            yield break;
        }

        switch (situation.Order)
        {
            case { Kind: RiotOrderObservationKind.Terminal, OrderState: RiotOrderState.Failed or RiotOrderState.Cancelled or RiotOrderState.Deleted }:
                yield break;
            case { Kind: RiotOrderObservationKind.Active or RiotOrderObservationKind.Terminal }:
                yield return "FAULT_RECOVERY_CURRENT_ORDER_NOT_ENDED";
                yield break;
            default:
                yield return "FAULT_RECOVERY_CURRENT_ORDER_UNKNOWN";
                yield break;
        }
    }

    private static bool InEffect(VehicleFaultFact? fault) => fault is not null && fault.Level != VehicleFaultLevel.None;

    private static VehicleFaultRecoveryDecision Refused(IReadOnlyList<string> reasons, long? generation) =>
        new(VehicleFaultRecoveryOutcome.Refused, reasons, VehicleFaultRecoveryDispositions.None, generation);

    private VehicleFaultRecoveryDecision Record(VehicleFaultRecoveryRequest request, VehicleFaultRecoveryDecision decision)
    {
        LogRequest(
            logger,
            request.Action.ToString(),
            request.Subject.AgvId,
            string.IsNullOrWhiteSpace(request.OperatorId) ? "-" : request.OperatorId,
            decision.Outcome.ToString(),
            decision.Disposition,
            // Six is LoggerMessage's limit, so the note rides with the reasons rather than being dropped.
            $"{(decision.Reasons.Count == 0 ? "-" : string.Join(',', decision.Reasons))}; note: {(string.IsNullOrWhiteSpace(request.Note) ? "-" : request.Note)}",
            null);
        return decision;
    }

    private sealed record Situation(
        RiotVehicleEmergencyObservation Emergency,
        bool StopOpen,
        RiotVehicleOrderObservation Orders,
        JourneyRuntimeRow? Journey,
        OrderIntentRow? Intent,
        RiotOrderObservation? Order,
        bool InFlight);
}
