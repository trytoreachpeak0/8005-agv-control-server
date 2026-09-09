using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Host.Composition;

/// <summary>
/// Read-only views onto the runtime's own store.
/// </summary>
internal static class RuntimeQueryEndpointsModule
{
    internal static WebApplication MapRuntimeQueries(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet(
            "/api/runtime/sessions",
            async (ControlServerDbContext dbContext, CancellationToken cancellationToken) =>
                await dbContext.SessionRecoveries.AsNoTracking()
                    .OrderBy(row => row.AgvId)
                    .Select(row => new
                    {
                        row.AgvId,
                        row.SessionGeneration,
                        readiness = row.Readiness.ToString(),
                        row.ReasonCode,
                        row.UpdatedAt
                    })
                    .ToArrayAsync(cancellationToken));
        return app;
    }
}
