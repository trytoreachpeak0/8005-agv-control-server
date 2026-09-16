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

    public static string BuildRouteEvidenceId(
        RiotMapStationCatalogSnapshot catalog,
        RiotMapStation pickup,
        RiotMapStation gate,
        string area,
        string eqp)
    {
        string canonical = string.Join('|',
            catalog.MapId,
            catalog.ContentSha256,
            pickup.StationId,
            pickup.StationName,
            gate.StationId,
            gate.StationName,
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

public sealed class StationResolutionException(string reasonCode, string message)
    : InvalidOperationException(message)
{
    public string ReasonCode { get; } = reasonCode;
}
