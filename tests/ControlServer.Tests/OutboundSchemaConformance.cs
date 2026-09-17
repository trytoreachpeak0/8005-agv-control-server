using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using ControlServer.Domain;

[assembly: AssemblyFixture(typeof(ControlServer.Tests.OutboundSchemaConformance))]

namespace ControlServer.Tests;

/// <summary>
/// Every protocol line this side sends during a test run is checked against the protocol's JSON
/// Schemas (control-server#85). Why it exists: two schema-required fields had no sender at all and
/// all eight slices of <c>CONTROL_SERVER_G2</c> were green, because nothing here ever compared a
/// sent line with the contract.
/// </summary>
/// <remarks>
/// <para>
/// Lines are collected while the tests run, through <see cref="ProtocolEnvelope.OutboundObserver"/>,
/// and validated once at the end by <c>tools/ControlServer.SchemaConformance</c> in its own process
/// -- that project says why it cannot run in here. A violation fails the run as a test assembly
/// cleanup failure: <c>dotnet test</c> and every G2 slice exit non-zero, but the console summary
/// still reads "Failed: 0". The details are in the TRX and in <c>schema-violations.json</c> and
/// <c>schema-coverage.json</c> in the report directory, which is <c>WIRE_TO_GATE_SCHEMA_REPORT_DIR</c>
/// when set (<c>test-wire-to-gate.ps1</c> points it at the G2 evidence) and <c>schema-conformance</c>
/// next to the test assembly otherwise.
/// </para>
/// <para>
/// The report names the sending method, not the observation point: the site is read off the stack at
/// the moment the line is built. A line whose nearest caller is a test is that test exercising the
/// envelope itself, and is skipped rather than validated.
/// </para>
/// </remarks>
public sealed class OutboundSchemaConformance : IAsyncDisposable
{
    private const string ReportDirectoryVariable = "WIRE_TO_GATE_SCHEMA_REPORT_DIR";
    private readonly ConcurrentQueue<ObservedLine> lines = new();

    public OutboundSchemaConformance() => ProtocolEnvelope.OutboundObserver = Observe;

    public async ValueTask DisposeAsync()
    {
        ProtocolEnvelope.OutboundObserver = null;
        string reportDirectory = Environment.GetEnvironmentVariable(ReportDirectoryVariable) is { Length: > 0 } configured
            ? configured
            : Path.Combine(AppContext.BaseDirectory, "schema-conformance");
        Directory.CreateDirectory(reportDirectory);
        // The raw lines stay out of the report directory: a full run is megabytes, and every violation
        // already carries a sample line.
        string linesPath = Path.Combine(
            Path.GetTempPath(), "w2g-outbound-" + Guid.NewGuid().ToString("N") + ".ndjson");
        await File.WriteAllLinesAsync(
            linesPath,
            lines.Select(line => JsonSerializer.Serialize(line, ProtocolEnvelope.SerializerOptions)),
            TestContext.Current.CancellationToken);
        try
        {
            ProcessStartInfo start = new(ValidatorPath())
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add("--lines");
            start.ArgumentList.Add(linesPath);
            start.ArgumentList.Add("--report");
            start.ArgumentList.Add(reportDirectory);
            start.ArgumentList.Add("--known");
            start.ArgumentList.Add(Path.Combine(
                RepositoryRoot(), "tests", "ControlServer.Tests", "schema-known-violations.json"));

            using Process process = Process.Start(start)
                ?? throw new InvalidOperationException("Could not start " + start.FileName);
            Task<string> output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            Task<string> error = process.StandardError.ReadToEndAsync(CancellationToken.None);
            await process.WaitForExitAsync(CancellationToken.None);
            string report = await output + await error;
            await File.WriteAllTextAsync(
                Path.Combine(reportDirectory, "schema-conformance.txt"),
                report,
                TestContext.Current.CancellationToken);
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Outbound schema conformance failed (exit {process.ExitCode}); report in {reportDirectory}." +
                    Environment.NewLine + report);
            }
        }
        finally
        {
            File.Delete(linesPath);
        }
    }

    private void Observe(string messageType, string line)
    {
        (string origin, string site) = Locate(new StackTrace(1, false));
        lines.Enqueue(new ObservedLine(messageType, origin, site, line));
    }

    private static (string Origin, string Site) Locate(StackTrace trace)
    {
        string? origin = null;
        List<string> site = [];
        foreach (StackFrame frame in trace.GetFrames())
        {
            MethodBase? method = frame.GetMethod();
            Type? type = method?.DeclaringType;
            string assembly = type?.Assembly.GetName().Name ?? string.Empty;
            // ControlServer.Domain is where the choke point lives, so its own frames say nothing
            // about who is sending.
            if (method is null || type is null ||
                !assembly.StartsWith("ControlServer.", StringComparison.Ordinal) ||
                assembly == "ControlServer.Domain")
            {
                continue;
            }
            origin ??= assembly switch
            {
                "ControlServer.Tests" => "test",
                "ControlServer.FakeOnboard" => "synthetic-peer",
                _ => "product"
            };
            if (assembly == "ControlServer.Tests")
            {
                break;
            }
            // An async method shows up twice in a row: its kickoff stub, then its state machine.
            string name = Describe(method, type);
            if (site.Count == 0 || site[^1] != name)
            {
                site.Add(name);
            }
            if (site.Count == 3)
            {
                break;
            }
        }
        return (origin ?? "unknown", string.Join(" <- ", site));
    }

    // Async bodies run as MoveNext on a compiler-generated <Name>d__N type, lambdas as <Name>b__N_M,
    // often on a <>c display class; name the method a reader would search for.
    private static string Describe(MethodBase method, Type type)
    {
        // An async lambda nests the manglings: its state machine is <<Outer>b__0>d.
        string generated = type.Name.StartsWith('<') && method.Name == "MoveNext" ? type.Name : method.Name;
        string name = Unmangle(generated);
        int localFunction = generated.IndexOf(">g__", StringComparison.Ordinal);
        if (localFunction >= 0)
        {
            int end = generated.IndexOf('|', localFunction);
            name += "." + (end > 0 ? generated[(localFunction + 4)..end] : generated[(localFunction + 4)..]);
        }
        else if (generated.Contains(">b__", StringComparison.Ordinal))
        {
            name += "(lambda)";
        }
        while (type.Name.StartsWith('<') && type.DeclaringType is not null)
        {
            type = type.DeclaringType;
        }
        return type.Name + "." + name;
    }

    private static string Unmangle(string generated)
    {
        string trimmed = generated.TrimStart('<');
        int close = trimmed.IndexOf('>', StringComparison.Ordinal);
        return close > 0 ? trimmed[..close] : generated;
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ControlServer.sln")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName
            ?? throw new DirectoryNotFoundException("No ControlServer.sln above " + AppContext.BaseDirectory);
    }

    /// <summary>
    /// Where the build put the validator, read from the attribute the test project injects rather than
    /// guessed from this assembly's path (which carries a configuration, a target framework and a RID).
    /// </summary>
    private static string ValidatorPath()
    {
        string path = Path.ChangeExtension(
            typeof(OutboundSchemaConformance).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .Single(attribute => attribute.Key == "ControlServer.SchemaConformance.Path").Value!,
            ".exe");
        return File.Exists(path)
            ? path
            : throw new FileNotFoundException(
                "The schema validator is not built. The test project references it for build order only.", path);
    }

    private sealed record ObservedLine(string MessageType, string Origin, string Site, string Line);
}
