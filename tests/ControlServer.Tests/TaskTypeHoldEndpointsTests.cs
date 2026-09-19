using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime.TaskTypeStations;
using ControlServer.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static ControlServer.Tests.TaskTypeStationTestData;

namespace ControlServer.Tests;

/// <summary>
/// 看板人工收紧的服务端入口（REQ-0340 收紧半边、REQ-0348，control-server#162）：只收本机来源（回环或本机连接地址）、不设凭据、只能收紧。
/// </summary>
public sealed class TaskTypeHoldEndpointsTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ARequestFromAnotherMachineIsForbiddenWhateverAddressTheServerListensOn()
    {
        await using TaskTypeStationPersistenceFixture fixture = await TaskTypeStationPersistenceFixture.CreateAsync();
        await TaskTypeHoldTestKit.ActivateAsync(fixture, 25, GateBinding, StagingBinding);

        IResult listeningOnLoopback = await Post(fixture, ValidRequest(), IPAddress.Parse("172.19.205.30"));
        IResult listeningOnThePlantInterface = await Post(
            fixture, ValidRequest(), IPAddress.Parse("172.19.205.30"), local: IPAddress.Parse("172.19.205.222"));

        Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(listeningOnLoopback));
        Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(listeningOnThePlantInterface));
        Assert.Empty(await fixture.Holds.ListUnreleasedAsync(25, Token));
    }

    [Fact]
    public async Task ARequestFromAnotherMachineOnAConnectionWithNoLocalAddressIsForbidden()
    {
        // control-server#201 (#162 re-review): with no local address, only loopback is this machine. The helper used to
        // fill in loopback for a missing local address, so this case was never exercised.
        await using TaskTypeStationPersistenceFixture fixture = await TaskTypeStationPersistenceFixture.CreateAsync();
        await TaskTypeHoldTestKit.ActivateAsync(fixture, 25, GateBinding, StagingBinding);

        IResult result = await Post(
            fixture, ValidRequest() with { ClaimedRole = "厂长" }, IPAddress.Parse("172.19.205.30"), noLocalAddress: true);
        fixture.Context.ChangeTracker.Clear();

        Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(result));
        Assert.Empty(await fixture.Holds.ListUnreleasedAsync(25, Token));
        AdministratorAuditRecordRow audit = Assert.Single(await fixture.Context.Set<AdministratorAuditRecordRow>()
            .Where(row => row.Action == TaskTypeHoldEndpoints.HoldRequestedAction)
            .ToArrayAsync(Token));
        Assert.Equal(GovernanceActionOutcome.Failed, audit.Outcome);
        Assert.Null(audit.ClaimedAdministratorRole);
        using JsonDocument detail = JsonDocument.Parse(audit.DetailJson);
        Assert.Equal("172.19.205.30", detail.RootElement.GetProperty("remoteAddress").GetString());
        Assert.Equal(JsonValueKind.Null, detail.RootElement.GetProperty("localAddress").ValueKind);
        Assert.Equal("FORBIDDEN_NOT_LOCAL", detail.RootElement.GetProperty("result").GetString());
        Assert.False(detail.RootElement.TryGetProperty("reason", out _));
        Assert.False(detail.RootElement.TryGetProperty("claimedRole", out _));
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("::ffff:127.0.0.1", true)]
    [InlineData("172.19.205.30", false)]
    [InlineData("::ffff:172.19.205.30", false)]
    public void WithNoLocalAddressOnlyLoopbackIsThisMachine(string remote, bool expected) =>
        Assert.Equal(expected, TaskTypeHoldEndpoints.IsFromThisMachine(IPAddress.Parse(remote), local: null));

    [Theory]
    [InlineData("172.19.205.222", "172.19.205.222")]
    [InlineData("::ffff:172.19.205.222", "172.19.205.222")]
    [InlineData("172.19.205.222", "::ffff:172.19.205.222")]
    public async Task ARequestFromThisMachinesOwnPlantAddressIsTakenBecauseThatIsHowTheDashboardReachesAServerBoundToIt(
        string remote, string local)
    {
        // Install-ControlServerLocal.ps1 binds the HTTP surface to the plant-facing interface and points the dashboard's
        // controlServerBaseUrl at that same address, so the dashboard's forwarded request arrives from the machine's own
        // interface address, not from loopback.
        await using TaskTypeStationPersistenceFixture fixture = await TaskTypeStationPersistenceFixture.CreateAsync();
        await TaskTypeHoldTestKit.ActivateAsync(fixture, 25, GateBinding, StagingBinding);

        IResult result = await Post(fixture, ValidRequest(), IPAddress.Parse(remote), local: IPAddress.Parse(local));

        Assert.Equal(StatusCodes.Status201Created, StatusOf(result));
        Assert.Single(await fixture.Holds.ListUnreleasedAsync(25, Token));
    }

    [Fact]
    public async Task AForbiddenRequestIsAuditedByAddressAloneAndNoneOfWhatItClaimedIsKept()
    {
        // Anyone on the plant network can reach a server bound to the plant interface. What a refused caller typed must
        // not become immutable audit text.
        await using TaskTypeStationPersistenceFixture fixture = await TaskTypeStationPersistenceFixture.CreateAsync();
        await TaskTypeHoldTestKit.ActivateAsync(fixture, 25, GateBinding, StagingBinding);

        IResult result = await Post(
            fixture,
            ValidRequest() with { Reason = new string('x', 4000), ClaimedRole = "厂长" },
            IPAddress.Parse("172.19.205.30"),
            local: IPAddress.Parse("172.19.205.222"));
        fixture.Context.ChangeTracker.Clear();

        Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(result));
        AdministratorAuditRecordRow audit = Assert.Single(await fixture.Context.Set<AdministratorAuditRecordRow>()
            .Where(row => row.Action == TaskTypeHoldEndpoints.HoldRequestedAction)
            .ToArrayAsync(Token));
        Assert.Equal(GovernanceActionOutcome.Failed, audit.Outcome);
        Assert.Null(audit.ClaimedAdministratorRole);
        using JsonDocument detail = JsonDocument.Parse(audit.DetailJson);
        Assert.Equal("172.19.205.30", detail.RootElement.GetProperty("remoteAddress").GetString());
        Assert.Equal("FORBIDDEN_NOT_LOCAL", detail.RootElement.GetProperty("result").GetString());
        Assert.False(detail.RootElement.TryGetProperty("reason", out _));
        Assert.False(detail.RootElement.TryGetProperty("claimedRole", out _));
        Assert.DoesNotContain("xxxx", audit.DetailJson, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(TaskTypeHoldEndpoints.MaxReasonLength + 1, 1, "REASON_TOO_LONG")]
    [InlineData(1, TaskTypeHoldEndpoints.MaxClaimedRoleLength + 1, "CLAIMED_ROLE_TOO_LONG")]
    public async Task AnOverlongReasonOrClaimedRoleIsRefusedWithACodeAndIsNotWrittenIntoTheAudit(
        int reasonLength, int roleLength, string code)
    {
        await using TaskTypeStationPersistenceFixture fixture = await TaskTypeStationPersistenceFixture.CreateAsync();
        await TaskTypeHoldTestKit.ActivateAsync(fixture, 25, GateBinding, StagingBinding);

        IResult result = await Post(
            fixture,
            ValidRequest() with { Reason = new string('r', reasonLength), ClaimedRole = new string('c', roleLength) },
            IPAddress.Loopback);
        fixture.Context.ChangeTracker.Clear();

        Assert.Equal(StatusCodes.Status422UnprocessableEntity, StatusOf(result));
        ProblemHttpResult problem = Assert.IsType<ProblemHttpResult>(result);
        string[] codes = Assert.IsType<string[]>(problem.ProblemDetails.Extensions["codes"]);
        Assert.Equal([code], codes);
        Assert.Empty(await fixture.Holds.ListUnreleasedAsync(25, Token));
        AdministratorAuditRecordRow audit = Assert.Single(await fixture.Context.Set<AdministratorAuditRecordRow>()
            .Where(row => row.Action == TaskTypeHoldEndpoints.HoldRequestedAction)
            .ToArrayAsync(Token));
        Assert.True(audit.DetailJson.Length < 2000, $"audit detail is {audit.DetailJson.Length} characters");
        Assert.True((audit.ClaimedAdministratorRole?.Length ?? 0) <= TaskTypeHoldEndpoints.MaxClaimedRoleLength);
    }

    [Fact]
    public async Task AReasonAndClaimedRoleAtTheLimitAreTaken()
    {
        await using TaskTypeStationPersistenceFixture fixture = await TaskTypeStationPersistenceFixture.CreateAsync();
        await TaskTypeHoldTestKit.ActivateAsync(fixture, 25, GateBinding, StagingBinding);

        IResult result = await Post(
            fixture,
            ValidRequest() with
            {
                Reason = new string('r', TaskTypeHoldEndpoints.MaxReasonLength),
                ClaimedRole = new string('c', TaskTypeHoldEndpoints.MaxClaimedRoleLength)
            },
            IPAddress.Loopback);

        Assert.Equal(StatusCodes.Status201Created, StatusOf(result));
    }

    [Theory]
    [InlineData(99, TransportTaskTypes.WireToGate, "站点被占作他用")]
    [InlineData(25, "WIRE_TO_NOWHERE", "站点被占作他用")]
    [InlineData(25, TransportTaskTypes.WireToGate, " ")]
    public async Task AnUnknownMapAnUnknownTaskTypeOrAMissingReasonIsUnprocessable(int mapId, string taskType, string reason)
    {
        await using TaskTypeStationPersistenceFixture fixture = await TaskTypeStationPersistenceFixture.CreateAsync();
        await TaskTypeHoldTestKit.ActivateAsync(fixture, 25, GateBinding, StagingBinding);

        IResult result = await Post(fixture, new TaskTypeHoldRequest(mapId, taskType, reason, null), IPAddress.Loopback);

        Assert.Equal(StatusCodes.Status422UnprocessableEntity, StatusOf(result));
        Assert.Empty(await fixture.Holds.ListUnreleasedAsync(25, Token));
        Assert.Empty(await fixture.Holds.ListUnreleasedAsync(99, Token));
    }

    [Fact]
    public async Task ALoopbackRequestHoldsTheTaskTypeAtOnceAndAuditsWhoAskedAsADeploymentNotAPerson()
    {
        await using TaskTypeStationPersistenceFixture fixture = await TaskTypeStationPersistenceFixture.CreateAsync();
        long version = await TaskTypeHoldTestKit.ActivateAsync(fixture, 25, GateBinding, StagingBinding);
        await TaskTypeHoldTestKit.SeedJourneyUnderWayAsync(fixture.Context, "D-1", TransportTaskTypes.WireToGate);

        IResult result = await Post(
            fixture, ValidRequest() with { ClaimedRole = "班组长" }, IPAddress.IPv6Loopback);
        fixture.Context.ChangeTracker.Clear();

        Assert.Equal(StatusCodes.Status201Created, StatusOf(result));
        TaskTypeStationHold hold = Assert.Single(await fixture.Holds.ListUnreleasedAsync(25, Token));
        Assert.Equal(TransportTaskTypes.WireToGate, hold.TaskType);
        Assert.Equal(TaskTypeStationHoldSource.Manual, hold.Source);
        Assert.Equal(TaskTypeHoldEndpoints.ManualHoldReasonCode, hold.ReasonCode);
        Assert.False(await fixture.Holds.IsHeldAsync(25, TransportTaskTypes.StagingToWire, Token));

        AdministratorAuditRecordRow audit = Assert.Single(await fixture.Context.Set<AdministratorAuditRecordRow>()
            .Where(row => row.Action == TaskTypeHoldEndpoints.HoldRequestedAction)
            .ToArrayAsync(Token));
        Assert.Equal(AuditActorAttribution.NotAttributableToNaturalPerson, audit.ActorAttribution);
        Assert.StartsWith(AuditActorAttribution.DeploymentIdentityPrefix, audit.ActorIdentity, StringComparison.Ordinal);
        Assert.Equal("班组长", audit.ClaimedAdministratorRole);
        Assert.Equal(GovernedObjectKind.PublicStationBinding, audit.ObjectKind);
        Assert.Equal("map-25", audit.ObjectId);
        Assert.Equal(version, audit.Version);
        Assert.Equal(GovernanceActionOutcome.Succeeded, audit.Outcome);
        using JsonDocument detail = JsonDocument.Parse(audit.DetailJson);
        JsonElement root = detail.RootElement;
        Assert.Equal(25, root.GetProperty("mapId").GetInt32());
        Assert.Equal(TransportTaskTypes.WireToGate, root.GetProperty("taskType").GetString());
        Assert.Equal(210, root.GetProperty("stationRiotId").GetInt32());
        Assert.Equal("关卡", root.GetProperty("stationName").GetString());
        Assert.Equal(version, root.GetProperty("bindingSetVersion").GetInt64());
        Assert.Equal("关卡门口堆了料车", root.GetProperty("reason").GetString());
        Assert.Equal("班组长", root.GetProperty("claimedRole").GetString());
        Assert.Equal(1, root.GetProperty("inFlightDemands").GetInt32());
        Assert.Equal("CREATED", root.GetProperty("result").GetString());
        Assert.Equal(hold.HoldId, root.GetProperty("holdId").GetString());
    }

    [Fact]
    public async Task AMapWhoseActivationWasClosedByHandStillTakesAManualHold()
    {
        // #161's tombstone leaves the Map with no active version until the next activation, and an activation does not
        // lift a manual hold. Refusing the hold here would leave the person no way to keep the task type stopped through
        // that activation -- the fail-safe direction is to take it.
        await using TaskTypeStationPersistenceFixture fixture = await TaskTypeStationPersistenceFixture.CreateAsync();
        long closedVersion = await TaskTypeHoldTestKit.ActivateAsync(fixture, 25, GateBinding, StagingBinding);
        TaskTypeStationActiveBindingSetRow pointer = fixture.Context.Set<TaskTypeStationActiveBindingSetRow>().Single();
        pointer.ActiveVersion = null;
        pointer.State = TaskTypeStationActivationState.ClosedManually;
        await fixture.Context.SaveChangesAsync(Token);
        fixture.Context.ChangeTracker.Clear();

        IResult result = await Post(fixture, ValidRequest(), IPAddress.Loopback);
        fixture.Context.ChangeTracker.Clear();

        Assert.Equal(StatusCodes.Status201Created, StatusOf(result));
        Assert.Equal(TransportTaskTypes.WireToGate, Assert.Single(await fixture.Holds.ListUnreleasedAsync(25, Token)).TaskType);
        AdministratorAuditRecordRow audit = Assert.Single(await fixture.Context.Set<AdministratorAuditRecordRow>()
            .Where(row => row.Action == TaskTypeHoldEndpoints.HoldRequestedAction)
            .ToArrayAsync(Token));
        Assert.Null(audit.Version);
        using JsonDocument detail = JsonDocument.Parse(audit.DetailJson);
        Assert.Equal(TaskTypeStationActivationState.ClosedManually, detail.RootElement.GetProperty("activationState").GetString());
        Assert.Equal(JsonValueKind.Null, detail.RootElement.GetProperty("bindingSetVersion").ValueKind);
        Assert.NotEqual(0, closedVersion);
    }

    [Fact]
    public async Task ASecondRequestForAHeldTaskTypeAddsNoHoldButIsStillAudited()
    {
        await using TaskTypeStationPersistenceFixture fixture = await TaskTypeStationPersistenceFixture.CreateAsync();
        await TaskTypeHoldTestKit.ActivateAsync(fixture, 25, GateBinding, StagingBinding);

        IResult first = await Post(fixture, ValidRequest(), IPAddress.Loopback);
        IResult second = await Post(fixture, ValidRequest() with { Reason = "再点一次" }, IPAddress.Loopback);
        fixture.Context.ChangeTracker.Clear();

        Assert.Equal(StatusCodes.Status201Created, StatusOf(first));
        Assert.Equal(StatusCodes.Status200OK, StatusOf(second));
        Assert.Single(await fixture.Holds.ListUnreleasedAsync(25, Token));
        string[] results =
        [
            .. (await fixture.Context.Set<AdministratorAuditRecordRow>()
                    .Where(row => row.Action == TaskTypeHoldEndpoints.HoldRequestedAction)
                    .ToArrayAsync(Token))
                .OrderBy(row => row.RecordedAtUtcTicks)
                .Select(row => JsonDocument.Parse(row.DetailJson).RootElement.GetProperty("result").GetString()!)
        ];
        Assert.Equal(["CREATED", "ALREADY_HELD"], results);
    }

    [Fact]
    public async Task TheRouteOnlyTightensThereIsNoWayToReleaseAHoldOverHttp()
    {
        await using TaskTypeStationPersistenceFixture fixture = await TaskTypeStationPersistenceFixture.CreateAsync();
        await TaskTypeHoldTestKit.ActivateAsync(fixture, 25, GateBinding, StagingBinding);

        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddDbContext<ControlServerDbContext>(options => options.UseSqlite(fixture.Connection));
        builder.Services.AddSingleton(new GovernanceDeploymentIdentity("deployment:8005-controlserver@test"));
        builder.Services.AddSingleton(AuditRetentionPolicy.Default);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddScoped<GovernanceStore>();
        builder.Services.AddScoped<IGovernanceAuditWriter>(services => services.GetRequiredService<GovernanceStore>());
        builder.Services.AddScoped(services => new GovernedConfigurationPublisher(
            services.GetRequiredService<GovernanceStore>(), services.GetRequiredService<GovernanceStore>()));
        builder.Services.AddScoped<ITaskTypeStationRuleStore, TaskTypeStationRuleStore>();
        builder.Services.AddScoped<ITaskTypeStationBindingStore, TaskTypeStationBindingStore>();
        builder.Services.AddScoped<ITaskTypeStationHoldStore, TaskTypeStationHoldStore>();
        await using WebApplication app = builder.Build();
        app.MapTaskTypeHolds();
        await app.StartAsync(Token);
        string address = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using HttpClient client = new() { BaseAddress = new Uri(address) };

        using HttpResponseMessage created = await client.PostAsJsonAsync(TaskTypeHoldEndpoints.Route, ValidRequest(), Token);
        string holdId = Assert.Single(await fixture.Holds.ListUnreleasedAsync(25, Token)).HoldId;
        HttpStatusCode[] releaseAttempts =
        [
            await SendAsync(client, HttpMethod.Delete, TaskTypeHoldEndpoints.Route),
            await SendAsync(client, HttpMethod.Put, TaskTypeHoldEndpoints.Route),
            await SendAsync(client, HttpMethod.Patch, TaskTypeHoldEndpoints.Route),
            await SendAsync(client, HttpMethod.Delete, $"{TaskTypeHoldEndpoints.Route}/{holdId}"),
            await SendAsync(client, HttpMethod.Post, $"{TaskTypeHoldEndpoints.Route}/{holdId}/release"),
            await SendAsync(client, HttpMethod.Post, $"{TaskTypeHoldEndpoints.Route}/release"),
            await SendAsync(client, HttpMethod.Post, "/api/task-type-hold-releases"),
        ];
        await app.StopAsync(Token);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.All(releaseAttempts, status => Assert.False(
            (int)status is >= 200 and < 300, $"A release attempt answered {(int)status}."));
        Assert.Single(await fixture.Holds.ListUnreleasedAsync(25, Token));
    }

    private static TaskTypeHoldRequest ValidRequest() =>
        new(25, TransportTaskTypes.WireToGate, "关卡门口堆了料车", null);

    private static async Task<HttpStatusCode> SendAsync(HttpClient client, HttpMethod method, string path)
    {
        using HttpRequestMessage request = new(method, path)
        {
            Content = JsonContent.Create(new { mapId = 25, taskType = TransportTaskTypes.WireToGate, reason = "解除" })
        };
        using HttpResponseMessage response = await client.SendAsync(request, Token);
        return response.StatusCode;
    }

    private static async Task<IResult> Post(
        TaskTypeStationPersistenceFixture fixture,
        TaskTypeHoldRequest request,
        IPAddress remote,
        IPAddress? local = null,
        bool noLocalAddress = false)
    {
        DefaultHttpContext context = new();
        context.Connection.RemoteIpAddress = remote;
        context.Connection.LocalIpAddress = noLocalAddress ? null : local ?? IPAddress.Loopback;
        IResult result = await TaskTypeHoldEndpoints.HandleAsync(
            context,
            request,
            fixture.Context,
            fixture.Rules,
            fixture.Bindings,
            fixture.Holds,
            fixture.Governance,
            new GovernanceDeploymentIdentity("deployment:8005-controlserver@test"),
            TimeProvider.System,
            Token);
        return result;
    }

    private static int StatusOf(IResult result) =>
        Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode ?? StatusCodes.Status200OK;
}
