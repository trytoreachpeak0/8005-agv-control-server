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
//
// Each input line is {"messageType","origin","site","line"}: origin is product, synthetic-peer or
// test, site names the sending method. Writes schema-coverage.json always and schema-violations.json
// when anything failed. Exit 0 = every line conforms, 1 = violations, 2 = the vendored contract is
// not the one ProtocolCandidateIdentity names, or the input is unusable.
//
// It runs as its own process on purpose. Corvus.Json.Validator 4.6.7 is the version the protocol
// repository's compatibility matrix pins for an "isolated .NET conformance process", and it brings
// System.Text.Json 10.x and Roslyn with it: loaded into the test host, that System.Text.Json would
// replace the 8.0 one the product ships with, and the tests would then measure a serializer
// production never runs.
const int ErrorsPerLine = 5;
const string Validator = "Corvus.Json.Validator 4.6.7";

Dictionary<string, string> arguments = ParseArguments(args);
string linesPath = Required(arguments, "lines");
string reportDirectory = Required(arguments, "report");
// The build copies vendor/8005-agv-protocol next to the executable; --vendor exists so a caller can
// point the validator at a copy of its own, which is how the hash self-check is tested.
string vendorRoot = arguments.GetValueOrDefault("vendor")
    ?? Path.Combine(AppContext.BaseDirectory, "vendor", "8005-agv-protocol");
Directory.CreateDirectory(reportDirectory);
// A violations file an earlier run left in the same directory must not outlive the run that wrote it.
File.Delete(Path.Combine(reportDirectory, "schema-violations.json"));

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

// Compiled only when there is something to validate: a run that sent nothing pays nothing.
JsonSchema[] envelopeSchema = checkedLines.Length > 0 ? [Compile("schemas/envelope.schema.json")] : [];
Dictionary<string, Violation> violations = [];
int distinctLines = 0;
foreach (IGrouping<string, ObservedLine> byType in checkedLines.GroupBy(line => line.MessageType).OrderBy(group => group.Key, StringComparer.Ordinal))
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

Violation[] ordered = violations.Values
    .OrderBy(violation => violation.MessageType, StringComparer.Ordinal)
    .ThenBy(violation => violation.Origin, StringComparer.Ordinal)
    .ThenBy(violation => violation.Site, StringComparer.Ordinal)
    .ToArray();

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
        linesInViolation = ordered.Sum(violation => violation.Count),
        schemaCompilationMilliseconds = compileTime.ElapsedMilliseconds,
        validationMilliseconds = validationTime.ElapsedMilliseconds,
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
    $"{checkedLines.Length} lines, {distinctLines} distinct, {checkedLines.Select(line => line.MessageType).Distinct().Count()} message types; " +
    $"{ordered.Length} distinct violations, {ordered.Sum(violation => violation.Count)} lines; " +
    $"schema compilation {compileTime.ElapsedMilliseconds} ms, validation {validationTime.ElapsedMilliseconds} ms."));
if (ordered.Length == 0)
{
    return 0;
}

File.WriteAllText(Path.Combine(reportDirectory, "schema-violations.json"), JsonSerializer.Serialize(ordered, reportOptions) + "\n");
foreach (Violation violation in ordered)
{
    Console.WriteLine(string.Create(
        CultureInfo.InvariantCulture,
        $"SCHEMA VIOLATION x{violation.Count}: {violation.MessageType} sent by {violation.Origin} {violation.Site}"));
    foreach (Error error in violation.Errors)
    {
        Console.WriteLine($"  {error.Pointer} [{error.Keyword}] {error.Message} actual: {error.Actual}");
    }
}
return 1;

void Record(ObservedLine line, Error[] errors)
{
    string key = line.MessageType + "\n" + line.Origin + "\n" + line.Site + "\n" +
        string.Join("\n", errors.Select(error => error.Pointer + " " + error.Keyword + " " + error.Actual));
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

internal sealed record Violation(string MessageType, string Origin, string Site, Error[] Errors, string SampleLine)
{
    public int Count { get; set; }
}
