using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Host.Runtime.Faults;

/// <summary>
/// The person's way out of an automatic rebuild that stopped (control-server#345), on the same entry point, with the same
/// operator identity and the same event 9203 as a fault clearance.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it has to exist.</b> REQ-0361 says a demand that has a problem again within the window is not rebuilt
/// automatically but held, alarmed and "handled by a person" -- and until this there was nothing for the person to handle it
/// with. The journey stayed under <c>OWN_ORDER_REBUILD_STOPPED</c>, its demand not redispatched and its vehicle taking no new
/// work, until an engineer edited the database.
/// </para>
/// <para>
/// <b>Only a stopped journey.</b> The action is judged on the record the journey's current stop waits on
/// (<see cref="OwnOrderRebuilds.ForStopAsync"/>): it has to be <see cref="OwnOrderRebuildStates.Stopped"/>, and stopped by the
/// third guard. A journey whose rebuild is merely waiting is refused -- the engine will make it -- and so is one that is not
/// waiting on a rebuild at all. The one other state accepted is a trip already handed to the exception recovery session
/// whose handoff did not end it, by failing or by handing off only some of its demands: giving it up and handing it over
/// again apply to it, so that it never falls back to needing an engineer (independent review M1).
/// </para>
/// <para>
/// <b>Three ways out, chosen by a person.</b> A rebuild once more, due at once, which the engine's next round takes through
/// every check #318 put before a create (<see cref="OwnOrderRebuilds.StageManualRebuildAsync"/>); giving a trip with nothing
/// on board up (<see cref="TerminateStoppedAsync"/>); or handing a loaded one to the vehicle's exception recovery session
/// (<see cref="PrepareCargoHandoffAsync"/>). None of them releases anything for redispatch, and none sends RIoT a command:
/// giving up reads RIoT once, before the gate, and the others do not call it at all. Every write is this server's own, made
/// under <see cref="JourneyMutationGate"/> like a clearance.
/// </para>
/// </remarks>
public sealed partial class VehicleFaultRecoveryService
{
    /// <summary>The journey is not waiting on a rebuild the third guard stopped; there is nothing to rebuild on request.</summary>
    public const string ExitNotStoppedReason = "OWN_ORDER_REBUILD_EXIT_NOT_STOPPED";

    /// <summary>
    /// The rebuild stopped because a snapshot showed the cargo not in its slots: REQ-0238 leaves only taking it out, handing it
    /// over and ending the demand in an exception recovery session, never carrying on (REQ-0362).
    /// </summary>
    public const string ExitCargoNotInPlaceReason = "OWN_ORDER_REBUILD_EXIT_CARGO_NOT_IN_PLACE";

    /// <summary>
    /// The rebuild stopped because the vehicle is no longer admitted for a demand still to be loaded: the release service
    /// releases that demand for redispatch (REQ-0328), and a rebuild would only be cancelled by it again.
    /// </summary>
    public const string ExitVehicleIneligibleReason = "OWN_ORDER_REBUILD_EXIT_VEHICLE_INELIGIBLE";

    /// <summary>
    /// The code a journey closes under when a person gives its stopped trip up (control-server#345). Not one of the local
    /// cancellations <c>PickupStopTermination.KeySuppressingReasonCodes</c> suppresses by business key: the demand is ended
    /// under its <c>DemandId</c> alone, as a fault cargo handoff's is.
    /// </summary>
    public const string TripTerminatedReason = "TERMINATED_BY_OPERATOR_AFTER_REBUILD_STOP";

    /// <summary>
    /// Giving the trip up was asked while something is, or may be, on board: a demand past "still to load" and not ended, or a
    /// live cargo binding. REQ-0238 leaves a loaded trip two ways only -- carrying on after a repair, or taking the cargo out,
    /// handing it over and ending the demand in an exception recovery session -- never simply ending it.
    /// </summary>
    public const string ExitCargoOnBoardReason = "OWN_ORDER_REBUILD_EXIT_CARGO_ON_BOARD";

    /// <summary>
    /// Handing the trip to the exception recovery session was asked with nothing on board: there is no cargo to take out. A
    /// person rebuilds it or gives it up instead.
    /// </summary>
    public const string ExitNothingOnBoardReason = "OWN_ORDER_REBUILD_EXIT_NOTHING_ON_BOARD";

    /// <summary>The block a journey carries while it waits for its exception recovery session (control-server#345).</summary>
    public const string AwaitingCargoHandoffReason = WireToGateStore.AwaitingCargoHandoffJourneyReason;

    /// <summary>
    /// The trip was handed to the exception recovery session and waits there: a person rebuild is not one of its ways on. Giving
    /// it up (nothing left on board) or handing it over again (cargo still on board) are (independent review M1).
    /// </summary>
    public const string ExitAwaitingHandoffReason = "OWN_ORDER_REBUILD_EXIT_AWAITING_CARGO_HANDOFF";

    private async Task<VehicleFaultRecoveryDecision> RebuildStoppedAsync(
        VehicleFaultRecoveryRequest request,
        CancellationToken cancellationToken)
    {
        List<string> reasons = [.. PersonReasons(request)];
        using IDisposable? round = await gate.TryEnterAsync(gateWait, cancellationToken).ConfigureAwait(false);
        if (round is null)
        {
            return Refused(["FAULT_RECOVERY_RUNTIME_BUSY"], null);
        }

        StoppedTrip trip = await ReadStoppedTripAsync(request.Subject.AgvId, cancellationToken).ConfigureAwait(false);
        if (trip.Refusal == ExitNotStoppedReason && reasons.Count == 0 && trip.Runtime is not null &&
            await LastRebuildWasAPersonsAsync(trip.Runtime, cancellationToken).ConfigureAwait(false))
        {
            // The same request again, after the first was carried out -- an HTTP retry, or a second person: answered, not
            // done twice. Judged only for a request that names its person and confirms, as #299's "already cleared" is.
            return AlreadyDone();
        }

        if (trip.Refusal is not null)
        {
            reasons.Add(trip.Refusal);
        }

        if (reasons.Count > 0)
        {
            return Refused(reasons, null);
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        JourneyRuntimeRow runtime = trip.Runtime!;
        OwnOrderRebuildRow manual = await OwnOrderRebuilds.StageManualRebuildAsync(
                dbContext, runtime, trip.Stop!, trip.Stopped!, request.OperatorId!, now, cancellationToken)
            .ConfigureAwait(false);
        runtime.SetBlockReason(TransitionalCode(manual.Source), now);
        runtime.UpdatedAt = now;
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new VehicleFaultRecoveryDecision(
            VehicleFaultRecoveryOutcome.RebuildRequested, [], VehicleFaultRecoveryDispositions.RebuildScheduled, null);
    }

    /// <summary>
    /// Gives a stopped trip up (control-server#345, the user's decision of 2026-09-23, issuecomment-5798100435): every demand
    /// still to load on it ends and the journey closes, through the same tail every other ending of a pickup stop uses
    /// (<see cref="PickupStopTermination"/>), which releases the lease, the order occupancy and the purpose claim together.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Not a release, not a redispatch.</b> The user reads it as REQ-0361's "handled by a person": the demand is ended, not
    /// handed to another vehicle, and it is never dispatched again under its <c>DemandId</c> -- MES still lists it, and moving
    /// the goods and closing it in MES are left to people.
    /// </para>
    /// <para>
    /// <b>Only with nothing on board</b> (<see cref="ExitCargoOnBoardReason"/>), and only when RIoT says the vehicle holds no
    /// unfinished order: a journey that closes while its vehicle still runs an order hands out a vehicle that is not free. RIoT
    /// is read before the gate is taken, as every RIoT read of this service is (independent review M2 of control-server#299);
    /// the journey is judged again under the gate, and a trip stopped by the third guard creates no order meanwhile.
    /// </para>
    /// <para>
    /// The closure snapshot goes to the vehicle once it is committed (control-server#323); a vehicle that is not connected gets
    /// it on its next handshake.
    /// </para>
    /// </remarks>
    private async Task<VehicleFaultRecoveryDecision> TerminateStoppedAsync(
        VehicleFaultRecoveryRequest request,
        CancellationToken cancellationToken)
    {
        string agvId = request.Subject.AgvId;
        List<string> reasons = [.. PersonReasons(request)];
        RiotVehicleOrderObservation orders = await orderFacts
            .ReadUnfinishedOrdersAsync(request.Subject.DeviceKey, cancellationToken).ConfigureAwait(false);
        using (IDisposable? round = await gate.TryEnterAsync(gateWait, cancellationToken).ConfigureAwait(false))
        {
            if (round is null)
            {
                return Refused(["FAULT_RECOVERY_RUNTIME_BUSY"], null);
            }

            StoppedTrip trip = await ReadStoppedTripAsync(agvId, cancellationToken).ConfigureAwait(false);
            if (trip.Refusal == ExitNotStoppedReason && reasons.Count == 0 && trip.Runtime is null &&
                await LastJourneyWasGivenUpAsync(agvId, cancellationToken).ConfigureAwait(false))
            {
                return AlreadyDone();
            }

            List<JourneyDemandRow> memberships = trip.Runtime is null
                ? []
                : await dbContext.Set<JourneyDemandRow>()
                    .Where(row => row.JourneyId == trip.Runtime.JourneyId && row.RemovedAt == null)
                    .ToListAsync(cancellationToken).ConfigureAwait(false);
            // A trip handed to the exception recovery session may be given up too, once nothing is left on board: after a
            // partial handoff, what remains is demands still to load, which no session can end (independent review M1 (b)).
            string? refusal = trip.Refusal == ExitAwaitingHandoffReason ? null : trip.Refusal;
            if (refusal is not null)
            {
                reasons.Add(refusal);
            }
            else if (await MayCarryAsync(agvId, trip.Runtime!, cancellationToken).ConfigureAwait(false))
            {
                reasons.Add(ExitCargoOnBoardReason);
            }

            reasons.AddRange(VehicleOrderReasons(orders));
            if (reasons.Count > 0)
            {
                return Refused(reasons, null);
            }

            DateTimeOffset now = timeProvider.GetUtcNow();
            JourneyRuntimeRow runtime = trip.Runtime!;
            PickupStopTermination termination = new(dbContext);
            foreach (JourneyDemandRow membership in memberships.Where(row => row.Status == JourneyDemandStatuses.PendingLoad))
            {
                await termination.StageDemandTerminationAsync(membership.DemandId, cancellationToken).ConfigureAwait(false);
            }

            trip.Stopped!.State = OwnOrderRebuildStates.Ended;
            JourneyStopCursor stops = await JourneyStopCursor.LoadAsync(dbContext, runtime, cancellationToken).ConfigureAwait(false);
            await termination.StageJourneyClosureAsync(
                    runtime, stops.CurrentSublotRequestMessageIdOrNone(runtime.WorklistRevision), TripTerminatedReason, now,
                    cancellationToken)
                .ConfigureAwait(false);
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        await JourneyClosure.SendAsync(publisher, dbContext, agvId, cancellationToken).ConfigureAwait(false);
        return new VehicleFaultRecoveryDecision(
            VehicleFaultRecoveryOutcome.TripTerminated, [], VehicleFaultRecoveryDispositions.TripTerminated, null);
    }

    /// <summary>
    /// Hands a stopped trip with cargo, or possibly cargo, on board to the vehicle's exception recovery session
    /// (control-server#345): REQ-0238's other way for a loaded trip -- the cargo taken out, handed over and its demand ended --
    /// and, when a snapshot has shown the cargo not in its slots, the only one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What was missing.</b> The session has existed all along; a stopped trip could not reach it. The server opens a session
    /// on a demand only while its journey is <see cref="JourneyRuntimeStage.Blocked"/>, and a stopped rebuild keeps its journey
    /// in its arrival stage; and the onboard offers the handoff only while the session reads RECOVERY_REQUIRED, which nothing
    /// said for a vehicle standing still with its fault cleared. So the journey is blocked under
    /// <see cref="AwaitingCargoHandoffReason"/>, which <c>WireToGateStore.DecideReadinessAsync</c> holds the session for, and
    /// the record turns <see cref="OwnOrderRebuildStates.AwaitingCargoHandoff"/>, for which the Host asks the vehicle for a
    /// snapshot once (<see cref="OwnOrderRebuilds.ClaimCargoEvidenceRequestAsync"/>), so that readiness is judged again and
    /// announced without waiting for the vehicle to report on its own.
    /// </para>
    /// <para>
    /// <b>A person's step, not the engine's</b> (the coordinator's decision of 2026-09-23): REQ-0238 does not let a path be
    /// chosen automatically, a stopped trip with cargo on board has two -- a person's rebuild or this -- and every hand-over
    /// then carries a named person and event 9203. The engine's stop is left as it was. From here the session's own rules
    /// govern: its administrator proof, its actions, and the settlement of their results, which ends the demand.
    /// </para>
    /// <para>
    /// <b>Again, after a handoff that did not end the trip</b> (independent review M1 (a)): a session whose result does not
    /// reconcile has the recovery coordinator block the journey under its own code, with the cargo still on board. Asked again
    /// then, the trip is put back under <see cref="AwaitingCargoHandoffReason"/> and the vehicle asked for a snapshot once
    /// more; readiness never let go meanwhile, because it holds on the record, not on the code.
    /// </para>
    /// </remarks>
    private async Task<VehicleFaultRecoveryDecision> PrepareCargoHandoffAsync(
        VehicleFaultRecoveryRequest request,
        CancellationToken cancellationToken)
    {
        string agvId = request.Subject.AgvId;
        List<string> reasons = [.. PersonReasons(request)];
        using IDisposable? round = await gate.TryEnterAsync(gateWait, cancellationToken).ConfigureAwait(false);
        if (round is null)
        {
            return Refused(["FAULT_RECOVERY_RUNTIME_BUSY"], null);
        }

        StoppedTrip trip = await ReadStoppedTripAsync(agvId, cancellationToken).ConfigureAwait(false);
        bool handedOver = trip.Refusal == ExitAwaitingHandoffReason;
        string? refusal = trip.Refusal is ExitCargoNotInPlaceReason or ExitAwaitingHandoffReason ? null : trip.Refusal;
        if (refusal is null && !await MayCarryAsync(agvId, trip.Runtime!, cancellationToken).ConfigureAwait(false))
        {
            // Nothing on board: nothing to take out. After a partial handoff that leaves only demands still to load, the
            // person gives the rest up instead (independent review M1 (b)).
            refusal = ExitNothingOnBoardReason;
        }

        if (refusal is null && reasons.Count == 0 && handedOver && trip.Runtime!.BlockReasonCode == AwaitingCargoHandoffReason)
        {
            // The same request again while the trip still waits for its session: answered, not done twice.
            return AlreadyDone();
        }

        if (refusal is not null)
        {
            reasons.Add(refusal);
        }

        if (reasons.Count > 0)
        {
            return Refused(reasons, null);
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        JourneyRuntimeRow runtime = trip.Runtime!;
        runtime.Stage = JourneyRuntimeStage.Blocked;
        runtime.SetBlockReason(AwaitingCargoHandoffReason, now);
        runtime.UpdatedAt = now;
        trip.Stopped!.State = OwnOrderRebuildStates.AwaitingCargoHandoff;
        OwnOrderRebuilds.WithdrawCargoEvidenceRequest(trip.Stopped);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new VehicleFaultRecoveryDecision(
            VehicleFaultRecoveryOutcome.HandoffPrepared, [], VehicleFaultRecoveryDispositions.AwaitingCargoHandoff, null);
    }

    /// <summary>
    /// Whether something is, or may be, on the vehicle: a demand of the journey past "still to load" and not ended, or a live
    /// cargo binding -- the same reading as a clearance's (<see cref="DisposeOfTheJourneyAsync"/>).
    /// </summary>
    private async Task<bool> MayCarryAsync(string agvId, JourneyRuntimeRow runtime, CancellationToken cancellationToken) =>
        await dbContext.Set<JourneyDemandRow>().AsNoTracking()
            .AnyAsync(
                row => row.JourneyId == runtime.JourneyId && row.RemovedAt == null &&
                       row.Status != JourneyDemandStatuses.PendingLoad && row.Status != JourneyDemandStatuses.Unloaded &&
                       row.Status != JourneyDemandStatuses.Terminated,
                cancellationToken)
            .ConfigureAwait(false) ||
        await faults.ReadLiveCargoAsync(agvId, cancellationToken).ConfigureAwait(false) is not null;

    /// <summary>
    /// The vehicle's journey, the stop it waits at and the rebuild record that stop waits on, read afresh; and why a person's
    /// way out does not apply to it, or null when the rebuild was stopped by the third guard -- the one state all three ways
    /// out are for.
    /// </summary>
    /// <remarks>
    /// A <c>Blocked</c> journey waits on no stop's order; its record is the one handed to the exception recovery session, if
    /// any, found by its state (<see cref="ExitAwaitingHandoffReason"/>). Only the handoff's own ways on -- giving up, handing
    /// over again -- accept that one, whatever code the journey carries by then (independent review M1).
    /// </remarks>
    private async Task<StoppedTrip> ReadStoppedTripAsync(string agvId, CancellationToken cancellationToken)
    {
        dbContext.ChangeTracker.Clear();
        JourneyRuntimeRow? runtime = await dbContext.JourneyRuntimes
            .SingleOrDefaultAsync(row => row.AgvId == agvId && row.Stage != JourneyRuntimeStage.Completed, cancellationToken)
            .ConfigureAwait(false);
        JourneyStopRow? stop = null;
        OwnOrderRebuildRow? stopped = null;
        if (runtime is not null && WaitsOnAnOrder(runtime))
        {
            stop = (await JourneyStopCursor.LoadAsync(dbContext, runtime, cancellationToken).ConfigureAwait(false)).Current;
            stopped = await OwnOrderRebuilds.ForStopAsync(dbContext, stop, cancellationToken).ConfigureAwait(false);
        }
        else if (runtime is { Stage: JourneyRuntimeStage.Blocked })
        {
            stopped = await dbContext.OwnOrderRebuilds
                .FirstOrDefaultAsync(
                    row => row.JourneyId == runtime.JourneyId && row.State == OwnOrderRebuildStates.AwaitingCargoHandoff,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        string? refusal = stopped switch
        {
            { State: OwnOrderRebuildStates.AwaitingCargoHandoff } => ExitAwaitingHandoffReason,
            { State: OwnOrderRebuildStates.Stopped, StoppedReason: OwnOrderRebuilds.CargoNotProvenInOriginalSlots } =>
                ExitCargoNotInPlaceReason,
            { State: OwnOrderRebuildStates.Stopped, StoppedReason: OwnOrderRebuilds.VehicleNoLongerEligible } =>
                ExitVehicleIneligibleReason,
            { State: OwnOrderRebuildStates.Stopped } => null,
            _ => ExitNotStoppedReason,
        };
        return new StoppedTrip(runtime, stop, stopped, refusal);
    }

    /// <summary>
    /// Whether the vehicle's latest journey -- by its last update, compared in memory -- was closed by a person giving its
    /// trip up. Asked only when the vehicle has no journey open: a journey taken since makes a later request a new one.
    /// </summary>
    private async Task<bool> LastJourneyWasGivenUpAsync(string agvId, CancellationToken cancellationToken) =>
        (await dbContext.JourneyRuntimes.AsNoTracking()
            .Where(row => row.AgvId == agvId)
            .Select(row => new { row.UpdatedAt, row.BlockReasonCode })
            .ToArrayAsync(cancellationToken).ConfigureAwait(false))
        .MaxBy(row => row.UpdatedAt)?.BlockReasonCode == TripTerminatedReason;

    private static VehicleFaultRecoveryDecision AlreadyDone() =>
        new(VehicleFaultRecoveryOutcome.AlreadyDone, [], VehicleFaultRecoveryDispositions.None, null);

    /// <summary>What <see cref="ReadStoppedTripAsync"/> found, tracked in this context.</summary>
    private sealed record StoppedTrip(
        JourneyRuntimeRow? Runtime,
        JourneyStopRow? Stop,
        OwnOrderRebuildRow? Stopped,
        string? Refusal);

    /// <summary>
    /// Whether the journey's latest rebuild record -- by when it was recorded, compared in memory, as SQLite cannot order a
    /// <see cref="DateTimeOffset"/> -- is one a person asked for. Anything recorded since, an automatic rebuild or a stop,
    /// makes a later request a new one.
    /// </summary>
    private async Task<bool> LastRebuildWasAPersonsAsync(JourneyRuntimeRow runtime, CancellationToken cancellationToken)
    {
        OwnOrderRebuildRow[] records = await dbContext.OwnOrderRebuilds.AsNoTracking()
            .Where(row => row.JourneyId == runtime.JourneyId)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        return records.MaxBy(row => row.RecordedAt) is { } last && OwnOrderRebuilds.IsPersonsRebuild(last, records);
    }

    /// <summary>
    /// The code the journey waits under until the engine's next round names what the rebuild waits on, or confirms it: the
    /// one its source waits under before an automatic rebuild is due.
    /// </summary>
    private static string TransitionalCode(string source) => source switch
    {
        OwnOrderRebuildSources.FaultClearedCargoOnBoard => CargoOnBoardReason,
        OwnOrderRebuildSources.FaultClearedNothingOnBoard => NothingOnBoardReason,
        _ => JourneyRuntimeEngine.OrderEndedWithoutArrivalReason,
    };
}
