using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Dispatch;
using ControlServer.Host.Runtime.Fleet;

namespace ControlServer.Tests;

/// <summary>
/// 车辆侧的次序：<b>这一条任务派给哪辆车</b>（批次7-06，control-server#211）。
/// </summary>
/// <remarks>
/// <para>
/// 与任务侧的 <see cref="DispatchCandidateOrderingTests"/> 对称，但要证的事不同。任务侧是<b>行为不变</b>的守卫：
/// 拿翻转轮次之前的那个排序当基准逐条对照。车辆侧这几层是本票<b>新写</b>的，没有基准可对——所以这里钉的是
/// 每一层的方向、以及带内三层的相对次序。
/// </para>
/// <para>
/// <b>为什么单独写这个类：这些层写反了不会有任何东西变红。</b>批次7-06 落地时车辆侧只有四条用例
/// （<c>RouteGraphDispatchTests</c> 里从任务侧搬过来的那四条），覆盖的是成本层与按车号的兜底层。带内那两层
/// ——接单久远、电量——一条都没有，而它们的方向<b>一正一反</b>：接单久远取<b>早</b>的，电量取<b>高</b>的。
/// 电量层因此写成 <c>yBattery.CompareTo(xBattery)</c>，两个参数的位置是倒的，是这整张表里最容易写反的一行。
/// </para>
/// <para>
/// 每条方向判据都<b>正反两向各断一次</b>。只断一向的话，一个恒返回 -1 的实现能通过——它对任何两辆车都说
/// 「前一个先取」，那不是排序，是原样返回。
/// </para>
/// </remarks>
public sealed class DispatchVehicleOrderingTests
{
    private static readonly DateTimeOffset Origin = new(2026, 9, 21, 8, 0, 0, TimeSpan.Zero);

    // ---- 每一层自己的方向 ---------------------------------------------------------------------

    [Fact]
    public void TheBatteryLayerPutsTheFullerVehicleFirst()
    {
        // 电量层是整张表里唯一一个倒序的层，比较的两个参数位置是反着写的。正反两向都断，
        // 所以把那一行写成 xBattery.CompareTo(yBattery) 会红，写成恒定值也会红。
        VehicleBatteryLayer layer = new();
        EligibleVehicleOffer full = Offer("agv-full", batteryPercent: 90);
        EligibleVehicleOffer empty = Offer("agv-empty", batteryPercent: 20);

        Assert.True(layer.Compare(full, empty) < 0);
        Assert.True(layer.Compare(empty, full) > 0);
    }

    [Fact]
    public void TheBatteryLayerDoesNotJudgeAVehicleWhoseBatteryItCannotRead()
    {
        // 读不到电量的车在 VehicleDynamicFactsCriterion 那一关就该被挡下；走到排序说明那一关放行了，
        // 这一层不替它重判一次（少了这条，一个「读不到就排最后」的实现会悄悄变成第二道门）。
        VehicleBatteryLayer layer = new();
        EligibleVehicleOffer unknown = Offer("agv-unknown", batteryPercent: null);
        EligibleVehicleOffer known = Offer("agv-known", batteryPercent: 90);

        Assert.Equal(0, layer.Compare(unknown, known));
        Assert.Equal(0, layer.Compare(known, unknown));
    }

    [Fact]
    public void TheRecencyLayerPutsTheLongestWaitFirst()
    {
        // 接单久远层取「早」的，与电量层相反——两层挨着，方向却是反的，这正是要分别钉住的理由。
        LeastRecentlyDispatchedVehicleLayer layer = new();
        EligibleVehicleOffer waitedLong = Offer("agv-long", lastDispatchedAt: Origin.AddHours(-8));
        EligibleVehicleOffer justDispatched = Offer("agv-recent", lastDispatchedAt: Origin.AddMinutes(-1));

        Assert.True(layer.Compare(waitedLong, justDispatched) < 0);
        Assert.True(layer.Compare(justDispatched, waitedLong) > 0);
    }

    [Fact]
    public void AVehicleThatHasNeverTakenAJourneyGoesFirst()
    {
        // 一辆刚上线的车应当先得到机会，而不是因为「没有记录」被排到最后——空值在这里是「等了无限久」，
        // 不是「无从比较」。这两种读法的代价差得很远：按后者写，新车会被永远压在队尾。
        LeastRecentlyDispatchedVehicleLayer layer = new();
        EligibleVehicleOffer neverDispatched = Offer("agv-new", lastDispatchedAt: null);
        EligibleVehicleOffer dispatchedLongAgo = Offer("agv-old", lastDispatchedAt: Origin.AddDays(-30));

        Assert.True(layer.Compare(neverDispatched, dispatchedLongAgo) < 0);
        Assert.True(layer.Compare(dispatchedLongAgo, neverDispatched) > 0);
        Assert.Equal(0, layer.Compare(neverDispatched, Offer("agv-new-too", lastDispatchedAt: null)));
    }

    [Fact]
    public void TheZonePreferenceLayerSeparatesNothingInThisBatch()
    {
        // 仓里没有 DispatchZoneVehiclePreference 这个载体，所以本批这一层不区分任何两辆车（票面第 6 条允许
        // 「未配置即该层不区分」）。这条不是在证明一个空实现是对的——它是那个决定的护栏：哪天有人给这一层
        // 填了内容，它会红，提醒那个人回来核带内三层的次序还对不对，而不是让新行为无声地插到最前面。
        DispatchZoneVehiclePreferenceLayer layer = new();

        Assert.Equal(0, layer.Compare(
            Offer("agv-a", batteryPercent: 90, lastDispatchedAt: null),
            Offer("agv-b", batteryPercent: 10, lastDispatchedAt: Origin.AddDays(-30))));
    }

    // ---- 带内三层的相对次序 -------------------------------------------------------------------

    [Fact]
    public void TheLayerOrderIsTheOneTheSpecificationFixes()
    {
        // 成本在带内之前，带内是「偏好、接单久远、电量」，最后一层保证确定性。写成完整序列而不是数个数：
        // 一条 Assert.Equal(6, layers.Count) 对调换两层毫无反应。
        Assert.Equal(
            [
                typeof(PricedVehicleBeforeUnpricedLayer),
                typeof(MarginalTripCostLayer),
                typeof(DispatchZoneVehiclePreferenceLayer),
                typeof(LeastRecentlyDispatchedVehicleLayer),
                typeof(VehicleBatteryLayer),
                typeof(VehicleIdOrdinalLayer),
            ],
            DispatchVehicleOrdering.Layers().Select(layer => layer.GetType()));
    }

    [Fact]
    public void RecencyOutranksBattery()
    {
        // 上一条钉的是类型序列，改个类名或加一层就要回来改它；这一条钉的是后果，不认识任何类型名。
        // 两辆车成本相同，一辆电量满却刚接过单，一辆电量低却久未接单：规格把接单久远排在电量之前，
        // 所以该走的是后者。把注册表里那两行对调，这条红，而类型序列那条也红——两条一起红才说明
        // 「次序」这件事被两个互不依赖的判据看着。
        Assert.Equal(
            "agv-starved",
            DispatchVehicleOrdering.SelectNext(
            [
                Offer("agv-full", marginalCostMm: 10000, batteryPercent: 95, lastDispatchedAt: Origin.AddMinutes(-1)),
                Offer("agv-starved", marginalCostMm: 10000, batteryPercent: 25, lastDispatchedAt: Origin.AddHours(-8)),
            ]).Vehicle.AgvId);
    }

    [Fact]
    public void CostOutranksEverythingInsideTheBand()
    {
        // 反过来的一半：成本不同时，带内三层一个都不该说话。少了这条，一个把成本层挪到带内之后的实现
        // 只会让上面那条更容易过，不会让任何东西变红。
        Assert.Equal(
            "agv-cheap",
            DispatchVehicleOrdering.SelectNext(
            [
                Offer("agv-cheap", marginalCostMm: 10000, batteryPercent: 25, lastDispatchedAt: Origin.AddMinutes(-1)),
                Offer("agv-dear", marginalCostMm: 90000, batteryPercent: 95, lastDispatchedAt: Origin.AddHours(-8)),
            ]).Vehicle.AgvId);
    }

    // ---- 「上次接单」这个事实的产地 -----------------------------------------------------------

    [Fact]
    public void TheMostRecentJourneyIsTheOneThatCounts()
    {
        // 语料里最大的那一行故意放在中间：放末尾的话一个 Last() 实现也能过，放开头 First() 能过，
        // 而这一批里最像的写错是把 Max 写成 Min——所以还要断它不等于最小的那个。
        DateTimeOffset newest = Origin.AddHours(-1);
        Dictionary<string, DateTimeOffset> last = DispatchRoundRunner.LastDispatchAtByVehicle(
        [
            new DispatchRoundRunner.JourneyStart("agv-02", Origin.AddDays(-9)),
            new DispatchRoundRunner.JourneyStart("agv-02", newest),
            new DispatchRoundRunner.JourneyStart("agv-02", Origin.AddDays(-3)),
            new DispatchRoundRunner.JourneyStart("agv-03", Origin.AddDays(-30)),
        ]);

        Assert.Equal(newest, last["agv-02"]);
        Assert.NotEqual(Origin.AddDays(-9), last["agv-02"]);
        Assert.Equal(Origin.AddDays(-30), last["agv-03"]);
    }

    [Fact]
    public void AVehicleWithNoJourneyIsAbsentRatherThanCarryingADefaultTime()
    {
        // 轮次拿 TryGetValue 把「不在」读成 null，而 null 在接单久远那一层的意思是「等了无限久、排最前」。
        // 给每辆车都填一个默认时刻也能让代码跑起来，但那个默认值是 0001 年，同样排最前——
        // 两种写法在只有一辆新车时看不出差别，车一多就分道扬镳。这条钉的是「不在」本身。
        Assert.False(
            DispatchRoundRunner.LastDispatchAtByVehicle([new DispatchRoundRunner.JourneyStart("agv-02", Origin)])
                .ContainsKey("agv-03"));
    }

    // ---- 一辆车对一条任务的出价，只填排序层读的那几样 -----------------------------------------

    private static EligibleVehicleOffer Offer(
        string agvId,
        long? marginalCostMm = 10000,
        int? batteryPercent = 50,
        DateTimeOffset? lastDispatchedAt = null) => new(
        new FleetVehicle(agvId, $"VK-{agvId}", 1),
        new DispatchVehicleFacts(
            $"VK-{agvId}",
            agvId,
            new OnboardDispatchFacts(1, [1], true, true, true, true, false),
            new RiotVehicleObservation(
                $"VK-{agvId}", true, true, "IDLE", "MAP-25-WIRE_TO_GATE", 12, batteryPercent, "DISCHARGING", 0, Origin),
            Origin),
        Candidate(marginalCostMm),
        marginalCostMm,
        Placement: null,
        DispatchZoneParameterVersion: null,
        LastDispatchedAt: lastDispatchedAt);

    private static EligibleDispatchCandidate Candidate(long? cost) => new(
        new AcceptedDemandSnapshot(
            "DEMAND-1",
            TransportDemandKey: "DEMAND-1",
            DemandRevision: 0,
            HistoryEpoch: "EPOCH",
            CatalogRevision: 0,
            AcceptedAt: Origin,
            SeriesId: "SERIES",
            WorkType: "WIRE_TO_GATE",
            Sublot: "SUBLOT",
            CreatedAt: Origin),
        new ResolvedJourneyRoute(
            "ZONE", "EVIDENCE", "N1-3_N1-7", 12, "关卡", 210,
            FixedTaskStationResolution.Resolved("WIRE_TO_GATE", FixedStationEnd.Destination, new RiotMapStation(210, "关卡"))),
        1,
        [1],
        Origin,
        cost);
}
