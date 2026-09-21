using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime.Dispatch;
using ControlServer.Host.Runtime.Dispatch.Criteria;

namespace ControlServer.Host.Runtime.Release;

/// <summary>
/// REQ-0328 的两个判断：这辆车对这条需求还合不合格（触发），这条需求此刻能不能释放（裁决）（批次7-10，control-server#215）。
/// </summary>
/// <remarks>
/// <para>
/// <b>两者都是纯函数</b>，读库、问 RIoT、取消订单都在释放服务里；这里只回答问题，好让每一条规则单独有用例。
/// </para>
/// <para>
/// <b>触发只取稳定的车辆级事实</b>：故障阻断、车辆任务类型准入被收回、分区车辆准入被收回、车辆离开本图。车载端事实没就绪、
/// RIoT 事实过期、电量不够这类会自己恢复的原因<b>不</b>触发——拿它们释放，车一抖需求就被抢走，下一轮车好了又派不回来。
/// 「本区一辆车都没配」也不触发：那是分区的事实，不是这辆车的，需求换哪辆车都一样不合格（票面「该需求本身仍合格」）。
/// </para>
/// </remarks>
public static class DemandReleaseRules
{
    /// <summary>这辆车对这条需求不再合格的理由；仍合格或说不清时为空——说不清不是释放的理由。</summary>
    /// <param name="observation">RIoT 上这辆车最近一次观测；读不到为空。只有新鲜的观测才能说「离开了本图」。</param>
    /// <remarks>
    /// <b>入口的门：观测为空或车不在线时，任何判据都不判「不再合格」</b>（审查 S1）。失败的读取有两种形状——抛异常
    /// （释放服务接住后传空），以及 <c>HttpRiotMovementGateway</c> 不抛、返回的 <c>UnknownVehicle</c>（离线、地图为空、
    /// 观测时刻为此刻，按新鲜度它是新鲜的）。门挡在所有判据之前，所以「失败读取不能触发释放」不靠每条判据各自记着，
    /// 以后新加的判据也在门后。代价是 RIoT 读不到期间，连已确认隔离的车也不释放需求：那是活性，安全方向。
    /// </remarks>
    public static string? VehicleNoLongerEligible(
        VehicleFaultFact? fault,
        VehicleDispatchPolicy policy,
        string agvId,
        string taskType,
        string dispatchZone,
        RiotVehicleObservation? observation,
        DateTimeOffset now,
        JourneyRuntimeOptions options)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(options);
        if (observation is not { Connected: true })
        {
            return null;
        }

        switch (fault?.Level)
        {
            case VehicleFaultLevel.ConfirmedIsolated:
                return VehicleFaultBlockCriterion.IsolatedReason;
            case VehicleFaultLevel.SuspectedBlocked:
                return VehicleFaultBlockCriterion.SuspectedReason;
        }

        string taskTypeVerdict = VehicleTaskTypeAdmissionCriterion.Evaluate(policy, agvId, taskType);
        if (!string.Equals(taskTypeVerdict, DispatchAdmissionChain.Eligible, StringComparison.Ordinal))
        {
            return taskTypeVerdict;
        }

        if (policy.ZoneVehicles.TryGetValue(dispatchZone, out IReadOnlySet<string>? zoneVehicles) &&
            !zoneVehicles.Contains(agvId))
        {
            return DispatchZoneVehicleCriterion.VehicleNotInZoneReason;
        }

        // 与在途判据同一个比较（InTransitVehicleFactsCriterion），只是多一道新鲜度：过期的观测说的是过去的地图。
        if (!string.IsNullOrEmpty(observation.CurrentMap) &&
            observation.ObservedAt <= now &&
            now - observation.ObservedAt <= options.MaximumEvidenceAge &&
            !string.Equals(observation.CurrentMap, options.MapIdentity, StringComparison.Ordinal))
        {
            return MapMismatchReason;
        }

        return null;
    }

    /// <summary>与 <see cref="InTransitVehicleFactsCriterion"/> 同一个码，那里是字面量。</summary>
    public const string MapMismatchReason = "RIOT_VEHICLE_MAP_MISMATCH";

    /// <summary>这条需求此刻能不能从这辆车上释放。</summary>
    /// <param name="membershipStatus">它在这趟旅程里的归属状态；只有待装的（集合 B、未取货）走本条。</param>
    /// <param name="isAnchor">它是不是旅程行上的锚需求。</param>
    /// <param name="otherOpenDemands">这趟旅程上除它之外还没终结的需求有几条。</param>
    /// <param name="pickupIsCurrentNextStop">它的取货停靠是不是当前下一站。</param>
    /// <param name="arrivedAtCurrentNextStop">车是否已经到了当前下一站（到站已记录）。</param>
    public static DemandReleaseDecision Decide(
        string membershipStatus,
        bool isAnchor,
        int otherOpenDemands,
        bool pickupIsCurrentNextStop,
        bool arrivedAtCurrentNextStop)
    {
        // 集合 A（装着的、正在装的）与已经结束的都不走本条（票面第 4 条）：它们留在原车，由既有阻断与恢复路径处理。
        if (!string.Equals(membershipStatus, JourneyDemandStatuses.PendingLoad, StringComparison.Ordinal))
        {
            return DemandReleaseDecision.NotApplicable;
        }

        if (pickupIsCurrentNextStop)
        {
            // 到站之后清单已经发给车，删掉当前下一站会让下一个停靠算出同一个清单号、发不同的内容（调度决策 8）。
            if (arrivedAtCurrentNextStop)
            {
                return DemandReleaseDecision.Refuse(DemandReleaseReasons.AfterArrival);
            }

            // 车正开往它的取货站，旅程上还有别的需求：取消这张单之后得有人把车重新派往下一站，那是引擎的活，本票不做。
            if (otherOpenDemands > 0)
            {
                return DemandReleaseDecision.Refuse(DemandReleaseReasons.CurrentStopWithOtherDemands);
            }

            return DemandReleaseDecision.CancelPickupOrderThenRelease;
        }

        // 调度决策 4：锚需求只在它是最后一条未结需求时才释放。走到这里的锚需求取货停靠不是当前下一站，而锚的取货停靠
        // 是旅程的第一站，所以它要么还在前面（上面那一支）、要么已经走过——这一支今天走不到，留着是为了规则不依赖这个推理。
        if (isAnchor && otherOpenDemands > 0)
        {
            return DemandReleaseDecision.Refuse(DemandReleaseReasons.AnchorWithOtherDemands);
        }

        return DemandReleaseDecision.ReleaseWithoutOrder;
    }
}

/// <summary>释放服务对一条需求的裁决。</summary>
public sealed record DemandReleaseDecision(DemandReleaseAction Action, string? RefusalReason)
{
    public static DemandReleaseDecision NotApplicable { get; } = new(DemandReleaseAction.NotApplicable, null);

    public static DemandReleaseDecision ReleaseWithoutOrder { get; } = new(DemandReleaseAction.ReleaseWithoutOrder, null);

    public static DemandReleaseDecision CancelPickupOrderThenRelease { get; } =
        new(DemandReleaseAction.CancelPickupOrderThenRelease, null);

    public static DemandReleaseDecision Refuse(string reason) => new(DemandReleaseAction.Refuse, reason);
}

public enum DemandReleaseAction
{
    /// <summary>不走本条：不是待装的需求。</summary>
    NotApplicable,

    /// <summary>取货停靠不是当前下一站：它没有 RIoT 订单（追加的停靠到车离开上一站才建单），写事务里核实后直接释放。</summary>
    ReleaseWithoutOrder,

    /// <summary>车正开往它的取货站、旅程上只剩它：先经订单命令面取消那张单并对账，确认之后释放并关闭旅程。</summary>
    CancelPickupOrderThenRelease,

    /// <summary>不释放，需求与车保持原状，写下 <see cref="DemandReleaseDecision.RefusalReason"/>。</summary>
    Refuse,
}

/// <summary>
/// 释放服务写下的原因码。都是服务端内部的阻断原因，不进协议报文——协议的 <c>ErrorCode</c> 枚举与它们无关。
/// </summary>
public static class DemandReleaseReasons
{
    /// <summary>归属行上的移除原因与积压行上的原因：这条需求被释放、等着改派。与 <see cref="Infrastructure.Persistence.DemandJourneyLookup.ReleasedForRedispatchReason"/> 同值。</summary>
    public const string Released = Infrastructure.Persistence.DemandJourneyLookup.ReleasedForRedispatchReason;

    /// <summary>车已到当前下一站，不释放（调度决策 8）。</summary>
    public const string AfterArrival = "RELEASE_AFTER_ARRIVAL";

    /// <summary>当前下一站是它的取货站、旅程上还有别的需求，不释放（本票限度）。</summary>
    public const string CurrentStopWithOtherDemands = "RELEASE_CURRENT_STOP_WITH_OTHER_DEMANDS";

    /// <summary>锚需求、旅程上还有别的未结需求，不释放（调度决策 4）。</summary>
    public const string AnchorWithOtherDemands = "RELEASE_ANCHOR_WITH_OTHER_DEMANDS";

    /// <summary>取货单的取消没有确认（Pending、Failed 或 Unknown），不释放（REQ-0328：结果未知不释放）。</summary>
    public const string OrderCancelNotConfirmed = "RELEASE_ORDER_CANCEL_NOT_CONFIRMED";

    /// <summary>
    /// 开往当前下一站的取货单处在「创建已发出、结果未知」一类状态（意图上还没有订单号，而 RIoT 上可能已经有一张活的），
    /// 不释放（REQ-0328：结果未知不释放）。引擎把意图对账出结论之后下一轮再判。
    /// </summary>
    public const string OrderStateUnknown = "RELEASE_ORDER_STATE_UNKNOWN";

    /// <summary>
    /// 释放被拒时可能写在旅程阻断码上的那几个码（审查 M4）。只有这几个会被释放服务改写或清掉；引擎自己的码一个都不碰。
    /// <see cref="AfterArrival"/> 在内只为清掉旧版本写下的残留——现在它不再写：到站之后不释放是正常作业，不是阻断。
    /// </summary>
    public static bool IsRefusalCode(string? code) => code is
        AfterArrival or CurrentStopWithOtherDemands or AnchorWithOtherDemands or OrderCancelNotConfirmed or
        OrderStateUnknown or PickupOrderAppeared;

    /// <summary>写事务里发现取货停靠已经有了 RIoT 订单意图，与轮次开头读到的不同，这一轮不释放。</summary>
    public const string PickupOrderAppeared = "RELEASE_PICKUP_ORDER_APPEARED";
}
