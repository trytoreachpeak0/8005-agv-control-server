using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ControlServer.Dashboard;

/// <summary>
/// 车队视图「充电告警」（批次9-10，control-server#408；规格 5.5、8.6，REQ-0287）：此刻还成立的充电告警，按严重程度排，每条写码与中文说明、
/// 车与桩、自何时、已持续多久、现场该做什么。本批不做告警推送，现场看告警只靠看板与日志。
/// </summary>
public sealed class ChargingAlarmCard : IDashboardCard
{
    public string CardId => "charging-alarms";

    public string Title => "充电告警";

    public DashboardView View => DashboardView.Fleet;

    public string SourcePath => DashboardPaths.QueryPrefix + "charging-alarms";

    public string RenderFact(JsonElement fact)
    {
        StringBuilder html = new();
        if (ChargingCardRendering.Count(fact, "alarms") == 0)
        {
            html.Append(ChargingCardRendering.Paragraph("charging-alarms-none", "此刻没有充电告警"));
        }
        else
        {
            html.Append("<table><tr><th>严重程度</th><th>码</th><th>agvId</th><th>桩</th><th>自</th><th>已持续</th>")
                .Append("<th>现场该做什么</th><th>细节</th></tr>");
            ChargingCardRendering.Rows(html, fact, "alarms", Alarm);
            html.Append("</table>");
        }
        foreach (JsonElement note in ChargingCardRendering.Items(fact, "notReadable"))
        {
            html.Append(ChargingCardRendering.Paragraph("charging-alarm-not-readable", note.ToString()));
        }
        html.Append(StationHoldingRendering.UnavailableVehicles(fact, "车辆一侧的情况"));
        return html.ToString();
    }

    private static string Alarm(JsonElement alarm)
    {
        string severity = StationHoldingRendering.Str(alarm, "severity") ?? "";
        string style = severity switch
        {
            "CRITICAL" => "background:#c00;color:#fff;font-weight:bold",
            "HIGH" => "background:#f8d7da;color:#900",
            _ => "background:#fff3cd;color:#664d03",
        };
        string station = StationHoldingRendering.Str(alarm, "stationId") is { } stationId
            ? $"{DashboardPageRenderer.Text(alarm, "mapId")}/{stationId}"
            : "";
        string standing = alarm.TryGetProperty("standingSeconds", out JsonElement seconds) && seconds.ValueKind == JsonValueKind.Number
            ? Duration(seconds.GetInt64())
            : "";
        return $"<tr class=\"charging-alarm-{severity.ToLowerInvariant()}\" style=\"{style}\">"
               + DashboardPageRenderer.Cell(ChargingCardRendering.Coded(alarm, "severity", "severityDescription"))
               + DashboardPageRenderer.Cell(ChargingCardRendering.Coded(alarm, "code", "codeDescription"))
               + DashboardPageRenderer.Cell(StationHoldingRendering.Str(alarm, "agvId") ?? "")
               + DashboardPageRenderer.Cell(station)
               + DashboardPageRenderer.Cell(StationHoldingRendering.Str(alarm, "since") ?? "")
               + DashboardPageRenderer.Cell(standing)
               + DashboardPageRenderer.Cell(DashboardPageRenderer.Text(alarm, "fieldAction"))
               + DashboardPageRenderer.Cell(DashboardPageRenderer.Text(alarm, "detail"))
               + "</tr>";
    }

    private static string Duration(long seconds)
    {
        TimeSpan span = TimeSpan.FromSeconds(seconds);
        return span.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(long)span.TotalHours} 小时 {span.Minutes} 分")
            : string.Create(CultureInfo.InvariantCulture, $"{span.Minutes} 分 {span.Seconds} 秒");
    }
}
