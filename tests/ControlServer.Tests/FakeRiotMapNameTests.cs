using System.Net;
using ControlServer.Application;

namespace ControlServer.Tests;

/// <summary>
/// 假 RIoT 的地图列表与改名入口（control-server#186）：生产适配器读得懂它答的形状；控制面能在同一 <c>mapId</c> 下改名，
/// 也能只让地图列表答 500 而站点目录照常——L2 场景 <c>map-rename-holds-all-task-types</c> 靠这两件事。
/// </summary>
public sealed class FakeRiotMapNameTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheMapListIsServedInTheShapeTheProductionAdapterParsesAndARenameShowsUpUnderTheSameId()
    {
        await using FakeRiotTests.FakeRiotFixture fixture = await FakeRiotTests.FakeRiotFixture.StartAsync();

        RiotMapNameListing seeded = await fixture.Gateway().ReadMapNamesAsync(Token);
        await fixture.CommandAsync(HttpMethod.Put, "maps/25/name", new { name = "老厂前线new_wk2" });
        RiotMapNameListing renamed = await fixture.Gateway().ReadMapNamesAsync(Token);

        // The seed's Map name is its own setting, not mapIdentity (MAP-TEST in this fixture).
        Assert.Equal([new RiotMapName(25, "老厂前线new_wk")], seeded.Maps);
        Assert.Equal([new RiotMapName(25, "老厂前线new_wk2")], renamed.Maps);
    }

    [Fact]
    public async Task AMapListFaultFailsOnlyTheMapListAndClearingItAnswersAgain()
    {
        await using FakeRiotTests.FakeRiotFixture fixture = await FakeRiotTests.FakeRiotFixture.StartAsync();

        await fixture.CommandAsync(HttpMethod.Put, "maps/list-fault", new { serverError = true });

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Gateway().ReadMapNamesAsync(Token));
        Assert.NotEmpty((await fixture.Gateway().ReadMapStationsAsync(25, Token)).Stations);

        await fixture.CommandAsync(HttpMethod.Put, "maps/list-fault", new { serverError = false });
        Assert.Single((await fixture.Gateway().ReadMapNamesAsync(Token)).Maps);
    }

    [Fact]
    public async Task ARenameWithoutANameIsRefused()
    {
        await using FakeRiotTests.FakeRiotFixture fixture = await FakeRiotTests.FakeRiotFixture.StartAsync();

        using HttpResponseMessage response = await fixture.SendCommandAsync(HttpMethod.Put, "maps/25/name", new { name = " " });

        Assert.False(response.IsSuccessStatusCode);
        Assert.NotEqual(HttpStatusCode.NotFound, response.StatusCode);
    }
}
