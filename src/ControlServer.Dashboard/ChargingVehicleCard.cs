using System.Text;
using System.Text.Json;

namespace ControlServer.Dashboard;

/// <summary>
/// 车队视图「逐车充电状态」（批次9-10，control-server#408）：每台车的用途、<c>chargingCycleState</c>、周期阶段、目标桩、名册与策略版本、
/// 充电旅程上的码，是否在人工充电等待或资格暂停。电量、batteryState 与排队原因服务端没有可读的现状，照实写读不到；听不到的车只写失联。
/// </summary>
public sealed class ChargingVehicleCard : IDashboardCard
{
    public string CardId => "charging-vehicles";

    public string Title => "逐车充电状态";

    public DashboardView View => DashboardView.Fleet;

    public string SourcePath => DashboardPaths.QueryPrefix + "charging-vehicles";

    public string RenderFact(JsonElement fact)
    {
        StringBuilder html = new();
        if (StationHoldingRendering.Str(fact, "batteryNotReadable") is { } battery)
        {
            html.Append(ChargingCardRendering.Paragraph("charging-battery-not-readable", "电量与 batteryState：" + battery));
        }
        if (StationHoldingRendering.Str(fact, "queueReasonNotReadable") is { } queue)
        {
            html.Append(ChargingCardRendering.Paragraph("charging-queue-not-readable", "排队原因：" + queue));
        }

        html.Append("<table><tr><th>agvId</th><th>电量</th><th>batteryState</th><th>用途</th><th>chargingCycleState</th>")
            .Append("<th>周期阶段</th><th>目标桩</th><th>名册版本</th><th>策略版本</th><th>充电旅程的码</th><th>排队原因</th>")
            .Append("<th>人工充电等待</th><th>充电资格暂停</th></tr>");
        ChargingCardRendering.Rows(html, fact, "vehicles", Vehicle);
        html.Append("</table>");
        html.Append(StationHoldingRendering.UnavailableVehicles(fact, "电量、用途与充电周期"));
        return html.ToString();
    }

    private static string Vehicle(JsonElement vehicle)
    {
        JsonElement cycle = vehicle.TryGetProperty("cycle", out JsonElement value) ? value : default;
        bool hasCycle = cycle.ValueKind == JsonValueKind.Object;
        string purpose = StationHoldingRendering.Str(vehicle, "purpose") ?? "无";
        string? purposeDescription = StationHoldingRendering.Str(vehicle, "purposeDescription");
        string holder = StationHoldingRendering.Str(vehicle, "holderJourneyId") is { } journeyId
            ? $"；持有者 {StationHoldingRendering.Str(vehicle, "holderKindDescription") ?? "旅程"} {journeyId}"
            : "";
        string manual = vehicle.TryGetProperty("manualChargingHold", out JsonElement hold) && hold.ValueKind == JsonValueKind.Object
            ? $"{ChargingCardRendering.Coded(hold, "reason", "reasonDescription")}，自 {DashboardPageRenderer.Text(hold, "since")}"
            : "";
        bool eligibilityHeld = vehicle.TryGetProperty("eligibilityHeld", out JsonElement held) && held.ValueKind == JsonValueKind.True;
        return "<tr>"
               + DashboardPageRenderer.Cell(DashboardPageRenderer.Text(vehicle, "agvId"))
               + DashboardPageRenderer.Cell("读不到（见上）")
               + DashboardPageRenderer.Cell("读不到（见上）")
               + DashboardPageRenderer.Cell((purposeDescription is null ? purpose : $"{purpose}（{purposeDescription}）") + holder)
               + DashboardPageRenderer.Cell(ChargingCardRendering.Coded(vehicle, "chargingCycleState", "chargingCycleStateDescription"))
               + DashboardPageRenderer.Cell(hasCycle ? ChargingCardRendering.Coded(cycle, "phase", "phaseDescription") : "")
               + DashboardPageRenderer.Cell(hasCycle ? Target(cycle) : "")
               + DashboardPageRenderer.Cell(hasCycle ? "充电桩名册 v" + DashboardPageRenderer.Text(cycle, "chargerRosterVersion") : "")
               + DashboardPageRenderer.Cell(hasCycle ? "充电策略 v" + DashboardPageRenderer.Text(cycle, "chargingPolicyVersion") : "")
               + DashboardPageRenderer.Cell(ChargingCardRendering.JourneyCode(vehicle))
               + DashboardPageRenderer.Cell("读不到（见上）")
               + DashboardPageRenderer.Cell(manual)
               + DashboardPageRenderer.Cell(eligibilityHeld ? "暂停中（见「充电暂停与等待」）" : "")
               + "</tr>";
    }

    private static string Target(JsonElement cycle)
    {
        string name = StationHoldingRendering.Str(cycle, "targetStationName") is { } stationName ? " " + stationName : "";
        return $"{DashboardPageRenderer.Text(cycle, "targetMapId")}/{DashboardPageRenderer.Text(cycle, "targetStationId")}{name}，"
               + $"分配于 {DashboardPageRenderer.Text(cycle, "allocatedAt")}";
    }
}
