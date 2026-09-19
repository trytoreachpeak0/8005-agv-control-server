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
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static ControlServer.Tests.TaskTypeStationTestData;

namespace ControlServer.Tests;

/// <summary>
/// 看板人工收紧的服务端入口（REQ-0340 收紧半边、REQ-0348，control-server#162）：只收回环来源、不设凭据、只能收紧。
/// </summary>
public sealed class TaskTypeHoldEndpointsTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ARequestFromAnywhereButLoopbackIsForbiddenWhateverAddressTheServerListensOn()
    {
        await using TaskTypeStationPersistenceFixture fixture = await TaskTypeStationPersistenceFixture.CreateAsync();
        await TaskTypeHoldTestKit.ActivateAsync(fixture, 25, GateBinding, StagingBinding);

        IResult result = await Post(fixture, ValidRequest(), IPAddress.Parse("172.19.205.30"));

        Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(result));
        Assert.Empty(await fixture.Holds.ListUnreleasedAsync(25, Token));
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
    public async Task AMapWhoseActivationWasClosedByHandCanStillBeHeldSoTheHoldOutlivesTheNextActivation()
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
        IPAddress remote)
    {
        DefaultHttpContext context = new();
        context.Connection.RemoteIpAddress = remote;
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
