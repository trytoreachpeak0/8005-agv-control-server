using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ControlServer.Domain;
using ControlServer.Host.Composition;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.CreateGate;
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
        { "JourneyRuntime": { "allowedWorkTypes": ["DIE_TO_WIRE_STAGING", "DIE_TO_OVEN", "WIRE_TO_GATE", "WIRE_TO_OPTICAL", "STAGING_TO_WIRE", "WIRE_TO_NITROGEN", "WIRE_TO_FULL_INSPECTION"],
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
    public void FakeDefinitionOverPackageAppSettingsBindsItsOwnSevenWorkTypes()
    {
        string root = FindRepositoryRoot();
        string overlay = GenerateOverlay(root, "instance-factory01-v2.json");
        JourneyRuntimeOptions options = Bind(
            File.ReadAllText(Path.Combine(root, "src", "ControlServer.Host", "appsettings.json")), overlay, out IConfiguration configuration);

        Assert.Equal(
            ["DIE_TO_WIRE_STAGING", "DIE_TO_OVEN", "WIRE_TO_GATE", "WIRE_TO_OPTICAL", "STAGING_TO_WIRE", "WIRE_TO_NITROGEN", "WIRE_TO_FULL_INSPECTION"],
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
    // control-server#578. The roster is an array of objects, each holding two arrays of its own, and an
    // index merge leaves an earlier layer's extra cars and extra task types in force. Per-vehicle task
    // types are exactly what keeps each car to its own work, so the roster is taken whole too.
    // ------------------------------------------------------------------------------------------------

    private const string TwoCarRoster = """
        { "JourneyRuntime": { "fleet": [
            { "agvId": "A2", "vehicleKey": "K2", "agvLifecycleGeneration": 1, "roundTimeoutMilliseconds": 45000,
              "allowedTaskTypes": ["STAGING_TO_WIRE", "WIRE_TO_GATE"], "zones": ["WIRE", "OVEN"] },
            { "agvId": "A3", "vehicleKey": "K3", "agvLifecycleGeneration": 1,
              "allowedTaskTypes": ["DIE_TO_OVEN"], "zones": ["OVEN"] } ] } }
        """;

    [Fact]
    public void ShorterRosterInLaterLayerReplacesEarlierRosterWholeDownToEachVehiclesLists()
    {
        JourneyRuntimeOptions options = Bind(TwoCarRoster, """
            { "JourneyRuntime": { "fleet": [
                { "agvId": "A2", "vehicleKey": "K2", "agvLifecycleGeneration": 1,
                  "allowedTaskTypes": ["STAGING_TO_WIRE"], "zones": ["WIRE"] } ] } }
            """, out _);

        FleetVehicleOptions vehicle = Assert.Single(options.Fleet);
        Assert.Equal("K2", vehicle.VehicleKey);
        Assert.Equal(["STAGING_TO_WIRE"], vehicle.AllowedTaskTypes);
        Assert.Equal(["WIRE"], vehicle.Zones);
        // Taken whole: a field the later layer leaves out is the default, not the earlier layer's value.
        Assert.Equal(30_000, vehicle.RoundTimeoutMilliseconds);
    }

    [Fact]
    public void SameSizedRosterInLaterLayerKeepsNoneOfAVehiclesEarlierTaskTypesOrZones()
    {
        JourneyRuntimeOptions options = Bind(TwoCarRoster, """
            { "JourneyRuntime": { "fleet": [
                { "agvId": "A2", "vehicleKey": "K2", "agvLifecycleGeneration": 1,
                  "allowedTaskTypes": ["STAGING_TO_WIRE"], "zones": ["WIRE"] },
                { "agvId": "A3", "vehicleKey": "K3", "agvLifecycleGeneration": 1,
                  "allowedTaskTypes": ["DIE_TO_OVEN"], "zones": ["OVEN"] } ] } }
            """, out _);

        Assert.Equal(["STAGING_TO_WIRE"], options.Fleet[0].AllowedTaskTypes);
        Assert.Equal(["WIRE"], options.Fleet[0].Zones);
    }

    [Fact]
    public void LaterLayerThatDoesNotNameRosterLeavesEarlierRosterAlone()
    {
        JourneyRuntimeOptions options = Bind(TwoCarRoster, """{ "JourneyRuntime": { "allowedWorkTypes": ["STAGING_TO_WIRE"] } }""", out _);

        Assert.Equal(["K2", "K3"], options.Fleet.Select(vehicle => vehicle.VehicleKey));
        Assert.Equal(["STAGING_TO_WIRE", "WIRE_TO_GATE"], options.Fleet[0].AllowedTaskTypes);
        Assert.Equal(45_000, options.Fleet[0].RoundTimeoutMilliseconds);
    }

    [Fact]
    public void RosterInsideAChainedConfigurationIsStillTakenWholeFromItsLastLayer()
    {
        IConfigurationRoot inner = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(TwoCarRoster)))
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(
                """{ "JourneyRuntime": { "fleet": [ { "agvId": "A3", "vehicleKey": "K3", "allowedTaskTypes": ["DIE_TO_WIRE_STAGING"] } ] } }""")))
            .Build();
        using ConfigurationManager outer = new();
        outer.AddConfiguration(inner);
        ServiceCollection services = new();
        services.AddJourneyRuntimeOptions(outer);
        using ServiceProvider provider = services.BuildServiceProvider();

        FleetVehicleOptions vehicle = Assert.Single(provider.GetRequiredService<IOptions<JourneyRuntimeOptions>>().Value.Fleet);
        Assert.Equal(["DIE_TO_WIRE_STAGING"], vehicle.AllowedTaskTypes);
        Assert.Empty(vehicle.Zones);
    }

    [Fact]
    public void RosterFromEnvironmentVariablesOverAFileRosterIsTakenWhole()
    {
        // The L2 rig and any field override write the roster as JourneyRuntime__Fleet__N__... variables. The real
        // environment-variable provider, under a prefix of this test's own so that parallel tests never see the variables.
        string prefix = $"CS578_{Guid.NewGuid():N}_";
        Dictionary<string, string> variables = new()
        {
            [$"{prefix}JourneyRuntime__Fleet__0__AgvId"] = "A3",
            [$"{prefix}JourneyRuntime__Fleet__0__VehicleKey"] = "K3",
            [$"{prefix}JourneyRuntime__Fleet__0__AllowedTaskTypes__0"] = "DIE_TO_OVEN",
        };
        try
        {
            foreach ((string name, string value) in variables)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
            IConfigurationRoot root = new ConfigurationBuilder()
                .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(TwoCarRoster)))
                .AddEnvironmentVariables(prefix)
                .Build();
            ServiceCollection services = new();
            services.AddJourneyRuntimeOptions(root);
            using ServiceProvider provider = services.BuildServiceProvider();

            FleetVehicleOptions vehicle = Assert.Single(provider.GetRequiredService<IOptions<JourneyRuntimeOptions>>().Value.Fleet);
            Assert.Equal("K3", vehicle.VehicleKey);
            Assert.Equal(["DIE_TO_OVEN"], vehicle.AllowedTaskTypes);
        }
        finally
        {
            foreach (string name in variables.Keys)
            {
                Environment.SetEnvironmentVariable(name, null);
            }
        }
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
        // The two spare cars keep the six: WIRE_TO_FULL_INSPECTION is agv01's alone (control-server#737, program#195 decision 9).
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
        string line = RenderEvent(options, configuration["MesIngest:baseUrl"]!, BindCatalog(configuration));

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

    // ------------------------------------------------------------------------------------------------
    // control-server#669. The cutover definition: two dispatch zones, three cars dedicated by task type.
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// What the Host binds from each cutover definition (the gated one and its runtime-on twin) over the package's
    /// appsettings.json, judged by the Host's own options validator with the runtime on, and the zone-to-car table the
    /// Host would write into the new database from it
    /// (<c>DispatchZoneVehicles</c>, which the area assignment import reads its zones from).
    /// </summary>
    /// <remarks>
    /// The overlay is generated without the definition check: that check refuses agv01 and WIRE_TO_GATE from the production
    /// MesIngest until the cutover switch (control-server#564) opens them, and Test-ParallelInstance.ps1 pins that exact
    /// refusal. The validator runs with <c>Enabled</c> set, because it checks zones only for a server that dispatches; the
    /// two secrets it requires come from the machine's environment and are left out of the judgement.
    /// </remarks>
    [Theory]
    [InlineData("instance-factory01-v2.cutover-two-zones.json", false)]
    [InlineData("instance-factory01-v2.cutover-two-zones.runtime-on.json", true)]
    public void CutoverDefinitionBindsTwoZonesAndThreeDedicatedCarsTheHostAccepts(string definitionFile, bool runtimeEnabled)
    {
        string root = FindRepositoryRoot();
        JourneyRuntimeOptions options = Bind(
            File.ReadAllText(Path.Combine(root, "src", "ControlServer.Host", "appsettings.json")),
            GenerateOverlay(root, definitionFile, checkDefinition: false), out IConfiguration configuration);

        Assert.Equal(["WIRE_BONDING", "DIE_ATTACH"], options.AllowedDispatchZones);
        Assert.Equal("WIRE_BONDING", options.DispatchZone);
        Assert.Equal([Agv01Key, Agv02Key, Agv03Key], options.Fleet.Select(vehicle => vehicle.VehicleKey));
        // control-server#737: agv01 takes four, the other two cars are unchanged.
        Assert.Equal(["WIRE_TO_GATE", "WIRE_TO_OPTICAL", "WIRE_TO_NITROGEN", "WIRE_TO_FULL_INSPECTION"], options.Fleet[0].AllowedTaskTypes);
        Assert.Equal(["STAGING_TO_WIRE"], options.Fleet[1].AllowedTaskTypes);
        Assert.Equal(["DIE_TO_OVEN", "DIE_TO_WIRE_STAGING"], options.Fleet[2].AllowedTaskTypes);
        Assert.Equal(["WIRE_BONDING", "WIRE_BONDING", "DIE_ATTACH"], options.Fleet.Select(vehicle => string.Join(",", vehicle.Zones)));

        // The production waits the user set on 2026-10-13 (D9 in control-server#564's comment), not the package's or a test period's.
        Assert.Equal(TimeSpan.FromMinutes(5), options.StationDepartureWaitTimeout);
        Assert.Equal(TimeSpan.FromMinutes(5), options.CargoHoldingTimeout);
        Assert.Equal(runtimeEnabled, options.Enabled);
        options.Enabled = true;
        ValidateOptionsResult result = new JourneyRuntimeOptionsValidator(configuration).Validate(null, options);
        string[] failures =
        [
            .. (result.Failures ?? []).Where(failure =>
                !failure.EndsWith("must name a populated external environment variable.", StringComparison.Ordinal)),
        ];
        Assert.True(failures.Length == 0, string.Join(Environment.NewLine, failures));

        ControlServer.Application.VehicleDispatchPolicy policy =
            new ControlServer.Host.Runtime.Fleet.VehicleDispatchPolicyAccess(null!, Options.Create(options), TimeProvider.System)
                .FromConfiguration();
        Assert.Equal(["DIE_ATTACH", "WIRE_BONDING"], policy.ZoneVehicles.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(["老厂前线新多仓位1", "老厂前线新多仓位2"], policy.ZoneVehicles["WIRE_BONDING"].Order(StringComparer.Ordinal));
        Assert.Equal(["老厂前线新多仓位3"], policy.ZoneVehicles["DIE_ATTACH"]);
    }

    // ------------------------------------------------------------------------------------------------
    // control-server#638. JourneyRuntime:ClearanceToWaitingPointEnabled (REQ-0178) on the parallel instance: the definition's
    // journeyRuntime.clearanceToWaitingPointEnabled through the overlay, bound by the Host, logged in its event, judged.
    // ------------------------------------------------------------------------------------------------

    private const string ClearanceOn = "$d['journeyRuntime']['clearanceToWaitingPointEnabled'] = $true; ";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EffectiveConfigurationEventCarriesTheClearanceSwitchAsABoolean(bool enabled)
    {
        JourneyRuntimeOptions options = new()
        {
            AgvId = "老厂前线新多仓位2",
            VehicleKey = Agv02Key,
            AllowedWorkTypes = ["WIRE_TO_GATE"],
            AllowedDispatchZones = ["WIRE"],
            ClearanceToWaitingPointEnabled = enabled,
        };

        using JsonDocument logged = JsonDocument.Parse(RenderEvent(options, "http://127.0.0.1:58188"));

        Assert.Contains(" clearanceToWaitingPointEnabled={ClearanceToWaitingPointEnabled}", logged.RootElement.GetProperty("@mt").GetString(), StringComparison.Ordinal);
        Assert.Equal(enabled ? JsonValueKind.True : JsonValueKind.False, logged.RootElement.GetProperty("ClearanceToWaitingPointEnabled").ValueKind);
    }

    [Theory]
    [InlineData("instance-factory01-v2.json", "", false)]
    [InlineData("instance-factory01-v2.production-mes.json", "", false)]
    [InlineData("instance-factory01-v2.json", ClearanceOn, true)]
    public void TheClearanceSwitchTheDefinitionWritesIsWhatTheHostBinds(string definitionFile, string definitionEdit, bool expected)
    {
        // Over the package's appsettings.json, which does not name the switch: left out of the definition it binds the
        // product default (off), written it binds what was written.
        string root = FindRepositoryRoot();
        JourneyRuntimeOptions options = Bind(
            File.ReadAllText(Path.Combine(root, "src", "ControlServer.Host", "appsettings.json")),
            GenerateOverlay(root, definitionFile, definitionEdit), out _);

        Assert.Equal(expected, options.ClearanceToWaitingPointEnabled);
    }

    [Theory]
    [InlineData(ClearanceOn, false, "Pass")]
    [InlineData(ClearanceOn, true, "StopServiceAndRefuse")]
    [InlineData("", false, "Pass")]
    [InlineData("", true, "StopServiceAndRefuse")]
    public void InstallerReadBackJudgesTheClearanceSwitchTheHostReallyLogs(string definitionEdit, bool hostFlipsIt, string expectedAction)
    {
        // hostFlipsIt stands for a Host that bound the other value than the definition says: written and not in effect, or
        // left over from an earlier layer.
        string root = FindRepositoryRoot();
        const string definitionFile = "instance-factory01-v2.json";
        JourneyRuntimeOptions options = Bind(
            File.ReadAllText(Path.Combine(root, "src", "ControlServer.Host", "appsettings.json")),
            GenerateOverlay(root, definitionFile, definitionEdit), out IConfiguration configuration);
        if (hostFlipsIt)
        {
            options.ClearanceToWaitingPointEnabled = !options.ClearanceToWaitingPointEnabled;
        }
        string line = RenderEvent(options, configuration["MesIngest:baseUrl"]!, BindCatalog(configuration));

        string verdict = JudgeReadBack(root, definitionFile, line, definitionEdit);

        Assert.StartsWith(expectedAction + "|", verdict, StringComparison.Ordinal);
        if (hostFlipsIt)
        {
            Assert.Contains("clearanceToWaitingPointEnabled bound", verdict, StringComparison.Ordinal);
        }
    }

    // ------------------------------------------------------------------------------------------------
    // control-server#531. REQ-0302's two approved values on the parallel instance: the definition's mapStationCatalog through
    // the overlay -- merged into an appsettings.Production.json that may carry the 10-09..10-12 field hand edit -- bound by
    // the Host's own registration and validator, logged in its event, judged by the installer.
    // ------------------------------------------------------------------------------------------------

    private const string CatalogLeftOut = "$d.Remove('mapStationCatalog'); ";
    private const string CatalogFieldHandEdit = """
        { "MapStationCatalog": { "ApprovedSyncPeriod": "00:01:00", "ApprovedMaxUnconfirmed": "00:10:00" } }
        """;

    [Theory]
    [InlineData("instance-factory01-v2.json")]
    [InlineData("instance-factory01-v2.production-mes.json")]
    [InlineData(TwoCarExample)]
    [InlineData("instance-factory01-v2.req0178-field-off.json")]
    [InlineData("instance-factory01-v2.req0178-field-on.json")]
    [InlineData("instance-factory01-v2.batch89-auto.json")]
    [InlineData("instance-factory01-v2.batch89-manual.json")]
    public void EveryShippedDefinitionBindsTheApprovedPairOverThePackageAppSettings(string definitionFile)
    {
        string root = FindRepositoryRoot();
        JourneyRuntimeOptions options = Bind(File.ReadAllText(Path.Combine(root, "src", "ControlServer.Host", "appsettings.json")),
            GenerateOverlay(root, definitionFile), out IConfiguration configuration);

        MapStationCatalogOptions catalog = BindCatalog(configuration);

        Assert.True(catalog.IsApproved);
        Assert.Equal(TimeSpan.FromSeconds(30), catalog.ApprovedSyncPeriod);
        Assert.Equal(TimeSpan.FromMinutes(5), catalog.ApprovedMaxUnconfirmed);

        // control-server#609: what the Host binds from the definition through the overlay, not what the definition says --
        // the user's 2026-10-11 ceiling for every deployment (the batch 8/9 drills run shorter).
        Assert.InRange(options.CargoHoldingTimeout, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(5));
    }

    [Theory]
    [InlineData("", true)]
    [InlineData(CatalogLeftOut, false)]
    public void TheDefinitionWinsOverTheFieldHandEditInTheSameFile(string definitionEdit, bool approved)
    {
        // The upgrade path: the installed appsettings.Production.json already carries the hand edit (other values, PascalCase),
        // and the installer merges the overlay into that very file (Merge-ConfigurationTree, keys ignoring case). Left out of
        // the definition, the JSON nulls the overlay writes must clear the hand edit -- not leave it bound.
        string root = FindRepositoryRoot();
        string merged = GenerateMergedProductionFile(root, "instance-factory01-v2.json", definitionEdit, CatalogFieldHandEdit);
        Bind(File.ReadAllText(Path.Combine(root, "src", "ControlServer.Host", "appsettings.json")), merged, out IConfiguration configuration);

        MapStationCatalogOptions catalog = BindCatalog(configuration);

        Assert.Equal(approved, catalog.IsApproved);
        Assert.Equal(approved ? TimeSpan.FromSeconds(30) : null, catalog.ApprovedSyncPeriod);
        Assert.Equal(approved ? TimeSpan.FromMinutes(5) : null, catalog.ApprovedMaxUnconfirmed);
    }

    [Fact]
    public void EffectiveConfigurationEventCarriesTheCatalogApprovalAsExactlyTwoStrings()
    {
        JourneyRuntimeOptions options = new() { AgvId = "老厂前线新多仓位2", VehicleKey = Agv02Key, AllowedWorkTypes = ["WIRE_TO_GATE"], AllowedDispatchZones = ["WIRE"] };

        using JsonDocument approved = JsonDocument.Parse(RenderEvent(options, "http://127.0.0.1:58188",
            new MapStationCatalogOptions { ApprovedSyncPeriod = TimeSpan.FromSeconds(30), ApprovedMaxUnconfirmed = TimeSpan.FromMinutes(5) }));
        using JsonDocument unset = JsonDocument.Parse(RenderEvent(options, "http://127.0.0.1:58188", new MapStationCatalogOptions()));

        // No longer the last field since control-server#617 appended the release entry.
        Assert.Contains(" mapStationCatalog={MapStationCatalog}", approved.RootElement.GetProperty("@mt").GetString(), StringComparison.Ordinal);
        JsonElement logged = approved.RootElement.GetProperty("MapStationCatalog");
        Assert.Equal(["ApprovedSyncPeriod", "ApprovedMaxUnconfirmed"], logged.EnumerateObject().Select(property => property.Name));
        Assert.Equal("00:00:30", logged.GetProperty("ApprovedSyncPeriod").GetString());
        Assert.Equal("00:05:00", logged.GetProperty("ApprovedMaxUnconfirmed").GetString());
        Assert.Equal(JsonValueKind.Null, unset.RootElement.GetProperty("MapStationCatalog").GetProperty("ApprovedSyncPeriod").ValueKind);
        Assert.Equal(JsonValueKind.Null, unset.RootElement.GetProperty("MapStationCatalog").GetProperty("ApprovedMaxUnconfirmed").ValueKind);
    }

    [Theory]
    [InlineData("", "as-bound", "Pass")]
    [InlineData("", "unset", "StopServiceAndRefuse")]
    [InlineData("", "longer", "StopServiceAndRefuse")]
    [InlineData(CatalogLeftOut, "as-bound", "Pass")]
    [InlineData(CatalogLeftOut, "approved", "StopServiceAndRefuse")]
    public void InstallerReadBackJudgesTheCatalogApprovalTheHostReallyLogs(string definitionEdit, string host, string expectedAction)
    {
        // host stands for what the Host bound: as the overlay gave it, nothing (written and not in effect), another maximum
        // (an environment variable, a stale layer), or the approved pair under a definition that leaves it out (left over).
        string root = FindRepositoryRoot();
        const string definitionFile = "instance-factory01-v2.json";
        JourneyRuntimeOptions options = Bind(
            File.ReadAllText(Path.Combine(root, "src", "ControlServer.Host", "appsettings.json")),
            GenerateOverlay(root, definitionFile, definitionEdit), out IConfiguration configuration);
        MapStationCatalogOptions catalog = host switch
        {
            "as-bound" => BindCatalog(configuration),
            "unset" => new MapStationCatalogOptions(),
            "longer" => new MapStationCatalogOptions { ApprovedSyncPeriod = TimeSpan.FromSeconds(30), ApprovedMaxUnconfirmed = TimeSpan.FromMinutes(10) },
            _ => new MapStationCatalogOptions { ApprovedSyncPeriod = TimeSpan.FromSeconds(30), ApprovedMaxUnconfirmed = TimeSpan.FromMinutes(5) },
        };
        string line = RenderEvent(options, configuration["MesIngest:baseUrl"]!, catalog);

        string verdict = JudgeReadBack(root, definitionFile, line, definitionEdit);

        Assert.StartsWith(expectedAction + "|", verdict, StringComparison.Ordinal);
        if (expectedAction != "Pass")
        {
            Assert.Contains("mapStationCatalog.", verdict, StringComparison.Ordinal);
        }
    }

    // ------------------------------------------------------------------------------------------------
    // control-server#672 (PR #670 review L3, extended from cs#609). After an install nothing said which admission policy
    // version and deployment label the Host bound -- only the overlay file on disk, or AdmissionPolicyState in the database
    // -- nor which cargo holding and station departure waits: on 2026-10-12 a hand edit to five minutes was quietly
    // reinstalled back to thirty (evidence/field/20261012-req0178/52-closeout-server.txt). The event now carries all four.
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public void EffectiveConfigurationEventCarriesTheAdmissionPolicyAndTheTwoWaitsAsBound()
    {
        JourneyRuntimeOptions options = new()
        {
            AgvId = "老厂前线新多仓位2",
            VehicleKey = Agv02Key,
            AllowedWorkTypes = ["WIRE_TO_GATE"],
            AllowedDispatchZones = ["WIRE"],
            AdmissionPolicyVersion = 6,
            AdmissionPolicyDeploymentId = "MAP-26-WIRE_TO_GATE-20261007",
            CargoHoldingTimeout = TimeSpan.FromMinutes(5),
            StationDepartureWaitTimeout = TimeSpan.FromSeconds(270),
        };

        using JsonDocument logged = JsonDocument.Parse(RenderEvent(options, "http://127.0.0.1:58188"));
        JsonElement root = logged.RootElement;
        string template = root.GetProperty("@mt").GetString()!;

        // Appended: the fields #535, #571, #638, #531 and #617 read keep their names and their place.
        Assert.StartsWith(
            "EFFECTIVE_CONFIGURATION allowedWorkTypes={AllowedWorkTypes} allowedDispatchZones={AllowedDispatchZones} mesIngestBaseUrl={MesIngestBaseUrl} fleet={Fleet} clearanceToWaitingPointEnabled={ClearanceToWaitingPointEnabled} mapStationCatalog={MapStationCatalog} foreignRunningOrderRelease={ForeignRunningOrderRelease}",
            template, StringComparison.Ordinal);
        // No longer the last fields since control-server#686 appended the slot configuration gate.
        Assert.Contains(
            " admissionPolicyVersion={AdmissionPolicyVersion} admissionPolicyDeploymentId={AdmissionPolicyDeploymentId} cargoHoldingTimeout={CargoHoldingTimeout} stationDepartureWaitTimeout={StationDepartureWaitTimeout} ",
            template, StringComparison.Ordinal);
        Assert.Equal(JsonValueKind.Number, root.GetProperty("AdmissionPolicyVersion").ValueKind);
        Assert.Equal(6, root.GetProperty("AdmissionPolicyVersion").GetInt64());
        Assert.Equal("MAP-26-WIRE_TO_GATE-20261007", root.GetProperty("AdmissionPolicyDeploymentId").GetString());
        Assert.Equal("00:05:00", root.GetProperty("CargoHoldingTimeout").GetString());
        Assert.Equal("00:04:30", root.GetProperty("StationDepartureWaitTimeout").GetString());
    }

    [Theory]
    [InlineData("as-bound", "Pass", null)]
    [InlineData("version-5", "StopServiceAndRefuse", "admissionPolicyVersion bound '5'")]
    [InlineData("other-label", "StopServiceAndRefuse", "admissionPolicyDeploymentId bound 'MAP-26-WIRE_TO_GATE-OTHER'")]
    [InlineData("cargo-30", "StopServiceAndRefuse", "cargoHoldingTimeout bound '00:30:00'")]
    [InlineData("departure-30", "StopServiceAndRefuse", "stationDepartureWaitTimeout bound '00:30:00'")]
    public void InstallerReadBackJudgesTheAdmissionPolicyAndTheTwoWaitsTheHostReallyLogs(string host, string expectedAction, string? says)
    {
        // as-bound: the shipped definition's overlay over the package's appsettings.json, bound as the Host binds it. The
        // others stand for a Host that bound something else: an environment variable added after the install, a stale
        // layer, or -- cargo-30 -- the value the field found in effect on 2026-10-12.
        string root = FindRepositoryRoot();
        const string definitionFile = "instance-factory01-v2.json";
        JourneyRuntimeOptions options = Bind(
            File.ReadAllText(Path.Combine(root, "src", "ControlServer.Host", "appsettings.json")),
            GenerateOverlay(root, definitionFile), out IConfiguration configuration);
        switch (host)
        {
            case "version-5": options.AdmissionPolicyVersion = 5; break;
            case "other-label": options.AdmissionPolicyDeploymentId = "MAP-26-WIRE_TO_GATE-OTHER"; break;
            case "cargo-30": options.CargoHoldingTimeout = TimeSpan.FromMinutes(30); break;
            case "departure-30": options.StationDepartureWaitTimeout = TimeSpan.FromMinutes(30); break;
        }
        string line = RenderEvent(options, configuration["MesIngest:baseUrl"]!, BindCatalog(configuration));

        string verdict = JudgeReadBack(root, definitionFile, line);

        Assert.StartsWith(expectedAction + "|", verdict, StringComparison.Ordinal);
        if (says is not null)
        {
            Assert.Contains("EFFECTIVE_CONFIGURATION_MISMATCH", verdict, StringComparison.Ordinal);
            Assert.Contains(says, verdict, StringComparison.Ordinal);
        }
    }

    // ------------------------------------------------------------------------------------------------
    // control-server#686. The slot configuration readiness gate is derived from the installed definition's
    // productionCutover.phase by the overlay, never written by hand: production Enforcing, the trial and every other
    // definition Off. The Host logs the mode it really bound, and the installer compares it, so neither a stale layer nor an
    // environment variable can leave production running without the gate or the trial stopped by it.
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public void EffectiveConfigurationEventCarriesTheSlotConfigurationGateAsBound()
    {
        JourneyRuntimeOptions options = new()
        {
            AgvId = "老厂前线新多仓位2",
            VehicleKey = Agv02Key,
            AllowedWorkTypes = ["WIRE_TO_GATE"],
            AllowedDispatchZones = ["WIRE"],
        };

        using JsonDocument logged = JsonDocument.Parse(
            RenderEvent(options, "http://127.0.0.1:58188", gate: SlotConfigurationGateMode.Enforcing));
        JsonElement root = logged.RootElement;

        // control-server#709 appended its two keys after the gate; the gate still follows the waits.
        Assert.Contains(
            " stationDepartureWaitTimeout={StationDepartureWaitTimeout} slotConfigurationReadinessGate={SlotConfigurationReadinessGate}",
            root.GetProperty("@mt").GetString()!, StringComparison.Ordinal);
        Assert.Equal(JsonValueKind.String, root.GetProperty("SlotConfigurationReadinessGate").ValueKind);
        Assert.Equal("Enforcing", root.GetProperty("SlotConfigurationReadinessGate").GetString());
    }

    /// <summary>
    /// control-server#709 (independent review L5): the staging rules switch and its door alarm are logged as bound, so the
    /// changeover can read back whether the switch really took. Off and five minutes when nothing configures them.
    /// </summary>
    [Fact]
    public void EffectiveConfigurationEventCarriesTheStagingDepartureRulesAsBound()
    {
        JourneyRuntimeOptions options = new()
        {
            AgvId = "老厂前线新多仓位2",
            VehicleKey = Agv02Key,
            AllowedWorkTypes = ["STAGING_TO_WIRE"],
            AllowedDispatchZones = ["WIRE"],
        };

        using (JsonDocument shipped = JsonDocument.Parse(RenderEvent(options, "http://127.0.0.1:58188")))
        {
            Assert.EndsWith(
                " stagingDepartureRulesEnabled={StagingDepartureRulesEnabled} stagingLoadNotSettledAlarmAfter={StagingLoadNotSettledAlarmAfter}",
                shipped.RootElement.GetProperty("@mt").GetString()!, StringComparison.Ordinal);
            Assert.Equal(JsonValueKind.False, shipped.RootElement.GetProperty("StagingDepartureRulesEnabled").ValueKind);
            Assert.Equal("00:05:00", shipped.RootElement.GetProperty("StagingLoadNotSettledAlarmAfter").GetString());
        }

        options.StagingDepartureRules.Enabled = true;
        options.StagingDepartureRules.LoadNotSettledAlarmAfter = TimeSpan.FromMinutes(3);
        using JsonDocument on = JsonDocument.Parse(RenderEvent(options, "http://127.0.0.1:58188"));
        Assert.Equal(JsonValueKind.True, on.RootElement.GetProperty("StagingDepartureRulesEnabled").ValueKind);
        Assert.Equal("00:03:00", on.RootElement.GetProperty("StagingLoadNotSettledAlarmAfter").GetString());
    }

    [Theory]
    [InlineData("production", "as-bound", "Enforcing", "Pass", null)]
    [InlineData("production", "off", "Enforcing", "StopServiceAndRefuse", "slotConfigurationReadinessGate bound 'Off', the definition says 'Enforcing'")]
    [InlineData("trial", "as-bound", "Off", "Pass", null)]
    [InlineData("trial", "enforcing", "Off", "StopServiceAndRefuse", "slotConfigurationReadinessGate bound 'Enforcing', the definition says 'Off'")]
    [InlineData("shipped", "as-bound", "Off", "Pass", null)]
    public void TheGateTheOverlayWritesIsThePhasesAndTheReadBackHoldsTheHostToIt(
        string phase, string host, string expectedBound, string expectedAction, string? says)
    {
        // as-bound: the overlay generated from the definition over the package's appsettings.json, the mode resolved as
        // GovernanceModule resolves it at startup. The others stand for a Host that bound something else.
        string root = FindRepositoryRoot();
        string definitionFile = phase == "shipped" ? "instance-factory01-v2.json" : "instance-factory01-v2.cutover-trial-s1.json";
        string definitionEdit = phase == "production" ? "$d['productionCutover']['phase'] = 'production'; " : "";
        JourneyRuntimeOptions options = Bind(
            File.ReadAllText(Path.Combine(root, "src", "ControlServer.Host", "appsettings.json")),
            GenerateOverlay(root, definitionFile, definitionEdit, checkDefinition: phase == "shipped"),
            out IConfiguration configuration);
        SlotConfigurationGateMode bound = GovernanceModule.ResolveGateMode(configuration);
        Assert.Equal(expectedBound, bound.ToString());
        bound = host switch
        {
            "off" => SlotConfigurationGateMode.Off,
            "enforcing" => SlotConfigurationGateMode.Enforcing,
            _ => bound,
        };
        string line = RenderEvent(options, configuration["MesIngest:baseUrl"]!, BindCatalog(configuration), bound);

        string verdict = JudgeReadBack(root, definitionFile, line, definitionEdit);

        Assert.StartsWith(expectedAction + "|", verdict, StringComparison.Ordinal);
        if (says is not null)
        {
            Assert.Contains("EFFECTIVE_CONFIGURATION_MISMATCH", verdict, StringComparison.Ordinal);
            Assert.Contains(says, verdict, StringComparison.Ordinal);
        }
    }

    // ------------------------------------------------------------------------------------------------
    // control-server#587. The Host's log now rolls at its size limit. The installer's read-back looks for
    // EFFECTIVE_CONFIGURATION and 2001, logged as the process starts, and used to read only the two newest files:
    // once the log rolled more than once after the start, both events were in a file it did not read.
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task InstallerReadBackFindsTheEffectiveConfigurationAnd2001AfterTheLogRolledManyTimes()
    {
        string root = FindRepositoryRoot();
        const string definitionFile = "instance-factory01-v2.json";
        JourneyRuntimeOptions options = Bind(
            File.ReadAllText(Path.Combine(root, "src", "ControlServer.Host", "appsettings.json")), GenerateOverlay(root, definitionFile), out IConfiguration bound);
        string directory = ServerLogVolumeTests.NewDirectory();
        try
        {
            // As the installer takes it: the service process's start, less two seconds.
            DateTimeOffset since = DateTimeOffset.UtcNow.AddSeconds(-2);
            using (Serilog.Core.Logger serilog = ServerLogVolumeTests.CreateInstalledFileLogger(directory, fileSizeLimitBytes: 4096, rollOnFileSizeLimit: true))
            using (Serilog.Extensions.Logging.SerilogLoggerFactory factory = new(serilog))
            {
                EffectiveConfigurationEvent.Log(factory.CreateLogger("ControlServer.Host"), options, BindCatalog(bound), bound["MesIngest:baseUrl"] ?? "http://127.0.0.1:5088");
                // The real 2001: the worker with its runtime held off, as the installer's Held phase starts it.
                using JourneyMutationGate gate = new();
                JourneyRuntimeWorker worker = new(null!, gate, Options.Create(new JourneyRuntimeOptions { Enabled = false }),
                    TimeProvider.System, new Microsoft.Extensions.Logging.Logger<JourneyRuntimeWorker>(factory));
                await worker.StartAsync(TestContext.Current.CancellationToken);
                await worker.StopAsync(TestContext.Current.CancellationToken);
                Microsoft.Extensions.Logging.ILogger noise = factory.CreateLogger("ControlServer.Tests.Noise");
                for (int index = 0; index < 150; index++)
                {
                    LogNoise(noise, index, new string('x', 100), null);
                }
            }
            Assert.True(ServerLogVolumeTests.LogFilesNewestFirst(directory).Length >= 6, "the log should have rolled several times");

            string parallel = Path.Combine(root, "scripts", "parallel");
            string verdict = RunPwsh(
                $"Import-Module '{Path.Combine(parallel, "ParallelInstance.psm1")}' -Force; " +
                $"$d = Read-ParallelInstanceDefinition -Path '{Path.Combine(parallel, definitionFile)}'; " +
                $"$since = [datetimeoffset]::Parse('{since:o}', [cultureinfo]::InvariantCulture); " +
                $"$files = Get-ParallelLogFilesSince -Directory '{directory}' -Filter 'controlserver-*.ndjson' -Since $since; " +
                "$v = Get-ParallelReadBackAction -Phase Held -Definition $d -Lines @(Read-ParallelLogLines -Files $files) -Since $since; " +
                "\"$($v.Action)|$($v.Waiting)|$($v.Message)\"",
                allowFailure: false).Trim();

            Assert.StartsWith("Pass|False|", verdict, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static readonly Action<Microsoft.Extensions.Logging.ILogger, int, string, Exception?> LogNoise =
        Microsoft.Extensions.Logging.LoggerMessage.Define<int, string>(Microsoft.Extensions.Logging.LogLevel.Information, new Microsoft.Extensions.Logging.EventId(1), "noise {Index} {Padding}");

    private static string RenderEvent(
        JourneyRuntimeOptions options,
        string mesIngestBaseUrl,
        MapStationCatalogOptions? catalog = null,
        SlotConfigurationGateMode gate = SlotConfigurationGateMode.Off)
    {
        using StringWriter writer = new();
        using (Serilog.Core.Logger serilog = new Serilog.LoggerConfiguration()
                   .WriteTo.Sink(new CompactJsonSink(writer))
                   .CreateLogger())
        using (Serilog.Extensions.Logging.SerilogLoggerFactory factory = new(serilog))
        {
            EffectiveConfigurationEvent.Log(
                factory.CreateLogger("ControlServer.Host"), options, catalog ?? new MapStationCatalogOptions(), mesIngestBaseUrl,
                slotConfigurationGate: gate);
        }
        return writer.ToString().Trim();
    }

    /// <summary>The Host's formatter (Install-ControlServerLocal.ps1 configures CompactJsonFormatter), into a string.</summary>
    private sealed class CompactJsonSink(TextWriter writer) : Serilog.Core.ILogEventSink
    {
        private readonly Serilog.Formatting.Compact.CompactJsonFormatter _formatter = new();

        public void Emit(Serilog.Events.LogEvent logEvent) => _formatter.Format(logEvent, writer);
    }

    private static string JudgeReadBack(string root, string definitionFile, string line, string definitionEdit = "")
    {
        string parallel = Path.Combine(root, "scripts", "parallel");
        string log = Path.Combine(Path.GetTempPath(), $"cs571-effective-{Guid.NewGuid():N}.log");
        File.WriteAllText(log, line + Environment.NewLine, new UTF8Encoding(false));
        try
        {
            return RunPwsh(
                $"Import-Module '{Path.Combine(parallel, "ParallelInstance.psm1")}' -Force; " +
                $"$d = Read-ParallelInstanceDefinition -Path '{Path.Combine(parallel, definitionFile)}'; {definitionEdit}" +
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
    /// <see cref="MapStationCatalogOptions"/> registered as Program.cs registers it -- bound from its section, with its validator
    /// -- so a configuration the Host would refuse at startup throws here too.
    /// </summary>
    private static MapStationCatalogOptions BindCatalog(IConfiguration configuration)
    {
        ServiceCollection services = new();
        services.AddOptions<MapStationCatalogOptions>().Bind(configuration.GetSection(MapStationCatalogOptions.SectionName));
        services.AddSingleton<IValidateOptions<MapStationCatalogOptions>, MapStationCatalogOptionsValidator>();
        using ServiceProvider provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<MapStationCatalogOptions>>().Value;
    }

    /// <summary>
    /// appsettings.Production.json as the installer leaves it on an upgrade: the overlay merged into the file already
    /// installed (<paramref name="installedJson"/>) with Merge-ConfigurationTree, through pwsh.
    /// </summary>
    private static string GenerateMergedProductionFile(string root, string definitionFile, string definitionEdit, string installedJson)
    {
        string parallel = Path.Combine(root, "scripts", "parallel");
        string installed = Path.Combine(Path.GetTempPath(), $"cs531-installed-{Guid.NewGuid():N}.json");
        string output = Path.Combine(Path.GetTempPath(), $"cs531-merged-{Guid.NewGuid():N}.json");
        File.WriteAllText(installed, installedJson, new UTF8Encoding(false));
        try
        {
            RunPwsh(
                $"Import-Module '{Path.Combine(parallel, "ParallelInstance.psm1")}' -Force; " +
                $"$d = Read-ParallelInstanceDefinition -Path '{Path.Combine(parallel, definitionFile)}'; {definitionEdit}" +
                "$null = Assert-ParallelInstanceDefinition -Definition $d; " +
                $"$base = Get-Content -Raw -LiteralPath '{installed}' -Encoding utf8 | ConvertFrom-Json -AsHashtable -Depth 12; " +
                "Merge-ConfigurationTree -Base $base -Overlay (New-ParallelInstanceConfigurationOverlay -Definition $d) | ConvertTo-Json -Depth 12 | " +
                $"Set-Content -LiteralPath '{output}' -Encoding utf8NoBOM",
                allowFailure: false);
            return File.ReadAllText(output);
        }
        finally
        {
            File.Delete(installed);
            File.Delete(output);
        }
    }

    /// <summary>
    /// The overlay exactly as the installer writes it: New-ParallelInstanceConfigurationOverlay on the
    /// shipped definition, through pwsh, saved as UTF-8 without a BOM. <paramref name="checkDefinition"/> false skips the
    /// definition check, for a definition the installer does not take yet (control-server#669).
    /// </summary>
    private static string GenerateOverlay(string root, string definitionFile, string definitionEdit = "", bool checkDefinition = true)
    {
        string parallel = Path.Combine(root, "scripts", "parallel");
        string output = Path.Combine(Path.GetTempPath(), $"cs535-overlay-{Guid.NewGuid():N}.json");
        string command =
            $"$ErrorActionPreference = 'Stop'; Import-Module '{Path.Combine(parallel, "ParallelInstance.psm1")}' -Force; " +
            $"$d = Read-ParallelInstanceDefinition -Path '{Path.Combine(parallel, definitionFile)}'; {definitionEdit}" +
            (checkDefinition ? "$null = Assert-ParallelInstanceDefinition -Definition $d; " : "") +
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
