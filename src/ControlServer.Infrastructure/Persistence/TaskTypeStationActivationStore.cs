using System.Globalization;
using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ControlServer.Infrastructure.Persistence;

/// <summary>激活两步落库、读回、对账与解除暂停的存取（control-server#161）。</summary>
/// <remarks>
/// 激活尝试用 #159 生效指针表的 <c>State</c>／<c>PendingVersion</c> 表达，不另建表（零 migration）：指针处于
/// <c>ACTIVATION_UNKNOWN</c> 就是有一次未结的尝试，它的来历（尝试号、目标版本、原版本）在第一步那条审计里，
/// 也写在它置下的每条暂停的 <c>DetailJson</c> 里。失败的写入整体回滚，并清掉上下文里没提交的跟踪，免得下一次保存把它们带上。
/// </remarks>
public sealed class TaskTypeStationActivationStore(
    ControlServerDbContext context,
    ITaskTypeStationBindingStore bindings,
    IGovernanceAuditWriter audit) : ITaskTypeStationActivationStore
{
    private readonly ControlServerDbContext _context = context ?? throw new ArgumentNullException(nameof(context));
    private readonly ITaskTypeStationBindingStore _bindings = bindings ?? throw new ArgumentNullException(nameof(bindings));
    private readonly IGovernanceAuditWriter _audit = audit ?? throw new ArgumentNullException(nameof(audit));

    public async Task<TaskTypeStationActivationAttempt> BeginAsync(
        TaskTypeStationActivationStart start,
        Func<TaskTypeStationActivationAttempt, GovernanceAuditEntry> startedAudit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(startedAudit);
        TaskTypeStationCandidate candidate = start.Candidate;

        try
        {
            await using IDbContextTransaction transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
            TaskTypeStationActiveBindingSetRow? pointer = await FreshPointerAsync(candidate.MapId, cancellationToken);
            if (pointer is not null
                && string.Equals(pointer.State, TaskTypeStationActivationState.ActivationUnknown, StringComparison.Ordinal))
            {
                throw new TaskTypeStationActivationConflictException(Invariant(
                    $"Map {candidate.MapId} already has an activation of version {pointer.PendingVersion} whose result is unknown."));
            }
            if (pointer?.ActiveVersion != start.ExpectedPreviousVersion)
            {
                throw new TaskTypeStationActivationConflictException(Invariant(
                    $"Map {candidate.MapId}'s active version is {pointer?.ActiveVersion}, not {start.ExpectedPreviousVersion} as validated."));
            }

            TaskTypeStationVersionWrite<TaskTypeStationBindingSetVersion> target = await _bindings.WriteVersionAsync(
                candidate.MapId,
                candidate.RuleVersion,
                candidate.RequiredTaskTypes,
                candidate.Bindings,
                start.CatalogRevision,
                start.Source,
                start.StartedAt,
                cancellationToken);

            if (pointer is null)
            {
                pointer = new TaskTypeStationActiveBindingSetRow
                {
                    MapId = candidate.MapId,
                    State = TaskTypeStationActivationState.ActivationUnknown
                };
                _context.Set<TaskTypeStationActiveBindingSetRow>().Add(pointer);
            }
            pointer.State = TaskTypeStationActivationState.ActivationUnknown;
            pointer.PendingVersion = target.Version.Version;
            pointer.UpdatedAt = start.StartedAt;

            TaskTypeStationActivationAttempt attempt = new(
                start.AttemptId,
                candidate.MapId,
                start.ExpectedPreviousVersion,
                target.Version.Version,
                start.HeldTaskTypes,
                []);
            attempt = attempt with
            {
                HoldIds = [.. start.HeldTaskTypes.Select(taskType => AddUnknownHold(attempt, taskType, start.StartedAt).HoldId)]
            };
            await _context.SaveChangesAsync(cancellationToken);
            await _audit.WriteBusinessAsync(startedAudit(attempt), start.StartedAt, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return attempt;
        }
        catch
        {
            _context.ChangeTracker.Clear();
            throw;
        }
    }

    public async Task CompleteAsync(
        TaskTypeStationActivationAttempt attempt,
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        try
        {
            await using IDbContextTransaction transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
            TaskTypeStationActiveBindingSetRow pointer = await FreshPointerAsync(attempt.MapId, cancellationToken)
                ?? throw new InvalidOperationException(Invariant($"Map {attempt.MapId} has no active pointer row."));
            pointer.ActiveVersion = attempt.TargetVersion;
            pointer.State = TaskTypeStationActivationState.Active;
            pointer.PendingVersion = null;
            pointer.UpdatedAt = at;
            await ReleaseAttemptHoldsAsync(attempt, at, cancellationToken);
            await _context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            _context.ChangeTracker.Clear();
            throw;
        }
    }

    public async Task<TaskTypeStationActiveReadBack> ReadBackAsync(int mapId, CancellationToken cancellationToken)
    {
        TaskTypeStationActivePointer? pointer = await _bindings.ReadActivePointerAsync(mapId, cancellationToken);
        if (pointer?.ActiveVersion is not long activeVersion)
        {
            return new TaskTypeStationActiveReadBack(pointer, null, false, Invariant($"Map {mapId} has no active version."));
        }
        TaskTypeStationBindingSetVersion? active = await _bindings.ReadVersionAsync(mapId, activeVersion, cancellationToken);
        if (active is null)
        {
            return new TaskTypeStationActiveReadBack(
                pointer, null, false, Invariant($"Map {mapId}'s pointer names version {activeVersion}, which does not exist."));
        }

        // Recomputed from the rows as they stand, not taken from the header: the header is what was meant, the rows are
        // what is there.
        string recomputed = TaskTypeStationContent.Sha256(TaskTypeStationContent.BindingSetJson(
            mapId, active.RuleVersion, active.RequiredTaskTypes, active.Bindings));
        string? frozen = await _context.Set<GovernedConfigurationSnapshotRow>()
            .AsNoTracking()
            .Where(row => row.SnapshotId == active.SnapshotId)
            .Select(row => row.ContentSha256)
            .SingleOrDefaultAsync(cancellationToken);
        bool verified = string.Equals(recomputed, active.ContentSha256, StringComparison.Ordinal)
            && string.Equals(frozen, active.ContentSha256, StringComparison.Ordinal);
        return new TaskTypeStationActiveReadBack(
            pointer,
            active,
            verified,
            verified
                ? Invariant($"Map {mapId} version {activeVersion} reads back with content {recomputed}.")
                : Invariant($"Map {mapId} version {activeVersion} reads back with content {recomputed}, but its header says {active.ContentSha256} and its snapshot {frozen ?? "<missing>"}."));
    }

    public async Task<TaskTypeStationActivationAttempt> MarkUnknownAsync(
        TaskTypeStationActivationAttempt attempt,
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        try
        {
            await using IDbContextTransaction transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
            TaskTypeStationActiveBindingSetRow? pointer = await FreshPointerAsync(attempt.MapId, cancellationToken);
            if (pointer is null)
            {
                pointer = new TaskTypeStationActiveBindingSetRow
                {
                    MapId = attempt.MapId,
                    State = TaskTypeStationActivationState.ActivationUnknown
                };
                _context.Set<TaskTypeStationActiveBindingSetRow>().Add(pointer);
            }
            // The active version is left as it reads: which one is really in force is the reconciliation's question.
            pointer.State = TaskTypeStationActivationState.ActivationUnknown;
            pointer.PendingVersion = attempt.TargetVersion;
            pointer.UpdatedAt = at;

            List<TaskTypeStationHoldRow> open = await OpenAttemptHoldsAsync(attempt.MapId, attempt.AttemptId, cancellationToken);
            HashSet<string> held = new(open.Select(row => row.TaskType), StringComparer.Ordinal);
            foreach (string taskType in attempt.HeldTaskTypes.Where(taskType => !held.Contains(taskType)))
            {
                open.Add(AddUnknownHold(attempt, taskType, at));
            }
            await _context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return attempt with { HoldIds = [.. open.Select(row => row.HoldId).Order(StringComparer.Ordinal)] };
        }
        catch
        {
            _context.ChangeTracker.Clear();
            throw;
        }
    }

    public async Task<TaskTypeStationActivationAttempt?> ReadOpenAttemptAsync(int mapId, CancellationToken cancellationToken)
    {
        TaskTypeStationActivePointer? pointer = await _bindings.ReadActivePointerAsync(mapId, cancellationToken);
        if (pointer is null
            || !string.Equals(pointer.State, TaskTypeStationActivationState.ActivationUnknown, StringComparison.Ordinal)
            || pointer.PendingVersion is not long target)
        {
            return null;
        }

        // The attempt's history is the started record the first step committed together with the pointer.
        string objectId = TaskTypeStationGovernance.BindingSetObjectId(mapId);
        BusinessAuditRecordRow[] started = await _context.Set<BusinessAuditRecordRow>()
            .AsNoTracking()
            .Where(row => row.ObjectKind == GovernedObjectKind.PublicStationBinding
                && row.ObjectId == objectId
                && row.Action == TaskTypeStationActivationAuditActions.Started
                && row.Version == target)
            .ToArrayAsync(cancellationToken);
        BusinessAuditRecordRow? latest = started.OrderByDescending(row => row.RecordedAtUtcTicks).FirstOrDefault();
        if (latest is null)
        {
            // Not a state the service produces; say what the pointer says and hold on whatever is held.
            List<TaskTypeStationHoldRow> anyOpen = await _context.Set<TaskTypeStationHoldRow>()
                .AsNoTracking()
                .Where(row => row.MapId == mapId
                    && row.Source == TaskTypeStationHoldSource.ActivationResultUnknown
                    && row.ReleasedAt == null)
                .ToListAsync(cancellationToken);
            return new TaskTypeStationActivationAttempt(
                "unknown", mapId, pointer.ActiveVersion, target,
                [.. anyOpen.Select(row => row.TaskType).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
                [.. anyOpen.Select(row => row.HoldId).Order(StringComparer.Ordinal)]);
        }

        using JsonDocument detail = JsonDocument.Parse(latest.DetailJson);
        JsonElement root = detail.RootElement;
        string attemptId = root.GetProperty("attemptId").GetString()!;
        JsonElement previous = root.GetProperty("bindingSetVersion").GetProperty("previous");
        string[] heldTaskTypes =
        [
            .. root.TryGetProperty("heldTaskTypes", out JsonElement held) && held.ValueKind == JsonValueKind.Array
                ? held.EnumerateArray().Select(item => item.GetString()!)
                : []
        ];
        List<TaskTypeStationHoldRow> open = await OpenAttemptHoldsAsync(mapId, attemptId, cancellationToken);
        return new TaskTypeStationActivationAttempt(
            attemptId,
            mapId,
            previous.ValueKind == JsonValueKind.Number ? previous.GetInt64() : null,
            target,
            heldTaskTypes,
            [.. open.Select(row => row.HoldId).Order(StringComparer.Ordinal)]);
    }

    public async Task<IReadOnlyList<string>> ResolveAsync(
        TaskTypeStationActivationAttempt attempt,
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        try
        {
            await using IDbContextTransaction transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
            TaskTypeStationActiveBindingSetRow pointer = await FreshPointerAsync(attempt.MapId, cancellationToken)
                ?? throw new InvalidOperationException(Invariant($"Map {attempt.MapId} has no active pointer row."));
            pointer.State = TaskTypeStationActivationState.Active;
            pointer.PendingVersion = null;
            pointer.UpdatedAt = at;
            List<TaskTypeStationHoldRow> open = await OpenAttemptHoldsAsync(attempt.MapId, attempt.AttemptId, cancellationToken);
            foreach (TaskTypeStationHoldRow row in open)
            {
                row.ReleasedAt = at;
                row.ReleasedBy = HoldActor(attempt);
            }
            await _context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return [.. open.Select(row => row.HoldId).Order(StringComparer.Ordinal)];
        }
        catch
        {
            _context.ChangeTracker.Clear();
            throw;
        }
    }

    public async Task<IReadOnlyList<TaskTypeStationHold>> ReleaseManualAndCatalogHoldsAsync(
        int mapId,
        string taskType,
        string releasedBy,
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskType);
        ArgumentException.ThrowIfNullOrWhiteSpace(releasedBy);
        try
        {
            List<TaskTypeStationHoldRow> open = await _context.Set<TaskTypeStationHoldRow>()
                .Where(row => row.MapId == mapId
                    && row.TaskType == taskType
                    && (row.Source == TaskTypeStationHoldSource.Manual || row.Source == TaskTypeStationHoldSource.CatalogChange)
                    && row.ReleasedAt == null)
                .ToListAsync(cancellationToken);
            open = await FreshOpenAsync(open, cancellationToken);
            foreach (TaskTypeStationHoldRow row in open)
            {
                // Released, never deleted: the row stays the record that the task type was held, by whom and why.
                row.ReleasedAt = at;
                row.ReleasedBy = releasedBy;
            }
            await _context.SaveChangesAsync(cancellationToken);
            return
            [
                .. open.OrderBy(row => row.RaisedAt).ThenBy(row => row.HoldId, StringComparer.Ordinal)
                    .Select(row => new TaskTypeStationHold(
                        row.HoldId, row.MapId, row.TaskType, row.Source, row.ReasonCode, row.DetailJson, row.RaisedAt,
                        row.RaisedBy, row.ReleasedAt, row.ReleasedBy))
            ];
        }
        catch
        {
            _context.ChangeTracker.Clear();
            throw;
        }
    }

    public async Task<IReadOnlyList<TaskTypeStationInFlightDemand>> ListInFlightDemandsAsync(
        int mapId,
        CancellationToken cancellationToken)
    {
        string objectId = TaskTypeStationGovernance.BindingSetObjectId(mapId);
        var freezes = await _context.Set<ConfigurationConsumerBindingRow>()
            .AsNoTracking()
            .Where(row => row.ConsumerKind == TaskTypeStationGovernance.DemandConsumerKind
                && row.ObjectKind == GovernedObjectKind.PublicStationBinding
                && row.ObjectId == objectId)
            .Join(
                _context.Set<AcceptedDemandRow>().AsNoTracking(),
                freeze => freeze.ConsumerId,
                demand => demand.DemandId,
                (freeze, demand) => new { freeze.ConsumerId, freeze.FrozenVersion, demand.WorkType })
            .ToArrayAsync(cancellationToken);
        string[] demandIds = [.. freezes.Select(row => row.ConsumerId)];
        // Same notion of in flight as the area assignment preview: frozen, and its journey has not completed.
        HashSet<string> completed = new(
            await _context.Set<JourneyRuntimeRow>()
                .AsNoTracking()
                .Where(row => demandIds.Contains(row.DemandId) && row.Stage == JourneyRuntimeStage.Completed)
                .Select(row => row.DemandId)
                .ToArrayAsync(cancellationToken),
            StringComparer.Ordinal);
        return
        [
            .. freezes.Where(row => !completed.Contains(row.ConsumerId))
                .OrderBy(row => row.ConsumerId, StringComparer.Ordinal)
                .Select(row => new TaskTypeStationInFlightDemand(row.ConsumerId, row.WorkType, row.FrozenVersion))
        ];
    }

    private TaskTypeStationHoldRow AddUnknownHold(TaskTypeStationActivationAttempt attempt, string taskType, DateTimeOffset at)
    {
        TaskTypeStationHoldRow row = new()
        {
            HoldId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
            MapId = attempt.MapId,
            TaskType = taskType,
            Source = TaskTypeStationHoldSource.ActivationResultUnknown,
            ReasonCode = TaskTypeStationActivationReasonCodes.ActivationResultUnknown,
            DetailJson = JsonSerializer.Serialize(new
            {
                attemptId = attempt.AttemptId,
                targetVersion = attempt.TargetVersion,
                previousVersion = attempt.PreviousVersion
            }),
            RaisedAt = at,
            RaisedBy = HoldActor(attempt)
        };
        _context.Set<TaskTypeStationHoldRow>().Add(row);
        return row;
    }

    /// <summary>撤掉本次尝试置下、仍成立的「激活结果未知」暂停。只认本次尝试的，人工与目录变化的暂停一条不碰。</summary>
    private async Task<List<string>> ReleaseAttemptHoldsAsync(
        TaskTypeStationActivationAttempt attempt,
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        string[] ids = [.. attempt.HoldIds];
        List<TaskTypeStationHoldRow> open = await _context.Set<TaskTypeStationHoldRow>()
            .Where(row => ids.Contains(row.HoldId)
                && row.Source == TaskTypeStationHoldSource.ActivationResultUnknown
                && row.ReleasedAt == null)
            .ToListAsync(cancellationToken);
        open = await FreshOpenAsync(open, cancellationToken);
        foreach (TaskTypeStationHoldRow row in open)
        {
            row.ReleasedAt = at;
            row.ReleasedBy = HoldActor(attempt);
        }
        return [.. open.Select(row => row.HoldId).Order(StringComparer.Ordinal)];
    }

    /// <summary>
    /// 该图的指针行，按库里此刻的值。同一个上下文先前跟踪过这一行时，EF 的身份解析会把旧的那个实例原样还回来，
    /// 在它上面改值可能被判「没变」而不落库——所以跟踪过的就重读一遍。
    /// </summary>
    private async Task<TaskTypeStationActiveBindingSetRow?> FreshPointerAsync(int mapId, CancellationToken cancellationToken)
    {
        TaskTypeStationActiveBindingSetRow? row = await _context.Set<TaskTypeStationActiveBindingSetRow>()
            .SingleOrDefaultAsync(candidate => candidate.MapId == mapId, cancellationToken);
        if (row is not null)
        {
            await _context.Entry(row).ReloadAsync(cancellationToken);
        }
        return row;
    }

    /// <summary>同上，给暂停行：重读之后仍未解除的才算数。</summary>
    private async Task<List<TaskTypeStationHoldRow>> FreshOpenAsync(
        List<TaskTypeStationHoldRow> rows,
        CancellationToken cancellationToken)
    {
        foreach (TaskTypeStationHoldRow row in rows)
        {
            await _context.Entry(row).ReloadAsync(cancellationToken);
        }
        return [.. rows.Where(row => row.ReleasedAt is null)];
    }

    /// <summary>该图仍成立、由这次尝试置下的「激活结果未知」暂停（尝试号在暂停的 <c>DetailJson</c> 里）。</summary>
    private async Task<List<TaskTypeStationHoldRow>> OpenAttemptHoldsAsync(
        int mapId,
        string attemptId,
        CancellationToken cancellationToken)
    {
        List<TaskTypeStationHoldRow> open = await _context.Set<TaskTypeStationHoldRow>()
            .Where(row => row.MapId == mapId
                && row.Source == TaskTypeStationHoldSource.ActivationResultUnknown
                && row.ReleasedAt == null)
            .ToListAsync(cancellationToken);
        open = await FreshOpenAsync(open, cancellationToken);
        return [.. open.Where(row => string.Equals(AttemptOf(row), attemptId, StringComparison.Ordinal))];
    }

    private static string? AttemptOf(TaskTypeStationHoldRow row)
    {
        using JsonDocument detail = JsonDocument.Parse(row.DetailJson);
        return detail.RootElement.TryGetProperty("attemptId", out JsonElement attemptId) ? attemptId.GetString() : null;
    }

    private static string HoldActor(TaskTypeStationActivationAttempt attempt) =>
        "fieldops:activation:" + attempt.AttemptId;

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
