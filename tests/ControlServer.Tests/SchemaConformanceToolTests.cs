using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ControlServer.Domain;

namespace ControlServer.Tests;

/// <summary>
/// The outbound schema validator, driven the way the test-host fixture will drive it: as its own
/// process, over the contract this repository actually vendors (control-server#85).
/// </summary>
/// <remarks>
/// <para>
/// The validator is the one place a sent protocol line is compared with the protocol's JSON Schemas,
/// so the three exit codes are the whole interface: 0 conforms, 1 violates, 2 the vendored copy is not
/// the contract <see cref="ProtocolCandidateIdentity"/> names. Each is pinned here against the real
/// <c>vendor/8005-agv-protocol/</c> tree -- the same tree the gate will read -- rather than against a
/// hand-made schema, so a contract refresh that breaks the validator breaks these tests too.
/// </para>
/// <para>
/// <b>It must never be an assembly reference.</b> Corvus.Json.Validator carries System.Text.Json 10
/// and Roslyn; loaded into this host, its System.Text.Json would replace the 8.0 one the product
/// ships with, and every test in the suite would then be measuring a serializer production never
/// runs. The project reference is build order only, and the two tests at the bottom of this class
/// hold that line.
/// </para>
/// </remarks>
public sealed class SchemaConformanceToolTests : IDisposable
{
    private const string Agv = "AGV-8005-01";
    private const string MessageId = "11111111-1111-4111-8111-111111111111";
    private const string CorrelationId = "22222222-2222-4222-8222-222222222222";
    private const string DurableAckSite = "OnboardRecoveryCoordinator.DurableAckAsync";
    private const string SentAt = "2026-09-17T08:00:00.000+00:00";
    private static readonly string ToolExecutable = Path.ChangeExtension(
        typeof(SchemaConformanceToolTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "ControlServer.SchemaConformance.Path").Value!,
        ".exe");

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "controlserver-schema-conformance-" + Guid.NewGuid().ToString("N"));

    public SchemaConformanceToolTests() => Directory.CreateDirectory(_directory);

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
    public async Task AConformingLineExitsZeroAndIsCountedInTheCoverageReport()
    {
        ToolRun run = await RunToolAsync(
            "conforming", [Record("DurableAck", DurableAckSite, DurableAckLine())]);

        Assert.Equal(0, run.ExitCode);
        JsonNode coverage = Coverage(run);
        Assert.Equal(1, coverage["linesChecked"]!.GetValue<int>());
        Assert.Equal(0, coverage["linesInViolation"]!.GetValue<int>());
        Assert.Equal(1, coverage["byMessageType"]!["DurableAck"]!["product"]!.GetValue<int>());
        Assert.False(
            File.Exists(Path.Combine(run.ReportDirectory, "schema-violations.json")),
            "a run with nothing to report must not leave a violations report behind");
    }

    [Fact]
    public async Task ALineMissingARequiredFieldExitsOneAndPointsAtTheMissingProperty()
    {
        ToolRun run = await RunToolAsync(
            "violation",
            [
                Record("DurableAck", DurableAckSite, DurableAckLine()),
                Record("DurableAck", DurableAckSite, DurableAckLine(without: "durablyAcceptedAt"))
            ]);

        Assert.Equal(1, run.ExitCode);
        JsonNode coverage = Coverage(run);
        Assert.Equal(2, coverage["linesChecked"]!.GetValue<int>());
        // The conforming line is not dragged down with the one next to it.
        Assert.Equal(1, coverage["linesInViolation"]!.GetValue<int>());

        JsonArray violations = Violations(run);
        Assert.Single(violations);
        Assert.Equal("DurableAck", violations[0]!["messageType"]!.GetValue<string>());
        Assert.Equal("product", violations[0]!["origin"]!.GetValue<string>());
        // The sending method, not this program: that is what names who has to fix it.
        Assert.Equal(DurableAckSite, violations[0]!["site"]!.GetValue<string>());
        Assert.Single(violations[0]!["errors"]!.AsArray());
        Assert.Equal("#/payload/durablyAcceptedAt", violations[0]!["errors"]![0]!["pointer"]!.GetValue<string>());
        Assert.Equal("required", violations[0]!["errors"]![0]!["keyword"]!.GetValue<string>());
        Assert.Equal("(absent)", violations[0]!["errors"]![0]!["actual"]!.GetValue<string>());
    }

    [Fact]
    public async Task AMessageTypeTheContractDoesNotHaveIsAViolationAgainstTheManifest()
    {
        ToolRun run = await RunToolAsync(
            "unknown-type", [Record("NotAMessage", DurableAckSite, DurableAckLine(messageType: "NotAMessage"))]);

        Assert.Equal(1, run.ExitCode);
        JsonArray violations = Violations(run);
        Assert.Single(violations);
        Assert.Equal("NotAMessage", violations[0]!["messageType"]!.GetValue<string>());
        Assert.Equal("#/messageType", violations[0]!["errors"]![0]!["pointer"]!.GetValue<string>());
    }

    [Fact]
    public async Task AChangedSchemaByteInACopyOfTheContractExitsTwo()
    {
        string vendor = CopyContract("changed-schema");
        // A trailing space: the file still parses, so only the hash can tell that it moved.
        await File.AppendAllTextAsync(
            Path.Combine(vendor, "schemas", "messages", "DurableAck.schema.json"),
            " ",
            TestContext.Current.CancellationToken);

        ToolRun run = await RunToolAsync(
            "changed-schema", [Record("DurableAck", DurableAckSite, DurableAckLine())], vendor);

        Assert.Equal(2, run.ExitCode);
        Assert.Contains("SchemaBundleSha256", run.Error, StringComparison.Ordinal);
        Assert.False(
            File.Exists(Path.Combine(run.ReportDirectory, "schema-coverage.json")),
            "nothing was validated, so there is no coverage to report");
    }

    [Fact]
    public async Task AChangedManifestByteInACopyOfTheContractExitsTwo()
    {
        string vendor = CopyContract("changed-manifest");
        await File.AppendAllTextAsync(
            Path.Combine(vendor, "manifest", "release.json"), " ", TestContext.Current.CancellationToken);

        ToolRun run = await RunToolAsync(
            "changed-manifest", [Record("DurableAck", DurableAckSite, DurableAckLine())], vendor);

        Assert.Equal(2, run.ExitCode);
        Assert.Contains("ManifestSha256", run.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AContractThatIsNotThereExitsTwo()
    {
        ToolRun run = await RunToolAsync(
            "absent-contract",
            [Record("DurableAck", DurableAckSite, DurableAckLine())],
            Path.Combine(_directory, "no-such-vendor"));

        Assert.Equal(2, run.ExitCode);
        Assert.Contains("No vendored protocol", run.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALineSetThatIsNotThereExitsTwo()
    {
        ToolRun run = await RunToolOnAsync(
            "absent-lines", Path.Combine(_directory, "absent-lines.ndjson"));

        Assert.Equal(2, run.ExitCode);
        Assert.Contains("No lines file", run.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTestHostStillLoadsTheSystemTextJsonTheProductShipsWith()
    {
        Assert.Equal(8, typeof(JsonSerializer).Assembly.GetName().Version!.Major);
    }

    /// <summary>
    /// The reference is build order only, checked rather than assumed: the validator's package
    /// closure must not be in this project's dependency graph, and none of it may be sitting next to
    /// the tests where the host could load it.
    /// </summary>
    /// <remarks>
    /// <c>ControlServer.SchemaConformance.deps.json</c> is beside the tests too -- content items
    /// travel with a project reference -- and that file does name Corvus. It is inert: no
    /// <c>runtimeconfig.json</c> points at it, and none of the assemblies it names are here. This is
    /// the line that keeps it inert, because the day one of them appears is the day the whole suite
    /// starts measuring the wrong serializer.
    /// </remarks>
    [Fact]
    public void TheValidatorsLibraryClosureDoesNotReachTheTestHost()
    {
        string dependencies = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "ControlServer.Tests.deps.json"));

        Assert.DoesNotContain("Corvus", dependencies, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFiles(AppContext.BaseDirectory, "Corvus.*.dll"));
        Assert.Empty(Directory.EnumerateFiles(AppContext.BaseDirectory, "System.Text.Json.dll"));
    }

    /// <summary>
    /// A line of the shape the product sends: <c>OnboardJourneyPublisher.SerializeWire</c>'s envelope,
    /// with the identity fields read off <see cref="ProtocolCandidateIdentity"/> rather than spelled
    /// out, so a contract refresh moves this fixture with it instead of turning the test red for a
    /// reason that is not the validator's.
    /// </summary>
    private static string DurableAckLine(string messageType = "DurableAck", string? without = null) =>
        JsonSerializer.Serialize(new
        {
            protocolVersion = ProtocolCandidateIdentity.ProtocolVersion,
            profileId = ProtocolCandidateIdentity.ProfileId,
            protocolReleaseVersion = ProtocolCandidateIdentity.ReleaseVersion,
            protocolReleaseManifestSha256 = ProtocolCandidateIdentity.ManifestSha256,
            messageType,
            messageId = MessageId,
            correlationId = CorrelationId,
            agvId = Agv,
            sessionGeneration = 1L,
            sentAt = SentAt,
            payload = DurableAckPayload(without)
        });

    private static Dictionary<string, object?> DurableAckPayload(string? without)
    {
        Dictionary<string, object?> payload = new(StringComparer.Ordinal)
        {
            ["acceptedMessageId"] = "33333333-3333-4333-8333-333333333333",
            ["acceptedMessageType"] = "OperationResult",
            ["acceptedContentSha256"] = new string('0', 64),
            ["durablyAcceptedAt"] = SentAt
        };
        if (without is not null)
        {
            payload.Remove(without);
        }
        return payload;
    }

    /// <summary>
    /// One record of the input format: the message type, where the line came from, the sending method
    /// and the wire line itself.
    /// </summary>
    private static string Record(string messageType, string site, string line) =>
        JsonSerializer.Serialize(new { messageType, origin = "product", site, line });

    private static JsonNode Coverage(ToolRun run) => JsonNode.Parse(
        File.ReadAllText(Path.Combine(run.ReportDirectory, "schema-coverage.json")))!;

    private static JsonArray Violations(ToolRun run) => JsonNode.Parse(
        File.ReadAllText(Path.Combine(run.ReportDirectory, "schema-violations.json")))!.AsArray();

    /// <summary>
    /// The contract as the build copied it next to the validator, copied again so a test can move one
    /// byte of it without touching either the repository's <c>vendor/</c> or another test's copy.
    /// </summary>
    private string CopyContract(string name)
    {
        string source = Path.Combine(Path.GetDirectoryName(ToolExecutable)!, "vendor", "8005-agv-protocol");
        string destination = Path.Combine(_directory, name + "-vendor");
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        return destination;
    }

    private async Task<ToolRun> RunToolAsync(string name, string[] records, string? vendor = null)
    {
        string linesPath = Path.Combine(_directory, name + ".ndjson");
        await File.WriteAllLinesAsync(linesPath, records, TestContext.Current.CancellationToken);
        return await RunToolOnAsync(name, linesPath, vendor);
    }

    private async Task<ToolRun> RunToolOnAsync(string name, string linesPath, string? vendor = null)
    {
        string reportDirectory = Path.Combine(_directory, name + "-report");

        Assert.True(File.Exists(ToolExecutable), $"the validator was not built: {ToolExecutable}");
        ProcessStartInfo start = new(ToolExecutable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("--lines");
        start.ArgumentList.Add(linesPath);
        start.ArgumentList.Add("--report");
        start.ArgumentList.Add(reportDirectory);
        if (vendor is not null)
        {
            start.ArgumentList.Add("--vendor");
            start.ArgumentList.Add(vendor);
        }

        using Process process = Process.Start(start)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        Task<string> error = process.StandardError.ReadToEndAsync(CancellationToken.None);
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
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
        return new ToolRun(process.ExitCode, await output, await error, reportDirectory);
    }

    private sealed record ToolRun(int ExitCode, string Output, string Error, string ReportDirectory);
}
