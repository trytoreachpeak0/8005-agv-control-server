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
/// control-server#799：推进轮里读车的预算（<c>JourneyRuntime:RiotVehicleReadBudget</c>，默认 2 秒）。RIoT 读车挂住时，每一处读在预算到点就放弃、
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

    /// <summary>挂住的读车最长等多久：远长于任何预算，短于测试本身的超时。</summary>
    private static readonly TimeSpan Hang = TimeSpan.FromSeconds(20);

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
    /// 默认 2 秒：短于车载端会话超时（6 秒），也短于行驶中门锁读不到的默认宽限（3 秒，CP-0017）——后者只是两个默认值的关系，校验不保证。
    /// 0、负数与不短于会话超时的值启动时被拒。
    /// </summary>
    [Theory]
    [InlineData("00:00:00", false)]
    [InlineData("-00:00:01", false)]
    [InlineData("00:00:00.200", true)]
    [InlineData("00:00:05.999", true)]
    [InlineData("00:00:06", false)]
    public async Task TheBudgetDefaultsToTwoSecondsAndStaysBelowTheSessionTimeout(string value, bool valid)
    {
        JourneyRuntimeOptions defaults = new();
        Assert.Equal(TimeSpan.FromSeconds(2), defaults.RiotVehicleReadBudget);
        Assert.True(defaults.RiotVehicleReadBudget < SessionLiveness.Timeout);
        Assert.True(defaults.RiotVehicleReadBudget < new Host.Runtime.Faults.VehicleFaultOptions().DoorUnknownGrace);

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
    /// 票面复现（cs#783 审查 S-4 实测 22 秒）：等录入阶段，RIoT 读车挂住 20 秒。修之前派车轮的在途计划读只受每车派车预算（30 秒）截断，
    /// 等满 20 秒，加上监看读电量的 2 秒，一轮 22 秒。现在每一处读都在默认预算（2 秒）到点放弃：一轮多出的时间不超过「预算 × 被截断的处数」。
    /// </summary>
    [Fact]
    public async Task AtTheSublotWaitARoundOnAHangingRiotCostsAtMostTheBudgetPerRead()
    {
        await using RuntimeFixture fixture = await ReachSublotWaitAsync();
        TimeSpan budget = fixture.Options.RiotVehicleReadBudget;

        (TimeSpan elapsed, int cut) = await TimedRoundWithHangingReadsAsync(fixture);

        output.WriteLine($"round {elapsed.TotalSeconds:0.00} s, {cut} vehicle reads cut off at {budget.TotalSeconds:0.###} s");
        Assert.True(cut >= 1, "No vehicle read hung, so this proves nothing.");
        Assert.True(
            elapsed < budget * cut + TimeSpan.FromSeconds(3),
            $"The round took {elapsed} with {cut} reads cut off at {budget}; a read waited past its budget.");
    }

    /// <summary>
    /// 票面「两车各有一处读挂住，第二辆车的急停确认不被拖满」的单车版：开着的急停在轮末监看（REQ-0248），RIoT 上有人把它解开，
    /// 这一轮照样重新急停。读车挂住 20 秒时，修之前这一轮要先等完到站核验那一次读与派车轮那一次读，重新急停推迟四十秒；现在推迟不超过
    /// 「预算 × 被截断的处数」。
    /// </summary>
    /// <remarks>
    /// 轮末监看对每一辆车都排在同一轮里所有车的推进与派车之后（<c>JourneyRuntimeEngine.ExecuteOnceAsync</c> 的 finally），所以一辆车的读挂住推迟的
    /// 是每一辆车的监看；单车夹具量的就是这一段。
    /// </remarks>
    [Fact]
    [Trait("Requirement", "REQ-0248")]
    public async Task AnOpenStopReleasedInRiotIsReTriggeredWithinTheBudgetPerReadWhileVehicleReadsHang()
    {
        await using RuntimeFixture fixture = await InTransitDoorEmergencyReleaseTests.LatchedForTheDoorsAsync();
        string upperId = (await fixture.RuntimeAsync()).GateUpperId!;
        fixture.Riot.SetOrderState(upperId, RiotOrderState.Success, terminal: true);
        fixture.UnfinishedOrderIds = [];
        await DriveOneRoundAsync(fixture);
        Assert.Equal(1, await CountAsync(fixture, RiotCommandTypeNames.TriggerEmergency));
        TimeSpan budget = fixture.Options.RiotVehicleReadBudget;

        fixture.EmergencyLatched = false;
        (TimeSpan elapsed, int cut) = await TimedRoundWithHangingReadsAsync(fixture, hear: true);

        output.WriteLine($"round {elapsed.TotalSeconds:0.00} s, {cut} vehicle reads cut off at {budget.TotalSeconds:0.###} s");
        Assert.Equal(2, await CountAsync(fixture, RiotCommandTypeNames.TriggerEmergency));
        Assert.True(cut >= 1, "No vehicle read hung, so this proves nothing.");
        Assert.True(
            elapsed < budget * cut + TimeSpan.FromSeconds(3),
            $"The re-trigger came {elapsed} into the round with {cut} reads cut off at {budget}.");
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
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(10), $"The round took {elapsed.Elapsed}.");

        fixture.Riot.ReadVehicleDelay = null;
        await TickAndRunAsync(fixture);
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, (await JourneyOfAsync(fixture, FirstDemandId)).Stage);
    }

    // ---- helpers --------------------------------------------------------------------------------------------------------

    /// <summary>读车挂住 <see cref="Hang"/> 跑一轮：这一轮的真实耗时，与有几次读被预算截断。</summary>
    private static async Task<(TimeSpan Elapsed, int Cut)> TimedRoundWithHangingReadsAsync(
        RuntimeFixture fixture, bool hear = false)
    {
        int cut = 0;
        fixture.Riot.ReadVehicleDelay = async token =>
        {
            try
            {
                await Task.Delay(Hang, token);
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref cut);
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
        return (elapsed.Elapsed, Volatile.Read(ref cut));
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
