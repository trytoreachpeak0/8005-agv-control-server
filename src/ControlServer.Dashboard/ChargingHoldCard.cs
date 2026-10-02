using System.Text;
using System.Text.Json;

namespace ControlServer.Dashboard;

/// <summary>
/// 车队视图「充电暂停与等待」（批次9-10，control-server#408；REQ-0171、REQ-0178、REQ-0179、REQ-0285）：车辆充电资格暂停、服务端持有的
/// <c>ManualChargingHold</c>（原因、何时进入、怎么解除）、清桩中的车与最近的清桩记录。只读，操作走服务端接口。
/// </summary>
public sealed class ChargingHoldCard : IDashboardCard
{
    public string CardId => "charging-holds";

    public string Title => "充电暂停与等待";

    public DashboardView View => DashboardView.Fleet;

    public string SourcePath => DashboardPaths.QueryPrefix + "charging-holds";

    public string RenderFact(JsonElement fact)
    {
        StringBuilder html = new();

        html.Append("<h3>服务端持有的人工充电等待（ManualChargingHold）</h3>");
        if (ChargingCardRendering.Count(fact, "manualChargingHolds") == 0)
        {
            html.Append(ChargingCardRendering.Paragraph("manual-charging-holds-none", "没有车在人工充电等待中"));
        }
        else
        {
            html.Append("<table class=\"manual-charging-holds\"><tr><th>agvId</th><th>原因</th><th>进入于</th><th>怎么解除</th></tr>");
            ChargingCardRendering.Rows(html, fact, "manualChargingHolds", hold => "<tr>"
                + DashboardPageRenderer.Cell(ChargingCardRendering.Vehicle(hold))
                + DashboardPageRenderer.Cell(ChargingCardRendering.Coded(hold, "reason", "reasonDescription"))
                + DashboardPageRenderer.Cell(DashboardPageRenderer.Text(hold, "since"))
                + DashboardPageRenderer.Cell(DashboardPageRenderer.Text(hold, "releaseHint"))
                + "</tr>");
            html.Append("</table>");
        }

        html.Append("<h3>清桩中的车</h3>");
        if (ChargingCardRendering.Count(fact, "clearing") == 0)
        {
            html.Append(ChargingCardRendering.Paragraph("charging-clearing-none", "没有车在清桩中"));
        }
        else
        {
            html.Append("<table class=\"charging-clearing\"><tr><th>agvId</th><th>桩</th><th>此刻的码</th><th>人工确认</th>")
                .Append("<th>现场要做的</th></tr>");
            ChargingCardRendering.Rows(html, fact, "clearing", Clearing);
            html.Append("</table>");
        }

        html.Append("<h3>车辆充电资格暂停</h3>");
        if (StationHoldingRendering.Str(fact, "eligibilityHoldsNote") is { } note)
        {
            html.Append(ChargingCardRendering.Paragraph("charging-eligibility-note", note));
        }
        if (ChargingCardRendering.Count(fact, "eligibilityHolds") > 0)
        {
            html.Append("<table class=\"charging-eligibility-holds\"><tr><th>agvId</th><th>原因</th><th>暂停于</th><th>周期</th></tr>");
            ChargingCardRendering.Rows(html, fact, "eligibilityHolds", hold => "<tr>"
                + DashboardPageRenderer.Cell(ChargingCardRendering.Vehicle(hold))
                + DashboardPageRenderer.Cell(ChargingCardRendering.Coded(hold, "reason", "reasonDescription"))
                + DashboardPageRenderer.Cell(DashboardPageRenderer.Text(hold, "heldAt"))
                + DashboardPageRenderer.Cell(StationHoldingRendering.Str(hold, "cycleId") ?? "")
                + "</tr>");
            html.Append("</table>");
        }

        html.Append("<h3>最近的清桩记录</h3>");
        if (ChargingCardRendering.Count(fact, "recentClearances") == 0)
        {
            html.Append(ChargingCardRendering.Paragraph("charging-clearances-none", "还没有完成过清桩"));
        }
        else
        {
            html.Append("<table class=\"charging-recent-clearances\"><tr><th>agvId</th><th>桩</th><th>开始于</th><th>人工确认于</th>")
                .Append("<th>清桩完成于</th><th>证明</th><th>确认人</th><th>协助者</th><th>车最终位置</th><th>旧单处置</th>")
                .Append("<th>腾空情况</th></tr>");
            ChargingCardRendering.Rows(html, fact, "recentClearances", record => "<tr>"
                + DashboardPageRenderer.Cell(DashboardPageRenderer.Text(record, "agvId"))
                + DashboardPageRenderer.Cell($"{DashboardPageRenderer.Text(record, "mapId")}/{DashboardPageRenderer.Text(record, "stationId")}")
                + DashboardPageRenderer.Cell(DashboardPageRenderer.Text(record, "startedAt"))
                + DashboardPageRenderer.Cell(StationHoldingRendering.Str(record, "confirmedAt") ?? "")
                + DashboardPageRenderer.Cell(DashboardPageRenderer.Text(record, "completedAt"))
                + DashboardPageRenderer.Cell(ChargingCardRendering.Coded(record, "proof", "proofDescription"))
                + DashboardPageRenderer.Cell(StationHoldingRendering.Str(record, "confirmedBy") is { } by
                    ? $"{by}（{DashboardPageRenderer.Text(record, "confirmedByRole")}）"
                    : "")
                + DashboardPageRenderer.Cell(DashboardPageRenderer.Text(record, "assistantsJson"))
                + DashboardPageRenderer.Cell(StationHoldingRendering.Str(record, "vehicleFinalPosition") ?? "")
                + DashboardPageRenderer.Cell(StationHoldingRendering.Str(record, "oldOrderDisposition") ?? "")
                + DashboardPageRenderer.Cell(StationHoldingRendering.Str(record, "clearedCondition") ?? "")
                + "</tr>");
            html.Append("</table>");
        }

        html.Append(ChargingCardRendering.HostEntries(fact));
        html.Append(StationHoldingRendering.UnavailableVehicles(fact, "车辆一侧的情况"));
        return html.ToString();
    }

    private static string Clearing(JsonElement clearing)
    {
        string station = $"{DashboardPageRenderer.Text(clearing, "mapId")}/{DashboardPageRenderer.Text(clearing, "stationId")}"
                         + (StationHoldingRendering.Str(clearing, "stationName") is { } name ? " " + name : "");
        string confirmed = "还没有人工确认";
        if (clearing.TryGetProperty("clearance", out JsonElement clearance) && clearance.ValueKind == JsonValueKind.Object)
        {
            confirmed = StationHoldingRendering.Str(clearance, "confirmedAt") is { } at
                ? $"人工确认已记下于 {at}（{DashboardPageRenderer.Text(clearance, "confirmedBy")}，"
                  + $"{DashboardPageRenderer.Text(clearance, "confirmedByRole")}）；清桩还没完成，旧单终结、桩放掉的那一轮才完成"
                : $"清桩开始于 {DashboardPageRenderer.Text(clearance, "startedAt")}，还没有人工确认";
        }
        return "<tr>"
               + DashboardPageRenderer.Cell(ChargingCardRendering.Vehicle(clearing))
               + DashboardPageRenderer.Cell(station)
               + DashboardPageRenderer.Cell(ChargingCardRendering.JourneyCode(clearing))
               + DashboardPageRenderer.Cell(confirmed)
               + DashboardPageRenderer.Cell(DashboardPageRenderer.Text(clearing, "guidance"))
               + "</tr>";
    }
}
