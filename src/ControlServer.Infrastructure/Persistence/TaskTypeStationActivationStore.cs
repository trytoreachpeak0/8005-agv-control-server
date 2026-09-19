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
/// <c>ACTIVATION_UNKNOWN</c> 就是有一次未结的尝试，它的来历（尝试号、目标版本、原版本）写在它置下的每条暂停的
/// <c>DetailJson</c> 里，对账从那里取（审查 S2）。失败的写入整体回滚，并清掉上下文里没提交的跟踪，免得下一次保存把它们带上。
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
            TaskTypeStationActiveBindingSetRow? pointer = await FreshPointerAsync(attempt.MapId, cancellationToken);
            // Only the pointer this attempt left behind may be switched. A reconciliation that already concluded, or a newer
            // attempt that already began, is the truth now; a late second step must not overwrite it (review S1).
            if (pointer is null
                || !string.Equals(pointer.State, TaskTypeStationActivationState.ActivationUnknown, StringComparison.Ordinal)
                || pointer.PendingVersion != attempt.TargetVersion
                || pointer.ActiveVersion != attempt.PreviousVersion)
            {
                throw new TaskTypeStationActivationConflictException(Invariant(
                    $"Map {attempt.MapId}'s pointer is {pointer?.State ?? "absent"} with active version {pointer?.ActiveVersion} and pending version {pointer?.PendingVersion}, not what attempt {attempt.AttemptId} left (unknown, pending {attempt.TargetVersion}, active {attempt.PreviousVersion}); the second step writes nothing."));
            }
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
            if (pointer is not null
                && string.Equals(pointer.State, TaskTypeStationActivationState.ActivationUnknown, StringComparison.Ordinal)
                && pointer.PendingVersion != attempt.TargetVersion)
            {
                throw new TaskTypeStationActivationConflictException(Invariant(
                    $"Map {attempt.MapId} is waiting on another attempt (pending version {pointer.PendingVersion}); attempt {attempt.AttemptId} does not take it over."));
            }
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

        // The attempt's history is in the holds it raised, not in audit timestamps (review S2): those come from the clock of
        // whichever machine ran FieldOps, and one target version can have several started records when a version is reused.
        TaskTypeStationHoldRow[] open = await _context.Set<TaskTypeStationHoldRow>()
            .AsNoTracking()
            .Where(row => row.MapId == mapId
                && row.Source == TaskTypeStationHoldSource.ActivationResultUnknown
                && row.ReleasedAt == null)
            .ToArrayAsync(cancellationToken);
        HoldAttempt[] ofTarget = [.. open.Select(ParseHold).Where(hold => hold.TargetVersion == target)];
        string[] holdIds = [.. open.Select(row => row.HoldId).Order(StringComparer.Ordinal)];
        if (ofTarget.Length > 0)
        {
            HoldAttempt first = ofTarget.OrderBy(hold => hold.AttemptId, StringComparer.Ordinal).First();
            return new TaskTypeStationActivationAttempt(
                first.AttemptId,
                mapId,
                first.PreviousVersion,
                target,
                [
                    .. ofTarget.Where(hold => hold.AttemptId == first.AttemptId)
                        .Select(hold => hold.TaskType).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
                ],
                holdIds);
        }

        // Unknown with nothing held (a map whose requirement set was empty). Neither step touches the active version before
        // the second step commits, so whatever is active and is not the target is the version from before.
        return new TaskTypeStationActivationAttempt(
            "unattributed",
            mapId,
            pointer.ActiveVersion == target ? null : pointer.ActiveVersion,
            target,
            [],
            holdIds);
    }

    public async Task<TaskTypeStationReconciliation> ReconcileAsync(
        int mapId,
        Func<TaskTypeStationActivationAttempt?, TaskTypeStationActiveReadBack, TaskTypeStationReconciliationConclusion> decide,
        Func<TaskTypeStationActivationAttempt?, TaskTypeStationActiveReadBack, TaskTypeStationReconciliationConclusion, IReadOnlyList<string>, GovernanceAuditEntry> audit,
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(decide);
        ArgumentNullException.ThrowIfNull(audit);
        try
        {
            // One transaction for the reads the verdict rests on and the writes it causes (review S1): SQLite's BEGIN
            // IMMEDIATE keeps a second step from committing in between.
            await using IDbContextTransaction transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
            TaskTypeStationActivationAttempt? attempt = await ReadOpenAttemptAsync(mapId, cancellationToken);
            TaskTypeStationActiveReadBack readBack = await ReadBackAsync(mapId, cancellationToken);
            TaskTypeStationReconciliationConclusion conclusion = decide(attempt, readBack);

            List<string> released = [];
            if (conclusion != TaskTypeStationReconciliationConclusion.Contradictory)
            {
                TaskTypeStationActiveBindingSetRow? pointer = await FreshPointerAsync(mapId, cancellationToken);
                if (pointer is not null && pointer.ActiveVersion is null
                    && conclusion == TaskTypeStationReconciliationConclusion.PreviousActive)
                {
                    // The version from before was none: back to a map without an active version, not an ACTIVE pointer that
                    // names nothing (review S4), so the preset may still load as its first version.
                    _context.Set<TaskTypeStationActiveBindingSetRow>().Remove(pointer);
                }
                else if (pointer is not null
                    && string.Equals(pointer.State, TaskTypeStationActivationState.ActivationUnknown, StringComparison.Ordinal))
                {
                    pointer.State = TaskTypeStationActivationState.Active;
                    pointer.PendingVersion = null;
                    pointer.UpdatedAt = at;
                }
                // A map has at most one open attempt, so once there is a verdict every activation hold on it is released,
                // orphans included (review S3).
                released = await ReleaseActivationHoldsAsync(mapId, at, cancellationToken);
                await _context.SaveChangesAsync(cancellationToken);
            }
            string auditId = await _audit.WriteBusinessAsync(audit(attempt, readBack, conclusion, released), at, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new TaskTypeStationReconciliation(attempt, readBack, conclusion, released, auditId);
        }
        catch
        {
            _context.ChangeTracker.Clear();
            throw;
        }
    }

    public async Task<TaskTypeStationManualClose> CloseManuallyAsync(
        int mapId,
        Func<TaskTypeStationActivationAttempt?, TaskTypeStationActiveReadBack, bool> isContradictory,
        Func<TaskTypeStationActivationAttempt?, TaskTypeStationActiveReadBack, bool, IReadOnlyList<string>, GovernanceAuditEntry> audit,
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(isContradictory);
        ArgumentNullException.ThrowIfNull(audit);
        try
        {
            await using IDbContextTransaction transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
            TaskTypeStationActivationAttempt? attempt = await ReadOpenAttemptAsync(mapId, cancellationToken);
            TaskTypeStationActiveReadBack readBack = await ReadBackAsync(mapId, cancellationToken);
            bool close = isContradictory(attempt, readBack);

            List<string> released = [];
            if (close)
            {
                // Neither version reads back whole, so none is claimed to be in force: the map goes back to having no active
                // version, and a new activation or rollback is how it gets one again.
                TaskTypeStationActiveBindingSetRow? pointer = await FreshPointerAsync(mapId, cancellationToken);
                if (pointer is not null)
                {
                    _context.Set<TaskTypeStationActiveBindingSetRow>().Remove(pointer);
                }
                released = await ReleaseActivationHoldsAsync(mapId, at, cancellationToken);
                await _context.SaveChangesAsync(cancellationToken);
            }
            string auditId = await _audit.WriteBusinessAsync(audit(attempt, readBack, close, released), at, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new TaskTypeStationManualClose(attempt, readBack, close, released, auditId);
        }
        catch
        {
            _context.ChangeTracker.Clear();
            throw;
        }
    }

    public async Task<(IReadOnlyList<TaskTypeStationHold> Released, string AuditRecordId)> ReleaseManualAndCatalogHoldsAsync(
        int mapId,
        string taskType,
        string releasedBy,
        Func<IReadOnlyList<TaskTypeStationHold>, GovernanceAuditEntry> audit,
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskType);
        ArgumentException.ThrowIfNullOrWhiteSpace(releasedBy);
        ArgumentNullException.ThrowIfNull(audit);
        try
        {
            // The release and its audit are one transaction (review O2): a release nobody recorded did not happen.
            await using IDbContextTransaction transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
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
            TaskTypeStationHold[] released =
            [
                .. open.OrderBy(row => row.RaisedAt).ThenBy(row => row.HoldId, StringComparer.Ordinal)
                    .Select(row => new TaskTypeStationHold(
                        row.HoldId, row.MapId, row.TaskType, row.Source, row.ReasonCode, row.DetailJson, row.RaisedAt,
                        row.RaisedBy, row.ReleasedAt, row.ReleasedBy))
            ];
            string auditId = await _audit.WriteBusinessAsync(audit(released), at, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return (released, auditId);
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

    /// <summary>撤该图全部仍成立的「激活结果未知」暂停，以置下它的那次尝试的名义。</summary>
    private async Task<List<string>> ReleaseActivationHoldsAsync(int mapId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        List<TaskTypeStationHoldRow> open = await _context.Set<TaskTypeStationHoldRow>()
            .Where(row => row.MapId == mapId
                && row.Source == TaskTypeStationHoldSource.ActivationResultUnknown
                && row.ReleasedAt == null)
            .ToListAsync(cancellationToken);
        open = await FreshOpenAsync(open, cancellationToken);
        foreach (TaskTypeStationHoldRow row in open)
        {
            row.ReleasedAt = at;
            row.ReleasedBy = row.RaisedBy;
        }
        return [.. open.Select(row => row.HoldId).Order(StringComparer.Ordinal)];
    }

    private sealed record HoldAttempt(string AttemptId, long? TargetVersion, long? PreviousVersion, string TaskType);

    private static HoldAttempt ParseHold(TaskTypeStationHoldRow row)
    {
        using JsonDocument detail = JsonDocument.Parse(row.DetailJson);
        JsonElement root = detail.RootElement;
        return new HoldAttempt(
            root.TryGetProperty("attemptId", out JsonElement attemptId) ? attemptId.GetString() ?? string.Empty : string.Empty,
            root.TryGetProperty("targetVersion", out JsonElement target) && target.ValueKind == JsonValueKind.Number ? target.GetInt64() : null,
            root.TryGetProperty("previousVersion", out JsonElement previous) && previous.ValueKind == JsonValueKind.Number ? previous.GetInt64() : null,
            row.TaskType);
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
