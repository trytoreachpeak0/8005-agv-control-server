using ControlServer.Application;
using ControlServer.Host.Runtime;

namespace ControlServer.Tests;

public sealed class MapStationResolverTests
{
    private readonly MapStationResolver resolver = new();

    [Theory]
    [InlineData("C15-13", 11)]
    [InlineData("N1-3", 12)]
    [InlineData("N1-7", 12)]
    [InlineData("Q12-12", 13)]
    public void ResolveUniquePickupReturnsExactOneToThreeAreaNamedStation(
        string area,
        int expectedStationId)
    {
        RiotMapStationCatalogSnapshot catalog = Catalog(
            new RiotMapStation(11, "C15-13"),
            new RiotMapStation(12, "N1-3_N1-7"),
            new RiotMapStation(13, "Q12-12_Q24-10_N25-5"),
            new RiotMapStation(210, "关卡"),
            new RiotMapStation(300, "等待点"));

        RiotMapStation result = resolver.ResolveUniquePickup(catalog, area);

        Assert.Equal(expectedStationId, result.StationId);
        Assert.NotEqual("关卡", result.StationName);
    }

    [Fact]
    public void ParseAreaNamedMachineStationsIgnoresOrdinaryAndMalformedStations()
    {
        RiotMapStationCatalogSnapshot catalog = Catalog(
            new RiotMapStation(11, "C15-13"),
            new RiotMapStation(12, "C15-13_BAD"),
            new RiotMapStation(13, "N1-3_N1-7_Q12-12_Q24-10"),
            new RiotMapStation(210, "关卡"),
            new RiotMapStation(300, "等待点"));

        IReadOnlyList<RiotMapStation> result = resolver.ParseAreaNamedMachineStations(catalog);

        RiotMapStation station = Assert.Single(result);
        Assert.Equal(11, station.StationId);
        Assert.Equal("C15-13", station.StationName);
    }

    [Fact]
    public void ResolveUniquePickupFailsClosedForDuplicateAreaAcrossStations()
    {
        RiotMapStationCatalogSnapshot catalog = Catalog(
            new RiotMapStation(11, "N1-3"),
            new RiotMapStation(12, "N1-3_N1-7"),
            new RiotMapStation(210, "关卡"));

        StationResolutionException error = Assert.Throws<StationResolutionException>(
            () => resolver.ResolveUniquePickup(catalog, "N1-3"));

        Assert.Equal("AREA_STATION_NOT_UNIQUE", error.ReasonCode);
    }

    [Fact]
    public void RequireFixedStationFailsClosedForWrongGateNameAtStation210()
    {
        RiotMapStationCatalogSnapshot catalog = Catalog(
            new RiotMapStation(11, "N1-3"),
            new RiotMapStation(210, "其它站"));

        StationResolutionException error = Assert.Throws<StationResolutionException>(
            () => resolver.RequireFixedStation(catalog, 210, "关卡"));

        Assert.Equal("FIXED_STATION_BINDING_INVALID", error.ReasonCode);
    }

    private static RiotMapStationCatalogSnapshot Catalog(params RiotMapStation[] stations) =>
        new(25, new DateTimeOffset(2026, 8, 27, 1, 0, 0, TimeSpan.Zero), new string('a', 64), stations);
}
