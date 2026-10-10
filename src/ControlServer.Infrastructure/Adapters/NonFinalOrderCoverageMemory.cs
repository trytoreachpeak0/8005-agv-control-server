using RIoT.Sdk.Core;

namespace ControlServer.Infrastructure.Adapters;

/// <summary>
/// The last non-final order listing that added up, and when the read that produced it started (control-server#573).
/// </summary>
/// <remarks>
/// A singleton, because the movement gateway is scoped: every complete listing any caller reads refreshes it, and only the
/// onboard projection ever reads it back (<see cref="ControlServer.Application.IOnboardVehicleSafetyProjection"/>).
/// </remarks>
public sealed class NonFinalOrderCoverageMemory
{
    private RememberedNonFinalOrders? current;

    public void Remember(DateTimeOffset readStartedAt, IReadOnlyList<OrderStateRecord> records) =>
        current = new RememberedNonFinalOrders(readStartedAt, records);

    public RememberedNonFinalOrders? Recall() => current is null ? null : null;
}

/// <summary>A complete non-final order listing and the moment the read that produced it started.</summary>
public sealed record RememberedNonFinalOrders(DateTimeOffset ReadStartedAt, IReadOnlyList<OrderStateRecord> Records);
