using System.Text;
using System.Text.Json;

namespace ControlServer.Dashboard;

/// <summary>
/// 车队视图「逐车充电状态」（批次9-10，control-server#408）：每台车的用途、<c>chargingCycleState</c>、周期阶段、目标桩、名册与策略版本、
/// 充电旅程上的码，是否在人工充电等待或资格暂停；电量、batteryState 与排队原因取自最近一轮已完成的充电分配，没评估时照实写没评估、不写任何数。
/// 听不到的车只写失联。
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
        JsonElement allocation = vehicle.TryGetProperty("allocation", out JsonElement found) ? found : default;
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
               + DashboardPageRenderer.Cell(Battery(allocation))
               + DashboardPageRenderer.Cell(allocation.ValueKind == JsonValueKind.Object
                   ? ChargingCardRendering.Coded(allocation, "batteryState", "batteryStateDescription")
                   : "")
               + DashboardPageRenderer.Cell((purposeDescription is null ? purpose : $"{purpose}（{purposeDescription}）") + holder)
               + DashboardPageRenderer.Cell(ChargingCardRendering.Coded(vehicle, "chargingCycleState", "chargingCycleStateDescription"))
               + DashboardPageRenderer.Cell(hasCycle ? ChargingCardRendering.Coded(cycle, "phase", "phaseDescription") : "")
               + DashboardPageRenderer.Cell(hasCycle ? Target(cycle) : "")
               + DashboardPageRenderer.Cell(hasCycle ? "充电桩名册 v" + DashboardPageRenderer.Text(cycle, "chargerRosterVersion") : "")
               + DashboardPageRenderer.Cell(hasCycle ? "充电策略 v" + DashboardPageRenderer.Text(cycle, "chargingPolicyVersion") : "")
               + DashboardPageRenderer.Cell(ChargingCardRendering.JourneyCode(vehicle))
               + DashboardPageRenderer.Cell(QueueReason(allocation))
               + DashboardPageRenderer.Cell(manual)
               + DashboardPageRenderer.Cell(eligibilityHeld ? "暂停中（见「充电暂停与等待」）" : "本版本未实施（批次9-09）")
               + "</tr>";
    }

    /// <summary>电量与读数来源；没评估时只写那一句话（同一句也写在排队原因那一格），不写任何数。</summary>
    private static string Battery(JsonElement allocation)
    {
        if (allocation.ValueKind != JsonValueKind.Object)
        {
            return "（服务端未提供该字段）";
        }
        if (StationHoldingRendering.Str(allocation, "note") is { } note)
        {
            return note;
        }
        if (StationHoldingRendering.Str(allocation, "batteryPercent") is not { } percent)
        {
            // Offline, disabled, or no battery reported: there is no current reading, and no number is shown (REQ-0269).
            return $"电量拿不到：RIoT 没报电量，或报这辆车离线、被禁用（取自充电分配 {DashboardPageRenderer.Text(allocation, "passCompletedAt")} 走完的那一轮）";
        }
        return $"{percent}%（RIoT 读数，观测于 {DashboardPageRenderer.Text(allocation, "batteryObservedAt")}，"
               + $"RIoT 电池状态 {StationHoldingRendering.Str(allocation, "riotBatteryState") ?? "没报"}；"
               + $"取自充电分配 {DashboardPageRenderer.Text(allocation, "passCompletedAt")} 走完的那一轮）";
    }

    private static string QueueReason(JsonElement allocation)
    {
        if (allocation.ValueKind != JsonValueKind.Object)
        {
            return "";
        }
        if (StationHoldingRendering.Str(allocation, "note") is { } note)
        {
            return note;
        }
        if (StationHoldingRendering.Str(allocation, "voidedReason") is not null)
        {
            // The battery this verdict rests on was not current: the server's sentence carries the original code in brackets.
            return StationHoldingRendering.Str(allocation, "reasonDescription") ?? "";
        }
        string reason = ChargingCardRendering.Coded(allocation, "reason", "reasonDescription");
        return StationHoldingRendering.Str(allocation, "detail") is { Length: > 0 } detail ? $"{reason}。细节：{detail}" : reason;
    }

    private static string Target(JsonElement cycle)
    {
        string name = StationHoldingRendering.Str(cycle, "targetStationName") is { } stationName ? " " + stationName : "";
        return $"{DashboardPageRenderer.Text(cycle, "targetMapId")}/{DashboardPageRenderer.Text(cycle, "targetStationId")}{name}，"
               + $"分配于 {DashboardPageRenderer.Text(cycle, "allocatedAt")}";
    }
}
