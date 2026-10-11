using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace ControlServer.Tests;

/// <summary>
/// <c>scripts/test-wire-to-gate.ps1</c> finds the outbound schema report where the test process
/// wrote it, and does not certify a slice whose report it cannot find (control-server#559).
/// </summary>
/// <remarks>
/// <para>
/// Why it exists: batch 8's exit ran all fifteen <c>CONTROL_SERVER_G2</c> slices with a relative
/// <c>-Output</c>. The script handed that relative path to the test process as
/// <c>WIRE_TO_GATE_SCHEMA_REPORT_DIR</c>, the test host resolved it against its own working
/// directory under <c>bin/</c>, and every <c>gate-result.json</c> said <c>PASS</c> with
/// <c>schemaConformance: null</c> -- the check had run, its evidence had gone into the build output.
/// </para>
/// <para>
/// The real script runs here, with <c>WIRE_TO_GATE_DOTNET_EXE</c> pointed at a stand-in that does
/// what the test host does with that variable: resolve it against a working directory that is not
/// the caller's. So these tests show the conversion is right, not only that it is written first.
/// What they cannot show is that the stand-in's working directory is the real test host's; that is
/// what the run recorded under <c>evidence/cs559/</c> shows.
/// </para>
/// </remarks>
public sealed class G2GateScriptReportLocationTests : IDisposable
{
    private readonly string work = Path.Combine(Path.GetTempPath(), "cs559-" + Guid.NewGuid().ToString("N"));
    private readonly string caller;
    private readonly string testHost;
    private readonly string fakeDotnet;

    public G2GateScriptReportLocationTests()
    {
        caller = Directory.CreateDirectory(Path.Combine(work, "caller")).FullName;
        testHost = Directory.CreateDirectory(Path.Combine(work, "testhost", "bin")).FullName;
        fakeDotnet = Path.Combine(work, "fake-dotnet.ps1");
        File.WriteAllText(fakeDotnet, FakeDotnet, new UTF8Encoding(false));
    }

    public void Dispose()
    {
        if (Directory.Exists(work))
        {
            Directory.Delete(work, recursive: true);
        }
    }

    [Fact]
    public void ARelativeOutputGetsTheSchemaReportNextToTheGateResult()
    {
        GateRun run = Run(output: Path.Combine("evidence", "rel"), reportMode: "write", testExitCode: 0);

        string evidence = Path.Combine(caller, "evidence", "rel");
        Assert.True(
            File.Exists(Path.Combine(evidence, "schema-coverage.json")),
            "schema-coverage.json is not under -Output. " + run.Console);
        Assert.False(
            Directory.Exists(Path.Combine(testHost, "evidence")),
            "the report went to the test host's working directory. " + run.Console);
        JsonElement result = run.GateResult(evidence);
        Assert.Equal("PASS", result.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Object, result.GetProperty("schemaConformance").ValueKind);
        Assert.Equal(3, result.GetProperty("schemaConformance").GetProperty("linesChecked").GetInt32());
        Assert.Equal(0, run.ExitCode);
    }

    private GateRun Run(string output, string reportMode, int testExitCode, string? protocolManifest = null)
    {
        string root = ProtocolIdentityArchitectureTests.RepositoryRoot();
        ProcessStartInfo start = new("pwsh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = caller,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string argument in new[]
                 {
                     "-NoProfile", "-NonInteractive", "-File",
                     Path.Combine(root, "scripts", "test-wire-to-gate.ps1"),
                     "-Slice", "FP-IS-00",
                     "-ProtocolManifest", protocolManifest ?? ProtocolIdentityArchitectureTests.ManifestPath(),
                     "-Output", output,
                 })
        {
            start.ArgumentList.Add(argument);
        }
        start.Environment["WIRE_TO_GATE_DOTNET_EXE"] = fakeDotnet;
        start.Environment["CS559_FAKE_TESTHOST_DIR"] = testHost;
        start.Environment["CS559_FAKE_REPORT_MODE"] = reportMode;
        start.Environment["CS559_FAKE_TEST_EXIT"] = testExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture);
        start.Environment.Remove("WIRE_TO_GATE_SCHEMA_REPORT_DIR");

        using Process process = Process.Start(start) ?? throw new InvalidOperationException("pwsh did not start.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        Task<string> stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        Assert.True(process.WaitForExit(TimeSpan.FromMinutes(2)), "test-wire-to-gate.ps1 did not finish within two minutes.");
        return new GateRun(process.ExitCode, stdout.Result + stderr.Result);
    }

    private sealed record GateRun(int ExitCode, string Console)
    {
        public JsonElement GateResult(string evidence)
        {
            string path = Path.Combine(evidence, "gate-result.json");
            Assert.True(File.Exists(path), "no gate-result.json under " + evidence + ". " + Console);
            using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));
            return document.RootElement.Clone();
        }
    }

    // Stands in for dotnet. --list-tests selects one test so the script's empty-slice refusal stays
    // out of the way. Otherwise it does what the test host does with WIRE_TO_GATE_SCHEMA_REPORT_DIR
    // (OutboundSchemaConformance.DisposeAsync): resolve it against the test host's working
    // directory, which is not the caller's, and write schema-coverage.json there -- unless the mode
    // says the report is lost.
    private const string FakeDotnet = """
        #Requires -Version 7
        if ($args -contains '--list-tests') {
            '    ControlServer.Tests.FakeSlice.Selected'
            exit 0
        }
        if ($env:CS559_FAKE_REPORT_MODE -eq 'write') {
            $report = [IO.Path]::GetFullPath($env:WIRE_TO_GATE_SCHEMA_REPORT_DIR, $env:CS559_FAKE_TESTHOST_DIR)
            New-Item -ItemType Directory -Force -Path $report | Out-Null
            '{"linesChecked":3,"linesInViolation":0,"knownViolationsMatched":0}' |
                Set-Content -LiteralPath (Join-Path $report 'schema-coverage.json') -Encoding utf8NoBOM
        }
        exit ([int]$env:CS559_FAKE_TEST_EXIT)
        """;
}
