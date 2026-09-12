using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Tests;

/// <summary>
/// The machine guard on the shape of what this server actually puts on the wire: for every snapshot
/// the journey publisher sends, the payload's property names are exactly the ones the frozen schema
/// requires, and every field the schema constrains to an enumeration carries one of its values.
/// </summary>
/// <remarks>
/// <para>
/// Nothing checked this either, and the v2 switch is how it showed. Moving the identity to
/// <c>protocolVersion 2</c> / <c>AGV_FULL_PRODUCT</c> left the suite at 583 passed while the server
/// emitted three snapshots its own frozen schemas reject in seven separate ways:
/// <c>UpcomingStopPlanSnapshot</c> carried a top-level <c>demandId</c> the payload forbids, its legs
/// were missing <c>stopPurposeCategory</c>, <c>demandId</c> and <c>publicStationFunction</c>, and
/// its <c>legType</c> said <c>TO_GATE</c> where v2 permits only <c>TO_PICKUP</c> or
/// <c>TO_DROPOFF</c>; <c>CurrentStopWorklistSnapshot</c> sent <c>stopRole: "GATE"</c> against an
/// enumeration of <c>PICKUP</c> and <c>DROPOFF</c>; <c>VehicleBusinessStateSnapshot</c> omitted the
/// required <c>activePurpose</c>. Section 6.3 of the scope specification already names why none of
/// it was caught -- neither end validates against the schemas at runtime -- and the same silence had
/// let seven out-of-registry reason codes through eight green gates.
/// </para>
/// <para>
/// <b>What this checks, and what it does not.</b> It reads the frozen schema and compares the
/// emitted object's property-name set against <c>required</c>, and checks enum membership. It is
/// <b>not</b> a JSON Schema validator: string patterns, numeric bounds, formats, <c>$ref</c> chains
/// beyond one hop into <c>common/types.schema.json</c>, and cross-field rules are all unchecked.
/// Those two properties are chosen because they are the two that broke, and because both schemas'
/// payload objects are <c>additionalProperties: false</c> with every property required -- which
/// makes name-set equality exactly structural conformance for them, rather than an approximation of
/// it. Building a real validator is a dependency decision this repository has not taken; section
/// 6.6 item 1 puts schema authorship on the protocol side.
/// </para>
/// <para>
/// <b>The field lists are not copied here.</b> <c>vendor/8005-agv-protocol/schemas/</c> is the
/// protocol's schema tree file for file, and <see cref="ApprovedSchemaTreeSha256"/> pins all 69 of
/// them with one digest. That digest is <b>ours, not the protocol's</b>: the manifest's
/// <c>schemaBundleSha256</c> is computed by the protocol's own bundling, which this repository does
/// not reproduce, so pinning against it would mean reimplementing an algorithm rather than checking
/// a copy. Refreshing the copy is <c>vendor/8005-agv-protocol/README.md</c>.
/// </para>
/// <para>
/// <b>No <c>IntegrationSlice</c> trait, and no cluster</b>, for the reason
/// <see cref="ProtocolVectorTestBindingArchitectureTests"/> gives.
/// </para>
/// </remarks>
public sealed class ProtocolPayloadShapeArchitectureTests
{
    /// <summary>
    /// SHA-256 over the vendored schema tree: for each file in relative-path order, the path, a
    /// newline, the file's own SHA-256 in lower hex, and a newline.
    /// </summary>
    private const string ApprovedSchemaTreeSha256 =
        "fcdf6f71849cf6734073f656d07bb2a80c63c14efd5b393a7b7cb678b9a7ddd9";

    [Fact]
    public void TheVendoredSchemaTreeIsTheProtocolSchemaTreeFileForFile()
    {
        Assert.Equal(ApprovedSchemaTreeSha256, SchemaTreeDigest());
        Assert.Equal(69, SchemaFiles().Length);
    }

    /// <summary>
    /// Every snapshot the journey publisher sends, checked against its own schema.
    /// </summary>
    /// <remarks>
    /// The payloads come from the real publisher rather than from a fixture written beside the
    /// assertion: a hand-built sample proves the sample conforms, not the server. The three
    /// snapshots are the C_TO_O ones this runtime emits; the commands and responses are checked by
    /// the tests that own their behaviour, and adding them here would duplicate rather than extend.
    /// </remarks>
    [Fact]
    public async Task EverySnapshotThePublisherSendsMatchesItsFrozenSchema()
    {
        string[] wire = await PublishedSnapshotsAsync();
        Assert.Equal(3, wire.Length);

        List<string> offences = [];
        foreach (string line in wire)
        {
            using JsonDocument envelope = JsonDocument.Parse(line);
            string messageType = envelope.RootElement.GetProperty("messageType").GetString()!;
            using JsonDocument schema = JsonDocument.Parse(File.ReadAllBytes(SchemaPath(messageType)));

            offences.AddRange(Offences(
                messageType,
                "payload",
                envelope.RootElement.GetProperty("payload"),
                schema.RootElement.GetProperty("properties").GetProperty("payload")));
        }

        Assert.True(
            offences.Count == 0,
            "The server sends payloads its own frozen schemas reject: " + string.Join("; ", offences));
    }

    /// <summary>
    /// Proves the shape check is not vacuous, by running it over the payload shape v1 sent.
    /// </summary>
    /// <remarks>
    /// The three legs of the real defect, reproduced exactly: a top-level <c>demandId</c> the v2
    /// payload forbids, a leg missing the three fields v2 added, and <c>legType: "TO_GATE"</c>. If
    /// the check reported nothing here it would have reported nothing on 2026-09-08 either.
    /// </remarks>
    [Fact]
    public void TheShapeCheckReportsThePayloadShapeVersionOneSent()
    {
        const string v1Payload = """
            {
              "planRevision": 7,
              "demandId": "DEMAND-1",
              "legs": [
                {
                  "movementLegId": "00000000-0000-4000-8000-000000000326",
                  "legType": "TO_GATE",
                  "sequence": 2,
                  "stationId": "GATE",
                  "mapId": "25",
                  "state": "PLANNED"
                }
              ]
            }
            """;

        using JsonDocument payload = JsonDocument.Parse(v1Payload);
        using JsonDocument schema = JsonDocument.Parse(
            File.ReadAllBytes(SchemaPath("UpcomingStopPlanSnapshot")));

        string[] offences =
        [
            .. Offences(
                "UpcomingStopPlanSnapshot",
                "payload",
                payload.RootElement,
                schema.RootElement.GetProperty("properties").GetProperty("payload"))
        ];

        Assert.Contains(offences, offence => offence.Contains("demandId", StringComparison.Ordinal));
        Assert.Contains(
            offences,
            offence => offence.Contains("stopPurposeCategory", StringComparison.Ordinal));
        Assert.Contains(offences, offence => offence.Contains("TO_GATE", StringComparison.Ordinal));
    }

    /// <summary>
    /// Name-set equality against <c>required</c>, then enum membership, then the same recursively
    /// for each array element.
    /// </summary>
    private static List<string> Offences(
        string messageType,
        string path,
        JsonElement value,
        JsonElement schema)
    {
        List<string> offences = [];
        JsonElement resolved = Resolve(schema);

        if (value.ValueKind == JsonValueKind.Array)
        {
            if (!resolved.TryGetProperty("items", out JsonElement items))
            {
                return offences;
            }

            int index = 0;
            foreach (JsonElement element in value.EnumerateArray())
            {
                offences.AddRange(Offences(messageType, $"{path}[{index++}]", element, items));
            }

            return offences;
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            if (!resolved.TryGetProperty("required", out JsonElement required))
            {
                return offences;
            }

            string[] expected = [.. required.EnumerateArray().Select(name => name.GetString()!).Order(StringComparer.Ordinal)];
            string[] actual = [.. value.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal)];

            string[] missing = [.. expected.Except(actual, StringComparer.Ordinal)];
            string[] unexpected = [.. actual.Except(expected, StringComparer.Ordinal)];

            if (missing.Length > 0)
            {
                offences.Add($"{messageType}.{path} omits required {string.Join(", ", missing)}");
            }

            if (unexpected.Length > 0)
            {
                offences.Add(
                    $"{messageType}.{path} carries {string.Join(", ", unexpected)}, which the schema "
                    + "does not declare and additionalProperties forbids");
            }

            JsonElement properties = resolved.GetProperty("properties");
            foreach (JsonProperty property in value.EnumerateObject())
            {
                if (properties.TryGetProperty(property.Name, out JsonElement propertySchema))
                {
                    offences.AddRange(Offences(
                        messageType, $"{path}.{property.Name}", property.Value, propertySchema));
                }
            }

            return offences;
        }

        string[] permitted = EnumValues(resolved);
        if (permitted.Length > 0
            && value.ValueKind == JsonValueKind.String
            && !permitted.Contains(value.GetString(), StringComparer.Ordinal))
        {
            offences.Add(
                $"{messageType}.{path} is \"{value.GetString()}\", outside the frozen enumeration "
                + string.Join("/", permitted));
        }

        return offences;
    }

    /// <summary>
    /// The enumeration a field is constrained to, following one <c>$ref</c> hop and unwrapping the
    /// <c>anyOf [ ..., null ]</c> the protocol uses for a nullable enum.
    /// </summary>
    private static string[] EnumValues(JsonElement schema)
    {
        if (schema.TryGetProperty("enum", out JsonElement values))
        {
            return [.. values.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String)
                .Select(value => value.GetString()!)];
        }

        if (schema.TryGetProperty("anyOf", out JsonElement branches))
        {
            foreach (JsonElement branch in branches.EnumerateArray())
            {
                string[] nested = EnumValues(Resolve(branch));
                if (nested.Length > 0)
                {
                    return nested;
                }
            }
        }

        return [];
    }

    /// <summary>
    /// One <c>$ref</c> hop into <c>common/types.schema.json</c>, which is as deep as the protocol's
    /// own payloads go. Anything else is returned unchanged and simply goes unchecked -- stated
    /// rather than hidden, because a resolver that silently returned an empty schema would make
    /// this whole class quietly weaker.
    /// </summary>
    private static JsonElement Resolve(JsonElement schema)
    {
        if (!schema.TryGetProperty("$ref", out JsonElement reference))
        {
            return schema;
        }

        string target = reference.GetString()!;
        int fragment = target.IndexOf("#/$defs/", StringComparison.Ordinal);
        if (fragment < 0 || !target.Contains("common/types.schema.json", StringComparison.Ordinal))
        {
            return schema;
        }

        JsonDocument types = TypeDefinitions.Value;
        return types.RootElement.GetProperty("$defs").GetProperty(target[(fragment + 8)..]);
    }

    private static readonly Lazy<JsonDocument> TypeDefinitions = new(() => JsonDocument.Parse(
        File.ReadAllBytes(Path.Combine(SchemaRoot(), "common", "types.schema.json"))));

    /// <summary>
    /// Drives the real publisher once per snapshot and returns the exact bytes it sent.
    /// </summary>
    private static async Task<string[]> PublishedSnapshotsAsync()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        DbContextOptions<ControlServerDbContext> options = new DbContextOptionsBuilder<ControlServerDbContext>()
            .UseSqlite(connection)
            .Options;
        await using ControlServerDbContext context = new(options);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

        CapturingPeer peer = new();
        OnboardJourneyPublisher publisher = new(new WireToGateStore(context), peer, new FixedClock());
        const string agvId = "AGV-001";
        const long generation = 9;
        const string demandId = "00000000-0000-4000-8000-000000000401";

        await publisher.PublishVehicleBusinessStateAsync(
            "00000000-0000-4000-8000-000000000411",
            agvId,
            generation,
            new VehicleBusinessProjection(3, "READY", "TRANSPORT", false, "SUFFICIENT", []),
            TestContext.Current.CancellationToken);
        await publisher.PublishCurrentStopWorklistAsync(
            "00000000-0000-4000-8000-000000000412",
            agvId,
            generation,
            new CurrentStopWorklistProjection(
                "PICKUP-01",
                5,
                "00000000-0000-4000-8000-000000000413",
                [new CurrentStopWorklistItem(
                    demandId, "SUBLOT-001|WIRE_TO_GATE", "SUBLOT-001", "WIRE_TO_GATE", "PICKUP", 2)]),
            TestContext.Current.CancellationToken);
        await publisher.PublishUpcomingStopPlanAsync(
            "00000000-0000-4000-8000-000000000414",
            agvId,
            generation,
            new UpcomingStopPlanProjection(
                7,
                [
                    new UpcomingMovementLeg(
                        "00000000-0000-4000-8000-000000000415",
                        "TO_PICKUP", "BUSINESS", demandId, null, 1, "PICKUP-01", "25", "ARRIVED"),
                    new UpcomingMovementLeg(
                        "00000000-0000-4000-8000-000000000416",
                        "TO_DROPOFF", "BUSINESS", demandId, null, 2, "GATE", "25", "PLANNED")
                ]),
            TestContext.Current.CancellationToken);

        return [.. peer.Lines];
    }

    private static string SchemaPath(string messageType) =>
        Path.Combine(SchemaRoot(), "messages", $"{messageType}.schema.json");

    private static string SchemaRoot() => Path.Combine(
        ProtocolIdentityArchitectureTests.RepositoryRoot(), "vendor", "8005-agv-protocol", "schemas");

    private static string[] SchemaFiles() =>
    [
        .. Directory.EnumerateFiles(SchemaRoot(), "*.json", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(SchemaRoot(), path).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
    ];

    private static string SchemaTreeDigest()
    {
        using IncrementalHash tree = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string relative in SchemaFiles())
        {
            byte[] bytes = File.ReadAllBytes(Path.Combine(SchemaRoot(), relative));
            tree.AppendData(Encoding.UTF8.GetBytes(relative + "\n"));
            tree.AppendData(Encoding.ASCII.GetBytes(
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() + "\n"));
        }

        return Convert.ToHexString(tree.GetHashAndReset()).ToLowerInvariant();
    }

    private sealed class CapturingPeer : IOnboardPeer
    {
        public List<string> Lines { get; } = [];

        public Task SendAsync(ReadOnlyMemory<byte> ndjsonLine, CancellationToken cancellationToken)
        {
            Lines.Add(Encoding.UTF8.GetString(ndjsonLine.Span));
            return Task.CompletedTask;
        }
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 9, 8, 9, 0, 0, TimeSpan.Zero);
    }
}
