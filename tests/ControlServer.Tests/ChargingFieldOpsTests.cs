using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using ControlServer.Application;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using static ControlServer.Tests.ChargingGovernanceHarness;
using static ControlServer.Tests.WaitingPointImportHarness;

namespace ControlServer.Tests;

/// <summary>
/// FieldOps 名册与策略动词的进程入口（control-server#400）：仓里的两份现场名册文件与现场策略文件照说明导入、置空与启用同一个动词、
/// 输出列出进行中的周期、策略三步各自留痕、只读动词以只读模式开库、两个进程同时导入。真起一个 <c>ControlServer.FieldOps.exe</c> 进程，
/// 对同一个真 SQLite 文件。
/// </summary>
public sealed class ChargingFieldOpsTests
{
    private static readonly string FieldOps = typeof(ChargingFieldOpsTests).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(attribute => attribute.Key == "ControlServer.FieldOps.Path").Value!;

    private static string Field(string name) => Path.Combine(RepositoryRoot(), "docs", "field", name);

    /// <summary>
    /// 现场的开窗与关窗照说明走一遍：先预演 211 名册、再导入、关窗导入空名册、再开窗。每一步是同一个动词、各自一个版本；
    /// 仓里的两份现场文件就是导入的那两份。
    /// </summary>
    [Fact]
    public async Task TheShippedRosterFilesOpenAndCloseTheWindowThroughTheSameVerb()
    {
        await using WaitingPointImportHarness harness = await CreateAsync();
        string open = Field("charger-roster-map26-station211.json");
        string close = Field("charger-roster-empty.json");

        (int previewExit, JsonElement preview) = await RunAsync(Import(harness, open, "--dry-run"));
        (int openExit, JsonElement opened) = await RunAsync(Import(harness, open));
        (int closeExit, JsonElement closed) = await RunAsync(Import(harness, close));
        (int againExit, JsonElement closedAgain) = await RunAsync(Import(harness, close));
        (int reopenExit, JsonElement reopened) = await RunAsync(Import(harness, open));
        (int readExit, JsonElement read) = await RunAsync("charger-roster", "--database", harness.DatabasePath);

        Assert.Equal((0, 0, 0, 0, 0, 0), (previewExit, openExit, closeExit, againExit, reopenExit, readExit));
        Assert.Equal(("OK", true, JsonValueKind.Null), (preview.GetProperty("outcome").GetString(), preview.GetProperty("dryRun").GetBoolean(), preview.GetProperty("version").ValueKind));
        Assert.Equal(("OK", 1L, false), (opened.GetProperty("outcome").GetString(), opened.GetProperty("version").GetInt64(), opened.GetProperty("emptyRoster").GetBoolean()));
        Assert.Equal(["211 ADDED"], Changes(opened));
        Assert.Equal(("OK", 2L, true, true), (closed.GetProperty("outcome").GetString(), closed.GetProperty("version").GetInt64(), closed.GetProperty("emptyRoster").GetBoolean(), closed.GetProperty("windowCanClose").GetBoolean()));
        Assert.Equal(["211 REMOVED"], Changes(closed));
        Assert.Equal(("UNCHANGED", 2L), (closedAgain.GetProperty("outcome").GetString(), closedAgain.GetProperty("version").GetInt64()));
        Assert.Equal(("OK", 3L), (reopened.GetProperty("outcome").GetString(), reopened.GetProperty("version").GetInt64()));
        Assert.Equal((3L, false), (read.GetProperty("version").GetInt64(), read.GetProperty("emptyRoster").GetBoolean()));
        JsonElement charger = Assert.Single(read.GetProperty("chargers").EnumerateArray().ToArray());
        Assert.Equal((26, 211, "充电点1", 212, 212), (charger.GetProperty("mapId").GetInt32(), charger.GetProperty("stationId").GetInt32(), charger.GetProperty("stationName").GetString(), charger.GetProperty("entryStationId").GetInt32(), charger.GetProperty("exitStationId").GetInt32()));
        // Versions 1 and 3 each hold station 211; version 2 is empty.
        Assert.Equal((3L, 2L, 0L, 3L, 3L, 1L), await harness.RosterFootprintAsync());
    }

    /// <summary>关窗时还有进行中的周期：输出列出它与桩上的预占，<c>windowCanClose</c> 为假，说明写明窗口还不能关。</summary>
    [Fact]
    public async Task ClosingTheWindowWhileACycleIsUnderWaySaysTheWindowCannotCloseYet()
    {
        await using WaitingPointImportHarness harness = await CreateAsync();
        await RunAsync(Import(harness, Field("charger-roster-map26-station211.json")));
        await harness.StartCycleAsync("C-1", VehicleB, rosterVersion: 1, policyVersion: 1);

        (int exit, JsonElement closed) = await RunAsync(Import(harness, Field("charger-roster-empty.json")));

        Assert.Equal((0, false), (exit, closed.GetProperty("windowCanClose").GetBoolean()));
        Assert.Contains("the window cannot close", closed.GetProperty("windowNote").GetString(), StringComparison.Ordinal);
        JsonElement inProgress = closed.GetProperty("inProgress");
        JsonElement cycle = Assert.Single(inProgress.GetProperty("openCycles").EnumerateArray().ToArray());
        Assert.Equal(("C-1", VehicleB, 211, "ALLOCATED"), (cycle.GetProperty("cycleId").GetString(), cycle.GetProperty("vehicleKey").GetString(), cycle.GetProperty("stationId").GetInt32(), cycle.GetProperty("wireState").GetString()));
        JsonElement reservation = Assert.Single(inProgress.GetProperty("chargerReservations").EnumerateArray().ToArray());
        Assert.Equal((211, VehicleB), (reservation.GetProperty("stationId").GetInt32(), reservation.GetProperty("vehicleKey").GetString()));
        Assert.Equal(ChargingWorkInProgress.Retention, inProgress.GetProperty("retention").GetString());
    }

    [Fact]
    public async Task ARejectedRosterExitsOneListsEveryReasonAndWritesNothing()
    {
        await using WaitingPointImportHarness harness = await CreateAsync();
        string file = Path.Combine(harness.Directory, "bad.json");
        await File.WriteAllTextAsync(file, RosterFile(Charger(999, "不存在"), Charger(211, "充电点1", scope: ["VK-Z"])), new UTF8Encoding(false), Token);

        (int exit, JsonElement rejected) = await RunAsync(Import(harness, file));

        Assert.Equal((1, "REJECTED"), (exit, rejected.GetProperty("outcome").GetString()));
        Assert.Equal(
            [ChargerRosterImportReasonCodes.StationNotInCatalog, ChargerRosterImportReasonCodes.VehicleOutsideFleet],
            rejected.GetProperty("errors").EnumerateArray().Select(error => error.GetProperty("reasonCode").GetString()));
        Assert.Equal((0L, 0L, 0L, 0L, 0L, 0L), await harness.RosterFootprintAsync());
    }

    /// <summary>并发读改写：两个进程几乎同时导入同一份名册，一个写成版本、一个报 UNCHANGED（并记一条审计），没有只换了号的第二版。</summary>
    [Fact]
    public async Task TwoProcessesImportingTheSameRosterAtOnceWriteOneVersion()
    {
        await using WaitingPointImportHarness harness = await CreateAsync();
        string open = Field("charger-roster-map26-station211.json");

        (int, JsonElement)[] results = await Task.WhenAll(RunAsync(Import(harness, open)), RunAsync(Import(harness, open)));

        Assert.All(results, result => Assert.Equal(0, result.Item1));
        Assert.Equal(["OK", "UNCHANGED"], results.Select(result => result.Item2.GetProperty("outcome").GetString()!).Order(StringComparer.Ordinal));
        Assert.Equal((1L, 1L, 0L, 1L, 1L, 1L), await harness.RosterFootprintAsync());
    }

    /// <summary>
    /// 现场策略文件照说明走完三步：导入只写版本（不批准不激活）；批准与激活各是一个动词；激活前后 <c>charging-policy --fleet</c> 读到每辆车
    /// 从不投运变成投运。测试批准（L2_PRESET）不带开关激活被拒。
    /// </summary>
    [Fact]
    public async Task TheShippedPolicyFileIsImportedApprovedAndActivatedAsThreeRecordedSteps()
    {
        await using WaitingPointImportHarness harness = await CreateAsync();
        string policy = Field("charging-policy-map26.json");

        (int importExit, JsonElement imported) = await RunAsync("import-charging-policy", "--database", harness.DatabasePath, "--input", policy, "--fleet", "VK-A;VK-B");
        (int beforeExit, JsonElement before) = await RunAsync("charging-policy", "--database", harness.DatabasePath, "--fleet", "VK-A;VK-B");
        (int unapprovedExit, JsonElement unapproved) = await RunAsync(Activate(harness, 1));
        (int approveExit, JsonElement approved) = await RunAsync(
            "approve-charging-policy", "--database", harness.DatabasePath, "--version", "1", "--approved-by", "Zhengyu Shao",
            "--role", "PRODUCT_OWNER", "--basis", "ticket-tools/batch9/plan.md 7.6", "--source", "FIELD");
        (int activateExit, JsonElement activated) = await RunAsync(Activate(harness, 1));
        (int afterExit, JsonElement after) = await RunAsync("charging-policy", "--database", harness.DatabasePath, "--fleet", "VK-A;VK-B");

        Assert.Equal((0, 0, 1, 0, 0, 0), (importExit, beforeExit, unapprovedExit, approveExit, activateExit, afterExit));
        Assert.Equal(("OK", 1L), (imported.GetProperty("outcome").GetString(), imported.GetProperty("version").GetInt64()));
        Assert.Equal(
            (20, 30, 80, 10),
            (imported.GetProperty("content").GetProperty("minimumPostTaskBatteryMarginPercent").GetInt32(),
             imported.GetProperty("content").GetProperty("mandatoryChargeEntryThresholdPercent").GetInt32(),
             imported.GetProperty("content").GetProperty("chargingCompletionThresholdPercent").GetInt32(),
             imported.GetProperty("content").GetProperty("estimatedTaskConsumptionPercent").GetInt32()));
        Assert.Equal([false, false], Commissioned(before));
        Assert.Equal(ChargingPolicyReasonCodes.VersionNotApproved, Assert.Single(unapproved.GetProperty("errors").EnumerateArray().ToArray()).GetProperty("reasonCode").GetString());
        Assert.Equal(("OK", "FIELD"), (approved.GetProperty("outcome").GetString(), approved.GetProperty("source").GetString()));
        Assert.Equal(("OK", 1L), (activated.GetProperty("outcome").GetString(), activated.GetProperty("sequence").GetInt64()));
        Assert.Equal([true, true], Commissioned(after));
        Assert.Equal(1L, after.GetProperty("activeVersion").GetInt64());
        Assert.Equal((1L, 0L, 1L, 1L, 1L, 1L, 1L, 1L), await harness.PolicyFootprintAsync());
    }

    [Fact]
    public async Task AnL2PresetApprovalActivatesOnlyWithTheExplicitSwitch()
    {
        await using WaitingPointImportHarness harness = await CreateAsync();
        await RunAsync("import-charging-policy", "--database", harness.DatabasePath, "--input", Field("charging-policy-map26.json"), "--fleet", "VK-A;VK-B");
        await RunAsync(
            "approve-charging-policy", "--database", harness.DatabasePath, "--version", "1", "--approved-by", "L2",
            "--role", "L2_PRESET", "--basis", "scripts/l2", "--source", "L2_PRESET");

        (int refusedExit, JsonElement refused) = await RunAsync(Activate(harness, 1));
        (int allowedExit, JsonElement allowed) = await RunAsync([.. Activate(harness, 1), "--allow-non-field-approval"]);

        Assert.Equal((1, 0), (refusedExit, allowedExit));
        Assert.Equal(ChargingPolicyReasonCodes.OnlyNonFieldApproval, Assert.Single(refused.GetProperty("errors").EnumerateArray().ToArray()).GetProperty("reasonCode").GetString());
        Assert.True(allowed.GetProperty("allowNonFieldApproval").GetBoolean());
    }

    [Theory]
    [InlineData("charger-roster", true)]
    [InlineData("charging-policy", true)]
    [InlineData("import-charger-roster", false)]
    [InlineData("import-charging-policy", false)]
    [InlineData("approve-charging-policy", false)]
    [InlineData("activate-charging-policy", false)]
    public async Task OnlyTheTwoReadVerbsOpenTheDatabaseReadOnly(string command, bool readOnly)
    {
        await using WaitingPointImportHarness harness = await CreateAsync();

        Assert.Equal(readOnly, ControlServer.FieldOps.Program.OpensReadOnly(command));
        if (readOnly)
        {
            await using SqliteConnection connection = new(ControlServerSqlite.ForDatabaseFile(harness.DatabasePath, readOnly: true));
            await connection.OpenAsync(Token);
            await using SqliteCommand write = connection.CreateCommand();
            write.CommandText = "DELETE FROM ChargerRosterVersions";
            SqliteException refused = await Assert.ThrowsAsync<SqliteException>(() => write.ExecuteNonQueryAsync(Token));
            Assert.Equal(8, refused.SqliteErrorCode); // SQLITE_READONLY
        }
    }

    private static string[] Import(WaitingPointImportHarness harness, string file, params string[] extra) =>
    [
        "import-charger-roster", "--database", harness.DatabasePath, "--input", file, "--catalog", harness.CatalogPath,
        "--map", "26", "--fleet", "VK-A;VK-B", .. extra
    ];

    private static string[] Activate(WaitingPointImportHarness harness, long version) =>
    [
        "activate-charging-policy", "--database", harness.DatabasePath, "--version", version.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "--activated-by", "Zhengyu Shao", "--fleet", "VK-A;VK-B"
    ];

    private static string[] Changes(JsonElement output) =>
        [.. output.GetProperty("changes").EnumerateArray().Select(change =>
            $"{change.GetProperty("stationId").GetInt32()} {change.GetProperty("kind").GetString()}")];

    private static bool[] Commissioned(JsonElement output) =>
        [.. output.GetProperty("vehicles").EnumerateArray().Select(vehicle => vehicle.GetProperty("commissioned").GetBoolean())];

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ControlServer.sln")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the ControlServer repository root.");
    }

    private static async Task<(int ExitCode, JsonElement Output)> RunAsync(params string[] arguments)
    {
        Assert.True(File.Exists(FieldOps), $"FieldOps was not built next to the tests: {FieldOps}");
        ProcessStartInfo start = new(FieldOps)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        using Process process = Process.Start(start)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(Token);
        Task<string> stderr = process.StandardError.ReadToEndAsync(Token);
        await process.WaitForExitAsync(Token);
        string output = (await stdout).Trim();
        string error = await stderr;
        Assert.True(output.Length > 0, $"FieldOps printed nothing (exit {process.ExitCode}): {error}");
        using JsonDocument document = JsonDocument.Parse(output);
        return (process.ExitCode, document.RootElement.Clone());
    }
}
