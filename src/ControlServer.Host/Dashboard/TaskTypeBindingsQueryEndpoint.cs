using ControlServer.Infrastructure.Persistence;

namespace ControlServer.Host.Dashboard;

/// <summary>The task type bindings and holds, per Map (control-server#162).</summary>
internal sealed class TaskTypeBindingsQueryEndpoint : IDashboardQueryEndpoint
{
    public string Path => DashboardQueryEndpointCatalog.QueryPrefix + "task-type-bindings";

    public Task<object> ReadAsync(ControlServerDbContext dbContext, CancellationToken cancellationToken) =>
        Task.FromResult<object>(new { maps = Array.Empty<object>() });
}
