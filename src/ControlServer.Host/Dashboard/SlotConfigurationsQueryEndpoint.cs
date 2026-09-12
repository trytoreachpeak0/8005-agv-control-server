using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Host.Dashboard;

/// <summary>仓位视图的数据面：每台车当前生效的仓位配置版本与指纹。</summary>
internal sealed class SlotConfigurationsQueryEndpoint : IDashboardQueryEndpoint
{
    public string Path => DashboardQueryEndpointCatalog.QueryPrefix + "slot-configurations";

    public async Task<object> ReadAsync(ControlServerDbContext dbContext, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        return await dbContext.Set<ActiveSlotConfigurationRow>().AsNoTracking()
            .OrderBy(row => row.AgvId)
            .Select(row => new
            {
                agvId = row.AgvId,
                configurationVersion = row.ConfigurationVersion,
                fingerprint = row.Fingerprint
            })
            .ToArrayAsync(cancellationToken);
    }
}
