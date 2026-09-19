using System.Data.Common;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime.TaskTypeStations;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;

namespace ControlServer.Tests;

/// <summary>
/// 引擎在每次目录完整确认之后跑目录变化收敛（control-server#162 的挂接）：一轮里读到绑定站点改名，这一轮结束时该任务类型就有
/// 一条「目录变化」暂停，下一轮的准入读得到它。
/// </summary>
public sealed class CatalogBindingHoldEngineHookTests
{
    [Fact]
    public async Task ARoundThatConfirmsACatalogWithTheBoundGateRenamedLeavesWireToGateHeld()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Riot.SetMapStations(
            new RiotMapStation(12, "N1-1"),
            new RiotMapStation(13, "N1-2_N1-3"),
            new RiotMapStation(TaskTypeStationRuntimeSeed.GateStationRiotId, "关卡-临时堆放"),
            new RiotMapStation(300, "等待点"));

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        TaskTypeStationHold hold = Assert.Single(
            await new TaskTypeStationHoldStore(fixture.Context).ListUnreleasedAsync(25, TestContext.Current.CancellationToken));
        Assert.Equal(TransportTaskTypes.WireToGate, hold.TaskType);
        Assert.Equal(TaskTypeStationHoldSource.CatalogChange, hold.Source);
        Assert.Equal(CatalogBindingHoldReasons.StationRenamed, hold.ReasonCode);
    }

    [Fact]
    public async Task ARoundOverAnUnchangedCatalogRaisesNoHold()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        Assert.Empty(
            await new TaskTypeStationHoldStore(fixture.Context).ListUnreleasedAsync(25, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// control-server#201 (review D of #162): the convergence failing on a busy database is logged and the round goes on.
    /// The journey under way still advances this round, nothing the failed attempt added stays tracked to be written by a
    /// later save of the same round, and the next round converges again.
    /// </summary>
    [Fact]
    public async Task AConvergenceFailingOnABusyDatabaseDoesNotStopTheRoundAndTheHoldLandsOnTheNextOne()
    {
        FailingHoldInsert failing = new();
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(commands: failing);
        fixture.Catalog.Set(fixture.Demand("10000000-0000-4000-8000-000000000001", "SUBLOT-001", createdAt: Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 7);
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        JourneyRuntimeRow heading = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingPickupArrival, heading.Stage);
        fixture.Riot.SetSuccessfulArrival("TO_PICKUP", heading.PickupStationRiotId);
        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { CurrentStationId = heading.PickupStationRiotId };
        fixture.Riot.SetMapStations(RenamedGateMap);
        failing.Arm(() => new SqliteException("database is locked", 5));

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, failing.Thrown);
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, (await fixture.RuntimeAsync()).Stage);
        Assert.DoesNotContain(
            fixture.Context.ChangeTracker.Entries(),
            entry => entry.Entity is TaskTypeStationHoldRow or TaskTypeStationCatalogChangeRow or BusinessAuditRecordRow
                && entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted);
        Assert.Contains(fixture.EngineLog.Entries, entry => entry.Level == LogLevel.Warning
            && entry.Message.Contains("catalog change convergence", StringComparison.Ordinal));
        await using (ControlServerDbContext other = new(fixture.DbOptionsForTests))
        {
            Assert.Empty(await new TaskTypeStationHoldStore(other).ListUnreleasedAsync(25, TestContext.Current.CancellationToken));
            Assert.Empty(await new TaskTypeStationCatalogChangeStore(other).ListAsync(25, TestContext.Current.CancellationToken));
        }

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        TaskTypeStationHold hold = Assert.Single(
            await new TaskTypeStationHoldStore(fixture.Context).ListUnreleasedAsync(25, TestContext.Current.CancellationToken));
        Assert.Equal(TransportTaskTypes.WireToGate, hold.TaskType);
        Assert.Single(await new TaskTypeStationCatalogChangeStore(fixture.Context)
            .ListAsync(25, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// The hold is saved inside the convergence's transaction, then its change record fails: the rollback takes the hold
    /// out of the database, but the saved hold stays tracked as Unchanged -- a row the context believes exists and the
    /// database does not have. Nothing of the failed attempt may stay tracked (review of control-server#201).
    /// </summary>
    [Fact]
    public async Task AFailureAfterTheHoldWasSavedLeavesNothingOfTheAttemptTrackedAndTheNextRoundConverges()
    {
        FailingHoldInsert failing = new("INSERT INTO \"TaskTypeStationCatalogChanges\"");
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(commands: failing);
        fixture.Riot.SetMapStations(RenamedGateMap);
        failing.Arm(() => new SqliteException("database is locked", 5));

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, failing.Thrown);
        Assert.DoesNotContain(
            fixture.Context.ChangeTracker.Entries(),
            entry => entry.Entity is TaskTypeStationHoldRow or TaskTypeStationCatalogChangeRow or BusinessAuditRecordRow);
        await using (ControlServerDbContext other = new(fixture.DbOptionsForTests))
        {
            Assert.Empty(await new TaskTypeStationHoldStore(other).ListUnreleasedAsync(25, TestContext.Current.CancellationToken));
        }

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        Assert.Single(
            await new TaskTypeStationHoldStore(fixture.Context).ListUnreleasedAsync(25, TestContext.Current.CancellationToken));
        Assert.Single(await new TaskTypeStationCatalogChangeStore(fixture.Context)
            .ListAsync(25, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AShutdownCancellationDuringTheConvergenceStillEndsTheRound()
    {
        FailingHoldInsert failing = new();
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(commands: failing);
        fixture.Riot.SetMapStations(RenamedGateMap);
        using CancellationTokenSource stopping = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        failing.Arm(() =>
        {
            stopping.Cancel();
            return new OperationCanceledException(stopping.Token);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Engine.ExecuteOnceAsync(stopping.Token));
        Assert.Equal(1, failing.Thrown);
    }

    private static readonly RiotMapStation[] RenamedGateMap =
    [
        new RiotMapStation(12, "N1-1"),
        new RiotMapStation(13, "N1-2_N1-3"),
        new RiotMapStation(TaskTypeStationRuntimeSeed.GateStationRiotId, "关卡-临时堆放"),
        new RiotMapStation(300, "等待点")
    ];

    /// <summary>Fails the next insert into one table once, the way SQLite does when another writer holds the database.</summary>
    private sealed class FailingHoldInsert(string insert = "INSERT INTO \"TaskTypeStationHolds\"") : DbCommandInterceptor
    {
        private Func<Exception>? _failure;

        public int Thrown { get; private set; }

        public void Arm(Func<Exception> failure) => _failure = failure;

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            FailIfArmed(command);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            FailIfArmed(command);
            return ValueTask.FromResult(result);
        }

        private void FailIfArmed(DbCommand command)
        {
            if (_failure is null
                || !command.CommandText.Contains(insert, StringComparison.Ordinal))
            {
                return;
            }
            Func<Exception> failure = _failure;
            _failure = null;
            Thrown++;
            throw failure();
        }
    }
}
