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
    /// control-server#198：反向旅程在机台等准入恢复的门槛默认 10 分钟，必须为正——没有「一直等」这个选项。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-11")]
    public void TheAreaEndAdmissionRevokedTimeoutDefaultsToTenMinutesAndMustBePositive()
    {
        const string failure = "AreaEndAdmissionRevokedTimeout must be positive.";
        Assert.Equal(TimeSpan.FromMinutes(10), new JourneyRuntimeOptions().AreaEndAdmissionRevokedTimeout);
        JourneyRuntimeOptionsValidator validator = new(new ConfigurationBuilder().Build());
        JourneyRuntimeOptions options = ValidEnabledOptions();

        foreach (TimeSpan accepted in new[] { TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(10) })
        {
            options.AreaEndAdmissionRevokedTimeout = accepted;
            Assert.DoesNotContain(failure, validator.Validate(null, options).Failures ?? [], StringComparer.Ordinal);
        }
        foreach (TimeSpan refused in new[] { TimeSpan.Zero, TimeSpan.FromSeconds(-1) })
        {
            options.AreaEndAdmissionRevokedTimeout = refused;
            Assert.Contains(failure, validator.Validate(null, options).Failures ?? [], StringComparer.Ordinal);
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
