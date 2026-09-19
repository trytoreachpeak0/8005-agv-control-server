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
/// <remarks>
/// <para>
/// <b>整表原子，只拦表自身的错。</b>一次导入是完整的一张表，表里没列的分区即未配置。五类错误任一出现即整份拒绝，一行都不写，并且一次把
/// 全部错误行报出来。通过的表写成一个新版本，版本行、分区行、治理快照与业务审计在 <see cref="IDispatchZoneParameterStore.WriteVersionAsync"/>
/// 的同一个事务里。
/// </para>
/// <para>
/// <b>内容与当前版本相同则不产生新版本</b>，一版都没有时导入空表也不产生（与分区归属表不同，那边同内容也出新版本）：读参数的派车轮按版本号判断参数变没变，一个只换了
/// 号的版本会被当成一次变更。回滚是把旧内容再导入一次，它与当前内容不同，所以照常形成新版本。两个导入并发时「比较」与「写入」要在同一个
/// 写事务里才可靠：调用方（FieldOps）在外面开写事务，<see cref="IDispatchZoneParameterStore.WriteVersionAsync"/> 加入它而不另开。
/// </para>
/// <para>
/// <b>不停车生效。</b>这里只写库；服务端不重启，读参数的派车轮在下一轮读到新版本。一轮之内只读一次版本，轮中发生的导入本轮不生效——
/// 那是读方（control-server#211、#214）的纪律，本服务保证的是版本号单调、每个版本写入后原样可读。
/// </para>
/// </remarks>
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

    public async Task<DispatchZoneParameterImportResult> ImportAsync(
        string csvText,
        bool dryRun,
        DateTimeOffset importedAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(csvText);

        DispatchZoneParameterTableVersion? current =
            await _store.ReadCurrentAsync(cancellationToken).ConfigureAwait(false);

        List<DispatchZoneParameterImportError> errors = [];
        List<(int Line, DispatchZoneParameters Zone)> rows = ReadCsv(csvText, errors);
        if (rows.Count > 0)
        {
            IReadOnlySet<string> zones = await _facts.ReadDispatchZonesAsync(cancellationToken).ConfigureAwait(false);
            Dictionary<string, int> firstLineByZone = new(StringComparer.Ordinal);
            foreach ((int line, DispatchZoneParameters zone) in rows)
            {
                if (!zones.Contains(zone.DispatchZone))
                {
                    errors.Add(new DispatchZoneParameterImportError(
                        line,
                        DispatchZoneParameterImportReasonCodes.DispatchZoneNotFound,
                        zone.DispatchZone,
                        null,
                        $"The stored dispatch policy has no zone '{zone.DispatchZone}'. Start the server once with the "
                        + "intended configuration first; a zone no vehicle serves does not exist here."));
                }
                if (firstLineByZone.TryGetValue(zone.DispatchZone, out int firstLine))
                {
                    errors.Add(new DispatchZoneParameterImportError(
                        line,
                        DispatchZoneParameterImportReasonCodes.DispatchZoneDuplicated,
                        zone.DispatchZone,
                        null,
                        FormattableString.Invariant($"The table already lists zone {zone.DispatchZone} on line {firstLine}.")));
                }
                else
                {
                    firstLineByZone[zone.DispatchZone] = line;
                }
            }
        }

        if (errors.Count > 0)
        {
            return new DispatchZoneParameterImportResult(
                DispatchZoneParameterImportOutcome.Rejected,
                dryRun,
                0,
                current?.Version,
                null,
                [.. errors.OrderBy(error => error.Line)],
                []);
        }

        DispatchZoneParameters[] accepted = [.. rows.Select(row => row.Zone)];
        IReadOnlyList<DispatchZoneParameterChange> changes = Compare(current, accepted);
        if (changes.Count == 0)
        {
            // Every zone reads the same as it does now, so a new version would change nothing but the version number -- and
            // the round that compares numbers would treat it as a change. A rollback always differs from what is current.
            // With no version at all every zone is already unconfigured, so an empty table changes nothing either.
            return new DispatchZoneParameterImportResult(
                DispatchZoneParameterImportOutcome.Unchanged, dryRun, accepted.Length, current?.Version, current, [], []);
        }

        DispatchZoneParameterTableVersion? written = dryRun
            ? null
            : await _store.WriteVersionAsync(accepted, importedAt, cancellationToken).ConfigureAwait(false);
        return new DispatchZoneParameterImportResult(
            DispatchZoneParameterImportOutcome.Accepted, dryRun, accepted.Length, current?.Version, written, [], changes);
    }

    /// <summary>
    /// 与当前版本相比取值变了的分区，按分区名排。比的是每个分区的有效取值：表里没列与列了但两个值都空，都是未配置。
    /// </summary>
    private static IReadOnlyList<DispatchZoneParameterChange> Compare(
        DispatchZoneParameterTableVersion? current,
        IReadOnlyList<DispatchZoneParameters> accepted)
    {
        Dictionary<string, DispatchZoneParameters> before = current is null
            ? new(StringComparer.Ordinal)
            : current.Zones.Values.Where(IsConfigured).ToDictionary(zone => zone.DispatchZone, StringComparer.Ordinal);
        Dictionary<string, DispatchZoneParameters> after =
            accepted.Where(IsConfigured).ToDictionary(zone => zone.DispatchZone, StringComparer.Ordinal);
        return
        [
            .. before.Keys.Union(after.Keys, StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .Select(zone => new DispatchZoneParameterChange(
                    zone, before.GetValueOrDefault(zone), after.GetValueOrDefault(zone)))
                .Where(change => change.Before != change.After)
        ];
    }

    private static bool IsConfigured(DispatchZoneParameters zone) =>
        zone.EnRouteAdditionMaxPathCostIncrease is not null || zone.StarvationThresholdSeconds is not null;

    /// <summary>
    /// 受控格式：UTF-8，表头固定三列，字段不许有首尾空白，不支持引号。末尾的空行忽略，表中间的空行是坏行——与分区归属表导入同一套规矩。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 只有表头的表是一张完整的空表：每个分区都未配置。这是朝 fail-safe 的方向（规格 8.6：未配置即本区禁止途中追加、只计龄不升级），
    /// 也是把参数整体撤回的唯一写法。
    /// </para>
    /// <para>
    /// 取值只收不带符号、不带单位的十进制整数，留空即未配置。途中追加增量 <c>0</c> 表示本区禁止（REQ-0198）；防饥饿阈值 <c>0</c>
    /// 不收：REQ-0203 没有零的语义，零阈值等于每条等待中的需求立刻升级，那不是一个能被现场批准的取值。
    /// </para>
    /// </remarks>
    private static List<(int Line, DispatchZoneParameters Zone)> ReadCsv(
        string csvText,
        List<DispatchZoneParameterImportError> errors)
    {
        string[] lines = csvText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (!lines[0].Split(',').SequenceEqual(Header, StringComparer.Ordinal))
        {
            errors.Add(new DispatchZoneParameterImportError(
                1,
                DispatchZoneParameterImportReasonCodes.HeaderInvalid,
                null,
                null,
                $"The controlled header is '{string.Join(",", Header)}': the increase in planned path cost in millimetres, "
                + "the starvation threshold in seconds."));
            return [];
        }

        int lastContentIndex = lines.Length - 1;
        while (lastContentIndex >= 1 && lines[lastContentIndex].Length == 0)
        {
            lastContentIndex--;
        }

        List<(int Line, DispatchZoneParameters Zone)> rows = [];
        for (int index = 1; index <= lastContentIndex; index++)
        {
            int line = index + 1;
            string[] fields = lines[index].Split(',');
            if (fields.Length != Header.Count ||
                fields[0].Length == 0 ||
                fields.Any(field => !string.Equals(field, field.Trim(), StringComparison.Ordinal)))
            {
                errors.Add(new DispatchZoneParameterImportError(
                    line,
                    DispatchZoneParameterImportReasonCodes.RowMalformed,
                    null,
                    null,
                    $"A row is {Header.Count} comma-separated fields with no outer whitespace, and names a zone."));
                continue;
            }

            string zone = fields[0];
            bool valid = true;
            long? increase = ReadValue(line, zone, fields[1], Header[1], 0, MaxEnRouteAdditionPathCostIncrease, errors, ref valid);
            long? threshold = ReadValue(line, zone, fields[2], Header[2], 1, MaxStarvationThresholdSeconds, errors, ref valid);
            if (valid)
            {
                rows.Add((line, new DispatchZoneParameters(zone, increase, threshold)));
            }
            else
            {
                // Still judged for zone and duplicate errors, so every bad line is reported in one run.
                rows.Add((line, new DispatchZoneParameters(zone, null, null)));
            }
        }
        return rows;
    }

    private static long? ReadValue(
        int line,
        string zone,
        string text,
        string column,
        long minimum,
        long maximum,
        List<DispatchZoneParameterImportError> errors,
        ref bool valid)
    {
        if (text.Length == 0)
        {
            return null;
        }
        // No sign, no unit, no leading zero: "020000" and "00" are not how anybody writes the value they mean.
        if (text.All(char.IsAsciiDigit) && (text.Length == 1 || text[0] != '0') &&
            long.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long value) &&
            value >= minimum && value <= maximum)
        {
            return value;
        }

        valid = false;
        errors.Add(new DispatchZoneParameterImportError(
            line,
            DispatchZoneParameterImportReasonCodes.ValueInvalid,
            zone,
            column,
            FormattableString.Invariant(
                $"'{text}' is not allowed in {column}: write a whole number from {minimum} to {maximum} with no sign or unit, or leave it empty for unconfigured.")));
        return null;
    }
}

/// <summary>导入要判的库内事实。</summary>
public interface IDispatchZoneParameterImportFacts
{
    /// <summary>库内当前调度策略里有车服务的分区。</summary>
    Task<IReadOnlySet<string>> ReadDispatchZonesAsync(CancellationToken cancellationToken);
}
