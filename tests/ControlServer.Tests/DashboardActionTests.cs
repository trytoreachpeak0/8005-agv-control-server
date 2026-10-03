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

    private static readonly string[] LooseningWords = ["release", "lift", "cancel", "resume"];

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// 看板上的写操作是一张名单，不是「有几个算几个」：无人员认证时这里只放收紧方向的动作（规格 5.7）。control-server#384 把
    /// 人工判故障加进名单——它只让一次装卸停下转人工恢复，前置条件与凭据由服务端的判定接口判，看板只转交。
    /// </summary>
    [Fact]
    public void TheDashboardHasExactlyTheFailSafeActionsOnItsList()
    {
        DashboardActionCatalog catalog = DashboardActionCatalog.Discovered;

        Assert.Equal(
            [SlotFaultDeclarationAction.Id, TaskTypeBindingCard.HoldActionId],
            catalog.Actions.Select(action => action.ActionId));
        Assert.IsType<SlotFaultDeclarationAction>(catalog.Find(SlotFaultDeclarationAction.Id));
        Assert.IsType<TaskTypeHoldAction>(catalog.Find(TaskTypeBindingCard.HoldActionId));
        Assert.Equal("/api/task-type-holds", catalog.Find(TaskTypeBindingCard.HoldActionId)!.TargetPath);
        Assert.Equal(
            ControlServer.Host.Runtime.SlotFaultDeclarationEndpoints.Route,
            catalog.Find(SlotFaultDeclarationAction.Id)!.TargetPath);
        Assert.Contains(DashboardCardCatalog.Discovered.Cards, card => card is TaskTypeBindingCard);
        Assert.Contains(DashboardCardCatalog.Discovered.Cards, card => card is ExpectedActionOverdueCard);
    }

    [Fact]
    public void AnActionWithTwoCredentialsOrAChoiceWithNothingToChooseIsRefused()
    {
        Assert.Throws<InvalidOperationException>(() => new DashboardActionCatalog(
        [
            new ExampleAction("example", "/api/one",
            [
                new("a", "A", DashboardActionFieldKind.BearerCredential, Required: true),
                new("b", "B", DashboardActionFieldKind.BearerCredential, Required: true)
            ])
        ]));
        Assert.Throws<InvalidOperationException>(() => new DashboardActionCatalog(
            [new ExampleAction("example", "/api/one", [new("c", "C", DashboardActionFieldKind.Choice, Required: true)])]));
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
        // Every action on the list, the slot fault declaration included (control-server#384): nothing that lifts, releases,
        // cancels or resumes.
        Assert.All(DashboardActionCatalog.Discovered.Actions, action =>
        {
            foreach (string loosening in LooseningWords)
            {
                Assert.DoesNotContain(loosening, action.TargetPath, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(loosening, action.ActionId, StringComparison.OrdinalIgnoreCase);
            }
        });
    }

    // --- 人工判故障（REQ-0359，control-server#384） -------------------------------------------------------------------

    [Fact]
    public async Task TheDeclarationPageCarriesVehicleAndSlotOffersEveryCategoryAndRoleAndNeverEchoesTheCredential()
    {
        await using Rig rig = await Rig.StartAsync();

        using HttpResponseMessage response = await rig.Dashboard.GetAsync(
            "/actions/slot-fault-declaration?agvId=AGV-001&slotNo=3&credential=leaked-through-a-link", Token);
        string page = await response.Content.ReadAsStringAsync(Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("http-equiv=\"refresh\"", page, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("action=\"/actions/slot-fault-declaration\"", page, StringComparison.Ordinal);
        Assert.Contains("name=\"agvId\" value=\"AGV-001\"", page, StringComparison.Ordinal);
        Assert.Contains("name=\"slotNo\" value=\"3\"", page, StringComparison.Ordinal);
        foreach (string value in ControlServer.Host.Runtime.SlotFaultDeclarationEndpoints.FaultCategories
                     .Concat(ControlServer.Host.Runtime.SlotFaultDeclarationEndpoints.AdministratorRoles))
        {
            Assert.Contains($"<option value=\"{value}\">", page, StringComparison.Ordinal);
        }
        Assert.Contains("<select name=\"faultCategory\" required>", page, StringComparison.Ordinal);
        Assert.Contains("<select name=\"administratorRole\" required>", page, StringComparison.Ordinal);
        Assert.Contains("<textarea name=\"note\"", page, StringComparison.Ordinal);
        Assert.Contains("name=\"operatorId\" required", page, StringComparison.Ordinal);
        Assert.Contains("<input type=\"password\" name=\"credential\" autocomplete=\"off\" required>", page, StringComparison.Ordinal);
        Assert.DoesNotContain("leaked-through-a-link", page, StringComparison.Ordinal);
    }

    /// <summary>
    /// The forwarded body is the declaration contract of control-server#383 field for field -- it binds onto the server's own
    /// request record with nothing left over -- and the credential goes only as the Bearer header, never into the body.
    /// </summary>
    [Fact]
    public async Task ADeclarationFromTheDashboardIsForwardedInTheServersContractWithTheCredentialAsItsBearerOnly()
    {
        await using Rig rig = await Rig.StartAsync(serverStatus: StatusCodes.Status202Accepted);

        using HttpResponseMessage response = await rig.Dashboard.SendAsync(
            Rig.Declaration(rig.DashboardOrigin), Token);

        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        Assert.Equal("/", response.Headers.Location?.OriginalString);
        (string path, string body) = Assert.Single(rig.Forwarded);
        Assert.Equal(ControlServer.Host.Runtime.SlotFaultDeclarationEndpoints.Route, path);
        Assert.Equal("Bearer declaration-credential", Assert.Single(rig.Authorizations));
        Assert.DoesNotContain("declaration-credential", body, StringComparison.Ordinal);

        using JsonDocument forwarded = JsonDocument.Parse(body);
        string[] contract = [.. typeof(ControlServer.Host.Runtime.SlotFaultDeclarationHttpRequest).GetProperties()
            .Select(property => JsonNamingPolicy.CamelCase.ConvertName(property.Name)).Order(StringComparer.Ordinal)];
        Assert.Equal(contract, forwarded.RootElement.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        ControlServer.Host.Runtime.SlotFaultDeclarationHttpRequest request =
            JsonSerializer.Deserialize<ControlServer.Host.Runtime.SlotFaultDeclarationHttpRequest>(
                body, WebJson)!;
        Assert.True(Guid.TryParseExact(request.RequestId, "D", out _));
        Assert.Equal("AGV-001", request.AgvId);
        Assert.Equal(3, request.SlotNo);
        Assert.Equal("LOCK", request.FaultCategory);
        Assert.Equal("门已关，锁一直读未锁", request.Note);
        Assert.Equal("maintenance-7", request.OperatorId);
        Assert.Equal("MAINTENANCE_ADMINISTRATOR", request.AdministratorRole);
    }

    [Theory]
    [InlineData("http://evil.example")]
    [InlineData(null)]
    public async Task ADeclarationThatDidNotComeFromTheDashboardItselfIsRefusedAndForwardsNothing(string? origin)
    {
        await using Rig rig = await Rig.StartAsync(serverStatus: StatusCodes.Status202Accepted);

        using HttpResponseMessage response = await rig.Dashboard.SendAsync(Rig.Declaration(origin), Token);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(rig.Forwarded);
        Assert.Empty(rig.Authorizations);
    }

    /// <summary>A refusal shows every reason the server gave, on the confirmation's own page, which does not refresh.</summary>
    [Theory]
    [InlineData(StatusCodes.Status409Conflict, "SLOT_FAULT_EXPECTED_ACTION_NOT_OVERDUE,SLOT_FAULT_NOT_THE_CURRENT_SLOT")]
    [InlineData(StatusCodes.Status422UnprocessableEntity, "requestId (a UUID), agvId, slotNo (1 or more), a non-empty note and operatorId are required")]
    public async Task AServerRefusalOfADeclarationShowsEveryReasonItGave(int status, string detail)
    {
        await using Rig rig = await Rig.StartAsync(responses: [(status, Problem("Slot fault declaration refused", detail))]);

        using HttpResponseMessage response = await rig.Dashboard.SendAsync(Rig.Declaration(rig.DashboardOrigin), Token);
        string page = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(Token));

        Assert.Equal(status, (int)response.StatusCode);
        foreach (string reason in detail.Split(','))
        {
            Assert.Contains(reason, page, StringComparison.Ordinal);
        }
        Assert.DoesNotContain("http-equiv=\"refresh\"", page, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Two people submitting the same row almost at once: the server's one-pending-declaration-per-attempt constraint
    /// (control-server#383) holds them to one, and the dashboard shows the second its 409 as it came.
    /// </summary>
    [Fact]
    public async Task TwoDeclarationsOfTheSameRowAreHeldToOneByTheServerAndTheSecondIsShownWhy()
    {
        await using Rig rig = await Rig.StartAsync(responses:
        [
            (StatusCodes.Status202Accepted, """{"declarationId":"d-1","state":"PENDING"}"""),
            (StatusCodes.Status409Conflict, Problem("Slot fault declaration refused", "SLOT_FAULT_DECLARATION_PENDING"))
        ]);

        using HttpResponseMessage first = await rig.Dashboard.SendAsync(Rig.Declaration(rig.DashboardOrigin), Token);
        using HttpResponseMessage second = await rig.Dashboard.SendAsync(Rig.Declaration(rig.DashboardOrigin), Token);

        Assert.Equal(HttpStatusCode.SeeOther, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Contains("SLOT_FAULT_DECLARATION_PENDING", await second.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);
        string[] requestIds = [.. rig.Forwarded.Select(item => JsonDocument.Parse(item.Body).RootElement.GetProperty("requestId").GetString()!)];
        Assert.Equal(2, requestIds.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData(StatusCodes.Status401Unauthorized, "凭据不对或没有填（401）")]
    [InlineData(StatusCodes.Status404NotFound, "可能是现场没有开启它")]
    public async Task AnAnswerWithoutABodySaysWhatItsStatusMeans(int status, string shown)
    {
        await using Rig rig = await Rig.StartAsync(responses: [(status, null)]);

        using HttpResponseMessage response = await rig.Dashboard.SendAsync(Rig.Declaration(rig.DashboardOrigin), Token);

        Assert.Equal(status, (int)response.StatusCode);
        Assert.Contains(shown, WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(Token)), StringComparison.Ordinal);
    }

    /// <summary>The server does not answer in time: the outcome is unknown, and the page sends the person to the row's state.</summary>
    [Fact]
    public async Task ADeclarationTheServerDoesNotAnswerInTimeIsReportedAsUnknown()
    {
        await using Rig rig = await Rig.StartAsync(hang: true, timeoutSeconds: 1);

        using HttpResponseMessage response = await rig.Dashboard.SendAsync(Rig.Declaration(rig.DashboardOrigin), Token);
        string page = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(Token));

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.Contains("提交结果未知，请回主页看这一行的状态", page, StringComparison.Ordinal);
    }

    private static string Problem(string title, string detail) =>
        JsonSerializer.Serialize(new { title, detail, status = 0 });

    private sealed class ExampleAction(
        string actionId, string targetPath, IReadOnlyList<DashboardActionField>? fields = null) : IDashboardAction
    {
        public string ActionId => actionId;

        public string Title => "示例动作";

        public string TargetPath => targetPath;

        public IReadOnlyList<DashboardActionField> Fields => fields ?? [];

        public object BuildRequest(IReadOnlyDictionary<string, string> form) => new { };
    }

    /// <summary>A dashboard with the action routes, and a stand-in ControlServer that records what reaches it.</summary>
    private sealed class Rig : IAsyncDisposable
    {
        private readonly WebApplication _server;
        private readonly WebApplication _dashboard;

        private Rig(
            WebApplication server,
            WebApplication dashboard,
            string dashboardAddress,
            ConcurrentQueue<(string, string)> forwarded,
            ConcurrentQueue<string> authorizations)
        {
            _server = server;
            _dashboard = dashboard;
            Forwarded = forwarded;
            Authorizations = authorizations;
            DashboardOrigin = dashboardAddress;
            Dashboard = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            {
                BaseAddress = new Uri(dashboardAddress)
            };
        }

        public HttpClient Dashboard { get; }

        public string DashboardOrigin { get; }

        public ConcurrentQueue<(string Path, string Body)> Forwarded { get; }

        /// <summary>The <c>Authorization</c> header of each forwarded request that carried one.</summary>
        public ConcurrentQueue<string> Authorizations { get; }

        /// <param name="responses">
        /// What the stand-in answers, one entry per request in order (the last one repeats); a null body answers with none.
        /// Without it, <paramref name="serverStatus"/> with the hold's body, as before.
        /// </param>
        /// <param name="hang">The stand-in never answers.</param>
        /// <param name="timeoutSeconds">The dashboard's <c>controlServerTimeoutSeconds</c>; its default otherwise.</param>
        public static async Task<Rig> StartAsync(
            int serverStatus = StatusCodes.Status201Created,
            IReadOnlyList<(int Status, string? Body)>? responses = null,
            bool hang = false,
            int? timeoutSeconds = null)
        {
            ConcurrentQueue<(string, string)> forwarded = new();
            ConcurrentQueue<string> authorizations = new();
            int calls = 0;
            WebApplicationBuilder serverBuilder = WebApplication.CreateBuilder();
            serverBuilder.WebHost.UseUrls("http://127.0.0.1:0");
            serverBuilder.Logging.ClearProviders();
            WebApplication server = serverBuilder.Build();
            server.Run(async context =>
            {
                using StreamReader reader = new(context.Request.Body);
                forwarded.Enqueue((context.Request.Path.Value!, await reader.ReadToEndAsync()));
                if (context.Request.Headers.Authorization.ToString() is { Length: > 0 } authorization)
                {
                    authorizations.Enqueue(authorization);
                }
                if (hang)
                {
                    await Task.Delay(Timeout.Infinite, context.RequestAborted).ContinueWith(_ => { }, TaskScheduler.Default);
                    return;
                }
                if (responses is not null)
                {
                    (int status, string? body) = responses[Math.Min(Interlocked.Increment(ref calls) - 1, responses.Count - 1)];
                    context.Response.StatusCode = status;
                    if (body is not null)
                    {
                        context.Response.ContentType = "application/problem+json";
                        await context.Response.WriteAsync(body);
                    }
                    return;
                }
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
                ["Dashboard:controlServerBaseUrl"] = Address(server),
                ["Dashboard:controlServerTimeoutSeconds"] = timeoutSeconds?.ToString(System.Globalization.CultureInfo.InvariantCulture)
            });
            dashboardBuilder.Services.AddDashboardActions(dashboardBuilder.Configuration);
            WebApplication dashboard = dashboardBuilder.Build();
            dashboard.MapDashboardActions();
            await dashboard.StartAsync(Token);
            return new Rig(server, dashboard, Address(dashboard), forwarded, authorizations);
        }

        public static HttpRequestMessage Declaration(string? origin)
        {
            HttpRequestMessage request = new(HttpMethod.Post, "/actions/slot-fault-declaration")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["agvId"] = "AGV-001",
                    ["slotNo"] = "3",
                    ["faultCategory"] = "LOCK",
                    ["note"] = "门已关，锁一直读未锁",
                    ["operatorId"] = "maintenance-7",
                    ["administratorRole"] = "MAINTENANCE_ADMINISTRATOR",
                    ["credential"] = "declaration-credential"
                })
            };
            if (origin is not null)
            {
                request.Headers.Add("Origin", origin);
            }
            return request;
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
