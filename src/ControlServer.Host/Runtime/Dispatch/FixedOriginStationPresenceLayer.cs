namespace ControlServer.Host.Runtime.Dispatch;

/// <summary>
/// 选车软层（<c>REQ-0204</c> 修订，批次8-20，control-server#391）：货物的取货起点就是公共站点时，已经在点的车先取。
/// </summary>
/// <remarks>
/// <para>
/// <b>只在取货起点是公共站点时给</b>——今天只有 <c>STAGING_TO_WIRE</c>（固定站在起点，<see cref="FixedStationEnd.Origin"/>）。
/// 公共点只是任务终点时（<c>WIRE_TO_GATE</c> 的关卡）不适用：停在关卡的车对一条从机台取货的需求没有任何先手。
/// 货物不会因为车在哪而换取货点，这一层只挑车，不改路线。
/// </para>
/// <para>
/// <b>「通过全部硬准入」不在这里判</b>：进到车辆侧排序的出价都已经清了整条资格链，不合格的车根本不在这里比较，
/// 「该车不合格时自然比较其他车辆」由此成立。
/// </para>
/// <para>
/// <b>「在点」读 RIoT 观测的当前站</b>，与派车轮读车辆位置是同一份事实（<see cref="DispatchVehicleFacts.Vehicle"/>）。
/// </para>
/// <para>
/// <b>排在最前、成本层之前。</b>规格给它的是「一个选车软层」，而放在成本层之后它几乎不起作用：成本精确比较、零容差
/// （<see cref="MarginalTripCostLayer"/>），在点的空闲车与另一辆车成本恰好相等的情形几乎不会出现，这一层就成了摆设。
/// 在点的车走完这一趟的全程通常本来就最短，放在前面只在一种情形下改变结果——在途车插入的边际成本比在点的空闲车全程还小，
/// 那时让已经占着站点的车接，站点就不会被第二辆车挤着去。
/// </para>
/// </remarks>
public sealed class FixedOriginStationPresenceLayer : IDispatchVehicleComparisonLayer
{
    public int Compare(EligibleVehicleOffer x, EligibleVehicleOffer y)
    {
        ArgumentNullException.ThrowIfNull(x);
        ArgumentNullException.ThrowIfNull(y);
        return (StandsAtFixedOrigin(x), StandsAtFixedOrigin(y)) switch
        {
            (true, false) => -1,
            (false, true) => 1,
            _ => 0,
        };
    }

    /// <summary>这份出价的取货起点是公共站点，而这辆车此刻就在那里。</summary>
    /// <remarks>
    /// 按需求原文判「取货起点就是该公共站点」，不另判固定站在哪一端：固定站在终点（<c>WIRE_TO_GATE</c>）时它就是卸货站，
    /// 不会等于取货站，另判一次端点只是同一件事的第二种说法（注入变异 M8 证实它删掉也没有用例会红）。
    /// </remarks>
    internal static bool StandsAtFixedOrigin(EligibleVehicleOffer offer)
    {
        ResolvedJourneyRoute route = offer.Candidate.Route;
        return route.FixedStation.Station?.StationId == route.PickupStationRiotId &&
               offer.Facts.Vehicle.CurrentStationId == route.PickupStationRiotId;
    }
}
