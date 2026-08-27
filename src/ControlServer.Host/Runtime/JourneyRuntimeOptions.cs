using Microsoft.Extensions.Options;

namespace ControlServer.Host.Runtime;

public sealed class JourneyRuntimeOptions
{
    public const string SectionName = "JourneyRuntime";

    public bool Enabled { get; set; }
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);
    public string AgvId { get; set; } = string.Empty;
    public string VehicleKey { get; set; } = string.Empty;
    public long AgvLifecycleGeneration { get; set; }
    public int MapId { get; set; }
    public string MapIdentity { get; set; } = string.Empty;
    public string GateStationId { get; set; } = string.Empty;
    public int GateStationRiotId { get; set; }
    public string DispatchZone { get; set; } = string.Empty;
    public long DispatchGeneration { get; set; }
    public int MinimumBatteryPercent { get; set; }
    public TimeSpan MaximumEvidenceAge { get; set; } = TimeSpan.FromSeconds(30);
    public string SublotBoxCountPath { get; set; } = string.Empty;
    public string[] AllowedWorkTypes { get; set; } = [];
    public string[] AllowedDispatchZones { get; set; } = [];
    public long AdmissionPolicyVersion { get; set; }
    public string AdmissionPolicyDeploymentId { get; set; } = string.Empty;
    public PackageCapacityRuleOptions[] PackageCapacityRules { get; set; } = [];
}

public sealed class PackageCapacityRuleOptions
{
    public string Pattern { get; set; } = string.Empty;
    public string MatchType { get; set; } = string.Empty;
    public int MaxBoxesPerBasket { get; set; }
}

public sealed class JourneyRuntimeOptionsValidator(IConfiguration configuration) : IValidateOptions<JourneyRuntimeOptions>
{
    public ValidateOptionsResult Validate(string? name, JourneyRuntimeOptions options)
    {
        _ = name;
        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        List<string> failures = [];
        RequireText(options.AgvId, nameof(options.AgvId), failures);
        RequireText(options.VehicleKey, nameof(options.VehicleKey), failures);
        RequireText(options.MapIdentity, nameof(options.MapIdentity), failures);
        RequireText(options.GateStationId, nameof(options.GateStationId), failures);
        RequireText(options.DispatchZone, nameof(options.DispatchZone), failures);
        RequireText(options.SublotBoxCountPath, nameof(options.SublotBoxCountPath), failures);
        if (options.SublotBoxCountPath.Length == 0 || options.SublotBoxCountPath[0] != '/' ||
            options.SublotBoxCountPath.StartsWith("//", StringComparison.Ordinal) ||
            Uri.TryCreate(options.SublotBoxCountPath, UriKind.Absolute, out _))
        {
            failures.Add("SublotBoxCountPath must be a same-origin absolute path.");
        }
        if (options.PollInterval < TimeSpan.FromMilliseconds(100)) failures.Add("PollInterval must be at least 100 ms.");
        if (options.MaximumEvidenceAge <= TimeSpan.Zero) failures.Add("MaximumEvidenceAge must be positive.");
        if (options.AgvLifecycleGeneration <= 0) failures.Add("AgvLifecycleGeneration must be positive.");
        if (options.MapId <= 0) failures.Add("MapId must be positive.");
        if (options.GateStationRiotId <= 0) failures.Add("GateStationRiotId must be positive.");
        if (options.DispatchGeneration <= 0) failures.Add("DispatchGeneration must be positive.");
        if (options.MinimumBatteryPercent is < 1 or > 100) failures.Add("MinimumBatteryPercent must be in 1..100.");
        if (options.AdmissionPolicyVersion <= 0) failures.Add("AdmissionPolicyVersion must be positive.");
        RequireText(options.AdmissionPolicyDeploymentId, nameof(options.AdmissionPolicyDeploymentId), failures);
        if (!options.AllowedWorkTypes.Contains("WIRE_TO_GATE", StringComparer.Ordinal))
            failures.Add("AllowedWorkTypes must explicitly include WIRE_TO_GATE.");
        if (!options.AllowedDispatchZones.Contains(options.DispatchZone, StringComparer.Ordinal))
            failures.Add("AllowedDispatchZones must explicitly include DispatchZone.");
        if (options.PackageCapacityRules.Length == 0) failures.Add("PackageCapacityRules must not be empty.");
        RequireExternalSecretUnlessLoopback(
            "MesIngest:baseUrl",
            "MesIngest:sharedSecretEnvironmentVariable",
            failures);
        RequireExternalSecret("RIoT:callApiKeyEnvironmentVariable", failures);
        RequireExternalSecret("OnboardTransport:credentialEnvironmentVariable", failures);
        foreach (PackageCapacityRuleOptions rule in options.PackageCapacityRules)
        {
            RequireText(rule.Pattern, "PackageCapacityRules[].Pattern", failures);
            if (rule.MatchType is not ("exact" or "prefix"))
                failures.Add("PackageCapacityRules[].MatchType must be exact or prefix.");
            if (rule.MaxBoxesPerBasket <= 0)
                failures.Add("PackageCapacityRules[].MaxBoxesPerBasket must be positive.");
        }
        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static void RequireText(string value, string name, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(value)) failures.Add($"{name} is required.");
    }

    private void RequireExternalSecret(string configurationKey, List<string> failures)
    {
        string? variable = configuration[configurationKey];
        if (string.IsNullOrWhiteSpace(variable) ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(variable)))
        {
            failures.Add($"{configurationKey} must name a populated external environment variable.");
        }
    }

    private void RequireExternalSecretUnlessLoopback(
        string baseUrlKey,
        string secretVariableKey,
        List<string> failures)
    {
        string? baseUrl = configuration[baseUrlKey];
        if (Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? uri) && uri.IsLoopback)
        {
            return;
        }

        RequireExternalSecret(secretVariableKey, failures);
    }
}
