using System.Globalization;
using System.Text.Json;
using ControlServer.Application;
using ControlServer.Infrastructure.Persistence;

namespace ControlServer.FieldOps;

/// <summary>
/// 公共站点绑定集的 FieldOps 动词（control-server#161，批次6-05）：激活、回滚、对账、解除暂停，外加一个只读查看。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么是 FieldOps，不是看板</b>（规格 5.7）：完整产品没有任何人员认证，界面上只允许 fail-safe 方向的动作。换生效版本与撤销暂停
/// 都是 fail-unsafe 的，所以只走这条要人在机器前刻意敲一次、每次都写不可改写审计的路。<c>--role</c> 原样记下，不是两级管理员分工
/// （REQ-0336 延后），审计的操作者始终是部署标识。
/// </para>
/// <para>
/// <b>本工具够不到 RIoT。</b>站点目录由运维带进来（<c>--catalog</c>），必须正好是服务端最近一次完整确认、且确认仍新鲜的那一份，
/// 否则什么都不激活、不解除——见 <see cref="TaskTypeStationCatalogEvidence"/>。
/// </para>
/// </remarks>
internal static partial class Program
{
    private const string ActivateTaskTypeStationsCommand = "activate-task-type-stations";

    private const string RollbackTaskTypeStationsCommand = "rollback-task-type-stations";

    private const string ReconcileTaskTypeStationsCommand = "reconcile-task-type-stations";

    private const string ReleaseTaskTypeStationHoldCommand = "release-task-type-station-hold";

    private const string ReadTaskTypeStationsCommand = "task-type-stations";

    private static async Task<int> ActivateTaskTypeStationsAsync(
        ControlServerDbContext context,
        GovernanceStore governance,
        Dictionary<string, string> options,
        DateTimeOffset now)
    {
        if (!options.TryGetValue("input", out string? inputPath) || !options.TryGetValue("catalog", out string? catalogPath))
        {
            return Usage($"{ActivateTaskTypeStationsCommand} needs --input <candidate.json> --catalog <stations.json> --reason <text>");
        }
        if (!TryReadRequest(options, ActivateTaskTypeStationsCommand, out TaskTypeStationChangeRequest? request, out int usage))
        {
            return usage;
        }
        if (!TryReadCandidate(inputPath, out TaskTypeStationCandidate? candidate, out string? problem)
            || !TryReadCatalog(catalogPath, now, out RiotMapStationCatalogSnapshot? catalog, out problem))
        {
            return Usage(problem!);
        }

        bool dryRun = options.ContainsKey("dry-run");
        TaskTypeStationActivationResult result = await ActivationService(context, governance).ActivateAsync(
            candidate!, catalog, request!, dryRun, now, CancellationToken.None);
        return EmitActivation(ActivateTaskTypeStationsCommand, result, dryRun);
    }

    /// <summary>回滚：把历史版本的内容当作一次新的激活再走一遍全部校验，生效的是一个新版本号。</summary>
    private static async Task<int> RollbackTaskTypeStationsAsync(
        ControlServerDbContext context,
        GovernanceStore governance,
        Dictionary<string, string> options,
        DateTimeOffset now)
    {
        if (!TryReadMap(options, out int mapId)
            || !options.TryGetValue("version", out string? versionText)
            || !long.TryParse(versionText, NumberStyles.None, CultureInfo.InvariantCulture, out long version))
        {
            return Usage($"{RollbackTaskTypeStationsCommand} needs --map <id> --version <n> --catalog <stations.json> --reason <text>");
        }
        if (!TryReadRequest(options, RollbackTaskTypeStationsCommand, out TaskTypeStationChangeRequest? request, out int usage))
        {
            return usage;
        }
        if (!options.TryGetValue("catalog", out string? catalogPath))
        {
            return Usage($"{RollbackTaskTypeStationsCommand} needs --catalog <stations.json>");
        }
        if (!TryReadCatalog(catalogPath, now, out RiotMapStationCatalogSnapshot? catalog, out string? problem))
        {
            return Usage(problem!);
        }

        bool dryRun = options.ContainsKey("dry-run");
        TaskTypeStationActivationResult result = await ActivationService(context, governance).RollbackAsync(
            mapId, version, catalog, request!, dryRun, now, CancellationToken.None);
        return EmitActivation(RollbackTaskTypeStationsCommand, result, dryRun);
    }

    /// <summary>对账：读实际生效的版本与完整内容，写下结论。矛盾时退出码 1，暂停保留。</summary>
    private static async Task<int> ReconcileTaskTypeStationsAsync(
        ControlServerDbContext context,
        GovernanceStore governance,
        Dictionary<string, string> options,
        DateTimeOffset now)
    {
        if (!TryReadMap(options, out int mapId))
        {
            return Usage($"{ReconcileTaskTypeStationsCommand} needs --map <id> --reason <text>");
        }
        if (!TryReadRequest(options, ReconcileTaskTypeStationsCommand, out TaskTypeStationChangeRequest? request, out int usage))
        {
            return usage;
        }

        TaskTypeStationReconciliationResult result = await ActivationService(context, governance).ReconcileAsync(
            mapId, request!, now, CancellationToken.None);
        bool contradictory = result.Conclusion == TaskTypeStationReconciliationConclusion.Contradictory;
        return Emit(
            new
            {
                command = ReconcileTaskTypeStationsCommand,
                outcome = contradictory ? "RESULT_UNKNOWN" : "OK",
                conclusion = TaskTypeStationActivationService.ConclusionName(result.Conclusion),
                mapId = result.MapId,
                attemptId = result.AttemptId,
                previousVersion = result.PreviousVersion,
                targetVersion = result.TargetVersion,
                activeVersion = result.ActiveVersion,
                activeContentSha256 = result.ActiveContentSha256,
                releasedHoldIds = result.ReleasedHoldIds,
                auditRecordId = result.AuditRecordId,
                detail = result.Detail
            },
            contradictory ? 1 : 0);
    }

    /// <summary>解除一个 <c>Map + TASK_TYPE</c> 上人工或目录变化来源的暂停；带现场核对记录，对新鲜目录完整重验。</summary>
    private static async Task<int> ReleaseTaskTypeStationHoldAsync(
        ControlServerDbContext context,
        GovernanceStore governance,
        Dictionary<string, string> options,
        DateTimeOffset now)
    {
        if (!TryReadMap(options, out int mapId)
            || !options.TryGetValue("task-type", out string? taskType)
            || !options.TryGetValue("catalog", out string? catalogPath))
        {
            return Usage(
                $"{ReleaseTaskTypeStationHoldCommand} needs --map <id> --task-type <TASK_TYPE> --site-verification <ref>"
                + " --catalog <stations.json> --reason <text>");
        }
        if (!TryReadRequest(options, ReleaseTaskTypeStationHoldCommand, out TaskTypeStationChangeRequest? request, out int usage))
        {
            return usage;
        }
        if (!TryReadCatalog(catalogPath, now, out RiotMapStationCatalogSnapshot? catalog, out string? problem))
        {
            return Usage(problem!);
        }
        // A missing site verification is not a usage error: it is one of the reasons a release is refused, and a
        // refusal is audited like any other outcome.
        options.TryGetValue("site-verification", out string? siteVerification);

        TaskTypeStationHoldReleaseResult result = await ActivationService(context, governance).ReleaseHoldAsync(
            mapId, taskType, siteVerification, catalog, request!, now, CancellationToken.None);
        bool released = result.Outcome == TaskTypeStationHoldReleaseOutcome.Released;
        return Emit(
            new
            {
                command = ReleaseTaskTypeStationHoldCommand,
                outcome = released ? "OK" : "REJECTED",
                mapId = result.MapId,
                taskType = result.TaskType,
                violations = Violations(result.Violations),
                released = result.Released.Select(hold => new
                {
                    holdId = hold.HoldId,
                    source = hold.Source,
                    reasonCode = hold.ReasonCode,
                    raisedAt = hold.RaisedAt,
                    raisedBy = hold.RaisedBy,
                    releasedAt = hold.ReleasedAt
                }),
                auditRecordId = result.AuditRecordId
            },
            released ? 0 : 1);
    }

    /// <summary>
    /// 某图的生效版本、历史版本、当前未解除的暂停与未结的激活尝试。<b>只读，库以 SQLite 只读模式开</b>（<see cref="OpensReadOnly"/>）。
    /// </summary>
    private static async Task<int> ReadTaskTypeStationsAsync(
        ControlServerDbContext context,
        GovernanceStore governance,
        Dictionary<string, string> options)
    {
        if (!TryReadMap(options, out int mapId))
        {
            return Usage($"{ReadTaskTypeStationsCommand} needs --map <id>");
        }

        // Read paths never publish; the publisher is only there to build the stores.
        TaskTypeStationBindingStore bindings = new(context, new GovernedConfigurationPublisher(governance, governance));
        TaskTypeStationActivationStore activations = new(context, bindings, governance);
        CancellationToken none = CancellationToken.None;
        TaskTypeStationActivePointer? pointer = await bindings.ReadActivePointerAsync(mapId, none);
        TaskTypeStationBindingSetVersion? active = await bindings.ReadActiveAsync(mapId, none);
        TaskTypeStationBindingSetVersion? latest = await bindings.ReadLatestAsync(mapId, none);
        List<TaskTypeStationBindingSetVersion> versions = [];
        for (long version = 1; version <= (latest?.Version ?? 0); version++)
        {
            if (await bindings.ReadVersionAsync(mapId, version, none) is { } found)
            {
                versions.Add(found);
            }
        }
        IReadOnlyList<TaskTypeStationHold> holds = await new TaskTypeStationHoldStore(context).ListUnreleasedAsync(mapId, none);
        TaskTypeStationActivationAttempt? open = await activations.ReadOpenAttemptAsync(mapId, none);

        return Emit(
            new
            {
                command = ReadTaskTypeStationsCommand,
                outcome = "OK",
                mapId,
                activeVersion = pointer?.ActiveVersion,
                state = pointer?.State,
                pendingVersion = pointer?.PendingVersion,
                active = active is null ? null : Version(active),
                versions = versions.Select(Version),
                unreleasedHolds = holds.Select(hold => new
                {
                    holdId = hold.HoldId,
                    taskType = hold.TaskType,
                    source = hold.Source,
                    reasonCode = hold.ReasonCode,
                    raisedAt = hold.RaisedAt,
                    raisedBy = hold.RaisedBy
                }),
                openAttempt = open is null
                    ? null
                    : new
                    {
                        attemptId = open.AttemptId,
                        previousVersion = open.PreviousVersion,
                        targetVersion = open.TargetVersion,
                        heldTaskTypes = open.HeldTaskTypes,
                        holdIds = open.HoldIds
                    }
            },
            0);
    }

    private static TaskTypeStationActivationService ActivationService(ControlServerDbContext context, GovernanceStore governance)
    {
        GovernedConfigurationPublisher publisher = new(governance, governance);
        TaskTypeStationBindingStore bindings = new(context, publisher);
        return new TaskTypeStationActivationService(
            new TaskTypeStationRuleStore(context, publisher),
            bindings,
            new TaskTypeStationActivationStore(context, bindings, governance),
            new CatalogAvailabilityStore(context),
            governance);
    }

    private static int EmitActivation(string command, TaskTypeStationActivationResult result, bool dryRun) =>
        Emit(
            new
            {
                command,
                outcome = result.Outcome switch
                {
                    TaskTypeStationActivationOutcome.Previewed => "PREVIEW",
                    TaskTypeStationActivationOutcome.Activated => "OK",
                    TaskTypeStationActivationOutcome.Rejected => "REJECTED",
                    _ => "RESULT_UNKNOWN"
                },
                dryRun,
                requestCategory = result.RequestCategory,
                mapId = result.MapId,
                attemptId = result.AttemptId,
                previousVersion = result.PreviousVersion,
                targetVersion = result.TargetVersion,
                ruleVersion = result.RuleVersion,
                catalogRevision = result.CatalogRevision,
                violations = Violations(result.Violations),
                changes = result.Changes.Select(change => new
                {
                    taskType = change.TaskType,
                    before = Station(change.Before),
                    after = Station(change.After)
                }),
                impact = new
                {
                    inFlightDemandCount = result.Impact.InFlightDemandCount,
                    affected = result.Impact.Affected.Select(item => new
                    {
                        demandId = item.DemandId,
                        taskType = item.TaskType,
                        frozenBindingSetVersion = item.FrozenBindingSetVersion,
                        frozenStationRiotId = item.FrozenStationRiotId,
                        candidateStationRiotId = item.CandidateStationRiotId
                    })
                },
                auditRecordIds = result.AuditRecordIds,
                detail = result.Detail
            },
            result.Outcome is TaskTypeStationActivationOutcome.Previewed or TaskTypeStationActivationOutcome.Activated ? 0 : 1);

    private static IEnumerable<object> Violations(IReadOnlyList<TaskTypeStationViolation> violations) =>
        violations.Select(violation => new
        {
            reasonCode = violation.ReasonCode,
            taskType = violation.TaskType,
            stationRiotId = violation.StationRiotId,
            detail = violation.Detail
        });

    private static object? Station(TaskTypeStationBinding? binding) =>
        binding is null
            ? null
            : new
            {
                stationRiotId = binding.StationRiotId,
                stationName = binding.StationName,
                siteVerificationRef = binding.SiteVerificationRef
            };

    private static object Version(TaskTypeStationBindingSetVersion version) => new
    {
        version = version.Version,
        ruleVersion = version.RuleVersion,
        contentSha256 = version.ContentSha256,
        snapshotId = version.SnapshotId,
        catalogRevision = version.CatalogRevision,
        loadedAt = version.LoadedAt,
        source = version.Source,
        requiredTaskTypes = version.RequiredTaskTypes,
        bindings = version.Bindings.Select(Station)
    };

    private static bool TryReadMap(Dictionary<string, string> options, out int mapId)
    {
        mapId = 0;
        return options.TryGetValue("map", out string? text)
            && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out mapId)
            && mapId > 0;
    }

    /// <summary>变更理由是必填参数（REQ-0348）；缺了是用法错误，什么都不做。</summary>
    private static bool TryReadRequest(
        Dictionary<string, string> options,
        string command,
        out TaskTypeStationChangeRequest? request,
        out int usage)
    {
        request = null;
        usage = 0;
        if (!options.TryGetValue("reason", out string? reason) || string.IsNullOrWhiteSpace(reason))
        {
            usage = Usage($"{command} needs --reason <text>: every change records why it was made");
            return false;
        }
        options.TryGetValue("role", out string? role);
        request = new TaskTypeStationChangeRequest(reason, role);
        return true;
    }

    private static bool TryReadCandidate(string path, out TaskTypeStationCandidate? candidate, out string? problem)
    {
        candidate = null;
        problem = null;
        if (!File.Exists(path))
        {
            problem = $"candidate file not found: {path}";
            return false;
        }
        try
        {
            CandidateFile? file = JsonSerializer.Deserialize<CandidateFile>(File.ReadAllText(path), Input);
            if (file is null || file.MapId <= 0 || file.RuleVersion <= 0)
            {
                problem = $"candidate file {path} needs a positive mapId and ruleVersion";
                return false;
            }
            candidate = new TaskTypeStationCandidate(
                file.MapId,
                file.RuleVersion,
                file.RequiredTaskTypes ?? [],
                [
                    .. (file.Bindings ?? []).Select(binding => new TaskTypeStationBinding(
                        binding.TaskType ?? string.Empty,
                        binding.StationRiotId,
                        binding.StationName ?? string.Empty,
                        binding.SiteVerificationRef ?? string.Empty))
                ]);
            return true;
        }
        catch (JsonException malformed)
        {
            problem = $"candidate file {path} is malformed: {malformed.Message}";
            return false;
        }
    }

    private static bool TryReadCatalog(
        string path,
        DateTimeOffset now,
        out RiotMapStationCatalogSnapshot? catalog,
        out string? problem)
    {
        catalog = null;
        problem = null;
        if (!File.Exists(path))
        {
            problem = $"catalog file not found: {path}";
            return false;
        }
        try
        {
            CatalogFile? file = JsonSerializer.Deserialize<CatalogFile>(File.ReadAllText(path), Input);
            RiotMapStation[] stations =
            [
                .. (file?.Stations ?? []).Select(station => new RiotMapStation(station.StationId, station.StationName ?? string.Empty))
            ];
            // The same shape the server insists on when it reads RIoT: a non-empty list of unique, named stations.
            if (file is null || file.MapId <= 0 || stations.Length == 0
                || stations.Any(station => station.StationId <= 0 || string.IsNullOrWhiteSpace(station.StationName))
                || stations.Select(station => station.StationId).Distinct().Count() != stations.Length)
            {
                problem = $"catalog file {path} needs a positive mapId and a non-empty list of stations with unique positive ids and names";
                return false;
            }
            catalog = TaskTypeStationCatalogEvidence.Supplied(file.MapId, stations, now);
            return true;
        }
        catch (JsonException malformed)
        {
            problem = $"catalog file {path} is malformed: {malformed.Message}";
            return false;
        }
    }

    private sealed record CandidateFile(
        int MapId,
        long RuleVersion,
        IReadOnlyList<string>? RequiredTaskTypes,
        IReadOnlyList<CandidateBinding>? Bindings);

    private sealed record CandidateBinding(
        string? TaskType,
        int StationRiotId,
        string? StationName,
        string? SiteVerificationRef);

    private sealed record CatalogFile(int MapId, IReadOnlyList<CatalogStation>? Stations);

    private sealed record CatalogStation(int StationId, string? StationName);
}
