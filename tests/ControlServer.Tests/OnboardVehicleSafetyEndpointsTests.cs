using ControlServer.Application;
using ControlServer.Host.Runtime;
using ControlServer.Host.Transport;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
            Options.Create(JourneyOptions()),
            Options.Create(ProjectionOptions(variable)),
            NullLoggerFactory.Instance,
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
            Options.Create(JourneyOptions()),
            Options.Create(ProjectionOptions(variable)),
            NullLoggerFactory.Instance,
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
            Options.Create(JourneyOptions()),
            Options.Create(ProjectionOptions(variable)),
            NullLoggerFactory.Instance,
            TestContext.Current.CancellationToken);

        Ok<OnboardVehicleSafetyResponse> ok = Assert.IsType<Ok<OnboardVehicleSafetyResponse>>(result.Result);
        Assert.NotNull(ok.Value);
        Assert.Equal("UNKNOWN", ok.Value.MotionState);
        Assert.Equal(["RIOT_MOVEMENT_NOT_FINISHED"], ok.Value.ReasonCodes);
        Assert.Equal(1, facts.ReadCount);
        Assert.Equal("VEHICLE-KEY-01", facts.LastVehicleKey);
        Assert.Equal(2, facts.LastRereads);
        Assert.Equal(TimeSpan.FromSeconds(2), facts.LastBudget);
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
    /// control-server#573：一次投影请求里清单最多再读几次，0～4，默认 2。RIoT 与 MVP 共用，一次拉取最坏是 1 + 3 页 ×（1 + 重读次数）个请求，
    /// 4 次就是 16 个。越界就拒绝启动；投影关着也照查，免得打开那天才发现。
    /// </summary>
    [Theory]
    [InlineData(-1, true, false)]
    [InlineData(0, true, true)]
    [InlineData(2, true, true)]
    [InlineData(4, true, true)]
    [InlineData(5, true, false)]
    [InlineData(-1, false, false)]
    [InlineData(5, false, false)]
    public void TheListingRereadsAreBounded(int rereads, bool enabled, bool accepted)
    {
        string variable = "CONTROL_SERVER_TEST_" + Guid.NewGuid().ToString("N");
        using EnvironmentVariableScope credential = new(variable, "onboard-only-credential");
        OnboardSafetyProjectionOptions options = ProjectionOptions(variable);
        options.Enabled = enabled;
        options.NonFinalOrderReadRetries = rereads;

        ValidateOptionsResult result = new OnboardSafetyProjectionOptionsValidator().Validate(null, options);

        Assert.Equal(accepted, result.Succeeded);
        if (!accepted)
        {
            Assert.Contains("NonFinalOrderReadRetries", Assert.Single(result.Failures!), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheListingIsRereadTwiceByDefault() =>
        Assert.Equal(2, new OnboardSafetyProjectionOptions().NonFinalOrderReadRetries);

    /// <summary>
    /// control-server#573（真装置 run 38082575561 之后）：整次读取的预算默认 2 秒，必须小于车载端自己的请求超时（默认 3 秒），
    /// 否则答案到的时候车载端已经放弃了。越界就拒绝启动，投影关着也照查。
    /// </summary>
    [Theory]
    [InlineData(2_000, 3_000, true)]
    [InlineData(2_999, 3_000, true)]
    [InlineData(3_000, 3_000, false)]
    [InlineData(4_000, 3_000, false)]
    [InlineData(0, 3_000, false)]
    [InlineData(-1, 3_000, false)]
    [InlineData(2_000, 0, false)]
    public void TheReadBudgetMustEndBeforeTheOnboardGivesUp(int budget, int onboardTimeout, bool accepted)
    {
        foreach (bool enabled in new[] { true, false })
        {
            string variable = "CONTROL_SERVER_TEST_" + Guid.NewGuid().ToString("N");
            using EnvironmentVariableScope credential = new(variable, "onboard-only-credential");
            OnboardSafetyProjectionOptions options = ProjectionOptions(variable);
            options.Enabled = enabled;
            options.ReadBudgetMilliseconds = budget;
            options.OnboardRequestTimeoutMilliseconds = onboardTimeout;

            ValidateOptionsResult result = new OnboardSafetyProjectionOptionsValidator().Validate(null, options);

            Assert.Equal(accepted, result.Succeeded);
            if (!accepted)
            {
                Assert.Contains("OnboardRequestTimeoutMilliseconds", Assert.Single(result.Failures!), StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void TheReadBudgetIsTwoSecondsBelowAThreeSecondOnboardTimeoutByDefault()
    {
        OnboardSafetyProjectionOptions options = new();
        Assert.Equal(2_000, options.ReadBudgetMilliseconds);
        Assert.Equal(3_000, options.OnboardRequestTimeoutMilliseconds);
    }

    /// <summary>
    /// 车载端放弃了这次请求（它的 3 秒超时，或者断开）：记成取消（499）并单独记一条警告，不是 500、也不吞掉。
    /// 真装置 run 38082575561 与现场 10-10 都把这种情况记成了 500。
    /// </summary>
    [Fact]
    public async Task TheOnboardGivingUpIsLoggedAsACancellationNotAServerError()
    {
        string variable = "CONTROL_SERVER_TEST_" + Guid.NewGuid().ToString("N");
        using EnvironmentVariableScope credential = new(variable, "onboard-only-credential");
        DefaultHttpContext context = new();
        context.Request.Scheme = "http";
        context.Request.Headers.Authorization = "Bearer onboard-only-credential";
        using CancellationTokenSource onboard = new();
        RecordingSafetyFacts facts = new() { CancelDuringRead = onboard };
        RecordingLoggerFactory logs = new();

        var result = await OnboardVehicleSafetyEndpoints.HandleAsync(
            context,
            facts,
            Options.Create(JourneyOptions()),
            Options.Create(ProjectionOptions(variable)),
            logs,
            onboard.Token);

        StatusCodeHttpResult status = Assert.IsType<StatusCodeHttpResult>(result.Result);
        Assert.Equal(StatusCodes.Status499ClientClosedRequest, status.StatusCode);
        (LogLevel level, string message) = Assert.Single(logs.Entries);
        Assert.Equal(LogLevel.Warning, level);
        Assert.Contains("cancelled by the onboard", message, StringComparison.Ordinal);
        Assert.Contains("VEHICLE-KEY-01", message, StringComparison.Ordinal);
    }

    /// <summary>取消不是车载端发起的（请求令牌没取消），就不归这里管：照旧往外抛，不被改写成 499。</summary>
    [Fact]
    public async Task ACancellationTheOnboardDidNotAskForIsNotRelabelled()
    {
        string variable = "CONTROL_SERVER_TEST_" + Guid.NewGuid().ToString("N");
        using EnvironmentVariableScope credential = new(variable, "onboard-only-credential");
        DefaultHttpContext context = new();
        context.Request.Scheme = "http";
        context.Request.Headers.Authorization = "Bearer onboard-only-credential";
        RecordingSafetyFacts facts = new() { ThrowUnaskedCancellation = true };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => OnboardVehicleSafetyEndpoints.HandleAsync(
            context,
            facts,
            Options.Create(JourneyOptions()),
            Options.Create(ProjectionOptions(variable)),
            NullLoggerFactory.Instance,
            TestContext.Current.CancellationToken));
    }

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
        public int? LastRereads { get; private set; }
        public TimeSpan? LastBudget { get; private set; }
        public CancellationTokenSource? CancelDuringRead { get; init; }
        public bool ThrowUnaskedCancellation { get; init; }

        public Task<RiotVehicleSafetyObservation> ReadForOnboardAsync(
            string vehicleKey,
            int listingRereads,
            TimeSpan readBudget,
            CancellationToken cancellationToken)
        {
            ReadCount++;
            LastVehicleKey = vehicleKey;
            LastRereads = listingRereads;
            LastBudget = readBudget;
            if (ThrowUnaskedCancellation)
            {
                throw new OperationCanceledException("Cancelled by something other than the onboard.");
            }
            if (CancelDuringRead is not null)
            {
                CancelDuringRead.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }
            return Task.FromResult(Observation);
        }
    }

    private sealed class RecordingLoggerFactory : ILoggerFactory, ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public ILogger CreateLogger(string categoryName) => this;
        public void AddProvider(ILoggerProvider provider) => _ = provider;
        public void Dispose() { }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            _ = eventId;
            Entries.Add((logLevel, formatter(state, exception)));
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
