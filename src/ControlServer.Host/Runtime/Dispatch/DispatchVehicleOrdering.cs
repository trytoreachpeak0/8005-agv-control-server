using ControlServer.Application;

namespace ControlServer.Host.Runtime.Dispatch;

/// <summary>
/// 车辆侧排序的一层：为<b>同一条任务</b>比较两辆车的出价，只回答一个问题，别的都算平手
/// （批次7-06，control-server#211）。
/// </summary>
/// <remarks>
/// <para>
/// 与 <see cref="IDispatchCandidateComparisonLayer"/>（任务侧）是两条互不相交的链，因为派车轮翻成了任务优先
/// （REQ-0200：先定任务、再只为该任务选车）。任务侧回答「先派哪一条需求」——批次7-09（control-server#214）
/// 的优先级带与等待年龄加在那一侧；车辆侧回答「这一条派给哪辆车」——本票的成本层与带内层加在这一侧。
/// 两侧分开，是因为 7-09 与本票各自往里加层时不会碰到同一个表达式。
/// </para>
/// <para>
/// 一层分不出高下就返回 0，下一层接着判——与任务侧同一套规矩。
/// </para>
/// </remarks>
public interface IDispatchVehicleComparisonLayer
{
    /// <summary><paramref name="x"/> 先取为负，<paramref name="y"/> 先取为正，分不出为 0。</summary>
    int Compare(EligibleVehicleOffer x, EligibleVehicleOffer y);
}

/// <summary>算得出边际成本的车排在算不出的前面。</summary>
/// <remarks>
/// REQ-0207 的同一条道理搬到车辆侧：可达性是门，成本是序。算不出成本的车不被淘汰，只是排在后面——
/// 而当一辆都算不出时，这一层与成本层都判平手，由带内各层接着分。
/// </remarks>
public sealed class PricedVehicleBeforeUnpricedLayer : IDispatchVehicleComparisonLayer
{
    public int Compare(EligibleVehicleOffer x, EligibleVehicleOffer y)
    {
        ArgumentNullException.ThrowIfNull(x);
        ArgumentNullException.ThrowIfNull(y);
        return (x.MarginalCostMm.HasValue, y.MarginalCostMm.HasValue) switch
        {
            (true, false) => -1,
            (false, true) => 1,
            _ => 0,
        };
    }
}

/// <summary>
/// 边际成本低的车先取（REQ-0206）：加入这条需求之后，相对它原计划增加的行程代价。
/// </summary>
/// <remarks>
/// <para>
/// <b>用计划锚，自插入位的前一站起算，是精确值不是近似</b>（规格第 5.2 节）。在途车的增量由
/// <see cref="EnRouteAppendPlanner"/> 在选插入位时一并算出；空闲车的原计划是空的，增量就是从它当前位置
/// 走完这一趟的全程。所以两者是同一个量纲的两个值，可以直接比大小，<b>空闲或在途的身份本身不产生优先级</b>。
/// </para>
/// <para>
/// <b>容差未批准，按零容差</b>（规格第 5.2 节）：只有成本<b>完全相同</b>才落到带内各层。一个「差不多就算平手」
/// 的容差会让带内层在成本差几毫米时就接管排序，而那个容差到底多大从来没有人批准过。
/// </para>
/// </remarks>
public sealed class MarginalTripCostLayer : IDispatchVehicleComparisonLayer
{
    public int Compare(EligibleVehicleOffer x, EligibleVehicleOffer y)
    {
        ArgumentNullException.ThrowIfNull(x);
        ArgumentNullException.ThrowIfNull(y);
        return x.MarginalCostMm is long xCost && y.MarginalCostMm is long yCost
            ? xCost.CompareTo(yCost)
            : 0;
    }
}

/// <summary>
/// 带内第一层：分区对车辆的偏好（<c>DispatchZoneVehiclePreference</c>）。
/// </summary>
/// <remarks>
/// <b>仓里没有这个载体，所以这一层此刻不区分任何两辆车</b>（票面第 6 条允许「未配置即该层不区分」）。写成一个
/// 恒返回 0 的层而不是不写，是因为带内的次序是规格定的：偏好在前、接单久远在中、电量在后。少写一层，
/// 下一个人补上它时要重新论证它该插在哪；写成空层，那个位置已经在这里了。
/// </remarks>
public sealed class DispatchZoneVehiclePreferenceLayer : IDispatchVehicleComparisonLayer
{
    public int Compare(EligibleVehicleOffer x, EligibleVehicleOffer y)
    {
        ArgumentNullException.ThrowIfNull(x);
        ArgumentNullException.ThrowIfNull(y);
        return 0;
    }
}

/// <summary>
/// 带内第二层：从未成功接单的车先取，其次是距上次成功接单最久的那一辆。
/// </summary>
/// <remarks>
/// <b>从既有旅程记录推出，没有新的列。</b>「上次成功接单」就是这辆车最近一趟旅程的受理时刻，由轮次在开始时
/// 一次查出（<c>JourneyRuntimes</c> 按车取 <c>CreatedAt</c> 的最大值）。从未接过单的车没有这个时刻，排最前——
/// 一辆刚上线的车应当先得到机会，而不是因为「没有记录」被排到最后。
/// </remarks>
public sealed class LeastRecentlyDispatchedVehicleLayer : IDispatchVehicleComparisonLayer
{
    public int Compare(EligibleVehicleOffer x, EligibleVehicleOffer y)
    {
        ArgumentNullException.ThrowIfNull(x);
        ArgumentNullException.ThrowIfNull(y);
        return (x.LastDispatchedAt, y.LastDispatchedAt) switch
        {
            (null, null) => 0,
            (null, not null) => -1,
            (not null, null) => 1,
            ({ } xAt, { } yAt) => xAt.CompareTo(yAt),
        };
    }
}

/// <summary>
/// 带内第三层：剩余电量高的车先取。
/// </summary>
/// <remarks>
/// <b>本批用当前电量裁决</b>（票面第 6 条）。<c>REQ-0208</c> 的电量半边——把充电计划、续航估计一起算进来——
/// 在规格第 19.5 节，出口报告按那里的口径写明本批实现到哪一步。读不到电量的车在这一层不参与比较：
/// 它在 <see cref="Criteria.VehicleDynamicFactsCriterion"/> 那一关就该被挡下，走到这里说明那一关放行了，
/// 这一层不替它重判一次。
/// </remarks>
public sealed class VehicleBatteryLayer : IDispatchVehicleComparisonLayer
{
    public int Compare(EligibleVehicleOffer x, EligibleVehicleOffer y)
    {
        ArgumentNullException.ThrowIfNull(x);
        ArgumentNullException.ThrowIfNull(y);
        return x.Facts.Vehicle.BatteryPercent is { } xBattery && y.Facts.Vehicle.BatteryPercent is { } yBattery
            ? yBattery.CompareTo(xBattery)
            : 0;
    }
}

/// <summary>最后一层：按 <c>agvId</c> 定序，让「每一层都分不出」时的结果仍是确定的。</summary>
public sealed class VehicleIdOrdinalLayer : IDispatchVehicleComparisonLayer
{
    public int Compare(EligibleVehicleOffer x, EligibleVehicleOffer y)
    {
        ArgumentNullException.ThrowIfNull(x);
        ArgumentNullException.ThrowIfNull(y);
        return string.CompareOrdinal(x.Vehicle.AgvId, y.Vehicle.AgvId);
    }
}

/// <summary>车辆侧排序的注册表——它的层，按被问到的顺序。</summary>
/// <remarks>
/// <b>加一层是一个文件加这里一行。</b>成本层在带内各层之前，带内三层的次序是规格定的（偏好、接单久远、电量），
/// 最后一层保证确定性。批次7-09（control-server#214）的优先级带与等待年龄加在<b>任务侧</b>，与这张表不相交。
/// </remarks>
public static class DispatchVehicleOrdering
{
    /// <summary>每一层，按被问到的顺序。</summary>
    public static IReadOnlyList<IDispatchVehicleComparisonLayer> Layers() =>
    [
        new PricedVehicleBeforeUnpricedLayer(),
        new MarginalTripCostLayer(),
        new DispatchZoneVehiclePreferenceLayer(),
        new LeastRecentlyDispatchedVehicleLayer(),
        new VehicleBatteryLayer(),
        new VehicleIdOrdinalLayer(),
    ];

    /// <summary>这几层里排第一的那辆车。</summary>
    public static EligibleVehicleOffer SelectNext(IReadOnlyList<EligibleVehicleOffer> offers)
    {
        ArgumentNullException.ThrowIfNull(offers);
        if (offers.Count == 0)
        {
            throw new ArgumentException("The vehicle ranking is only called with at least one offer.", nameof(offers));
        }

        IReadOnlyList<IDispatchVehicleComparisonLayer> layers = Layers();
        EligibleVehicleOffer selected = offers[0];
        for (int index = 1; index < offers.Count; index++)
        {
            if (Compare(layers, offers[index], selected) < 0)
            {
                selected = offers[index];
            }
        }

        return selected;
    }

    private static int Compare(
        IReadOnlyList<IDispatchVehicleComparisonLayer> layers,
        EligibleVehicleOffer x,
        EligibleVehicleOffer y)
    {
        foreach (IDispatchVehicleComparisonLayer layer in layers)
        {
            int order = layer.Compare(x, y);
            if (order != 0)
            {
                return order;
            }
        }

        return 0;
    }
}
