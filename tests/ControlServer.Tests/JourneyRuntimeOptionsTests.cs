using ControlServer.Host.Runtime;
using Microsoft.Extensions.Configuration;

namespace ControlServer.Tests;

public sealed class JourneyRuntimeOptionsTests
{
    [Fact]
    public void ConfirmedBatteryThresholdDefaultsToThirtyPercent()
    {
        Assert.Equal(30, new JourneyRuntimeOptions().MinimumBatteryPercent);
    }

    [Fact]
    public void DisabledRuntimeIsTheSafeDefaultWithoutDeploymentIdentity()
    {
        IConfiguration configuration = new ConfigurationBuilder().Build();
        JourneyRuntimeOptionsValidator validator = new(configuration);

        Microsoft.Extensions.Options.ValidateOptionsResult result = validator.Validate(
            null, new JourneyRuntimeOptions());

        Assert.True(result.Succeeded);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-01")]
    public void EnabledRuntimeRejectsMissingRemoteExternalSecretReferencesButAllowsLoopbackMesIngest()
    {
        string suffix = Guid.NewGuid().ToString("N");
        Dictionary<string, string?> values = new()
        {
            ["MesIngest:baseUrl"] = "http://127.0.0.1:5088",
            ["MesIngest:sharedSecretEnvironmentVariable"] = $"MISSING_MES_{suffix}",
            ["RIoT:callApiKeyEnvironmentVariable"] = $"MISSING_RIOT_{suffix}",
            ["OnboardTransport:credentialEnvironmentVariable"] = $"MISSING_ONBOARD_{suffix}"
        };
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        JourneyRuntimeOptionsValidator validator = new(configuration);

        Microsoft.Extensions.Options.ValidateOptionsResult result = validator.Validate(null, ValidEnabledOptions());

        Assert.True(result.Failed);
        Assert.Equal(2, result.Failures.Count());
        Assert.DoesNotContain(result.Failures, failure => failure.StartsWith("MesIngest:", StringComparison.Ordinal));
        Assert.Contains(result.Failures, failure => failure.StartsWith("RIoT:", StringComparison.Ordinal));
        Assert.Contains(result.Failures, failure => failure.StartsWith("OnboardTransport:", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    public void EnabledRuntimeRejectsCrossOriginSublotQueryPath()
    {
        IConfiguration configuration = new ConfigurationBuilder().Build();
        JourneyRuntimeOptionsValidator validator = new(configuration);
        JourneyRuntimeOptions options = ValidEnabledOptions();
        options.SublotBoxCountPath = "https://untrusted.example/sublot-box-count";

        Microsoft.Extensions.Options.ValidateOptionsResult result = validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(
            "SublotBoxCountPath must be a same-origin absolute path.",
            result.Failures,
            StringComparer.Ordinal);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    public void StationDepartureWaitDefaultsToFiveMinutesAndIsEitherOffOrAtLeastFiveSeconds()
    {
        const string failure = "StationDepartureWaitTimeout must be zero (off) or at least 5 s.";
        Assert.Equal(TimeSpan.FromMinutes(5), new JourneyRuntimeOptions().StationDepartureWaitTimeout);
        JourneyRuntimeOptionsValidator validator = new(new ConfigurationBuilder().Build());
        JourneyRuntimeOptions options = ValidEnabledOptions();

        foreach (TimeSpan accepted in new[] { TimeSpan.Zero, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(5) })
        {
            options.StationDepartureWaitTimeout = accepted;
            Microsoft.Extensions.Options.ValidateOptionsResult result = validator.Validate(null, options);
            Assert.DoesNotContain(failure, result.Failures ?? [], StringComparer.Ordinal);
        }
        foreach (TimeSpan refused in new[] { TimeSpan.FromSeconds(-1), TimeSpan.FromSeconds(4) })
        {
            options.StationDepartureWaitTimeout = refused;
            Microsoft.Extensions.Options.ValidateOptionsResult result = validator.Validate(null, options);
            Assert.Contains(failure, result.Failures ?? [], StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// 终点改由绑定给出（control-server#160）：关卡两个标量从选项与出厂配置里删掉，「必须含 WIRE_TO_GATE」那条校验也删掉——
    /// 部署只放行别的任务类型不再被拒；#159 为防两份真相分叉加的过渡校验随标量一起删。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-10")]
    public void TheGateScalarsAreGoneAndAnAllowListWithoutWireToGateIsNoLongerRefused()
    {
        Assert.Null(typeof(JourneyRuntimeOptions).GetProperty("GateStationId"));
        Assert.Null(typeof(JourneyRuntimeOptions).GetProperty("GateStationRiotId"));
        JourneyRuntimeOptions options = ValidEnabledOptions();
        options.AllowedWorkTypes = ["STAGING_TO_WIRE"];

        Microsoft.Extensions.Options.ValidateOptionsResult result =
            new JourneyRuntimeOptionsValidator(new ConfigurationBuilder().Build()).Validate(null, options);

        Assert.DoesNotContain(result.Failures ?? [], failure => failure.Contains("WIRE_TO_GATE", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Failures ?? [], failure => failure.Contains("GateStation", StringComparison.Ordinal));

        string settings = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "ControlServer.Host", "appsettings.json"));
        Assert.DoesNotContain("gateStationId", settings, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("gateStationRiotId", settings, StringComparison.OrdinalIgnoreCase);

        Assert.Null(typeof(ControlServer.Application.TaskTypeStationConfigurationValidator).Assembly
            .GetType("ControlServer.Application.TransitionalGateStation"));
        Assert.Null(typeof(ControlServer.Application.TaskTypeStationReasonCodes).GetField("BindingGateScalarMismatch"));
        Assert.Equal(
            2,
            typeof(ControlServer.Application.TaskTypeStationConfigurationValidator)
                .GetMethod("ValidateStatic")!.GetParameters().Length);
    }

    /// <summary>
    /// 出厂配置的 <c>allowedWorkTypes</c> 列全六类：放不放行交给规则表与本图绑定（control-server#160）。只列
    /// <c>WIRE_TO_GATE</c> 的话，一条未绑定的 <c>STAGING_TO_WIRE</c> 需求会被笼统挡成范围外，而不是报缺绑定。
    /// 部署仍可以收窄它，收窄掉的任务类型报 <c>OUT_OF_SCOPE_WORK_TYPE</c>。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-10")]
    public void TheShippedAllowListNamesAllSixTaskTypesSoTheBindingsDecide()
    {
        using System.Text.Json.JsonDocument settings = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "ControlServer.Host", "appsettings.json")));

        string[] allowed =
        [
            .. settings.RootElement.GetProperty("JourneyRuntime").GetProperty("allowedWorkTypes").EnumerateArray()
                .Select(item => item.GetString()!)
        ];

        Assert.Equal(
            ControlServer.Application.TransportTaskTypes.All.Order(StringComparer.Ordinal),
            allowed.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// 持货超时（ADR-cross-0057，批次 7 建表票 control-server#206）：默认 30 分钟、必须为正，出厂配置写明这一项。
    /// 本票没有读者，读它的是批次7-07（control-server#212）。
    /// </summary>
    [Fact]
    public void CargoHoldingTimeoutDefaultsToThirtyMinutesMustBePositiveAndIsShippedInAppSettings()
    {
        const string failure = "CargoHoldingTimeout must be positive.";
        Assert.Equal(TimeSpan.FromMinutes(30), new JourneyRuntimeOptions().CargoHoldingTimeout);
        JourneyRuntimeOptionsValidator validator = new(new ConfigurationBuilder().Build());
        JourneyRuntimeOptions options = ValidEnabledOptions();

        foreach (TimeSpan accepted in new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(40), TimeSpan.FromMinutes(30) })
        {
            options.CargoHoldingTimeout = accepted;
            Assert.DoesNotContain(failure, validator.Validate(null, options).Failures ?? [], StringComparer.Ordinal);
        }
        foreach (TimeSpan refused in new[] { TimeSpan.Zero, TimeSpan.FromSeconds(-1) })
        {
            options.CargoHoldingTimeout = refused;
            Assert.Contains(failure, validator.Validate(null, options).Failures ?? [], StringComparer.Ordinal);
        }

        IConfiguration shipped = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(RepositoryRoot(), "src", "ControlServer.Host", "appsettings.json"), optional: false)
            .Build();
        Assert.Equal("00:30:00", shipped["JourneyRuntime:cargoHoldingTimeout"]);
        Assert.Equal(TimeSpan.FromMinutes(30), shipped.GetSection("JourneyRuntime").Get<JourneyRuntimeOptions>()!.CargoHoldingTimeout);
    }

    [Fact]
    public void CargoHoldingTimeoutBindsFromTheEnvironmentVariableTheL2OrchestratorSets()
    {
        // scripts/l2/Invoke-L2Scenario.ps1 maps the CargoHoldingTimeout setup key to JourneyRuntime__cargoHoldingTimeout,
        // the way it maps StationDepartureWaitTimeout. A prefix of this test's own keeps the variable out of every other test.
        const string prefix = "CONTROL_SERVER_TEST_B7_CARGO_";
        Environment.SetEnvironmentVariable(prefix + "JourneyRuntime__cargoHoldingTimeout", "00:00:40");
        try
        {
            IConfiguration configuration = new ConfigurationBuilder().AddEnvironmentVariables(prefix).Build();

            Assert.Equal(
                TimeSpan.FromSeconds(40),
                configuration.GetSection("JourneyRuntime").Get<JourneyRuntimeOptions>()!.CargoHoldingTimeout);
        }
        finally
        {
            Environment.SetEnvironmentVariable(prefix + "JourneyRuntime__cargoHoldingTimeout", null);
        }
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "global.json")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new InvalidOperationException("No repository root above the test output.");
    }

    private static JourneyRuntimeOptions ValidEnabledOptions() => new()
    {
        Enabled = true,
        PollInterval = TimeSpan.FromSeconds(1),
        MaximumEvidenceAge = TimeSpan.FromSeconds(30),
        AgvId = "老厂前线新多仓位1",
        VehicleKey = "BROKERX-0c20ff0600d644869a6a80c186065d85",
        AgvLifecycleGeneration = 1,
        MapId = 25,
        MapIdentity = "MAP-25",
        DispatchZone = "MAP-25-WIRE_TO_GATE",
        DispatchGeneration = 1,
        MinimumBatteryPercent = 40,
        SublotBoxCountPath = "/api/v2/sublot-box-count",
        AllowedWorkTypes = ["WIRE_TO_GATE"],
        AllowedDispatchZones = ["MAP-25-WIRE_TO_GATE"],
        AdmissionPolicyVersion = 1,
        AdmissionPolicyDeploymentId = "TEST-DEPLOYMENT-1"
    };
}
