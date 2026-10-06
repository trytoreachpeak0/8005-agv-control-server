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
/// 每行最后一格是人工判故障（REQ-0359，control-server#384）：还没判过的仓给一个链接，指到「人工判故障」动作的确认页，带上车与
/// 仓位；判过的标出状态——已判定、等车载端结果，已生效，或车载端拒绝了判定及它给的原因（拒绝之后可以再判）。
/// </para>
/// <para>
/// 卡片本身仍然没有表单、没有按钮：主页每 2 秒刷新，表单放在这里会把正在填的说明刷掉，所以表单只在确认页上
/// （<see cref="DashboardActionRoutes"/>）。卡片不改谁先到场。
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
            html.Append("<table><tr><th>车</th><th>站点</th><th>操作</th><th>仓位</th><th>期待的动作</th><th>已等待</th><th>读数</th><th>站点期限</th><th>人工判故障</th></tr>");
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
                    .Append(Declaration(slot))
                    .Append("</tr>");
            }
            html.Append("</table>");
        }

        foreach (JsonElement vehicle in Array(fact, "unavailableVehicles"))
        {
            html.Append("<p class=\"expected-action-overdue-vehicle-unknown\">")
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
                var other => other ?? "不明"
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

    /// <summary>The last cell: the way to declare, or what became of the declaration.</summary>
    private static string Declaration(JsonElement slot)
    {
        string link = WebUtility.HtmlEncode(SlotFaultDeclarationAction.Link(
            DashboardPageRenderer.Text(slot, "agvId"), DashboardPageRenderer.Text(slot, "slotNo")));
        if (!slot.TryGetProperty("declaration", out JsonElement declaration) || declaration.ValueKind != JsonValueKind.Object)
        {
            return $"<td><a href=\"{link}\">判故障</a></td>";
        }
        string declaredAt = Clock(declaration, "declaredAt");
        string answeredAt = Clock(declaration, "resultReceivedAt");
        string by = DashboardPageRenderer.Text(declaration, "administratorId");
        return DashboardPageRenderer.Text(declaration, "state") switch
        {
            "PENDING" => DashboardPageRenderer.Cell($"已判定、等车载端结果（{declaredAt} 由 {by} 判定）"),
            "APPLIED" => DashboardPageRenderer.Cell($"判定已生效（{declaredAt} 由 {by} 判定，{answeredAt} 车载端应用），这次装卸转人工异常处理"),
            "NOT_APPLICABLE" => $"<td>{WebUtility.HtmlEncode(
                    $"车载端拒绝了判定（{declaredAt} 由 {by} 判定，{answeredAt} 拒绝）：{Refusal(declaration)}"
                    + (UnreconciledOnOperation(declaration)
                        ? "；这次装卸之前有一项判定车载端已放弃应答，车上是否已生效未知，这次装卸不能再取消"
                        : string.Empty))}"
                + $" <a href=\"{link}\">再判</a></td>",
            "UNRECONCILED" => $"<td>{WebUtility.HtmlEncode(
                    $"车载端已放弃对判定的应答（{declaredAt} 由 {by} 判定，{answeredAt} 放弃），两端结论不一致，车上是否已生效未知，请人工核对；这次装卸不能再取消")}"
                + $" <a href=\"{link}\">再判</a></td>",
            var other => DashboardPageRenderer.Cell($"判定状态 {other}（{declaredAt}）")
        };
    }

    /// <summary>
    /// 这次装卸上是否有车载端放弃了应答的判定（control-server#481）。行上只显示最近一次判定，之后再判被拒时，
    /// 取消仍被那一项挡着，要在拒绝那一格里说出来。
    /// </summary>
    private static bool UnreconciledOnOperation(JsonElement declaration) =>
        declaration.TryGetProperty("unreconciledOnOperation", out JsonElement value) && value.ValueKind == JsonValueKind.True;

    private static string Refusal(JsonElement declaration)
    {
        string? message = declaration.TryGetProperty("displayMessage", out JsonElement text) && text.ValueKind == JsonValueKind.String
            ? text.GetString()
            : null;
        string? code = declaration.TryGetProperty("reasonCode", out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
        return (message, code) switch
        {
            (null, null) => "车载端没有给原因",
            (null, _) => code!,
            (_, null) => message,
            _ => $"{message}（{code}）"
        };
    }

    private static string Clock(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
        && value.TryGetDateTimeOffset(out DateTimeOffset at)
            ? at.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture)
            : "时刻不明";

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
