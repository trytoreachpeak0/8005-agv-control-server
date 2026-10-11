using ControlServer.Application;
using ControlServer.Host.Runtime;
using ControlServer.Host.Transport;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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

        // Non-default values (control-server#573 review S2), so a value written into the endpoint cannot pass as the configured one.
        OnboardSafetyProjectionOptions options = ProjectionOptions(variable);
        options.NonFinalOrderReadRetries = 3;
        options.ReadBudgetMilliseconds = 1_500;

        var result = await OnboardVehicleSafetyEndpoints.HandleAsync(
            context,
            facts,
            Options.Create(JourneyOptions()),
            Options.Create(options),
            NullLoggerFactory.Instance,
            TestContext.Current.CancellationToken);

        Ok<OnboardVehicleSafetyResponse> ok = Assert.IsType<Ok<OnboardVehicleSafetyResponse>>(result.Result);
        Assert.NotNull(ok.Value);
        Assert.Equal("UNKNOWN", ok.Value.MotionState);
        Assert.Equal(["RIOT_MOVEMENT_NOT_FINISHED"], ok.Value.ReasonCodes);
        Assert.Equal(1, facts.ReadCount);
        Assert.Equal("VEHICLE-KEY-01", facts.LastVehicleKey);
        Assert.Equal(3, facts.LastRereads);
        Assert.Equal(TimeSpan.FromMilliseconds(1_500), facts.LastBudget);
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
        (LogLevel level, EventId eventId, string message) = Assert.Single(logs.Entries);
        Assert.Equal(LogLevel.Warning, level);
        Assert.Equal(5730, eventId.Id);
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

    /// <summary>
    /// control-server#600：两车实例里每台车用自己的凭据，各自拿到自己的投影。修前接口固定读 JourneyRuntime.VehicleKey，
    /// agv03 拿到的是 agv02 的投影（cs#566 现场续场二，16-agv03-deploy-onboard.txt）。
    /// </summary>
    [Fact]
    public async Task EachVehicleReadsItsOwnProjectionWithItsOwnCredential()
    {
        using TwoCarCredentials cars = new();

        foreach ((string credential, string vehicleKey) in new[]
                 {
                     (TwoCarCredentials.Agv02Credential, Agv02Key),
                     (TwoCarCredentials.Agv03Credential, Agv03Key)
                 })
        {
            DefaultHttpContext context = Request(credential);
            RecordingSafetyFacts facts = new() { EchoRequestedVehicle = true };

            var result = await OnboardVehicleSafetyEndpoints.HandleAsync(
                context,
                facts,
                Options.Create(TwoCarJourneyOptions()),
                Options.Create(cars.ProjectionOptions()),
                NullLoggerFactory.Instance,
                TestContext.Current.CancellationToken);

            Ok<OnboardVehicleSafetyResponse> ok = Assert.IsType<Ok<OnboardVehicleSafetyResponse>>(result.Result);
            Assert.Equal(vehicleKey, ok.Value!.VehicleKey);
            Assert.Equal(vehicleKey, facts.LastVehicleKey);
            Assert.Equal(1, facts.ReadCount);
        }
    }

    /// <summary>control-server#600：agv03 的凭据自称 agv02，被拒（403），不读 RIoT，也不回任何一台车的投影。</summary>
    [Fact]
    public async Task AVehicleNamingAnotherVehicleIsRefusedWithoutReadingRiot()
    {
        using TwoCarCredentials cars = new();
        DefaultHttpContext context = Request(TwoCarCredentials.Agv03Credential, "?vehicleKey=" + Agv02Key);
        RecordingSafetyFacts facts = new() { EchoRequestedVehicle = true };

        var result = await OnboardVehicleSafetyEndpoints.HandleAsync(
            context,
            facts,
            Options.Create(TwoCarJourneyOptions()),
            Options.Create(cars.ProjectionOptions()),
            NullLoggerFactory.Instance,
            TestContext.Current.CancellationToken);

        ProblemHttpResult problem = Assert.IsType<ProblemHttpResult>(result.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, problem.StatusCode);
        Assert.Equal(0, facts.ReadCount);
    }

    /// <summary>control-server#600：车队表外的车号被拒（403），单车与两车配置都一样，不读 RIoT。</summary>
    [Fact]
    public async Task AVehicleOutsideTheRosterIsRefusedWithoutReadingRiot()
    {
        using TwoCarCredentials cars = new();
        foreach ((string credential, JourneyRuntimeOptions journey, OnboardSafetyProjectionOptions projection) in new[]
                 {
                     (TwoCarCredentials.Agv02Credential, TwoCarJourneyOptions(), cars.ProjectionOptions()),
                     (TwoCarCredentials.SharedCredential, JourneyOptions(), ProjectionOptions(cars.SharedVariable))
                 })
        {
            DefaultHttpContext context = Request(credential, "?vehicleKey=BROKERX-NOT-IN-ROSTER");
            RecordingSafetyFacts facts = new() { EchoRequestedVehicle = true };

            var result = await OnboardVehicleSafetyEndpoints.HandleAsync(
                context,
                facts,
                Options.Create(journey),
                Options.Create(projection),
                NullLoggerFactory.Instance,
                TestContext.Current.CancellationToken);

            ProblemHttpResult problem = Assert.IsType<ProblemHttpResult>(result.Result);
            Assert.Equal(StatusCodes.Status403Forbidden, problem.StatusCode);
            Assert.Equal(0, facts.ReadCount);
        }
    }

    /// <summary>
    /// control-server#600：单车配置（没有车队表、没有按车凭据）照旧用共享凭据读 JourneyRuntime.VehicleKey；车载端新模板会在
    /// 地址里带上自己的车号，带对了照旧回投影。
    /// </summary>
    [Fact]
    public async Task ASingleVehicleDeploymentStillAnswersTheSharedCredentialWithOrWithoutItsVehicleKey()
    {
        using TwoCarCredentials cars = new();
        foreach (string? query in new[] { null, "?vehicleKey=VEHICLE-KEY-01" })
        {
            DefaultHttpContext context = Request(TwoCarCredentials.SharedCredential, query);
            RecordingSafetyFacts facts = new() { EchoRequestedVehicle = true };

            var result = await OnboardVehicleSafetyEndpoints.HandleAsync(
                context,
                facts,
                Options.Create(JourneyOptions()),
                Options.Create(ProjectionOptions(cars.SharedVariable)),
                NullLoggerFactory.Instance,
                TestContext.Current.CancellationToken);

            Ok<OnboardVehicleSafetyResponse> ok = Assert.IsType<Ok<OnboardVehicleSafetyResponse>>(result.Result);
            Assert.Equal("VEHICLE-KEY-01", ok.Value!.VehicleKey);
            Assert.Equal("VEHICLE-KEY-01", facts.LastVehicleKey);
        }
    }

    /// <summary>control-server#600：车号为空或出现两次都被拒（403），不读 RIoT。</summary>
    [Theory]
    [InlineData("?vehicleKey=")]
    [InlineData("?vehicleKey=%20")]
    [InlineData("?vehicleKey=BROKERX-AGV02&vehicleKey=BROKERX-AGV02")]
    public async Task AnEmptyOrRepeatedVehicleKeyIsRefused(string query)
    {
        using TwoCarCredentials cars = new();
        DefaultHttpContext context = Request(TwoCarCredentials.Agv02Credential, query);
        RecordingSafetyFacts facts = new() { EchoRequestedVehicle = true };

        var result = await OnboardVehicleSafetyEndpoints.HandleAsync(
            context,
            facts,
            Options.Create(TwoCarJourneyOptions()),
            Options.Create(cars.ProjectionOptions()),
            NullLoggerFactory.Instance,
            TestContext.Current.CancellationToken);

        ProblemHttpResult problem = Assert.IsType<ProblemHttpResult>(result.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, problem.StatusCode);
        Assert.Equal(0, facts.ReadCount);
    }

    /// <summary>
    /// control-server#600：配了按车凭据，共享凭据（每台车都有）就不再能读任何一台车的投影（401）；带上自己的车号也不行。
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("?vehicleKey=BROKERX-AGV02")]
    public async Task TheSharedCredentialReadsNoProjectionOncePerVehicleCredentialsAreSet(string? query)
    {
        using TwoCarCredentials cars = new();
        DefaultHttpContext context = Request(TwoCarCredentials.SharedCredential, query);
        RecordingSafetyFacts facts = new() { EchoRequestedVehicle = true };

        var result = await OnboardVehicleSafetyEndpoints.HandleAsync(
            context,
            facts,
            Options.Create(TwoCarJourneyOptions()),
            Options.Create(cars.ProjectionOptions()),
            NullLoggerFactory.Instance,
            TestContext.Current.CancellationToken);

        Assert.IsType<UnauthorizedHttpResult>(result.Result);
        Assert.Equal(0, facts.ReadCount);
    }

    /// <summary>
    /// control-server#600：多车却没配按车凭据（启动校验本该挡住）时，接口也不把主车的投影回给拿共享凭据的车：503，不读 RIoT。
    /// 这正是 cs#566 现场续场二 agv03 拿到 agv02 投影的那个配置。
    /// </summary>
    [Fact]
    public async Task ARosterOfTwoWithOnlyTheSharedCredentialAnswersNobody()
    {
        using TwoCarCredentials cars = new();
        DefaultHttpContext context = Request(TwoCarCredentials.SharedCredential);
        RecordingSafetyFacts facts = new() { EchoRequestedVehicle = true };

        var result = await OnboardVehicleSafetyEndpoints.HandleAsync(
            context,
            facts,
            Options.Create(TwoCarJourneyOptions()),
            Options.Create(ProjectionOptions(cars.SharedVariable)),
            NullLoggerFactory.Instance,
            TestContext.Current.CancellationToken);

        ProblemHttpResult problem = Assert.IsType<ProblemHttpResult>(result.Result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, problem.StatusCode);
        Assert.Equal(0, facts.ReadCount);
    }

    /// <summary>control-server#600：拒绝记一条警告（事件 5731），写原因与凭据的车，不写请求里的车号，更不写凭据。</summary>
    [Fact]
    public async Task ARefusalIsLoggedWithTheCredentialsVehicleAndNotTheClaim()
    {
        using TwoCarCredentials cars = new();
        DefaultHttpContext context = Request(TwoCarCredentials.Agv03Credential, "?vehicleKey=" + Agv02Key);
        RecordingLoggerFactory logs = new();

        await OnboardVehicleSafetyEndpoints.HandleAsync(
            context,
            new RecordingSafetyFacts(),
            Options.Create(TwoCarJourneyOptions()),
            Options.Create(cars.ProjectionOptions()),
            logs,
            TestContext.Current.CancellationToken);

        (LogLevel level, EventId eventId, string message) = Assert.Single(logs.Entries);
        Assert.Equal(LogLevel.Warning, level);
        Assert.Equal(5731, eventId.Id);
        Assert.Contains("VEHICLE_KEY_NOT_THE_CREDENTIALS", message, StringComparison.Ordinal);
        Assert.Contains(Agv03Key, message, StringComparison.Ordinal);
        Assert.DoesNotContain(Agv02Key, message, StringComparison.Ordinal);
        Assert.DoesNotContain(TwoCarCredentials.Agv03Credential, message, StringComparison.Ordinal);
        Assert.DoesNotContain(TwoCarCredentials.Agv02Credential, message, StringComparison.Ordinal);
    }

    /// <summary>
    /// control-server#600：车队表两台以上、投影开着、却没配按车凭据，启动就拒绝，报错写清缺什么、去哪配。
    /// 服务端此时分不出是哪台车在问；回 503 会让每台车悄悄变成未就绪，启动失败在部署时当场就能看到。
    /// </summary>
    [Fact]
    public void ARosterOfTwoWithoutPerVehicleCredentialsRefusesToStart()
    {
        using TwoCarCredentials cars = new();

        ValidateOptionsResult result = new OnboardSafetyProjectionOptionsValidator(TwoCarJourneyOptions(), cars.Transport())
            .Validate(null, ProjectionOptions(cars.SharedVariable));

        Assert.True(result.Failed);
        string failure = Assert.Single(result.Failures!);
        Assert.Contains("OnboardSafetyProjection:vehicleCredentials", failure, StringComparison.Ordinal);
        Assert.Contains(Agv02Key, failure, StringComparison.Ordinal);
        Assert.Contains(Agv03Key, failure, StringComparison.Ordinal);
    }

    /// <summary>
    /// control-server#600 审查 S1：校验器要经 Host 的注册拿到车队表与 TCP 凭据，否则按车检查全部跳过。两车车队表、投影开着、
    /// 只有共享凭据时，Host.StartAsync 必须抛 OptionsValidationException。
    /// </summary>
    [Fact]
    public async Task TheHostRefusesToStartARosterOfTwoWithOnlyTheSharedCredential()
    {
        using TwoCarCredentials cars = new();
        HostApplicationBuilder builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(
            new HostApplicationBuilderSettings
            {
                DisableDefaults = true,
                ApplicationName = typeof(OnboardVehicleSafetyEndpointsTests).Assembly.GetName().Name!
            });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"{OnboardSafetyProjectionOptions.SectionName}:Enabled"] = "true",
            [$"{OnboardSafetyProjectionOptions.SectionName}:CredentialEnvironmentVariable"] = cars.SharedVariable,
            [$"{OnboardTransportOptions.SectionName}:CredentialEnvironmentVariable"] = cars.SharedVariable,
            [$"{JourneyRuntimeOptions.SectionName}:AgvId"] = "AGV02",
            [$"{JourneyRuntimeOptions.SectionName}:VehicleKey"] = Agv02Key,
            [$"{JourneyRuntimeOptions.SectionName}:Fleet:0:AgvId"] = "AGV02",
            [$"{JourneyRuntimeOptions.SectionName}:Fleet:0:VehicleKey"] = Agv02Key,
            [$"{JourneyRuntimeOptions.SectionName}:Fleet:1:AgvId"] = "AGV03",
            [$"{JourneyRuntimeOptions.SectionName}:Fleet:1:VehicleKey"] = Agv03Key
        });
        builder.Services.AddJourneyRuntimeOptions(builder.Configuration);
        builder.Services.AddOptions<OnboardTransportOptions>()
            .Bind(builder.Configuration.GetSection(OnboardTransportOptions.SectionName));
        builder.Services.AddOnboardSafetyProjectionOptions(builder.Configuration);
        using IHost host = builder.Build();

        OptionsValidationException error = await Assert.ThrowsAsync<OptionsValidationException>(() =>
            host.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains(error.Failures, failure =>
            failure.Contains("OnboardSafetyProjection:vehicleCredentials", StringComparison.Ordinal) &&
            failure.Contains(Agv03Key, StringComparison.Ordinal));
    }

    /// <summary>Program.cs 注册投影选项只走这个扩展方法（审查 S1）：直接注册无参校验器会跳过按车检查。</summary>
    [Fact]
    public void TheHostRegistersTheProjectionOptionsThroughTheExtension()
    {
        string program = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "ControlServer.Host", "Program.cs"));

        Assert.Contains("builder.Services.AddOnboardSafetyProjectionOptions(builder.Configuration);", program, StringComparison.Ordinal);
        Assert.DoesNotContain("OnboardSafetyProjectionOptionsValidator", program, StringComparison.Ordinal);
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ControlServer.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("ControlServer.sln not found above the test output.");
    }

    /// <summary>投影关着时多车不必配按车凭据：没有人会读到这个接口。</summary>
    [Fact]
    public void ARosterOfTwoNeedsNoPerVehicleCredentialsWhileTheProjectionIsOff()
    {
        using TwoCarCredentials cars = new();
        OnboardSafetyProjectionOptions options = ProjectionOptions(cars.SharedVariable);
        options.Enabled = false;

        Assert.True(new OnboardSafetyProjectionOptionsValidator(TwoCarJourneyOptions(), cars.Transport())
            .Validate(null, options).Succeeded);
    }

    [Fact]
    public void TwoVehiclesWithTheirOwnCredentialsStart()
    {
        using TwoCarCredentials cars = new();

        ValidateOptionsResult result = new OnboardSafetyProjectionOptionsValidator(TwoCarJourneyOptions(), cars.Transport())
            .Validate(null, cars.ProjectionOptions());

        Assert.True(result.Succeeded, string.Join(" | ", result.Failures ?? []));
    }

    /// <summary>
    /// control-server#600 启动校验：按车凭据表里的车必须都在车队表里、不重复、车队表每台都有；每个变量必须有值、值两两不同、
    /// 且不能等于共享凭据（TCP 用的那个）。报错只写变量名，不写值。
    /// </summary>
    [Theory]
    [InlineData("outside-roster", "is not in the roster")]
    [InlineData("duplicate-vehicle", "more than once")]
    [InlineData("roster-vehicle-missing", "has no projection credential")]
    [InlineData("empty-variable-name", "must name an environment variable")]
    [InlineData("unpopulated-variable", "is not set")]
    [InlineData("same-value-twice", "share one value")]
    [InlineData("equals-projection-shared", "equals the shared onboard credential")]
    [InlineData("equals-transport-shared", "equals the shared onboard credential")]
    public void PerVehicleCredentialsAreCheckedAtStartup(string defect, string expected)
    {
        using TwoCarCredentials cars = new();
        OnboardSafetyProjectionOptions options = cars.ProjectionOptions();
        OnboardTransportOptions transport = cars.Transport();
        string transportVariable = "CONTROL_SERVER_TEST_TCP_" + Guid.NewGuid().ToString("N");
        using EnvironmentVariableScope tcp = new(transportVariable, "tcp-only-credential");
        using EnvironmentVariableScope blank = new(cars.Agv03Variable + "_BLANK", "");
        switch (defect)
        {
            case "outside-roster":
                options.VehicleCredentials[1].VehicleKey = "BROKERX-NOT-IN-ROSTER";
                break;
            case "duplicate-vehicle":
                options.VehicleCredentials[1].VehicleKey = Agv02Key;
                break;
            case "roster-vehicle-missing":
                options.VehicleCredentials = [options.VehicleCredentials[0]];
                break;
            case "empty-variable-name":
                options.VehicleCredentials[1].CredentialEnvironmentVariable = " ";
                break;
            case "unpopulated-variable":
                options.VehicleCredentials[1].CredentialEnvironmentVariable = cars.Agv03Variable + "_BLANK";
                break;
            case "same-value-twice":
                options.VehicleCredentials[1].CredentialEnvironmentVariable = cars.Agv02Variable;
                break;
            case "equals-projection-shared":
                options.VehicleCredentials[1].CredentialEnvironmentVariable = cars.SharedVariable;
                break;
            case "equals-transport-shared":
                transport.CredentialEnvironmentVariable = transportVariable;
                Environment.SetEnvironmentVariable(cars.Agv03Variable, "tcp-only-credential");
                break;
        }

        ValidateOptionsResult result = new OnboardSafetyProjectionOptionsValidator(TwoCarJourneyOptions(), transport)
            .Validate(null, options);

        Assert.True(result.Failed, defect);
        Assert.Contains(result.Failures!, failure => failure.Contains(expected, StringComparison.Ordinal));
        foreach (string failure in result.Failures!)
        {
            foreach (string value in new[]
                     {
                         TwoCarCredentials.SharedCredential, TwoCarCredentials.Agv02Credential,
                         TwoCarCredentials.Agv03Credential, "tcp-only-credential"
                     })
            {
                Assert.DoesNotContain(value, failure, StringComparison.Ordinal);
            }
        }
    }

    /// <summary>单车配置的启动校验不变：只有一台车时不要求按车凭据。</summary>
    [Fact]
    public void ASingleVehicleNeedsNoPerVehicleCredentials()
    {
        using TwoCarCredentials cars = new();

        Assert.True(new OnboardSafetyProjectionOptionsValidator(JourneyOptions(), cars.Transport())
            .Validate(null, ProjectionOptions(cars.SharedVariable)).Succeeded);
    }

    private const string Agv02Key = "BROKERX-AGV02";
    private const string Agv03Key = "BROKERX-AGV03";

    private static DefaultHttpContext Request(string credential, string? query = null)
    {
        DefaultHttpContext context = new();
        context.Request.Scheme = "http";
        context.Request.Headers.Authorization = "Bearer " + credential;
        if (query is not null) context.Request.QueryString = new QueryString(query);
        return context;
    }

    /// <summary>The two-car roster of instance-factory01-v2.two-car-example.json, agv02 primary.</summary>
    private static JourneyRuntimeOptions TwoCarJourneyOptions() => new()
    {
        AgvId = "AGV02",
        VehicleKey = Agv02Key,
        Fleet =
        [
            new FleetVehicleOptions { AgvId = "AGV02", VehicleKey = Agv02Key, AgvLifecycleGeneration = 1 },
            new FleetVehicleOptions { AgvId = "AGV03", VehicleKey = Agv03Key, AgvLifecycleGeneration = 1 }
        ]
    };

    /// <summary>
    /// Environment variables for the shared TCP credential and one projection credential per car, each with its own value.
    /// </summary>
    private sealed class TwoCarCredentials : IDisposable
    {
        public const string SharedCredential = "shared-tcp-credential";
        public const string Agv02Credential = "agv02-safety-credential";
        public const string Agv03Credential = "agv03-safety-credential";

        private readonly string suffix = Guid.NewGuid().ToString("N");
        private readonly List<EnvironmentVariableScope> scopes = [];

        public TwoCarCredentials()
        {
            scopes.Add(new EnvironmentVariableScope(SharedVariable, SharedCredential));
            scopes.Add(new EnvironmentVariableScope(Agv02Variable, Agv02Credential));
            scopes.Add(new EnvironmentVariableScope(Agv03Variable, Agv03Credential));
        }

        public string SharedVariable => "CONTROL_SERVER_TEST_SHARED_" + suffix;
        public string Agv02Variable => "CONTROL_SERVER_TEST_AGV02_" + suffix;
        public string Agv03Variable => "CONTROL_SERVER_TEST_AGV03_" + suffix;

        public OnboardSafetyProjectionOptions ProjectionOptions() => new()
        {
            Enabled = true,
            CredentialEnvironmentVariable = SharedVariable,
            VehicleCredentials =
            [
                new OnboardSafetyVehicleCredentialOptions { VehicleKey = Agv02Key, CredentialEnvironmentVariable = Agv02Variable },
                new OnboardSafetyVehicleCredentialOptions { VehicleKey = Agv03Key, CredentialEnvironmentVariable = Agv03Variable }
            ]
        };

        /// <summary>The TCP transport reading the same shared credential, as appsettings.json ships it.</summary>
        public OnboardTransportOptions Transport() => new() { CredentialEnvironmentVariable = SharedVariable };

        public void Dispose()
        {
            foreach (EnvironmentVariableScope scope in scopes) scope.Dispose();
        }
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

        /// <summary>Answer with the vehicle that was asked for, as the real read does.</summary>
        public bool EchoRequestedVehicle { get; init; }

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
            return Task.FromResult(EchoRequestedVehicle ? Observation with { VehicleKey = vehicleKey } : Observation);
        }
    }

    private sealed class RecordingLoggerFactory : ILoggerFactory, ILogger
    {
        public List<(LogLevel Level, EventId EventId, string Message)> Entries { get; } = [];

        public ILogger CreateLogger(string categoryName) => this;
        public void AddProvider(ILoggerProvider provider) => _ = provider;
        public void Dispose() { }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, eventId, formatter(state, exception)));
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
