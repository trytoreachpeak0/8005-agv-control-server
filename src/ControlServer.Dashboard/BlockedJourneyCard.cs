using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace ControlServer.Dashboard;

/// <summary>
/// 车队视图的旅程阻断卡片：每条被阻断的旅程挂了多久、现在该谁去处理（control-server#80，program#55）。
/// </summary>
/// <remarks>
/// <para>
/// 分档不在这里算。两道线是服务端配置，档位由服务端按它判好随行下发；这里只按档位上色，免得看板另抄一份时长、与服务端悄悄对不上。
/// 三档对应规程里的三个岗位：操作员、满 10 分钟班组长、满 30 分钟维护管理员（线的实际值同样随行下发，显示在卡片上）。
/// </para>
/// <para>
/// 已挂多久是服务端这一轮取数时算的，看板 2 秒一刷，所以它就是当下的值。开始时间没有记录的阻断如实说没有记录，不拿别的时间顶替。
/// </para>
/// </remarks>
public sealed class BlockedJourneyCard : IDashboardCard
{
    public string CardId => "blocked-journeys";

    public string Title => "旅程阻断";

    public DashboardView View => DashboardView.Fleet;

    public string SourcePath => DashboardPaths.QueryPrefix + "blocked-journeys";

    public string RenderFact(JsonElement fact)
    {
        StringBuilder html = new();
        html.Append("<p class=\"blocked-journey-thresholds\">")
            .Append(WebUtility.HtmlEncode(
                $"满 {Duration(fact, "shiftLeaderAfterSeconds")} 转班组长，满 {Duration(fact, "maintenanceAdministratorAfterSeconds")} 转维护管理员；安全证据不全直接由维护管理员处理，只有车带着服务端自己的在途运单时照时长走。"))
            .Append("</p>");

        JsonElement[] journeys =
            fact.TryGetProperty("journeys", out JsonElement rows) && rows.ValueKind == JsonValueKind.Array
                ? [.. rows.EnumerateArray()]
                : [];
        if (journeys.Length == 0)
        {
            html.Append("<p>无被阻断的旅程</p>");
            return html.ToString();
        }

        html.Append("<table><tr><th>车</th><th>站</th><th>阻断码</th><th>从何时起</th><th>已挂</th><th>处理人</th><th>会话</th></tr>");
        foreach (JsonElement journey in journeys)
        {
            string level = DashboardPageRenderer.Text(journey, "escalationLevel");
            (string cssClass, string style, string role) = Presentation(level);
            html.Append(CultureInfo.InvariantCulture, $"<tr class=\"{cssClass}\" style=\"{style}\">")
                .Append(DashboardPageRenderer.Cell(DashboardPageRenderer.Text(journey, "agvId")))
                .Append(DashboardPageRenderer.Cell(DashboardPageRenderer.Text(journey, "stationId")))
                .Append(DashboardPageRenderer.Cell(DashboardPageRenderer.Text(journey, "blockReasonCode")))
                .Append(DashboardPageRenderer.Cell(Since(journey)))
                .Append(DashboardPageRenderer.Cell(Elapsed(journey)))
                .Append(DashboardPageRenderer.Cell(role + Attribution(journey)))
                .Append(DashboardPageRenderer.Cell(Session(journey)))
                .Append("</tr>");
        }
        html.Append("</table>");
        return html.ToString();
    }

    /// <summary>档位到样式与岗位名。服务端给了一个这里不认识的档位时，照最高档显示并原样写出档位名，不猜它更轻。</summary>
    private static (string CssClass, string Style, string Role) Presentation(string level) => level switch
    {
        "Operator" => ("escalation-operator", "background:#e8f5e9", "操作员"),
        "ShiftLeader" => ("escalation-shift-leader", "background:#fff3cd", "班组长"),
        "MaintenanceAdministrator" => ("escalation-maintenance-administrator", "background:#f8d7da;color:#900", "维护管理员"),
        _ => ("escalation-maintenance-administrator", "background:#f8d7da;color:#900", $"维护管理员（未登记的档位 {level}）")
    };

    /// <summary>
    /// 服务端判定「安全证据有未知项」完全由自己的在途移动单解释时（control-server#139），说明这一行为什么没有直接交给维护管理员：
    /// 车带着服务端自己下的单在排队、避让或行驶，RIoT 读数必然是「运动状态未知」。不认识的归因原样写出，不替它解释。
    /// </summary>
    private static string Attribution(JsonElement journey) =>
        journey.TryGetProperty("unknownExplainedBy", out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() switch
            {
                "OWN_MOVEMENT_ORDER_IN_FLIGHT" => "；行驶中（由在途运单解释）",
                string other => $"；{other}",
                null => string.Empty
            }
            : string.Empty;

    private static string Since(JsonElement journey) =>
        journey.TryGetProperty("blockReasonSince", out JsonElement value)
        && value.ValueKind == JsonValueKind.String
        && value.TryGetDateTimeOffset(out DateTimeOffset since)
            ? since.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
            : "开始时间没有记录";

    private static string Elapsed(JsonElement journey) =>
        journey.TryGetProperty("blockedSeconds", out JsonElement value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt64(out long seconds)
            ? Format(TimeSpan.FromSeconds(Math.Max(0, seconds)))
            : "不明";

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

    /// <summary>只有 <c>ONBOARD_SESSION_NOT_READY</c> 带会话字段；其余的码这一格留空。</summary>
    private static string Session(JsonElement journey)
    {
        if (!journey.TryGetProperty("session", out JsonElement session) || session.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }
        if (session.TryGetProperty("present", out JsonElement present) && present.ValueKind == JsonValueKind.False)
        {
            return "没有会话行";
        }
        string unknown = session.TryGetProperty("safetyUnknownPresent", out JsonElement flag)
            ? flag.ValueKind switch
            {
                JsonValueKind.False => "否",
                JsonValueKind.True => "是",
                _ => "未报告"
            }
            : "未报告";
        return $"原因码 {DashboardPageRenderer.Text(session, "reasonCode")}；安全原因码 {DashboardPageRenderer.Text(session, "safetyReasonCodesJson")}；安全证据有未知项：{unknown}";
    }
}
