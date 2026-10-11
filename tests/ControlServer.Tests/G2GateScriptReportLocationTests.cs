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

    /// <summary>
    /// The test run exits 0 and leaves no report. On this side that is never "the slice sent
    /// nothing": the validator writes schema-coverage.json on every run, zero lines included, and
    /// any failure of its own fails the run. So a missing report is lost evidence, and is not PASS.
    /// </summary>
    [Fact]
    public void AMissingReportIsNotCertifiedAsPass()
    {
        GateRun run = Run(output: "lost", reportMode: "lose", testExitCode: 0);

        JsonElement result = run.GateResult(Path.Combine(caller, "lost"));
        Assert.NotEqual("PASS", result.GetProperty("status").GetString());
        Assert.True(result.GetProperty("schemaConformanceMissing").GetBoolean());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("schemaConformance").ValueKind);
        Assert.Equal(0, result.GetProperty("testExitCode").GetInt32());
        Assert.NotEqual(0, run.ExitCode);
        Assert.Contains("SCHEMA_REPORT_MISSING", run.Console, StringComparison.Ordinal);
    }

    /// <summary>
    /// A failed test run keeps its own exit code and is not reported as a lost report: without the
    /// validator's report on a failed run there is nothing unusual to shout about.
    /// </summary>
    [Fact]
    public void AFailedRunKeepsItsExitCodeAndIsNotReportedAsALostReport()
    {
        GateRun run = Run(output: "failed", reportMode: "lose", testExitCode: 7);

        JsonElement result = run.GateResult(Path.Combine(caller, "failed"));
        Assert.Equal("FAIL", result.GetProperty("status").GetString());
        Assert.Equal(7, result.GetProperty("testExitCode").GetInt32());
        Assert.True(result.GetProperty("schemaConformanceMissing").GetBoolean());
        Assert.Equal(7, run.ExitCode);
        Assert.DoesNotContain("SCHEMA_REPORT_MISSING", run.Console, StringComparison.Ordinal);
    }

    /// <summary>
    /// The second run into the same relative -Output is refused before anything is written, as it
    /// was before the path became absolute.
    /// </summary>
    [Fact]
    public void ASecondRunIntoTheSameRelativeOutputIsRefused()
    {
        Assert.Equal(0, Run(output: "once", reportMode: "write", testExitCode: 0).ExitCode);
        string before = File.ReadAllText(Path.Combine(caller, "once", "gate-result.json"));

        GateRun second = Run(output: "once", reportMode: "write", testExitCode: 0);

        Assert.NotEqual(0, second.ExitCode);
        Assert.Contains("Output directory already exists", second.Console, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllText(Path.Combine(caller, "once", "gate-result.json")));
    }

    /// <summary>
    /// A relative -ProtocolManifest is resolved against the caller too.
    /// </summary>
    [Fact]
    public void ARelativeProtocolManifestIsResolvedAgainstTheCaller()
    {
        // A copy next to the caller, not a path into the repository: the temp directory and the
        // checkout are often on different drives (worktrees live on F:), where no relative path
        // between them exists. The script checks the copy's hash, so it has to be the same bytes.
        string copy = Path.Combine(Directory.CreateDirectory(Path.Combine(work, "proto")).FullName, "release.json");
        File.Copy(ProtocolIdentityArchitectureTests.ManifestPath(), copy);
        string relative = Path.Combine("..", "proto", "release.json");

        GateRun run = Run(output: "manifest", reportMode: "write", testExitCode: 0, protocolManifest: relative);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("PASS", run.GateResult(Path.Combine(caller, "manifest")).GetProperty("status").GetString());
    }

    /// <summary>
    /// The guard against moving the conversion back downstream: both path parameters are made
    /// absolute before the script first uses either, and before it creates the evidence directory or
    /// starts dotnet. It shows the conversion is written first, not that it is right -- the tests
    /// above and the run recorded under evidence/cs559/ show that.
    /// </summary>
    [Fact]
    public void BothPathsAreMadeAbsoluteBeforeTheirFirstUse()
    {
        string path = Path.Combine(ProtocolIdentityArchitectureTests.RepositoryRoot(), "scripts", "test-wire-to-gate.ps1");
        string text = File.ReadAllText(path);

        // Parsed by pwsh itself so that comments and strings are not mistaken for code: each line is
        // "<kind>|<offset>|<text>" for the conversions, the evidence directory, the dotnet calls and
        // every read of the two variables.
        string listing = RunPwsh(
            "$errors = $null; $ast = [System.Management.Automation.Language.Parser]::ParseFile(" +
            $"'{path.Replace("'", "''", StringComparison.Ordinal)}', [ref]$null, [ref]$errors); " +
            "\"errors|0|$($errors.Count)\"; " +
            "$ast.EndBlock.FindAll({ param($n) $n -is [System.Management.Automation.Language.AssignmentStatementAst] -and " +
            "$n.Left.VariablePath.UserPath -in 'Output','ProtocolManifest' -and $n.Right.Extent.Text -like '*GetUnresolvedProviderPathFromPSPath*' }, $true) | " +
            "ForEach-Object { \"convert-$($_.Left.VariablePath.UserPath)|$($_.Extent.StartOffset)|\" }; " +
            "$ast.EndBlock.FindAll({ param($n) $n -is [System.Management.Automation.Language.VariableExpressionAst] -and " +
            "$n.VariablePath.UserPath -in 'Output','ProtocolManifest' -and -not ($n.Parent -is [System.Management.Automation.Language.AssignmentStatementAst] -and $n.Parent.Left -eq $n) }, $true) | " +
            "ForEach-Object { \"use-$($_.VariablePath.UserPath)|$($_.Extent.StartOffset)|\" }; " +
            "$ast.EndBlock.FindAll({ param($n) $n -is [System.Management.Automation.Language.CommandAst] -and " +
            "($n.GetCommandName() -eq 'New-Item' -or $n.CommandElements[0].Extent.Text -eq '$dotnet') }, $true) | " +
            "ForEach-Object { \"command|$($_.Extent.StartOffset)|$($_.CommandElements[0].Extent.Text)\" }");
        List<(string Kind, int Offset)> found = [.. listing
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split('|'))
            .Select(parts => (parts[0], int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture)))];

        Assert.Contains("errors|0|0", listing, StringComparison.Ordinal);
        foreach (string name in new[] { "Output", "ProtocolManifest" })
        {
            int[] conversions = [.. found.Where(item => item.Kind == "convert-" + name).Select(item => item.Offset)];
            Assert.True(conversions.Length == 1, $"expected one conversion of ${name}, found {conversions.Length}. {listing}");
            int conversion = conversions[0];
            // The conversion's own right-hand side reads the variable, after the statement's start offset.
            Assert.False(
                found.Any(item => item.Kind == "use-" + name && item.Offset < conversion),
                $"${name} is read before it is made absolute. {listing}");
            Assert.False(
                found.Any(item => item.Kind == "command" && item.Offset < conversion),
                $"New-Item or dotnet runs before ${name} is made absolute. {listing}");
        }
        Assert.Contains("GetUnresolvedProviderPathFromPSPath", text, StringComparison.Ordinal);
    }

    private static string RunPwsh(string command)
    {
        ProcessStartInfo start = new("pwsh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-Command", "$ErrorActionPreference = 'Stop'; " + command })
        {
            start.ArgumentList.Add(argument);
        }
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("pwsh did not start.");
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"pwsh failed (exit {process.ExitCode}): {stdout}{stderr}");
        return stdout;
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
