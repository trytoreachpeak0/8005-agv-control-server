using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Tests;

/// <summary>
/// 批次 7 建表票（control-server#206）各测试类共用的受理夹具：一个迁移过的内存 SQLite 库，和走真实受理路径
/// （<see cref="WireToGateStore"/>）种出旅程的几个辅助函数。不手写旅程行——回填与释放的测试要的正是受理路径写出的那些行。
/// </summary>
internal sealed class Batch7JourneyFixture : IAsyncDisposable
{
    internal static readonly DateTimeOffset Now = new(2026, 9, 19, 9, 0, 0, TimeSpan.Zero);

    private Batch7JourneyFixture(SqliteConnection connection)
    {
        Connection = connection;
        Context = NewContext();
    }

    public SqliteConnection Connection { get; }

    public ControlServerDbContext Context { get; private set; }

    /// <param name="migrate">
    /// The schema of every migration, copied from <see cref="MigratedDatabaseTemplate"/>; <c>false</c> leaves the database
    /// empty for a test that migrates it step by step.
    /// </param>
    public static async Task<Batch7JourneyFixture> CreateAsync(bool migrate = true)
    {
        SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        Batch7JourneyFixture fixture = new(connection);
        if (migrate)
        {
            await MigratedDatabaseTemplate.ApplyAsync(fixture.Context.Database, TestContext.Current.CancellationToken);
        }
        return fixture;
    }

    /// <summary>
    /// The schema of every migration, written by running them rather than copied from the template: for a test about the
    /// migrations themselves, or one that migrates down and up again (control-server#553).
    /// </summary>
    public static async Task<Batch7JourneyFixture> CreateMigratedForRealAsync()
    {
        Batch7JourneyFixture fixture = await CreateAsync(migrate: false);
        await fixture.Context.Database.MigrateAsync(TestContext.Current.CancellationToken);
        return fixture;
    }

    /// <summary>A second context on the same database, sharing nothing tracked with <see cref="Context"/>.</summary>
    public ControlServerDbContext NewContext(params Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor[] interceptors)
    {
        DbContextOptionsBuilder<ControlServerDbContext> builder =
            new DbContextOptionsBuilder<ControlServerDbContext>().UseSqlite(Connection);
        if (interceptors.Length > 0)
        {
            builder.AddInterceptors(interceptors);
        }
        return new ControlServerDbContext(builder.Options);
    }

    /// <summary>Drops everything <see cref="Context"/> tracks by replacing it.</summary>
    public async Task RenewContextAsync()
    {
        await Context.DisposeAsync();
        Context = NewContext();
    }

    /// <summary>
    /// Lets today's acceptance and release paths seed a database migrated only as far as a migration from before batch 8-16
    /// (control-server#387), and leaves behind what the version of that time wrote: a dispatch lease per claim, not a record.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Today's paths write the claim's record into <c>VehiclePurposeClaimRecords</c>, which a database from before
    /// control-server#386 does not have, and no longer write the lease. So a stand-in record table is created for the
    /// seeding, and on dispose every record becomes the lease that version would have written beside the claim -- same
    /// journey, vehicle and moments, the demand the journey is anchored on -- and the stand-in is dropped. On a database
    /// that has had the record table since #386 (empty until #387, which nothing wrote before) the records the seeding wrote
    /// are removed the same way.
    /// </para>
    /// <para>
    /// Only for migration tests that must seed a schema from before the lease was retired. Everything else seeds the
    /// current schema and reads the records.
    /// </para>
    /// </remarks>
    internal static async Task<IAsyncDisposable> WriteLeasesTheWayTheOldVersionDidAsync(SqliteConnection connection)
    {
        bool hadRecords = await ScalarAsync(
            connection, "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = 'VehiclePurposeClaimRecords'") > 0;
        if (!hadRecords)
        {
            await ExecuteAsync(
                connection,
                """
                CREATE TABLE "VehiclePurposeClaimRecords" (
                    "RecordId" TEXT NOT NULL PRIMARY KEY, "VehicleKey" TEXT NOT NULL, "Purpose" TEXT NOT NULL,
                    "JourneyId" TEXT NOT NULL, "AcquiredAt" TEXT NOT NULL, "ReleasedAt" TEXT NULL, "ReleaseReason" TEXT NULL)
                """);
        }
        long before = hadRecords
            ? await ScalarAsync(connection, """SELECT coalesce(max(rowid), 0) FROM "VehiclePurposeClaimRecords" """)
            : 0;
        return new LeaseEra(connection, hadRecords, before);
    }

    private sealed class LeaseEra(SqliteConnection connection, bool hadRecords, long recordsBefore) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await ExecuteAsync(
                connection,
                """
                INSERT INTO "VehicleDispatchLeases" ("JourneyId", "DemandId", "VehicleKey", "AcquiredAt", "ReleasedAt")
                SELECT r."JourneyId",
                       coalesce((SELECT j."DemandId" FROM "JourneyRuntimes" AS j WHERE j."JourneyId" = r."JourneyId"),
                                substr(r."JourneyId", length('journey:') + 1)),
                       r."VehicleKey", r."AcquiredAt", r."ReleasedAt"
                FROM "VehiclePurposeClaimRecords" AS r
                WHERE r.rowid > $before
                """.Replace("$before", recordsBefore.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal));
            await ExecuteAsync(
                connection,
                hadRecords
                    ? $"""DELETE FROM "VehiclePurposeClaimRecords" WHERE rowid > {recordsBefore.ToString(System.Globalization.CultureInfo.InvariantCulture)}"""
                    : """DROP TABLE "VehiclePurposeClaimRecords" """);
        }
    }

    private static async Task<long> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    internal static AcceptedDemandSnapshot Snapshot(string demandId, DateTimeOffset acceptedAt) =>
        new(demandId, $"SUBLOT-{demandId}|WIRE_TO_GATE", 7, "history-1", 21, acceptedAt,
            SeriesId: $"SERIES-{demandId}", WorkType: "WIRE_TO_GATE", Sublot: $"SUBLOT-{demandId}", Generation: 1,
            CreatedAt: acceptedAt.AddMinutes(-5), ValueObservedAt: acceptedAt.AddMinutes(-1),
            ValuePollTraceId: $"TRACE-{demandId}", ValueProjectionCommitId: $"COMMIT-{demandId}");

    /// <summary>
    /// A plan whose derived ids come from <see cref="JourneyPlanBuilder.StableGuid"/> exactly as the runtime derives them,
    /// so what the store stores is what the runtime would have stored.
    /// </summary>
    internal static JourneyExecutionPlan Plan(
        string demandId, string agvId, string vehicleKey, DateTimeOffset createdAt, long dispatchGeneration = 1) =>
        new(
            agvId,
            vehicleKey,
            1,
            25,
            "MAP-25",
            "MAP-25-WIRE_TO_GATE",
            $"route-{demandId}",
            "ST-PICKUP",
            101,
            "ST-GATE",
            202,
            2,
            [1, 2],
            JourneyPlanBuilder.StableGuid(demandId, "operation-session"),
            JourneyPlanBuilder.StableGuid(demandId, "pickup-leg"),
            $"W2G-{demandId}-PICKUP-{dispatchGeneration}",
            JourneyPlanBuilder.StableGuid(demandId, "gate-leg"),
            $"W2G-{demandId}-GATE-{dispatchGeneration}",
            dispatchGeneration,
            createdAt);

    internal static async Task<JourneyExecutionPlan> AcceptAsync(
        ControlServerDbContext context, string demandId, string agvId, string vehicleKey, DateTimeOffset at)
    {
        JourneyExecutionPlan plan = Plan(demandId, agvId, vehicleKey, at);
        await using (await WithTodaysTrailingColumnsAsync(context))
        {
            await new WireToGateStore(context).AcceptWithOrderIntentAsync(
                Snapshot(demandId, at),
                JourneyPlanBuilder.PickupIntent(plan, demandId, at),
                plan,
                TestContext.Current.CancellationToken);
        }
        return plan;
    }

    /// <summary>Ends a journey the way a successful unload does through the direct completion entry.</summary>
    internal static async Task CompleteByUnloadAsync(ControlServerDbContext context, string demandId, DateTimeOffset at)
    {
        await using (await WithTodaysTrailingColumnsAsync(context))
        {
            await new WireToGateStore(context).CompleteDemandAfterUnloadAsync(
                $"UNLOAD-{demandId}",
                demandId,
                $"SUBLOT-{demandId}|WIRE_TO_GATE",
                7,
                [new SlotPhysicalEvidence(1, SlotBusinessState.Empty, true, true)],
                "all-empty-locked-output-reset",
                at,
                TestContext.Current.CancellationToken);
        }
    }

    /// <summary>
    /// The columns later migrations appended to tables the acceptance path reads and writes, each with the definition its
    /// migration added. Batch 9 (control-server#399) is the first to append to them since these fixtures began seeding
    /// databases at earlier migrations.
    /// </summary>
    private static readonly (string Table, string Column, string Definition)[] TodaysTrailingColumns =
    [
        ("OrderIntents", "OrderShape", "TEXT NOT NULL DEFAULT 'SINGLE_MOVE'"),
        ("JourneyRuntimes", "ChargingPolicyVersion", "INTEGER NULL"),
        ("JourneyRuntimes", "PublishedBatteryState", "TEXT NULL"),
        ("StationExclusivities", "ChargerRosterVersion", "INTEGER NULL"),
        ("StationExclusivityRecords", "ChargerRosterVersion", "INTEGER NULL"),
    ];

    /// <summary>
    /// Lets today's model read and write a database migrated only to an earlier migration: every column in
    /// <see cref="TodaysTrailingColumns"/> that such a database lacks is added for the duration, and dropped again on
    /// dispose with SQLite's own <c>DROP COLUMN</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each is its table's last column, so the drop gives back the stored table definition exactly as it was, and what a
    /// migration test then compares -- schema text, columns, rows -- is what the earlier migration left. The values the
    /// seeding put there go with the column: they are the default or null, the same the migration would give them.
    /// </para>
    /// <para>
    /// On a database at the current schema it adds and drops nothing.
    /// </para>
    /// <para>
    /// The connection is opened through the context and handed back on dispose: a context built from a connection string
    /// keeps its connection closed between operations, and EF closes on its own only what it opened itself.
    /// </para>
    /// </remarks>
    internal static async Task<IAsyncDisposable> WithTodaysTrailingColumnsAsync(ControlServerDbContext context)
    {
        await context.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        SqliteConnection connection = (SqliteConnection)context.Database.GetDbConnection();
        List<(string Table, string Column)> added = [];
        foreach ((string table, string column, string definition) in TodaysTrailingColumns)
        {
            bool tableExists = await ScalarAsync(
                connection, $"SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = '{table}'") > 0;
            if (!tableExists || await ScalarAsync(
                    connection, $"SELECT count(*) FROM pragma_table_info('{table}') WHERE name = '{column}'") > 0)
            {
                continue;
            }
            await ExecuteAsync(connection, $"""ALTER TABLE "{table}" ADD COLUMN "{column}" {definition}""");
            added.Add((table, column));
        }
        return new TrailingColumns(context, connection, added);
    }

    private sealed class TrailingColumns(
        ControlServerDbContext context, SqliteConnection connection, List<(string Table, string Column)> added)
        : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            for (int index = added.Count - 1; index >= 0; index--)
            {
                await ExecuteAsync(connection, $"""ALTER TABLE "{added[index].Table}" DROP COLUMN "{added[index].Column}" """);
            }
            await context.Database.CloseConnectionAsync();
        }
    }

    /// <summary>Every row of a table, all columns, as SQLite's own literal for each value, sorted.</summary>
    internal static async Task<string[]> DumpAsync(SqliteConnection connection, string table)
    {
        List<string> columns = [];
        await using (SqliteCommand info = connection.CreateCommand())
        {
            info.CommandText = $"PRAGMA table_info('{table}')";
            await using SqliteDataReader reader = await info.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            {
                columns.Add(reader.GetString(1));
            }
        }
        await using SqliteCommand select = connection.CreateCommand();
        select.CommandText =
            $"SELECT {string.Join(" || '|' || ", columns.Select(name => $"'{name}=' || quote(\"{name}\")"))} FROM \"{table}\"";
        List<string> rows = [];
        await using SqliteDataReader rowsReader = await select.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await rowsReader.ReadAsync(TestContext.Current.CancellationToken))
        {
            rows.Add(rowsReader.GetString(0));
        }
        rows.Sort(StringComparer.Ordinal);
        return [.. rows];
    }

    public async ValueTask DisposeAsync()
    {
        await Context.DisposeAsync();
        await Connection.DisposeAsync();
    }
}
