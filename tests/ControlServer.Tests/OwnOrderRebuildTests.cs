using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using static ControlServer.Tests.Batch7StopDrivenAdvanceDriver;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;

namespace ControlServer.Tests;

/// <summary>
/// 本服务端自己建的单终结之后，为同一辆车、同一 <c>DemandId</c> 自动重建，继续这一趟（control-server#318）：
/// 单在 RIoT 里被取消或删除、人工清除「FAILED 且车上可能有货」的故障、人工清除「FAILED 且车上没货」的故障，三个来源共用一套重建，
/// 套同样的三条护栏——延迟、车况不允许就不建、短时二次出问题即停。
/// </summary>
/// <remarks>
/// <para>
/// 规则以用户的原话为准（票面点名的三条评论，最后一条 issuecomment-5787511271：「不改派啊，留在本车上」）：不释放、不改派、不等人确认。
/// </para>
/// <para>
/// 「重建出了正确的那一张单」逐项断：同车（<c>VehicleKey</c>）、同 <c>DemandId</c>、目标是终结那张单要去的那个停靠（续行起点就是这个停靠，
/// 车从哪里开由 RIoT 按车的当前位置定）、剩余停靠一个不少也不多、停靠指向新单，旧单的意图行原样留着。只断「没有释放」是不够的，空动作也满足它。
/// </para>
/// <para>码写成字面量：它们是看板上给人看的东西，改名应当让这里红。</para>
/// </remarks>
public sealed class OwnOrderRebuildTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ---- 来源一：单在 RIoT 里被取消 ---------------------------------------------------------------------------------

    /// <summary>
    /// 开往取货站的单被人在 RIoT 里取消：旅程先写 <c>ORDER_ENDED_WITHOUT_ARRIVAL</c>（#316 的挡住报警，现在是重建前的过渡）；
    /// 延迟不到不建；延迟一到，给同一辆车、同一条需求建一张开往同一个取货站的新单，旅程接着走，车到了照常进等录入。
    /// </summary>
    /// <remarks>
    /// 延迟从服务端读到取消的那一轮算。差一秒的那一轮是护栏一的反向判据：拿掉延迟，那一轮就建了。
    /// </remarks>
    [Fact]
    public async Task AnOrderCancelledInRiotIsRebuiltForTheSameVehicleAndDemandOnceTheDelayIsOver()
    {
        await using RuntimeFixture fixture = await DispatchedToPickupAsync();
        JourneyRuntimeRow before = await fixture.RuntimeAsync();
        JourneyStopRow[] stopsBefore = await StopsAsync(fixture, before.JourneyId);
        JourneyStopRow pickup = stopsBefore.Single(stop => stop.StopRole == JourneyStopRoles.Pickup);
        fixture.Riot.CancelOrder(pickup.UpperId);

        await TickAndRunAsync(fixture);
        DateTimeOffset detectedAt = fixture.Clock.GetUtcNow();
        Assert.Equal("ORDER_ENDED_WITHOUT_ARRIVAL", (await fixture.RuntimeAsync()).BlockReasonCode);

        fixture.Clock.Advance(fixture.Options.OwnOrderRebuildDelay - TimeSpan.FromSeconds(1));
        await fixture.HearFromPeerAsync();
        await fixture.Engine.ExecuteOnceAsync(Token);
        Assert.Equal(1, fixture.Riot.CreateCount("TO_PICKUP"));
        Assert.Equal(1, await IntentCountAsync(fixture));

        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        await fixture.HearFromPeerAsync();
        await fixture.Engine.ExecuteOnceAsync(Token);

        Assert.Equal(TimeSpan.FromSeconds(30), fixture.Clock.GetUtcNow() - detectedAt);
        Assert.Equal(2, fixture.Riot.CreateCount("TO_PICKUP"));
        await AssertRebuiltAsync(fixture, before, stopsBefore, pickup);
        JourneyRuntimeRow rebuilt = await fixture.RuntimeAsync();
        Assert.Equal((JourneyRuntimeStage.AwaitingPickupArrival, (string?)null), (rebuilt.Stage, rebuilt.BlockReasonCode));

        JourneyRuntimeRow arrived = await ArriveAtCurrentStopAsync(fixture, FirstDemandId, "TO_PICKUP");
        Assert.Equal((before.JourneyId, JourneyRuntimeStage.AwaitingSublot), (arrived.JourneyId, arrived.Stage));
    }

    /// <summary>
    /// 装着货开往卸货站的单被取消：同样延迟后给同一辆车、同一条需求重建，去的是同一个卸货停靠——车上的货送完这一趟；
    /// 车到了照常卸货。
    /// </summary>
    [Fact]
    public async Task ALoadedOrderCancelledInRiotIsRebuiltToTheSameUnloadStop()
    {
        await using RuntimeFixture fixture = await GateArrivalWaitAsync();
        JourneyRuntimeRow before = await fixture.RuntimeAsync();
        JourneyStopRow[] stopsBefore = await StopsAsync(fixture, before.JourneyId);
        JourneyStopRow unload = stopsBefore.Single(stop => stop.StopRole == JourneyStopRoles.Unload);
        fixture.Riot.CancelOrder(unload.UpperId);
        int gateCreates = fixture.Riot.CreateCount("TO_GATE");

        await TickAndRunAsync(fixture);
        Assert.Equal("ORDER_ENDED_WITHOUT_ARRIVAL", (await fixture.RuntimeAsync()).BlockReasonCode);
        await PassTheDelayAsync(fixture);

        Assert.Equal(gateCreates + 1, fixture.Riot.CreateCount("TO_GATE"));
        await AssertRebuiltAsync(fixture, before, stopsBefore, unload);
        Assert.Equal(JourneyRuntimeStage.AwaitingGateArrival, (await fixture.RuntimeAsync()).Stage);

        JourneyRuntimeRow arrived = await ArriveAtCurrentStopAsync(fixture, FirstDemandId, "TO_GATE");
        Assert.Equal((before.JourneyId, JourneyRuntimeStage.AwaitingUnloadResult), (arrived.JourneyId, arrived.Stage));
    }

    // ---- 护栏二：车况不允许就不建 ------------------------------------------------------------------------------

    /// <summary>
    /// 延迟到点时车处在急停、手动（下线、未启用、解抱闸）或故障里：不建，旅程码换成 <c>OWN_ORDER_REBUILD_WAITING_VEHICLE</c>，
    /// 记录上写明在等什么，告警只打一次；车恢复之后下一轮就建。
    /// </summary>
    /// <remarks>
    /// 车况读的是 RIoT 的车辆安全读取（车载端安全投影用的同一次读、同一组原因码）与服务端自己的故障事实。
    /// 拿掉这道护栏，第一轮就建了单，这一条红在「一张都没建」上。
    /// </remarks>
    [Theory]
    [InlineData("RIOT_EMERGENCY_NOT_OK")]
    [InlineData("RIOT_VEHICLE_NOT_ENABLED")]
    [InlineData("RIOT_VEHICLE_NOT_ONLINE")]
    [InlineData("RIOT_BRAKE_NOT_MOVABLE")]
    [InlineData("RIOT_CONTROL_NOT_OK")]
    [InlineData("VEHICLE_FAULT_IN_EFFECT")]
    public async Task ARebuildWaitsWhileTheVehicleMayNotMoveAndIsMadeOnceItMay(string condition)
    {
        await using RuntimeFixture fixture = await DispatchedToPickupAsync();
        JourneyRuntimeRow before = await fixture.RuntimeAsync();
        JourneyStopRow[] stopsBefore = await StopsAsync(fixture, before.JourneyId);
        JourneyStopRow pickup = stopsBefore.Single(stop => stop.StopRole == JourneyStopRoles.Pickup);
        fixture.Riot.CancelOrder(pickup.UpperId);
        await TickAndRunAsync(fixture);
        if (condition == "VEHICLE_FAULT_IN_EFFECT")
        {
            await RecordFaultAsync(fixture);
        }
        else
        {
            fixture.Riot.SafetyReasons = [condition];
        }

        await PassTheDelayAsync(fixture);
        await fixture.HearFromPeerAsync();
        await TickAndRunAsync(fixture);

        Assert.Equal(1, fixture.Riot.CreateCount("TO_PICKUP"));
        Assert.Equal("OWN_ORDER_REBUILD_WAITING_VEHICLE", (await fixture.RuntimeAsync()).BlockReasonCode);
        await using (ControlServerDbContext reading = new(fixture.DbOptionsForTests))
        {
            OwnOrderRebuildRow waiting = await reading.OwnOrderRebuilds.AsNoTracking().SingleAsync(Token);
            Assert.Equal(OwnOrderRebuildStates.Pending, waiting.State);
            Assert.Contains(condition, waiting.WaitingReason, StringComparison.Ordinal);
        }
        Assert.Single(fixture.EngineLog.Entries, entry =>
            entry.Message.Contains("is held back", StringComparison.Ordinal) &&
            entry.Message.Contains(condition, StringComparison.Ordinal));

        fixture.Riot.SafetyReasons = [];
        await ClearFaultAsync(fixture);
        await fixture.HearFromPeerAsync();
        await TickAndRunAsync(fixture);

        Assert.Equal(2, fixture.Riot.CreateCount("TO_PICKUP"));
        await AssertRebuiltAsync(fixture, before, stopsBefore, pickup);
        Assert.Null((await fixture.RuntimeAsync()).BlockReasonCode);
    }

    // ---- 夹具 ----------------------------------------------------------------------------------------------

    /// <summary>
    /// 重建出了正确的那一张单：同车、同 <c>DemandId</c>、去终结那张单要去的那个停靠；停靠指向新单，其余停靠原样；
    /// 旅程还是这一趟，归属一条都没被移除，旧单的意图原样留着。
    /// </summary>
    internal static async Task AssertRebuiltAsync(
        RuntimeFixture fixture,
        JourneyRuntimeRow before,
        JourneyStopRow[] stopsBefore,
        JourneyStopRow endedStop)
    {
        await using ControlServerDbContext reading = new(fixture.DbOptionsForTests);
        JourneyRuntimeRow journey = await reading.JourneyRuntimes.AsNoTracking().SingleAsync(Token);
        Assert.Equal((before.JourneyId, before.AgvId, before.VehicleKey), (journey.JourneyId, journey.AgvId, journey.VehicleKey));

        JourneyStopRow[] stopsAfter = await reading.Set<JourneyStopRow>().AsNoTracking()
            .Where(row => row.JourneyId == before.JourneyId).OrderBy(row => row.Sequence).ToArrayAsync(Token);
        Assert.Equal(
            stopsBefore.Select(stop => (stop.StopId, stop.Sequence, stop.StationId, stop.Status)),
            stopsAfter.Select(stop => (stop.StopId, stop.Sequence, stop.StationId, stop.Status)));
        JourneyStopRow repointed = stopsAfter.Single(stop => stop.StopId == endedStop.StopId);
        Assert.NotEqual(endedStop.UpperId, repointed.UpperId);
        Assert.NotEqual(endedStop.MovementLegId, repointed.MovementLegId);
        Assert.All(
            stopsAfter.Where(stop => stop.StopId != endedStop.StopId),
            stop => Assert.Equal(
                stopsBefore.Single(old => old.StopId == stop.StopId).UpperId, stop.UpperId));

        OrderIntentRow rebuilt = await reading.OrderIntents.AsNoTracking()
            .SingleAsync(row => row.UpperId == repointed.UpperId, Token);
        Assert.Equal(
            (repointed.MovementLegId, before.DemandId, before.VehicleKey, endedStop.StationRiotId, "CONFIRMED"),
            (rebuilt.MovementLegId, rebuilt.DemandId, rebuilt.VehicleKey, rebuilt.DestinationStationId, rebuilt.Status));
        Assert.StartsWith("W2G-", rebuilt.UpperId, StringComparison.Ordinal);
        Assert.NotNull(rebuilt.OrderId);

        OrderIntentRow ended = await reading.OrderIntents.AsNoTracking()
            .SingleAsync(row => row.UpperId == endedStop.UpperId, Token);
        Assert.Equal(endedStop.MovementLegId, ended.MovementLegId);

        Assert.All(
            await reading.Set<JourneyDemandRow>().AsNoTracking().ToArrayAsync(Token),
            membership => Assert.Null(membership.RemovedAt));
        Assert.All(
            await reading.JourneyBacklog.AsNoTracking().ToArrayAsync(Token),
            backlog => Assert.NotNull(backlog.AcceptedAt));

        // 记账：这一次重建的记录说它建成了，指向的正是停靠此刻指向的那张新单。
        OwnOrderRebuildRow record = await reading.OwnOrderRebuilds.AsNoTracking()
            .SingleAsync(row => row.EndedUpperId == endedStop.UpperId, Token);
        Assert.Equal(
            (OwnOrderRebuildStates.Rebuilt, repointed.UpperId, repointed.MovementLegId, before.DemandId),
            (record.State, record.NewUpperId, record.NewMovementLegId, record.DemandId));
        Assert.NotNull(record.RebuiltAt);
    }

    /// <summary>延迟到点的那一轮：拨过延迟、车载端刚说过话，跑一轮。</summary>
    internal static async Task PassTheDelayAsync(RuntimeFixture fixture)
    {
        DateTimeOffset before = fixture.Clock.GetUtcNow();
        fixture.Clock.Advance(fixture.Options.OwnOrderRebuildDelay);
        Assert.True(fixture.Clock.GetUtcNow() > before, "the clock did not move past the delay");
        await fixture.HearFromPeerAsync();
        await fixture.Engine.ExecuteOnceAsync(Token);
    }

    /// <summary>服务端自己的故障事实：这辆车被记为疑似故障（与在途单 FAILED 记下的同一级）。</summary>
    private static async Task RecordFaultAsync(RuntimeFixture fixture)
    {
        await using ControlServerDbContext writing = new(fixture.DbOptionsForTests);
        await new VehicleFaultStore(writing).RecordLevelAsync(
            fixture.Options.AgvId, VehicleFaultLevel.SuspectedBlocked, "L1_FAULT", false, fixture.Clock.GetUtcNow(), Token);
    }

    private static async Task ClearFaultAsync(RuntimeFixture fixture)
    {
        await using ControlServerDbContext writing = new(fixture.DbOptionsForTests);
        VehicleFaultStore faults = new(writing);
        if (await faults.ReadAsync(fixture.Options.AgvId, Token) is { Level: not VehicleFaultLevel.None } fault)
        {
            await faults.ClearAsync(fixture.Options.AgvId, fault.FaultGeneration, "L1", fixture.Clock.GetUtcNow(), Token);
        }
    }

    internal static async Task<JourneyStopRow[]> StopsAsync(RuntimeFixture fixture, string journeyId)
    {
        await using ControlServerDbContext reading = new(fixture.DbOptionsForTests);
        JourneyStopRow[] stops = await reading.Set<JourneyStopRow>().AsNoTracking()
            .Where(row => row.JourneyId == journeyId).OrderBy(row => row.Sequence).ToArrayAsync(Token);
        Assert.NotEmpty(stops);
        return stops;
    }

    private static async Task<int> IntentCountAsync(RuntimeFixture fixture)
    {
        await using ControlServerDbContext reading = new(fixture.DbOptionsForTests);
        return await reading.OrderIntents.AsNoTracking().CountAsync(row => row.DemandId == FirstDemandId, Token);
    }

    private static async Task<RuntimeFixture> GateArrivalWaitAsync()
    {
        RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(FirstDemandId, FirstSublot, createdAt: Now.AddMinutes(-10)));
        fixture.BoxCounts.Set(FirstSublot, 7);
        await fixture.AdvanceToGateArrivalAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingGateArrival, (await fixture.RuntimeAsync()).Stage);
        fixture.Riot.MovementState = "MT_FINISHED";
        fixture.Context.ChangeTracker.Clear();
        return fixture;
    }

    private static async Task<RuntimeFixture> DispatchedToPickupAsync()
    {
        RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(FirstDemandId, FirstSublot, Now.AddMinutes(-10)));
        fixture.BoxCounts.Set(FirstSublot, 7);
        await TickAndRunAsync(fixture);
        Assert.Equal(JourneyRuntimeStage.AwaitingPickupArrival, (await fixture.RuntimeAsync()).Stage);
        fixture.Riot.MovementState = "MT_FINISHED";
        fixture.Context.ChangeTracker.Clear();
        return fixture;
    }
}
