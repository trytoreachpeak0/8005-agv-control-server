using System.Globalization;
using System.Text.RegularExpressions;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ControlServer.Tests;

/// <summary>
/// 批次8-16（control-server#387）的迁移 <c>Batch8RetireOldVehicleOccupancy</c>：删前核数据、回填占有记录、删租约表与订单占用两列。
/// </summary>
/// <remarks>
/// <para>
/// 种子一律走真实的受理与释放路径，种在删表之前那个迁移（<see cref="Batch7MigrationDisciplineTests.LastMigrationWithTheLeases"/>）
/// 的库上；今天的路径写占有记录而不写租约，所以种的那一段用 <see cref="Batch7JourneyFixture.WriteLeasesTheWayTheOldVersionDidAsync"/>
/// 把记录换成那一版会写的租约。
/// </para>
/// <para>
/// 两类「数据已经对不上」的旧占用用原始 SQL 造：它们正是正常路径造不出来的状态。
/// </para>
/// </remarks>
public sealed class Batch8OccupancyRetirementMigrationTests
{
    internal const string Migration = "20260929070322_Batch8RetireOldVehicleOccupancy";

    private static readonly string[] RetiredNames =
        ["VehicleDispatchLease", "VehicleOccupancyClaimedAt", "VehicleOccupancyReleasedAt"];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task WithJourneysInFlightItRunsKeepsEveryOtherRowAndLeavesOneRecordPerJourneyOpenOnlyWhileItsClaimIs()
    {
        await using Batch7JourneyFixture fixture = await SeededAsync();
        Dictionary<string, string[]> columnsBefore = await ColumnsOfEveryTableAsync(fixture.Connection);
        Dictionary<string, string[]> rowsBefore = await DumpEveryTableAsync(fixture.Connection);
        Assert.Equal(2, rowsBefore["VehiclePurposeClaims"].Length);
        Assert.Equal(4, rowsBefore["VehicleDispatchLeases"].Length);
        Assert.Empty(rowsBefore["VehiclePurposeClaimRecords"]);

        await fixture.Context.Database.MigrateAsync(Token);

        Dictionary<string, string[]> columnsAfter = await ColumnsOfEveryTableAsync(fixture.Connection);
        Assert.Equal(["VehicleDispatchLeases"], columnsBefore.Keys.Except(columnsAfter.Keys));
        Assert.Empty(columnsAfter.Keys.Except(columnsBefore.Keys));
        // Every other table keeps its columns in their order and every row value for value; OrderIntents loses exactly its
        // last two columns, and nothing else of any of its rows.
        foreach ((string table, string[] columns) in columnsBefore.Where(pair => pair.Key is not "VehicleDispatchLeases"))
        {
            if (table == "OrderIntents")
            {
                Assert.Equal(["VehicleOccupancyClaimedAt", "VehicleOccupancyReleasedAt"], columns[^2..]);
                Assert.Equal(columns[..^2], columnsAfter[table]);
                Assert.Equal(
                    rowsBefore[table].Select(row => Regex.Replace(row, @"\|VehicleOccupancyClaimedAt=.*$", "")),
                    await Batch7JourneyFixture.DumpAsync(fixture.Connection, table));
                continue;
            }
            Assert.Equal(columns, columnsAfter[table]);
            if (table != "VehiclePurposeClaimRecords")
            {
                Assert.Equal(rowsBefore[table], await Batch7JourneyFixture.DumpAsync(fixture.Connection, table));
            }
        }
        Assert.DoesNotContain(await IndexNamesAsync(fixture.Connection), name => name == "IX_OrderIntents_VehicleKey");

        // One record per journey: open for the two held now (from their claims), closed for the two whose lease was
        // released, with the lease's own moments.
        Assert.Equal(
            [
                "backfill:VK-01:journey:D-1|VK-01|TRANSPORT|journey:D-1|2026-09-19 09:00:00+00:00|NULL|NULL",
                "backfill:VK-02:journey:D-2|VK-02|TRANSPORT|journey:D-2|2026-09-19 09:01:00+00:00|NULL|NULL",
                "lease:VK-03:journey:D-3|VK-03|TRANSPORT|journey:D-3|2026-09-19 09:02:00+00:00|2026-09-19 09:10:00+00:00|DISPATCH_LEASE_RELEASED",
                "lease:VK-04:journey:D-4|VK-04|TRANSPORT|journey:D-4|2026-09-19 09:03:00+00:00|2026-09-19 09:11:00+00:00|DISPATCH_LEASE_RELEASED",
            ],
            await RecordsAsync(fixture.Connection));
        await using ControlServerDbContext read = fixture.NewContext();
        await VehicleOccupancyAssertions.AssertOpenClaimRecordsAndPurposeClaimsMatchAsync(read);
        Assert.Equal(Migration, (await read.Database.GetAppliedMigrationsAsync(Token)).Last());
    }

    [Fact]
    public async Task AnUnreleasedLeaseWithoutItsClaimRefusesTheMigrationWholeNamingTheLeaseAndDropsNothing()
    {
        await using Batch7JourneyFixture fixture = await SeededAsync();
        await ExecuteAsync(fixture.Connection, "DELETE FROM VehiclePurposeClaims WHERE JourneyId = 'journey:D-2'");

        await AssertRefusedAsync(
            fixture, ["VehicleDispatchLeases unreleased: DemandId=D-2 JourneyId=journey:D-2 VehicleKey=VK-02"]);
    }

    [Fact]
    public async Task AnUnreleasedOrderOccupancyOfAClosedJourneyWithoutAClaimRefusesTheMigrationWholeNamingTheOrder()
    {
        await using Batch7JourneyFixture fixture = await SeededAsync();
        // D-3's journey closed and its claim went, but its pickup order still says the vehicle is occupied.
        await ExecuteAsync(
            fixture.Connection,
            "UPDATE OrderIntents SET VehicleOccupancyReleasedAt = NULL WHERE UpperId = 'W2G-D-3-PICKUP-1'");

        await AssertRefusedAsync(
            fixture, ["OrderIntents occupancy unreleased: UpperId=W2G-D-3-PICKUP-1 DemandId=D-3 VehicleKey=VK-03"]);
    }

    [Fact]
    public async Task EveryMismatchIsNamedInOneRefusal()
    {
        await using Batch7JourneyFixture fixture = await SeededAsync();
        await ExecuteAsync(fixture.Connection, "DELETE FROM VehiclePurposeClaims");
        await ExecuteAsync(
            fixture.Connection,
            "UPDATE OrderIntents SET VehicleOccupancyReleasedAt = NULL WHERE UpperId = 'W2G-D-3-PICKUP-1'");

        await AssertRefusedAsync(
            fixture,
            [
                "VehicleDispatchLeases unreleased: DemandId=D-1 JourneyId=journey:D-1 VehicleKey=VK-01",
                "VehicleDispatchLeases unreleased: DemandId=D-2 JourneyId=journey:D-2 VehicleKey=VK-02",
                "OrderIntents occupancy unreleased: UpperId=W2G-D-3-PICKUP-1 DemandId=D-3 VehicleKey=VK-03",
            ]);
    }

    /// <summary>
    /// 调度在 control-server#387 票面要求的第一格：升级时在途 → 升级 → 经引擎路径结束 → 同一辆车能经账本再认领。
    /// </summary>
    [Fact]
    public async Task AJourneyInFlightAtTheUpgradeEndsThroughTheEngineAndItsVehicleIsClaimedAgain()
    {
        await using Batch7JourneyFixture fixture = await SeededAsync();
        await fixture.Context.Database.MigrateAsync(Token);
        await fixture.RenewContextAsync();

        await Batch7JourneyFixture.CompleteByUnloadAsync(fixture.Context, "D-1", Batch7JourneyFixture.Now.AddMinutes(20));
        await fixture.RenewContextAsync();

        VehiclePurposeLedgerStore ledger = new(fixture.Context);
        Assert.Null(await ledger.ReadClaimAsync("VK-01", Token));
        Assert.Equal(
            VehiclePurposeAcquisitionOutcome.Acquired,
            await ledger.TryAcquireAsync(
                new VehiclePurposeClaim("VK-01", VehiclePurposes.Transport, "journey:NEXT", Batch7JourneyFixture.Now.AddMinutes(21)),
                station: null,
                Token));
        IReadOnlyList<VehiclePurposeClaimRecord> history = await ledger.ListClaimHistoryAsync("VK-01", Token);
        Assert.Equal(
            [
                ("journey:D-1", (DateTimeOffset?)Batch7JourneyFixture.Now.AddMinutes(20), (string?)VehiclePurposeReleaseReasons.LastDemandUnloaded),
                ("journey:NEXT", null, null),
            ],
            history.Select(record => (record.JourneyId, record.ReleasedAt, record.ReleaseReason)));
    }

    /// <summary>第二格：升级前已结束的旅程，升级后同车受理新旅程。</summary>
    [Fact]
    public async Task AJourneyThatEndedBeforeTheUpgradeLeavesItsVehicleFreeForTheNextAcceptance()
    {
        await using Batch7JourneyFixture fixture = await SeededAsync();
        await fixture.Context.Database.MigrateAsync(Token);
        await fixture.RenewContextAsync();

        await Batch7JourneyFixture.AcceptAsync(fixture.Context, "D-5", "AGV-03", "VK-03", Batch7JourneyFixture.Now.AddMinutes(30));
        await fixture.RenewContextAsync();

        Assert.Equal(
            "journey:D-5",
            (await new VehiclePurposeLedgerStore(fixture.Context).ReadClaimAsync("VK-03", Token))?.JourneyId);
        Assert.Equal(
            [("journey:D-3", true), ("journey:D-5", false)],
            (await new VehiclePurposeLedgerStore(fixture.Context).ListClaimHistoryAsync("VK-03", Token))
            .Select(record => (record.JourneyId, record.ReleasedAt is not null)));
    }

    /// <summary>
    /// 旧的一轮：正常卸完、用途占有已放、旅程还没写 Completed，订单占用还开着。这是停机升级可能停在的正常时刻，不许拒绝。
    /// </summary>
    [Fact]
    public async Task TheRoundInWhichAnUnloadReleasedTheClaimButNotYetTheOrderOccupancyDoesNotRefuse()
    {
        await using Batch7JourneyFixture fixture = await SeededAsync();
        Assert.Equal(
            ["W2G-D-4-PICKUP-1"],
            await ScalarsAsync(
                fixture.Connection,
                """
                SELECT o.UpperId FROM OrderIntents AS o JOIN JourneyRuntimes AS j ON j.PickupUpperId = o.UpperId
                WHERE o.VehicleOccupancyClaimedAt IS NOT NULL AND o.VehicleOccupancyReleasedAt IS NULL
                  AND j.Stage <> 'Completed'
                  AND NOT EXISTS (SELECT 1 FROM VehiclePurposeClaims AS c WHERE c.JourneyId = j.JourneyId)
                """));

        await fixture.Context.Database.MigrateAsync(Token);

        Assert.Equal(Migration, (await fixture.Context.Database.GetAppliedMigrationsAsync(Token)).Last());
    }

    [Fact]
    public async Task DownRebuildsALeasePerJourneyAndPutsTheColumnsBackInPlaceAndUpAgainWritesTheSameRecords()
    {
        await using Batch7JourneyFixture fixture = await SeededAsync();
        string[] leasesBefore = await Batch7JourneyFixture.DumpAsync(fixture.Connection, "VehicleDispatchLeases");
        Dictionary<string, string[]> columnsBefore = await ColumnsOfEveryTableAsync(fixture.Connection);
        IMigrator migrator = fixture.Context.GetService<IMigrator>();
        await migrator.MigrateAsync(Migration, Token);
        string[] recordsAfterFirstUp = await RecordsAsync(fixture.Connection);

        await migrator.MigrateAsync(Batch7MigrationDisciplineTests.LastMigrationWithTheLeases, Token);

        Assert.Equal(columnsBefore, await ColumnsOfEveryTableAsync(fixture.Connection));
        // The leases come back as they were: journey, demand, vehicle and both moments.
        Assert.Equal(leasesBefore, await Batch7JourneyFixture.DumpAsync(fixture.Connection, "VehicleDispatchLeases"));
        // The order occupancy does not (known limit, stated on the migration).
        Assert.Empty(await ScalarsAsync(
            fixture.Connection, "SELECT UpperId FROM OrderIntents WHERE VehicleOccupancyClaimedAt IS NOT NULL"));
        Assert.Equal(recordsAfterFirstUp, await RecordsAsync(fixture.Connection));

        await migrator.MigrateAsync(Migration, Token);

        Assert.Equal(recordsAfterFirstUp, await RecordsAsync(fixture.Connection));
    }

    /// <summary>
    /// 降级跑过旧引擎（它放车时删占有行、不碰记录）再升级：开着却没有占有行的记录被关上，同一趟旅程不出第二条。
    /// </summary>
    [Fact]
    public async Task UpAgainAfterTheOldEngineReleasedAClaimClosesItsRecordRatherThanLeaveItOpen()
    {
        await using Batch7JourneyFixture fixture = await SeededAsync();
        IMigrator migrator = fixture.Context.GetService<IMigrator>();
        await migrator.MigrateAsync(Migration, Token);
        await migrator.MigrateAsync(Batch7MigrationDisciplineTests.LastMigrationWithTheLeases, Token);
        // What the old engine does when D-2 unloads: the claim goes and the lease is released, the record is untouched.
        await ExecuteAsync(
            fixture.Connection,
            """
            DELETE FROM VehiclePurposeClaims WHERE JourneyId = 'journey:D-2';
            UPDATE VehicleDispatchLeases SET ReleasedAt = '2026-09-19 09:40:00+00:00' WHERE JourneyId = 'journey:D-2';
            """);

        await migrator.MigrateAsync(Migration, Token);

        Assert.Equal(
            "backfill:VK-02:journey:D-2|VK-02|TRANSPORT|journey:D-2|2026-09-19 09:01:00+00:00|2026-09-19 09:40:00+00:00|CLAIM_GONE_BEFORE_MIGRATION",
            Assert.Single(await RecordsAsync(fixture.Connection), row => row.Contains("journey:D-2", StringComparison.Ordinal)));
        await using ControlServerDbContext read = fixture.NewContext();
        await VehicleOccupancyAssertions.AssertOpenClaimRecordsAndPurposeClaimsMatchAsync(read);
    }

    /// <summary>
    /// 票面第 6 条：<c>Batch7MigrationDisciplineTests</c> 那条护栏防的是「脚本直查租约表时找不到列」。本票同时改掉了全部读者，
    /// 所以改由这一条防：仓里不再有任何东西按名字读退役的表与列，也就没有读者会因为它们没了而静默读空。
    /// </summary>
    /// <remarks>
    /// 按文本检索、不分大小写，SQL 字符串与注释都算。放过的只有两类，逐个点名：迁移本身（历史，不能改），以及必须在删表之前的
    /// 库上造数据、断言删表过程的迁移测试。
    /// </remarks>
    [Fact]
    public void NothingUnderScriptsTestsSrcOrToolsNamesTheRetiredOccupancyAnyMore()
    {
        string root = RepositoryRoot();
        string[] allowed =
        [
            // The migrations are history: every one from the lease's creation to its drop names it.
            "src/ControlServer.Infrastructure/Persistence/Migrations/",
            // Seeds and asserts schemas from before the drop.
            "tests/ControlServer.Tests/Batch7MigrationDisciplineTests.cs",
            "tests/ControlServer.Tests/Batch8OccupancyRetirementMigrationTests.cs",
            "tests/ControlServer.Tests/Batch7JourneyFixture.cs",
        ];
        string[] offenders =
        [
            .. ((string[])["scripts", "tests", "src", "tools"])
                .Select(directory => Path.Combine(root, directory))
                .Where(Directory.Exists)
                .SelectMany(directory => Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
                .Where(path => !path.Contains("/bin/", StringComparison.Ordinal) && !path.Contains("/obj/", StringComparison.Ordinal))
                .Where(path => !allowed.Any(prefix => path.StartsWith(prefix, StringComparison.Ordinal)))
                .Where(path => IsText(path))
                .Where(path =>
                {
                    string text = File.ReadAllText(Path.Combine(root, path));
                    return RetiredNames.Any(name => text.Contains(name, StringComparison.OrdinalIgnoreCase));
                })
                .Order(StringComparer.Ordinal),
        ];

        Assert.Empty(offenders);
        // The check itself finds what it looks for: the migration that drops the table names it.
        Assert.Contains(
            "VehicleDispatchLeases",
            File.ReadAllText(Path.Combine(
                root, "src/ControlServer.Infrastructure/Persistence/Migrations/20260929070322_Batch8RetireOldVehicleOccupancy.cs")),
            StringComparison.Ordinal);

        static bool IsText(string path) =>
            Path.GetExtension(path).ToLowerInvariant() is ".cs" or ".ps1" or ".psm1" or ".psd1" or ".py" or ".md" or ".json"
                or ".txt" or ".sql" or ".sh" or ".yml" or ".yaml" or ".csproj" or ".props" or ".targets" or ".html" or ".js";
    }

    /// <summary>
    /// 删表之前那个迁移的库，走真实受理与释放路径种了四趟旅程：D-1、D-2 在途；D-3 正常卸完并关闭；D-4 卸完了、旅程还没写
    /// Completed、订单占用还开着（旧版晚一轮放的那一轮）。D-3 与 D-4 的订单占用按旧版写过。
    /// </summary>
    private static async Task<Batch7JourneyFixture> SeededAsync()
    {
        Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync(migrate: false);
        await fixture.Context.GetService<IMigrator>().MigrateAsync(Batch7MigrationDisciplineTests.LastMigrationWithTheLeases, Token);
        DateTimeOffset now = Batch7JourneyFixture.Now;
        await using (await Batch7JourneyFixture.WriteLeasesTheWayTheOldVersionDidAsync(fixture.Connection))
        {
            string[][] journeys = [["D-1", "AGV-01", "VK-01"], ["D-2", "AGV-02", "VK-02"], ["D-3", "AGV-03", "VK-03"], ["D-4", "AGV-04", "VK-04"]];
            for (int index = 0; index < journeys.Length; index++)
            {
                await Batch7JourneyFixture.AcceptAsync(
                    fixture.Context, journeys[index][0], journeys[index][1], journeys[index][2], now.AddMinutes(index));
                await fixture.RenewContextAsync();
            }
            await Batch7JourneyFixture.CompleteByUnloadAsync(fixture.Context, "D-3", now.AddMinutes(10));
            await fixture.RenewContextAsync();
            await Batch7JourneyFixture.CompleteByUnloadAsync(fixture.Context, "D-4", now.AddMinutes(11));
            await fixture.RenewContextAsync();
        }
        // What the old dispatch round and the old engine wrote on the pickup orders: D-3's journey closed and released its
        // order occupancy; D-4's unload released the claim and the lease, and its journey waits for the next round.
        await ExecuteAsync(
            fixture.Connection,
            """
            UPDATE OrderIntents SET VehicleOccupancyClaimedAt = '2026-09-19 09:02:00+00:00',
                                    VehicleOccupancyReleasedAt = '2026-09-19 09:12:00+00:00'
            WHERE UpperId = 'W2G-D-3-PICKUP-1';
            UPDATE JourneyRuntimes SET Stage = 'Completed' WHERE JourneyId = 'journey:D-3';
            UPDATE OrderIntents SET VehicleOccupancyClaimedAt = '2026-09-19 09:03:00+00:00'
            WHERE UpperId = 'W2G-D-4-PICKUP-1';
            UPDATE OrderIntents SET VehicleOccupancyClaimedAt = '2026-09-19 09:00:00+00:00' WHERE UpperId = 'W2G-D-1-PICKUP-1';
            """);
        return fixture;
    }

    private static async Task AssertRefusedAsync(Batch7JourneyFixture fixture, string[] named)
    {
        Dictionary<string, string[]> columnsBefore = await ColumnsOfEveryTableAsync(fixture.Connection);
        Dictionary<string, string[]> rowsBefore = await DumpEveryTableAsync(fixture.Connection);

        SqliteException refusal = await Assert.ThrowsAsync<SqliteException>(() => fixture.Context.Database.MigrateAsync(Token));

        Assert.Equal(1811, refusal.SqliteExtendedErrorCode); // SQLITE_CONSTRAINT_TRIGGER: the guard's RAISE(ABORT, ...)
        Assert.Contains("Batch8RetireOldVehicleOccupancy refused, nothing was dropped", refusal.Message, StringComparison.Ordinal);
        Assert.Contains($" {named.Length} unfinished old vehicle occupancy row(s)", refusal.Message, StringComparison.Ordinal);
        foreach (string row in named)
        {
            Assert.Contains(row, refusal.Message, StringComparison.Ordinal);
        }
        // Whole: every table, column and row as it was, and the migration not recorded as applied.
        Assert.Equal(columnsBefore, await ColumnsOfEveryTableAsync(fixture.Connection));
        Dictionary<string, string[]> rowsAfter = await DumpEveryTableAsync(fixture.Connection);
        foreach ((string table, string[] rows) in rowsBefore)
        {
            Assert.Equal(rows, rowsAfter[table]);
        }
        Assert.Equal(
            Batch7MigrationDisciplineTests.LastMigrationWithTheLeases,
            (await fixture.Context.Database.GetAppliedMigrationsAsync(Token)).Last());
    }

    private static async Task<string[]> RecordsAsync(SqliteConnection connection) =>
        await ScalarsAsync(
            connection,
            """
            SELECT RecordId || '|' || VehicleKey || '|' || Purpose || '|' || JourneyId || '|' || AcquiredAt || '|'
                   || coalesce(ReleasedAt, 'NULL') || '|' || coalesce(ReleaseReason, 'NULL')
            FROM VehiclePurposeClaimRecords ORDER BY RecordId
            """);

    private static async Task<string[]> IndexNamesAsync(SqliteConnection connection) =>
        await ScalarsAsync(connection, "SELECT name FROM sqlite_master WHERE type = 'index' ORDER BY name");

    private static async Task<Dictionary<string, string[]>> ColumnsOfEveryTableAsync(SqliteConnection connection)
    {
        Dictionary<string, string[]> columns = new(StringComparer.Ordinal);
        foreach (string table in await TablesAsync(connection))
        {
            columns[table] = await ScalarsAsync(
                connection, $"SELECT name FROM pragma_table_info('{table}') ORDER BY cid");
        }
        return columns;
    }

    private static async Task<Dictionary<string, string[]>> DumpEveryTableAsync(SqliteConnection connection)
    {
        Dictionary<string, string[]> rows = new(StringComparer.Ordinal);
        foreach (string table in await TablesAsync(connection))
        {
            rows[table] = await Batch7JourneyFixture.DumpAsync(connection, table);
        }
        return rows;
    }

    private static Task<string[]> TablesAsync(SqliteConnection connection) =>
        ScalarsAsync(
            connection,
            "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' AND name <> '__EFMigrationsHistory' ORDER BY name");

    private static async Task<string[]> ScalarsAsync(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        List<string> values = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(Token);
        while (await reader.ReadAsync(Token))
        {
            values.Add(Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture)!);
        }
        return [.. values];
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(Token);
    }

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
}
