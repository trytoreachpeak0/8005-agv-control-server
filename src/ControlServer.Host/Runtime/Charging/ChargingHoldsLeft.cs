using System.Globalization;
using ControlServer.Application;

namespace ControlServer.Host.Runtime.Charging;

/// <summary>
/// 一侧恢复之后点名另一侧还挂着的暂停（control-server#877）。<c>REQ-0286</c> 让桩与车各凭各的证据恢复：清桩不解车的资格暂停，解车不放桩的分配暂停。
/// 这一点不改；这里只把「另一侧还挂着哪一条、为什么、何时写下、从哪个入口解除」说出来，免得现场以为一次恢复就收拾完了（11-03 清桩后 agv02 的资格暂停
/// 一直挂着，耗到断电；11-04 解了车，230 的暂停漏到 11-05）。
/// </summary>
public static class ChargingHoldsLeft
{
    /// <summary>车的一条资格暂停，作为接口结果里的一项。</summary>
    public static ChargingHoldLeft Of(VehicleChargingEligibilityHold hold, string agvId)
    {
        ArgumentNullException.ThrowIfNull(hold);
        return new ChargingHoldLeft(
            hold.HoldId, hold.Reason, hold.HeldAt, hold.CycleId, agvId, null, null, ChargingStationEndpoints.VehicleRecoveryRoute);
    }

    /// <summary>桩的一条分配暂停，作为接口结果里的一项。</summary>
    public static ChargingHoldLeft Of(ChargingStationAllocationHold hold)
    {
        ArgumentNullException.ThrowIfNull(hold);
        return new ChargingHoldLeft(
            hold.HoldId, hold.Trigger, hold.HeldAt, hold.CycleId, null, hold.MapId, hold.StationId, ChargingStationEndpoints.RecoveryRoute);
    }

    /// <summary>日志与分配结论里的一条：<c>hold &lt;id&gt; (&lt;reason&gt;, written &lt;heldAt:O&gt;)</c>。</summary>
    public static string Describe(string holdId, string reason, DateTimeOffset heldAt) =>
        string.Create(CultureInfo.InvariantCulture, $"hold {holdId} ({reason}, written {heldAt:O})");

    /// <summary>
    /// <c>CHARGING_VEHICLE_ELIGIBILITY_HELD</c> 的细节：挡住它的每一条暂停与解除入口。同一组暂停每轮得出同一句，分配板按它判「结论没变」。
    /// </summary>
    public static string DescribeEligibilityHeld(IReadOnlyList<VehicleChargingEligibilityHold> holds)
    {
        ArgumentNullException.ThrowIfNull(holds);
        return "Charging eligibility paused by " +
               string.Join("; ", holds.Select(hold => Describe(hold.HoldId, hold.Reason, hold.HeldAt))) +
               $". No charger is allocated to it until each is recovered with POST {ChargingStationEndpoints.VehicleRecoveryRoute} " +
               "(a charger clearance or recovery does not lift it, REQ-0286).";
    }

    /// <summary>清桩之后车还挂着资格暂停时给人看的一句话；没有就为空。</summary>
    public static string? VehicleNotice(string agvId, IReadOnlyList<ChargingHoldLeft> left)
    {
        ArgumentNullException.ThrowIfNull(left);
        if (left.Count == 0)
        {
            return null;
        }
        string holds = string.Join("、", left.Select(hold => string.Create(
            CultureInfo.InvariantCulture, $"{hold.HoldId}（原因 {hold.Reason}，写入于 {hold.HeldAt:O}）")));
        return $"车 {agvId} 还挂着充电资格保持 {holds}。清桩不会解除它，需单独解除：查明原因后经车辆充电资格恢复入口 " +
               $"POST {ChargingStationEndpoints.VehicleRecoveryRoute}；不解除的话，这辆车电量低于强制充电线时分不到任何充电桩。";
    }

    /// <summary>解车之后同一次中断的桩还暂停着时给人看的一句话；没有就为空。</summary>
    public static string? StationNotice(IReadOnlyList<ChargingHoldLeft> left)
    {
        ArgumentNullException.ThrowIfNull(left);
        if (left.Count == 0)
        {
            return null;
        }
        string holds = string.Join("、", left.Select(hold => string.Create(
            CultureInfo.InvariantCulture,
            $"充电桩 {hold.MapId}/{hold.StationId} 的分配暂停 {hold.HoldId}（原因 {hold.Reason}，写入于 {hold.HeldAt:O}）")));
        return $"同一次中断留下的 {holds} 还在。解除车的充电资格不会放开充电桩，需单独解除：桩查过后经充电桩恢复确认入口 " +
               $"POST {ChargingStationEndpoints.RecoveryRoute}；不解除的话，这个桩不再分给任何车。";
    }
}
