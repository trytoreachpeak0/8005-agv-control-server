using System.Net;
using System.Text;
using System.Text.Json;

namespace ControlServer.Dashboard;

/// <summary>
/// 车队视图「等待点」（批次8-21，control-server#392；REQ-0289、REQ-0293、REQ-0297）：当前登记版本，每个等待点的状态、持有车辆、
/// 进入该状态的时刻、所依据的登记版本与白名单。停用或删除之后仍被预占、占用的点照样列出并写明。
/// </summary>
public sealed class WaitingPointCard : IDashboardCard
{
    public string CardId => "waiting-points";

    public string Title => "等待点";

    public DashboardView View => DashboardView.Fleet;

    public string SourcePath => DashboardPaths.QueryPrefix + "waiting-points";

    public string RenderFact(JsonElement fact)
    {
        StringBuilder html = new();
        if (fact.TryGetProperty("registration", out JsonElement registration) && registration.ValueKind == JsonValueKind.Object)
        {
            html.Append("<p class=\"waiting-point-registration\">")
                .Append(WebUtility.HtmlEncode(
                    $"当前登记版本 v{DashboardPageRenderer.Text(registration, "version")}，"
                    + $"载入于 {DashboardPageRenderer.Text(registration, "loadedAt")}，"
                    + $"内容摘要 {DashboardPageRenderer.Text(registration, "contentSha256")}"))
                .Append("</p>");
        }
        else
        {
            html.Append("<p class=\"waiting-point-registration\">还没有登记任何等待点</p>");
        }

        html.Append("<table><tr><th>地图</th><th>站点</th><th>站名</th><th>白名单</th><th>登记</th>")
            .Append(StationHoldingRendering.HeaderCells)
            .Append("</tr>");
        if (fact.TryGetProperty("points", out JsonElement points) && points.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement point in points.EnumerateArray())
            {
                html.Append("<tr>")
                    .Append(DashboardPageRenderer.Cell(DashboardPageRenderer.Text(point, "mapId")))
                    .Append(DashboardPageRenderer.Cell(DashboardPageRenderer.Text(point, "stationId")))
                    .Append(DashboardPageRenderer.Cell(StationHoldingRendering.Str(point, "stationName") ?? ""))
                    .Append(DashboardPageRenderer.Cell(Scope(point)))
                    .Append(DashboardPageRenderer.Cell(Registration(point)))
                    .Append(point.TryGetProperty("holding", out JsonElement holding)
                        ? StationHoldingRendering.Cells(holding)
                        : DashboardPageRenderer.Cell("（服务端未提供该字段）"))
                    .Append("</tr>");
            }
        }
        html.Append("</table>");
        html.Append(StationHoldingRendering.UnavailableVehicles(fact, "车辆一侧的情况"));
        return html.ToString();
    }

    private static string Scope(JsonElement point) =>
        point.TryGetProperty("vehicleScope", out JsonElement scope) && scope.ValueKind == JsonValueKind.Array &&
        scope.GetArrayLength() > 0
            ? string.Join("、", scope.EnumerateArray().Select(vehicle => vehicle.ToString()))
            : "同图全部车辆";

    private static string Registration(JsonElement point)
    {
        if (StationHoldingRendering.Str(point, "registrationNoteDescription") is { } note)
        {
            return note;
        }
        return point.TryGetProperty("disabled", out JsonElement disabled) && disabled.ValueKind == JsonValueKind.True
            ? "停用：不接新的空闲返回"
            : "启用";
    }
}
