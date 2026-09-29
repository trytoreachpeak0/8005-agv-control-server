using System.Globalization;
using System.Text.Json;
using ControlServer.Domain;

namespace ControlServer.Application;

// 批次9-02（control-server#400）：充电桩名册的受治理导入（REQ-0171、REQ-0288；规格 5.5、8.6）。
// 持久化是 control-server#399 的 IChargerRoster，本票零 migration。
//
// 用户 2026-09-29 定的并行期隔离：窗口外名册置空，窗口内登记 26 号图站 211。置空与启用都经这一个导入：
// 置空就是导入一份零条目的名册文件，与登记 211 走同一条校验、同一次治理发布、同一种审计——没有第三条路。

/// <summary>名册导入被拒时的原因码。每类一个稳定取值，现场与 L2 断言都按这些字符串读。</summary>
public static class ChargerRosterImportReasonCodes
{
    /// <summary>文件不是受控的 JSON：解析不了、缺字段、多字段、字段类型或写法不对。</summary>
    public const string FileMalformed = "CHARGER_ROSTER_FILE_MALFORMED";

    /// <summary>桩的图号或站点目录的图号不是 <c>--map</c>（服务端所跑的图）。</summary>
    public const string MapMismatch = "CHARGER_ROSTER_MAP_MISMATCH";

    /// <summary>桩（或它的进点、出点）不在运维带来的该图站点目录里。</summary>
    public const string StationNotInCatalog = "CHARGER_ROSTER_STATION_NOT_IN_CATALOG";

    /// <summary>文件里写的站名与站点目录里的不一致。</summary>
    public const string StationNameMismatch = "CHARGER_ROSTER_STATION_NAME_MISMATCH";

    /// <summary>同一个站在文件里出现了不止一次。</summary>
    public const string StationDuplicated = "CHARGER_ROSTER_STATION_DUPLICATED";

    /// <summary>站点登记在当前等待点登记版本里（REQ-0289：等待点不承担业务角色）。</summary>
    public const string WaitingPoint = "CHARGER_ROSTER_STATION_IS_WAITING_POINT";

    /// <summary>站点是某个任务类型绑定的固定站（生效版本或还没激活的最新版本）。</summary>
    public const string FixedTaskStation = "CHARGER_ROSTER_STATION_IS_FIXED_TASK_STATION";

    /// <summary>站点是机台站（站名能读出 AREA）。</summary>
    public const string MachineStation = "CHARGER_ROSTER_STATION_IS_MACHINE_STATION";

    /// <summary>候选车辆子集里有 <c>--fleet</c>（投运名册）之外的车。</summary>
    public const string VehicleOutsideFleet = "CHARGER_ROSTER_VEHICLE_OUTSIDE_FLEET";
}

/// <summary>名册导入的审计动作名。写成新版本的那一条是 <see cref="ChargingGovernance.RosterVersionImportedAction"/>，由治理发布写。</summary>
public static class ChargerRosterImportAudit
{
    /// <summary>
    /// 导入的内容与当前版本逐字相同，不产生新版本；仍记一条，因为置空与启用每一次都要留痕（用户 2026-09-29）——
    /// 「关窗时名册本来就是空的」也是一次有人做过的关窗。
    /// </summary>
    public const string UnchangedAction = "CHARGER_ROSTER_IMPORT_UNCHANGED";
}

/// <summary>整份导入的结论。</summary>
public enum ChargerRosterImportOutcome
{
    /// <summary>通过校验且与当前版本不同（或一版都没有）：<c>--dry-run</c> 时只预览，否则已写成新版本。</summary>
    Accepted,

    /// <summary>通过校验，但桩与当前版本逐项相同：不产生新版本，记一条「已是此内容」的审计（预览时不记）。</summary>
    Unchanged,

    /// <summary>文件有错，整份拒绝，一行都没写。</summary>
    Rejected
}

/// <summary>一次导入要判的输入。</summary>
/// <param name="MapId">服务端所跑的图（<c>JourneyRuntime:mapId</c>）；每个桩与站点目录都要是它。</param>
/// <param name="Fleet">投运名册的 <c>VehicleKey</c>，候选车辆子集只能取它们。</param>
/// <param name="Catalog">运维带来的该图站点目录。桩的身份只来自这份名册文件，目录只用来核对站号与站名在图上。</param>
public sealed record ChargerRosterImportRequest(
    string FileText,
    int MapId,
    IReadOnlyList<string> Fleet,
    RiotMapStationCatalogSnapshot Catalog);

/// <summary>被拒的一处。<paramref name="StationId"/> 与文件整体有关时为空。</summary>
public sealed record ChargerRosterImportError(string ReasonCode, int? StationId, string Detail);

/// <summary>与当前版本相比变了的一个桩。<paramref name="Before"/> 为空是新增，<paramref name="After"/> 为空是删除。</summary>
public sealed record ChargerRosterChange(int MapId, int StationId, ChargerRosterEntry? Before, ChargerRosterEntry? After);

/// <summary>
/// 导入时正在进行的充电周期与桩上的预占：它们按原快照（周期记下的名册版本与策略版本）继续到结束，本次导入只影响新的分配
/// （<c>REQ-0282</c>、<c>REQ-0173</c>）。导入不取消订单、不释放预占、不结束周期。
/// </summary>
/// <param name="OpenCycles">未结束（阶段不是 <c>ENDED</c>）的周期，按分配时刻排。</param>
/// <param name="ChargerReservations">
/// 名册上（当前版本、新版本或进行中的周期所在）的桩此刻的 <c>CHARGER</c> 独占，按站号排。
/// </param>
public sealed record ChargingWorkInProgress(
    IReadOnlyList<ChargingCycle> OpenCycles,
    IReadOnlyList<StationExclusivity> ChargerReservations)
{
    public const string Retention =
        "These continue under the snapshot they started with until they end; this import affects only new allocations "
        + "(REQ-0282, REQ-0173). It cancels no order, releases no reservation and ends no cycle.";

    public bool Any => OpenCycles.Count > 0 || ChargerReservations.Count > 0;
}

/// <summary>整份导入的结果。</summary>
/// <param name="PreviousVersion">判定时读到的当前版本号；一版都没有为空。</param>
/// <param name="Version">写成的新版本；未变时是当前版本；预览与被拒时为空。</param>
/// <param name="WindowCanClose">
/// 导入的是空名册（关窗）时：没有进行中的周期与桩预占才为真，有就为假——窗口还不能关，名册已置空只挡新的分配。
/// 导入的不是空名册时为空。
/// </param>
public sealed record ChargerRosterImportResult(
    ChargerRosterImportOutcome Outcome,
    bool DryRun,
    int EntryCount,
    long? PreviousVersion,
    ChargerRosterVersion? Version,
    ChargerRosterApproval? Approval,
    IReadOnlyList<ChargerRosterImportError> Errors,
    IReadOnlyList<ChargerRosterChange> Changes,
    ChargingWorkInProgress InProgress,
    bool? WindowCanClose);

/// <summary>名册导入要判的库内事实。</summary>
public interface IChargerRosterImportFacts
{
    /// <summary>该图任务类型绑定的固定站站号：生效版本与最新版本的并集（与等待点导入同一个口径）。</summary>
    Task<IReadOnlySet<int>> ReadFixedTaskStationIdsAsync(int mapId, CancellationToken cancellationToken);

    /// <summary>全部未结束的充电周期，按分配时刻排。</summary>
    Task<IReadOnlyList<ChargingCycle>> ListOpenCyclesAsync(CancellationToken cancellationToken);
}

/// <summary>
/// FieldOps 整份导入充电桩名册（<c>REQ-0171</c>、<c>REQ-0288</c>；规格 5.5）。
/// </summary>
/// <remarks>
/// <para>
/// <b>桩的身份只来自这份文件。</b>RIoT 没有充电站类型（26 号图全是 <c>type=1</c>），所以这里不按站点类型或站名认桩：站点目录只用来核对
/// 「这个站号在这张图上、叫这个名字」，从不用来推断「它是不是桩」（白名单文档 1.2 节）。
/// </para>
/// <para>
/// <b>整份，任一错误整份拒绝，全部错误一次报出。</b>零条目不是错误：它就是「名册置空」，服务端照常启动、照常派搬运，
/// 退化行为（人工充电等待并告警）在批次9-06。
/// </para>
/// <para>
/// <b>只影响新的分配。</b>输出列出进行中的充电周期与桩上的预占；它们按原快照继续到结束，本服务不碰它们。
/// </para>
/// </remarks>
public sealed class ChargerRosterImportService(
    IChargerRoster roster,
    IWaitingPointRegistry waitingPoints,
    IStationExclusivityStore exclusivity,
    IChargerRosterImportFacts facts,
    IGovernanceAuditWriter audit)
{
    private static readonly string[] FileFields = ["approvedBy", "approvalBasis", "changeNote", "chargers"];

    private static readonly string[] ChargerFields =
        ["mapId", "stationId", "stationName", "entryStationId", "exitStationId", "vehicleScope"];

    private readonly IChargerRoster _roster = roster ?? throw new ArgumentNullException(nameof(roster));
    private readonly IWaitingPointRegistry _waitingPoints = waitingPoints ?? throw new ArgumentNullException(nameof(waitingPoints));
    private readonly IStationExclusivityStore _exclusivity = exclusivity ?? throw new ArgumentNullException(nameof(exclusivity));
    private readonly IChargerRosterImportFacts _facts = facts ?? throw new ArgumentNullException(nameof(facts));
    private readonly IGovernanceAuditWriter _audit = audit ?? throw new ArgumentNullException(nameof(audit));

    /// <summary>
    /// 判定并（非预览时）写入。调用方要把它放在一个写事务里：读当前版本、比较与写入因此在同一个事务，两个几乎同时的导入一个写成、
    /// 另一个读到它报「已是此内容」，或被版本号的主键整体回滚。
    /// </summary>
    public async Task<ChargerRosterImportResult> ImportAsync(
        ChargerRosterImportRequest request,
        bool dryRun,
        DateTimeOffset importedAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.FileText);
        ArgumentNullException.ThrowIfNull(request.Fleet);
        ArgumentNullException.ThrowIfNull(request.Catalog);

        ChargerRosterVersion? current = await _roster.ReadCurrentAsync(cancellationToken).ConfigureAwait(false);
        List<ChargerRosterImportError> errors = [];
        (ChargerRosterApproval? approval, List<ChargerRosterEntry> chargers) = ReadFile(request.FileText, errors);
        if (request.Catalog.MapId != request.MapId)
        {
            errors.Add(new ChargerRosterImportError(
                ChargerRosterImportReasonCodes.MapMismatch,
                null,
                Invariant($"The station catalog is for Map {request.Catalog.MapId}, but the server runs Map {request.MapId} (--map).")));
        }
        if (chargers.Count > 0)
        {
            WaitingPointRegistrationVersion? registration =
                await _waitingPoints.ReadCurrentAsync(cancellationToken).ConfigureAwait(false);
            IReadOnlySet<int> fixedStations =
                await _facts.ReadFixedTaskStationIdsAsync(request.MapId, cancellationToken).ConfigureAwait(false);
            Judge(request, chargers, registration, fixedStations, errors);
        }

        if (errors.Count > 0)
        {
            return new ChargerRosterImportResult(
                ChargerRosterImportOutcome.Rejected, dryRun, 0, current?.Version, null, approval, errors, [],
                new ChargingWorkInProgress([], []), null);
        }

        IReadOnlyList<ChargerRosterChange> changes = Compare(current, chargers);
        ChargingWorkInProgress inProgress = await InProgressAsync(current, chargers, cancellationToken).ConfigureAwait(false);
        bool? windowCanClose = chargers.Count == 0 ? !inProgress.Any : null;
        // A roster that exists and names the same chargers. "No version yet" is never unchanged: importing the empty
        // roster onto nothing writes version 1, so the record says the roster was deliberately emptied.
        if (current is not null && changes.Count == 0)
        {
            if (!dryRun)
            {
                await _audit.WriteBusinessAsync(
                    new GovernanceAuditEntry(
                        ChargerRosterImportAudit.UnchangedAction,
                        GovernedObjectKind.ChargerRoster,
                        ChargingGovernance.RosterObjectId,
                        current.Version,
                        GovernanceActionOutcome.Succeeded,
                        UnchangedDetail(current, approval!, chargers.Count),
                        current.SnapshotId),
                    importedAt,
                    cancellationToken).ConfigureAwait(false);
            }
            return new ChargerRosterImportResult(
                ChargerRosterImportOutcome.Unchanged, dryRun, chargers.Count, current.Version, current, approval, [], [],
                inProgress, windowCanClose);
        }

        ChargerRosterVersion? written = dryRun
            ? null
            : await _roster.WriteVersionAsync(chargers, approval!, importedAt, cancellationToken).ConfigureAwait(false);
        return new ChargerRosterImportResult(
            ChargerRosterImportOutcome.Accepted, dryRun, chargers.Count, current?.Version, written, approval, [], changes,
            inProgress, windowCanClose);
    }

    private static void Judge(
        ChargerRosterImportRequest request,
        List<ChargerRosterEntry> chargers,
        WaitingPointRegistrationVersion? registration,
        IReadOnlySet<int> fixedStations,
        List<ChargerRosterImportError> errors)
    {
        Dictionary<int, string> catalog = request.Catalog.Stations.ToDictionary(station => station.StationId, station => station.StationName);
        HashSet<string> fleet = new(request.Fleet, StringComparer.Ordinal);
        HashSet<int> waitingPoints =
        [
            .. (registration?.Points ?? []).Where(point => point.MapId == request.MapId).Select(point => point.StationId)
        ];
        HashSet<int> seen = [];
        foreach (ChargerRosterEntry charger in chargers)
        {
            int station = charger.StationId;
            if (charger.MapId != request.MapId)
            {
                errors.Add(new ChargerRosterImportError(ChargerRosterImportReasonCodes.MapMismatch, station,
                    Invariant($"Station {station} is registered on Map {charger.MapId}, but the server runs Map {request.MapId} (--map).")));
            }
            if (!catalog.TryGetValue(station, out string? catalogName))
            {
                errors.Add(new ChargerRosterImportError(ChargerRosterImportReasonCodes.StationNotInCatalog, station,
                    Invariant($"Station {station} is not in the supplied catalog of Map {request.Catalog.MapId}.")));
            }
            else if (!string.Equals(catalogName, charger.StationName, StringComparison.Ordinal))
            {
                errors.Add(new ChargerRosterImportError(ChargerRosterImportReasonCodes.StationNameMismatch, station,
                    Invariant($"The file names station {station} '{charger.StationName}', the catalog '{catalogName}'.")));
            }
            foreach ((string role, int? point) in new[] { ("entry", charger.EntryStationId), ("exit", charger.ExitStationId) })
            {
                if (point is int id && !catalog.ContainsKey(id))
                {
                    errors.Add(new ChargerRosterImportError(ChargerRosterImportReasonCodes.StationNotInCatalog, station,
                        Invariant($"The {role} station {id} of charger {station} is not in the supplied catalog of Map {request.Catalog.MapId}.")));
                }
            }
            if (!seen.Add(station))
            {
                errors.Add(new ChargerRosterImportError(ChargerRosterImportReasonCodes.StationDuplicated, station,
                    Invariant($"The file lists station {station} more than once.")));
            }
            if (waitingPoints.Contains(station))
            {
                errors.Add(new ChargerRosterImportError(ChargerRosterImportReasonCodes.WaitingPoint, station,
                    Invariant($"Station {station} is a waiting point in registration version {registration!.Version}; a waiting point takes no business role (REQ-0289).")));
            }
            if (fixedStations.Contains(station))
            {
                errors.Add(new ChargerRosterImportError(ChargerRosterImportReasonCodes.FixedTaskStation, station,
                    Invariant($"Station {station} is bound as a task type's fixed station on Map {request.MapId}; a charger is not shared (REQ-0171).")));
            }
            if (AreaNamedStationName.IsAreaNamed(catalogName ?? charger.StationName))
            {
                errors.Add(new ChargerRosterImportError(ChargerRosterImportReasonCodes.MachineStation, station,
                    Invariant($"Station {station} is a machine station; a charger is not shared (REQ-0171).")));
            }
            foreach (string vehicleKey in charger.VehicleScope.Where(vehicleKey => !fleet.Contains(vehicleKey)))
            {
                errors.Add(new ChargerRosterImportError(ChargerRosterImportReasonCodes.VehicleOutsideFleet, station,
                    Invariant($"The vehicle subset of charger {station} names {vehicleKey}, which is not in the fleet (--fleet).")));
            }
        }
    }

    private async Task<ChargingWorkInProgress> InProgressAsync(
        ChargerRosterVersion? current,
        IReadOnlyList<ChargerRosterEntry> proposed,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ChargingCycle> cycles = await _facts.ListOpenCyclesAsync(cancellationToken).ConfigureAwait(false);
        (int MapId, int StationId)[] stations =
        [
            .. (current?.Chargers ?? []).Select(charger => (charger.MapId, charger.StationId))
                .Concat(proposed.Select(charger => (charger.MapId, charger.StationId)))
                .Concat(cycles.Select(cycle => (cycle.MapId, cycle.StationId)))
                .Distinct()
                .OrderBy(station => station.MapId)
                .ThenBy(station => station.StationId)
        ];
        List<StationExclusivity> reservations = [];
        foreach ((int mapId, int stationId) in stations)
        {
            StationExclusivity? held = await _exclusivity.ReadAsync(mapId, stationId, cancellationToken).ConfigureAwait(false);
            if (held is not null && held.StationKind == StationExclusivityKinds.Charger)
            {
                reservations.Add(held);
            }
        }
        return new ChargingWorkInProgress(cycles, reservations);
    }

    private static IReadOnlyList<ChargerRosterChange> Compare(
        ChargerRosterVersion? current,
        IReadOnlyList<ChargerRosterEntry> proposed)
    {
        Dictionary<(int, int), ChargerRosterEntry> before = (current?.Chargers ?? [])
            .ToDictionary(charger => (charger.MapId, charger.StationId));
        Dictionary<(int, int), ChargerRosterEntry> after = proposed.ToDictionary(charger => (charger.MapId, charger.StationId));
        return
        [
            .. before.Keys.Union(after.Keys)
                .OrderBy(key => key.Item1)
                .ThenBy(key => key.Item2)
                .Select(key => new ChargerRosterChange(
                    key.Item1, key.Item2, before.GetValueOrDefault(key), after.GetValueOrDefault(key)))
                .Where(change => !SameEntry(change.Before, change.After))
        ];
    }

    private static bool SameEntry(ChargerRosterEntry? before, ChargerRosterEntry? after) =>
        before is not null && after is not null
        && before.MapId == after.MapId
        && before.StationId == after.StationId
        && string.Equals(before.StationName, after.StationName, StringComparison.Ordinal)
        && before.EntryStationId == after.EntryStationId
        && before.ExitStationId == after.ExitStationId
        && before.VehicleScope.Order(StringComparer.Ordinal)
            .SequenceEqual(after.VehicleScope.Order(StringComparer.Ordinal), StringComparer.Ordinal);

    private static string UnchangedDetail(ChargerRosterVersion current, ChargerRosterApproval approval, int entryCount) =>
        JsonSerializer.Serialize(new
        {
            contentSha256 = current.ContentSha256,
            entryCount,
            requestedBy = approval.ApprovedBy,
            approvalBasis = approval.ApprovalBasis,
            changeNote = approval.ChangeNote,
            detail = "The imported roster equals the current version; no new version was written."
        });

    /// <summary>
    /// 受控格式：UTF-8 JSON，一个对象，字段逐字是 <c>approvedBy</c>、<c>approvalBasis</c>、<c>changeNote</c>、<c>chargers</c>，
    /// 每个桩逐字是 <c>mapId</c>、<c>stationId</c>、<c>stationName</c>、<c>entryStationId</c>、<c>exitStationId</c>、
    /// <c>vehicleScope</c>——不多不少。<c>chargers</c> 为空数组即空名册。进点、出点与 <c>changeNote</c> 可以是 <c>null</c>；
    /// <c>vehicleScope</c> 为空数组即对投运名册里的全部车开放。
    /// </summary>
    private static (ChargerRosterApproval? Approval, List<ChargerRosterEntry> Chargers) ReadFile(
        string text,
        List<ChargerRosterImportError> errors)
    {
        const string shape =
            "The roster file is one JSON object with exactly approvedBy, approvalBasis, changeNote and chargers; each charger has "
            + "exactly mapId, stationId, stationName, entryStationId, exitStationId and vehicleScope. Ids are positive whole numbers "
            + "(entry and exit may be null), names and the approval are non-blank strings without outer whitespace, vehicleScope is "
            + "an array of distinct VehicleKeys (empty for every vehicle in the fleet), and chargers may be empty.";
        List<ChargerRosterEntry> chargers = [];
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text.TrimStart('﻿'));
        }
        catch (JsonException malformed)
        {
            errors.Add(new ChargerRosterImportError(ChargerRosterImportReasonCodes.FileMalformed, null, $"{shape} ({malformed.Message})"));
            return (null, chargers);
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (!HasExactly(root, FileFields)
                || !TryReadText(root.GetProperty("approvedBy"), out string? approvedBy)
                || !TryReadText(root.GetProperty("approvalBasis"), out string? basis)
                || !TryReadOptionalText(root.GetProperty("changeNote"), out string? changeNote)
                || root.GetProperty("chargers").ValueKind != JsonValueKind.Array)
            {
                errors.Add(new ChargerRosterImportError(ChargerRosterImportReasonCodes.FileMalformed, null, shape));
                return (null, chargers);
            }

            int index = 0;
            foreach (JsonElement item in root.GetProperty("chargers").EnumerateArray())
            {
                if (!HasExactly(item, ChargerFields)
                    || !TryReadId(item.GetProperty("mapId"), out int mapId)
                    || !TryReadId(item.GetProperty("stationId"), out int stationId)
                    || !TryReadText(item.GetProperty("stationName"), out string? stationName)
                    || !TryReadOptionalId(item.GetProperty("entryStationId"), out int? entry)
                    || !TryReadOptionalId(item.GetProperty("exitStationId"), out int? exit)
                    || !TryReadScope(item.GetProperty("vehicleScope"), out string[] scope))
                {
                    errors.Add(new ChargerRosterImportError(ChargerRosterImportReasonCodes.FileMalformed, null,
                        Invariant($"chargers[{index}]: {shape}")));
                }
                else
                {
                    chargers.Add(new ChargerRosterEntry(mapId, stationId, stationName!, entry, exit, scope));
                }
                index++;
            }
            return (new ChargerRosterApproval(approvedBy!, basis!, changeNote), chargers);
        }
    }

    private static bool HasExactly(JsonElement element, string[] fields) =>
        element.ValueKind == JsonValueKind.Object
        && element.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal)
            .SequenceEqual(fields.Order(StringComparer.Ordinal), StringComparer.Ordinal);

    private static bool TryReadId(JsonElement element, out int value)
    {
        value = 0;
        return element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out value) && value > 0;
    }

    private static bool TryReadOptionalId(JsonElement element, out int? value)
    {
        value = null;
        if (element.ValueKind == JsonValueKind.Null)
        {
            return true;
        }
        bool read = TryReadId(element, out int id);
        value = id;
        return read;
    }

    private static bool TryReadText(JsonElement element, out string? value)
    {
        value = element.ValueKind == JsonValueKind.String ? element.GetString() : null;
        return value is not null && value.Length > 0 && string.Equals(value, value.Trim(), StringComparison.Ordinal);
    }

    private static bool TryReadOptionalText(JsonElement element, out string? value)
    {
        value = null;
        return element.ValueKind == JsonValueKind.Null || TryReadText(element, out value);
    }

    private static bool TryReadScope(JsonElement element, out string[] scope)
    {
        scope = [];
        if (element.ValueKind != JsonValueKind.Array)
        {
            return false;
        }
        List<string> keys = [];
        foreach (JsonElement key in element.EnumerateArray())
        {
            if (!TryReadText(key, out string? text))
            {
                return false;
            }
            keys.Add(text!);
        }
        scope = [.. keys];
        return keys.Distinct(StringComparer.Ordinal).Count() == keys.Count;
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
