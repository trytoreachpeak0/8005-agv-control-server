using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ControlServer.Domain;
using Corvus.Json;
using Corvus.Json.Validator;

// Validates outbound protocol lines against the JSON Schemas of the protocol release this server is
// bound to, read from the vendored copy the repository already holds under vendor/8005-agv-protocol
// (8005-agv-control-server#85).
//
//   ControlServer.SchemaConformance --lines <ndjson> --report <directory> [--vendor <directory>]
//                                     [--known <json>] [--processes <n>]
//
// Each input line is {"messageType","origin","site","line"}: origin is product, synthetic-peer or
// test, site names the sending method. Writes schema-coverage.json always and schema-violations.json
// when anything failed. Exit 0 = every line conforms or every violation is on file, 1 = a violation
// that is not, 2 = the vendored contract is not the one ProtocolCandidateIdentity names, or the
// input is unusable.
//
// It runs as its own process on purpose. Corvus.Json.Validator 4.6.7 is the version the protocol
// repository's compatibility matrix pins for an "isolated .NET conformance process", and it brings
// System.Text.Json 10.x and Roslyn with it: loaded into the test host, that System.Text.Json would
// replace the 8.0 one the product ships with, and the tests would then measure a serializer
// production never runs.
//
// Compiling the schemas is most of what a run costs: 1-5 s a schema, and Corvus compiles them one after
// another however many threads ask (control-server#130 measured Parallel.ForEach at 44.3 s against 47.6 s
// serial). So the message types are split over --processes copies of this program (default 4), each
// validating its share and handing back what it found; the report is merged from those and is the
// report a serial run writes, timings aside. --processes 1 validates everything in this process.
// A copy is started with --partial <file> instead of --report, and writes only that file.
const int ErrorsPerLine = 5;
const string Validator = "Corvus.Json.Validator 4.6.7";

// A missing or malformed argument is unusable input, and unusable input is exit 2 like every other
// one: an unhandled exception would end the process with the runtime's own code and a stack trace,
// which a caller that reads the exit code -- a test fixture, the G2 script -- cannot tell apart from
// a crash in the schema library. Caught rather than avoided, so --name value pairs stay the only
// spelling and nothing has to be checked twice.
const string Usage = "Usage: ControlServer.SchemaConformance --lines <ndjson> --report <directory> [--vendor <directory>] [--processes <n>]";
const int DefaultProcesses = 4;
Dictionary<string, string> arguments;
string linesPath;
string reportDirectory;
// Set only in a copy started by another run of this program: where it writes what it found.
string? partialPath;
int processes;
try
{
    arguments = ParseArguments(args);
    linesPath = Required(arguments, "lines");
    partialPath = arguments.GetValueOrDefault("partial");
    reportDirectory = partialPath is null ? Required(arguments, "report") : string.Empty;
    processes = arguments.GetValueOrDefault("processes") is { } count
        ? int.TryParse(count, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) && parsed is >= 1 and <= 16
            ? parsed
            : throw new ArgumentException("--processes must be a whole number from 1 to 16.")
        : DefaultProcesses;
}
catch (ArgumentException exception)
{
    return Fail($"{exception.Message} {Usage}");
}
// The build copies vendor/8005-agv-protocol next to the executable; --vendor exists so a caller can
// point the validator at a copy of its own, which is how the hash self-check is tested.
string vendorRoot = arguments.GetValueOrDefault("vendor")
    ?? Path.Combine(AppContext.BaseDirectory, "vendor", "8005-agv-protocol");
// Violations already filed, each naming the issue that owns it. Optional: a run without the table
// judges every line on its own.
KnownViolation[] known = arguments.GetValueOrDefault("known") is { } knownPath
    ? JsonSerializer.Deserialize<KnownViolation[]>(File.ReadAllText(knownPath), JsonSerializerOptions.Web) ?? []
    : [];
if (known.FirstOrDefault(entry => !entry.IsWellFormed) is { } malformed)
{
    return Fail(
        "Every known-violation entry names messageType, pointer, keyword and actual, and an issue URL " +
        "(the protocol's for a contract defect, this repository's for a product one): " + malformed);
}
if (partialPath is null)
{
    Directory.CreateDirectory(reportDirectory);
    // A violations file an earlier run left in the same directory must not outlive the run that wrote it.
    File.Delete(Path.Combine(reportDirectory, "schema-violations.json"));
}

string manifestPath = Path.Combine(vendorRoot, "manifest", "release.json");
string schemaRoot = Path.Combine(vendorRoot, "schemas");
if (!File.Exists(manifestPath) || !Directory.Exists(schemaRoot))
{
    return Fail($"No vendored protocol at {vendorRoot} (expected manifest/release.json and schemas/).");
}

// The vendored copy is only trusted once it reproduces the identity this build puts on every
// envelope: the manifest's bytes hash to ManifestSha256, and schemas/ hashes to SchemaBundleSha256
// again -- computed here by the protocol's own algorithm, so the pair is a check on the copy rather
// than a comparison of one constant with another. Both constants are checked on every run, so the
// gate always validates against the contract this repository is currently bound to.
byte[] manifestBytes = File.ReadAllBytes(manifestPath);
string manifestSha256 = Sha256Hex(manifestBytes);
if (manifestSha256 != ProtocolCandidateIdentity.ManifestSha256)
{
    return Fail(
        $"Vendored manifest hashes to {manifestSha256}, ProtocolCandidateIdentity.ManifestSha256 is " +
        $"{ProtocolCandidateIdentity.ManifestSha256}.");
}
string schemaBundleSha256 = SchemaBundleSha256(schemaRoot);
if (schemaBundleSha256 != ProtocolCandidateIdentity.SchemaBundleSha256)
{
    return Fail(
        $"Vendored schemas/ hashes to {schemaBundleSha256}, ProtocolCandidateIdentity.SchemaBundleSha256 is " +
        $"{ProtocolCandidateIdentity.SchemaBundleSha256}.");
}

if (JsonNode.Parse(manifestBytes)?["messages"]?.AsObject() is not { } messages)
{
    return Fail($"Vendored manifest at {manifestPath} has no messages table.");
}

if (!File.Exists(linesPath))
{
    return Fail($"No lines file at {linesPath}.");
}
List<ObservedLine> observed = [];
try
{
    foreach (string text in File.ReadLines(linesPath))
    {
        if (text.Length > 0)
        {
            observed.Add(JsonSerializer.Deserialize<ObservedLine>(text, JsonSerializerOptions.Web)
                ?? throw new InvalidDataException($"Empty record in {linesPath}."));
        }
    }
}
catch (Exception exception) when (exception is JsonException or InvalidDataException or IOException)
{
    return Fail($"{linesPath} is not the input format (one JSON object per line, naming messageType, origin, site and line): {exception.Message}");
}

// A test calling the envelope serializer itself is exercising the serializer, not sending anything.
ObservedLine[] checkedLines = observed.Where(line => line.Origin != "test").ToArray();

// Every schema is registered under its own $id, and that is what every $ref in the tree names. The
// base URI is therefore whatever the vendored files say it is: this program spells no schema URI,
// and a version moved in vendor/ carries it without an edit here.
PrepopulatedDocumentResolver resolver = new();
Dictionary<string, string> schemaIds = new(StringComparer.Ordinal);
foreach (string file in Directory.EnumerateFiles(schemaRoot, "*.json", SearchOption.AllDirectories))
{
    JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(file));
    string id = document.RootElement.GetProperty("$id").GetString()
        ?? throw new InvalidDataException(file + " has no $id.");
    resolver.AddDocument(id, document);
    schemaIds["schemas/" + RelativeUnixPath(schemaRoot, file)] = id;
}
JsonSchema.Options schemaOptions = new(resolver, false, null, true);

Stopwatch compileTime = new();
Stopwatch validationTime = new();
// Milliseconds reported by the copies of this program that did the work, when it was split.
long splitCompileMilliseconds = 0;
long splitValidationMilliseconds = 0;
JsonSchema Compile(string manifestRelativePath)
{
    compileTime.Start();
    try
    {
        return JsonSchema.FromText(
            File.ReadAllText(Path.Combine(vendorRoot, manifestRelativePath)), schemaIds[manifestRelativePath], schemaOptions);
    }
    finally
    {
        compileTime.Stop();
    }
}

Dictionary<string, Violation> violations = [];
int distinctLines = 0;
IGrouping<string, ObservedLine>[] byMessageType = [.. checkedLines.GroupBy(line => line.MessageType).OrderBy(group => group.Key, StringComparer.Ordinal)];
// A copy never splits again. Nor does a run with fewer than two message types: there is nothing to share.
int compilationProcesses = partialPath is null && processes > 1 && byMessageType.Length > 1
    ? Math.Min(processes, byMessageType.Length)
    : 1;
int validationExit = compilationProcesses > 1 ? await ValidateInProcessesAsync() : ValidateHere(byMessageType);
if (validationExit != 0)
{
    return validationExit;
}

if (partialPath is not null)
{
    File.WriteAllText(partialPath, JsonSerializer.Serialize(new PartialResult(
        distinctLines, compileTime.ElapsedMilliseconds, validationTime.ElapsedMilliseconds, [.. violations.Values]),
        JsonSerializerOptions.Web));
    return 0;
}

int ValidateHere(IEnumerable<IGrouping<string, ObservedLine>> groups)
{
    // Compiled only when there is something to validate: a run that sent nothing pays nothing.
    JsonSchema[] envelopeSchema = checkedLines.Length > 0 ? [Compile("schemas/envelope.schema.json")] : [];
    foreach (IGrouping<string, ObservedLine> byType in groups)
    {
        // One schema per messageType, never the bundle's oneOf: every message schema repeats the envelope
        // fields and adds the payload, so one file is self-contained, and a oneOf failure buries the real
        // cause under every other branch's.
        string? schemaPath = messages[byType.Key]?["schema"]?.GetValue<string>();
        if (schemaPath is not null && !schemaIds.ContainsKey(schemaPath))
        {
            return Fail($"{schemaPath} is named by the manifest but is not vendored.");
        }
        JsonSchema[] messageSchema = schemaPath is null ? [] : [Compile(schemaPath)];
        validationTime.Start();
        foreach (IGrouping<string, ObservedLine> byContent in byType.GroupBy(line => line.Line, StringComparer.Ordinal))
        {
            distinctLines++;
            Error[] errors = schemaPath is null
                ? [new Error("#/messageType", "messageType", $"'{byType.Key}' is not a message of {ProtocolCandidateIdentity.Tag} {ProtocolCandidateIdentity.ReleaseVersion}.", Quote(byType.Key))]
                : Validate(envelopeSchema, messageSchema, byContent.Key);
            if (errors.Length > 0)
            {
                foreach (ObservedLine line in byContent)
                {
                    Record(line, errors);
                }
            }
        }
        validationTime.Stop();
    }
    return 0;
}

// Each copy gets whole message types, dealt round-robin from the busiest down so no copy draws all the
// heavy ones, and a lines file of its own holding only those types' lines in their original order. What
// comes back is merged in message-type order: a violation's key begins with its type and a type is only
// ever in one copy, so within a type the order is the order a serial run records them in, and the sort
// below settles the rest exactly as it would for a serial run.
async Task<int> ValidateInProcessesAsync()
{
    string scratch = Path.Combine(Path.GetTempPath(), "w2g-schema-split-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(scratch);
    try
    {
        List<IGrouping<string, ObservedLine>>[] shares =
            [.. Enumerable.Range(0, compilationProcesses).Select(_ => new List<IGrouping<string, ObservedLine>>())];
        int next = 0;
        foreach (IGrouping<string, ObservedLine> group in byMessageType
            .OrderByDescending(group => group.Count()).ThenBy(group => group.Key, StringComparer.Ordinal))
        {
            shares[next++ % compilationProcesses].Add(group);
        }
        (int ExitCode, string Output)[] results = await Task.WhenAll(shares.Select(RunShareAsync));
        List<Violation> found = [];
        for (int index = 0; index < results.Length; index++)
        {
            if (results[index].ExitCode != 0)
            {
                Console.Error.Write(results[index].Output);
                return results[index].ExitCode;
            }
            PartialResult partial = JsonSerializer.Deserialize<PartialResult>(
                File.ReadAllText(Path.Combine(scratch, $"partial-{index}.json")), JsonSerializerOptions.Web)
                ?? throw new InvalidDataException($"Copy {index} of the validator wrote an empty result.");
            distinctLines += partial.DistinctLines;
            splitCompileMilliseconds += partial.CompileMilliseconds;
            splitValidationMilliseconds += partial.ValidationMilliseconds;
            found.AddRange(partial.Violations);
        }
        foreach (Violation violation in found.OrderBy(violation => violation.MessageType, StringComparer.Ordinal))
        {
            violations[violation.Key] = violation;
        }
        return 0;
    }
    finally
    {
        Directory.Delete(scratch, recursive: true);
    }

    async Task<(int ExitCode, string Output)> RunShareAsync(List<IGrouping<string, ObservedLine>> share, int index)
    {
        HashSet<string> types = new(share.Select(group => group.Key), StringComparer.Ordinal);
        string sharePath = Path.Combine(scratch, $"lines-{index}.ndjson");
        await File.WriteAllLinesAsync(sharePath, checkedLines
            .Where(line => types.Contains(line.MessageType))
            .Select(line => JsonSerializer.Serialize(line, JsonSerializerOptions.Web)));
        ProcessStartInfo start = new(Environment.ProcessPath ?? throw new InvalidOperationException("No process path."))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in (string[])["--lines", sharePath, "--partial", Path.Combine(scratch, $"partial-{index}.json"), "--vendor", vendorRoot])
        {
            start.ArgumentList.Add(argument);
        }
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("Could not start " + start.FileName);
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await output + await error);
    }
}

Violation[] ordered = violations.Values
    .OrderBy(violation => violation.MessageType, StringComparer.Ordinal)
    .ThenBy(violation => violation.Origin, StringComparer.Ordinal)
    .ThenBy(violation => violation.Site, StringComparer.Ordinal)
    .ToArray();
// A violation on file is reported every run and does not fail. Every error of a line has to match
// one, so a defect riding along with a registered one cannot hide behind it, and an entry that
// matched nothing is reported too -- an entry nobody can trigger only silences the next real defect.
HashSet<KnownViolation> usedEntries = [];
foreach (Violation violation in ordered)
{
    KnownViolation?[] matches = violation.Errors
        .Select(error => known.FirstOrDefault(entry => entry.Matches(violation.MessageType, error)))
        .ToArray();
    if (matches.All(match => match is not null))
    {
        violation.OnFile = matches.Distinct().Cast<KnownViolation>().ToArray();
        usedEntries.UnionWith(violation.OnFile);
    }
}
int LinesWhere(Func<Violation, bool> predicate) => ordered.Where(predicate).Sum(violation => violation.Count);
int unknownViolations = ordered.Count(violation => violation.OnFile is null);

// Reported, never judged: which messages a run happens to send is a fact about the tests. A message
// no test sends is one whose sender this gate cannot see, and that gap belongs in the account rather
// than in the verdict.
SortedSet<string> observedTypes = new(checkedLines.Select(line => line.MessageType), StringComparer.Ordinal);
string[] notObserved = [.. messages
    .Where(entry => entry.Value?["sender"]?.GetValue<string>() == "CONTROL_SERVER")
    .Select(entry => entry.Key)
    .Where(type => !observedTypes.Contains(type))
    .Order(StringComparer.Ordinal)];

JsonSerializerOptions reportOptions = new(JsonSerializerDefaults.Web)
{
    WriteIndented = true,
    // The reports are read by people, and a sample line with every quote escaped is not readable.
    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
};
File.WriteAllText(
    Path.Combine(reportDirectory, "schema-coverage.json"),
    JsonSerializer.Serialize(new
    {
        protocol = new
        {
            ProtocolCandidateIdentity.Tag,
            ProtocolCandidateIdentity.ReleaseVersion,
            ProtocolCandidateIdentity.ProfileId,
            ProtocolCandidateIdentity.ProtocolVersion,
            ProtocolCandidateIdentity.ApprovalStatus,
            manifestSha256,
            schemaBundleSha256,
            vendoredSchemaFiles = schemaIds.Count
        },
        validator = Validator,
        linesObserved = observed.Count,
        testAuthoredLinesSkipped = observed.Count - checkedLines.Length,
        linesChecked = checkedLines.Length,
        distinctLinesChecked = distinctLines,
        linesInViolation = LinesWhere(violation => violation.OnFile is null),
        linesInKnownViolation = LinesWhere(violation => violation.OnFile is not null),
        knownViolationsMatched = usedEntries.Count,
        knownViolationEntriesNotMatched = known.Where(entry => !usedEntries.Contains(entry)).ToArray(),
        // Summed over the processes when the work was split, so it is effort rather than wall-clock time.
        schemaCompilationMilliseconds = compileTime.ElapsedMilliseconds + splitCompileMilliseconds,
        validationMilliseconds = validationTime.ElapsedMilliseconds + splitValidationMilliseconds,
        schemaCompilationProcesses = compilationProcesses,
        // The server's own message types this run never sent. Reported, not judged: which messages a
        // test happens to send says nothing about whether the ones it sent are right.
        serverMessageTypesNotObserved = notObserved,
        // Reported, never judged: which messages a run happens to send is a fact about the tests. A
        // message no test sends is one whose sender this gate cannot see.
        byMessageType = new SortedDictionary<string, SortedDictionary<string, int>>(
            checkedLines.GroupBy(line => line.MessageType).ToDictionary(
                group => group.Key,
                group => new SortedDictionary<string, int>(
                    group.GroupBy(line => line.Origin).ToDictionary(byOrigin => byOrigin.Key, byOrigin => byOrigin.Count()),
                    StringComparer.Ordinal)),
            StringComparer.Ordinal)
    }, reportOptions) + "\n");

Console.WriteLine(string.Create(
    CultureInfo.InvariantCulture,
    $"Schema conformance ({ProtocolCandidateIdentity.Tag} {ProtocolCandidateIdentity.ApprovalStatus}): " +
    $"{checkedLines.Length} lines, {distinctLines} distinct, {observedTypes.Count} message types; " +
    $"{ordered.Length} distinct violations, {unknownViolations} of them not on file; " +
    $"{known.Length - usedEntries.Count} known-violation entries matched nothing; " +
    $"schema compilation {compileTime.ElapsedMilliseconds + splitCompileMilliseconds} ms, " +
    $"validation {validationTime.ElapsedMilliseconds + splitValidationMilliseconds} ms, in {compilationProcesses} process(es)."));
// Printed before the early return: an entry that matched nothing on a run with no violations at all
// is exactly the case worth seeing, and it would otherwise only ever appear in the report file.
foreach (KnownViolation entry in known.Where(entry => !usedEntries.Contains(entry)))
{
    Console.WriteLine($"KNOWN VIOLATION NOT MATCHED: {entry.MessageType} {entry.Pointer} [{entry.Keyword}] {entry.Actual} ({entry.Issue})");
}
if (ordered.Length == 0)
{
    return 0;
}

File.WriteAllText(Path.Combine(reportDirectory, "schema-violations.json"), JsonSerializer.Serialize(ordered, reportOptions) + "\n");
foreach (Violation violation in ordered)
{
    string label = violation.OnFile switch
    {
        null => "SCHEMA VIOLATION",
        { } onFile => "KNOWN SCHEMA VIOLATION (" + string.Join(", ", onFile.Select(entry => entry.Issue).Distinct()) + ")"
    };
    Console.WriteLine(string.Create(
        CultureInfo.InvariantCulture,
        $"{label} x{violation.Count}: {violation.MessageType} sent by {violation.Origin} {violation.Site}"));
    foreach (Error error in violation.Errors)
    {
        Console.WriteLine($"  {error.Pointer} [{error.Keyword}] {error.Message} actual: {error.Actual}");
    }
}
return unknownViolations > 0 ? 1 : 0;

void Record(ObservedLine line, Error[] errors)
{
    string key = Violation.KeyOf(line.MessageType, line.Origin, line.Site, errors);
    if (violations.TryGetValue(key, out Violation? existing))
    {
        existing.Count++;
        return;
    }
    violations[key] = new Violation(line.MessageType, line.Origin, line.Site, errors, line.Line) { Count = 1 };
}

// envelope.schema.json describes the envelope alone: its payload is a closed empty object, and the
// protocol's own G1 never applies it to a line that carries one. So it is applied to the line with
// its payload emptied, and the message's own schema -- which repeats every envelope field and adds
// the payload -- to the line as sent. A payload error is therefore reported once, by the message
// schema; an envelope error both schemas see is de-duplicated.
static Error[] Validate(JsonSchema[] envelopeSchema, JsonSchema[] messageSchema, string line)
{
    JsonNode? parsed;
    try
    {
        parsed = JsonNode.Parse(line);
    }
    catch (JsonException exception)
    {
        return [new Error("#", "json", exception.Message, Truncate(line))];
    }
    string envelopeOnly = line;
    if (parsed is System.Text.Json.Nodes.JsonObject root && root.ContainsKey("payload"))
    {
        root["payload"] = new System.Text.Json.Nodes.JsonObject();
        envelopeOnly = root.ToJsonString();
    }
    return [
        .. Check(envelopeSchema, envelopeOnly)
            .Concat(Check(messageSchema, line))
            .DistinctBy(error => (error.Pointer, error.Keyword, error.Actual))
            .Take(ErrorsPerLine)
    ];

    static IEnumerable<Error> Check(JsonSchema[] schemas, string text)
    {
        using JsonDocument document = JsonDocument.Parse(text);
        List<Error> errors = [];
        foreach (JsonSchema schema in schemas)
        {
            if (!schema.Validate(document.RootElement, ValidationLevel.Flag).IsValid)
            {
                errors.AddRange(schema.Validate(document.RootElement, ValidationLevel.Detailed).Results
                    .Where(result => !result.Valid)
                    .Select(result => Describe(result, document.RootElement)));
            }
        }
        return errors;
    }
}

static Error Describe(ValidationResult result, JsonElement root)
{
    string validationLocation = string.Empty;
    string pointer = string.Empty;
    if (result.Location is { } location)
    {
        validationLocation = location.Item1.ToString();
        pointer = location.Item3.ToString().TrimStart('#');
    }
    string keyword = KeywordOf(validationLocation);
    const string MessagePrefix = "Validation ";
    if (keyword == "(unknown)" && result.Message is { } text && text.StartsWith(MessagePrefix, StringComparison.Ordinal) &&
        text.IndexOf(' ', MessagePrefix.Length) is > 0 and var end)
    {
        // Some keywords (const among them) are reported at a location that does not name them; the
        // message always does: "Validation const - the value '1' did not match '3'."
        keyword = text[MessagePrefix.Length..end];
    }
    const string RequiredPrefix = "the required property '";
    if (keyword == "required" && result.Message is { } required &&
        required.IndexOf(RequiredPrefix, StringComparison.Ordinal) is >= 0 and var start &&
        required.IndexOf('\'', start + RequiredPrefix.Length) is > 0 and var close)
    {
        // Point at the missing property itself, not at the object that lacks it: the object's raw
        // text carries ids that differ on every line, which would split one defect into as many
        // groups as there are lines.
        string name = required[(start + RequiredPrefix.Length)..close];
        pointer += "/" + name.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
    }
    string actual = Resolve(root, pointer) is { } value ? Truncate(value.GetRawText()) : "(absent)";
    string message = string.IsNullOrEmpty(result.Message) ? "violates '" + keyword + "'" : result.Message;
    return new Error("#" + pointer, keyword, message, actual);
}

// The validation location is a path through the schema; the violated keyword is the last segment that
// is one. Anything after it names the offending property (additionalProperties/extra) or index.
static string KeywordOf(string validationLocation)
{
    string[] keywords =
    [
        "additionalProperties", "allOf", "anyOf", "const", "contains", "dependentRequired", "enum",
        "exclusiveMaximum", "exclusiveMinimum", "format", "items", "maxItems", "maxLength", "maxProperties",
        "maximum", "minItems", "minLength", "minProperties", "minimum", "multipleOf", "not", "oneOf", "pattern",
        "prefixItems", "propertyNames", "required", "type", "unevaluatedProperties", "uniqueItems"
    ];
    string[] segments = validationLocation.Split('/');
    for (int index = segments.Length - 1; index >= 0; index--)
    {
        if (keywords.Contains(segments[index], StringComparer.Ordinal))
        {
            return segments[index];
        }
    }
    return "(unknown)";
}

static JsonElement? Resolve(JsonElement root, string pointer)
{
    JsonElement current = root;
    foreach (string raw in pointer.Split('/', StringSplitOptions.RemoveEmptyEntries))
    {
        string segment = raw.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
        if (current.ValueKind == JsonValueKind.Object && current.TryGetProperty(segment, out JsonElement property))
        {
            current = property;
        }
        else if (current.ValueKind == JsonValueKind.Array &&
            int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out int index) &&
            index < current.GetArrayLength())
        {
            current = current[index];
        }
        else
        {
            return null;
        }
    }
    return current;
}

// The protocol's own schema bundle digest, reproduced from schemas/: sorted relative paths, each
// written as "<path>:<sha256>\n", the whole hashed. Implemented in 8005-agv-protocol
// tools/finalize-manifest.mjs as combine("schemas/"), and the value it produces is the manifest's
// schemaBundleSha256, which ProtocolCandidateIdentity carries.
static string SchemaBundleSha256(string schemaRoot)
{
    StringBuilder lines = new();
    foreach (string file in Directory.EnumerateFiles(schemaRoot, "*", SearchOption.AllDirectories)
        .OrderBy(file => RelativeUnixPath(schemaRoot, file), StringComparer.Ordinal))
    {
        lines.Append("schemas/").Append(RelativeUnixPath(schemaRoot, file)).Append(':')
            .Append(Sha256Hex(File.ReadAllBytes(file))).Append('\n');
    }
    return Sha256Hex(Encoding.UTF8.GetBytes(lines.ToString()));
}

static string RelativeUnixPath(string root, string file) => Path.GetRelativePath(root, file).Replace('\\', '/');

static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

static string Quote(string value) => JsonSerializer.Serialize(value);

static string Truncate(string value) => value.Length <= 200 ? value : value[..200] + "...";

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 2;
}

static string Required(Dictionary<string, string> arguments, string name) =>
    arguments.GetValueOrDefault(name) ?? throw new ArgumentException($"--{name} is required.");

static Dictionary<string, string> ParseArguments(string[] values)
{
    Dictionary<string, string> result = new(StringComparer.OrdinalIgnoreCase);
    for (int index = 0; index < values.Length; index += 2)
    {
        if (index + 1 >= values.Length || !values[index].StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException("Arguments must use --name value pairs.");
        }
        result[values[index][2..]] = values[index + 1];
    }
    return result;
}

internal sealed record ObservedLine(string MessageType, string Origin, string Site, string Line);

internal sealed record Error(string Pointer, string Keyword, string Message, string Actual);

/// <summary>What one copy of this program found in its share of the message types.</summary>
internal sealed record PartialResult(int DistinctLines, long CompileMilliseconds, long ValidationMilliseconds, Violation[] Violations);

internal sealed record Violation(string MessageType, string Origin, string Site, Error[] Errors, string SampleLine)
{
    public int Count { get; set; }

    /// <summary>What makes lines one violation: the same type, sender and errors.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string Key => KeyOf(MessageType, Origin, Site, Errors);

    public static string KeyOf(string messageType, string origin, string site, Error[] errors) =>
        messageType + "\n" + origin + "\n" + site + "\n" +
        string.Join("\n", errors.Select(error => error.Pointer + " " + error.Keyword + " " + error.Actual));

    /// <summary>The entries that cover every error of this line, or null while one is uncovered.</summary>
    public KnownViolation[]? OnFile { get; set; }
}

/// <summary>
/// A violation on file: printed and reported every run, not failed on, until the defect is fixed and
/// the entry deleted. The issue is the whole point of the entry -- "we know" without an owner is how
/// a real defect gets quiet. Array indices in <see cref="Pointer"/> are written <c>*</c> so one entry
/// covers every occurrence.
/// </summary>
internal sealed record KnownViolation(
    string MessageType, string Pointer, string Keyword, string Actual, string Issue)
{
    public bool IsWellFormed =>
        !string.IsNullOrWhiteSpace(MessageType) && !string.IsNullOrWhiteSpace(Pointer) &&
        !string.IsNullOrWhiteSpace(Keyword) && Actual is not null &&
        Issue is { } issue && issue.StartsWith("https://github.com/trytoreachpeak0/", StringComparison.Ordinal) &&
        issue.Contains("/issues/", StringComparison.Ordinal);

    public bool Matches(string messageType, Error error) =>
        messageType == MessageType &&
        error.Keyword == Keyword &&
        error.Actual == Actual &&
        string.Join('/', error.Pointer.Split('/').Select(segment => segment.Length > 0 && segment.All(char.IsAsciiDigit) ? "*" : segment)) == Pointer;
}
