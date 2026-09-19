using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace ControlServer.Dashboard;

/// <summary>
/// 任务类型绑定与暂停（control-server#162）：按图、按任务类型逐行列出规则表里的每个任务类型、是否在本图需求集合里、绑定的
/// Station、生效绑定集版本与状态；已暂停的写明每条暂停的来源、起始时间与理由。
/// </summary>
/// <remarks>
/// 每行一个「暂停」链接，指向不自动刷新的确认页（看板动作 <c>task-type-hold</c>）。这张卡片只能收紧：页面上没有任何让暂停
/// 失效的入口，那是现场核对之后 FieldOps 的事。
/// </remarks>
public sealed class TaskTypeBindingCard : IDashboardCard
{
    /// <summary>「暂停」链接指向的看板动作。</summary>
    public const string HoldActionId = "task-type-hold";

    public string CardId => "task-type-bindings";

    public string Title => "任务类型绑定与暂停";

    public DashboardView View => DashboardView.Fleet;

    public string SourcePath => DashboardPaths.QueryPrefix + "task-type-bindings";

    public string RenderFact(JsonElement fact)
    {
        StringBuilder html = new();
        if (!fact.TryGetProperty("maps", out JsonElement maps) || maps.GetArrayLength() == 0)
        {
            html.Append("<p>没有任何一张图配置了任务类型绑定。</p>");
            return html.ToString();
        }
        foreach (JsonElement map in maps.EnumerateArray())
        {
            string mapId = DashboardPageRenderer.Text(map, "mapId");
            html.Append(CultureInfo.InvariantCulture, $"<h3>Map {WebUtility.HtmlEncode(mapId)}，生效绑定集版本 ")
                .Append(WebUtility.HtmlEncode(DashboardPageRenderer.Text(map, "activeBindingSetVersion")))
                .Append("</h3>");
            html.Append("<table><tr><th>任务类型</th><th>需求集合</th><th>绑定站点</th><th>状态</th><th>暂停</th><th></th></tr>");
            foreach (JsonElement row in map.GetProperty("taskTypes").EnumerateArray())
            {
                string taskType = DashboardPageRenderer.Text(row, "taskType");
                html.Append("<tr>")
                    .Append(DashboardPageRenderer.Cell(taskType))
                    .Append(DashboardPageRenderer.Cell(
                        row.TryGetProperty("required", out JsonElement required) && required.ValueKind == JsonValueKind.True
                            ? "在本图需求集合"
                            : "不在本图需求集合"))
                    .Append(DashboardPageRenderer.Cell(Station(row)))
                    .Append(DashboardPageRenderer.Cell(Status(DashboardPageRenderer.Text(row, "status"))))
                    .Append(DashboardPageRenderer.Cell(Holds(row)))
                    .Append(CultureInfo.InvariantCulture, $"<td><a href=\"{HoldLink(mapId, taskType)}\">暂停</a></td>")
                    .Append("</tr>");
            }
            html.Append("</table>");
        }
        return html.ToString();
    }

    private static string Station(JsonElement row) =>
        row.TryGetProperty("stationRiotId", out JsonElement id) && id.ValueKind == JsonValueKind.Number
            ? $"{id.GetInt32().ToString(CultureInfo.InvariantCulture)} {DashboardPageRenderer.Text(row, "stationName")}"
            : "无";

    private static string Status(string status) => status switch
    {
        "NORMAL" => "正常",
        "BINDING_MISSING" => "缺绑定",
        "STATION_NOT_IN_CATALOG" => "绑定站点不在目录",
        "HELD" => "已暂停",
        "NOT_REQUIRED" => "未启用",
        _ => status
    };

    private static string Holds(JsonElement row)
    {
        if (!row.TryGetProperty("holds", out JsonElement holds) || holds.GetArrayLength() == 0)
        {
            return "无";
        }
        return string.Join(
            "；",
            holds.EnumerateArray().Select(hold =>
            {
                string reason = hold.TryGetProperty("reason", out JsonElement value) && value.ValueKind == JsonValueKind.String
                    ? value.GetString()!
                    : "未记理由";
                return $"{DashboardPageRenderer.Text(hold, "sourceLabel")}，自 {DashboardPageRenderer.Text(hold, "raisedAt")}，{reason}";
            }));
    }

    private static string HoldLink(string mapId, string taskType) =>
        WebUtility.HtmlEncode(
            $"/actions/{HoldActionId}?mapId={Uri.EscapeDataString(mapId)}&taskType={Uri.EscapeDataString(taskType)}");
}
