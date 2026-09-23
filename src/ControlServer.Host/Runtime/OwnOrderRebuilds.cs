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
/// <b>The third guard is decided here</b>, when the ending is recorded: a demand whose previous rebuild was confirmed within
/// <see cref="JourneyRuntimeOptions.OwnOrderRebuildRepeatWindow"/> before this ending is not rebuilt again, and the row is
/// staged already <see cref="OwnOrderRebuildStates.Stopped"/>. The window runs from the previous rebuild's confirmation to
/// this ending: "the rebuilt order ended again soon after it was built", which is what the user called someone really
/// meaning it to stop.
/// </para>
/// </remarks>
internal static class OwnOrderRebuilds
{
    /// <summary>Why a rebuild was not made: the demand's rebuilt order ended again within the window (the third guard).</summary>
    public const string EndedAgainWithinWindow = "REBUILT_ORDER_ENDED_AGAIN_WITHIN_WINDOW";

    /// <summary>Why a rebuild was not made: the new order ended in RIoT before it was ever confirmed -- a second ending.</summary>
    public const string EndedBeforeConfirmation = "REBUILT_ORDER_ENDED_BEFORE_CONFIRMATION";

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

        // The ordinal only has to keep this journey's rebuilt upperIds apart; the row keeps the value it was given.
        int ordinal = await dbContext.OwnOrderRebuilds
            .CountAsync(row => row.JourneyId == runtime.JourneyId, cancellationToken).ConfigureAwait(false) + 1;
        DateTimeOffset since = incidentAt - options.OwnOrderRebuildRepeatWindow;
        // Compared in memory: SQLite cannot order or compare DateTimeOffset columns in the store.
        bool rebuiltRecently = (await dbContext.OwnOrderRebuilds.AsNoTracking()
                .Where(row => row.DemandId == runtime.DemandId && row.State == OwnOrderRebuildStates.Rebuilt)
                .Select(row => row.RebuiltAt)
                .ToArrayAsync(cancellationToken).ConfigureAwait(false))
            .Any(rebuiltAt => rebuiltAt is { } at && at >= since && at <= incidentAt);

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

    /// <summary>
    /// The record the stop is waiting on: its ended order's, while that is still to be rebuilt or was stopped, or the one
    /// whose new order the stop already points at and which is not yet confirmed. A confirmed rebuild is not returned -- the
    /// stop then waits on its new order like on any other.
    /// </summary>
    public static Task<OwnOrderRebuildRow?> ForStopAsync(
        ControlServerDbContext dbContext,
        JourneyStopRow stop,
        CancellationToken cancellationToken) =>
        dbContext.OwnOrderRebuilds.SingleOrDefaultAsync(
            row => row.StopId == stop.StopId &&
                   ((row.EndedUpperId == stop.UpperId &&
                     (row.State == OwnOrderRebuildStates.Pending || row.State == OwnOrderRebuildStates.Stopped)) ||
                    (row.NewUpperId == stop.UpperId && row.State == OwnOrderRebuildStates.Ordering)),
            cancellationToken);
}
