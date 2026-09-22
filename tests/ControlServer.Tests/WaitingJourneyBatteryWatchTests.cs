using System.Data.Common;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;

namespace ControlServer.Tests;

/// <summary>
/// 旅程等人期间的电量监看（control-server#273，用户 2026-09-22 定方案 A）：车停着等人超过门槛，服务端读 RIoT 电量、记下来、按间隔打日志，
/// 低于接单线升 Error，低于救命线写明需要人工挪车充电——<b>从不下发行驶或充电命令</b>。
/// </summary>
/// <remarks>
/// <para>
/// v2 线没有自动充电（规格第 5.5 节，批次 9），票面「等人期间自动充电被冻住」的结构在这里不存在；这条线的风险是车在闸口等卸货、
/// 被阻断这类无期限的等待里一声不响地把电耗光。REQ-0169 只许电量下降「提高告警等级和响应紧迫度」，所以这里的每条用例都同时断言
/// 「没有新建任何 RIoT 订单、没有往车上发任何新报文、阶段与原因码原样」。
/// </para>
/// <para>
/// <b>计时一律拨夹具的钟，并先断言钟确实拨到了</b>：门槛前一秒不打、到点才打，是用拨过的钟证明的，不是用一个恒等的起点。
/// </para>
/// </remarks>
public sealed class WaitingJourneyBatteryWatchTests
{
    private const string WatchMarker = "has waited for a person";
    private const string RescueAdvice = "需要人工挪车充电";
    private const string DemandId = "10000000-0000-4000-8000-000000000001";

    // ---- 票面补测试第 1 条与评论里闸口那条 -------------------------------------------------------------------------

    /// <summary>
    /// 评论补的那条：满载停在闸口等人取货（<c>AwaitingUnloadResult</c>，没有原因码、没有期限），等满门槛就带着电量打一条，
    /// 门槛前一秒一条都没有。车一动不动：没有新订单，没有新报文，阶段不变。
    /// </summary>
    [Fact]
    public async Task AGateWaitingForItsUnloadIsLoggedWithItsBatteryOnceItHasWaitedTheThresholdAndNotASecondBefore()
    {
        await using RuntimeFixture fixture = await GateUnloadAsync();
        DateTimeOffset arrived = fixture.Clock.GetUtcNow();
        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { BatteryPercent = 55 };
        Quiet quiet = await Quiet.TakeAsync(fixture);

        await RoundAtAsync(fixture, arrived + fixture.Options.WaitingJourneyWarningAfter - TimeSpan.FromSeconds(1));
        Assert.Empty(WatchEntries(fixture));

        await RoundAtAsync(fixture, arrived + fixture.Options.WaitingJourneyWarningAfter);
        (LogLevel level, string message) = Assert.Single(WatchEntries(fixture));
        Assert.Equal(LogLevel.Warning, level);
        Assert.Contains(JourneyIdentity.ForAnchorDemand(DemandId), message, StringComparison.Ordinal);
        Assert.Contains("AwaitingUnloadResult", message, StringComparison.Ordinal);
        Assert.Contains("battery 55%", message, StringComparison.Ordinal);
        Assert.Contains("10 min", message, StringComparison.Ordinal);

        JourneyRuntimeRow runtime = await ReloadAsync(fixture);
        Assert.Equal(JourneyRuntimeStage.AwaitingUnloadResult, runtime.Stage);
        Assert.Null(runtime.BlockReasonCode);
        Assert.Equal(arrived, runtime.StageSince);
        Assert.Equal(55, runtime.WaitingBatteryPercent);
        Assert.Equal(arrived + fixture.Options.WaitingJourneyWarningAfter, runtime.WaitingBatteryObservedAt);
        await quiet.AssertNothingWasSentAsync(fixture);
    }

    /// <summary>
    /// 票面第 1 条：<c>Blocked</c>、车静止、电量低于救命线——票面要的是「触发充电」，用户定的是只报不动：升 Error，写明需要人工挪车充电，
    /// 阶段与原因码原样，<b>不建充电单</b>。
    /// </summary>
    [Fact]
    public async Task ABlockedJourneyUnderTheRescueLineIsAnErrorThatAsksForTheVehicleToBeMovedByHandAndNothingMovesIt()
    {
        await using RuntimeFixture fixture = await BlockedAtGateAsync();
        DateTimeOffset blocked = fixture.Clock.GetUtcNow();
        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { BatteryPercent = 12 };
        Quiet quiet = await Quiet.TakeAsync(fixture);

        await RoundAtAsync(fixture, blocked + fixture.Options.WaitingJourneyWarningAfter);

        (LogLevel level, string message) = Assert.Single(WatchEntries(fixture));
        Assert.Equal(LogLevel.Error, level);
        Assert.Contains("battery 12%", message, StringComparison.Ordinal);
        Assert.Contains("UNLOAD_RESULT_REQUIRES_RECOVERY", message, StringComparison.Ordinal);
        Assert.Contains(RescueAdvice, message, StringComparison.Ordinal);
        JourneyRuntimeRow runtime = await ReloadAsync(fixture);
        Assert.Equal(JourneyRuntimeStage.Blocked, runtime.Stage);
        Assert.Equal("UNLOAD_RESULT_REQUIRES_RECOVERY", runtime.BlockReasonCode);
        await quiet.AssertNothingWasSentAsync(fixture);
    }

    /// <summary>
    /// 票面第 2 条：<c>Blocked</c>、电量健康——不能一阻断就报；等满门槛才报，而且只是 Warning，没有救命那句话。
    /// </summary>
    [Fact]
    public async Task ABlockedJourneyWithAHealthyBatteryIsNotLoggedBeforeTheThresholdAndOnlyWarnedAfterIt()
    {
        await using RuntimeFixture fixture = await BlockedAtGateAsync();
        DateTimeOffset blocked = fixture.Clock.GetUtcNow();

        await RoundAtAsync(fixture, blocked + TimeSpan.FromMinutes(9));
        Assert.Empty(WatchEntries(fixture));

        await RoundAtAsync(fixture, blocked + TimeSpan.FromMinutes(10));
        (LogLevel level, string message) = Assert.Single(WatchEntries(fixture));
        Assert.Equal(LogLevel.Warning, level);
        Assert.Contains("battery 80%", message, StringComparison.Ordinal);
        Assert.DoesNotContain(RescueAdvice, message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 两道线的四个边界：等于接单线不升级，差 1 升 Error；等于救命线只升 Error，差 1 才加那句话。读不到电量是 Warning，写「unknown」。
    /// 夹具的接单线是 40（不是产品默认 30），救命线是默认 15；先断言这两个前提，免得夹具改了这里还在测别的数。
    /// </summary>
    [Theory]
    [InlineData(40, LogLevel.Warning, "battery 40%", false)]
    [InlineData(39, LogLevel.Error, "battery 39%", false)]
    [InlineData(15, LogLevel.Error, "battery 15%", false)]
    [InlineData(14, LogLevel.Error, "battery 14%", true)]
    [InlineData(null, LogLevel.Warning, "battery unknown", false)]
    public async Task TheLevelFollowsTheDispatchMinimumAndTheRescueLine(
        int? battery, LogLevel expectedLevel, string expectedBattery, bool asksForRescue)
    {
        await using RuntimeFixture fixture = await BlockedAtGateAsync();
        Assert.Equal(40, fixture.Options.MinimumBatteryPercent);
        Assert.Equal(15, fixture.Options.WaitingJourneyRescueBatteryPercent);
        DateTimeOffset blocked = fixture.Clock.GetUtcNow();
        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { BatteryPercent = battery };

        await RoundAtAsync(fixture, blocked + fixture.Options.WaitingJourneyWarningAfter);

        (LogLevel level, string message) = Assert.Single(WatchEntries(fixture));
        Assert.Equal(expectedLevel, level);
        Assert.Contains(expectedBattery, message, StringComparison.Ordinal);
        Assert.Equal(asksForRescue, message.Contains(RescueAdvice, StringComparison.Ordinal));
        Assert.Equal(battery, (await ReloadAsync(fixture)).WaitingBatteryPercent);
    }

    // ---- 间隔 ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// 等待持续期间按间隔重复：到点一条，间隔内的轮次一条不加，满一个间隔再一条。中间重启一次服务端（换一台引擎）也不提前重复——
    /// 上一次打的时刻落了库。
    /// </summary>
    [Fact]
    public async Task TheWaitIsLoggedAgainOnceEveryRepeatIntervalAndARestartNeitherRepeatsNorLosesTheCadence()
    {
        await using RuntimeFixture fixture = await BlockedAtGateAsync();
        DateTimeOffset blocked = fixture.Clock.GetUtcNow();
        TimeSpan first = fixture.Options.WaitingJourneyWarningAfter;
        TimeSpan repeat = fixture.Options.WaitingJourneyWarningRepeat;

        await RoundAtAsync(fixture, blocked + first);
        Assert.Single(WatchEntries(fixture));

        await RoundAtAsync(fixture, blocked + first + TimeSpan.FromMinutes(1));
        await fixture.RecreateEngineAsync();
        await RoundAtAsync(fixture, blocked + first + repeat - TimeSpan.FromSeconds(1));
        Assert.Single(WatchEntries(fixture));

        await RoundAtAsync(fixture, blocked + first + repeat);
        Assert.Equal(2, WatchEntries(fixture).Length);
        Assert.Contains("15 min", WatchEntries(fixture)[1].Message, StringComparison.Ordinal);
    }

    // ---- 读电量失败不能拖住推进 ------------------------------------------------------------------------------------

    /// <summary>
    /// RIoT 读车失败（抛异常）：这一轮照常结束、不抛，日志写 battery unknown，库里记下「那一刻未知」；之后读仍然失败，人把货取了，
    /// 下一轮照样把旅程推到完成——读电量失败从不拖住推进。
    /// </summary>
    [Fact]
    public async Task AFailedBatteryReadIsLoggedAsUnknownAndNeverHoldsTheJourneyBack()
    {
        await using RuntimeFixture fixture = await GateUnloadAsync();
        DateTimeOffset arrived = fixture.Clock.GetUtcNow();
        fixture.Riot.BeforeReadVehicle = () => throw new HttpRequestException("RIoT is not answering.");

        DateTimeOffset due = arrived + fixture.Options.WaitingJourneyWarningAfter;
        await RoundAtAsync(fixture, due);

        (LogLevel level, string message) = Assert.Single(WatchEntries(fixture));
        Assert.Equal(LogLevel.Warning, level);
        Assert.Contains("battery unknown", message, StringComparison.Ordinal);
        JourneyRuntimeRow runtime = await ReloadAsync(fixture);
        Assert.Null(runtime.WaitingBatteryPercent);
        Assert.Equal(due, runtime.WaitingBatteryObservedAt);

        StationOperationRow unload = await fixture.OperationAsync(SlotOperationType.Unload);
        await fixture.ApplySafeResultAsync(unload, SlotOperationType.Unload, SlotBusinessState.Empty);
        await RoundAtAsync(fixture, due + TimeSpan.FromSeconds(2));

        Assert.Equal(JourneyRuntimeStage.Completed, (await ReloadAsync(fixture)).Stage);
    }

    /// <summary>
    /// 库拒绝监看的那次写（这里模拟 SQLite 忙，<c>SQLITE_BUSY</c>）：这一轮照常结束、不抛，日志照打，另记一条「这一轮没记下」；
    /// 旅程的阶段与原因码原样，下一轮库好了就补记上。监看的失败从不变成这一轮的失败。
    /// </summary>
    [Fact]
    public async Task AWatchWriteTheDatabaseRefusesNeitherEndsTheRoundNorTouchesTheJourney()
    {
        RefuseWatchWrites refuse = new();
        await using RuntimeFixture fixture = await BlockedAtGateAsync(refuse);
        DateTimeOffset blocked = fixture.Clock.GetUtcNow();
        refuse.Armed = true;

        DateTimeOffset due = blocked + fixture.Options.WaitingJourneyWarningAfter;
        await RoundAtAsync(fixture, due);

        Assert.True(refuse.Fired);
        Assert.Single(WatchEntries(fixture));
        lock (fixture.EngineLog.Entries)
        {
            Assert.Contains(fixture.EngineLog.Entries, entry =>
                entry.Level == LogLevel.Warning &&
                entry.Message.Contains("could not record journey", StringComparison.Ordinal));
        }
        JourneyRuntimeRow runtime = await ReloadAsync(fixture);
        Assert.Equal(JourneyRuntimeStage.Blocked, runtime.Stage);
        Assert.Equal("UNLOAD_RESULT_REQUIRES_RECOVERY", runtime.BlockReasonCode);
        Assert.Null(runtime.WaitingWarnedAt);

        refuse.Armed = false;
        await RoundAtAsync(fixture, due + TimeSpan.FromSeconds(2));
        Assert.Equal(due + TimeSpan.FromSeconds(2), (await ReloadAsync(fixture)).WaitingWarnedAt);
    }

    // ---- 票面第 3 条：正常推进不变 --------------------------------------------------------------------------------

    /// <summary>
    /// 票面第 3 条：正在路上、没有任何原因码的旅程不是等人，走多久都不报；监看也不给它记新的读数（在取货站停着时记的那一次原样留着）。
    /// </summary>
    [Fact]
    public async Task AJourneyUnderWayOnALegIsNotAWaitHoweverLongItTakes()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(DemandId, "SUBLOT-001", Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        JourneyRuntimeRow onTheWay = await fixture.AdvanceToGateArrivalAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingGateArrival, onTheWay.Stage);
        DateTimeOffset left = fixture.Clock.GetUtcNow();
        // The reading taken while it stood at the pickup; a leg under way must not add one.
        DateTimeOffset? lastReadAtThePickup = (await ReloadAsync(fixture)).WaitingBatteryObservedAt;

        await RoundAtAsync(fixture, left + TimeSpan.FromMinutes(30));

        JourneyRuntimeRow runtime = await ReloadAsync(fixture);
        Assert.Equal(JourneyRuntimeStage.AwaitingGateArrival, runtime.Stage);
        Assert.Null(runtime.BlockReasonCode);
        Assert.Empty(WatchEntries(fixture));
        Assert.Equal(lastReadAtThePickup, runtime.WaitingBatteryObservedAt);
    }

    /// <summary>
    /// 同一条路上一旦引擎说出了「为什么没到」（这里是 RIoT 把单子挂起、引擎命名的那一类），它就是等人，照样按门槛报。
    /// </summary>
    [Fact]
    public async Task ALegWhoseReasonForNotArrivingIsNamedIsAWaitLikeAnyOther()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(DemandId, "SUBLOT-001", Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        JourneyRuntimeRow onTheWay = await fixture.AdvanceToGateArrivalAsync();
        DateTimeOffset left = fixture.Clock.GetUtcNow();
        fixture.Riot.FailOrder(onTheWay.GateUpperId);
        await RoundAtAsync(fixture, left + TimeSpan.FromSeconds(2));
        JourneyRuntimeRow failed = await ReloadAsync(fixture);
        Assert.Equal(JourneyRuntimeStage.AwaitingGateArrival, failed.Stage);
        Assert.NotNull(failed.BlockReasonCode);

        await RoundAtAsync(fixture, left + fixture.Options.WaitingJourneyWarningAfter);

        (_, string message) = Assert.Single(WatchEntries(fixture));
        Assert.Contains(failed.BlockReasonCode, message, StringComparison.Ordinal);
    }

    // ---- 等人阶段的集中定义 ----------------------------------------------------------------------------------------

    /// <summary>
    /// 每个阶段都分了类，而且整张表就是审过的那张（阶段一实读 <c>AdvanceAsync</c> 得出）。给枚举加一个阶段而不来这里登记，
    /// 第一条断言就红——键集合必须与枚举的全部取值相等，不是「包含」。
    /// </summary>
    [Fact]
    public void EveryStageIsClassifiedAndTheTableIsTheOneThatWasReviewed()
    {
        Dictionary<JourneyRuntimeStage, JourneyStageActivity> reviewed = new()
        {
            [JourneyRuntimeStage.AwaitingPickupArrival] = JourneyStageActivity.Travelling,
            [JourneyRuntimeStage.AwaitingSublot] = JourneyStageActivity.Stationary,
            [JourneyRuntimeStage.AwaitingLoadResult] = JourneyStageActivity.Stationary,
            [JourneyRuntimeStage.AwaitingStationDeparture] = JourneyStageActivity.Stationary,
            [JourneyRuntimeStage.AwaitingDepartureSafety] = JourneyStageActivity.Stationary,
            [JourneyRuntimeStage.AwaitingGateArrival] = JourneyStageActivity.Travelling,
            [JourneyRuntimeStage.AwaitingUnloadResult] = JourneyStageActivity.Stationary,
            [JourneyRuntimeStage.Blocked] = JourneyStageActivity.Stationary,
            [JourneyRuntimeStage.Completed] = JourneyStageActivity.Finished
        };

        Assert.Equal(
            Enum.GetValues<JourneyRuntimeStage>().Order(),
            reviewed.Keys.Order());
        foreach ((JourneyRuntimeStage stage, JourneyStageActivity activity) in reviewed)
        {
            Assert.Equal(activity, JourneyWaitClassification.Of(stage));
        }
        Assert.Throws<InvalidDataException>(() => JourneyWaitClassification.Of((JourneyRuntimeStage)999));
    }

    // ---- 阶段起点由上下文盖章 ----------------------------------------------------------------------------------------

    /// <summary>
    /// <c>StageSince</c> 由保存盖章：新增的行取它的 <c>UpdatedAt</c>；换阶段的那次保存取那次的 <c>UpdatedAt</c>；只动别的列、
    /// 或者把阶段写成它已有的值（再阻断一次、换个原因码），起点不动。这是判据的前提——已等时长从这里算。
    /// </summary>
    [Fact]
    public async Task TheStageStartIsStampedByTheSaveThatMovesTheStageAndNoOtherSave()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        DbContextOptions<ControlServerDbContext> options =
            new DbContextOptionsBuilder<ControlServerDbContext>().UseSqlite(connection).Options;
        await using (ControlServerDbContext create = new(options))
        {
            await create.Database.EnsureCreatedAsync(cancellationToken);
        }
        DateTimeOffset t0 = new(2026, 9, 22, 1, 0, 0, TimeSpan.Zero);

        await using (ControlServerDbContext add = new(options))
        {
            add.JourneyRuntimes.Add(Runtime("D-STAMP", JourneyRuntimeStage.AwaitingUnloadResult, t0));
            await add.SaveChangesAsync(cancellationToken);
        }
        Assert.Equal(t0, await StageSinceAsync(options));

        await EditAsync(options, row => { row.WorklistRevision++; row.UpdatedAt = t0.AddMinutes(3); });
        Assert.Equal(t0, await StageSinceAsync(options));

        await EditAsync(options, row =>
        {
            row.Stage = JourneyRuntimeStage.Blocked;
            row.SetBlockReason("UNLOAD_RESULT_REQUIRES_RECOVERY", t0.AddMinutes(7));
            row.UpdatedAt = t0.AddMinutes(7);
        });
        Assert.Equal(t0.AddMinutes(7), await StageSinceAsync(options));

        await EditAsync(options, row =>
        {
            row.Stage = JourneyRuntimeStage.Blocked;
            row.SetBlockReason("ONBOARD_SESSION_LOST", t0.AddMinutes(9));
            row.UpdatedAt = t0.AddMinutes(9);
        });
        Assert.Equal(t0.AddMinutes(7), await StageSinceAsync(options));
    }

    // ---- 配置 ------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheDefaultsAreTheOnesTheUserDecided()
    {
        JourneyRuntimeOptions defaults = new();
        Assert.Equal(TimeSpan.FromMinutes(10), defaults.WaitingJourneyWarningAfter);
        Assert.Equal(TimeSpan.FromMinutes(5), defaults.WaitingJourneyWarningRepeat);
        Assert.Equal(30, defaults.MinimumBatteryPercent);
        Assert.Equal(15, defaults.WaitingJourneyRescueBatteryPercent);
    }

    [Theory]
    [InlineData("WaitingJourneyWarningAfter", "00:00:00", "WaitingJourneyWarningAfter must be positive.")]
    [InlineData("WaitingJourneyWarningRepeat", "00:00:00", "WaitingJourneyWarningRepeat must be positive.")]
    [InlineData("WaitingJourneyRescueBatteryPercent", "0", "WaitingJourneyRescueBatteryPercent must be at least 1 and below MinimumBatteryPercent.")]
    [InlineData("WaitingJourneyRescueBatteryPercent", "40", "WaitingJourneyRescueBatteryPercent must be at least 1 and below MinimumBatteryPercent.")]
    public async Task ASettingThatCannotBeAWaitingLineIsRefused(string key, string value, string failure)
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        JourneyRuntimeOptions options = fixture.Options;
        Assert.Equal(40, options.MinimumBatteryPercent);
        new ConfigurationBuilder()
            .AddInMemoryCollection([new KeyValuePair<string, string?>(key, value)])
            .Build()
            .Bind(options);

        Microsoft.Extensions.Options.ValidateOptionsResult result =
            new JourneyRuntimeOptionsValidator(new ConfigurationBuilder().Build()).Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(failure, result.Failures ?? []);
    }

    [Fact]
    public async Task TheFixtureDefaultsPassTheWaitingChecks()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        Microsoft.Extensions.Options.ValidateOptionsResult result =
            new JourneyRuntimeOptionsValidator(new ConfigurationBuilder().Build()).Validate(null, fixture.Options);
        // The fixture leaves the three secret environment variables unset, which the validator also reports; only the
        // waiting settings are this test's business.
        Assert.DoesNotContain(result.Failures ?? [], failure => failure.Contains("WaitingJourney", StringComparison.Ordinal));
    }

    // ---- helpers ---------------------------------------------------------------------------------------------------

    /// <summary>一趟单需求旅程停在闸口、卸货命令已发、等人取货。</summary>
    private static async Task<RuntimeFixture> GateUnloadAsync(DbCommandInterceptor? commands = null)
    {
        RuntimeFixture fixture = await RuntimeFixture.CreateAsync(commands: commands);
        fixture.Catalog.Set(fixture.Demand(DemandId, "SUBLOT-001", Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        JourneyRuntimeRow runtime = await fixture.RunToGateUnloadAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingUnloadResult, runtime.Stage);
        return fixture;
    }

    /// <summary>同一趟旅程，卸货结果回来是需恢复，引擎把它阻断成 <c>UNLOAD_RESULT_REQUIRES_RECOVERY</c>——生产上会走的那条路。</summary>
    private static async Task<RuntimeFixture> BlockedAtGateAsync(DbCommandInterceptor? commands = null)
    {
        RuntimeFixture fixture = await GateUnloadAsync(commands);
        StationOperationRow unload = await fixture.OperationAsync(SlotOperationType.Unload);
        await fixture.ApplyTimedOutResultAsync(unload, SlotOperationType.Unload);
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        JourneyRuntimeRow runtime = await ReloadAsync(fixture);
        Assert.Equal(JourneyRuntimeStage.Blocked, runtime.Stage);
        Assert.Equal("UNLOAD_RESULT_REQUIRES_RECOVERY", runtime.BlockReasonCode);
        Assert.Equal(fixture.Clock.GetUtcNow(), runtime.StageSince);
        return fixture;
    }

    /// <summary>把钟拨到 <paramref name="at"/>（断言确实拨到了），车载端照常有心跳，然后跑一轮。</summary>
    private static async Task RoundAtAsync(RuntimeFixture fixture, DateTimeOffset at)
    {
        TimeSpan step = at - fixture.Clock.GetUtcNow();
        Assert.True(step >= TimeSpan.Zero, $"The clock cannot go back ({step}).");
        fixture.Clock.Advance(step);
        Assert.Equal(at, fixture.Clock.GetUtcNow());
        await fixture.HearFromPeerAsync();
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
    }

    private static (LogLevel Level, string Message)[] WatchEntries(RuntimeFixture fixture)
    {
        lock (fixture.EngineLog.Entries)
        {
            return [.. fixture.EngineLog.Entries.Where(entry => entry.Message.Contains(WatchMarker, StringComparison.Ordinal))];
        }
    }

    private static async Task<JourneyRuntimeRow> ReloadAsync(RuntimeFixture fixture)
    {
        fixture.Context.ChangeTracker.Clear();
        return await fixture.RuntimeAsync();
    }

    /// <summary>「车一动不动」的证据：此刻为止 RIoT 建过的单数与发件箱里的报文，之后都不许多出来。</summary>
    private sealed record Quiet(int Creates, string[] Outbox)
    {
        public static async Task<Quiet> TakeAsync(RuntimeFixture fixture) =>
            new(fixture.Riot.TotalCreateCount, await OutboxIdsAsync(fixture));

        public async Task AssertNothingWasSentAsync(RuntimeFixture fixture)
        {
            Assert.Equal(Creates, fixture.Riot.TotalCreateCount);
            Assert.Equal(Outbox, await OutboxIdsAsync(fixture));
        }

        private static async Task<string[]> OutboxIdsAsync(RuntimeFixture fixture) =>
            [.. (await fixture.Context.ProtocolOutbox.AsNoTracking()
                    .Select(row => row.MessageId)
                    .ToArrayAsync(TestContext.Current.CancellationToken))
                .Order(StringComparer.Ordinal)];
    }

    private static async Task<DateTimeOffset?> StageSinceAsync(DbContextOptions<ControlServerDbContext> options)
    {
        await using ControlServerDbContext read = new(options);
        return (await read.JourneyRuntimes.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken)).StageSince;
    }

    private static async Task EditAsync(DbContextOptions<ControlServerDbContext> options, Action<JourneyRuntimeRow> edit)
    {
        await using ControlServerDbContext write = new(options);
        JourneyRuntimeRow row = await write.JourneyRuntimes.SingleAsync(TestContext.Current.CancellationToken);
        edit(row);
        await write.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Refuses the watch's one write -- the UPDATE that records a battery reading -- the way a busy database does.</summary>
    private sealed class RefuseWatchWrites : DbCommandInterceptor
    {
        public bool Armed { get; set; }

        public bool Fired { get; private set; }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (Armed &&
                command.CommandText.TrimStart().StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase) &&
                command.CommandText.Contains("\"WaitingBatteryObservedAt\"", StringComparison.Ordinal))
            {
                Fired = true;
                throw new SqliteException("database is locked", 5);
            }
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    internal static JourneyRuntimeRow Runtime(string demandId, JourneyRuntimeStage stage, DateTimeOffset updatedAt) => new()
    {
        JourneyId = JourneyIdentity.ForAnchorDemand(demandId),
        DemandId = demandId,
        Stage = stage,
        AgvId = "AGV-" + demandId,
        VehicleKey = "KEY-" + demandId,
        AgvLifecycleGeneration = 1,
        MapId = 25,
        MapIdentity = "map-25",
        DispatchZone = "MAP-25-WIRE_TO_GATE",
        RouteEvidenceId = "route-" + demandId,
        PickupStationId = "PICKUP-1",
        PickupStationRiotId = 11,
        GateStationId = "GATE-1",
        GateStationRiotId = 22,
        ExpectedBasketCount = 2,
        TargetSlotsJson = "[1,2]",
        OperationSessionId = "session-" + demandId,
        PickupMovementLegId = "pickup-leg-" + demandId,
        PickupUpperId = "UPPER-PICKUP-" + demandId,
        GateMovementLegId = "gate-leg-" + demandId,
        GateUpperId = "UPPER-GATE-" + demandId,
        DispatchGeneration = 1,
        VehicleBusinessRevision = 1,
        WorklistRevision = 1,
        PlanRevision = 1,
        VehicleBusinessMessageId = "vb-" + demandId,
        WorklistMessageId = "wl-" + demandId,
        PlanMessageId = "plan-" + demandId,
        SublotRequestMessageId = "sublot-" + demandId,
        LoadCommandMessageId = "load-" + demandId,
        LoadSlotOperationAttemptId = "load-attempt-" + demandId,
        PreDepartureSafetyCheckMessageId = "check-message-" + demandId,
        PreDepartureSafetyCheckId = "check-" + demandId,
        GateVehicleBusinessMessageId = "gate-vb-" + demandId,
        GateWorklistMessageId = "gate-wl-" + demandId,
        GatePlanMessageId = "gate-plan-" + demandId,
        UnloadCommandMessageId = "unload-" + demandId,
        UnloadSlotOperationAttemptId = "unload-attempt-" + demandId,
        CreatedAt = updatedAt.AddHours(-1),
        UpdatedAt = updatedAt
    };
}
