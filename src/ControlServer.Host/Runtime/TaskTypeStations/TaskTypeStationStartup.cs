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
/// 启动时装载预置配置（control-server#159）：读 <see cref="TaskTypeStationPreset"/> → 静态校验 → 在<b>同一个事务</b>里写规则版本与
/// 该图的绑定集版本（内容未变则不出新版本），并把生效指针指向它。
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
/// 启动装载即激活，是批次6-05 之前的形态；批次6-05 起预置文件只在一张图还没有生效版本时装为第一版（规格 21.2 第 4 条）。
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
        TaskTypeStationVersionWrite<TaskTypeStationBindingSetVersion> bindingWrite = await bindings.WriteVersionAsync(
            map.MapId,
            ruleWrite.Version.Version,
            map.RequiredTaskTypes,
            map.Bindings,
            catalogRevision: null,
            source,
            now,
            cancellationToken);
        TaskTypeStationActivePointer? pointer = await bindings.ReadActivePointerAsync(map.MapId, cancellationToken);
        if (pointer?.ActiveVersion != bindingWrite.Version.Version
            || !string.Equals(pointer.State, TaskTypeStationActivationState.Active, StringComparison.Ordinal))
        {
            await bindings.SetActiveAsync(map.MapId, bindingWrite.Version.Version, now, cancellationToken);
        }
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
}
