namespace ControlServer.Application;

/// <summary>每区派车参数整表导入被拒时的原因码。每类一个稳定取值，现场与 L2 断言都按这些字符串读。</summary>
public static class DispatchZoneParameterImportReasonCodes
{
    /// <summary>表头不是受控的三列（列名里带单位，所以写错单位的表头也落在这里）。</summary>
    public const string HeaderInvalid = "DISPATCH_ZONE_PARAMETERS_CSV_HEADER_INVALID";

    /// <summary>某一行的字段数不对、字段带了首尾空白、分区为空，或表中间夹着空行。</summary>
    public const string RowMalformed = "DISPATCH_ZONE_PARAMETERS_CSV_ROW_MALFORMED";

    /// <summary>分区不在库内当前调度策略里。与分区归属表导入同一个取值。</summary>
    public const string DispatchZoneNotFound = "DISPATCH_ZONE_NOT_FOUND";

    /// <summary>同一个分区在表里出现了不止一次。</summary>
    public const string DispatchZoneDuplicated = "DISPATCH_ZONE_DUPLICATED";

    /// <summary>取值不是不带符号、不带单位的整数，或超出该列允许的范围。</summary>
    public const string ValueInvalid = "DISPATCH_ZONE_PARAMETER_VALUE_INVALID";
}

/// <summary>整表导入的结论。</summary>
public enum DispatchZoneParameterImportOutcome
{
    /// <summary>表通过校验且与当前版本不同：<c>--dry-run</c> 时只预览，否则已写成新版本。</summary>
    Accepted,

    /// <summary>表通过校验，但每个分区的取值都与当前版本相同：不产生新版本。</summary>
    Unchanged,

    /// <summary>表自身有错，整份拒绝，一行都没写。</summary>
    Rejected
}

/// <summary>
/// 被拒的一处。
/// </summary>
/// <remarks>
/// <paramref name="Line"/> 是 CSV 的物理行号，表头是第 1 行。<paramref name="DispatchZone"/> 是该行的分区原文，行本身读不出分区时为
/// <c>null</c>；<paramref name="Column"/> 只在取值错误时给出是哪一列。
/// </remarks>
public sealed record DispatchZoneParameterImportError(
    int Line, string ReasonCode, string? DispatchZone, string? Column, string Detail);

/// <summary>
/// 与当前版本相比取值变了的一个分区。<paramref name="Before"/> 为 <c>null</c> 表示当前版本里它未配置（或一版都没有），
/// <paramref name="After"/> 为 <c>null</c> 表示新表里它未配置。
/// </summary>
public sealed record DispatchZoneParameterChange(
    string DispatchZone, DispatchZoneParameters? Before, DispatchZoneParameters? After);

/// <summary>整表导入的结果。</summary>
/// <remarks>
/// <paramref name="PreviousVersion"/> 是导入判定时读到的当前版本号，一版都没有为 <c>null</c>。<paramref name="Version"/> 是
/// 写成的新版本；内容未变时是那个未变的当前版本；预览与被拒时为 <c>null</c>。
/// </remarks>
public sealed record DispatchZoneParameterImportResult(
    DispatchZoneParameterImportOutcome Outcome,
    bool DryRun,
    int EntryCount,
    long? PreviousVersion,
    DispatchZoneParameterTableVersion? Version,
    IReadOnlyList<DispatchZoneParameterImportError> Errors,
    IReadOnlyList<DispatchZoneParameterChange> Changes);

/// <summary>
/// FieldOps 整表导入每区派车参数（REQ-0198、REQ-0203；规格 5.1 第 2 条的导入形状，第 22.2 节第 2 条的量纲）。
/// </summary>
public sealed class DispatchZoneParameterImportService(
    IDispatchZoneParameterStore store,
    IDispatchZoneParameterImportFacts facts)
{
    /// <summary>受控 CSV 的表头，逐字相等才收。列名带单位：途中追加增量是计划路径代价（毫米），防饥饿阈值是秒。</summary>
    public static readonly IReadOnlyList<string> Header =
        ["dispatch_zone", "en_route_addition_max_path_cost_increase_mm", "starvation_threshold_seconds"];

    /// <summary>途中追加最大允许增量的上限（毫米，即 1 公里）。</summary>
    public const long MaxEnRouteAdditionPathCostIncrease = 1_000_000;

    /// <summary>防饥饿阈值的上限（秒，即 24 小时）。</summary>
    public const long MaxStarvationThresholdSeconds = 86_400;

    private readonly IDispatchZoneParameterStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly IDispatchZoneParameterImportFacts _facts = facts ?? throw new ArgumentNullException(nameof(facts));

    public Task<DispatchZoneParameterImportResult> ImportAsync(
        string csvText,
        bool dryRun,
        DateTimeOffset importedAt,
        CancellationToken cancellationToken) =>
        throw new NotImplementedException("control-server#216: implementation follows the tests.");
}

/// <summary>导入要判的库内事实。</summary>
public interface IDispatchZoneParameterImportFacts
{
    /// <summary>库内当前调度策略里有车服务的分区。</summary>
    Task<IReadOnlySet<string>> ReadDispatchZonesAsync(CancellationToken cancellationToken);
}
