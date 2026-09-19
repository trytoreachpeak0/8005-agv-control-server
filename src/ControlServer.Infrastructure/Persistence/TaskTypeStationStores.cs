using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using ControlServer.Application;
using ControlServer.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ControlServer.Infrastructure.Persistence;

/// <summary>
/// 任务类型规则表的版本存取（REQ-0343）：整张表一个版本号。
/// </summary>
/// <remarks>
/// 与 <see cref="AreaAssignmentStore"/> 同一个做法：每写一版都经 <see cref="GovernedConfigurationPublisher"/> 冻结快照并写业务审计，
/// 与版本行、规则行在同一个事务里；版本行与规则行写入即不可改，由 <see cref="PublishedVersionImmutabilityGuard"/> 在上下文上拦。
/// 只存取，不做业务校验——那是 <see cref="TaskTypeStationConfigurationValidator"/> 的事。版本号取「当前最大 + 1」，两次写入同时进来时
/// 后到的一方因主键冲突整体回滚（沿用批次 4 的口径）。
/// </remarks>
public sealed class TaskTypeStationRuleStore(
    ControlServerDbContext context,
    GovernedConfigurationPublisher publisher) : ITaskTypeStationRuleStore
{
    private readonly ControlServerDbContext _context = context ?? throw new ArgumentNullException(nameof(context));
    private readonly GovernedConfigurationPublisher _publisher =
        publisher ?? throw new ArgumentNullException(nameof(publisher));

    public async Task<TaskTypeStationRuleVersion?> ReadCurrentAsync(CancellationToken cancellationToken)
    {
        long? current = await _context.Set<TaskTypeStationRuleVersionRow>()
            .MaxAsync(row => (long?)row.Version, cancellationToken);
        return current is null ? null : await ReadVersionAsync(current.Value, cancellationToken);
    }

    public async Task<TaskTypeStationRuleVersion?> ReadVersionAsync(long version, CancellationToken cancellationToken)
    {
        TaskTypeStationRuleVersionRow? header = await _context.Set<TaskTypeStationRuleVersionRow>()
            .AsNoTracking()
            .SingleOrDefaultAsync(row => row.Version == version, cancellationToken);
        if (header is null)
        {
            return null;
        }
        TaskTypeStationRuleRow[] rows = await _context.Set<TaskTypeStationRuleRow>()
            .AsNoTracking()
            .Where(row => row.Version == version)
            .ToArrayAsync(cancellationToken);
        return new TaskTypeStationRuleVersion(
            header.Version,
            header.ContentSha256,
            header.SnapshotId,
            header.LoadedAt,
            header.Source,
            [
                .. rows.OrderBy(row => row.TaskType, StringComparer.Ordinal)
                    .Select(row => new TaskTypeStationRule(row.TaskType, row.FixedEnd))
            ]);
    }

    public async Task<TaskTypeStationVersionWrite<TaskTypeStationRuleVersion>> WriteVersionAsync(
        IReadOnlyList<TaskTypeStationRule> rules,
        string source,
        DateTimeOffset loadedAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        foreach (TaskTypeStationRule rule in rules)
        {
            ArgumentNullException.ThrowIfNull(rule, nameof(rules));
            ArgumentException.ThrowIfNullOrWhiteSpace(rule.TaskType, nameof(rules));
            ArgumentException.ThrowIfNullOrWhiteSpace(rule.FixedEnd, nameof(rules));
        }

        // Ordered so that the same table always freezes to the same content and the same SHA-256.
        TaskTypeStationRule[] ordered = [.. rules.OrderBy(rule => rule.TaskType, StringComparer.Ordinal)];
        string contentJson = TaskTypeStationContent.RulesJson(ordered);

        await using IDbContextTransaction? transaction = _context.Database.CurrentTransaction is null
            ? await _context.Database.BeginTransactionAsync(cancellationToken)
            : null;

        TaskTypeStationRuleVersionRow? latest = await _context.Set<TaskTypeStationRuleVersionRow>()
            .AsNoTracking()
            .OrderByDescending(row => row.Version)
            .FirstOrDefaultAsync(cancellationToken);
        if (latest is not null
            && string.Equals(latest.ContentSha256, TaskTypeStationContent.Sha256(contentJson), StringComparison.Ordinal))
        {
            return new(
                (await ReadVersionAsync(latest.Version, cancellationToken))!,
                Created: false);
        }

        long version = (latest?.Version ?? 0) + 1;
        GovernedConfigurationSnapshot snapshot = await _publisher.PublishVersionAsync(
            GovernedObjectKind.TaskTypeStationRule,
            TaskTypeStationGovernance.RuleObjectId,
            version,
            contentJson,
            TaskTypeStationGovernance.RuleVersionLoadedAction,
            loadedAt,
            cancellationToken);
        _context.Set<TaskTypeStationRuleVersionRow>().Add(new TaskTypeStationRuleVersionRow
        {
            Version = version,
            ContentSha256 = snapshot.ContentSha256,
            SnapshotId = snapshot.SnapshotId,
            LoadedAt = loadedAt,
            Source = source
        });
        _context.Set<TaskTypeStationRuleRow>().AddRange(ordered.Select(rule => new TaskTypeStationRuleRow
        {
            Version = version,
            TaskType = rule.TaskType,
            FixedEnd = rule.FixedEnd
        }));
        await _context.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        return new(
            new TaskTypeStationRuleVersion(
                version, snapshot.ContentSha256, snapshot.SnapshotId, loadedAt, source, ordered),
            Created: true);
    }
}

/// <summary>
/// 按图的绑定集版本存取（REQ-0337）与生效指针。每张 Map 一条版本线，版本号按图各自从 1 编号。
/// </summary>
/// <remarks>
/// 内容是需求集、绑定与所依赖的规则版本；目录修订、来源与时间是这一版的来历，不算内容，所以同样的绑定换一个目录修订重启不出新版本。
/// 绑定行上的唯一索引 <c>(MapId, Version, StationRiotId)</c> 是数据库自己的一道线：这里不预先查「同一 Station 两个任务类型」，
/// 违反时整个版本随事务回滚。
/// </remarks>
public sealed class TaskTypeStationBindingStore(
    ControlServerDbContext context,
    GovernedConfigurationPublisher publisher) : ITaskTypeStationBindingStore
{
    private readonly ControlServerDbContext _context = context ?? throw new ArgumentNullException(nameof(context));
    private readonly GovernedConfigurationPublisher _publisher =
        publisher ?? throw new ArgumentNullException(nameof(publisher));

    public async Task<TaskTypeStationBindingSetVersion?> ReadActiveAsync(int mapId, CancellationToken cancellationToken)
    {
        TaskTypeStationActivePointer? pointer = await ReadActivePointerAsync(mapId, cancellationToken);
        return pointer?.ActiveVersion is long active
            ? await ReadVersionAsync(mapId, active, cancellationToken)
            : null;
    }

    public async Task<TaskTypeStationBindingSetVersion?> ReadLatestAsync(int mapId, CancellationToken cancellationToken)
    {
        long? latest = await _context.Set<TaskTypeStationBindingSetVersionRow>()
            .Where(row => row.MapId == mapId)
            .MaxAsync(row => (long?)row.Version, cancellationToken);
        return latest is null ? null : await ReadVersionAsync(mapId, latest.Value, cancellationToken);
    }

    public async Task<TaskTypeStationBindingSetVersion?> ReadVersionAsync(
        int mapId,
        long version,
        CancellationToken cancellationToken)
    {
        TaskTypeStationBindingSetVersionRow? header = await _context.Set<TaskTypeStationBindingSetVersionRow>()
            .AsNoTracking()
            .SingleOrDefaultAsync(row => row.MapId == mapId && row.Version == version, cancellationToken);
        if (header is null)
        {
            return null;
        }
        string[] required = await _context.Set<TaskTypeStationRequirementRow>()
            .AsNoTracking()
            .Where(row => row.MapId == mapId && row.Version == version)
            .Select(row => row.TaskType)
            .ToArrayAsync(cancellationToken);
        TaskTypeStationBindingRow[] bindings = await _context.Set<TaskTypeStationBindingRow>()
            .AsNoTracking()
            .Where(row => row.MapId == mapId && row.Version == version)
            .ToArrayAsync(cancellationToken);
        return new TaskTypeStationBindingSetVersion(
            header.MapId,
            header.Version,
            header.RuleVersion,
            header.ContentSha256,
            header.SnapshotId,
            header.CatalogRevision,
            header.LoadedAt,
            header.Source,
            [.. required.Order(StringComparer.Ordinal)],
            [
                .. bindings.OrderBy(row => row.TaskType, StringComparer.Ordinal)
                    .Select(row => new TaskTypeStationBinding(
                        row.TaskType, row.StationRiotId, row.StationName, row.SiteVerificationRef))
            ]);
    }

    public async Task<TaskTypeStationVersionWrite<TaskTypeStationBindingSetVersion>> WriteVersionAsync(
        int mapId,
        long ruleVersion,
        IReadOnlyList<string> requiredTaskTypes,
        IReadOnlyList<TaskTypeStationBinding> bindings,
        long? catalogRevision,
        string source,
        DateTimeOffset loadedAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requiredTaskTypes);
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        foreach (TaskTypeStationBinding binding in bindings)
        {
            ArgumentNullException.ThrowIfNull(binding, nameof(bindings));
            ArgumentException.ThrowIfNullOrWhiteSpace(binding.TaskType, nameof(bindings));
            ArgumentNullException.ThrowIfNull(binding.StationName, nameof(bindings));
            ArgumentNullException.ThrowIfNull(binding.SiteVerificationRef, nameof(bindings));
        }

        string[] required = [.. requiredTaskTypes.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        TaskTypeStationBinding[] ordered = [.. bindings.OrderBy(binding => binding.TaskType, StringComparer.Ordinal)];
        string contentJson = TaskTypeStationContent.BindingSetJson(mapId, ruleVersion, required, ordered);

        await using IDbContextTransaction? transaction = _context.Database.CurrentTransaction is null
            ? await _context.Database.BeginTransactionAsync(cancellationToken)
            : null;

        // A binding set names the rule version it was validated against; one naming a version nobody can read back has no
        // rules behind it. Checked inside the transaction, so it is the same database state the version lands in.
        if (!await _context.Set<TaskTypeStationRuleVersionRow>()
                .AnyAsync(row => row.Version == ruleVersion, cancellationToken))
        {
            throw new InvalidOperationException(string.Create(
                CultureInfo.InvariantCulture,
                $"Task type rule version {ruleVersion} does not exist, so Map {mapId} cannot have a binding set version built on it."));
        }

        TaskTypeStationBindingSetVersionRow? latest = await _context.Set<TaskTypeStationBindingSetVersionRow>()
            .AsNoTracking()
            .Where(row => row.MapId == mapId)
            .OrderByDescending(row => row.Version)
            .FirstOrDefaultAsync(cancellationToken);
        if (latest is not null
            && string.Equals(latest.ContentSha256, TaskTypeStationContent.Sha256(contentJson), StringComparison.Ordinal))
        {
            return new(
                (await ReadVersionAsync(mapId, latest.Version, cancellationToken))!,
                Created: false);
        }

        long version = (latest?.Version ?? 0) + 1;
        GovernedConfigurationSnapshot snapshot = await _publisher.PublishVersionAsync(
            GovernedObjectKind.PublicStationBinding,
            TaskTypeStationGovernance.BindingSetObjectId(mapId),
            version,
            contentJson,
            TaskTypeStationGovernance.BindingSetVersionLoadedAction,
            loadedAt,
            cancellationToken);
        _context.Set<TaskTypeStationBindingSetVersionRow>().Add(new TaskTypeStationBindingSetVersionRow
        {
            MapId = mapId,
            Version = version,
            RuleVersion = ruleVersion,
            ContentSha256 = snapshot.ContentSha256,
            SnapshotId = snapshot.SnapshotId,
            CatalogRevision = catalogRevision,
            LoadedAt = loadedAt,
            Source = source
        });
        _context.Set<TaskTypeStationRequirementRow>().AddRange(required.Select(taskType =>
            new TaskTypeStationRequirementRow { MapId = mapId, Version = version, TaskType = taskType }));
        _context.Set<TaskTypeStationBindingRow>().AddRange(ordered.Select(binding => new TaskTypeStationBindingRow
        {
            MapId = mapId,
            Version = version,
            TaskType = binding.TaskType,
            StationRiotId = binding.StationRiotId,
            StationName = binding.StationName,
            SiteVerificationRef = binding.SiteVerificationRef
        }));
        await _context.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        return new(
            new TaskTypeStationBindingSetVersion(
                mapId, version, ruleVersion, snapshot.ContentSha256, snapshot.SnapshotId, catalogRevision, loadedAt,
                source, required, ordered),
            Created: true);
    }

    public async Task<TaskTypeStationActivePointer?> ReadActivePointerAsync(
        int mapId,
        CancellationToken cancellationToken)
    {
        TaskTypeStationActiveBindingSetRow? row = await _context.Set<TaskTypeStationActiveBindingSetRow>()
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.MapId == mapId, cancellationToken);
        return row is null ? null : Project(row);
    }

    public async Task<TaskTypeStationActivePointer> SetActiveAsync(
        int mapId,
        long version,
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        bool exists = await _context.Set<TaskTypeStationBindingSetVersionRow>()
            .AnyAsync(row => row.MapId == mapId && row.Version == version, cancellationToken);
        if (!exists)
        {
            throw new InvalidOperationException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Map {mapId} has no binding set version {version}; the active pointer only names a version that exists."));
        }

        TaskTypeStationActiveBindingSetRow? row = await _context.Set<TaskTypeStationActiveBindingSetRow>()
            .SingleOrDefaultAsync(candidate => candidate.MapId == mapId, cancellationToken);
        if (row is null)
        {
            row = new TaskTypeStationActiveBindingSetRow { MapId = mapId, State = TaskTypeStationActivationState.Active };
            _context.Set<TaskTypeStationActiveBindingSetRow>().Add(row);
        }
        row.ActiveVersion = version;
        row.State = TaskTypeStationActivationState.Active;
        row.PendingVersion = null;
        row.UpdatedAt = at;
        await _context.SaveChangesAsync(cancellationToken);
        return Project(row);
    }

    private static TaskTypeStationActivePointer Project(TaskTypeStationActiveBindingSetRow row) =>
        new(row.MapId, row.ActiveVersion, row.State, row.PendingVersion, row.UpdatedAt);
}

/// <summary>
/// 按 <c>Map + TASK_TYPE</c> 的暂停（REQ-0340）。只存取，不判断何时该暂停——人工收紧与目录变化收敛是批次6-06 的事。
/// </summary>
public sealed class TaskTypeStationHoldStore(ControlServerDbContext context) : ITaskTypeStationHoldStore
{
    private readonly ControlServerDbContext _context = context ?? throw new ArgumentNullException(nameof(context));

    public async Task<TaskTypeStationHold> RaiseAsync(
        int mapId,
        string taskType,
        string source,
        string reasonCode,
        string detailJson,
        string raisedBy,
        DateTimeOffset raisedAt,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskType);
        ArgumentException.ThrowIfNullOrWhiteSpace(reasonCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(detailJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(raisedBy);
        if (source is not (TaskTypeStationHoldSource.Manual or TaskTypeStationHoldSource.CatalogChange))
        {
            throw new ArgumentException(
                $"A hold's source is {TaskTypeStationHoldSource.Manual} or {TaskTypeStationHoldSource.CatalogChange}, not '{source}'.",
                nameof(source));
        }

        TaskTypeStationHoldRow row = new()
        {
            HoldId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
            MapId = mapId,
            TaskType = taskType,
            Source = source,
            ReasonCode = reasonCode,
            DetailJson = detailJson,
            RaisedAt = raisedAt,
            RaisedBy = raisedBy
        };
        _context.Set<TaskTypeStationHoldRow>().Add(row);
        await _context.SaveChangesAsync(cancellationToken);
        return Project(row);
    }

    public async Task<bool> ReleaseAsync(
        string holdId,
        string releasedBy,
        DateTimeOffset releasedAt,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(holdId);
        ArgumentException.ThrowIfNullOrWhiteSpace(releasedBy);

        TaskTypeStationHoldRow? row = await _context.Set<TaskTypeStationHoldRow>()
            .SingleOrDefaultAsync(candidate => candidate.HoldId == holdId && candidate.ReleasedAt == null, cancellationToken);
        if (row is null)
        {
            return false;
        }
        // Released, never deleted: the row is the record that the task type was held, by whom and why.
        row.ReleasedAt = releasedAt;
        row.ReleasedBy = releasedBy;
        await _context.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<TaskTypeStationHold>> ListUnreleasedAsync(
        int mapId,
        CancellationToken cancellationToken)
    {
        TaskTypeStationHoldRow[] rows = await _context.Set<TaskTypeStationHoldRow>()
            .AsNoTracking()
            .Where(row => row.MapId == mapId && row.ReleasedAt == null)
            .ToArrayAsync(cancellationToken);
        // Ordered in memory: SQLite cannot ORDER BY a DateTimeOffset.
        return
        [
            .. rows.OrderBy(row => row.RaisedAt)
                .ThenBy(row => row.HoldId, StringComparer.Ordinal)
                .Select(Project)
        ];
    }

    public Task<bool> IsHeldAsync(int mapId, string taskType, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskType);
        return _context.Set<TaskTypeStationHoldRow>()
            .AnyAsync(
                row => row.MapId == mapId && row.TaskType == taskType && row.ReleasedAt == null,
                cancellationToken);
    }

    private static TaskTypeStationHold Project(TaskTypeStationHoldRow row) =>
        new(row.HoldId, row.MapId, row.TaskType, row.Source, row.ReasonCode, row.DetailJson, row.RaisedAt,
            row.RaisedBy, row.ReleasedAt, row.ReleasedBy);
}

/// <summary>目录变化记录的存取（REQ-0341、REQ-0342），按 <c>(MapId, StationRiotId, CatalogRevision)</c> 去重。</summary>
public sealed class TaskTypeStationCatalogChangeStore(ControlServerDbContext context)
    : ITaskTypeStationCatalogChangeStore
{
    private readonly ControlServerDbContext _context = context ?? throw new ArgumentNullException(nameof(context));

    public async Task<TaskTypeStationCatalogChange> RecordAsync(
        TaskTypeStationCatalogChange change,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        ArgumentException.ThrowIfNullOrWhiteSpace(change.ChangeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(change.ChangeKind);
        ArgumentException.ThrowIfNullOrWhiteSpace(change.RiskClass);
        ArgumentNullException.ThrowIfNull(change.PreviousStationName);
        ArgumentNullException.ThrowIfNull(change.AffectedTaskTypes);

        TaskTypeStationCatalogChangeRow? existing = await FindAsync(change, cancellationToken);
        if (existing is not null)
        {
            return Project(existing);
        }

        TaskTypeStationCatalogChangeRow row = new()
        {
            ChangeId = change.ChangeId,
            MapId = change.MapId,
            StationRiotId = change.StationRiotId,
            PreviousStationName = change.PreviousStationName,
            CurrentStationName = change.CurrentStationName,
            ChangeKind = change.ChangeKind,
            RiskClass = change.RiskClass,
            CatalogRevision = change.CatalogRevision,
            ObservedAt = change.ObservedAt,
            AffectedTaskTypesJson = JsonSerializer.Serialize(
                change.AffectedTaskTypes.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()),
            HoldId = change.HoldId
        };
        _context.Set<TaskTypeStationCatalogChangeRow>().Add(row);
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException failure) when (
            failure.InnerException is SqliteException { SqliteErrorCode: SqliteConstraintErrorCode }
            && failure.Entries.Any(entry => ReferenceEquals(entry.Entity, row)))
        {
            // Another writer recorded the same station and revision between the read and the insert: theirs is the record.
            _context.Entry(row).State = EntityState.Detached;
            TaskTypeStationCatalogChangeRow? winner = await FindAsync(change, cancellationToken);
            if (winner is null)
            {
                throw;
            }
            return Project(winner);
        }
        return Project(row);
    }

    public async Task<IReadOnlyList<TaskTypeStationCatalogChange>> ListAsync(
        int mapId,
        CancellationToken cancellationToken)
    {
        TaskTypeStationCatalogChangeRow[] rows = await _context.Set<TaskTypeStationCatalogChangeRow>()
            .AsNoTracking()
            .Where(row => row.MapId == mapId)
            .ToArrayAsync(cancellationToken);
        return
        [
            .. rows.OrderBy(row => row.ObservedAt)
                .ThenBy(row => row.ChangeId, StringComparer.Ordinal)
                .Select(Project)
        ];
    }

    private const int SqliteConstraintErrorCode = 19;

    private Task<TaskTypeStationCatalogChangeRow?> FindAsync(
        TaskTypeStationCatalogChange change,
        CancellationToken cancellationToken) =>
        _context.Set<TaskTypeStationCatalogChangeRow>()
            .AsNoTracking()
            .SingleOrDefaultAsync(
                row => row.MapId == change.MapId
                    && row.StationRiotId == change.StationRiotId
                    && row.CatalogRevision == change.CatalogRevision,
                cancellationToken);

    private static TaskTypeStationCatalogChange Project(TaskTypeStationCatalogChangeRow row) =>
        new(row.ChangeId, row.MapId, row.StationRiotId, row.PreviousStationName, row.CurrentStationName, row.ChangeKind,
            row.RiskClass, row.CatalogRevision, row.ObservedAt,
            JsonSerializer.Deserialize<string[]>(row.AffectedTaskTypesJson) ?? [], row.HoldId);
}

/// <summary>
/// 需求冻结规则版本与绑定集版本（REQ-0344），存在批次 3 的通用冻结表 <c>ConfigurationConsumerBindings</c> 里，一需求两行。
/// </summary>
/// <remarks>
/// 不给 <c>JourneyRuntimes</c> 加列：那张表批次 7 要改主键。两行在同一次 <c>SaveChanges</c> 里落，所以不会只剩一行。
/// 站点本身已由 <c>FrozenDemandStations</c> 冻结，这里只记版本。写入调用在批次6-04。
/// </remarks>
public sealed class DemandTaskTypeStationFreezeStore(ControlServerDbContext context) : IDemandTaskTypeStationFreeze
{
    private readonly ControlServerDbContext _context = context ?? throw new ArgumentNullException(nameof(context));

    public async Task<DemandTaskTypeStationFreeze> FreezeAsync(
        string demandId,
        long ruleVersion,
        int mapId,
        long bindingSetVersion,
        DateTimeOffset frozenAt,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(demandId);

        DemandTaskTypeStationFreeze? existing = await ReadAsync(demandId, cancellationToken);
        if (existing is not null)
        {
            return AgainstExisting(existing, ruleVersion, mapId, bindingSetVersion);
        }

        TaskTypeStationRuleVersionRow rules = await _context.Set<TaskTypeStationRuleVersionRow>()
            .AsNoTracking()
            .SingleOrDefaultAsync(row => row.Version == ruleVersion, cancellationToken)
            ?? throw new InvalidOperationException(string.Create(
                CultureInfo.InvariantCulture,
                $"Task type rule version {ruleVersion} does not exist, so demand {demandId} cannot freeze it."));
        TaskTypeStationBindingSetVersionRow bindings = await _context.Set<TaskTypeStationBindingSetVersionRow>()
            .AsNoTracking()
            .SingleOrDefaultAsync(row => row.MapId == mapId && row.Version == bindingSetVersion, cancellationToken)
            ?? throw new InvalidOperationException(string.Create(
                CultureInfo.InvariantCulture,
                $"Map {mapId} has no binding set version {bindingSetVersion}, so demand {demandId} cannot freeze it."));
        if (bindings.RuleVersion != rules.Version)
        {
            // The pair must be one that held together: a binding set is validated against exactly one rule version, and a
            // demand frozen against another would replay under rules its bindings were never checked with (REQ-0344).
            throw new InvalidOperationException(string.Create(
                CultureInfo.InvariantCulture,
                $"Map {mapId} binding set version {bindingSetVersion} was built on rule version {bindings.RuleVersion}, not rule version {ruleVersion}; demand {demandId} cannot freeze the two together."));
        }

        ConfigurationConsumerBindingRow ruleRow = new()
        {
            ConsumerKind = TaskTypeStationGovernance.DemandConsumerKind,
            ConsumerId = demandId,
            ObjectKind = GovernedObjectKind.TaskTypeStationRule,
            ObjectId = TaskTypeStationGovernance.RuleObjectId,
            FrozenVersion = rules.Version,
            FrozenAt = frozenAt,
            SnapshotId = rules.SnapshotId
        };
        ConfigurationConsumerBindingRow bindingRow = new()
        {
            ConsumerKind = TaskTypeStationGovernance.DemandConsumerKind,
            ConsumerId = demandId,
            ObjectKind = GovernedObjectKind.PublicStationBinding,
            ObjectId = TaskTypeStationGovernance.BindingSetObjectId(mapId),
            FrozenVersion = bindings.Version,
            FrozenAt = frozenAt,
            SnapshotId = bindings.SnapshotId
        };
        _context.Set<ConfigurationConsumerBindingRow>().AddRange(ruleRow, bindingRow);
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException failure) when (
            failure.InnerException is SqliteException { SqliteErrorCode: SqliteConstraintErrorCode }
            && failure.Entries.Any(entry => ReferenceEquals(entry.Entity, ruleRow) || ReferenceEquals(entry.Entity, bindingRow)))
        {
            // A second writer froze this demand between the read and the insert. Its freeze stands and is judged exactly
            // as if the read had seen it (same shape as DemandAreaAssignmentFreezeStore).
            _context.Entry(ruleRow).State = EntityState.Detached;
            _context.Entry(bindingRow).State = EntityState.Detached;
            DemandTaskTypeStationFreeze? winner = await ReadAsync(demandId, cancellationToken);
            if (winner is null)
            {
                throw;
            }
            return AgainstExisting(winner, ruleVersion, mapId, bindingSetVersion);
        }
        return new DemandTaskTypeStationFreeze(demandId, rules.Version, mapId, bindings.Version, frozenAt);
    }

    public async Task<DemandTaskTypeStationFreeze?> ReadAsync(string demandId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(demandId);

        ConfigurationConsumerBindingRow[] rows = await _context.Set<ConfigurationConsumerBindingRow>()
            .AsNoTracking()
            .Where(row => row.ConsumerKind == TaskTypeStationGovernance.DemandConsumerKind
                && row.ConsumerId == demandId
                && ((row.ObjectKind == GovernedObjectKind.TaskTypeStationRule
                        && row.ObjectId == TaskTypeStationGovernance.RuleObjectId)
                    || (row.ObjectKind == GovernedObjectKind.PublicStationBinding
                        && row.ObjectId.StartsWith(MapObjectIdPrefix))))
            .ToArrayAsync(cancellationToken);
        ConfigurationConsumerBindingRow? rule =
            rows.SingleOrDefault(row => row.ObjectKind == GovernedObjectKind.TaskTypeStationRule);
        ConfigurationConsumerBindingRow? binding =
            rows.SingleOrDefault(row => row.ObjectKind == GovernedObjectKind.PublicStationBinding);
        if (rule is null && binding is null)
        {
            return null;
        }
        if (rule is null || binding is null)
        {
            // Both rows are written in one save, so one without the other is not a state this store produces.
            throw new InvalidOperationException(
                $"Demand {demandId} has only half of its task type station freeze; refusing to guess the other half.");
        }
        return new DemandTaskTypeStationFreeze(
            demandId,
            rule.FrozenVersion,
            int.Parse(binding.ObjectId[MapObjectIdPrefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture),
            binding.FrozenVersion,
            rule.FrozenAt);
    }

    private const string MapObjectIdPrefix = "map-";

    private const int SqliteConstraintErrorCode = 19;

    private static DemandTaskTypeStationFreeze AgainstExisting(
        DemandTaskTypeStationFreeze existing,
        long ruleVersion,
        int mapId,
        long bindingSetVersion) =>
        existing.RuleVersion == ruleVersion && existing.MapId == mapId && existing.BindingSetVersion == bindingSetVersion
            ? existing
            : throw new DemandTaskTypeStationFreezeConflictException(string.Create(
                CultureInfo.InvariantCulture,
                $"Demand {existing.DemandId} already froze rule version {existing.RuleVersion} and Map {existing.MapId} binding set version {existing.BindingSetVersion}; ")
                + string.Create(
                    CultureInfo.InvariantCulture,
                    $"it cannot be frozen again at rule version {ruleVersion}, Map {mapId} binding set version {bindingSetVersion}. Later versions do not change a frozen demand."));
}

/// <summary>规则表与绑定集冻结进快照的内容，以及算指纹的方式（与 <see cref="GovernanceStore"/> 同一种 SHA-256）。</summary>
internal static class TaskTypeStationContent
{
    // Station names are Chinese; they stay readable in the frozen snapshot rather than turning into \uXXXX escapes.
    private static readonly JsonSerializerOptions Options = new()
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };

    internal static string RulesJson(IEnumerable<TaskTypeStationRule> ordered) =>
        JsonSerializer.Serialize(
            new { rules = ordered.Select(rule => new { taskType = rule.TaskType, fixedEnd = rule.FixedEnd }) },
            Options);

    internal static string BindingSetJson(
        int mapId,
        long ruleVersion,
        IEnumerable<string> orderedRequired,
        IEnumerable<TaskTypeStationBinding> orderedBindings) =>
        JsonSerializer.Serialize(new
        {
            mapId,
            ruleVersion,
            requiredTaskTypes = orderedRequired,
            bindings = orderedBindings.Select(binding => new
            {
                taskType = binding.TaskType,
                stationRiotId = binding.StationRiotId,
                stationName = binding.StationName,
                siteVerificationRef = binding.SiteVerificationRef
            })
        }, Options);

    internal static string Sha256(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
}
