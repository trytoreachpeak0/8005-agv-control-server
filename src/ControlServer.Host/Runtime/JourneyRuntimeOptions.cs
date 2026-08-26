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
    public string PickupStationId { get; set; } = string.Empty;
    public int PickupStationRiotId { get; set; }
    public string GateStationId { get; set; } = string.Empty;
    public int GateStationRiotId { get; set; }
    public long DispatchGeneration { get; set; }
    public int MinimumBatteryPercent { get; set; }
    public TimeSpan MaximumEvidenceAge { get; set; } = TimeSpan.FromSeconds(30);
    public string SublotBoxCountPath { get; set; } = string.Empty;
    public string[] AllowedWorkTypes { get; set; } = [];
    public string[] AllowedDispatchZones { get; set; } = [];
    public long AdmissionPolicyVersion { get; set; }
    public string AdmissionPolicyDeploymentId { get; set; } = string.Empty;
    public StationTaskTypeAdmissionOptions[] StationTaskTypeAdmissions { get; set; } = [];
    public JourneyRouteOptions[] Routes { get; set; } = [];
    public PackageCapacityRuleOptions[] PackageCapacityRules { get; set; } = [];
}

public sealed class JourneyRouteOptions
{
    public string Area { get; set; } = string.Empty;
    public string Eqp { get; set; } = string.Empty;
    public string DispatchZone { get; set; } = string.Empty;
    public string PickupStationId { get; set; } = string.Empty;
    public int PickupStationRiotId { get; set; }
    public string RouteEvidenceId { get; set; } = string.Empty;
}

public sealed class PackageCapacityRuleOptions
{
    public string Pattern { get; set; } = string.Empty;
    public string MatchType { get; set; } = string.Empty;
    public int MaxBoxesPerBasket { get; set; }
}

public sealed class StationTaskTypeAdmissionOptions
{
    public string StationId { get; set; } = string.Empty;
    public string TaskType { get; set; } = string.Empty;
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
        RequireText(options.PickupStationId, nameof(options.PickupStationId), failures);
        RequireText(options.GateStationId, nameof(options.GateStationId), failures);
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
        if (options.PickupStationRiotId <= 0) failures.Add("PickupStationRiotId must be positive.");
        if (options.GateStationRiotId <= 0) failures.Add("GateStationRiotId must be positive.");
        if (options.DispatchGeneration <= 0) failures.Add("DispatchGeneration must be positive.");
        if (options.MinimumBatteryPercent is < 1 or > 100) failures.Add("MinimumBatteryPercent must be in 1..100.");
        if (options.AdmissionPolicyVersion <= 0) failures.Add("AdmissionPolicyVersion must be positive.");
        RequireText(options.AdmissionPolicyDeploymentId, nameof(options.AdmissionPolicyDeploymentId), failures);
        if (!options.AllowedWorkTypes.Contains("WIRE_TO_GATE", StringComparer.Ordinal))
            failures.Add("AllowedWorkTypes must explicitly include WIRE_TO_GATE.");
        if (options.AllowedDispatchZones.Length == 0) failures.Add("AllowedDispatchZones must not be empty.");
        if (options.Routes.Length == 0) failures.Add("Routes must not be empty.");
        if (options.PackageCapacityRules.Length == 0) failures.Add("PackageCapacityRules must not be empty.");
        if (options.StationTaskTypeAdmissions.Length == 0)
            failures.Add("StationTaskTypeAdmissions must not be empty.");
        RequireExternalSecret("MesIngest:sharedSecretEnvironmentVariable", failures);
        RequireExternalSecret("RIoT:callApiKeyEnvironmentVariable", failures);
        RequireExternalSecret("OnboardTransport:credentialEnvironmentVariable", failures);

        foreach (JourneyRouteOptions route in options.Routes)
        {
            RequireText(route.Area, "Routes[].Area", failures);
            RequireText(route.Eqp, "Routes[].Eqp", failures);
            RequireText(route.DispatchZone, "Routes[].DispatchZone", failures);
            RequireText(route.PickupStationId, "Routes[].PickupStationId", failures);
            RequireText(route.RouteEvidenceId, "Routes[].RouteEvidenceId", failures);
            if (route.PickupStationRiotId <= 0) failures.Add("Routes[].PickupStationRiotId must be positive.");
        }
        foreach (PackageCapacityRuleOptions rule in options.PackageCapacityRules)
        {
            RequireText(rule.Pattern, "PackageCapacityRules[].Pattern", failures);
            if (rule.MatchType is not ("exact" or "prefix"))
                failures.Add("PackageCapacityRules[].MatchType must be exact or prefix.");
            if (rule.MaxBoxesPerBasket <= 0)
                failures.Add("PackageCapacityRules[].MaxBoxesPerBasket must be positive.");
        }
        HashSet<(string StationId, string TaskType)> admissions = [];
        foreach (StationTaskTypeAdmissionOptions admission in options.StationTaskTypeAdmissions)
        {
            RequireText(admission.StationId, "StationTaskTypeAdmissions[].StationId", failures);
            RequireText(admission.TaskType, "StationTaskTypeAdmissions[].TaskType", failures);
            if (admission.StationId != options.PickupStationId && admission.StationId != options.GateStationId)
                failures.Add("StationTaskTypeAdmissions[].StationId must name a configured journey station.");
            if (!options.AllowedWorkTypes.Contains(admission.TaskType, StringComparer.Ordinal))
                failures.Add("StationTaskTypeAdmissions[].TaskType must name an allowed work type.");
            if (!admissions.Add((admission.StationId, admission.TaskType)))
                failures.Add("StationTaskTypeAdmissions must not contain duplicate relations.");
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
}
