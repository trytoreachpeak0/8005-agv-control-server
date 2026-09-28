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
/// <b>The way out</b> (the user's option A of 2026-09-28): once the doors read fresh, known and locked again, the latch this
/// fault raised is released automatically -- only past this server's own confirmed hold of that generation, see
/// <c>VehicleFaultCoordinator.DoorReleaseAllowance</c> -- and the order stays HELD until a person resumes it
/// (<c>VehicleFaultRecoveryAction.ResumeHeldOrder</c>), which clears the fault. Before this, a loaded vehicle stopped here had
/// no way out on this server: the fault stood, and every release refused the held order as unfinished.
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
    private static readonly Action<ILogger, string, string, string, long?, long?, Exception?> LogDoorsNotProvenLocked =
        LoggerMessage.Define<string, string, string, long?, long?>(
            LogLevel.Warning,
            new EventId(2194, nameof(LogDoorsNotProvenLocked)),
            "Vehicle {AgvId} is driving on order {UpperId} with its doors {DoorState} (generation {Generation}, safety " +
            "version {SafetyStateVersion}); handed to the fault model.");

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
        InTransitDoorVerdict doors = await InTransitDoorFacts.ReadAsync(dbContext, runtime.AgvId, now, cancellationToken)
            .ConfigureAwait(false);
        bool doorFaultInEffect = await dbContext.VehicleFaultStates.AsNoTracking()
            .AnyAsync(
                row => row.AgvId == runtime.AgvId &&
                       row.Level != VehicleFaultLevel.None &&
                       row.EvidenceCode == VehicleFaultEvidence.DoorNotProvenLocked,
                cancellationToken)
            .ConfigureAwait(false);
        // Only what a live onboard reports raises the symptom; a vehicle not heard from stays with control-server#234's code
        // (see InTransitDoorVerdict.ReportedNotLocked). A fault already raised is still driven every round, silent or not.
        if (!doors.ReportedNotLocked && !doorFaultInEffect)
        {
            return false;
        }

        if (doors.ReportedNotLocked)
        {
            LogDoorsNotProvenLocked(
                logger, runtime.AgvId, intent.UpperId, doors.State.ToString(), doors.Generation, doors.SafetyStateVersion, null);
        }

        // The doors read fresh, known and locked again is REQ-0167's "原原因消除" for a latch this fault raised; the coordinator
        // releases on it only past this server's own confirmed hold (user's option A, 2026-09-28).
        FaultedVehicleContext inFlight = await InFlightFaultContextAsync(runtime, intent, orderId, cancellationToken)
            .ConfigureAwait(false);
        FaultedVehicleContext context = inFlight with { DoorCauseRemoved = doors.State == InTransitDoorState.ProvenLocked };
        await faults.ObserveAsync(
            new EmergencyStopSubject(runtime.AgvId, runtime.VehicleKey),
            VehicleFaultEvidence.DoorNotProvenLocked,
            context,
            cancellationToken).ConfigureAwait(false);

        checkpointWaits.Clear(runtime.VehicleKey);
        if (!string.Equals(runtime.BlockReasonCode, VehicleFaultEvidence.DoorNotProvenLocked, StringComparison.Ordinal))
        {
            now = timeProvider.GetUtcNow();
            runtime.SetBlockReason(VehicleFaultEvidence.DoorNotProvenLocked, now);
            runtime.UpdatedAt = now;
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        return true;
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
                runtime.DemandId,
                intent.MovementLegId,
                transportDemandKey,
                LoadingWitnessed: true,
                CargoStateKnown: true)
            : null;
        return new FaultedVehicleContext(new RiotOrderCommandTarget(runtime.AgvId, intent.UpperId, orderId), cargo);
    }
}
