using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using ControlServer.Application;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using static ControlServer.Tests.WaitingPointImportHarness;

namespace ControlServer.Tests;

/// <summary>
/// FieldOps 等待点动词的进程入口（control-server#388）：参数解析、每条命令一个 JSON 对象、退出码 0／1／2、只读动词以只读模式开库、
/// 两个进程同时导入。真起一个 <c>ControlServer.FieldOps.exe</c> 进程，对同一个真 SQLite 文件。
/// </summary>
public sealed class WaitingPointFieldOpsTests
{
    private const string Import = "import-waiting-points";

    private const string Read = "read-waiting-points";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly string FieldOps = typeof(WaitingPointFieldOpsTests).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(attribute => attribute.Key == "ControlServer.FieldOps.Path").Value!;

    private static readonly string Site = Csv("26,214,等待点1,true,", "26,215,等待点2,true,", "26,216,等待点3,true,");

    [Fact]
    public async Task DryRunThenImportThenTheSameTableAgainThenReadBackThroughTheProcess()
    {
        await using WaitingPointImportHarness harness = await CreateAsync();
        string table = harness.WriteCsv("site.csv", Site);

        (int previewExit, JsonElement preview) = await RunAsync(ImportArguments(harness, table, "--dry-run"));
        Assert.Equal((0, "OK", true), (previewExit, preview.GetProperty("outcome").GetString(), preview.GetProperty("dryRun").GetBoolean()));
        Assert.Equal(JsonValueKind.Null, preview.GetProperty("version").ValueKind);
        Assert.Equal(["214 ADDED", "215 ADDED", "216 ADDED"], Changes(preview));
        Assert.Equal((0L, 0L, 0L, 0L, 0L), await harness.FootprintAsync());

        (int importExit, JsonElement imported) = await RunAsync(ImportArguments(harness, table));
        Assert.Equal((0, "OK", 1L), (importExit, imported.GetProperty("outcome").GetString(), imported.GetProperty("version").GetInt64()));
        Assert.Equal((2, 3, true), Coverage(imported));
        Assert.Equal(JsonValueKind.Null, imported.GetProperty("catalogMatchesServerConfirmation").ValueKind);

        (int againExit, JsonElement again) = await RunAsync(ImportArguments(harness, table));
        Assert.Equal((0, "UNCHANGED", 1L), (againExit, again.GetProperty("outcome").GetString(), again.GetProperty("version").GetInt64()));
        Assert.Equal((1L, 3L, 0L, 1L, 1L), await harness.FootprintAsync());

        (int readExit, JsonElement read) = await RunAsync(Read, "--database", harness.DatabasePath, "--map", "26", "--fleet", "VK-A;VK-B");
        Assert.Equal((0, 1L), (readExit, read.GetProperty("version").GetInt64()));
        Assert.Equal(
            ["214 等待点1", "215 等待点2", "216 等待点3"],
            read.GetProperty("points").EnumerateArray().Select(point =>
                $"{point.GetProperty("stationId").GetInt32()} {point.GetProperty("stationName").GetString()}"));
        Assert.Equal((2, 3, true), Coverage(read));
    }

    [Fact]
    public async Task ARejectedImportExitsOneListsEveryReasonAndWritesNothing()
    {
        await using WaitingPointImportHarness harness = await CreateAsync();
        string table = harness.WriteCsv("bad.csv", Csv("26,212,充电准备点1,true,", "26,214,等待点1,true,VK-Z", "26,214,等待点1,true,"));

        (int exit, JsonElement rejected) = await RunAsync(ImportArguments(harness, table));

        Assert.Equal((1, "REJECTED"), (exit, rejected.GetProperty("outcome").GetString()));
        Assert.Equal(
            [
                $"2 {WaitingPointImportReasonCodes.ReservedRole}",
                $"3 {WaitingPointImportReasonCodes.VehicleOutsideFleet}",
                $"4 {WaitingPointImportReasonCodes.StationDuplicated}",
            ],
            rejected.GetProperty("errors").EnumerateArray()
                .Select(error => $"{error.GetProperty("line").GetInt32()} {error.GetProperty("reasonCode").GetString()}"));
        Assert.Equal((0L, 0L, 0L, 0L, 0L), await harness.FootprintAsync());
    }

    [Fact]
    public async Task ThePreviewOfDisablingAReservedPointListsTheReservationThroughTheProcess()
    {
        await using WaitingPointImportHarness harness = await CreateAsync();
        await harness.ImportAsync(Site);
        await harness.ReserveAsync(215, VehicleB, "idle:VK-B:1", version: 1);
        string table = harness.WriteCsv("disable.csv", Csv("26,214,等待点1,true,", "26,215,等待点2,false,", "26,216,等待点3,true,"));

        (int exit, JsonElement preview) = await RunAsync(ImportArguments(harness, table, "--dry-run"));

        Assert.Equal(0, exit);
        Assert.Equal(["215 DISABLED"], Changes(preview));
        JsonElement retained = Assert.Single(preview.GetProperty("retainedReferences").EnumerateArray().ToArray());
        Assert.Equal(
            ("RESERVED", "VK-B", "idle:VK-B:1", 1L, WaitingPointEligibilityReasons.Disabled),
            (retained.GetProperty("state").GetString(), retained.GetProperty("vehicleKey").GetString(),
                retained.GetProperty("journeyId").GetString(), retained.GetProperty("waitingPointVersion").GetInt64(),
                retained.GetProperty("newVersionReason").GetString()));
        Assert.Equal(WaitingPointImportService.RetentionNote, retained.GetProperty("retention").GetString());
    }

    /// <summary>并发读改写：两个进程几乎同时导入同一张新表，一个写成版本、一个报 UNCHANGED，没有只换了号的第二版。</summary>
    [Fact]
    public async Task TwoProcessesImportingTheSameNewTableAtOnceWriteOneVersion()
    {
        await using WaitingPointImportHarness harness = await CreateAsync();
        await harness.ImportAsync(Csv("26,214,等待点1,true,"));
        string table = harness.WriteCsv("same.csv", Site);

        for (int attempt = 0; attempt < 3; attempt++)
        {
            (int, JsonElement)[] results = await Task.WhenAll(
                RunAsync(ImportArguments(harness, table)),
                RunAsync(ImportArguments(harness, table)));

            Assert.All(results, result => Assert.Equal(0, result.Item1));
            Assert.Equal(
                attempt == 0 ? ["OK", "UNCHANGED"] : ["UNCHANGED", "UNCHANGED"],
                results.Select(result => result.Item2.GetProperty("outcome").GetString()!).Order(StringComparer.Ordinal));
        }
        Assert.Equal((2L, 4L, 0L, 2L, 2L), await harness.FootprintAsync());
    }

    [Fact]
    public async Task TheReadVerbOpensTheDatabaseReadOnlyAndTheImportDoesNot()
    {
        await using WaitingPointImportHarness harness = await CreateAsync();

        Assert.True(ControlServer.FieldOps.Program.OpensReadOnly(Read));
        Assert.False(ControlServer.FieldOps.Program.OpensReadOnly(Import));
        await using SqliteConnection connection = new(ControlServerSqlite.ForDatabaseFile(
            harness.DatabasePath, ControlServer.FieldOps.Program.OpensReadOnly(Read)));
        await connection.OpenAsync(Token);
        await using SqliteCommand write = connection.CreateCommand();
        write.CommandText = "DELETE FROM WaitingPoints";
        SqliteException refused = await Assert.ThrowsAsync<SqliteException>(() => write.ExecuteNonQueryAsync(Token));
        Assert.Equal(8, refused.SqliteErrorCode); // SQLITE_READONLY
    }

    [Theory]
    [InlineData("--catalog")]
    [InlineData("--map")]
    [InlineData("--fleet")]
    public async Task AnImportMissingAnyOfItsFourInputsIsAUsageError(string missing)
    {
        await using WaitingPointImportHarness harness = await CreateAsync();
        string table = harness.WriteCsv("site.csv", Site);
        string[] arguments = ImportArguments(harness, table);
        int index = Array.IndexOf(arguments, missing);

        (int exit, _) = await RunAsync([.. arguments[..index], .. arguments[(index + 2)..]]);

        Assert.Equal(2, exit);
        Assert.Equal((0L, 0L, 0L, 0L, 0L), await harness.FootprintAsync());
    }

    private static string[] ImportArguments(WaitingPointImportHarness harness, string table, params string[] extra) =>
    [
        Import, "--database", harness.DatabasePath, "--input", table, "--catalog", harness.CatalogPath,
        "--map", "26", "--fleet", "VK-A;VK-B", .. extra
    ];

    private static string[] Changes(JsonElement output) =>
        [.. output.GetProperty("changes").EnumerateArray().Select(change =>
            $"{change.GetProperty("stationId").GetInt32()} {change.GetProperty("kind").GetString()}")];

    private static (int, int, bool) Coverage(JsonElement output)
    {
        JsonElement coverage = output.GetProperty("coverage");
        return (coverage.GetProperty("vehicleCount").GetInt32(), coverage.GetProperty("enabledOnMap").GetInt32(),
            coverage.GetProperty("sufficient").GetBoolean());
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
