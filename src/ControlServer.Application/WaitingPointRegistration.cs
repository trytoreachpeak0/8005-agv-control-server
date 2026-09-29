using System.Globalization;
using ControlServer.Domain;

namespace ControlServer.Application;

// 批次8-17（control-server#388）：等待点登记的受治理导入、覆盖判定（启动校验与导入预览共用）、
// 「这个等待点此刻能不能接新承诺」的判定，以及「这个等待点在哪个版本下被预占」的读法（REQ-0289、REQ-0297，规格 5.4）。
// 持久化是 control-server#386 的 IWaitingPointRegistry，本票零 migration。

/// <summary>等待点登记导入被拒时的原因码。每类一个稳定取值，现场与 L2 断言都按这些字符串读。</summary>
public static class WaitingPointImportReasonCodes
{
    /// <summary>表头不是受控的五列。</summary>
    public const string HeaderInvalid = "WAITING_POINTS_CSV_HEADER_INVALID";

    /// <summary>某一行字段数不对、字段带首尾空白、数字或启用状态写法不对、白名单写法不对，或表中间夹着空行。</summary>
    public const string RowMalformed = "WAITING_POINTS_CSV_ROW_MALFORMED";

    /// <summary>行的图号或站点目录的图号不是 <c>--map</c>（服务端所跑的图）。</summary>
    public const string MapMismatch = "WAITING_POINT_MAP_MISMATCH";

    /// <summary>站点不在运维带来的该图站点目录里。</summary>
    public const string StationNotInCatalog = "WAITING_POINT_STATION_NOT_IN_CATALOG";

    /// <summary>表里写的站名与站点目录里的不一致。</summary>
    public const string StationNameMismatch = "WAITING_POINT_STATION_NAME_MISMATCH";

    /// <summary>站点是机台站（站名能读出 AREA）。</summary>
    public const string MachineStation = "WAITING_POINT_IS_MACHINE_STATION";

    /// <summary>站点是某个任务类型绑定的固定站（批次 6 的绑定，生效版本或最新版本里）。</summary>
    public const string FixedTaskStation = "WAITING_POINT_IS_FIXED_TASK_STATION";

    /// <summary>站点承担已知的非等待点角色：关卡、充电点、充电准备点（规格 5.4：站点 212 不得登记为等待点）。</summary>
    public const string ReservedRole = "WAITING_POINT_STATION_HAS_OTHER_ROLE";

    /// <summary>同一个站在表里出现了不止一次。</summary>
    public const string StationDuplicated = "WAITING_POINT_STATION_DUPLICATED";

    /// <summary>白名单里有 <c>--fleet</c>（投运名册）之外的车。</summary>
    public const string VehicleOutsideFleet = "WAITING_POINT_VEHICLE_OUTSIDE_FLEET";
}

/// <summary>整表导入的结论。</summary>
public enum WaitingPointImportOutcome
{
    /// <summary>表通过校验且与当前版本不同：<c>--dry-run</c> 时只预览，否则已写成新版本。</summary>
    Accepted,

    /// <summary>表通过校验，但该图的等待点与当前版本逐项相同：不产生新版本。</summary>
    Unchanged,

    /// <summary>表有错，整份拒绝，一行都没写。</summary>
    Rejected
}

/// <summary>一次导入要判的输入。</summary>
/// <param name="MapId">服务端所跑的图（<c>JourneyRuntime:mapId</c>），表里每一行与站点目录都要是它。</param>
/// <param name="Fleet">投运名册的 <c>VehicleKey</c>，白名单只能取它们；也用来算覆盖。</param>
/// <param name="Catalog">运维带来的该图站点目录。</param>
public sealed record WaitingPointImportRequest(
    string CsvText,
    int MapId,
    IReadOnlyList<string> Fleet,
    RiotMapStationCatalogSnapshot Catalog);

/// <summary>被拒的一处。<paramref name="Line"/> 是 CSV 物理行号，表头是第 1 行；与表无关的错（站点目录的图号）为 0。</summary>
public sealed record WaitingPointImportError(int Line, string ReasonCode, int? StationId, string Detail);

/// <summary>与当前版本相比变了的一个等待点。<paramref name="Before"/> 为空是新增，<paramref name="After"/> 为空是删除。</summary>
public sealed record WaitingPointChange(int MapId, int StationId, WaitingPointEntry? Before, WaitingPointEntry? After);

/// <summary>
/// 新版本让一个等待点不再接它当前持有者的新承诺，而这个点上已有预占或占用（<c>REQ-0297</c>）。
/// 那条记录照旧引用它的版本，保留到车辆安全离点并完成对账；导入不取消订单、不释放独占。
/// </summary>
/// <param name="NewVersionReason">新版本下对持有者的判定原因（<see cref="WaitingPointEligibilityReasons"/>）。</param>
public sealed record WaitingPointRetainedReference(
    StationExclusivity Exclusivity,
    string NewVersionReason,
    string Retention);

/// <summary>
/// 一张图上的等待点够不够这支车队用（规格 5.4：投运车辆数大于 1 时，等待点少于车辆数即拒绝启动）。
/// </summary>
/// <param name="VehicleCount">投运车辆数。</param>
/// <param name="EnabledOnMap">这张图上启用的等待点数（排除项之前）。</param>
/// <param name="Assignable">
/// 最多能给多少辆车各分一个互不相同、它有资格停的启用等待点（二分图最大匹配）。白名单只对部分车开放时，这个数可以小于
/// <see cref="EnabledOnMap"/>：两个点都只对车 A 开放，车 B 就无点可去。
/// </param>
/// <param name="OnOtherMaps">登记在别的图上的等待点，按图号计数；它们对这台服务端不算数。</param>
/// <param name="Excluded">因与固定站重合而不算数的启用等待点的站号。</param>
/// <param name="Unassigned">
/// 在一种最优分配里分不到点的车（<c>VehicleKey</c>）。最优分配不止一种时，具体是哪几辆会随之不同，但个数总是
/// <see cref="VehicleCount"/> − <see cref="Assignable"/>：告诉现场「缺几个、哪些车没着落」，补点时照白名单看。
/// </param>
public sealed record WaitingPointCoverage(
    int MapId,
    int VehicleCount,
    int EnabledOnMap,
    int Assignable,
    IReadOnlyDictionary<int, int> OnOtherMaps,
    IReadOnlyList<int> Excluded,
    IReadOnlyList<string> Unassigned)
{
    /// <summary>还差几个点才够每辆车各分一个。</summary>
    public int Shortfall => VehicleCount <= 1 ? 0 : Math.Max(0, VehicleCount - Assignable);

    /// <summary>单车不校验；多车时每辆车都要能分到一个等待点。</summary>
    public bool Sufficient => VehicleCount <= 1 || Assignable >= VehicleCount;
}

/// <summary>整表导入的结果。</summary>
/// <remarks>
/// <para><paramref name="PreviousVersion"/> 是判定时读到的当前版本号；<paramref name="Version"/> 是写成的新版本，未变时是当前版本，预览与被拒时为空。</para>
/// <para>
/// <paramref name="CatalogMatchesServerConfirmation"/>：带来的目录是否就是服务端最近一次完整确认的那一份。只作为事实报出，不挡导入——
/// 多车服务端在没有登记时起不来，起不来就不会再确认目录，要求它相等会把现场卡死在改库上。
/// 为空表示服务端从没确认过这张图。
/// </para>
/// </remarks>
public sealed record WaitingPointImportResult(
    WaitingPointImportOutcome Outcome,
    bool DryRun,
    int EntryCount,
    long? PreviousVersion,
    WaitingPointRegistrationVersion? Version,
    IReadOnlyList<WaitingPointImportError> Errors,
    IReadOnlyList<WaitingPointChange> Changes,
    IReadOnlyList<WaitingPointRetainedReference> RetainedReferences,
    WaitingPointCoverage? Coverage,
    bool? CatalogMatchesServerConfirmation);

/// <summary>本图任务类型绑定的一个固定站，出自哪一版绑定集，以及那一版此刻是不是生效版本。</summary>
public sealed record FixedTaskStationOrigin(int StationId, long BindingSetVersion, bool Active);

/// <summary>
/// 「本图的任务类型固定站」只在这一处推导：生效版本 ∪ 最新版本（含还没激活的）。导入、只读查看、启动校验与承诺前的判定都经它，
/// 所以四处数的永远是同一批站。
/// </summary>
/// <remarks>
/// 最新版本也算，是因为批次 6 激活两步走，第一步写下的版本在第二步之前就已经是「马上要成为固定站」的承诺；它若永远没激活成功，
/// 用 <c>rollback-task-type-stations --version &lt;当前生效版本&gt;</c> 把生效内容再写成一个新版本，那一版就不再是最新（回滚写新版本、不拨指针）。
/// </remarks>
public static class WaitingPointFixedTaskStations
{
    public static IReadOnlyList<FixedTaskStationOrigin> Derive(
        TaskTypeStationBindingSetVersion? active,
        TaskTypeStationBindingSetVersion? latest)
    {
        List<FixedTaskStationOrigin> origins = [.. (active?.Bindings ?? []).Select(binding =>
            new FixedTaskStationOrigin(binding.StationRiotId, active!.Version, Active: true))];
        if (latest is not null && latest.Version != active?.Version)
        {
            origins.AddRange(latest.Bindings.Select(binding =>
                new FixedTaskStationOrigin(binding.StationRiotId, latest.Version, Active: false)));
        }
        return [.. origins.Distinct().OrderBy(origin => origin.StationId).ThenByDescending(origin => origin.Active)];
    }

    public static async Task<IReadOnlyList<FixedTaskStationOrigin>> ReadAsync(
        ITaskTypeStationBindingStore bindings, int mapId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        TaskTypeStationBindingSetVersion? active = await bindings.ReadActiveAsync(mapId, cancellationToken).ConfigureAwait(false);
        TaskTypeStationBindingSetVersion? latest = await bindings.ReadLatestAsync(mapId, cancellationToken).ConfigureAwait(false);
        return Derive(active, latest);
    }

    public static IReadOnlySet<int> StationIds(IEnumerable<FixedTaskStationOrigin> origins) =>
        origins.Select(origin => origin.StationId).ToHashSet();

    /// <summary>
    /// 例如 <c>305 (binding set version 2, latest, not active)</c>：给报错与日志用，一次没激活成功的绑定与生效绑定分得开。
    /// </summary>
    public static string Describe(int stationId, IEnumerable<FixedTaskStationOrigin> origins)
    {
        string[] sources =
        [
            .. origins.Where(origin => origin.StationId == stationId).Select(origin => origin.Active
                ? string.Create(CultureInfo.InvariantCulture, $"binding set version {origin.BindingSetVersion}, active")
                : string.Create(CultureInfo.InvariantCulture, $"binding set version {origin.BindingSetVersion}, latest, not active"))
        ];
        return sources.Length == 0
            ? stationId.ToString(CultureInfo.InvariantCulture)
            : string.Create(CultureInfo.InvariantCulture, $"{stationId} ({string.Join("; ", sources)})");
    }
}

/// <summary>导入要判的库内事实。</summary>
public interface IWaitingPointImportFacts
{
    /// <summary>该图任务类型绑定的固定站站号：生效版本与最新版本的并集。</summary>
    Task<IReadOnlySet<int>> ReadFixedTaskStationIdsAsync(int mapId, CancellationToken cancellationToken);

    /// <summary>服务端最近一次完整确认该图目录时记下的修订；从没确认过为空。</summary>
    Task<long?> ReadConfirmedCatalogRevisionAsync(int mapId, CancellationToken cancellationToken);
}

/// <summary>
/// FieldOps 整表导入一张图的等待点登记（<c>REQ-0289</c>、<c>REQ-0297</c>；规格 5.4）。
/// </summary>
/// <remarks>
/// <para>
/// <b>按图整表，任一错误整份拒绝。</b>表里是 <c>--map</c> 这张图的全部等待点；表里没列的即删除。别的图上的登记原样带进新版本，
/// 导入一张图不会抹掉另一张图的。全部错误一次报出。
/// </para>
/// <para>
/// <b>服务端停着也能导。</b>站点目录由运维带来，不要求它就是服务端最近确认的那一份（见 <see cref="WaitingPointImportResult"/>）；
/// 运行时兜底在 <see cref="WaitingPointEligibility"/>：等待点要在实时目录里、站名一致，才接新承诺。
/// </para>
/// <para>
/// <b>导入只影响新承诺。</b>新版本让某个已被预占或占用的点不再接它的持有者时，预览把那条记录列出来；那条记录不动，照旧引用它的版本
/// （<c>REQ-0297</c>）。点数因此少于车辆数时也不拒收：坏掉的点必须停得掉。覆盖不足写在结果里，下一次启动会被拒。
/// </para>
/// </remarks>
public sealed class WaitingPointImportService(
    IWaitingPointRegistry registry,
    IStationExclusivityStore exclusivity,
    IWaitingPointImportFacts facts)
{
    /// <summary>受控 CSV 的表头，逐字相等才收。白名单是 <c>VehicleKey</c> 用分号隔开，留空即同图全部车辆开放。</summary>
    public static readonly IReadOnlyList<string> Header = ["map_id", "station_id", "station_name", "enabled", "vehicle_scope"];

    /// <summary>站名含这些字的站承担关卡或充电角色，不得登记为等待点（规格 5.4 点名的 212「充电准备点1」、210「关卡」、211「充电点1」）。</summary>
    public static readonly IReadOnlyList<string> ReservedRoleNameMarkers = ["关卡", "充电"];

    /// <summary>预览里每条保留记录的说明。</summary>
    public const string RetentionNote =
        "Kept until the vehicle has safely left the point and the record is reconciled (REQ-0297); this import cancels no order and releases no exclusivity.";

    private readonly IWaitingPointRegistry _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    private readonly IStationExclusivityStore _exclusivity = exclusivity ?? throw new ArgumentNullException(nameof(exclusivity));
    private readonly IWaitingPointImportFacts _facts = facts ?? throw new ArgumentNullException(nameof(facts));

    public async Task<WaitingPointImportResult> ImportAsync(
        WaitingPointImportRequest request,
        bool dryRun,
        DateTimeOffset importedAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.CsvText);
        ArgumentNullException.ThrowIfNull(request.Fleet);
        ArgumentNullException.ThrowIfNull(request.Catalog);

        WaitingPointRegistrationVersion? current = await _registry.ReadCurrentAsync(cancellationToken).ConfigureAwait(false);
        long? confirmedRevision = await _facts.ReadConfirmedCatalogRevisionAsync(request.MapId, cancellationToken)
            .ConfigureAwait(false);
        bool? catalogMatches = confirmedRevision is long revision
            ? request.Catalog.MapId == request.MapId
              && TaskTypeStationCatalogEvidence.RevisionOf(request.Catalog.ContentSha256) == revision
            : null;

        List<WaitingPointImportError> errors = [];
        List<(int Line, WaitingPointEntry Entry)> rows = ReadCsv(request.CsvText, errors);
        if (request.Catalog.MapId != request.MapId)
        {
            errors.Add(new WaitingPointImportError(
                0,
                WaitingPointImportReasonCodes.MapMismatch,
                null,
                Invariant($"The station catalog is for Map {request.Catalog.MapId}, but the server runs Map {request.MapId} (--map).")));
        }
        IReadOnlySet<int> fixedStations = await _facts.ReadFixedTaskStationIdsAsync(request.MapId, cancellationToken)
            .ConfigureAwait(false);
        if (rows.Count > 0)
        {
            Judge(request, rows, fixedStations, errors);
        }

        if (errors.Count > 0)
        {
            return new WaitingPointImportResult(
                WaitingPointImportOutcome.Rejected, dryRun, 0, current?.Version, null,
                [.. errors.OrderBy(error => error.Line)], [], [], null, catalogMatches);
        }

        // The other maps' points ride along unchanged: importing one map must not delete another's registration.
        WaitingPointEntry[] proposed =
        [
            .. (current?.Points ?? []).Where(point => point.MapId != request.MapId),
            .. rows.Select(row => row.Entry)
        ];
        IReadOnlyList<WaitingPointChange> changes = Compare(current, rows.Select(row => row.Entry).ToArray(), request.MapId);
        // The same exclusion the startup check makes, so the preview and the start count the same points. An accepted table
        // holds no fixed station of this map (refused above), so today this changes nothing here; it keeps the two from
        // drifting apart if that refusal is ever relaxed. read-waiting-points is where it bites: a binding made after an import.
        WaitingPointCoverage coverage = WaitingPointCoverageCalculator.Evaluate(
            proposed, request.MapId, request.Fleet, fixedStations);
        if (changes.Count == 0)
        {
            return new WaitingPointImportResult(
                WaitingPointImportOutcome.Unchanged, dryRun, rows.Count, current?.Version, current, [], [], [], coverage,
                catalogMatches);
        }

        IReadOnlyList<WaitingPointRetainedReference> retained =
            await RetainedReferencesAsync(current, proposed, request, cancellationToken).ConfigureAwait(false);
        WaitingPointRegistrationVersion? written = dryRun
            ? null
            : await _registry.WriteVersionAsync(proposed, importedAt, cancellationToken).ConfigureAwait(false);
        return new WaitingPointImportResult(
            WaitingPointImportOutcome.Accepted, dryRun, rows.Count, current?.Version, written, [], changes, retained, coverage,
            catalogMatches);
    }

    private static void Judge(
        WaitingPointImportRequest request,
        List<(int Line, WaitingPointEntry Entry)> rows,
        IReadOnlySet<int> fixedStations,
        List<WaitingPointImportError> errors)
    {
        Dictionary<int, string> catalog = request.Catalog.Stations.ToDictionary(station => station.StationId, station => station.StationName);
        HashSet<string> fleet = new(request.Fleet, StringComparer.Ordinal);
        Dictionary<int, int> firstLineByStation = [];
        foreach ((int line, WaitingPointEntry entry) in rows)
        {
            int station = entry.StationId;
            if (entry.MapId != request.MapId)
            {
                errors.Add(new WaitingPointImportError(line, WaitingPointImportReasonCodes.MapMismatch, station,
                    Invariant($"The row is for Map {entry.MapId}, but the server runs Map {request.MapId} (--map).")));
            }
            if (!catalog.TryGetValue(station, out string? catalogName))
            {
                errors.Add(new WaitingPointImportError(line, WaitingPointImportReasonCodes.StationNotInCatalog, station,
                    Invariant($"Station {station} is not in the supplied catalog of Map {request.Catalog.MapId}.")));
            }
            else if (!string.Equals(catalogName, entry.StationName, StringComparison.Ordinal))
            {
                errors.Add(new WaitingPointImportError(line, WaitingPointImportReasonCodes.StationNameMismatch, station,
                    Invariant($"The row names station {station} '{entry.StationName}', the catalog '{catalogName}'.")));
            }
            string name = catalogName ?? entry.StationName;
            if (AreaNamedStationName.IsAreaNamed(name))
            {
                errors.Add(new WaitingPointImportError(line, WaitingPointImportReasonCodes.MachineStation, station,
                    Invariant($"Station {station} '{name}' is a machine station; a waiting point is a dedicated point (REQ-0289).")));
            }
            if (fixedStations.Contains(station))
            {
                errors.Add(new WaitingPointImportError(line, WaitingPointImportReasonCodes.FixedTaskStation, station,
                    Invariant($"Station {station} is bound as a task type's fixed station on Map {request.MapId}; a waiting point takes no business role (REQ-0289).")));
            }
            string? marker = ReservedRoleNameMarkers.FirstOrDefault(marker => name.Contains(marker, StringComparison.Ordinal));
            if (marker is not null)
            {
                errors.Add(new WaitingPointImportError(line, WaitingPointImportReasonCodes.ReservedRole, station,
                    Invariant($"Station {station} '{name}' is a gate or charging station ('{marker}'); it cannot be a waiting point (REQ-0289, specification 5.4).")));
            }
            if (firstLineByStation.TryGetValue(station, out int firstLine))
            {
                errors.Add(new WaitingPointImportError(line, WaitingPointImportReasonCodes.StationDuplicated, station,
                    Invariant($"The table already lists station {station} on line {firstLine}.")));
            }
            else
            {
                firstLineByStation[station] = line;
            }
            foreach (string vehicleKey in entry.VehicleScope.Where(vehicleKey => !fleet.Contains(vehicleKey)))
            {
                errors.Add(new WaitingPointImportError(line, WaitingPointImportReasonCodes.VehicleOutsideFleet, station,
                    Invariant($"The whitelist names {vehicleKey}, which is not in the fleet (--fleet).")));
            }
        }
    }

    private async Task<IReadOnlyList<WaitingPointRetainedReference>> RetainedReferencesAsync(
        WaitingPointRegistrationVersion? current,
        WaitingPointEntry[] proposed,
        WaitingPointImportRequest request,
        CancellationToken cancellationToken)
    {
        if (current is null)
        {
            return [];
        }
        // Judged against the registration alone: whether the proposal still accepts this holder's next commitment.
        WaitingPointRegistrationVersion next = current with { Version = current.Version + 1, Points = proposed };
        List<WaitingPointRetainedReference> retained = [];
        foreach (WaitingPointEntry point in current.Points.Where(point => point.MapId == request.MapId))
        {
            StationExclusivity? held = await _exclusivity.ReadAsync(point.MapId, point.StationId, cancellationToken)
                .ConfigureAwait(false);
            if (held is null || held.StationKind != StationExclusivityKinds.WaitingPoint)
            {
                continue;
            }
            WaitingPointEligibilityDecision decision = WaitingPointEligibility.JudgeRegistration(
                next, point.MapId, point.StationId, held.VehicleKey);
            if (!decision.Accepts)
            {
                retained.Add(new WaitingPointRetainedReference(held, decision.Reason, RetentionNote));
            }
        }
        return retained;
    }

    private static IReadOnlyList<WaitingPointChange> Compare(
        WaitingPointRegistrationVersion? current,
        IReadOnlyList<WaitingPointEntry> accepted,
        int mapId)
    {
        Dictionary<int, WaitingPointEntry> before = (current?.Points ?? [])
            .Where(point => point.MapId == mapId)
            .ToDictionary(point => point.StationId);
        Dictionary<int, WaitingPointEntry> after = accepted.ToDictionary(point => point.StationId);
        return
        [
            .. before.Keys.Union(after.Keys)
                .Order()
                .Select(station => new WaitingPointChange(
                    mapId, station, before.GetValueOrDefault(station), after.GetValueOrDefault(station)))
                .Where(change => !SameEntry(change.Before, change.After))
        ];
    }

    private static bool SameEntry(WaitingPointEntry? before, WaitingPointEntry? after) =>
        before is not null && after is not null
        && before.MapId == after.MapId
        && before.StationId == after.StationId
        && string.Equals(before.StationName, after.StationName, StringComparison.Ordinal)
        && before.Enabled == after.Enabled
        && before.VehicleScope.Order(StringComparer.Ordinal)
            .SequenceEqual(after.VehicleScope.Order(StringComparer.Ordinal), StringComparer.Ordinal);

    /// <summary>
    /// 受控格式：UTF-8，表头固定五列，字段不许有首尾空白，不支持引号。末尾空行忽略，表中间的空行是坏行——与分区归属表、每区派车参数同一套规矩。
    /// 只有表头的表是这张图一个等待点也没有。
    /// </summary>
    private static List<(int Line, WaitingPointEntry Entry)> ReadCsv(string csvText, List<WaitingPointImportError> errors)
    {
        string[] lines = csvText.TrimStart('﻿').Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (!lines[0].Split(',').SequenceEqual(Header, StringComparer.Ordinal))
        {
            errors.Add(new WaitingPointImportError(1, WaitingPointImportReasonCodes.HeaderInvalid, null,
                $"The controlled header is '{string.Join(",", Header)}'."));
            return [];
        }

        int lastContentIndex = lines.Length - 1;
        while (lastContentIndex >= 1 && lines[lastContentIndex].Length == 0)
        {
            lastContentIndex--;
        }

        List<(int Line, WaitingPointEntry Entry)> rows = [];
        for (int index = 1; index <= lastContentIndex; index++)
        {
            int line = index + 1;
            string[] fields = lines[index].Split(',');
            if (fields.Length != Header.Count
                || fields.Any(field => !string.Equals(field, field.Trim(), StringComparison.Ordinal))
                || !TryReadId(fields[0], out int mapId)
                || !TryReadId(fields[1], out int stationId)
                || fields[2].Length == 0
                || fields[3] is not ("true" or "false")
                || !TryReadScope(fields[4], out string[] scope))
            {
                errors.Add(new WaitingPointImportError(line, WaitingPointImportReasonCodes.RowMalformed, null,
                    "A row is five comma-separated fields with no outer whitespace: a positive map id, a positive station id, "
                    + "the station name, true or false, and the whitelist as VehicleKeys separated by ';' (empty for every vehicle)."));
                continue;
            }
            rows.Add((line, new WaitingPointEntry(mapId, stationId, fields[2], fields[3] == "true", scope)));
        }
        return rows;
    }

    // No sign, no leading zero, positive: the ids RIoT hands out.
    private static bool TryReadId(string text, out int value)
    {
        value = 0;
        return text.Length > 0 && text.All(char.IsAsciiDigit) && text[0] != '0'
            && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryReadScope(string text, out string[] scope)
    {
        scope = text.Length == 0 ? [] : text.Split(';');
        return scope.All(key => key.Length > 0 && string.Equals(key, key.Trim(), StringComparison.Ordinal))
            && scope.Distinct(StringComparer.Ordinal).Count() == scope.Length;
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// 等待点够不够车队用：给每辆车各分一个互不相同、它有资格停的启用等待点，最多能分几辆（二分图最大匹配）。
/// </summary>
/// <remarks>
/// 口径是匹配而不是总数（规格只写了总数，白名单是本票要定的）：规格 5.4 拒绝的理由是桩少于车时会互锁，而互锁看的是「每辆车有没有
/// 自己的点可去」。总数口径会放过「两车两点、两个点都只对车 A 开放」；按每辆车各自可用的点数算会放过「两车都只能停同一个点」。
/// 匹配两种都拦得住，白名单为空时它就等于启用点数。
/// </remarks>
public static class WaitingPointCoverageCalculator
{
    public static WaitingPointCoverage Evaluate(
        IReadOnlyList<WaitingPointEntry> points,
        int mapId,
        IReadOnlyList<string> vehicleKeys,
        IReadOnlySet<int>? excludedStations)
    {
        ArgumentNullException.ThrowIfNull(points);
        ArgumentNullException.ThrowIfNull(vehicleKeys);

        WaitingPointEntry[] enabled = [.. points.Where(point => point.MapId == mapId && point.Enabled).OrderBy(point => point.StationId)];
        int[] excluded = [.. enabled.Where(point => excludedStations?.Contains(point.StationId) == true).Select(point => point.StationId)];
        WaitingPointEntry[] usable = [.. enabled.Where(point => !excluded.Contains(point.StationId))];
        string[] vehicles = [.. vehicleKeys.Distinct(StringComparer.Ordinal)];
        Dictionary<int, int> onOtherMaps = points
            .Where(point => point.MapId != mapId)
            .GroupBy(point => point.MapId)
            .OrderBy(group => group.Key)
            .ToDictionary(group => group.Key, group => group.Count());
        HashSet<int> matched = MaximumMatching(vehicles, usable);
        return new WaitingPointCoverage(
            mapId, vehicles.Length, enabled.Length, matched.Count, onOtherMaps, excluded,
            [.. vehicles.Where((_, index) => !matched.Contains(index))]);
    }

    // Kuhn's augmenting paths. A fleet and its waiting points number in the single digits. Returns the matched vehicles.
    private static HashSet<int> MaximumMatching(string[] vehicles, WaitingPointEntry[] points)
    {
        int[] vehicleOfPoint = Enumerable.Repeat(-1, points.Length).ToArray();
        for (int vehicle = 0; vehicle < vehicles.Length; vehicle++)
        {
            TryAugment(vehicle, new bool[points.Length]);
        }
        return [.. vehicleOfPoint.Where(vehicle => vehicle >= 0)];

        bool TryAugment(int vehicle, bool[] visited)
        {
            for (int point = 0; point < points.Length; point++)
            {
                if (visited[point] || !Admits(points[point], vehicles[vehicle]))
                {
                    continue;
                }
                visited[point] = true;
                if (vehicleOfPoint[point] < 0 || TryAugment(vehicleOfPoint[point], visited))
                {
                    vehicleOfPoint[point] = vehicle;
                    return true;
                }
            }
            return false;
        }
    }

    /// <summary><c>WaitingPointVehicleScope</c>：白名单为空即同图全部车辆开放，否则只开放给白名单里的车（<c>REQ-0289</c>）。</summary>
    public static bool Admits(WaitingPointEntry point, string vehicleKey)
    {
        ArgumentNullException.ThrowIfNull(point);
        return point.VehicleScope.Count == 0 || point.VehicleScope.Contains(vehicleKey, StringComparer.Ordinal);
    }
}

/// <summary><see cref="WaitingPointEligibility"/> 的判定原因。</summary>
public static class WaitingPointEligibilityReasons
{
    public const string Accepts = "ACCEPTS";

    /// <summary>一版登记都没有，或当前版本里没有这个点（删除，或改成别的角色）。</summary>
    public const string NotRegistered = "WAITING_POINT_NOT_REGISTERED";

    public const string Disabled = "WAITING_POINT_DISABLED";

    /// <summary>白名单不含这辆车。</summary>
    public const string VehicleNotInScope = "WAITING_POINT_VEHICLE_NOT_IN_SCOPE";

    /// <summary>实时站点目录里没有这个站，或站名与登记的不一致：登记是离线导入的，现场地图可能已经变了。</summary>
    public const string NotInLiveCatalog = "WAITING_POINT_NOT_IN_LIVE_CATALOG";

    /// <summary>
    /// 这个站现在是某个任务类型绑定的固定站（生效版本或还没激活的最新版本）：登记之后被改成了业务角色（REQ-0297 的「改角色」）。
    /// 批次 6 的绑定激活不查等待点登记，所以这件事只能在这里拦。
    /// </summary>
    public const string RoleChangedToFixedTaskStation = "WAITING_POINT_ROLE_CHANGED_TO_FIXED_TASK_STATION";
}

/// <summary>判定结果。<paramref name="Version"/> 是据以判定的登记版本，一版都没有为空。</summary>
public sealed record WaitingPointEligibilityDecision(bool Accepts, string Reason, long? Version);

/// <summary>
/// 这个等待点此刻能不能接这辆车的新承诺（<c>REQ-0297</c>；给批次8-18 control-server#389 用）。
/// </summary>
/// <remarks>
/// 只回答「新承诺」。删除、停用、改角色、白名单收窄都只让它说「不接」；已有的预占与占用由 <see cref="IStationExclusivityStore"/> 的行持有，
/// 那一行记着它依据的登记版本（<see cref="StationExclusivity.WaitingPointVersion"/>），这里既不读也不改它——释放要离点证据，
/// 在批次8-19 control-server#390。
/// </remarks>
public static class WaitingPointEligibility
{
    /// <summary>
    /// 按当前登记、这张图的任务类型固定站与实时站点目录判。<paramref name="liveCatalog"/> 为空或不是这张图的，一律不接。
    /// </summary>
    /// <param name="fixedTaskStations">
    /// 这张图任务类型绑定的固定站站号，生效版本与最新版本的并集——与启动校验、FieldOps 导入同一个口径
    /// （<see cref="IWaitingPointImportFacts.ReadFixedTaskStationIdsAsync"/>）。
    /// </param>
    public static WaitingPointEligibilityDecision Judge(
        WaitingPointRegistrationVersion? current,
        IReadOnlySet<int> fixedTaskStations,
        RiotMapStationCatalogSnapshot? liveCatalog,
        int mapId,
        int stationId,
        string vehicleKey)
    {
        ArgumentNullException.ThrowIfNull(fixedTaskStations);
        WaitingPointEligibilityDecision registration = JudgeRegistration(current, mapId, stationId, vehicleKey);
        if (!registration.Accepts)
        {
            return registration;
        }
        if (fixedTaskStations.Contains(stationId))
        {
            return new WaitingPointEligibilityDecision(
                false, WaitingPointEligibilityReasons.RoleChangedToFixedTaskStation, current!.Version);
        }
        WaitingPointEntry point = current!.Points.Single(point => point.MapId == mapId && point.StationId == stationId);
        bool listed = liveCatalog is not null && liveCatalog.MapId == mapId && liveCatalog.Stations.Any(station =>
            station.StationId == stationId && string.Equals(station.StationName, point.StationName, StringComparison.Ordinal));
        return listed
            ? registration
            : new WaitingPointEligibilityDecision(false, WaitingPointEligibilityReasons.NotInLiveCatalog, current.Version);
    }

    /// <summary>只按登记判，不看目录。导入预览用它判「新版本还接不接这个点现在的持有者」。</summary>
    public static WaitingPointEligibilityDecision JudgeRegistration(
        WaitingPointRegistrationVersion? current,
        int mapId,
        int stationId,
        string vehicleKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vehicleKey);
        WaitingPointEntry? point = current?.Points.SingleOrDefault(point => point.MapId == mapId && point.StationId == stationId);
        string reason = point switch
        {
            null => WaitingPointEligibilityReasons.NotRegistered,
            { Enabled: false } => WaitingPointEligibilityReasons.Disabled,
            _ when !WaitingPointCoverageCalculator.Admits(point, vehicleKey) => WaitingPointEligibilityReasons.VehicleNotInScope,
            _ => WaitingPointEligibilityReasons.Accepts
        };
        return new WaitingPointEligibilityDecision(reason == WaitingPointEligibilityReasons.Accepts, reason, current?.Version);
    }
}

/// <summary>一个等待点此刻的独占，以及它被预占时依据的那一版登记里这个点的样子。</summary>
/// <param name="Exclusivity">此刻的独占；没人占为空。</param>
/// <param name="ReservedUnder">
/// <see cref="StationExclusivity.WaitingPointVersion"/> 那一版登记里的这个点。当前版本删了、停了、改了它，这里仍是预占时的样子。
/// </param>
public sealed record WaitingPointReservationReference(
    StationExclusivity? Exclusivity,
    WaitingPointRegistrationVersion? ReservedUnderVersion,
    WaitingPointEntry? ReservedUnder);

/// <summary>「这个等待点在哪个版本下被预占」的读法（<c>REQ-0297</c>）。</summary>
public static class WaitingPointReservationReader
{
    public static async Task<WaitingPointReservationReference> ReadAsync(
        IStationExclusivityStore exclusivity,
        IWaitingPointRegistry registry,
        int mapId,
        int stationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(exclusivity);
        ArgumentNullException.ThrowIfNull(registry);

        StationExclusivity? held = await exclusivity.ReadAsync(mapId, stationId, cancellationToken).ConfigureAwait(false);
        if (held?.WaitingPointVersion is not long version)
        {
            return new WaitingPointReservationReference(held, null, null);
        }
        WaitingPointRegistrationVersion? under = await registry.ReadVersionAsync(version, cancellationToken).ConfigureAwait(false);
        return new WaitingPointReservationReference(
            held,
            under,
            under?.Points.SingleOrDefault(point => point.MapId == mapId && point.StationId == stationId));
    }
}
