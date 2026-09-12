using System.Text;
using System.Text.Json;

namespace ControlServer.Dashboard;

/// <summary>
/// 车队视图的告警卡片：每台车当下的全部告警（REQ-0270），或者拿不到它的原因。
/// </summary>
/// <remarks>
/// 接入它没有改看板主文件——这个文件就是它在看板这一侧的全部。失联的车这一行显示的是失联本身，
/// 不是它失联前的最后一批告警：一个不确定新旧的旧值比没有值更糟（REQ-0269）。
/// </remarks>
public sealed class OnboardAlarmCard : IDashboardCard
{
    public string CardId => "onboard-alarms";

    public string Title => "车载告警";

    public DashboardView View => DashboardView.Fleet;

    public string SourcePath => DashboardPaths.QueryPrefix + "onboard-alarms";

    public string RenderFact(JsonElement fact)
    {
        StringBuilder html = new();
        html.Append("<table><tr><th>agvId</th><th>告警</th></tr>");
        foreach (JsonElement vehicle in fact.EnumerateArray())
        {
            html.Append("<tr>").Append(DashboardPageRenderer.Cell(DashboardPageRenderer.Text(vehicle, "agvId")));
            html.Append(vehicle.TryGetProperty("available", out JsonElement available) && available.GetBoolean()
                ? DashboardPageRenderer.Cell(Alarms(vehicle))
                : DashboardPageRenderer.Cell(DashboardPageRenderer.Text(vehicle, "unavailableReason")));
            html.Append("</tr>");
        }
        html.Append("</table>");
        return html.ToString();
    }

    private static string Alarms(JsonElement vehicle)
    {
        if (!vehicle.TryGetProperty("alarms", out JsonElement alarms) || alarms.GetArrayLength() == 0)
        {
            return "无";
        }
        return string.Join(
            "、",
            alarms.EnumerateArray().Select(alarm => DashboardPageRenderer.Text(alarm, "alarmCode")));
    }
}
