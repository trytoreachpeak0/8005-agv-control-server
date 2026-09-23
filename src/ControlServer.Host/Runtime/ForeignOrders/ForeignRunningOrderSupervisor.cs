using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime.Fleet;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ControlServer.Host.Runtime.ForeignOrders;

/// <summary>
/// Finds orders RIoT shows running on this server's vehicles that this server did not create, cancels the proven ones once,
/// and holds the vehicle until RIoT reads them back ended (control-server#330; REQ-0148 and REQ-0164 as revised in
/// requirements baseline v1.5.0).
/// </summary>
/// <remarks>
/// <para>
/// <b>Only an order running on one of our vehicles is looked at.</b> RIoT holds other areas' orders for other vehicles, and an
/// order still queueing, or not yet queued, does not show which vehicle it will run on; the user's rule is that none of those
/// is read, alarmed or touched. "Running" is EXECUTING, PAUSED/HELD or HANG with a real <c>executeVehicleKey</c>
/// (<see cref="ForeignRunningOrders.IsRunningWithItsVehicle"/>); "ours" is a vehicle of the <see cref="VehicleRoster"/> whose
/// lifecycle is not archived -- its current <c>RiotVehicleBinding</c>, by <c>deviceKey</c>, never by name.
/// </para>
/// <para>
/// <b>Whose order it is rests on this server's own records.</b> An <c>OrderIntent</c> naming the order, by <c>upperId</c> or by
/// RIoT's <c>orderId</c>, makes it this server's. With no intent, an order command audit of this server's naming it, or an
/// <c>upperId</c> shaped like this server's (<see cref="ForeignRunningOrders.OwnUpperIdPrefix"/>), leaves it unprovable:
/// held and alarmed, never cancelled. Only an order none of the three points back to is proven foreign.
/// </para>
/// <para>
/// <b>The cancel, and only the cancel, once.</b> Before it goes out the listing is read again, and the order has to be still
/// running on the same vehicle of ours. The decision is saved (<see cref="ForeignRiotOrderStates.CancelDecided"/>), then the
/// audit attempt is armed -- the point from which the cancel "may have gone out" -- then the call is made. An armed attempt for
/// the order, found by any later round or after a restart, means no second cancel, whatever the order does next: a cancel that
/// did not end it within <see cref="CancelSettleTime"/> is handed to a person
/// (<see cref="ForeignRiotOrderStates.StillRunningAfterCancel"/>). Nothing else -- no HELD, no CONTINUE -- is ever sent to a
/// foreign order (REQ-0148).
/// </para>
/// <para>
/// <b>Only where the deployment is authorized to cancel</b> (<see cref="RiotForeignOrderCancelOptions"/>, closed by default).
/// Where it is not, a proven foreign order is recognised, recorded, alarmed and holds its vehicle all the same
/// (<see cref="ForeignRiotOrderStates.HeldCancelNotAuthorized"/>), and nothing is sent -- the rule before REQ-0164's revision.
/// The gate is read after the armed-attempt check: a cancel that may already have gone out is never re-decided by it.
/// </para>
/// <para>
/// <b>The 0/1 gate is not relaxed.</b> While a row is in one of <see cref="ForeignRiotOrderStates.Holding"/> its vehicle takes
/// no new dispatch or appended demand, and its session's unknown is not this server's own order
/// (<see cref="ForeignRunningOrders.HeldAgvIdsAsync"/>). A row stops holding only when RIoT reads the order back in an explicit
/// ending -- CANCELLED, FAILED, SUCCESS or DELETED; SUSPENDED is not one -- or shows it running somewhere other than a vehicle
/// of ours. An order this server cancelled ends in CANCELLED (BC-ORDER-003 for EXECUTING, BC-ORDER-006 for HELD); one cancelled
/// on the vehicle itself goes to HANG (BC-ORDER-015), which is not an ending. What an API cancel does to a HANG order the lab
/// has not recorded, so nothing here assumes it works: it is read back like any other. An order that leaves the running
/// listing without being read back ended -- SUSPENDED, or not found at all -- goes to a person after
/// <see cref="CancelSettleTime"/> (<see cref="ForeignRiotOrderStates.Unsettled"/>) and keeps holding: there is no release but a
/// confirmed ending, and no manual one in this server.
/// </para>
/// <para>
/// <b>An ending here is never this server's own order ending.</b> Nothing a foreign order does reaches a journey: the journey
/// runtime reads only its own stops' orders, so the cancel neither rebuilds anything nor raises control-server#318's
/// "cancelled in RIoT" alarm (REQ-0360's last sentence).
/// </para>
/// </remarks>
public sealed class ForeignRunningOrderSupervisor(
    ControlServerDbContext dbContext,
    IRiotOrderListingFacts orders,
    IRiotOrderCommandGateway commands,
    IRiotOrderCommandAuditStore audit,
    VehicleRoster roster,
    IOptions<RiotForeignOrderCancelOptions> cancelGate,
    TimeProvider timeProvider,
    ILogger<ForeignRunningOrderSupervisor> logger)
{
    /// <summary>
    /// How long after its cancel went out an order not yet read back ended is handed to a person. Long enough for RIoT to move
    /// an order it accepted a cancel for (the lab saw it within one read), short enough that a cancel RIoT ignored is on the
    /// dashboard within seconds.
    /// </summary>
    public static readonly TimeSpan CancelSettleTime = TimeSpan.FromSeconds(10);

    private static readonly Action<ILogger, string, string?, string, string, int?, string, Exception?> LogForeignOrderFound =
        LoggerMessage.Define<string, string?, string, string, int?, string>(
            LogLevel.Warning,
            new EventId(2180, nameof(LogForeignOrderFound)),
            "RIoT order {OrderId} (upperId {UpperId}) is running on vehicle {AgvId} ({DeviceKey}) in state {OrderState} and was " +
            "not created by this server ({Basis}). The vehicle takes no new work until it has ended.");

    private static readonly Action<ILogger, string, string?, string, string, int?, string, Exception?> LogForeignOrderUnproven =
        LoggerMessage.Define<string, string?, string, string, int?, string>(
            LogLevel.Error,
            new EventId(2181, nameof(LogForeignOrderUnproven)),
            "RIoT order {OrderId} (upperId {UpperId}) is running on vehicle {AgvId} ({DeviceKey}) in state {OrderState}, and " +
            "whether this server created it cannot be proven ({Basis}). It is not cancelled; the vehicle takes no new work until " +
            "it has ended. A person has to look at it in RIoT.");

    private static readonly Action<ILogger, string, string, DateTimeOffset, string, string, Exception?> LogForeignOrderCancelSent =
        LoggerMessage.Define<string, string, DateTimeOffset, string, string>(
            LogLevel.Warning,
            new EventId(2182, nameof(LogForeignOrderCancelSent)),
            "The cancel of foreign order {OrderId} on vehicle {AgvId} went out at {SentAt}: the call answered {Disposition}, the " +
            "order reads back {ReadBack}. No second cancel is ever sent to it.");

    private static readonly Action<ILogger, string, string, int?, DateTimeOffset, string, Exception?> LogForeignOrderEnded =
        LoggerMessage.Define<string, string, int?, DateTimeOffset, string>(
            LogLevel.Warning,
            new EventId(2183, nameof(LogForeignOrderEnded)),
            "Foreign order {OrderId} on vehicle {AgvId} reads back ended in state {OrderState} at {EndedAt} (cancel result " +
            "{CancelResult}); the vehicle is no longer held for it.");

    private static readonly Action<ILogger, string, string, DateTimeOffset, Exception?> LogForeignOrderStillRunning =
        LoggerMessage.Define<string, string, DateTimeOffset>(
            LogLevel.Error,
            new EventId(2184, nameof(LogForeignOrderStillRunning)),
            "Foreign order {OrderId} on vehicle {AgvId} has not ended since its cancel went out at {SentAt}. It is not cancelled " +
            "again; the vehicle stays held. A person has to end the order in RIoT or at the vehicle.");

    private static readonly Action<ILogger, string, string, string, Exception?> LogForeignOrderLeftVehicle =
        LoggerMessage.Define<string, string, string>(
            LogLevel.Warning,
            new EventId(2185, nameof(LogForeignOrderLeftVehicle)),
            "Foreign order {OrderId} is no longer running on vehicle {AgvId} ({Where}); nothing is sent to it, and the vehicle " +
            "is no longer held for it.");

    private static readonly Action<ILogger, string, string, string, Exception?> LogForeignOrderCancelNotAuthorized =
        LoggerMessage.Define<string, string, string>(
            LogLevel.Error,
            new EventId(2186, nameof(LogForeignOrderCancelNotAuthorized)),
            "Foreign order {OrderId} is running on vehicle {AgvId} ({DeviceKey}), and this deployment is not authorized to cancel " +
            "it (RiotForeignOrderCancel:Enabled is false). It is not cancelled; the vehicle takes no new work until it has " +
            "ended. A person has to end it in RIoT or at the vehicle.");

    private static readonly Action<ILogger, string, string, string, DateTimeOffset, Exception?> LogForeignOrderUnsettled =
        LoggerMessage.Define<string, string, string, DateTimeOffset>(
            LogLevel.Error,
            new EventId(2187, nameof(LogForeignOrderUnsettled)),
            "Foreign order {OrderId} on vehicle {AgvId} has left RIoT's running listing, but RIoT reads it back as {ReadBack}, " +
            "not as ended, since {LastSeenRunningAt}. The vehicle stays held; a person has to find out in RIoT what became of it.");

    /// <summary>One round: follow what is known, recognise what is new, and take each proven foreign order one step further.</summary>
    public async Task SuperviseAsync(CancellationToken cancellationToken)
    {
        Dictionary<string, FleetVehicle> ours = await OurVehiclesAsync(cancellationToken).ConfigureAwait(false);
        RiotUnfinishedOrderListing listing = await orders.ListUnfinishedOrdersAsync(cancellationToken).ConfigureAwait(false);
        string[] listedIds = [.. listing.Orders.Select(order => order.OrderId)];
        // Every row still in play, and any ended one whose order RIoT lists again. An ended row is otherwise history: it is
        // not read back every round.
        List<ForeignRiotOrderRow> rows = await dbContext.ForeignRiotOrders
            .Where(row => row.State != ForeignRiotOrderStates.Ended || listedIds.Contains(row.RiotOrderId))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        // An incomplete listing concludes nothing: no row is ended or released on what it leaves out, and nothing new is
        // recognised from it. Rows already holding go on holding.
        if (listing.IsComplete)
        {
            foreach (ForeignRiotOrderRow row in rows)
            {
                await FollowAsync(row, listing, ours, cancellationToken).ConfigureAwait(false);
            }

            await RecogniseAsync(rows, listing, ours, cancellationToken).ConfigureAwait(false);
            // A round with nothing foreign on our vehicles -- nearly every round -- writes nothing.
            if (dbContext.ChangeTracker.HasChanges())
            {
                await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        bool mayCancel = cancelGate.Value.Enabled;
        foreach (ForeignRiotOrderRow row in rows.Where(row =>
                     row.Ownership == ForeignRiotOrderOwnership.Foreign &&
                     (row.State is ForeignRiotOrderStates.Detected or ForeignRiotOrderStates.CancelDecided ||
                      (mayCancel && row.State == ForeignRiotOrderStates.HeldCancelNotAuthorized))))
        {
            await CancelAsync(row, ours, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The vehicles this server manages now, by RIoT <c>deviceKey</c>: the roster's, less any whose lifecycle is archived --
    /// an archived vehicle's binding is not current (REQ-0164: "deviceKey 属于 8005 当前有效的 RiotVehicleBinding").
    /// </summary>
    private async Task<Dictionary<string, FleetVehicle>> OurVehiclesAsync(CancellationToken cancellationToken)
    {
        HashSet<string> archived = new(
            await dbContext.Set<AgvLifecycleRow>().AsNoTracking()
                .Where(row => row.Archived)
                .Select(row => row.AgvId)
                .ToArrayAsync(cancellationToken).ConfigureAwait(false),
            StringComparer.Ordinal);
        return roster.Vehicles
            .Where(vehicle => !archived.Contains(vehicle.AgvId))
            .ToDictionary(vehicle => vehicle.VehicleKey, StringComparer.Ordinal);
    }

    /// <summary>Which vehicle of ours <paramref name="order"/> is running on, or null when it is not running on one.</summary>
    private static FleetVehicle? RunningOnOurs(RiotListedOrder? order, Dictionary<string, FleetVehicle> ours) =>
        order is not null && ForeignRunningOrders.IsRunningWithItsVehicle(order) &&
        ours.TryGetValue(order.ExecuteVehicleKey!.Trim(), out FleetVehicle? vehicle)
            ? vehicle
            : null;

    /// <summary>What a complete listing says about an order already on record. Staged; the caller saves.</summary>
    private async Task FollowAsync(
        ForeignRiotOrderRow row,
        RiotUnfinishedOrderListing listing,
        Dictionary<string, FleetVehicle> ours,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        RiotListedOrder? order = listing.Orders.FirstOrDefault(
            listed => string.Equals(listed.OrderId, row.RiotOrderId, StringComparison.Ordinal));
        if (RunningOnOurs(order, ours) is { } vehicle)
        {
            row.LastSeenRunningAt = now;
            row.AgvId = vehicle.AgvId;
            row.DeviceKey = vehicle.VehicleKey;
            if (row.State is ForeignRiotOrderStates.LeftVehicle or ForeignRiotOrderStates.Ended or ForeignRiotOrderStates.Unsettled)
            {
                // Back on a vehicle of ours, or back in the running listing. A cancel that already went out is not sent again.
                row.State = row.CancelCommandAuditId is not null
                    ? ForeignRiotOrderStates.CancelSent
                    : row.Ownership == ForeignRiotOrderOwnership.Foreign
                        ? ForeignRiotOrderStates.Detected
                        : ForeignRiotOrderStates.HeldUnproven;
                row.EndedAt = null;
                row.EndedOrderState = null;
                row.UpdatedAt = now;
                if (row.CancelCommandAuditId is null)
                {
                    LogFound(row, order!.OrderState);
                }
            }

            HandOverIfTheCancelDidNotTake(row, now);
            return;
        }

        if (row.State is ForeignRiotOrderStates.Ended or ForeignRiotOrderStates.LeftVehicle)
        {
            return;
        }

        if (order is null)
        {
            // Not among the unfinished orders any more: whether it ended is RIoT's to say, by the order itself.
            RiotOrderStateReading reading = await orders.ReadOrderStateAsync(row.RiotOrderId, cancellationToken)
                .ConfigureAwait(false);
            if (ForeignRunningOrders.IsExplicitlyEnded(reading.OrderState))
            {
                await EndAsync(row, reading.OrderState!.Value, cancellationToken).ConfigureAwait(false);
                return;
            }

            // Neither running nor ended -- SUSPENDED, or RIoT cannot find it (deleted, for one). Nothing releases the vehicle but
            // a confirmed ending (REQ-0164), so past the settle time it goes to a person rather than waiting unseen (independent
            // review S1).
            if (row.State != ForeignRiotOrderStates.Unsettled && now - row.LastSeenRunningAt >= CancelSettleTime)
            {
                row.State = ForeignRiotOrderStates.Unsettled;
                row.UpdatedAt = now;
                LogForeignOrderUnsettled(
                    logger, row.RiotOrderId, row.AgvId,
                    reading.OrderState?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "UNREAD",
                    row.LastSeenRunningAt, null);
            }

            return;
        }

        // Listed, but queueing, without a vehicle, or on a vehicle that is not ours: not ours to act on.
        row.State = ForeignRiotOrderStates.LeftVehicle;
        row.UpdatedAt = now;
        LogForeignOrderLeftVehicle(logger, row.RiotOrderId, row.AgvId, Where(order, ours), null);
    }

    /// <summary>A cancel that went out and has not ended the order within <see cref="CancelSettleTime"/> goes to a person.</summary>
    private void HandOverIfTheCancelDidNotTake(ForeignRiotOrderRow row, DateTimeOffset now)
    {
        if (row.State != ForeignRiotOrderStates.CancelSent || row.CancelSentAt is not { } sentAt ||
            now - sentAt < CancelSettleTime)
        {
            return;
        }

        row.State = ForeignRiotOrderStates.StillRunningAfterCancel;
        row.CancelResult ??= ForeignRiotOrderCancelResults.StillRunning;
        row.CancelResultAt ??= now;
        row.UpdatedAt = now;
        LogForeignOrderStillRunning(logger, row.RiotOrderId, row.AgvId, sentAt, null);
    }

    /// <summary>Records every order running on a vehicle of ours that no row knows yet and that is not this server's own.</summary>
    private async Task RecogniseAsync(
        List<ForeignRiotOrderRow> rows,
        RiotUnfinishedOrderListing listing,
        Dictionary<string, FleetVehicle> ours,
        CancellationToken cancellationToken)
    {
        HashSet<string> known = new(rows.Select(row => row.RiotOrderId), StringComparer.Ordinal);
        foreach (RiotListedOrder order in listing.Orders)
        {
            if (known.Contains(order.OrderId) || RunningOnOurs(order, ours) is not { } vehicle)
            {
                continue;
            }

            (string? ownership, string basis) = await OwnershipAsync(order, cancellationToken).ConfigureAwait(false);
            if (ownership is null)
            {
                continue;
            }

            DateTimeOffset now = timeProvider.GetUtcNow();
            ForeignRiotOrderRow row = new()
            {
                RiotOrderId = order.OrderId,
                UpperId = order.UpperId,
                AgvId = vehicle.AgvId,
                DeviceKey = vehicle.VehicleKey,
                Ownership = ownership,
                OwnershipBasis = basis,
                State = ownership == ForeignRiotOrderOwnership.Foreign
                    ? ForeignRiotOrderStates.Detected
                    : ForeignRiotOrderStates.HeldUnproven,
                OrderStateAtDetection = order.OrderState,
                DetectedAt = now,
                LastSeenRunningAt = now,
                UpdatedAt = now,
            };
            dbContext.ForeignRiotOrders.Add(row);
            rows.Add(row);
            known.Add(row.RiotOrderId);
            LogFound(row, order.OrderState);
        }
    }

    private void LogFound(ForeignRiotOrderRow row, int? orderState)
    {
        if (row.Ownership == ForeignRiotOrderOwnership.Foreign)
        {
            LogForeignOrderFound(
                logger, row.RiotOrderId, row.UpperId, row.AgvId, row.DeviceKey, orderState, row.OwnershipBasis, null);
        }
        else
        {
            LogForeignOrderUnproven(
                logger, row.RiotOrderId, row.UpperId, row.AgvId, row.DeviceKey, orderState, row.OwnershipBasis, null);
        }
    }

    /// <summary>
    /// Whose <paramref name="order"/> is: null when it is this server's own, else <see cref="ForeignRiotOrderOwnership"/> and
    /// what that rests on.
    /// </summary>
    private async Task<(string? Ownership, string Basis)> OwnershipAsync(
        RiotListedOrder order,
        CancellationToken cancellationToken)
    {
        string orderId = order.OrderId;
        string? upperId = string.IsNullOrWhiteSpace(order.UpperId) ? null : order.UpperId;
        if (await dbContext.OrderIntents.AsNoTracking()
                .AnyAsync(row => row.OrderId == orderId || (upperId != null && row.UpperId == upperId), cancellationToken)
                .ConfigureAwait(false))
        {
            return (null, "ORDER_INTENT");
        }

        // A command this server issued to the order as its own -- this handling's own cancels aside -- means it was once
        // managed here, whatever became of its intent.
        if (await dbContext.RiotOrderCommandAudit.AsNoTracking()
                .Where(row => row.TargetOrderId == orderId || (upperId != null && row.TargetUpperId == upperId))
                .Where(row => !dbContext.ForeignRiotOrders.Any(foreign => foreign.CancelCommandAuditId == row.CommandAuditId))
                .AnyAsync(cancellationToken).ConfigureAwait(false))
        {
            return (ForeignRiotOrderOwnership.Unproven, "COMMAND_AUDIT_WITHOUT_INTENT");
        }

        if (upperId is not null &&
            upperId.TrimStart().StartsWith(ForeignRunningOrders.OwnUpperIdPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return (ForeignRiotOrderOwnership.Unproven, "UPPER_ID_SHAPED_AS_OWN_WITHOUT_INTENT");
        }

        return (ForeignRiotOrderOwnership.Foreign, "NO_INTENT_NO_COMMAND_AUDIT");
    }

    /// <summary>
    /// Takes one proven foreign order from recognised to cancelled: re-read, decide, arm, send, read back. Saves at each point
    /// a crash has to be told apart at.
    /// </summary>
    private async Task CancelAsync(
        ForeignRiotOrderRow row,
        Dictionary<string, FleetVehicle> ours,
        CancellationToken cancellationToken)
    {
        string target = AuditTarget(row);
        // An attempt already armed for this order -- a crash between arming it and recording it here -- may have gone out.
        RiotOrderCommandAttempt? armed = (await audit.ReadAttemptsAsync(
                RiotCommandTypeNames.CancelOrder, target, cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(attempt => string.Equals(attempt.TargetOrderId, row.RiotOrderId, StringComparison.Ordinal));
        if (armed is not null)
        {
            row.State = ForeignRiotOrderStates.CancelSent;
            row.CancelCommandAuditId = armed.CommandAuditId;
            row.CancelSentAt ??= armed.IssuedAt;
            row.UpdatedAt = timeProvider.GetUtcNow();
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        // The cancel gate (independent review M1): a deployment not authorized to cancel only recognises, holds and alarms.
        if (!cancelGate.Value.Enabled)
        {
            if (row.State != ForeignRiotOrderStates.HeldCancelNotAuthorized)
            {
                row.State = ForeignRiotOrderStates.HeldCancelNotAuthorized;
                row.UpdatedAt = timeProvider.GetUtcNow();
                LogForeignOrderCancelNotAuthorized(logger, row.RiotOrderId, row.AgvId, row.DeviceKey, null);
                await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        // The re-read before the cancel: still running, on the same vehicle, and that vehicle still ours.
        RiotUnfinishedOrderListing reread = await orders.ListUnfinishedOrdersAsync(cancellationToken).ConfigureAwait(false);
        if (!reread.IsComplete)
        {
            return;
        }

        RiotListedOrder? order = reread.Orders.FirstOrDefault(
            listed => string.Equals(listed.OrderId, row.RiotOrderId, StringComparison.Ordinal));
        if (RunningOnOurs(order, ours) is { } elsewhere &&
            !string.Equals(elsewhere.VehicleKey, row.DeviceKey, StringComparison.Ordinal))
        {
            // Moved to another vehicle of ours between the two reads: not "the same vehicle", so nothing is sent now. The row
            // follows it -- that vehicle is held from now on -- and the next round reads again before sending.
            row.AgvId = elsewhere.AgvId;
            row.DeviceKey = elsewhere.VehicleKey;
            row.LastSeenRunningAt = timeProvider.GetUtcNow();
            row.UpdatedAt = row.LastSeenRunningAt;
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (RunningOnOurs(order, ours) is null)
        {
            if (order is null)
            {
                RiotOrderStateReading reading = await orders.ReadOrderStateAsync(row.RiotOrderId, cancellationToken)
                    .ConfigureAwait(false);
                if (ForeignRunningOrders.IsExplicitlyEnded(reading.OrderState))
                {
                    await EndAsync(row, reading.OrderState!.Value, cancellationToken).ConfigureAwait(false);
                    await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                }

                // Neither listed nor explicitly ended: nothing can be concluded, and nothing is sent. Asked again next round.
                return;
            }

            row.State = ForeignRiotOrderStates.LeftVehicle;
            row.UpdatedAt = timeProvider.GetUtcNow();
            LogForeignOrderLeftVehicle(logger, row.RiotOrderId, row.AgvId, Where(order, ours), null);
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        // Decided. A crash from here until the attempt is armed leaves a cancel that never went out: the next round reads
        // again and sends it.
        DateTimeOffset decidedAt = timeProvider.GetUtcNow();
        row.State = ForeignRiotOrderStates.CancelDecided;
        row.CancelDecidedAt ??= decidedAt;
        row.LastSeenRunningAt = decidedAt;
        row.UpdatedAt = decidedAt;
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // Armed: from this save on, the cancel may have gone out, and nothing sends another.
        RiotOrderCommandAttempt attempt = await audit.ArmAttemptAsync(
            RiotCommandTypeNames.CancelOrder,
            row.AgvId,
            target,
            row.RiotOrderId,
            SemanticHash(row, target),
            faultGeneration: null,
            timeProvider.GetUtcNow(),
            cancellationToken).ConfigureAwait(false);
        row.State = ForeignRiotOrderStates.CancelSent;
        row.CancelCommandAuditId = attempt.CommandAuditId;
        row.CancelSentAt = attempt.IssuedAt;
        row.UpdatedAt = attempt.IssuedAt;
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        RiotCommandCallResult call = await commands.IssueOrderCommandAsync(
            RiotOrderCommandKind.Cancel, row.RiotOrderId, ForeignRunningOrders.CancelReason, cancellationToken)
            .ConfigureAwait(false);
        row.CancelCallDisposition = call.Disposition.ToString();
        RiotOrderStateReading readBack = await orders.ReadOrderStateAsync(row.RiotOrderId, cancellationToken)
            .ConfigureAwait(false);
        DateTimeOffset now = timeProvider.GetUtcNow();
        await audit.RecordOutcomeAsync(
            attempt.CommandAuditId,
            Outcome(call.Disposition, readBack.OrderState),
            ReceiptJson(call, readBack),
            now,
            cancellationToken).ConfigureAwait(false);
        LogForeignOrderCancelSent(
            logger, row.RiotOrderId, row.AgvId, attempt.IssuedAt, call.Disposition.ToString(),
            readBack.OrderState?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "UNREAD", null);
        if (ForeignRunningOrders.IsExplicitlyEnded(readBack.OrderState))
        {
            await EndAsync(row, readBack.OrderState!.Value, cancellationToken).ConfigureAwait(false);
        }

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// RIoT reads the order back explicitly ended: the row stops holding the vehicle, and a cancel that went out is settled
    /// in the row and in its audit attempt. Staged; the caller saves.
    /// </summary>
    private async Task EndAsync(ForeignRiotOrderRow row, int orderState, CancellationToken cancellationToken)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        row.State = ForeignRiotOrderStates.Ended;
        row.EndedOrderState = orderState;
        row.EndedAt = now;
        row.UpdatedAt = now;
        if (row.CancelCommandAuditId is { } auditId)
        {
            // Rewritten, not only filled in (review L5): a cancel handed to a person and read back cancelled later ends as
            // CANCELLED; that it was handed over is event 2184.
            row.CancelResult = orderState == RiotOrderState.Cancelled
                ? ForeignRiotOrderCancelResults.Cancelled
                : ForeignRiotOrderCancelResults.EndedOtherwise;
            row.CancelResultAt = now;

            RiotOrderCommandAttempt? attempt = (await audit.ReadAttemptsAsync(
                    RiotCommandTypeNames.CancelOrder, AuditTarget(row), cancellationToken).ConfigureAwait(false))
                .FirstOrDefault(item => string.Equals(item.CommandAuditId, auditId, StringComparison.Ordinal));
            if (attempt is { Outcome: not (RiotOrderCommandOutcome.Confirmed or RiotOrderCommandOutcome.Failed) })
            {
                await audit.RecordOutcomeAsync(
                    auditId,
                    Outcome(RiotCommandCallDisposition.Accepted, orderState),
                    attempt.ReceiptJson,
                    now,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        LogForeignOrderEnded(logger, row.RiotOrderId, row.AgvId, orderState, now, row.CancelResult ?? "NONE_SENT", null);
    }

    /// <summary>
    /// What the order's state says about the cancel, the way <c>RiotOrderCommandService.Reconcile</c> reads it for this server's
    /// own orders: CANCELLED confirms it; any other ending means it can no longer do what it was for; a live order leaves it
    /// pending on an accepted call, failed on a refused one, unknown otherwise.
    /// </summary>
    private static RiotOrderCommandOutcome Outcome(RiotCommandCallDisposition disposition, int? orderState) => orderState switch
    {
        RiotOrderState.Cancelled => RiotOrderCommandOutcome.Confirmed,
        _ when ForeignRunningOrders.IsExplicitlyEnded(orderState) => RiotOrderCommandOutcome.Failed,
        null => RiotOrderCommandOutcome.Unknown,
        _ => disposition switch
        {
            RiotCommandCallDisposition.Accepted => RiotOrderCommandOutcome.Pending,
            RiotCommandCallDisposition.Failed => RiotOrderCommandOutcome.Failed,
            _ => RiotOrderCommandOutcome.Unknown,
        },
    };

    /// <summary>
    /// The audit trail's target for the cancel: the order's own <c>upperId</c> when it has one, which is what a person looking
    /// in RIoT sees, else RIoT's <c>orderId</c>. Attempts are matched back by <c>TargetOrderId</c> too, so two foreign orders
    /// sharing an <c>upperId</c> are never taken for one.
    /// </summary>
    private static string AuditTarget(ForeignRiotOrderRow row) =>
        string.IsNullOrWhiteSpace(row.UpperId) ? $"RIOT-ORDER:{row.RiotOrderId}" : row.UpperId;

    private static string Where(RiotListedOrder order, Dictionary<string, FleetVehicle> ours) =>
        !ForeignRunningOrders.IsRunningWithItsVehicle(order)
            ? $"state {order.OrderState?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"} without a vehicle running it"
            : ours.ContainsKey(order.ExecuteVehicleKey!.Trim())
                ? $"running on another vehicle of ours, {order.ExecuteVehicleKey}"
                : $"running on {order.ExecuteVehicleKey}, not a vehicle of ours";

    private static string SemanticHash(ForeignRiotOrderRow row, string target) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(
                '\u001f', RiotCommandTypeNames.CancelOrder, row.AgvId, target, row.RiotOrderId, ForeignRunningOrders.CancelReason))))
            .ToLowerInvariant();

    private static string ReceiptJson(RiotCommandCallResult call, RiotOrderStateReading readBack) =>
        JsonSerializer.Serialize(new
        {
            reason = ForeignRunningOrders.CancelReason,
            call = new
            {
                disposition = call.Disposition.ToString(),
                call.Receipt.Operation,
                call.Receipt.Classification,
                call.Receipt.HttpStatusCode,
                call.Receipt.BusinessCode,
                call.Receipt.FailureCategory,
                observedAt = call.Receipt.ObservedAt,
            },
            readBack = new
            {
                readBack.OrderId,
                readBack.OrderState,
                readBack.ObservedAt,
            },
        });
}
