using System.Globalization;

namespace ControlServer.Application;

// control-server#616: every upperId (and orderName, which is the same string) this server sends RIoT is built here, so its
// length can be bounded and tested in one place.

/// <summary>The upperIds this server creates RIoT orders under, and the longest one RIoT is known to accept.</summary>
public static class RiotUpperIds
{
    /// <summary>
    /// The longest upperId this server may send. RIoT's real limit is not known; this is the longest upperId RIoT is known to
    /// have accepted: of 3000 orders read on 2026-10-11 (215 with an upperId) none was longer than 52
    /// (<c>W2G-a1000566-0000-0000-0000-000000000001-REBUILD-1-1</c>, created 2026-10-10 20:00:49), while 69 and 71 were refused
    /// with business code <c>00002</c> (control-server#566, evidence <c>cs566b/20261011-00002-investigation</c>).
    /// Raise it only on a RIoT document, its SDK, or a behaviour-lab measurement that names the limit.
    /// </summary>
    public const int MaxLength = 52;

    /// <summary>The prefix every upperId this server creates starts with.</summary>
    public const string Prefix = "W2G-";

    /// <summary>A demand's pickup leg: <c>W2G-{demandId}-PICKUP-{generation}</c>.</summary>
    public static string Pickup(string demandId, long generation) =>
        Prefix + demandId + "-PICKUP-" + generation.ToString(CultureInfo.InvariantCulture);

    /// <summary>A demand's gate leg: <c>W2G-{demandId}-GATE-{generation}</c>.</summary>
    public static string Gate(string demandId, long generation) =>
        Prefix + demandId + "-GATE-" + generation.ToString(CultureInfo.InvariantCulture);

    /// <summary>A demand's rebuilt leg: <c>W2G-{demandId}-REBUILD-{generation}-{ordinal}</c>.</summary>
    public static string Rebuild(string demandId, long generation, int ordinal) =>
        Prefix + demandId + "-REBUILD-" + generation.ToString(CultureInfo.InvariantCulture) + "-" +
        ordinal.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// A journey without a demand, from what follows its journey id's prefix (<c>{vehicleKey}:{yyyyMMddTHHmmssfffZ}</c>):
    /// <c>W2G-{kind}-{vehicleKey}-{instant}</c>.
    /// </summary>
    public static string VehicleJourney(string kind, string vehicleAndInstant)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(vehicleAndInstant);
        return Prefix + kind + "-" + vehicleAndInstant.Replace(':', '-');
    }
}
