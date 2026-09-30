using ControlServer.Application;

namespace ControlServer.Host.Runtime.Dispatch.Criteria;

/// <summary>
/// 一辆车做决定时所依据的那一版充电策略（批次9-05，control-server#403；<c>REQ-0281</c>、<c>REQ-0282</c>）：版本号与三个阈值、每趟耗电估计。
/// </summary>
/// <remarks>
/// 空闲车取「此刻为这辆车做新决定用的版本」（<see cref="IChargingPolicyResolver.ResolveForNewDecisionAsync"/>），在途车取它那趟旅程
/// 派出时记下的版本（<see cref="IChargingPolicyResolver.ReadFrozenAsync"/>）。一轮里每辆车只读一次，放进
/// <see cref="DispatchVehicleFacts.BatteryPolicy"/>：这一轮对它的每条候选、受理前的复查、记到旅程上的版本号用的都是这一份。
/// </remarks>
public sealed record DispatchBatteryPolicy(long Version, ChargingPolicyContent Content)
{
    public static DispatchBatteryPolicy? From(VehicleChargingPolicyDecision? decision) =>
        decision?.Effective is { } effective ? new DispatchBatteryPolicy(effective.Policy.Version, effective.Policy.Content) : null;

    public static DispatchBatteryPolicy From(ChargingPolicyVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return new DispatchBatteryPolicy(version.Version, version.Content);
    }
}

/// <summary>
/// 电量资格、强制充电判定与 <c>batteryState</c> 投影（批次9-05，control-server#403）。三个阈值的每一处比较都在这里，
/// 派车两条链、空闲返回（<c>PolicyMandatoryChargeLine</c>）、下发的车辆业务状态与批次9-06 的充电分配都调这里，不另写。
/// </summary>
/// <remarks>
/// <para>
/// <b>三个阈值判定时点不同</b>（<c>REQ-0281</c>）：<c>MandatoryChargeEntryThreshold</c> 比的是<b>当前</b>电量；最低任务后电量余量比的是
/// <b>预计任务完成后</b>的电量，即当前电量减去每趟耗电估计乘以要覆盖的趟数；<c>ChargingCompletionThreshold</c> 只属于充电周期（批次9-07），
/// 这里不读。
/// </para>
/// <para>
/// <b>没有任何缺省值。</b>策略缺失（没有已批准版本、读不到）一律 fail-closed：不派，投影为 <c>UNKNOWN</c>。
/// </para>
/// </remarks>
public static class BatteryEligibility
{
    /// <summary>车在充电中：本票保留「充电中一律不派」这一支（放开由批次9-07 按充电周期状态做）。</summary>
    public const string ChargingBatteryState = "CHARGING";

    /// <summary>
    /// 这个电量是否已到强制充电（<c>REQ-0290</c>）：低于 <c>MandatoryChargeEntryThreshold</c> 即是。等于线不算——与此前
    /// <c>MinimumBatteryPercent</c>「低于即拒、等于放行」同一个比较。派车、空闲返回、充电分配问的都是这一个函数。
    /// </summary>
    public static bool IsMandatoryCharge(int batteryPercent, ChargingPolicyContent policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return batteryPercent < policy.MandatoryChargeEntryThresholdPercent;
    }

    /// <summary>
    /// 这一版策略的强制充电线是否不高于服务端的救命告警线（control-server#403）：是则整版不可用。派车电量一段（按本轮读定的那一份）与
    /// 共用投运判定（<see cref="VehicleNewPurposeReadiness.CommissioningVerdictAsync"/>）都问这一个函数。
    /// </summary>
    public static bool EntryNotAboveRescueLine(ChargingPolicyContent policy, int rescueBatteryPercent)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return policy.MandatoryChargeEntryThresholdPercent <= rescueBatteryPercent;
    }

    /// <summary>
    /// 做完 <paramref name="tasksToCover"/> 趟之后预计的电量是否仍不低于最低任务后电量余量（<c>REQ-0208</c>、<c>REQ-0281</c>）。
    /// </summary>
    public static bool KeepsMarginAfter(int batteryPercent, ChargingPolicyContent policy, int tasksToCover)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentOutOfRangeException.ThrowIfLessThan(tasksToCover, 1);
        long after = (long)batteryPercent - (long)policy.EstimatedTaskConsumptionPercent * tasksToCover;
        return after >= policy.MinimumPostTaskBatteryMarginPercent;
    }

    /// <summary>
    /// 在桩上充电的车能不能接活离桩：批次9-07 按充电周期状态放开（充满后在桩上接活）。本票不放开，恒为否——
    /// 这一支的判定入口就是这里，9-07 只换这个函数的实现。
    /// </summary>
    public static bool ChargingVehicleMayTakeWork(RiotVehicleObservation vehicle)
    {
        ArgumentNullException.ThrowIfNull(vehicle);
        return false;
    }

    /// <summary>
    /// 派车资格的电量一段（<c>REQ-0208</c> 电量那一半、<c>REQ-0281</c>、<c>REQ-0290</c>）。能接答
    /// <see cref="DispatchAdmissionChain.Eligible"/>；否则答第一条不满足的原因码，依次是：
    /// 没有策略 <see cref="DispatchReasonCodes.ChargingPolicyNotApproved"/>；电量或电池状态缺失 <c>BATTERY_FACT_UNKNOWN</c>；
    /// 充电中 <c>BATTERY_POLICY_NOT_SATISFIED</c>；当前电量低于强制充电线 <see cref="DispatchReasonCodes.MandatoryChargeRequired"/>；
    /// 预计任务后保不住余量 <c>BATTERY_POLICY_NOT_SATISFIED</c>。
    /// </summary>
    /// <param name="tasksToCover">
    /// 要按每趟耗电估计覆盖的趟数：空闲车接一条是 1；在途车追加时是追加后整趟还没卸完的需求数（<see cref="TasksToCoverAfterAppend"/>）。
    /// </param>
    /// <remarks>
    /// 读数过期不在这里判：两条链在它前面都有 <c>RIOT_VEHICLE_FACT_STALE</c>（观测时刻整体过期），过期的电量走不到这一段。
    /// 那一条的码与位置本票不动，既有判定因此逐条不变。
    /// </remarks>
    /// <param name="rescueBatteryPercent">
    /// 服务端的救命告警线。本轮读定的这一版若强制充电线不高于它，答 <see cref="DispatchReasonCodes.ChargingPolicyEntryNotAboveRescueLine"/>
    /// （审查 S1：投运判定另读一次解析器，两次读之间可能换了版本；派车只认本轮读定、会记到旅程上的这一份）。
    /// </param>
    public static string Judge(RiotVehicleObservation vehicle, DispatchBatteryPolicy? policy, int tasksToCover, int rescueBatteryPercent)
    {
        ArgumentNullException.ThrowIfNull(vehicle);
        if (policy is null)
        {
            return DispatchReasonCodes.ChargingPolicyNotApproved;
        }

        if (EntryNotAboveRescueLine(policy.Content, rescueBatteryPercent))
        {
            return DispatchReasonCodes.ChargingPolicyEntryNotAboveRescueLine;
        }

        if (vehicle.BatteryPercent is not int battery || string.IsNullOrWhiteSpace(vehicle.BatteryState))
        {
            return VehicleDynamicFactsCriterion.BatteryFactUnknownReason;
        }

        if (string.Equals(vehicle.BatteryState, ChargingBatteryState, StringComparison.Ordinal) &&
            !ChargingVehicleMayTakeWork(vehicle))
        {
            return VehicleDynamicFactsCriterion.BatteryPolicyNotSatisfiedReason;
        }

        if (IsMandatoryCharge(battery, policy.Content))
        {
            return DispatchReasonCodes.MandatoryChargeRequired;
        }

        return KeepsMarginAfter(battery, policy.Content, tasksToCover)
            ? DispatchAdmissionChain.Eligible
            : VehicleDynamicFactsCriterion.BatteryPolicyNotSatisfiedReason;
    }

    /// <summary>
    /// 在途车追加一条需求后要覆盖的趟数：当前下一站起、各卸货停靠上还没卸完的需求数，再加上追加的这一条。
    /// </summary>
    /// <remarks>
    /// 只往保守的方向估（票面第 2 条）：已在途的每条需求都按完整一趟计，哪怕它已经走了大半——这一趟到底还剩多少耗电没人知道，
    /// 按整趟算只会让车更早停止接追加，不会让一辆保不住余量的车多接。读不出计划时按 2 趟（这一趟加追加的一条）。
    /// </remarks>
    public static int TasksToCoverAfterAppend(EnRouteVehiclePlan? plan)
    {
        if (plan is null)
        {
            return 2;
        }

        int open = plan.Stops
            .Skip(Math.Max(0, plan.CurrentNextStopIndex))
            .Where(stop => string.Equals(stop.Role, JourneyStopRoles.Unload, StringComparison.Ordinal))
            .Sum(stop => plan.WorklistItemsByStopId.TryGetValue(stop.StopId, out int items) ? Math.Max(0, items) : 0);
        return Math.Max(1, open) + 1;
    }

    /// <summary>
    /// 下发给车载端的 <c>VehicleBusinessStateSnapshot.batteryState</c>（四值）。映射表：
    /// <list type="table">
    /// <item><term><c>UNKNOWN</c></term><description>没有策略（没有已批准版本、读不到）；电量或电池状态读不到；读数过期</description></item>
    /// <item><term><c>MANDATORY_CHARGE</c></term><description>当前电量低于 <c>MandatoryChargeEntryThreshold</c></description></item>
    /// <item><term><c>LOW</c></term><description>不低于入口线，但减去一趟耗电估计后保不住最低任务后电量余量</description></item>
    /// <item><term><c>SUFFICIENT</c></term><description>其余</description></item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// 充电中的车按电量照样映射：<c>batteryState</c> 说的是电量够不够，充没充电是 <c>chargingCycleState</c> 的事（批次9-07）。
    /// 投影只在开新的一版快照时算一次、记在旅程上（<c>JourneyRuntimeRow.PublishedBatteryState</c>），同一个消息 id 重发时原样读回，
    /// 从不在重发时现读电量。
    /// </remarks>
    public static string Project(RiotVehicleObservation? vehicle, DispatchBatteryPolicy? policy, bool observationFresh)
    {
        if (policy is null || vehicle is null || !observationFresh ||
            vehicle.BatteryPercent is not int battery || string.IsNullOrWhiteSpace(vehicle.BatteryState))
        {
            return BatteryStates.Unknown;
        }

        if (IsMandatoryCharge(battery, policy.Content))
        {
            return BatteryStates.MandatoryCharge;
        }

        return KeepsMarginAfter(battery, policy.Content, 1) ? BatteryStates.Sufficient : BatteryStates.Low;
    }
}

/// <summary>
/// 每辆车此刻是否在强制充电（低于它的 <c>MandatoryChargeEntryThreshold</c>）的最近一次结论，跨轮保留（批次9-05，control-server#403）。
/// 宿主里是单例：派车每一轮是一个新的作用域，一辆低电量的车每两秒判一次，只在进入与离开时各记一条日志。
/// </summary>
public sealed class MandatoryChargeBoard
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _below = new(StringComparer.Ordinal);

    /// <summary>记下这辆车这一轮的结论；与上一次不同时答真。第一次见到一辆车时，只有「在强制充电」算变化。</summary>
    public bool Record(string vehicleKey, bool below)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vehicleKey);
        bool changed = _below.TryGetValue(vehicleKey, out bool before) ? before != below : below;
        _below[vehicleKey] = below;
        return changed;
    }
}
