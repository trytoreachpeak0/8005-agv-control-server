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

    /// <summary>
    /// How long a vehicle may hold cargo waiting for more before it must leave (ADR-cross-0057), counted from the first
    /// LoadBatch's safety closure and never reset by a new stop, a disconnect or a restart. Kept apart from
    /// <see cref="StationDepartureWaitTimeout"/> on purpose: the two clocks must never share a field.
    /// </summary>
    /// <remarks>
    /// Thirty minutes by default and always positive. Batch 7's schema ticket (control-server#206) adds the setting; its
    /// reader is control-server#212.
    /// </remarks>
    public TimeSpan CargoHoldingTimeout { get; set; } = TimeSpan.FromMinutes(30);
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

    /// <summary>
    /// How long a journey may stand waiting for a person in one stage before the waiting journey watch logs it, with the
    /// vehicle's battery (control-server#273). Ten minutes by default, decided by the user on 2026-09-22.
    /// </summary>
    /// <remarks>
    /// The watch only reports: REQ-0169 lets a falling battery raise the alarm and nothing else, so no threshold here ever
    /// moves a vehicle. Must be positive.
    /// </remarks>
    public TimeSpan WaitingJourneyWarningAfter { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>How often the watch logs a wait again while it lasts. Five minutes by default; must be positive.</summary>
    public TimeSpan WaitingJourneyWarningRepeat { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The rescue line (control-server#273): under it the watch's log says a person has to move the vehicle to a charger.
    /// Fifteen percent by default; must be in 1..100 here, and below the <c>MandatoryChargeEntryThreshold</c> of every
    /// charging policy version in effect, which the server checks when it starts (<c>ChargingPolicyStartupCheck</c>,
    /// control-server#403): that threshold is the line under which the same log is already raised to an error.
    /// </summary>
    public int WaitingJourneyRescueBatteryPercent { get; set; } = 15;

    /// <summary>
    /// How long the waiting journey watch waits for one vehicle's battery from RIoT before it records "unknown"
    /// (control-server#273, review of #320). Two seconds by default; must be positive and at most ten seconds. The gateway's
    /// own timeout is thirty, and the watch reads one vehicle after another at the end of every round.
    /// </summary>
    public TimeSpan WaitingJourneyBatteryReadBudget { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How long after one of this server's own orders ended -- cancelled or deleted in RIoT, or its FAILED fault cleared by a
    /// person -- the server waits before it rebuilds the order for the same vehicle and the same demand (control-server#318,
    /// the first of its three guards). Thirty seconds by default, the user's decision of 2026-09-22: time for whoever is by
    /// the vehicle to step away, or to stop it. Positive -- zero would be no guard at all (review S6) -- and at most ten minutes.
    /// </summary>
    public TimeSpan OwnOrderRebuildDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The third guard of control-server#318 (REQ-0361): a demand that has a problem again within this long of its first
    /// problem -- counted from that problem, not from the rebuild it led to -- is not rebuilt again; the journey is held and
    /// alarmed for a person. What counts as "again" depends on the first problem's source (<c>OwnOrderRebuilds</c>). Ten
    /// minutes by default; must be positive.
    /// </summary>
    public TimeSpan OwnOrderRebuildRepeatWindow { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How long RIoT has to go on answering "no such order" for a charge order whose create went out with its result unknown,
    /// before the server gives the order up and ends the charging commitment as a confirmed failure (control-server#404,
    /// independent review M2). Every reading in that time has to be "not found" -- HTTP 404, or the exact absent-at-observation
    /// read real RIoT gives (HTTP 200, code 0, no result; review M-A) -- any other answer, or none, starts the count
    /// again -- and the vehicle has to be proven stopped with no unfinished order of its own. Without it a create whose answer
    /// was lost kept the vehicle's CHARGING purpose and the charger's reservation for ever. Two minutes by default; positive
    /// and at most one hour.
    /// </summary>
    public TimeSpan ChargingOrderAbsentAbandonAfter { get; set; } = TimeSpan.FromSeconds(120);

    /// <summary>
    /// Whether the server itself cancels the old charge order of a cycle in the clearing loop after an unable-to-charge
    /// (control-server#406). <b>Off by default.</b> REQ-0148 as revised in requirements baseline v1.9.0 (CP-0010) allows
    /// <c>CMD_ORDER_CANCEL</c> for this server's own charge order in its first case -- the vehicle is in REQ-0178's clearing
    /// loop and the cycle's old order has not ended -- but nobody has yet seen what cancelling a HANG charge order does to a
    /// vehicle standing on its charger: whether RIoT inserts the leave-the-charger act(78,2,0) and moves it beside the person
    /// clearing it (the independent review of #406; admission line 1). It stays off until that is measured on agv02, with the
    /// user's authorisation, on site (10-08). Off, a person ends the old order in RIoT, the server reads it ended, and with the
    /// manual confirmation the clearance completes.
    /// </summary>
    /// <remarks>
    /// On, it cancels exactly one order, once, inside REQ-0148's first case: only while the cycle is clearing and its clearance
    /// has not completed (no <c>CompletedAt</c>), whether or not a person has confirmed yet; only while this round's read finds
    /// it <c>HANG</c> and in no other state; only the order of this cycle's own intent (<c>order.OrderId == intent.OrderId</c>,
    /// ownership proven by the persisted intent, not by the id's shape), executed by this cycle's own vehicle (never one RIoT
    /// reads on any other vehicle or on none); and only when the command audit holds no cancel for that order yet. An
    /// unconfirmed cancel is not sent again (event 2270). It releases nothing and rebuilds nothing: the clearance completes
    /// only once the old order reads ended and a person's confirmation is recorded, and the vehicle stays held until then.
    /// </remarks>
    public bool UnableToChargeOldOrderCancelEnabled { get; set; }

    /// <summary>
    /// Whether a vehicle in the clearing loop is driven to a waiting point by this server (control-server#409, REQ-0178, the
    /// system proof of REQ-0179). <b>Off by default</b>, and off means exactly what the clearing loop did before: the vehicle
    /// stays where it is and only a person's manual station clearance (control-server#406) completes it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Not to be switched on before the RIoT call allowlist names this use.</b> The order is form one of section 1.2 (one
    /// <c>move</c> through <c>byDefaultMissions</c>, REQ-0294), whose "use" column today reads "transport, idle return" only;
    /// the coordinator adds "clearance to a waiting point" through a change of its own. Off, this server makes no RIoT call it
    /// did not make before.
    /// </para>
    /// <para>
    /// On, the vehicle sets off by itself the round after its old charge order reads ended -- which, with
    /// <see cref="UnableToChargeOldOrderCancelEnabled"/> off, is the moment a person ends it in RIoT. People on site have to know
    /// that before it is switched on: it is a run that moves a vehicle (admission line 1).
    /// </para>
    /// <para>
    /// <b>Switching it off stops new departures only.</b> A commitment whose order has not gone out yet is withdrawn; a move
    /// whose order has already gone out is driven to its end -- this server never cancels a clearance move (allowlist 1.3
    /// approves no cancel for it). To stop a vehicle already on its way, cancel its order in RIoT; the clearing then does not
    /// set off by itself again and only a manual station clearance completes it.
    /// </para>
    /// </remarks>
    public bool ClearanceToWaitingPointEnabled { get; set; }

    /// <summary>
    /// How long a charging vehicle has to read "not charging" without a break before it is a confirmed interruption
    /// (control-server#407, REQ-0285). The first reading only starts the observation; the interruption is confirmed at the
    /// first later reading whose observation time is at least this far from it, every reading in between fresh, continuous
    /// (no gap over <see cref="MaximumEvidenceAge"/>) and not charging. One poll interval apart (about two seconds on site)
    /// two reads can be one RIoT snapshot read twice (independent review S3a of #442). Sixty seconds by default; positive
    /// and at most ten minutes. It lives here, not in the ChargingPolicyVersion: that table has no column for it and this
    /// ticket adds no migration.
    /// </summary>
    public TimeSpan ChargingInterruptionConfirmAfter { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long after the server's cancel of a clearing cycle's old charge order it waits for RIoT to read the order ended
    /// before it warns that the cancel did not take (event 2270; control-server#406 review S3). Sixty seconds by default;
    /// positive and at most ten minutes.
    /// </summary>
    /// <remarks>
    /// The cancel's own read-back comes a moment after the call, and if RIoT applies a cancel asynchronously that read can
    /// still say HANG for an order that is about to end: warning on it would cry wolf. Sixty seconds is twelve of the field
    /// runtime's five-second rounds and two of the thirty-second waits this runtime already gives a person by the vehicle
    /// (<see cref="OwnOrderRebuildDelay"/>); an order still not ended after that is no longer "on its way". Nothing waits on
    /// it -- the clearance completes whenever the order reads ended -- it only decides when the warning is said.
    /// </remarks>
    public TimeSpan UnableToChargeOldOrderCancelSettleWindow { get; set; } = TimeSpan.FromSeconds(60);
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
    /// <summary>The configuration key batch 9-05 retired (control-server#403). Configuration keys are case-insensitive.</summary>
    public const string RetiredMinimumBatteryPercentKey = JourneyRuntimeOptions.SectionName + ":minimumBatteryPercent";

    public const string RetiredMinimumBatteryPercentMessage =
        "JourneyRuntime:minimumBatteryPercent is no longer read: the battery thresholds come from the approved, activated " +
        "charging policy version (MandatoryChargeEntryThreshold, the minimum post-task battery margin and the estimated " +
        "consumption per task; REQ-0281, REQ-0282, control-server#403). Remove the key (or the environment variable " +
        "JourneyRuntime__minimumBatteryPercent) and import, approve and activate a charging policy with ControlServer.FieldOps.";

    public ValidateOptionsResult Validate(string? name, JourneyRuntimeOptions options)
    {
        _ = name;
        // Before the Enabled check: a key that looks like it governs dispatch must not survive on any server, running
        // journeys or not (control-server#403).
        if (configuration.GetSection(RetiredMinimumBatteryPercentKey).Exists())
        {
            return ValidateOptionsResult.Fail(RetiredMinimumBatteryPercentMessage);
        }

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
        if (options.ChargingInterruptionConfirmAfter <= TimeSpan.Zero ||
            options.ChargingInterruptionConfirmAfter > TimeSpan.FromMinutes(10))
        {
            failures.Add("ChargingInterruptionConfirmAfter must be positive and at most 10 minutes.");
        }
        if (options.CheckpointWaitBudget <= TimeSpan.Zero)
        {
            failures.Add("CheckpointWaitBudget must be positive.");
        }
        if (options.AreaEndAdmissionRevokedTimeout <= TimeSpan.Zero)
        {
            failures.Add("AreaEndAdmissionRevokedTimeout must be positive.");
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
        if (options.CargoHoldingTimeout <= TimeSpan.Zero) failures.Add("CargoHoldingTimeout must be positive.");
        if (options.AgvLifecycleGeneration <= 0) failures.Add("AgvLifecycleGeneration must be positive.");
        if (options.MapId <= 0) failures.Add("MapId must be positive.");
        if (options.DispatchGeneration <= 0) failures.Add("DispatchGeneration must be positive.");
        if (options.WaitingJourneyWarningAfter <= TimeSpan.Zero) failures.Add("WaitingJourneyWarningAfter must be positive.");
        if (options.WaitingJourneyWarningRepeat <= TimeSpan.Zero) failures.Add("WaitingJourneyWarningRepeat must be positive.");
        // Below every effective MandatoryChargeEntryThreshold too, checked against the database at startup
        // (ChargingPolicyStartupCheck, control-server#403); the options alone can only check the range.
        if (options.WaitingJourneyRescueBatteryPercent is < 1 or > 100)
        {
            failures.Add("WaitingJourneyRescueBatteryPercent must be in 1..100.");
        }
        if (options.WaitingJourneyBatteryReadBudget <= TimeSpan.Zero ||
            options.WaitingJourneyBatteryReadBudget > TimeSpan.FromSeconds(10))
        {
            failures.Add("WaitingJourneyBatteryReadBudget must be positive and at most 10 s.");
        }
        if (options.UnableToChargeOldOrderCancelSettleWindow <= TimeSpan.Zero ||
            options.UnableToChargeOldOrderCancelSettleWindow > TimeSpan.FromMinutes(10))
        {
            failures.Add("UnableToChargeOldOrderCancelSettleWindow must be positive and at most 10 minutes.");
        }
        if (options.OwnOrderRebuildDelay <= TimeSpan.Zero || options.OwnOrderRebuildDelay > TimeSpan.FromMinutes(10))
        {
            failures.Add("OwnOrderRebuildDelay must be positive and at most 10 min.");
        }
        if (options.OwnOrderRebuildRepeatWindow <= TimeSpan.Zero) failures.Add("OwnOrderRebuildRepeatWindow must be positive.");
        if (options.ChargingOrderAbsentAbandonAfter <= TimeSpan.Zero ||
            options.ChargingOrderAbsentAbandonAfter > TimeSpan.FromHours(1))
        {
            failures.Add("ChargingOrderAbsentAbandonAfter must be positive and at most 1 h.");
        }
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
