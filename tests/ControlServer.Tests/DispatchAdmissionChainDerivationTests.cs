using ControlServer.Application;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Dispatch;
using ControlServer.Host.Runtime.Dispatch.Criteria;
using ControlServer.Host.Runtime.RouteGraph;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ControlServer.Tests;

/// <summary>
/// 在途车那条准入链是从空闲链<b>派生</b>的（REQ-0205，批次7-06，control-server#211）：换掉一条车辆动态事实，
/// 加上追加的四道门，其余逐条共用。这里守的是那句「派生」本身。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么这几条必须存在。</b><see cref="DispatchAdmissionCriteria.InTransit"/> 的 remarks 里写着派生而不是
/// 另写一张表的理由——「下一个人往空闲链加判据时不会知道在途链也该加，而那正是『在途车放行了一条空闲车挡下的
/// 需求』的样子」。在这几条用例之前，那段话是<b>纪律</b>：把 <c>InTransit</c> 改成另写一张固定表，全量测试一条
/// 都不会红，因为没有任何判据读这条链<b>装了什么</b>。
/// </para>
/// <para>
/// <b>最后一条是唯一能抓住「另写一张表」的。</b>前四条比的是今天这两条链的内容，一张恰好抄对了今天内容的固定表
/// 能全部通过；只有「往空闲链塞一条，看它自己会不会出现在在途链里」问的是<b>派生关系</b>，而那正是要守的东西。
/// </para>
/// </remarks>
public sealed class DispatchAdmissionChainDerivationTests
{
    /// <summary>空闲链独有的那一条。</summary>
    private static readonly string[] OnlyOnTheIdleChain = [nameof(VehicleDynamicFactsCriterion)];

    /// <summary>在途链独有的两条：换上去的车辆事实，加上追加的四道门。</summary>
    private static readonly string[] OnlyOnTheInTransitChain =
        [nameof(EnRouteAppendCriterion), nameof(InTransitVehicleFactsCriterion)];

    [Fact]
    public void TheTwoChainsDifferByExactlyThoseThreeCriteria()
    {
        IReadOnlyList<IDispatchAdmissionCriterion> idle = IdleChain();
        IReadOnlyList<IDispatchAdmissionCriterion> inTransit = InTransitChain(idle, RouteGraph());

        Assert.Equal(OnlyOnTheIdleChain, Only(idle, inTransit));
        Assert.Equal(OnlyOnTheInTransitChain, Only(inTransit, idle));
    }

    /// <summary>
    /// 共用的那十几条在两条链上是同一串——<b>包括每条只出现一次</b>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 它与上面那条差集用例只差一点，而那一点正是它存在的理由：<c>Except</c> 是集合运算，<b>对重复不敏感</b>。
    /// 一个把某条共用判据装了两次的实现，差集完全正确，这条比序列的才红。重复不是无害的——链上有副作用的判据
    /// （记仓位账、读箱数）会因此跑两遍。
    /// </para>
    /// <para>
    /// <b>「次序」不是这条在守的东西</b>，尽管比的是序列：<c>Order</c> 是判据类型自己的属性，两条链拿的又是同
    /// 一批实例，所以共用部分的相对次序不是一个能独立变坏的自由度。位置那一头由下面「同一个位置」那条守。
    /// </para>
    /// </remarks>
    [Fact]
    public void EverythingTheTwoChainsShareRunsInTheSameOrder()
    {
        IReadOnlyList<IDispatchAdmissionCriterion> idle = IdleChain();
        IReadOnlyList<IDispatchAdmissionCriterion> inTransit = InTransitChain(idle, RouteGraph());

        // Where 而不是 Except：后者是集合运算，会把重复的那条一并去掉——也就是把这条用例唯一
        // 要抓的东西抓之前先吃掉。第一版正是那么写的，「装两次」的注入一条都没红。
        string[] sharedOnIdle =
            [.. InOrder(idle).Where(name => !OnlyOnTheIdleChain.Contains(name, StringComparer.Ordinal))];
        string[] sharedOnInTransit =
            [.. InOrder(inTransit).Where(name => !OnlyOnTheInTransitChain.Contains(name, StringComparer.Ordinal))];

        Assert.NotEmpty(sharedOnIdle);
        Assert.Equal(sharedOnIdle, sharedOnInTransit);
    }

    /// <summary>
    /// 没装路网引擎时，在途链只换掉车辆事实那一条，不会凭空长出追加门。
    /// </summary>
    /// <remarks>
    /// 四道追加门全都要问路网（分区连续、延迟门禁都读图），没有图时它们无从判断。这条守的是<b>省略</b>这个动作本身：
    /// 一个把 <see cref="EnRouteAppendCriterion"/> 无条件加进去的实现，会在没有图的部署上拿一个读不到图的判据去拦车。
    /// </remarks>
    [Fact]
    public void WithoutARouteGraphTheInTransitChainGainsNoAppendGate()
    {
        IReadOnlyList<IDispatchAdmissionCriterion> idle = IdleChain();
        IReadOnlyList<IDispatchAdmissionCriterion> inTransit = InTransitChain(idle, routeGraph: null);

        Assert.Equal(OnlyOnTheIdleChain, Only(idle, inTransit));
        Assert.Equal([nameof(InTransitVehicleFactsCriterion)], Only(inTransit, idle));
        Assert.DoesNotContain(nameof(EnRouteAppendCriterion), InOrder(inTransit));
    }

    /// <summary>
    /// 换上去的那条车辆事实判据，在链上占的是被换掉那条的位置。
    /// </summary>
    /// <remarks>
    /// <see cref="InTransitVehicleFactsCriterion"/> 的 summary 写着「与空闲链上
    /// <see cref="VehicleDynamicFactsCriterion"/> 同一个位置，两条链因此形状一致」——那是一句写下来的保证，
    /// 而 <c>Order</c> 是两个独立的字面量，改一个另一个不会跟着动。上面那条比共用部分的用例看不见它：
    /// 这两条判据各自只在一条链上，都落在差集里。
    /// </remarks>
    [Fact]
    public void TheTwoVehicleFactCriteriaOccupyTheSamePositionInTheirChains()
    {
        IOptions<JourneyRuntimeOptions> options = Options.Create(new JourneyRuntimeOptions());

        Assert.Equal(
            new VehicleDynamicFactsCriterion(options).Order,
            new InTransitVehicleFactsCriterion(options).Order);
    }

    /// <summary>
    /// 往空闲链加一条判据，它自己会出现在在途链上——没有第二处要记得改。
    /// </summary>
    /// <remarks>
    /// 这是 <see cref="DispatchAdmissionCriteria.InTransit"/> 那段 remarks 的直接翻译，也是这个文件里唯一一条
    /// 「另写一张表」通不过的用例。上面四条问的是今天这两条链<b>长什么样</b>，一张抄对了今天内容的固定表全能通过；
    /// 这条问的是<b>它从哪来</b>。
    /// </remarks>
    [Fact]
    public void ACriterionAddedToTheIdleChainReachesTheInTransitChainOnItsOwn()
    {
        NewlyAddedCriterion added = new();
        IReadOnlyList<IDispatchAdmissionCriterion> idle = [.. IdleChain(), added];

        IReadOnlyList<IDispatchAdmissionCriterion> inTransit = InTransitChain(idle, RouteGraph());

        Assert.Contains(added, inTransit);
        // 差集不因此变大：新来的那条两边都有，仍然只有那三条是独有的。
        Assert.Equal(OnlyOnTheIdleChain, Only(idle, inTransit));
        Assert.Equal(OnlyOnTheInTransitChain, Only(inTransit, idle));
    }

    /// <summary>被测的是装配，不是判断，所以每个协作者都可以是空的。</summary>
    private static IReadOnlyList<IDispatchAdmissionCriterion> IdleChain() =>
        DispatchAdmissionCriteria.Default(
            Options.Create(new JourneyRuntimeOptions()),
            new MapStationResolver(),
            packageCapacityStore: null!,
            store: null!,
            faultStore: null!,
            boxCountReader: null!,
            NullLogger<SlotCapacityCriterion>.Instance);

    private static IReadOnlyList<IDispatchAdmissionCriterion> InTransitChain(
        IReadOnlyList<IDispatchAdmissionCriterion> idle,
        RouteGraphAccess? routeGraph) =>
        DispatchAdmissionCriteria.InTransit(idle, Options.Create(new JourneyRuntimeOptions()), routeGraph);

    /// <summary>判据只在构造时被数进链里，不读这三个协作者，所以存储可以是空的。</summary>
    private static RouteGraphAccess RouteGraph() =>
        new(store: null!, Options.Create(new RouteGraphOptions()), TimeProvider.System);

    private static string[] InOrder(IEnumerable<IDispatchAdmissionCriterion> chain) =>
        [.. chain.OrderBy(criterion => criterion.Order).Select(criterion => criterion.GetType().Name)];

    private static string[] Only(
        IEnumerable<IDispatchAdmissionCriterion> chain,
        IEnumerable<IDispatchAdmissionCriterion> other) =>
        [.. InOrder(chain)
            .Except(InOrder(other), StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)];

    /// <summary>「下一个人加的那一条」。次序落在共用区间里，免得与被换掉的那条撞位置。</summary>
    private sealed class NewlyAddedCriterion : IDispatchAdmissionCriterion
    {
        public int Order => 45;

        public Task<string> EvaluateAsync(
            DispatchCandidateEvaluation evaluation, CancellationToken cancellationToken) =>
            Task.FromResult(DispatchAdmissionChain.Eligible);
    }
}
