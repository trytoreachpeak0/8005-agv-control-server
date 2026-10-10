using System.Net.Http.Json;
using System.Text.Json;
using ControlServer.FakeRiot;
using Microsoft.AspNetCore.Builder;
using RIoT.Sdk.Core;
using RIoT.Sdk.Facade;

namespace ControlServer.Tests;

/// <summary>
/// The fake's route-graph and command surfaces, read through the real SDK.
/// </summary>
/// <remarks>
/// <para>
/// Driving the fake with the same Facade the product uses is the point: those five endpoints are
/// deserialized by hand inside the SDK, so a fake that answered in tidier JSON than the plant's
/// RCS would let a wrong reader pass here and fail in the field. Round 43 is what both sides are
/// pinned to.
/// </para>
/// <para>
/// A PASS here says the shapes line up. It says nothing about the real RCS behaving this way —
/// only Round 43's captures do, and the four quirks below are exactly what they captured.
/// </para>
/// </remarks>
public sealed class FakeRiotRouteGraphTests
{
    private const string VehicleKey = "BROKERX-TEST-0001";
    private const int MapId = 25;

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-01")]
    public async Task EdgesAreServedInTheSnakeCaseShapeTheSdkDeserializesByHand()
    {
        await using RouteGraphFixture fixture = await RouteGraphFixture.StartAsync();
        await using RiotSession session = fixture.Session();

        IReadOnlyList<MapEdge> edges = await session.Maps.ListEdgesAsync(
            MapId, TestContext.Current.CancellationToken);

        // Eight seeded edges, six forward and two reverse: the graph is directed, and a consumer
        // that treated it as undirected would reach stations it cannot actually reach.
        Assert.Equal(8, edges.Count);

        MapEdge first = Assert.Single(edges, edge => edge.Id == 1);
        // s_node/e_node — the generated model reads snode/enode and would leave these at zero.
        Assert.Equal((1, 2), (first.StartNode, first.EndNode));
        Assert.Equal(10000d, first.CostMm);
        Assert.Equal((0, 0), (first.StartX, first.StartY));
        Assert.Equal((0, 10000), (first.EndX, first.EndY));
        Assert.False(first.IsBackEdge);

        // Cost is the Euclidean length, which is what Round 43 measured on the real Map.
        MapEdge diagonalRun = Assert.Single(edges, edge => edge.Id == 5);
        Assert.Equal(20000d, diagonalRun.CostMm);

        Assert.Equal([1, 2], edges.Where(edge => edge.Id is 7 or 8).Select(edge => edge.EndNode));
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-01")]
    public async Task StationDetailsCarryTheDottedPositionKeysAndTheEdgeTheySitOn()
    {
        await using RouteGraphFixture fixture = await RouteGraphFixture.StartAsync();
        await using RiotSession session = fixture.Session();

        IReadOnlyList<MapStationDetail> stations = await session.Maps.ListStationDetailsAsync(
            MapId, TestContext.Current.CancellationToken);

        Assert.Equal([11, 12, 210], stations.Select(station => station.StationId));

        MapStationDetail gate = Assert.Single(stations, station => station.StationId == 210);
        Assert.Equal("关卡", gate.Name);
        // pos.x / pos.y are keys containing a literal dot; nothing generated reads them.
        Assert.Equal((20000d, 20000d), (gate.PosX, gate.PosY));
        // The gate sits on node 5, and edge 4 ends there — which is how a consumer places it.
        Assert.Equal(4, gate.EdgeId);
        // Zero on every real map25 station, so it cannot be used to place anything.
        Assert.Equal(0, gate.StationOffset);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-01")]
    public async Task TheCatalogFacadeStillReadsTheSameStationsAfterTheShapeGrew()
    {
        // The route-graph fields were added to the row the catalog Facade already read. If that
        // broke it, every existing scenario would break with it.
        await using RouteGraphFixture fixture = await RouteGraphFixture.StartAsync();
        await using RiotSession session = fixture.Session();

        IReadOnlyList<Station> stations = await session.Maps.ListStationsAsync(
            MapId, TestContext.Current.CancellationToken);

        Assert.Equal([11, 12, 210], stations.Select(station => station.StationId));
        Assert.Equal(["C15-13", "N1-3_N1-7", "关卡"], stations.Select(station => station.Name));
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-01")]
    public async Task RemovalsAndEdgeGroupsAreEmptyByDefaultJustAsMap25Is()
    {
        await using RouteGraphFixture fixture = await RouteGraphFixture.StartAsync();
        await using RiotSession session = fixture.Session();

        Assert.Empty(await session.Maps.ListRemovedEdgesAsync(MapId, TestContext.Current.CancellationToken));
        Assert.Empty(await session.Maps.ListRemovedStationsAsync(MapId, TestContext.Current.CancellationToken));
        // map25 has no edge groups at all — every group on the production RIoT belongs to another
        // Map — so an engine's staleness rule meets an empty fingerprint first, not a missing one.
        Assert.Empty(await session.Maps.ListEdgeGroupsAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-01")]
    public async Task SeededRemovalsReachTheFacadeAsDomainFacts()
    {
        await using RouteGraphFixture fixture = await RouteGraphFixture.StartAsync(
            "--FakeRiot:Seed:removedEdgeIds:0=3",
            "--FakeRiot:Seed:removedStationIds:0=12");
        await using RiotSession session = fixture.Session();

        RemovedEdge removedEdge = Assert.Single(
            await session.Maps.ListRemovedEdgesAsync(MapId, TestContext.Current.CancellationToken));
        Assert.Equal((3, MapId), (removedEdge.EdgeId, removedEdge.MapId));

        RemovedStation removedStation = Assert.Single(
            await session.Maps.ListRemovedStationsAsync(MapId, TestContext.Current.CancellationToken));
        Assert.Equal(12, removedStation.StationId);
        Assert.Equal("N1-3_N1-7", removedStation.StationName);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-01")]
    public async Task ThreeVehiclesAreServedAtRestOnTheSameMap()
    {
        await using RouteGraphFixture fixture = await RouteGraphFixture.StartAsync(
            "--FakeRiot:Seed:additionalVehicleKeys:0=BROKERX-TEST-0002",
            "--FakeRiot:Seed:additionalVehicleKeys:1=BROKERX-TEST-0003");
        await using RiotSession session = fixture.Session();

        foreach (string key in new[] { VehicleKey, "BROKERX-TEST-0002", "BROKERX-TEST-0003" })
        {
            VehicleCard card = await session.Tasks.GetVehicleCardAsync(
                key, TestContext.Current.CancellationToken);
            Assert.Equal(key, card.DeviceKey);
            Assert.Equal("IDLE", card.ProcState);
            Assert.Equal(0d, card.Speed);
            // Each vehicle answers as itself; one observation must never stand in for another's.
            Assert.Equal(210, card.CurrentPosition);
        }
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-01")]
    public async Task OrderCommandsAreRecordedWithTheirArgumentsAndNeverApplied()
    {
        await using RouteGraphFixture fixture = await RouteGraphFixture.StartAsync();
        await using RiotSession session = fixture.Session();

        await session.Tasks.CancelOrderAsync("ORDER-000001", "test", TestContext.Current.CancellationToken);
        await session.Tasks.OrderHoldAsync("ORDER-000001", "test", TestContext.Current.CancellationToken);
        await session.Tasks.OrderHoldAsync("ORDER-000001", "test", TestContext.Current.CancellationToken);
        await session.Tasks.OrderContinueAsync("ORDER-000001", null, TestContext.Current.CancellationToken);
        await session.Tasks.HangContinueAsync("ORDER-000002", null, TestContext.Current.CancellationToken);

        JsonElement[] recorded = await fixture.CommandInvocationsAsync();

        Assert.Equal(
            ["CMD_ORDER_CANCEL", "CMD_ORDER_HELD", "CMD_ORDER_HELD", "CMD_ORDER_CONTINUE_FROM_HELD",
             "CMD_ORDER_CONTINUE_FROM_HANG"],
            recorded.Select(item => item.GetProperty("commandType").GetString()));
        // Two holds, not one: "called exactly once" has to be decidable, so a repeat is a second
        // row rather than an overwrite.
        Assert.Equal(2, recorded.Count(item =>
            item.GetProperty("commandType").GetString() == "CMD_ORDER_HELD"));
        Assert.Equal("ORDER-000002", recorded[^1].GetProperty("target").GetString());
        Assert.Contains(
            "CMD_ORDER_CONTINUE_FROM_HANG",
            recorded[^1].GetProperty("argumentsJson").GetString(),
            StringComparison.Ordinal);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-01")]
    public async Task EmergencyServicesAreRecordedAndRejectABodyMissingThingsProperties()
    {
        await using RouteGraphFixture fixture = await RouteGraphFixture.StartAsync();
        await using RiotSession session = fixture.Session();

        await session.Device.TriggerEmergencyStopAsync(VehicleKey, TestContext.Current.CancellationToken);
        await session.Device.CancelEmergencyStopAsync(VehicleKey, TestContext.Current.CancellationToken);

        JsonElement[] recorded = await fixture.CommandInvocationsAsync();
        Assert.Equal(
            ["triggerEmergency", "cancelEmergency"],
            recorded.Select(item => item.GetProperty("commandType").GetString()));
        Assert.All(recorded, item => Assert.Equal(VehicleKey, item.GetProperty("target").GetString()));

        // The real service answers an empty body with a kernel NPE rather than a structured error,
        // so a caller that omits thingsProperties must not pass here either.
        using HttpResponseMessage response = await fixture.Client.PostAsJsonAsync(
            $"/api/device/v1/command/sync/service/{VehicleKey}/triggerEmergency",
            new { },
            TestContext.Current.CancellationToken);
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>(
            TestContext.Current.CancellationToken);
        Assert.Equal("00002", body.GetProperty("code").GetString());
        // Refused, so still two recorded calls.
        Assert.Equal(2, (await fixture.CommandInvocationsAsync()).Length);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-01")]
    public async Task RemovedEdgeDetailIsNotServedBecauseCp0001DidNotApproveIt()
    {
        await using RouteGraphFixture fixture = await RouteGraphFixture.StartAsync();

        using HttpResponseMessage response = await fixture.Client.GetAsync(
            new Uri($"/api/imap/v1/mapResource/removedEdgeDetail/{MapId}", UriKind.Relative),
            TestContext.Current.CancellationToken);

        // A product path that reached for it should fail here rather than quietly work.
        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// 场景替换站表之后，seed 里给了节点的站仍在那个节点上，没给的站不在路网上（批次7-10，control-server#215，看板例外第 13 条）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 在这之前替换站表一律建出不带坐标的站，连原来那三个也一起掉出路网，于是「换了站表又开路网」的场景里任何路径代价都算不出。
    /// STAGING_TO_WIRE 的多停靠场景两样都要：派工待送站 305 与第三个机台站 13 在表里，也在路网上。
    /// </para>
    /// <para>
    /// 三类站各断一次：原有的站坐标与所挂的边与初始建表时一字不差（替换不改它们）；新挂上节点的站落在自己的节点上；
    /// seed 里没有节点的站仍然不在路网上（坐标与边都是 0）——最后这一条防的是「替换时一律挂节点」那种写宽了的实现。
    /// </para>
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-01")]
    public async Task ReplacingTheStationTableKeepsEveryStationThatHasANodeOnTheGraph()
    {
        await using RouteGraphFixture fixture = await RouteGraphFixture.StartAsync();
        JsonElement snapshot = await fixture.Client.GetFromJsonAsync<JsonElement>(
            new Uri("/control/v1/snapshot", UriKind.Relative), TestContext.Current.CancellationToken);
        using HttpResponseMessage replaced = await fixture.Client.PutAsJsonAsync(
            new Uri($"/control/v1/maps/{MapId}/stations", UriKind.Relative),
            new
            {
                runId = snapshot.GetProperty("runId").GetString(),
                commandId = "replace-stations",
                stations = new Dictionary<string, string>
                {
                    ["210"] = "关卡",
                    ["12"] = "N1-3_N1-7",
                    ["11"] = "C15-13",
                    ["305"] = "派工待送取货",
                    ["13"] = "N1-5",
                    ["99"] = "OFF-GRAPH",
                },
            },
            TestContext.Current.CancellationToken);
        replaced.EnsureSuccessStatusCode();
        await using RiotSession session = fixture.Session();

        IReadOnlyList<MapStationDetail> stations = await session.Maps.ListStationDetailsAsync(
            MapId, TestContext.Current.CancellationToken);

        (double X, double Y, int Edge) At(int id)
        {
            MapStationDetail station = Assert.Single(stations, item => item.StationId == id);
            return (station.PosX, station.PosY, station.EdgeId);
        }

        Assert.Equal((20000d, 20000d, 4), At(210));
        Assert.Equal((0d, 20000d, 2), At(12));
        Assert.Equal((0d, 0d, 6), At(11));
        Assert.Equal((0d, 10000d, 1), At(305));
        Assert.Equal((10000d, 20000d, 3), At(13));
        Assert.Equal((0d, 0d, 0), At(99));
    }

    private sealed class RouteGraphFixture : IAsyncDisposable
    {
        private WebApplication app = null!;

        public HttpClient Client { get; private set; } = null!;

        public static async Task<RouteGraphFixture> StartAsync(params string[] extraArgs)
        {
            RouteGraphFixture fixture = new();
            WebApplication? app = FakeRiotHost.TryCreate(
            [
                "--FakeRiot:port=0",
                "--FakeRiot:instanceId=fake-riot-route-graph-test",
                "--FakeRiot:Seed:vehicleKey=" + VehicleKey,
                .. extraArgs,
            ]);
            Assert.NotNull(app);
            fixture.app = app;
            await app.StartAsync(TestContext.Current.CancellationToken);
            fixture.Client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
            return fixture;
        }

        public RiotSession Session() => new(new RiotOptions
        {
            BaseUrl = Client.BaseAddress!.ToString().TrimEnd('/'),
            CallApiKey = "fake-riot-test-key",
        });

        public async Task<JsonElement[]> CommandInvocationsAsync()
        {
            JsonElement snapshot = await Client.GetFromJsonAsync<JsonElement>(
                new Uri("/control/v1/snapshot", UriKind.Relative),
                TestContext.Current.CancellationToken);
            return snapshot.GetProperty("body").GetProperty("commandInvocations").EnumerateArray().ToArray();
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.StopAsync(TestContext.Current.CancellationToken);
            await app.DisposeAsync();
        }
    }
}
