using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Host.Runtime;

/// <summary>
/// 被放弃的需求腿单（control-server#673）：建单发出过、结果未知，RIoT 一直查无此单（或现场人员确认它没建成），这一段已记成「单从没出现过」、
/// 交给同车重建（<see cref="OwnOrderRebuildSources.OrderNeverAppeared"/>）。这里回答「RIoT 上正在跑的这张单是不是那样一张」。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么要认得它。</b>「查无此单」只证明那一刻没有。旧单号的订单意图还在库里，外来单监督器原本会把它当成自己的在途单、不去管；可这一段已经改由
/// 重建的新单（新单号）负责，或者已经由人放弃、转交，旧单若事后冒出来、把车开走，没有任何东西在看着它——车上可能还有货。
/// </para>
/// <para>
/// <b>只挡车、告警，不取消</b>，与被放弃的空闲返回单、清桩移动单同理（<see cref="IdleReturn.AbandonedIdleReturnOrders"/>）：<c>REQ-0148</c> 情形二
/// （需求基线 <c>v1.9.0</c>，<c>CP-0010</c>）只授权取消本服务端放弃的<b>充电</b>单。挡车期间重建也不会建新单：重建建单前的车况检查读到车上有未完成单就等。
/// </para>
/// <para>判定全凭本库：这张单的 <c>upperId</c>（或 RIoT 单号）对得上一条订单意图，而那个 <c>upperId</c> 有一条来源为「从没出现过」的重建记录。</para>
/// </remarks>
public static class AbandonedDemandLegOrders
{
    /// <summary>监督器记在 <c>ForeignRiotOrders.OwnershipBasis</c> 上的依据。</summary>
    public const string OwnershipBasis = "OWN_DEMAND_LEG_ORDER_ABANDONED";

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
        return upperIds.Length > 0 &&
               await dbContext.OwnOrderRebuilds.AsNoTracking()
                   .AnyAsync(
                       row => upperIds.Contains(row.EndedUpperId) && row.Source == OwnOrderRebuildSources.OrderNeverAppeared,
                       cancellationToken)
                   .ConfigureAwait(false);
    }
}
