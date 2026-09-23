using System.Text;
using System.Text.Json;

namespace ControlServer.Dashboard;

/// <summary>
/// 车队视图：我们车上正在运行、却不是本服务端建的订单（control-server#330）。
/// </summary>
public sealed class ForeignRunningOrderCard : IDashboardCard
{
    public string CardId => "foreign-running-orders";

    public string Title => "车上的外来订单";

    public DashboardView View => DashboardView.Fleet;

    public string SourcePath => DashboardPaths.QueryPrefix + "foreign-running-orders";

    public string RenderFact(JsonElement fact)
    {
        StringBuilder html = new();
        html.Append("<table><tr><th>agvId</th><th>订单</th></tr>");
        html.Append("</table>");
        return html.ToString();
    }
}
