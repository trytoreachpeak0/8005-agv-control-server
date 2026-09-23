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

    /// <summary>
    /// 这张单是本服务端自己取消的（释放服务在车不再合格时经订单命令面发过 <c>CANCEL</c>）：不是「在 RIoT 里被人取消」，不重建。
    /// 旅程照 #316 说出原因、等人，不建新单，也不记重建。
    /// </summary>
    /// <remarks>
    /// 取消是服务端有意做的决定——那一刻它判定这辆车不该再跑这一趟——把它当成误操作重建，等于撤销自己的决定。
    /// 今天释放服务取消成功之后在同一轮就关闭旅程，走到这里要靠「取消成了、释放没落库」这类中断；这一条把「自己取消的不重建」
    /// 从「今天走不到」搬到构造上。
    /// </remarks>
    [Fact]
    public async Task AnOrderThisServerCancelledItselfIsNotRebuilt()
    {
        await using RuntimeFixture fixture = await DispatchedToPickupAsync();
        JourneyRuntimeRow before = await fixture.RuntimeAsync();
        await using (ControlServerDbContext writing = new(fixture.DbOptionsForTests))
        {
            writing.RiotOrderCommandAudit.Add(new RiotOrderCommandAuditRow
            {
                CommandAuditId = "L1-OWN-CANCEL-1",
                CommandType = RiotCommandTypeNames.CancelOrder,
                AgvId = before.AgvId,
                TargetUpperId = before.PickupUpperId,
                TargetOrderId = "ORDER-TO_PICKUP",
                AttemptNumber = 1,
                RequestSemanticSha256 = new string('a', 64),
                IssuedAt = fixture.Clock.GetUtcNow(),
                Outcome = RiotOrderCommandOutcome.Confirmed,
                ReconciledAt = fixture.Clock.GetUtcNow(),
            });
            await writing.SaveChangesAsync(Token);
        }

        fixture.Riot.CancelOrder(before.PickupUpperId);
        await TickAndRunAsync(fixture);
        await PassTheDelayAsync(fixture);
        await PassTheDelayAsync(fixture);

        Assert.Equal(1, fixture.Riot.CreateCount("TO_PICKUP"));
        Assert.Equal("ORDER_ENDED_WITHOUT_ARRIVAL", (await fixture.RuntimeAsync()).BlockReasonCode);
        await using ControlServerDbContext reading = new(fixture.DbOptionsForTests);
        Assert.Empty(await reading.OwnOrderRebuilds.AsNoTracking().ToArrayAsync(Token));
        Assert.Contains(fixture.EngineLog.Entries, entry =>
            entry.Message.Contains(before.PickupUpperId, StringComparison.Ordinal) &&
            entry.Message.Contains("cancelled by this server", StringComparison.Ordinal));
    }

    // ---- 护栏二：车况不允许就不建 ------------------------------------------------------------------------------

    /// <summary>
    /// 延迟到点时车处在急停、手动（下线、未启用、解抱闸）、故障里，或者不在本图上：不建，旅程码换成 <c>OWN_ORDER_REBUILD_WAITING_VEHICLE</c>，
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
    [InlineData("RIOT_VEHICLE_MAP_MISMATCH")]
    public async Task ARebuildWaitsWhileTheVehicleMayNotMoveAndIsMadeOnceItMay(string condition)
    {
        await using RuntimeFixture fixture = await DispatchedToPickupAsync();
        JourneyRuntimeRow before = await fixture.RuntimeAsync();
        JourneyStopRow[] stopsBefore = await StopsAsync(fixture, before.JourneyId);
        JourneyStopRow pickup = stopsBefore.Single(stop => stop.StopRole == JourneyStopRoles.Pickup);
        fixture.Riot.CancelOrder(pickup.UpperId);
        await TickAndRunAsync(fixture);
        RiotVehicleObservation onTheMap = fixture.Riot.Vehicle;
        if (condition == "VEHICLE_FAULT_IN_EFFECT")
        {
            await RecordFaultAsync(fixture);
        }
        else if (condition == "RIOT_VEHICLE_MAP_MISMATCH")
        {
            fixture.Riot.Vehicle = onTheMap with { CurrentMap = "MAP-ELSEWHERE" };
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
        fixture.Riot.Vehicle = onTheMap;
        await ClearFaultAsync(fixture);
        await fixture.HearFromPeerAsync();
        await TickAndRunAsync(fixture);

        Assert.Equal(2, fixture.Riot.CreateCount("TO_PICKUP"));
        await AssertRebuiltAsync(fixture, before, stopsBefore, pickup);
        Assert.Null((await fixture.RuntimeAsync()).BlockReasonCode);
    }

    /// <summary>
    /// 重建在等车况时车载端不说话了：看板上仍是 <c>OWN_ORDER_REBUILD_WAITING_VEHICLE</c>，不被「车不说话」（<c>ONBOARD_SESSION_LOST</c>）
    /// 盖掉；车恢复、车载端重新说话之后照常重建。
    /// </summary>
    /// <remarks>
    /// 与 #316 的停住码同一个理由：走到车前的人要知道的是「这趟在等车恢复就会自己重建」，不是它的一个症状。重建这一族码都在
    /// <c>JourneyRuntimeEngine.IsStalledOrderReason</c> 里，失联判定照那一族让路；把它们从族里拿掉，这一条红。
    /// </remarks>
    /// <summary>
    /// REQ-0239「重新通过正常派车和安全门禁」在车载端那一半：RIoT 说车停着、没故障还不够，车载端会话的安全摘要也要说这辆车可以走——
    /// 可离站、车已停、目标仓全锁、开锁输出全复位、没有未知，与首张取货单派车时同一组判据（<c>VehicleDynamicFactsCriterion</c>）。
    /// 任何一项不满足，重建都等着，旅程码 <c>OWN_ORDER_REBUILD_WAITING_VEHICLE</c>，记录写 <c>ONBOARD_DEPARTURE_UNSAFE</c>；满足之后才建。
    /// </summary>
    /// <remarks>
    /// 独立审查、调度提级为必修 M2：重建不走正常的离站核验与授权，原先只看 RIoT 的车辆安全读取。故障清除时人刚在车前，门可能被动过。
    /// 本票选的是「证明替代判据」：这五项正是门锁闭、开锁输出复位、无未知项与车载端自己的离站判断。
    /// </remarks>
    [Theory]
    [InlineData("not-departure-safe")]
    [InlineData("moving")]
    [InlineData("slot-unlocked")]
    [InlineData("output-not-reset")]
    [InlineData("unknown-present")]
    [Trait("Requirement", "REQ-0360")]
    public async Task ARebuildWaitsUntilTheOnboardSaysTheVehicleMayDepart(string unsafeBy)
    {
        await using RuntimeFixture fixture = await DispatchedToPickupAsync();
        JourneyRuntimeRow before = await fixture.RuntimeAsync();
        JourneyStopRow[] stopsBefore = await StopsAsync(fixture, before.JourneyId);
        fixture.Riot.CancelOrder(before.PickupUpperId);
        await TickAndRunAsync(fixture);
        await (unsafeBy switch
        {
            "not-departure-safe" => fixture.SetDepartureSummaryAsync(departureSafe: false),
            "moving" => fixture.SetDepartureSummaryAsync(vehicleStopped: false),
            "slot-unlocked" => fixture.SetDepartureSummaryAsync(allTargetSlotsLocked: false),
            "output-not-reset" => fixture.SetDepartureSummaryAsync(allUnlockOutputsReset: false),
            _ => fixture.SetDepartureSummaryAsync(unknownPresent: true),
        });

        await PassTheDelayAsync(fixture);
        await PassTheDelayAsync(fixture);

        Assert.Equal(1, fixture.Riot.CreateCount("TO_PICKUP"));
        OwnOrderRebuildRow waiting = await SingleRebuildAsync(fixture, before.PickupUpperId);
        Assert.Equal(OwnOrderRebuildStates.Pending, waiting.State);
        Assert.Contains("ONBOARD_DEPARTURE_UNSAFE", waiting.WaitingReason, StringComparison.Ordinal);

        await fixture.SetDepartureSummaryAsync();
        await PassTheDelayAsync(fixture);

        Assert.Equal(2, fixture.Riot.CreateCount("TO_PICKUP"));
        await AssertRebuiltAsync(fixture, before, stopsBefore, stopsBefore.Single(stop => stop.StopRole == JourneyStopRoles.Pickup));
    }

    [Fact]
    public async Task ASilentSessionDoesNotOverwriteARebuildThatWaitsForTheVehicle()
    {
        await using RuntimeFixture fixture = await DispatchedToPickupAsync();
        JourneyRuntimeRow before = await fixture.RuntimeAsync();
        fixture.Riot.CancelOrder(before.PickupUpperId);
        await TickAndRunAsync(fixture);
        fixture.Riot.SafetyReasons = ["RIOT_EMERGENCY_NOT_OK"];
        await PassTheDelayAsync(fixture);
        JourneyRuntimeRow waiting = await fixture.RuntimeAsync();
        Assert.Equal("OWN_ORDER_REBUILD_WAITING_VEHICLE", waiting.BlockReasonCode);

        fixture.Clock.Advance(SessionLiveness.Timeout + TimeSpan.FromSeconds(4));
        await fixture.Engine.ExecuteOnceAsync(Token);
        await fixture.Engine.ExecuteOnceAsync(Token);

        JourneyRuntimeRow silent = await fixture.RuntimeAsync();
        Assert.True(waiting.BlockReasonSince < fixture.Clock.GetUtcNow(), "the clock did not move, so keeping the start time proves nothing");
        Assert.Equal(("OWN_ORDER_REBUILD_WAITING_VEHICLE", waiting.BlockReasonSince), (silent.BlockReasonCode, silent.BlockReasonSince));
        Assert.Equal(1, fixture.Riot.CreateCount("TO_PICKUP"));

        fixture.Riot.SafetyReasons = [];
        await fixture.HearFromPeerAsync();
        await TickAndRunAsync(fixture);
        Assert.Equal(2, fixture.Riot.CreateCount("TO_PICKUP"));
    }

    // ---- 建单门禁（REQ-0305） ------------------------------------------------------------------------------------

    /// <summary>
    /// 重建的单和每一张还不存在的移动单一样，建之前过 REQ-0305 的建单门禁，问的是<b>它要去的那一站</b>（重建的取货单问取货站，
    /// 不是需求冻结的卸货站）。门禁不放行时不建，码 <c>OWN_ORDER_REBUILD_BLOCKED_BY_CREATE_GATE</c>，记录写明门禁的原因；放行之后照常建。
    /// </summary>
    [Fact]
    public async Task ARebuildGoesThroughTheCreateGateForTheStationItGoesTo()
    {
        await using RuntimeFixture fixture = await DispatchedToPickupAsync();
        JourneyRuntimeRow before = await fixture.RuntimeAsync();
        JourneyStopRow[] stopsBefore = await StopsAsync(fixture, before.JourneyId);
        JourneyStopRow pickup = stopsBefore.Single(stop => stop.StopRole == JourneyStopRoles.Pickup);
        fixture.Riot.CancelOrder(pickup.UpperId);
        await TickAndRunAsync(fixture);
        fixture.RouteCosts.FailFor(pickup.StationRiotId);
        fixture.RouteCosts.Calls.Clear();

        await PassTheDelayAsync(fixture);

        Assert.Equal(1, fixture.Riot.CreateCount("TO_PICKUP"));
        Assert.Equal("OWN_ORDER_REBUILD_BLOCKED_BY_CREATE_GATE", (await fixture.RuntimeAsync()).BlockReasonCode);
        Assert.Equal(
            "CREATE_GATE:CREATE_GATE_ROUTE_COST_UNAVAILABLE",
            (await SingleRebuildAsync(fixture, pickup.UpperId)).WaitingReason);
        Assert.Equal([(fixture.Options.MapId, pickup.StationRiotId, before.VehicleKey)], fixture.RouteCosts.Calls);

        fixture.RouteCosts.Set(pickup.StationRiotId, 9000);
        await fixture.HearFromPeerAsync();
        await TickAndRunAsync(fixture);

        Assert.Equal(2, fixture.Riot.CreateCount("TO_PICKUP"));
        await AssertRebuiltAsync(fixture, before, stopsBefore, pickup);
    }

    // ---- 护栏三：短时二次出问题即停 --------------------------------------------------------------------------

    /// <summary>
    /// 重建出来的单在窗口之内又被取消：不再重建，旅程码 <c>OWN_ORDER_REBUILD_STOPPED</c>，打错误级告警，记录写明原因；
    /// 再过多久都不建。窗口之外再被取消：照常再重建一次（新单号与第一次不同）。
    /// </summary>
    /// <remarks>
    /// REQ-0361：同一 <c>DemandId</c> 在第一次出问题之后的窗口内再次出问题，就不再自动重建。窗口的起点是第一次出问题的时刻
    /// （CP-0006 新增项二说明 5），不是重建建成的时刻——两者之差见 <see cref="Req0361TheWindowRunsFromTheFirstProblemNotFromTheRebuild"/>。
    /// 「连续取消说明有人确实想让它停下」是调度转述的理由，不是用户原话（同一份提案说明 2）。拿掉这道护栏，窗口内那一行红在
    /// 「建了第三张」上。窗口外那一行守的是反方向：护栏不能把「一次重建之后永远不再重建」当成挡法。
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [Trait("Requirement", "REQ-0361")]
    public async Task ARebuiltOrderCancelledAgainSoonIsNotRebuiltASecondTime(bool withinTheWindow)
    {
        await using RuntimeFixture fixture = await DispatchedToPickupAsync();
        JourneyRuntimeRow before = await fixture.RuntimeAsync();
        fixture.Riot.CancelOrder(before.PickupUpperId);
        await TickAndRunAsync(fixture);
        await PassTheDelayAsync(fixture);
        JourneyStopRow rebuiltStop = await CurrentStopAsync(fixture, FirstDemandId);
        Assert.Equal(2, fixture.Riot.CreateCount("TO_PICKUP"));
        OwnOrderRebuildRow first = await SingleRebuildAsync(fixture, before.PickupUpperId);
        Assert.NotNull(first.RebuiltAt);

        TimeSpan window = fixture.Options.OwnOrderRebuildRepeatWindow;
        fixture.Clock.Advance(withinTheWindow ? window - TimeSpan.FromMinutes(1) : window + TimeSpan.FromMinutes(1));
        await fixture.HearFromPeerAsync();
        fixture.Riot.CancelOrder(rebuiltStop.UpperId);
        await fixture.Engine.ExecuteOnceAsync(Token);
        await PassTheDelayAsync(fixture);
        await PassTheDelayAsync(fixture);

        OwnOrderRebuildRow second = await SingleRebuildAsync(fixture, rebuiltStop.UpperId);
        JourneyRuntimeRow after = await fixture.RuntimeAsync();
        if (withinTheWindow)
        {
            Assert.Equal(2, fixture.Riot.CreateCount("TO_PICKUP"));
            Assert.Equal(
                (OwnOrderRebuildStates.Stopped, "REBUILT_ORDER_ENDED_AGAIN_WITHIN_WINDOW"),
                (second.State, second.StoppedReason));
            Assert.Equal((JourneyRuntimeStage.AwaitingPickupArrival, "OWN_ORDER_REBUILD_STOPPED"), (after.Stage, after.BlockReasonCode));
            Assert.Equal(rebuiltStop.UpperId, (await CurrentStopAsync(fixture, FirstDemandId)).UpperId);
            Assert.Single(fixture.EngineLog.Entries, entry =>
                entry.Level == Microsoft.Extensions.Logging.LogLevel.Error &&
                entry.Message.Contains(rebuiltStop.UpperId, StringComparison.Ordinal) &&
                entry.Message.Contains("REBUILT_ORDER_ENDED_AGAIN_WITHIN_WINDOW", StringComparison.Ordinal));
        }
        else
        {
            Assert.Equal(3, fixture.Riot.CreateCount("TO_PICKUP"));
            Assert.Equal(OwnOrderRebuildStates.Rebuilt, second.State);
            Assert.NotEqual(first.NewUpperId, second.NewUpperId);
            Assert.Equal(second.NewUpperId, (await CurrentStopAsync(fixture, FirstDemandId)).UpperId);
            Assert.Null(after.BlockReasonCode);
        }
    }

    /// <summary>
    /// REQ-0361 的窗口从同一需求第一次出问题的时刻起算：第一次取消之后车况挡了 9 分钟才建成，第一次取消之后 12 分钟新单又被取消——
    /// 离第一次出问题已过窗口（10 分钟），照常再重建，哪怕离重建建成只有 3 分钟。
    /// </summary>
    /// <remarks>
    /// 这一条专门区分两种起点：从「重建建成」算，3 分钟在窗口内，会停；从「第一次出问题」算，12 分钟在窗口外，要建。用例里先断言
    /// 两个间隔确实落在窗口两侧，否则这条分不出两种实现。
    /// </remarks>
    [Fact]
    [Trait("Requirement", "REQ-0361")]
    public async Task Req0361TheWindowRunsFromTheFirstProblemNotFromTheRebuild()
    {
        await using RuntimeFixture fixture = await DispatchedToPickupAsync();
        JourneyRuntimeRow before = await fixture.RuntimeAsync();
        fixture.Riot.CancelOrder(before.PickupUpperId);
        await TickAndRunAsync(fixture);
        fixture.Riot.SafetyReasons = ["RIOT_EMERGENCY_NOT_OK"];
        fixture.Clock.Advance(TimeSpan.FromMinutes(9));
        await fixture.HearFromPeerAsync();
        await fixture.Engine.ExecuteOnceAsync(Token);
        Assert.Equal(1, fixture.Riot.CreateCount("TO_PICKUP"));
        fixture.Riot.SafetyReasons = [];
        await fixture.HearFromPeerAsync();
        await fixture.Engine.ExecuteOnceAsync(Token);
        Assert.Equal(2, fixture.Riot.CreateCount("TO_PICKUP"));
        OwnOrderRebuildRow first = await SingleRebuildAsync(fixture, before.PickupUpperId);
        JourneyStopRow rebuiltStop = await CurrentStopAsync(fixture, FirstDemandId);

        fixture.Clock.Advance(TimeSpan.FromMinutes(3));
        await fixture.HearFromPeerAsync();
        fixture.Riot.CancelOrder(rebuiltStop.UpperId);
        await fixture.Engine.ExecuteOnceAsync(Token);
        await PassTheDelayAsync(fixture);

        OwnOrderRebuildRow second = await SingleRebuildAsync(fixture, rebuiltStop.UpperId);
        TimeSpan window = fixture.Options.OwnOrderRebuildRepeatWindow;
        Assert.True(second.IncidentAt - first.IncidentAt > window, "the second problem must fall outside the window from the first");
        Assert.True(second.IncidentAt - first.RebuiltAt!.Value < window, "the second problem must fall inside the window from the rebuild");
        Assert.Equal((OwnOrderRebuildStates.Rebuilt, (string?)null), (second.State, second.StoppedReason));
        Assert.Equal(3, fixture.Riot.CreateCount("TO_PICKUP"));
        Assert.Equal(second.NewUpperId, (await CurrentStopAsync(fixture, FirstDemandId)).UpperId);
    }

    /// <summary>
    /// REQ-0360：一张移动单服务几条需求时，重建承载的是这一站上尚未终止的需求，已终止的不复活。开往取货站途中，这一站挂着两条需求、
    /// 其中一条已终止；单被取消、延迟后重建，新单去的还是这一站，两条需求的归属行一字不变——已终止的仍是已终止（移除时刻、停靠都不变），
    /// 另一条仍是待装。
    /// </summary>
    /// <remarks>
    /// 已终止那一条是直接写库造的：开往取货站途中的扫码前取消会被拒绝（<c>Batch7MultiDemandCancellationTests</c>），能让一站上一条终止、
    /// 一条不终止的真实路径发生在到站之后。这里钉的是重建这条路从不写归属行——它换的是停靠指向哪张单，不是停靠上挂着谁。
    /// </remarks>
    [Fact]
    [Trait("Requirement", "REQ-0360")]
    public async Task Req0360ARebuildCarriesTheStopsOpenDemandsAndRevivesNoTerminatedOne()
    {
        await using RuntimeFixture fixture = await DispatchedToPickupAsync();
        JourneyRuntimeRow before = await fixture.RuntimeAsync(FirstDemandId);
        await Batch7MultiDemandAdvanceTests.AddSecondDemandToJourneyAsync(fixture, before);
        await using (ControlServerDbContext writing = new(fixture.DbOptionsForTests))
        {
            JourneyDemandRow second = await writing.Set<JourneyDemandRow>().SingleAsync(row => row.DemandId == SecondDemandId, Token);
            second.Status = JourneyDemandStatuses.Terminated;
            await writing.SaveChangesAsync(Token);
        }

        fixture.Context.ChangeTracker.Clear();
        JourneyDemandRow[] membersBefore = await MembersAsync(fixture);
        Assert.Equal(
            [(FirstDemandId, JourneyDemandStatuses.PendingLoad), (SecondDemandId, JourneyDemandStatuses.Terminated)],
            membersBefore.Select(row => (row.DemandId, row.Status)));
        JourneyStopRow[] stopsBefore = await StopsAsync(fixture, before.JourneyId);
        JourneyStopRow pickup = stopsBefore.Single(stop => stop.StopRole == JourneyStopRoles.Pickup);
        fixture.Riot.CancelOrder(pickup.UpperId);
        await TickAndRunAsync(fixture);
        await PassTheDelayAsync(fixture);

        Assert.Equal(2, fixture.Riot.CreateCount("TO_PICKUP"));
        await AssertRebuiltAsync(fixture, before, stopsBefore, pickup);
        JourneyDemandRow[] membersAfter = await MembersAsync(fixture);
        Assert.Equal(
            membersBefore.Select(row => (row.DemandId, row.Status, row.RemovedAt, row.RemovalReason, row.PickupStopId, row.UnloadStopId)),
            membersAfter.Select(row => (row.DemandId, row.Status, row.RemovedAt, row.RemovalReason, row.PickupStopId, row.UnloadStopId)));
    }

    private static Task<JourneyDemandRow[]> MembersAsync(RuntimeFixture fixture) =>
        fixture.Context.Set<JourneyDemandRow>().AsNoTracking().OrderBy(row => row.DemandId).ToArrayAsync(Token);

    // ---- 幂等、失败与崩溃 --------------------------------------------------------------------------------------

    /// <summary>
    /// 同一次取消被处理了不止一次——延迟之内每一轮都读到它，中间还重启了一次：只记一行，只建一张新单；建成之后再跑几轮、再重启，
    /// 也不再建。
    /// </summary>
    /// <remarks>记录的主键由终结那张单的 upperId 派生，一张 RIoT 单只会终结一次，所以「第二次处理」落到的是同一行。</remarks>
    [Fact]
    public async Task TheSameCancellationHandledAgainAndAcrossARestartRebuildsOnce()
    {
        await using RuntimeFixture fixture = await DispatchedToPickupAsync();
        JourneyRuntimeRow before = await fixture.RuntimeAsync();
        JourneyStopRow[] stopsBefore = await StopsAsync(fixture, before.JourneyId);
        fixture.Riot.CancelOrder(before.PickupUpperId);
        await TickAndRunAsync(fixture);
        await TickAndRunAsync(fixture);
        await fixture.RecreateEngineAsync();
        await TickAndRunAsync(fixture);

        await PassTheDelayAsync(fixture);
        await fixture.RecreateEngineAsync();
        await fixture.HearFromPeerAsync();
        await TickAndRunAsync(fixture);
        await fixture.HearFromPeerAsync();
        await TickAndRunAsync(fixture);

        Assert.Equal(2, fixture.Riot.CreateCount("TO_PICKUP"));
        await using ControlServerDbContext reading = new(fixture.DbOptionsForTests);
        Assert.Single(await reading.OwnOrderRebuilds.AsNoTracking().ToArrayAsync(Token));
        Assert.Equal(2, await reading.OrderIntents.AsNoTracking().CountAsync(row => row.DemandId == FirstDemandId, Token));
        await AssertRebuiltAsync(fixture, before, stopsBefore, stopsBefore.Single(stop => stop.StopRole == JourneyStopRoles.Pickup));
    }

    /// <summary>
    /// 去重键本身：同一次终结被两个作用域各记一次（两轮引擎、或引擎与清除请求，各自一个上下文），落到的是同一行，
    /// 第二次拿到的就是第一次记下的那一行——同一个新单号、同一条腿——不会有第二行，也就建不出第二张单。
    /// </summary>
    /// <remarks>
    /// 上面那条走的是引擎：停靠上已有这条记录时引擎根本不再读那张旧单，所以它守不到键。这一条直接对着键：记录主键由终结那张单的
    /// upperId 派生（<c>StableGuid(EndedUpperId, ...)</c>），一张 RIoT 单只会终结一次。把主键换成随机值，第二个作用域就会再记一行。
    /// </remarks>
    [Fact]
    public async Task TheSameEndingRecordedFromTwoScopesIsOneRecordWithOneNewOrder()
    {
        await using RuntimeFixture fixture = await DispatchedToPickupAsync();
        JourneyRuntimeRow runtime = await fixture.RuntimeAsync();
        JourneyStopRow pickup = (await StopsAsync(fixture, runtime.JourneyId)).Single(stop => stop.StopRole == JourneyStopRoles.Pickup);

        OwnOrderRebuildRow first;
        await using (ControlServerDbContext one = new(fixture.DbOptionsForTests))
        {
            first = await OwnOrderRebuilds.StageAsync(
                one, runtime, pickup, pickup.UpperId, "ORDER-TO_PICKUP", RiotOrderState.Cancelled,
                OwnOrderRebuildSources.CancelledInRiot, fixture.Clock.GetUtcNow(), fixture.Clock.GetUtcNow(), null,
                fixture.Options, Token);
            await one.SaveChangesAsync(Token);
        }

        fixture.Clock.Advance(TimeSpan.FromSeconds(5));
        OwnOrderRebuildRow second;
        await using (ControlServerDbContext two = new(fixture.DbOptionsForTests))
        {
            second = await OwnOrderRebuilds.StageAsync(
                two, runtime, pickup, pickup.UpperId, "ORDER-TO_PICKUP", RiotOrderState.Cancelled,
                OwnOrderRebuildSources.CancelledInRiot, fixture.Clock.GetUtcNow(), fixture.Clock.GetUtcNow(), null,
                fixture.Options, Token);
            await two.SaveChangesAsync(Token);
        }

        Assert.Equal(
            (first.RebuildId, first.NewUpperId, first.NewMovementLegId, first.DueAt),
            (second.RebuildId, second.NewUpperId, second.NewMovementLegId, second.DueAt));
        await using ControlServerDbContext reading = new(fixture.DbOptionsForTests);
        Assert.Single(await reading.OwnOrderRebuilds.AsNoTracking().ToArrayAsync(Token));
    }

    /// <summary>
    /// 建新单那一次的回应丢了（RIoT 那边其实建成了）：这一轮不当成建成，也不当成失败，码是 <c>OWN_ORDER_REBUILD_ORDER_UNCONFIRMED</c>；
    /// 下一轮按同一个 upperId 对账，认出那张单，确认建成——前后只向 RIoT 发过一次创建。
    /// </summary>
    [Fact]
    public async Task ARebuildWhoseCreateAnswerIsLostIsConfirmedNextRoundWithoutASecondCreate()
    {
        await using RuntimeFixture fixture = await DispatchedToPickupAsync();
        JourneyRuntimeRow before = await fixture.RuntimeAsync();
        JourneyStopRow[] stopsBefore = await StopsAsync(fixture, before.JourneyId);
        fixture.Riot.CancelOrder(before.PickupUpperId);
        await TickAndRunAsync(fixture);
        fixture.Riot.LoseNextCreateResponse = true;

        await PassTheDelayAsync(fixture);

        Assert.Equal(2, fixture.Riot.CreateCount("TO_PICKUP"));
        Assert.Equal("OWN_ORDER_REBUILD_ORDER_UNCONFIRMED", (await fixture.RuntimeAsync()).BlockReasonCode);
        Assert.Equal(OwnOrderRebuildStates.Ordering, (await SingleRebuildAsync(fixture, before.PickupUpperId)).State);

        await fixture.HearFromPeerAsync();
        await TickAndRunAsync(fixture);

        Assert.Equal(2, fixture.Riot.CreateCount("TO_PICKUP"));
        await AssertRebuiltAsync(fixture, before, stopsBefore, stopsBefore.Single(stop => stop.StopRole == JourneyStopRoles.Pickup));
        Assert.Null((await fixture.RuntimeAsync()).BlockReasonCode);
    }

    /// <summary>
    /// 两个崩溃点：「已决定重建、未建单」（新单的意图与停靠的指向已落库，还没问 RIoT，进程没了）与「已建单、未记账」
    /// （RIoT 已经建成，回应还没记下，进程没了）。重启之后两种都只建成一张新单，并把这次重建记成已建成。
    /// </summary>
    [Theory]
    [InlineData("decided-not-created")]
    [InlineData("created-not-recorded")]
    public async Task ARebuildCutShortResumesAfterARestartWithExactlyOneNewOrder(string crashPoint)
    {
        await using RuntimeFixture fixture = await DispatchedToPickupAsync();
        JourneyRuntimeRow before = await fixture.RuntimeAsync();
        JourneyStopRow[] stopsBefore = await StopsAsync(fixture, before.JourneyId);
        fixture.Riot.CancelOrder(before.PickupUpperId);
        await TickAndRunAsync(fixture);
        string newUpperId = (await SingleRebuildAsync(fixture, before.PickupUpperId)).NewUpperId;
        if (crashPoint == "decided-not-created")
        {
            fixture.Riot.CrashOnNextReconcileOf = newUpperId;
        }
        else
        {
            fixture.Riot.CrashAfterNextCreate = true;
        }

        fixture.Clock.Advance(fixture.Options.OwnOrderRebuildDelay);
        await fixture.HearFromPeerAsync();
        await Assert.ThrowsAsync<IOException>(() => fixture.Engine.ExecuteOnceAsync(Token));
        Assert.True(fixture.Riot.CrashOnNextReconcileOf is null && !fixture.Riot.CrashAfterNextCreate, "the crash point was never reached");
        Assert.Equal(crashPoint == "decided-not-created" ? 1 : 2, fixture.Riot.CreateCount("TO_PICKUP"));
        Assert.Equal(newUpperId, (await StopsAsync(fixture, before.JourneyId)).Single(stop => stop.StopRole == JourneyStopRoles.Pickup).UpperId);
        Assert.Equal(OwnOrderRebuildStates.Ordering, (await SingleRebuildAsync(fixture, before.PickupUpperId)).State);

        await fixture.RecreateEngineAsync();
        await fixture.HearFromPeerAsync();
        await TickAndRunAsync(fixture);
        await fixture.HearFromPeerAsync();
        await TickAndRunAsync(fixture);

        Assert.Equal(2, fixture.Riot.CreateCount("TO_PICKUP"));
        await AssertRebuiltAsync(fixture, before, stopsBefore, stopsBefore.Single(stop => stop.StopRole == JourneyStopRoles.Pickup));
    }

    /// <summary>
    /// 崩在「已决定重建、未建单」之后，重启之前车进了急停（或建单门禁关了）：重启之后不建单，旅程码说出在等什么；车恢复（门禁放行）之后才建，
    /// 仍只建一张。
    /// </summary>
    /// <remarks>
    /// 独立审查（调度提级为必修 M1）：「已决定」那次保存把记录转成 Ordering，下一轮原本直接按新单号对账建单、不再读车况与门禁——
    /// 上面那条崩溃用例在重启前没改车况，看不出。意图从没发出过（<c>CreateAttemptCount == 0</c>）时，Ordering 与 Pending 一样要过全部建单前检查。
    /// </remarks>
    [Theory]
    [InlineData("emergency")]
    [InlineData("create-gate")]
    [Trait("Requirement", "REQ-0360")]
    public async Task ARebuildDecidedBeforeACrashChecksTheVehicleAndTheGateAgainAfterTheRestart(string closedBy)
    {
        await using RuntimeFixture fixture = await DispatchedToPickupAsync();
        JourneyRuntimeRow before = await fixture.RuntimeAsync();
        JourneyStopRow[] stopsBefore = await StopsAsync(fixture, before.JourneyId);
        JourneyStopRow pickup = stopsBefore.Single(stop => stop.StopRole == JourneyStopRoles.Pickup);
        fixture.Riot.CancelOrder(pickup.UpperId);
        await TickAndRunAsync(fixture);
        string newUpperId = (await SingleRebuildAsync(fixture, pickup.UpperId)).NewUpperId;
        fixture.Riot.CrashOnNextReconcileOf = newUpperId;
        fixture.Clock.Advance(fixture.Options.OwnOrderRebuildDelay);
        await fixture.HearFromPeerAsync();
        await Assert.ThrowsAsync<IOException>(() => fixture.Engine.ExecuteOnceAsync(Token));
        Assert.Null(fixture.Riot.CrashOnNextReconcileOf);
        Assert.Equal(OwnOrderRebuildStates.Ordering, (await SingleRebuildAsync(fixture, pickup.UpperId)).State);

        if (closedBy == "emergency")
        {
            fixture.Riot.SafetyReasons = ["RIOT_EMERGENCY_NOT_OK"];
        }
        else
        {
            fixture.RouteCosts.FailFor(pickup.StationRiotId);
        }

        await fixture.RecreateEngineAsync();
        await fixture.HearFromPeerAsync();
        await TickAndRunAsync(fixture);
        await fixture.HearFromPeerAsync();
        await TickAndRunAsync(fixture);

        Assert.Equal(1, fixture.Riot.CreateCount("TO_PICKUP"));
        Assert.Equal(
            closedBy == "emergency" ? "OWN_ORDER_REBUILD_WAITING_VEHICLE" : "OWN_ORDER_REBUILD_BLOCKED_BY_CREATE_GATE",
            (await fixture.RuntimeAsync()).BlockReasonCode);
        Assert.Equal(OwnOrderRebuildStates.Ordering, (await SingleRebuildAsync(fixture, pickup.UpperId)).State);

        fixture.Riot.SafetyReasons = [];
        fixture.RouteCosts.Set(pickup.StationRiotId, 9000);
        await fixture.HearFromPeerAsync();
        await TickAndRunAsync(fixture);

        Assert.Equal(2, fixture.Riot.CreateCount("TO_PICKUP"));
        await AssertRebuiltAsync(fixture, before, stopsBefore, pickup);
    }

    /// <summary>
    /// 新单建出去之后、还没对账确认之前，它在 RIoT 里就又被取消了：这本身就是「短时二次出问题」，不再重建，
    /// 旅程码 <c>OWN_ORDER_REBUILD_STOPPED</c>，记录写 <c>REBUILT_ORDER_ENDED_BEFORE_CONFIRMATION</c>。
    /// </summary>
    /// <remarks>对账会把那张单判成「终态、要人对账」（<c>TerminalReconciliationRequired</c>）；把它当成「没确认、下一轮再说」会每轮原地打转。</remarks>
    [Fact]
    public async Task ARebuiltOrderThatEndsBeforeItIsConfirmedStopsTheRebuilding()
    {
        await using RuntimeFixture fixture = await DispatchedToPickupAsync();
        JourneyRuntimeRow before = await fixture.RuntimeAsync();
        fixture.Riot.CancelOrder(before.PickupUpperId);
        await TickAndRunAsync(fixture);
        fixture.Riot.LoseNextCreateResponse = true;
        await PassTheDelayAsync(fixture);
        OwnOrderRebuildRow ordering = await SingleRebuildAsync(fixture, before.PickupUpperId);
        Assert.Equal(OwnOrderRebuildStates.Ordering, ordering.State);

        fixture.Riot.CancelOrder(ordering.NewUpperId);
        await fixture.HearFromPeerAsync();
        await TickAndRunAsync(fixture);
        await PassTheDelayAsync(fixture);

        OwnOrderRebuildRow stopped = await SingleRebuildAsync(fixture, before.PickupUpperId);
        Assert.Equal(
            (OwnOrderRebuildStates.Stopped, "REBUILT_ORDER_ENDED_BEFORE_CONFIRMATION"),
            (stopped.State, stopped.StoppedReason));
        Assert.Equal("OWN_ORDER_REBUILD_STOPPED", (await fixture.RuntimeAsync()).BlockReasonCode);
        Assert.Equal(2, fixture.Riot.CreateCount("TO_PICKUP"));
    }

    // ---- 会话因本服务端自己的在途单而未就绪（真车载端行驶全程的样子） ---------------------------------------------

    /// <summary>
    /// 来源一在真车载端上的样子：车在途、会话因本服务端自己的单停在 <c>DEPARTURE_SAFETY_NOT_READY</c>，单在这时被取消。
    /// 闸门后面照样记下、照样说出原因（不被 <c>ONBOARD_SESSION_NOT_READY</c> 盖掉）；延迟之后<b>不在闸门后面建单</b>，等会话回到就绪、
    /// 车载端说这辆车可以走，才重建。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 形状照 <c>PickupDispatchPlanPastOwnOrderTests.DropSessionOnOwnOrderAsync</c>。合成车载端永远报安全，合成 L2 按构造看不见这种会话。
    /// </para>
    /// <para>
    /// <b>翻转（审查 M2）</b>：第一版在这一格照样建单。可这一格里车载端报的正是 <c>unknownPresent=true</c>，离站安全没有人担保，给不出不依赖
    /// 就绪的安全依据，所以改成等。它不会一直等下去：单一旦终结，服务端的车辆安全读取就不再带 <c>RIOT_NONFINAL_ORDER_PRESENT</c>
    /// （<c>HttpRiotMovementGateway.ReadVehicleSafetyAsync</c> 只把 1、3、7、9 算未终结），车停稳后车载端的这一格自己会解除。
    /// </para>
    /// </remarks>
    [Fact]
    [Trait("Requirement", "REQ-0360")]
    public async Task AnOrderCancelledWhileTheSessionIsNotReadyOnItsOwnOrderWaitsForTheSessionBeforeItIsRebuilt()
    {
        await using RuntimeFixture fixture = await DispatchedToPickupAsync();
        JourneyRuntimeRow before = await fixture.RuntimeAsync();
        JourneyStopRow[] stopsBefore = await StopsAsync(fixture, before.JourneyId);
        await DropSessionOnOwnOrderAsync(fixture);
        fixture.Riot.CancelOrder(before.PickupUpperId);

        await TickAndRunAsync(fixture);
        await TickAndRunAsync(fixture);
        Assert.Equal("ORDER_ENDED_WITHOUT_ARRIVAL", (await fixture.RuntimeAsync()).BlockReasonCode);
        Assert.Equal(1, fixture.Riot.CreateCount("TO_PICKUP"));

        await PassTheDelayAsync(fixture);
        await PassTheDelayAsync(fixture);

        Assert.Equal(1, fixture.Riot.CreateCount("TO_PICKUP"));
        Assert.Equal("OWN_ORDER_REBUILD_WAITING_VEHICLE", (await fixture.RuntimeAsync()).BlockReasonCode);
        OwnOrderRebuildRow waiting = await SingleRebuildAsync(fixture, before.PickupUpperId);
        Assert.Equal((OwnOrderRebuildStates.Pending, "ONBOARD_SESSION_NOT_READY"), (waiting.State, waiting.WaitingReason));
        Assert.Equal(SessionReadiness.RecoveryRequired, (await fixture.Context.SessionRecoveries.AsNoTracking().SingleAsync(Token)).Readiness);

        fixture.Context.ChangeTracker.Clear();
        await fixture.RestoreSessionReadyAsync();
        await PassTheDelayAsync(fixture);

        Assert.Equal(2, fixture.Riot.CreateCount("TO_PICKUP"));
        await AssertRebuiltAsync(fixture, before, stopsBefore, stopsBefore.Single(stop => stop.StopRole == JourneyStopRoles.Pickup));
    }

    /// <summary>
    /// 闸门后面的另一面：会话不是因为车本身未就绪，而是在等一次作业恢复（仓门这一侧的事没有人担保），或者听不到车——
    /// 这时不建单，记录写明在等会话；会话回到就绪之后照常重建。
    /// </summary>
    /// <remarks>放宽成「闸门后面一律可以建」，这一条红在「一张都没建」上。</remarks>
    [Theory]
    [InlineData("operation-recovery")]
    [InlineData("silent")]
    public async Task ARebuildWaitsBehindTheGateWhenTheSessionIsHeldForMoreThanTheVehicle(string held)
    {
        await using RuntimeFixture fixture = await DispatchedToPickupAsync();
        JourneyRuntimeRow before = await fixture.RuntimeAsync();
        JourneyStopRow[] stopsBefore = await StopsAsync(fixture, before.JourneyId);
        fixture.Riot.CancelOrder(before.PickupUpperId);
        await TickAndRunAsync(fixture);
        if (held == "operation-recovery")
        {
            await fixture.MarkSessionRecoveryRequiredByOperationAsync();
        }
        else
        {
            await DropSessionOnOwnOrderAsync(fixture);
        }

        fixture.Clock.Advance(fixture.Options.OwnOrderRebuildDelay);
        if (held != "silent")
        {
            await fixture.HearFromPeerAsync();
        }
        await fixture.Engine.ExecuteOnceAsync(Token);
        await TickAndRunAsync(fixture);

        Assert.Equal(1, fixture.Riot.CreateCount("TO_PICKUP"));
        OwnOrderRebuildRow waiting = await SingleRebuildAsync(fixture, before.PickupUpperId);
        Assert.Equal((OwnOrderRebuildStates.Pending, "ONBOARD_SESSION_NOT_READY"), (waiting.State, waiting.WaitingReason));

        fixture.Context.ChangeTracker.Clear();
        await fixture.RestoreSessionReadyAsync();
        await PassTheDelayAsync(fixture);

        Assert.Equal(2, fixture.Riot.CreateCount("TO_PICKUP"));
        await AssertRebuiltAsync(fixture, before, stopsBefore, stopsBefore.Single(stop => stop.StopRole == JourneyStopRoles.Pickup));
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

    private static async Task<OwnOrderRebuildRow> SingleRebuildAsync(RuntimeFixture fixture, string endedUpperId)
    {
        await using ControlServerDbContext reading = new(fixture.DbOptionsForTests);
        return await reading.OwnOrderRebuilds.AsNoTracking().SingleAsync(row => row.EndedUpperId == endedUpperId, Token);
    }

    /// <summary>车载端读到车辆安全接口之后会话行变成的样子，照 <c>PickupDispatchPlanPastOwnOrderTests.DropSessionOnOwnOrderAsync</c>。</summary>
    internal static async Task DropSessionOnOwnOrderAsync(RuntimeFixture fixture)
    {
        SessionRecoveryRow session = await fixture.Context.SessionRecoveries.SingleAsync(Token);
        session.Readiness = SessionReadiness.RecoveryRequired;
        session.ReasonCode = "DEPARTURE_SAFETY_NOT_READY";
        session.DepartureSafe = false;
        session.SafetyReasonCodesJson = """["VEHICLE_NOT_READY"]""";
        session.SafetyUnknownPresent = true;
        session.UpdatedAt = fixture.Clock.GetUtcNow();
        await fixture.Context.SaveChangesAsync(Token);
        fixture.Context.ChangeTracker.Clear();
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
