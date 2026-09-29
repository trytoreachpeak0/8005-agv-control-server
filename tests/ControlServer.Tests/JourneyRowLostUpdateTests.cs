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
/// <b>入站的提交用原始 SQL 做</b>，写的列与入站那条路径写的相同，连同 <c>Version = Version + 1</c>——入站经 <c>SaveChanges</c>
/// 保存，那里的钩子一定递增它，原始 SQL 替它做钩子做的事：这里要钉的是「引擎读之后、存之前有人提交了」，
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
                    "BlockReasonSince = $at, WaitingSince = NULL, StationDepartureWaitStartedAt = NULL, Version = Version + 1 " +
                    "WHERE JourneyId = $id",
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
    /// 一趟要人工恢复的旅程被放行（第一步结果表 #2）。所以断言：阶段与码保住；注入之后这一轮没有任何出站消息落库（离站核验没发出去）。
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
                "UPDATE JourneyRuntimes SET Stage = 'Blocked', BlockReasonCode = 'LoadCancellationResult_NOT_RECONCILED', " +
                "Version = Version + 1 WHERE JourneyId = $id; UPDATE AcceptedDemands SET Status = 'RecoveryRequired' WHERE DemandId = '" +
                DemandId + "'",
                loading.JourneyId);
            outboxAtInjection = Count(fixture, "SELECT COUNT(*) FROM ProtocolOutbox");
        });
        await fixture.Engine.ExecuteOnceAsync(token);

        Assert.Equal(1, interleaver.Fired);
        AssertYieldedOnceFor(fixture.EngineLog, loading.JourneyId);
        JourneyRuntimeRow after = await ReadAsync(fixture, loading.JourneyId);
        Assert.Equal(
            (JourneyRuntimeStage.Blocked, "LoadCancellationResult_NOT_RECONCILED"),
            (after.Stage, after.BlockReasonCode));
        Assert.Equal(outboxAtInjection, Count(fixture, "SELECT COUNT(*) FROM ProtocolOutbox"));

        // 下一轮按新行读到 Blocked，照旧不动它。（「没有去关卡的单」不在这里断：这一轮没有 SAFE 答复，修前修后都建不了单，
        // 那一格由 ABlockCommittedBeforeTheGateLegIsCreatedStopsTheOrder 断——独立审查建议。）
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        fixture.Context.ChangeTracker.Clear();
        await fixture.Engine.ExecuteOnceAsync(token);
        Assert.Equal(JourneyRuntimeStage.Blocked, (await ReadAsync(fixture, loading.JourneyId)).Stage);
    }

    /// <summary>
    /// 车已答离站 SAFE，引擎这一轮要为关卡那一段建单；入站在引擎读完旅程行之后、读这条答复时写下 Blocked。
    /// </summary>
    /// <remarks>
    /// 这一格钉的是 <see cref="ControlServerDbContext.GuardedJourneyId"/>，不是令牌本身：建单之前的那次保存存的是订单意图，
    /// 旅程行此刻未必改过，只靠旅程行上的令牌就核不到它，入站的 Blocked 会被一张已经建好的单绕过去。守护让那次保存也先核旅程行，
    /// 失败在建单之前。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    public async Task ABlockCommittedBeforeTheGateLegIsCreatedStopsTheOrder()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        bool journeysRead = false;
        OnceAfterReading interleaver = new(text =>
        {
            if (text.Contains("\"JourneyRuntimes\"", StringComparison.Ordinal) &&
                text.Contains("<> 'Completed'", StringComparison.Ordinal))
            {
                journeysRead = true;
                return false;
            }
            return journeysRead && text.Contains("PreDepartureSafetyCheckResult", StringComparison.Ordinal);
        });
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(commands: interleaver);
        fixture.Catalog.Set(fixture.Demand(DemandId, "SUBLOT-001", createdAt: Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        JourneyRuntimeRow departing = await fixture.AdvanceToDepartureSafetyAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingDepartureSafety, departing.Stage);
        await fixture.AddInboxAsync(
            Guid.NewGuid().ToString("D"),
            "PreDepartureSafetyCheckResult",
            new
            {
                preDepartureSafetyCheckId = departing.PreDepartureSafetyCheckId,
                checkPurpose = "DEPARTURE",
                outcome = "SAFE",
                observedAt = fixture.Clock.GetUtcNow(),
                safetyStateVersion = 7,
                validUntil = fixture.Clock.GetUtcNow().AddMinutes(1),
                safety = new
                {
                    departureSafe = true,
                    vehicleStopped = true,
                    allTargetSlotsLocked = true,
                    allUnlockOutputsReset = true,
                    unknownPresent = false,
                    reasonCodes = Array.Empty<string>()
                }
            },
            departing.PreDepartureSafetyCheckMessageId);
        fixture.Context.ChangeTracker.Clear();

        interleaver.Arm(() => Execute(fixture,
            "UPDATE JourneyRuntimes SET Stage = 'Blocked', BlockReasonCode = 'LoadCancellationResult_NOT_RECONCILED', " +
            "Version = Version + 1 WHERE JourneyId = $id",
            departing.JourneyId));
        await fixture.Engine.ExecuteOnceAsync(token);

        Assert.Equal(1, interleaver.Fired);
        AssertYieldedOnceFor(fixture.EngineLog, departing.JourneyId);
        Assert.Equal(0, fixture.Riot.CreateCount("TO_GATE"));
        Assert.Equal(0, Count(fixture, "SELECT COUNT(*) FROM OrderIntents WHERE UpperId LIKE '%-GATE-%'"));
        Assert.Equal(JourneyRuntimeStage.Blocked, (await ReadAsync(fixture, departing.JourneyId)).Stage);
    }

    /// <summary>
    /// 真车载端挂着本服务端在途单时整段路报未就绪（<c>RecoveryRequired</c> / <c>DEPARTURE_SAFETY_NOT_READY</c>），引擎这台车走会话闸门
    /// 那一支、只写码。入站在引擎读完旅程行之后以故障货物交接终结了这趟旅程（交接在车停在哪都会发生）。
    /// </summary>
    /// <remarks>
    /// 闸门那一支是这台车在路上每一轮都走的路，写的是 <c>ONBOARD_SESSION_NOT_READY</c>；它同样出自这一轮开头的那一次读，
    /// 同样不能盖掉入站的终结。形状照 <c>PickupDispatchPlanPastOwnOrderTests</c>：受理那一轮建单并确认，之后会话因这张单未就绪。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    public async Task AClosureCommittedWhileTheSessionIsNotReadyOnTheOwnOrderKeepsItsReason()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        bool journeysRead = false;
        OnceAfterReading interleaver = new(text =>
        {
            if (text.Contains("\"JourneyRuntimes\"", StringComparison.Ordinal) &&
                text.Contains("<> 'Completed'", StringComparison.Ordinal))
            {
                journeysRead = true;
                return false;
            }
            return journeysRead && text.Contains("FROM \"SessionRecoveries\"", StringComparison.Ordinal);
        });
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(commands: interleaver);
        fixture.Catalog.Set(fixture.Demand(DemandId, "SUBLOT-001", Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        await fixture.Engine.ExecuteOnceAsync(token);
        JourneyRuntimeRow travelling = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingPickupArrival, travelling.Stage);
        Assert.Equal("CONFIRMED", await fixture.IntentStatusAsync("TO_PICKUP"));
        await PickupDispatchPlanPastOwnOrderTests.DropSessionOnOwnOrderAsync(fixture);
        fixture.Context.ChangeTracker.Clear();

        long outboxAtInjection = -1;
        DateTimeOffset endedAt = fixture.Clock.GetUtcNow();
        interleaver.Arm(() =>
        {
            Execute(fixture,
                "UPDATE JourneyRuntimes SET Stage = 'Completed', BlockReasonCode = 'TERMINATED_BY_FAULT_CARGO_HANDOFF', " +
                "BlockReasonSince = $at, WaitingSince = NULL, Version = Version + 1 WHERE JourneyId = $id",
                travelling.JourneyId, endedAt);
            outboxAtInjection = Count(fixture, "SELECT COUNT(*) FROM ProtocolOutbox");
        });
        await fixture.Engine.ExecuteOnceAsync(token);

        Assert.Equal(1, interleaver.Fired);
        AssertYieldedOnceFor(fixture.EngineLog, travelling.JourneyId);
        JourneyRuntimeRow after = await ReadAsync(fixture, travelling.JourneyId);
        Assert.Equal(
            (JourneyRuntimeStage.Completed, "TERMINATED_BY_FAULT_CARGO_HANDOFF", (DateTimeOffset?)endedAt),
            (after.Stage, after.BlockReasonCode, after.BlockReasonSince));
        Assert.Equal(outboxAtInjection, Count(fixture, "SELECT COUNT(*) FROM ProtocolOutbox"));
    }

    /// <summary>
    /// 入站写落在 RIoT 建单之后的第一次保存处（独立审查必修 1）：单已经建了，记录它的那几次保存不能被守护丢掉；旅程行自己的改动吃令牌，
    /// 这台车让开一轮，下一轮重放沿用已存意图——不抛、不建第二张单，推进到等关卡到站。
    /// </summary>
    /// <remarks>
    /// 修之前（守护一直开到推进结束、意图按新的 now 生成）：下一轮起这台车每一轮都抛 <c>BusinessIdentityConflictException</c>，
    /// 它后面的车都不再推进。
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("IntegrationSlice", "FP-IS-02")]
    public async Task AJourneyWrittenAfterTheGateOrderWasCreatedIsReplayedWithoutASecondOrder(bool blockedThenRecovered)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(DemandId, "SUBLOT-001", createdAt: Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        JourneyRuntimeRow departing = await fixture.AdvanceToDepartureSafetyAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingDepartureSafety, departing.Stage);
        await fixture.AddInboxAsync(
            Guid.NewGuid().ToString("D"),
            "PreDepartureSafetyCheckResult",
            new
            {
                preDepartureSafetyCheckId = departing.PreDepartureSafetyCheckId,
                checkPurpose = "DEPARTURE",
                outcome = "SAFE",
                observedAt = fixture.Clock.GetUtcNow(),
                safetyStateVersion = 7,
                validUntil = fixture.Clock.GetUtcNow().AddMinutes(1),
                safety = new
                {
                    departureSafe = true,
                    vehicleStopped = true,
                    allTargetSlotsLocked = true,
                    allUnlockOutputsReset = true,
                    unknownPresent = false,
                    reasonCodes = Array.Empty<string>()
                }
            },
            departing.PreDepartureSafetyCheckMessageId);
        fixture.Context.ChangeTracker.Clear();

        // 入站的那一次写：取消结果证明不了空转阻塞，或者只写一个码。落在建单之后的第一次保存上。
        string inbound = blockedThenRecovered
            ? "UPDATE JourneyRuntimes SET Stage = 'Blocked', BlockReasonCode = 'LoadCancellationResult_NOT_RECONCILED', " +
              "Version = Version + 1 WHERE JourneyId = $id"
            : "UPDATE JourneyRuntimes SET BlockReasonCode = 'ONBOARD_SESSION_NOT_READY', Version = Version + 1 WHERE JourneyId = $id";
        bool injected = false;
        fixture.SaveChanges.FailWhen = _ =>
        {
            if (!injected && fixture.Riot.CreateCount("TO_GATE") == 1)
            {
                injected = true;
                Execute(fixture, inbound, departing.JourneyId);
            }
            return false;
        };
        await fixture.Engine.ExecuteOnceAsync(token);
        fixture.SaveChanges.FailWhen = null;

        // 前提：单建了、注入发生了、这台车因旅程行冲突让开了一次，而且让开的正是它。
        Assert.True(injected);
        Assert.Equal(1, fixture.Riot.CreateCount("TO_GATE"));
        AssertYieldedOnceFor(fixture.EngineLog, departing.JourneyId);
        Assert.Equal(1, Count(fixture, "SELECT COUNT(*) FROM OrderIntents WHERE UpperId LIKE '%-GATE-%'"));
        // 建单之后撤掉守护：记下「单已建、已确认」的那次保存不因这次冲突丢掉。守护若一直开着，这一轮让开会把它连同审计一起丢掉，
        // 下一轮只能靠向 RIoT 对账补回「确认」，这次建单本身在派车审计里就缺了一段——不会建第二张单，所以只有这一句看得见它。
        Assert.Equal(1, Count(fixture, "SELECT COUNT(*) FROM OrderIntents WHERE UpperId LIKE '%-GATE-%' AND Status = 'CONFIRMED'"));

        if (blockedThenRecovered)
        {
            fixture.Clock.Advance(TimeSpan.FromSeconds(1));
            fixture.Context.ChangeTracker.Clear();
            await fixture.Engine.ExecuteOnceAsync(token);
            Assert.Equal(JourneyRuntimeStage.Blocked, (await ReadAsync(fixture, departing.JourneyId)).Stage);
            // 人处理完，旅程回到离站那一步。
            Execute(fixture,
                "UPDATE JourneyRuntimes SET Stage = 'AwaitingDepartureSafety', BlockReasonCode = NULL, BlockReasonSince = NULL, " +
                "Version = Version + 1 WHERE JourneyId = $id",
                departing.JourneyId);
        }
        for (int round = 0; round < 2; round++)
        {
            fixture.Clock.Advance(TimeSpan.FromSeconds(1));
            fixture.Context.ChangeTracker.Clear();
            Exception? thrown = await Record.ExceptionAsync(() => fixture.Engine.ExecuteOnceAsync(token));
            Assert.Null(thrown);
        }

        Assert.Equal(1, fixture.Riot.CreateCount("TO_GATE"));
        Assert.Equal(JourneyRuntimeStage.AwaitingGateArrival, (await ReadAsync(fixture, departing.JourneyId)).Stage);
    }

    /// <summary>
    /// 入站写落在建单之前的对账审计那次保存上（增量复核必修 M1）：意图已存、RIoT 已按单号查过没有这张单，接下来就要建。
    /// 这几次保存不碰旅程行、令牌核不到，只有守护挡得住——单不能建，这台车恰好让开一次，Blocked 保住。
    /// </summary>
    /// <remarks>
    /// 上一版在意图存下之后立刻撤守护，这一格就漏了：车让开了，关卡单照样建了一张（复核员的探针 <c>c=1</c>）。守护现在由派车服务在对账审计之后、
    /// 用掉建单计数之前撤。把撤守护挪回意图存下之后，这一条红。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    public async Task ABlockCommittedAtThePreCreateReconciliationStopsTheGateOrder()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(DemandId, "SUBLOT-001", createdAt: Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        JourneyRuntimeRow departing = await fixture.AdvanceToDepartureSafetyAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingDepartureSafety, departing.Stage);
        await fixture.AddInboxAsync(
            Guid.NewGuid().ToString("D"),
            "PreDepartureSafetyCheckResult",
            new
            {
                preDepartureSafetyCheckId = departing.PreDepartureSafetyCheckId,
                checkPurpose = "DEPARTURE",
                outcome = "SAFE",
                observedAt = fixture.Clock.GetUtcNow(),
                safetyStateVersion = 7,
                validUntil = fixture.Clock.GetUtcNow().AddMinutes(1),
                safety = new
                {
                    departureSafe = true,
                    vehicleStopped = true,
                    allTargetSlotsLocked = true,
                    allUnlockOutputsReset = true,
                    unknownPresent = false,
                    reasonCodes = Array.Empty<string>()
                }
            },
            departing.PreDepartureSafetyCheckMessageId);
        fixture.Context.ChangeTracker.Clear();

        bool injected = false;
        fixture.SaveChanges.FailWhen = written =>
        {
            // 这一轮第一次写派车审计、单还没建：建单之前的对账审计。
            if (!injected && fixture.Riot.CreateCount("TO_GATE") == 0 &&
                written.Any(column => column.StartsWith("RiotDispatchAuditEventRow.", StringComparison.Ordinal)))
            {
                injected = true;
                Execute(fixture,
                    "UPDATE JourneyRuntimes SET Stage = 'Blocked', BlockReasonCode = 'LoadCancellationResult_NOT_RECONCILED', " +
                    "Version = Version + 1 WHERE JourneyId = $id",
                    departing.JourneyId);
            }
            return false;
        };
        await fixture.Engine.ExecuteOnceAsync(token);
        fixture.SaveChanges.FailWhen = null;

        Assert.True(injected);
        AssertYieldedOnceFor(fixture.EngineLog, departing.JourneyId);
        Assert.Equal(0, fixture.Riot.CreateCount("TO_GATE"));
        Assert.Equal(JourneyRuntimeStage.Blocked, (await ReadAsync(fixture, departing.JourneyId)).Stage);
    }

    /// <summary>
    /// 前提（独立审查必修 3）：这台车恰好因旅程行冲突让开一次（2191），让开的正是 <paramref name="journeyId"/>。缺了它，引擎因别的原因
    /// 没走到保存（没走到建单、没走到发送）时，后果断言一样成立，用例空绿。
    /// </summary>
    internal static void AssertYieldedOnceFor(RecordingLogger<ControlServer.Host.Runtime.JourneyRuntimeEngine> log, string journeyId)
    {
        string line = Assert.Single(
            log.Entries.Select(entry => entry.Message),
            message => message.Contains("control-server#357", StringComparison.Ordinal));
        Assert.Contains(journeyId, line, StringComparison.Ordinal);
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
