using System.Text.Json;
using System.Text.Json.Nodes;
using ControlServer.SchemaConformance;
using Corvus.Json;
using JsonArray = System.Text.Json.Nodes.JsonArray;
using JsonObject = System.Text.Json.Nodes.JsonObject;

// Drives CompiledSchemaCache over a schema tree the caller names, for SchemaCompilationCacheTests (control-server#584).
// Test-only: the validator refuses any tree but the contract it is bound to, and this program is how the cache is shown
// to tell a changed tree from the one it compiled before.
//
//   ControlServer.SchemaCacheProbe --schemas <schemas dir> --schema <path under it> --line <file> --cache <dir>
//                                  --tool-version <text> --corvus-version <text> [--compare true]
//
// Validates the one JSON value in --line against the one schema and prints a JSON object: the entry's key, the cache's
// counts, the verdict and every failed result. The two version parts are taken from the command line so a test can move
// each one alone; the schema bundle digest never is, it is measured from --schemas exactly as the validator measures it.
// With --compare true the same line is validated by Corvus.Json.Validator.JsonSchema.FromText as well, with the options
// the validator used before the cache, and that verdict is printed next to this one.
Dictionary<string, string> arguments = new(StringComparer.OrdinalIgnoreCase);
for (int index = 0; index + 1 < args.Length; index += 2)
{
    arguments[args[index].TrimStart('-')] = args[index + 1];
}
string schemaRoot = arguments["schemas"];
string schemaPath = arguments["schema"];

PrepopulatedDocumentResolver resolver = new();
string? schemaId = null;
foreach (string file in Directory.EnumerateFiles(schemaRoot, "*.json", SearchOption.AllDirectories))
{
    JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(file));
    string id = document.RootElement.GetProperty("$id").GetString()!;
    resolver.AddDocument(id, document);
    if (CompiledSchemaCache.RelativeUnixPath(schemaRoot, file) == schemaPath)
    {
        schemaId = id;
    }
}
if (schemaId is null)
{
    Console.Error.WriteLine($"{schemaPath} is not under {schemaRoot}.");
    return 2;
}

CompiledSchemaCache cache = new(
    arguments["cache"],
    new SchemaCacheKeyParts(
        CompiledSchemaCache.SchemaBundleSha256(schemaRoot), arguments["tool-version"], arguments["corvus-version"]),
    resolver);
using JsonDocument line = JsonDocument.Parse(File.ReadAllText(arguments["line"]));
ValidationContext verdict = cache.Get(schemaId).Validate(line.RootElement, ValidationLevel.Detailed);

JsonObject output = new()
{
    ["key"] = cache.KeyOf(schemaId),
    ["schemaBundleSha256"] = cache.KeyParts.SchemaBundleSha256,
    ["hits"] = cache.Hits,
    ["misses"] = cache.Misses,
    ["rejected"] = cache.Rejected,
    ["valid"] = verdict.IsValid,
    ["errors"] = Failed(verdict)
};
if (arguments.GetValueOrDefault("compare") == "true")
{
    ValidationContext corvus = Corvus.Json.Validator.JsonSchema
        .FromText(File.ReadAllText(Path.Combine(schemaRoot, schemaPath)), schemaId, new(resolver, false, null, true))
        .Validate(line.RootElement, ValidationLevel.Detailed);
    output["corvusValid"] = corvus.IsValid;
    output["corvusErrors"] = Failed(corvus);
}
Console.WriteLine(output.ToJsonString());
return 0;

static JsonArray Failed(ValidationContext context) =>
    [.. context.Results.Where(result => !result.Valid).Select(result => (JsonNode)JsonValue.Create(
        (result.Location is { } location ? location.Item3.ToString() + " " : string.Empty) + result.Message)!)];
