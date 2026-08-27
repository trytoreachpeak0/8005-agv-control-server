using ControlServer.Application;
using ControlServer.Host.Runtime;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace ControlServer.Tests;

public sealed class OnboardVehicleSafetyEndpointsTests
{
    [Fact]
    public async Task PlainHttpIsRejectedBeforeCredentialOrRiotFactsAreRead()
    {
        DefaultHttpContext context = new();
        context.Request.Scheme = "http";
        RecordingSafetyFacts facts = new();

        var result = await OnboardVehicleSafetyEndpoints.HandleAsync(
            context,
            facts,
            Options.Create(JourneyOptions()),
            Options.Create(ProjectionOptions("UNUSED_CREDENTIAL")),
            TestContext.Current.CancellationToken);

        StatusCodeHttpResult status = Assert.IsType<StatusCodeHttpResult>(result.Result);
        Assert.Equal(StatusCodes.Status426UpgradeRequired, status.StatusCode);
        Assert.Equal(0, facts.ReadCount);
    }

    [Fact]
    public async Task InvalidExternalBearerCredentialIsRejectedWithoutReadingRiotFacts()
    {
        string variable = "CONTROL_SERVER_TEST_" + Guid.NewGuid().ToString("N");
        using EnvironmentVariableScope credential = new(variable, "expected-credential");
        DefaultHttpContext context = new();
        context.Request.Scheme = "https";
        context.Request.Headers.Authorization = "Bearer wrong-credential";
        RecordingSafetyFacts facts = new();

        var result = await OnboardVehicleSafetyEndpoints.HandleAsync(
            context,
            facts,
            Options.Create(JourneyOptions()),
            Options.Create(ProjectionOptions(variable)),
            TestContext.Current.CancellationToken);

        Assert.IsType<UnauthorizedHttpResult>(result.Result);
        Assert.Equal("Bearer", context.Response.Headers.WWWAuthenticate);
        Assert.Equal(0, facts.ReadCount);
    }

    [Fact]
    public async Task AuthenticatedHttpsRequestReturnsFailClosedRiotProjectionWithoutExposingCredentials()
    {
        string variable = "CONTROL_SERVER_TEST_" + Guid.NewGuid().ToString("N");
        using EnvironmentVariableScope credential = new(variable, "onboard-only-credential");
        DefaultHttpContext context = new();
        context.Request.Scheme = "https";
        context.Request.Headers.Authorization = "Bearer onboard-only-credential";
        RecordingSafetyFacts facts = new()
        {
            Observation = new RiotVehicleSafetyObservation(
                "VEHICLE-KEY-01",
                RiotVehicleMotionState.Unknown,
                new DateTimeOffset(2026, 8, 27, 2, 0, 0, TimeSpan.Zero),
                "RIOT_BEHAVIOR_LAB_R41",
                ["RIOT_MOVEMENT_NOT_FINISHED"])
        };

        var result = await OnboardVehicleSafetyEndpoints.HandleAsync(
            context,
            facts,
            Options.Create(JourneyOptions()),
            Options.Create(ProjectionOptions(variable)),
            TestContext.Current.CancellationToken);

        Ok<OnboardVehicleSafetyResponse> ok = Assert.IsType<Ok<OnboardVehicleSafetyResponse>>(result.Result);
        Assert.NotNull(ok.Value);
        Assert.Equal("UNKNOWN", ok.Value.MotionState);
        Assert.Equal(["RIOT_MOVEMENT_NOT_FINISHED"], ok.Value.ReasonCodes);
        Assert.Equal(1, facts.ReadCount);
        Assert.Equal("VEHICLE-KEY-01", facts.LastVehicleKey);
        Assert.Equal("no-store", context.Response.Headers.CacheControl);
        Assert.Equal("no-cache", context.Response.Headers.Pragma);
        Assert.DoesNotContain("credential", ok.Value.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EnabledProjectionRequiresHttpsCredentialAndCertificateConfiguration()
    {
        string variable = "CONTROL_SERVER_TEST_MISSING_" + Guid.NewGuid().ToString("N");
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Health:url"] = "http://127.0.0.1:58007" }).Build();
        OnboardSafetyProjectionOptions options = ProjectionOptions(variable);
        options.RequireHttps = false;

        ValidateOptionsResult result = new OnboardSafetyProjectionOptionsValidator(configuration)
            .Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, failure => failure.Contains("RequireHttps", StringComparison.Ordinal));
        Assert.Contains(result.Failures, failure => failure.Contains("Health:url", StringComparison.Ordinal));
        Assert.Contains(result.Failures, failure => failure.Contains("credential", StringComparison.Ordinal));
        Assert.Contains(result.Failures, failure => failure.Contains("serverCertificatePath", StringComparison.Ordinal));
    }

    private static JourneyRuntimeOptions JourneyOptions() => new()
    {
        VehicleKey = "VEHICLE-KEY-01"
    };

    private static OnboardSafetyProjectionOptions ProjectionOptions(string credentialVariable) => new()
    {
        Enabled = true,
        RequireHttps = true,
        CredentialEnvironmentVariable = credentialVariable
    };

    private sealed class RecordingSafetyFacts : IRiotVehicleSafetyFacts
    {
        public RiotVehicleSafetyObservation Observation { get; set; } = new(
            "VEHICLE-KEY-01",
            RiotVehicleMotionState.Unknown,
            DateTimeOffset.UnixEpoch,
            "TEST",
            ["TEST_UNKNOWN"]);
        public int ReadCount { get; private set; }
        public string? LastVehicleKey { get; private set; }

        public Task<RiotVehicleSafetyObservation> ReadVehicleSafetyAsync(
            string vehicleKey,
            CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            ReadCount++;
            LastVehicleKey = vehicleKey;
            return Task.FromResult(Observation);
        }
    }

    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly string name;
        private readonly string? original;

        public EnvironmentVariableScope(string name, string value)
        {
            this.name = name;
            original = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose() => Environment.SetEnvironmentVariable(name, original);
    }
}
