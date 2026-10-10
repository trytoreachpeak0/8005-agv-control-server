using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ControlServer.Dashboard;

/// <summary>
/// 车队视图的「等人中的旅程」卡片（control-server#273）：车停着等人的每趟旅程一行——车、在等什么、原因码、已等多久、电量与读数时间、该怎么办。
/// </summary>
/// <remarks>
/// <para>
/// <b>只报不动。</b>v2 在批次 9 之前没有自动充电，REQ-0169 只许电量下降提高告警等级，所以这张卡片没有按钮：救命线下写「需要人工挪车充电」（control-server#330 起写明用车上单机方式，不在 RIoT 里下单），
/// 接单线下写「低于接单线」，决定挪不挪车的是人。
/// </para>
/// <para>
/// 已等多久、电量等级、过没过门槛都由服务端算好随行下发（从落库的等人起点与监看落库的读数算），这里只把它们写成字；读不到电量写「未知」，
/// 一行不省略。服务端给了一个这里不认识的阶段或等级时原样写出，不猜。
/// </para>
/// </remarks>
public sealed class WaitingJourneyCard : IDashboardCard
{
    public string CardId => "waiting-journeys";

    public string Title => "等人中的旅程";

    public DashboardView View => DashboardView.Fleet;

    public string SourcePath => DashboardPaths.QueryPrefix + "waiting-journeys";

    public string RenderFact(JsonElement fact)
    {
        JsonElement[] journeys =
            fact.TryGetProperty("journeys", out JsonElement rows) && rows.ValueKind == JsonValueKind.Array
                ? [.. rows.EnumerateArray()]
                : [];
        if (journeys.Length == 0)
        {
            return "<p>没有等人中的旅程</p>";
        }

        StringBuilder html = new();
        html.Append("<table><tr><th>车</th><th>在等什么</th><th>原因码</th><th>已等</th><th>电量</th><th>读数时间</th><th>处理</th></tr>");
        foreach (JsonElement journey in journeys)
        {
            string level = DashboardPageRenderer.Text(journey, "batteryLevel");
            html.Append("<tr>")
                .Append(DashboardPageRenderer.Cell(DashboardPageRenderer.Text(journey, "agvId")))
                .Append(DashboardPageRenderer.Cell(Stage(DashboardPageRenderer.Text(journey, "stage"))))
                .Append(DashboardPageRenderer.Cell(DashboardPageRenderer.Text(journey, "blockReasonCode")))
                .Append(DashboardPageRenderer.Cell(Waited(journey)))
                .Append(DashboardPageRenderer.Cell(Battery(journey)))
                .Append(DashboardPageRenderer.Cell(Time(journey, "batteryObservedAt")))
                .Append(DashboardPageRenderer.Cell(Advice(level, Flag(journey, "pastWarningThreshold"))))
                .Append("</tr>");
        }
        html.Append("</table>");
        return html.ToString();
    }

    /// <summary>每个阶段车在等什么（等人阶段的判法在服务端 <c>JourneyWaitClassification</c>，这里只管写成字）。</summary>
    private static string Stage(string stage) => stage switch
    {
        "AwaitingSublot" => "取货站等录入",
        "AwaitingLoadResult" => "取货站等装货闭环",
        "AwaitingStationDeparture" => "取货站等离站（持货等单或离站等待）",
        "AwaitingDepartureSafety" => "等离站安全核验",
        "AwaitingUnloadResult" => "闸口等卸货",
        "Blocked" => "阻断，等人处理",
        "AwaitingPickupArrival" => "去取货站途中停住",
        "AwaitingGateArrival" => "去卸货站途中停住",
        _ => stage
    };

    private static string Battery(JsonElement journey) =>
        journey.TryGetProperty("batteryPercent", out JsonElement value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out int percent)
            ? string.Create(CultureInfo.InvariantCulture, $"{percent}%")
            : "未知";

    private static string Advice(string level, bool pastThreshold) => level switch
    {
        // control-server#330: a vehicle-local (单机) move creates no RIoT order, while an order a person places in RIoT on a
        // vehicle of ours -- a move or a charge -- is a foreign order, which a deployment authorized to cancel cancels. The
        // user decided on 2026-09-23 that such orders are not exempted, so this wording is the lasting one.
        "BelowRescueLine" => "需要人工挪车充电：在车上用单机方式挪车、充电，不要在 RIoT 里给这辆车下单",
        "BelowDispatchMinimum" => "低于接单线，尽快处理",
        "Unknown" => "读不到电量，到现场查看",
        "Sufficient" => pastThreshold ? "等人已超过告警门槛" : string.Empty,
        _ => level
    };

    private static string Waited(JsonElement journey) =>
        journey.TryGetProperty("waitedSeconds", out JsonElement value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt64(out long seconds)
            ? Format(TimeSpan.FromSeconds(Math.Max(0, seconds)))
            : "未知";

    private static string Time(JsonElement row, string propertyName) =>
        row.TryGetProperty(propertyName, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
        && value.TryGetDateTimeOffset(out DateTimeOffset time)
            ? time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
            : "未读到";

    private static bool Flag(JsonElement row, string propertyName) =>
        row.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind == JsonValueKind.True;

    private static string Format(TimeSpan span) =>
        span.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalHours} 小时 {span.Minutes} 分")
            : string.Create(CultureInfo.InvariantCulture, $"{span.Minutes} 分 {span.Seconds} 秒");
}
