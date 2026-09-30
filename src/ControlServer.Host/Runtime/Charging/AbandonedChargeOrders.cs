using ControlServer.Application;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Host.Runtime.Charging;

/// <summary>
/// 被放弃的充电单（批次9-06，control-server#404 独立审查 M2(b)）：建单发出过、结果未知，RIoT 持续答「查无此单」，服务端按已确认失败收了尾
/// （<see cref="ChargingExecutionReasons.OrderNeverAppeared"/>）。这里回答「RIoT 上正在跑的这张单是不是那样一张」。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么要认得它。</b>「查无此单」只证明那一刻没有。建单请求可能还在路上，RIoT 事后才把单建出来、派给车——而那趟充电旅程已经收尾，
/// 用途占有已经释放，桩的预占可能已经放掉、给了别的车。这张迟到的单没有任何旅程在看着它，绝不能让它把车开走。
/// </para>
/// <para>
/// <b>怎么处理。</b>复用外来单监督器（control-server#330，<c>ForeignRunningOrderSupervisor</c>）：它每轮读未完成订单清单、只看在本项目车辆上
/// 运行的单，已经有「取消只发一次、留审计、取消前重读、期间车不接新活、没取消掉就交给人」的整套护栏。订单意图在库里时监督器本来把单当成自己的、
/// 不去管；对一张被放弃的充电单，它改答「要取消的」，依据记 <see cref="OwnershipBasis"/>。另写一支等于把那套护栏再抄一遍。
/// </para>
/// <para>
/// <b>不受外来单取消开关管</b>（<c>RiotForeignOrderCancel:Enabled</c>）：那个开关问的是「本部署有没有被授权取消<b>别人</b>的单」。这张单是本服务端
/// 自己建的，取消自己的单不需要那份授权。
/// </para>
/// <para>
/// 判定全凭本库：这张单的 <c>upperId</c>（或 RIoT 单号）对得上一条订单意图，那条意图是一个 <c>CHARGER</c> 停靠的，而那趟旅程的充电周期以
/// <see cref="ChargingExecutionReasons.OrderNeverAppeared"/> 结束。不用订单意图的状态列做标记：它是别的读者的并发令牌与判据。
/// </para>
/// </remarks>
public static class AbandonedChargeOrders
{
    /// <summary>监督器记在 <c>ForeignRiotOrders.OwnershipBasis</c> 上的依据。</summary>
    public const string OwnershipBasis = "OWN_CHARGE_ORDER_ABANDONED";

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
            .Where(stop => upperIds.Contains(stop.UpperId) && stop.StopRole == JourneyStopRoles.Charger)
            .Select(stop => stop.JourneyId)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        return journeyIds.Length > 0 &&
               await dbContext.Set<ChargingCycleRow>().AsNoTracking()
                   .AnyAsync(
                       cycle => journeyIds.Contains(cycle.JourneyId) &&
                                cycle.EndReason == ChargingExecutionReasons.OrderNeverAppeared,
                       cancellationToken)
                   .ConfigureAwait(false);
    }
}
