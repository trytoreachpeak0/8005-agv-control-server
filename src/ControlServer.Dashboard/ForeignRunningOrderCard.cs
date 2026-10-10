using System.Text;
using System.Text.Json;

namespace ControlServer.Dashboard;

/// <summary>
/// 车队视图：我们车上正在运行、却不是本服务端建的订单（control-server#330）。
/// </summary>
/// <remarks>
/// 只有此刻挡着车的那几张：服务端正在取消的、取消过仍未终结要人处理的、归属证明不了只挡不取消的。每行说清楚是哪辆车、哪张单、
/// 为什么挡着、要不要人去做什么；数据面见 <c>ForeignRunningOrdersQueryEndpoint</c>。
/// </remarks>
public sealed class ForeignRunningOrderCard : IDashboardCard
{
    public string CardId => "foreign-running-orders";

    public string Title => "车上的外来订单";

    public DashboardView View => DashboardView.Fleet;

    public string SourcePath => DashboardPaths.QueryPrefix + "foreign-running-orders";

    public string RenderFact(JsonElement fact)
    {
        StringBuilder html = new();
        if (fact.ValueKind != JsonValueKind.Array || fact.GetArrayLength() == 0)
        {
            return "<p>没有外来订单挡着车。</p>";
        }

        html.Append("<table><tr><th>agvId</th><th>deviceKey</th><th>RIoT 订单</th><th>upperId</th><th>归属依据</th>")
            .Append("<th>原因码</th><th>说明</th><th>认出时刻</th><th>取消发出时刻</th><th>取消结果</th></tr>");
        foreach (JsonElement order in fact.EnumerateArray())
        {
            html.Append("<tr>")
                .Append(DashboardPageRenderer.Cell(DashboardPageRenderer.Text(order, "agvId")))
                .Append(DashboardPageRenderer.Cell(DashboardPageRenderer.Text(order, "deviceKey")))
                .Append(DashboardPageRenderer.Cell(DashboardPageRenderer.Text(order, "riotOrderId")))
                .Append(DashboardPageRenderer.Cell(DashboardPageRenderer.Text(order, "upperId")))
                .Append(DashboardPageRenderer.Cell(DashboardPageRenderer.Text(order, "ownershipBasis")))
                .Append(DashboardPageRenderer.Cell(DashboardPageRenderer.Text(order, "reasonCode")))
                .Append(DashboardPageRenderer.Cell(DashboardPageRenderer.Text(order, "reasonDescription")))
                .Append(DashboardPageRenderer.Cell(DashboardPageRenderer.Text(order, "detectedAt")))
                .Append(DashboardPageRenderer.Cell(DashboardPageRenderer.Text(order, "cancelSentAt")))
                .Append(DashboardPageRenderer.Cell(DashboardPageRenderer.Text(order, "cancelResult")))
                .Append("</tr>");
        }
        html.Append("</table>");
        return html.ToString();
    }
}
