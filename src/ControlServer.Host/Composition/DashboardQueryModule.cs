using ControlServer.Host.Dashboard;
using ControlServer.Infrastructure.Persistence;

namespace ControlServer.Host.Composition;

/// <summary>
/// 看板的只读查询端点。
/// </summary>
/// <remarks>
/// 这个文件只知道「有一份目录」，不知道目录里有哪些端点。新增一个数据面＝新增一个
/// <c>IDashboardQueryEndpoint</c> 文件，这里一个字不用改。
/// </remarks>
internal static class DashboardQueryModule
{
    internal static WebApplication MapDashboardQueries(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        DashboardQueryEndpointCatalog catalog =
            DashboardQueryEndpointCatalog.Discover(typeof(DashboardQueryModule).Assembly, app.Services);
        foreach (IDashboardQueryEndpoint endpoint in catalog.Endpoints)
        {
            IDashboardQueryEndpoint captured = endpoint;
            app.MapGet(
                captured.Path,
                async (ControlServerDbContext dbContext, CancellationToken cancellationToken) =>
                    Results.Ok(await captured.ReadAsync(dbContext, cancellationToken)));
        }
        return app;
    }
}
