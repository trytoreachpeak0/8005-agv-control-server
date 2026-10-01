using System.Text;
using System.Text.Json;

namespace ControlServer.Dashboard;

/// <summary>
/// 车队视图「空闲返回」（批次8-21，control-server#392；REQ-0290～0297）：每台车的空闲返回走到哪一步、原因码与中文说明、失败按 REQ-0296
/// 的哪一支、占着的等待点、最近一趟空闲返回怎么收的尾。
/// </summary>
/// <remarks>
/// 「在点」的车写「等待点占用直到离点证据满足」，不写成空闲。听不到的车只写失联，不写它失联前在哪一步。
/// </remarks>
public sealed class IdleReturnCard : IDashboardCard
{
    public string CardId => "idle-returns";

    public string Title => "空闲返回";

    public DashboardView View => DashboardView.Fleet;

    public string SourcePath => DashboardPaths.QueryPrefix + "idle-returns";

    public string RenderFact(JsonElement fact)
    {
        StringBuilder html = new();
        html.Append("<table><tr><th>agvId</th><th>步骤</th><th>等待点</th><th>原因码</th><th>REQ-0296 分支</th>")
            .Append("<th>评估结论</th><th>上一趟收尾</th></tr>");
        if (fact.TryGetProperty("vehicles", out JsonElement vehicles) && vehicles.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement vehicle in vehicles.EnumerateArray())
            {
                html.Append("<tr>")
                    .Append(DashboardPageRenderer.Cell(DashboardPageRenderer.Text(vehicle, "agvId")))
                    .Append(DashboardPageRenderer.Cell(WithDescription(vehicle, "step", "stepDescription")))
                    .Append(DashboardPageRenderer.Cell(Station(vehicle)))
                    .Append(DashboardPageRenderer.Cell(Reason(vehicle, "reasonCode", "reasonDescription", "reasonSince")))
                    .Append(DashboardPageRenderer.Cell(StationHoldingRendering.Str(vehicle, "failureBranchDescription") ?? ""))
                    .Append(DashboardPageRenderer.Cell(Verdict(vehicle)))
                    .Append(DashboardPageRenderer.Cell(LastEnded(vehicle)))
                    .Append("</tr>");
            }
        }
        html.Append("</table>");
        html.Append(StationHoldingRendering.UnavailableVehicles(fact, "空闲返回状态"));
        return html.ToString();
    }

    private static string WithDescription(JsonElement element, string code, string description)
    {
        string value = StationHoldingRendering.Str(element, code) ?? "（服务端未提供该字段）";
        return StationHoldingRendering.Str(element, description) is { } text ? $"{value}（{text}）" : value;
    }

    /// <summary>原因码照写英文，后面跟中文说明；服务端没给说明时明说没有，不留空让人以为没事。</summary>
    private static string Reason(JsonElement element, string code, string description, string? since)
    {
        if (StationHoldingRendering.Str(element, code) is not { } value)
        {
            return "";
        }
        string text = StationHoldingRendering.Str(element, description) ?? "这个码没有中文说明，请报开发";
        string from = since is not null && StationHoldingRendering.Str(element, since) is { } at ? $"，自 {at}" : "";
        return $"{value}：{text}{from}";
    }

    private static string Station(JsonElement vehicle)
    {
        List<string> parts = [];
        if (StationHoldingRendering.Str(vehicle, "stationId") is { } stationId)
        {
            string name = StationHoldingRendering.Str(vehicle, "stationName") is { } stationName ? $" {stationName}" : "";
            parts.Add($"目标 {stationId}{name}");
        }
        if (vehicle.TryGetProperty("waitingPoint", out JsonElement point) && point.ValueKind == JsonValueKind.Object &&
            point.TryGetProperty("holding", out JsonElement holding))
        {
            parts.Add($"持有等待点 {StationHoldingRendering.Str(point, "stationId")}："
                      + $"{StationHoldingRendering.Str(holding, "status")}，自 {StationHoldingRendering.Str(holding, "stateSince")}");
        }
        return string.Join("；", parts);
    }

    /// <summary>
    /// 评估器最近一轮已完成的评估对这辆车的结论（冷却、停止、电量、外来订单……）。这一轮没评估到它时照服务端的话说没评估，不写更早的码。
    /// </summary>
    private static string Verdict(JsonElement vehicle)
    {
        if (!vehicle.TryGetProperty("verdict", out JsonElement verdict) || verdict.ValueKind != JsonValueKind.Object)
        {
            return "（服务端未提供该字段）";
        }
        if (StationHoldingRendering.Str(verdict, "note") is { } note)
        {
            return note;
        }
        string reason = Reason(verdict, "reasonCode", "reasonDescription", since: null);
        string detail = StationHoldingRendering.Str(verdict, "detail") is { } text ? $"。细节：{text}" : "";
        string at = StationHoldingRendering.Str(verdict, "passStartedAt") is { } passAt ? $"（评估于 {passAt}）" : "";
        return $"{reason}{detail}{at}";
    }

    private static string LastEnded(JsonElement vehicle)
    {
        if (!vehicle.TryGetProperty("lastEnded", out JsonElement ended) || ended.ValueKind != JsonValueKind.Object)
        {
            return "";
        }
        string branch = StationHoldingRendering.Str(ended, "failureBranchDescription") is { } text ? $"。{text}" : "";
        string at = StationHoldingRendering.Str(ended, "endedAt") is { } endedAt ? $"（收尾于 {endedAt}）" : "";
        return $"{Reason(ended, "reasonCode", "reasonDescription", since: null)}{at}{branch}";
    }
}
