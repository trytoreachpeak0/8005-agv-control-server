using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace ControlServer.Dashboard;

/// <summary>看板的刷新节奏。车队与仓位两个视图共用这一个值。</summary>
public static class DashboardRefresh
{
    /// <summary>REQ-0268 的统一 2 秒节奏。</summary>
    public static TimeSpan Interval { get; } = TimeSpan.FromSeconds(2);
}

/// <summary>
/// 看板主文件：把目录里的卡片渲染成页面。
/// </summary>
/// <remarks>
/// 它不认识任何一张具体卡片。接入一张新卡片不需要改这个文件——这是 #12 立的约定，
/// <c>DashboardSelfRegistrationTests</c> 用一张示例卡片守着它。
/// </remarks>
public static class DashboardPageRenderer
{
    public static string RenderPage(
        DashboardCardCatalog catalog,
        IReadOnlyDictionary<string, DashboardCardData> dataByCardId)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(dataByCardId);

        StringBuilder html = new();
        html.Append("<!doctype html><html lang=\"zh-CN\"><head><meta charset=\"utf-8\">");
        html.Append(
            CultureInfo.InvariantCulture,
            $"<meta http-equiv=\"refresh\" content=\"{(int)DashboardRefresh.Interval.TotalSeconds}\">");
        html.Append("<title>8005 车队与仓位看板</title></head><body>");
        html.Append(RenderView(catalog, dataByCardId, DashboardView.Fleet, "车队"));
        html.Append(RenderView(catalog, dataByCardId, DashboardView.Slots, "仓位"));
        html.Append("</body></html>");
        return html.ToString();
    }

    public static string RenderCard(IDashboardCard card, DashboardCardData data)
    {
        ArgumentNullException.ThrowIfNull(card);
        ArgumentNullException.ThrowIfNull(data);

        StringBuilder html = new();
        html.Append(CultureInfo.InvariantCulture, $"<section class=\"card\" id=\"{WebUtility.HtmlEncode(card.CardId)}\">");
        html.Append(CultureInfo.InvariantCulture, $"<h2>{WebUtility.HtmlEncode(card.Title)}</h2>");
        if (data.IsAvailable)
        {
            html.Append(card.RenderFact(data.Fact!.Value));
        }
        else
        {
            // 取不到当前事实时直接说原因。这里不存在也不允许存在一个「上次的值」可以退回去显示。
            html.Append(
                CultureInfo.InvariantCulture,
                $"<p class=\"unavailable\">{WebUtility.HtmlEncode(data.UnavailableReason!)}</p>");
        }
        html.Append("</section>");
        return html.ToString();
    }

    private static string RenderView(
        DashboardCardCatalog catalog,
        IReadOnlyDictionary<string, DashboardCardData> dataByCardId,
        DashboardView view,
        string heading)
    {
        StringBuilder html = new();
        html.Append(CultureInfo.InvariantCulture, $"<h1>{WebUtility.HtmlEncode(heading)}</h1>");
        foreach (IDashboardCard card in catalog.For(view))
        {
            DashboardCardData data = dataByCardId.TryGetValue(card.CardId, out DashboardCardData? found)
                ? found
                : DashboardCardData.Unavailable("本轮尚未取到该数据面。");
            html.Append(RenderCard(card, data));
        }
        return html.ToString();
    }

    /// <summary>把一段文字放进表格单元格。卡片实现共用它，免得各写各的转义。</summary>
    public static string Cell(string text) => $"<td>{WebUtility.HtmlEncode(text)}</td>";

    /// <summary>读一个 JSON 属性的字符串形态；缺失时给出一个明说缺失的值，而不是空白。</summary>
    public static string Text(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out JsonElement value)
            ? value.ToString()
            : "（服务端未提供该字段）";
}
