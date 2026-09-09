using System.Text;
using System.Text.Json;

namespace ControlServer.Dashboard;

/// <summary>
/// 车队视图的第一张真实数据卡片：每台车的会话就绪与原因码。
/// </summary>
/// <remarks>
/// 接入它没有改看板主文件——这个文件就是它的全部。
/// </remarks>
public sealed class FleetSessionReadinessCard : IDashboardCard
{
    public string CardId => "fleet-session-readiness";

    public string Title => "车队会话就绪";

    public DashboardView View => DashboardView.Fleet;

    public string SourcePath => DashboardPaths.QueryPrefix + "fleet-sessions";

    public string RenderFact(JsonElement fact)
    {
        StringBuilder html = new();
        html.Append("<table><tr><th>agvId</th><th>就绪</th><th>原因码</th></tr>");
        foreach (JsonElement vehicle in fact.EnumerateArray())
        {
            html.Append("<tr>")
                .Append(DashboardPageRenderer.Cell(DashboardPageRenderer.Text(vehicle, "agvId")))
                .Append(DashboardPageRenderer.Cell(DashboardPageRenderer.Text(vehicle, "readiness")))
                .Append(DashboardPageRenderer.Cell(DashboardPageRenderer.Text(vehicle, "reasonCode")))
                .Append("</tr>");
        }
        html.Append("</table>");
        return html.ToString();
    }
}
