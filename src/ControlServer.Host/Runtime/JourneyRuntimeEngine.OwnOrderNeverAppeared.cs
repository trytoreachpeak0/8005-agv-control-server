using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Host.Runtime;

/// <summary>
/// control-server#673: a demand leg's order whose create went out with its result unknown, and which RIoT then never shows, is
/// recorded as ended -- "never appeared" -- and handed to control-server#318's rebuild, the way an order someone cancelled in RIoT
/// is.
/// </summary>
/// <remarks>
/// <para>
/// <b>What was wrong.</b> Once a create has been sent, <c>MovementDispatchService.ReconcileOrCreateAsync</c> never creates again
/// for that upperId, and a later "no such order" only keeps the intent <c>RESULT_UNKNOWN</c>. The arrival branches wrote
/// <c>PICKUP_ResultUnknown</c> or <c>GATE_ResultUnknown</c> every round and nothing else: no window, no exit, and none of the
/// existing paths -- the rebuild, the fault clearance, control-server#345's exits -- accepted the state. The vehicle stood where it
/// was with its demand, and possibly its cargo, until someone edited the database. The same hole sat under a rebuild's own new
/// order (<c>OWN_ORDER_REBUILD_ORDER_UNCONFIRMED</c> for ever).
/// </para>
/// <para>
/// <b>The evidence is control-server#611's, not a second copy</b> (<see cref="OrderNeverAppearedAsync"/>): RIoT answers exactly
/// "no such order" for this upperId -- 404, or real RIoT's HTTP 200 with business code 0 and no result -- for
/// <c>JourneyRuntime:ChargingOrderAbsentAbandonAfter</c> without a break, the vehicle is proven stopped with no task, and RIoT's
/// unfinished-order listing, read in full, holds nothing for it. The preconditions it leaves to its caller are checked here: the
/// create was sent (<c>CreateAttemptCount &gt; 0</c>), and the intent never got an orderId.
/// </para>
/// <para>
/// <b>Why the rebuild and not a failure.</b> A create that never took is the same situation as an order of ours someone ended in
/// RIoT before the vehicle arrived -- the trip is not done, the vehicle is where it was -- and REQ-0360 already says what happens
/// then: the same vehicle carries on with the same demand, nothing released, nothing redispatched. So the rebuild takes the stop
/// over with every guard it already has: the delay, the vehicle's condition (which re-reads that RIoT shows no unfinished order on
/// it right before the create), REQ-0305's create gate, the vehicle no longer being admitted for a demand still to load, and, with
/// cargo on board, a snapshot received afterwards that shows the cargo whole in its slots (REQ-0360 as CP-0007 revised it -- the
/// same proof a cancellation with cargo on board waits for). A second problem of the demand within REQ-0361's window stops it for a
/// person, whom control-server#345's three exits serve.
/// </para>
/// <para>
/// <b>Never a second order under the same upperId</b>: the rebuild's new order has its own upperId, and the old one is never sent
/// again. Should the old one turn up running after all, the foreign-order supervisor holds the vehicle and alarms
/// (<see cref="AbandonedDemandLegOrders"/>), never cancels.
/// </para>
/// </remarks>
public sealed partial class JourneyRuntimeEngine
{
    /// <summary>
    /// The order of this leg was sent to RIoT with its result unknown, and RIoT has answered "no such order" long enough with the
    /// vehicle proven idle: recorded to be rebuilt for the same vehicle and demand (control-server#673). The transitional code until
    /// the rebuild names what it waits on.
    /// </summary>
    public const string OwnOrderNeverAppearedReason = "OWN_ORDER_NEVER_APPEARED";

    private static readonly Action<ILogger, string, string, string, double, Exception?> LogOwnOrderNeverAppeared =
        LoggerMessage.Define<string, string, string, double>(
            LogLevel.Warning,
            new EventId(2233, nameof(LogOwnOrderNeverAppeared)),
            "Order {UpperId} of journey {JourneyId} on {AgvId} was sent with its result unknown and RIoT answered no such order for " +
            "{AbsentSeconds:0} s with the vehicle proven idle: taken as never created, and recorded to be rebuilt (control-server#673).");

    /// <summary>
    /// Records the order under <paramref name="upperId"/> -- the one the journey's current stop waits on -- as never having appeared,
    /// when control-server#611's evidence says so; true when it did, and the journey now carries the code for it. False, with nothing
    /// written, when a precondition does not hold or the evidence is not complete this round.
    /// </summary>
    /// <param name="endedRebuild">
    /// The rebuild whose new order this is, when it is one: it ends, and the record staged here takes over from it, as for a new
    /// order cancelled before it was confirmed.
    /// </param>
    private async Task<bool> RecordOwnOrderNeverAppearedAsync(
        JourneyRuntimeRow runtime,
        string upperId,
        OwnOrderRebuildRow? endedRebuild,
        CancellationToken cancellationToken)
    {
        // A demand's leg only: an idle return, a charge order and a clearance move have their own ending for this (control-server#611).
        if (runtime.CarriesNoDemand() || !WaitsOnAnArrivalTheVehicleReports(runtime.Stage))
        {
            return false;
        }

        // The preconditions OrderNeverAppearedAsync leaves to its caller, read afresh: the reconciliation that just answered may
        // have changed the row. An intent that ever got an orderId had an order in RIoT, whatever RIoT says now.
        OrderIntentRow? intent = await dbContext.OrderIntents.AsNoTracking()
            .SingleOrDefaultAsync(row => row.UpperId == upperId, cancellationToken).ConfigureAwait(false);
        if (intent is not { Status: "RESULT_UNKNOWN", OrderId: null, CreateAttemptCount: > 0 })
        {
            return false;
        }

        JourneyStopRow? stop = await dbContext.Set<JourneyStopRow>().AsNoTracking()
            .SingleOrDefaultAsync(row => row.JourneyId == runtime.JourneyId && row.UpperId == upperId, cancellationToken)
            .ConfigureAwait(false);
        if (stop is null ||
            await dbContext.OwnOrderRebuilds.AsNoTracking()
                .AnyAsync(row => row.RebuildId == OwnOrderRebuilds.RebuildIdFor(upperId), cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        if (await OrderNeverAppearedAsync(upperId, runtime.VehicleKey, cancellationToken).ConfigureAwait(false) is not { } evidence)
        {
            return false;
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        if (endedRebuild is not null)
        {
            endedRebuild.State = OwnOrderRebuildStates.Ended;
            endedRebuild.StoppedReason = OwnOrderRebuilds.NeverAppearedBeforeConfirmation;
            endedRebuild.StoppedAt = now;
            endedRebuild.WaitingReason = null;
            endedRebuild.WaitingSince = null;
        }

        OwnOrderRebuildRow rebuild = await OwnOrderRebuilds.StageAsync(
            dbContext, runtime, stop, upperId, endedOrderId: null, endedOrderState: null, OwnOrderRebuildSources.OrderNeverAppeared,
            incidentAt: now, now, operatorId: null, runtimeOptions, cancellationToken).ConfigureAwait(false);
        LogOwnOrderNeverAppeared(logger, upperId, runtime.JourneyId, runtime.AgvId, evidence.Absent.TotalSeconds, null);
        string code;
        if (rebuild.State == OwnOrderRebuildStates.Stopped)
        {
            LogOwnOrderRebuildStopped(logger, upperId, runtime.JourneyId, runtime.AgvId, rebuild.StoppedReason!, null);
            code = OwnOrderRebuildStoppedReason;
        }
        else
        {
            LogOwnOrderRebuildScheduled(logger, upperId, runtime.JourneyId, runtime.AgvId, rebuild.Source, rebuild.DueAt, null);
            code = OwnOrderNeverAppearedReason;
        }

        checkpointWaits.Clear(runtime.VehicleKey);
        runtime.SetBlockReason(code, now);
        runtime.UpdatedAt = now;
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        dispatchRound.Charging.Board.SeenOrUnread(upperId);
        return true;
    }
}
