using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime.Commands;
using ControlServer.Host.Runtime.Release;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;

namespace ControlServer.Host.Runtime.Faults;

/// <summary>What a person asks the server to do about a faulted vehicle (control-server#299).</summary>
public enum VehicleFaultRecoveryAction
{
    /// <summary>The order that raised the fault has ended; clear the fault and let the vehicle work again.</summary>
    ClearFault,

    /// <summary>The order was held (PAUSED 7); continue it on the same vehicle (REQ-0239, first half).</summary>
    ResumeHeldOrder,
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

    /// <summary>A person had already cleared or resumed this fault; nothing was done again.</summary>
    AlreadyCleared,

    /// <summary>Refused; the reasons name every criterion that is not met.</summary>
    Refused,
}

/// <summary>What was done with the vehicle's journey when its fault was cleared.</summary>
public static class VehicleFaultRecoveryDispositions
{
    /// <summary>No journey was waiting on the order, or nothing was cleared, so there was nothing to dispose of.</summary>
    public const string None = "NONE";

    /// <summary>
    /// The demand stays with the vehicle and nothing is released, cargo on board or not: the order is rebuilt for the same
    /// vehicle and the same demand once the delay is over and the vehicle may move (control-server#318).
    /// </summary>
    public const string RebuildScheduled = "REBUILD_SCHEDULED";

    /// <summary>
    /// The demand stays with the vehicle and nothing is released, but the order is not rebuilt: its rebuilt order ended again
    /// within the window (control-server#318's third guard), and the journey waits for a person.
    /// </summary>
    public const string RebuildStopped = "REBUILD_STOPPED";
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
/// everything else is read here: the fault is in effect, the order that raised it has FAILED (read, and an order that
/// cannot be read has not), RIoT holds no unfinished order for the vehicle, and no latch is engaged and no stop of this
/// server's is still open. <b>Clearing never releases a latch</b>: a latched vehicle is released first, by a person,
/// through REQ-0356's entry point. Every unmet criterion is named, never only the first, for the reason REQ-0356's
/// refusal names them all.
/// </para>
/// <para>
/// <b>Only FAILED, never CANCELLED or DELETED</b> (independent review M1). An order of this server's that someone cancelled
/// or deleted in RIoT is rebuilt by the engine on its own, as its first source of control-server#318, with no fault and no
/// person; a clearance must not be a second way into that. No fault is recorded on such an order today, so the refusal
/// (<c>FAULT_RECOVERY_CURRENT_ORDER_CANCELLED_IN_RIOT</c>) is not reachable in the product; it is pinned so that the two
/// sources cannot come to handle one ending twice.
/// </para>
/// <para>
/// <b>Clearing the fault is not enough on its own.</b> The journey still waits in its arrival stage on the FAILED order,
/// so the engine's next round would observe that order again and record a new fault. So the journey is disposed of in the
/// same transaction as the fault is cleared -- and since control-server#318 (the user's decision of 2026-09-23,
/// issuecomment-5787511271: "不改派啊，留在本车上"), disposing of it means keeping it: nothing is released, nothing is
/// redispatched and the journey does not close, cargo on board or not. The ending is recorded to be rebuilt for the same
/// vehicle and the same demand (<see cref="OwnOrderRebuilds"/>), which the engine does once the delay is over and the
/// vehicle may move; the journey stays in its arrival stage meanwhile, under <see cref="CargoOnBoardReason"/> or
/// <see cref="NothingOnBoardReason"/>, and the engine reads the record instead of the FAILED order from then on. A person on
/// site clearing the fault is that person saying the vehicle may move. Until control-server#318 this released every demand
/// still to load for redispatch and closed the journey when nothing was on board, and held a loaded journey
/// <see cref="JourneyRuntimeStage.Blocked"/> for a person.
/// </para>
/// <para>
/// <b>One transaction, so a crash leaves nothing half done.</b> The fault store saves as it goes; inside the
/// transaction its save commits nothing until the whole of the clearance does. A request cut short leaves the database
/// as it was before, and the same request made again does all of it.
/// </para>
/// <para>
/// <b>Serialised with the runtime loop</b> through <see cref="JourneyMutationGate"/>; see there for the interleaving that
/// would otherwise record a fresh fault on a vehicle whose journey was just closed. <b>No RIoT call is made while the gate
/// is held</b> (independent review M2): the gate is the whole fleet's runtime round, and RIoT is slowest exactly when
/// vehicles fault, so a call under it could hold every vehicle's round -- stop confirmation, REQ-0248's re-trigger,
/// dispatch -- for as long as RIoT's timeout. So a request reads this server's tables and then RIoT without the gate,
/// takes it, reads its own tables again, and refuses with <c>FAULT_RECOVERY_STATE_CHANGED</c> if the fault generation,
/// the journey's stage or the order it waits on moved in between; only then does it judge and commit, from the database
/// alone. The tables are read before RIoT so that a round landing between the two is always seen by the second read.
/// </para>
/// <para>
/// <b>A second request for a clearance already made does nothing</b> and says so: the fault fact records a person's
/// clearance or resumption in <c>ClearedReason</c>, and a vehicle whose fault was ended that way is answered
/// <see cref="VehicleFaultRecoveryOutcome.AlreadyCleared"/> without a read of RIoT or a write -- above all without
/// touching the journey the vehicle may have taken since. <b>The request is still judged for who made it first</b>
/// (independent review L3): an unnamed or unconfirmed request is refused for that even on a cleared vehicle, because
/// "already cleared" is an answer to the same person repeating a request, not a way round the two things a person must
/// supply. The same holds for a repeated resume, which is not continued a second time.
/// </para>
/// <para>
/// <b>A held order is continued, not cleared</b>: <see cref="VehicleFaultRecoveryAction.ResumeHeldOrder"/> is REQ-0239's
/// first half, and goes to <see cref="VehicleFaultCoordinator.ResumeAsync"/>, which clears the fault only on a confirmed
/// continue. It is refused while a latch is engaged: under a latch RIoT refuses the continue (100021, Round27), and a
/// continued order is exactly what REQ-0356's release and the automatic release both refuse to release under -- a loop
/// with no way out on this server. It is judged under the gate like a clearance, and the continue itself -- a RIoT
/// command -- is issued after the gate is released: the coordinator reads the fault and the order again right before
/// it and refuses unless the order is still HELD and the fault still in effect, and the fault store clears only the
/// generation it was given, so a round that moved on in between cannot have the wrong fault cleared. The gate cannot
/// stop a second resume of the same vehicle from being judged while the first one's continue is in flight, so a resume
/// also holds the vehicle's <see cref="VehicleFaultResumeFlights"/> flight from before its first read until the continue
/// is confirmed or refused; a concurrent one is refused with <c>FAULT_RECOVERY_RESUME_IN_PROGRESS</c>.
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
    VehicleFaultResumeFlights resumeFlights,
    IOptions<JourneyRuntimeOptions> runtimeOptions,
    TimeProvider timeProvider,
    ILogger<VehicleFaultRecoveryService> logger,
    TimeSpan? gateTimeout = null)
{
    /// <summary>
    /// The block a journey carries when its vehicle's fault was cleared with cargo possibly on board, until its order is
    /// rebuilt to deliver it (control-server#318).
    /// </summary>
    public const string CargoOnBoardReason = "VEHICLE_FAULT_CLEARED_CARGO_ON_BOARD";

    /// <summary>
    /// The block a journey carries when its vehicle's fault was cleared with nothing on board, until its order is rebuilt
    /// (control-server#318).
    /// </summary>
    public const string NothingOnBoardReason = "VEHICLE_FAULT_CLEARED_NOTHING_ON_BOARD";

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

        dbContext.ChangeTracker.Clear();
        VehicleFaultRecoveryDecision decision = request.Action switch
        {
            VehicleFaultRecoveryAction.ClearFault => await ClearAsync(request, cancellationToken).ConfigureAwait(false),
            VehicleFaultRecoveryAction.ResumeHeldOrder => await ResumeAsync(request, cancellationToken).ConfigureAwait(false),
            _ => Refused(["FAULT_RECOVERY_ACTION_UNKNOWN"], null),
        };
        return Record(request, decision);
    }

    private async Task<VehicleFaultRecoveryDecision> ClearAsync(
        VehicleFaultRecoveryRequest request,
        CancellationToken cancellationToken)
    {
        EmergencyStopSubject subject = request.Subject;
        List<string> person = [.. PersonReasons(request)];
        VehicleFaultFact? fault = await faults.ReadAsync(subject.AgvId, cancellationToken).ConfigureAwait(false);
        if (EndedByAPerson(fault))
        {
            return person.Count > 0 ? Refused(person, fault!.FaultGeneration) : AlreadyCleared(fault!);
        }

        Reading reading = await ReadAsync(subject, fault, cancellationToken).ConfigureAwait(false);

        using IDisposable? round = await gate.TryEnterAsync(gateWait, cancellationToken).ConfigureAwait(false);
        if (round is null)
        {
            return Refused(["FAULT_RECOVERY_RUNTIME_BUSY"], fault?.FaultGeneration);
        }

        // From here on: this server's tables only, no RIoT.
        Standing standing = await ReadStandingAsync(subject, reading.Emergency, cancellationToken).ConfigureAwait(false);
        if (!standing.Agrees(reading))
        {
            return Refused(["FAULT_RECOVERY_STATE_CHANGED"], standing.Fault?.FaultGeneration);
        }

        List<string> reasons = [.. person];
        if (!InEffect(standing.Fault))
        {
            reasons.Add("FAULT_RECOVERY_FAULT_NOT_IN_EFFECT");
        }

        reasons.AddRange(EmergencyReasons(reading.Emergency, standing.StopOpen));
        reasons.AddRange(VehicleOrderReasons(reading.Orders));
        reasons.AddRange(CurrentOrderReasons(reading));
        if (reasons.Count > 0)
        {
            return Refused(reasons, standing.Fault?.FaultGeneration);
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        await using IDbContextTransaction transaction =
            await dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        string disposition = await DisposeOfTheJourneyAsync(
                subject.AgvId, standing.Journey, standing.Intent, standing.Fault!, request.OperatorId!, now, cancellationToken)
            .ConfigureAwait(false);
        await faults.ClearAsync(
            subject.AgvId,
            standing.Fault!.FaultGeneration,
            $"{ClearedByOperatorReason}:{request.OperatorId}",
            now,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        // Nothing is sent to the vehicle: the journey goes on, so it has no closure to be told of (control-server#323's path 7
        // was the release this replaced), and the stop it shows is the one the rebuilt order goes to.

        // A new episode starts from an empty window, as after a resumption.
        ledger.Forget(subject.DeviceKey);
        return new VehicleFaultRecoveryDecision(
            VehicleFaultRecoveryOutcome.Cleared, [], disposition, standing.Fault.FaultGeneration);
    }

    private async Task<VehicleFaultRecoveryDecision> ResumeAsync(
        VehicleFaultRecoveryRequest request,
        CancellationToken cancellationToken)
    {
        EmergencyStopSubject subject = request.Subject;
        List<string> person = [.. PersonReasons(request)];
        VehicleFaultFact? fault = await faults.ReadAsync(subject.AgvId, cancellationToken).ConfigureAwait(false);
        if (EndedByAPerson(fault))
        {
            return person.Count > 0 ? Refused(person, fault!.FaultGeneration) : AlreadyCleared(fault!);
        }

        // Held until the continue is confirmed or refused: the continue goes out after the gate is released, so the gate
        // alone would let a second request judge the order still HELD and continue it again (see VehicleFaultResumeFlights).
        using IDisposable? flight = resumeFlights.TryBegin(subject.AgvId);
        if (flight is null)
        {
            return Refused(["FAULT_RECOVERY_RESUME_IN_PROGRESS"], fault?.FaultGeneration);
        }

        Reading reading = await ReadAsync(subject, fault, cancellationToken).ConfigureAwait(false);

        List<string> reasons = [.. person];
        VehicleFaultResumption? resumption = null;
        long? generation;
        using (IDisposable? round = await gate.TryEnterAsync(gateWait, cancellationToken).ConfigureAwait(false))
        {
            if (round is null)
            {
                return Refused(["FAULT_RECOVERY_RUNTIME_BUSY"], fault?.FaultGeneration);
            }

            Standing standing = await ReadStandingAsync(subject, reading.Emergency, cancellationToken).ConfigureAwait(false);
            generation = standing.Fault?.FaultGeneration;
            if (!standing.Agrees(reading))
            {
                return Refused(["FAULT_RECOVERY_STATE_CHANGED"], generation);
            }

            reasons.AddRange(EmergencyReasons(reading.Emergency, standing.StopOpen));
            FaultedCargoBinding? cargo = await faults.ReadLiveCargoAsync(subject.AgvId, cancellationToken).ConfigureAwait(false);
            if (standing.Journey is not JourneyRuntimeRow journey ||
                standing.Intent is not { OrderId: string orderId } intent ||
                reading.Order is not RiotOrderObservation order)
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
                reasons.AddRange(VehicleFaultCoordinator.ResumeRefusals(subject, resumption, standing.Fault, cargo, order));
            }
        }

        if (reasons.Count > 0)
        {
            return Refused(reasons, generation);
        }

        // The continue is a RIoT command, so it goes out after the gate is released; see the class remarks for why that
        // cannot clear the wrong fault.
        VehicleFaultResumeDecision resumed = await coordinator.ResumeAsync(subject, resumption!, cancellationToken)
            .ConfigureAwait(false);
        return resumed.Resumed
            ? new VehicleFaultRecoveryDecision(
                VehicleFaultRecoveryOutcome.Resumed, [], VehicleFaultRecoveryDispositions.None, generation)
            : Refused(resumed.Refusals, generation);
    }

    /// <summary>
    /// Everything a request needs from RIoT, read without the gate. This server's tables are read first: a runtime round
    /// that lands after them, while RIoT is being read, is then always seen by <see cref="ReadStandingAsync"/>.
    /// </summary>
    private async Task<Reading> ReadAsync(
        EmergencyStopSubject subject,
        VehicleFaultFact? fault,
        CancellationToken cancellationToken)
    {
        (JourneyRuntimeRow? journey, OrderIntentRow? intent) = await ReadJourneyAsync(subject, cancellationToken)
            .ConfigureAwait(false);
        RiotVehicleEmergencyObservation emergency = await emergencyFacts
            .ReadEmergencyStateAsync(subject.DeviceKey, cancellationToken).ConfigureAwait(false);
        RiotVehicleOrderObservation orders = await orderFacts
            .ReadUnfinishedOrdersAsync(subject.DeviceKey, cancellationToken).ConfigureAwait(false);
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

        return new Reading(fault, journey, intent, emergency, orders, order);
    }

    /// <summary>This server's own tables, read again under the gate: the facts a decision is committed on.</summary>
    private async Task<Standing> ReadStandingAsync(
        EmergencyStopSubject subject,
        RiotVehicleEmergencyObservation emergency,
        CancellationToken cancellationToken)
    {
        dbContext.ChangeTracker.Clear();
        VehicleFaultFact? fault = await faults.ReadAsync(subject.AgvId, cancellationToken).ConfigureAwait(false);
        (JourneyRuntimeRow? journey, OrderIntentRow? intent) = await ReadJourneyAsync(subject, cancellationToken)
            .ConfigureAwait(false);
        // A release REQ-0356 issued, whose read-back still saw the latch, has taken effect once the latch reads OK; left
        // unsettled it would read as a stop still open, and the person who has just released the vehicle would be told
        // to release it again. Settling writes this server's own audit, which is why it happens here and not before.
        await emergencyStop.SettleReleaseTakenEffectAsync(subject, emergency, cancellationToken).ConfigureAwait(false);
        bool stopOpen = await emergencyStop.HasOpenEpisodeAsync(subject, cancellationToken).ConfigureAwait(false);
        return new Standing(fault, journey, intent, stopOpen);
    }

    /// <summary>The vehicle's journey, and -- only while it waits on a move order -- the intent of that order.</summary>
    private async Task<(JourneyRuntimeRow? Journey, OrderIntentRow? Intent)> ReadJourneyAsync(
        EmergencyStopSubject subject,
        CancellationToken cancellationToken)
    {
        JourneyRuntimeRow? journey = await dbContext.JourneyRuntimes.AsNoTracking()
            .SingleOrDefaultAsync(
                row => row.AgvId == subject.AgvId && row.Stage != JourneyRuntimeStage.Completed,
                cancellationToken).ConfigureAwait(false);
        if (journey is null || !WaitsOnAnOrder(journey))
        {
            // No order of this server's is in flight on the vehicle: nothing to have ended, and nothing to dispose of.
            return (journey, null);
        }

        JourneyStopCursor stops = await JourneyStopCursor.LoadAsync(dbContext, journey, cancellationToken).ConfigureAwait(false);
        OrderIntentRow? intent = await dbContext.OrderIntents.AsNoTracking()
            .SingleOrDefaultAsync(row => row.MovementLegId == stops.Current.MovementLegId, cancellationToken)
            .ConfigureAwait(false);
        return (journey, intent);
    }

    /// <summary>
    /// What is done with the journey that waited on the ended order, staged and saved inside the caller's transaction: it is
    /// kept, and the ending recorded to be rebuilt for the same vehicle and demand (control-server#318).
    /// </summary>
    /// <remarks>
    /// Cargo decides only the code the journey waits under, and which of the two fault sources the record names: with cargo
    /// possibly on board the rebuilt order delivers it, with none it goes on to the pickup. Neither releases anything.
    /// </remarks>
    private async Task<string> DisposeOfTheJourneyAsync(
        string agvId,
        JourneyRuntimeRow? read,
        OrderIntentRow? intent,
        VehicleFaultFact fault,
        string operatorId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (read is null || !WaitsOnAnOrder(read) || intent is null)
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
        // was not written for. All of those count as cargo on board.
        bool mayCarry = cargoBound || memberships.Any(row => row.Status is not
            (JourneyDemandStatuses.PendingLoad or JourneyDemandStatuses.Unloaded or JourneyDemandStatuses.Terminated));
        JourneyStopRow stop = (await JourneyStopCursor.LoadAsync(dbContext, runtime, cancellationToken).ConfigureAwait(false))
            .Current;
        OwnOrderRebuildRow rebuild = await OwnOrderRebuilds.StageAsync(
            dbContext,
            runtime,
            stop,
            intent.UpperId,
            intent.OrderId,
            RiotOrderState.Failed,
            mayCarry ? OwnOrderRebuildSources.FaultClearedCargoOnBoard : OwnOrderRebuildSources.FaultClearedNothingOnBoard,
            incidentAt: fault.EnteredAt ?? now,
            now,
            operatorId,
            runtimeOptions.Value,
            cancellationToken).ConfigureAwait(false);
        bool stopped = rebuild.State == OwnOrderRebuildStates.Stopped;
        runtime.SetBlockReason(
            stopped ? JourneyRuntimeEngine.OwnOrderRebuildStoppedReason
            : mayCarry ? CargoOnBoardReason
            : NothingOnBoardReason,
            now);
        runtime.UpdatedAt = now;
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return stopped ? VehicleFaultRecoveryDispositions.RebuildStopped : VehicleFaultRecoveryDispositions.RebuildScheduled;
    }

    /// <summary>The two stages in which the journey waits on a move order in flight -- the only ones a FAILED order stops.</summary>
    private static bool WaitsOnAnOrder(JourneyRuntimeRow journey) =>
        journey.Stage is JourneyRuntimeStage.AwaitingPickupArrival or JourneyRuntimeStage.AwaitingGateArrival;

    /// <summary>What the person has to supply: who they are, and that the cause has been removed on site.</summary>
    private static IEnumerable<string> PersonReasons(VehicleFaultRecoveryRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.OperatorId))
        {
            yield return "FAULT_RECOVERY_OPERATOR_UNIDENTIFIED";
        }

        if (!request.FaultRemedied)
        {
            yield return "FAULT_RECOVERY_REMEDY_NOT_CONFIRMED";
        }
    }

    /// <summary>No latch, and no stop of this server's still open. Clearing never releases a latch (REQ-0356 does).</summary>
    private static IEnumerable<string> EmergencyReasons(RiotVehicleEmergencyObservation emergency, bool stopOpen)
    {
        if (!emergency.IsKnown)
        {
            yield return "FAULT_RECOVERY_EMERGENCY_STATE_UNKNOWN";
        }
        else if (emergency.IsLatched)
        {
            yield return "FAULT_RECOVERY_EMERGENCY_LATCHED";
        }
        else if (stopOpen)
        {
            yield return "FAULT_RECOVERY_EMERGENCY_STOP_OPEN";
        }
    }

    /// <summary>RIoT holds no unfinished order for the vehicle -- REQ-0356's reading of the same question.</summary>
    private static IEnumerable<string> VehicleOrderReasons(RiotVehicleOrderObservation orders)
    {
        if (!orders.IsKnown)
        {
            yield return "FAULT_RECOVERY_VEHICLE_ORDERS_UNKNOWN";
        }
        else if (orders.HasUnfinishedOrder == true)
        {
            yield return "FAULT_RECOVERY_VEHICLE_ORDER_NOT_FINISHED";
        }
    }

    /// <summary>
    /// The order the journey waits on has FAILED, read as terminal. CANCELLED and DELETED have ended too, but an order of
    /// this server's that was cancelled in RIoT is rebuilt for the same demand, not released (see the class remarks), so
    /// they get a code of their own. SUCCESS is an arrival, not an ending, and everything else -- running, held, hanging,
    /// SUSPENDED -- has not ended. An order that could not be read, or that RIoT does not know, has not been shown to have
    /// ended either.
    /// </summary>
    private static IEnumerable<string> CurrentOrderReasons(Reading reading)
    {
        if (reading.Journey is not { } journey || !WaitsOnAnOrder(journey))
        {
            yield break;
        }

        switch (reading.Order)
        {
            case { Kind: RiotOrderObservationKind.Terminal, OrderState: RiotOrderState.Failed }:
                yield break;
            case { Kind: RiotOrderObservationKind.Terminal, OrderState: RiotOrderState.Cancelled or RiotOrderState.Deleted }:
                yield return "FAULT_RECOVERY_CURRENT_ORDER_CANCELLED_IN_RIOT";
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

    /// <summary>The fault was ended here, by a person: cleared, or resumed on a confirmed continue.</summary>
    private static bool EndedByAPerson(VehicleFaultFact? fault) =>
        fault is { Level: VehicleFaultLevel.None, ClearedReason: string reason } &&
        (reason.StartsWith(ClearedByOperatorReason + ":", StringComparison.Ordinal) ||
         reason.StartsWith(ResumedByOperatorReason + ":", StringComparison.Ordinal));

    private static VehicleFaultRecoveryDecision AlreadyCleared(VehicleFaultFact fault) =>
        new(VehicleFaultRecoveryOutcome.AlreadyCleared, [], VehicleFaultRecoveryDispositions.None, fault.FaultGeneration);

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

    /// <summary>What was read before the gate: this server's tables, then RIoT.</summary>
    private sealed record Reading(
        VehicleFaultFact? Fault,
        JourneyRuntimeRow? Journey,
        OrderIntentRow? Intent,
        RiotVehicleEmergencyObservation Emergency,
        RiotVehicleOrderObservation Orders,
        RiotOrderObservation? Order);

    /// <summary>This server's tables as read again under the gate.</summary>
    private sealed record Standing(
        VehicleFaultFact? Fault,
        JourneyRuntimeRow? Journey,
        OrderIntentRow? Intent,
        bool StopOpen)
    {
        /// <summary>
        /// Nothing the RIoT readings were taken against has moved: the same fault generation, still in effect or not, the
        /// same journey in the same stage, waiting on the same order in the same state. Anything else means a runtime
        /// round landed between the two reads, and the readings may describe an order the journey no longer waits on.
        /// </summary>
        public bool Agrees(Reading reading) =>
            Fault?.FaultGeneration == reading.Fault?.FaultGeneration &&
            InEffect(Fault) == InEffect(reading.Fault) &&
            Journey?.JourneyId == reading.Journey?.JourneyId &&
            Journey?.Stage == reading.Journey?.Stage &&
            Intent?.UpperId == reading.Intent?.UpperId &&
            Intent?.OrderId == reading.Intent?.OrderId &&
            Intent?.Status == reading.Intent?.Status;
    }
}
