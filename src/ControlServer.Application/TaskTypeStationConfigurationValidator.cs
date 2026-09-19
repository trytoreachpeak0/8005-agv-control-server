using System.Globalization;
using ControlServer.Domain;

namespace ControlServer.Application;

/// <summary>
/// 任务类型规则与按图绑定的校验，纯函数（control-server#159）。两半用途不同：
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ValidateStatic"/> 只看配置本身，列出全部违规项；有任何一项，服务拒绝启动（启动装载）或拒绝激活（批次6-05）。
/// 「缺绑定」与「配错」分开：一个任务类型既不在需求集、也没有绑定，就是未启用，不是违规。
/// </para>
/// <para>
/// <see cref="EvaluateCatalog"/> 拿绑定去对一份站点目录，<b>从不拒绝启动</b>，只给受影响的那个任务类型一条原因码
/// （规格 21.2 第 1 条）。现场地图与生产共用，别处改一个站若让 v2 服务起不来，就连带所有任务类型，违背 REQ-0342。
/// 批次6-04 每轮在准入里调用它。
/// </para>
/// </remarks>
public static class TaskTypeStationConfigurationValidator
{
    /// <param name="configuration">预置配置的内容。</param>
    /// <param name="runtimeMapId"><c>JourneyRuntime:mapId</c>；预置文件的图必须与它同值。</param>
    /// <returns>全部违规项；为空表示通过。顺序按规则、再按绑定，同一类里按任务类型与站点。</returns>
    public static IReadOnlyList<TaskTypeStationViolation> ValidateStatic(
        TaskTypeStationConfiguration configuration,
        int runtimeMapId)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(configuration.Rules);
        ArgumentNullException.ThrowIfNull(configuration.Map);

        List<TaskTypeStationViolation> violations = [];
        TaskTypeStationMapConfiguration map = configuration.Map;
        IReadOnlyList<string> required = map.RequiredTaskTypes ?? [];
        IReadOnlyList<TaskTypeStationBinding> bindings = map.Bindings ?? [];

        ValidateRules(configuration.Rules, violations);
        ValidateRuleCoverage(configuration.Rules, required, bindings, violations);
        ValidateMap(map.MapId, runtimeMapId, violations);
        ValidateBindings(bindings, violations);
        ValidateRequirementCoverage(required, bindings, violations);
        return violations;
    }

    /// <summary>
    /// 目录相关的一半：每个绑定的站点要在<b>新鲜</b>目录里按 id 与名字都存在、且目录与绑定同图。
    /// </summary>
    /// <param name="catalog">该图最近一份完整目录；读不到（结果未知）时为 <c>null</c>。</param>
    /// <param name="catalogFresh">目录是否新鲜（REQ-0302 的判定由调用方给出）。</param>
    /// <returns>每个不满足的任务类型一条，其它任务类型不受影响。</returns>
    public static IReadOnlyList<TaskTypeStationViolation> EvaluateCatalog(
        int mapId,
        IReadOnlyList<TaskTypeStationBinding> bindings,
        RiotMapStationCatalogSnapshot? catalog,
        bool catalogFresh)
    {
        ArgumentNullException.ThrowIfNull(bindings);

        List<TaskTypeStationViolation> violations = [];
        foreach (TaskTypeStationBinding binding in bindings.OrderBy(binding => binding.TaskType, StringComparer.Ordinal))
        {
            if (catalog is null || !catalogFresh)
            {
                violations.Add(new(
                    TaskTypeStationReasonCodes.BindingCatalogNotFresh,
                    binding.TaskType,
                    binding.StationRiotId,
                    Invariant($"Map {mapId} has no fresh station catalog, so station {binding.StationName}/{binding.StationRiotId} bound to {binding.TaskType} cannot be confirmed.")));
                continue;
            }
            if (catalog.MapId != mapId)
            {
                violations.Add(new(
                    TaskTypeStationReasonCodes.BindingStationNotInCatalog,
                    binding.TaskType,
                    binding.StationRiotId,
                    Invariant($"The catalog is for Map {catalog.MapId}, not Map {mapId}; station {binding.StationName}/{binding.StationRiotId} bound to {binding.TaskType} is not confirmed.")));
                continue;
            }
            RiotMapStation? sameId = catalog.Stations.FirstOrDefault(station => station.StationId == binding.StationRiotId);
            if (sameId is null)
            {
                violations.Add(new(
                    TaskTypeStationReasonCodes.BindingStationNotInCatalog,
                    binding.TaskType,
                    binding.StationRiotId,
                    Invariant($"Map {mapId} has no station {binding.StationRiotId} (bound to {binding.TaskType} as {binding.StationName}).")));
            }
            else if (!string.Equals(sameId.StationName, binding.StationName, StringComparison.Ordinal))
            {
                violations.Add(new(
                    TaskTypeStationReasonCodes.BindingStationNotInCatalog,
                    binding.TaskType,
                    binding.StationRiotId,
                    Invariant($"Map {mapId} station {binding.StationRiotId} is now named {sameId.StationName}, not {binding.StationName} as bound to {binding.TaskType}.")));
            }
        }
        return violations;
    }

    private static void ValidateRules(
        IReadOnlyList<TaskTypeStationRule> rules,
        List<TaskTypeStationViolation> violations)
    {
        foreach (TaskTypeStationRule rule in rules.OrderBy(rule => rule.TaskType, StringComparer.Ordinal))
        {
            if (!TransportTaskTypes.All.Contains(rule.TaskType, StringComparer.Ordinal))
            {
                violations.Add(new(
                    TaskTypeStationReasonCodes.RuleUnknownTaskType,
                    rule.TaskType,
                    null,
                    Invariant($"Rule task type '{rule.TaskType}' is not one of the six MES literals ({string.Join(", ", TransportTaskTypes.All)}).")));
            }
            if (rule.FixedEnd is not (TaskTypeFixedEnd.Origin or TaskTypeFixedEnd.Destination))
            {
                violations.Add(new(
                    TaskTypeStationReasonCodes.RuleFixedEndInvalid,
                    rule.TaskType,
                    null,
                    Invariant($"Rule for {rule.TaskType} has fixed end '{rule.FixedEnd}'; it must be {TaskTypeFixedEnd.Origin} or {TaskTypeFixedEnd.Destination}.")));
            }
        }
        foreach (IGrouping<string, TaskTypeStationRule> repeated in rules
                     .GroupBy(rule => rule.TaskType, StringComparer.Ordinal)
                     .Where(group => group.Count() > 1)
                     .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            violations.Add(new(
                TaskTypeStationReasonCodes.RuleDuplicate,
                repeated.Key,
                null,
                Invariant($"The rule table names {repeated.Key} {repeated.Count()} times; each task type has at most one rule.")));
        }
    }

    private static void ValidateRuleCoverage(
        IReadOnlyList<TaskTypeStationRule> rules,
        IReadOnlyList<string> required,
        IReadOnlyList<TaskTypeStationBinding> bindings,
        List<TaskTypeStationViolation> violations)
    {
        HashSet<string> ruled = new(rules.Select(rule => rule.TaskType), StringComparer.Ordinal);
        foreach (string taskType in required
                     .Concat(bindings.Select(binding => binding.TaskType))
                     .Distinct(StringComparer.Ordinal)
                     .Where(taskType => !ruled.Contains(taskType))
                     .Order(StringComparer.Ordinal))
        {
            violations.Add(new(
                TaskTypeStationReasonCodes.RuleMissing,
                taskType,
                null,
                Invariant($"{taskType} is required or bound but has no rule; a task type is never let through without its rule.")));
        }
    }

    private static void ValidateMap(int mapId, int runtimeMapId, List<TaskTypeStationViolation> violations)
    {
        if (mapId <= 0)
        {
            violations.Add(new(
                TaskTypeStationReasonCodes.BindingIdentityInvalid,
                null,
                null,
                Invariant($"The preset's mapId is {mapId}; it must be positive.")));
        }
        if (mapId != runtimeMapId)
        {
            violations.Add(new(
                TaskTypeStationReasonCodes.BindingMapMismatch,
                null,
                null,
                Invariant($"The preset binds Map {mapId} but JourneyRuntime:mapId is {runtimeMapId}; the two must be the same map.")));
        }
    }

    private static void ValidateBindings(
        IReadOnlyList<TaskTypeStationBinding> bindings,
        List<TaskTypeStationViolation> violations)
    {
        foreach (TaskTypeStationBinding binding in bindings
                     .OrderBy(binding => binding.TaskType, StringComparer.Ordinal)
                     .ThenBy(binding => binding.StationRiotId))
        {
            bool nameBlank = string.IsNullOrWhiteSpace(binding.StationName);
            if (binding.StationRiotId <= 0 || nameBlank)
            {
                violations.Add(new(
                    TaskTypeStationReasonCodes.BindingIdentityInvalid,
                    binding.TaskType,
                    binding.StationRiotId,
                    Invariant($"{binding.TaskType} is bound to station '{binding.StationName}'/{binding.StationRiotId}; the id must be positive and the name non-blank.")));
            }
            if (!nameBlank && AreaNamedStationName.IsAreaNamed(binding.StationName))
            {
                violations.Add(new(
                    TaskTypeStationReasonCodes.BindingAreaNamedStation,
                    binding.TaskType,
                    binding.StationRiotId,
                    Invariant($"{binding.TaskType} is bound to {binding.StationName}/{binding.StationRiotId}, an AREA-named machine station; a fixed station is never a machine station.")));
            }
            if (string.IsNullOrWhiteSpace(binding.SiteVerificationRef))
            {
                violations.Add(new(
                    TaskTypeStationReasonCodes.BindingSiteVerificationMissing,
                    binding.TaskType,
                    binding.StationRiotId,
                    Invariant($"{binding.TaskType} is bound to {binding.StationName}/{binding.StationRiotId} without a site verification reference.")));
            }
        }
        foreach (IGrouping<string, TaskTypeStationBinding> repeated in bindings
                     .GroupBy(binding => binding.TaskType, StringComparer.Ordinal)
                     .Where(group => group.Count() > 1)
                     .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            violations.Add(new(
                TaskTypeStationReasonCodes.BindingDuplicate,
                repeated.Key,
                null,
                Invariant($"{repeated.Key} is bound {repeated.Count()} times on one map ({string.Join(", ", repeated.Select(binding => $"{binding.StationName}/{binding.StationRiotId}"))}); a task type has at most one station per map.")));
        }
        foreach (IGrouping<int, TaskTypeStationBinding> shared in bindings
                     .Where(binding => binding.StationRiotId > 0)
                     .GroupBy(binding => binding.StationRiotId)
                     .Where(group => group.Select(binding => binding.TaskType).Distinct(StringComparer.Ordinal).Count() > 1)
                     .OrderBy(group => group.Key))
        {
            string[] taskTypes = [.. shared.Select(binding => binding.TaskType).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
            violations.Add(new(
                TaskTypeStationReasonCodes.StationReused,
                null,
                shared.Key,
                Invariant($"Station {shared.First().StationName}/{shared.Key} is bound to {taskTypes.Length} task types ({string.Join(", ", taskTypes)}); one station serves at most one task type.")));
        }
    }

    private static void ValidateRequirementCoverage(
        IReadOnlyList<string> required,
        IReadOnlyList<TaskTypeStationBinding> bindings,
        List<TaskTypeStationViolation> violations)
    {
        HashSet<string> bound = new(bindings.Select(binding => binding.TaskType), StringComparer.Ordinal);
        foreach (string taskType in required
                     .Distinct(StringComparer.Ordinal)
                     .Where(taskType => !bound.Contains(taskType))
                     .Order(StringComparer.Ordinal))
        {
            violations.Add(new(
                TaskTypeStationReasonCodes.BindingRequiredMissing,
                taskType,
                null,
                Invariant($"{taskType} is in this map's requirement set but has no binding.")));
        }
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
