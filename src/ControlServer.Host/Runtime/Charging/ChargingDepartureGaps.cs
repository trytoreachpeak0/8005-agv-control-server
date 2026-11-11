namespace ControlServer.Host.Runtime.Charging;

/// <summary>
/// <see cref="ChargingAllocationReasons.DepartureNotProven"/> 的细节里出发门缺的那几项怎么写、怎么读回来：分配写在细节末尾的这一段之后、
/// 逗号分隔，看板的充电告警按它读出缺项（control-server#865）。
/// </summary>
public static class ChargingDepartureGaps
{
    /// <summary>细节里缺项那一段的开头。</summary>
    public const string Marker = "; departure: ";

    /// <summary>细节里出发门缺的那几项；细节里没有这一段时为空。</summary>
    public static IReadOnlyList<string> Of(string detail)
    {
        ArgumentNullException.ThrowIfNull(detail);
        int at = detail.LastIndexOf(Marker, StringComparison.Ordinal);
        return at < 0
            ? []
            : detail[(at + Marker.Length)..].Split(", ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
