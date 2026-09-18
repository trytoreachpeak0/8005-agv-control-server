using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Commands;
using ControlServer.Host.Runtime.Fleet;
using ControlServer.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ControlServer.Tests;

/// <summary>
/// REQ-0356's entry point: the HTTP edge of the release on a person's confirmation.
/// </summary>
/// <remarks>
/// The release rules themselves are <see cref="EmergencyStopSupervisorTests"/>'. What is proved here
/// is the edge: no credential, no entry point; the wrong one, no call; an incomplete request or a
/// vehicle this server does not drive, refused before anything is read from RIoT; and the three
/// outcomes mapped so that only a release RIoT read back <c>OK</c> reports 200.
/// </remarks>
public sealed class EmergencyStopReleaseEndpointsTests
{
    private const string AgvId = "agv02";

    private const string VehicleKey = "BROKERX-0002";

    private static readonly DateTimeOffset Now = new(2026, 9, 15, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AnUnpopulatedCredentialVariableMakesTheEntryPointUnavailable()
    {
        await using EndpointFixture fixture = await EndpointFixture.CreateAsync();
        string variable = "CONTROL_SERVER_TEST_MISSING_" + Guid.NewGuid().ToString("N");

        var result = await fixture.PostAsync(variable, "Bearer anything", ValidRequest());

        ProblemHttpResult problem = Assert.IsType<ProblemHttpResult>(result.Result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, problem.StatusCode);
        Assert.Empty(fixture.Riot.Calls);
    }

    [Fact]
    public async Task AWrongBearerCredentialIsRejectedBeforeAnythingIsCalled()
    {
        await using EndpointFixture fixture = await EndpointFixture.CreateAsync();
        string variable = "CONTROL_SERVER_TEST_" + Guid.NewGuid().ToString("N");
        using EnvironmentVariableScope credential = new(variable, "release-credential");

        var result = await fixture.PostAsync(variable, "Bearer wrong-credential", ValidRequest());

        Assert.IsType<UnauthorizedHttpResult>(result.Result);
        Assert.Empty(fixture.Riot.Calls);
    }

    [Fact]
    public async Task ARequestWithoutAnOperatorIdIsIncomplete()
    {
        await using EndpointFixture fixture = await EndpointFixture.CreateAsync();
        string variable = "CONTROL_SERVER_TEST_" + Guid.NewGuid().ToString("N");
        using EnvironmentVariableScope credential = new(variable, "release-credential");

        var result = await fixture.PostAsync(
            variable, "Bearer release-credential", ValidRequest() with { OperatorId = " " });

        ProblemHttpResult problem = Assert.IsType<ProblemHttpResult>(result.Result);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, problem.StatusCode);
        Assert.Empty(fixture.Riot.Calls);
    }

    /// <summary>The vehicle is named, and a name this server does not drive is not resolved to anything.</summary>
    [Fact]
    public async Task AVehicleThisServerDoesNotDriveIsNotFound()
    {
        await using EndpointFixture fixture = await EndpointFixture.CreateAsync();
        string variable = "CONTROL_SERVER_TEST_" + Guid.NewGuid().ToString("N");
        using EnvironmentVariableScope credential = new(variable, "release-credential");

        var result = await fixture.PostAsync(
            variable, "Bearer release-credential", ValidRequest() with { AgvId = "agv09" });

        ProblemHttpResult problem = Assert.IsType<ProblemHttpResult>(result.Result);
        Assert.Equal(StatusCodes.Status404NotFound, problem.StatusCode);
        Assert.Empty(fixture.Riot.Calls);
    }

    /// <summary>A refusal is 409 and names every reason, so the person learns all of what is missing at once.</summary>
    [Fact]
    public async Task ARefusedReleaseIsAConflictNamingEveryReason()
    {
        await using EndpointFixture fixture = await EndpointFixture.CreateAsync();
        await fixture.LatchAsync();
        string variable = "CONTROL_SERVER_TEST_" + Guid.NewGuid().ToString("N");
        using EnvironmentVariableScope credential = new(variable, "release-credential");

        var result = await fixture.PostAsync(
            variable,
            "Bearer release-credential",
            ValidRequest() with { VehicleEmpty = false, AllDoorsClosed = false });

        ProblemHttpResult problem = Assert.IsType<ProblemHttpResult>(result.Result);
        Assert.Equal(StatusCodes.Status409Conflict, problem.StatusCode);
        IReadOnlyList<string> reasons = Assert.IsAssignableFrom<IReadOnlyList<string>>(
            problem.ProblemDetails.Extensions["reasons"]);
        Assert.Equal(
            ["EMERGENCY_VEHICLE_EMPTY_NOT_CONFIRMED", "EMERGENCY_DOORS_CLOSED_NOT_CONFIRMED"],
            reasons);
        Assert.DoesNotContain(fixture.Riot.Calls, call => call == RiotCommandTypeNames.CancelEmergency);
    }

    /// <summary>
    /// The usual field outcome: the release went out and RIoT has not cleared the latch yet. 202, not
    /// 200 — the vehicle has not been seen released.
    /// </summary>
    [Fact]
    public async Task AReleaseNotYetReadBackOkIsAccepted()
    {
        await using EndpointFixture fixture = await EndpointFixture.CreateAsync();
        await fixture.LatchAsync();
        string variable = "CONTROL_SERVER_TEST_" + Guid.NewGuid().ToString("N");
        using EnvironmentVariableScope credential = new(variable, "release-credential");

        var result = await fixture.PostAsync(variable, "Bearer release-credential", ValidRequest());

        Accepted<EmergencyStopReleaseResponse> accepted =
            Assert.IsType<Accepted<EmergencyStopReleaseResponse>>(result.Result);
        Assert.Equal(nameof(EmergencyStopAction.RecoveryUnconfirmed), accepted.Value!.Action);
        Assert.Equal(RiotVehicleEmergencyObservation.CanRecover, accepted.Value.EmergencyState);
        Assert.False(string.IsNullOrWhiteSpace(accepted.Value.CommandAuditId));
    }

    [Fact]
    public async Task AReleaseReadBackOkIsOk()
    {
        await using EndpointFixture fixture = await EndpointFixture.CreateAsync();
        await fixture.LatchAsync();
        fixture.Riot.LatchAfterRelease = RiotVehicleEmergencyObservation.Ok;
        string variable = "CONTROL_SERVER_TEST_" + Guid.NewGuid().ToString("N");
        using EnvironmentVariableScope credential = new(variable, "release-credential");

        var result = await fixture.PostAsync(variable, "Bearer release-credential", ValidRequest());

        Ok<EmergencyStopReleaseResponse> ok = Assert.IsType<Ok<EmergencyStopReleaseResponse>>(result.Result);
        Assert.Equal(AgvId, ok.Value!.AgvId);
        Assert.Equal(nameof(EmergencyStopAction.Recovered), ok.Value.Action);
        Assert.Equal(nameof(RiotOrderCommandOutcome.Confirmed), ok.Value.Outcome);
        Assert.Equal(
            [RiotCommandTypeNames.TriggerEmergency, RiotCommandTypeNames.CancelEmergency],
            fixture.Riot.Calls);
    }

    private static EmergencyStopReleaseRequest ValidRequest() =>
        new(AgvId, "operator-7", CauseCleared: true, VehicleEmpty: true, AllDoorsClosed: true, Note: null);

    private sealed class FakeRiot(TimeProvider clock)
        : IRiotOrderCommandGateway, IRiotVehicleEmergencyFacts, IRiotVehicleOrderFacts
    {
        public string? Latch { get; set; } = RiotVehicleEmergencyObservation.Ok;

        public string? LatchAfterRelease { get; set; } = RiotVehicleEmergencyObservation.CanRecover;

        public List<string> Calls { get; } = [];

        public Task<RiotCommandCallResult> IssueOrderCommandAsync(
            RiotOrderCommandKind kind,
            string orderId,
            string? reason,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("The release entry point issues no order commands.");

        public Task<RiotCommandCallResult> IssueEmergencyCommandAsync(
            RiotEmergencyCommandKind kind,
            string deviceKey,
            CancellationToken cancellationToken)
        {
            string commandType = RiotCommandTypeNames.For(kind);
            Calls.Add(commandType);
            Latch = kind == RiotEmergencyCommandKind.Trigger ? RiotVehicleEmergencyObservation.CanRecover : LatchAfterRelease;
            return Task.FromResult(new RiotCommandCallResult(
                RiotCommandCallDisposition.Accepted,
                new RiotOrderCallReceipt(commandType, "SdkAccepted", clock.GetUtcNow())));
        }

        public Task<RiotVehicleEmergencyObservation> ReadEmergencyStateAsync(
            string deviceKey,
            CancellationToken cancellationToken) =>
            Task.FromResult(new RiotVehicleEmergencyObservation(deviceKey, Latch, clock.GetUtcNow()));

        public Task<RiotVehicleOrderObservation> ReadUnfinishedOrdersAsync(
            string deviceKey,
            CancellationToken cancellationToken) =>
            Task.FromResult(new RiotVehicleOrderObservation(deviceKey, false, [], clock.GetUtcNow()));
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly string name;

        public EnvironmentVariableScope(string name, string value)
        {
            this.name = name;
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose() => Environment.SetEnvironmentVariable(name, null);
    }

    private sealed class EndpointFixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly ControlServerDbContext context;

        private EndpointFixture(SqliteConnection connection, ControlServerDbContext context)
        {
            this.connection = connection;
            this.context = context;
            TimeProvider clock = new FixedClock();
            Riot = new FakeRiot(clock);
            Supervisor = new EmergencyStopSupervisor(
                Riot,
                Riot,
                Riot,
                new RiotOrderCommandAuditStore(context),
                new VehicleFaultStore(context),
                Options.Create(new RiotCommandOptions()),
                clock,
                NullLogger<EmergencyStopSupervisor>.Instance);
            Roster = new VehicleRoster(Options.Create(new JourneyRuntimeOptions
            {
                AgvId = AgvId,
                VehicleKey = VehicleKey,
                AgvLifecycleGeneration = 1,
            }));
        }

        public FakeRiot Riot { get; }

        public EmergencyStopSupervisor Supervisor { get; }

        public VehicleRoster Roster { get; }

        public static async Task<EndpointFixture> CreateAsync()
        {
            SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            ControlServerDbContext context = new(
                new DbContextOptionsBuilder<ControlServerDbContext>().UseSqlite(connection).Options);
            await context.Database.MigrateAsync(TestContext.Current.CancellationToken);
            return new EndpointFixture(connection, context);
        }

        /// <summary>The server stops the vehicle, and the latch engages.</summary>
        public Task<EmergencyStopDecision> LatchAsync() => Supervisor.RequestStopAsync(
            new EmergencyStopRequest(
                new EmergencyStopSubject(AgvId, VehicleKey),
                EmergencyStopRequestSource.Automatic,
                RequesterIdentity: null,
                Reason: "VEHICLE_ORDER_FAILED",
                FaultGeneration: null),
            TestContext.Current.CancellationToken);

        public Task<Results<
            Ok<EmergencyStopReleaseResponse>,
            Accepted<EmergencyStopReleaseResponse>,
            UnauthorizedHttpResult,
            ProblemHttpResult>> PostAsync(
            string credentialVariable,
            string authorization,
            EmergencyStopReleaseRequest request)
        {
            DefaultHttpContext http = new();
            http.Request.Scheme = "http";
            http.Request.Headers.Authorization = authorization;
            return EmergencyStopReleaseEndpoints.HandleAsync(
                http,
                request,
                Supervisor,
                Roster,
                Options.Create(new EmergencyStopReleaseOptions
                {
                    Enabled = true,
                    CredentialEnvironmentVariable = credentialVariable,
                }),
                TestContext.Current.CancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            await context.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
