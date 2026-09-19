namespace ControlServer.Domain;

/// <summary>
/// 一个 RIoT 站名是不是 AREA 命名的机台站：按 <c>_</c> 拆成一到三段、互不重复、每段都是 <see cref="AreaCodeFormat"/>。
/// </summary>
/// <remarks>
/// 与 <c>MapStationResolver</c> 解析机台站用的是同一条规则。这里单独放一份，是因为任务类型绑定的校验器在
/// Application 层，而 FieldOps 的激活（批次6-05）也要复用它，够不到 Host 里的解析器；两份的一致性由
/// <c>TaskTypeStationConfigurationValidatorTests</c> 逐条对照钉住。
/// </remarks>
public static class AreaNamedStationName
{
    /// <summary>站名里的 AREA 段；不是 AREA 命名时为空。</summary>
    public static IReadOnlyList<string> ParseAreaCodes(string? stationName)
    {
        if (string.IsNullOrEmpty(stationName))
        {
            return [];
        }
        string[] tokens = stationName.Split('_', StringSplitOptions.None);
        return tokens.Length is >= 1 and <= 3
               && tokens.Distinct(StringComparer.Ordinal).Count() == tokens.Length
               && tokens.All(AreaCodeFormat.IsValid)
            ? tokens
            : [];
    }

    public static bool IsAreaNamed(string? stationName) => ParseAreaCodes(stationName).Count > 0;
}
