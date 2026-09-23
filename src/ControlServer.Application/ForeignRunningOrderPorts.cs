namespace ControlServer.Application;

/// <summary>
/// One order as RIoT's by-state order listing reports it (<c>GET /api/order/v1/orderRecord</c>), with exactly the fields
/// control-server#330 needs to tell whose order it is and which vehicle it is running on.
/// </summary>
/// <param name="OrderId">RIoT's string <c>orderId</c> -- what the command endpoint addresses (BC-ORDER-005).</param>
/// <param name="UpperId">The creator's own identity for the order, when it gave one. Only ever an auxiliary hint here.</param>
/// <param name="OrderState">RIoT's <c>orderState</c> (<see cref="RiotOrderState"/>); null when RIoT did not say.</param>
/// <param name="AppointVehicleKey">The vehicle the creator asked for. Not evidence of where the order runs.</param>
/// <param name="ExecuteVehicleKey">
/// The vehicle RIoT has bound the order to, or RIoT's <c>"--"</c> placeholder before it has (BC-ORDER-012, BC-ORDER-013).
/// </param>
public sealed record RiotListedOrder(
    string OrderId,
    string? UpperId,
    int? OrderState,
    string? AppointVehicleKey,
    string? ExecuteVehicleKey);

/// <summary>
/// Every order RIoT holds in states 1, 3, 7 and 9, or the admission that they could not all be read.
/// </summary>
/// <remarks>
/// <see cref="IsComplete"/> is false when RIoT could not be asked, or answered with a page that does not cover every record.
/// An incomplete listing is not "no order": nothing may be concluded from what it leaves out.
/// </remarks>
public sealed record RiotUnfinishedOrderListing(
    bool IsComplete,
    IReadOnlyList<RiotListedOrder> Orders,
    DateTimeOffset ObservedAt);

/// <summary>
/// One order's state read by its RIoT <c>orderId</c> (<c>detailByOrderId</c>). <see cref="OrderState"/> is null when the read
/// did not answer; that is never read as an ending.
/// </summary>
public sealed record RiotOrderStateReading(
    string OrderId,
    int? OrderState,
    DateTimeOffset ObservedAt);

/// <summary>
/// The two read-only order queries control-server#330 identifies foreign running orders with: the by-state listing and the
/// by-orderId detail, both approved observation calls (REQ-0146; allowlist section 1.1). Neither throws for a RIoT failure.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why two reads and not one.</b> The listing is the only read that carries an order's executing vehicle, so it is what
/// "running on our vehicle" and the re-read before a cancel are judged on. The detail read carries only the state, but it
/// answers for an order that has left the listing -- which is exactly the question after a cancel: has it ended, and in
/// which state (REQ-0164: "经回查确认已明确终结").
/// </para>
/// </remarks>
public interface IRiotOrderListingFacts
{
    Task<RiotUnfinishedOrderListing> ListUnfinishedOrdersAsync(CancellationToken cancellationToken);

    Task<RiotOrderStateReading> ReadOrderStateAsync(string orderId, CancellationToken cancellationToken);
}
