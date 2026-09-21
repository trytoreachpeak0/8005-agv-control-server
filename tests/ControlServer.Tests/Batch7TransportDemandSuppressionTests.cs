using System.Data.Common;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ControlServer.Tests;

/// <summary>
/// 批次7-05（control-server#210）写入侧：终结需求那一步，终态码属于 <c>REQ-0156</c> 的四个之一时，按该需求的业务键写一条抑制，
/// 与终态同一次保存；先写者胜、不覆盖。公开入口是批次7-02 的终结服务 <see cref="PickupStopTermination"/>。
/// </summary>
/// <remarks>
/// <para>
/// 这里是服务层的每一种情形与票面点名的时刻：四个码各一条、不写的两种、两个写者同时终结同一需求、重放、写抑制失败。
/// 经真实运行时与报文处理器走完各条终结路径的端到端证据，在那些路径各自的端到端测试里（与 <see cref="ZeroChangePin"/> 同一处）
/// 各多一行 <see cref="SuppressionAssertions"/>。
/// </para>
/// <para>
/// <c>TERMINATED_BY_FAULT_CARGO_HANDOFF</c> 不写：需求基线 <c>REQ-0156</c> 只列四个码。MVP 多写了它（<c>557644a6</c>），
/// 本票照基线。
/// </para>
/// </remarks>
public sealed class Batch7TransportDemandSuppressionTests
{
    private const string Demand = "D-7051";
    private static readonly DateTimeOffset At = Batch7JourneyFixture.Now;
    private static string KeyOf(string demandId) => $"SUBLOT-{demandId}|WIRE_TO_GATE";

    /// <summary>
    /// 四个码各写一条，记下需求、码与终结时刻；而且是在调用方那一次保存里——暂存之后、保存之前，另一个连接上什么都看不到，
    /// 需求也还没被取消。
    /// </summary>
    [Theory]
    [InlineData("CANCELLED_BY_OPERATOR")]
    [InlineData("CANCELLED_BY_LOAD_COMPENSATION")]
    [InlineData("CANCELLED_BY_STOP_COMPLETE")]
    [InlineData("CANCELLED_BY_STATION_TIMEOUT")]
    public async Task EachLocalCancellationSuppressesTheKeyInTheSameSaveAsTheEnding(string reasonCode)
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await Batch7JourneyFixture.AcceptAsync(fixture.Context, Demand, "agv-01", "VK-01", At);
        await fixture.RenewContextAsync();

        await using var transaction = await fixture.Context.Database.BeginTransactionAsync(cancellationToken);
        JourneyRuntimeRow runtime = await DemandJourneyLookup.JourneyOf(fixture.Context, Demand).SingleAsync(cancellationToken);
        await new PickupStopTermination(fixture.Context).StageAsync(runtime, Demand, reasonCode, At.AddMinutes(5), cancellationToken);
        Assert.Contains(fixture.Context.ChangeTracker.Entries<TransportDemandSuppressionRow>(),
            entry => entry.State == EntityState.Added);
        // Same connection as the staging context: anything the staging had already saved would be visible here.
        await using (ControlServerDbContext peeking = fixture.NewContext())
        {
            Assert.Empty(await SuppressionAssertions.AllAsync(peeking));
            Assert.Equal(DemandExecutionStatus.Accepted,
                (await peeking.AcceptedDemands.SingleAsync(row => row.DemandId == Demand, cancellationToken)).Status);
        }
        await fixture.Context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        await using ControlServerDbContext reading = fixture.NewContext();
        Assert.Equal(DemandExecutionStatus.Cancelled,
            (await reading.AcceptedDemands.SingleAsync(row => row.DemandId == Demand, cancellationToken)).Status);
        TransportDemandSuppression suppression = Assert.Single(await SuppressionAssertions.AllAsync(reading));
        Assert.Equal(new TransportDemandSuppression(KeyOf(Demand), Demand, reasonCode, At.AddMinutes(5)), suppression);
    }

    // ---- Endings that are not a local cancellation ------------------------------------------------------------------

    /// <summary>
    /// 故障货物交接与强制机械取出都以 <c>TERMINATED_BY_FAULT_CARGO_HANDOFF</c> 终结，不是本地取消，不抑制（REQ-0156 只列四个）。
    /// 两条路径的端到端各在自己的测试里断言同一件事；这里是终结服务本身。
    /// </summary>
    [Fact]
    public async Task AFaultCargoHandoffEndsTheDemandWithoutSuppressingItsKey()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        await Batch7JourneyFixture.AcceptAsync(fixture.Context, Demand, "agv-01", "VK-01", At);
        await fixture.RenewContextAsync();

        await EndAsync(fixture.Context, Demand, "TERMINATED_BY_FAULT_CARGO_HANDOFF", At.AddMinutes(5));

        await using ControlServerDbContext reading = fixture.NewContext();
        Assert.Equal(DemandExecutionStatus.Cancelled,
            (await reading.AcceptedDemands.SingleAsync(TestContext.Current.CancellationToken)).Status);
        await SuppressionAssertions.AssertNothingSuppressedAsync(reading);
    }

    [Fact]
    public async Task ASuccessfulUnloadSuppressesNothing()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        await Batch7JourneyFixture.AcceptAsync(fixture.Context, Demand, "agv-01", "VK-01", At);

        await Batch7JourneyFixture.CompleteByUnloadAsync(fixture.Context, Demand, At.AddMinutes(10));

        await using ControlServerDbContext reading = fixture.NewContext();
        Assert.Equal(DemandExecutionStatus.Succeeded,
            (await reading.AcceptedDemands.SingleAsync(TestContext.Current.CancellationToken)).Status);
        await SuppressionAssertions.AssertNothingSuppressedAsync(reading);
    }

    // ---- The same ending a second time -------------------------------------------------------------------------

    /// <summary>
    /// 同一取消结果重放一次：抑制不重写，<c>SuppressedAt</c> 与码都是第一次的；也不抛（主键冲突会在保存时把整次保存拒掉）。
    /// 再来一次别的码（晚到的站点期限）也一样。
    /// </summary>
    [Fact]
    public async Task AReplayedEndingNeitherRewritesTheSuppressionNorThrows()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        await Batch7JourneyFixture.AcceptAsync(fixture.Context, Demand, "agv-01", "VK-01", At);
        await fixture.RenewContextAsync();
        await EndAsync(fixture.Context, Demand, "CANCELLED_BY_OPERATOR", At.AddMinutes(5));

        await EndAsync(fixture.Context, Demand, "CANCELLED_BY_OPERATOR", At.AddMinutes(8));
        await EndAsync(fixture.Context, Demand, "CANCELLED_BY_STATION_TIMEOUT", At.AddMinutes(9));

        await using ControlServerDbContext reading = fixture.NewContext();
        Assert.Equal(
            new TransportDemandSuppression(KeyOf(Demand), Demand, "CANCELLED_BY_OPERATOR", At.AddMinutes(5)),
            Assert.Single(await SuppressionAssertions.AllAsync(reading)));
    }

    /// <summary>
    /// 同一需求在一次改动里被暂存两次终结、中间不保存：只暂存一条，是先暂存的那一条。
    /// </summary>
    [Fact]
    public async Task TwoEndingsStagedInOneChangeStageOneSuppression()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await Batch7JourneyFixture.AcceptAsync(fixture.Context, Demand, "agv-01", "VK-01", At);
        await fixture.RenewContextAsync();

        await using (var transaction = await fixture.Context.Database.BeginTransactionAsync(cancellationToken))
        {
            JourneyRuntimeRow runtime = await DemandJourneyLookup.JourneyOf(fixture.Context, Demand).SingleAsync(cancellationToken);
            await new PickupStopTermination(fixture.Context).StageAsync(
                runtime, Demand, "CANCELLED_BY_STATION_TIMEOUT", At.AddMinutes(5), cancellationToken);
            await new PickupStopTermination(fixture.Context).StageAsync(
                runtime, Demand, "CANCELLED_BY_OPERATOR", At.AddMinutes(6), cancellationToken);
            await fixture.Context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        await using ControlServerDbContext reading = fixture.NewContext();
        Assert.Equal(
            new TransportDemandSuppression(KeyOf(Demand), Demand, "CANCELLED_BY_STATION_TIMEOUT", At.AddMinutes(5)),
            Assert.Single(await SuppressionAssertions.AllAsync(reading)));
    }

    // ---- Two writers at once -----------------------------------------------------------------------------------

    /// <summary>
    /// 站点期限到期与取消结果同时终结同一需求，各在自己连接的写事务（<c>BEGIN IMMEDIATE</c>）里：恰好一条抑制，码是先提交者的，
    /// 后者不改写、不抛。需求状态与旅程收尾照今天：需求取消，租约与用途占有只放一次，放在先提交者的时刻。
    /// </summary>
    /// <remarks>
    /// 交错是强制的：期限一方暂存完、还没保存时停下，等取消一方开始发 <c>BEGIN</c>；取消一方的 <c>BEGIN IMMEDIATE</c> 因此
    /// 要等期限一方提交之后才拿得到写锁，于是它读「键上有没有抑制」时读到的是期限一方已提交的那一条。
    /// 若那一读落在写事务之外、或者先写者胜只靠保存时的主键冲突，后保存的那一方会撞主键、整次保存被拒——那正是这条测试要抓的。
    /// </remarks>
    [Fact]
    public async Task AStationDeadlineAndACancellationEndingOneDemandAtOnceLeaveExactlyTheFirstCommittersSuppression()
    {
        string path = Path.Combine(Path.GetTempPath(), $"cs210-{Guid.NewGuid():N}.db");
        string connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();
        try
        {
            CancellationToken cancellationToken = TestContext.Current.CancellationToken;
            await using (ControlServerDbContext setup = FileContext(connectionString))
            {
                await setup.Database.MigrateAsync(cancellationToken);
                await Batch7JourneyFixture.AcceptAsync(setup, Demand, "agv-01", "VK-01", At);
            }

            TaskCompletionSource deadlineStaged = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource cancellationBeginning = new(TaskCreationOptions.RunContinuationsAsynchronously);
            SuppressionReadWatch deadlineWatch = new();
            SuppressionReadWatch cancellationWatch = new();
            await using ControlServerDbContext deadline = FileContext(connectionString, deadlineWatch);
            await using ControlServerDbContext cancelling = FileContext(connectionString, cancellationWatch);

            Task deadlineWriter = Task.Run(async () =>
            {
                await using var transaction = await deadline.Database.BeginTransactionAsync(cancellationToken);
                JourneyRuntimeRow runtime = await DemandJourneyLookup.JourneyOf(deadline, Demand).SingleAsync(cancellationToken);
                await new PickupStopTermination(deadline).StageAsync(
                    runtime, Demand, "CANCELLED_BY_STATION_TIMEOUT", At.AddMinutes(5), cancellationToken);
                deadlineStaged.TrySetResult();
                await cancellationBeginning.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
                // Give the cancellation's BEGIN IMMEDIATE a moment to be the one waiting on the lock.
                await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
                await deadline.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }, cancellationToken);
            await deadlineStaged.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            Task cancellationWriter = Task.Run(async () =>
            {
                cancellationBeginning.TrySetResult();
                await using var transaction = await cancelling.Database.BeginTransactionAsync(cancellationToken);
                JourneyRuntimeRow runtime = await DemandJourneyLookup.JourneyOf(cancelling, Demand).SingleAsync(cancellationToken);
                await new PickupStopTermination(cancelling).StageAsync(
                    runtime, Demand, "CANCELLED_BY_OPERATOR", At.AddMinutes(6), cancellationToken);
                await cancelling.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }, cancellationToken);
            await Task.WhenAll(deadlineWriter, cancellationWriter);

            await using ControlServerDbContext reading = FileContext(connectionString);
            Assert.Equal(
                new TransportDemandSuppression(KeyOf(Demand), Demand, "CANCELLED_BY_STATION_TIMEOUT", At.AddMinutes(5)),
                Assert.Single(await SuppressionAssertions.AllAsync(reading)));
            Assert.Equal(DemandExecutionStatus.Cancelled, (await reading.AcceptedDemands.SingleAsync(cancellationToken)).Status);
            Assert.Equal(At.AddMinutes(5), (await reading.VehicleDispatchLeases.SingleAsync(cancellationToken)).ReleasedAt);
            Assert.Empty(await reading.Set<VehiclePurposeClaimRow>().ToArrayAsync(cancellationToken));
            await VehicleOccupancyAssertions.AssertActiveLeasesAndPurposeClaimsMatchAsync(reading);
            // Both writers asked, and each asked inside its write transaction.
            Assert.True(deadlineWatch.Reads > 0 && cancellationWatch.Reads > 0,
                $"suppression reads: deadline {deadlineWatch.Reads}, cancellation {cancellationWatch.Reads}");
            Assert.Equal(0, deadlineWatch.ReadsOutsideATransaction + cancellationWatch.ReadsOutsideATransaction);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    // ---- The suppression failing to be written ----------------------------------------------------------------

    /// <summary>
    /// 写抑制那一条语句失败：同一次保存里的需求终态、租约释放、用途占有释放、旅程收尾一起回滚，不留下「已取消但键未抑制」。
    /// </summary>
    [Fact]
    public async Task WhenTheSuppressionCannotBeWrittenTheEndingRollsBackWhole()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await Batch7JourneyFixture.AcceptAsync(fixture.Context, Demand, "agv-01", "VK-01", At);
        await fixture.RenewContextAsync();
        string[] before = await DumpEndingTablesAsync(fixture);
        FailSuppressionInsert injection = new();

        await using (ControlServerDbContext failing = fixture.NewContext(injection))
        {
            await Assert.ThrowsAnyAsync<Exception>(async () =>
            {
                await using var transaction = await failing.Database.BeginTransactionAsync(cancellationToken);
                JourneyRuntimeRow runtime = await DemandJourneyLookup.JourneyOf(failing, Demand).SingleAsync(cancellationToken);
                await new PickupStopTermination(failing).StageAsync(
                    runtime, Demand, "CANCELLED_BY_OPERATOR", At.AddMinutes(5), cancellationToken);
                await failing.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            });
        }

        // The injection fired -- the ending did reach the one statement it fails -- and nothing of the ending survived.
        Assert.Equal(1, injection.Fired);
        Assert.Equal(before, await DumpEndingTablesAsync(fixture));
        await using ControlServerDbContext reading = fixture.NewContext();
        Assert.Equal(DemandExecutionStatus.Accepted, (await reading.AcceptedDemands.SingleAsync(cancellationToken)).Status);
        Assert.Null((await reading.VehicleDispatchLeases.SingleAsync(cancellationToken)).ReleasedAt);
        Assert.Single(await reading.Set<VehiclePurposeClaimRow>().ToArrayAsync(cancellationToken));
        Assert.NotEqual(JourneyRuntimeStage.Completed, (await reading.JourneyRuntimes.SingleAsync(cancellationToken)).Stage);
        await SuppressionAssertions.AssertNothingSuppressedAsync(reading);
    }

    // ---- Helpers --------------------------------------------------------------------------------------------------

    private const string SuppressionInsert = "INSERT INTO \"TransportDemandSuppressions\"";

    /// <summary>Ends a demand the way every caller does: staged into a write transaction, saved once.</summary>
    private static async Task EndAsync(ControlServerDbContext context, string demandId, string reasonCode, DateTimeOffset at)
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        JourneyRuntimeRow runtime = await DemandJourneyLookup.JourneyOf(context, demandId).SingleAsync(cancellationToken);
        await new PickupStopTermination(context).StageAsync(runtime, demandId, reasonCode, at, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        context.ChangeTracker.Clear();
    }

    private static async Task<string[]> DumpEndingTablesAsync(Batch7JourneyFixture fixture)
    {
        List<string> rows = [];
        foreach (string table in new[]
                 {
                     "AcceptedDemands", "VehicleDispatchLeases", "VehiclePurposeClaims", "OrderIntents", "JourneyRuntimes",
                     "JourneyDemands", "JourneyStops", "TransportDemandSuppressions",
                 })
        {
            rows.Add("## " + table);
            rows.AddRange(await Batch7JourneyFixture.DumpAsync(fixture.Connection, table));
        }
        return [.. rows];
    }

    private static ControlServerDbContext FileContext(string connectionString, params IInterceptor[] interceptors)
    {
        DbContextOptionsBuilder<ControlServerDbContext> builder =
            new DbContextOptionsBuilder<ControlServerDbContext>().UseSqlite(connectionString);
        if (interceptors.Length > 0)
        {
            builder.AddInterceptors(interceptors);
        }
        return new ControlServerDbContext(builder.Options);
    }

    /// <summary>Fails the statement that writes a suppression, as a full disk or a crash at that statement would.</summary>
    private sealed class FailSuppressionInsert : DbCommandInterceptor
    {
        public int Fired { get; private set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Fail(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Fail(command);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void Fail(DbCommand command)
        {
            if (command.CommandText.Contains(SuppressionInsert, StringComparison.Ordinal))
            {
                Fired++;
                throw new InvalidOperationException("Injected: the suppression could not be written.");
            }
        }
    }

    /// <summary>Counts the reads of the suppression table, and how many of them ran outside a transaction.</summary>
    private sealed class SuppressionReadWatch : DbCommandInterceptor
    {
        public int Reads { get; private set; }

        public int ReadsOutsideATransaction { get; private set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM \"TransportDemandSuppressions\"", StringComparison.Ordinal))
            {
                Reads++;
                if (command.Transaction is null)
                {
                    ReadsOutsideATransaction++;
                }
            }
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
