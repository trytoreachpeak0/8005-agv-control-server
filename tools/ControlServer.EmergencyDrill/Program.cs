using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ControlServer.EmergencyDrill;

/// <summary>
/// The reduced W1 empty-load emergency-stop drill (batch 2 ticket 19), for agv02 only.
/// </summary>
/// <remarks>
/// <para>
/// **Every step is its own invocation, on purpose.** Creating the move order, sending the emergency
/// stop, cancelling the order and releasing the stop are each authorised in chat before they are run;
/// a tool that chained them would turn four authorisations into one. Nothing here calls the next step.
/// The order is cancelled <em>before</em> the latch is released (user ruling, 2026-09-14): released
/// first, RIoT could drive on to the destination while staff stand beside the vehicle.
/// </para>
/// <para>
/// Each command prints human-readable lines and then one JSON object as its last stdout line. Exit
/// codes: 0 done, 1 refused / window missed / preflight failed, 2 usage error, 3 a call went out but
/// its read-back did not confirm the result.
/// </para>
/// </remarks>
internal static class Program
{
    private static readonly HashSet<string> BooleanFlags = new(StringComparer.Ordinal)
    {
        "stations",
        "allow-stationary",
        "fake-riot"
    };

    /// <summary>Unknown options are a usage error: a mistyped --allow-stationary must not silently mean "not given".</summary>
    private static readonly Dictionary<string, string[]> AllowedOptions = new(StringComparer.Ordinal)
    {
        ["init"] = ["evidence", "device-key", "map-id", "map-identity", "riot-base-url", "fake-riot"],
        ["status"] = ["evidence", "riot-base-url", "stations"],
        ["preflight"] = ["evidence", "riot-base-url", "accept-tun"],
        ["create-move"] = ["evidence", "riot-base-url", "to"],
        ["watch-moving"] = ["evidence", "riot-base-url", "timeout"],
        ["trigger"] = ["evidence", "riot-base-url", "allow-stationary", "observe-seconds"],
        ["release"] = ["evidence", "riot-base-url", "field-confirmed", "observe-seconds"],
        ["cancel-order"] = ["evidence", "riot-base-url"],
        ["summarize"] = ["evidence"]
    };

    internal static async Task<int> Main(string[] args)
    {
        // Station names and the map identity are Chinese; without this the console writes them in the
        // ANSI code page and the evidence holds a mangled map name that reads as a real mismatch.
        Console.OutputEncoding = new UTF8Encoding(false);

        if (args.Length == 0)
        {
            return Usage("no command given");
        }
        string command = args[0];
        if (!AllowedOptions.TryGetValue(command, out string[]? allowed))
        {
            return Usage($"unknown command '{command}'");
        }
        ParsedArgs? parsed = ParsedArgs.Parse(args, BooleanFlags, allowed, out string? problem);
        if (parsed is null)
        {
            return Usage(problem ?? "malformed arguments");
        }

        string? apiKey = Environment.GetEnvironmentVariable(DrillGuards.ApiKeyEnvironmentVariable);
        DrillEvidence.Secret = string.IsNullOrEmpty(apiKey) ? null : apiKey;

        if (command == "init")
        {
            CommandOutcome initialised = DrillCommands.Init(parsed);
            using DrillEvidence? created = initialised.CreatedEvidence;
            return Emit(initialised, parsed, created);
        }

        string? evidencePath = parsed.Get("evidence");
        if (evidencePath is null)
        {
            return Usage($"{command} needs --evidence <run directory created by init>");
        }
        using DrillEvidence evidence = DrillEvidence.At(evidencePath, command);
        if (!evidence.HasState)
        {
            return Emit(new CommandOutcome(command).Refuse($"no {DrillEvidence.StateFileName} in {evidence.Directory}; run init first"), parsed, null);
        }
        if (!evidence.TryLock())
        {
            return Emit(new CommandOutcome(command).Refuse("another drill command is running against this evidence directory"), parsed, null);
        }

        DrillState? state = evidence.LoadState();
        Uri? baseUrl = DrillGuards.ParseBaseUrl(state?.RiotBaseUrl);
        if (state is null || baseUrl is null)
        {
            return Emit(new CommandOutcome(command).Refuse($"{DrillEvidence.StateFileName} is unreadable"), parsed, evidence);
        }

        CommandOutcome common = new(command);
        string? givenUrl = parsed.Get("riot-base-url");
        Uri? given = givenUrl is null ? null : DrillGuards.ParseBaseUrl(givenUrl);
        common.Guard(
            "--riot-base-url matches the address this run was initialised with",
            givenUrl is null || (given is not null && string.Equals(DrillGuards.Normalize(given), state.RiotBaseUrl, StringComparison.OrdinalIgnoreCase)),
            $"given {givenUrl ?? "(not given)"}, run {state.RiotBaseUrl}");
        string? keyRefusal = DrillGuards.RefuseDeviceKey(state.DeviceKey, state.FakeRiot, baseUrl);
        common.Guard("run's device key is covered by the 2026-09-14 exception", keyRefusal is null, keyRefusal ?? state.VehicleAlias);
        common.Guard("run's map id is 25", state.MapId == DrillGuards.ApprovedMapId, state.MapId.ToString(CultureInfo.InvariantCulture));
        if (!common.AllGuardsPassed)
        {
            return Emit(common.Refuse($"{command} refused; nothing was sent"), parsed, evidence);
        }

        DrillContext context = new()
        {
            Command = command,
            Evidence = evidence,
            State = state,
            Args = parsed,
            BaseUrl = baseUrl
        };
        if (command == "summarize")
        {
            return Emit(DrillSummary.Write(context), parsed, evidence);
        }

        if (string.IsNullOrEmpty(apiKey))
        {
            return Emit(
                new CommandOutcome(command).Refuse($"environment variable {DrillGuards.ApiKeyEnvironmentVariable} is not set"),
                parsed,
                evidence);
        }

        using DrillRiot riot = DrillRiot.Create(baseUrl, apiKey, evidence);
        CommandOutcome outcome = command switch
        {
            "status" => await DrillCommands.StatusAsync(context, riot),
            "preflight" => await DrillCommands.PreflightAsync(context, riot),
            "create-move" => await DrillCommands.CreateMoveAsync(context, riot),
            "watch-moving" => await DrillCommands.WatchMovingAsync(context, riot),
            "trigger" => await DrillCommands.TriggerAsync(context, riot),
            "release" => await DrillCommands.ReleaseAsync(context, riot),
            "cancel-order" => await DrillCommands.CancelOrderAsync(context, riot),
            _ => new CommandOutcome(command).Usage($"unknown command '{command}'")
        };
        return Emit(outcome, parsed, evidence);
    }

    private static int Emit(CommandOutcome outcome, ParsedArgs args, DrillEvidence? evidence)
    {
        foreach (string line in outcome.Lines)
        {
            Console.Out.WriteLine(DrillEvidence.Redact(line));
        }
        foreach (GuardCheck guard in outcome.Guards)
        {
            Console.Out.WriteLine(DrillEvidence.Redact($"  [{(guard.Passed ? "ok" : "NO")}] {guard.Name} -- {guard.Detail}"));
        }
        if (outcome.Message.Length > 0)
        {
            Console.Out.WriteLine(DrillEvidence.Redact($"{outcome.Outcome}: {outcome.Message}"));
        }

        var payload = new
        {
            at = DateTimeOffset.Now,
            host = Environment.MachineName,
            command = outcome.Command,
            outcome = outcome.Outcome,
            exitCode = outcome.ExitCode,
            message = outcome.Message,
            options = args.Options,
            flags = args.Flags.Order(StringComparer.Ordinal).ToArray(),
            guards = outcome.Guards,
            data = outcome.Data
        };
        evidence?.AppendCommand(payload);
        Console.Out.WriteLine(DrillEvidence.Redact(JsonSerializer.Serialize(payload, DrillEvidence.Json)));
        return outcome.ExitCode;
    }

    private static int Usage(string problem)
    {
        Console.Error.WriteLine($"ControlServer.EmergencyDrill: {problem}.");
        Console.Error.WriteLine("usage: ControlServer.EmergencyDrill <command> --evidence <run dir> [options]");
        Console.Error.WriteLine("  init          --evidence <new dir> --device-key <agv02 key> --map-id 25 --riot-base-url <url> [--map-identity <name>] [--fake-riot]");
        Console.Error.WriteLine("  preflight     [--accept-tun \"<reason>\"]                      read-only: route + 5 card reads");
        Console.Error.WriteLine("  status        [--stations]                                   read-only");
        Console.Error.WriteLine("  create-move   --to <riotStationId>                           MOVES THE VEHICLE");
        Console.Error.WriteLine("  watch-moving  [--timeout <s>]                                read-only");
        Console.Error.WriteLine("  trigger       [--observe-seconds <s>] [--allow-stationary]   SENDS triggerEmergency (once per run); --allow-stationary is self-test only (--fake-riot run)");
        Console.Error.WriteLine("  cancel-order                                                 SENDS CMD_ORDER_CANCEL (once per run; after trigger, before release)");
        Console.Error.WriteLine("  release       --field-confirmed \"<name> stopped,empty,doors-closed\" [--observe-seconds <s>]   SENDS cancelEmergency (once per run; only after the order is terminal)");
        Console.Error.WriteLine("  summarize                                                    writes SUMMARY.md");
        Console.Error.WriteLine($"  every RIoT command reads the API key from {DrillGuards.ApiKeyEnvironmentVariable}; --riot-base-url, if given, must match init");
        return 2;
    }
}

internal sealed class ParsedArgs
{
    private ParsedArgs(string command) => Command = command;

    public string Command { get; }

    public Dictionary<string, string> Options { get; } = new(StringComparer.Ordinal);

    public HashSet<string> Flags { get; } = new(StringComparer.Ordinal);

    public string? Get(string name) => Options.TryGetValue(name, out string? value) ? value : null;

    public bool Has(string flag) => Flags.Contains(flag);

    internal static ParsedArgs? Parse(
        string[] args,
        HashSet<string> booleanFlags,
        string[] allowed,
        out string? problem)
    {
        ParsedArgs parsed = new(args[0]);
        for (int index = 1; index < args.Length; index++)
        {
            string argument = args[index];
            if (!argument.StartsWith("--", StringComparison.Ordinal) || argument.Length == 2)
            {
                problem = $"expected an --option near '{argument}'";
                return null;
            }
            string name = argument[2..];
            if (!allowed.Contains(name, StringComparer.Ordinal))
            {
                problem = $"'{parsed.Command}' does not take --{name}";
                return null;
            }
            if (parsed.Options.ContainsKey(name) || parsed.Flags.Contains(name))
            {
                problem = $"--{name} given twice";
                return null;
            }
            if (booleanFlags.Contains(name))
            {
                parsed.Flags.Add(name);
                continue;
            }
            if (index + 1 >= args.Length)
            {
                problem = $"--{name} needs a value";
                return null;
            }
            parsed.Options[name] = args[++index];
        }
        problem = null;
        return parsed;
    }
}

internal sealed record GuardCheck(string Name, bool Passed, string Detail);

internal sealed class CommandOutcome
{
    internal CommandOutcome(string command) => Command = command;

    public string Command { get; }

    public string Outcome { get; private set; } = "OK";

    public int ExitCode { get; private set; }

    public string Message { get; private set; } = string.Empty;

    public List<GuardCheck> Guards { get; } = [];

    public List<string> Lines { get; } = [];

    public Dictionary<string, object?> Data { get; } = new(StringComparer.Ordinal);

    public DrillEvidence? CreatedEvidence { get; set; }

    public bool AllGuardsPassed => Guards.TrueForAll(guard => guard.Passed);

    public bool Guard(string name, bool passed, string detail)
    {
        Guards.Add(new GuardCheck(name, passed, detail));
        return passed;
    }

    public CommandOutcome Refuse(string message) => Set("REFUSED", 1, message);

    public CommandOutcome Usage(string message) => Set("USAGE", 2, message);

    public CommandOutcome NotConfirmed(string message) => Set("NOT_CONFIRMED", 3, message);

    public CommandOutcome Set(string outcome, int exitCode, string message)
    {
        Outcome = outcome;
        ExitCode = exitCode;
        Message = message;
        return this;
    }
}

internal sealed class DrillContext
{
    public required string Command { get; init; }

    public required DrillEvidence Evidence { get; init; }

    public required DrillState State { get; init; }

    public required ParsedArgs Args { get; init; }

    public required Uri BaseUrl { get; init; }
}
