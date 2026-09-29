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
/// 空闲返回调 <see cref="JudgeAsync"/>，两者依次判。批次 9 的投运策略、强制充电、人工充电等待、充电资格暂停加在这里。
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

    /// <summary>故障阻断，然后车辆动态事实（安全、在线、绑定、IDLE、地图、新鲜、电量门槛、停止、RIoT 上没有它的单）。</summary>
    public static async Task<string> JudgeAsync(
        IVehicleFaultStore faults,
        DispatchVehicleFacts facts,
        JourneyRuntimeOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(facts);
        string fault = await FaultVerdictAsync(faults, facts.AgvId, cancellationToken).ConfigureAwait(false);
        return fault != DispatchAdmissionChain.Eligible ? fault : VehicleDynamicFactsCriterion.Evaluate(facts, options);
    }
}
