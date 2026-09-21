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

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-01")]
    public void AChargeTriggerBelowTheDemandFloorIsRefused()
    {
        // 8005-agv-program#53: shipped at 20 against a floor of 30, a vehicle between the two was
        // refused every demand and never sent to charge.
        IConfiguration configuration = new ConfigurationBuilder().Build();
        JourneyRuntimeOptionsValidator validator = new(configuration);
        JourneyRuntimeOptions options = ValidEnabledOptions();
        options.AutoChargingEnabled = true;
        options.ChargerStationId = "充电点1";
        options.ChargerStationRiotId = 211;
        options.ChargeResumeBatteryPercent = 80;
        const string refusal = "ChargeTriggerBatteryPercent must be at least MinimumBatteryPercent.";

        options.ChargeTriggerBatteryPercent = options.MinimumBatteryPercent - 1;
        Microsoft.Extensions.Options.ValidateOptionsResult below = validator.Validate(null, options);
        options.ChargeTriggerBatteryPercent = options.MinimumBatteryPercent;
        Microsoft.Extensions.Options.ValidateOptionsResult atFloor = validator.Validate(null, options);

        Assert.Contains(refusal, below.Failures ?? [], StringComparer.Ordinal);
        Assert.DoesNotContain(refusal, atFloor.Failures ?? [], StringComparer.Ordinal);
        Assert.True(new JourneyRuntimeOptions().ChargeTriggerBatteryPercent >=
                    new JourneyRuntimeOptions().MinimumBatteryPercent);
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-01")]
    public void TheBlockedJourneyRescueLineMustSitBelowTheOrdinaryTrigger()
    {
        // #273: at or above the ordinary trigger it would stop being a rescue, and a blocked
        // vehicle would leave the spot it stopped at on a reading that merely makes a fresh errand
        // wait. The shipped defaults have to satisfy it without any configuration.
        IConfiguration configuration = new ConfigurationBuilder().Build();
        JourneyRuntimeOptionsValidator validator = new(configuration);
        JourneyRuntimeOptions options = ValidEnabledOptions();
        options.AutoChargingEnabled = true;
        options.ChargerStationId = "充电点1";
        options.ChargerStationRiotId = 211;
        const string refusal = "BlockedJourneyChargeTriggerBatteryPercent must be below ChargeTriggerBatteryPercent.";

        Microsoft.Extensions.Options.ValidateOptionsResult shipped = validator.Validate(null, options);
        options.BlockedJourneyChargeTriggerBatteryPercent = options.ChargeTriggerBatteryPercent;
        Microsoft.Extensions.Options.ValidateOptionsResult atTrigger = validator.Validate(null, options);

        Assert.DoesNotContain(refusal, shipped.Failures ?? [], StringComparer.Ordinal);
        Assert.Contains(refusal, atTrigger.Failures ?? [], StringComparer.Ordinal);
        Assert.Equal(15, new JourneyRuntimeOptions().BlockedJourneyChargeTriggerBatteryPercent);
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
        AdmissionPolicyDeploymentId = "TEST-DEPLOYMENT-1"
    };
}
