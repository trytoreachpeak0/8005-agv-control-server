using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ControlServer.Dashboard;

/// <summary>
/// 车队视图的派车积压卡片：未清除的结构性派车阻断，与受理前正常积压的等待状态（REQ-0210），分两块显示。
/// </summary>
/// <remarks>
/// <para>
/// 两块不合成一张表：结构性阻断永远没有车能接，要人去处理，所以是告警样式；正常积压等着就行，不是告警。
/// </para>
/// <para>
/// 原因码的中文说明由服务端按它的码表随行下发，这里不另抄一份——抄一份就会与服务端的码悄悄对不上。
/// 服务端没给说明的码原样显示码值并标「未登记说明」，不猜它是什么意思。
/// </para>
/// <para>
/// 未映射 AREA 的需求按 REQ-0191 静默跳过：服务端不把它们放进积压列表，这里只在折叠处给一个计数，不用告警样式。
/// </para>
/// </remarks>
public sealed class DispatchBacklogCard : IDashboardCard
{
    /// <summary>服务端没有为某个原因码登记中文说明时显示的字样。</summary>
    public const string UnregisteredDescription = "未登记说明";

    public string CardId => "dispatch-backlog";

    public string Title => "派车积压与结构性派车阻断";

    public DashboardView View => DashboardView.Fleet;

    public string SourcePath => DashboardPaths.QueryPrefix + "dispatch-backlog";

    public string RenderFact(JsonElement fact)
    {
        StringBuilder html = new();
        RenderStructuralBlocks(html, Rows(fact, "structuralBlocks"));
        RenderThresholds(html, fact);
        RenderBacklog(html, Rows(fact, "backlog"));
        RenderSilentCount(html, fact);
        return html.ToString();
    }

    private static void RenderStructuralBlocks(StringBuilder html, JsonElement[] blocks)
    {
        if (blocks.Length == 0)
        {
            html.Append("<section class=\"structural-dispatch-blocks\"><h3>结构性派车阻断</h3><p>无</p></section>");
            return;
        }
        html.Append("<section class=\"structural-dispatch-blocks alarm\" style=\"border:2px solid #c00;color:#900\">");
        html.Append("<h3>结构性派车阻断（没有车能接，需人工处置）</h3>");
        html.Append("<table><tr><th>需求</th><th>原因码</th><th>说明</th><th>首次形成</th><th>最近一次仍成立</th></tr>");
        foreach (JsonElement block in blocks)
        {
            html.Append("<tr>")
                .Append(DashboardPageRenderer.Cell(Demand(block)))
                .Append(DashboardPageRenderer.Cell(DashboardPageRenderer.Text(block, "reasonCode")))
                .Append(DashboardPageRenderer.Cell(Description(block)))
                .Append(DashboardPageRenderer.Cell(Time(block, "firstRaisedAt")))
                .Append(DashboardPageRenderer.Cell(Time(block, "lastSeenAt")))
                .Append("</tr>");
        }
        html.Append("</table></section>");
    }

    private static void RenderBacklog(StringBuilder html, JsonElement[] backlog)
    {
        html.Append("<section class=\"dispatch-backlog\"><h3>派车积压（等待派车）</h3>");
        if (backlog.Length == 0)
        {
            html.Append("<p>无</p></section>");
            return;
        }
        html.Append(
            "<table><tr><th>需求</th><th>所在层</th><th>原因码</th><th>说明</th><th>首次出现</th><th>已等待</th><th>最近评估</th><th>防饥饿升级</th></tr>");
        foreach (JsonElement row in backlog)
        {
            html.Append("<tr>")
                .Append(DashboardPageRenderer.Cell(Demand(row)))
                .Append(DashboardPageRenderer.Cell(Tier(row)))
                .Append(DashboardPageRenderer.Cell(DashboardPageRenderer.Text(row, "reasonCode")))
                .Append(DashboardPageRenderer.Cell(Description(row)))
                .Append(DashboardPageRenderer.Cell(Time(row, "firstSeenAt")))
                .Append(DashboardPageRenderer.Cell(Waiting(row)))
                .Append(DashboardPageRenderer.Cell(Time(row, "lastEvaluatedAt")))
                .Append(DashboardPageRenderer.Cell(Escalation(row)))
                .Append("</tr>");
        }
        html.Append("</table></section>");
    }

    /// <summary>所在层（REQ-0202）。服务端给了一个这里不认识的层名时原样写出；没有这个字段（旧服务端）时留空。</summary>
    private static string Tier(JsonElement row) =>
        row.TryGetProperty("tier", out JsonElement tier) && tier.ValueKind == JsonValueKind.String
            ? tier.GetString() switch
            {
                "STARVATION_TIMEOUT" => "超时层",
                "TOP_BAND" => "最高带（STAGING_TO_WIRE）",
                "NORMAL_BAND" => "普通带",
                string other => other,
                null => string.Empty
            }
            : string.Empty;

    /// <summary>
    /// 防饥饿升级告警的标记（REQ-0210 后半）：批次7-09 进入超时层时写下的时刻与所用参数版本。告警在服务端日志里，这里只是标记，不是告警样式。
    /// </summary>
    private static string Escalation(JsonElement row) =>
        row.TryGetProperty("starvationEscalatedAt", out JsonElement at)
        && at.ValueKind == JsonValueKind.String
        && at.TryGetDateTimeOffset(out DateTimeOffset escalatedAt)
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"已升级告警 {escalatedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}（参数版本 {DashboardPageRenderer.Text(row, "starvationEscalationParameterVersion")}）")
            : string.Empty;

    /// <summary>
    /// 本区阈值未批准（REQ-0203）：一个分区都没有配防饥饿阈值时，整张卡片写明只计龄。积压行不带分区，所以说不到单条需求上。
    /// 部分分区已批准时列出每个分区的阈值。服务端没给这组字段（旧服务端）时什么都不写。
    /// </summary>
    private static void RenderThresholds(StringBuilder html, JsonElement fact)
    {
        if (!fact.TryGetProperty("starvationThresholdsApproved", out JsonElement approved)
            || approved.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return;
        }
        if (approved.ValueKind == JsonValueKind.False)
        {
            html.Append("<p class=\"starvation-thresholds-unapproved\">本区阈值未批准：只累计等待年龄，不跨带升级</p>");
            return;
        }
        string zones = string.Join("；", Rows(fact, "starvationThresholds").Select(zone =>
            zone.TryGetProperty("thresholdSeconds", out JsonElement seconds) && seconds.ValueKind == JsonValueKind.Number
                ? $"{DashboardPageRenderer.Text(zone, "dispatchZone")} {seconds.GetInt64()} 秒"
                : $"{DashboardPageRenderer.Text(zone, "dispatchZone")} 未批准（只计龄）"));
        html.Append("<p class=\"starvation-thresholds\">")
            .Append(System.Net.WebUtility.HtmlEncode("防饥饿阈值：" + zones))
            .Append("</p>");
    }

    private static void RenderSilentCount(StringBuilder html, JsonElement fact)
    {
        if (!fact.TryGetProperty("silentBacklogCount", out JsonElement count)
            || count.ValueKind != JsonValueKind.Number
            || count.GetInt32() == 0)
        {
            return;
        }
        html.Append(
                CultureInfo.InvariantCulture,
                $"<details class=\"dispatch-backlog-silent\"><summary>未映射 AREA 的需求：{count.GetInt32()} 条</summary>")
            .Append("<p>这些 AREA 不在分区归属表里，本服务不执行它们（REQ-0191），不派车，也不告警。</p></details>");
    }

    private static JsonElement[] Rows(JsonElement fact, string propertyName) =>
        fact.TryGetProperty(propertyName, out JsonElement rows) && rows.ValueKind == JsonValueKind.Array
            ? [.. rows.EnumerateArray()]
            : [];

    private static string Demand(JsonElement row) =>
        $"{DashboardPageRenderer.Text(row, "transportDemandKey")}（{DashboardPageRenderer.Text(row, "demandId")}）";

    private static string Description(JsonElement row) =>
        row.TryGetProperty("reasonDescription", out JsonElement description)
        && description.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(description.GetString())
            ? description.GetString()!
            : UnregisteredDescription;

    private static string Time(JsonElement row, string propertyName) =>
        row.TryGetProperty(propertyName, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
        && value.TryGetDateTimeOffset(out DateTimeOffset time)
            ? time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
            : DashboardPageRenderer.Text(row, propertyName);

    private static string Waiting(JsonElement row)
    {
        // MesIngest 没给建单时刻（批次7-09 审查低 3）：年龄按 0 算，不拿 0001-01-01 当起点，这里如实说不知道。
        if (row.TryGetProperty("waitingAgeKnown", out JsonElement known) && known.ValueKind == JsonValueKind.False)
        {
            return "建单时刻不明";
        }
        if (!row.TryGetProperty("waitingSeconds", out JsonElement value)
            || !value.TryGetInt64(out long seconds))
        {
            return DashboardPageRenderer.Text(row, "waitingSeconds");
        }
        TimeSpan waited = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return waited.TotalDays >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)waited.TotalDays} 天 {waited.Hours} 小时")
            : waited.TotalHours >= 1
                ? string.Create(CultureInfo.InvariantCulture, $"{(int)waited.TotalHours} 小时 {waited.Minutes} 分")
                : string.Create(CultureInfo.InvariantCulture, $"{waited.Minutes} 分 {waited.Seconds} 秒");
    }
}
