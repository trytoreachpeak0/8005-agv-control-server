using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using ControlServer.Application;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace ControlServer.Tests;

/// <summary>
/// FieldOps 绑定集动词的进程入口（control-server#161）：参数解析、每条命令一个 JSON 对象、退出码 0／1／2。真起一个
/// <c>ControlServer.FieldOps.exe</c> 进程，对同一个真 SQLite 文件。
/// </summary>
public sealed class TaskTypeStationFieldOpsTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly string FieldOps = typeof(TaskTypeStationFieldOpsTests).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(attribute => attribute.Key == "ControlServer.FieldOps.Path").Value!;

    [Fact]
    public async Task ActivateDryRunThenActivateThenReadBackThroughTheProcess()
    {
        await using TaskTypeStationActivationHarness harness = await TaskTypeStationActivationHarness.CreateAsync();
        // The process judges freshness by the real clock.
        await harness.ConfirmCatalogAsync(TaskTypeStationActivationHarness.Catalog, DateTimeOffset.UtcNow);
        string candidate = WriteCandidate(harness, TaskTypeStationActivationHarness.Gate, TaskTypeStationActivationHarness.Staging);
        string catalog = WriteCatalog(harness, TaskTypeStationActivationHarness.CatalogStations);

        (int previewExit, JsonElement preview) = await RunAsync(
            "activate-task-type-stations", "--database", harness.DatabasePath, "--input", candidate, "--catalog", catalog,
            "--reason", "加派工待送取货点", "--role", "现场工程师", "--dry-run");
        Assert.Equal(0, previewExit);
        Assert.Equal("PREVIEW", preview.GetProperty("outcome").GetString());
        Assert.Equal(
            TransportTaskTypes.StagingToWire,
            Assert.Single(preview.GetProperty("changes").EnumerateArray()).GetProperty("taskType").GetString());
        Assert.Equal("25|1|ACTIVE|<null>", await harness.PointerRowAsync());

        (int activateExit, JsonElement activated) = await RunAsync(
            "activate-task-type-stations", "--database", harness.DatabasePath, "--input", candidate, "--catalog", catalog,
            "--reason", "加派工待送取货点");
        Assert.Equal(0, activateExit);
        Assert.Equal("OK", activated.GetProperty("outcome").GetString());
        Assert.Equal(2, activated.GetProperty("targetVersion").GetInt64());

        (int readExit, JsonElement read) = await RunAsync(
            "task-type-stations", "--database", harness.DatabasePath, "--map", "25");
        Assert.Equal(0, readExit);
        Assert.Equal(2, read.GetProperty("activeVersion").GetInt64());
        Assert.Equal("ACTIVE", read.GetProperty("state").GetString());
        Assert.Equal(
            ["派工待送取货", "关卡"],
            read.GetProperty("active").GetProperty("bindings").EnumerateArray()
                .Select(binding => binding.GetProperty("stationName").GetString()));
        Assert.Equal([1L, 2L], read.GetProperty("versions").EnumerateArray().Select(version => version.GetProperty("version").GetInt64()));
        Assert.Empty(read.GetProperty("unreleasedHolds").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, read.GetProperty("openAttempt").ValueKind);
    }

    [Fact]
    public async Task ARejectedActivationExitsOneAndListsEveryReason()
    {
        await using TaskTypeStationActivationHarness harness = await TaskTypeStationActivationHarness.CreateAsync();
        // The process judges freshness by the real clock.
        await harness.ConfirmCatalogAsync(TaskTypeStationActivationHarness.Catalog, DateTimeOffset.UtcNow);
        string candidate = WriteCandidate(
            harness,
            TaskTypeStationActivationHarness.Gate,
            TaskTypeStationActivationHarness.Staging with { StationRiotId = 210, StationName = "关卡", SiteVerificationRef = "" });
        string catalog = WriteCatalog(harness, TaskTypeStationActivationHarness.CatalogStations);

        (int exit, JsonElement rejected) = await RunAsync(
            "activate-task-type-stations", "--database", harness.DatabasePath, "--input", candidate, "--catalog", catalog,
            "--reason", "试一下");

        Assert.Equal(1, exit);
        Assert.Equal("REJECTED", rejected.GetProperty("outcome").GetString());
        Assert.Equal(
            [TaskTypeStationReasonCodes.BindingSiteVerificationMissing, TaskTypeStationReasonCodes.StationReused],
            rejected.GetProperty("violations").EnumerateArray()
                .Select(violation => violation.GetProperty("reasonCode").GetString()).Order(StringComparer.Ordinal));
        Assert.Equal("25|1|ACTIVE|<null>", await harness.PointerRowAsync());
    }

    [Fact]
    public async Task RollbackReconcileAndReleaseRunThroughTheProcessWithTheirExitCodes()
    {
        await using TaskTypeStationActivationHarness harness = await TaskTypeStationActivationHarness.CreateAsync();
        // The process judges freshness by the real clock.
        await harness.ConfirmCatalogAsync(TaskTypeStationActivationHarness.Catalog, DateTimeOffset.UtcNow);
        string catalog = WriteCatalog(harness, TaskTypeStationActivationHarness.CatalogStations);
        TaskTypeStationActivationResult first = await harness.Default().ActivateAsync(
            TaskTypeStationActivationHarness.Candidate(TaskTypeStationActivationHarness.Gate, TaskTypeStationActivationHarness.Staging),
            at: DateTimeOffset.UtcNow);
        Assert.True(
            first.Outcome == TaskTypeStationActivationOutcome.Activated,
            string.Join("; ", first.Violations.Select(violation => violation.ReasonCode + " " + violation.Detail)));

        (int rollbackExit, JsonElement rollback) = await RunAsync(
            "rollback-task-type-stations", "--database", harness.DatabasePath, "--map", "25", "--version", "1",
            "--catalog", catalog, "--reason", "撤回派工待送取货点");
        Assert.Equal(0, rollbackExit);
        Assert.Equal(("OK", "ROLLBACK", 3L), (
            rollback.GetProperty("outcome").GetString(),
            rollback.GetProperty("requestCategory").GetString(),
            rollback.GetProperty("targetVersion").GetInt64()));

        (int reconcileExit, JsonElement reconcile) = await RunAsync(
            "reconcile-task-type-stations", "--database", harness.DatabasePath, "--map", "25", "--reason", "例行核对");
        Assert.Equal(0, reconcileExit);
        Assert.Equal("NOTHING_TO_RECONCILE", reconcile.GetProperty("conclusion").GetString());

        await harness.Default().Holds.RaiseAsync(
            25, TransportTaskTypes.WireToGate, TaskTypeStationHoldSource.Manual, "MANUAL_TIGHTEN", "{}", "operator",
            TaskTypeStationActivationHarness.Now, Token);
        (int refusedExit, JsonElement refused) = await RunAsync(
            "release-task-type-station-hold", "--database", harness.DatabasePath, "--map", "25",
            "--task-type", TransportTaskTypes.WireToGate, "--catalog", catalog, "--reason", "核对完毕");
        Assert.Equal(1, refusedExit);
        Assert.Equal("REJECTED", refused.GetProperty("outcome").GetString());
        Assert.Equal(
            TaskTypeStationReasonCodes.BindingSiteVerificationMissing,
            Assert.Single(refused.GetProperty("violations").EnumerateArray()).GetProperty("reasonCode").GetString());
        (int releaseExit, JsonElement release) = await RunAsync(
            "release-task-type-station-hold", "--database", harness.DatabasePath, "--map", "25",
            "--task-type", TransportTaskTypes.WireToGate, "--site-verification", "SITE-RECHECK-0919",
            "--catalog", catalog, "--reason", "核对完毕");
        Assert.Equal(0, releaseExit);
        Assert.Equal("OK", release.GetProperty("outcome").GetString());
        Assert.Single(release.GetProperty("released").EnumerateArray());
    }

    [Theory]
    [InlineData("activate-task-type-stations", "--input", "candidate.json", "--catalog", "catalog.json")]
    [InlineData("rollback-task-type-stations", "--map", "25", "--version", "1")]
    [InlineData("reconcile-task-type-stations", "--map", "25", "--role", "x")]
    [InlineData("close-task-type-station-activation", "--map", "25", "--role", "x")]
    public async Task AMissingReasonIsAUsageErrorAndWritesNothing(string command, string a, string b, string c, string d)
    {
        await using TaskTypeStationActivationHarness harness = await TaskTypeStationActivationHarness.CreateAsync();
        IReadOnlyDictionary<string, long> before = await harness.CountRowsAsync();

        (int exit, _) = await RunAsync(command, "--database", harness.DatabasePath, a, b, c, d);

        Assert.Equal(2, exit);
        Assert.Equal(before, await harness.CountRowsAsync());
    }

    [Fact]
    public async Task ManualCloseOnAMapWithNothingOpenIsRefusedWithExitOne()
    {
        await using TaskTypeStationActivationHarness harness = await TaskTypeStationActivationHarness.CreateAsync();

        (int exit, JsonElement refused) = await RunAsync(
            "close-task-type-station-activation", "--database", harness.DatabasePath, "--map", "25", "--reason", "试一下");

        Assert.Equal(1, exit);
        Assert.Equal("REJECTED", refused.GetProperty("outcome").GetString());
        Assert.Equal(
            TaskTypeStationActivationReasonCodes.NothingToClose,
            Assert.Single(refused.GetProperty("violations").EnumerateArray()).GetProperty("reasonCode").GetString());
        Assert.Equal("25|1|ACTIVE|<null>", await harness.PointerRowAsync());
    }

    [Fact]
    public async Task TheReadOnlyVerbOpensTheDatabaseInSqliteReadOnlyMode()
    {
        await using TaskTypeStationActivationHarness harness = await TaskTypeStationActivationHarness.CreateAsync();

        Assert.True(ControlServer.FieldOps.Program.OpensReadOnly("task-type-stations"));
        Assert.False(ControlServer.FieldOps.Program.OpensReadOnly("activate-task-type-stations"));
        await using SqliteConnection connection = new(ControlServerSqlite.ForDatabaseFile(
            harness.DatabasePath, ControlServer.FieldOps.Program.OpensReadOnly("task-type-stations")));
        await connection.OpenAsync(Token);
        await using SqliteCommand write = connection.CreateCommand();
        write.CommandText = "UPDATE TaskTypeStationActiveBindingSets SET State = 'X'";
        SqliteException refused = await Assert.ThrowsAsync<SqliteException>(() => write.ExecuteNonQueryAsync(Token));
        Assert.Equal(8, refused.SqliteErrorCode); // SQLITE_READONLY
        Assert.Equal("25|1|ACTIVE|<null>", await harness.PointerRowAsync());
    }

    private static string WriteCandidate(TaskTypeStationActivationHarness harness, params TaskTypeStationBinding[] bindings)
    {
        string path = Path.Combine(Path.GetDirectoryName(harness.DatabasePath)!, "candidate.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            mapId = 25,
            ruleVersion = 1,
            requiredTaskTypes = bindings.Select(binding => binding.TaskType),
            bindings = bindings.Select(binding => new
            {
                taskType = binding.TaskType,
                stationRiotId = binding.StationRiotId,
                stationName = binding.StationName,
                siteVerificationRef = binding.SiteVerificationRef
            })
        }), new UTF8Encoding(false));
        return path;
    }

    private static string WriteCatalog(TaskTypeStationActivationHarness harness, IEnumerable<RiotMapStation> stations)
    {
        string path = Path.Combine(Path.GetDirectoryName(harness.DatabasePath)!, "catalog.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            mapId = 25,
            stations = stations.Select(station => new { stationId = station.StationId, stationName = station.StationName })
        }), new UTF8Encoding(false));
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
