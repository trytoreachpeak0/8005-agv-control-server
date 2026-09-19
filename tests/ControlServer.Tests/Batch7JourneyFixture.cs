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

    public static async Task<Batch7JourneyFixture> CreateAsync(bool migrate = true)
    {
        SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        Batch7JourneyFixture fixture = new(connection);
        if (migrate)
        {
            await fixture.Context.Database.MigrateAsync(TestContext.Current.CancellationToken);
        }
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
        await new WireToGateStore(context).AcceptWithOrderIntentAsync(
            Snapshot(demandId, at),
            JourneyPlanBuilder.PickupIntent(plan, demandId, at),
            plan,
            TestContext.Current.CancellationToken);
        return plan;
    }

    /// <summary>Ends a journey the way a successful unload does through the direct completion entry.</summary>
    internal static Task CompleteByUnloadAsync(ControlServerDbContext context, string demandId, DateTimeOffset at) =>
        new WireToGateStore(context).CompleteDemandAfterUnloadAsync(
            $"UNLOAD-{demandId}",
            demandId,
            $"SUBLOT-{demandId}|WIRE_TO_GATE",
            7,
            [new SlotPhysicalEvidence(1, SlotBusinessState.Empty, true, true)],
            "all-empty-locked-output-reset",
            at,
            TestContext.Current.CancellationToken);

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
