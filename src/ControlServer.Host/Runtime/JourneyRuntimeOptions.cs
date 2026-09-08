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
    public int MinimumBatteryPercent { get; set; } = 30;

    /// <summary>
    /// Whether the runtime drives the vehicle to the charger by itself when it runs low between
    /// demands. Off by default: it needs the charger's exact station identity on the live map, and
    /// a deployment that has not supplied one must not start moving the vehicle on its own.
    /// </summary>
    public bool AutoChargingEnabled { get; set; }
    public string ChargerStationId { get; set; } = string.Empty;
    public int ChargerStationRiotId { get; set; }

    /// <summary>
    /// Below this the runtime sends the vehicle to charge. It sits below
    /// <see cref="MinimumBatteryPercent"/> on purpose -- a vehicle that is merely too low to accept
    /// a demand is not yet worth a trip to the pad, and the gap keeps it from oscillating between
    /// "just able to work" and "off to charge".
    /// </summary>
    public int ChargeTriggerBatteryPercent { get; set; } = 20;

    /// <summary>
    /// The level at which a charging vehicle becomes available for demands again. The vehicle stays
    /// physically on the charger and keeps reporting CHARGING, so this level -- not the charging
    /// state -- is what ends the refusal in ValidateDynamicFacts.
    /// </summary>
    public int ChargeResumeBatteryPercent { get; set; } = 80;
    public TimeSpan MaximumEvidenceAge { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long a runtime iteration waits, right after asking for a pre-departure safety check, for
    /// the peer's answer. The peer stamps that answer with its own short validity window, so coming
    /// back for it a whole poll interval later read evidence that had already expired. The peer
    /// answers in tens of milliseconds; this only has to cover that.
    /// </summary>
    public TimeSpan DepartureSafetyResultWait { get; set; } = TimeSpan.FromMilliseconds(1500);
    /// <summary>
    /// How long the pickup stop waits for an operator to enter a sublot before the runtime ends the
    /// demand on its own. A stop can legitimately have nothing to load, and without this the
    /// journey holds the vehicle and the pickup station forever. <see cref="TimeSpan.Zero"/>
    /// disables the timeout and leaves the operator's explicit cancellation as the only way out.
    /// </summary>
    public TimeSpan SublotWaitTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long a journey may go on collecting cargo before it has to head for the gate, measured
    /// from the moment its first LoadBatch closed safely. ADR-cross-0057 sets it at 30 minutes.
    /// </summary>
    /// <remarks>
    /// It is a different deadline from <see cref="SublotWaitTimeout"/> and does not replace it:
    /// that one answers "does this stop keep waiting for an operator" and ends the stop; this one
    /// answers "does this vehicle keep taking cargo" and ends the whole loading phase. It is not
    /// reset by a new stop or a new batch -- a vehicle that keeps being handed demands would
    /// otherwise never leave. <see cref="TimeSpan.Zero"/> disables it, leaving "full" as the only
    /// way the loading phase ends on its own.
    /// </remarks>
    public TimeSpan HoldingTimeout { get; set; } = TimeSpan.FromMinutes(30);
    public string SublotBoxCountPath { get; set; } = string.Empty;
    public string[] AllowedWorkTypes { get; set; } = [];
    public string[] AllowedDispatchZones { get; set; } = [];
    public long AdmissionPolicyVersion { get; set; }
    public string AdmissionPolicyDeploymentId { get; set; } = string.Empty;
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
        // Zero disables it. The floor only catches a slipped decimal point -- a value that cancels
        // demands out from under an operator still walking to the vehicle is a configuration
        // review's job, not a validator's, and the shipped value is five minutes. It is this low
        // because the L2 scenario layer has to cross the window inside a run that lasts seconds.
        if (options.SublotWaitTimeout < TimeSpan.Zero ||
            options.SublotWaitTimeout > TimeSpan.Zero && options.SublotWaitTimeout < TimeSpan.FromSeconds(5))
        {
            failures.Add("SublotWaitTimeout must be zero (disabled) or at least 5 s.");
        }
        // Same shape and the same reason: zero disables it, and the floor only catches a slipped
        // decimal point. The shipped value is thirty minutes; the L2 scenario layer has to cross the
        // window inside a run that lasts seconds.
        if (options.HoldingTimeout < TimeSpan.Zero ||
            options.HoldingTimeout > TimeSpan.Zero && options.HoldingTimeout < TimeSpan.FromSeconds(5))
        {
            failures.Add("HoldingTimeout must be zero (disabled) or at least 5 s.");
        }
        if (options.MaximumEvidenceAge <= TimeSpan.Zero) failures.Add("MaximumEvidenceAge must be positive.");
        if (options.DepartureSafetyResultWait <= TimeSpan.Zero ||
            options.DepartureSafetyResultWait > TimeSpan.FromSeconds(10))
        {
            failures.Add("DepartureSafetyResultWait must be positive and at most 10 s.");
        }
        if (options.AgvLifecycleGeneration <= 0) failures.Add("AgvLifecycleGeneration must be positive.");
        if (options.MapId <= 0) failures.Add("MapId must be positive.");
        if (options.GateStationRiotId <= 0) failures.Add("GateStationRiotId must be positive.");
        if (options.DispatchGeneration <= 0) failures.Add("DispatchGeneration must be positive.");
        if (options.MinimumBatteryPercent is < 1 or > 100) failures.Add("MinimumBatteryPercent must be in 1..100.");
        if (options.AutoChargingEnabled)
        {
            RequireText(options.ChargerStationId, nameof(options.ChargerStationId), failures);
            if (options.ChargerStationRiotId <= 0) failures.Add("ChargerStationRiotId must be positive.");
            if (options.ChargeTriggerBatteryPercent is < 1 or > 100)
                failures.Add("ChargeTriggerBatteryPercent must be in 1..100.");
            if (options.ChargeResumeBatteryPercent is < 1 or > 100)
                failures.Add("ChargeResumeBatteryPercent must be in 1..100.");
            // Resuming at or below the trigger would send the vehicle back to the charger the moment
            // it was released, and resuming below the demand floor would release it into a state
            // where every candidate is refused for battery anyway.
            if (options.ChargeResumeBatteryPercent <= options.ChargeTriggerBatteryPercent)
                failures.Add("ChargeResumeBatteryPercent must exceed ChargeTriggerBatteryPercent.");
            if (options.ChargeResumeBatteryPercent < options.MinimumBatteryPercent)
                failures.Add("ChargeResumeBatteryPercent must be at least MinimumBatteryPercent.");
        }
        if (options.AdmissionPolicyVersion <= 0) failures.Add("AdmissionPolicyVersion must be positive.");
        RequireText(options.AdmissionPolicyDeploymentId, nameof(options.AdmissionPolicyDeploymentId), failures);
        if (!options.AllowedWorkTypes.Contains("WIRE_TO_GATE", StringComparer.Ordinal))
            failures.Add("AllowedWorkTypes must explicitly include WIRE_TO_GATE.");
        if (!options.AllowedDispatchZones.Contains(options.DispatchZone, StringComparer.Ordinal))
            failures.Add("AllowedDispatchZones must explicitly include DispatchZone.");
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
