using System.Globalization;
using System.Security.Cryptography;
using System.Text;

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

    /// <summary>A demand's pickup leg: <c>W2G-{demandId}-PICKUP-{generation}</c>, 49 characters at generation 1.</summary>
    public static string Pickup(string demandId, long generation) =>
        Bounded(Prefix + demandId + "-PICKUP-" + generation.ToString(CultureInfo.InvariantCulture));

    /// <summary>A demand's gate leg: <c>W2G-{demandId}-GATE-{generation}</c>, 47 characters at generation 1.</summary>
    public static string Gate(string demandId, long generation) =>
        Bounded(Prefix + demandId + "-GATE-" + generation.ToString(CultureInfo.InvariantCulture));

    /// <summary>A demand's rebuilt leg: <c>W2G-{demandId}-REBUILD-{generation}-{ordinal}</c>, 52 characters at 1-1.</summary>
    public static string Rebuild(string demandId, long generation, int ordinal) =>
        Bounded(Prefix + demandId + "-REBUILD-" + generation.ToString(CultureInfo.InvariantCulture) + "-" +
                ordinal.ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// A journey without a demand, from what follows its journey id's prefix (<c>{vehicleKey}:{yyyyMMddTHHmmssfffZ}</c>):
    /// <c>W2G-{kind}-{vehicle tag}-{instant}</c>, 39 characters for <c>CHARGE</c> and 37 for <c>IDLE</c>.
    /// </summary>
    /// <remarks>
    /// The device key is not written out: a real one is 40 characters (<c>BROKERX-</c> and 32 hex digits), which made these
    /// upperIds 69 to 71 long. <see cref="VehicleTag"/> stands in for it. Which vehicle the order is for is on the order itself
    /// (<c>appointVehicleKey</c>), and this server knows its own orders by the upperId stored on the intent, never by parsing one.
    /// </remarks>
    public static string VehicleJourney(string kind, string vehicleAndInstant)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(vehicleAndInstant);
        int separator = vehicleAndInstant.LastIndexOf(':');
        if (separator <= 0 || separator == vehicleAndInstant.Length - 1)
        {
            throw new ArgumentException($"'{vehicleAndInstant}' is not '{{vehicleKey}}:{{instant}}'.", nameof(vehicleAndInstant));
        }
        return Bounded(Prefix + kind + "-" + VehicleTag(vehicleAndInstant[..separator]) + "-" + vehicleAndInstant[(separator + 1)..]);
    }

    /// <summary>
    /// Eight lowercase hex digits of the device key's SHA-256. Two vehicles share one with a chance of about 1 in 2^32 per pair,
    /// and their upperIds would then still differ unless both committed in the same millisecond.
    /// </summary>
    public static string VehicleTag(string vehicleKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vehicleKey);
        return Sha256Hex(vehicleKey)[..8];
    }

    /// <summary>
    /// <paramref name="upperId"/> as it is when it fits <see cref="MaxLength"/>; otherwise <c>W2G-H-</c> and 32 hex digits of its
    /// SHA-256 (38 characters). The readable shapes fit at every value in use; this is what keeps a generation, a rebuild ordinal
    /// or a clearance attempt that runs into many digits from producing an upperId RIoT refuses. The same input always gives the
    /// same upperId, so a retry or a restart never changes it (REQ-0283).
    /// </summary>
    public static string Bounded(string upperId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(upperId);
        return upperId.Length <= MaxLength ? upperId : Prefix + "H-" + Sha256Hex(upperId)[..32];
    }

    private static string Sha256Hex(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
