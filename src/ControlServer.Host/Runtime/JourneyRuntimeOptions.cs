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
    public string DispatchZone { get; set; } = string.Empty;
    public long DispatchGeneration { get; set; }
    public int MinimumBatteryPercent { get; set; } = 30;
    public TimeSpan MaximumEvidenceAge { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long a runtime iteration waits, right after asking for a pre-departure safety check, for
    /// the peer's answer. The peer stamps that answer with its own short validity window, so coming
    /// back for it a whole poll interval later read evidence that had already expired. The peer
    /// answers in tens of milliseconds; this only has to cover that.
    /// </summary>
    public TimeSpan DepartureSafetyResultWait { get; set; } = TimeSpan.FromMilliseconds(1500);

    /// <summary>
    /// How long a vehicle stays at the pickup after its load commits before departure safety is
    /// asked for (ADR-cross-0055's StationDepartureWaitTimeout). This is the window REQ-0237 keeps for
    /// correcting an ordinary mis-placement: a correction may start only inside it, holds the vehicle
    /// while it is open, and restarts the full wait when it closes.
    /// </summary>
    /// <remarks>
    /// The project default is five minutes. <see cref="TimeSpan.Zero"/> turns the wait off, which
    /// also takes the correction window away; any other value must be at least five seconds.
    /// </remarks>
    public TimeSpan StationDepartureWaitTimeout { get; set; } = TimeSpan.FromMinutes(5);
    public string SublotBoxCountPath { get; set; } = string.Empty;
    public string[] AllowedWorkTypes { get; set; } = [];
    public string[] AllowedDispatchZones { get; set; } = [];
    public long AdmissionPolicyVersion { get; set; }
    public string AdmissionPolicyDeploymentId { get; set; } = string.Empty;

    /// <summary>
    /// The vehicles this server drives, each with the identity pair and the policy slice that
    /// applies to it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Empty means one vehicle, the one named by the fields above.</b> That is not a permissive
    /// default: the single-vehicle keys, <see cref="AllowedWorkTypes"/> and
    /// <see cref="DispatchZone"/> are configuration too, and deriving one roster entry from them
    /// is what keeps an existing deployment behaviour-identical when it is upgraded to a build
    /// that can drive several. A vehicle that is in neither the roster nor the derived entry is
    /// not dispatched at all.
    /// </para>
    /// <para>
    /// When the roster is stated explicitly it must contain the primary pair, so that the map
    /// and dispatch generation the fields above fix are the ones the roster's
    /// vehicles actually run under rather than a second, contradictory configuration.
    /// </para>
    /// </remarks>
    public FleetVehicleOptions[] Fleet { get; set; } = [];

    /// <summary>
    /// How long a vehicle may hold at a traffic checkpoint before the wait stops being ordinary.
    /// </summary>
    /// <remarks>
    /// A checkpoint wait is normal traffic behaviour with several vehicles on one map, so it is
    /// reported rather than acted on — until it lasts longer than this, at which point the journey
    /// names a different reason and the wait is logged for a person. The budget is deliberately
    /// generous: the failure this guards against is a vehicle that never gets its turn, not one
    /// that waits a while.
    /// </remarks>
    public TimeSpan CheckpointWaitBudget { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long a journey that has arrived at its AREA machine to unload may wait for the machine to admit its task type
    /// again before it is blocked for manual recovery (control-server#198).
    /// </summary>
    /// <remarks>
    /// Only a journey whose AREA machine is the drop-off (STAGING_TO_WIRE) is asked at arrival, and it arrives loaded:
    /// the goods are on the vehicle and nobody is told. Ten minutes by default, the first line of the site escalation
    /// procedure (T0 + 10 min). Must be positive; there is no switch to wait for ever.
    /// </remarks>
    public TimeSpan AreaEndAdmissionRevokedTimeout { get; set; } = TimeSpan.FromMinutes(10);
}

/// <summary>One vehicle's identity and the policy slice configured for it.</summary>
/// <remarks>
/// The identity is a pair because the two systems name the same vehicle differently: 8005 keys
/// facts on <see cref="AgvId"/> and RIoT keys dispatch on <see cref="VehicleKey"/>. Carrying both
/// here is what lets a round decide for a vehicle and read that vehicle's fault state.
/// </remarks>
public sealed class FleetVehicleOptions
{
    public string AgvId { get; set; } = string.Empty;
    public string VehicleKey { get; set; } = string.Empty;
    public long AgvLifecycleGeneration { get; set; }

    /// <summary>The task types this vehicle may take. Empty means it may take none.</summary>
    public string[] AllowedTaskTypes { get; set; } = [];

    /// <summary>The dispatch zones this vehicle serves. Empty means it serves none.</summary>
    public string[] Zones { get; set; } = [];

    /// <summary>
    /// How long this vehicle's segment of one dispatch round may take before the round moves on.
    /// </summary>
    /// <remarks>
    /// The budget is per vehicle rather than per round because the thing it protects against is
    /// one vehicle's reads hanging: without it, a single unreachable peer stalls every other
    /// vehicle behind it for as long as the hang lasts.
    /// </remarks>
    public int RoundTimeoutMilliseconds { get; set; } = 30_000;
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
        if (options.CheckpointWaitBudget <= TimeSpan.Zero)
        {
            failures.Add("CheckpointWaitBudget must be positive.");
        }
        if (options.DepartureSafetyResultWait <= TimeSpan.Zero ||
            options.DepartureSafetyResultWait > TimeSpan.FromSeconds(10))
        {
            failures.Add("DepartureSafetyResultWait must be positive and at most 10 s.");
        }
        if (options.StationDepartureWaitTimeout != TimeSpan.Zero &&
            options.StationDepartureWaitTimeout < TimeSpan.FromSeconds(5))
        {
            failures.Add("StationDepartureWaitTimeout must be zero (off) or at least 5 s.");
        }
        if (options.AgvLifecycleGeneration <= 0) failures.Add("AgvLifecycleGeneration must be positive.");
        if (options.MapId <= 0) failures.Add("MapId must be positive.");
        if (options.DispatchGeneration <= 0) failures.Add("DispatchGeneration must be positive.");
        if (options.MinimumBatteryPercent is < 1 or > 100) failures.Add("MinimumBatteryPercent must be in 1..100.");
        if (options.AdmissionPolicyVersion <= 0) failures.Add("AdmissionPolicyVersion must be positive.");
        RequireText(options.AdmissionPolicyDeploymentId, nameof(options.AdmissionPolicyDeploymentId), failures);
        if (!options.AllowedDispatchZones.Contains(options.DispatchZone, StringComparer.Ordinal))
            failures.Add("AllowedDispatchZones must explicitly include DispatchZone.");
        ValidateFleet(options, failures);
        RequireExternalSecretUnlessLoopback(
            "MesIngest:baseUrl",
            "MesIngest:sharedSecretEnvironmentVariable",
            failures);
        RequireExternalSecret("RIoT:callApiKeyEnvironmentVariable", failures);
        RequireExternalSecret("OnboardTransport:credentialEnvironmentVariable", failures);
        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    /// <summary>
    /// Checks the roster as a whole, not entry by entry: what makes a fleet configuration wrong is
    /// usually a relation between entries — a duplicated identity, or a roster that contradicts the
    /// primary pair — rather than a malformed field.
    /// </summary>
    private static void ValidateFleet(JourneyRuntimeOptions options, List<string> failures)
    {
        if (options.Fleet.Length == 0)
        {
            return;
        }

        foreach (FleetVehicleOptions vehicle in options.Fleet)
        {
            RequireText(vehicle.AgvId, "Fleet:AgvId", failures);
            RequireText(vehicle.VehicleKey, "Fleet:VehicleKey", failures);
            if (vehicle.AgvLifecycleGeneration <= 0)
            {
                failures.Add($"Fleet entry '{vehicle.AgvId}' must have a positive AgvLifecycleGeneration.");
            }
            // A budget outside these bounds is a configuration mistake in one of two directions: a
            // budget shorter than a single RIoT round-trip cancels every round before it can
            // decide anything, and one longer than ten minutes is not a budget.
            if (vehicle.RoundTimeoutMilliseconds is < 1_000 or > 600_000)
            {
                failures.Add(
                    $"Fleet entry '{vehicle.AgvId}' must have RoundTimeoutMilliseconds in 1000..600000.");
            }
            if (vehicle.Zones.Any(string.IsNullOrWhiteSpace) ||
                vehicle.AllowedTaskTypes.Any(string.IsNullOrWhiteSpace))
            {
                failures.Add($"Fleet entry '{vehicle.AgvId}' must not name an empty zone or task type.");
            }
        }

        if (options.Fleet.Select(vehicle => vehicle.AgvId).Distinct(StringComparer.Ordinal).Count() !=
            options.Fleet.Length)
        {
            failures.Add("Fleet must not name the same AgvId twice.");
        }
        if (options.Fleet.Select(vehicle => vehicle.VehicleKey).Distinct(StringComparer.Ordinal).Count() !=
            options.Fleet.Length)
        {
            failures.Add("Fleet must not name the same VehicleKey twice.");
        }
        if (!options.Fleet.Any(vehicle =>
                string.Equals(vehicle.AgvId, options.AgvId, StringComparison.Ordinal) &&
                string.Equals(vehicle.VehicleKey, options.VehicleKey, StringComparison.Ordinal)))
        {
            failures.Add("Fleet must contain the primary AgvId/VehicleKey pair.");
        }
        foreach (FleetVehicleOptions vehicle in options.Fleet)
        {
            foreach (string zone in vehicle.Zones.Where(zone =>
                         !options.AllowedDispatchZones.Contains(zone, StringComparer.Ordinal)))
            {
                failures.Add(
                    $"Fleet entry '{vehicle.AgvId}' serves zone '{zone}', which AllowedDispatchZones omits.");
            }
        }
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
