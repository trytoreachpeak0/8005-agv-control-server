using ControlServer.Application;
using ControlServer.Domain;

namespace ControlServer.Host.Runtime.Dispatch.Criteria;

/// <summary>
/// 这辆车此刻能不能承接一个新用途——车辆这一侧的判定，派车与空闲返回共用（control-server#389 审查 M1）。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么共用。</b>派车链把「车能不能接活」拆在几条判据里（故障阻断 15、动态事实 80），空闲返回的资格另写一份时，
/// 故障那一条就被漏掉了：一辆被判疑似阻塞、甚至已确认隔离的车，派车链不给它活，空闲返回却承诺它开往等待点（准入线 1，
/// REQ-0291「受阻的车不改状态」）。两份各写各的会再漂一次，所以车辆侧的这几条只在这里定义，两边都来调。
/// </para>
/// <para>
/// 派车链仍是两条判据、顺序不变（结构性告警按判据顺序分类）：<see cref="VehicleFaultBlockCriterion"/> 调
/// <see cref="FaultVerdictAsync"/>，<see cref="VehicleDynamicFactsCriterion"/> 就是 <see cref="VehicleDynamicFactsCriterion.Evaluate"/>。
/// 空闲返回调 <see cref="JudgeAsync"/>，三者依次判。批次 9 的投运策略、强制充电、人工充电等待、充电资格暂停加在这里。
/// 投运策略（control-server#400）是第三格 <see cref="CommissioningVerdictAsync"/>，派车链那一侧是
/// <see cref="ChargingPolicyCommissioningCriterion"/>（Order 17）——同一个判定，每条链只判一次。
/// </para>
/// </remarks>
public static class VehicleNewPurposeReadiness
{
    /// <summary>故障阻断（REQ-0232、REQ-0234）：任一级别都不接新用途；身份解析不出也不接。能接答 <see cref="DispatchAdmissionChain.Eligible"/>。</summary>
    public static async Task<string> FaultVerdictAsync(
        IVehicleFaultStore faults, string agvId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(faults);
        if (string.IsNullOrWhiteSpace(agvId))
        {
            return VehicleFaultBlockCriterion.IdentityUnresolvedReason;
        }

        VehicleFaultFact? fault = await faults.ReadAsync(agvId, cancellationToken).ConfigureAwait(false);
        return fault?.Level switch
        {
            VehicleFaultLevel.ConfirmedIsolated => VehicleFaultBlockCriterion.IsolatedReason,
            VehicleFaultLevel.SuspectedBlocked => VehicleFaultBlockCriterion.SuspectedReason,
            _ => DispatchAdmissionChain.Eligible
        };
    }

    /// <summary>
    /// 投运（control-server#400；REQ-0282，规格 8.6 逐车硬阻断）：没有已批准、已激活、覆盖这辆车的充电策略版本，或读不到，
    /// 都不接新用途。能接答 <see cref="DispatchAdmissionChain.Eligible"/>，否则 <see cref="DispatchReasonCodes.ChargingPolicyNotApproved"/>；
    /// 判定本身一并交回，调用方要写日志时用它的原因与说明。
    /// </summary>
    /// <remarks>
    /// control-server#403：投运之外再判一条——生效版本的强制充电线不高于 <paramref name="rescueBatteryPercent"/>（服务端的救命告警线）时，
    /// 这一版视为不可用，答 <see cref="DispatchReasonCodes.ChargingPolicyEntryNotAboveRescueLine"/>。激活走 FieldOps、不经服务端，
    /// 所以这是服务端在「用」的时候唯一能拦的一处；读它的是派车两条链、空闲返回与充电分配，一处定义。
    /// </remarks>
    public static async Task<(string Verdict, VehicleChargingPolicyDecision Decision)> CommissioningVerdictAsync(
        IChargingPolicyResolver chargingPolicy, string vehicleKey, int rescueBatteryPercent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chargingPolicy);
        if (string.IsNullOrWhiteSpace(vehicleKey))
        {
            return (DispatchReasonCodes.ChargingPolicyNotApproved,
                new VehicleChargingPolicyDecision(vehicleKey ?? string.Empty, ChargingPolicyCommissioningReasons.NotApproved, null, "no vehicle key"));
        }

        VehicleChargingPolicyDecision decision =
            await chargingPolicy.ResolveForNewDecisionAsync(vehicleKey, cancellationToken).ConfigureAwait(false);
        if (decision.Effective is { } effective &&
            effective.Policy.Content.MandatoryChargeEntryThresholdPercent <= rescueBatteryPercent)
        {
            return (DispatchReasonCodes.ChargingPolicyEntryNotAboveRescueLine, decision with
            {
                Detail = string.Create(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $"charging policy version {effective.Policy.Version} has MandatoryChargeEntryThreshold {effective.Policy.Content.MandatoryChargeEntryThresholdPercent}, not above JourneyRuntime:WaitingJourneyRescueBatteryPercent {rescueBatteryPercent}")
            });
        }

        return (decision.Commissioned ? DispatchAdmissionChain.Eligible : DispatchReasonCodes.ChargingPolicyNotApproved, decision);
    }

    /// <summary>故障阻断，然后投运策略，然后车辆动态事实（安全、在线、绑定、IDLE、地图、新鲜、电量门槛、停止、RIoT 上没有它的单）。</summary>
    public static async Task<string> JudgeAsync(
        IVehicleFaultStore faults,
        IChargingPolicyResolver chargingPolicy,
        DispatchVehicleFacts facts,
        JourneyRuntimeOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(facts);
        string fault = await FaultVerdictAsync(faults, facts.AgvId, cancellationToken).ConfigureAwait(false);
        if (fault != DispatchAdmissionChain.Eligible)
        {
            return fault;
        }

        (string commissioning, _) = await CommissioningVerdictAsync(
                chargingPolicy, facts.VehicleKey, options.WaitingJourneyRescueBatteryPercent, cancellationToken)
            .ConfigureAwait(false);
        return commissioning != DispatchAdmissionChain.Eligible
            ? commissioning
            : VehicleDynamicFactsCriterion.Evaluate(facts, options);
    }
}
