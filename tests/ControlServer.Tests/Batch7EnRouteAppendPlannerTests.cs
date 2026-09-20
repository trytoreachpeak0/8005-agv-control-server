using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime.Dispatch;

namespace ControlServer.Tests;

/// <summary>
/// 途中追加的四道门（批次7-06，control-server#211）：当前下一站不可改（REQ-0196）、分区连续（REQ-0195）、
/// 延迟门禁（REQ-0198）、协议上限九腿八项。
/// </summary>
/// <remarks>
/// <para>
/// 这些用例喂给 <see cref="EnRouteAppendPlanner.Plan"/> 的是一维站点图：站号就是坐标，两站之间的计划路径代价是
/// 站号之差的绝对值乘一千毫米。<b>所以每条用例里的增量都是手算得出的</b>，而不是「跑出来是多少就断言多少」——
/// 后者在门禁判据改错方向时照样绿。
/// </para>
/// <para>
/// <b>上限那两个常量各有一条正向用例。</b>只写「第十条腿被拒」，把 <see cref="EnRouteAppendPlanner.MaximumLegs"/>
/// 从 9 改成 10 之后它仍然红不了；配一条「第九条腿通过」，9 才是上下都被钉住的。八项同理，而且那一对用例的腿数
/// 从头到尾都是 9、只有清单项数不同，所以「被拒是因为项数」不靠原因码字面，靠的是对照。
/// </para>
/// </remarks>
public sealed class Batch7EnRouteAppendPlannerTests
{
    private const string ZoneA = "ZONE-A";
    private const string ZoneB = "ZONE-B";
    private const string ZoneC = "ZONE-C";

    /// <summary>一维站点图：代价是站号之差乘一千毫米。</summary>
    private static readonly Func<int, int, long?> Distance =
        (from, to) => Math.Abs(from - to) * 1000L;

    // ---- 延迟门禁（REQ-0198） ----------------------------------------------------------------

    /// <summary>
    /// 本区没配置上限，就是本区禁止途中追加——参数批准之前，在途车的行为与今天一模一样。
    /// </summary>
    [Fact]
    public void AZoneWithNoParametersRefusesEveryAppend()
    {
        EnRouteAppendDecision decision = EnRouteAppendPlanner.Plan(
            PlanOf(vehicleAt: 0, Pickup("s-p", 10, ZoneA), Unload("s-u", 20, ZoneA)),
            CandidateOf(Pickup("new-p", 12, ZoneA), Unload("new-u", 20, ZoneA), ZoneA),
            Zones((ZoneB, 50_000)),
            Distance);

        Assert.Equal(DispatchReasonCodes.EnRouteAppendNotConfigured, decision.RefusalReasonCode);
    }

    /// <summary>
    /// 上限配成 0 与没配置是同一件事：本区禁止追加，不是「只许零增量」。
    /// </summary>
    /// <remarks>
    /// 这条需求顺路，增量本来就是 0；上限 0 仍然拒，所以判的确实是「有没有配置」而不是「增量是否超」。
    /// </remarks>
    [Fact]
    public void AZoneWhoseAllowanceIsZeroRefusesEveryAppend()
    {
        EnRouteAppendDecision decision = EnRouteAppendPlanner.Plan(
            PlanOf(vehicleAt: 0, Pickup("s-p", 10, ZoneA), Unload("s-u", 20, ZoneA)),
            CandidateOf(Pickup("new-p", 12, ZoneA), Unload("new-u", 20, ZoneA), ZoneA),
            Zones((ZoneA, 0)),
            Distance);

        Assert.Equal(DispatchReasonCodes.EnRouteAppendNotConfigured, decision.RefusalReasonCode);
    }

    /// <summary>
    /// 顺路的需求增量为零，在任何非零上限下都插得进去：车在 0，计划是 10 装、20 卸，全程 20000；
    /// 新需求 12 装、20 卸，走 0→10→12→20 仍是 20000。
    /// </summary>
    [Fact]
    public void AnAppendThatCostsNothingExtraIsPlaced()
    {
        EnRouteAppendDecision decision = EnRouteAppendPlanner.Plan(
            PlanOf(vehicleAt: 0, Pickup("s-p", 10, ZoneA), Unload("s-u", 20, ZoneA)),
            CandidateOf(Pickup("new-p", 12, ZoneA), Unload("new-u", 20, ZoneA), ZoneA),
            Zones((ZoneA, 1)),
            Distance);

        Assert.Null(decision.RefusalReasonCode);
        Assert.Equal(0, decision.Placement!.MarginalCostMm);
    }

    /// <summary>
    /// 增量恰好等于上限要放行，超过一毫米就拒：门禁是 <c>&gt;</c> 而不是 <c>&gt;=</c>，而这两行只差那一毫米。
    /// </summary>
    /// <remarks>
    /// 手算：车在 0，计划 10 装、20 卸，全程 20000。新需求在 8 号站装。8 在 10 之前，而插入位不得早于当前下一站，
    /// 所以最省的走法是 0→10→8→20 = 10+2+12 = 24000，增量 4000。
    /// </remarks>
    [Theory]
    [InlineData(4000, null)]
    [InlineData(3999, DispatchReasonCodes.EnRouteAppendDelayGateExceeded)]
    public void TheDelayGateAdmitsExactlyItsAllowanceAndNoMore(long allowance, string? expectedRefusal)
    {
        EnRouteAppendDecision decision = EnRouteAppendPlanner.Plan(
            PlanOf(vehicleAt: 0, Pickup("s-p", 10, ZoneA), Unload("s-u", 20, ZoneA)),
            CandidateOf(Pickup("new-p", 8, ZoneA), Unload("new-u", 20, ZoneA), ZoneA),
            Zones((ZoneA, allowance)),
            Distance);

        Assert.Equal(expectedRefusal, decision.RefusalReasonCode);
        if (expectedRefusal is null)
        {
            Assert.Equal(4000, decision.Placement!.MarginalCostMm);
        }
    }

    /// <summary>
    /// 路网算不出某一段的代价时，拒绝是「算不出」而不是「超限」：一个是路网的问题、一个是这条需求的问题，
    /// 运维看到之后的下一步动作不同，所以这两个原因码不能混。
    /// </summary>
    [Fact]
    public void AnUncomputablePathIsRefusedAsUncomputableRatherThanOverTheGate()
    {
        EnRouteAppendDecision decision = EnRouteAppendPlanner.Plan(
            PlanOf(vehicleAt: 0, Pickup("s-p", 10, ZoneA), Unload("s-u", 20, ZoneA)),
            CandidateOf(Pickup("new-p", 99, ZoneA), Unload("new-u", 20, ZoneA), ZoneA),
            Zones((ZoneA, 500_000)),
            // 99 号站到不了：凡是碰它的那一段都没有代价。
            (from, to) => from == 99 || to == 99 ? null : Distance(from, to));

        Assert.Equal(DispatchReasonCodes.EnRouteAppendDelayUncomputable, decision.RefusalReasonCode);
    }

    /// <summary>
    /// 延迟门禁保护的是<b>每一条</b>既有需求，不是它遇到的第一条。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 场景：车在 0，计划四站——2 卸（ZoneA，未配置上限）、10 装、12 卸（ZoneB，很宽）、60 卸（ZoneC，只许 1000）。
    /// 新需求在 ZoneB，要去 1 装、3 卸，也就是车刚过去的那一小段，得折返。手算：原全程 2+8+2+48 = 60000；
    /// 最省的插法是紧接当前下一站，0→2→1→3→10→12→60 = 2+1+2+7+2+48 = 62000。ZoneA 那条是当前下一站，
    /// 前缀代价一毫米没变；ZoneC 那条在最后，被延的就是这 2000，而它只许 1000。
    /// </para>
    /// <para>
    /// 这一条守的是一个真写错过的实现：循环里对「这一条没被延」直接返回放行，于是排在它后面的既有需求一次都没被
    /// 检查过，这次追加会被放行。三个分区的上限是特意错开的——新需求自己那一区很宽，所以拒绝<b>只能</b>来自
    /// 遍历到了 ZoneC 那一条，而不是新需求自己的增量超了——它自己那一区允许 100000。
    /// </para>
    /// </remarks>
    [Fact]
    public void TheDelayGateChecksEveryExistingDemandNotOnlyTheFirst()
    {
        EnRouteAppendDecision decision = EnRouteAppendPlanner.Plan(
            PlanOf(
                vehicleAt: 0,
                Unload("s-a", 2, ZoneA),
                Pickup("s-b", 10, ZoneB),
                Unload("s-c", 12, ZoneB),
                Unload("s-d", 60, ZoneC)),
            CandidateOf(Pickup("new-p", 1, ZoneB), Unload("new-u", 3, ZoneB), ZoneB),
            Zones((ZoneB, 100_000), (ZoneC, 1000)),
            Distance);

        Assert.Equal(DispatchReasonCodes.EnRouteAppendDelayGateExceeded, decision.RefusalReasonCode);
    }

    /// <summary>
    /// 既有需求所在分区没配置上限时，它一毫米都不接受：「本区禁止追加」与「本区随便延」是两回事，
    /// REQ-0198 里「未配置」说的是前者。
    /// </summary>
    /// <remarks>
    /// 与上一条同形，只把 ZoneC 的上限从 1000 改成「没有这一行」，于是同一次追加换一个原因码被拒。
    /// </remarks>
    [Fact]
    public void AnExistingDemandInAnUnconfiguredZoneAcceptsNoDelayAtAll()
    {
        EnRouteAppendDecision decision = EnRouteAppendPlanner.Plan(
            PlanOf(
                vehicleAt: 0,
                Unload("s-a", 2, ZoneA),
                Pickup("s-b", 10, ZoneB),
                Unload("s-c", 12, ZoneB),
                Unload("s-d", 60, ZoneC)),
            CandidateOf(Pickup("new-p", 1, ZoneB), Unload("new-u", 3, ZoneB), ZoneB),
            Zones((ZoneB, 100_000)),
            Distance);

        Assert.Equal(DispatchReasonCodes.EnRouteAppendNotConfigured, decision.RefusalReasonCode);
    }

    // ---- 分区连续（REQ-0195） ----------------------------------------------------------------

    /// <summary>
    /// 分区连续这道门改变的是<b>插在哪</b>：最便宜的那个插入位会让 A→B→A 交错，于是它被排除，选中的是贵一些
    /// 但保住区段的那个。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 场景：车在 0，计划 10 装（ZoneA）、12 卸（ZoneA）、50 装（ZoneB）、52 卸（ZoneB），全程 52000。
    /// 新需求在 ZoneB，要去 11 装、13 卸。
    /// </para>
    /// <para>
    /// 把它插进 ZoneA 那两站之间最省：0→10→11→12→13→50→52 = 52000，增量 0——但分区排成 A,B,A,B,B,B，
    /// ZoneA 断开后重现。合法的最早位置是 ZoneA 段之后：0→10→12→11→13→50→52 = 54000，增量 2000。
    /// <b>这条用例断言的就是那个 2000</b>：把连续性判据改成恒真，它立刻变回 0。
    /// </para>
    /// <para>
    /// <b>为什么不测「因为破坏连续而整条被拒」</b>：在这个模型里那几乎不可达。新需求的两个停靠可以相邻插入，
    /// 而任何一个分区要么等于计划第一站的分区（插在它后面就连续），要么在计划里出现过（插在它自己那一段旁边），
    /// 要么全新（接在末尾）。所以 <c>EN_ROUTE_APPEND_BREAKS_ZONE_CONTIGUITY</c> 实际的作用是排除插入位，
    /// 只有在腿数上限同时把别的位置挤掉时才会成为最终结论。PR 与出口报告照这个说法写。
    /// </para>
    /// </remarks>
    [Fact]
    public void ZoneContiguityRulesOutTheCheapestPlacementAndTheNextOneIsTaken()
    {
        EnRouteAppendDecision decision = EnRouteAppendPlanner.Plan(
            PlanOf(
                vehicleAt: 0,
                Pickup("s-p1", 10, ZoneA),
                Unload("s-u1", 12, ZoneA),
                Pickup("s-p2", 50, ZoneB),
                Unload("s-u2", 52, ZoneB)),
            CandidateOf(Pickup("new-p", 11, ZoneB), Unload("new-u", 13, ZoneB), ZoneB),
            Zones((ZoneA, 500_000), (ZoneB, 500_000)),
            Distance);

        Assert.Null(decision.RefusalReasonCode);
        Assert.Equal(2000, decision.Placement!.MarginalCostMm);
    }

    /// <summary>同一个分区接在自己后面永远连续：A→A→A 可以，这是上一条的反面。</summary>
    [Fact]
    public void AnAppendInTheSameZoneKeepsTheSegmentContiguous()
    {
        EnRouteAppendDecision decision = EnRouteAppendPlanner.Plan(
            PlanOf(
                vehicleAt: 0,
                Pickup("s-p", 10, ZoneA),
                Unload("s-u1", 12, ZoneA),
                Unload("s-u2", 14, ZoneA)),
            CandidateOf(Pickup("new-p", 11, ZoneA), Unload("new-u", 13, ZoneA), ZoneA),
            Zones((ZoneA, 500_000)),
            Distance);

        Assert.Null(decision.RefusalReasonCode);
    }

    /// <summary>
    /// 尾部接一个全新分区是连续的：A→A→B 可以。区段的规则是「不得断开后重现」，不是「一张计划只许一个分区」。
    /// </summary>
    [Fact]
    public void ANewZoneAppendedAfterTheLastStopIsContiguous()
    {
        EnRouteAppendDecision decision = EnRouteAppendPlanner.Plan(
            PlanOf(vehicleAt: 0, Pickup("s-p", 10, ZoneA), Unload("s-u", 12, ZoneA)),
            CandidateOf(Pickup("new-p", 14, ZoneB), Unload("new-u", 16, ZoneB), ZoneB),
            Zones((ZoneA, 500_000), (ZoneB, 500_000)),
            Distance);

        Assert.Null(decision.RefusalReasonCode);
    }

    // ---- 当前下一站不可改（REQ-0196） --------------------------------------------------------

    /// <summary>
    /// 车正驶向的那一站不能被插到前面去，也不能被并进去：同站同角色的新需求排在它<b>后面</b>另起一个停靠。
    /// </summary>
    /// <remarks>
    /// 物理上车不动，逻辑上是第二次到站、第二个作业会话——「每次到站一个作业会话」的口径因此不变，而
    /// REQ-0196 说的「当前下一站不可改」也不会被一次「只是给它多加一项清单」绕过去。
    /// </remarks>
    [Fact]
    public void TheCurrentNextStopIsNeitherPrecededNorMergedInto()
    {
        EnRouteAppendDecision decision = EnRouteAppendPlanner.Plan(
            PlanOf(vehicleAt: 0, Pickup("s-p", 10, ZoneA), Unload("s-u", 20, ZoneA)),
            CandidateOf(Pickup("new-p", 10, ZoneA), Unload("new-u", 20, ZoneA), ZoneA),
            Zones((ZoneA, 500_000)),
            Distance);

        Assert.Null(decision.RefusalReasonCode);
        EnRouteAppendPlacement placement = decision.Placement!;
        Assert.Null(placement.MergeIntoPickupStopId);
        Assert.Equal(1, placement.Resequenced.Single(stop => stop.StopId == "s-p").Sequence);
        Assert.Equal(2, placement.Resequenced.Single(stop => stop.StopId == "new-p").Sequence);
    }

    /// <summary>
    /// 当前下一站之后的同站同角色停靠是可以并入的：车本来就要去那一站，多一项清单不改任何既有停靠的次序。
    /// </summary>
    [Fact]
    public void AStopBehindTheCurrentNextOneIsMergedIntoRatherThanDuplicated()
    {
        EnRouteAppendDecision decision = EnRouteAppendPlanner.Plan(
            PlanOf(
                vehicleAt: 0,
                Pickup("s-p1", 10, ZoneA),
                Pickup("s-p2", 12, ZoneA),
                Unload("s-u", 20, ZoneA)),
            CandidateOf(Pickup("new-p", 12, ZoneA), Unload("new-u", 20, ZoneA), ZoneA),
            Zones((ZoneA, 500_000)),
            Distance);

        Assert.Null(decision.RefusalReasonCode);
        Assert.Equal("s-p2", decision.Placement!.MergeIntoPickupStopId);
        // 并入不新增停靠，所以序位一个都没动。
        Assert.Equal(3, decision.Placement.Resequenced.Count);
    }

    /// <summary>计划里一个未完成的停靠都没有时，没有「当前下一站之后」这个位置可插。</summary>
    [Fact]
    public void APlanWithNoRemainingStopsHasNowhereToInsert()
    {
        EnRouteAppendDecision decision = EnRouteAppendPlanner.Plan(
            PlanOf(vehicleAt: 0),
            CandidateOf(Pickup("new-p", 10, ZoneA), Unload("new-u", 20, ZoneA), ZoneA),
            Zones((ZoneA, 500_000)),
            Distance);

        Assert.Equal(DispatchReasonCodes.EnRouteAppendNoInsertionPoint, decision.RefusalReasonCode);
    }

    // ---- 协议上限：九腿八项 --------------------------------------------------------------------

    /// <summary>
    /// 七个既有停靠加上新需求的两个正好九条腿，是协议允许的最大一张计划，要放行。
    /// </summary>
    /// <remarks>下一条把既有停靠加到八个，唯一的差别就是那一条腿——两条一起把 9 这个数字钉死。</remarks>
    [Fact]
    public void APlanThatEndsUpWithNineLegsIsAllowed()
    {
        EnRouteAppendDecision decision = EnRouteAppendPlanner.Plan(
            PlanOf(vehicleAt: 0, PickupChain(7)),
            CandidateOf(Pickup("new-p", 80, ZoneA), Unload("new-u", 82, ZoneA), ZoneA),
            Zones((ZoneA, 500_000)),
            Distance);

        Assert.Null(decision.RefusalReasonCode);
        Assert.Equal(EnRouteAppendPlanner.MaximumLegs, decision.Placement!.Resequenced.Count);
    }

    /// <summary>第十条腿协议装不下，所以这条需求这一轮插不进去。</summary>
    [Fact]
    public void APlanThatWouldNeedATenthLegIsRefused()
    {
        EnRouteAppendDecision decision = EnRouteAppendPlanner.Plan(
            PlanOf(vehicleAt: 0, PickupChain(8)),
            CandidateOf(Pickup("new-p", 80, ZoneA), Unload("new-u", 82, ZoneA), ZoneA),
            Zones((ZoneA, 500_000)),
            Distance);

        Assert.Equal(DispatchReasonCodes.EnRouteAppendPlanLimitReached, decision.RefusalReasonCode);
    }

    /// <summary>
    /// 并入可以让一个停靠的清单涨到八项，第九项就装不下了。
    /// </summary>
    /// <remarks>
    /// 九个既有停靠已经把腿数占满，所以唯一插得下的方案是两边都并入——<b>这一对用例的腿数从头到尾都是 9</b>，
    /// 项数因此是唯一的变量，「被拒是因为项数」不靠原因码字面。
    /// </remarks>
    [Theory]
    [InlineData(7, null)]
    [InlineData(8, DispatchReasonCodes.EnRouteAppendPlanLimitReached)]
    public void AMergedStopMayCarryEightWorklistItemsAndNoMore(int existingItems, string? expectedRefusal)
    {
        EnRouteStop[] stops = ChainEndingInAnUnload(9);
        EnRouteStop mergePickupTarget = stops[1];
        EnRouteStop mergeUnloadTarget = stops[^1];
        EnRouteAppendDecision decision = EnRouteAppendPlanner.Plan(
            new EnRouteVehiclePlan(
                stops,
                VehicleStationRiotId: 0,
                CurrentNextStopIndex: 0,
                stops.ToDictionary(
                    stop => stop.StopId,
                    stop => stop.StopId == mergePickupTarget.StopId ? existingItems : 1,
                    StringComparer.Ordinal)),
            CandidateOf(
                Pickup("new-p", mergePickupTarget.StationRiotId, ZoneA),
                Unload("new-u", mergeUnloadTarget.StationRiotId, ZoneA),
                ZoneA),
            Zones((ZoneA, 500_000)),
            Distance);

        Assert.Equal(expectedRefusal, decision.RefusalReasonCode);
        if (expectedRefusal is null)
        {
            Assert.Equal(mergePickupTarget.StopId, decision.Placement!.MergeIntoPickupStopId);
            Assert.Equal(EnRouteAppendPlanner.MaximumLegs, decision.Placement.Resequenced.Count);
        }
    }

    // ---- 插入位的选择 ---------------------------------------------------------------------------

    /// <summary>
    /// 有好几个合法插入位时选边际代价最小的那个：车在 0，计划 10 装、30 卸（30000），新需求 20 装、30 卸。
    /// 插在既有卸货之前顺路，增量 0；插在它之后要折返，增量 20000。
    /// </summary>
    [Fact]
    public void ThePlannerPicksTheCheapestLegalPlacement()
    {
        EnRouteAppendDecision decision = EnRouteAppendPlanner.Plan(
            PlanOf(vehicleAt: 0, Pickup("s-p", 10, ZoneA), Unload("s-u", 30, ZoneA)),
            CandidateOf(Pickup("new-p", 20, ZoneA), Unload("new-u", 30, ZoneA), ZoneA),
            Zones((ZoneA, 500_000)),
            Distance);

        Assert.Null(decision.RefusalReasonCode);
        Assert.Equal(0, decision.Placement!.MarginalCostMm);
    }

    // ---- helpers ---------------------------------------------------------------------------------

    private static EnRouteStop Pickup(string stopId, int station, string zone) =>
        new(stopId, $"ST-{station}", station, zone, JourneyStopRoles.Pickup);

    private static EnRouteStop Unload(string stopId, int station, string zone) =>
        new(stopId, $"ST-{station}", station, zone, JourneyStopRoles.Unload);

    /// <summary>站号 10, 12, 14 … 递增的一串取货停靠，用来把腿数顶到某个数。</summary>
    private static EnRouteStop[] PickupChain(int count) =>
        [.. Enumerable.Range(0, count).Select(index => Pickup($"s-{index}", 10 + (index * 2), ZoneA))];

    /// <summary>同一串，但最后一个是卸货停靠——并入卸货需要一个同角色的目标。</summary>
    private static EnRouteStop[] ChainEndingInAnUnload(int count)
    {
        EnRouteStop[] stops = PickupChain(count);
        EnRouteStop last = stops[^1];
        stops[^1] = Unload(last.StopId, last.StationRiotId, ZoneA);
        return stops;
    }

    private static EnRouteVehiclePlan PlanOf(int vehicleAt, params EnRouteStop[] stops) =>
        new(
            stops,
            vehicleAt,
            CurrentNextStopIndex: 0,
            stops.ToDictionary(stop => stop.StopId, _ => 1, StringComparer.Ordinal));

    private static EnRouteAppendCandidate CandidateOf(EnRouteStop pickup, EnRouteStop unload, string zone) =>
        new(pickup, unload, zone);

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
