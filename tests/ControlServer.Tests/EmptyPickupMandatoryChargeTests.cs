using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Charging;
using ControlServer.Host.Runtime.Dispatch;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using static ControlServer.Tests.Batch7StopDrivenAdvanceDriver;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;

namespace ControlServer.Tests;

/// <summary>
/// 空车取货旅程途中电量跌破强制充电线（control-server#883，用户 2026-11-17 定方案 B）：当前这一站照常等完，之后剩下没去的取货站撤回、需求退回待派，
/// 车去充电。
/// </summary>
/// <remarks>
/// <para>
/// <b>现场序列</b>（11-05 夜 agv01，旅程 93b05ef9）：一趟取货旅程在几个机台取货站之间轮转等录入，夜里没人录；00:11 电量 39%，低于强制充电线 40%，
/// 旅程不被打断，一路轮到 01:12 结束时只剩 15%，充电单 RIoT 没接，车载机低电反复断电。这里用两个机台取货站（12、13）重演：车在第一站等录入时
/// 电量跌到线下，没人扫码，期限到了离站时，第二站不去了。
/// </para>
/// <para>
/// <b>当前这一站怎么结束不归本规则</b>：没人扫码就按站点期限结束（REQ-0365：终结、按业务键抑制、挂人工搬运告警，与电量无关）；有人扫码装上货，
/// 车就不再是空车，按带货的既有规则走（control-server#782、#866）。本规则只管「离开这一站时车上什么都没有、后面还有取货站」那一刻。
/// </para>
/// <para>
/// 夹具的充电策略两道线都是 40%（<see cref="TestChargingPolicies.AllApprovedAt"/>），电量 10% 即在线下，80% 在线上。
/// </para>
/// </remarks>
public sealed class EmptyPickupMandatoryChargeTests
{
    private const string SecondPickupArea = "N1-2";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// 现场序列：第一站等录入时电量跌破强制充电线，没人扫码，期限到。车不去第二站：第二站的需求退回待派（仍是 <c>Accepted</c>、释放改派、不抑制），
    /// 旅程收尾、三套占用放开，下一轮充电分配把这辆车当空闲车排进充电（这里名册上只有一台谁也到不了的桩，所以结论是「此刻没有可用的桩」，
    /// 不是「车有用途」）。
    /// </summary>
    /// <remarks>
    /// 改前：期限一到车照常发离站核验去第二站，旅程不收尾，充电分配每轮判 <c>CHARGING_VEHICLE_HAS_PURPOSE</c>——正是那晚的样子。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    [Trait("IntegrationSlice", "FP-IS-11")]
    public async Task AnEmptyPickupJourneyBelowTheLineFinishesTheCurrentStopThenGivesUpTheRestAndGoesToCharge()
    {
        await using RuntimeFixture fixture = await WaitAtTheFirstOfTwoMachinePickupsAsync();
        JourneyStopRow firstStop = await CurrentStopAsync(fixture, FirstDemandId);
        await DropBelowTheLineAsync(fixture, batteryPercent: 10);

        await LetTheStationDeadlinePassAsync(fixture);

        JourneyRuntimeRow journey = await JourneyOfAsync(fixture, FirstDemandId);
        Assert.False(
            await DepartureCheckSentAsync(fixture, firstStop),
            $"The empty vehicle below the line was asked to leave for the next pickup. stage={journey.Stage} block={journey.BlockReasonCode}");
        Assert.Equal(JourneyRuntimeStage.Completed, journey.Stage);
        Assert.Equal(JourneyRuntimeEngine.EmptyPickupChargingRequiredReason, journey.BlockReasonCode);

        // 第二站的需求退回待派：仍是 Accepted，归属按释放改派移除，派车读口认它是「已释放待改派」。
        Assert.Equal(DemandExecutionStatus.Accepted, (await DemandAsync(fixture, SecondDemandId)).Status);
        JourneyDemandRow released = await MembershipAsync(fixture, SecondDemandId);
        Assert.NotNull(released.RemovedAt);
        Assert.Equal(DemandJourneyLookup.ReleasedForRedispatchReason, released.RemovalReason);
        Assert.True(
            await DemandJourneyLookup.ReleasedForRedispatch(fixture.Context).AnyAsync(row => row.DemandId == SecondDemandId, Token),
            "The withdrawn demand is not waiting to be dispatched again.");

        // 不压制：库里唯一的一条抑制是第一站那条按站点期限终结的（REQ-0365，本规则之外的既有行为），第二站的键上没有。
        await SuppressionAssertions.AssertSuppressedAsync(
            fixture.Context, FirstDemandId, JourneyRuntimeEngine.StationTimeoutCancellationReason);

        Assert.False(await fixture.Context.Set<VehiclePurposeClaimRow>().AnyAsync(Token), "The vehicle is still claimed.");
        Assert.Contains(
            fixture.EngineLog.Entries,
            entry => entry.Message.Contains("went back to the backlog instead of being visited", StringComparison.Ordinal));

        // 车成了空闲车：下一轮充电分配把它当成要充电的车（名册上只有一台到不了的桩，所以是「没有可用的桩」，不是「车有用途」），
        // 派车不再给它搬运，第二站那条需求的积压行说的是强制充电。
        await ChargingTestKit.WriteRosterWithAChargerNobodyIsSentToAsync(fixture.Context, fixture.Options.MapId, fixture.Clock.GetUtcNow());
        await fixture.HearFromPeerAsync();
        await TickAndRunAsync(fixture);
        Assert.Equal(
            ChargingAllocationReasons.NoChargerAvailable,
            fixture.ChargingBoard.Verdicts[fixture.Options.AgvId].Reason);
        Assert.Equal(1, await fixture.Context.JourneyRuntimes.CountAsync(Token));
        Assert.Equal(DispatchReasonCodes.MandatoryChargeRequired, (await fixture.BacklogAsync(SecondDemandId)).ReasonCode);
    }

    /// <summary>
    /// 当前这一站有人扫码、装上了货：车不再是空车，本规则不管。第二站不撤，车照常发离站核验（带货的去留归 control-server#782、#866 的既有规则）。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    [Trait("IntegrationSlice", "FP-IS-11")]
    public async Task ABasketLoadedAtTheCurrentStopKeepsTheJourneyUnderTheLoadedRules()
    {
        await using RuntimeFixture fixture = await WaitAtTheFirstOfTwoMachinePickupsAsync();
        JourneyStopRow firstStop = await CurrentStopAsync(fixture, FirstDemandId);
        await DropBelowTheLineAsync(fixture, batteryPercent: 10);

        await EnterSublotAsync(fixture, FirstDemandId, FirstSublot, FirstSubmissionId);
        await ApplySafeResultAsync(fixture, FirstDemandId, SlotOperationType.Load, SlotBusinessState.Occupied);
        // 装货落定的那一轮把离站等待的起点重置到此刻（给已装的留纠错时间），期限从这里再算。
        await TickAndRunAsync(fixture);
        await LetTheStationDeadlinePassAsync(fixture);

        JourneyRuntimeRow journey = await JourneyOfAsync(fixture, FirstDemandId);
        Assert.NotEqual(JourneyRuntimeStage.Completed, journey.Stage);
        Assert.NotEqual(JourneyRuntimeEngine.EmptyPickupChargingRequiredReason, journey.BlockReasonCode);
        Assert.True(await DepartureCheckSentAsync(fixture, firstStop), "The loaded vehicle was not asked to leave.");
        Assert.Equal(JourneyDemandStatuses.Loaded, (await MembershipAsync(fixture, FirstDemandId)).Status);
        Assert.Null((await MembershipAsync(fixture, SecondDemandId)).RemovedAt);
        Assert.True(await fixture.Context.Set<VehiclePurposeClaimRow>().AnyAsync(Token), "The loaded vehicle was let go.");
    }

    /// <summary>电量没跌破线：一切照旧，期限到了车去第二站，第二站的需求留在旅程里。</summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    [Trait("IntegrationSlice", "FP-IS-11")]
    public async Task AboveTheLineTheEmptyVehicleGoesOnToTheNextPickupAsBefore()
    {
        await using RuntimeFixture fixture = await WaitAtTheFirstOfTwoMachinePickupsAsync();
        JourneyStopRow firstStop = await CurrentStopAsync(fixture, FirstDemandId);
        await DropBelowTheLineAsync(fixture, batteryPercent: 80);

        await LetTheStationDeadlinePassAsync(fixture);

        JourneyRuntimeRow journey = await JourneyOfAsync(fixture, FirstDemandId);
        Assert.NotEqual(JourneyRuntimeStage.Completed, journey.Stage);
        Assert.True(await DepartureCheckSentAsync(fixture, firstStop), "The vehicle above the line did not go on.");
        Assert.Null((await MembershipAsync(fixture, SecondDemandId)).RemovedAt);
        Assert.True(await fixture.Context.Set<VehiclePurposeClaimRow>().AnyAsync(Token));
        Assert.DoesNotContain(
            fixture.EngineLog.Entries,
            entry => entry.Message.Contains("went back to the backlog instead of being visited", StringComparison.Ordinal));
    }

    /// <summary>两条机台需求、两个取货站（12、13），车到了第一站在等录入。离站期限 10 秒。</summary>
    private static async Task<RuntimeFixture> WaitAtTheFirstOfTwoMachinePickupsAsync()
    {
        RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        try
        {
            fixture.Catalog.Set(
                fixture.Demand(FirstDemandId, FirstSublot, Now.AddMinutes(-10)),
                fixture.Demand(SecondDemandId, SecondSublot, Now.AddMinutes(-9), area: SecondPickupArea));
            fixture.BoxCounts.Set(FirstSublot, 7);
            fixture.BoxCounts.Set(SecondSublot, 7);
            await TickAndRunAsync(fixture);
            await Batch7ThreeStopJourneyTests.AppendSecondDemandAsync(fixture);
            await ArriveAtPickupAsync(fixture, FirstDemandId);
            Assert.Equal(JourneyRuntimeStage.AwaitingSublot, (await JourneyOfAsync(fixture, FirstDemandId)).Stage);
            fixture.Options.StationDepartureWaitTimeout = TimeSpan.FromSeconds(10);
            return fixture;
        }
        catch
        {
            await fixture.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// 电量变成 <paramref name="batteryPercent"/>。生产的 RIoT 网关在 HTTP 调用返回之后才给读数盖时间戳，所以每次读车先把钟拨前 1 毫秒
    /// （同 <c>StagingPickupNoCountdownTests</c>）。
    /// </summary>
    private static Task DropBelowTheLineAsync(RuntimeFixture fixture, int batteryPercent)
    {
        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { BatteryPercent = batteryPercent };
        fixture.Riot.BeforeReadVehicle = () => fixture.Clock.Advance(TimeSpan.FromMilliseconds(1));
        return Task.CompletedTask;
    }

    /// <summary>仓门证明关着，期限过去，再跑三轮（期限结束本站一轮、离站等待一轮、余量一轮），每轮之前车都有心跳。</summary>
    private static async Task LetTheStationDeadlinePassAsync(RuntimeFixture fixture)
    {
        await fixture.ProveSlotDoorsClosedAsync();
        fixture.Clock.Advance(TimeSpan.FromSeconds(10));
        for (int round = 0; round < 3; round++)
        {
            await fixture.HearFromPeerAsync();
            await TickAndRunAsync(fixture);
        }
    }

    private static async Task<bool> DepartureCheckSentAsync(RuntimeFixture fixture, JourneyStopRow stop)
    {
        string? checkId = (await fixture.Context.Set<JourneyStopRow>().AsNoTracking()
            .SingleAsync(row => row.StopId == stop.StopId, Token)).DepartureSafetyCheckMessageId;
        return checkId is not null &&
            await fixture.Context.ProtocolOutbox.AsNoTracking().AnyAsync(row => row.MessageId == checkId, Token);
    }

    private static Task<AcceptedDemandRow> DemandAsync(RuntimeFixture fixture, string demandId) =>
        fixture.Context.AcceptedDemands.AsNoTracking().SingleAsync(row => row.DemandId == demandId, Token);

    private static Task<JourneyDemandRow> MembershipAsync(RuntimeFixture fixture, string demandId) =>
        fixture.Context.Set<JourneyDemandRow>().AsNoTracking().SingleAsync(row => row.DemandId == demandId, Token);
}
