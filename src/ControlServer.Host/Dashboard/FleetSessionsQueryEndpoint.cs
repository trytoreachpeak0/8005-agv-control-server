using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Host.Dashboard;

/// <summary>车队视图的数据面：每台车的会话就绪与原因码。</summary>
internal sealed class FleetSessionsQueryEndpoint : IDashboardQueryEndpoint
{
    public string Path => DashboardQueryEndpointCatalog.QueryPrefix + "fleet-sessions";

    public async Task<object> ReadAsync(ControlServerDbContext dbContext, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        return await dbContext.SessionRecoveries.AsNoTracking()
            .OrderBy(row => row.AgvId)
            .Select(row => new
            {
                agvId = row.AgvId,
                readiness = row.Readiness.ToString(),
                reasonCode = row.ReasonCode
            })
            .ToArrayAsync(cancellationToken);
    }
}
