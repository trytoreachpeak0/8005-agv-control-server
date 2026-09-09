using System.Reflection;
using ControlServer.Infrastructure.Persistence;

namespace ControlServer.Host.Dashboard;

/// <summary>
/// 一个数据面的只读查询端点。
/// </summary>
/// <remarks>
/// **这是 #12 立的约定在服务端这半。**每个数据面自带一个只读端点，实现放一个文件，
/// <see cref="DashboardQueryEndpointCatalog"/> 用反射发现它——所以新增一个数据面只加文件，
/// 不改 <c>Program.cs</c>，也不改任何既有的组合根文件。
///
/// 这些端点**不进协议 v2**：它们全部在 <c>/api/dashboard/</c> 之下，只读，只服务看板。
/// </remarks>
internal interface IDashboardQueryEndpoint
{
    /// <summary>端点路径。必须在 <see cref="DashboardQueryEndpointCatalog.QueryPrefix"/> 之下。</summary>
    string Path { get; }

    Task<object> ReadAsync(ControlServerDbContext dbContext, CancellationToken cancellationToken);
}

internal sealed class DashboardQueryEndpointCatalog
{
    /// <summary>看板只读查询端点的前缀，与看板工程里的 <c>DashboardPaths.QueryPrefix</c> 是同一个值。</summary>
    internal const string QueryPrefix = "/api/dashboard/";

    private DashboardQueryEndpointCatalog(IReadOnlyList<IDashboardQueryEndpoint> endpoints)
    {
        Endpoints = endpoints;
    }

    internal IReadOnlyList<IDashboardQueryEndpoint> Endpoints { get; }

    internal static DashboardQueryEndpointCatalog Discover(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        List<IDashboardQueryEndpoint> endpoints = [];
        foreach (Type type in assembly.GetTypes())
        {
            if (type.IsAbstract
                || type.IsInterface
                || !typeof(IDashboardQueryEndpoint).IsAssignableFrom(type)
                || type.GetConstructor(Type.EmptyTypes) is null)
            {
                continue;
            }
            endpoints.Add((IDashboardQueryEndpoint)Activator.CreateInstance(type)!);
        }
        List<IDashboardQueryEndpoint> ordered = [.. endpoints.OrderBy(endpoint => endpoint.Path, StringComparer.Ordinal)];
        string[] duplicates = [.. ordered.GroupBy(endpoint => endpoint.Path, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)];
        if (duplicates.Length > 0)
        {
            throw new InvalidOperationException(
                $"Two dashboard query endpoints claim the same path: {string.Join(", ", duplicates)}.");
        }
        foreach (IDashboardQueryEndpoint endpoint in ordered)
        {
            if (!endpoint.Path.StartsWith(QueryPrefix, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Dashboard query endpoint {endpoint.GetType().Name} maps {endpoint.Path}, which is outside "
                    + $"{QueryPrefix}. Dashboard queries live under that prefix so that they stay disjoint from "
                    + "the protocol message surface.");
            }
        }
        return new DashboardQueryEndpointCatalog(ordered);
    }
}
