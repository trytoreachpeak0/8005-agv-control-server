using System.Text;
using System.Text.Json;

namespace ControlServer.Dashboard;

/// <summary>
/// 车队视图「公共站点占用」（批次8-21，control-server#392；REQ-0204 修订）：每个固定公共站点的预占与占用，与等待点同一种显示。
/// 已不在生效绑定集里却仍被持有的站点照样列出并写明。
/// </summary>
public sealed class FixedTaskStationCard : IDashboardCard
{
    public string CardId => "fixed-task-stations";

    public string Title => "公共站点占用";

    public DashboardView View => DashboardView.Fleet;

    public string SourcePath => DashboardPaths.QueryPrefix + "fixed-task-stations";

    public string RenderFact(JsonElement fact)
    {
        StringBuilder html = new();
        html.Append("<table><tr><th>地图</th><th>站点</th><th>站名</th><th>任务类型</th><th>绑定</th>")
            .Append(StationHoldingRendering.HeaderCells)
            .Append("</tr>");
        if (fact.TryGetProperty("stations", out JsonElement stations) && stations.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement station in stations.EnumerateArray())
            {
                html.Append("<tr>")
                    .Append(DashboardPageRenderer.Cell(DashboardPageRenderer.Text(station, "mapId")))
                    .Append(DashboardPageRenderer.Cell(DashboardPageRenderer.Text(station, "stationId")))
                    .Append(DashboardPageRenderer.Cell(StationHoldingRendering.Str(station, "stationName") ?? ""))
                    .Append(DashboardPageRenderer.Cell(TaskTypes(station)))
                    .Append(DashboardPageRenderer.Cell(Binding(station)))
                    .Append(station.TryGetProperty("holding", out JsonElement holding)
                        ? StationHoldingRendering.Cells(holding)
                        : StationHoldingRendering.MissingCells())
                    .Append("</tr>");
            }
        }
        html.Append("</table>");
        html.Append(StationHoldingRendering.UnavailableVehicles(fact, "车辆一侧的情况"));
        return html.ToString();
    }

    private static string TaskTypes(JsonElement station) =>
        station.TryGetProperty("taskTypes", out JsonElement types) && types.ValueKind == JsonValueKind.Array
            ? string.Join("、", types.EnumerateArray().Select(type => type.ToString()))
            : "";

    private static string Binding(JsonElement station) =>
        StationHoldingRendering.Str(station, "registrationNoteDescription")
        ?? (StationHoldingRendering.Str(station, "bindingSetVersion") is { } version ? $"生效绑定集 v{version}" : "");
}
