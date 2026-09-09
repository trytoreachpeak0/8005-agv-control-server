using System.Text;
using System.Text.Json;

namespace ControlServer.Dashboard;

/// <summary>
/// 仓位视图的第一张真实数据卡片：每台车当前生效的仓位配置版本与指纹。
/// </summary>
public sealed class SlotConfigurationCard : IDashboardCard
{
    public string CardId => "slot-active-configuration";

    public string Title => "生效仓位配置";

    public DashboardView View => DashboardView.Slots;

    public string SourcePath => DashboardPaths.QueryPrefix + "slot-configurations";

    public string RenderFact(JsonElement fact)
    {
        StringBuilder html = new();
        html.Append("<table><tr><th>agvId</th><th>配置版本</th><th>指纹</th></tr>");
        foreach (JsonElement vehicle in fact.EnumerateArray())
        {
            html.Append("<tr>")
                .Append(DashboardPageRenderer.Cell(DashboardPageRenderer.Text(vehicle, "agvId")))
                .Append(DashboardPageRenderer.Cell(
                    DashboardPageRenderer.Text(vehicle, "configurationVersion")))
                .Append(DashboardPageRenderer.Cell(DashboardPageRenderer.Text(vehicle, "fingerprint")))
                .Append("</tr>");
        }
        html.Append("</table>");
        return html.ToString();
    }
}
