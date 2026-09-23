using ControlServer.Application;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Host.Runtime.ForeignOrders;

/// <summary>
/// What control-server#330 shares between the supervisor that handles foreign running orders, the journey runtime that holds
/// their vehicles, and the dashboard that shows them.
/// </summary>
public static class ForeignRunningOrders
{
    /// <summary>
    /// The prefix of every <c>upperId</c> this server creates orders under (<c>JourneyPlanBuilder</c>'s pickup and gate legs,
    /// <c>OwnOrderRebuilds</c>' rebuilt legs). An auxiliary hint only (REQ-0164: "upperId 的形态只作辅助，不能单独作判据"): it can
    /// only make an order unprovable, never prove it this server's or foreign. <c>ForeignRunningOrderTests</c> pins that the
    /// upperIds this server creates do start with it.
    /// </summary>
    public const string OwnUpperIdPrefix = "W2G-";

    /// <summary>
    /// The dashboard code of a vehicle held while this server cancels a proven foreign order running on it.
    /// </summary>
    public const string CancellingReason = "FOREIGN_RUNNING_ORDER_CANCELLING";

    /// <summary>The cancel went out and the order is still running: held and alarmed for a person, not cancelled again.</summary>
    public const string StillRunningAfterCancelReason = "FOREIGN_RUNNING_ORDER_STILL_RUNNING_AFTER_CANCEL";

    /// <summary>Whether this server created the running order cannot be proven: held and alarmed, never cancelled.</summary>
    public const string OwnershipUnprovenReason = "FOREIGN_RUNNING_ORDER_OWNERSHIP_UNPROVEN";

    /// <summary>Proven foreign, but this deployment is not authorized to cancel it: held and alarmed for a person.</summary>
    public const string CancelNotAuthorizedReason = "FOREIGN_RUNNING_ORDER_CANCEL_NOT_AUTHORIZED";

    /// <summary>Gone from the running listing without RIoT reading it back ended: held, and a person has to find out why.</summary>
    public const string UnsettledReason = "FOREIGN_RUNNING_ORDER_UNSETTLED";

    /// <summary>The reason the cancel is issued with, carried in its audit attempt's semantic hash and receipt.</summary>
    public const string CancelReason = "FOREIGN_RUNNING_ORDER_ON_MANAGED_VEHICLE";

    /// <summary>RIoT's placeholder in <c>executeVehicleKey</c> before a vehicle is bound (BC-ORDER-012, BC-ORDER-013).</summary>
    private const string UnassignedVehicleKey = "--";

    /// <summary>
    /// Whether RIoT shows <paramref name="order"/> running with its executing vehicle: EXECUTING (3), PAUSED/HELD (7) or HANG (9),
    /// with a real <c>executeVehicleKey</c>. A QUEUEING order (1) is never running, whatever keys it carries: the user's rule
    /// is that a queued order does not show which vehicle it will run on, and the appointed key is not that (BC-ORDER-013).
    /// </summary>
    public static bool IsRunningWithItsVehicle(RiotListedOrder order)
    {
        ArgumentNullException.ThrowIfNull(order);
        return order.OrderState is RiotOrderState.Executing or RiotOrderState.Paused or RiotOrderState.Hang &&
               !string.IsNullOrWhiteSpace(order.ExecuteVehicleKey) &&
               !string.Equals(order.ExecuteVehicleKey.Trim(), UnassignedVehicleKey, StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether <paramref name="orderState"/> is an explicit ending: CANCELLED (2), FAILED (4), SUCCESS (5) or DELETED (6).
    /// SUSPENDED (8) is not one -- REQ-0164 counts it as occupying the vehicle -- and neither is a state RIoT did not report.
    /// </summary>
    public static bool IsExplicitlyEnded(int? orderState) => orderState is
        RiotOrderState.Cancelled or RiotOrderState.Failed or RiotOrderState.Success or RiotOrderState.Deleted;

    /// <summary>The dashboard code for a row in one of the holding states, or null when the row holds nothing.</summary>
    public static string? ReasonFor(string state) => state switch
    {
        ForeignRiotOrderStates.Detected or ForeignRiotOrderStates.CancelDecided or ForeignRiotOrderStates.CancelSent =>
            CancellingReason,
        ForeignRiotOrderStates.StillRunningAfterCancel => StillRunningAfterCancelReason,
        ForeignRiotOrderStates.HeldUnproven => OwnershipUnprovenReason,
        ForeignRiotOrderStates.HeldCancelNotAuthorized => CancelNotAuthorizedReason,
        ForeignRiotOrderStates.Unsettled => UnsettledReason,
        _ => null,
    };

    /// <summary>
    /// The vehicles (by <c>agvId</c>) a foreign running order holds right now: no new dispatch, no appended demand, and no
    /// unknown on their session is "explained" by this server's own order (REQ-0164; control-server#330).
    /// </summary>
    public static async Task<HashSet<string>> HeldAgvIdsAsync(
        ControlServerDbContext dbContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        string[] holding = [.. ForeignRiotOrderStates.Holding];
        return new HashSet<string>(
            await dbContext.ForeignRiotOrders.AsNoTracking()
                .Where(row => holding.Contains(row.State))
                .Select(row => row.AgvId)
                .ToArrayAsync(cancellationToken).ConfigureAwait(false),
            StringComparer.Ordinal);
    }
}
