using ControlServer.Application;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ControlServer.Host.Runtime.TaskTypeStations;

/// <summary>启动装载的结果；没有装载（旅程运行时关闭、或配置里没有这一节）时整体为 <c>null</c>。</summary>
public sealed record TaskTypeStationStartupResult(
    TaskTypeStationVersionWrite<TaskTypeStationRuleVersion> Rules,
    TaskTypeStationVersionWrite<TaskTypeStationBindingSetVersion> Bindings);

/// <summary>
/// 启动时装载预置配置（control-server#159）：读 <see cref="TaskTypeStationPreset"/> → 静态校验 → 在<b>同一个事务</b>里写规则版本；
/// 该图还没有生效版本、也没有结果未知的激活时，再写绑定集第一版并把生效指针指向它（control-server#161）。
/// </summary>
/// <remarks>
/// <para>
/// 规则与绑定一起校验、一起落库，不存在「规则放行了、绑定还没写」的中间态（REQ-0343）。任何一项静态违规都拒绝启动，并在日志里逐条列出，
/// 一次改完。形状照 <c>AreaAssignmentDispatchZoneStartupCheck</c>：旅程运行时关闭时跳过，那个服务不派车，它的绑定没有意义。
/// </para>
/// <para>
/// <b>目录相关的一半不在这里判</b>（规格 21.2 第 1 条）：现场地图与生产共用，别处改一个站不能让 v2 服务起不来。站点目录没有落库，
/// 启动时读它就要现场连 RIoT，所以启动装载不核验站点，绑定集版本的 <c>CatalogRevision</c> 写空；批次6-04 每轮拿新鲜目录调
/// <see cref="TaskTypeStationConfigurationValidator.EvaluateCatalog"/>。
/// </para>
/// <para>
/// <b>预置文件只是一张图的第一版</b>（规格 21.2 第 4 条，control-server#161）。该图一旦有了生效版本（不论是预置装的还是 FieldOps
/// 激活的），或正有一次激活结果未知，换版本只走 FieldOps 激活，重启不再写绑定集、不动指针与暂停；预置内容与生效版本不同时如实记一条日志，
/// 说它没有生效。指针行在、却是 <c>ACTIVE</c> 且不指向任何版本的，算没有生效版本，预置照装（审查 S4）；人工收尾留下的墓碑
/// <c>CLOSED_MANUALLY</c> 不是「第一次」，预置不装，该图保持无生效版本直到下一次 FieldOps 激活或回滚（第二轮复审 N1）。规则表不在此列：它不分图，仍按预置文件装（内容未变不出新版本）。
/// </para>
/// </remarks>
public static class TaskTypeStationStartup
{
    private static readonly Action<ILogger, string, int, Exception?> NoPreset = LoggerMessage.Define<string, int>(
        LogLevel.Warning,
        new EventId(9300, "TaskTypeStationPresetAbsent"),
        "No {Section} preset was found and Map {MapId} has no active binding set version, so no task type is enabled.");

    private static readonly Action<ILogger, string, int, long, Exception?> NoPresetActiveKept =
        LoggerMessage.Define<string, int, long>(
            LogLevel.Warning,
            new EventId(9303, "TaskTypeStationPresetAbsentActiveKept"),
            "No {Section} preset was found; nothing is loaded, and Map {MapId} binding set version {ActiveVersion} stays active as it was.");

    private static readonly Action<ILogger, string, string, string, Exception?> Refused =
        LoggerMessage.Define<string, string, string>(
            LogLevel.Error,
            new EventId(9301, "TaskTypeStationPresetRefused"),
            "Task type station preset {Path} refused: {ReasonCode} {Detail}");

    private static readonly Action<ILogger, string, long, string, int, long, string, Exception?> Loaded =
        LoggerMessage.Define<string, long, string, int, long, string>(
            LogLevel.Information,
            new EventId(9302, "TaskTypeStationPresetLoaded"),
            "Task type station preset {Path} loaded: rule version {RuleVersion} ({RuleOutcome}), Map {MapId} binding set version {BindingSetVersion} ({BindingOutcome}) active.");

    private static readonly Action<ILogger, string, string, Exception?> GateScalarsIgnored =
        LoggerMessage.Define<string, string>(
            LogLevel.Warning,
            new EventId(9304, "TaskTypeStationGateScalarsIgnored"),
            "JourneyRuntime:gateStationId ({GateStationId}) / gateStationRiotId ({GateStationRiotId}) are still configured but are ignored since control-server#160: the WIRE_TO_GATE end station is the Map's binding in the task type station preset. Remove the two keys, and check that binding if the gate here is not the preset's.");

    private static readonly Action<ILogger, string, int, string, string, Exception?> NotApplied =
        LoggerMessage.Define<string, int, string, string>(
            LogLevel.Warning,
            new EventId(9305, "TaskTypeStationPresetNotApplied"),
            "Task type station preset {Path} was not applied to Map {MapId} (pointer state {State}): {Standing}; a different version only comes from a FieldOps activation or rollback.");

    public static async Task<TaskTypeStationStartupResult?> EnsureAsync(
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);

        await using AsyncServiceScope scope = services.CreateAsyncScope();
        IServiceProvider provider = scope.ServiceProvider;
        JourneyRuntimeOptions runtime = provider.GetRequiredService<IOptions<JourneyRuntimeOptions>>().Value;
        if (!runtime.Enabled)
        {
            return null;
        }

        ILogger logger = provider.GetService<ILoggerFactory>()?.CreateLogger(typeof(TaskTypeStationStartup).FullName!)
            ?? NullLogger.Instance;
        // An installation upgraded straight from before #159 may still carry the two scalars, and one whose gate is
        // not the preset's station would otherwise be moved to the preset's station without a word.
        IConfiguration configuration = provider.GetRequiredService<IConfiguration>();
        string? leftoverName = configuration[JourneyRuntimeOptions.SectionName + ":gateStationId"];
        string? leftoverRiotId = configuration[JourneyRuntimeOptions.SectionName + ":gateStationRiotId"];
        if (!string.IsNullOrWhiteSpace(leftoverName) || !string.IsNullOrWhiteSpace(leftoverRiotId))
        {
            GateScalarsIgnored(logger, leftoverName ?? "(absent)", leftoverRiotId ?? "(absent)", null);
        }
        TaskTypeStationPresetFile? preset = TaskTypeStationPreset.Load(
            AppContext.BaseDirectory,
            configuration[TaskTypeStationPreset.SettingsFileKey]);
        if (preset is null)
        {
            // The pointer is left alone (specification 21.2 item 4: a restart does not take away a version that is
            // active), so what the log says is whichever of the two actually holds.
            TaskTypeStationActivePointer? kept = await provider.GetRequiredService<ITaskTypeStationBindingStore>()
                .ReadActivePointerAsync(runtime.MapId, cancellationToken);
            if (kept?.ActiveVersion is long activeVersion)
            {
                NoPresetActiveKept(logger, TaskTypeStationPreset.SectionName, runtime.MapId, activeVersion, null);
            }
            else
            {
                NoPreset(logger, TaskTypeStationPreset.SectionName, runtime.MapId, null);
            }
            return null;
        }

        IReadOnlyList<TaskTypeStationViolation> violations = TaskTypeStationConfigurationValidator.ValidateStatic(
            preset.Configuration,
            runtime.MapId);
        if (violations.Count > 0)
        {
            foreach (TaskTypeStationViolation violation in violations)
            {
                Refused(logger, preset.Path, violation.ReasonCode, violation.Detail, null);
            }
            throw new TaskTypeStationConfigurationException(violations);
        }

        ControlServerDbContext context = provider.GetRequiredService<ControlServerDbContext>();
        ITaskTypeStationRuleStore rules = provider.GetRequiredService<ITaskTypeStationRuleStore>();
        ITaskTypeStationBindingStore bindings = provider.GetRequiredService<ITaskTypeStationBindingStore>();
        DateTimeOffset now = (provider.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow();
        string source = TaskTypeStationGovernance.PresetSourcePrefix + preset.Path;
        TaskTypeStationMapConfiguration map = preset.Configuration.Map;

        await using IDbContextTransaction transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        TaskTypeStationVersionWrite<TaskTypeStationRuleVersion> ruleWrite = await rules.WriteVersionAsync(
            preset.Configuration.Rules, source, now, cancellationToken);
        TaskTypeStationActivePointer? existing = await bindings.ReadActivePointerAsync(map.MapId, cancellationToken);
        // The preset is only a map's first version (specification 21.2 item 4). It loads where there is no pointer row, or a
        // row that is ACTIVE and names no version (control-server#161 review S4). Everything else keeps it out: a version is
        // active, an activation's result is unknown, or a manual close left its tombstone -- a map with no active version
        // until the next FieldOps activation or rollback, which an unplanned restart must not undo (review round 2, N1).
        if (existing is not null
            && (existing.ActiveVersion is not null
                || string.Equals(existing.State, TaskTypeStationActivationState.ActivationUnknown, StringComparison.Ordinal)
                || string.Equals(existing.State, TaskTypeStationActivationState.ClosedManually, StringComparison.Ordinal)))
        {
            TaskTypeStationBindingSetVersion? kept = existing.ActiveVersion is long activeVersion
                ? await bindings.ReadVersionAsync(map.MapId, activeVersion, cancellationToken)
                : await bindings.ReadLatestAsync(map.MapId, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            if (kept is null)
            {
                throw new InvalidOperationException(FormattableString.Invariant(
                    $"Map {map.MapId} has an active pointer but no binding set version to read back."));
            }
            if (!SameContent(kept, ruleWrite.Version.Version, map)
                || !string.Equals(existing.State, TaskTypeStationActivationState.Active, StringComparison.Ordinal))
            {
                NotApplied(logger, preset.Path, map.MapId, existing.State, Standing(existing), null);
            }
            else
            {
                Loaded(logger, preset.Path, ruleWrite.Version.Version, ruleWrite.Created ? "new" : "unchanged", map.MapId,
                    kept.Version, "unchanged", null);
            }
            return new TaskTypeStationStartupResult(ruleWrite, new(kept, Created: false));
        }

        TaskTypeStationVersionWrite<TaskTypeStationBindingSetVersion> bindingWrite = await bindings.WriteVersionAsync(
            map.MapId,
            ruleWrite.Version.Version,
            map.RequiredTaskTypes,
            map.Bindings,
            catalogRevision: null,
            source,
            now,
            cancellationToken);
        // First version of this map: nothing was active, so the preset is what becomes active.
        await bindings.SetActiveAsync(map.MapId, bindingWrite.Version.Version, now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        Loaded(
            logger,
            preset.Path,
            ruleWrite.Version.Version,
            ruleWrite.Created ? "new" : "unchanged",
            map.MapId,
            bindingWrite.Version.Version,
            bindingWrite.Created ? "new" : "unchanged",
            null);
        return new TaskTypeStationStartupResult(ruleWrite, bindingWrite);
    }

    /// <summary>9305 日志里「该图现在是什么样」那一句，按指针状态说，不打印空版本号。</summary>
    private static string Standing(TaskTypeStationActivePointer pointer) =>
        pointer.State switch
        {
            TaskTypeStationActivationState.ClosedManually =>
                "it has no active version since an activation was closed (by hand, or by a reconciliation of an attempt with no hold to restore), and stays without one",
            TaskTypeStationActivationState.ActivationUnknown => pointer.ActiveVersion is long active
                ? FormattableString.Invariant($"binding set version {active} stays as it is while an activation of version {pointer.PendingVersion} has an unknown result, until reconcile-task-type-stations concludes it")
                : FormattableString.Invariant($"it has no active version while an activation of version {pointer.PendingVersion} has an unknown result, until reconcile-task-type-stations concludes it"),
            _ => FormattableString.Invariant($"binding set version {pointer.ActiveVersion} stays active")
        };

    private static bool SameContent(TaskTypeStationBindingSetVersion kept, long ruleVersion, TaskTypeStationMapConfiguration map) =>
        kept.RuleVersion == ruleVersion
        && kept.RequiredTaskTypes.SequenceEqual(
            map.RequiredTaskTypes.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal), StringComparer.Ordinal)
        && kept.Bindings.SequenceEqual(map.Bindings.OrderBy(binding => binding.TaskType, StringComparer.Ordinal));
}
