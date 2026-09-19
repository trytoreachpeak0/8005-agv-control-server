using System.Text.Json;
using ControlServer.Application;
using ControlServer.Host.Runtime.TaskTypeStations;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Host.Dashboard;

/// <summary>
/// 看板「任务类型绑定与暂停」的数据面（control-server#162）：每张图、规则表里的每个任务类型一行——是否在本图需求集合里、绑定的
/// Station、生效绑定集版本、状态，以及每条未解除暂停的来源、起始时间与理由。
/// </summary>
/// <remarks>
/// <para>
/// 状态按这个次序取第一个成立的：<c>HELD</c>（有未解除暂停）、<c>NO_ACTIVE_BINDING_SET</c>（该图没有生效版本，例如墓碑
/// <c>CLOSED_MANUALLY</c>——人工收尾之后，或无暂停可还原的结果未知尝试对账之后）、<c>STATION_NOT_IN_CATALOG</c>（当前目录修订下记过绑定站点已不在目录）、<c>BINDING_MISSING</c>（在需求集合里却没有绑定）、<c>NOT_REQUIRED</c>（不在需求集合里）、<c>NORMAL</c>。
/// </para>
/// <para>
/// 暂停来源的中文名在这里给出而不在看板里：看板源码守着一张 fail-safe 词表，「激活结果未知」这个来源名本身不是入口，
/// 但把它写进看板源码就要给那张词表开口子。批次6-05 在激活事务里直接写的 <c>ACTIVATION_RESULT_UNKNOWN</c> 来源同样照名显示。
/// </para>
/// </remarks>
internal sealed class TaskTypeBindingsQueryEndpoint : IDashboardQueryEndpoint
{
    public string Path => DashboardQueryEndpointCatalog.QueryPrefix + "task-type-bindings";

    public async Task<object> ReadAsync(ControlServerDbContext dbContext, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        long? ruleVersion = await dbContext.Set<TaskTypeStationRuleVersionRow>()
            .MaxAsync(row => (long?)row.Version, cancellationToken);
        TaskTypeStationRuleRow[] rules = ruleVersion is null
            ? []
            : await dbContext.Set<TaskTypeStationRuleRow>().AsNoTracking()
                .Where(row => row.Version == ruleVersion.Value)
                .ToArrayAsync(cancellationToken);
        TaskTypeStationActiveBindingSetRow[] pointers = await dbContext.Set<TaskTypeStationActiveBindingSetRow>()
            .AsNoTracking()
            .ToArrayAsync(cancellationToken);
        TaskTypeStationHoldRow[] holds = await dbContext.Set<TaskTypeStationHoldRow>().AsNoTracking()
            .Where(row => row.ReleasedAt == null)
            .ToArrayAsync(cancellationToken);

        int[] mapIds = [.. pointers.Select(pointer => pointer.MapId).Concat(holds.Select(hold => hold.MapId)).Distinct().Order()];
        List<object> maps = [];
        foreach (int mapId in mapIds)
        {
            maps.Add(await ReadMapAsync(
                dbContext,
                mapId,
                pointers.SingleOrDefault(pointer => pointer.MapId == mapId),
                rules,
                [.. holds.Where(hold => hold.MapId == mapId)],
                cancellationToken));
        }
        return new { ruleVersion, maps };
    }

    private static async Task<object> ReadMapAsync(
        ControlServerDbContext dbContext,
        int mapId,
        TaskTypeStationActiveBindingSetRow? pointer,
        TaskTypeStationRuleRow[] rules,
        TaskTypeStationHoldRow[] holds,
        CancellationToken cancellationToken)
    {
        long? activeVersion = pointer?.ActiveVersion;
        string[] required = activeVersion is null
            ? []
            : await dbContext.Set<TaskTypeStationRequirementRow>().AsNoTracking()
                .Where(row => row.MapId == mapId && row.Version == activeVersion.Value)
                .Select(row => row.TaskType)
                .ToArrayAsync(cancellationToken);
        TaskTypeStationBindingRow[] bindings = activeVersion is null
            ? []
            : await dbContext.Set<TaskTypeStationBindingRow>().AsNoTracking()
                .Where(row => row.MapId == mapId && row.Version == activeVersion.Value)
                .ToArrayAsync(cancellationToken);
        long? catalogRevision = await dbContext.MapStationCatalogStates.AsNoTracking()
            .Where(row => row.MapId == mapId)
            .Select(row => (long?)row.CatalogRevision)
            .SingleOrDefaultAsync(cancellationToken);
        int[] goneFromCatalog = catalogRevision is null
            ? []
            : await dbContext.Set<TaskTypeStationCatalogChangeRow>().AsNoTracking()
                .Where(row => row.MapId == mapId
                    && row.CatalogRevision == catalogRevision.Value
                    && row.CurrentStationName == null)
                .Select(row => row.StationRiotId)
                .ToArrayAsync(cancellationToken);

        string[] taskTypes =
        [
            .. rules.Select(rule => rule.TaskType)
                .Concat(holds.Select(hold => hold.TaskType))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
        ];
        return new
        {
            mapId,
            activeBindingSetVersion = activeVersion,
            activationState = pointer?.State,
            catalogRevision,
            taskTypes = taskTypes.Select(taskType =>
            {
                TaskTypeStationBindingRow? binding = bindings.SingleOrDefault(
                    row => string.Equals(row.TaskType, taskType, StringComparison.Ordinal));
                bool isRequired = required.Contains(taskType, StringComparer.Ordinal);
                TaskTypeStationHoldRow[] standing =
                [
                    .. holds.Where(hold => string.Equals(hold.TaskType, taskType, StringComparison.Ordinal))
                        .OrderBy(hold => hold.RaisedAt)
                        .ThenBy(hold => hold.HoldId, StringComparer.Ordinal)
                ];
                string status = standing.Length > 0 ? "HELD"
                    : activeVersion is null ? "NO_ACTIVE_BINDING_SET"
                    : binding is not null && goneFromCatalog.Contains(binding.StationRiotId) ? "STATION_NOT_IN_CATALOG"
                    : isRequired && binding is null ? "BINDING_MISSING"
                    : !isRequired ? "NOT_REQUIRED"
                    : "NORMAL";
                return new
                {
                    taskType,
                    fixedEnd = rules.SingleOrDefault(rule => string.Equals(rule.TaskType, taskType, StringComparison.Ordinal))?.FixedEnd,
                    required = isRequired,
                    stationRiotId = binding?.StationRiotId,
                    stationName = binding?.StationName,
                    status,
                    holds = standing.Select(hold => new
                    {
                        holdId = hold.HoldId,
                        source = hold.Source,
                        sourceLabel = SourceLabel(hold.Source),
                        reasonCode = hold.ReasonCode,
                        raisedAt = hold.RaisedAt,
                        reason = Reason(hold)
                    })
                };
            })
        };
    }

    private static string SourceLabel(string source) => source switch
    {
        TaskTypeStationHoldSource.Manual => "看板人工",
        TaskTypeStationHoldSource.CatalogChange => "目录变化",
        // Written by batch 6-05's activation transaction directly, not through RaiseAsync (control-server#161).
        TaskTypeStationHoldSource.ActivationResultUnknown => "激活结果未知",
        _ => source
    };

    private static string? Reason(TaskTypeStationHoldRow hold)
    {
        switch (hold.ReasonCode)
        {
            case CatalogBindingHoldReasons.StationRenamed:
                return "站点改名";
            case CatalogBindingHoldReasons.StationNotInCatalog:
                return "站点已不在目录";
        }
        try
        {
            using JsonDocument detail = JsonDocument.Parse(hold.DetailJson);
            return detail.RootElement.ValueKind == JsonValueKind.Object
                && detail.RootElement.TryGetProperty("reason", out JsonElement reason)
                && reason.ValueKind == JsonValueKind.String
                    ? reason.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
