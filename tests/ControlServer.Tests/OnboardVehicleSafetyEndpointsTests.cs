using ControlServer.Application;
using ControlServer.Host.Runtime;
using ControlServer.Host.Transport;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace ControlServer.Tests;

public sealed class OnboardVehicleSafetyEndpointsTests
{
    [Fact]
    public async Task MissingExternalCredentialKeepsTheProjectionUnavailable()
    {
        string variable = "CONTROL_SERVER_TEST_MISSING_" + Guid.NewGuid().ToString("N");
        DefaultHttpContext context = new();
        context.Request.Scheme = "http";
        context.Request.Headers.Authorization = "Bearer any-credential";
        RecordingSafetyFacts facts = new();

        var result = await OnboardVehicleSafetyEndpoints.HandleAsync(
            context,
            facts,
            new ThrowingLedger(),
            Options.Create(JourneyOptions()),
            Options.Create(ProjectionOptions(variable)),
            TestContext.Current.CancellationToken);

        ProblemHttpResult problem = Assert.IsType<ProblemHttpResult>(result.Result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, problem.StatusCode);
        Assert.Equal(0, facts.ReadCount);
    }

    [Fact]
    public async Task InvalidExternalBearerCredentialIsRejectedWithoutReadingRiotFacts()
    {
        string variable = "CONTROL_SERVER_TEST_" + Guid.NewGuid().ToString("N");
        using EnvironmentVariableScope credential = new(variable, "expected-credential");
        DefaultHttpContext context = new();
        context.Request.Scheme = "http";
        context.Request.Headers.Authorization = "Bearer wrong-credential";
        RecordingSafetyFacts facts = new();

        var result = await OnboardVehicleSafetyEndpoints.HandleAsync(
            context,
            facts,
            new ThrowingLedger(),
            Options.Create(JourneyOptions()),
            Options.Create(ProjectionOptions(variable)),
            TestContext.Current.CancellationToken);

        Assert.IsType<UnauthorizedHttpResult>(result.Result);
        Assert.Equal("Bearer", context.Response.Headers.WWWAuthenticate);
        Assert.Equal(0, facts.ReadCount);
    }

    [Fact]
    public async Task AuthenticatedPlainHttpRequestReturnsTheFailClosedRiotProjection()
    {
        string variable = "CONTROL_SERVER_TEST_" + Guid.NewGuid().ToString("N");
        using EnvironmentVariableScope credential = new(variable, "onboard-only-credential");
        DefaultHttpContext context = new();
        context.Request.Scheme = "http";
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
            new ThrowingLedger(),
            Options.Create(JourneyOptions()),
            Options.Create(ProjectionOptions(variable)),
            TestContext.Current.CancellationToken);

        Ok<OnboardVehicleSafetyResponse> ok = Assert.IsType<Ok<OnboardVehicleSafetyResponse>>(result.Result);
        Assert.NotNull(ok.Value);
        Assert.Equal("UNKNOWN", ok.Value.MotionState);
        Assert.Equal(["RIOT_MOVEMENT_NOT_FINISHED"], ok.Value.ReasonCodes);
        Assert.Equal(1, facts.ReadCount);
        Assert.Equal("VEHICLE-KEY-01", facts.LastVehicleKey);
        Assert.Equal(TimeSpan.FromMilliseconds(1_500), facts.LastCarryOver);
        Assert.IsType<ThrowingLedger>(facts.LastLedger);
        Assert.Equal("no-store", context.Response.Headers.CacheControl);
        Assert.Equal("no-cache", context.Response.Headers.Pragma);
        Assert.DoesNotContain("credential", ok.Value.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EnabledProjectionRequiresOnlyAPopulatedCredentialVariable()
    {
        string variable = "CONTROL_SERVER_TEST_MISSING_" + Guid.NewGuid().ToString("N");

        ValidateOptionsResult result = new OnboardSafetyProjectionOptionsValidator()
            .Validate(null, ProjectionOptions(variable));

        Assert.True(result.Failed);
        string failure = Assert.Single(result.Failures);
        Assert.Contains("credential", failure, StringComparison.Ordinal);
    }

    [Fact]
    public void EnabledProjectionNoLongerDependsOnCertificateOrHttpsConfiguration()
    {
        string variable = "CONTROL_SERVER_TEST_" + Guid.NewGuid().ToString("N");
        using EnvironmentVariableScope credential = new(variable, "onboard-only-credential");

        ValidateOptionsResult result = new OnboardSafetyProjectionOptionsValidator()
            .Validate(null, ProjectionOptions(variable));

        Assert.True(result.Succeeded);
    }

    /// <summary>
    /// control-server#573：沿用窗口 0～2000 毫秒——车载端合同 5000 毫秒，减它的时钟容差上限 1000、拉取间隔 1000、留给下一次请求的 1000
    /// （<c>VehicleSafetySettings</c>；车载端每次用证据时按当时的时刻判时效，所以沿用来的证据要撑到下一次拉取返回）。
    /// 越界就拒绝启动；投影关着也照查，免得打开那天才发现。
    /// </summary>
    [Theory]
    [InlineData(-1, true, false)]
    [InlineData(0, true, true)]
    [InlineData(1_500, true, true)]
    [InlineData(2_000, true, true)]
    [InlineData(2_001, true, false)]
    [InlineData(-1, false, false)]
    [InlineData(2_001, false, false)]
    public void TheOrderCoverageCarryOverIsBoundedByTheOnboardEvidenceAge(int carryOverMs, bool enabled, bool accepted)
    {
        string variable = "CONTROL_SERVER_TEST_" + Guid.NewGuid().ToString("N");
        using EnvironmentVariableScope credential = new(variable, "onboard-only-credential");
        OnboardSafetyProjectionOptions options = ProjectionOptions(variable);
        options.Enabled = enabled;
        options.OrderCoverageCarryOverMs = carryOverMs;

        ValidateOptionsResult result = new OnboardSafetyProjectionOptionsValidator().Validate(null, options);

        Assert.Equal(accepted, result.Succeeded);
        if (!accepted)
        {
            Assert.Contains("OrderCoverageCarryOverMs", Assert.Single(result.Failures!), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheOrderCoverageCarryOverDefaultsToOneAndAHalfSeconds() =>
        Assert.Equal(1_500, new OnboardSafetyProjectionOptions().OrderCoverageCarryOverMs);

    [Theory]
    [InlineData("OnboardTransport:serverCertificatePath", "C:\\certs\\server.pfx")]
    [InlineData("OnboardTransport:serverCertificatePasswordEnvironmentVariable", "CONTROL_SERVER_ONBOARD_CERTIFICATE_PASSWORD")]
    [InlineData("OnboardTransport:allowInsecureLoopback", "true")]
    [InlineData("OnboardSafetyProjection:requireHttps", "true")]
    public void RemovedCertificateKeysAreRejectedInsteadOfSilentlyIgnored(string key, string value)
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { [key] = value }).Build();

        ValidateOptionsResult result = new OnboardTransportOptionsValidator(configuration)
            .Validate(null, new OnboardTransportOptions());

        Assert.True(result.Failed);
        string failure = Assert.Single(result.Failures);
        Assert.Contains(key, failure, StringComparison.Ordinal);
        Assert.Contains("plaintext", failure, StringComparison.Ordinal);
    }

    [Fact]
    public void PlaintextConfigurationPassesTheRemovedKeyCheck()
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["OnboardTransport:listenAddress"] = "0.0.0.0",
                ["OnboardTransport:port"] = "58005",
                ["OnboardSafetyProjection:enabled"] = "true"
            }).Build();

        ValidateOptionsResult result = new OnboardTransportOptionsValidator(configuration)
            .Validate(null, new OnboardTransportOptions());

        Assert.True(result.Succeeded);
    }

    private static JourneyRuntimeOptions JourneyOptions() => new()
    {
        VehicleKey = "VEHICLE-KEY-01"
    };

    private static OnboardSafetyProjectionOptions ProjectionOptions(string credentialVariable) => new()
    {
        Enabled = true,
        CredentialEnvironmentVariable = credentialVariable
    };

    private sealed class RecordingSafetyFacts : IOnboardVehicleSafetyProjection
    {
        public RiotVehicleSafetyObservation Observation { get; set; } = new(
            "VEHICLE-KEY-01",
            RiotVehicleMotionState.Unknown,
            DateTimeOffset.UnixEpoch,
            "TEST",
            ["TEST_UNKNOWN"]);
        public int ReadCount { get; private set; }
        public string? LastVehicleKey { get; private set; }
        public TimeSpan? LastCarryOver { get; private set; }
        public IOwnOrderCreationLedger? LastLedger { get; private set; }

        public Task<RiotVehicleSafetyObservation> ReadForOnboardAsync(
            string vehicleKey,
            TimeSpan coverageCarryOver,
            IOwnOrderCreationLedger ownOrders,
            CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            ReadCount++;
            LastVehicleKey = vehicleKey;
            LastCarryOver = coverageCarryOver;
            LastLedger = ownOrders;
            return Task.FromResult(Observation);
        }
    }

    private sealed class ThrowingLedger : IOwnOrderCreationLedger
    {
        public Task<bool> MayHaveCreatedSinceAsync(string vehicleKey, DateTimeOffset since, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("not expected in these tests");
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
