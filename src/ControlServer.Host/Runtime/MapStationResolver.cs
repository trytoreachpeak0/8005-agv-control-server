using System.Security.Cryptography;
using System.Text;
using ControlServer.Application;
using ControlServer.Domain;

namespace ControlServer.Host.Runtime;

public sealed class MapStationResolver
{
    public IReadOnlyList<RiotMapStation> ParseAreaNamedMachineStations(
        RiotMapStationCatalogSnapshot catalog) =>
        catalog.Stations.Where(station => ParseAreaCodes(station.StationName).Length > 0).ToArray();

    public RiotMapStation RequireFixedStation(
        RiotMapStationCatalogSnapshot catalog,
        int stationId,
        string stationName)
    {
        RiotMapStation[] matches = catalog.Stations.Where(station =>
            station.StationId == stationId &&
            string.Equals(station.StationName, stationName, StringComparison.Ordinal)).ToArray();
        return matches.Length == 1
            ? matches[0]
            : throw new StationResolutionException(
                "FIXED_STATION_BINDING_INVALID",
                $"Map {catalog.MapId} does not contain exact fixed station {stationName}/{stationId}.");
    }

    public RiotMapStation ResolveUniquePickup(
        RiotMapStationCatalogSnapshot catalog,
        string area)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(area);
        RiotMapStation[] matches = catalog.Stations.Where(station =>
            ParseAreaCodes(station.StationName).Contains(area, StringComparer.Ordinal)).ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new StationResolutionException(
                "AREA_STATION_NOT_FOUND",
                $"AREA {area} has no AreaNamedMachineStation on Map {catalog.MapId}."),
            _ => throw new StationResolutionException(
                "AREA_STATION_NOT_UNIQUE",
                $"AREA {area} resolves to more than one Station on Map {catalog.MapId}.")
        };
    }

    /// <summary>
    /// The id a route is frozen and replayed under: the Map content, both ends in order, AREA and EQP.
    /// </summary>
    /// <remarks>
    /// The origin is hashed first and the destination second. For WIRE_TO_GATE that is the AREA
    /// pickup station and then the gate, which is the order this hash has always had, so the ids of
    /// journeys already under way do not move.
    /// </remarks>
    public static string BuildRouteEvidenceId(
        RiotMapStationCatalogSnapshot catalog,
        RouteEndpoints endpoints,
        string area,
        string eqp)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        string canonical = string.Join('|',
            catalog.MapId,
            catalog.ContentSha256,
            endpoints.Origin.StationId,
            endpoints.Origin.StationName,
            endpoints.Destination.StationId,
            endpoints.Destination.StationName,
            area,
            eqp);
        string hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        return $"MAPCAT-{hash}";
    }

    private static string[] ParseAreaCodes(string stationName)
    {
        string[] tokens = stationName.Split('_', StringSplitOptions.None);
        return tokens.Length is >= 1 and <= 3 &&
               tokens.Distinct(StringComparer.Ordinal).Count() == tokens.Length &&
               tokens.All(AreaCodeFormat.IsValid)
            ? tokens
            : [];
    }
}

/// <summary>The two ends of a route, which travel by name rather than by position.</summary>
/// <remarks>
/// Two <see cref="RiotMapStation"/> parameters side by side can be swapped at a call site without
/// the compiler noticing, and a swapped route evidence id no longer matches on idempotent replay
/// (scope specification 5.3). Required init-only members and no positional constructor leave a call
/// site one way to write it: <c>new RouteEndpoints { Origin = …, Destination = … }</c>.
/// </remarks>
public sealed class RouteEndpoints
{
    public required RiotMapStation Origin { get; init; }

    public required RiotMapStation Destination { get; init; }
}

public sealed class StationResolutionException(string reasonCode, string message)
    : InvalidOperationException(message)
{
    public string ReasonCode { get; } = reasonCode;
}
