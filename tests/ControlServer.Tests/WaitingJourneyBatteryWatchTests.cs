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
    // The log's rescue advice. The dashboard card says it in Chinese; the log stays English (see WaitingJourneyWatch).
    private const string RescueAdvice = "a person has to move the vehicle to a charger";
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
        Assert.Equal(arrived, runtime.WaitingSince);
        Assert.Equal(55, runtime.WaitingBatteryPercent);
        Assert.Equal(arrived + fixture.Options.WaitingJourneyWarningAfter, runtime.WaitingBatteryObservedAt);
        await quiet.AssertNothingWasSentAsync(fixture);

        // The person takes the goods: the journey completes, and no later round says a word about it again -- not at the
        // next repeat, not half an hour on.
        StationOperationRow unload = await fixture.OperationAsync(SlotOperationType.Unload);
        await fixture.ApplySafeResultAsync(unload, SlotOperationType.Unload, SlotBusinessState.Empty);
        await RoundAtAsync(fixture, arrived + fixture.Options.WaitingJourneyWarningAfter + TimeSpan.FromSeconds(2));
        Assert.Equal(JourneyRuntimeStage.Completed, (await ReloadAsync(fixture)).Stage);
        await RoundAtAsync(fixture, arrived + fixture.Options.WaitingJourneyWarningAfter + fixture.Options.WaitingJourneyWarningRepeat);
        await RoundAtAsync(fixture, arrived + TimeSpan.FromMinutes(45));
        Assert.Single(WatchEntries(fixture));
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
    /// 车载会话未就绪（调度 09-22 转来，#316 审查坐实的坑）：<c>AdvanceAsync</c> 在 <c>session is null</c> 那一支写上
    /// <c>ONBOARD_SESSION_NOT_READY</c> 就返回，放在它后面的逻辑在真车上整段不跑；真车载端挂着本服务端的在途单时照例就是未就绪，
    /// 合成车载端永远报安全，合成 L2 看不出来。监看不在那一支后面——引擎在全部旅程推进之后另行调用它——所以闸口等人超过门槛时，
    /// 电量照读、告警照打，原因码写的是会话未就绪，阶段与等人起点不动。
    /// </summary>
    /// <remarks>会话掉线的形状照 <c>PickupDispatchPlanPastOwnOrderTests.DropSessionOnOwnOrderAsync</c>。</remarks>
    [Fact]
    public async Task AGateWaitWhileTheOnboardSessionIsNotReadyIsStillReadAndLogged()
    {
        await using RuntimeFixture fixture = await GateUnloadAsync();
        DateTimeOffset arrived = fixture.Clock.GetUtcNow();
        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { BatteryPercent = 21 };
        Quiet quiet = await Quiet.TakeAsync(fixture);
        SessionRecoveryRow session = await fixture.Context.SessionRecoveries.SingleAsync(TestContext.Current.CancellationToken);
        session.Readiness = SessionReadiness.RecoveryRequired;
        session.ReasonCode = "DEPARTURE_SAFETY_NOT_READY";
        session.DepartureSafe = false;
        session.SafetyReasonCodesJson = """["VEHICLE_NOT_READY"]""";
        session.SafetyUnknownPresent = true;
        session.UpdatedAt = fixture.Clock.GetUtcNow();
        await fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        DateTimeOffset due = arrived + fixture.Options.WaitingJourneyWarningAfter;
        await RoundAtAsync(fixture, due);

        JourneyRuntimeRow runtime = await ReloadAsync(fixture);
        // The round did go through the not-ready branch: without this the test could pass on a session still Ready.
        Assert.Equal("ONBOARD_SESSION_NOT_READY", runtime.BlockReasonCode);
        Assert.Equal(JourneyRuntimeStage.AwaitingUnloadResult, runtime.Stage);
        Assert.Equal(arrived, runtime.WaitingSince);
        Assert.Equal(21, runtime.WaitingBatteryPercent);
        Assert.Equal(due, runtime.WaitingBatteryObservedAt);
        (LogLevel level, string message) = Assert.Single(WatchEntries(fixture));
        Assert.Equal(LogLevel.Error, level);
        Assert.Contains("battery 21%", message, StringComparison.Ordinal);
        Assert.Contains("(reason ONBOARD_SESSION_NOT_READY)", message, StringComparison.Ordinal);
        Assert.Equal(quiet.Creates, fixture.Riot.TotalCreateCount);
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

    /// <summary>
    /// RIoT 读车卡住（#320 审查中项 2）：读电量有自己的预算，超时即记「未知」，这一轮在预算量级内结束，不等网关自己的 30 秒。
    /// 替身的读车要 20 秒才答，预算设 200 毫秒；没有预算的实现这一轮要 20 秒，判据给 5 秒。计时用真实时钟——预算的计时器就是真实的，
    /// 与派车轮的按车预算同一种做法（<c>DispatchRoundRunner.TryAdmitToRoundAsync</c>）。
    /// </summary>
    [Fact]
    public async Task ABatteryReadThatHangsIsCutOffAtItsBudgetAndRecordedAsUnknown()
    {
        await using RuntimeFixture fixture = await BlockedAtGateAsync();
        fixture.Options.WaitingJourneyBatteryReadBudget = TimeSpan.FromMilliseconds(200);
        DateTimeOffset blocked = fixture.Clock.GetUtcNow();
        fixture.Riot.ReadVehicleDelay = token => Task.Delay(TimeSpan.FromSeconds(20), token);

        System.Diagnostics.Stopwatch elapsed = System.Diagnostics.Stopwatch.StartNew();
        await RoundAtAsync(fixture, blocked + fixture.Options.WaitingJourneyWarningAfter);
        elapsed.Stop();

        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(5), $"The round took {elapsed.Elapsed}.");
        (_, string message) = Assert.Single(WatchEntries(fixture));
        Assert.Contains("battery unknown", message, StringComparison.Ordinal);
        JourneyRuntimeRow runtime = await ReloadAsync(fixture);
        Assert.Null(runtime.WaitingBatteryPercent);
        Assert.Equal(blocked + fixture.Options.WaitingJourneyWarningAfter, runtime.WaitingBatteryObservedAt);
    }

    /// <summary>
    /// 这一轮读地图目录失败、提前返回（#320 审查低项 5）：引擎在这种时候最该有人知道车停着，所以监看照跑。先断言这一轮确实走了
    /// 目录失败那条路（日志里有那一条），再断言告警照打。
    /// </summary>
    [Fact]
    public async Task TheWatchStillRunsInARoundWhoseMapCatalogReadFailed()
    {
        await using RuntimeFixture fixture = await BlockedAtGateAsync();
        DateTimeOffset blocked = fixture.Clock.GetUtcNow();
        fixture.Riot.FailNextMapRead = new HttpRequestException("RIoT map endpoint is down.");

        await RoundAtAsync(fixture, blocked + fixture.Options.WaitingJourneyWarningAfter);

        lock (fixture.EngineLog.Entries)
        {
            Assert.Contains(fixture.EngineLog.Entries, entry =>
                entry.Message.Contains("RIoT Map station catalog failed closed", StringComparison.Ordinal));
        }
        (_, string message) = Assert.Single(WatchEntries(fixture));
        Assert.Contains("UNLOAD_RESULT_REQUIRES_RECOVERY", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 这一轮推进抛了异常（#320 审查低项 5）：这里用同一辆车挂两趟未完成旅程——引擎每一轮都在推进之前就抛
    /// <see cref="BusinessIdentityConflictException"/>，是一台真卡住的引擎的样子。异常照样抛出这一轮，监看在它之前照跑，告警照打。
    /// </summary>
    [Fact]
    public async Task TheWatchStillRunsInARoundThatThrew()
    {
        await using RuntimeFixture fixture = await BlockedAtGateAsync();
        DateTimeOffset blocked = fixture.Clock.GetUtcNow();
        JourneyRuntimeRow waiting = await ReloadAsync(fixture);
        JourneyRuntimeRow second = Runtime("D-DOUBLE-BOOKED", JourneyRuntimeStage.AwaitingPickupArrival, blocked);
        second.AgvId = waiting.AgvId;
        second.VehicleKey = waiting.VehicleKey;
        fixture.Context.JourneyRuntimes.Add(second);
        await fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        TimeSpan step = blocked + fixture.Options.WaitingJourneyWarningAfter - fixture.Clock.GetUtcNow();
        fixture.Clock.Advance(step);
        await fixture.HearFromPeerAsync();
        BusinessIdentityConflictException thrown = await Assert.ThrowsAsync<BusinessIdentityConflictException>(
            () => fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken));

        Assert.Contains("More than one unresolved journey", thrown.Message, StringComparison.Ordinal);
        (_, string message) = Assert.Single(WatchEntries(fixture));
        Assert.Contains(waiting.JourneyId, message, StringComparison.Ordinal);
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
    /// 同一条路上一旦引擎说出了「为什么没到」（这里是 RIoT 报单子失败、引擎命名的那一类），它就是等人——<b>从原因出现的那一刻算</b>，
    /// 不从出发算（#320 审查中项 1）。车正常开了 12 分钟、到尾声才出事：出事后 9 分 59 秒一条不报，满 10 分钟才报，报的是「10 min」。
    /// 从出发算的实现会在出事那一刻就报「已等 12 分钟」。
    /// </summary>
    [Fact]
    public async Task ALegThatNamesAReasonLateWaitsFromTheReasonNotFromTheDeparture()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(DemandId, "SUBLOT-001", Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        JourneyRuntimeRow onTheWay = await fixture.AdvanceToGateArrivalAsync();
        DateTimeOffset left = fixture.Clock.GetUtcNow();
        await RoundAtAsync(fixture, left + TimeSpan.FromMinutes(12));
        Assert.Null((await ReloadAsync(fixture)).WaitingSince);

        fixture.Riot.FailOrder(onTheWay.GateUpperId);
        DateTimeOffset failed = left + TimeSpan.FromMinutes(12) + TimeSpan.FromSeconds(2);
        await RoundAtAsync(fixture, failed);
        JourneyRuntimeRow named = await ReloadAsync(fixture);
        Assert.Equal(JourneyRuntimeStage.AwaitingGateArrival, named.Stage);
        Assert.NotNull(named.BlockReasonCode);
        Assert.Equal(named.BlockReasonSince, named.WaitingSince);
        Assert.Equal(failed, named.WaitingSince);
        Assert.Empty(WatchEntries(fixture));

        await RoundAtAsync(fixture, failed + fixture.Options.WaitingJourneyWarningAfter - TimeSpan.FromSeconds(1));
        Assert.Empty(WatchEntries(fixture));

        await RoundAtAsync(fixture, failed + fixture.Options.WaitingJourneyWarningAfter);
        (_, string message) = Assert.Single(WatchEntries(fixture));
        Assert.Contains(named.BlockReasonCode, message, StringComparison.Ordinal);
        Assert.Contains("for 10 min", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 探针的形状（#320 增量审查中项 Q1，审查者在克隆里复现过）：出发 1 秒后车载会话进 RecoveryRequired——真车载端挂着本服务端的
    /// 在途单时按设计就是这样（cs#314、#316）——引擎在路上写 <c>ONBOARD_SESSION_NOT_READY</c>；正常开 12 分钟，会话恢复 Ready、到站。
    /// 修前：第 10 分钟误报「路上已等 10 分」，到站后等人起点还沿用出发时刻。修后：路上一条不报，到站从到站那一刻计时，满门槛才报。
    /// 先断言引擎真的写了那个码——判据经过的是引擎自己那一笔写入，不是手写的原因码。
    /// </summary>
    [Fact]
    public async Task ADriveWithTheSessionNotReadyIsNotAWaitAndTheStopWaitsFromArrival()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(DemandId, "SUBLOT-001", Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        JourneyRuntimeRow onTheWay = await fixture.AdvanceToGateArrivalAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingGateArrival, onTheWay.Stage);
        DateTimeOffset left = fixture.Clock.GetUtcNow();

        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        SessionRecoveryRow session = await fixture.Context.SessionRecoveries.SingleAsync(TestContext.Current.CancellationToken);
        session.Readiness = SessionReadiness.RecoveryRequired;
        session.ReasonCode = "DEPARTURE_SAFETY_NOT_READY";
        session.DepartureSafe = false;
        session.SafetyReasonCodesJson = """["VEHICLE_NOT_READY"]""";
        session.SafetyUnknownPresent = true;
        session.UpdatedAt = fixture.Clock.GetUtcNow();
        await fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        await RoundAtAsync(fixture, left + TimeSpan.FromSeconds(2));
        JourneyRuntimeRow gated = await ReloadAsync(fixture);
        Assert.Equal(JourneyRuntimeStage.AwaitingGateArrival, gated.Stage);
        Assert.Equal(JourneyWaitClassification.SessionNotReadyReason, gated.BlockReasonCode);
        Assert.Null(gated.WaitingSince);

        await RoundAtAsync(fixture, left + TimeSpan.FromMinutes(10));
        await RoundAtAsync(fixture, left + TimeSpan.FromMinutes(12));
        Assert.Empty(WatchEntries(fixture));

        await fixture.RestoreSessionReadyAsync();
        fixture.Riot.SetSuccessfulArrival("TO_GATE", TaskTypeStationRuntimeSeed.GateStationRiotId);
        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { CurrentStationId = TaskTypeStationRuntimeSeed.GateStationRiotId };
        DateTimeOffset arrived = left + TimeSpan.FromMinutes(12) + TimeSpan.FromSeconds(2);
        await RoundAtAsync(fixture, arrived);
        JourneyRuntimeRow atTheGate = await ReloadAsync(fixture);
        Assert.Equal(JourneyRuntimeStage.AwaitingUnloadResult, atTheGate.Stage);
        Assert.Equal(arrived, atTheGate.WaitingSince);
        Assert.Null(atTheGate.WaitingWarnedAt);

        await RoundAtAsync(fixture, arrived + fixture.Options.WaitingJourneyWarningAfter - TimeSpan.FromSeconds(1));
        Assert.Empty(WatchEntries(fixture));
        await RoundAtAsync(fixture, arrived + fixture.Options.WaitingJourneyWarningAfter);
        (_, string message) = Assert.Single(WatchEntries(fixture));
        Assert.Contains("AwaitingUnloadResult", message, StringComparison.Ordinal);
        Assert.Contains("for 10 min", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 出发那一次保存里派车没有确认（这里是 RIoT 的建单应答丢了，引擎把 <c>ResultUnknown</c> 写成原因码）：调度定为「继续等」
    /// ——车多半没开走——所以等人起点沿用站内的那一个，不清零（#320 增量审查低项 L-a）。
    /// </summary>
    [Fact]
    public async Task ADepartureWhoseOrderIsNotConfirmedKeepsWaitingFromTheStation()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(DemandId, "SUBLOT-001", Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        JourneyRuntimeRow checking = await fixture.AdvanceToDepartureSafetyAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingDepartureSafety, checking.Stage);
        DateTimeOffset? atTheStation = checking.WaitingSince;
        Assert.NotNull(atTheStation);

        fixture.Clock.Advance(TimeSpan.FromSeconds(30));
        fixture.Riot.LoseNextCreateResponse = true;
        await fixture.AddInboxAsync(
            Guid.NewGuid().ToString("D"),
            "PreDepartureSafetyCheckResult",
            new
            {
                preDepartureSafetyCheckId = checking.PreDepartureSafetyCheckId,
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
            checking.PreDepartureSafetyCheckMessageId);
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        JourneyRuntimeRow unconfirmed = await ReloadAsync(fixture);
        Assert.Equal(JourneyRuntimeStage.AwaitingGateArrival, unconfirmed.Stage);
        Assert.Equal(nameof(MovementDispatchOutcome.ResultUnknown), unconfirmed.BlockReasonCode);
        Assert.Equal(atTheStation, unconfirmed.WaitingSince);
    }

    // ---- 等人阶段之间切换不归零，离开等人才清零（#320 审查低项 4、6） --------------------------------------------------

    /// <summary>
    /// 闸口等了 12 分钟之后卸货结果回来是需恢复、旅程转成阻断：车没动过、电一直在掉，所以计时不归零。第一条在 10 分钟（闸口等卸货），
    /// 第二条满一个重复间隔、在 15 分钟（此时已是阻断），写的是「15 min」。归零的实现会让第二条推迟到阻断后 10 分钟。
    /// </summary>
    [Fact]
    public async Task MovingFromOneWaitingStageToAnotherDoesNotStartTheWaitOver()
    {
        await using RuntimeFixture fixture = await GateUnloadAsync();
        DateTimeOffset arrived = fixture.Clock.GetUtcNow();

        await RoundAtAsync(fixture, arrived + TimeSpan.FromMinutes(10));
        Assert.Contains("AwaitingUnloadResult", Assert.Single(WatchEntries(fixture)).Message, StringComparison.Ordinal);

        fixture.Clock.Advance(TimeSpan.FromMinutes(2));
        StationOperationRow unload = await fixture.OperationAsync(SlotOperationType.Unload);
        await fixture.ApplyTimedOutResultAsync(unload, SlotOperationType.Unload);
        await RoundAtAsync(fixture, arrived + TimeSpan.FromMinutes(12));
        JourneyRuntimeRow blocked = await ReloadAsync(fixture);
        Assert.Equal(JourneyRuntimeStage.Blocked, blocked.Stage);
        Assert.Equal(arrived, blocked.WaitingSince);

        await RoundAtAsync(fixture, arrived + TimeSpan.FromMinutes(15));
        (_, string second) = WatchEntries(fixture)[^1];
        Assert.Equal(2, WatchEntries(fixture).Length);
        Assert.Contains("Blocked", second, StringComparison.Ordinal);
        Assert.Contains("for 15 min", second, StringComparison.Ordinal);
    }

    /// <summary>
    /// 离开等人（出发）清掉等人起点与上一次告警时刻；到下一站重新等人，就是一段新的等待，从它自己的门槛报。重复间隔故意设得比门槛长
    /// （30 分钟对 10 分钟）：上一段的告警时刻若没清掉，新一段到门槛时「距上次告警」还不满间隔，就会一条不报。
    /// 这是审查低项 6 的二选一：不禁止这种配置，删掉「告警早于本段起点」那一支，改由离开等人时把告警时刻一起清掉，
    /// 让新一段在构造上就是没告警过的。
    /// </summary>
    [Fact]
    public async Task DepartingEndsTheWaitAndTheNextStopWaitsFromItsOwnThreshold()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Options.WaitingJourneyWarningRepeat = TimeSpan.FromMinutes(30);
        fixture.Catalog.Set(fixture.Demand(DemandId, "SUBLOT-001", Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        JourneyRuntimeRow loading = await fixture.AdvanceToLoadResultAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingLoadResult, loading.Stage);
        DateTimeOffset atThePickup = fixture.Clock.GetUtcNow();

        await RoundAtAsync(fixture, atThePickup + TimeSpan.FromMinutes(10));
        Assert.Contains("AwaitingLoadResult", Assert.Single(WatchEntries(fixture)).Message, StringComparison.Ordinal);

        StationOperationRow load = await fixture.OperationAsync(SlotOperationType.Load);
        await fixture.ApplySafeResultAsync(load, SlotOperationType.Load, SlotBusinessState.Occupied);
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        JourneyRuntimeRow checking = await ReloadAsync(fixture);
        Assert.Equal(JourneyRuntimeStage.AwaitingDepartureSafety, checking.Stage);
        await fixture.AddInboxAsync(
            Guid.NewGuid().ToString("D"),
            "PreDepartureSafetyCheckResult",
            new
            {
                preDepartureSafetyCheckId = checking.PreDepartureSafetyCheckId,
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
            checking.PreDepartureSafetyCheckMessageId);
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        JourneyRuntimeRow departed = await ReloadAsync(fixture);
        Assert.Equal(JourneyRuntimeStage.AwaitingGateArrival, departed.Stage);
        Assert.Null(departed.WaitingSince);
        Assert.Null(departed.WaitingWarnedAt);

        fixture.Riot.SetSuccessfulArrival("TO_GATE", TaskTypeStationRuntimeSeed.GateStationRiotId);
        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { CurrentStationId = TaskTypeStationRuntimeSeed.GateStationRiotId };
        await RoundAtAsync(fixture, atThePickup + TimeSpan.FromMinutes(11));
        JourneyRuntimeRow atTheGate = await ReloadAsync(fixture);
        Assert.Equal(JourneyRuntimeStage.AwaitingUnloadResult, atTheGate.Stage);
        DateTimeOffset arrived = atThePickup + TimeSpan.FromMinutes(11);
        Assert.Equal(arrived, atTheGate.WaitingSince);

        await RoundAtAsync(fixture, arrived + TimeSpan.FromMinutes(10) - TimeSpan.FromSeconds(1));
        Assert.Single(WatchEntries(fixture));
        await RoundAtAsync(fixture, arrived + TimeSpan.FromMinutes(10));
        Assert.Equal(2, WatchEntries(fixture).Length);
        Assert.Contains("AwaitingUnloadResult", WatchEntries(fixture)[1].Message, StringComparison.Ordinal);
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

        // The session gate: on a leg it is not a wait, at a stop it changes nothing (incremental review of #320, Q1 a).
        Assert.False(JourneyWaitClassification.IsWaiting(JourneyRuntimeStage.AwaitingGateArrival, "ONBOARD_SESSION_NOT_READY"));
        Assert.False(JourneyWaitClassification.IsWaiting(JourneyRuntimeStage.AwaitingPickupArrival, "ONBOARD_SESSION_NOT_READY"));
        Assert.True(JourneyWaitClassification.IsWaiting(JourneyRuntimeStage.AwaitingGateArrival, "VEHICLE_ORDER_FAILED"));
        Assert.True(JourneyWaitClassification.IsWaiting(JourneyRuntimeStage.AwaitingGateArrival, "ONBOARD_SESSION_LOST"));
        Assert.True(JourneyWaitClassification.IsWaiting(JourneyRuntimeStage.AwaitingUnloadResult, "ONBOARD_SESSION_NOT_READY"));
        Assert.False(JourneyWaitClassification.IsWaiting(JourneyRuntimeStage.AwaitingGateArrival, null));
    }

    // ---- 等人起点由上下文维护 ----------------------------------------------------------------------------------------

    /// <summary>
    /// <c>WaitingSince</c> 由保存维护（#320 审查中项 1、低项 4）。逐步：新增一行闸口等卸货，起点是它的 <c>UpdatedAt</c>；只动别的列，不动；
    /// 转成阻断、再换一个原因码，都不动——车没挪过；出发（去下一站、没有原因码），起点与告警时刻一起清掉；路上报出原因，起点是
    /// <b>原因的起点</b>而不是这次写行的时刻；原因换了，不动；原因清掉（车又动了），清掉；完成，仍为空。
    /// </summary>
    [Fact]
    public async Task TheWaitStartIsKeptAcrossWaitingStagesAndClearedOnlyWhenTheJourneyStopsWaiting()
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
            add.JourneyRuntimes.Add(Runtime("D-WAIT", JourneyRuntimeStage.AwaitingUnloadResult, t0));
            await add.SaveChangesAsync(cancellationToken);
        }
        Assert.Equal(t0, (await ReadAsync(options)).WaitingSince);

        await EditAsync(options, row => { row.WorklistRevision++; row.WaitingWarnedAt = t0.AddMinutes(2); row.UpdatedAt = t0.AddMinutes(3); });
        Assert.Equal(t0, (await ReadAsync(options)).WaitingSince);

        await EditAsync(options, row =>
        {
            row.Stage = JourneyRuntimeStage.Blocked;
            row.SetBlockReason("UNLOAD_RESULT_REQUIRES_RECOVERY", t0.AddMinutes(7));
            row.UpdatedAt = t0.AddMinutes(7);
        });
        Assert.Equal(t0, (await ReadAsync(options)).WaitingSince);

        await EditAsync(options, row =>
        {
            row.SetBlockReason("ONBOARD_SESSION_LOST", t0.AddMinutes(9));
            row.UpdatedAt = t0.AddMinutes(9);
        });
        JourneyRuntimeRow stillWaiting = await ReadAsync(options);
        Assert.Equal(t0, stillWaiting.WaitingSince);
        Assert.Equal(t0.AddMinutes(2), stillWaiting.WaitingWarnedAt);

        await EditAsync(options, row =>
        {
            row.Stage = JourneyRuntimeStage.AwaitingGateArrival;
            row.SetBlockReason(null, t0.AddMinutes(11));
            row.UpdatedAt = t0.AddMinutes(11);
        });
        JourneyRuntimeRow departed = await ReadAsync(options);
        Assert.Null(departed.WaitingSince);
        Assert.Null(departed.WaitingWarnedAt);

        await EditAsync(options, row =>
        {
            row.SetBlockReason("VEHICLE_WAITING_AT_CHECKPOINT", t0.AddMinutes(12));
            row.UpdatedAt = t0.AddMinutes(13);
        });
        Assert.Equal(t0.AddMinutes(12), (await ReadAsync(options)).WaitingSince);

        await EditAsync(options, row =>
        {
            row.SetBlockReason("VEHICLE_CHECKPOINT_WAIT_EXCEEDED", t0.AddMinutes(18));
            row.UpdatedAt = t0.AddMinutes(18);
        });
        Assert.Equal(t0.AddMinutes(12), (await ReadAsync(options)).WaitingSince);

        await EditAsync(options, row => { row.SetBlockReason(null, t0.AddMinutes(19)); row.UpdatedAt = t0.AddMinutes(19); });
        Assert.Null((await ReadAsync(options)).WaitingSince);

        await EditAsync(options, row => { row.Stage = JourneyRuntimeStage.Completed; row.UpdatedAt = t0.AddMinutes(30); });
        Assert.Null((await ReadAsync(options)).WaitingSince);
    }

    /// <summary>
    /// 到站重新计时（#320 增量审查 Q1 b）：路上真停过（检查点等待，算等人）、之后到站，新的一段等待从到站那一刻算，告警时刻一并清掉——
    /// 车动过了。阻断例外：路上被阻断是原地阻断，起点沿用。
    /// </summary>
    [Fact]
    public async Task ArrivingStartsANewWaitButABlockOnTheLegKeepsItsStart()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        DbContextOptions<ControlServerDbContext> options =
            new DbContextOptionsBuilder<ControlServerDbContext>().UseSqlite(connection).Options;
        await using (ControlServerDbContext create = new(options))
        {
            await create.Database.EnsureCreatedAsync(cancellationToken);
            JourneyRuntimeRow onTheLeg = Runtime("D-ARRIVE", JourneyRuntimeStage.AwaitingGateArrival, T0);
            onTheLeg.SetBlockReason("VEHICLE_WAITING_AT_CHECKPOINT", T0);
            create.JourneyRuntimes.Add(onTheLeg);
            JourneyRuntimeRow blockedOnTheLeg = Runtime("D-BLOCK-ON-LEG", JourneyRuntimeStage.AwaitingGateArrival, T0);
            blockedOnTheLeg.SetBlockReason("VEHICLE_ORDER_FAILED", T0);
            create.JourneyRuntimes.Add(blockedOnTheLeg);
            await create.SaveChangesAsync(cancellationToken);
        }

        await EditAsync(options, "D-ARRIVE", row => { row.WaitingWarnedAt = T0.AddMinutes(10); row.UpdatedAt = T0.AddMinutes(10); });
        await EditAsync(options, "D-ARRIVE", row =>
        {
            row.Stage = JourneyRuntimeStage.AwaitingUnloadResult;
            row.SetBlockReason(null, T0.AddMinutes(14));
            row.UpdatedAt = T0.AddMinutes(14);
        });
        JourneyRuntimeRow arrived = await ReadAsync(options, "D-ARRIVE");
        Assert.Equal(T0.AddMinutes(14), arrived.WaitingSince);
        Assert.Null(arrived.WaitingWarnedAt);

        await EditAsync(options, "D-BLOCK-ON-LEG", row =>
        {
            row.Stage = JourneyRuntimeStage.Blocked;
            row.SetBlockReason("LOAD_RESULT_REQUIRES_RECOVERY", T0.AddMinutes(20));
            row.UpdatedAt = T0.AddMinutes(20);
        });
        Assert.Equal(T0, (await ReadAsync(options, "D-BLOCK-ON-LEG")).WaitingSince);
    }

    private static readonly DateTimeOffset T0 = new(2026, 9, 22, 1, 0, 0, TimeSpan.Zero);

    // ---- 配置 ------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheDefaultsAreTheOnesTheUserDecided()
    {
        JourneyRuntimeOptions defaults = new();
        Assert.Equal(TimeSpan.FromMinutes(10), defaults.WaitingJourneyWarningAfter);
        Assert.Equal(TimeSpan.FromMinutes(5), defaults.WaitingJourneyWarningRepeat);
        Assert.Equal(30, defaults.MinimumBatteryPercent);
        Assert.Equal(15, defaults.WaitingJourneyRescueBatteryPercent);
        Assert.Equal(TimeSpan.FromSeconds(2), defaults.WaitingJourneyBatteryReadBudget);
    }

    [Theory]
    [InlineData("WaitingJourneyWarningAfter", "00:00:00", "WaitingJourneyWarningAfter must be positive.")]
    [InlineData("WaitingJourneyWarningRepeat", "00:00:00", "WaitingJourneyWarningRepeat must be positive.")]
    [InlineData("WaitingJourneyRescueBatteryPercent", "0", "WaitingJourneyRescueBatteryPercent must be at least 1 and below MinimumBatteryPercent.")]
    [InlineData("WaitingJourneyRescueBatteryPercent", "40", "WaitingJourneyRescueBatteryPercent must be at least 1 and below MinimumBatteryPercent.")]
    [InlineData("WaitingJourneyBatteryReadBudget", "00:00:00", "WaitingJourneyBatteryReadBudget must be positive and at most 10 s.")]
    [InlineData("WaitingJourneyBatteryReadBudget", "00:00:11", "WaitingJourneyBatteryReadBudget must be positive and at most 10 s.")]
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
        DateTimeOffset arrived = fixture.Clock.GetUtcNow();
        StationOperationRow unload = await fixture.OperationAsync(SlotOperationType.Unload);
        await fixture.ApplyTimedOutResultAsync(unload, SlotOperationType.Unload);
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        JourneyRuntimeRow runtime = await ReloadAsync(fixture);
        Assert.Equal(JourneyRuntimeStage.Blocked, runtime.Stage);
        Assert.Equal("UNLOAD_RESULT_REQUIRES_RECOVERY", runtime.BlockReasonCode);
        Assert.Equal(arrived, runtime.WaitingSince);
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

    private static async Task<JourneyRuntimeRow> ReadAsync(DbContextOptions<ControlServerDbContext> options)
    {
        await using ControlServerDbContext read = new(options);
        return await read.JourneyRuntimes.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
    }

    private static async Task EditAsync(
        DbContextOptions<ControlServerDbContext> options, string demandId, Action<JourneyRuntimeRow> edit)
    {
        await using ControlServerDbContext write = new(options);
        JourneyRuntimeRow row = await write.JourneyRuntimes.SingleAsync(
            item => item.DemandId == demandId, TestContext.Current.CancellationToken);
        edit(row);
        await write.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<JourneyRuntimeRow> ReadAsync(DbContextOptions<ControlServerDbContext> options, string demandId)
    {
        await using ControlServerDbContext read = new(options);
        return await read.JourneyRuntimes.AsNoTracking().SingleAsync(
            item => item.DemandId == demandId, TestContext.Current.CancellationToken);
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
