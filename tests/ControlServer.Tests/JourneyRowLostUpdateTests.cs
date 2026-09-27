using System.Data.Common;
using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;

namespace ControlServer.Tests;

/// <summary>
/// 入站在引擎一轮的读与存之间提交了旅程行，引擎不能用这一轮开头读到的旧行把它盖掉（control-server#357）。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么只有这个方向。</b>入站的每一次写都在收件箱的 <c>BEGIN IMMEDIATE</c> 事务里、读在拿写锁之后，读到的总是最新值；
/// 引擎每轮开头带跟踪地读出全部未完成旅程，逐车推进、中间夹着 RIoT 调用，最后才存。第一步的普查（票面评论）是这句话的依据。
/// </para>
/// <para>
/// <b>入站的提交用原始 SQL 做</b>，写的列与入站那条路径写的相同：这里要钉的是「引擎读之后、存之前有人提交了」，
/// 不是入站怎样判定。交错点由拦截器在引擎这一轮的某次查询读完时触发，并断言它确实触发了、而且落在引擎读旅程行之后——
/// 落在之前，引擎读到的就是新值，用例空绿。
/// </para>
/// </remarks>
public sealed class JourneyRowLostUpdateTests
{
    private const string DemandId = "10000000-0000-4000-8000-000000000001";

    /// <summary>
    /// cs#339 真装置 run 35896134304 那一次：门关上的同一刻取消收尾，引擎在内存里清掉门未关的码，保存时把入站刚写下的终结原因
    /// 与它的起始时刻盖成空。原是 cs#339 的探针（<c>evidence/l1/20260924-cs339-lost-update-probe</c>）。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    public async Task AClosureCommittedBetweenTheEnginesReadAndSaveKeepsItsReason()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        await JourneyRuntimeWorkerLoadDeadlineTests.AdvanceToLoadWithStationDeadlineAsync(fixture);
        await fixture.SetSafetyEvidenceAsync(unknownPresent: false, reasonCodes: ["LOCK_NOT_CLOSED"]);
        fixture.Clock.Advance(TimeSpan.FromSeconds(10));
        await fixture.Engine.ExecuteOnceAsync(token);
        JourneyRuntimeRow alarmed = await fixture.RuntimeAsync();
        Assert.Equal("STATION_TIMEOUT_DOOR_NOT_CLOSED", alarmed.BlockReasonCode);

        await fixture.ProveSlotDoorsClosedAsync();
        fixture.Context.ChangeTracker.Clear();
        DateTimeOffset closedAt = fixture.Clock.GetUtcNow();
        bool injected = false;
        fixture.SaveChanges.FailWhen = written =>
        {
            if (!injected && written.Contains("JourneyRuntimeRow.BlockReasonCode"))
            {
                injected = true;
                Execute(fixture,
                    "UPDATE JourneyRuntimes SET Stage = 'Completed', BlockReasonCode = 'CANCELLED_BY_OPERATOR', " +
                    "BlockReasonSince = $at, WaitingSince = NULL, StationDepartureWaitStartedAt = NULL WHERE JourneyId = $id",
                    alarmed.JourneyId, closedAt);
            }
            return false;
        };
        await fixture.Engine.ExecuteOnceAsync(token);
        fixture.SaveChanges.FailWhen = null;

        // 前提：注入发生在引擎要写原因码的那一次保存里，即引擎确实拿着旧行走到了保存。
        Assert.True(injected);
        JourneyRuntimeRow after = await ReadAsync(fixture, alarmed.JourneyId);
        Assert.Equal(
            (JourneyRuntimeStage.Completed, "CANCELLED_BY_OPERATOR", (DateTimeOffset?)closedAt),
            (after.Stage, after.BlockReasonCode, after.BlockReasonSince));
    }

    /// <summary>
    /// 装货已落定，引擎这一轮读完旅程行之后、读装货操作之前，入站按「取消结果证明不了空」把旅程写成 Blocked、需求写成
    /// RecoveryRequired（<c>OnboardRecoveryCoordinator.KeepDemandAndJourneyBlockedAsync</c>，它不动装货操作）。
    /// </summary>
    /// <remarks>
    /// 修之前引擎拿旧行往离站推：阶段被盖成 <c>AwaitingDepartureSafety</c>、同一轮给车发离站核验，车答 SAFE 的下一轮建去关卡的单——
    /// 一趟要人工恢复的旅程被放行（第一步结果表 #2）。所以断言三件：阶段与码保住；注入之后这一轮没有任何出站消息落库；
    /// 再跑一轮也没有去关卡的订单意图。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    public async Task ABlockCommittedWhileTheEngineAdvancesACommittedLoadStaysBlocked()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        OnceAfterReading interleaver = OnceAfterReading.FirstStationOperationReadAfterTheJourneysWereRead();
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(commands: interleaver);
        fixture.Catalog.Set(fixture.Demand(DemandId, "SUBLOT-001", createdAt: Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        JourneyRuntimeRow loading = await fixture.AdvanceToLoadResultAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingLoadResult, loading.Stage);
        StationOperationRow load = await fixture.OperationAsync(SlotOperationType.Load);
        await fixture.ApplySafeResultAsync(load, SlotOperationType.Load, SlotBusinessState.Occupied);
        fixture.Context.ChangeTracker.Clear();

        long outboxAtInjection = -1;
        interleaver.Arm(() =>
        {
            Execute(fixture,
                "UPDATE JourneyRuntimes SET Stage = 'Blocked', BlockReasonCode = 'LoadCancellationResult_NOT_RECONCILED' " +
                "WHERE JourneyId = $id; UPDATE AcceptedDemands SET Status = 'RecoveryRequired' WHERE DemandId = '" +
                DemandId + "'",
                loading.JourneyId);
            outboxAtInjection = Count(fixture, "SELECT COUNT(*) FROM ProtocolOutbox");
        });
        await fixture.Engine.ExecuteOnceAsync(token);

        Assert.Equal(1, interleaver.Fired);
        JourneyRuntimeRow after = await ReadAsync(fixture, loading.JourneyId);
        Assert.Equal(
            (JourneyRuntimeStage.Blocked, "LoadCancellationResult_NOT_RECONCILED"),
            (after.Stage, after.BlockReasonCode));
        Assert.Equal(outboxAtInjection, Count(fixture, "SELECT COUNT(*) FROM ProtocolOutbox"));

        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        fixture.Context.ChangeTracker.Clear();
        await fixture.Engine.ExecuteOnceAsync(token);
        Assert.Equal(JourneyRuntimeStage.Blocked, (await ReadAsync(fixture, loading.JourneyId)).Stage);
        Assert.Equal(0, Count(fixture, "SELECT COUNT(*) FROM OrderIntents WHERE UpperId LIKE '%-GATE-%'"));
    }

    internal static void Execute(RuntimeFixture fixture, string sql, string journeyId, DateTimeOffset? at = null)
    {
        using SqliteCommand command = Connection(fixture).CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", journeyId);
        if (at is { } value)
        {
            command.Parameters.AddWithValue("$at", value);
        }
        Assert.True(command.ExecuteNonQuery() >= 1);
    }

    internal static long Count(RuntimeFixture fixture, string sql)
    {
        using SqliteCommand command = Connection(fixture).CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }

    private static SqliteConnection Connection(RuntimeFixture fixture)
    {
        SqliteConnection connection = (SqliteConnection)fixture.Context.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            connection.Open();
        }
        return connection;
    }

    internal static Task<JourneyRuntimeRow> ReadAsync(RuntimeFixture fixture, string journeyId) =>
        fixture.Context.JourneyRuntimes.AsNoTracking()
            .SingleAsync(row => row.JourneyId == journeyId, TestContext.Current.CancellationToken);

    /// <summary>在第一条匹配的查询读完时做一次事，模拟入站在那一刻提交。</summary>
    internal sealed class OnceAfterReading(Func<string, bool> matches) : DbCommandInterceptor
    {
        private Action? _pending;

        public int Fired { get; private set; }

        /// <summary>
        /// 先见到引擎这一轮读未完成的旅程（旧行从那一刻起在它手里），之后第一次读装卸操作时触发。只按后者匹配的话，
        /// 一次更早的装卸操作查询会让注入落在旧读之前。
        /// </summary>
        public static OnceAfterReading FirstStationOperationReadAfterTheJourneysWereRead()
        {
            bool journeysRead = false;
            return new OnceAfterReading(text =>
            {
                if (text.Contains("\"JourneyRuntimes\"", StringComparison.Ordinal) &&
                    text.Contains("<> 'Completed'", StringComparison.Ordinal))
                {
                    journeysRead = true;
                    return false;
                }
                return journeysRead && text.Contains("FROM \"StationOperations\"", StringComparison.Ordinal);
            });
        }

        public void Arm(Action action) => _pending = action;

        public override InterceptionResult DataReaderClosing(
            DbCommand command, DataReaderClosingEventData eventData, InterceptionResult result)
        {
            Fire(command);
            return result;
        }

        public override ValueTask<InterceptionResult> DataReaderClosingAsync(
            DbCommand command, DataReaderClosingEventData eventData, InterceptionResult result)
        {
            Fire(command);
            return ValueTask.FromResult(result);
        }

        private void Fire(DbCommand command)
        {
            if (_pending is { } action && matches(command.CommandText))
            {
                _pending = null;
                Fired++;
                action();
            }
        }
    }
}
