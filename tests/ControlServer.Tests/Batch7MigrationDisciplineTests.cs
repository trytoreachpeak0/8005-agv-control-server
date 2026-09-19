using System.Globalization;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ControlServer.Tests;

/// <summary>
/// 批次 7 只有一个建表迁移，由建表票 control-server#206 独占；批次7-02～7-12 零迁移（规格 18.2、第 22 节补记）。
/// </summary>
/// <remarks>
/// <para>
/// 本迁移按<b>完整后缀</b>认出，不用 <c>Contains("Batch7")</c>：排在它后面的迁移通道遗留票 control-server#199（审计触发器）与
/// control-server#186（Map 级改名检测）的迁移名也可能带这个字样，按子串认会把它们当成第二个批次 7 迁移。
/// </para>
/// <para>
/// 它之后允许有哪些迁移，按名字点在 <see cref="MigrationsAfterBatch7"/>：本票合入时为空，#199、#186 各自合入时加一行。
/// 批次 3 那份纪律测试的 <c>MigrationsAfterBatch3</c> 名单也点了本迁移的名。
/// </para>
/// </remarks>
public sealed class Batch7MigrationDisciplineTests
{
    internal const string Batch6Migration = "20260919021150_Batch6TaskTypeStationBindings";
    internal const string Batch7MigrationSuffix = "_Batch7MultiDemandJourneyPersistence";

    /// <summary>批次 7 迁移之后允许存在的迁移，按名字点出来。</summary>
    private static readonly string[] MigrationsAfterBatch7 = [];

    [Fact]
    public async Task Batch7AddsExactlyOneMigrationStraightAfterBatch6AndOnlyNamedOnesFollowIt()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync(migrate: false);
        string[] migrations = [.. fixture.Context.Database.GetMigrations()];

        string batch7 = Assert.Single(
            migrations, name => name.EndsWith(Batch7MigrationSuffix, StringComparison.Ordinal));
        int index = Array.IndexOf(migrations, batch7);
        Assert.Equal(Batch6Migration, migrations[index - 1]);
        Assert.Equal(MigrationsAfterBatch7, migrations[(index + 1)..]);
    }

    [Fact]
    public async Task EveryJourneyInFlightIsBackFilledAsOnePickupStopOneUnloadStopAndOneDemandWithItsOwnIds()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        await SeedFourJourneysThenMigrateDownToBatch6Async(fixture);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Dictionary<string, string[]> before = await DumpEveryTableAsync(fixture.Connection);
        Assert.Equal(4, before["JourneyRuntimes"].Length);
        Assert.NotEmpty(before["ProtocolOutbox"]);

        await fixture.Context.Database.MigrateAsync(cancellationToken);

        // Existing tables, restricted to the columns they had at batch 6, row for row the same -- ProtocolOutbox included,
        // so what a reconnect replays is the original wire.
        foreach ((string table, string[] rows) in before)
        {
            Assert.Equal(rows, await DumpAsync(fixture.Connection, table, await ColumnsAtBatch6Async(table)));
        }

        await using ControlServerDbContext read = fixture.NewContext();
        JourneyRuntimeRow[] journeys = await read.JourneyRuntimes.AsNoTracking().ToArrayAsync(cancellationToken);
        JourneyStopRow[] stops = await read.Set<JourneyStopRow>().AsNoTracking().ToArrayAsync(cancellationToken);
        JourneyDemandRow[] demands = await read.Set<JourneyDemandRow>().AsNoTracking().ToArrayAsync(cancellationToken);
        foreach (JourneyRuntimeRow journey in journeys)
        {
            Assert.Equal("journey:" + journey.DemandId, journey.JourneyId);
            JourneyStopRow[] own = [.. stops.Where(stop => stop.JourneyId == journey.JourneyId).OrderBy(stop => stop.Sequence)];
            Assert.Equal(2, own.Length);
            JourneyStopRow pickup = own[0];
            JourneyStopRow unload = own[1];
            Assert.Equal(
                [journey.JourneyId + "|PICKUP", "PICKUP", journey.PickupStationId, Text(journey.PickupStationRiotId),
                 journey.DispatchZone, journey.OperationSessionId, journey.PickupMovementLegId, journey.PickupUpperId,
                 journey.VehicleBusinessMessageId, journey.WorklistMessageId, journey.PlanMessageId,
                 journey.SublotRequestMessageId, journey.PreDepartureSafetyCheckMessageId, journey.PreDepartureSafetyCheckId],
                StopFields(pickup));
            Assert.Equal(
                [journey.JourneyId + "|UNLOAD", "UNLOAD", journey.GateStationId, Text(journey.GateStationRiotId),
                 journey.DispatchZone, journey.OperationSessionId, journey.GateMovementLegId, journey.GateUpperId,
                 journey.GateVehicleBusinessMessageId, journey.GateWorklistMessageId, journey.GatePlanMessageId,
                 "-", "-", "-"],
                StopFields(unload));

            JourneyDemandRow demand = Assert.Single(demands, row => row.JourneyId == journey.JourneyId);
            Assert.Equal(
                [journey.DemandId, pickup.StopId, unload.StopId, Text(journey.ExpectedBasketCount),
                 journey.TargetSlotsJson, journey.LoadSlotOperationAttemptId, journey.LoadCommandMessageId,
                 journey.UnloadSlotOperationAttemptId, journey.UnloadCommandMessageId, journey.DispatchZone,
                 Text(journey.DispatchGeneration)],
                [demand.DemandId, demand.PickupStopId, demand.UnloadStopId, Text(demand.ExpectedBasketCount),
                 demand.TargetSlotsJson, demand.LoadSlotOperationAttemptId, demand.LoadCommandMessageId,
                 demand.UnloadSlotOperationAttemptId, demand.UnloadCommandMessageId, demand.DispatchZone,
                 Text(demand.DispatchGeneration)]);
            Assert.Equal(journey.CreatedAt, demand.AddedAt);
            Assert.Equal(journey.CreatedAt, pickup.CreatedAt);
            Assert.Null(demand.RemovedAt);
        }
        Assert.Equal(8, stops.Length);
        Assert.Equal(4, demands.Length);

        // Where each journey stood, as its stops and its demand say it after the back-fill.
        Assert.Equal(
            [
                "D-A PICKUP=COMPLETED UNLOAD=COMPLETED demand=UNLOADED",
                "D-B PICKUP=ACTIVE UNLOAD=PENDING demand=PENDING_LOAD",
                "D-C PICKUP=COMPLETED UNLOAD=PENDING demand=LOADED",
                "D-D PICKUP=ACTIVE UNLOAD=PENDING demand=PENDING_LOAD",
            ],
            journeys.OrderBy(journey => journey.DemandId, StringComparer.Ordinal).Select(journey =>
                $"{journey.DemandId} "
                + string.Join(" ", stops.Where(stop => stop.JourneyId == journey.JourneyId).OrderBy(stop => stop.Sequence)
                    .Select(stop => $"{stop.StopRole}={stop.Status}"))
                + $" demand={demands.Single(row => row.JourneyId == journey.JourneyId).Status}"));

        // One purpose claim per active lease, for the same journey; a released lease has none.
        VehicleDispatchLeaseRow[] leases = await read.VehicleDispatchLeases.AsNoTracking().ToArrayAsync(cancellationToken);
        VehiclePurposeClaimRow[] claims = await read.Set<VehiclePurposeClaimRow>().AsNoTracking().ToArrayAsync(cancellationToken);
        Assert.Equal(
            leases.Where(lease => lease.ReleasedAt == null)
                .Select(lease => $"{lease.VehicleKey} TRANSPORT {lease.JourneyId} {lease.AcquiredAt:O}")
                .Order(StringComparer.Ordinal),
            claims.Select(claim => $"{claim.VehicleKey} {claim.Purpose} {claim.JourneyId} {claim.ClaimedAt:O}")
                .Order(StringComparer.Ordinal));
        Assert.Equal(3, claims.Length);
        Assert.All(leases, lease => Assert.Equal("journey:" + lease.DemandId, lease.JourneyId));

        // The counter holds each vehicle's highest stored revision on each stream.
        Assert.Equal(
            ["agv-01 3/3/4", "agv-02 1/1/1", "agv-03 1/1/1"],
            (await read.Set<VehicleSnapshotRevisionRow>().AsNoTracking().ToArrayAsync(cancellationToken))
                .OrderBy(row => row.AgvId, StringComparer.Ordinal)
                .Select(row => $"{row.AgvId} {row.VehicleBusinessRevision}/{row.WorklistRevision}/{row.PlanRevision}"));
    }

    private static string Text(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string[] StopFields(JourneyStopRow stop) =>
    [
        stop.StopId, stop.StopRole, stop.StationId, Text(stop.StationRiotId),
        stop.DispatchZone, stop.OperationSessionId, stop.MovementLegId, stop.UpperId,
        stop.VehicleBusinessMessageId, stop.WorklistMessageId, stop.PlanMessageId,
        stop.SublotRequestMessageId ?? "-", stop.DepartureSafetyCheckMessageId ?? "-", stop.DepartureSafetyCheckId ?? "-",
    ];

    /// <summary>
    /// Four journeys through the real acceptance path, in the four stages the ticket names -- two of them on one vehicle --
    /// plus two outbox rows, then the database brought down to batch 6.
    /// </summary>
    /// <remarks>
    /// The acceptance path today writes the batch 7 columns, so it cannot run against a batch 6 schema. The rows are
    /// therefore accepted on the current schema and taken to batch 6 by the migration's own Down(), which only drops what
    /// batch 7 added; that the result has exactly batch 6's columns is asserted here against a database migrated straight
    /// to batch 6.
    /// </remarks>
    private static async Task SeedFourJourneysThenMigrateDownToBatch6Async(Batch7JourneyFixture fixture)
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        DateTimeOffset now = Batch7JourneyFixture.Now;
        ControlServerDbContext context = fixture.Context;

        // agv-01: a completed journey, then a second one waiting for its sublot.
        await Batch7JourneyFixture.AcceptAsync(context, "D-A", "agv-01", "VK-01", now);
        await Batch7JourneyFixture.CompleteByUnloadAsync(context, "D-A", now.AddMinutes(10));
        (await context.JourneyRuntimes.SingleAsync(row => row.DemandId == "D-A", cancellationToken)).Stage =
            JourneyRuntimeStage.Completed;
        await context.SaveChangesAsync(cancellationToken);
        await Batch7JourneyFixture.AcceptAsync(context, "D-B", "agv-01", "VK-01", now.AddMinutes(11));
        (await context.JourneyRuntimes.SingleAsync(row => row.DemandId == "D-B", cancellationToken)).Stage =
            JourneyRuntimeStage.AwaitingSublot;

        // agv-02: loaded, cleared to leave, on its way to the gate.
        await Batch7JourneyFixture.AcceptAsync(context, "D-C", "agv-02", "VK-02", now.AddMinutes(1));
        JourneyRuntimeRow toGate = await context.JourneyRuntimes.SingleAsync(row => row.DemandId == "D-C", cancellationToken);
        toGate.Stage = JourneyRuntimeStage.AwaitingGateArrival;
        await context.SaveChangesAsync(cancellationToken);
        await new WireToGateStore(context).AuthorizeMovementAsync(
            JourneyPlanBuilder.GateIntent(toGate, now.AddMinutes(5)),
            new SafetyCheckObservation(toGate.PreDepartureSafetyCheckId, 1, true, now.AddMinutes(5), now.AddMinutes(6)),
            now.AddMinutes(5),
            cancellationToken);

        // agv-03: blocked at its pickup.
        await Batch7JourneyFixture.AcceptAsync(context, "D-D", "agv-03", "VK-03", now.AddMinutes(2));
        JourneyRuntimeRow blocked = await context.JourneyRuntimes.SingleAsync(row => row.DemandId == "D-D", cancellationToken);
        blocked.Stage = JourneyRuntimeStage.Blocked;
        blocked.SetBlockReason("LOAD_RESULT_REQUIRES_RECOVERY", now.AddMinutes(3));

        context.ProtocolOutbox.AddRange(
            new ProtocolOutboxRow
            {
                MessageId = toGate.GateVehicleBusinessMessageId,
                MessageType = "VehicleBusinessStateSnapshot",
                PayloadJson = "{\"revision\":1}",
                CreatedAt = now.AddMinutes(5)
            },
            new ProtocolOutboxRow
            {
                MessageId = blocked.SublotRequestMessageId,
                MessageType = "SublotEntryRequest",
                PayloadJson = "{}",
                CreatedAt = now.AddMinutes(2),
                AcknowledgedAt = now.AddMinutes(3)
            });
        await context.SaveChangesAsync(cancellationToken);
        await fixture.RenewContextAsync();

        await fixture.Context.GetService<IMigrator>().MigrateAsync(Batch6Migration, cancellationToken);

        foreach (string table in await ReadTableNamesAsync(fixture.Connection))
        {
            Assert.Equal(await ColumnsAtBatch6Async(table), await ColumnsAsync(fixture.Connection, table));
        }
    }

    private static readonly Lazy<Task<Dictionary<string, string[]>>> Batch6Columns = new(ReadBatch6ColumnsAsync);

    /// <summary>A table's columns as a database migrated straight to batch 6 has them.</summary>
    private static async Task<string[]> ColumnsAtBatch6Async(string table) => (await Batch6Columns.Value)[table];

    private static async Task<Dictionary<string, string[]>> ReadBatch6ColumnsAsync()
    {
        await using Batch7JourneyFixture batch6 = await Batch7JourneyFixture.CreateAsync(migrate: false);
        await batch6.Context.GetService<IMigrator>().MigrateAsync(Batch6Migration, CancellationToken.None);
        Dictionary<string, string[]> columns = new(StringComparer.Ordinal);
        foreach (string table in await ReadTableNamesAsync(batch6.Connection))
        {
            columns[table] = await ColumnsAsync(batch6.Connection, table);
        }
        return columns;
    }

    private static async Task<Dictionary<string, string[]>> DumpEveryTableAsync(SqliteConnection connection)
    {
        Dictionary<string, string[]> dump = new(StringComparer.Ordinal);
        foreach (string table in await ReadTableNamesAsync(connection))
        {
            if (table != "__EFMigrationsHistory")
            {
                dump[table] = await DumpAsync(connection, table, await ColumnsAsync(connection, table));
            }
        }
        return dump;
    }

    /// <summary>Every row of a table, over the named columns, as SQLite's own literal for each value, sorted.</summary>
    private static async Task<string[]> DumpAsync(SqliteConnection connection, string table, string[] columns)
    {
        await using SqliteCommand select = connection.CreateCommand();
        select.CommandText =
            $"SELECT {string.Join(" || '|' || ", columns.Select(name => $"'{name}=' || quote(\"{name}\")"))} FROM \"{table}\"";
        List<string> rows = [];
        await using SqliteDataReader reader = await select.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            rows.Add(reader.GetString(0));
        }
        rows.Sort(StringComparer.Ordinal);
        return [.. rows];
    }

    private static async Task<string[]> ColumnsAsync(SqliteConnection connection, string table)
    {
        List<string> columns = [];
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info('{table}')";
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            columns.Add(reader.GetString(1));
        }
        columns.Sort(StringComparer.Ordinal);
        return [.. columns];
    }

    private static async Task<string[]> ReadTableNamesAsync(SqliteConnection connection)
    {
        List<string> tables = [];
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%'";
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            tables.Add(reader.GetString(0));
        }
        tables.Sort(StringComparer.Ordinal);
        return [.. tables];
    }

    [Fact]
    public async Task MigratingTheBackFilledDatabaseDownToBatch6AndUpAgainLeavesSchemaAndRowsByteForByteTheSame()
    {
        // Known limit, stated in the PR: once a journey carries a second demand or a stop the single-demand shape cannot
        // hold, Down() loses it -- JourneyRuntimes only has columns for one pickup and one gate. With nothing but
        // single-demand journeys, which is all this ticket can produce, the round trip is exact.
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        await SeedFourJourneysThenMigrateDownToBatch6Async(fixture);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await fixture.Context.Database.MigrateAsync(cancellationToken);
        string[] schemaBefore = await ReadSchemaAsync(fixture.Connection);
        Dictionary<string, string[]> rowsBefore = await DumpEveryTableAsync(fixture.Connection);

        await fixture.Context.GetService<IMigrator>().MigrateAsync(Batch6Migration, cancellationToken);
        await fixture.Context.Database.MigrateAsync(cancellationToken);

        Assert.Equal(schemaBefore, await ReadSchemaAsync(fixture.Connection));
        Dictionary<string, string[]> rowsAfter = await DumpEveryTableAsync(fixture.Connection);
        Assert.Equal(rowsBefore.Keys.Order(StringComparer.Ordinal), rowsAfter.Keys.Order(StringComparer.Ordinal));
        foreach ((string table, string[] rows) in rowsBefore)
        {
            Assert.Equal(rows, rowsAfter[table]);
        }
        Assert.Equal(8, rowsAfter["JourneyStops"].Length);
    }

    /// <summary>Every table, index and trigger as SQLite stores its definition.</summary>
    private static async Task<string[]> ReadSchemaAsync(SqliteConnection connection)
    {
        List<string> schema = [];
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT type || ' ' || name || ': ' || coalesce(sql, '') FROM sqlite_master WHERE name NOT LIKE 'sqlite_%'";
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            schema.Add(reader.GetString(0));
        }
        schema.Sort(StringComparer.Ordinal);
        return [.. schema];
    }

    [Fact]
    public async Task JourneyRuntimesAndVehicleDispatchLeasesKeepEveryColumnTheyHadAtBatch6SoScriptsStillFindThem()
    {
        // scripts/ and the G3 scenarios query these two tables directly -- WHERE DemandId = ..., SELECT DemandId, VehicleKey,
        // AcquiredAt, ReleasedAt, SELECT * -- so the key change must only ever add columns.
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();

        foreach (string table in (string[])["JourneyRuntimes", "VehicleDispatchLeases", "JourneyBacklog"])
        {
            string[] after = await ColumnsAsync(fixture.Connection, table);
            Assert.Empty((await ColumnsAtBatch6Async(table)).Except(after, StringComparer.Ordinal));
            Assert.Contains("DemandId", after);
        }
        Assert.Contains("JourneyId", await ColumnsAsync(fixture.Connection, "JourneyRuntimes"));
        Assert.Contains("JourneyId", await ColumnsAsync(fixture.Connection, "VehicleDispatchLeases"));
    }

    [Fact]
    public async Task MigratingAnEmptyDatabaseCreatesEveryBatch7TableWithItsKeysAndIndexes()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();

        Assert.Equal(
            [
                "DispatchZoneParameterVersions pk=Version",
                "DispatchZoneParameters pk=Version,DispatchZone",
                "JourneyBacklog pk=DemandId",
                "JourneyDemands pk=JourneyId,DemandId",
                "JourneyRuntimes pk=JourneyId",
                "JourneyStops pk=StopId",
                "TransportDemandSuppressions pk=TransportDemandKey",
                "VehicleDispatchLeases pk=JourneyId",
                "VehiclePurposeClaims pk=VehicleKey",
                "VehicleSnapshotRevisions pk=AgvId",
            ],
            await Task.WhenAll(
                ((string[])["DispatchZoneParameterVersions", "DispatchZoneParameters", "JourneyBacklog", "JourneyDemands",
                    "JourneyRuntimes", "JourneyStops", "TransportDemandSuppressions", "VehicleDispatchLeases",
                    "VehiclePurposeClaims", "VehicleSnapshotRevisions"])
                .Select(async table => $"{table} pk={string.Join(",", await PrimaryKeyAsync(fixture.Connection, table))}")));

        Assert.Equal(
            [
                "IX_JourneyDemands_DemandId unique on DemandId where RemovedAt IS NULL",
                "IX_JourneyRuntimes_DemandId on DemandId",
                "IX_JourneyStops_JourneyId_Sequence on JourneyId,Sequence",
                "IX_TransportDemandSuppressions_DemandId on DemandId",
                "IX_VehicleDispatchLeases_DemandId on DemandId",
                "IX_VehicleDispatchLeases_VehicleKey unique on VehicleKey where ReleasedAt IS NULL",
                "IX_VehiclePurposeClaims_JourneyId on JourneyId",
            ],
            await IndexesAsync(
                fixture.Connection,
                "JourneyDemands", "JourneyRuntimes", "JourneyStops", "TransportDemandSuppressions",
                "VehicleDispatchLeases", "VehiclePurposeClaims"));

        foreach ((string table, string column) in (ValueTuple<string, string>[])
                 [
                     ("JourneyRuntimes", "CargoHoldingStartedAt"), ("JourneyRuntimes", "LoadingPhaseState"),
                     ("JourneyRuntimes", "LoadingClosedReason"), ("JourneyRuntimes", "YieldTriggeredAt"),
                     ("JourneyRuntimes", "YieldTriggeredByVehicleKey"), ("JourneyRuntimes", "FullSlotPositionsJson"),
                     ("JourneyBacklog", "StarvationEscalatedAt"), ("JourneyBacklog", "StarvationEscalationParameterVersion"),
                     ("DispatchZoneParameters", "EnRouteAdditionMaxPathCostIncrease"),
                     ("DispatchZoneParameters", "StarvationThresholdSeconds"),
                 ])
        {
            Assert.True(await IsNullableAsync(fixture.Connection, table, column), $"{table}.{column} must be nullable.");
        }
    }

    [Fact]
    public async Task TheModelSnapshotMatchesTheModel()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync(migrate: false);

        Assert.False(fixture.Context.Database.HasPendingModelChanges());
    }

    private static async Task<string[]> PrimaryKeyAsync(SqliteConnection connection, string table)
    {
        List<(long Position, string Name)> key = [];
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info('{table}')";
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            long position = reader.GetInt64(reader.GetOrdinal("pk"));
            if (position > 0)
            {
                key.Add((position, reader.GetString(reader.GetOrdinal("name"))));
            }
        }
        return [.. key.OrderBy(column => column.Position).Select(column => column.Name)];
    }

    private static async Task<bool> IsNullableAsync(SqliteConnection connection, string table, string column)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT \"notnull\" FROM pragma_table_info('{table}') WHERE name = '{column}'";
        object? notNull = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(notNull);
        return Convert.ToInt64(notNull, CultureInfo.InvariantCulture) == 0;
    }

    /// <summary>Every named index on the tables, with its columns, uniqueness and filter as SQLite stores them.</summary>
    private static async Task<string[]> IndexesAsync(SqliteConnection connection, params string[] tables)
    {
        List<string> indexes = [];
        foreach (string table in tables)
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                $"SELECT name, sql FROM sqlite_master WHERE type = 'index' AND tbl_name = '{table}' AND sql IS NOT NULL";
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            {
                string name = reader.GetString(0);
                string sql = reader.GetString(1);
                int open = sql.IndexOf('(', StringComparison.Ordinal);
                int close = sql.IndexOf(')', open);
                string columns = sql[(open + 1)..close].Replace("\"", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal);
                int where = sql.IndexOf(" WHERE ", StringComparison.Ordinal);
                indexes.Add(
                    $"{name} {(sql.StartsWith("CREATE UNIQUE", StringComparison.Ordinal) ? "unique " : "")}on {columns}"
                    + (where < 0 ? "" : $" where {sql[(where + 7)..].Replace("\"", "", StringComparison.Ordinal)}"));
            }
        }
        indexes.Sort(StringComparer.Ordinal);
        return [.. indexes];
    }
}
