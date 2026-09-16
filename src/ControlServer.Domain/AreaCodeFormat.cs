using System.Text.RegularExpressions;

namespace ControlServer.Domain;

/// <summary>
/// 一个 MES AREA 的书写形式。
/// </summary>
/// <remarks>
/// 只有一处定义：站名里解析 AREA（<c>MapStationResolver</c>）与 FieldOps 导入分区归属表都按这个规则判，
/// 两边分开写会在现场表现为「导入收了的 AREA 永远匹配不到站点」。
/// </remarks>
public static class AreaCodeFormat
{
    /// <summary>一个大写字母起头的字母数字段、一个连字号、一串数字，例如 <c>N01-1</c>。</summary>
    public const string Pattern = "^[A-Z][A-Z0-9]*-[0-9]+$";

    private static readonly Regex Compiled = new(
        Pattern,
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public static bool IsValid(string? area) => !string.IsNullOrEmpty(area) && Compiled.IsMatch(area);
}
