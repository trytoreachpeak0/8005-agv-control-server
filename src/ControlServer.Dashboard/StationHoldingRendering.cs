using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace ControlServer.Dashboard;

/// <summary>
/// 站点独占那几格的写法（批次8-21，control-server#392）：等待点、公共站点两张卡片共用，批次9 的充电桩卡片（control-server#408）照样用。
/// </summary>
/// <remarks>
/// 读的是服务端统一投影的 <c>holding</c> 对象（<c>StationHoldings.Project</c>）：状态与中文说明、持有车辆、持有者、进入该状态的时刻、
/// 所依据的配置版本。持有车辆失联时只写「车辆失联」，不替它说车此刻在不在点上。
/// </remarks>
public static class StationHoldingRendering
{
    /// <summary>表头里与 <see cref="Cells"/> 对应的那几列。</summary>
    public const string HeaderCells = "<th>状态</th><th>持有车辆</th><th>持有者</th><th>进入该状态</th><th>依据版本</th>";

    /// <summary>服务端没给 <c>holding</c> 时补齐与 <see cref="HeaderCells"/> 对应的五格，第一格说明缺了什么，表格不错列。</summary>
    public static string MissingCells() =>
        DashboardPageRenderer.Cell("（服务端未提供该字段）") + string.Concat(Enumerable.Repeat(DashboardPageRenderer.Cell(""), 4));

    /// <summary>一个站点的独占写成五格。</summary>
    public static string Cells(JsonElement holding)
    {
        StringBuilder html = new();
        string status = Str(holding, "status") ?? "（服务端未提供该字段）";
        string description = Str(holding, "statusDescription") ?? "";
        html.Append(DashboardPageRenderer.Cell(description.Length == 0 ? status : $"{status}（{description}）"));

        string? agvId = Str(holding, "holderAgvId") ?? Str(holding, "holderVehicleKey");
        string holder = agvId ?? "";
        if (agvId is not null && holding.TryGetProperty("holderInContact", out JsonElement inContact) &&
            inContact.ValueKind == JsonValueKind.False)
        {
            holder += "（车辆失联：服务端仍为它保留这个点，车此刻在不在点上拿不到）";
        }
        html.Append(DashboardPageRenderer.Cell(holder));

        string? journeyId = Str(holding, "holderJourneyId");
        html.Append(DashboardPageRenderer.Cell(
            journeyId is null ? "" : $"{Str(holding, "holderKind") ?? "旅程"} {journeyId}"));
        html.Append(DashboardPageRenderer.Cell(Str(holding, "stateSince") ?? ""));
        html.Append(DashboardPageRenderer.Cell(Versions(holding)));
        return html.ToString();
    }

    /// <summary>失联车的那几行：只有车号与原因。</summary>
    public static string UnavailableVehicles(JsonElement fact, string what)
    {
        StringBuilder html = new();
        if (!fact.TryGetProperty("unavailableVehicles", out JsonElement vehicles) || vehicles.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }
        foreach (JsonElement vehicle in vehicles.EnumerateArray())
        {
            html.Append("<p class=\"vehicle-out-of-contact\">")
                .Append(WebUtility.HtmlEncode(
                    $"{DashboardPageRenderer.Text(vehicle, "agvId")}：{DashboardPageRenderer.Text(vehicle, "reason")}，{what}拿不到"))
                .Append("</p>");
        }
        return html.ToString();
    }

    /// <summary>读一个字符串或数字字段；缺失或为 null 时为空。</summary>
    public static string? Str(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out JsonElement value) &&
        value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            ? value.ToString()
            : null;

    private static string Versions(JsonElement holding)
    {
        List<string> versions = [];
        if (Str(holding, "waitingPointVersion") is { } waitingPoint)
        {
            versions.Add(string.Create(CultureInfo.InvariantCulture, $"等待点登记 v{waitingPoint}"));
        }
        if (Str(holding, "chargerRosterVersion") is { } roster)
        {
            versions.Add(string.Create(CultureInfo.InvariantCulture, $"充电桩名册 v{roster}"));
        }
        return string.Join("；", versions);
    }
}
