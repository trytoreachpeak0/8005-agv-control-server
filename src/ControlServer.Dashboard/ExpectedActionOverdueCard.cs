using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace ControlServer.Dashboard;

/// <summary>
/// 车队视图的期待动作超时卡片：哪个仓等操作员等了太久、该做什么、车读到的锁与光幕是什么（REQ-0358，control-server#142）。
/// </summary>
/// <remarks>
/// <para>
/// 读数是给管理员判断用的：例如「门早就关了，锁却一直读未锁」就很像锁传感器坏了（CP-0005 新增项一说明 5）。所以读数的观测时刻照写，
/// 读数之后车报告这个仓又变了的也照写，不把一份旧读数当成当下的（REQ-0269）。
/// </para>
/// <para>
/// 站点期限到了、门还没关（<c>STATION_TIMEOUT_DOOR_NOT_CLOSED</c>）与期待动作超时在装货站上会同时出现，这里合在同一行，不另起一行。
/// </para>
/// <para>
/// 只读，也只让人看到：没有表单、没有按钮，不改谁先到场。判定表单属于 <c>protocol-v3.0.0</c> 的票（program#115）。
/// </para>
/// </remarks>
public sealed class ExpectedActionOverdueCard : IDashboardCard
{
    public string CardId => "expected-action-overdue";

    public string Title => "期待动作超时";

    public DashboardView View => DashboardView.Fleet;

    public string SourcePath => DashboardPaths.QueryPrefix + "expected-action-overdue";

    public string RenderFact(JsonElement fact)
    {
        StringBuilder html = new();
        html.Append("<p class=\"expected-action-overdue-threshold\">")
            .Append(WebUtility.HtmlEncode(
                $"门槛 {Duration(fact, "thresholdSeconds")}：当前仓自第一次开锁起累计等待达到门槛仍未闭环时由车载端上报；只让人看到，班组长或管理员到现场查看。"))
            .Append("</p>");

        JsonElement[] slots = Array(fact, "slots");
        if (slots.Length == 0)
        {
            html.Append("<p>无期待动作超时的仓位</p>");
        }
        else
        {
            html.Append("<table><tr><th>车</th><th>站点</th><th>操作</th><th>仓位</th><th>期待的动作</th><th>已等待</th><th>读数</th><th>站点期限</th></tr>");
            foreach (JsonElement slot in slots)
            {
                html.Append("<tr>")
                    .Append(DashboardPageRenderer.Cell(DashboardPageRenderer.Text(slot, "agvId")))
                    .Append(DashboardPageRenderer.Cell(OrUnknown(slot, "stationId")))
                    .Append(DashboardPageRenderer.Cell(Operation(slot)))
                    .Append(DashboardPageRenderer.Cell(DashboardPageRenderer.Text(slot, "slotNo")))
                    .Append(DashboardPageRenderer.Cell(DashboardPageRenderer.Text(slot, "expectedAction")))
                    .Append(DashboardPageRenderer.Cell(Waited(slot)))
                    .Append(DashboardPageRenderer.Cell(Readings(slot)))
                    .Append(DashboardPageRenderer.Cell(StationDeadline(slot)))
                    .Append("</tr>");
            }
            html.Append("</table>");
        }

        foreach (JsonElement vehicle in Array(fact, "unavailableVehicles"))
        {
            html.Append("<p class=\"expected-action-overdue-unavailable\">")
                .Append(WebUtility.HtmlEncode(
                    $"{DashboardPageRenderer.Text(vehicle, "agvId")}：{DashboardPageRenderer.Text(vehicle, "reason")}，期待动作超时状态不明"))
                .Append("</p>");
        }
        return html.ToString();
    }

    private static JsonElement[] Array(JsonElement fact, string propertyName) =>
        fact.TryGetProperty(propertyName, out JsonElement rows) && rows.ValueKind == JsonValueKind.Array
            ? [.. rows.EnumerateArray()]
            : [];

    private static string OrUnknown(JsonElement slot, string propertyName) =>
        slot.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : "不明";

    private static string Operation(JsonElement slot) =>
        slot.TryGetProperty("operationType", out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() switch
            {
                "LOAD" => "装货",
                "UNLOAD" => "卸货",
                string other => other
            }
            : "不明";

    private static string Waited(JsonElement slot) =>
        slot.TryGetProperty("waitedSeconds", out JsonElement value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt64(out long seconds)
            ? Format(TimeSpan.FromSeconds(Math.Max(0, seconds)))
            : "不明";

    private static string Readings(JsonElement slot)
    {
        if (!slot.TryGetProperty("readings", out JsonElement readings) || readings.ValueKind != JsonValueKind.Object)
        {
            return "尚无读数";
        }
        string observed = readings.TryGetProperty("observedAt", out JsonElement at)
                          && at.ValueKind == JsonValueKind.String
                          && at.TryGetDateTimeOffset(out DateTimeOffset observedAt)
            ? observedAt.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture)
            : "时刻不明";
        string text =
            $"锁 {DashboardPageRenderer.Text(readings, "lockState")}；光幕 {DashboardPageRenderer.Text(readings, "physicalState")}；"
            + $"开锁输出 {DashboardPageRenderer.Text(readings, "unlockOutputState")}（{observed} 读数）";
        return readings.TryGetProperty("changedSinceObserved", out JsonElement changed) && changed.ValueKind == JsonValueKind.True
            ? text + "；读数之后车报告该仓有变化，新读数未到"
            : text;
    }

    private static string StationDeadline(JsonElement slot) =>
        slot.TryGetProperty("stationTimeoutDoorNotClosed", out JsonElement value) && value.ValueKind == JsonValueKind.True
            ? "站点期限已过，门未关（STATION_TIMEOUT_DOOR_NOT_CLOSED）"
            : string.Empty;

    private static string Duration(JsonElement fact, string propertyName) =>
        fact.TryGetProperty(propertyName, out JsonElement value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt64(out long seconds)
            ? Format(TimeSpan.FromSeconds(seconds))
            : DashboardPageRenderer.Text(fact, propertyName);

    private static string Format(TimeSpan span) =>
        span.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalHours} 小时 {span.Minutes} 分")
            : span.Seconds == 0
                ? string.Create(CultureInfo.InvariantCulture, $"{span.Minutes} 分钟")
                : string.Create(CultureInfo.InvariantCulture, $"{span.Minutes} 分 {span.Seconds} 秒");
}
