using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime.CreateGate;
using ControlServer.Host.Runtime.Dispatch;
using ControlServer.Host.Runtime.Dispatch.Criteria;
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
/// <b>Both sides of the readiness gate, but an order only in front of it.</b> A real onboard reports its session not ready
/// for the whole of a leg that carries this server's order, so the ending is usually read behind the gate, and the record is
/// kept and named there. The new order, though, is created only when the session is Ready and Onboard's safety summary says
/// the vehicle may depart -- the same five facts dispatch admission requires before a journey's first order
/// (<see cref="VehicleDynamicFactsCriterion.SaysTheVehicleMayDepart"/>): departure safe, stopped, target slots locked, unlock
/// outputs reset, nothing unknown. That is REQ-0239's "normal dispatch and safety gates" on the vehicle's side, and it is what
/// the independent review asked for (M2): a rebuild does not go through the gate leg's pre-departure check and authorisation,
/// so it has to show the facts that check would. The first version also created behind the gate when the session was not ready
/// for the vehicle's own sake alone; it no longer does, because Onboard reports exactly <c>unknownPresent=true</c> in that
/// state and nothing then vouches for the doors. The state does not last: once the ended order is final, the server's
/// vehicle-safety read drops <c>RIOT_NONFINAL_ORDER_PRESENT</c> (only states 1, 3, 7 and 9 count), and a stopped vehicle's
/// session becomes Ready again.
/// </para>
/// <para>
/// <b>A cleared fault with cargo on board is rebuilt only on fresh evidence</b> (REQ-0362, which keeps REQ-0238's premise for
/// continuing after a repair: the cargo still whole in its original slots, and the safety loop closed again). The evidence is
/// a <c>SafetyStateSnapshot</c> the server received after the clearance -- the only message that carries each slot's state --
/// in which every slot the committed loads targeted reads OCCUPIED, LOCKED and RESET and nothing is unknown
/// (<see cref="CargoEvidenceAsync"/>). Until one arrives the journey waits under
/// <see cref="OwnOrderRebuildWaitingCargoEvidenceReason"/>; the Host asks the vehicle for one
/// (<c>OwnOrderRebuilds.ClaimCargoEvidenceRequestAsync</c>). One that arrives and does not show the cargo in place stops
/// the rebuild for a person (<see cref="OwnOrderRebuildCargoNotInPlaceReason"/>): the cargo may not be where it was. The
/// user chose this over an operator's tick-box (cs#318, scope comment of 2026-09-23, relayed by the coordinator).
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
    /// The demand had a problem again within the window after its first: no further automatic rebuild, held and alarmed for a
    /// person (control-server#318's third guard, REQ-0361).
    /// </summary>
    public const string OwnOrderRebuildStoppedReason = "OWN_ORDER_REBUILD_STOPPED";

    /// <summary>
    /// A fault with cargo on board was cleared and the rebuild is due, but the vehicle has not yet sent a snapshot, received
    /// after the clearance, that shows the cargo in its slots (REQ-0362). A wait on the vehicle, not on a person; it is in the
    /// stalled-order family all the same, so nothing overwrites it and the vehicle takes no appended demand meanwhile.
    /// </summary>
    public const string OwnOrderRebuildWaitingCargoEvidenceReason = "OWN_ORDER_REBUILD_WAITING_CARGO_EVIDENCE";

    /// <summary>
    /// A snapshot received after the clearance did not show the cargo whole in its slots -- a slot empty, unlocked or with its
    /// unlock output active, or something unknown: no automatic rebuild, held and alarmed for a person (REQ-0362).
    /// </summary>
    public const string OwnOrderRebuildCargoNotInPlaceReason = "OWN_ORDER_REBUILD_CARGO_NOT_IN_PLACE";

    /// <summary>
    /// While the order waited to be rebuilt, the vehicle stopped being eligible for a demand still to be loaded at the stop --
    /// its task-type or zone admission was withdrawn: no rebuild, and the release service releases the demand for redispatch
    /// (REQ-0328), as it would have if the order had still been running (independent review S1).
    /// </summary>
    public const string OwnOrderRebuildVehicleIneligibleReason = "OWN_ORDER_REBUILD_VEHICLE_INELIGIBLE";

    /// <summary>What <see cref="OwnOrderRebuildRow.WaitingReason"/> says while no snapshot after the clearance has arrived.</summary>
    private const string CargoEvidenceNotReceived = "CARGO_EVIDENCE_NOT_RECEIVED";

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
            await NameRebuildAsync(runtime, StoppedCode(rebuild), now, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (rebuild.State == OwnOrderRebuildStates.Failed)
        {
            // The new order FAILED before it was confirmed (review S2): fed to the fault model every round, as a confirmed
            // order's FAILED is from the arrival check, until a person clears the fault and the next record takes over.
            await ObserveRebuiltOrderFailureAsync(runtime, rebuild, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (rebuild.State == OwnOrderRebuildStates.Pending)
        {
            if (now < rebuild.DueAt)
            {
                return true;
            }

            // REQ-0362: cargo is carried on only once the vehicle has shown it whole in its slots since the clearance. Before
            // the session's readiness is looked at: the snapshot is what is missing whichever way the session stands.
            if (rebuild.Source == OwnOrderRebuildSources.FaultClearedCargoOnBoard && rebuild.CargoProvenAt is null)
            {
                CargoEvidence evidence = await CargoEvidenceAsync(runtime, rebuild, cancellationToken).ConfigureAwait(false);
                if (evidence.MessageId is null)
                {
                    await WaitForRebuildAsync(
                        runtime, rebuild, CargoEvidenceNotReceived, OwnOrderRebuildWaitingCargoEvidenceReason, now,
                        cancellationToken).ConfigureAwait(false);
                    return true;
                }

                rebuild.CargoEvidenceMessageId = evidence.MessageId;
                if (evidence.NotShown is not null)
                {
                    rebuild.State = OwnOrderRebuildStates.Stopped;
                    rebuild.StoppedReason = OwnOrderRebuilds.CargoNotProvenInOriginalSlots;
                    rebuild.StoppedAt = now;
                    rebuild.WaitingReason = evidence.NotShown;
                    rebuild.WaitingSince = now;
                    LogOwnOrderRebuildStopped(
                        logger, rebuild.EndedUpperId, runtime.JourneyId, runtime.AgvId,
                        $"{OwnOrderRebuilds.CargoNotProvenInOriginalSlots}: {evidence.NotShown}", null);
                    // Saved here, not left to NameRebuildAsync: that one saves only when the journey's code changes.
                    await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    await NameRebuildAsync(runtime, OwnOrderRebuildCargoNotInPlaceReason, now, cancellationToken)
                        .ConfigureAwait(false);
                    return true;
                }

                rebuild.CargoProvenAt = now;
                await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }

            if (await HeldBeforeCreateAsync(runtime, rebuild, stop, currentMap, mayCreate, now, cancellationToken)
                    .ConfigureAwait(false))
            {
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
        else if (await dbContext.OrderIntents.AsNoTracking()
                     .Where(row => row.MovementLegId == rebuild.NewMovementLegId)
                     .Select(row => row.CreateAttemptCount)
                     .SingleAsync(cancellationToken).ConfigureAwait(false) == 0)
        {
            // Decided but never sent -- a crash between the "decided" save and RIoT, then a restart (independent review,
            // made required as M1). The vehicle and the gate are asked again exactly as before deciding: nothing about the
            // decision still says the vehicle may move now.
            if (await HeldBeforeCreateAsync(runtime, rebuild, stop, currentMap, mayCreate, now, cancellationToken)
                    .ConfigureAwait(false))
            {
                return true;
            }
        }
        else if (!mayCreate)
        {
            await WaitForRebuildAsync(
                runtime, rebuild, "ONBOARD_SESSION_NOT_READY", OwnOrderRebuildWaitingVehicleReason, now, cancellationToken)
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
            return await EndedBeforeConfirmationAsync(runtime, rebuild, now, cancellationToken).ConfigureAwait(false);
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
    /// The new order ended in RIoT before it was ever confirmed (review S2). How it ended decides what that is, exactly as it
    /// would for an order that had been confirmed: a cancellation or deletion is recorded as a problem of its own under the new
    /// order, and REQ-0361's window judges it -- it repeats a cancellation, not a cleared fault's FAILED; a FAILED is an
    /// ordinary FAILED, recorded as a fault the clearance entry can clear. Anything else -- SUCCESS, or a state this was not
    /// written for -- says nothing about what happened to the vehicle, and stops the rebuild for a person.
    /// </summary>
    /// <remarks>
    /// The first version stopped the rebuild in every case (<c>REBUILT_ORDER_ENDED_BEFORE_CONFIRMATION</c>). For a FAILED that
    /// recorded no fault, so the clearance entry of control-server#299 had nothing to clear and the journey could not move again;
    /// for a cancellation outside the window it stopped what REQ-0361 says to rebuild.
    /// </remarks>
    private async Task<bool> EndedBeforeConfirmationAsync(
        JourneyRuntimeRow runtime,
        OwnOrderRebuildRow rebuild,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        RiotOrderObservation order = await vehicleFacts.ReconcileByUpperIdAsync(rebuild.NewUpperId, cancellationToken)
            .ConfigureAwait(false);
        if (order is { Kind: RiotOrderObservationKind.Terminal, OrderState: RiotOrderState.Failed })
        {
            rebuild.State = OwnOrderRebuildStates.Failed;
            rebuild.StoppedReason = OwnOrderRebuilds.FailedBeforeConfirmation;
            rebuild.StoppedAt = now;
            rebuild.WaitingReason = null;
            rebuild.WaitingSince = null;
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await ObserveOrderFailureAsync(runtime, await RebuiltIntentAsync(rebuild, cancellationToken).ConfigureAwait(false),
                order, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (order is { Kind: RiotOrderObservationKind.Terminal, OrderState: RiotOrderState.Cancelled or RiotOrderState.Deleted })
        {
            rebuild.State = OwnOrderRebuildStates.Ended;
            rebuild.StoppedReason = OwnOrderRebuilds.CancelledBeforeConfirmation;
            rebuild.StoppedAt = now;
            rebuild.WaitingReason = null;
            rebuild.WaitingSince = null;
            string code = await RecordOrderEndedInRiotAsync(runtime, rebuild.NewUpperId, order, cancellationToken)
                .ConfigureAwait(false);
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await NameRebuildAsync(runtime, code, now, cancellationToken).ConfigureAwait(false);
            return true;
        }

        rebuild.State = OwnOrderRebuildStates.Stopped;
        rebuild.StoppedReason = OwnOrderRebuilds.EndedBeforeConfirmation;
        rebuild.StoppedAt = now;
        LogOwnOrderRebuildStopped(
            logger, rebuild.EndedUpperId, runtime.JourneyId, runtime.AgvId, OwnOrderRebuilds.EndedBeforeConfirmation, null);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await NameRebuildAsync(runtime, OwnOrderRebuildStoppedReason, now, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>Feeds the FAILED new order of <paramref name="rebuild"/> to the fault model once more.</summary>
    private async Task ObserveRebuiltOrderFailureAsync(
        JourneyRuntimeRow runtime,
        OwnOrderRebuildRow rebuild,
        CancellationToken cancellationToken)
    {
        RiotOrderObservation order = await vehicleFacts.ReconcileByUpperIdAsync(rebuild.NewUpperId, cancellationToken)
            .ConfigureAwait(false);
        if (!await ObserveOrderFailureAsync(
                runtime, await RebuiltIntentAsync(rebuild, cancellationToken).ConfigureAwait(false), order, cancellationToken)
                .ConfigureAwait(false))
        {
            // A terminal FAILED does not change; a read that no longer says so is not trusted to mean anything else.
            await NameRebuildAsync(runtime, VehicleFaultEvidence.OrderFailed, timeProvider.GetUtcNow(), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private Task<OrderIntentRow> RebuiltIntentAsync(OwnOrderRebuildRow rebuild, CancellationToken cancellationToken) =>
        dbContext.OrderIntents.AsNoTracking()
            .SingleAsync(row => row.MovementLegId == rebuild.NewMovementLegId, cancellationToken);

    /// <summary>
    /// Everything that has to hold before the new order may be asked of RIoT: the session, the vehicle's condition (the second
    /// guard) and REQ-0305's create gate. True when one of them holds the rebuild back, which is then recorded and named on the
    /// journey. Asked before the decision and again after it for as long as the order has never been sent (M1).
    /// </summary>
    private async Task<bool> HeldBeforeCreateAsync(
        JourneyRuntimeRow runtime,
        OwnOrderRebuildRow rebuild,
        JourneyStopRow stop,
        RiotMapStationCatalogSnapshot currentMap,
        bool mayCreate,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // Review S1: a vehicle no longer admitted for a demand still to be loaded here is not sent off again only to have the
        // release service cancel the new order a round later. Asked first, and on either side of the gate: it does not wait on
        // anything the vehicle can supply, and nothing about it gets better by waiting.
        if (stop.StopRole == JourneyStopRoles.Pickup &&
            await NoLongerEligibleAsync(runtime, stop, now, cancellationToken).ConfigureAwait(false) is { } trigger)
        {
            rebuild.State = OwnOrderRebuildStates.Stopped;
            rebuild.StoppedReason = OwnOrderRebuilds.VehicleNoLongerEligible;
            rebuild.StoppedAt = now;
            rebuild.WaitingReason = trigger;
            rebuild.WaitingSince = now;
            LogOwnOrderRebuildStopped(
                logger, rebuild.EndedUpperId, runtime.JourneyId, runtime.AgvId,
                $"{OwnOrderRebuilds.VehicleNoLongerEligible}: {trigger}", null);
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await NameRebuildAsync(runtime, OwnOrderRebuildVehicleIneligibleReason, now, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (!mayCreate)
        {
            await WaitForRebuildAsync(
                runtime, rebuild, "ONBOARD_SESSION_NOT_READY", OwnOrderRebuildWaitingVehicleReason, now, cancellationToken)
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

        return false;
    }

    /// <summary>The journey's code for a stopped rebuild, by why it stopped.</summary>
    private static string StoppedCode(OwnOrderRebuildRow rebuild) => rebuild.StoppedReason switch
    {
        OwnOrderRebuilds.CargoNotProvenInOriginalSlots => OwnOrderRebuildCargoNotInPlaceReason,
        OwnOrderRebuilds.VehicleNoLongerEligible => OwnOrderRebuildVehicleIneligibleReason,
        _ => OwnOrderRebuildStoppedReason,
    };

    /// <summary>
    /// Why the vehicle is no longer eligible for one of the demands still to be loaded at <paramref name="stop"/>, or null. The
    /// release service's own verdict (<see cref="Release.DemandReleaseRules.VehicleNoLongerEligible"/>) on the facts this server
    /// holds -- task-type and zone admission. The fault and the Map are the second guard's, so they are left out here.
    /// </summary>
    private async Task<string?> NoLongerEligibleAsync(
        JourneyRuntimeRow runtime,
        JourneyStopRow stop,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var waiting = await (
                from membership in dbContext.Set<JourneyDemandRow>().AsNoTracking()
                join demand in dbContext.AcceptedDemands.AsNoTracking() on membership.DemandId equals demand.DemandId
                where membership.JourneyId == runtime.JourneyId && membership.PickupStopId == stop.StopId &&
                      membership.RemovedAt == null && membership.Status == JourneyDemandStatuses.PendingLoad
                select new { demand.WorkType, membership.DispatchZone })
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        if (waiting.Length == 0)
        {
            return null;
        }

        VehicleDispatchPolicy policy = await dispatchPolicy.EnsureCurrentAsync(cancellationToken).ConfigureAwait(false);
        return waiting
            .Select(item => Release.DemandReleaseRules.VehicleNoLongerEligible(
                fault: null, policy, runtime.AgvId, item.WorkType, item.DispatchZone, observation: null, now, runtimeOptions))
            .FirstOrDefault(trigger => trigger is not null);
    }

    /// <summary>
    /// What the vehicle has shown about its cargo since the clearance (REQ-0362): no snapshot yet (<c>MessageId</c> null), or
    /// the freshest snapshot received after <see cref="OwnOrderRebuildRow.RecordedAt"/> and, when it does not show the cargo
    /// in place, why not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Which slots.</b> Those the committed loads of the journey's demands still on board targeted -- the load batch's own
    /// <see cref="StationOperationRow.TargetSlotsJson"/>. A load of theirs that is neither committed nor settled (prepared, or
    /// waiting on recovery) leaves where the cargo is an open question, and so does having no committed load at all; both are
    /// "not shown", never "shown".
    /// </para>
    /// <para>
    /// <b>After the clearance, by the server's receive clock.</b> The clearance time and the receive time are both this
    /// server's; the snapshot's own <c>observedAt</c> is the vehicle's clock. A server clock stepped back can make a fresh
    /// snapshot look old, which only makes the rebuild wait -- the safe way round.
    /// </para>
    /// </remarks>
    private async Task<CargoEvidence> CargoEvidenceAsync(
        JourneyRuntimeRow runtime,
        OwnOrderRebuildRow rebuild,
        CancellationToken cancellationToken)
    {
        ProtocolInboxRow[] snapshots = await dbContext.ProtocolInbox.AsNoTracking()
            .Where(row => row.MessageType == "SafetyStateSnapshot")
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        // Compared in memory: SQLite cannot order or compare DateTimeOffset columns in the store.
        ProtocolInboxRow? fresh = snapshots
            .Where(row => row.ReceivedAt > rebuild.RecordedAt && SnapshotOf(row) == runtime.AgvId)
            .OrderByDescending(row => row.ReceivedAt)
            .FirstOrDefault();
        if (fresh is null)
        {
            return new CargoEvidence(null, null);
        }

        string[] onBoard = await dbContext.Set<JourneyDemandRow>().AsNoTracking()
            .Where(row => row.JourneyId == runtime.JourneyId && row.RemovedAt == null &&
                          row.Status != JourneyDemandStatuses.PendingLoad &&
                          row.Status != JourneyDemandStatuses.Unloaded &&
                          row.Status != JourneyDemandStatuses.Terminated)
            .Select(row => row.DemandId)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        StationOperationRow[] loads = await dbContext.StationOperations.AsNoTracking()
            .Where(row => onBoard.Contains(row.DemandId) && row.OperationType == SlotOperationType.Load)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        if (loads.FirstOrDefault(row => row.Status is StationOperationStatus.Prepared or StationOperationStatus.RecoveryRequired)
            is { } unsettled)
        {
            return new CargoEvidence(fresh.MessageId, $"LOAD_NOT_SETTLED:{unsettled.SlotOperationAttemptId}");
        }

        int[] cargoSlots = [.. loads
            .Where(row => row.Status == StationOperationStatus.Committed)
            .SelectMany(row => JsonSerializer.Deserialize<int[]>(row.TargetSlotsJson) ?? [])
            .Distinct()
            .Order()];
        if (cargoSlots.Length == 0)
        {
            return new CargoEvidence(fresh.MessageId, "CARGO_SLOTS_UNKNOWN");
        }

        using JsonDocument document = JsonDocument.Parse(fresh.RequestJson);
        JsonElement payload = document.RootElement.GetProperty("payload");
        List<string> notShown = [];
        if (!payload.GetProperty("safety").TryGetProperty("unknownPresent", out JsonElement unknown) ||
            unknown.ValueKind != JsonValueKind.False)
        {
            notShown.Add("UNKNOWN_PRESENT");
        }

        Dictionary<int, JsonElement> slots = payload.GetProperty("slotStates").EnumerateArray()
            .ToDictionary(item => item.GetProperty("slotNo").GetInt32());
        foreach (int slot in cargoSlots)
        {
            if (!slots.TryGetValue(slot, out JsonElement state))
            {
                notShown.Add($"SLOT_{slot}:NOT_REPORTED");
                continue;
            }

            string physical = state.GetProperty("physicalState").GetString() ?? "";
            string locked = state.GetProperty("lockState").GetString() ?? "";
            string output = state.GetProperty("unlockOutputState").GetString() ?? "";
            if (physical != "OCCUPIED" || locked != "LOCKED" || output != "RESET")
            {
                notShown.Add($"SLOT_{slot}:{physical},{locked},{output}");
            }
        }

        return new CargoEvidence(fresh.MessageId, notShown.Count == 0 ? null : string.Join(';', notShown));

        static string SnapshotOf(ProtocolInboxRow row)
        {
            using JsonDocument envelope = JsonDocument.Parse(row.RequestJson);
            return envelope.RootElement.GetProperty("agvId").GetString() ?? "";
        }
    }

    /// <summary>The snapshot that answered the cargo question, if any, and what it did not show (null when it showed it all).</summary>
    private sealed record CargoEvidence(string? MessageId, string? NotShown);

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
        // Onboard's half (review M2): a Ready session whose safety summary vouches for sending the vehicle off.
        OnboardDispatchFacts? onboard = await ReadOnboardFactsAsync(runtime.AgvId, cancellationToken).ConfigureAwait(false);
        if (onboard is null)
        {
            reasons.Add("ONBOARD_FACTS_NOT_READY");
        }
        else if (!VehicleDynamicFactsCriterion.SaysTheVehicleMayDepart(onboard))
        {
            reasons.Add("ONBOARD_DEPARTURE_UNSAFE");
        }

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
