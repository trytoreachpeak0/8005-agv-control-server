using ControlServer.Host.Runtime;
using Microsoft.Extensions.Configuration;

namespace ControlServer.Tests;

public sealed class JourneyRuntimeOptionsTests
{
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
    [Trait("IntegrationSlice", "W2G-IS-01")]
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
    [Trait("IntegrationSlice", "W2G-IS-02")]
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
        GateStationId = "关卡",
        GateStationRiotId = 210,
        DispatchZone = "MAP-25-WIRE_TO_GATE",
        DispatchGeneration = 1,
        MinimumBatteryPercent = 40,
        SublotBoxCountPath = "/api/v2/sublot-box-count",
        AllowedWorkTypes = ["WIRE_TO_GATE"],
        AllowedDispatchZones = ["MAP-25-WIRE_TO_GATE"],
        AdmissionPolicyVersion = 1,
        AdmissionPolicyDeploymentId = "TEST-DEPLOYMENT-1",
        PackageCapacityRules =
        [
            new PackageCapacityRuleOptions
            {
                Pattern = "PKG-01",
                MatchType = "exact",
                MaxBoxesPerBasket = 4
            }
        ]
    };
}
