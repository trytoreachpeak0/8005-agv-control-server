using ControlServer.Infrastructure.Persistence;

namespace ControlServer.Host.Dashboard;

/// <summary>Stub for the test commit of control-server#830; the implementation commit replaces it.</summary>
internal sealed class RemainingBasketsSkipsQueryEndpoint : IDashboardQueryEndpoint
{
    internal static readonly IReadOnlyDictionary<string, string> Descriptions = new Dictionary<string, string>(StringComparer.Ordinal);

    public string Path => DashboardQueryEndpointCatalog.QueryPrefix + "remaining-baskets-skips";

    public Task<object> ReadAsync(ControlServerDbContext dbContext, CancellationToken cancellationToken) =>
        Task.FromResult<object>(new { skips = Array.Empty<object>() });
}
