using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Host.Runtime.Withdrawal;

/// <summary>
/// 人工撤下的旅程名下的单（control-server#771 审查 S-3）：这里回答「RIoT 上正在跑的这张单是不是那样一张」。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么要认得它。</b>撤下要求现场先核过 RIoT 上本车没有未结束的单，所以正常情形下不会有这样一张单在跑。可撤下只收尾旅程、不碰订单意图：
/// 那张单真在跑的话（核错了、或撤下之后才冒出来），外来单监督器认得它的意图，原本会把它当成本服务端自己的在途单、不去管——而它所属的旅程已经收尾，
/// 再没有任何东西看着它，车会照那张单开走。
/// </para>
/// <para>
/// <b>只挡车、告警，不取消</b>，与被放弃的需求腿单、空闲返回单同理（<see cref="AbandonedDemandLegOrders"/>）：取消要授权，
/// 这里只把它交给人。
/// </para>
/// <para>判定全凭本库：这张单的 <c>upperId</c>（或 RIoT 单号）对得上一条订单意图，而那个 <c>upperId</c> 是一趟以
/// <see cref="JourneyWithdrawal.WithdrawnReason"/> 收尾的旅程的停靠或旅程行点名的单。</para>
/// </remarks>
public static class WithdrawnJourneyOrders
{
    /// <summary>监督器记在 <c>ForeignRiotOrders.OwnershipBasis</c> 上的依据。</summary>
    public const string OwnershipBasis = "OWN_JOURNEY_WITHDRAWN_BY_OPERATOR";

    public static async Task<bool> IsWithdrawnAsync(
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

        IQueryable<JourneyRuntimeRow> withdrawn = dbContext.JourneyRuntimes.AsNoTracking()
            .Where(row => row.Stage == JourneyRuntimeStage.Completed && row.BlockReasonCode == JourneyWithdrawal.WithdrawnReason);
        return await withdrawn
                   .AnyAsync(row => upperIds.Contains(row.PickupUpperId) ||
                                    (row.GateUpperId != null && upperIds.Contains(row.GateUpperId)),
                       cancellationToken).ConfigureAwait(false) ||
               await dbContext.Set<JourneyStopRow>().AsNoTracking()
                   .AnyAsync(stop => upperIds.Contains(stop.UpperId) && withdrawn.Any(row => row.JourneyId == stop.JourneyId),
                       cancellationToken).ConfigureAwait(false);
    }
}
