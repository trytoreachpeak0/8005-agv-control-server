using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Infrastructure.Persistence;
using ControlServer.Host.Runtime.Faults;
using ControlServer.Host.Runtime.Fleet;
using ControlServer.Host.Runtime.Recovery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ControlServer.Tests;

/// <summary>
/// 站点独占人工释放的 HTTP 入口（control-server#419）：故障人工恢复入口那把凭据；凭据没配不可用、不对什么都不做；没点名站 422；
/// 放了 200，拒绝 409 并列出全部理由码；服务端在线时读 RIoT 做交叉核对。
/// </summary>
/// <remarks>判定本身是 <see cref="Batch8StationExclusivityManualReleaseTests"/> 的事，这里证边界。</remarks>
public sealed class StationExclusivityReleaseEndpointsTests
{
    private const string Credential = "station-release-credential";
    private const string KeyA = "KEY-A";

    private static readonly DateTimeOffset At = Batch7JourneyFixture.Now;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AnUnpopulatedCredentialVariableMakesTheEntryUnavailable()
    {
        await using Batch7JourneyFixture fixture = await HeldAsync();

        var result = await PostAsync(
            fixture, "CONTROL_SERVER_TEST_MISSING_" + Guid.NewGuid().ToString("N"), "Bearer anything", Request());

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, Assert.IsType<ProblemHttpResult>(result.Result).StatusCode);
        await AssertUntouchedAsync(fixture, audits: 0);
    }

    [Fact]
    public async Task AWrongBearerCredentialIsRejectedBeforeAnythingIsReadOrWritten()
    {
        await using Batch7JourneyFixture fixture = await HeldAsync();
        using CredentialScope scope = new();

        var result = await PostAsync(fixture, scope.Variable, "Bearer wrong", Request());

        Assert.IsType<UnauthorizedHttpResult>(result.Result);
        await AssertUntouchedAsync(fixture, audits: 0);
    }

    [Theory]
    [InlineData(0, 202)]
    [InlineData(25, 0)]
    public async Task ARequestNamingNoStationIsIncomplete(int mapId, int stationId)
    {
        await using Batch7JourneyFixture fixture = await HeldAsync();
        using CredentialScope scope = new();

        var result = await PostAsync(
            fixture, scope.Variable, $"Bearer {Credential}", Request() with { MapId = mapId, StationId = stationId });

        Assert.Equal(StatusCodes.Status422UnprocessableEntity, Assert.IsType<ProblemHttpResult>(result.Result).StatusCode);
        await AssertUntouchedAsync(fixture, audits: 0);
    }

    /// <summary>缺现场核实记录是一条没满足的判据：409，理由码列在 <c>codes</c> 里，审计记下这次失败。</summary>
    [Fact]
    public async Task ARefusalIsAConflictListingEveryCodeAndIsAudited()
    {
        await using Batch7JourneyFixture fixture = await HeldAsync();
        using CredentialScope scope = new();

        var result = await PostAsync(
            fixture, scope.Variable, $"Bearer {Credential}", Request() with { SiteVerification = null, OperatorId = "" });

        ProblemHttpResult problem = Assert.IsType<ProblemHttpResult>(result.Result);
        Assert.Equal(StatusCodes.Status409Conflict, problem.StatusCode);
        Assert.Equal(
            [StationExclusivityManualRelease.OperatorRequired, StationExclusivityManualRelease.SiteVerificationRequired],
            Assert.IsAssignableFrom<IReadOnlyList<string>>(problem.ProblemDetails.Extensions["codes"]));
        await AssertUntouchedAsync(fixture, audits: 1);
    }

    /// <summary>服务端在线时读 RIoT：车在线且报在这个站上，409 <c>VEHICLE_REPORTED_AT_STATION</c>。</summary>
    [Fact]
    public async Task TheServerChecksRiotAndRefusesAVehicleReportedAtTheStation()
    {
        await using Batch7JourneyFixture fixture = await HeldAsync();
        using CredentialScope scope = new();

        var result = await PostAsync(
            fixture, scope.Variable, $"Bearer {Credential}", Request(), new Facts(connected: true, station: 202));

        ProblemHttpResult problem = Assert.IsType<ProblemHttpResult>(result.Result);
        Assert.Equal(
            [StationExclusivityManualRelease.VehicleReportedAtStation],
            Assert.IsAssignableFrom<IReadOnlyList<string>>(problem.ProblemDetails.Extensions["codes"]));
        Assert.Equal(StationExclusivityManualRelease.CrossCheckAtThisStation, problem.ProblemDetails.Extensions["riotCrossCheck"]);
        await AssertUntouchedAsync(fixture, audits: 1);
    }

    [Fact]
    public async Task AReleaseIsOkWithTheHoldingItReleasedAndItsAuditRecord()
    {
        await using Batch7JourneyFixture fixture = await HeldAsync();
        using CredentialScope scope = new();

        var result = await PostAsync(
            fixture, scope.Variable, $"Bearer {Credential}", Request(), new Facts(connected: false, station: null));

        StationExclusivityReleaseResponse body = Assert.IsType<Ok<StationExclusivityReleaseResponse>>(result.Result).Value!;
        Assert.Equal(
            ("RELEASED", StationExclusivityKinds.FixedTaskStation, KeyA, "journey:a", StationExclusivityManualRelease.CrossCheckOffline),
            (body.Outcome, body.StationKind, body.HolderVehicleKey, body.HolderJourneyId, body.RiotCrossCheck));
        await using ControlServerDbContext read = fixture.NewContext();
        Assert.Empty(await read.Set<StationExclusivityRow>().ToArrayAsync(Token));
        AdministratorAuditRecordRow audit = await read.Set<AdministratorAuditRecordRow>()
            .SingleAsync(row => row.Action == StationExclusivityManualRelease.AuditAction, Token);
        Assert.Equal((body.AuditRecordId, GovernanceActionOutcome.Succeeded), (audit.AuditRecordId, audit.Outcome));
    }

    /// <summary>
    /// 挂不挂由故障恢复的开关决定，经 Program 调用的同一个方法（#422 审查建议 3）：关着时两个入口都不在路由表里，开着时都在。
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    [InlineData("true")]
    public async Task TheEntryIsMappedOnlyWithTheFaultRecoverySwitchOn(string? enabled)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        if (enabled is not null)
        {
            builder.Configuration[VehicleFaultRecoveryEndpoints.EnabledKey] = enabled;
        }
        // Registered so that the handlers' parameters read as services, never resolved: routing is all this looks at.
        builder.Services.AddScoped<VehicleFaultRecoveryService>(_ => throw new InvalidOperationException("not resolved"));
        builder.Services.AddScoped<WaitingPointArrivalSettlement>(_ => throw new InvalidOperationException("not resolved"));
        builder.Services.AddScoped<RecoverySessionAdministratorClose>(_ => throw new InvalidOperationException("not resolved"));
        builder.Services.AddScoped<VehicleRoster>(_ => throw new InvalidOperationException("not resolved"));
        builder.Services.AddScoped<ControlServerDbContext>(_ => throw new InvalidOperationException("not resolved"));
        builder.Services.AddScoped<IGovernanceAuditWriter>(_ => throw new InvalidOperationException("not resolved"));
        builder.Services.AddSingleton(TimeProvider.System);
        await using WebApplication app = builder.Build();

        bool mapped = app.MapVehicleFaultRecoveryEntriesWhenEnabled();

        string[] routes =
        [
            .. ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>()
                .Select(endpoint => endpoint.RoutePattern.RawText!)
        ];
        bool on = enabled == "true";
        Assert.Equal(on, mapped);
        Assert.Equal(on, routes.Contains(StationExclusivityReleaseEndpoints.Route));
        Assert.Equal(on, routes.Contains(VehicleFaultRecoveryEndpoints.Route));
        // control-server#447: the waiting point arrival settlement hangs on the same switch.
        Assert.Equal(on, routes.Contains(WaitingPointArrivalSettlementEndpoints.Route));
        // control-server#483: so does the exception recovery session closing.
        Assert.Equal(on, routes.Contains(RecoverySessionAdministratorCloseEndpoints.Route));
    }

    // ---- helpers ------------------------------------------------------------------------------------------------

    private static StationExclusivityReleaseHttpRequest Request() =>
        new(25, 202, KeyA, "OP-7", "A 车离线，已拖离关卡", "SITE-2026-0930-01", null);

    private static async Task<Batch7JourneyFixture> HeldAsync()
    {
        Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        await new StationExclusivityStore(fixture.NewContext()).TryAcquireAsync(
            new StationExclusivityRequest(25, 202, StationExclusivityKinds.FixedTaskStation, StationExclusivityStates.Occupied, null),
            KeyA, "journey:a", At, Token);
        return fixture;
    }

    private static async Task AssertUntouchedAsync(Batch7JourneyFixture fixture, int audits)
    {
        await using ControlServerDbContext read = fixture.NewContext();
        Assert.Equal("journey:a", (await read.Set<StationExclusivityRow>().SingleAsync(Token)).JourneyId);
        Assert.Equal(
            audits,
            await read.Set<AdministratorAuditRecordRow>().CountAsync(row => row.Action == StationExclusivityManualRelease.AuditAction, Token));
    }

    private static async Task<Results<Ok<StationExclusivityReleaseResponse>, UnauthorizedHttpResult, ProblemHttpResult>> PostAsync(
        Batch7JourneyFixture fixture,
        string credentialVariable,
        string authorization,
        StationExclusivityReleaseHttpRequest request,
        IRiotVehicleFacts? facts = null)
    {
        ServiceCollection services = new();
        if (facts is not null)
        {
            services.AddSingleton(facts);
        }
        DefaultHttpContext http = new() { RequestServices = services.BuildServiceProvider() };
        http.Request.Scheme = "http";
        http.Request.Headers.Authorization = authorization;
        await using ControlServerDbContext context = fixture.NewContext();
        return await StationExclusivityReleaseEndpoints.HandleAsync(
            http,
            request,
            context,
            new GovernanceStore(context, new GovernanceDeploymentIdentity("deployment:8005-controlserver@test"), AuditRetentionPolicy.Default),
            Options.Create(new VehicleFaultRecoveryOptions { Enabled = true, CredentialEnvironmentVariable = credentialVariable }),
            new FixedClock(At.AddMinutes(30)),
            Token);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class Facts(bool connected, int? station) : IRiotVehicleFacts
    {
        public Task<RiotVehicleObservation> ReadVehicleAsync(string vehicleKey, CancellationToken cancellationToken) =>
            Task.FromResult(new RiotVehicleObservation(vehicleKey, connected, true, "IDLE", "MAP-25", station, 50, "DISCHARGING", 0, At));

        public Task<RiotOrderObservation> ReconcileByUpperIdAsync(string upperId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A station release reads no order.");

        public Task<RiotOrderObservation> CreateAsync(OrderIntent intent, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A station release creates no order.");
    }

    private sealed class CredentialScope : IDisposable
    {
        public CredentialScope()
        {
            Environment.SetEnvironmentVariable(Variable, Credential);
        }

        public string Variable { get; } = "CONTROL_SERVER_TEST_" + Guid.NewGuid().ToString("N");

        public void Dispose() => Environment.SetEnvironmentVariable(Variable, null);
    }
}
