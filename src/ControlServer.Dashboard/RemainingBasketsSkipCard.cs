using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ControlServer.Dashboard;

/// <summary>
/// 车队视图「剩下的不装记录」（REQ-0367，CP-0021，control-server#830）：操作员在多篮装货里装进一部分、申请剩下的不装，服务端的每一次判定一行——
/// 批号、车、减篮 N→k、操作员工号、申请与判定时刻、同意还是拒绝（拒绝写原因）、是否已按 k 篮结算。
/// </summary>
/// <remarks>
/// 同意了的需求按 k 篮当作正常送达：剩下的篮子系统不再管、不发人工搬运告警，所以这里是现场与审计查「这批料为什么少送了几篮」的地方。
/// </remarks>
public sealed class RemainingBasketsSkipCard : IDashboardCard
{
    public string CardId => "remaining-baskets-skips";

    public string Title => "剩下的不装记录";

    public DashboardView View => DashboardView.Fleet;

    public string SourcePath => DashboardPaths.QueryPrefix + "remaining-baskets-skips";

    public string RenderFact(JsonElement fact)
    {
        StringBuilder html = new();
        JsonElement[] rows = fact.TryGetProperty("skips", out JsonElement skips) && skips.ValueKind == JsonValueKind.Array
            ? [.. skips.EnumerateArray()]
            : [];
        if (rows.Length == 0)
        {
            html.Append(ChargingCardRendering.Paragraph("remaining-baskets-skips-none", "最近没有「剩下的不装」申请"));
            return html.ToString();
        }
        html.Append("<section class=\"remaining-baskets-skips\">")
            .Append("<p>多篮装货里操作员申请「剩下的不装」：同意后这条需求按已装篮数送完、批号算送完，剩下的篮子系统不再管。</p>")
            .Append("<table><tr><th>批号</th><th>车</th><th>减篮</th><th>已装仓位</th><th>操作员</th><th>申请时刻</th><th>判定时刻</th>")
            .Append("<th>结论</th><th>已按实装结算</th></tr>");
        foreach (JsonElement row in rows)
        {
            string expected = row.TryGetProperty("expectedBasketCount", out JsonElement n) && n.ValueKind == JsonValueKind.Number
                ? n.GetInt32().ToString(CultureInfo.InvariantCulture)
                : "?";
            string loaded = row.TryGetProperty("loadedBasketCount", out JsonElement k) && k.ValueKind == JsonValueKind.Number
                ? k.GetInt32().ToString(CultureInfo.InvariantCulture)
                : "?";
            string slots = row.TryGetProperty("loadedSlots", out JsonElement list) && list.ValueKind == JsonValueKind.Array
                ? string.Join(",", list.EnumerateArray().Select(item => item.GetInt32().ToString(CultureInfo.InvariantCulture)))
                : "";
            bool authorized = StationHoldingRendering.Str(row, "outcome") == "AUTHORIZED";
            bool settled = row.TryGetProperty("settledAsLoaded", out JsonElement s) && s.ValueKind == JsonValueKind.True;
            html.Append("<tr>")
                .Append(DashboardPageRenderer.Cell(StationHoldingRendering.Str(row, "sublot") ?? ""))
                .Append(DashboardPageRenderer.Cell(StationHoldingRendering.Str(row, "agvId") ?? ""))
                .Append(DashboardPageRenderer.Cell($"{expected} → {loaded}"))
                .Append(DashboardPageRenderer.Cell(slots))
                .Append(DashboardPageRenderer.Cell(StationHoldingRendering.Str(row, "operatorId") ?? "（未记下）"))
                .Append(DashboardPageRenderer.Cell(StationHoldingRendering.Str(row, "requestedAt") ?? ""))
                .Append(DashboardPageRenderer.Cell(StationHoldingRendering.Str(row, "decidedAt") ?? ""))
                .Append(authorized
                    ? DashboardPageRenderer.Cell("同意")
                    : DashboardPageRenderer.Cell("拒绝：" + ChargingCardRendering.Coded(row, "reasonCode", "reasonDescription")))
                .Append(DashboardPageRenderer.Cell(!authorized ? "" : settled ? "是" : "否"))
                .Append("</tr>");
        }
        html.Append("</table></section>");
        return html.ToString();
    }
}
