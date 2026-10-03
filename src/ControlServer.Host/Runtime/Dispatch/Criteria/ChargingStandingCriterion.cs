using ControlServer.Application;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Host.Runtime.Dispatch.Criteria;

/// <summary>
/// 已承诺充电、或在人工充电等待中的车不接搬运（<c>REQ-0290</c>、<c>REQ-0173</c>、<c>REQ-0171</c>；批次9-06，control-server#404）。
/// </summary>
/// <remarks>
/// <para>
/// <b>已承诺充电</b>（持有 <c>CHARGING</c> 用途占有）：承诺是派车轮在任务循环之前形成的，这辆车在同一轮的任务循环里还在空闲车之列，
/// 照常被问；这条判据让它答「不接」，积压行与轮次结局说得出原因。原因码 <see cref="DispatchReasonCodes.VehicleCommittedToCharging"/>。
/// 仲裁仍在库里：受理时 <c>VehiclePurposeClaims</c> 的主键一车一行，这条判据漏了，受理也会被主键整个拒掉。
/// </para>
/// <para>
/// <b>人工充电等待</b>（服务端持有，名册为空时需要充电的车、或充电单反复被取消的车）：电量回升本身不恢复资格，出口只有「充电后返回服务」。
/// 所以它不能靠电量判据挡——被人充过电的车电量在线上，电量判据会放行。原因码 <see cref="DispatchReasonCodes.VehicleInManualChargingHold"/>，
/// 读法与空闲返回、充电分配共用（<see cref="VehicleNewPurposeReadiness.ManualChargingHoldVerdictAsync"/>）。
/// </para>
/// <para>
/// 只挡这两样。搬运占有是在途车自己的（在途链从空闲链派生、带着这条判据），必须放行；空闲返回的占有由
/// <see cref="IdleReturnCommitmentCriterion"/> 挡。
/// </para>
/// <para>
/// 排在故障阻断（15）、空闲返回承诺（16）与投运策略（17）之后（18）：都是「这辆车此刻不接活」，便宜，不必让后面读网络的判据先跑。
/// 序号不与别的判据并列（<c>DispatchChainSeamTests</c> 守着）：结构性告警按判据次序分类，次序不该靠注册先后碰巧。
/// </para>
/// </remarks>
public sealed class ChargingStandingCriterion(ControlServerDbContext dbContext) : IDispatchAdmissionCriterion
{
    public int Order => 18;

    public async Task<string> EvaluateAsync(
        DispatchCandidateEvaluation evaluation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        string vehicleKey = evaluation.Vehicle.VehicleKey;
        if (await dbContext.Set<VehiclePurposeClaimRow>().AsNoTracking()
                .AnyAsync(row => row.VehicleKey == vehicleKey && row.Purpose == VehiclePurposes.Charging, cancellationToken)
                .ConfigureAwait(false))
        {
            return DispatchReasonCodes.VehicleCommittedToCharging;
        }

        return await VehicleNewPurposeReadiness.ManualChargingHoldVerdictAsync(dbContext, vehicleKey, cancellationToken)
            .ConfigureAwait(false);
    }
}
