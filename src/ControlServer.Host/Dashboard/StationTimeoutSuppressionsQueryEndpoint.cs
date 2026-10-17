using ControlServer.Infrastructure.Persistence;

namespace ControlServer.Host.Dashboard;

internal sealed class StationTimeoutSuppressionsQueryEndpoint : IDashboardQueryEndpoint
{
    public string Path => DashboardQueryEndpointCatalog.QueryPrefix + "station-timeout-suppressions";

    public Task<object> ReadAsync(ControlServerDbContext dbContext, CancellationToken cancellationToken) =>
        Task.FromResult<object>(new { suppressions = Array.Empty<object>() });
}
