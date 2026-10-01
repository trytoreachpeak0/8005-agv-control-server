using System.Text;
using System.Text.Json;

namespace ControlServer.Dashboard;

/// <summary>
/// 车队视图「车辆用途」（批次8-21，control-server#392）：每台车此刻的用途、持有者与取得时刻。听不到的车只写失联，不写它失联前的用途。
/// </summary>
public sealed class VehiclePurposeCard : IDashboardCard
{
    public string CardId => "vehicle-purposes";

    public string Title => "车辆用途";

    public DashboardView View => DashboardView.Fleet;

    public string SourcePath => DashboardPaths.QueryPrefix + "vehicle-purposes";

    public string RenderFact(JsonElement fact)
    {
        StringBuilder html = new();
        html.Append("<table><tr><th>agvId</th><th>用途</th><th>持有者</th><th>取得时刻</th></tr>");
        if (fact.TryGetProperty("vehicles", out JsonElement vehicles) && vehicles.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement vehicle in vehicles.EnumerateArray())
            {
                string purpose = StationHoldingRendering.Str(vehicle, "purpose") ?? "无";
                string? description = StationHoldingRendering.Str(vehicle, "purposeDescription");
                string? journeyId = StationHoldingRendering.Str(vehicle, "holderJourneyId");
                html.Append("<tr>")
                    .Append(DashboardPageRenderer.Cell(DashboardPageRenderer.Text(vehicle, "agvId")))
                    .Append(DashboardPageRenderer.Cell(description is null ? purpose : $"{purpose}（{description}）"))
                    .Append(DashboardPageRenderer.Cell(journeyId is null
                        ? ""
                        : $"{StationHoldingRendering.Str(vehicle, "holderKindDescription") ?? "旅程"} {journeyId}"))
                    .Append(DashboardPageRenderer.Cell(StationHoldingRendering.Str(vehicle, "claimedAt") ?? ""))
                    .Append("</tr>");
            }
        }
        html.Append("</table>");
        html.Append(StationHoldingRendering.UnavailableVehicles(fact, "用途"));
        return html.ToString();
    }
}
