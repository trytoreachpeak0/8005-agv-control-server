using System.Diagnostics;
using System.Text;
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
