using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Dispatch;
using ControlServer.Infrastructure.Persistence;

namespace ControlServer.Tests;

/// <summary>
/// 计划修订（REQ-0197；批次7-10，control-server#215）：没有剩余作业的停靠删掉，当前下一站之后在分区连续与延迟保护之内
/// 换成更省的顺序。
/// </summary>
/// <remarks>
/// <para>
/// 与 <see cref="Batch7EnRouteAppendPlannerTests"/> 同一张一维站点图：站号就是坐标，两站代价是站号之差乘一千毫米。
/// <b>每条用例的代价与增量都是手算的</b>，写在用例注释里；「跑出来是多少就断言多少」在判据写反时照样绿。
/// </para>
/// <para>
/// <b>延迟保护用一对边界用例钉住</b>：最省的那个排列让一条需求多走 10000 毫米，上限 9999 时它必须被放弃、
/// 10000 时必须被选中。只写其中一条，判据差一或写反都红不了。<see cref="JourneyPlanRevision.MaximumReorderableStops"/>
/// 同理，一条正向（八个照换）一条反向（九个不换）。
/// </para>
/// </remarks>
public sealed class Batch7PlanRevisionTests
{
    private const string ZoneA = "ZONE-A";
    private const string ZoneB = "ZONE-B";

    private static readonly Func<int, int, long?> Distance = (from, to) => Math.Abs(from - to) * 1000L;

    // ---- 重排 ------------------------------------------------------------------------------------

    /// <summary>
    /// 当前下一站之后的三个卸货停靠按 30、10、20 排，全程 30+20+10=60；换成 10、20、30 是 30，
    /// 而且每条需求都更早到——没有任何增量，所以分区没配置也照换。
    /// </summary>
    [Fact]
    public void StopsAfterTheCurrentNextStopAreReorderedToTheCheaperOrder()
    {
        PlanRevisionResult result = Revise(
            [Current("cur", 0), Unload("u30", 30), Unload("u10", 10), Unload("u20", 20)],
            [Loaded("d30", "u30"), Loaded("d10", "u10"), Loaded("d20", "u20"), Loaded("dcur", "cur")],
            zones: null);

        Assert.True(result.Reordered);
        Assert.Null(result.OrderKeptBecause);
        Assert.Equal(["cur", "u10", "u20", "u30"], OpenOrder(result));
        Assert.Empty(result.RemovedStopIds);
    }

    /// <summary>
    /// 当前下一站在 50，后面两个停靠在 0 与 45。无论怎么换，当前下一站都排第一，只换它之后的。
    /// </summary>
    [Fact]
    public void TheCurrentNextStopIsNeverMoved()
    {
        PlanRevisionResult result = Revise(
            [Current("cur", 50), Unload("u0", 0), Unload("u45", 45)],
            [Loaded("dcur", "cur"), Loaded("d0", "u0"), Loaded("d45", "u45")],
            zones: null);

        Assert.Equal("cur", OpenOrder(result)[0]);
        // 当前下一站之后：50→0→45 是 95，50→45→0 是 50，后者更省且 d0 的前缀 50→0 由 50 变成 50（不涨），d45 由 95 变 5。
        Assert.Equal(["cur", "u45", "u0"], OpenOrder(result));
    }

    /// <summary>
    /// A 区在 20、B 区在 10：先去 B 再回 A 更省（10+10=20 对 20+10=30），但那样是 A→B→A，分区断开，放弃，保持原序。
    /// </summary>
    [Fact]
    public void AReorderThatBreaksZoneContiguityIsAbandoned()
    {
        PlanRevisionResult result = Revise(
            [Current("cur", 0, ZoneA), Unload("a20", 20, ZoneA), Unload("b10", 10, ZoneB)],
            [Loaded("dcur", "cur"), Loaded("da", "a20"), Loaded("db", "b10")],
            zones: null);

        Assert.False(result.Reordered);
        Assert.Equal(["cur", "a20", "b10"], OpenOrder(result));
    }

    /// <summary>
    /// 一条还没装的需求：取货在 20，卸货在 10。先卸后取更省（10+10 对 20+10），但先卸后取没有意义，放弃。
    /// </summary>
    [Fact]
    public void AReorderThatPutsAnUnloadBeforeItsPickupIsAbandoned()
    {
        PlanRevisionResult result = Revise(
            [Current("cur", 0), Pickup("p20", 20), Unload("u10", 10)],
            [Loaded("dcur", "cur"), Pending("d", "p20", "u10")],
            zones: null);

        Assert.False(result.Reordered);
        Assert.Equal(["cur", "p20", "u10"], OpenOrder(result));
    }

    // ---- 延迟保护 -------------------------------------------------------------------------------
    //
    // 三条已装车的需求，卸货在 10、-5、12，原序全程 10+15+17=42。
    //   (-5,10,12) 全程 22：10 那条的前缀由 10 变 20，涨 10000；其余两条都更早。——最省
    //   (10,12,-5) 全程 29：-5 那条由 25 变 29，涨 4000。
    //   (-5,12,10) 全程 24：10 那条由 10 变 24，涨 14000。
    //   (12,10,-5) 全程 29：10 那条由 10 变 14、-5 那条由 25 变 29，各涨 4000；与 (10,12,-5) 同价，只接受严格更省的，先出现的那个胜出。
    //   (12,-5,10) 全程 44，比原序贵。

    /// <summary>上限 10000：最省的那个排列增量恰好到顶，选它。</summary>
    [Fact]
    public void TheCheapestOrderIsTakenWhenEveryIncreaseIsWithinTheZoneAllowance()
    {
        PlanRevisionResult result = ReviseTheDelayCase(allowance: 10_000);

        Assert.Equal(["cur", "u-5", "u10", "u12"], OpenOrder(result));
    }

    /// <summary>上限 9999：最省的那个超了一毫米，放弃；24 的那个涨 14000 也放弃；29 的只涨 4000，选它。</summary>
    [Fact]
    public void AnOrderThatDelaysADemandBeyondItsZoneAllowanceIsAbandoned()
    {
        PlanRevisionResult result = ReviseTheDelayCase(allowance: 9_999);

        Assert.Equal(["cur", "u10", "u12", "u-5"], OpenOrder(result));
    }

    /// <summary>上限 3999：每个比原序省的排列都会让某条需求多走至少 4000，全部放弃，保持原序。</summary>
    [Fact]
    public void WhenEveryCheaperOrderDelaysSomeoneTooMuchTheOrderIsKept()
    {
        PlanRevisionResult result = ReviseTheDelayCase(allowance: 3_999);

        Assert.False(result.Reordered);
        Assert.Equal(["cur", "u10", "u-5", "u12"], OpenOrder(result));
    }

    /// <summary>
    /// 分区没配置时一点增量都不接受：与途中追加的延迟门禁同一口径——「未配置」是「不许延」，不是「随便延」。
    /// </summary>
    [Fact]
    public void AnUnconfiguredZoneAcceptsNoIncreaseAtAll()
    {
        PlanRevisionResult result = Revise(DelayCaseStops(), DelayCaseDemands(), zones: null);

        Assert.False(result.Reordered);
        Assert.Equal(["cur", "u10", "u-5", "u12"], OpenOrder(result));
    }

    /// <summary>
    /// 分区配成 0 与没配置同一口径：一点增量都不接受。这条防的是把 0 读成「没有上限」——那会退化成随便延。
    /// 已用那种变异反向验证过它会红；把 <c>allowance &lt;= 0</c> 改成 <c>&lt; 0</c> 则是等价变异（上限 0 下任何正增量本来就超），它不红是对的。
    /// </summary>
    [Fact]
    public void AZoneAllowanceOfZeroAcceptsNoIncreaseAtAll()
    {
        PlanRevisionResult result = ReviseTheDelayCase(allowance: 0);

        Assert.False(result.Reordered);
        Assert.Equal(["cur", "u10", "u-5", "u12"], OpenOrder(result));
    }

    /// <summary>有一段代价算不出，就不换，并且说出理由：算不出的增量不是零。</summary>
    [Fact]
    public void AnUncomputableCostKeepsTheOrderAndSaysWhy()
    {
        Func<int, int, long?> brokenBetween0And30 = (from, to) =>
            (from, to) is (0, 30) or (30, 0) ? null : Distance(from, to);

        PlanRevisionResult result = JourneyPlanRevision.Revise(
            new PlanRevisionInput(
                [Current("cur", 0), Unload("u30", 30), Unload("u10", 10)],
                [Loaded("dcur", "cur"), Loaded("d30", "u30"), Loaded("d10", "u10")],
                CurrentStopMayGo: false),
            null,
            brokenBetween0And30);

        Assert.False(result.Reordered);
        Assert.Equal(PlanRevisionKeptReasons.DelayUncomputable, result.OrderKeptBecause);
    }

    /// <summary>八个可换序的停靠照换：上限是八，八以内都穷举。</summary>
    [Fact]
    public void EightReorderableStopsAreStillReordered()
    {
        PlanRevisionResult result = ReviseReversedChain(8);

        Assert.True(result.Reordered);
        Assert.Equal(["cur", .. Enumerable.Range(1, 8).Select(station => $"u{station}")], OpenOrder(result));
    }

    /// <summary>九个就不穷举，保持原序并说出理由——协议最多九条腿，当前下一站固定之后真实计划到不了九个。</summary>
    [Fact]
    public void NineReorderableStopsAreLeftInTheirOrder()
    {
        PlanRevisionResult result = ReviseReversedChain(9);

        Assert.False(result.Reordered);
        Assert.Equal(PlanRevisionKeptReasons.TooManyStops, result.OrderKeptBecause);
    }

    // ---- 删除 ------------------------------------------------------------------------------------

    /// <summary>
    /// 一条还没取货就离开的需求：它的取货停靠没有别的作业，删；它的卸货停靠同样没有别的作业，删。其余两个停靠原样。
    /// </summary>
    [Fact]
    public void ALeavingDemandTakesItsStopsWithItWhenNothingElseIsThere()
    {
        PlanRevisionResult result = Revise(
            [Current("cur", 0), Pickup("p10", 10), Unload("u20", 20), Unload("u30", 30)],
            [Pending("stay", "cur", "u30"), Leaving("gone", "p10", "u20")],
            zones: null);

        Assert.Equal(["p10", "u20"], result.RemovedStopIds);
        Assert.Equal(["cur", "u30"], OpenOrder(result));
    }

    /// <summary>同站还有别的需求要卸：卸货停靠留下，只删没人用的取货停靠。</summary>
    [Fact]
    public void AStopThatStillHasOtherWorkIsKept()
    {
        PlanRevisionResult result = Revise(
            [Current("cur", 0), Pickup("p10", 10), Unload("u20", 20)],
            [Pending("stay", "cur", "u20"), Leaving("gone", "p10", "u20")],
            zones: null);

        Assert.Equal(["p10"], result.RemovedStopIds);
        Assert.Equal(["cur", "u20"], OpenOrder(result));
    }

    /// <summary>
    /// 已装车的需求：旅程上其它需求全都离开或终结了，它的卸货停靠仍然留着——目的站保留到卸货完成（REQ-0197）。
    /// </summary>
    [Fact]
    public void ALoadedDemandKeepsItsDestination()
    {
        PlanRevisionResult result = Revise(
            [Current("cur", 0), Pickup("p10", 10), Unload("u20", 20), Unload("u30", 30)],
            [Loaded("onboard", "u30"), Leaving("gone", "p10", "u20"), Ended("done", "cur", "u20")],
            zones: null);

        Assert.Contains("u30", OpenOrder(result));
        Assert.DoesNotContain("u30", result.RemovedStopIds);
    }

    /// <summary>
    /// 在当前停靠上还没装就被终结的需求：它的卸货停靠在后面，没有别的作业，删。当前停靠本身不删——它不由修订决定。
    /// </summary>
    [Fact]
    public void AnEndedDemandsLaterUnloadIsRemovedButTheCurrentStopStays()
    {
        PlanRevisionResult result = Revise(
            [Current("cur", 0), Unload("u20", 20), Unload("u30", 30)],
            [Ended("cancelled", "cur", "u20"), Pending("stay", "cur", "u30")],
            zones: null);

        Assert.Equal(["u20"], result.RemovedStopIds);
        Assert.Equal(["cur", "u30"], OpenOrder(result));
    }

    /// <summary>当前下一站没有剩余作业、调用方也没放行：它留着。</summary>
    [Fact]
    public void TheCurrentNextStopIsKeptWithoutWorkUnlessTheCallerLetsItGo()
    {
        PlanRevisionResult kept = Revise(
            [Current("cur", 0), Unload("u10", 10)],
            [Leaving("gone", "cur", "u10")],
            zones: null);
        PlanRevisionResult released = Revise(
            [Current("cur", 0), Unload("u10", 10)],
            [Leaving("gone", "cur", "u10")],
            zones: null,
            currentStopMayGo: true);

        Assert.Equal(["u10"], kept.RemovedStopIds);
        Assert.Equal(["cur"], OpenOrder(kept));
        Assert.Equal(["cur", "u10"], released.RemovedStopIds);
        Assert.Empty(OpenOrder(released));
    }

    /// <summary>
    /// 取货停靠上唯一的需求已经装上车：取货停靠没有剩余作业了（放行时删掉），卸货停靠留着。
    /// 其余用例里已装的需求都把取货停靠指向自己的卸货停靠，只有这一条真的让「已装」挂在一个取货停靠上。
    /// </summary>
    [Fact]
    public void APickupStopWhoseOnlyDemandIsLoadedHasNoWorkLeft()
    {
        PlanRevisionResult result = Revise(
            [Current("cur", 0), Pickup("p10", 10), Unload("u20", 20)],
            [new PlanRevisionDemand("onboard", "p10", "u20", PlanRevisionDemandState.Loaded), Pending("stay", "cur", "u20")],
            zones: null);

        Assert.Equal(["p10"], result.RemovedStopIds);
        Assert.Equal(["cur", "u20"], OpenOrder(result));
    }

    // ---- 序位 ------------------------------------------------------------------------------------

    /// <summary>
    /// 已完成的停靠序位原样；没删的开放停靠从当前下一站的序位起连续编号；删掉的接在所有开放的之后（审查 M1）。
    /// </summary>
    [Fact]
    public void CompletedStopsKeepTheirSequenceAndTheRestAreNumberedAfterThem()
    {
        PlanRevisionResult result = Revise(
            [Done("p1", 1), Current("cur", 0, sequence: 2), Pickup("p10", 10, sequence: 3), Unload("u20", 20, sequence: 4)],
            [Loaded("d1", "u20"), Pending("dcur", "cur", "u20"), Leaving("gone", "p10", "u20")],
            zones: null);

        Assert.Equal(
            [("p1", 1), ("cur", 2), ("u20", 3), ("p10", 4)],
            result.Sequences.Select(item => (item.StopId, item.Sequence)));
    }

    /// <summary>
    /// 之前删掉、仍留着较大旧序位的停靠不把当前下一站往后推（审查 M1 的探针）：当前下一站原样是 1。
    /// </summary>
    /// <remarks>
    /// 旧的编号从「已完成或已删的最大序位 + 1」数起，这里那个最大值是之前删掉的 <c>gone</c> 的 2，于是当前下一站变成 3。
    /// 落到库里的后果（清单号跳）由 <c>Batch7PlanRevisionStageTests.ASecondRevisionKeepsTheCurrentStopAndItsWorklistRevisionWhereTheyWere</c> 守。
    /// </remarks>
    [Fact]
    public void AStopRemovedEarlierDoesNotPushTheCurrentStopBack()
    {
        PlanRevisionResult result = Revise(
            [Current("cur", 0, sequence: 1), Done("gone", 2) with { StationRiotId = 5 }, Pickup("p", 10, sequence: 3),
             Unload("u", 20, sequence: 4)],
            [Pending("dcur", "cur", "u"), Pending("dp", "p", "u")],
            zones: null);

        Assert.Equal(1, result.Sequences.Single(item => item.StopId == "cur").Sequence);
        Assert.Equal([1, 2, 3, 4], result.Sequences.Select(item => item.Sequence).Order());
        Assert.Equal(4, result.Sequences.Single(item => item.StopId == "gone").Sequence);
    }

    /// <summary>
    /// 没有人离开、原序已经最省：什么都不变——序位、顺序、理由都与输入一致。单需求旅程走的就是这一条。
    /// </summary>
    [Fact]
    public void NothingChangesWhenNoOneLeavesAndTheOrderIsAlreadyBest()
    {
        PlanRevisionResult result = Revise(
            [Current("cur", 0, sequence: 1), Unload("u10", 10, sequence: 2)],
            [Pending("only", "cur", "u10")],
            zones: null);

        Assert.Empty(result.RemovedStopIds);
        Assert.False(result.Reordered);
        Assert.Null(result.OrderKeptBecause);
        Assert.Equal([("cur", 1), ("u10", 2)], result.Sequences.Select(item => (item.StopId, item.Sequence)));
    }

    // ---- 投影 ------------------------------------------------------------------------------------

    /// <summary>
    /// 删掉的停靠不出现在发给车载端的计划里。修订只改停靠行的状态，不删行——行留着是为了审计与身份不复用——
    /// 所以投影那一侧必须自己跳过它，否则车会被告知去一个已经不去的站。删掉的停靠序位保持原值，
    /// 可能与重新编号后的开放停靠撞号，这条用例故意让它们撞上。
    /// </summary>
    [Fact]
    public void ARemovedStopIsNotProjectedIntoThePlanSentToTheVehicle()
    {
        JourneyRuntimeRow runtime = PlanRuntime();
        JourneyStopRow pickup = StopRow("s-pickup", 1, JourneyStopRoles.Pickup, JourneyStopStatuses.Active);
        JourneyStopRow removed = StopRow("s-removed", 2, JourneyStopRoles.Pickup, JourneyStopStatuses.Removed);
        JourneyStopRow unload = StopRow("s-unload", 2, JourneyStopRoles.Unload, JourneyStopStatuses.Pending);

        UpcomingStopPlanProjection plan = JourneyPlanBuilder.Plan(
            runtime, [pickup, removed, unload], pickup, arrivedAtCurrent: false, revision: 5);

        Assert.Equal(
            [("leg-s-pickup", "TO_PICKUP", 1, "ACTIVE"), ("leg-s-unload", "TO_DROPOFF", 2, "PLANNED")],
            plan.Legs.Select(leg => (leg.MovementLegId, leg.LegType, leg.Sequence, leg.State)));
    }

    // ---- 构造 ------------------------------------------------------------------------------------

    private static PlanRevisionResult ReviseTheDelayCase(long allowance) =>
        Revise(DelayCaseStops(), DelayCaseDemands(), Zones((ZoneA, allowance)));

    private static PlanRevisionStop[] DelayCaseStops() =>
        [Current("cur", 0), Unload("u10", 10), Unload("u-5", -5), Unload("u12", 12)];

    private static PlanRevisionDemand[] DelayCaseDemands() =>
        [Loaded("dcur", "cur"), Loaded("d10", "u10"), Loaded("d-5", "u-5"), Loaded("d12", "u12")];

    /// <summary>当前下一站在 0，后面 count 个卸货停靠按站号倒着排：顺着排显然更省，而且每条都更早到。</summary>
    private static PlanRevisionResult ReviseReversedChain(int count) =>
        Revise(
            [Current("cur", 0), .. Enumerable.Range(1, count).Reverse().Select(station => Unload($"u{station}", station))],
            [Loaded("dcur", "cur"), .. Enumerable.Range(1, count).Select(station => Loaded($"d{station}", $"u{station}"))],
            zones: null);

    private static PlanRevisionResult Revise(
        PlanRevisionStop[] stops,
        PlanRevisionDemand[] demands,
        DispatchZoneParameterTableVersion? zones,
        bool currentStopMayGo = false)
    {
        // 没写序位的停靠按出现的先后编号，已完成的在前。
        int next = 1;
        PlanRevisionStop[] numbered = [.. stops.Select(stop => stop.Sequence > 0 ? stop : stop with { Sequence = next++ })];
        return JourneyPlanRevision.Revise(new PlanRevisionInput(numbered, demands, currentStopMayGo), zones, Distance);
    }

    /// <summary>
    /// 修订之后开放停靠的顺序（按新序位）。已完成的停靠只有序位用例里的 <c>p1</c> 一个，删掉的停靠也有序位（排在开放的之后，
    /// 审查 M1），两者都在这里滤掉。
    /// </summary>
    private static string[] OpenOrder(PlanRevisionResult result) =>
        [.. result.Sequences.OrderBy(item => item.Sequence).Select(item => item.StopId)
            .Where(id => id != "p1" && !result.RemovedStopIds.Contains(id, StringComparer.Ordinal))];

    private static PlanRevisionStop Current(string id, int station, string zone = ZoneA, int sequence = 0) =>
        new(id, JourneyStopRoles.Pickup, station, zone, sequence, Done: false);

    private static PlanRevisionStop Pickup(string id, int station, string zone = ZoneA, int sequence = 0) =>
        new(id, JourneyStopRoles.Pickup, station, zone, sequence, Done: false);

    private static PlanRevisionStop Unload(string id, int station, string zone = ZoneA, int sequence = 0) =>
        new(id, JourneyStopRoles.Unload, station, zone, sequence, Done: false);

    private static PlanRevisionStop Done(string id, int sequence) =>
        new(id, JourneyStopRoles.Pickup, 0, ZoneA, sequence, Done: true);

    /// <summary>装了还没卸的需求。取货停靠用它自己的卸货停靠占位：已装的需求不看取货停靠。</summary>
    private static PlanRevisionDemand Loaded(string id, string unloadStopId) =>
        new(id, unloadStopId, unloadStopId, PlanRevisionDemandState.Loaded);

    private static PlanRevisionDemand Pending(string id, string pickupStopId, string unloadStopId) =>
        new(id, pickupStopId, unloadStopId, PlanRevisionDemandState.PendingLoad);

    private static PlanRevisionDemand Ended(string id, string pickupStopId, string unloadStopId) =>
        new(id, pickupStopId, unloadStopId, PlanRevisionDemandState.Ended);

    private static PlanRevisionDemand Leaving(string id, string pickupStopId, string unloadStopId) =>
        new(id, pickupStopId, unloadStopId, PlanRevisionDemandState.Leaving);

    private static JourneyStopRow StopRow(string stopId, int sequence, string role, string status) => new()
    {
        StopId = stopId,
        JourneyId = "journey-plan",
        Sequence = sequence,
        StopRole = role,
        StationId = "station-" + stopId,
        StationRiotId = sequence,
        DispatchZone = ZoneA,
        OperationSessionId = "session",
        MovementLegId = "leg-" + stopId,
        UpperId = "upper-" + stopId,
        VehicleBusinessMessageId = "vb-" + stopId,
        WorklistMessageId = "wl-" + stopId,
        PlanMessageId = "plan-" + stopId,
        Status = status,
    };

    private static JourneyRuntimeRow PlanRuntime() => new()
    {
        JourneyId = "journey-plan",
        DemandId = "demand-plan",
        Stage = JourneyRuntimeStage.AwaitingPickupArrival,
        AgvId = "agv02",
        VehicleKey = "KEY-agv02",
        AgvLifecycleGeneration = 1,
        MapId = 25,
        MapIdentity = "map-25",
        DispatchZone = ZoneA,
        RouteEvidenceId = "route",
        PickupStationId = "station-s-pickup",
        PickupStationRiotId = 1,
        GateStationId = "station-s-unload",
        GateStationRiotId = 2,
        ExpectedBasketCount = 1,
        TargetSlotsJson = "[1]",
        OperationSessionId = "session",
        PickupMovementLegId = "leg-s-pickup",
        PickupUpperId = "upper-s-pickup",
        GateMovementLegId = "leg-s-unload",
        GateUpperId = "upper-s-unload",
        DispatchGeneration = 1,
        VehicleBusinessRevision = 1,
        WorklistRevision = 1,
        PlanRevision = 1,
        VehicleBusinessMessageId = "vb",
        WorklistMessageId = "wl",
        PlanMessageId = "plan",
        SublotRequestMessageId = "sublot",
        LoadCommandMessageId = "load",
        LoadSlotOperationAttemptId = "load-attempt",
        PreDepartureSafetyCheckMessageId = "check-message",
        PreDepartureSafetyCheckId = "check",
        GateVehicleBusinessMessageId = "gate-vb",
        GateWorklistMessageId = "gate-wl",
        GatePlanMessageId = "gate-plan",
        UnloadCommandMessageId = "unload",
        UnloadSlotOperationAttemptId = "unload-attempt",
        CreatedAt = DateTimeOffset.UnixEpoch,
        UpdatedAt = DateTimeOffset.UnixEpoch,
    };

    private static DispatchZoneParameterTableVersion Zones(params (string Zone, long Allowance)[] zones) =>
        new(
            Version: 7,
            ContentSha256: new string('0', 64),
            SnapshotId: null,
            LoadedAt: DateTimeOffset.UnixEpoch,
            Source: "test",
            zones.ToDictionary(
                entry => entry.Zone,
                entry => new DispatchZoneParameters(entry.Zone, entry.Allowance, null),
                StringComparer.Ordinal));
}
