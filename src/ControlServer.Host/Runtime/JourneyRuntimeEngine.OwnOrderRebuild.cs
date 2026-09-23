using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime.CreateGate;
using ControlServer.Host.Runtime.Faults;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Host.Runtime;

/// <summary>
/// control-server#318: an order of this server's own that ended without its vehicle arriving is rebuilt for the same vehicle
/// and the same demand, to the same stop, behind three guards -- no release, no redispatch, no person's confirmation.
/// </summary>
/// <remarks>
/// <para>
/// <b>The user's rule</b> (the three comments control-server#318 names, the last one issuecomment-5787511271): an order of
/// this server's cancelled or deleted in RIoT is almost always a mistake, and a fault a person has cleared on site is that
/// person saying the vehicle may move; either way the trip is continued on the same vehicle, "不改派啊，留在本车上". Nobody
/// is asked, because usually nobody is watching the system.
/// </para>
/// <para>
/// <b>What is rebuilt.</b> One new RIoT move order to the stop the ended one was going to, carrying the journey's anchor
/// demand like every leg does (<see cref="JourneyPlanBuilder.LegIntent"/>). The stop keeps its identity, its sequence and its
/// station; only its movement leg and upperId move to the new order, whose intent row is added beside the ended one, which
/// stays as the record of what ended. Every other stop is left as it was. Where the vehicle starts from is RIoT's: a move
/// order names only its destination, and the vehicle drives there from wherever it stands.
/// </para>
/// <para>
/// <b>The three guards</b>, each with its own code on the journey while it holds: the delay
/// (<see cref="JourneyRuntimeOptions.OwnOrderRebuildDelay"/>, the transitional code of the source stays meanwhile); the
/// vehicle's condition (<see cref="OwnOrderRebuildWaitingVehicleReason"/>, read in <see cref="VehicleConditionReasonsAsync"/>);
/// and a second problem of the demand within <see cref="JourneyRuntimeOptions.OwnOrderRebuildRepeatWindow"/> of its first
/// (REQ-0361), decided when the ending is
/// recorded (<see cref="OwnOrderRebuilds"/>), which stops the automatic rebuild for good
/// (<see cref="OwnOrderRebuildStoppedReason"/>). Like every other move order that does not exist yet, the new one also goes
/// through REQ-0305's create gate (<see cref="OwnOrderRebuildBlockedByCreateGateReason"/>).
/// </para>
/// <para>
/// <b>Both sides of the readiness gate.</b> A real onboard reports its session not ready for the whole of a leg that carries
/// this server's order, so the ending is usually read, and the rebuild often made, behind the gate. Creating the order sends
/// nothing to the peer, so it is allowed there -- but only when the session is not ready for the vehicle's own sake alone
/// (<see cref="Dashboard.OwnMovementOrderExplanation.OnlyTheVehicleKeepsItNotReady"/>) and the peer is heard from; a session
/// held back for a slot operation to recover, a forced recovery or an unfinished handshake keeps the rebuild waiting, because
/// the vehicle's doors are exactly what such a session has not vouched for.
/// </para>
/// </remarks>
public sealed partial class JourneyRuntimeEngine
{
    /// <summary>The rebuild is due, but the vehicle is in an emergency stop, in manual, faulted or otherwise not ready.</summary>
    public const string OwnOrderRebuildWaitingVehicleReason = "OWN_ORDER_REBUILD_WAITING_VEHICLE";

    /// <summary>The rebuild is due, but REQ-0305's create gate does not let the new order be created yet.</summary>
    public const string OwnOrderRebuildBlockedByCreateGateReason = "OWN_ORDER_REBUILD_BLOCKED_BY_CREATE_GATE";

    /// <summary>The new order was asked of RIoT and its creation is not confirmed yet; the next round reconciles it.</summary>
    public const string OwnOrderRebuildOrderUnconfirmedReason = "OWN_ORDER_REBUILD_ORDER_UNCONFIRMED";

    /// <summary>
    /// The demand's rebuilt order ended again soon after it was built: no further automatic rebuild, held and alarmed for a
    /// person (control-server#318's third guard).
    /// </summary>
    public const string OwnOrderRebuildStoppedReason = "OWN_ORDER_REBUILD_STOPPED";

    /// <summary>What a fault cleared with a cargo binding leaves on the vehicle once the rebuilt order is confirmed.</summary>
    public const string CargoRebuiltOnOriginalVehicleReason = "REBUILT_ON_ORIGINAL_VEHICLE";

    private static readonly Action<ILogger, string, string, string, string, DateTimeOffset, Exception?> LogOwnOrderRebuildScheduled =
        LoggerMessage.Define<string, string, string, string, DateTimeOffset>(
            LogLevel.Warning,
            new EventId(2170, nameof(LogOwnOrderRebuildScheduled)),
            "Order {EndedUpperId} of journey {JourneyId} on {AgvId} ended ({Source}); it will be rebuilt for the same vehicle " +
            "and demand from {DueAt} on.");

    private static readonly Action<ILogger, string, string, string, string, Exception?> LogOwnOrderRebuilt =
        LoggerMessage.Define<string, string, string, string>(
            LogLevel.Warning,
            new EventId(2171, nameof(LogOwnOrderRebuilt)),
            "Order {EndedUpperId} of journey {JourneyId} on {AgvId} was rebuilt as {NewUpperId}.");

    private static readonly Action<ILogger, string, string, string, string, Exception?> LogOwnOrderRebuildWaiting =
        LoggerMessage.Define<string, string, string, string>(
            LogLevel.Warning,
            new EventId(2172, nameof(LogOwnOrderRebuildWaiting)),
            "The rebuild of order {EndedUpperId} of journey {JourneyId} on {AgvId} is held back: {WaitingReason}.");

    private static readonly Action<ILogger, string, string, string, Exception?> LogOwnCancellationNotRebuilt =
        LoggerMessage.Define<string, string, string>(
            LogLevel.Warning,
            new EventId(2174, nameof(LogOwnCancellationNotRebuilt)),
            "Order {EndedUpperId} of journey {JourneyId} on {AgvId} was cancelled by this server itself; it is not rebuilt, and " +
            "the journey waits for a person.");

    private static readonly Action<ILogger, string, string, string, string, Exception?> LogOwnOrderRebuildStopped =
        LoggerMessage.Define<string, string, string, string>(
            LogLevel.Error,
            new EventId(2173, nameof(LogOwnOrderRebuildStopped)),
            "Order {EndedUpperId} of journey {JourneyId} on {AgvId} is not rebuilt ({StoppedReason}); the journey waits for a person.");

    /// <summary>
    /// Takes the rebuild the journey's current stop is waiting on one step further, and says whether this round stops here.
    /// False when there is none, or when the new order was just confirmed and the round may go on to watch it like any other.
    /// </summary>
    /// <param name="mayCreate">Whether this round may create an order at all; see the class remarks on the readiness gate.</param>
    /// <param name="reasonOnceRebuilt">The journey's code once the new order is confirmed: none, or the gate's own behind it.</param>
    private async Task<bool> AdvanceOwnOrderRebuildAsync(
        JourneyRuntimeRow runtime,
        JourneyStopRow stop,
        RiotMapStationCatalogSnapshot currentMap,
        bool mayCreate,
        string? reasonOnceRebuilt,
        CancellationToken cancellationToken)
    {
        if (!WaitsOnAnArrivalTheVehicleReports(runtime.Stage) ||
            await OwnOrderRebuilds.ForStopAsync(dbContext, stop, cancellationToken).ConfigureAwait(false) is not { } rebuild)
        {
            return false;
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        if (rebuild.State == OwnOrderRebuildStates.Stopped)
        {
            await NameRebuildAsync(runtime, OwnOrderRebuildStoppedReason, now, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (rebuild.State == OwnOrderRebuildStates.Pending)
        {
            if (now < rebuild.DueAt)
            {
                return true;
            }

            if (!mayCreate)
            {
                await WaitForRebuildAsync(runtime, rebuild, "ONBOARD_SESSION_NOT_READY", null, now, cancellationToken)
                    .ConfigureAwait(false);
                return true;
            }

            string[] vehicle = await VehicleConditionReasonsAsync(runtime, cancellationToken).ConfigureAwait(false);
            if (vehicle.Length > 0)
            {
                await WaitForRebuildAsync(
                    runtime, rebuild, string.Join(',', vehicle), OwnOrderRebuildWaitingVehicleReason, now, cancellationToken)
                    .ConfigureAwait(false);
                return true;
            }

            CreateGateOutcome gate = await GateLegAsync(
                    runtime, stop, currentMap, cancellationToken,
                    toTheStopItself: stop.StopRole == JourneyStopRoles.Pickup)
                .ConfigureAwait(false);
            if (!gate.IsAllowed)
            {
                await WaitForRebuildAsync(
                    runtime, rebuild, $"CREATE_GATE:{gate.BlockReason}", OwnOrderRebuildBlockedByCreateGateReason, now,
                    cancellationToken).ConfigureAwait(false);
                return true;
            }

            // Decided: the new order's intent and the stop's pointer to it are written in one save, before RIoT is asked. A
            // crash after this save is the "decided, not created" point, and the next round carries on from Ordering.
            JourneyStopRow tracked = await TrackedStopAsync(stop.StopId, cancellationToken).ConfigureAwait(false);
            tracked.MovementLegId = rebuild.NewMovementLegId;
            tracked.UpperId = rebuild.NewUpperId;
            dbContext.OrderIntents.Add(new OrderIntentRow
            {
                MovementLegId = rebuild.NewMovementLegId,
                DemandId = runtime.DemandId,
                UpperId = rebuild.NewUpperId,
                Purpose = stop.StopRole == JourneyStopRoles.Pickup ? "TO_PICKUP" : "TO_GATE",
                TargetStationId = stop.StationId,
                VehicleKey = runtime.VehicleKey,
                MapId = runtime.MapId,
                DestinationStationId = stop.StationRiotId,
                AgvLifecycleGeneration = runtime.AgvLifecycleGeneration,
                DispatchGeneration = runtime.DispatchGeneration,
                CreatedAt = now,
                DispatchAuditVersion = 1,
                DispatchAuditSequence = 0,
                CreateAttemptCount = 0,
            });
            rebuild.State = OwnOrderRebuildStates.Ordering;
            rebuild.WaitingReason = null;
            rebuild.WaitingSince = null;
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        else if (!mayCreate)
        {
            await WaitForRebuildAsync(runtime, rebuild, "ONBOARD_SESSION_NOT_READY", null, now, cancellationToken)
                .ConfigureAwait(false);
            return true;
        }

        // Ordering: ask RIoT for the order under the upperId fixed in the record. Idempotent there, so a create already made
        // before a crash -- the "created, not recorded" point -- is found and confirmed, never made twice.
        MovementDispatchResult result = await movementDispatch.ReconcileOrCreateAsync(rebuild.NewUpperId, cancellationToken)
            .ConfigureAwait(false);
        now = timeProvider.GetUtcNow();
        if (result.Outcome == MovementDispatchOutcome.TerminalReconciliationRequired)
        {
            // The new order ended in RIoT before it was ever confirmed: a second ending, and the plainest one there is.
            rebuild.State = OwnOrderRebuildStates.Stopped;
            rebuild.StoppedReason = OwnOrderRebuilds.EndedBeforeConfirmation;
            rebuild.StoppedAt = now;
            LogOwnOrderRebuildStopped(
                logger, rebuild.EndedUpperId, runtime.JourneyId, runtime.AgvId, OwnOrderRebuilds.EndedBeforeConfirmation, null);
            await NameRebuildAsync(runtime, OwnOrderRebuildStoppedReason, now, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (result.Outcome != MovementDispatchOutcome.Confirmed)
        {
            await WaitForRebuildAsync(
                runtime, rebuild, $"CREATE:{result.Outcome}", OwnOrderRebuildOrderUnconfirmedReason, now, cancellationToken)
                .ConfigureAwait(false);
            return true;
        }

        rebuild.State = OwnOrderRebuildStates.Rebuilt;
        rebuild.RebuiltAt = now;
        rebuild.WaitingReason = null;
        rebuild.WaitingSince = null;
        if (rebuild.Source == OwnOrderRebuildSources.FaultClearedCargoOnBoard)
        {
            // The cargo goes on on the vehicle it is on, as after a confirmed resumption (REQ-0239): the binding that held it
            // through the fault has done its work, and left live it would be taken for the next fault's.
            foreach (FaultedVehicleCargoRow cargo in await dbContext.FaultedVehicleCargo
                         .Where(row => row.AgvId == runtime.AgvId && row.ReleasedAt == null)
                         .ToArrayAsync(cancellationToken).ConfigureAwait(false))
            {
                cargo.ReleasedAt = now;
                cargo.ReleasedReason = CargoRebuiltOnOriginalVehicleReason;
            }
        }

        runtime.SetBlockReason(reasonOnceRebuilt, now);
        runtime.UpdatedAt = now;
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        LogOwnOrderRebuilt(logger, rebuild.EndedUpperId, runtime.JourneyId, runtime.AgvId, rebuild.NewUpperId, null);
        return true;
    }

    /// <summary>
    /// Records that the order under <paramref name="upperId"/> -- the one <paramref name="runtime"/> waits on -- was cancelled
    /// or deleted in RIoT, and returns the code the journey carries for it: the transitional one while the rebuild waits, or
    /// the stopped one when the third guard refuses it. Staged; the caller saves. An order this server cancelled itself is not
    /// recorded at all.
    /// </summary>
    private async Task<string> RecordOrderEndedInRiotAsync(
        JourneyRuntimeRow runtime,
        string upperId,
        RiotOrderObservation order,
        CancellationToken cancellationToken)
    {
        JourneyStopRow? stop = await dbContext.Set<JourneyStopRow>().AsNoTracking()
            .SingleOrDefaultAsync(row => row.JourneyId == runtime.JourneyId && row.UpperId == upperId, cancellationToken)
            .ConfigureAwait(false);
        if (stop is null)
        {
            return OrderEndedWithoutArrivalReason;
        }

        // An order this server cancelled itself -- the release service does, through the order command surface, when the
        // vehicle is no longer eligible -- was ended on purpose, not by mistake in RIoT. Rebuilding it would undo that decision.
        if (await dbContext.RiotOrderCommandAudit.AsNoTracking()
                .AnyAsync(
                    row => row.TargetUpperId == upperId && row.CommandType == RiotCommandTypeNames.CancelOrder,
                    cancellationToken)
                .ConfigureAwait(false))
        {
            if (!string.Equals(runtime.BlockReasonCode, OrderEndedWithoutArrivalReason, StringComparison.Ordinal))
            {
                LogOwnCancellationNotRebuilt(logger, upperId, runtime.JourneyId, runtime.AgvId, null);
            }

            return OrderEndedWithoutArrivalReason;
        }

        bool known = await dbContext.OwnOrderRebuilds.AsNoTracking()
            .AnyAsync(row => row.RebuildId == OwnOrderRebuilds.RebuildIdFor(upperId), cancellationToken).ConfigureAwait(false);
        DateTimeOffset now = timeProvider.GetUtcNow();
        OwnOrderRebuildRow rebuild = await OwnOrderRebuilds.StageAsync(
            dbContext, runtime, stop, upperId, order.OrderId, order.OrderState, OwnOrderRebuildSources.CancelledInRiot,
            incidentAt: now, now, operatorId: null, runtimeOptions, cancellationToken).ConfigureAwait(false);
        if (!known)
        {
            if (rebuild.State == OwnOrderRebuildStates.Stopped)
            {
                LogOwnOrderRebuildStopped(logger, upperId, runtime.JourneyId, runtime.AgvId, rebuild.StoppedReason!, null);
            }
            else
            {
                LogOwnOrderRebuildScheduled(
                    logger, upperId, runtime.JourneyId, runtime.AgvId, rebuild.Source, rebuild.DueAt, null);
            }
        }

        return rebuild.State == OwnOrderRebuildStates.Stopped ? OwnOrderRebuildStoppedReason : OrderEndedWithoutArrivalReason;
    }

    /// <summary>
    /// The vehicle's condition, the second guard: every reason it may not be sent off now, or none. Read from RIoT's vehicle
    /// safety read -- the same read, the same reason codes, the onboard's safety projection is built from
    /// (<see cref="IRiotVehicleSafetyFacts"/>: <c>emergencyState</c>, <c>breakSwitchState</c>, <c>controlState</c>,
    /// <c>enable</c>/<c>integrationLevel</c>, <c>procState</c>, <c>locationState</c>, speed and movement, and no unfinished order on
    /// the vehicle) -- from RIoT's vehicle read for the Map it stands on, and from this server's own fault fact
    /// (<c>VehicleFaultStates.Level</c>).
    /// </summary>
    /// <remarks>
    /// An emergency stop reads <c>RIOT_EMERGENCY_NOT_OK</c>; manual or offline reads <c>RIOT_VEHICLE_NOT_ENABLED</c>,
    /// <c>RIOT_VEHICLE_NOT_ONLINE</c> or <c>RIOT_BRAKE_NOT_MOVABLE</c>; a fault reads <c>RIOT_CONTROL_NOT_OK</c> on RIoT's side and
    /// <c>VEHICLE_FAULT_IN_EFFECT</c> on this server's; another Map reads <c>RIOT_VEHICLE_MAP_MISMATCH</c>. Anything the read
    /// cannot vouch for counts against it: a vehicle is sent
    /// off only when RIoT says it is standing still with nothing in the way.
    /// </remarks>
    private async Task<string[]> VehicleConditionReasonsAsync(JourneyRuntimeRow runtime, CancellationToken cancellationToken)
    {
        List<string> reasons = [];
        if (await dbContext.VehicleFaultStates.AsNoTracking()
                .AnyAsync(row => row.AgvId == runtime.AgvId && row.Level != VehicleFaultLevel.None, cancellationToken)
                .ConfigureAwait(false))
        {
            reasons.Add("VEHICLE_FAULT_IN_EFFECT");
        }

        try
        {
            // A move order names a station of this journey's Map; a vehicle standing on another one cannot be sent there. Only a
            // read that saw the vehicle online and on some map says so: a failed read reports neither.
            RiotVehicleObservation vehicle = await vehicleFacts.ReadVehicleAsync(runtime.VehicleKey, cancellationToken)
                .ConfigureAwait(false);
            if (vehicle is { Connected: true } && !string.IsNullOrEmpty(vehicle.CurrentMap) &&
                !string.Equals(vehicle.CurrentMap, runtime.MapIdentity, StringComparison.Ordinal))
            {
                reasons.Add(Release.DemandReleaseRules.MapMismatchReason);
            }

            RiotVehicleSafetyObservation safety = await vehicleSafety
                .ReadVehicleSafetyAsync(runtime.VehicleKey, cancellationToken).ConfigureAwait(false);
            if (safety.MotionState != RiotVehicleMotionState.Stopped)
            {
                reasons.AddRange(safety.ReasonCodes.Count > 0 ? safety.ReasonCodes : ["RIOT_VEHICLE_NOT_STOPPED"]);
            }
        }
        catch (Exception error) when (error is HttpRequestException or InvalidDataException or TaskCanceledException &&
                                      !cancellationToken.IsCancellationRequested)
        {
            reasons.Add("RIOT_VEHICLE_SAFETY_UNREADABLE");
        }

        return [.. reasons];
    }

    /// <summary>Whether a rebuild may create an order behind the readiness gate; see the class remarks.</summary>
    private async Task<bool> MayCreateBehindTheGateAsync(JourneyRuntimeRow runtime, CancellationToken cancellationToken)
    {
        SessionRecoveryRow? session = await dbContext.SessionRecoveries.AsNoTracking()
            .SingleOrDefaultAsync(row => row.AgvId == runtime.AgvId, cancellationToken).ConfigureAwait(false);
        return session is not null &&
               Dashboard.OwnMovementOrderExplanation.OnlyTheVehicleKeepsItNotReady(
                   session.ReasonCode, session.SafetyReasonCodesJson, session.SafetyUnknownPresent) &&
               await SessionLiveness.HeardFromAsync(
                   dbContext, runtime.AgvId, session.SessionGeneration, timeProvider.GetUtcNow(), cancellationToken)
                   .ConfigureAwait(false);
    }

    /// <summary>Holds a due rebuild back, writing what it waits for on the record and, when given, the journey's code.</summary>
    private async Task WaitForRebuildAsync(
        JourneyRuntimeRow runtime,
        OwnOrderRebuildRow rebuild,
        string waitingReason,
        string? journeyReason,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(rebuild.WaitingReason, waitingReason, StringComparison.Ordinal))
        {
            rebuild.WaitingReason = waitingReason;
            rebuild.WaitingSince = now;
            LogOwnOrderRebuildWaiting(logger, rebuild.EndedUpperId, runtime.JourneyId, runtime.AgvId, waitingReason, null);
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        if (journeyReason is not null)
        {
            await NameRebuildAsync(runtime, journeyReason, now, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Writes <paramref name="reason"/> on the journey when it is not already its code.</summary>
    private async Task NameRebuildAsync(
        JourneyRuntimeRow runtime,
        string reason,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        checkpointWaits.Clear(runtime.VehicleKey);
        if (string.Equals(runtime.BlockReasonCode, reason, StringComparison.Ordinal))
        {
            return;
        }

        runtime.SetBlockReason(reason, now);
        runtime.UpdatedAt = now;
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
