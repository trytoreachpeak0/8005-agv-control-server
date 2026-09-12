using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Host.Dashboard;

/// <summary>车队视图的数据面：每台车的会话就绪与原因码，或者拿不到它的原因。</summary>
/// <remarks>
/// 会话行上的 <c>Readiness</c> 是这台车最后一次在线时的判定，车断线后那一行原样留着。只投这一列，一台死掉的车
/// 在看板上会一直是 Ready——那是 REQ-0269 禁止的不确定新旧的旧值，与车载告警卡片 2026-09-10 修掉的是同一个缺陷。
/// 所以听不到当前这一代会话的车，就绪与原因码两项都不给，只给失联这个原因；在线判定见 <see cref="SessionLiveness"/>。
/// </remarks>
internal sealed class FleetSessionsQueryEndpoint : IDashboardQueryEndpoint
{
    public string Path => DashboardQueryEndpointCatalog.QueryPrefix + "fleet-sessions";

    public async Task<object> ReadAsync(ControlServerDbContext dbContext, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        HashSet<string> heard = await SessionLiveness.HeardFromAsync(
            dbContext, TimeProvider.System.GetUtcNow(), cancellationToken);
        SessionRecoveryRow[] rows = await dbContext.SessionRecoveries.AsNoTracking()
            .OrderBy(row => row.AgvId)
            .ToArrayAsync(cancellationToken);

        return rows.Select(row => heard.Contains(row.AgvId)
            ? Fact(row.AgvId, available: true, row.Readiness.ToString(), row.ReasonCode, unavailableReason: null)
            : Fact(row.AgvId, available: false, readiness: null, reasonCode: null, VehicleAlarmProjection.LinkDownReason))
            .ToArray();
    }

    private static object Fact(
        string agvId,
        bool available,
        string? readiness,
        string? reasonCode,
        string? unavailableReason) => new
        {
            agvId,
            available,
            readiness,
            reasonCode,
            unavailableReason
        };
}
