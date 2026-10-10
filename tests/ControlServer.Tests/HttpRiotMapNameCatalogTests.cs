using System.Net;
using System.Text;
using ControlServer.Application;
using ControlServer.Infrastructure.Adapters;
using RIoT.Sdk.Core;
using RIoT.Sdk.Facade;

namespace ControlServer.Tests;

/// <summary>
/// 读 RIoT 地图列表（control-server#186）：只读不带 <c>mapJson</c> 的列表接口，读不成就抛 <see cref="InvalidDataException"/>，
/// 从不交出半张或空的名单——空名单交出去，就等于告诉比较器「每张图都缺席」。
/// </summary>
public sealed class HttpRiotMapNameCatalogTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheMapListIsReadFromTheEndpointWithoutMapJsonAndObservedAfterTheReadCompletes()
    {
        DateTimeOffset before = new(2026, 9, 28, 15, 0, 0, TimeSpan.Zero);
        DateTimeOffset after = before.AddSeconds(1);
        SettableClock clock = new(before);
        int calls = 0;
        await using RiotSession session = Session((request, _) =>
        {
            calls++;
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/api/imap/v1/mapInfo/getALLMapInfoExcludeMapJson", request.RequestUri?.AbsolutePath);
            clock.Now = after;
            // The keys the real RIoT answered with on 2026-09-28 (evidence/field/2026-09-28-cs186-map-list-endpoint-check).
            return Json("""
                {"code":"0","message":"成功","result":[
                  {"id":25,"name":"老厂前线new","description":null,"floor":1,"gmtCreate":"2026-07-01 10:00:00",
                   "gmtUpdate":"2026-09-28 17:49:34","mapError":null,"source":"upload","state":"activated","syncState":"synced","url":"x"},
                  {"id":26,"name":"老厂前线new_wk","description":null,"floor":1,"gmtCreate":"2026-09-10 10:00:00",
                   "gmtUpdate":"2026-09-28 17:49:34","mapError":null,"source":"upload","state":"activated","syncState":"synced","url":"y"}]}
                """);
        });

        RiotMapNameListing listing = await new HttpRiotMovementGateway(session, clock).ReadMapNamesAsync(Token);

        Assert.Equal(1, calls);
        Assert.Equal([new RiotMapName(25, "老厂前线new"), new RiotMapName(26, "老厂前线new_wk")], listing.Maps);
        Assert.Equal(after, listing.ObservedAt);
    }

    /// <summary>
    /// The real RIoT's answer of 2026-09-28 23:51 (evidence/field/2026-09-28-cs186-map-list-endpoint-check/real-riot-raw, the
    /// url values replaced by a placeholder string, nothing else changed), replayed through the production adapter. The
    /// SDK reads <c>source</c>, <c>state</c> and <c>syncState</c> as string enums; had RIoT answered them as numbers the whole
    /// list would fail to parse every round -- never a rename, never detected. This turns red if an SDK upgrade or a RIoT
    /// change breaks that parse.
    /// </summary>
    [Fact]
    public async Task TheRealRiotAnswerOf20260928ParsesThroughTheAdapterWithMap26UnderItsName()
    {
        string body = await File.ReadAllTextAsync(ReplayPath("map-list-2026-09-28.json"), Token);
        await using RiotSession session = Session((_, _) => Json(body));

        RiotMapNameListing listing = await new HttpRiotMovementGateway(session, TimeProvider.System).ReadMapNamesAsync(Token);

        Assert.Equal([9, 12, 14, 19, 22, 24, 25, 26], listing.Maps.Select(map => map.MapId));
        Assert.Equal("老厂前线new_wk", Assert.Single(listing.Maps, map => map.MapId == 26).Name);
        Assert.Equal("老厂前线new", Assert.Single(listing.Maps, map => map.MapId == 25).Name);
    }

    /// <summary>
    /// A value RIoT may add one day to <c>source</c>, <c>state</c> or <c>syncState</c> -- one the SDK's enum does not know --
    /// must not make the whole list unreadable, or the rename check would go dark on the day RIoT upgrades (the coordinator's
    /// ask of 2026-09-28: pinned on the real answer, not on a hand-written one).
    /// </summary>
    [Theory]
    [InlineData("syncState", "partition", "resyncing")]
    [InlineData("source", "fetch", "importedFromCloud")]
    [InlineData("state", "activated", "archived")]
    public async Task AnEnumValueTheSdkDoesNotKnowStillReadsEveryMapAndItsName(string key, string known, string unknown)
    {
        string body = (await File.ReadAllTextAsync(ReplayPath("map-list-2026-09-28.json"), Token))
            .Replace($"\"{key}\": \"{known}\"", $"\"{key}\": \"{unknown}\"", StringComparison.Ordinal);
        Assert.Contains($"\"{key}\": \"{unknown}\"", body, StringComparison.Ordinal);
        await using RiotSession session = Session((_, _) => Json(body));

        RiotMapNameListing listing = await new HttpRiotMovementGateway(session, TimeProvider.System).ReadMapNamesAsync(Token);

        Assert.Equal([9, 12, 14, 19, 22, 24, 25, 26], listing.Maps.Select(map => map.MapId));
        Assert.Equal("老厂前线new_wk", Assert.Single(listing.Maps, map => map.MapId == 26).Name);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, "{\"code\":\"500\",\"message\":\"失败\",\"result\":null}")]
    [InlineData(HttpStatusCode.OK, "{\"code\":\"0\",\"result\":null}")]
    [InlineData(HttpStatusCode.OK, "{\"code\":\"0\",\"result\":[]}")]
    [InlineData(HttpStatusCode.OK, "{\"code\":\"0\",\"result\":[{\"id\":26,\"name\":\"\"}]}")]
    [InlineData(HttpStatusCode.OK, "not json")]
    [InlineData(HttpStatusCode.InternalServerError, "{\"code\":\"500\"}")]
    public async Task AReadThatDoesNotYieldANonEmptyListThrowsInsteadOfAnsweringEmpty(HttpStatusCode status, string body)
    {
        await using RiotSession session = Session((_, _) => new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        });

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new HttpRiotMovementGateway(session, TimeProvider.System).ReadMapNamesAsync(Token));
    }

    private static string ReplayPath(string name, [System.Runtime.CompilerServices.CallerFilePath] string sourceFile = "") =>
        Path.Combine(Path.GetDirectoryName(sourceFile)!, "RiotReplays", name);

    private static RiotSession Session(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> answer) => new(
        new RiotOptions { BaseUrl = "http://riot.test", CallApiKey = "test-call-api-key" },
        new Handler(answer));

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(answer(request, cancellationToken));
    }

    private sealed class SettableClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
