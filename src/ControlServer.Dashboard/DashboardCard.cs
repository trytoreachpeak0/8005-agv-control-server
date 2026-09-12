using System.Text.Json;

namespace ControlServer.Dashboard;

/// <summary>看板的两个视图。两者共用同一个刷新节奏。</summary>
public enum DashboardView
{
    Fleet,
    Slots
}

/// <summary>
/// 一张卡片这一轮拿到的东西：要么是当前事实，要么是取不到它的原因。
/// </summary>
/// <remarks>
/// 没有第三种状态，这是 REQ-0269 的全部内容。事实取不到时直接说原因——设备连接失败就显示
/// 「设备连接失败」，不显示一个灰掉的旧值。所以这个类型里没有「上次成功的值」这一项：留着它，
/// 迟早有人会去显示它。
/// </remarks>
public sealed record DashboardCardData
{
    private DashboardCardData(JsonElement? fact, string? unavailableReason)
    {
        Fact = fact;
        UnavailableReason = unavailableReason;
    }

    public JsonElement? Fact { get; }

    public string? UnavailableReason { get; }

    public bool IsAvailable => UnavailableReason is null;

    public static DashboardCardData Available(JsonElement fact) => new(fact, null);

    public static DashboardCardData Unavailable(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new DashboardCardData(null, reason);
    }
}

/// <summary>
/// 看板上的一张卡片。
/// </summary>
/// <remarks>
/// **这是本票立的约定，后面几张票的并行靠它。**每个数据面自带一个只读查询端点（服务端那半）
/// ＋ 一张自注册的卡片（这半）。<see cref="DashboardCardCatalog"/> 用反射发现实现，所以新增
/// 一张卡片只加一个文件，**不改看板主文件**。
///
/// 若哪张票发现必须改看板主文件才能接进来，说明这条约定没立住——回头修约定，别绕过去。
/// </remarks>
public interface IDashboardCard
{
    /// <summary>卡片标识，页面上稳定的锚点。</summary>
    string CardId { get; }

    /// <summary>卡片标题。</summary>
    string Title { get; }

    /// <summary>它长在哪个视图上。</summary>
    DashboardView View { get; }

    /// <summary>它读哪个只读查询端点。必须在 <c>/api/dashboard/</c> 之下。</summary>
    string SourcePath { get; }

    /// <summary>把当前事实渲染成卡片正文的 HTML。只在 <see cref="DashboardCardData.IsAvailable"/> 时被调用。</summary>
    string RenderFact(JsonElement fact);
}
