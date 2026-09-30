using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

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

    /// <summary>
    /// 故障阻断，然后门未证明的扣车（REQ-0364，control-server#385）：扣着的车不接任何新用途。派车链的故障阻断判据调它，
    /// <see cref="JudgeAsync"/> 也调它，所以搬运、空闲返回、充电走的是同一处。
    /// </summary>
    /// <remarks>
    /// 扣车也让会话不就绪（<c>WireToGateStore.DecideReadinessAsync</c>），动态事实那一格本来就会挡；这里单独判，是为了不靠
    /// 那条间接的路：原因码直说「被扣」，而且哪天就绪的算法变了、或有一条用途不看会话就绪，扣着的车照样派不出去（准入线 1）。
    /// </remarks>
    public static async Task<string> BlockVerdictAsync(
        IVehicleFaultStore faults, ControlServerDbContext dbContext, string agvId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        string fault = await FaultVerdictAsync(faults, agvId, cancellationToken).ConfigureAwait(false);
        if (fault != DispatchAdmissionChain.Eligible)
        {
            return fault;
        }

        return await dbContext.SlotDoorHolds.AsNoTracking()
            .AnyAsync(hold => hold.AgvId == agvId && hold.ReleasedAt == null, cancellationToken).ConfigureAwait(false)
            ? DispatchReasonCodes.VehicleSlotDoorHold
            : DispatchAdmissionChain.Eligible;
    }

    /// <summary>故障阻断与门未证明扣车（<see cref="BlockVerdictAsync"/>），然后车辆动态事实（安全、在线、绑定、IDLE、地图、新鲜、电量门槛、停止、RIoT 上没有它的单）。</summary>
    public static async Task<string> JudgeAsync(
        IVehicleFaultStore faults,
        ControlServerDbContext dbContext,
        DispatchVehicleFacts facts,
        JourneyRuntimeOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(facts);
        string blocked = await BlockVerdictAsync(faults, dbContext, facts.AgvId, cancellationToken).ConfigureAwait(false);
        return blocked != DispatchAdmissionChain.Eligible ? blocked : VehicleDynamicFactsCriterion.Evaluate(facts, options);
    }
}
