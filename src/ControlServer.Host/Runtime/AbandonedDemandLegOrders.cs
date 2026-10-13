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
               await NeverCreatedUpperIds(dbContext).AnyAsync(ended => upperIds.Contains(ended), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 被判作没建成的单号：有一条来源为 <see cref="OwnOrderRebuildSources.OrderNeverAppeared"/> 的重建记录以它为终结单。可以嵌进别的查询。
    /// </summary>
    public static IQueryable<string> NeverCreatedUpperIds(ControlServerDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        return dbContext.OwnOrderRebuilds.AsNoTracking()
            .Where(row => row.Source == OwnOrderRebuildSources.OrderNeverAppeared)
            .Select(row => row.EndedUpperId);
    }

    /// <summary>
    /// 这条意图的单已被判作没建成、而且从没拿到过单号：在本库的账上它没有订单（审查 H2、M1）。释放结算与空闲返回的「结果未知」检查都按这一条
    /// 把它当成已了结——否则它永远是 <c>RESULT_UNKNOWN</c>，需求放不掉、车再也不空闲返回。旧单真冒出来时由外来单监督器挡车。
    /// </summary>
    public static async Task<bool> TakenAsNeverCreatedAsync(
        ControlServerDbContext dbContext,
        OrderIntentRow intent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(intent);
        string upperId = intent.UpperId;
        return intent.OrderId is null &&
               await NeverCreatedUpperIds(dbContext).AnyAsync(ended => ended == upperId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// RIoT 是否曾就这条意图答过一张单：派车审计里有「接受」，或者带回过单号（审查 S1）。意图上的 <c>OrderId</c> 只在确认或终态时写，
    /// 建单应答带回的单号只留在审计里，所以「从没建成」的前提要连它一起看。
    /// </summary>
    public static Task<bool> RiotEverAnsweredWithAnOrderAsync(
        ControlServerDbContext dbContext,
        OrderIntentRow intent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(intent);
        string leg = intent.MovementLegId;
        return dbContext.RiotDispatchAuditEvents.AsNoTracking()
            .AnyAsync(row => row.MovementLegId == leg && (row.ReturnedOrderId != null || row.Outcome == "ACCEPTED"), cancellationToken);
    }
}
