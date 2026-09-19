using System.Reflection;

namespace ControlServer.Dashboard;

/// <summary>How a field shows on an action's confirmation page.</summary>
public enum DashboardActionFieldKind
{
    /// <summary>Carried over from the row the link came from; the person does not type it.</summary>
    Hidden,

    Text,

    TextArea
}

/// <summary>One field of an action's confirmation page.</summary>
public sealed record DashboardActionField(string Name, string Label, DashboardActionFieldKind Kind, bool Required);

/// <summary>
/// 看板上的一个写操作（control-server#162 立的约定，与 <see cref="IDashboardCard"/> 同一个做法）。
/// </summary>
/// <remarks>
/// <para>
/// 卡片那条约定说「约定没立住就回头修约定」：看板主程序原本只认 <c>GET /</c> 与只读卡片，接不进任何写操作，所以写操作也立成
/// 自注册——<see cref="DashboardActionCatalog"/> 反射发现实现，<see cref="DashboardActionRoutes"/> 一次挂上两条通用路由
/// （确认页与提交），看板主程序只为这件事改过一次。之后再加一个写操作只加一个文件。
/// </para>
/// <para>
/// 一个动作只声明三样东西：确认页上有哪些字段、把提交转给服务端的哪个路径、转交的请求体长什么样。表单、来源校验、转交与回到
/// 主页都在约定那一个文件里，动作文件里没有表单也没有路由。无人员认证的前提下这里只放收紧方向的动作
/// （规格 5.7），<c>DashboardActionTests</c> 按名单守着。
/// </para>
/// </remarks>
public interface IDashboardAction
{
    /// <summary>动作标识，确认页在 <c>/actions/{ActionId}</c>。</summary>
    string ActionId { get; }

    /// <summary>确认页标题。</summary>
    string Title { get; }

    /// <summary>ControlServer 上接这个动作的路径。不能在只读查询前缀 <c>/api/dashboard/</c> 之下。</summary>
    string TargetPath { get; }

    /// <summary>确认页上的字段，按显示次序。</summary>
    IReadOnlyList<DashboardActionField> Fields { get; }

    /// <summary>把提交上来的字段变成转给 ControlServer 的 JSON 请求体。字段校验由服务端做。</summary>
    object BuildRequest(IReadOnlyDictionary<string, string> form);
}

/// <summary>反射发现的看板动作集合。</summary>
public sealed class DashboardActionCatalog
{
    public DashboardActionCatalog(IEnumerable<IDashboardAction> actions)
    {
        ArgumentNullException.ThrowIfNull(actions);

        List<IDashboardAction> ordered = [.. actions.OrderBy(action => action.ActionId, StringComparer.Ordinal)];
        string[] duplicates = [.. ordered.GroupBy(action => action.ActionId, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)];
        if (duplicates.Length > 0)
        {
            throw new InvalidOperationException($"看板动作 id 重复：{string.Join("、", duplicates)}。");
        }
        foreach (IDashboardAction action in ordered)
        {
            if (action.TargetPath.StartsWith(DashboardPaths.QueryPrefix, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"看板动作 {action.ActionId} 的目标 {action.TargetPath} 在 {DashboardPaths.QueryPrefix} 之下；"
                    + "那个前缀只读，写操作不进去。");
            }
        }
        Actions = ordered;
    }

    public static DashboardActionCatalog Discovered { get; } = Discover(typeof(DashboardActionCatalog).Assembly);

    public IReadOnlyList<IDashboardAction> Actions { get; }

    public IDashboardAction? Find(string actionId) =>
        Actions.SingleOrDefault(action => string.Equals(action.ActionId, actionId, StringComparison.Ordinal));

    public static DashboardActionCatalog Discover(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        List<IDashboardAction> actions = [];
        foreach (Type type in assembly.GetTypes())
        {
            if (type.IsAbstract
                || type.IsInterface
                || !typeof(IDashboardAction).IsAssignableFrom(type)
                || type.GetConstructor(Type.EmptyTypes) is null)
            {
                continue;
            }
            actions.Add((IDashboardAction)Activator.CreateInstance(type)!);
        }
        return new DashboardActionCatalog(actions);
    }
}
