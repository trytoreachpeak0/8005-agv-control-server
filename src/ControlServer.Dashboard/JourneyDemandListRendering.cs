using System.Text.Json;

namespace ControlServer.Dashboard;

/// <summary>
/// 多需求旅程那一格的写法（批次7-12，control-server#217）：持货等单与旅程阻断两张卡片共用，一趟旅程一格，逐条写「业务键：状态」。
/// </summary>
/// <remarks>
/// 状态是服务端归属行上的原值（<c>JourneyDemandStatuses</c>）；看板不引用服务端程序集，所以这里是一份字面量。服务端给了一个这里不认识的值时原样写出，不猜。
/// </remarks>
public static class JourneyDemandListRendering
{
    /// <summary><c>demands</c> 数组写成一段文字；没有这个字段（旧服务端）或数组为空时返回空串。</summary>
    public static string Text(JsonElement journey)
    {
        if (!journey.TryGetProperty("demands", out JsonElement demands) || demands.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }
        return string.Join("；", demands.EnumerateArray().Select(demand =>
            $"{Label(demand)}：{Status(DashboardPageRenderer.Text(demand, "status"))}"));
    }

    private static string Label(JsonElement demand) =>
        demand.TryGetProperty("transportDemandKey", out JsonElement key) && key.ValueKind == JsonValueKind.String
            ? key.GetString()!
            : DashboardPageRenderer.Text(demand, "demandId");

    private static string Status(string status) => status switch
    {
        "PENDING_LOAD" => "待装",
        "LOADING" => "正在装",
        "LOADED" => "已装",
        "UNLOADED" => "已卸",
        "TERMINATED" => "已终结",
        _ => status
    };
}
