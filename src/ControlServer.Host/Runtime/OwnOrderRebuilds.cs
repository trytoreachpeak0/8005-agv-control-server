using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Host.Runtime;

/// <summary>
/// Records that one of this server's own move orders ended and is to be rebuilt for the same vehicle and the same demand
/// (control-server#318), and finds the record a journey's current stop is waiting on. Stages only; the caller saves.
/// </summary>
/// <remarks>
/// <para>
/// <b>Three sources, one record.</b> The engine stages it when it reads the order a journey waits on as CANCELLED or DELETED
/// in RIoT; the fault recovery service stages it, in the clearance's own transaction, when a person clears the fault a
/// FAILED order raised. From there the engine alone drives it (<c>JourneyRuntimeEngine.AdvanceOwnOrderRebuildAsync</c>):
/// the delay, the vehicle's condition, the create gate, the new order, its confirmation.
/// </para>
/// <para>
/// <b>Idempotent by construction.</b> The key is derived from the ended order's upperId, and one RIoT order ends once, so
/// staging the same ending twice -- the same cancellation read in two rounds, the same clearance made twice -- returns the
/// row already there instead of adding a second. The new order's upperId and movement leg are fixed in the row when it is
/// staged, so every later attempt, before or after a restart, asks RIoT for the same order (upperId idempotency,
/// BC-ORDER-004) and a crash between deciding and creating, or between creating and recording, neither doubles nor loses it.
/// </para>
/// <para>
/// <b>The third guard is decided here</b>, when the ending is recorded (REQ-0361, CP-0006): a demand that already had a
/// problem within <see cref="JourneyRuntimeOptions.OwnOrderRebuildRepeatWindow"/> before this one is not rebuilt again, and
/// the row is staged already <see cref="OwnOrderRebuildStates.Stopped"/>. The window starts at the earlier problem -- its
/// <see cref="OwnOrderRebuildRow.IncidentAt"/> -- not at the rebuild it led to: a rebuild the vehicle's condition held back
/// for minutes does not stretch the window. The earlier problem's source decides what counts as "again": after a
/// cancellation only another cancellation or deletion does; after a cleared fault, another FAILED or a cancellation does.
/// The reason the guard exists ("repeated cancelling means someone wants the vehicle to stop") is the coordinator's
/// wording, not the user's (CP-0006, item two, note 2).
/// </para>
/// </remarks>
internal static class OwnOrderRebuilds
{
    /// <summary>Why a rebuild was not made: the demand had a problem again within the window after an earlier one (REQ-0361).</summary>
    public const string EndedAgainWithinWindow = "REBUILT_ORDER_ENDED_AGAIN_WITHIN_WINDOW";

    /// <summary>
    /// Why a rebuild was not made: a snapshot received after a clearance with cargo on board did not show the cargo whole in
    /// its slots (REQ-0362). The record's waiting reason says which slot read what.
    /// </summary>
    public const string CargoNotProvenInOriginalSlots = "CARGO_NOT_PROVEN_IN_ORIGINAL_SLOTS";

    /// <summary>
    /// Why a rebuild was not made: the vehicle is no longer admitted for a demand still to be loaded at the stop (review S1).
    /// The record's waiting reason names the admission criterion. The release service releases the demand for redispatch.
    /// </summary>
    public const string VehicleNoLongerEligible = "VEHICLE_NO_LONGER_ELIGIBLE";

    /// <summary>
    /// Why a rebuild was not made: the new order ended in RIoT before it was ever confirmed, in a terminal state that is neither
    /// a cancellation nor FAILED -- RIoT's 8, SUSPENDED, which the gateway reads as terminal. Nothing then says what happened to
    /// the vehicle, so a person looks. (A SUCCESS before confirmation is taken as the order confirmed, and never ends here.)
    /// </summary>
    public const string EndedBeforeConfirmation = "REBUILT_ORDER_ENDED_BEFORE_CONFIRMATION";

    /// <summary>
    /// How a rebuild ended when its new order was cancelled or deleted in RIoT before it was confirmed: that cancellation is
    /// recorded as a problem of its own, under the new order, and REQ-0361's window judges it (review S2).
    /// </summary>
    public const string CancelledBeforeConfirmation = "REBUILT_ORDER_CANCELLED_BEFORE_CONFIRMATION";

    /// <summary>
    /// How a rebuild ended when its new order FAILED before it was confirmed: an ordinary FAILED, recorded as a fault, whose
    /// clearance by a person records the next rebuild (review S2).
    /// </summary>
    public const string FailedBeforeConfirmation = "REBUILT_ORDER_FAILED_BEFORE_CONFIRMATION";

    /// <summary>The stable key of the rebuild of the order under <paramref name="endedUpperId"/>.</summary>
    public static string RebuildIdFor(string endedUpperId) =>
        JourneyPlanBuilder.StableGuid(endedUpperId, "own-order-rebuild");

    /// <summary>
    /// Stages the record of <paramref name="endedUpperId"/>'s ending for <paramref name="stop"/>, or returns the one already
    /// staged or saved for it. Not saved.
    /// </summary>
    public static async Task<OwnOrderRebuildRow> StageAsync(
        ControlServerDbContext dbContext,
        JourneyRuntimeRow runtime,
        JourneyStopRow stop,
        string endedUpperId,
        string? endedOrderId,
        int? endedOrderState,
        string source,
        DateTimeOffset incidentAt,
        DateTimeOffset now,
        string? operatorId,
        JourneyRuntimeOptions options,
        CancellationToken cancellationToken)
    {
        string rebuildId = RebuildIdFor(endedUpperId);
        OwnOrderRebuildRow? existing = dbContext.ChangeTracker.Entries<OwnOrderRebuildRow>()
            .Select(entry => entry.Entity)
            .FirstOrDefault(row => row.RebuildId == rebuildId)
            ?? await dbContext.OwnOrderRebuilds
                .SingleOrDefaultAsync(row => row.RebuildId == rebuildId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return existing;
        }

        // A rebuild whose new order FAILED before it was confirmed waited on this order's fault; the record staged here takes
        // over from it, so it ends (review S2). Tracked, so the caller's save writes both.
        foreach (OwnOrderRebuildRow failed in await dbContext.OwnOrderRebuilds
                     .Where(row => row.NewUpperId == endedUpperId && row.State == OwnOrderRebuildStates.Failed)
                     .ToArrayAsync(cancellationToken).ConfigureAwait(false))
        {
            failed.State = OwnOrderRebuildStates.Ended;
        }

        // The ordinal only has to keep this journey's rebuilt upperIds apart; the row keeps the value it was given.
        int ordinal = await dbContext.OwnOrderRebuilds
            .CountAsync(row => row.JourneyId == runtime.JourneyId, cancellationToken).ConfigureAwait(false) + 1;
        DateTimeOffset since = incidentAt - options.OwnOrderRebuildRepeatWindow;
        // Compared in memory: SQLite cannot order or compare DateTimeOffset columns in the store.
        bool rebuiltRecently = (await dbContext.OwnOrderRebuilds.AsNoTracking()
                .Where(row => row.DemandId == runtime.DemandId)
                .Select(row => new { row.Source, row.IncidentAt })
                .ToArrayAsync(cancellationToken).ConfigureAwait(false))
            .Any(earlier => earlier.IncidentAt >= since && earlier.IncidentAt <= incidentAt &&
                            CountsAsAgain(earlier.Source, source));

        OwnOrderRebuildRow row = new()
        {
            RebuildId = rebuildId,
            JourneyId = runtime.JourneyId,
            DemandId = runtime.DemandId,
            AgvId = runtime.AgvId,
            VehicleKey = runtime.VehicleKey,
            StopId = stop.StopId,
            Source = source,
            EndedUpperId = endedUpperId,
            EndedOrderId = endedOrderId,
            EndedOrderState = endedOrderState,
            IncidentAt = incidentAt,
            RecordedAt = now,
            DueAt = now + options.OwnOrderRebuildDelay,
            OperatorId = operatorId,
            NewUpperId = $"W2G-{runtime.DemandId}-REBUILD-{runtime.DispatchGeneration}-{ordinal}",
            NewMovementLegId = JourneyPlanBuilder.StableGuid(rebuildId, "rebuilt-leg"),
            State = rebuiltRecently ? OwnOrderRebuildStates.Stopped : OwnOrderRebuildStates.Pending,
            StoppedReason = rebuiltRecently ? EndedAgainWithinWindow : null,
            StoppedAt = rebuiltRecently ? now : null,
        };
        dbContext.OwnOrderRebuilds.Add(row);
        return row;
    }

    /// <summary>The stable key of the rebuild a person asked for in place of the stopped one keyed <paramref name="stoppedRebuildId"/>.</summary>
    public static string ManualRebuildIdFor(string stoppedRebuildId) =>
        JourneyPlanBuilder.StableGuid(stoppedRebuildId, "manual-rebuild");

    /// <summary>
    /// Stages a person's rebuild of <paramref name="stopped"/>, a rebuild the third guard stopped (control-server#345), and
    /// returns the record the engine now drives. Not saved.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Due at once, and from there like any other.</b> The engine takes it through the vehicle's condition, the create
    /// gate, the session and Onboard's departure facts, and, for a cleared fault with cargo on board, a snapshot received after
    /// this request that shows the cargo in place: the record keeps the stopped one's <see cref="OwnOrderRebuildRow.Source"/>,
    /// and its <see cref="OwnOrderRebuildRow.RecordedAt"/> is now. Only the delay is not waited: a person has just looked at
    /// the vehicle and asked for it.
    /// </para>
    /// <para>
    /// <b>The window starts again from the request</b> (the coordinator's decision of 2026-09-23): the record's
    /// <see cref="OwnOrderRebuildRow.IncidentAt"/> is the moment the person asked, so the next ending of the demand within
    /// the window from then is judged "again" by <see cref="StageAsync"/> and stops for a person once more.
    /// </para>
    /// <para>
    /// <b>Two shapes, because one RIoT order has one record</b> (the unique index on
    /// <see cref="OwnOrderRebuildRow.EndedUpperId"/>). Stopped when the ending was recorded, the record still waits on the
    /// order that ended, and its new order was never decided: it is reopened in place, keeping why and when it stopped
    /// (<see cref="OwnOrderRebuildRow.StoppedReason"/> and <see cref="OwnOrderRebuildRow.StoppedAt"/> stay set on a record
    /// that is no longer stopped only this way). Stopped because its new order ended before it was confirmed, the stop points
    /// at that new order, which has no record of its own yet: a new record takes over for it, keyed on the stopped one
    /// (<see cref="ManualRebuildIdFor"/>), and the stopped one turns <see cref="OwnOrderRebuildStates.Ended"/>.
    /// </para>
    /// <para>
    /// Either way <see cref="OwnOrderRebuildRow.OperatorId"/> names the person who asked -- for a cleared fault that replaces
    /// the person who cleared it, who stays on the fault fact and in event 9203 -- and <see cref="IsPersonsRebuild"/> tells the
    /// record apart from an automatic one.
    /// </para>
    /// </remarks>
    public static async Task<OwnOrderRebuildRow> StageManualRebuildAsync(
        ControlServerDbContext dbContext,
        JourneyRuntimeRow runtime,
        JourneyStopRow stop,
        OwnOrderRebuildRow stopped,
        string operatorId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (stop.UpperId == stopped.EndedUpperId)
        {
            stopped.State = OwnOrderRebuildStates.Pending;
            stopped.IncidentAt = now;
            stopped.RecordedAt = now;
            stopped.DueAt = now;
            stopped.OperatorId = operatorId;
            stopped.WaitingReason = null;
            stopped.WaitingSince = null;
            stopped.CargoProvenAt = null;
            stopped.CargoEvidenceMessageId = null;
            WithdrawCargoEvidenceRequest(stopped);
            stopped.CargoEvidenceRequestedAt = null;
            return stopped;
        }

        string rebuildId = ManualRebuildIdFor(stopped.RebuildId);
        stopped.State = OwnOrderRebuildStates.Ended;
        int ordinal = await dbContext.OwnOrderRebuilds
            .CountAsync(row => row.JourneyId == runtime.JourneyId, cancellationToken).ConfigureAwait(false) + 1;
        string? endedOrderId = await dbContext.OrderIntents.AsNoTracking()
            .Where(row => row.UpperId == stop.UpperId)
            .Select(row => row.OrderId)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        OwnOrderRebuildRow row = new()
        {
            RebuildId = rebuildId,
            JourneyId = runtime.JourneyId,
            DemandId = runtime.DemandId,
            AgvId = runtime.AgvId,
            VehicleKey = runtime.VehicleKey,
            StopId = stop.StopId,
            Source = stopped.Source,
            EndedUpperId = stop.UpperId,
            EndedOrderId = endedOrderId,
            IncidentAt = now,
            RecordedAt = now,
            DueAt = now,
            OperatorId = operatorId,
            NewUpperId = $"W2G-{runtime.DemandId}-REBUILD-{runtime.DispatchGeneration}-{ordinal}",
            NewMovementLegId = JourneyPlanBuilder.StableGuid(rebuildId, "rebuilt-leg"),
            State = OwnOrderRebuildStates.Pending,
        };
        dbContext.OwnOrderRebuilds.Add(row);
        return row;
    }

    /// <summary>
    /// Whether <paramref name="row"/> is a rebuild a person asked for (<see cref="StageManualRebuildAsync"/>): a stopped
    /// record reopened in place, which alone carries a stop reason while it is waiting, being ordered or rebuilt; or the record
    /// that took over from a stopped one, keyed on it.
    /// </summary>
    public static bool IsPersonsRebuild(OwnOrderRebuildRow row, IEnumerable<OwnOrderRebuildRow> journeysRecords) =>
        (row.StoppedReason is not null &&
         row.State is OwnOrderRebuildStates.Pending or OwnOrderRebuildStates.Ordering or OwnOrderRebuildStates.Rebuilt) ||
        journeysRecords.Any(other => ManualRebuildIdFor(other.RebuildId) == row.RebuildId);

    /// <summary>
    /// Whether the Host should ask <paramref name="agvId"/>'s vehicle for a <c>SafetyStateSnapshot</c> now, for a rebuild after a
    /// cleared fault with cargo on board that is still waiting for the vehicle to show the cargo in its slots (REQ-0362); when
    /// it should, the request is recorded here and the caller must send it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The throttle</b>: once per session generation; once more in the same generation when the session has become ready
    /// since -- a vehicle whose session was not ready may not have answered; none once the cargo is proven, the rebuild is
    /// stopped, or it is not a cargo rebuild at all. So a session that never becomes ready is asked once, not on every
    /// message, and a reconnection asks again.
    /// </para>
    /// <para>
    /// <b>Cheap when nothing waits.</b> Every inbound message after the handshake comes through here. The question is a read on
    /// the <c>(AgvId, State)</c> index first, and only a due request becomes a write: one conditional update, so two
    /// connections of the same vehicle racing here claim the request once between them. The update touches only the request
    /// columns, not <see cref="OwnOrderRebuildRow.State"/>, the row's concurrency token, so it never conflicts with the
    /// engine's round.
    /// </para>
    /// </remarks>
    public static async Task<bool> ClaimCargoEvidenceRequestAsync(
        ControlServerDbContext dbContext,
        string agvId,
        long generation,
        bool ready,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        string[] due = await dbContext.OwnOrderRebuilds.AsNoTracking()
            .Where(row => row.AgvId == agvId &&
                          ((row.State == OwnOrderRebuildStates.Pending &&
                            row.Source == OwnOrderRebuildSources.FaultClearedCargoOnBoard && row.CargoProvenAt == null) ||
                           row.State == OwnOrderRebuildStates.AwaitingCargoHandoff) &&
                          (row.CargoEvidenceRequestedGeneration != generation ||
                           (ready && !row.CargoEvidenceRequestedWhileReady)))
            .Select(row => row.RebuildId)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        if (due.Length == 0)
        {
            return false;
        }

        int claimed = await dbContext.OwnOrderRebuilds
            .Where(row => due.Contains(row.RebuildId) &&
                          ((row.State == OwnOrderRebuildStates.Pending && row.CargoProvenAt == null) ||
                           row.State == OwnOrderRebuildStates.AwaitingCargoHandoff) &&
                          (row.CargoEvidenceRequestedGeneration != generation ||
                           (ready && !row.CargoEvidenceRequestedWhileReady)))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(row => row.CargoEvidenceRequestedGeneration, generation)
                    .SetProperty(row => row.CargoEvidenceRequestedWhileReady, ready)
                    .SetProperty(row => row.CargoEvidenceRequestedAt, now),
                cancellationToken)
            .ConfigureAwait(false);
        return claimed > 0;
    }

    /// <summary>
    /// Withdraws the snapshot request recorded on <paramref name="rebuild"/>, so <see cref="ClaimCargoEvidenceRequestAsync"/>
    /// finds it due again and the Host asks the vehicle once more on its next message -- inside the same session generation,
    /// which the per-generation throttle would otherwise refuse (review S4: a snapshot that could not settle where the cargo is).
    /// Tracked; the caller saves. The one way a request is withdrawn: the engine calls it, and the Host-side case that pins
    /// "the second request is really sent" calls the same method, so the two sides cannot come to disagree about which columns
    /// make a request due. The request still goes out only where the claim is made, past the handshake.
    /// </summary>
    public static void WithdrawCargoEvidenceRequest(OwnOrderRebuildRow rebuild)
    {
        ArgumentNullException.ThrowIfNull(rebuild);
        rebuild.CargoEvidenceRequestedGeneration = null;
        rebuild.CargoEvidenceRequestedWhileReady = false;
    }

    /// <summary>
    /// Whether a problem from <paramref name="again"/> repeats one from <paramref name="first"/> (REQ-0361): after a
    /// cancellation only another cancellation or deletion does; after a cleared fault, anything does -- another FAILED, which
    /// is recorded once it is cleared, or a cancellation.
    /// </summary>
    private static bool CountsAsAgain(string first, string again) =>
        first != OwnOrderRebuildSources.CancelledInRiot || again == OwnOrderRebuildSources.CancelledInRiot;

    /// <summary>
    /// The record the stop is waiting on: its ended order's, while that is still to be rebuilt; the one whose new order the
    /// stop already points at and which is not yet confirmed, or which FAILED before it was; or a stopped one, whichever of the
    /// two orders the stop points at -- a rebuild stopped because its new order ended before it was confirmed leaves the stop
    /// on that new order. A confirmed rebuild is not returned: the stop then waits on its new order like on any other. Nor is
    /// an ended one: the record that took over from it is.
    /// </summary>
    public static Task<OwnOrderRebuildRow?> ForStopAsync(
        ControlServerDbContext dbContext,
        JourneyStopRow stop,
        CancellationToken cancellationToken) =>
        dbContext.OwnOrderRebuilds.SingleOrDefaultAsync(
            row => row.StopId == stop.StopId &&
                   ((row.EndedUpperId == stop.UpperId && row.State == OwnOrderRebuildStates.Pending) ||
                    (row.NewUpperId == stop.UpperId &&
                     (row.State == OwnOrderRebuildStates.Ordering || row.State == OwnOrderRebuildStates.Failed)) ||
                    (row.State == OwnOrderRebuildStates.Stopped &&
                     (row.EndedUpperId == stop.UpperId || row.NewUpperId == stop.UpperId))),
            cancellationToken);
}
