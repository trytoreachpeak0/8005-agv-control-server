using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ControlServer.Application;
using Microsoft.Extensions.Options;

namespace ControlServer.Host.Runtime.Fleet;

/// <summary>
/// Keeps the stored dispatch policy equal to the configured one, and hands one round the snapshot
/// it decides against.
/// </summary>
/// <remarks>
/// <para>
/// <b>Configuration is the author, the tables are the record.</b> Ticket 06 gave the policy three
/// tables and a whole-policy replace; this is what writes them, following the same shape as the
/// station admission policy the runtime already applies each round — read what is stored, and only
/// write when it differs from what is configured.
/// </para>
/// <para>
/// <b>Difference is decided by content, not by a version an operator has to remember to bump.</b>
/// The stored <c>ConfigurationVersion</c> is a fingerprint of the policy's own content, so a
/// changed roster is always applied and an unchanged one never writes. A version string that has
/// to be maintained by hand fails in the direction where the tables silently disagree with the
/// file the operator is reading.
/// </para>
/// <para>
/// The policy is read once per round and passed down, so every candidate and every vehicle in that
/// round is judged against one consistent configuration.
/// </para>
/// </remarks>
public sealed class VehicleDispatchPolicyAccess(
    IVehicleDispatchPolicyStore store,
    IOptions<JourneyRuntimeOptions> options,
    TimeProvider timeProvider)
{
    private readonly JourneyRuntimeOptions _options = options.Value;

    /// <summary>Applies the configured policy if the stored one differs, and returns the current one.</summary>
    public async Task<VehicleDispatchPolicy> EnsureCurrentAsync(CancellationToken cancellationToken)
    {
        VehicleDispatchPolicy configured = FromConfiguration();
        VehicleDispatchPolicy stored = await store.ReadPolicyAsync(cancellationToken).ConfigureAwait(false);
        if (string.Equals(stored.ConfigurationVersion, configured.ConfigurationVersion, StringComparison.Ordinal))
        {
            return stored;
        }

        await store.ReplacePolicyAsync(configured, timeProvider.GetUtcNow(), cancellationToken)
            .ConfigureAwait(false);
        return configured;
    }

    /// <summary>
    /// Claims this vehicle for one in-flight order, returning false when it already has one.
    /// </summary>
    /// <remarks>
    /// The claim rides the journey's first order and is held for the whole journey: a journey's
    /// second leg is a second <c>OrderIntent</c> for the same vehicle, and having it claim as well
    /// would make the vehicle collide with itself. Uniqueness is decided by the filtered unique
    /// index ticket 06 put on <c>OrderIntents</c>, not by a read followed by a write, so two rounds
    /// racing for one vehicle cannot both win.
    /// </remarks>
    public Task<bool> TryClaimVehicleOccupancyAsync(string upperId, CancellationToken cancellationToken) =>
        store.TryClaimVehicleOccupancyAsync(upperId, timeProvider.GetUtcNow(), cancellationToken);

    /// <summary>Releases the claim, because the journey holding it has finished.</summary>
    public Task ReleaseVehicleOccupancyAsync(string upperId, CancellationToken cancellationToken) =>
        store.ReleaseVehicleOccupancyAsync(upperId, timeProvider.GetUtcNow(), cancellationToken);

    /// <summary>The policy the configuration describes.</summary>
    /// <remarks>
    /// An empty <c>Fleet</c> is the single-vehicle deployment, and its slice comes from the fields
    /// that already describe it — the work types it is allowed and the zone it serves. The
    /// alternative, an empty policy, would block the one configured vehicle from every candidate,
    /// which is fail-closed and also wrong: nothing about that deployment is unconfigured.
    /// </remarks>
    public VehicleDispatchPolicy FromConfiguration()
    {
        FleetVehicleOptions[] fleet = _options.Fleet.Length == 0
            ?
            [
                new FleetVehicleOptions
                {
                    AgvId = _options.AgvId,
                    VehicleKey = _options.VehicleKey,
                    AgvLifecycleGeneration = _options.AgvLifecycleGeneration,
                    AllowedTaskTypes = _options.AllowedWorkTypes,
                    Zones = [_options.DispatchZone],
                }
            ]
            : _options.Fleet;

        List<VehicleDispatchProfile> vehicles = [.. fleet
            .OrderBy(vehicle => vehicle.AgvId, StringComparer.Ordinal)
            .Select(vehicle => new VehicleDispatchProfile(
                vehicle.AgvId,
                vehicle.AllowedTaskTypes.ToHashSet(StringComparer.Ordinal),
                vehicle.RoundTimeoutMilliseconds))];

        Dictionary<string, IReadOnlySet<string>> zoneVehicles = fleet
            .SelectMany(vehicle => vehicle.Zones.Select(zone => (Zone: zone, vehicle.AgvId)))
            .GroupBy(pair => pair.Zone, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlySet<string>)group.Select(pair => pair.AgvId)
                    .ToHashSet(StringComparer.Ordinal),
                StringComparer.Ordinal);

        return new VehicleDispatchPolicy(vehicles, zoneVehicles, Fingerprint(vehicles, zoneVehicles));
    }

    /// <summary>
    /// A content fingerprint of the whole policy.
    /// </summary>
    /// <remarks>
    /// Every field is written length-prefixed, for the reason ticket 06 gave for the edge-group
    /// fingerprint: an agv id, a zone or a task type may contain any character an operator can
    /// type, so any separator can also occur inside a field and let two different policies
    /// normalise onto one string. A length prefix cannot.
    /// </remarks>
    private static string Fingerprint(
        IEnumerable<VehicleDispatchProfile> vehicles,
        IReadOnlyDictionary<string, IReadOnlySet<string>> zoneVehicles)
    {
        StringBuilder builder = new();
        foreach (VehicleDispatchProfile vehicle in vehicles.OrderBy(item => item.AgvId, StringComparer.Ordinal))
        {
            Append(builder, vehicle.AgvId);
            Append(builder, vehicle.RoundTimeoutMilliseconds.ToString(CultureInfo.InvariantCulture));
            foreach (string taskType in vehicle.AllowedTaskTypes.OrderBy(item => item, StringComparer.Ordinal))
            {
                Append(builder, taskType);
            }
        }

        foreach ((string zone, IReadOnlySet<string> agvIds) in zoneVehicles.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            Append(builder, zone);
            foreach (string agvId in agvIds.OrderBy(item => item, StringComparer.Ordinal))
            {
                Append(builder, agvId);
            }
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))
            .ToLowerInvariant();
    }

    private static void Append(StringBuilder builder, string value) =>
        builder.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value);
}
