using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using ControlServer.Application;
using ControlServer.Domain;

namespace ControlServer.EmergencyDrill;

/// <summary>
/// One method per subcommand. Each one that sends something to RIoT sends it at most once per run,
/// writes that it is about to into drill-state.json first, and never chains into the next step.
/// </summary>
/// <remarks>
/// The wind-down order is trigger → cancel-order → release, enforced by guards on both ends (user
/// ruling, 2026-09-14). Released first, the latch would clear under a still-active drill order and
/// RIoT could drive the vehicle on to its destination while staff stand beside it.
/// </remarks>
internal static class DrillCommands
{
    private const int PollIntervalMs = 500;
    private const int FastPollIntervalMs = 300;
    private const int CancelTerminalWaitSeconds = 20;
    private const string Ok = RiotVehicleEmergencyObservation.Ok;
    private const string CanRecover = RiotVehicleEmergencyObservation.CanRecover;
    private const string CanNotRecover = RiotVehicleEmergencyObservation.CanNotRecover;
    private const string CancelRefused =
        "cancel-order refused; nothing was sent. CMD_ORDER_CANCEL is cleanup after the stop, never a substitute for it";

    /// <summary>Safety reason codes that mean the vehicle may hold an order, or that nobody could tell.</summary>
    private static readonly HashSet<string> BlockingSafetyReasons = new(StringComparer.Ordinal)
    {
        "RIOT_NONFINAL_ORDER_PRESENT",
        "RIOT_NONFINAL_ORDER_COVERAGE_UNKNOWN",
        "RIOT_READ_TIMEOUT",
        "RIOT_READ_FAILED",
        "RIOT_MOTION_ACTIVE"
    };

    // ---- init -------------------------------------------------------------------------------------

    internal static CommandOutcome Init(ParsedArgs args)
    {
        CommandOutcome outcome = new("init");
        string? evidencePath = args.Get("evidence");
        string? deviceKey = args.Get("device-key");
        string? mapIdText = args.Get("map-id");
        string? baseUrlText = args.Get("riot-base-url");
        if (evidencePath is null || deviceKey is null || mapIdText is null || baseUrlText is null)
        {
            return outcome.Usage("init needs --evidence <dir> --device-key <key> --map-id 25 --riot-base-url <url>");
        }

        bool fakeRiot = args.Has("fake-riot");
        string mapIdentity = args.Get("map-identity") ?? DrillGuards.DefaultMapIdentity;
        Uri? baseUrl = DrillGuards.ParseBaseUrl(baseUrlText);
        outcome.Guard("RIoT base URL is an http(s) origin without credentials, path or query", baseUrl is not null, baseUrlText);
        string? keyRefusal = baseUrl is null
            ? "no valid base URL to check the key against"
            : DrillGuards.RefuseDeviceKey(deviceKey, fakeRiot, baseUrl);
        outcome.Guard("device key is covered by the 2026-09-14 exception", keyRefusal is null, keyRefusal ?? deviceKey);
        bool mapOk = int.TryParse(mapIdText, NumberStyles.None, CultureInfo.InvariantCulture, out int mapId) &&
            mapId == DrillGuards.ApprovedMapId;
        outcome.Guard("map id is 25", mapOk, mapIdText);
        outcome.Guard("map identity is not empty", !string.IsNullOrWhiteSpace(mapIdentity), mapIdentity);
        string directory = Path.GetFullPath(evidencePath);
        bool fresh = !Directory.Exists(directory) || !Directory.EnumerateFileSystemEntries(directory).Any();
        outcome.Guard("evidence directory is new (absent or empty); a used run directory is never reused", fresh, directory);
        if (!outcome.AllGuardsPassed || baseUrl is null)
        {
            return outcome.Refuse("init refused; nothing was created");
        }

        Directory.CreateDirectory(directory);
        DrillState state = new()
        {
            RunId = DateTimeOffset.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture) + "-" +
                Convert.ToHexString(RandomNumberGenerator.GetBytes(2)).ToLowerInvariant(),
            CreatedAt = DateTimeOffset.Now,
            CreatedOnHost = Environment.MachineName,
            RiotBaseUrl = DrillGuards.Normalize(baseUrl),
            DeviceKey = deviceKey,
            VehicleAlias = string.Equals(deviceKey, DrillGuards.ApprovedDeviceKey, StringComparison.Ordinal)
                ? DrillGuards.ApprovedVehicleAlias
                : "fake-riot-selftest",
            MapId = mapId,
            MapIdentity = mapIdentity,
            FakeRiot = fakeRiot
        };
        DrillEvidence evidence = DrillEvidence.At(directory, "init");
        evidence.SaveState(state);
        outcome.CreatedEvidence = evidence;
        outcome.Data["runId"] = state.RunId;
        outcome.Data["upperId"] = state.UpperId;
        outcome.Data["evidence"] = directory;
        outcome.Data["vehicle"] = state.VehicleAlias;
        outcome.Data["fakeRiot"] = fakeRiot;
        outcome.Lines.Add(Inv($"run {state.RunId} for {state.VehicleAlias} ({deviceKey}) on map {mapId} ({mapIdentity}) via {state.RiotBaseUrl}"));
        outcome.Lines.Add("evidence: " + directory);
        outcome.Lines.Add("next: preflight (on the host that will run the drill), then status");
        return outcome;
    }

    // ---- status -----------------------------------------------------------------------------------

    internal static async Task<CommandOutcome> StatusAsync(DrillContext context, DrillRiot riot)
    {
        CommandOutcome outcome = new("status");
        DrillState state = context.State;
        string key = state.DeviceKey;

        RiotVehicleObservation? card = await riot.ReadCardAsync(key);
        Observe(context, "vehicleCard", card);
        VehicleMotionSample? motion = await riot.SampleAsync(key);
        Observe(context, "motion", motion);
        RiotVehicleEmergencyObservation? emergency = await riot.ReadEmergencyAsync(key);
        Observe(context, "emergency", emergency);
        RiotVehicleSafetyObservation? safety = await riot.ReadSafetyAsync(key);
        Observe(context, "safety", safety);
        Remember(state, motion, emergency);

        outcome.Lines.Add("vehicle  " + DescribeCard(card));
        outcome.Lines.Add("motion   " + Motion.Describe(motion));
        outcome.Lines.Add("latch    emergencyState=" + (emergency?.EmergencyState ?? "unread"));
        outcome.Lines.Add("safety   " + (safety is null
            ? "unread (timeout)"
            : "motionState=" + safety.MotionState + " reasons=" + string.Join(',', safety.ReasonCodes)));
        outcome.Data["vehicleCard"] = card;
        outcome.Data["motion"] = motion;
        outcome.Data["emergencyState"] = emergency?.EmergencyState;
        outcome.Data["safety"] = safety;

        if (context.Args.Has("stations"))
        {
            StationsRead stations = await riot.ReadStationsAsync(state.MapId);
            Observe(context, "stations", stations);
            outcome.Lines.Add(stations.Snapshot is null
                ? "stations unread: " + stations.Error
                : Inv($"stations map {state.MapId}: {stations.Snapshot.Stations.Count} stations, content sha256 {stations.Snapshot.ContentSha256[..12]}"));
            foreach (RiotMapStation station in stations.Snapshot?.Stations ?? [])
            {
                outcome.Lines.Add(Inv($"         {station.StationId,6}  {station.StationName}"));
            }
            outcome.Data["stationCount"] = stations.Snapshot?.Stations.Count;
        }

        if (state.Order is not null)
        {
            RiotOrderObservation? order = await riot.ReconcileAsync(state.Order.UpperId);
            Observe(context, "order", order);
            outcome.Lines.Add("order    " + DescribeOrder(state.Order.UpperId, order));
            outcome.Data["order"] = order;
        }

        outcome.Lines.Add("run      " + DescribeRun(state));
        context.Evidence.SaveState(state);
        return outcome;
    }

    // ---- preflight --------------------------------------------------------------------------------

    internal static async Task<CommandOutcome> PreflightAsync(DrillContext context, DrillRiot riot)
    {
        CommandOutcome outcome = new("preflight");
        DrillState state = context.State;
        string? acceptReason = context.Args.Get("accept-tun");

        RouteProbe route = await NetworkPreflight.ProbeRouteAsync(context.BaseUrl);
        Observe(context, "preflightRoute", route);
        bool direct = route.Verdict is RouteVerdicts.Direct or RouteVerdicts.DirectLoopback;
        bool accepted = !direct && !string.IsNullOrWhiteSpace(acceptReason);
        outcome.Guard(
            "route to RIoT is direct (not via Clash TUN, not unknown)" + (accepted ? " -- overridden by --accept-tun" : string.Empty),
            direct || accepted,
            Inv($"{route.Verdict}: {route.Target} -> next hop {route.NextHop ?? "-"} via '{route.InterfaceName ?? "-"}' ({route.InterfaceDescription ?? "-"}); markers [{string.Join("; ", route.TunMarkers)}]; {route.LookupError ?? "lookup ok"}"));

        LatencyProbe latency = await NetworkPreflight.ProbeLatencyAsync(riot, state.DeviceKey);
        Observe(context, "preflightLatency", latency);
        outcome.Guard(
            Inv($"{NetworkPreflight.LatencyReads}/{NetworkPreflight.LatencyReads} vehicle-card reads succeeded"),
            latency.AllSucceeded,
            latency.Errors.Count == 0 ? "no errors" : string.Join("; ", latency.Errors));
        outcome.Guard(
            Inv($"slowest round trip <= {NetworkPreflight.MaxRoundTripMs:F0} ms"),
            latency.MaxMs is double max && max <= NetworkPreflight.MaxRoundTripMs,
            Inv($"median {Motion.Number(latency.MedianMs)} ms, max {Motion.Number(latency.MaxMs)} ms, samples [{string.Join(", ", latency.RoundTripsMs.Select(ms => ms.ToString("F1", CultureInfo.InvariantCulture)))}]"));

        bool passed = outcome.AllGuardsPassed;
        state.Preflight = new PreflightRecord
        {
            At = DateTimeOffset.Now,
            Host = route.Host,
            Passed = passed,
            RouteVerdict = route.Verdict,
            AcceptedRouteReason = accepted ? acceptReason?.Trim() : null,
            LatencyMedianMs = latency.MedianMs,
            LatencyMaxMs = latency.MaxMs,
            Failures = [.. outcome.Guards.Where(guard => !guard.Passed).Select(guard => guard.Name)]
        };
        context.Evidence.SaveState(state);

        outcome.Data["route"] = route;
        outcome.Data["latency"] = latency;
        outcome.Lines.Add(Inv($"host {route.Host}; RIoT {route.Target} resolved [{string.Join(", ", route.ResolvedAddresses)}]"));
        outcome.Lines.Add(Inv($"route {route.Verdict}; system proxy a default client would use: {route.SystemProxyWouldUse ?? "none"} (this tool: UseProxy=false)"));
        outcome.Lines.Add(Inv($"card reads: median {Motion.Number(latency.MedianMs)} ms, max {Motion.Number(latency.MaxMs)} ms, card status {Motion.Number(latency.CardStatus)}"));
        return passed
            ? outcome.Set("OK", 0, "preflight passed on " + route.Host)
            : outcome.Set("PREFLIGHT_FAILED", 1, "preflight failed; create-move and trigger will refuse until a preflight passes on this host");
    }

    // ---- create-move ------------------------------------------------------------------------------

    internal static async Task<CommandOutcome> CreateMoveAsync(DrillContext context, DrillRiot riot)
    {
        CommandOutcome outcome = new("create-move");
        DrillState state = context.State;
        string? toText = context.Args.Get("to");
        if (toText is null ||
            !int.TryParse(toText, NumberStyles.None, CultureInfo.InvariantCulture, out int destination) ||
            destination <= 0)
        {
            return outcome.Usage("create-move needs --to <riotStationId> (a positive integer)");
        }

        outcome.Guard(
            "no drill order recorded in this run (one order per run)",
            state.Order is null,
            state.Order is null ? "none" : Inv($"{state.Order.UpperId} attempted {state.Order.AttemptedAt:O}, {state.Order.Disposition}"));
        outcome.Guard("no trigger recorded in this run", state.Trigger is null, state.Trigger is null ? "none" : "trigger already attempted");
        PreflightGuard(outcome, state);
        if (!outcome.AllGuardsPassed)
        {
            return outcome.Refuse("create-move refused; nothing was sent");
        }

        string key = state.DeviceKey;
        RiotVehicleObservation? card = await riot.ReadCardAsync(key);
        Observe(context, "vehicleCard", card);
        VehicleMotionSample? motion = await riot.SampleAsync(key);
        Observe(context, "motion", motion);
        RiotVehicleEmergencyObservation? emergency = await riot.ReadEmergencyAsync(key);
        Observe(context, "emergency", emergency);
        RiotVehicleSafetyObservation? safety = await riot.ReadSafetyAsync(key);
        Observe(context, "safety", safety);
        StationsRead stations = await riot.ReadStationsAsync(state.MapId);
        Observe(context, "stations", stations);
        Remember(state, motion, emergency);

        int currentStation = card?.CurrentStationId ?? 0;
        outcome.Guard("vehicle connected", card is { Connected: true }, DescribeCard(card));
        outcome.Guard("vehicle enabled", card is { Enabled: true }, Inv($"enabled={card?.Enabled}"));
        outcome.Guard("procState IDLE", card?.ProcState == "IDLE", card?.ProcState ?? "unread");
        outcome.Guard(
            "on the configured map",
            card is not null && string.Equals(card.CurrentMap, state.MapIdentity, StringComparison.Ordinal),
            Inv($"currentMap={card?.CurrentMap ?? "unread"}, configured {state.MapIdentity}"));
        outcome.Guard("speed 0", card?.Speed is double speed && speed == 0, "speed=" + Motion.Number(card?.Speed));
        outcome.Guard("motion reading NotMoving", motion?.Reading == VehicleMotionReading.NotMoving, Motion.Describe(motion));
        outcome.Guard(
            "no current order on the vehicle card",
            card is not null && string.IsNullOrWhiteSpace(card.OrderTaskId),
            "orderTaskId=" + (card?.OrderTaskId ?? "-"));
        outcome.Guard(
            "no non-final RIoT order for this vehicle",
            safety is not null && !safety.ReasonCodes.Any(BlockingSafetyReasons.Contains),
            safety is null ? "unread (timeout)" : "reasons=" + string.Join(',', safety.ReasonCodes));
        outcome.Guard(
            Inv($"battery >= {DrillGuards.MinimumBatteryPercent}"),
            card?.BatteryPercent is int battery && battery >= DrillGuards.MinimumBatteryPercent,
            "battery=" + Motion.Number(card?.BatteryPercent));
        outcome.Guard("standing at a station (currentStationId > 0)", currentStation > 0, "currentStationId=" + Motion.Number(card?.CurrentStationId));
        outcome.Guard("destination differs from the current station", destination != currentStation, Inv($"to={destination}, current={currentStation}"));
        outcome.Guard(
            "destination exists in the live map 25 station list",
            stations.Snapshot?.Stations.Any(station => station.StationId == destination) == true,
            stations.Snapshot is null
                ? "station list unread: " + stations.Error
                : Inv($"{stations.Snapshot.Stations.Count} stations read"));
        outcome.Guard("emergencyState OK", emergency?.EmergencyState == Ok, "emergencyState=" + (emergency?.EmergencyState ?? "unread"));
        if (!outcome.AllGuardsPassed)
        {
            context.Evidence.SaveState(state);
            return outcome.Refuse("create-move refused; nothing was sent");
        }

        OrderRecord order = new()
        {
            UpperId = state.UpperId,
            StartStationId = currentStation,
            DestinationStationId = destination,
            AttemptedAt = DateTimeOffset.Now
        };
        state.Order = order;
        context.Evidence.SaveState(state);
        context.Evidence.AppendCallIntent("byDefaultMissions", state.UpperId);

        long started = Stopwatch.GetTimestamp();
        RiotOrderObservation created = await riot.Movement.CreateAsync(
            new OrderIntent(
                MovementLegId: state.UpperId,
                DemandId: state.UpperId,
                UpperId: state.UpperId,
                Purpose: "W1_EMERGENCY_DRILL",
                TargetStationId: destination.ToString(CultureInfo.InvariantCulture),
                CreatedAt: order.AttemptedAt,
                VehicleKey: key,
                MapId: state.MapId,
                DestinationStationId: destination),
            CancellationToken.None);
        Observe(context, "createOrderResult", new { created, elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds });
        order.Disposition = created.Kind.ToString();
        order.CreateReceipt = created.Receipt?.Classification;
        context.Evidence.SaveState(state);

        RiotOrderObservation? readBack = await riot.ReconcileAsync(state.UpperId);
        Observe(context, "order", readBack);
        order.OrderId = readBack?.OrderId ?? created.OrderId;
        order.OrderState = readBack?.OrderState ?? created.OrderState;
        order.ObservationKind = readBack?.Kind.ToString();
        context.Evidence.SaveState(state);

        outcome.Data["upperId"] = state.UpperId;
        outcome.Data["create"] = created;
        outcome.Data["readBack"] = readBack;
        outcome.Lines.Add(Inv($"create {state.UpperId}: {created.Kind} ({created.Receipt?.Classification ?? "-"})"));
        outcome.Lines.Add("read-back " + DescribeOrder(state.UpperId, readBack));

        bool confirmed = readBack is { Kind: RiotOrderObservationKind.Active, OrderId: not null } &&
            string.Equals(readBack.VehicleKey, key, StringComparison.Ordinal) &&
            readBack.MapId == state.MapId &&
            readBack.DestinationStationId == destination;
        return confirmed
            ? outcome.Set("OK", 0, Inv($"order {order.OrderId} for {state.VehicleAlias}: station {currentStation} -> {destination}; next: watch-moving"))
            : outcome.NotConfirmed("the create was sent but the read-back does not confirm an active order for this vehicle; do NOT create again -- run status");
    }

    // ---- watch-moving -----------------------------------------------------------------------------

    internal static async Task<CommandOutcome> WatchMovingAsync(DrillContext context, DrillRiot riot)
    {
        CommandOutcome outcome = new("watch-moving");
        DrillState state = context.State;
        if (!TryReadSeconds(context.Args, "timeout", 120, 1, 1800, out int timeoutSeconds))
        {
            return outcome.Usage("--timeout must be a whole number of seconds between 1 and 1800");
        }

        int? destination = state.Order?.DestinationStationId;
        DateTimeOffset deadline = DateTimeOffset.Now.AddSeconds(timeoutSeconds);
        int samples = 0;
        int consecutive = 0;
        bool sawMoving = false;
        string result = "TIMEOUT";
        VehicleMotionSample? last = null;
        while (DateTimeOffset.Now < deadline)
        {
            VehicleMotionSample? sample = await riot.SampleAsync(state.DeviceKey);
            samples++;
            Observe(context, "motion", sample);
            if (sample is null)
            {
                consecutive = 0;
            }
            else
            {
                last = sample;
                // Needs a reported speed above Motion.MinimumMovingSpeed: MT_RUNNING at speed 0 with no
                // station is also what RIoT reports while the vehicle rotates in place at the start
                // station (2026-09-15), and that rotation is not the window.
                if (Motion.IsMovingBetweenStations(sample))
                {
                    sawMoving = true;
                    // Two in a row, so one odd reading cannot open the window on its own.
                    if (++consecutive >= 2)
                    {
                        result = "READY";
                        break;
                    }
                }
                else
                {
                    consecutive = 0;
                    sawMoving |= sample.Reading == VehicleMotionReading.Moving;
                    if (sawMoving && sample.Reading == VehicleMotionReading.NotMoving &&
                        destination is int arrivedAt && sample.CurrentStationId == arrivedAt)
                    {
                        result = "ARRIVED";
                        break;
                    }
                }
            }
            await Task.Delay(PollIntervalMs);
        }

        state.LastWatch = new WatchRecord { At = DateTimeOffset.Now, Outcome = result, Samples = samples };
        if (last is not null)
        {
            state.LatestMotion = Motion.ToRecord(last);
        }
        context.Evidence.SaveState(state);
        outcome.Data["samples"] = samples;
        outcome.Data["result"] = result;
        outcome.Lines.Add("last sample: " + Motion.Describe(last));

        switch (result)
        {
            case "READY":
                outcome.Lines.Add("READY TO TRIGGER");
                if (state.Trigger is not null)
                {
                    outcome.Lines.Add("(a trigger is already recorded in this run; trigger will refuse)");
                }
                return outcome.Set("READY", 0, Inv($"the vehicle is moving between stations (2 consecutive samples with speed > {Motion.MinimumMovingSpeed} and no station)"));
            case "ARRIVED":
                return outcome.Set("WINDOW_MISSED", 1, Inv($"the vehicle reached station {destination} without a trigger window; do not trigger -- report to the user before anything else"));
            default:
                return outcome.Set("WINDOW_MISSED", 1, Inv($"no moving-between-stations window within {timeoutSeconds} s ({samples} samples)"));
        }
    }

    // ---- trigger ----------------------------------------------------------------------------------

    internal static async Task<CommandOutcome> TriggerAsync(DrillContext context, DrillRiot riot)
    {
        CommandOutcome outcome = new("trigger");
        DrillState state = context.State;
        string key = state.DeviceKey;
        if (!TryReadSeconds(context.Args, "observe-seconds", 20, 3, 120, out int observeSeconds))
        {
            return outcome.Usage("--observe-seconds must be a whole number of seconds between 3 and 120");
        }
        // The approved field exception allows triggerEmergency only while the empty vehicle drives between
        // two stations; waiving that is for the FakeRiot self-test and never for a real RIoT or vehicle.
        bool allowStationaryRequested = context.Args.Has("allow-stationary");
        bool selfTestRun = DrillGuards.IsSelfTestRun(key, state.FakeRiot, context.BaseUrl);
        bool allowStationary = allowStationaryRequested && selfTestRun;

        outcome.Guard(
            "no trigger recorded in this run (at most one triggerEmergency per run)",
            state.Trigger is null,
            state.Trigger is null ? "none" : Inv($"already attempted at {state.Trigger.AttemptedAt:O} ({state.Trigger.Disposition}); never sent twice, even without a latch"));
        string? keyRefusal = DrillGuards.RefuseDeviceKey(key, state.FakeRiot, context.BaseUrl);
        outcome.Guard("device key is the approved one", keyRefusal is null, keyRefusal ?? state.VehicleAlias + " " + key);
        if (allowStationaryRequested)
        {
            outcome.Guard(
                "--allow-stationary is self-test only (self-test device key, --fake-riot, literal loopback RIoT address)",
                selfTestRun,
                Inv($"this run: {state.VehicleAlias}, fakeRiot={state.FakeRiot}, RIoT {state.RiotBaseUrl}") +
                    (selfTestRun ? string.Empty : "; on a real run trigger only while the vehicle drives between two stations"));
        }
        PreflightGuard(outcome, state);
        outcome.Guard(
            "drill order recorded",
            state.Order?.OrderId is not null,
            state.Order is null ? "no order" : Inv($"{state.Order.UpperId} orderId={state.Order.OrderId ?? "-"}"));
        if (!outcome.AllGuardsPassed)
        {
            return outcome.Refuse("trigger refused; nothing was sent");
        }

        RiotVehicleEmergencyObservation? before = await riot.ReadEmergencyAsync(key);
        Observe(context, "emergency", before);
        VehicleMotionSample? pre = await riot.SampleAsync(key);
        Observe(context, "motion", pre);
        Remember(state, pre, before);
        outcome.Guard(
            "latch reads OK before the trigger (otherwise this run could not tell its own latch)",
            before?.EmergencyState == Ok,
            "emergencyState=" + (before?.EmergencyState ?? "unread"));
        bool moving = pre is not null && Motion.IsMovingBetweenStations(pre);
        outcome.Guard(
            Inv($"fresh sample: moving between stations (speed > {Motion.MinimumMovingSpeed}, no station)") +
                (allowStationary && !moving ? " -- waived by --allow-stationary" : string.Empty),
            moving || allowStationary,
            Motion.Describe(pre));
        if (!outcome.AllGuardsPassed)
        {
            context.Evidence.SaveState(state);
            return outcome.Refuse("trigger refused; nothing was sent");
        }

        TriggerRecord trigger = new()
        {
            AttemptedAt = DateTimeOffset.Now,
            AllowStationary = allowStationary && !moving,
            PreSample = pre is null ? null : Motion.ToRecord(pre),
            ObserveSeconds = observeSeconds
        };
        // Written and flushed before the call: from here on a crash, a timeout or a refusal by RIoT all
        // still read as "this run's one trigger has been used".
        state.Trigger = trigger;
        context.Evidence.SaveState(state);
        context.Evidence.AppendCallIntent("triggerEmergency", key);

        DateTimeOffset calledAt = DateTimeOffset.Now;
        long started = Stopwatch.GetTimestamp();
        RiotCommandCallResult result = await riot.Commands.IssueEmergencyCommandAsync(
            RiotEmergencyCommandKind.Trigger, key, CancellationToken.None);
        trigger.Receipt = Motion.ToReceipt("triggerEmergency", result, Stopwatch.GetElapsedTime(started));
        trigger.Disposition = result.Disposition.ToString();
        context.Evidence.SaveState(state);
        Observe(context, "triggerEmergencyResult", trigger.Receipt);

        List<StopSample> samples = [];
        StopVerdict? lastStop = null;
        DateTimeOffset deadline = calledAt.AddSeconds(observeSeconds);
        while (DateTimeOffset.Now < deadline)
        {
            RiotVehicleEmergencyObservation? latch = await riot.ReadEmergencyAsync(key);
            Observe(context, "emergency", latch);
            VehicleMotionSample? sample = await riot.SampleAsync(key);
            Observe(context, "motion", sample);
            Remember(state, sample, latch);
            // The latch read just above is the latest one taken at or before this sample. An unread
            // latch is not an engaged one, so MT_RUNNING next to it does not count as still.
            samples.Add(new StopSample(sample, latch is { IsLatched: true }));
            trigger.Samples++;
            if (latch?.EmergencyState is null || sample is null)
            {
                trigger.ReadFailures++;
            }
            if (!trigger.Latched && latch is { IsLatched: true })
            {
                trigger.Latched = true;
                trigger.LatchState = latch.EmergencyState;
                trigger.MsToLatch = Math.Round((latch.ObservedAt - calledAt).TotalMilliseconds, 0);
            }

            StopVerdict stop = Motion.EvaluateStop(samples);
            lastStop = stop;
            if (stop.Stopped && !trigger.Stopped)
            {
                trigger.Stopped = true;
                trigger.MsToStop = stop.Since is DateTimeOffset since ? Math.Round((since - calledAt).TotalMilliseconds, 0) : null;
                trigger.StopStreakProductReadingNotMoving = stop.ProductReadingNotMoving;
                trigger.StillWhileLatchedRunning = stop.StillWhileLatchedRunning;
                Observe(context, "stopVerdict", stop);
            }
            trigger.StopStreak = stop.Streak;
            if (trigger.Latched && trigger.Stopped)
            {
                break;
            }
            await Task.Delay(FastPollIntervalMs);
        }
        if (!trigger.Stopped && lastStop is not null)
        {
            Observe(context, "stopVerdict", lastStop);
        }

        trigger.CompletedAt = DateTimeOffset.Now;
        context.Evidence.SaveState(state);
        outcome.Data["trigger"] = trigger;
        outcome.Lines.Add(Inv($"triggerEmergency sent once: {trigger.Disposition} ({trigger.Receipt.Classification}, {trigger.Receipt.ElapsedMs:F0} ms)"));
        outcome.Lines.Add(Inv($"latched={trigger.Latched} {trigger.LatchState ?? "-"} after {Motion.Number(trigger.MsToLatch)} ms"));
        outcome.Lines.Add(Inv($"stopped={trigger.Stopped} after {Motion.Number(trigger.MsToStop)} ms (trailing still streak {trigger.StopStreak}, product reading NotMoving: {trigger.StopStreakProductReadingNotMoving?.ToString() ?? "-"}, stillWhileLatchedRunning: {trigger.StillWhileLatchedRunning?.ToString() ?? "-"})"));
        outcome.Lines.Add(Inv($"{trigger.Samples} samples, {trigger.ReadFailures} with a failed read; latest latch {state.LatestEmergency?.State ?? "unread"}"));
        if (state.CanNotRecoverObserved)
        {
            outcome.Lines.Add("CAN_NOT_RECOVER observed: cancel-order is still allowed; release will refuse -- hand over to RIoT staff");
        }

        return result.Disposition == RiotCommandCallDisposition.Accepted && trigger.Latched && trigger.Stopped
            ? outcome.Set("OK", 0, "RIoT latched and the vehicle stopped; next: cancel-order (the order is cancelled before the latch is released)")
            : outcome.NotConfirmed("triggerEmergency was sent once and this run will NOT send it again. The latch and/or the stop were not both observed in the window -- watch the vehicle, run status, and hand over to RIoT staff if it does not stop.");
    }

    // ---- cancel-order -----------------------------------------------------------------------------

    /// <summary>
    /// CMD_ORDER_CANCEL for the drill order, while the latch still holds the vehicle.
    /// </summary>
    /// <remarks>
    /// Runs between trigger and release. With the latch engaged the vehicle cannot act on anything the
    /// order still says, and once the order is terminal a later release has nothing to resume. It is
    /// allowed from either latched state: under CAN_NOT_RECOVER the latch is never released by this
    /// tool, but the order still has to go.
    /// </remarks>
    internal static async Task<CommandOutcome> CancelOrderAsync(DrillContext context, DrillRiot riot)
    {
        CommandOutcome outcome = new("cancel-order");
        DrillState state = context.State;
        string key = state.DeviceKey;

        outcome.Guard(
            "no cancel-order recorded in this run (at most one CMD_ORDER_CANCEL per run)",
            state.CancelOrder is null,
            state.CancelOrder is null ? "none" : Inv($"already attempted at {state.CancelOrder.AttemptedAt:O} ({state.CancelOrder.Disposition})"));
        outcome.Guard(
            "a trigger is recorded in this run",
            state.Trigger is not null,
            state.Trigger is null ? "no trigger" : Inv($"trigger {state.Trigger.Disposition} at {state.Trigger.AttemptedAt:O}"));
        outcome.Guard(
            "no release recorded in this run (the order is cancelled before the latch is released)",
            state.Release is null,
            state.Release is null ? "none" : Inv($"release already attempted at {state.Release.AttemptedAt:O}"));
        outcome.Guard(
            "drill order recorded with an orderId",
            state.Order?.OrderId is not null,
            state.Order is null ? "no order" : Inv($"{state.Order.UpperId} orderId={state.Order.OrderId ?? "-"}"));
        if (!outcome.AllGuardsPassed || state.Order?.OrderId is not string orderId)
        {
            return outcome.Refuse(CancelRefused);
        }

        RiotVehicleEmergencyObservation? latch = await riot.ReadEmergencyAsync(key);
        Observe(context, "emergency", latch);
        bool latchEngaged = latch is { IsLatched: true };
        (List<VehicleMotionSample?> samples, StopVerdict stop) = await SampleStillnessAsync(context, riot, key, latchEngaged);
        Remember(state, samples[^1], latch);
        outcome.Data["stop"] = stop;
        outcome.Guard(
            "latch is engaged (CAN_RECOVER or CAN_NOT_RECOVER)",
            latchEngaged,
            "emergencyState=" + (latch?.EmergencyState ?? "unread"));
        outcome.Guard(
            "latest 3 samples over >= 1 s show the vehicle stopped",
            stop.Stopped,
            StillnessDetail(stop, samples));
        if (!outcome.AllGuardsPassed)
        {
            context.Evidence.SaveState(state);
            return outcome.Refuse(CancelRefused);
        }

        CancelOrderRecord record = new()
        {
            AttemptedAt = DateTimeOffset.Now,
            OrderId = orderId,
            LatchAtCancel = latch?.EmergencyState,
            StillWhileLatchedRunning = stop.StillWhileLatchedRunning
        };
        RiotOrderObservation? before = await riot.ReconcileAsync(state.Order.UpperId);
        Observe(context, "order", before);
        if (before is { Kind: RiotOrderObservationKind.Terminal })
        {
            record.AlreadyTerminal = true;
            record.Disposition = "NOT_SENT_ALREADY_TERMINAL";
            record.OrderStateAfter = before.OrderState;
            record.TerminalObserved = true;
            record.CompletedAt = DateTimeOffset.Now;
            state.CancelOrder = record;
            context.Evidence.SaveState(state);
            outcome.Data["cancelOrder"] = record;
            return outcome.Set("OK", 0, Inv($"order {orderId} is already terminal (orderState {Motion.Number(before.OrderState)}); nothing sent. {NextAfterCancel(record)}"));
        }

        state.CancelOrder = record;
        context.Evidence.SaveState(state);
        context.Evidence.AppendCallIntent("CMD_ORDER_CANCEL", orderId);
        long started = Stopwatch.GetTimestamp();
        RiotCommandCallResult result = await riot.Commands.IssueOrderCommandAsync(
            RiotOrderCommandKind.Cancel, orderId, "W1-DRILL cleanup", CancellationToken.None);
        record.Receipt = Motion.ToReceipt("CMD_ORDER_CANCEL", result, Stopwatch.GetElapsedTime(started));
        record.Disposition = result.Disposition.ToString();
        context.Evidence.SaveState(state);
        Observe(context, "cancelOrderResult", record.Receipt);

        // A definitive refusal gets one read-back and no wait; anything else gets the full window.
        DateTimeOffset deadline = result.Disposition == RiotCommandCallDisposition.Failed
            ? DateTimeOffset.Now
            : DateTimeOffset.Now.AddSeconds(CancelTerminalWaitSeconds);
        do
        {
            RiotOrderObservation? after = await riot.ReconcileAsync(state.Order.UpperId);
            Observe(context, "order", after);
            record.OrderStateAfter = after?.OrderState ?? record.OrderStateAfter;
            if (after is { Kind: RiotOrderObservationKind.Terminal })
            {
                record.TerminalObserved = true;
                break;
            }
            if (DateTimeOffset.Now >= deadline)
            {
                break;
            }
            await Task.Delay(PollIntervalMs);
        }
        while (true);
        record.CompletedAt = DateTimeOffset.Now;
        context.Evidence.SaveState(state);

        outcome.Data["cancelOrder"] = record;
        outcome.Lines.Add(Inv($"CMD_ORDER_CANCEL {orderId} sent once with the latch {record.LatchAtCancel}: {record.Disposition} ({record.Receipt.Classification}, {record.Receipt.ElapsedMs:F0} ms)"));
        outcome.Lines.Add(Inv($"terminal observed: {record.TerminalObserved} (orderState {Motion.Number(record.OrderStateAfter)})"));
        bool confirmed = result.Disposition != RiotCommandCallDisposition.Failed && record.TerminalObserved;
        return confirmed
            ? outcome.Set("OK", 0, "drill order is terminal. " + NextAfterCancel(record))
            : outcome.NotConfirmed(Inv(
                $"CMD_ORDER_CANCEL was sent once and will not be sent again; RIoT {(result.Disposition == RiotCommandCallDisposition.Failed ? "refused it" : "did not show the order terminal")} within {CancelTerminalWaitSeconds} s. STOP: report to the user. Do NOT switch to releasing the emergency stop first -- release stays refused until the order is terminal."));
    }

    // ---- release ----------------------------------------------------------------------------------

    internal static async Task<CommandOutcome> ReleaseAsync(DrillContext context, DrillRiot riot)
    {
        CommandOutcome outcome = new("release");
        DrillState state = context.State;
        string key = state.DeviceKey;
        string? confirmation = context.Args.Get("field-confirmed");
        if (confirmation is null)
        {
            return outcome.Usage("release needs --field-confirmed \"<name> " + DrillGuards.FieldConfirmationSuffix + "\"");
        }
        if (!TryReadSeconds(context.Args, "observe-seconds", 30, 3, 120, out int observeSeconds))
        {
            return outcome.Usage("--observe-seconds must be a whole number of seconds between 3 and 120");
        }

        string? confirmedBy = DrillGuards.ParseFieldConfirmation(confirmation);
        outcome.Guard(
            "no release recorded in this run (at most one cancelEmergency per run)",
            state.Release is null,
            state.Release is null ? "none" : Inv($"already attempted at {state.Release.AttemptedAt:O} ({state.Release.Disposition})"));
        outcome.Guard("a trigger is recorded in this run", state.Trigger is not null, state.Trigger is null ? "no trigger" : Inv($"trigger {state.Trigger.Disposition} at {state.Trigger.AttemptedAt:O}"));
        bool orderTerminal = state.CancelOrder is { TerminalObserved: true } or { AlreadyTerminal: true };
        outcome.Guard(
            "cancel-order is recorded and the drill order was observed terminal (the order is cancelled before the latch is released)",
            orderTerminal,
            state.CancelOrder is null
                ? "no cancel-order in this run: run cancel-order first"
                : Inv($"cancel-order {state.CancelOrder.Disposition}, terminal observed {state.CancelOrder.TerminalObserved}"));
        outcome.Guard(
            "field confirmation reads \"<name> " + DrillGuards.FieldConfirmationSuffix + "\"",
            confirmedBy is not null,
            confirmation);
        if (!outcome.AllGuardsPassed)
        {
            return orderTerminal
                ? outcome.Refuse("release refused; nothing was sent")
                : outcome.Refuse("release refused; nothing was sent. Run cancel-order first: the latch is released only after the drill order is terminal");
        }

        RiotVehicleEmergencyObservation? latchBefore = await riot.ReadEmergencyAsync(key);
        Observe(context, "emergency", latchBefore);
        string? latch = latchBefore?.EmergencyState;
        outcome.Guard(
            "latch is CAN_RECOVER",
            latch == CanRecover,
            latch switch
            {
                CanNotRecover => "CAN_NOT_RECOVER: cancelEmergency is forbidden (allowlist 1.5); hand over to RIoT staff",
                Ok => "OK: no latch engaged, nothing to release",
                null => "unread",
                _ => latch
            });

        (List<VehicleMotionSample?> samples, StopVerdict stop) = await SampleStillnessAsync(
            context, riot, key, latchBefore is { IsLatched: true });
        Remember(state, samples[^1], latchBefore);
        outcome.Data["stop"] = stop;
        outcome.Guard(
            "latest 3 samples over >= 1 s show the vehicle stopped",
            stop.Stopped,
            StillnessDetail(stop, samples));

        RiotOrderObservation? order = state.Order is null ? null : await riot.ReconcileAsync(state.Order.UpperId);
        Observe(context, "order", order);
        if (!outcome.AllGuardsPassed)
        {
            context.Evidence.SaveState(state);
            return latch == CanNotRecover
                ? outcome.Refuse("CAN_NOT_RECOVER: never call cancelEmergency. Hand over to RIoT staff; nothing was sent")
                : outcome.Refuse("release refused; nothing was sent");
        }

        ReleaseRecord release = new()
        {
            AttemptedAt = DateTimeOffset.Now,
            FieldConfirmedBy = confirmedBy ?? string.Empty,
            FieldConfirmation = confirmation.Trim(),
            PreLatch = latch,
            OrderStateAtRelease = order?.OrderState,
            StillWhileLatchedRunning = stop.StillWhileLatchedRunning
        };
        state.Release = release;
        context.Evidence.SaveState(state);
        context.Evidence.AppendCallIntent("cancelEmergency", key);

        DateTimeOffset calledAt = DateTimeOffset.Now;
        long started = Stopwatch.GetTimestamp();
        RiotCommandCallResult result = await riot.Commands.IssueEmergencyCommandAsync(
            RiotEmergencyCommandKind.Cancel, key, CancellationToken.None);
        release.Receipt = Motion.ToReceipt("cancelEmergency", result, Stopwatch.GetElapsedTime(started));
        release.Disposition = result.Disposition.ToString();
        context.Evidence.SaveState(state);
        Observe(context, "cancelEmergencyResult", release.Receipt);

        DateTimeOffset deadline = calledAt.AddSeconds(observeSeconds);
        while (DateTimeOffset.Now < deadline)
        {
            RiotVehicleEmergencyObservation? reading = await riot.ReadEmergencyAsync(key);
            Observe(context, "emergency", reading);
            Remember(state, null, reading);
            release.LastLatch = reading?.EmergencyState;
            if (reading?.EmergencyState == Ok)
            {
                release.OkObserved = true;
                release.MsToOk = Math.Round((reading.ObservedAt - calledAt).TotalMilliseconds, 0);
                break;
            }
            await Task.Delay(FastPollIntervalMs);
        }
        release.CompletedAt = DateTimeOffset.Now;
        context.Evidence.SaveState(state);

        outcome.Data["release"] = release;
        outcome.Lines.Add(Inv($"cancelEmergency sent once (confirmed on site by {release.FieldConfirmedBy}): {release.Disposition} ({release.Receipt.Classification}, {release.Receipt.ElapsedMs:F0} ms)"));
        outcome.Lines.Add(Inv($"emergencyState=OK observed: {release.OkObserved} after {Motion.Number(release.MsToOk)} ms; last {release.LastLatch ?? "unread"}"));
        return release.OkObserved
            ? outcome.Set("OK", 0, "latch released (emergencyState=OK); next: summarize")
            : outcome.NotConfirmed("cancelEmergency was sent once and will not be sent again; emergencyState=OK was not observed in the window -- run status and hand over to RIoT staff if it stays latched");
    }

    // ---- shared -----------------------------------------------------------------------------------

    private static string NextAfterCancel(CancelOrderRecord record) => record.LatchAtCancel == CanNotRecover
        ? "The latch is CAN_NOT_RECOVER: do not release -- hand over to RIoT staff, then summarize"
        : "Next: field staff confirm stopped / empty / doors closed, then release";

    /// <summary>
    /// Three fresh samples spanning more than a second, not whatever an earlier command saw.
    /// <paramref name="latchEngaged"/> is the latch read just before sampling: only with it engaged
    /// does speed 0 + MT_RUNNING count as still (2026-09-15 field finding).
    /// </summary>
    private static async Task<(List<VehicleMotionSample?> Samples, StopVerdict Stop)> SampleStillnessAsync(
        DrillContext context,
        DrillRiot riot,
        string key,
        bool latchEngaged)
    {
        List<VehicleMotionSample?> samples = [];
        for (int index = 0; index < 3; index++)
        {
            VehicleMotionSample? sample = await riot.SampleAsync(key);
            Observe(context, "motion", sample);
            samples.Add(sample);
            if (index < 2)
            {
                await Task.Delay(PollIntervalMs + 50);
            }
        }
        StopVerdict stop = Motion.EvaluateStop(samples, latchEngaged);
        Observe(context, "stopVerdict", new { latchEngaged, stop });
        return (samples, stop);
    }

    private static string StillnessDetail(StopVerdict stop, List<VehicleMotionSample?> samples) =>
        stop.Describe() + ": " + string.Join(" | ", samples.Select(Motion.Describe));

    private static void PreflightGuard(CommandOutcome outcome, DrillState state)
    {
        PreflightRecord? preflight = state.Preflight;
        string host = Environment.MachineName;
        bool passed = preflight is { Passed: true } &&
            string.Equals(preflight.Host, host, StringComparison.OrdinalIgnoreCase) &&
            DateTimeOffset.Now - preflight.At <= DrillGuards.PreflightMaxAge;
        outcome.Guard(
            Inv($"preflight passed on this host within {DrillGuards.PreflightMaxAge.TotalMinutes:F0} min"),
            passed,
            preflight is null
                ? "no preflight in this run"
                : Inv($"latest preflight {(preflight.Passed ? "passed" : "FAILED")} on {preflight.Host} at {preflight.At:O} (route {preflight.RouteVerdict}); this host is {host}"));
    }

    private static void Remember(DrillState state, VehicleMotionSample? motion, RiotVehicleEmergencyObservation? emergency)
    {
        if (motion is not null)
        {
            state.LatestMotion = Motion.ToRecord(motion);
        }
        if (emergency is not null)
        {
            state.LatestEmergency = new EmergencyRecord { At = emergency.ObservedAt, State = emergency.EmergencyState };
            state.CanNotRecoverObserved |= emergency.EmergencyState == CanNotRecover;
        }
    }

    private static void Observe(DrillContext context, string kind, object? data) =>
        context.Evidence.AppendTimeline(kind, data ?? new { unread = "READ_TIMEOUT" });

    private static bool TryReadSeconds(ParsedArgs args, string name, int fallback, int minimum, int maximum, out int seconds)
    {
        string? text = args.Get(name);
        if (text is null)
        {
            seconds = fallback;
            return true;
        }
        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out seconds) &&
            seconds >= minimum && seconds <= maximum;
    }

    private static string DescribeCard(RiotVehicleObservation? card) => card is null
        ? "unread (timeout)"
        : Inv($"connected={card.Connected} enabled={card.Enabled} procState={card.ProcState} currentMap={card.CurrentMap} currentStationId={Motion.Number(card.CurrentStationId)} battery={Motion.Number(card.BatteryPercent)} speed={Motion.Number(card.Speed)} lockStatus={Motion.Number(card.LockStatus)} orderTaskId={card.OrderTaskId ?? "-"}");

    private static string DescribeOrder(string upperId, RiotOrderObservation? order) => order is null
        ? upperId + " unread (timeout)"
        : Inv($"{upperId} {order.Kind} orderId={order.OrderId ?? "-"} orderState={Motion.Number(order.OrderState)} vehicle={order.VehicleKey ?? "-"} map={Motion.Number(order.MapId)} destination={Motion.Number(order.DestinationStationId)}");

    private static string DescribeRun(DrillState state) => Inv(
        $"preflight={(state.Preflight is null ? "-" : state.Preflight.Passed ? "passed" : "FAILED")} order={state.Order?.OrderId ?? "-"} trigger={state.Trigger?.Disposition ?? "-"} cancelOrder={state.CancelOrder?.Disposition ?? "-"} release={state.Release?.Disposition ?? "-"} canNotRecoverObserved={state.CanNotRecoverObserved}");

    private static string Inv(FormattableString text) => FormattableString.Invariant(text);
}
