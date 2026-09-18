using ControlServer.Infrastructure.Persistence;

namespace ControlServer.Host.Dashboard;

internal sealed class ExpectedActionOverdueQueryEndpoint : IDashboardQueryEndpoint
{
    public ExpectedActionOverdueQueryEndpoint()
        : this(ExpectedActionOverdueOptions.Default, TimeProvider.System)
    {
    }

    internal ExpectedActionOverdueQueryEndpoint(ExpectedActionOverdueOptions options, TimeProvider clock)
    {
        _ = options;
        _ = clock;
    }

    public string Path => DashboardQueryEndpointCatalog.QueryPrefix + "expected-action-overdue";

    public Task<object> ReadAsync(ControlServerDbContext dbContext, CancellationToken cancellationToken) =>
        throw new NotImplementedException();
}
