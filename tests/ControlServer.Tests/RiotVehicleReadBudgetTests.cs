using System.Diagnostics;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using static ControlServer.Tests.Batch7StopDrivenAdvanceDriver;
using static ControlServer.Tests.JourneyRuntimeWorkerLoadCancellationBeforeSublotTests;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;
using static ControlServer.Tests.MultiVehicleExecutionTests;

namespace ControlServer.Tests;

/// <summary>
/// control-server#799：推进轮里读车的预算（<c>JourneyRuntime:RiotVehicleReadBudget</c>，默认 4 秒）。RIoT 读车挂住时，每一处读在预算到点就放弃、
/// 走它那一处原有的「读不到」分支；一轮多出来的时间以「预算 × 被截断的处数」为限，轮末的急停监看与 REQ-0248 重新急停不再被一个网关超时拖住。
/// </summary>
/// <remarks>
/// <para>
/// 预算的计时器是真实的（<c>CancellationTokenSource(budget, timeProvider)</c>，夹具的钟只覆盖 <c>GetUtcNow</c>），所以这里的耗时都用
/// <see cref="Stopwatch"/> 量，挂起用 <see cref="Task.Delay(TimeSpan, CancellationToken)"/>（记忆 budget-tests-real-timers）。
/// </para>
/// <para>
/// 逐处「超时走读不到的分支」的断言放在各处原有的「读失败」用例旁边，多加一格「读挂住」，断言与读失败那一格相同：
/// <c>ChargingApproachSupervisionTests</c>（去桩途中失联）、<c>ChargingCycleProgressTests</c>（离桩三项确认）、
/// <c>ChargingInterruptionTests</c>（充电读数缺口）、<c>ChargingExecutionTests</c>（桩占用读不到）、
/// <c>OwnOrderRebuildCargoProofTests</c>（重建出发前检查）、<c>Batch8StationExclusivityManualReleaseTests</c>（离点清扫）。
/// 这里放整轮、急停与到站核验。
/// </para>
/// </remarks>
public sealed class RiotVehicleReadBudgetTests(ITestOutputHelper output)
{
    /// <summary>逐处用例里给的读车预算：短到一轮里几处读叠起来也只有一两秒。</summary>
    internal static readonly TimeSpan ShortReadBudget = TimeSpan.FromMilliseconds(200);

    /// <summary>挂住的读车最长等多久：远长于任何预算，也长于每车派车预算（30 秒），短于测试本身的超时。</summary>
    private static readonly TimeSpan Hang = TimeSpan.FromSeconds(60);

    /// <summary>
    /// 一次被截断的读可以比预算晚多久回来。CI 上真装置与合成 L2 同机并发时，实测过一个 200 毫秒的计时器 5 秒后才触发（PR #903 的 CI），所以给得宽；
    /// 没有预算的读要等满挂起时长或每车派车预算（30 秒），远在这之外。
    /// </summary>
    private static readonly TimeSpan CutLateness = TimeSpan.FromSeconds(10);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// 多车夹具里让一辆车的读车挂住（<see cref="FleetRiot.HangOn"/>，挂满 60 秒判失败），读车预算设成 <see cref="ShortReadBudget"/>。
    /// 引擎与夹具共用同一个选项实例，改了立即生效。
    /// </summary>
    internal static void HangVehicleReads(FleetFixture fleet, string vehicleKey)
    {
        fleet.Options.RiotVehicleReadBudget = ShortReadBudget;
        fleet.Riot.HangOn = vehicleKey;
    }

    /// <summary>单车夹具里让读车挂住（最长 60 秒），读车预算设成 <see cref="ShortReadBudget"/>。放开用 <c>Riot.ReadVehicleDelay = null</c>。</summary>
    internal static void HangVehicleReads(RuntimeFixture fixture)
    {
        fixture.Options.RiotVehicleReadBudget = ShortReadBudget;
        fixture.Riot.ReadVehicleDelay = token => Task.Delay(TimeSpan.FromSeconds(60), token);
    }

    // ---- 帮助方法本身 ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AReadThatAnswersWithinTheBudgetIsReturned()
    {
        StubFacts facts = new(_ => Task.FromResult(Observed("KEY-1")));

        RiotVehicleObservation? read = await facts.ReadVehicleWithinAsync(
            "KEY-1", TimeSpan.FromSeconds(2), TimeProvider.System, Token);

        Assert.Equal("KEY-1", read!.VehicleKey);
        Assert.True(read.Connected);
    }

    [Fact]
    public async Task AReadPastTheBudgetIsNullAtTheBudgetAndItsTokenIsCancelled()
    {
        CancellationToken seen = default;
        StubFacts facts = new(async token =>
        {
            seen = token;
            await Task.Delay(Hang, token);
            return Observed("KEY-1");
        });

        Stopwatch elapsed = Stopwatch.StartNew();
        RiotVehicleObservation? read = await facts.ReadVehicleWithinAsync(
            "KEY-1", TimeSpan.FromMilliseconds(200), TimeProvider.System, Token);
        elapsed.Stop();

        Assert.Null(read);
        Assert.True(seen.IsCancellationRequested, "The read was not told to stop.");
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(5), $"Waited {elapsed.Elapsed} for a 200 ms budget.");
    }

    /// <summary>读本身不理会取消（SDK 或替身）：照样在预算到点不再等它。</summary>
    [Fact]
    public async Task AReadThatIgnoresItsTokenIsStillGivenUpOnAtTheBudget()
    {
        StubFacts facts = new(async _ =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None);
            return Observed("KEY-1");
        });

        Stopwatch elapsed = Stopwatch.StartNew();
        RiotVehicleObservation? read = await facts.ReadVehicleWithinAsync(
            "KEY-1", TimeSpan.FromMilliseconds(200), TimeProvider.System, Token);
        elapsed.Stop();

        Assert.Null(read);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(3), $"Waited {elapsed.Elapsed} for a read that ignored its token.");
    }

    /// <summary>宿主停机不是超时：取消照样抛出去，不变成「读不到」。</summary>
    [Fact]
    public async Task TheHostsShutdownIsNotATimeout()
    {
        using CancellationTokenSource shutdown = new();
        StubFacts facts = new(async token =>
        {
            await shutdown.CancelAsync();
            await Task.Delay(Hang, token);
            return Observed("KEY-1");
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => facts.ReadVehicleWithinAsync(
            "KEY-1", TimeSpan.FromSeconds(10), TimeProvider.System, shutdown.Token));
    }

    /// <summary>别的失败原样抛出，交给调用处原有的 catch；读自己的取消（不是预算、不是停机）也一样。</summary>
    [Fact]
    public async Task AnyOtherFailureReachesTheCallersOwnCatchUnchanged()
    {
        StubFacts failing = new(_ => throw new HttpRequestException("RIoT is not answering."));
        StubFacts ownDeadline = new(_ => throw new OperationCanceledException(
            "A deadline of the read's own.", new CancellationToken(true)));

        await Assert.ThrowsAsync<HttpRequestException>(() => failing.ReadVehicleWithinAsync(
            "KEY-1", TimeSpan.FromSeconds(2), TimeProvider.System, Token));
        await Assert.ThrowsAsync<OperationCanceledException>(() => ownDeadline.ReadVehicleWithinAsync(
            "KEY-1", TimeSpan.FromSeconds(2), TimeProvider.System, Token));
    }

    /// <summary>
    /// 读不到时补的那一份读数与产品网关读失败时答的逐字段相同（<c>HttpRiotMovementGateway.UnknownVehicle</c>）：需求释放就是按这几个字段认「没读到」的。
    /// </summary>
    [Fact]
    public void TheUnreadVehicleIsTheGatewaysShapeForAFailedRead()
    {
        DateTimeOffset at = Now;
        RiotVehicleObservation unread = RiotVehicleReadBudget.Unread("KEY-1", at);

        Assert.Equal(
            new RiotVehicleObservation(
                "KEY-1", Connected: false, Enabled: false, ProcState: "UNKNOWN", CurrentMap: string.Empty,
                CurrentStationId: null, BatteryPercent: null, BatteryState: null, Speed: null, ObservedAt: at,
                LockStatus: null, OrderTaskId: null),
            unread);
    }

    // ---- 配置项 ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// 默认 4 秒（PR #903 的 CI 之后由 2 秒调上来：合成 L2 <c>route-graph-staleness</c> 让替身 RIoT 每次调用都慢 2000 毫秒，2 秒的预算把每次读车都截断了），
    /// 短于车载端会话超时（6 秒）。0、负数与不短于会话超时的值启动时被拒。
    /// </summary>
    [Theory]
    [InlineData("00:00:00", false)]
    [InlineData("-00:00:01", false)]
    [InlineData("00:00:00.200", true)]
    [InlineData("00:00:05.999", true)]
    [InlineData("00:00:06", false)]
    public async Task TheBudgetDefaultsToFourSecondsAndStaysBelowTheSessionTimeout(string value, bool valid)
    {
        JourneyRuntimeOptions defaults = new();
        Assert.Equal(TimeSpan.FromSeconds(4), defaults.RiotVehicleReadBudget);
        Assert.True(defaults.RiotVehicleReadBudget < SessionLiveness.Timeout);

        // 校验器只在 Enabled 时逐项查，所以用夹具那份已启用的选项（同 SlotCommandStationRecheckTests 的做法）。
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        JourneyRuntimeOptions options = fixture.Options;
        options.RiotVehicleReadBudget = TimeSpan.Parse(value, System.Globalization.CultureInfo.InvariantCulture);

        Microsoft.Extensions.Options.ValidateOptionsResult result = new JourneyRuntimeOptionsValidator(
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()).Validate(null, options);

        Assert.Equal(
            valid,
            !(result.Failures ?? []).Contains(
                "RiotVehicleReadBudget must be positive and below the onboard session timeout (6 s)."));
    }

    // ---- 一轮的耗时 -----------------------------------------------------------------------------------------------------

    /// <summary>
    /// 票面复现（cs#783 审查 S-4 实测 22 秒）：等录入阶段，RIoT 读车挂住。修之前派车轮的在途计划读只受每车派车预算（30 秒）截断，挂 20 秒时一轮
    /// 22 秒。现在这一轮里每一次读车都在默认预算（4 秒）附近被截断，没有一次等满挂起时长或派车预算。
    /// </summary>
    /// <remarks>
    /// 判的是每一次读等了多久，不判整轮多长：整轮里读车以外的工作在 CI 负载下会被拖慢，与预算无关（PR #903 第一次 CI 红在这里）。
    /// </remarks>
    [Fact]
    public async Task AtTheSublotWaitEveryHangingVehicleReadIsCutOffAtItsBudget()
    {
        await using RuntimeFixture fixture = await ReachSublotWaitAsync();

        (TimeSpan elapsed, TimeSpan[] cut) = await TimedRoundWithHangingReadsAsync(fixture);

        AssertEveryReadCutOffAtABudget(elapsed, cut, fixture.Options);
    }

    /// <summary>
    /// 票面「两车各有一处读挂住，第二辆车的急停确认不被拖满」的单车版：开着的急停在轮末监看（REQ-0248），RIoT 上有人把它解开，
    /// 这一轮照样重新急停。读车挂住 20 秒时，修之前这一轮要先等完到站核验那一次读与派车轮那一次读，重新急停推迟四十秒；现在这一轮里每一次读车
    /// 都在预算附近被截断，重新急停推迟的是被截断读数之和（每次一个预算）。
    /// </summary>
    /// <remarks>
    /// 轮末监看对每一辆车都排在同一轮里所有车的推进与派车之后（<c>JourneyRuntimeEngine.ExecuteOnceAsync</c> 的 finally），所以一辆车的读挂住推迟的
    /// 是每一辆车的监看；单车夹具量的就是这一段。
    /// </remarks>
    [Fact]
    [Trait("Requirement", "REQ-0248")]
    public async Task AnOpenStopReleasedInRiotIsReTriggeredWithEveryHangingVehicleReadCutOffAtItsBudget()
    {
        await using RuntimeFixture fixture = await InTransitDoorEmergencyReleaseTests.LatchedForTheDoorsAsync();
        string upperId = (await fixture.RuntimeAsync()).GateUpperId!;
        fixture.Riot.SetOrderState(upperId, RiotOrderState.Success, terminal: true);
        fixture.UnfinishedOrderIds = [];
        await DriveOneRoundAsync(fixture);
        Assert.Equal(1, await CountAsync(fixture, RiotCommandTypeNames.TriggerEmergency));

        fixture.EmergencyLatched = false;
        (TimeSpan elapsed, TimeSpan[] cut) = await TimedRoundWithHangingReadsAsync(fixture, hear: true);

        Assert.Equal(2, await CountAsync(fixture, RiotCommandTypeNames.TriggerEmergency));
        AssertEveryReadCutOffAtABudget(elapsed, cut, fixture.Options);
    }

    // ---- 到站核验 -------------------------------------------------------------------------------------------------------

    /// <summary>
    /// 到站核验（<c>CheckArrivalAsync</c>）：单已 SUCCESS、车真停在取货站，但 RIoT 读车挂住。预算到点按读不到处理——车辆那一半证明不了，不判到站、
    /// 不进等录入；读数回来的下一轮照常判到。
    /// </summary>
    /// <remarks>去掉这一处的预算：这一读等满挂起时长后读到车在站上，这一轮就判到站，第一条断言红。</remarks>
    [Fact]
    public async Task AnArrivalWhoseVehicleReadHangsIsNotTakenUntilTheReadComesBack()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(FirstDemandId, FirstSublot, Now.AddMinutes(-10)));
        fixture.BoxCounts.Set(FirstSublot, 4);
        await TickAndRunAsync(fixture);
        JourneyStopRow stop = await CurrentStopAsync(fixture, FirstDemandId);
        fixture.Riot.SetSuccessfulArrival("TO_PICKUP", stop.UpperId, stop.StationRiotId);
        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { CurrentStationId = stop.StationRiotId };
        HangVehicleReads(fixture);

        Stopwatch elapsed = Stopwatch.StartNew();
        await TickAndRunAsync(fixture);
        elapsed.Stop();

        Assert.NotEqual(JourneyRuntimeStage.AwaitingSublot, (await JourneyOfAsync(fixture, FirstDemandId)).Stage);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(30), $"The round took {elapsed.Elapsed}, as long as a read that hangs.");

        fixture.Riot.ReadVehicleDelay = null;
        await TickAndRunAsync(fixture);
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, (await JourneyOfAsync(fixture, FirstDemandId)).Stage);
    }

    // ---- helpers --------------------------------------------------------------------------------------------------------

    /// <summary>读车挂住 <see cref="Hang"/> 跑一轮：这一轮的真实耗时，与每一次被截断的读等了多久。</summary>
    private static async Task<(TimeSpan Elapsed, TimeSpan[] Cut)> TimedRoundWithHangingReadsAsync(
        RuntimeFixture fixture, bool hear = false)
    {
        List<TimeSpan> cut = [];
        fixture.Riot.ReadVehicleDelay = async token =>
        {
            Stopwatch waited = Stopwatch.StartNew();
            try
            {
                await Task.Delay(Hang, token);
            }
            catch (OperationCanceledException)
            {
                lock (cut)
                {
                    cut.Add(waited.Elapsed);
                }
                throw;
            }
        };
        Stopwatch elapsed = Stopwatch.StartNew();
        if (hear)
        {
            await DriveOneRoundAsync(fixture);
        }
        else
        {
            await TickAndRunAsync(fixture);
        }
        elapsed.Stop();
        fixture.Riot.ReadVehicleDelay = null;
        lock (cut)
        {
            return (elapsed.Elapsed, [.. cut]);
        }
    }

    /// <summary>
    /// 至少一次读被截断；每一次都在这一轮某个读车预算附近被截断——不早于其中最短的，不晚于其中最长的加 <see cref="CutLateness"/>；整轮远短于挂起时长。
    /// 一轮里有三种读车预算：本票的 <c>RiotVehicleReadBudget</c>（4 秒），等待监看读电量的 <c>WaitingJourneyBatteryReadBudget</c> 与装卸前读位置的
    /// <c>SlotCommandPositionReadBudget</c>（各 2 秒）。没有预算的那一处要等满挂起时长或每车派车预算（30 秒），至少一条红。
    /// </summary>
    private void AssertEveryReadCutOffAtABudget(TimeSpan elapsed, TimeSpan[] cut, JourneyRuntimeOptions options)
    {
        TimeSpan[] budgets =
            [options.RiotVehicleReadBudget, options.WaitingJourneyBatteryReadBudget, options.SlotCommandPositionReadBudget];
        TimeSpan shortest = budgets.Min();
        TimeSpan longest = budgets.Max();
        output.WriteLine(
            $"round {elapsed.TotalSeconds:0.00} s; {cut.Length} vehicle reads cut off after " +
            $"{string.Join(", ", cut.Select(wait => wait.TotalSeconds.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)))} s " +
            $"(budgets {shortest.TotalSeconds:0.###}..{longest.TotalSeconds:0.###} s)");
        Assert.True(cut.Length >= 1, "No vehicle read hung, so this proves nothing.");
        Assert.All(cut, wait => Assert.True(
            wait >= shortest - TimeSpan.FromMilliseconds(100) && wait < longest + CutLateness,
            $"A read was cut off after {wait}, not at a budget ({shortest}..{longest})."));
        Assert.True(elapsed < Hang / 2, $"The round took {elapsed}, as long as a read that hangs.");
    }

    private static async Task DriveOneRoundAsync(RuntimeFixture fixture)
    {
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        await fixture.HearFromPeerAsync();
        fixture.Context.ChangeTracker.Clear();
        await fixture.Engine.ExecuteOnceAsync(Token);
        fixture.Context.ChangeTracker.Clear();
    }

    private static async Task<int> CountAsync(RuntimeFixture fixture, string commandType)
    {
        await using ControlServerDbContext reading = new(fixture.DbOptionsForTests);
        return await reading.RiotOrderCommandAudit.AsNoTracking().CountAsync(row => row.CommandType == commandType, Token);
    }

    private static RiotVehicleObservation Observed(string vehicleKey) => new(
        vehicleKey, Connected: true, Enabled: true, ProcState: "IDLE", CurrentMap: "MAP", CurrentStationId: 1,
        BatteryPercent: 80, BatteryState: "NO_CHARGE", Speed: 0, ObservedAt: Now, LockStatus: 0, OrderTaskId: null);

    private sealed class StubFacts(Func<CancellationToken, Task<RiotVehicleObservation>> read) : IRiotVehicleFacts
    {
        public Task<RiotVehicleObservation> ReadVehicleAsync(string vehicleKey, CancellationToken cancellationToken) =>
            read(cancellationToken);

        public Task<RiotOrderObservation> ReconcileByUpperIdAsync(string upperId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<RiotOrderObservation> CreateAsync(OrderIntent intent, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
