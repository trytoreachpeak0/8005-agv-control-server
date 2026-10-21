using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime.Commands;
using ControlServer.Host.Runtime.Faults;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Host.Runtime;

/// <summary>
/// control-server#335 (REQ-0246): a vehicle driving on this server's order whose doors are not proven locked is handed to the
/// fault model, which holds this order, reads HELD back and escalates to an emergency stop when it cannot prove the stop.
/// </summary>
/// <remarks>
/// <para>
/// <b>Through the fault model, never a second road to the emergency stop.</b> The symptom is recorded like the FAILED order
/// beside it (<see cref="ObserveOrderFailureAsync"/>) and <see cref="VehicleFaultCoordinator"/> does the rest -- the hold, the
/// read-back, the stop proof, the escalation, REQ-0248's re-trigger. <c>EmergencyStopSupervisor.RequestStopAsync</c> keeps its
/// one caller; <c>InTransitDoorLockFaultTests.OnlyTheFaultCoordinatorRequestsAnEmergencyStop</c> fails the day it gets two. Never
/// a cancel: the coordinator has no path to one, and cancelling would hand the vehicle back to RIoT to dispatch.
/// </para>
/// <para>
/// <b>Why the hold is nearly always followed by the stop in the same round</b>, and why that is REQ-0246 and not a shortcut:
/// the requirement's own heading is "无法证明停车时立即升级，不等待固定超时", and between stations RIoT reports no station, which
/// <see cref="VehicleFaultCoordinator.RequiresEscalation"/> reads as a watch that cannot exclude motion. A vehicle held at a
/// station with a stop proof is not stopped.
/// </para>
/// <para>
/// <b>Every round while the door fault is in effect, not only while the doors read wrong.</b> The coordinator's evaluation is the
/// only thing that advances the stop proof, the trigger's confirmation and the re-trigger of a latch that comes off while the
/// cause stands (<c>DemandReleaseRules.FaultSupervisionInEffect</c>'s premise). Stopping it when the doors read locked again
/// would leave a latched vehicle unwatched. Nothing here clears the fault: that is a person's, through the existing entry.
/// </para>
/// <para>
/// <b>The way out is a person's</b> (CP-0011, requirements baseline v1.10.0, control-server#539). From control-server#335 to
/// #539 the doors reading fresh, known and locked again released the latch on their own. The user ruled on 2026-10-13 that
/// a lock feedback that came back proves nothing about the lock -- "锁可能坏了，识别不到车门的实际状态，车子移动时车门打开
/// 可能把车门撞坏" -- so the vehicle stays out of service with its fault: a person confirms the lock was repaired and
/// releases the stop through the class-three entry (empty: <c>EmergencyStopSupervisor.ReleaseOnConfirmationAsync</c>; loaded:
/// <c>ReleaseLoadedOnGovernedConfirmationAsync</c>, which lets this server's held order stay), then resumes or clears the
/// fault through the existing entry (<c>VehicleFaultRecoveryAction.ResumeHeldOrder</c>).
/// </para>
/// <para>
/// <b>In transit means the arrival stages with an order not seen to have ended</b>: that is where
/// <see cref="NameStalledOrderAsync"/> is called from, on both sides of the readiness gate. A terminal order is an arrival or an
/// ending and other paths own it. An order RIoT has in HANG (9) is left alone, by the user's decision of 2026-09-22 on #299
/// (option H-a): handed to the coordinator it escalates between stations and then nothing on this server can release it. An order
/// that cannot be read counts as in flight: a read that did not answer is no evidence the vehicle stopped. Doors open at a station
/// for an authorised load or unload never reach here (REQ-0246's source decision, answer 3: <c>StationOperationGuard</c>, no alarm,
/// no stop).
/// </para>
/// </remarks>
public sealed partial class JourneyRuntimeEngine
{
    /// <summary>
    /// A latch released on a person's confirmation while this server's held order stayed (the loaded release of CP-0011), and
    /// RIoT now runs that order -- with nobody having pressed continue. riot-behavior-lab round-44 (agv03, 2026-09-28) and agv02
    /// on 2026-10-11 both saw a HELD order stay 7 after a release; this names the day RIoT behaves otherwise, for a person to
    /// look. A vehicle that actually moves is stopped again by the fault model on the motion it reads. An order run by this
    /// server's own continue, not refused, is neither (control-server#835, <see cref="VehicleFaultDecision.ResumedByOwnContinue"/>).
    /// </summary>
    public const string HeldOrderResumedWithoutContinueReason = "HELD_ORDER_RESUMED_WITHOUT_CONTINUE";

    private static readonly Action<ILogger, string, string, Exception?> LogHeldOrderResumedWithoutContinue =
        LoggerMessage.Define<string, string>(
            LogLevel.Critical,
            new EventId(2195, nameof(LogHeldOrderResumedWithoutContinue)),
            "Order {UpperId} on {AgvId} was held for its doors and its latch was released on a person's confirmation; RIoT " +
            "now reports it executing although nobody continued it.");

    private static readonly Action<ILogger, string, string, string, long?, long?, string, Exception?> LogDoorsNotProvenLocked =
        LoggerMessage.Define<string, string, string, long?, long?, string>(
            LogLevel.Warning,
            new EventId(2194, nameof(LogDoorsNotProvenLocked)),
            "Vehicle {AgvId} is driving on order {UpperId} with its doors {DoorState} (generation {Generation}, safety " +
            "version {SafetyStateVersion}); handed to the fault model ({HandedBecause}).");

    private static readonly Action<ILogger, string, string, long?, long?, double, Exception?> LogDoorUnknownGraceStarted =
        LoggerMessage.Define<string, string, long?, long?, double>(
            LogLevel.Warning,
            new EventId(2321, nameof(LogDoorUnknownGraceStarted)),
            "Vehicle {AgvId} is driving on order {UpperId} with a slot it cannot read (SLOT_STATE_UNKNOWN, generation " +
            "{Generation}, safety version {SafetyStateVersion}); graced for {GraceSeconds} s before its doors count as not " +
            "proven locked (CP-0017). Nothing is commanded.");

    private static readonly Action<ILogger, string, double, Exception?> LogDoorUnknownGraceEnded =
        LoggerMessage.Define<string, double>(
            LogLevel.Warning,
            new EventId(2322, nameof(LogDoorUnknownGraceEnded)),
            "Vehicle {AgvId}: the door reading came back locked after {UnreadSeconds} s unread; the grace ended without a fault.");

    /// <summary>
    /// Where the release's last check reads from (cs#839 third re-review): when this leg's order was created -- it is created
    /// on a departure the doors were proven locked for -- or, for an order created after the fault, the run look-back before
    /// the fault. Either way no later than the fault.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Not bounded by the episode's first reading (cs#839 fourth re-review, M1): a leg's first round reads back to the previous
    /// leg's last round, so an episode opened there begins at the station, and an authorised door open at the load would keep
    /// every unread stop of the leg with a person.
    /// </para>
    /// <para>
    /// The order created after the fault is defence, and not expected to be reached while a door fault stands: the rebuilds
    /// that follow a fault start once a person has cleared it (<c>OwnOrderRebuildSources.FaultCleared*</c>), and a cleared
    /// fault releases nothing here. That is read from the code, not proven, so the branch stays, and no test pins it (fourth
    /// re-review, L2).
    /// </para>
    /// </remarks>
    internal static DateTimeOffset InTransitDoorWindowStart(DateTimeOffset orderCreatedAt, DateTimeOffset? faultEnteredAt)
    {
        return faultEnteredAt is DateTimeOffset entered && orderCreatedAt > entered
            ? entered - InTransitDoorFacts.UnknownRunLookBack
            : orderCreatedAt;
    }

    private async Task<bool> ObserveDoorsInTransitAsync(
        JourneyRuntimeRow runtime,
        OrderIntentRow intent,
        RiotOrderObservation order,
        CancellationToken cancellationToken)
    {
        if (intent.OrderId is not string orderId ||
            order.Kind == RiotOrderObservationKind.Terminal ||
            (order.Kind == RiotOrderObservationKind.Active && order.OrderState == RiotOrderState.Hang))
        {
            return false;
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        // Every summary received since the vehicle's last round as well as the one in force (cs#839 review H1), but not further
        // back than the run look-back: a vehicle back on the road after an hour at a station has nothing older to tell.
        // What the cut leaves unread is the release's last check's to find (FaultedVehicleContext.NoDoorReadNotLockedInTransit).
        DateTimeOffset? since = _doorReadings.SinceFor(runtime.AgvId);
        if (since < now - InTransitDoorFacts.UnknownRunLookBack)
        {
            since = now - InTransitDoorFacts.UnknownRunLookBack;
        }

        InTransitDoorVerdict doors = await InTransitDoorFacts.ReadAsync(dbContext, runtime.AgvId, now, since, cancellationToken)
            .ConfigureAwait(false);
        if (_doorReadings.NeedsGraceHistory(runtime.AgvId))
        {
            // A process that has just started counts the graces the inbox shows were given in the window (cs#839 review M2),
            // all but the run the verdict is in, which this round's grace counts itself.
            IReadOnlyList<DateTimeOffset> starts = await InTransitDoorFacts.ReadUnknownRunStartsAsync(
                    dbContext, runtime.AgvId, now - _doorReadings.GraceWindow, cancellationToken)
                .ConfigureAwait(false);
            _doorReadings.SeedGraceStarts(
                runtime.AgvId, starts.Where(start => doors.UnknownSince is not DateTimeOffset current || start < current));
        }

        bool doorFaultInEffect = await dbContext.VehicleFaultStates.AsNoTracking()
            .AnyAsync(
                row => row.AgvId == runtime.AgvId &&
                       row.Level != VehicleFaultLevel.None &&
                       row.EvidenceCode == VehicleFaultEvidence.DoorNotProvenLocked,
                cancellationToken)
            .ConfigureAwait(false);
        // Only what a live onboard reports raises the symptom; a vehicle not heard from stays with control-server#234's code
        // (see InTransitDoorVerdict.ReadUnknown). A slot that cannot be read is graced first (CP-0017): no fault, no command,
        // an alarm. A fault already raised is still driven every round, silent or not.
        InTransitDoorReading reading = _doorReadings.Observe(runtime.AgvId, doors, doorFaultInEffect, now);
        if (reading.GraceEndedAfter is TimeSpan unread)
        {
            LogDoorUnknownGraceEnded(logger, runtime.AgvId, unread.TotalSeconds, null);
        }

        if (reading.GraceStarted)
        {
            LogDoorUnknownGraceStarted(
                logger, runtime.AgvId, intent.UpperId, doors.Generation, doors.SafetyStateVersion,
                _doorReadings.Grace.TotalSeconds, null);
        }

        if (reading.Action != InTransitDoorAction.HandToFault)
        {
            return false;
        }

        if (reading.HandedBecause is string handedBecause)
        {
            LogDoorsNotProvenLocked(
                logger, runtime.AgvId, intent.UpperId, doors.State.ToString(), doors.Generation, doors.SafetyStateVersion,
                handedBecause, null);
        }

        // Doors not proven locked again take away the exemption a person's release gave (REQ-0246; see
        // FaultedVehicleContext.DoorsNotProvenLocked). Reading locked again releases nothing (CP-0011).
        FaultedVehicleContext inFlight = await InFlightFaultContextAsync(runtime, intent, orderId, cancellationToken)
            .ConfigureAwait(false);
        // The doors reading locked again release nothing on a fault that read a door not locked (CP-0011). On one raised only
        // for doors that could not be read, ten unbroken seconds of them are REQ-0167's "原原因消除" (CP-0017): the coordinator
        // releases on it past this server's own confirmed hold, and the order stays held for a person's continue.
        // The rounds' reading is only the early answer; the last word before the release is the whole inbox since the leg began
        // (cs#839 third re-review, the user's ruling of 2026-10-21).
        FaultedVehicleContext context = inFlight with
        {
            DoorsNotProvenLocked = reading.DoorsNotProvenLocked,
            DoorReadingRestored = reading.ReadingRestored,
            NoDoorReadNotLockedInTransit = reading.ReadingRestored
                ? (faultEnteredAt, token) => InTransitDoorFacts.NoDoorReadNotLockedSinceAsync(
                    dbContext, runtime.AgvId, InTransitDoorWindowStart(intent.CreatedAt, faultEnteredAt), token)
                : null,
        };
        VehicleFaultDecision decision = await faults.ObserveAsync(
            new EmergencyStopSubject(runtime.AgvId, runtime.VehicleKey),
            VehicleFaultEvidence.DoorNotProvenLocked,
            context,
            cancellationToken).ConfigureAwait(false);

        // The held order running again after a release on confirmation is named over the door code: the doors are the reason
        // the vehicle stopped, but what a person has to know now is that it may be moving without anyone having let it. Not when
        // it runs on this server's own continue (control-server#835): a person did let it, through the fault entry.
        string code = VehicleFaultEvidence.DoorNotProvenLocked;
        if ((decision.ReleasedOnConfirmation || decision.ReleasedOnDoorReading) &&
            !decision.ResumedByOwnContinue &&
            order.Kind == RiotOrderObservationKind.Active &&
            order.OrderState == RiotOrderState.Executing)
        {
            code = HeldOrderResumedWithoutContinueReason;
            if (!string.Equals(runtime.BlockReasonCode, code, StringComparison.Ordinal))
            {
                LogHeldOrderResumedWithoutContinue(logger, intent.UpperId, runtime.AgvId, null);
            }
        }

        checkpointWaits.Clear(runtime.VehicleKey);
        if (!string.Equals(runtime.BlockReasonCode, code, StringComparison.Ordinal))
        {
            now = timeProvider.GetUtcNow();
            runtime.SetBlockReason(code, now);
            runtime.UpdatedAt = now;
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        return true;
    }

    /// <summary>
    /// A door fault still stands, and the order it held has since been cancelled or deleted in RIoT: keep handing it to the fault
    /// model every round (control-server#335 review P1).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Called at the top of both arrival stages, before the rebuild of control-server#318 takes the round over: once the engine
    /// has recorded that ending, <see cref="AdvanceOwnOrderRebuildAsync"/> returns early every round and
    /// <see cref="ObserveDoorsInTransitAsync"/> is never reached again. Without this the fault model, which alone evaluates the
    /// latch, went unasked for good -- a latched vehicle was never released, and the rebuild waited on the fault forever.
    /// </para>
    /// <para>
    /// It supervises only: no journey code is written, the rebuild keeps its own. A person releases the stop through the
    /// class-three entry and clears the fault (CP-0011: the doors reading locked again release nothing); the rebuild the engine
    /// recorded goes on from there.
    /// </para>
    /// <para>
    /// <b>Doors not proven locked do not take the released vehicle's exemption away here</b>, unlike on a live order
    /// (<see cref="FaultedVehicleContext.DoorsNotProvenLocked"/> is left false). With the order ended in RIoT nothing can drive the
    /// vehicle, so a door that will not read locked is no reason to stop it again; motion read, unreadable or stale still stops
    /// it. This is the governed way out for a lock sensor that is really broken (control-server#539 review S-2, probe P2): end
    /// the held order in RIoT, release through the class-three entry, clear the fault. Were the doors to take the exemption away
    /// here too, a vehicle whose sensor never reads locked would be stopped again the round after every release, with no way out
    /// but the database.
    /// </para>
    /// </remarks>
    private async Task SuperviseDoorFaultOnEndedOrderAsync(
        JourneyRuntimeRow runtime,
        JourneyStopRow stop,
        CancellationToken cancellationToken)
    {
        bool doorFaultInEffect = await dbContext.VehicleFaultStates.AsNoTracking()
            .AnyAsync(
                row => row.AgvId == runtime.AgvId &&
                       row.Level != VehicleFaultLevel.None &&
                       row.EvidenceCode == VehicleFaultEvidence.DoorNotProvenLocked,
                cancellationToken)
            .ConfigureAwait(false);
        if (!doorFaultInEffect)
        {
            return;
        }

        OrderIntentRow? intent = await dbContext.OrderIntents.AsNoTracking()
            .SingleOrDefaultAsync(row => row.MovementLegId == stop.MovementLegId, cancellationToken).ConfigureAwait(false);
        if (intent?.OrderId is not string orderId)
        {
            return;
        }

        RiotOrderObservation order = await vehicleFacts.ReconcileByUpperIdAsync(intent.UpperId, cancellationToken)
            .ConfigureAwait(false);
        if (order is not { Kind: RiotOrderObservationKind.Terminal, OrderState: RiotOrderState.Cancelled or RiotOrderState.Deleted })
        {
            return;
        }

        FaultedVehicleContext context = await InFlightFaultContextAsync(runtime, intent, orderId, cancellationToken)
            .ConfigureAwait(false);
        await faults.ObserveAsync(
            new EmergencyStopSubject(runtime.AgvId, runtime.VehicleKey),
            VehicleFaultEvidence.DoorNotProvenLocked,
            context,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The order and the cargo a fault on an in-flight order is about: the same for every symptom that meets a vehicle on the move,
    /// so the order FAILED and the doors not proven locked cannot come to bind different cargo for one vehicle.
    /// </summary>
    private async Task<FaultedVehicleContext> InFlightFaultContextAsync(
        JourneyRuntimeRow runtime,
        OrderIntentRow intent,
        string orderId,
        CancellationToken cancellationToken)
    {
        // An idle return (control-server#390) or a charging journey (control-server#404) carries no demand and so no cargo: the
        // fault is about its order alone.
        if (runtime.CarriesNoDemand())
        {
            return new FaultedVehicleContext(new RiotOrderCommandTarget(runtime.AgvId, intent.UpperId, orderId), null);
        }

        string transportDemandKey = await dbContext.AcceptedDemands
            .Where(row => row.DemandId == runtime.DemandId)
            .Select(row => row.TransportDemandKey)
            .SingleAsync(cancellationToken).ConfigureAwait(false);
        // 车上有没有货，问的是事实本身（批次7-06，control-server#211）。这之前判的是
        // arrival.Purpose == "TO_GATE"，而 JourneyPlanBuilder.LegIntent 给每一段后续腿都建 "TO_GATE"，
        // 所以那个判断真正表达的是「这不是第一段腿」——在只有取货和关卡两个停靠的旅程里，它与「装过货」
        // 恰好等价；多一个取货停靠就不再等价，而这条链通向故障货物处置，等价关系断了不会有东西变红。
        bool carryingCargo = await dbContext.Set<JourneyDemandRow>()
            .AnyAsync(
                row => row.JourneyId == runtime.JourneyId && row.Status == JourneyDemandStatuses.Loaded,
                cancellationToken).ConfigureAwait(false);
        // 腿取这一次出事的那一段，不取旅程行上锚需求的关卡腿。
        //
        // 需求与 transportDemandKey 仍然取锚需求，而多停靠下车上可能同时载着几条需求的货：这里说不出
        // 「是哪一条的货出了事」。本票不改它——判断哪条需求的货需要按停靠归属去认，那是移除停靠与故障货物
        // 归属一起要解决的事（批次7-10）。依赖的前提写在这里：**只要一趟旅程可能载多于一条需求的货，
        // 这两个字段就只是「这趟旅程的锚」，不是「出事的那一批货」**。
        FaultedVehicleCargoFacts? cargo = carryingCargo
            ? new FaultedVehicleCargoFacts(
                runtime.TransportColumn(runtime.DemandId),
                intent.MovementLegId,
                transportDemandKey,
                LoadingWitnessed: true,
                CargoStateKnown: true)
            : null;
        return new FaultedVehicleContext(new RiotOrderCommandTarget(runtime.AgvId, intent.UpperId, orderId), cargo);
    }
}
