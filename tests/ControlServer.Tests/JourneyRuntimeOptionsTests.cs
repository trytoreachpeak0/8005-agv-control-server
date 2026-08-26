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
    public void EnabledRuntimeRejectsMissingExternalSecretReferences()
    {
        string suffix = Guid.NewGuid().ToString("N");
        Dictionary<string, string?> values = new()
        {
            ["MesIngest:sharedSecretEnvironmentVariable"] = $"MISSING_MES_{suffix}",
            ["RIoT:callApiKeyEnvironmentVariable"] = $"MISSING_RIOT_{suffix}",
            ["OnboardTransport:credentialEnvironmentVariable"] = $"MISSING_ONBOARD_{suffix}"
        };
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        JourneyRuntimeOptionsValidator validator = new(configuration);

        Microsoft.Extensions.Options.ValidateOptionsResult result = validator.Validate(null, ValidEnabledOptions());

        Assert.True(result.Failed);
        Assert.Equal(3, result.Failures.Count());
        Assert.Contains(result.Failures, failure => failure.StartsWith("MesIngest:", StringComparison.Ordinal));
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
        AgvId = "AGV-8005-01",
        VehicleKey = "VEHICLE-KEY-01",
        AgvLifecycleGeneration = 1,
        MapId = 29,
        MapIdentity = "MAP-29",
        PickupStationId = "PICKUP-01",
        PickupStationRiotId = 12,
        GateStationId = "GATE-01",
        GateStationRiotId = 20,
        DispatchGeneration = 1,
        MinimumBatteryPercent = 40,
        SublotBoxCountPath = "/api/v2/sublot-box-count",
        AllowedWorkTypes = ["WIRE_TO_GATE"],
        AllowedDispatchZones = ["ZONE-01"],
        AdmissionPolicyVersion = 1,
        AdmissionPolicyDeploymentId = "TEST-DEPLOYMENT-1",
        StationTaskTypeAdmissions =
        [
            new StationTaskTypeAdmissionOptions
            {
                StationId = "PICKUP-01",
                TaskType = "WIRE_TO_GATE"
            }
        ],
        Routes =
        [
            new JourneyRouteOptions
            {
                Area = "AREA-01",
                Eqp = "EQP-01",
                DispatchZone = "ZONE-01",
                PickupStationId = "PICKUP-01",
                PickupStationRiotId = 12,
                RouteEvidenceId = "ROUTE-29-12-20"
            }
        ],
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
