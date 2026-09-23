using ControlServer.Host.Runtime.ForeignOrders;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Host.Dashboard;

/// <summary>
/// 车队视图里「车上的外来订单」的数据面（control-server#330）：此刻挡着一辆车的每一张外来订单——车、订单号、upperId、专用原因码
/// 与说明、什么时候认出的、取消什么时候发的、取消结果。
/// </summary>
/// <remarks>
/// <para>
/// 只列挡着车的那几种（<see cref="ForeignRiotOrderStates.Holding"/>）：已经回查确认终结的、已经不在我们车上的不列，它们不再要人做什么，
/// 审计记录仍在 <c>ForeignRiotOrders</c> 表里。
/// </para>
/// <para>
/// 这辆车若同时有旅程，旅程阻断卡片上它的「未知」不再归给自己的在途单（<see cref="OwnMovementOrderExplanation"/>），直接进最高档；
/// 这张卡片说清楚是哪一张单。
/// </para>
/// </remarks>
internal sealed class ForeignRunningOrdersQueryEndpoint : IDashboardQueryEndpoint
{
    /// <summary>每个原因码给现场人员的说明。键取常量，改名时编译期就断在这里。</summary>
    internal static IReadOnlyDictionary<string, string> Descriptions { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ForeignRunningOrders.CancellingReason] =
                "我们车上正在运行一张不是本服务端建的订单：服务端已认定它是外来订单，正在取消（只发一次），"
                + "RIoT 回查确认它已终结之前，这辆车不接新单、不接途中追加。取消见效后这一行自动消失；"
                + "持续十几秒以上仍在，说明取消没见效或还没发出，会转成要人处理的原因码",
            [ForeignRunningOrders.StillRunningAfterCancelReason] =
                "服务端对这张外来订单发过一次取消，但它仍在我们车上运行：服务端不会再发第二次，这辆车不接新单、不接途中追加。"
                + "请到 RIoT 按订单号核对这张单，必要时在 RIoT 手动取消或到车前处理；它终结后这一行自动消失",
            [ForeignRunningOrders.OwnershipUnprovenReason] =
                "我们车上正在运行一张订单，服务端证明不了它是不是自己建的（例如单号长得像本服务端的、库里却没有它的建单记录）："
                + "服务端不取消它，这辆车不接新单、不接途中追加。请到 RIoT 核实这张单是谁建的、要不要取消，它终结后这一行自动消失",
            [ForeignRunningOrders.CancelNotAuthorizedReason] =
                "我们车上正在运行一张不是本服务端建的订单，但这套部署没有被授权取消外来订单（RiotForeignOrderCancel:Enabled 为 false）："
                + "服务端不取消它，这辆车不接新单、不接途中追加。请到 RIoT 核实这张单，需要时在 RIoT 手动取消或到车前处理；"
                + "它终结后这一行自动消失",
            [ForeignRunningOrders.UnsettledReason] =
                "这张外来订单已不在 RIoT 的运行列表里，但按订单号回查读不到它已终结（常见的是回查调用失败或单子处在 SUSPENDED，"
                + "少数是 RIoT 里查不到这张单）：服务端只认明确终结才放车，这辆车一直不接新单、不接途中追加，本服务端没有人工解除入口。"
                + "请到 RIoT 按订单号核对这张单的状态；在 RIoT 里把它结束后这一行自动消失。RIoT 里查不到这张单时找值班工程师",
        };

    public string Path => DashboardQueryEndpointCatalog.QueryPrefix + "foreign-running-orders";

    public async Task<object> ReadAsync(ControlServerDbContext dbContext, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        string[] holding = [.. ForeignRiotOrderStates.Holding];
        ForeignRiotOrderRow[] rows = await dbContext.ForeignRiotOrders.AsNoTracking()
            .Where(row => holding.Contains(row.State))
            .ToArrayAsync(cancellationToken);

        // Ordered in memory: SQLite cannot ORDER BY a DateTimeOffset.
        return rows
            .OrderBy(row => row.DetectedAt)
            .ThenBy(row => row.AgvId, StringComparer.Ordinal)
            .ThenBy(row => row.RiotOrderId, StringComparer.Ordinal)
            .Select(row =>
            {
                string? reasonCode = ForeignRunningOrders.ReasonFor(row.State);
                return new
                {
                    agvId = row.AgvId,
                    deviceKey = row.DeviceKey,
                    riotOrderId = row.RiotOrderId,
                    upperId = row.UpperId,
                    ownership = row.Ownership,
                    ownershipBasis = row.OwnershipBasis,
                    state = row.State,
                    reasonCode,
                    reasonDescription = reasonCode is null ? null : Descriptions.GetValueOrDefault(reasonCode),
                    orderStateAtDetection = row.OrderStateAtDetection,
                    detectedAt = row.DetectedAt,
                    cancelSentAt = row.CancelSentAt,
                    cancelResult = row.CancelResult,
                };
            })
            .ToArray();
    }
}
