using ControlServer.Domain;

namespace ControlServer.Application;

/// <summary>整表导入被拒时的原因码。每类一个稳定取值，现场与 L2 断言都按这些字符串读。</summary>
public static class AreaAssignmentImportReasonCodes
{
    /// <summary>表头不是受控的三列。</summary>
    public const string HeaderInvalid = "AREA_ASSIGNMENT_CSV_HEADER_INVALID";

    /// <summary>某一行的字段数不对，或字段带了首尾空白。</summary>
    public const string RowMalformed = "AREA_ASSIGNMENT_CSV_ROW_MALFORMED";

    /// <summary>AREA 不符合 <see cref="AreaCodeFormat"/>。</summary>
    public const string AreaFormatInvalid = "AREA_FORMAT_INVALID";

    /// <summary>分组取值不属于库内已发布整车仓位模型的分组集合。</summary>
    public const string SlotPositionNotInPublishedModel = "SLOT_POSITION_NOT_IN_PUBLISHED_MODEL";

    /// <summary>这一行没有填分组。缺分组的映射不成立（REQ-0191）。</summary>
    public const string SlotPositionMissing = "SLOT_POSITION_MISSING";

    /// <summary>库里一个已发布的整车仓位模型都没有，判不了分组取值，整份拒绝。</summary>
    public const string NoPublishedSlotModel = "NO_PUBLISHED_SLOT_MODEL";

    /// <summary>同一个 AREA 在表里出现了不止一次。</summary>
    public const string AreaDuplicated = "AREA_DUPLICATED";

    /// <summary>分区不在库内当前调度策略里。</summary>
    public const string DispatchZoneNotFound = "DISPATCH_ZONE_NOT_FOUND";
}

/// <summary>整表导入的结论。</summary>
public enum AreaAssignmentImportOutcome
{
    /// <summary>表通过校验：<c>--dry-run</c> 时只预览，否则已写成新版本。</summary>
    Accepted,

    /// <summary>表自身有错，整份拒绝，一行都没写。</summary>
    Rejected
}

/// <summary>
/// 被拒的一处。
/// </summary>
/// <remarks>
/// <paramref name="Line"/> 是 CSV 的物理行号，表头是第 1 行，这样现场拿着行号就能直接翻到那一行。
/// <paramref name="Area"/> 是该行的 AREA 原文，行本身读不出 AREA 时为 <c>null</c>。
/// </remarks>
public sealed record AreaAssignmentImportError(int Line, string ReasonCode, string? Area, string Detail);

/// <summary>
/// 预览里的一条：这条在途需求按它冻结的那一版开的是 <paramref name="FrozenSlotPosition"/> 那一组，若它此刻
/// 才分配仓位、用的是新内容，开的就是 <paramref name="NewSlotPosition"/> 那一组。
/// </summary>
/// <remarks>
/// <paramref name="NewSlotPosition"/> 为 <c>null</c> 表示新表里不再映射这个 AREA。它只是提示：已冻结的需求
/// 不受影响，装货与卸货仍按冻结版本执行（REQ-0350）。
/// </remarks>
public sealed record AreaAssignmentPreviewEntry(
    string DemandId,
    string Area,
    long FrozenVersion,
    string? FrozenSlotPosition,
    string? NewSlotPosition);

/// <summary>整表导入的结果。</summary>
public sealed record AreaAssignmentImportResult(
    AreaAssignmentImportOutcome Outcome,
    bool DryRun,
    int EntryCount,
    AreaAssignmentTableVersion? Version,
    IReadOnlyList<AreaAssignmentImportError> Errors,
    IReadOnlyList<AreaAssignmentPreviewEntry> Preview);

/// <summary>
/// FieldOps 整表导入分区归属表（REQ-0350）：校验、预览，通过则写一个新版本。
/// </summary>
/// <remarks>
/// <para>
/// <b>只拦表自身的错。</b>五类错误任一出现即整份拒绝，一行都不写；一次把全部错误行报出来，而不是停在
/// 第一处——现场改一轮表要跑一趟，逐条挤牙膏等于逐条跑一趟。
/// </para>
/// <para>
/// <b>不做同一站点所挂 AREA 的分组一致性检查</b>（program#80 决议 7，REQ-0353）：同一站点可以挂前后两侧的
/// AREA，车身长，停在两台机台之间时前半段对着一台、后半段对着另一台。也不读站点位姿。
/// </para>
/// </remarks>
public sealed class AreaAssignmentImportService(
    IAreaAssignmentStore store,
    IDemandAreaAssignmentFreeze freezes,
    IAreaAssignmentImportFacts facts)
{
    /// <summary>受控 CSV 的表头，逐字相等才收。</summary>
    public static readonly IReadOnlyList<string> Header = ["area", "dispatch_zone", "slot_position"];

    private readonly IAreaAssignmentStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly IDemandAreaAssignmentFreeze _freezes = freezes ?? throw new ArgumentNullException(nameof(freezes));
    private readonly IAreaAssignmentImportFacts _facts = facts ?? throw new ArgumentNullException(nameof(facts));

    public async Task<AreaAssignmentImportResult> ImportAsync(
        string csvText,
        bool dryRun,
        DateTimeOffset importedAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(csvText);

        List<AreaAssignmentImportError> errors = [];
        List<(int Line, AreaAssignment Assignment)> rows = ReadCsv(csvText, errors);
        if (errors.Count > 0)
        {
            return Rejected(dryRun, errors);
        }

        IReadOnlySet<string> groups = await _facts
            .ReadPublishedSlotPositionGroupsAsync(cancellationToken)
            .ConfigureAwait(false);
        if (groups.Count == 0)
        {
            // 没有模型就没有分组集合，每一行的分组都判不了。这不是某一行的错，所以不挂在行上。
            return Rejected(
                dryRun,
                [
                    new AreaAssignmentImportError(
                        0,
                        AreaAssignmentImportReasonCodes.NoPublishedSlotModel,
                        null,
                        "No whole-vehicle slot model is published in this database, so the group values in "
                        + "the table cannot be judged. Seed the approved hardware facts first.")
                ]);
        }

        IReadOnlySet<string> zones = await _facts.ReadDispatchZonesAsync(cancellationToken).ConfigureAwait(false);
        Dictionary<string, int> firstLineByArea = new(StringComparer.Ordinal);

        foreach ((int line, AreaAssignment assignment) in rows)
        {
            if (!AreaCodeFormat.IsValid(assignment.Area))
            {
                errors.Add(new AreaAssignmentImportError(
                    line,
                    AreaAssignmentImportReasonCodes.AreaFormatInvalid,
                    assignment.Area,
                    $"An AREA is written as {AreaCodeFormat.Pattern}, for example C15-13."));
            }
            if (assignment.SlotPosition.Length == 0)
            {
                errors.Add(new AreaAssignmentImportError(
                    line,
                    AreaAssignmentImportReasonCodes.SlotPositionMissing,
                    assignment.Area,
                    "Every mapping assigns exactly one slot position group (REQ-0191)."));
            }
            else if (!groups.Contains(assignment.SlotPosition))
            {
                errors.Add(new AreaAssignmentImportError(
                    line,
                    AreaAssignmentImportReasonCodes.SlotPositionNotInPublishedModel,
                    assignment.Area,
                    "The published whole-vehicle slot models group slots as "
                    + $"{string.Join(", ", groups.Order(StringComparer.Ordinal))}."));
            }
            if (!zones.Contains(assignment.DispatchZone))
            {
                errors.Add(new AreaAssignmentImportError(
                    line,
                    AreaAssignmentImportReasonCodes.DispatchZoneNotFound,
                    assignment.Area,
                    $"The stored dispatch policy has no zone '{assignment.DispatchZone}'. Start the server "
                    + "once with the intended configuration first; a zone no vehicle serves does not exist here."));
            }
            if (firstLineByArea.TryGetValue(assignment.Area, out int firstLine))
            {
                errors.Add(new AreaAssignmentImportError(
                    line,
                    AreaAssignmentImportReasonCodes.AreaDuplicated,
                    assignment.Area,
                    FormattableString.Invariant(
                        $"The table already assigns AREA {assignment.Area} on line {firstLine}.")));
            }
            else
            {
                firstLineByArea[assignment.Area] = line;
            }
        }

        if (errors.Count > 0)
        {
            return Rejected(dryRun, errors);
        }

        AreaAssignment[] accepted = [.. rows.Select(row => row.Assignment)];
        // 预览在写入之前算：要比的是在途需求冻结的那一版与这份新内容，写完再算就把新版本自己算了进去。
        IReadOnlyList<AreaAssignmentPreviewEntry> preview =
            await PreviewAsync(accepted, cancellationToken).ConfigureAwait(false);
        AreaAssignmentTableVersion? written = dryRun
            ? null
            : await _store.WriteVersionAsync(accepted, importedAt, cancellationToken).ConfigureAwait(false);
        return new AreaAssignmentImportResult(
            AreaAssignmentImportOutcome.Accepted, dryRun, accepted.Length, written, [], preview);
    }

    /// <summary>
    /// 在途需求里，开门侧会随这份新内容变化的那些（REQ-0350）。
    /// </summary>
    /// <remarks>
    /// 「在途」由 <see cref="IDemandAreaAssignmentFreeze.ListInFlightAsync"/> 判——旅程未终结的冻结，宁可多列
    /// 不可漏列。读不出 AREA 的需求跳过：没有 AREA 就没有映射可比。
    /// </remarks>
    private async Task<IReadOnlyList<AreaAssignmentPreviewEntry>> PreviewAsync(
        IReadOnlyList<AreaAssignment> accepted,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<DemandAreaAssignmentFreeze> inFlight =
            await _freezes.ListInFlightAsync(cancellationToken).ConfigureAwait(false);
        if (inFlight.Count == 0)
        {
            return [];
        }

        IReadOnlyDictionary<string, string> areas = await _facts
            .ReadDemandAreasAsync([.. inFlight.Select(freeze => freeze.DemandId)], cancellationToken)
            .ConfigureAwait(false);
        Dictionary<string, string> newSlotPositionByArea = accepted.ToDictionary(
            assignment => assignment.Area, assignment => assignment.SlotPosition, StringComparer.Ordinal);
        Dictionary<long, AreaAssignmentTableVersion?> frozenVersions = [];

        List<AreaAssignmentPreviewEntry> preview = [];
        foreach (DemandAreaAssignmentFreeze freeze in inFlight)
        {
            if (!areas.TryGetValue(freeze.DemandId, out string? area))
            {
                continue;
            }
            if (!frozenVersions.TryGetValue(freeze.Version, out AreaAssignmentTableVersion? frozen))
            {
                frozen = await _store.ReadVersionAsync(freeze.Version, cancellationToken).ConfigureAwait(false);
                frozenVersions[freeze.Version] = frozen;
            }
            string? frozenSlotPosition = frozen is not null && frozen.ByArea.TryGetValue(area, out AreaAssignment? held)
                ? held.SlotPosition
                : null;
            string? newSlotPosition = newSlotPositionByArea.GetValueOrDefault(area);
            if (!string.Equals(frozenSlotPosition, newSlotPosition, StringComparison.Ordinal))
            {
                preview.Add(new AreaAssignmentPreviewEntry(
                    freeze.DemandId, area, freeze.Version, frozenSlotPosition, newSlotPosition));
            }
        }
        return [.. preview.OrderBy(entry => entry.DemandId, StringComparer.Ordinal)];
    }

    /// <summary>
    /// 受控格式：UTF-8，表头固定三列，字段不许有首尾空白。
    /// </summary>
    /// <remarks>
    /// 不支持引号包裹的字段：三列的取值都是标识符，没有一个能合法地含逗号，收了引号只会让「这一行到底是
    /// 什么」多一种解释。
    /// <para>
    /// <b>末尾的空行忽略，表中间的空行是坏行。</b>文件以换行结尾时一定会多出一个空串，那是编辑器留下的，
    /// 连着几个也一样；中间冒出来的空行说明这份表被编辑坏了，静默跳过它就等于收下一份读起来和作者写的不一样
    /// 的表，那正是这条动词要避免的方向。
    /// </para>
    /// </remarks>
    private static List<(int Line, AreaAssignment Assignment)> ReadCsv(
        string csvText,
        List<AreaAssignmentImportError> errors)
    {
        string[] lines = csvText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        string[] header = lines.Length == 0 ? [] : lines[0].Split(',');
        if (!header.SequenceEqual(Header, StringComparer.Ordinal))
        {
            errors.Add(new AreaAssignmentImportError(
                1,
                AreaAssignmentImportReasonCodes.HeaderInvalid,
                null,
                $"The controlled header is '{string.Join(",", Header)}'."));
            return [];
        }

        // 末尾连着的空行先划出去，剩下的每一行都得是一行内容——包括空行。
        int lastContentIndex = lines.Length - 1;
        while (lastContentIndex >= 1 && lines[lastContentIndex].Length == 0)
        {
            lastContentIndex--;
        }

        List<(int Line, AreaAssignment Assignment)> rows = [];
        for (int index = 1; index <= lastContentIndex; index++)
        {
            int line = index + 1;
            string[] fields = lines[index].Split(',');
            if (fields.Length != Header.Count ||
                fields.Any(field => !string.Equals(field, field.Trim(), StringComparison.Ordinal)))
            {
                errors.Add(new AreaAssignmentImportError(
                    line,
                    AreaAssignmentImportReasonCodes.RowMalformed,
                    null,
                    $"A row is {Header.Count} comma-separated fields with no outer whitespace."));
                continue;
            }
            rows.Add((line, new AreaAssignment(fields[0], fields[1], fields[2])));
        }
        return rows;
    }

    private static AreaAssignmentImportResult Rejected(
        bool dryRun,
        IReadOnlyList<AreaAssignmentImportError> errors) =>
        new(AreaAssignmentImportOutcome.Rejected, dryRun, 0, null, errors, []);
}

/// <summary>
/// 导入要判的那几份库内事实。
/// </summary>
/// <remarks>
/// FieldOps 只开 SQLite、不读设置文件，所以「当前车型有哪些分组」「有哪些分区」都从库里取：分组取已发布
/// 整车仓位模型（REQ-0349，不在代码里写死），分区取服务端启动时按配置写入的调度策略。
/// </remarks>
public interface IAreaAssignmentImportFacts
{
    /// <summary>库内全部已发布整车仓位模型出现过的 <c>SlotPosition</c> 去重值。</summary>
    Task<IReadOnlySet<string>> ReadPublishedSlotPositionGroupsAsync(CancellationToken cancellationToken);

    /// <summary>库内当前调度策略里有车服务的分区。</summary>
    Task<IReadOnlySet<string>> ReadDispatchZonesAsync(CancellationToken cancellationToken);

    /// <summary>给定需求的 AREA；没有 AREA 的需求不在结果里。</summary>
    Task<IReadOnlyDictionary<string, string>> ReadDemandAreasAsync(
        IReadOnlyCollection<string> demandIds,
        CancellationToken cancellationToken);
}
