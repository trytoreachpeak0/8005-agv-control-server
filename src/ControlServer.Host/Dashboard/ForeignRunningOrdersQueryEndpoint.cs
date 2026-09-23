using ControlServer.Infrastructure.Persistence;

namespace ControlServer.Host.Dashboard;

/// <summary>
/// 车队视图里「车上的外来订单」的数据面（control-server#330）。
/// </summary>
internal sealed class ForeignRunningOrdersQueryEndpoint : IDashboardQueryEndpoint
{
    public string Path => DashboardQueryEndpointCatalog.QueryPrefix + "foreign-running-orders";

    public Task<object> ReadAsync(ControlServerDbContext dbContext, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        return Task.FromResult<object>(Array.Empty<object>());
    }
}
