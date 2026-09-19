using System.Data.Common;
using ControlServer.Application;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using static ControlServer.Tests.TaskTypeStationTestData;

namespace ControlServer.Tests;

/// <summary>
/// 暂停的两条写入路径在重试与并发下的口径（control-server#162，承接 #159 PR #171 独立审查的交接）：
/// 同一个暂停写两次只有一条未解除暂停；两人同时解除，先到的那一方的 <c>ReleasedBy</c> 不被覆盖。
/// </summary>
public sealed class TaskTypeStationHoldWriteConcurrencyTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RaisingTheSameHoldTwiceLeavesOneUnreleasedHoldAndTellsTheRetryItWasAlreadyThere()
    {
        await using TaskTypeStationPersistenceFixture fixture = await TaskTypeStationPersistenceFixture.CreateAsync();

        TaskTypeStationHoldRaise first = await fixture.Holds.RaiseAsync(
            25, TransportTaskTypes.WireToGate, TaskTypeStationHoldSource.CatalogChange, "STATION_RENAMED",
            """{"stationRiotId":210}""", "server", Now, Token);
        // The retry a timed-out caller makes: same hold, a moment later.
        TaskTypeStationHoldRaise retry = await fixture.Holds.RaiseAsync(
            25, TransportTaskTypes.WireToGate, TaskTypeStationHoldSource.CatalogChange, "STATION_RENAMED",
            """{"stationRiotId":210}""", "server", Now.AddSeconds(5), Token);
        fixture.Context.ChangeTracker.Clear();

        Assert.True(first.Created);
        Assert.False(retry.Created);
        Assert.Equal(first.Hold.HoldId, retry.Hold.HoldId);
        Assert.Equal(Now, retry.Hold.RaisedAt);
        Assert.Single(await fixture.Holds.ListUnreleasedAsync(25, Token));
        Assert.Equal(1, await fixture.Context.Set<TaskTypeStationHoldRow>().CountAsync(Token));
    }

    [Fact]
    public async Task TwoReleasesOfOneHoldAtOnceLetOnlyTheFirstOneWinAndKeepItsReleasedBy()
    {
        await using SharedDatabase database = await SharedDatabase.CreateAsync();
        string holdId;
        await using (ControlServerDbContext setup = database.NewContext())
        {
            holdId = (await new TaskTypeStationHoldStore(setup).RaiseAsync(
                25, TransportTaskTypes.WireToGate, TaskTypeStationHoldSource.Manual, "MANUAL_TIGHTEN", "{}",
                "deployment:dashboard", Now, Token)).Hold.HoldId;
        }

        bool otherReleased = false;
        // The other releaser gets in after this one has looked at the row and before it writes -- the window a
        // read-then-save release leaves open.
        ReleaseBeforeHoldUpdate interleave = new(async () =>
        {
            await using ControlServerDbContext other = database.NewContext();
            otherReleased = await new TaskTypeStationHoldStore(other)
                .ReleaseAsync(holdId, "fieldops:first", Now.AddMinutes(1), Token);
        });
        bool thisReleased;
        await using (ControlServerDbContext late = database.NewContext(interleave))
        {
            thisReleased = await new TaskTypeStationHoldStore(late)
                .ReleaseAsync(holdId, "fieldops:second", Now.AddMinutes(2), Token);
        }

        Assert.True(interleave.Fired);
        Assert.True(otherReleased);
        Assert.False(thisReleased);
        await using ControlServerDbContext check = database.NewContext();
        TaskTypeStationHoldRow row = await check.Set<TaskTypeStationHoldRow>().SingleAsync(Token);
        Assert.Equal("fieldops:first", row.ReleasedBy);
        Assert.Equal(Now.AddMinutes(1), row.ReleasedAt);
    }

    /// <summary>Runs another writer just before the first UPDATE of the hold table, however it is issued.</summary>
    private sealed class ReleaseBeforeHoldUpdate(Func<Task> otherWriter) : DbCommandInterceptor
    {
        public bool Fired { get; private set; }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            await FireOnHoldUpdateAsync(command);
            return result;
        }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            await FireOnHoldUpdateAsync(command);
            return result;
        }

        private async Task FireOnHoldUpdateAsync(DbCommand command)
        {
            if (!Fired && command.CommandText.Contains("UPDATE \"TaskTypeStationHolds\"", StringComparison.Ordinal))
            {
                Fired = true;
                await otherWriter();
            }
        }
    }

    /// <summary>One migrated database file several contexts open, the way the server and FieldOps do.</summary>
    private sealed class SharedDatabase : IAsyncDisposable
    {
        private readonly string _directory;
        private readonly string _connectionString;

        private SharedDatabase(string directory)
        {
            _directory = directory;
            _connectionString = $"Data Source={Path.Combine(directory, "holds.db")};Pooling=False";
        }

        public static async Task<SharedDatabase> CreateAsync()
        {
            string directory = Path.Combine(Path.GetTempPath(), "cs162-holds-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            SharedDatabase database = new(directory);
            await using ControlServerDbContext migrator = database.NewContext();
            await migrator.Database.MigrateAsync(Token);
            return database;
        }

        public ControlServerDbContext NewContext(params IInterceptor[] interceptors) =>
            new(new DbContextOptionsBuilder<ControlServerDbContext>()
                .UseSqlite(_connectionString)
                .AddInterceptors(interceptors)
                .Options);

        public ValueTask DisposeAsync()
        {
            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temp directory is not a test failure.
            }
            return ValueTask.CompletedTask;
        }
    }
}
