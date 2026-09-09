using System.Reflection;

namespace ControlServer.Dashboard;

/// <summary>
/// 反射发现的卡片集合。
/// </summary>
/// <remarks>
/// 看板主文件（<see cref="DashboardPageRenderer"/> 与 <c>Program.cs</c>）遍历这份目录，不认识
/// 任何一张具体卡片的名字。新增一张卡片＝新增一个实现 <see cref="IDashboardCard"/> 的文件。
/// </remarks>
public sealed class DashboardCardCatalog
{
    private readonly IReadOnlyList<IDashboardCard> _cards;

    public DashboardCardCatalog(IEnumerable<IDashboardCard> cards)
    {
        ArgumentNullException.ThrowIfNull(cards);

        List<IDashboardCard> ordered = [.. cards.OrderBy(card => card.CardId, StringComparer.Ordinal)];
        string[] duplicates = [.. ordered.GroupBy(card => card.CardId, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)];
        if (duplicates.Length > 0)
        {
            throw new InvalidOperationException(
                $"看板卡片 id 重复：{string.Join("、", duplicates)}。");
        }
        foreach (IDashboardCard card in ordered)
        {
            if (!card.SourcePath.StartsWith(DashboardPaths.QueryPrefix, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"卡片 {card.CardId} 的数据源 {card.SourcePath} 不在 {DashboardPaths.QueryPrefix} 之下。"
                    + "看板只读 ControlServer 的只读查询端点，不进协议面。");
            }
        }
        _cards = ordered;
    }

    public static DashboardCardCatalog Discovered { get; } = Discover(typeof(DashboardCardCatalog).Assembly);

    public IReadOnlyList<IDashboardCard> Cards => _cards;

    public IReadOnlyList<IDashboardCard> For(DashboardView view) =>
        [.. _cards.Where(card => card.View == view)];

    public static DashboardCardCatalog Discover(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        List<IDashboardCard> cards = [];
        foreach (Type type in assembly.GetTypes())
        {
            if (type.IsAbstract
                || type.IsInterface
                || !typeof(IDashboardCard).IsAssignableFrom(type)
                || type.GetConstructor(Type.EmptyTypes) is null)
            {
                continue;
            }
            cards.Add((IDashboardCard)Activator.CreateInstance(type)!);
        }
        return new DashboardCardCatalog(cards);
    }
}

/// <summary>看板与 ControlServer 之间的路径约定。</summary>
public static class DashboardPaths
{
    /// <summary>
    /// 只读查询端点的前缀。看板的数据契约整个活在这个前缀之下，与协议 v2 的消息面无交集。
    /// </summary>
    public const string QueryPrefix = "/api/dashboard/";
}
