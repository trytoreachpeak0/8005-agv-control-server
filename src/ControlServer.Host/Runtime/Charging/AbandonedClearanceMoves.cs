using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Host.Runtime.Charging;

/// <summary>
/// 被放弃的清桩移动单（control-server#611，缺口自 control-server#409 起）：清桩开往等待点的单建单发出过、结果未知，RIoT 一直查无此单，
/// 引擎按「证明没单没动」放了预占（经过上写 <see cref="ClearanceMoveReleaseReasons.NeverAppeared"/>）、停靠标成移除、回到清桩中。
/// 这里回答「RIoT 上正在跑的这张单是不是那样一张」。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么要认得它。</b>订单意图还在库里，外来单监督器原本把它当成本服务端自己的在途单、不去管；可那次移动已经结束，没有任何东西在看着这张单，
/// 而它要去的等待点预占已经放掉、可能已归了别的车。
/// </para>
/// <para>
/// <b>只挡车、告警，不取消</b>，与被放弃的空闲返回单同理（<c>IdleReturn.AbandonedIdleReturnOrders</c>）：<c>REQ-0148</c> 情形二（需求基线
/// <c>v1.9.0</c>，<c>CP-0010</c>）只授权取消本服务端放弃的<b>充电</b>单；清桩移动单是开往等待点的单段移动，不是充电单，没有这条依据。
/// </para>
/// <para>
/// 判定全凭本库：这张单的 <c>upperId</c>（或 RIoT 单号）对得上一条订单意图，那条意图是一次已移除的清桩移动停靠，而那个停靠的等待点预占以
/// <see cref="ClearanceMoveReleaseReasons.NeverAppeared"/> 释放。
/// </para>
/// </remarks>
public static class AbandonedClearanceMoves
{
    /// <summary>监督器记在 <c>ForeignRiotOrders.OwnershipBasis</c> 上的依据。</summary>
    public const string OwnershipBasis = "OWN_CLEARANCE_MOVE_ABANDONED";

    public static async Task<bool> IsAbandonedAsync(
        ControlServerDbContext dbContext,
        string orderId,
        string? upperId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        string[] upperIds = await dbContext.OrderIntents.AsNoTracking()
            .Where(row => row.OrderId == orderId || (upperId != null && row.UpperId == upperId))
            .Select(row => row.UpperId)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        if (upperIds.Length == 0)
        {
            return false;
        }

        JourneyStopRow[] stops = await dbContext.Set<JourneyStopRow>().AsNoTracking()
            .Where(stop => upperIds.Contains(stop.UpperId) && stop.StopRole == JourneyStopRoles.WaitingPoint &&
                           stop.Status == JourneyStopStatuses.Removed)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        foreach (JourneyStopRow stop in stops.Where(ClearanceMoveShape.IsClearanceMove))
        {
            if (await dbContext.Set<StationExclusivityRecordRow>().AsNoTracking()
                    .AnyAsync(
                        row => row.JourneyId == stop.JourneyId && row.StationId == stop.StationRiotId &&
                               row.StationKind == StationExclusivityKinds.WaitingPoint &&
                               row.ReleaseReason == ClearanceMoveReleaseReasons.NeverAppeared,
                        cancellationToken)
                    .ConfigureAwait(false))
            {
                return true;
            }
        }
        return false;
    }
}
