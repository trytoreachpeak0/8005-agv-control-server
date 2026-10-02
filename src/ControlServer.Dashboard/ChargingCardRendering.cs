using System.Net;
using System.Text;
using System.Text.Json;

namespace ControlServer.Dashboard;

/// <summary>充电看板四张卡片（批次9-10，control-server#408）共用的几种写法：码配中文说明、失联标记、Host 接口指引。</summary>
public static class ChargingCardRendering
{
    /// <summary>一个码与它的中文说明写成「码：说明」；没有码时为空。</summary>
    public static string Coded(JsonElement element, string codeProperty, string descriptionProperty)
    {
        string? code = StationHoldingRendering.Str(element, codeProperty);
        if (code is null)
        {
            return "";
        }
        string? description = StationHoldingRendering.Str(element, descriptionProperty);
        return description is null ? $"{code}：服务端没给中文说明，请报开发" : $"{code}：{description}";
    }

    /// <summary>「码：说明，自 时刻」：写在充电旅程上的码。</summary>
    public static string JourneyCode(JsonElement element)
    {
        string coded = Coded(element, "journeyCode", "journeyCodeDescription");
        return coded.Length == 0 || StationHoldingRendering.Str(element, "journeyCodeSince") is not { } since
            ? coded
            : $"{coded}，自 {since}";
    }

    /// <summary>车号，车辆失联时加一句：这一行是服务端的记录，车此刻的情况拿不到。</summary>
    public static string Vehicle(JsonElement element)
    {
        string agvId = StationHoldingRendering.Str(element, "agvId") ?? "";
        return element.TryGetProperty("vehicleInContact", out JsonElement inContact) && inContact.ValueKind == JsonValueKind.False
            ? agvId + "（车辆失联：服务端仍记着这一条，车此刻的情况拿不到）"
            : agvId;
    }

    /// <summary>看板只读：维修暂停、解除暂停、人工清桩与人工释放都走服务端接口，这里写出路径作为指引。</summary>
    public static string HostEntries(JsonElement fact)
    {
        if (!fact.TryGetProperty("hostEntries", out JsonElement entries) || entries.ValueKind != JsonValueKind.Object)
        {
            return "";
        }
        return "<p class=\"charging-host-entries\">"
               + WebUtility.HtmlEncode(
                   "看板只读，这里不能操作。维修暂停：" + DashboardPageRenderer.Text(entries, "maintenanceHold")
                   + "；解除桩暂停：" + DashboardPageRenderer.Text(entries, "recovery")
                   + "；人工清桩：" + DashboardPageRenderer.Text(entries, "manualClearance")
                   + "；人工释放站点独占：" + DashboardPageRenderer.Text(entries, "stationExclusivityRelease")
                   + "（都要有权限的人经服务端接口操作）")
               + "</p>";
    }

    /// <summary>一段说明文字，HTML 编码后放进带类名的段落。</summary>
    public static string Paragraph(string cssClass, string text) =>
        $"<p class=\"{cssClass}\">{WebUtility.HtmlEncode(text)}</p>";

    /// <summary>一个数组字段的每一项，按 <paramref name="row"/> 写成表格行；字段缺失时什么也不写。</summary>
    public static void Rows(StringBuilder html, JsonElement fact, string property, Func<JsonElement, string> row)
    {
        if (fact.TryGetProperty(property, out JsonElement items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in items.EnumerateArray())
            {
                html.Append(row(item));
            }
        }
    }

    /// <summary>一个数组字段的每一项；字段缺失或不是数组时为空。</summary>
    public static IEnumerable<JsonElement> Items(JsonElement fact, string property) =>
        fact.ValueKind == JsonValueKind.Object && fact.TryGetProperty(property, out JsonElement items) &&
        items.ValueKind == JsonValueKind.Array
            ? items.EnumerateArray()
            : [];

    /// <summary>数组字段有几项；缺失为 0。</summary>
    public static int Count(JsonElement fact, string property) =>
        fact.TryGetProperty(property, out JsonElement items) && items.ValueKind == JsonValueKind.Array ? items.GetArrayLength() : 0;
}
