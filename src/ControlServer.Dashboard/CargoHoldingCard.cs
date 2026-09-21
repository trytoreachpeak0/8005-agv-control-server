using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ControlServer.Dashboard;

/// <summary>
/// 车队视图的持货等单卡片（批次7-12，control-server#217；REQ-0354、REQ-0355）：每趟处于装货阶段的旅程一行。
/// </summary>
/// <remarks>
/// <para>
/// <b>不是告警。</b>持货等单是正常作业（规格第 8.8 节第 4 条：不是相对 MVP 的倒退），所以这张卡片没有告警样式，文案也不写故障。
/// </para>
/// <para>
/// 期限、剩余时间、两侧满没满、适用不适用都由服务端算好随行下发，这里只把它们写成字：看板另算一套，迟早与引擎对不上。
/// 剩余时间是服务端这一轮取数时算的，看板 2 秒一刷，所以它就是当下的值；期限已过而一批装货还在执行时写「已到期，等待本次装货闭环」，
/// 从不写负数。结束原因以服务端给的结束原因为准，让站那一句只在结束原因是让站时出现，触发的车写在括号里。
/// </para>
/// </remarks>
public sealed class CargoHoldingCard : IDashboardCard
{
    public string CardId => "cargo-holding";

    public string Title => "持货等单";

    public DashboardView View => DashboardView.Fleet;

    public string SourcePath => DashboardPaths.QueryPrefix + "cargo-holding";

    public string RenderFact(JsonElement fact)
    {
        JsonElement[] journeys =
            fact.TryGetProperty("journeys", out JsonElement rows) && rows.ValueKind == JsonValueKind.Array
                ? [.. rows.EnumerateArray()]
                : [];
        if (journeys.Length == 0)
        {
            return "<p>没有处于装货阶段的车</p>";
        }

        StringBuilder html = new();
        html.Append("<table><tr><th>车</th><th>站</th><th>装货阶段</th><th>持货期限</th><th>剩余</th><th>两侧</th><th>结束原因</th><th>需求</th></tr>");
        foreach (JsonElement journey in journeys)
        {
            html.Append("<tr>")
                .Append(DashboardPageRenderer.Cell(DashboardPageRenderer.Text(journey, "agvId")))
                .Append(DashboardPageRenderer.Cell(DashboardPageRenderer.Text(journey, "stationId")))
                .Append(DashboardPageRenderer.Cell(Phase(DashboardPageRenderer.Text(journey, "loadingPhaseState"))))
                .Append(DashboardPageRenderer.Cell(Time(journey, "cargoHoldingDeadlineAt")))
                .Append(DashboardPageRenderer.Cell(Remaining(journey)))
                .Append(DashboardPageRenderer.Cell($"{Side(journey, "frontFull", "前侧")}；{Side(journey, "rearFull", "后侧")}"))
                .Append(DashboardPageRenderer.Cell(ClosedReason(journey)))
                .Append(DashboardPageRenderer.Cell(JourneyDemandListRendering.Text(journey)))
                .Append("</tr>");
        }
        html.Append("</table>");
        return html.ToString();
    }

    private static string Remaining(JsonElement journey)
    {
        if (Flag(journey, "awaitingLoadBatchClosure"))
        {
            return "已到期，等待本次装货闭环";
        }
        return journey.TryGetProperty("remainingSeconds", out JsonElement value)
               && value.ValueKind == JsonValueKind.Number
               && value.TryGetInt64(out long seconds)
            ? Format(TimeSpan.FromSeconds(Math.Max(0, seconds)))
            : string.Empty;
    }

    /// <summary>装货阶段的四个状态（program#94）。服务端给了一个这里不认识的值时原样写出，不猜。</summary>
    private static string Phase(string state) => state switch
    {
        "LOADING" => "装货中",
        "CARGO_HOLDING_WAIT" => "持货等单",
        "VEHICLE_FULL" => "已装满",
        "CLOSED" => "已结束",
        _ => state
    };

    /// <summary>
    /// 结束原因以库里的结束原因为准（票面评论，cs#213 审查）：让站那一句只在结束原因是让站时写，
    /// 触发让站的车只作补充；持货期限先到、让站晚到的，结束原因是持货超时，这里就只写持货超时。
    /// </summary>
    private static string ClosedReason(JsonElement journey)
    {
        string reason = DashboardPageRenderer.Text(journey, "closedReason");
        return reason switch
        {
            "" => string.Empty,
            "VEHICLE_FULL" => "两侧装满",
            "CARGO_HOLDING_TIMEOUT" => "持货超时",
            "WAITING_STATION_YIELD" => YieldedTo(journey) is { } vehicle
                ? $"另一辆车以本站为下一停靠，本车结束等单（{vehicle}）"
                : "另一辆车以本站为下一停靠，本车结束等单",
            "PLANNED_LOADING_COMPLETE" => "计划装货完成",
            _ => reason
        };
    }

    private static string? YieldedTo(JsonElement journey) =>
        journey.TryGetProperty("yieldedToVehicleKey", out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string Time(JsonElement row, string propertyName) =>
        row.TryGetProperty(propertyName, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
        && value.TryGetDateTimeOffset(out DateTimeOffset time)
            ? time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
            : string.Empty;

    private static string Side(JsonElement journey, string propertyName, string side) =>
        journey.TryGetProperty(propertyName, out JsonElement value)
            ? value.ValueKind switch
            {
                JsonValueKind.True => $"{side}：已满",
                JsonValueKind.False => $"{side}：未满",
                _ => $"{side}：未判定"
            }
            : $"{side}：未判定";

    private static bool Flag(JsonElement row, string propertyName) =>
        row.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind == JsonValueKind.True;

    private static string Format(TimeSpan span) =>
        span.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalHours} 小时 {span.Minutes} 分")
            : string.Create(CultureInfo.InvariantCulture, $"{span.Minutes} 分 {span.Seconds} 秒");
}
