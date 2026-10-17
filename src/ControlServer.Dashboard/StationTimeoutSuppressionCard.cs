using System.Text;
using System.Text.Json;

namespace ControlServer.Dashboard;

/// <summary>
/// 车队视图「站点超时抑制清单」（REQ-0365，CP-0016，control-server#751）：车到了取货站、离站期限内没人录入，系统已终结、不再派车的需求，
/// 货还在机台上，要人去搬或在 MES 处理。每行写批号、取货站点、终结时刻与说明；MES 关单后这一行自动消失。
/// </summary>
/// <remarks>
/// 同一批需求的告警也在派车积压卡片的结构性阻断里（那里按原因码列全部人工搬运告警）；这一张只收站点期限终结的，给现场一张按批号找料的单子。
/// </remarks>
public sealed class StationTimeoutSuppressionCard : IDashboardCard
{
    public string CardId => "station-timeout-suppressions";

    public string Title => "站点超时抑制清单";

    public DashboardView View => DashboardView.Fleet;

    public string SourcePath => DashboardPaths.QueryPrefix + "station-timeout-suppressions";

    public string RenderFact(JsonElement fact)
    {
        StringBuilder html = new();
        JsonElement[] rows = fact.TryGetProperty("suppressions", out JsonElement suppressions) && suppressions.ValueKind == JsonValueKind.Array
            ? [.. suppressions.EnumerateArray()]
            : [];
        if (rows.Length == 0)
        {
            html.Append(ChargingCardRendering.Paragraph("station-timeout-suppressions-none", "此刻没有因站点离站期限终结、等人工搬运的需求"));
            return html.ToString();
        }
        html.Append("<section class=\"station-timeout-suppressions alarm\" style=\"border:2px solid #c00;color:#900\">")
            .Append("<p>车到了取货站，离站期限内没人录入，系统已终结这些需求、不再派车；货还在机台上，请人工搬运或在 MES 处理，MES 关单后自动移出。</p>")
            .Append("<table><tr><th>批号</th><th>取货站点</th><th>终结时刻</th><th>需求</th><th>说明</th></tr>");
        foreach (JsonElement row in rows)
        {
            html.Append("<tr>")
                .Append(DashboardPageRenderer.Cell(StationHoldingRendering.Str(row, "sublot") ?? ""))
                .Append(DashboardPageRenderer.Cell(StationHoldingRendering.Str(row, "stationId") ?? "（未记下）"))
                .Append(DashboardPageRenderer.Cell(StationHoldingRendering.Str(row, "endedAt") ?? ""))
                .Append(DashboardPageRenderer.Cell(StationHoldingRendering.Str(row, "demandId") ?? ""))
                .Append(DashboardPageRenderer.Cell(ChargingCardRendering.Coded(row, "reasonCode", "reasonDescription")))
                .Append("</tr>");
        }
        html.Append("</table></section>");
        return html.ToString();
    }
}
