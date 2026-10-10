using RIoT.Sdk.Core;

namespace ControlServer.Infrastructure.Adapters;

/// <summary>
/// The last non-final order listing that added up, and when the read that produced it started (control-server#573).
/// </summary>
/// <remarks>
/// <para>
/// A singleton, because the movement gateway is scoped: every complete listing any caller reads refreshes it, and only the
/// onboard projection ever reads it back (<see cref="ControlServer.Application.IOnboardVehicleSafetyProjection"/>).
/// </para>
/// <para>
/// It only moves forward. Reads overlap -- the onboard poll and the journey runtime read the same listing -- and one that
/// started earlier can finish later; it must not put an older listing back, because the carry-over is measured from the
/// remembered start.
/// </para>
/// </remarks>
public sealed class NonFinalOrderCoverageMemory
{
    private RememberedNonFinalOrders? current;

    public void Remember(DateTimeOffset readStartedAt, IReadOnlyList<OrderStateRecord> records)
    {
        RememberedNonFinalOrders candidate = new(readStartedAt, records);
        while (true)
        {
            RememberedNonFinalOrders? seen = Volatile.Read(ref current);
            if (seen is not null && seen.ReadStartedAt >= readStartedAt)
            {
                return;
            }
            if (ReferenceEquals(Interlocked.CompareExchange(ref current, candidate, seen), seen))
            {
                return;
            }
        }
    }

    public RememberedNonFinalOrders? Recall() => Volatile.Read(ref current);
}

/// <summary>A complete non-final order listing and the moment the read that produced it started.</summary>
public sealed record RememberedNonFinalOrders(DateTimeOffset ReadStartedAt, IReadOnlyList<OrderStateRecord> Records);
