using System.Net;
using System.Text;
using System.Text.Json;

namespace ControlServer.Dashboard;

/// <summary>
/// 车队视图「充电桩」（批次9-10，control-server#408；REQ-0171、REQ-0173、REQ-0177，规格 5.5）：名册版本与批准、变更记录；名册为空时整张卡片
/// 顶上醒目写明「名册为空：自动充电已停，需要充电的车等人工充电」与在等人工充电的车；每个桩谁占着、哪个阶段、自何时、占了多久、
/// 为什么还没放，以及它的分配暂停。
/// </summary>
public sealed class ChargerCard : IDashboardCard
{
    public string CardId => "chargers";

    public string Title => "充电桩";

    public DashboardView View => DashboardView.Fleet;

    public string SourcePath => DashboardPaths.QueryPrefix + "chargers";

    public string RenderFact(JsonElement fact)
    {
        StringBuilder html = new();
        if (StationHoldingRendering.Str(fact, "rosterEmptyBanner") is { } banner)
        {
            html.Append("<section class=\"charger-roster-empty alarm\" style=\"border:2px solid #c00;background:#f8d7da;color:#900\">")
                .Append("<p style=\"font-size:1.4em;font-weight:bold\">").Append(WebUtility.HtmlEncode(banner)).Append("</p>");
            string[] waiting =
            [
                .. ChargingCardRendering.Items(fact, "manualChargingHoldVehicles")
                    .Select(vehicle => $"{DashboardPageRenderer.Text(vehicle, "agvId")}（{DashboardPageRenderer.Text(vehicle, "reason")}，"
                                       + $"自 {DashboardPageRenderer.Text(vehicle, "since")}）"),
            ];
            html.Append(ChargingCardRendering.Paragraph(
                    "charger-roster-empty-vehicles",
                    waiting.Length == 0 ? "此刻没有车在等人工充电" : "正在等人工充电的车：" + string.Join("、", waiting)))
                .Append("</section>");
        }

        if (fact.TryGetProperty("roster", out JsonElement roster) && roster.ValueKind == JsonValueKind.Object)
        {
            string text = StationHoldingRendering.Str(roster, "version") is { } version
                ? $"当前名册 v{version}（{DashboardPageRenderer.Text(roster, "state")}：{DashboardPageRenderer.Text(roster, "stateDescription")}），"
                  + $"批准人 {DashboardPageRenderer.Text(roster, "approvedBy")}，批准依据 {DashboardPageRenderer.Text(roster, "approvalBasis")}，"
                  + $"载入于 {DashboardPageRenderer.Text(roster, "loadedAt")}，变更说明 {StationHoldingRendering.Str(roster, "changeNote") ?? "（无）"}，"
                  + $"桩数 {DashboardPageRenderer.Text(roster, "chargerCount")}，内容摘要 {DashboardPageRenderer.Text(roster, "contentSha256")}"
                : $"{DashboardPageRenderer.Text(roster, "state")}：{DashboardPageRenderer.Text(roster, "stateDescription")}";
            html.Append(ChargingCardRendering.Paragraph("charger-roster", text));
        }

        html.Append("<table class=\"charger-roster-history\"><tr><th>名册版本</th><th>载入于</th><th>批准人</th><th>批准依据</th>")
            .Append("<th>变更说明</th><th>桩数</th></tr>");
        ChargingCardRendering.Rows(html, fact, "rosterHistory", version => "<tr>"
            + DashboardPageRenderer.Cell("v" + DashboardPageRenderer.Text(version, "version"))
            + DashboardPageRenderer.Cell(DashboardPageRenderer.Text(version, "loadedAt"))
            + DashboardPageRenderer.Cell(DashboardPageRenderer.Text(version, "approvedBy"))
            + DashboardPageRenderer.Cell(DashboardPageRenderer.Text(version, "approvalBasis"))
            + DashboardPageRenderer.Cell(StationHoldingRendering.Str(version, "changeNote") ?? "")
            + DashboardPageRenderer.Cell(DashboardPageRenderer.Text(version, "chargerCount"))
            + "</tr>");
        html.Append("</table>");

        html.Append("<table class=\"chargers\"><tr><th>地图</th><th>站点</th><th>站名</th><th>候选车辆</th><th>名册</th>")
            .Append("<th>阶段</th><th>为什么还没放</th>")
            .Append(StationHoldingRendering.HeaderCells)
            .Append("<th>分配暂停</th><th>清桩</th></tr>");
        ChargingCardRendering.Rows(html, fact, "chargers", Charger);
        html.Append("</table>");
        html.Append(ChargingCardRendering.HostEntries(fact));
        html.Append(StationHoldingRendering.UnavailableVehicles(fact, "车辆一侧的情况"));
        return html.ToString();
    }

    private static string Charger(JsonElement charger)
    {
        bool overdue = charger.TryGetProperty("overdue", out JsonElement flag) && flag.ValueKind == JsonValueKind.True;
        StringBuilder row = new(overdue ? "<tr class=\"charger-overdue\" style=\"background:#fff3cd\">" : "<tr>");
        row.Append(DashboardPageRenderer.Cell(DashboardPageRenderer.Text(charger, "mapId")))
            .Append(DashboardPageRenderer.Cell(DashboardPageRenderer.Text(charger, "stationId")))
            .Append(DashboardPageRenderer.Cell(StationHoldingRendering.Str(charger, "stationName") ?? ""))
            .Append(DashboardPageRenderer.Cell(Scope(charger)))
            .Append(DashboardPageRenderer.Cell(StationHoldingRendering.Str(charger, "rosterNoteDescription") ?? "在当前名册里"))
            .Append(DashboardPageRenderer.Cell(Stage(charger)))
            .Append(DashboardPageRenderer.Cell(Why(charger)))
            .Append(charger.TryGetProperty("holding", out JsonElement holding)
                ? StationHoldingRendering.Cells(holding)
                : StationHoldingRendering.MissingCells())
            .Append(DashboardPageRenderer.Cell(Holds(charger)))
            .Append(DashboardPageRenderer.Cell(Clearance(charger)))
            .Append("</tr>");
        return row.ToString();
    }

    private static string Scope(JsonElement charger) =>
        charger.TryGetProperty("inCurrentRoster", out JsonElement inRoster) && inRoster.ValueKind == JsonValueKind.False
            ? ""
            : charger.TryGetProperty("vehicleScope", out JsonElement scope) && scope.ValueKind == JsonValueKind.Array &&
              scope.GetArrayLength() > 0
                ? string.Join("、", scope.EnumerateArray().Select(vehicle => vehicle.ToString()))
                : "名册上的全部车辆";

    private static string Stage(JsonElement charger)
    {
        string stage = ChargingCardRendering.Coded(charger, "stage", "stageDescription");
        return StationHoldingRendering.Str(charger, "stageSeconds") is { } seconds
            ? $"{stage}；自 {DashboardPageRenderer.Text(charger, "stageSince")}，已 {seconds} 秒"
            : stage;
    }

    private static string Why(JsonElement charger)
    {
        List<string> parts = [];
        if (StationHoldingRendering.Str(charger, "overdueDescription") is { } overdue)
        {
            parts.Add(overdue);
        }
        if (ChargingCardRendering.JourneyCode(charger) is { Length: > 0 } code)
        {
            parts.Add(code);
        }
        if (charger.TryGetProperty("cycle", out JsonElement cycle) && cycle.ValueKind == JsonValueKind.Object &&
            ChargingCardRendering.Coded(cycle, "endReason", "endReasonDescription") is { Length: > 0 } ended)
        {
            parts.Add($"周期已结束于 {DashboardPageRenderer.Text(cycle, "endedAt")}：{ended}");
        }
        return string.Join("。", parts);
    }

    private static string Holds(JsonElement charger)
    {
        List<string> holds = [];
        foreach (JsonElement hold in ChargingCardRendering.Items(charger, "allocationHolds"))
        {
            string vehicle = StationHoldingRendering.Str(hold, "agvId") is { } agvId ? $"，车 {agvId}" : "";
            string confirmed = StationHoldingRendering.Str(hold, "confirmedByPersonId") is { } person
                ? $"，确认人 {person}（{DashboardPageRenderer.Text(hold, "confirmedByRole")}）"
                : "";
            string disposition = StationHoldingRendering.Str(hold, "siteDisposition") is { } site ? $"，现场处置 {site}" : "";
            holds.Add($"{ChargingCardRendering.Coded(hold, "trigger", "triggerDescription")}；自 {DashboardPageRenderer.Text(hold, "heldAt")}"
                      + $"{vehicle}，根因 {DashboardPageRenderer.Text(hold, "rootCause")}{confirmed}{disposition}，"
                      + $"暂停事件 {DashboardPageRenderer.Text(hold, "holdId")}（全部字段见数据面 {DashboardPaths.QueryPrefix}chargers）");
        }
        return holds.Count == 0 ? "没有暂停" : string.Join("；", holds);
    }

    private static string Clearance(JsonElement charger)
    {
        if (!charger.TryGetProperty("clearance", out JsonElement clearance) || clearance.ValueKind != JsonValueKind.Object)
        {
            return "";
        }
        string confirmed = StationHoldingRendering.Str(clearance, "confirmedAt") is { } at
            ? $"人工确认已记下于 {at}（{DashboardPageRenderer.Text(clearance, "confirmedBy")}，"
              + $"{DashboardPageRenderer.Text(clearance, "confirmedByRole")}），清桩还没完成（旧单终结、桩放掉的那一轮才完成）"
            : "还没有人工确认";
        return $"清桩开始于 {DashboardPageRenderer.Text(clearance, "startedAt")}；{confirmed}";
    }
}
