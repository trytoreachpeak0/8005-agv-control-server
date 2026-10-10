using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ControlServer.Host.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ControlServer.Tests;

/// <summary>
/// control-server#535 review M1/M2. What the v2 parallel instance's Host really binds, from the two
/// files it really reads: the package's <c>appsettings.json</c> and the <c>appsettings.Production.json</c>
/// overlay that <c>scripts/parallel/ParallelInstance.psm1</c> generates from an instance definition.
/// </summary>
/// <remarks>
/// .NET configuration merges an array by index: an overlay of <c>["STAGING_TO_WIRE"]</c> over the
/// package's six work types replaces index 0 and leaves the other five -- WIRE_TO_GATE among them --
/// in force. The production MesIngest mode exists to keep WIRE_TO_GATE out, so the only test that
/// counts is the one that binds through the real files and the real registration.
/// </remarks>
public sealed class ParallelInstanceEffectiveConfigurationTests
{
    private const string Base = """
        { "JourneyRuntime": { "allowedWorkTypes": ["DIE_TO_WIRE_STAGING", "DIE_TO_OVEN", "WIRE_TO_GATE", "WIRE_TO_OPTICAL", "STAGING_TO_WIRE", "WIRE_TO_NITROGEN"],
                              "allowedDispatchZones": ["MAP-25-WIRE_TO_GATE"] } }
        """;

    [Fact]
    public void ProductionDefinitionOverPackageAppSettingsBindsStagingToWireOnly()
    {
        string root = FindRepositoryRoot();
        string overlay = GenerateOverlay(root, "instance-factory01-v2.production-mes.json");
        JourneyRuntimeOptions options = Bind(
            File.ReadAllText(Path.Combine(root, "src", "ControlServer.Host", "appsettings.json")), overlay, out IConfiguration configuration);

        Assert.Equal(["STAGING_TO_WIRE"], options.AllowedWorkTypes);
        Assert.Equal(["WIRE"], options.AllowedDispatchZones);
        Assert.Equal("http://127.0.0.1:5088", configuration["MesIngest:baseUrl"]);
    }

    [Fact]
    public void FakeDefinitionOverPackageAppSettingsBindsItsOwnSixWorkTypes()
    {
        string root = FindRepositoryRoot();
        string overlay = GenerateOverlay(root, "instance-factory01-v2.json");
        JourneyRuntimeOptions options = Bind(
            File.ReadAllText(Path.Combine(root, "src", "ControlServer.Host", "appsettings.json")), overlay, out IConfiguration configuration);

        Assert.Equal(
            ["DIE_TO_WIRE_STAGING", "DIE_TO_OVEN", "WIRE_TO_GATE", "WIRE_TO_OPTICAL", "STAGING_TO_WIRE", "WIRE_TO_NITROGEN"],
            options.AllowedWorkTypes);
        Assert.Equal(["WIRE"], options.AllowedDispatchZones);
        Assert.Equal("http://127.0.0.1:58188", configuration["MesIngest:baseUrl"]);
    }

    [Fact]
    public void ShorterListInLaterLayerReplacesEarlierListWhole()
    {
        JourneyRuntimeOptions options = Bind(Base,
            """{ "JourneyRuntime": { "allowedWorkTypes": ["STAGING_TO_WIRE"] } }""", out _);

        Assert.Equal(["STAGING_TO_WIRE"], options.AllowedWorkTypes);
    }

    [Fact]
    public void LaterLayerThatDoesNotNameListLeavesEarlierListAlone()
    {
        JourneyRuntimeOptions options = Bind(Base, """{ "JourneyRuntime": { "enabled": false } }""", out _);

        Assert.Equal(6, options.AllowedWorkTypes.Length);
        Assert.Equal(["MAP-25-WIRE_TO_GATE"], options.AllowedDispatchZones);
    }

    [Fact]
    public void LongerListInLaterLayerIsTakenAsWritten()
    {
        JourneyRuntimeOptions options = Bind(Base,
            """{ "JourneyRuntime": { "allowedDispatchZones": ["WIRE", "OVEN"] } }""", out _);

        Assert.Equal(["WIRE", "OVEN"], options.AllowedDispatchZones);
    }

    [Fact]
    public void ShorterZoneListInLaterLayerReplacesEarlierZoneListWhole()
    {
        JourneyRuntimeOptions options = Bind(
            """{ "JourneyRuntime": { "allowedDispatchZones": ["WIRE", "MAP-25-WIRE_TO_GATE"] } }""",
            """{ "JourneyRuntime": { "allowedDispatchZones": ["WIRE"] } }""", out _);

        Assert.Equal(["WIRE"], options.AllowedDispatchZones);
    }

    [Fact]
    public void ListInsideAChainedConfigurationIsStillTakenWholeFromItsLastLayer()
    {
        // AddConfiguration wraps another configuration in a ChainedConfigurationProvider, whose GetChildKeys
        // answers with the inner configuration already merged by index.
        IConfigurationRoot inner = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(Base)))
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes("""{ "JourneyRuntime": { "allowedWorkTypes": ["STAGING_TO_WIRE"] } }""")))
            .Build();
        IConfigurationRoot outer = new ConfigurationBuilder().AddConfiguration(inner).Build();
        ServiceCollection services = new();
        services.AddJourneyRuntimeOptions(outer);
        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.Equal(["STAGING_TO_WIRE"], provider.GetRequiredService<IOptions<JourneyRuntimeOptions>>().Value.AllowedWorkTypes);
    }

    [Fact]
    public void ConfigurationWithoutLayersToAskIsRefusedRatherThanMergedByIndex()
    {
        IConfigurationRoot root = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes("""{ "Outer": """ + Base + " }")))
            .Build();
        ServiceCollection services = new();
        services.AddJourneyRuntimeOptions(root.GetSection("Outer"));
        using ServiceProvider provider = services.BuildServiceProvider();

        InvalidOperationException refused = Assert.Throws<InvalidOperationException>(
            () => provider.GetRequiredService<IOptions<JourneyRuntimeOptions>>().Value);
        Assert.Contains("allowedWorkTypes", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ShorterListReplacesEarlierListWholeUnderTheHostsConfigurationManager()
    {
        // WebApplicationBuilder.Configuration is a ConfigurationManager, not a ConfigurationRoot.
        using ConfigurationManager configuration = new();
        configuration.AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(Base)));
        configuration.AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes("""{ "JourneyRuntime": { "allowedWorkTypes": ["STAGING_TO_WIRE"] } }""")));
        ServiceCollection services = new();
        services.AddJourneyRuntimeOptions(configuration);
        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.Equal(["STAGING_TO_WIRE"], provider.GetRequiredService<IOptions<JourneyRuntimeOptions>>().Value.AllowedWorkTypes);
    }

    // ------------------------------------------------------------------------------------------------
    // control-server#571. The parallel instance takes a two-car roster. The ticket fails if agv01 in the
    // roster passes the check, or if the Host binds a roster other than the definition's without an
    // error -- so the event must carry the roster, and the installer's read-back must read it.
    // ------------------------------------------------------------------------------------------------

    private const string Agv01Key = "BROKERX-0c20ff0600d644869a6a80c186065d85";
    private const string Agv02Key = "BROKERX-f38975561adf46ccb1d2f23833c7d0e4";
    private const string Agv03Key = "BROKERX-7daca4ee91da498d8026c68b7b941127";
    private const string TwoCarExample = "instance-factory01-v2.two-car-example.json";

    [Fact]
    public void TwoCarExampleOverPackageAppSettingsBindsBothRowsWholeWithAgv02Primary()
    {
        string root = FindRepositoryRoot();
        JourneyRuntimeOptions options = Bind(
            File.ReadAllText(Path.Combine(root, "src", "ControlServer.Host", "appsettings.json")),
            GenerateOverlay(root, TwoCarExample), out _);

        Assert.Equal([Agv02Key, Agv03Key], options.Fleet.Select(vehicle => vehicle.VehicleKey));
        Assert.Equal(["老厂前线新多仓位2", "老厂前线新多仓位3"], options.Fleet.Select(vehicle => vehicle.AgvId));
        string[] six = ["DIE_TO_WIRE_STAGING", "DIE_TO_OVEN", "WIRE_TO_GATE", "WIRE_TO_OPTICAL", "STAGING_TO_WIRE", "WIRE_TO_NITROGEN"];
        Assert.All(options.Fleet, vehicle => Assert.Equal(six, vehicle.AllowedTaskTypes));
        Assert.All(options.Fleet, vehicle => Assert.Equal(["WIRE"], vehicle.Zones));
        // The package's appsettings.json names agv01 as the primary pair; the Host requires the roster to contain the
        // primary pair, so the overlay must have replaced it with a roster row.
        Assert.Equal(Agv02Key, options.VehicleKey);
        Assert.Equal("老厂前线新多仓位2", options.AgvId);
        Assert.DoesNotContain(options.Fleet, vehicle => vehicle.VehicleKey == Agv01Key);
    }

    [Fact]
    public void EffectiveConfigurationEventCarriesEachRosterVehicleWithExactlyFourFields()
    {
        JourneyRuntimeOptions options = new()
        {
            AllowedWorkTypes = ["STAGING_TO_WIRE", "WIRE_TO_GATE"],
            AllowedDispatchZones = ["WIRE"],
            Fleet =
            [
                new FleetVehicleOptions
                {
                    AgvId = "老厂前线新多仓位2", VehicleKey = Agv02Key, AgvLifecycleGeneration = 7,
                    AllowedTaskTypes = ["STAGING_TO_WIRE"], Zones = ["WIRE"], RoundTimeoutMilliseconds = 12345,
                },
                new FleetVehicleOptions
                {
                    AgvId = "老厂前线新多仓位3", VehicleKey = Agv03Key, AgvLifecycleGeneration = 8,
                    AllowedTaskTypes = ["WIRE_TO_GATE", "STAGING_TO_WIRE"], Zones = ["WIRE"],
                },
            ],
        };

        using JsonDocument logged = JsonDocument.Parse(RenderEvent(options, "http://127.0.0.1:58188"));
        JsonElement root = logged.RootElement;

        // The fields #535 reads, unchanged: same names, same event id, template still starting the same way.
        Assert.StartsWith(
            "EFFECTIVE_CONFIGURATION allowedWorkTypes={AllowedWorkTypes} allowedDispatchZones={AllowedDispatchZones} mesIngestBaseUrl={MesIngestBaseUrl}",
            root.GetProperty("@mt").GetString(), StringComparison.Ordinal);
        Assert.Equal(5350, root.GetProperty("EventId").GetProperty("Id").GetInt32());
        Assert.Equal(["STAGING_TO_WIRE", "WIRE_TO_GATE"], root.GetProperty("AllowedWorkTypes").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(["WIRE"], root.GetProperty("AllowedDispatchZones").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal("http://127.0.0.1:58188", root.GetProperty("MesIngestBaseUrl").GetString());

        JsonElement[] fleet = [.. root.GetProperty("Fleet").EnumerateArray()];
        Assert.Equal(2, fleet.Length);
        // Exactly these four, in this order: nothing else a roster row carries reaches the log.
        Assert.All(fleet, vehicle => Assert.Equal(
            ["AgvId", "VehicleKey", "AllowedTaskTypes", "Zones"], vehicle.EnumerateObject().Select(property => property.Name)));
        Assert.Equal("老厂前线新多仓位2", fleet[0].GetProperty("AgvId").GetString());
        Assert.Equal(Agv03Key, fleet[1].GetProperty("VehicleKey").GetString());
        Assert.Equal(["WIRE_TO_GATE", "STAGING_TO_WIRE"], fleet[1].GetProperty("AllowedTaskTypes").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(["WIRE"], fleet[0].GetProperty("Zones").EnumerateArray().Select(item => item.GetString()));
    }

    [Fact]
    public void EffectiveConfigurationEventOfASingleVehicleDeploymentCarriesAnEmptyRoster()
    {
        JourneyRuntimeOptions options = new() { AgvId = "老厂前线新多仓位2", VehicleKey = Agv02Key, AllowedWorkTypes = ["WIRE_TO_GATE"], AllowedDispatchZones = ["WIRE"] };

        using JsonDocument logged = JsonDocument.Parse(RenderEvent(options, "http://127.0.0.1:58188"));

        Assert.Equal(JsonValueKind.Array, logged.RootElement.GetProperty("Fleet").ValueKind);
        Assert.Equal(0, logged.RootElement.GetProperty("Fleet").GetArrayLength());
    }

    [Theory]
    [InlineData(TwoCarExample, false, "Pass")]
    [InlineData(TwoCarExample, true, "StopServiceAndRefuse")]
    [InlineData("instance-factory01-v2.json", false, "Pass")]
    public void InstallerReadBackJudgesTheEventTheHostReallyLogs(string definitionFile, bool dropLastVehicle, string expectedAction)
    {
        // End to end across the two sides: the definition through the installer's overlay, bound by the Host's own
        // registration over the package's appsettings.json, logged by the Host's own event, read back and judged by
        // the installer's module. dropLastVehicle stands for a Host that bound one car fewer than defined.
        string root = FindRepositoryRoot();
        JourneyRuntimeOptions options = Bind(
            File.ReadAllText(Path.Combine(root, "src", "ControlServer.Host", "appsettings.json")),
            GenerateOverlay(root, definitionFile), out IConfiguration configuration);
        if (dropLastVehicle)
        {
            options.Fleet = options.Fleet[..^1];
        }
        string line = RenderEvent(options, configuration["MesIngest:baseUrl"]!);

        string verdict = JudgeReadBack(root, definitionFile, line);

        Assert.StartsWith(expectedAction + "|", verdict, StringComparison.Ordinal);
    }

    [Fact]
    public void InstallerModuleRefusesTheTwoCarExampleWithAgv01InARow()
    {
        string root = FindRepositoryRoot();
        string parallel = Path.Combine(root, "scripts", "parallel");
        string output = RunPwsh(
            $"Import-Module '{Path.Combine(parallel, "ParallelInstance.psm1")}' -Force; " +
            $"$d = Read-ParallelInstanceDefinition -Path '{Path.Combine(parallel, TwoCarExample)}'; " +
            $"$d['journeyRuntime']['fleet'][1] = @{{ agvId = '老厂前线新多仓位1'; vehicleKey = '{Agv01Key}'; deviceKey = '{Agv01Key}'; riotId = 58; " +
            "agvLifecycleGeneration = 1; allowedTaskTypes = @('WIRE_TO_GATE'); zones = @('WIRE') }; " +
            "try { $null = Assert-ParallelInstanceDefinition -Definition $d; 'ACCEPTED' } catch { 'REFUSED: ' + $_.Exception.Message }",
            allowFailure: false);

        // Refused for the agv01 row, not for any reason: a check that crashed would also throw (PR #575 review, item 2).
        Assert.StartsWith("REFUSED: ", output.Trim(), StringComparison.Ordinal);
        Assert.Contains("journeyRuntime.fleet[1] names agv01", output, StringComparison.Ordinal);
    }

    private static string RenderEvent(JourneyRuntimeOptions options, string mesIngestBaseUrl)
    {
        using StringWriter writer = new();
        using (Serilog.Core.Logger serilog = new Serilog.LoggerConfiguration()
                   .WriteTo.Sink(new CompactJsonSink(writer))
                   .CreateLogger())
        using (Serilog.Extensions.Logging.SerilogLoggerFactory factory = new(serilog))
        {
            EffectiveConfigurationEvent.Log(factory.CreateLogger("ControlServer.Host"), options, mesIngestBaseUrl);
        }
        return writer.ToString().Trim();
    }

    /// <summary>The Host's formatter (Install-ControlServerLocal.ps1 configures CompactJsonFormatter), into a string.</summary>
    private sealed class CompactJsonSink(TextWriter writer) : Serilog.Core.ILogEventSink
    {
        private readonly Serilog.Formatting.Compact.CompactJsonFormatter _formatter = new();

        public void Emit(Serilog.Events.LogEvent logEvent) => _formatter.Format(logEvent, writer);
    }

    private static string JudgeReadBack(string root, string definitionFile, string line)
    {
        string parallel = Path.Combine(root, "scripts", "parallel");
        string log = Path.Combine(Path.GetTempPath(), $"cs571-effective-{Guid.NewGuid():N}.log");
        File.WriteAllText(log, line + Environment.NewLine, new UTF8Encoding(false));
        try
        {
            return RunPwsh(
                $"Import-Module '{Path.Combine(parallel, "ParallelInstance.psm1")}' -Force; " +
                $"$d = Read-ParallelInstanceDefinition -Path '{Path.Combine(parallel, definitionFile)}'; " +
                $"$e = Find-ParallelEffectiveConfiguration -Lines @(Get-Content -LiteralPath '{log}' -Encoding utf8) -Since ([datetimeoffset]::MinValue); " +
                "$a = Get-ParallelEffectiveConfigurationAction -Definition $d -Effective $e; \"$($a.Action)|$($a.Message)\"",
                allowFailure: false).Trim();
        }
        finally
        {
            File.Delete(log);
        }
    }

    private static string RunPwsh(string script, bool allowFailure)
    {
        ProcessStartInfo start = new("pwsh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string argument in new[]
                 {
                     "-NoProfile", "-NonInteractive", "-Command",
                     "$ErrorActionPreference = 'Stop'; [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false); " + script,
                 })
        {
            start.ArgumentList.Add(argument);
        }
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("pwsh did not start.");
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(allowFailure || process.ExitCode == 0, $"pwsh failed (exit {process.ExitCode}): {stdout}{stderr}");
        return stdout;
    }

    private static JourneyRuntimeOptions Bind(string baseJson, string overlayJson, out IConfiguration configuration)
    {
        IConfigurationRoot root = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(baseJson)))
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(overlayJson)))
            .Build();
        configuration = root;
        ServiceCollection services = new();
        services.AddJourneyRuntimeOptions(root);
        using ServiceProvider provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<JourneyRuntimeOptions>>().Value;
    }

    /// <summary>
    /// The overlay exactly as the installer writes it: New-ParallelInstanceConfigurationOverlay on the
    /// shipped definition, through pwsh, saved as UTF-8 without a BOM.
    /// </summary>
    private static string GenerateOverlay(string root, string definitionFile)
    {
        string parallel = Path.Combine(root, "scripts", "parallel");
        string output = Path.Combine(Path.GetTempPath(), $"cs535-overlay-{Guid.NewGuid():N}.json");
        string command =
            $"$ErrorActionPreference = 'Stop'; Import-Module '{Path.Combine(parallel, "ParallelInstance.psm1")}' -Force; " +
            $"$d = Read-ParallelInstanceDefinition -Path '{Path.Combine(parallel, definitionFile)}'; " +
            $"$null = Assert-ParallelInstanceDefinition -Definition $d; " +
            $"New-ParallelInstanceConfigurationOverlay -Definition $d | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath '{output}' -Encoding utf8NoBOM";
        ProcessStartInfo start = new("pwsh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-Command", command })
        {
            start.ArgumentList.Add(argument);
        }
        try
        {
            using Process process = Process.Start(start) ?? throw new InvalidOperationException("pwsh did not start.");
            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, $"generating the overlay failed (exit {process.ExitCode}): {stdout}{stderr}");
            return File.ReadAllText(output);
        }
        finally
        {
            File.Delete(output);
        }
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ControlServer.sln")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName
            ?? throw new InvalidOperationException("Could not find the repository root from the test binary.");
    }
}
