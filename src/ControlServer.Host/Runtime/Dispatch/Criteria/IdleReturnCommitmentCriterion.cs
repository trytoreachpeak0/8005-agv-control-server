using ControlServer.Application;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Host.Runtime.Dispatch.Criteria;

/// <summary>
/// 已承诺空闲返回的车不接搬运（<c>REQ-0292</c>；批次8-18，control-server#389）。
/// </summary>
/// <remarks>
/// <para>
/// 承诺形成之后，返回就是这辆车当前已承诺的下一站：后来出现的搬运、别的车优先级变化都不取消、不换点、不抢它。
/// 需求留在积压里，原因码 <see cref="DispatchReasonCodes.VehicleCommittedToIdleReturn"/>，等返回到点收敛（批次8-19）后下一轮重新派车。
/// </para>
/// <para>
/// <b>为什么是一条判据，而不是把车从空闲车里拿掉。</b>拿掉的话这辆车一句结论也不留，积压行上看不出「有车，但它承诺了返回」；
/// 判据让它照常被问、照常答「不接」，积压与轮次结局都说得出原因。仲裁仍在库里：受理时 <c>VehiclePurposeClaims</c> 的主键一车一行，
/// 这条判据漏了，受理也会被主键整个拒掉——它是让原因看得见的那一层，不是唯一的那一层。
/// </para>
/// <para>
/// 只挡 <c>IDLE_RETURN</c>。搬运占有是在途车自己的，在途链从空闲链派生、带着这条判据，搬运占有必须放行，否则每辆在途车都接不了追加。
/// 充电与清桩维护批次 8 还没有人取得；它们来了要不要挡、用什么码，是批次 9 的事。
/// </para>
/// <para>
/// 排在故障阻断之后（16）：与故障一样是「这辆车此刻不接活」，便宜，不必让后面读网络的判据先跑。
/// </para>
/// </remarks>
public sealed class IdleReturnCommitmentCriterion(ControlServerDbContext dbContext) : IDispatchAdmissionCriterion
{
    public int Order => 16;

    public async Task<string> EvaluateAsync(
        DispatchCandidateEvaluation evaluation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        bool committed = await dbContext.Set<VehiclePurposeClaimRow>().AsNoTracking()
            .AnyAsync(
                row => row.VehicleKey == evaluation.Vehicle.VehicleKey && row.Purpose == VehiclePurposes.IdleReturn,
                cancellationToken)
            .ConfigureAwait(false);
        return committed ? DispatchReasonCodes.VehicleCommittedToIdleReturn : DispatchAdmissionChain.Eligible;
    }
}
