using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using ControlServer.Dashboard;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ControlServer.Tests;

/// <summary>
/// 看板写操作的约定（control-server#162）：动作像卡片一样自注册，看板主程序只挂一次通用路由；确认页不自动刷新；
/// 只收看板自身来源的提交；看板上只有收紧方向的动作。
/// </summary>
public sealed class DashboardActionTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void TheHoldActionRegistersItselfAndIsTheOnlyActionTheDashboardHas()
    {
        DashboardActionCatalog catalog = DashboardActionCatalog.Discovered;

        IDashboardAction action = Assert.Single(catalog.Actions);
        Assert.IsType<TaskTypeHoldAction>(action);
        Assert.Equal(TaskTypeBindingCard.HoldActionId, action.ActionId);
        Assert.Equal("/api/task-type-holds", action.TargetPath);
        Assert.Contains(DashboardCardCatalog.Discovered.Cards, card => card is TaskTypeBindingCard);
    }

    [Fact]
    public void AnActionAimedAtTheReadOnlyQueryPrefixOrReusingAnIdIsRefused()
    {
        Assert.Throws<InvalidOperationException>(() => new DashboardActionCatalog(
            [new ExampleAction("example", DashboardPaths.QueryPrefix + "anything")]));
        Assert.Throws<InvalidOperationException>(() => new DashboardActionCatalog(
            [new ExampleAction("example", "/api/one"), new ExampleAction("example", "/api/two")]));
    }

    [Fact]
    public async Task TheConfirmationPageCarriesTheRowItCameFromAndNeverRefreshesItself()
    {
        await using Rig rig = await Rig.StartAsync();

        using HttpResponseMessage response = await rig.Dashboard.GetAsync(
            "/actions/task-type-hold?mapId=25&taskType=WIRE_TO_GATE", Token);
        string page = await response.Content.ReadAsStringAsync(Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("http-equiv=\"refresh\"", page, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("method=\"post\"", page, StringComparison.Ordinal);
        Assert.Contains("action=\"/actions/task-type-hold\"", page, StringComparison.Ordinal);
        Assert.Contains("name=\"mapId\" value=\"25\"", page, StringComparison.Ordinal);
        Assert.Contains("name=\"taskType\" value=\"WIRE_TO_GATE\"", page, StringComparison.Ordinal);
        Assert.Contains("name=\"reason\"", page, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://evil.example", null)]
    [InlineData(null, null)]
    [InlineData("null", null)]
    [InlineData("http://evil.example:58009", "evil.example:58009")]
    public async Task ASubmissionThatDidNotComeFromTheDashboardItselfIsRefusedAndForwardsNothing(
        string? origin,
        string? host)
    {
        await using Rig rig = await Rig.StartAsync();
        using HttpRequestMessage request = Rig.Submission(origin);
        if (host is not null)
        {
            request.Headers.Host = host;
        }

        using HttpResponseMessage response = await rig.Dashboard.SendAsync(request, Token);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(rig.Forwarded);
    }

    [Fact]
    public async Task ASubmissionFromTheDashboardIsForwardedToTheServerAndReturnsToTheMainPage()
    {
        await using Rig rig = await Rig.StartAsync();

        using HttpResponseMessage response = await rig.Dashboard.SendAsync(Rig.Submission(rig.DashboardOrigin), Token);

        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        Assert.Equal("/", response.Headers.Location?.OriginalString);
        (string path, string body) = Assert.Single(rig.Forwarded);
        Assert.Equal("/api/task-type-holds", path);
        using JsonDocument forwarded = JsonDocument.Parse(body);
        Assert.Equal(25, forwarded.RootElement.GetProperty("mapId").GetInt32());
        Assert.Equal("WIRE_TO_GATE", forwarded.RootElement.GetProperty("taskType").GetString());
        Assert.Equal("关卡被占用", forwarded.RootElement.GetProperty("reason").GetString());
        Assert.Equal("班组长", forwarded.RootElement.GetProperty("claimedRole").GetString());
    }

    [Fact]
    public async Task AServerRefusalIsShownAsItsReasonOnAPageThatDoesNotRefresh()
    {
        await using Rig rig = await Rig.StartAsync(serverStatus: StatusCodes.Status422UnprocessableEntity);

        using HttpResponseMessage response = await rig.Dashboard.SendAsync(Rig.Submission(rig.DashboardOrigin), Token);
        string page = await response.Content.ReadAsStringAsync(Token);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("A reason for the hold is required.", page, StringComparison.Ordinal);
        Assert.DoesNotContain("http-equiv=\"refresh\"", page, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TheDashboardOffersNoWayToLiftAHold()
    {
        await using Rig rig = await Rig.StartAsync();

        HttpStatusCode[] attempts =
        [
            (await rig.Dashboard.SendAsync(Rig.Submission(rig.DashboardOrigin, "/actions/task-type-hold-release"), Token)).StatusCode,
            (await rig.Dashboard.SendAsync(Rig.Submission(rig.DashboardOrigin, "/actions/task-type-release"), Token)).StatusCode,
            (await rig.Dashboard.DeleteAsync("/actions/task-type-hold", Token)).StatusCode,
        ];

        Assert.All(attempts, status => Assert.False((int)status is >= 200 and < 400, $"Answered {(int)status}."));
        Assert.Empty(rig.Forwarded);
        Assert.All(DashboardActionCatalog.Discovered.Actions, action =>
            Assert.DoesNotContain("release", action.TargetPath, StringComparison.OrdinalIgnoreCase));
    }

    private sealed class ExampleAction(string actionId, string targetPath) : IDashboardAction
    {
        public string ActionId => actionId;

        public string Title => "示例动作";

        public string TargetPath => targetPath;

        public IReadOnlyList<DashboardActionField> Fields => [];

        public object BuildRequest(IReadOnlyDictionary<string, string> form) => new { };
    }

    /// <summary>A dashboard with the action routes, and a stand-in ControlServer that records what reaches it.</summary>
    private sealed class Rig : IAsyncDisposable
    {
        private readonly WebApplication _server;
        private readonly WebApplication _dashboard;

        private Rig(WebApplication server, WebApplication dashboard, string dashboardAddress, ConcurrentQueue<(string, string)> forwarded)
        {
            _server = server;
            _dashboard = dashboard;
            Forwarded = forwarded;
            DashboardOrigin = dashboardAddress;
            Dashboard = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            {
                BaseAddress = new Uri(dashboardAddress)
            };
        }

        public HttpClient Dashboard { get; }

        public string DashboardOrigin { get; }

        public ConcurrentQueue<(string Path, string Body)> Forwarded { get; }

        public static async Task<Rig> StartAsync(int serverStatus = StatusCodes.Status201Created)
        {
            ConcurrentQueue<(string, string)> forwarded = new();
            WebApplicationBuilder serverBuilder = WebApplication.CreateBuilder();
            serverBuilder.WebHost.UseUrls("http://127.0.0.1:0");
            serverBuilder.Logging.ClearProviders();
            WebApplication server = serverBuilder.Build();
            server.Run(async context =>
            {
                using StreamReader reader = new(context.Request.Body);
                forwarded.Enqueue((context.Request.Path.Value!, await reader.ReadToEndAsync()));
                context.Response.StatusCode = serverStatus;
                await context.Response.WriteAsJsonAsync<object>(
                    serverStatus < 300
                        ? new { holdId = "hold-1", created = true }
                        : new { title = "Hold request refused", detail = "A reason for the hold is required." });
            });
            await server.StartAsync(Token);

            WebApplicationBuilder dashboardBuilder = WebApplication.CreateBuilder();
            dashboardBuilder.WebHost.UseUrls("http://127.0.0.1:0");
            dashboardBuilder.Logging.ClearProviders();
            dashboardBuilder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Dashboard:controlServerBaseUrl"] = Address(server)
            });
            dashboardBuilder.Services.AddDashboardActions(dashboardBuilder.Configuration);
            WebApplication dashboard = dashboardBuilder.Build();
            dashboard.MapDashboardActions();
            await dashboard.StartAsync(Token);
            return new Rig(server, dashboard, Address(dashboard), forwarded);
        }

        public static HttpRequestMessage Submission(string? origin, string path = "/actions/task-type-hold")
        {
            HttpRequestMessage request = new(HttpMethod.Post, path)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["mapId"] = "25",
                    ["taskType"] = "WIRE_TO_GATE",
                    ["reason"] = "关卡被占用",
                    ["claimedRole"] = "班组长"
                })
            };
            if (origin is not null)
            {
                request.Headers.Add("Origin", origin);
            }
            return request;
        }

        public async ValueTask DisposeAsync()
        {
            Dashboard.Dispose();
            await _dashboard.StopAsync(Token);
            await _dashboard.DisposeAsync();
            await _server.StopAsync(Token);
            await _server.DisposeAsync();
        }

        private static string Address(WebApplication app) =>
            app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    }
}
