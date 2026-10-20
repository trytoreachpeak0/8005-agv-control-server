using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ControlServer.Domain;

namespace ControlServer.Tests;

/// <summary>
/// The outbound schema validator's compiled-schema cache (control-server#584), driven through
/// <c>tools/ControlServer.SchemaCacheProbe</c> over copies of the vendored schemas/ tree that a test is free to change.
/// </summary>
/// <remarks>
/// <para>
/// What is at stake: a cache entry used under the wrong key is a gate judging new traffic by an old schema, and it stays
/// green while doing it. So the point of every test here is that the entry is <i>not</i> used when any part of what it
/// was compiled from has moved -- a schema file, a file the schema only references, the tool, the Corvus packages -- and
/// that the line is then judged by what is there now.
/// </para>
/// <para>
/// Not through the validator itself: it refuses, with exit 2, any tree that is not the contract
/// <see cref="ProtocolCandidateIdentity"/> names (<see cref="SchemaConformanceToolTests"/> pins that), so through it a
/// changed schema never reaches the cache at all. That refusal is the first line; the key is the second, and this is
/// where the second is tested. The probe is a process of its own for the validator's reason: Corvus must stay out of
/// this host.
/// </para>
/// </remarks>
public sealed class SchemaCompilationCacheTests : IDisposable
{
    private const string DurableAck = "messages/DurableAck.schema.json";
    private const string ToolVersion = "tool-a";
    private const string CorvusVersion = "Corvus.Json.Validator/4.6.7";
    private static readonly string ProbeExecutable = Path.ChangeExtension(
        typeof(SchemaCompilationCacheTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "ControlServer.SchemaCacheProbe.Path").Value!,
        ".exe");
    private static readonly string VendoredSchemas = Path.Combine(
        Path.GetDirectoryName(typeof(SchemaCompilationCacheTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "ControlServer.SchemaConformance.Path").Value!)!,
        "vendor", "8005-agv-protocol", "schemas");

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "controlserver-schema-cache-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _cache;
    private readonly string _schemas;

    public SchemaCompilationCacheTests()
    {
        _cache = Path.Combine(_directory, "cache");
        _schemas = Path.Combine(_directory, "schemas");
        foreach (string file in Directory.EnumerateFiles(VendoredSchemas, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(_schemas, Path.GetRelativePath(VendoredSchemas, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A file handle still open in the temporary directory is not worth a red test.
        }
    }

    [Fact]
    public async Task ASecondRunUsesTheEntryTheFirstWroteAndJudgesAsCorvusItselfDoes()
    {
        string conforming = await WriteLineAsync("conforming", DurableAckLine());
        string violating = await WriteLineAsync("violating", DurableAckLine(without: "durablyAcceptedAt"));

        JsonNode cold = await ProbeAsync(conforming, compare: true);
        JsonNode warm = await ProbeAsync(conforming, compare: true);
        JsonNode warmViolating = await ProbeAsync(violating, compare: true);

        AssertCounts(cold, hits: 0, misses: 1, rejected: 0);
        AssertCounts(warm, hits: 1, misses: 0, rejected: 0);
        AssertCounts(warmViolating, hits: 1, misses: 0, rejected: 0);
        Assert.True(cold["valid"]!.GetValue<bool>(), cold.ToJsonString());
        Assert.True(warm["valid"]!.GetValue<bool>(), warm.ToJsonString());
        Assert.False(warmViolating["valid"]!.GetValue<bool>(), warmViolating.ToJsonString());
        // The compiled-and-cached path is a port of Corvus's own; on both lines it says what Corvus says, result for result.
        foreach (JsonNode run in (JsonNode[])[cold, warm, warmViolating])
        {
            Assert.Equal(run["corvusValid"]!.GetValue<bool>(), run["valid"]!.GetValue<bool>());
            Assert.Equal(run["corvusErrors"]!.ToJsonString(), run["errors"]!.ToJsonString());
        }
        Assert.Contains("durablyAcceptedAt", warmViolating["errors"]!.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AChangedSchemaFileIsCompiledAgainAndTheSameLineIsJudgedByIt()
    {
        string line = await WriteLineAsync("line", DurableAckLine());
        JsonNode before = await ProbeAsync(line);
        Assert.True(before["valid"]!.GetValue<bool>(), before.ToJsonString());
        string original = await File.ReadAllTextAsync(Path.Combine(_schemas, DurableAck), TestContext.Current.CancellationToken);

        // A field the traffic does not carry becomes required: the same line must now fail.
        JsonObject changed = JsonNode.Parse(original)!.AsObject();
        changed["required"]!.AsArray().Add("probeRequiredField");
        await File.WriteAllTextAsync(Path.Combine(_schemas, DurableAck), changed.ToJsonString(), TestContext.Current.CancellationToken);
        JsonNode after = await ProbeAsync(line);

        AssertCounts(after, hits: 0, misses: 1, rejected: 0);
        Assert.NotEqual(before["key"]!.GetValue<string>(), after["key"]!.GetValue<string>());
        Assert.False(after["valid"]!.GetValue<bool>(), after.ToJsonString());
        Assert.Contains("probeRequiredField", after["errors"]!.ToJsonString(), StringComparison.Ordinal);

        // Put back, the tree is the one the first entry was written for again, and that entry serves it.
        await File.WriteAllTextAsync(Path.Combine(_schemas, DurableAck), original, TestContext.Current.CancellationToken);
        JsonNode restored = await ProbeAsync(line);
        AssertCounts(restored, hits: 1, misses: 0, rejected: 0);
        Assert.Equal(before["key"]!.GetValue<string>(), restored["key"]!.GetValue<string>());
        Assert.True(restored["valid"]!.GetValue<bool>(), restored.ToJsonString());
    }

    /// <summary>
    /// DurableAck.schema.json is not touched; only a definition it <c>$ref</c>s is. A key on the schema's own file would
    /// hit here and judge by the old definition, which is why the key is the whole tree's digest.
    /// </summary>
    [Fact]
    public async Task AChangedDefinitionTheSchemaOnlyReferencesIsCompiledAgainToo()
    {
        string line = await WriteLineAsync("line", DurableAckLine());
        JsonNode before = await ProbeAsync(line);
        Assert.True(before["valid"]!.GetValue<bool>(), before.ToJsonString());

        string types = Path.Combine(_schemas, "common", "types.schema.json");
        JsonObject changed = JsonNode.Parse(await File.ReadAllTextAsync(types, TestContext.Current.CancellationToken))!.AsObject();
        changed["$defs"]!["Id"] = new JsonObject { ["type"] = "string", ["maxLength"] = 3 };
        await File.WriteAllTextAsync(types, changed.ToJsonString(), TestContext.Current.CancellationToken);
        JsonNode after = await ProbeAsync(line);

        AssertCounts(after, hits: 0, misses: 1, rejected: 0);
        Assert.False(after["valid"]!.GetValue<bool>(), after.ToJsonString());
        Assert.Contains("maxLength", after["errors"]!.ToJsonString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnotherToolBuildDoesNotUseTheEntry()
    {
        string line = await WriteLineAsync("line", DurableAckLine());
        JsonNode first = await ProbeAsync(line);
        JsonNode again = await ProbeAsync(line);
        JsonNode otherTool = await ProbeAsync(line, toolVersion: "tool-b");

        AssertCounts(first, hits: 0, misses: 1, rejected: 0);
        AssertCounts(again, hits: 1, misses: 0, rejected: 0);
        AssertCounts(otherTool, hits: 0, misses: 1, rejected: 0);
        Assert.NotEqual(first["key"]!.GetValue<string>(), otherTool["key"]!.GetValue<string>());
    }

    [Fact]
    public async Task AnotherCorvusDoesNotUseTheEntry()
    {
        string line = await WriteLineAsync("line", DurableAckLine());
        JsonNode first = await ProbeAsync(line);
        JsonNode again = await ProbeAsync(line);
        JsonNode otherCorvus = await ProbeAsync(line, corvusVersion: "Corvus.Json.Validator/4.6.8");

        AssertCounts(first, hits: 0, misses: 1, rejected: 0);
        AssertCounts(again, hits: 1, misses: 0, rejected: 0);
        AssertCounts(otherCorvus, hits: 0, misses: 1, rejected: 0);
        Assert.NotEqual(first["key"]!.GetValue<string>(), otherCorvus["key"]!.GetValue<string>());
    }

    [Fact]
    public async Task ATruncatedEntryIsCompiledAgainAndReplacedNeverTrusted()
    {
        string violating = await WriteLineAsync("violating", DurableAckLine(without: "durablyAcceptedAt"));
        JsonNode first = await ProbeAsync(violating);
        string entry = Assert.Single(Directory.GetFiles(_cache, "*.schema-cache"));
        byte[] whole = await File.ReadAllBytesAsync(entry, TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(entry, whole[..(whole.Length / 2)], TestContext.Current.CancellationToken);

        JsonNode damaged = await ProbeAsync(violating);
        JsonNode repaired = await ProbeAsync(violating);

        AssertCounts(damaged, hits: 0, misses: 1, rejected: 1);
        Assert.False(damaged["valid"]!.GetValue<bool>(), "a damaged entry must not turn a violation into a pass: " + damaged.ToJsonString());
        Assert.Equal(first["errors"]!.ToJsonString(), damaged["errors"]!.ToJsonString());
        AssertCounts(repaired, hits: 1, misses: 0, rejected: 0);
        Assert.Equal(first["errors"]!.ToJsonString(), repaired["errors"]!.ToJsonString());
    }

    [Fact]
    public async Task AnEntryWhoseAssemblyBytesChangedIsCompiledAgain()
    {
        string line = await WriteLineAsync("line", DurableAckLine());
        await ProbeAsync(line);
        string entry = Assert.Single(Directory.GetFiles(_cache, "*.schema-cache"));
        byte[] bytes = await File.ReadAllBytesAsync(entry, TestContext.Current.CancellationToken);
        bytes[^1] ^= 0xFF;
        await File.WriteAllBytesAsync(entry, bytes, TestContext.Current.CancellationToken);

        JsonNode damaged = await ProbeAsync(line);

        AssertCounts(damaged, hits: 0, misses: 1, rejected: 1);
        Assert.True(damaged["valid"]!.GetValue<bool>(), damaged.ToJsonString());
    }

    /// <summary>
    /// <c>format</c> is asserted, as <c>JsonSchema.FromText</c> asserts it with the options the validator passes: the
    /// envelope's <c>Id</c> and <c>Instant</c> are guarded by <c>format: uuid</c> and <c>format: date-time</c> alone
    /// (common/types.schema.json, no pattern behind them), so this line fails on format and nothing else. The ported
    /// compile spells <c>alwaysAssertFormat: true</c> by hand; this is what holds that line.
    /// </summary>
    [Fact]
    public async Task AFormatViolationIsAViolationAsCorvusItselfJudgesIt()
    {
        string line = await WriteLineAsync(
            "bad-format", DurableAckLine(messageId: "not-a-uuid", sentAt: "yesterday at noon"));

        JsonNode cold = await ProbeAsync(line, compare: true);
        JsonNode warm = await ProbeAsync(line, compare: true);

        foreach (JsonNode run in (JsonNode[])[cold, warm])
        {
            Assert.False(run["valid"]!.GetValue<bool>(), run.ToJsonString());
            Assert.False(run["corvusValid"]!.GetValue<bool>(), run.ToJsonString());
            Assert.Equal(run["corvusErrors"]!.ToJsonString(), run["errors"]!.ToJsonString());
            Assert.Contains("format", run["errors"]!.ToJsonString(), StringComparison.OrdinalIgnoreCase);
        }
        AssertCounts(warm, hits: 1, misses: 0, rejected: 0);
    }

    /// <summary>
    /// Several validators compiling the same key at once -- every G2 validator splits into four, and slices can overlap.
    /// What this holds: every one of them judges the line the same, and what they leave is one entry that checks out --
    /// no temporary files, and the next run uses it. What it does not hold is the rename itself: six processes do not
    /// hit the instant in which an entry written in place is half there (control-server#584 mutation M6, writing in
    /// place, leaves this green). A half-written entry, should one be read, is rejected and compiled again; that is
    /// <see cref="ATruncatedEntryIsCompiledAgainAndReplacedNeverTrusted"/>.
    /// </summary>
    [Fact]
    public async Task ProcessesCompilingTheSameKeyAtOnceJudgeAlikeAndLeaveOneEntryThatChecksOut()
    {
        string line = await WriteLineAsync("line", DurableAckLine(without: "durablyAcceptedAt"));

        JsonNode[] racing = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => ProbeAsync(line)));
        JsonNode after = await ProbeAsync(line);

        Assert.All(racing, run => Assert.False(run["valid"]!.GetValue<bool>(), run.ToJsonString()));
        Assert.Single(Directory.GetFiles(_cache, "*.schema-cache"));
        Assert.Empty(Directory.GetFiles(_cache, "*.tmp"));
        AssertCounts(after, hits: 1, misses: 0, rejected: 0);
        Assert.Equal(racing[0]["errors"]!.ToJsonString(), after["errors"]!.ToJsonString());
    }

    private static void AssertCounts(JsonNode run, int hits, int misses, int rejected)
    {
        Assert.True(
            run["hits"]!.GetValue<int>() == hits && run["misses"]!.GetValue<int>() == misses &&
                run["rejected"]!.GetValue<int>() == rejected,
            $"expected {hits} hit(s), {misses} miss(es), {rejected} rejected: {run.ToJsonString()}");
    }

    private async Task<string> WriteLineAsync(string name, string line)
    {
        string path = Path.Combine(_directory, name + ".json");
        await File.WriteAllTextAsync(path, line, TestContext.Current.CancellationToken);
        return path;
    }

    private async Task<JsonNode> ProbeAsync(
        string linePath, bool compare = false, string toolVersion = ToolVersion, string corvusVersion = CorvusVersion)
    {
        Assert.True(File.Exists(ProbeExecutable), $"the probe was not built: {ProbeExecutable}");
        ProcessStartInfo start = new(ProbeExecutable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in (string[])[
            "--schemas", _schemas, "--schema", DurableAck, "--line", linePath, "--cache", _cache,
            "--tool-version", toolVersion, "--corvus-version", corvusVersion, "--compare", compare ? "true" : "false"])
        {
            start.ArgumentList.Add(argument);
        }
        using Process process = Process.Start(start)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        Task<string> error = process.StandardError.ReadToEndAsync(CancellationToken.None);
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        Assert.True(process.ExitCode == 0, $"probe exit {process.ExitCode}: {await output}{await error}");
        return JsonNode.Parse(await output)!;
    }

    /// <summary>A DurableAck of the shape the product sends, identity fields read off <see cref="ProtocolCandidateIdentity"/>.</summary>
    private static string DurableAckLine(
        string? without = null, string messageId = "11111111-1111-4111-8111-111111111111",
        string sentAt = "2026-09-17T08:00:00.000+00:00")
    {
        Dictionary<string, object?> payload = new(StringComparer.Ordinal)
        {
            ["acceptedMessageId"] = "33333333-3333-4333-8333-333333333333",
            ["acceptedMessageType"] = "OperationResult",
            ["acceptedContentSha256"] = new string('0', 64),
            ["durablyAcceptedAt"] = "2026-09-17T08:00:00.000+00:00"
        };
        if (without is not null)
        {
            payload.Remove(without);
        }
        return JsonSerializer.Serialize(new
        {
            protocolVersion = ProtocolCandidateIdentity.ProtocolVersion,
            profileId = ProtocolCandidateIdentity.ProfileId,
            protocolReleaseVersion = ProtocolCandidateIdentity.ReleaseVersion,
            protocolReleaseManifestSha256 = ProtocolCandidateIdentity.ManifestSha256,
            messageType = "DurableAck",
            messageId,
            correlationId = "22222222-2222-4222-8222-222222222222",
            agvId = "AGV-8005-01",
            sessionGeneration = 1L,
            sentAt,
            payload
        });
    }
}
