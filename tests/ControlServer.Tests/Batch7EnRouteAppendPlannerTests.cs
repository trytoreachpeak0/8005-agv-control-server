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
    /// <b>为什么不测「因为破坏连续而整条被拒」：那个形状造不出来，而这是一条结论，不是省略。</b>
    /// 新需求的两个停靠可以<b>相邻</b>插在当前下一站之后，于是它自己那两站总能形成一个独立的连续段——
    /// 分区等于当前下一站的就并进那一段，是计划里出现过的别的分区就插在它自己那一段旁边，全新的分区
    /// 接在哪里都自成一段。三种情形都连续。
    ///
    /// 批次7-06 为了给 <c>EN_ROUTE_APPEND_BREAKS_ZONE_CONTIGUITY</c> 补一条「它成为最终结论」的用例，试过
    /// 三种构造（同区、异区、新区），每一种都被上面那条相邻插入救了回去，最终结论都是放行。<b>所以这个码
    /// 在当前模型下只作为插入位的排除器起作用</b>——那个作用由下面这条用例钉着，它断言的 2000 正是最省的
    /// 位置被排除、退而求其次的代价。
    ///
    /// 没有穷尽证明它不可达，只是三次构造都失败加上上面那个理由。
    /// </para>
    /// <para>
    /// <b>腿数上限能不能把别的位置挤光、从而让连续性成为最终结论，也没有构造出来。</b>理由有两层，
    /// <b>都不是</b>这段注释此前写的那个——此前写的是「<c>lastRefusal ??=</c> 让第一个碰上的理由永久占位」，
    /// 而实现里<b>没有</b> <c>lastRefusal</c>：<see cref="EnRouteAppendPlanner"/> 用一个 <c>HashSet</c> 收下
    /// <b>每个插入位</b>各自的理由，最后由 <c>MostActionableRefusal</c> 按一张排序表挑。那个前提在写下它的时候
    /// 就已经不成立了，而结论照样对——这正是为什么要把前提本身写对：下一个人会照着前提推别的事。
    /// </para>
    /// <para>
    /// 真实的两层是：<b>其一</b>，同一个插入位撞上腿数就 <c>continue</c>，连续性那一判根本不会执行，
    /// 所以那个位置不会贡献连续性理由；<b>其二</b>，不同插入位可以各贡献一个（并入与新开的腿数增量不同，
    /// 边界上两种位置能并存），这时由排序表决定，而它把 <c>PLAN_LIMIT_REACHED</c> 排在
    /// <c>BREAKS_ZONE_CONTIGUITY</c> <b>之前</b>。
    /// </para>
    /// <para>
    /// <b>第二层是一个会变的前提，而今天没有判据钉着它——实测过。</b>排序表自己的注释写着「改掉收集方式时，
    /// 这张表要重新判」，而把表里那两项<b>对调</b>之后跑<b>全量 1952 条，零失败</b>
    /// （<c>evidence/b7-06/red/09-refusal-order-has-no-guard/</c>）。原因是现有用例造出的局面里那两个理由
    /// 从不同时出现：腿数满时所有新开位置都被腿数挡下并 <c>continue</c>，而并入位不改变停靠序列、
    /// 因此不会破坏连续。要钉住它，得构造一个「一部分插入位并入、一部分新开」的边界局面
    /// （两者的腿数增量不同，边界上能并存），让两个理由同时进 <c>HashSet</c>。
    /// 本票没有构造出来，写在这里而不是留着不说。
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

    // ---- 拒绝的原因码：报最有用的那一个，不是碰上的第一个 ----------------------------------------

    /// <summary>
    /// 一次追加里几个插入位栽在不同的门上时，报出去的是<b>操作员据此能做最多</b>的那一个
    /// （批次7-06，control-server#211）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 场景：八个既有停靠（最后一个是卸货），新需求的取货要绕一段路、卸货能并进末尾那个卸货停靠。
    /// </para>
    /// <list type="bullet">
    /// <item>两个停靠都新开的那些位置 → 十条腿，<c>PLAN_LIMIT_REACHED</c>；</item>
    /// <item>卸货并入、只新开取货的那些位置 → 九条腿放行，却超了本区 1000 毫米的上限，
    /// <c>DELAY_GATE_EXCEEDED</c>。</item>
    /// </list>
    /// <para>
    /// <b>循环先撞上的是腿数</b>（<c>pickupAt=1, unloadAt=1</c> 是第一个组合，两个都新开），所以「碰上的
    /// 第一个理由」会报 <c>PLAN_LIMIT_REACHED</c>——现场据此去等停靠减少，而真正的旋钮是那一区的延迟上限。
    /// </para>
    /// <para>
    /// <b>这条用例是这个改动唯一的判据</b>：改回报第一个碰上的理由，其余二十一条照样绿——它们每一条都只
    /// 触发一种理由，分辨不出先后。
    /// </para>
    /// </remarks>
    [Fact]
    public void TheRefusalReportedIsTheOneTheOperatorCanActOn()
    {
        EnRouteAppendDecision decision = EnRouteAppendPlanner.Plan(
            PlanOf(vehicleAt: 0, ChainEndingInAnUnload(8)),
            CandidateOf(Pickup("new-p", 9, ZoneA), Unload("new-u", 24, ZoneA), ZoneA),
            Zones((ZoneA, 1000)),
            Distance);

        Assert.Equal(DispatchReasonCodes.EnRouteAppendDelayGateExceeded, decision.RefusalReasonCode);
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

    /// <summary>
    /// 已经走完的停靠也占协议的腿数：一个已完成加六个还要走的，新需求的两个进来正好九条腿，放行。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>这一对用例钉的是「按哪些停靠数」，不是「9 这个数字」</b>——上面那一对已经钉死了 9。把门禁改回只数
    /// 还要走的那些，上面两条照样绿，因为它们没有已完成的停靠；红的是下面这一条。
    /// </para>
    /// <para>
    /// 为什么按整条旅程数：<c>JourneyPlanBuilder.Plan</c> 把传给它的停靠全部投影成腿，而调用方传的是
    /// <c>stops.Stops</c>，含已完成的。只数还要走的那些，差额正好是已完成的停靠数。
    /// </para>
    /// </remarks>
    [Fact]
    public void ACompletedStopStillCountsTowardsTheNineLegLimit()
    {
        EnRouteAppendDecision decision = EnRouteAppendPlanner.Plan(
            PlanFrom(vehicleAt: 0, currentNextStopIndex: 1, PickupChain(7)),
            CandidateOf(Pickup("new-p", 80, ZoneA), Unload("new-u", 82, ZoneA), ZoneA),
            Zones((ZoneA, 500_000)),
            Distance);

        Assert.Null(decision.RefusalReasonCode);
        Assert.Equal(EnRouteAppendPlanner.MaximumLegs, decision.Placement!.Resequenced.Count);
    }

    /// <summary>一个已完成加七个还要走的，新需求进来就是第十条腿：拒。</summary>
    [Fact]
    public void ACompletedStopPushesAPlanOverTheNineLegLimit()
    {
        EnRouteAppendDecision decision = EnRouteAppendPlanner.Plan(
            PlanFrom(vehicleAt: 0, currentNextStopIndex: 1, PickupChain(8)),
            CandidateOf(Pickup("new-p", 80, ZoneA), Unload("new-u", 82, ZoneA), ZoneA),
            Zones((ZoneA, 500_000)),
            Distance);

        Assert.Equal(DispatchReasonCodes.EnRouteAppendPlanLimitReached, decision.RefusalReasonCode);
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

    /// <summary>
    /// 当前下一站之后已删的停靠不算腿、只在重排最后占号（批次7-10，control-server#215，审查 M3）。
    /// </summary>
    /// <remarks>
    /// 同样九个停靠（七个开放、两个已删），两种交法对照：全放进停靠表（修前 DispatchRoundRunner 的交法），两个已删的也被当成
    /// 要去的站，加上新需求的两个就是十一条腿，被拒；已删的单独交（修后的交法），九条腿放行，重排覆盖全部十一个停靠、
    /// 已删的两个排在最后。两者只差已删的那两个怎么交，所以放行只能来自这一分。
    /// </remarks>
    [Fact]
    public void RemovedStopsAfterTheCurrentOneAreNotLegsAndAreNumberedLast()
    {
        EnRouteStop[] open = PickupChain(7);
        EnRouteStop[] removed = [Pickup("gone-1", 90, ZoneA), Pickup("gone-2", 92, ZoneA)];
        EnRouteAppendCandidate candidate = CandidateOf(Pickup("new-p", 80, ZoneA), Unload("new-u", 82, ZoneA), ZoneA);

        EnRouteAppendDecision asLegs = EnRouteAppendPlanner.Plan(
            PlanOf(vehicleAt: 0, [.. open, .. removed]), candidate, Zones((ZoneA, 500_000)), Distance);
        Assert.NotNull(asLegs.RefusalReasonCode);

        EnRouteAppendDecision trailing = EnRouteAppendPlanner.Plan(
            PlanOf(vehicleAt: 0, open) with { TrailingRemovedStopIds = ["gone-1", "gone-2"] },
            candidate, Zones((ZoneA, 500_000)), Distance);
        Assert.Null(trailing.RefusalReasonCode);
        Assert.Equal(
            [("gone-1", 10), ("gone-2", 11)],
            trailing.Placement!.Resequenced.Where(item => item.StopId.StartsWith("gone", StringComparison.Ordinal))
                .Select(item => (item.StopId, item.Sequence)));
        Assert.Equal(Enumerable.Range(1, 11), trailing.Placement.Resequenced.Select(item => item.Sequence).Order());
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

    /// <summary>当前下一站不在列表开头的计划：它前面那些是已经走完、不能再动的停靠。</summary>
    private static EnRouteVehiclePlan PlanFrom(int vehicleAt, int currentNextStopIndex, params EnRouteStop[] stops) =>
        new(
            stops,
            vehicleAt,
            currentNextStopIndex,
            stops.ToDictionary(stop => stop.StopId, _ => 1, StringComparer.Ordinal));

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
