using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Host.Runtime.IdleReturn;

/// <summary>
/// 被放弃的空闲返回单（control-server#611）：建单发出过、结果未知，RIoT 一直查无此单，空闲返回已按「单从没出现过」收尾
/// （<see cref="IdleReturnExecutionReasons.OrderNeverAppeared"/>，或人工收尾的两个同类码）。这里回答「RIoT 上正在跑的这张单是不是那样一张」。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么要认得它。</b>「查无此单」只证明那一刻没有。那趟空闲返回收尾之后用途已放、等待点可能已归了别的车，这张单若事后冒出来、把车开走，
/// 没有任何旅程在看着它。订单意图还在库里，外来单监督器原本会把它当成自己的在途单、不去管。
/// </para>
/// <para>
/// <b>只挡车、告警，不取消。</b>监督器把它记为「归属证明不了」（<c>Unproven</c>）：这辆车不接新活，告警交人（事件 2201），什么命令也不发。
/// 不像被放弃的充电单那样由服务端取消：<c>REQ-0148</c> 情形二（需求基线 <c>v1.9.0</c>，<c>CP-0010</c>）只授权取消本服务端放弃的<b>充电</b>单，
/// 空闲返回单没有这条依据；要服务端自己取消，得先改需求。
/// </para>
/// <para>
/// 判定全凭本库：这张单的 <c>upperId</c>（或 RIoT 单号）对得上一条订单意图，那条意图是一趟空闲返回的停靠，而那趟旅程以上面三个码之一收尾。
/// </para>
/// </remarks>
public static class AbandonedIdleReturnOrders
{
    /// <summary>监督器记在 <c>ForeignRiotOrders.OwnershipBasis</c> 上的依据。</summary>
    public const string OwnershipBasis = "OWN_IDLE_RETURN_ORDER_ABANDONED";

    /// <summary>空闲返回以「单从没出现过」收尾的码：自动放弃一个，人工收尾两个。</summary>
    public static IReadOnlySet<string> EndingCodes { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        IdleReturnExecutionReasons.OrderNeverAppeared,
        IdleReturnExecutionReasons.NeverAppearedByOperator,
        IdleReturnExecutionReasons.NeverAppearedAtWaitingPointByOperator,
    };

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

        string[] journeyIds = await dbContext.Set<JourneyStopRow>().AsNoTracking()
            .Where(stop => upperIds.Contains(stop.UpperId) && stop.JourneyId.StartsWith(IdleReturnIdentity.JourneyIdPrefix))
            .Select(stop => stop.JourneyId)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        if (journeyIds.Length == 0)
        {
            return false;
        }

        string[] codes = [.. EndingCodes];
        return await dbContext.JourneyRuntimes.AsNoTracking()
            .AnyAsync(
                row => journeyIds.Contains(row.JourneyId) && row.Stage == JourneyRuntimeStage.Completed &&
                       row.BlockReasonCode != null && codes.Contains(row.BlockReasonCode),
                cancellationToken)
            .ConfigureAwait(false);
    }
}
