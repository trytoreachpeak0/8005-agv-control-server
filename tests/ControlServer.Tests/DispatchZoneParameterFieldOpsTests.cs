using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using ControlServer.Application;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using static ControlServer.Tests.DispatchZoneParameterImportHarness;

namespace ControlServer.Tests;

/// <summary>
/// FieldOps 每区派车参数动词的进程入口（control-server#216）：参数解析、每条命令一个 JSON 对象、退出码 0／1／2、只读动词以只读模式开库。
/// 真起一个 <c>ControlServer.FieldOps.exe</c> 进程，对同一个真 SQLite 文件。
/// </summary>
public sealed class DispatchZoneParameterFieldOpsTests
{
    private const string Import = "import-dispatch-zone-parameters";

    private const string Read = "dispatch-zone-parameters";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly string FieldOps = typeof(DispatchZoneParameterFieldOpsTests).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(attribute => attribute.Key == "ControlServer.FieldOps.Path").Value!;

    [Fact]
    public async Task BeforeAnyImportTheReadVerbListsEveryZoneAsUnconfiguredNeverAsZero()
    {
        await using DispatchZoneParameterImportHarness harness = await CreateAsync();

        (int exit, JsonElement read) = await RunAsync(Read, "--database", harness.DatabasePath);

        Assert.Equal(0, exit);
        Assert.Equal("OK", read.GetProperty("outcome").GetString());
        Assert.Equal(JsonValueKind.Null, read.GetProperty("version").ValueKind);
        Assert.Equal(
            [$"{ZoneA} UNCONFIGURED/- UNCONFIGURED/-", $"{ZoneC} UNCONFIGURED/- UNCONFIGURED/-", $"{ZoneB} UNCONFIGURED/- UNCONFIGURED/-"],
            Zones(read));
    }

    [Fact]
    public async Task DryRunThenImportThenTheSameTableAgainThenReadBackThroughTheProcess()
    {
        await using DispatchZoneParameterImportHarness harness = await CreateAsync();
        string table = WriteCsv(harness, "zones.csv", Csv($"{ZoneA},0,", $"{ZoneB},20000,600"));

        (int previewExit, JsonElement preview) = await RunAsync(Import, "--database", harness.DatabasePath, "--input", table, "--dry-run");
        Assert.Equal(0, previewExit);
        Assert.Equal(("OK", true), (preview.GetProperty("outcome").GetString(), preview.GetProperty("dryRun").GetBoolean()));
        Assert.Equal(JsonValueKind.Null, preview.GetProperty("version").ValueKind);
        Assert.Equal([ZoneA, ZoneB], preview.GetProperty("changes").EnumerateArray().Select(change => change.GetProperty("dispatchZone").GetString()));
        Assert.Equal((0L, 0L, 0L, 0L), await harness.FootprintAsync());

        (int importExit, JsonElement imported) = await RunAsync(Import, "--database", harness.DatabasePath, "--input", table);
        Assert.Equal(0, importExit);
        Assert.Equal(("OK", 1L), (imported.GetProperty("outcome").GetString(), imported.GetProperty("version").GetInt64()));
        Assert.Equal(2, imported.GetProperty("entryCount").GetInt32());
        Assert.False(string.IsNullOrEmpty(imported.GetProperty("snapshotId").GetString()));

        (int againExit, JsonElement again) = await RunAsync(Import, "--database", harness.DatabasePath, "--input", table);
        Assert.Equal(0, againExit);
        Assert.Equal(("UNCHANGED", 1L), (again.GetProperty("outcome").GetString(), again.GetProperty("version").GetInt64()));
        Assert.Equal((1L, 2L, 1L, 1L), await harness.FootprintAsync());

        (int readExit, JsonElement read) = await RunAsync(Read, "--database", harness.DatabasePath);
        Assert.Equal(0, readExit);
        Assert.Equal(1, read.GetProperty("version").GetInt64());
        Assert.Equal(imported.GetProperty("contentSha256").GetString(), read.GetProperty("contentSha256").GetString());
        Assert.Equal(
            [$"{ZoneA} FORBIDDEN/0 UNCONFIGURED/-", $"{ZoneC} UNCONFIGURED/- UNCONFIGURED/-", $"{ZoneB} ALLOWED/20000 CONFIGURED/600"],
            Zones(read));
    }

    [Fact]
    public async Task ARejectedImportExitsOneAndListsEveryReasonWithItsLine()
    {
        await using DispatchZoneParameterImportHarness harness = await CreateAsync();
        string table = WriteCsv(harness, "bad.csv", Csv($"{ZoneA},20000,600", $"{ZoneA},-1,600", "MAP-99-X,1,1"));

        (int exit, JsonElement rejected) = await RunAsync(Import, "--database", harness.DatabasePath, "--input", table);

        Assert.Equal(1, exit);
        Assert.Equal("REJECTED", rejected.GetProperty("outcome").GetString());
        Assert.Equal(
            [
                $"3 {DispatchZoneParameterImportReasonCodes.DispatchZoneDuplicated}",
                $"3 {DispatchZoneParameterImportReasonCodes.ValueInvalid}",
                $"4 {DispatchZoneParameterImportReasonCodes.DispatchZoneNotFound}",
            ],
            rejected.GetProperty("errors").EnumerateArray()
                .Select(error => $"{error.GetProperty("line").GetInt32()} {error.GetProperty("reasonCode").GetString()}")
                .Order(StringComparer.Ordinal));
        Assert.Equal((0L, 0L, 0L, 0L), await harness.FootprintAsync());
    }

    [Fact]
    public async Task ReadingAnOlderVersionShowsItAsItWasAndAMissingVersionExitsOne()
    {
        await using DispatchZoneParameterImportHarness harness = await CreateAsync();
        await harness.ImportAsync(Csv($"{ZoneA},20000,600"));
        await harness.ImportAsync(Csv($"{ZoneA},,600"));

        (int oldExit, JsonElement old) = await RunAsync(Read, "--database", harness.DatabasePath, "--version", "1");
        (int missingExit, JsonElement missing) = await RunAsync(Read, "--database", harness.DatabasePath, "--version", "9");

        Assert.Equal((0, 1L), (oldExit, old.GetProperty("version").GetInt64()));
        Assert.Contains($"{ZoneA} ALLOWED/20000 CONFIGURED/600", Zones(old));
        Assert.Equal((1, "NOT_FOUND"), (missingExit, missing.GetProperty("outcome").GetString()));
    }

    [Fact]
    public async Task TheReadOnlyVerbOpensTheDatabaseInSqliteReadOnlyModeAndTheImportDoesNot()
    {
        await using DispatchZoneParameterImportHarness harness = await CreateAsync();

        Assert.True(ControlServer.FieldOps.Program.OpensReadOnly(Read));
        Assert.False(ControlServer.FieldOps.Program.OpensReadOnly(Import));
        await using SqliteConnection connection = new(ControlServerSqlite.ForDatabaseFile(
            harness.DatabasePath, ControlServer.FieldOps.Program.OpensReadOnly(Read)));
        await connection.OpenAsync(Token);
        await using SqliteCommand write = connection.CreateCommand();
        write.CommandText = "DELETE FROM DispatchZoneVehicles";
        SqliteException refused = await Assert.ThrowsAsync<SqliteException>(() => write.ExecuteNonQueryAsync(Token));
        Assert.Equal(8, refused.SqliteErrorCode); // SQLITE_READONLY
    }

    [Fact]
    public async Task AnImportWithoutAnInputIsAUsageError()
    {
        await using DispatchZoneParameterImportHarness harness = await CreateAsync();

        (int exit, _) = await RunAsync(Import, "--database", harness.DatabasePath);

        Assert.Equal(2, exit);
    }

    /// <summary>每个分区写成「分区 途中追加状态/毫米 防饥饿状态/秒」，没有取值写 <c>-</c>。</summary>
    private static string[] Zones(JsonElement read) =>
        [.. read.GetProperty("zones").EnumerateArray().Select(zone =>
        {
            JsonElement addition = zone.GetProperty("enRouteAddition");
            JsonElement starvation = zone.GetProperty("starvation");
            return $"{zone.GetProperty("dispatchZone").GetString()} "
                + $"{addition.GetProperty("state").GetString()}/{Value(addition.GetProperty("maxPathCostIncreaseMm"))} "
                + $"{starvation.GetProperty("state").GetString()}/{Value(starvation.GetProperty("thresholdSeconds"))}";
        })];

    private static string Value(JsonElement value) =>
        value.ValueKind == JsonValueKind.Null ? "-" : value.GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string WriteCsv(DispatchZoneParameterImportHarness harness, string name, string csv)
    {
        string path = Path.Combine(harness.Directory, name);
        File.WriteAllText(path, csv, new UTF8Encoding(false));
        return path;
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
        _ = await stderr;
        if (output.Length == 0)
        {
            return (process.ExitCode, default);
        }
        using JsonDocument document = JsonDocument.Parse(output);
        return (process.ExitCode, document.RootElement.Clone());
    }
}
