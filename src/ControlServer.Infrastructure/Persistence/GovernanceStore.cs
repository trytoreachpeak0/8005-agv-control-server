using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Infrastructure.Persistence;

/// <summary>
/// Who the audit says acted. There is no personnel authentication in this release, so this is a
/// deployment, and it is labelled as one.
/// </summary>
public sealed class GovernanceDeploymentIdentity
{
    public GovernanceDeploymentIdentity(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (!value.StartsWith(AuditActorAttribution.DeploymentIdentityPrefix, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"A deployment identity must start with '{AuditActorAttribution.DeploymentIdentityPrefix}' so that it "
                + "cannot be mistaken for a person.",
                nameof(value));
        }
        Value = value;
    }

    public string Value { get; }

    /// <summary>The identity used when nothing is configured: this service, on this machine.</summary>
    public static GovernanceDeploymentIdentity ForCurrentHost() =>
        new(FormattableString.Invariant(
            $"{AuditActorAttribution.DeploymentIdentityPrefix}8005-controlserver@{Environment.MachineName}"));
}

/// <summary>
/// The versioned-snapshot and immutable-audit mechanism the FP-C7, FP-C5 and (from batch 4) FP-C9b
/// clusters all use. It owns no business rule of its own: a caller decides what a version means and
/// what it contains, and this decides how it is frozen, audited and read back.
/// </summary>
public sealed class GovernanceStore : IConfigurationSnapshotStore, IGovernanceAuditWriter, IGovernedConfigurationReader
{
    private readonly ControlServerDbContext _context;
    private readonly GovernanceDeploymentIdentity _deploymentIdentity;

    public GovernanceStore(
        ControlServerDbContext context,
        GovernanceDeploymentIdentity deploymentIdentity,
        AuditRetentionPolicy retentionPolicy)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(deploymentIdentity);
        ArgumentNullException.ThrowIfNull(retentionPolicy);

        // The context defaults to the REQ-0271 floor; the configured policy is applied here because
        // this is the only path that writes or purges audit, so it is the only place the difference
        // between the floor and a longer configured period can be observed.
        context.AuditRetention = retentionPolicy;
        _context = context;
        _deploymentIdentity = deploymentIdentity;
    }

    /// <summary>
    /// 冻结一版。同一版同一份内容再冻一次拿回既有那一份；同一版不同内容则明着报错。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 「同一版不同内容」意味着两个写入方在同一条版本线上取到了同一个号。这里原样返回既有快照的话，
    /// 后到的那个写入方的行与审计就指向了一份它从没发布过的内容，而唯一索引
    /// <c>(ObjectKind, ObjectId, Version)</c> 一次都没被碰到——库里没有任何东西记下这件事发生过。
    /// 既有那一版一个字节不动，被拒的是「悄悄」。
    /// </para>
    /// <para>
    /// 读与写之间还有一个窗口：另一个进程可能恰好在这中间冻上同一版。那一条由唯一索引挡下，在这里被
    /// 翻译成同一个冲突——内容相同就当作重投，返回对方那一份。
    /// </para>
    /// </remarks>
    public async Task<GovernedConfigurationSnapshot> FreezeAsync(
        GovernedObjectKind objectKind,
        string objectId,
        long version,
        string contentJson,
        DateTimeOffset frozenAt,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentJson);

        GovernedConfigurationSnapshotRow? existing = await _context.Set<GovernedConfigurationSnapshotRow>()
            .SingleOrDefaultAsync(
                row => row.ObjectKind == objectKind && row.ObjectId == objectId && row.Version == version,
                cancellationToken);
        if (existing is not null)
        {
            return SameContentOrConflict(existing, contentJson);
        }

        GovernedConfigurationSnapshotRow row = new()
        {
            SnapshotId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
            ObjectKind = objectKind,
            ObjectId = objectId,
            Version = version,
            ContentJson = contentJson,
            ContentSha256 = Sha256(contentJson),
            FrozenAt = frozenAt
        };
        _context.Set<GovernedConfigurationSnapshotRow>().Add(row);
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException failure) when (IsVersionAlreadyFrozen(failure, row))
        {
            _context.Entry(row).State = EntityState.Detached;
            GovernedConfigurationSnapshotRow taken = await _context.Set<GovernedConfigurationSnapshotRow>()
                .AsNoTracking()
                .SingleAsync(
                    candidate => candidate.ObjectKind == objectKind
                        && candidate.ObjectId == objectId
                        && candidate.Version == version,
                    cancellationToken);
            return SameContentOrConflict(taken, contentJson, failure);
        }
        return Project(row);
    }

    /// <summary>一版有且只有一份内容：一样就是重投，不一样就是撞号。</summary>
    private static GovernedConfigurationSnapshot SameContentOrConflict(
        GovernedConfigurationSnapshotRow frozen,
        string contentJson,
        Exception? innerException = null) =>
        string.Equals(frozen.ContentJson, contentJson, StringComparison.Ordinal)
            ? Project(frozen)
            : throw new GovernedSnapshotVersionConflictException(
                frozen.ObjectKind,
                frozen.ObjectId,
                frozen.Version,
                frozen.SnapshotId,
                frozen.ContentSha256,
                Sha256(contentJson),
                innerException);

    /// <summary>
    /// 这次插入是不是被 <c>(ObjectKind, ObjectId, Version)</c> 的唯一索引挡下的。
    /// </summary>
    /// <remarks>
    /// SQLite 用 <c>SQLITE_CONSTRAINT</c>（19）报约束冲突。只认与本次插入同一行的失败，别的写入失败照常
    /// 抛出去——把一次磁盘错误当成撞号会让重试转到天亮。
    /// </remarks>
    private static bool IsVersionAlreadyFrozen(DbUpdateException failure, GovernedConfigurationSnapshotRow row) =>
        failure.InnerException is SqliteException { SqliteErrorCode: SqliteConstraintErrorCode }
        && failure.Entries.Any(entry => ReferenceEquals(entry.Entity, row));

    private const int SqliteConstraintErrorCode = 19;

    public async Task<GovernedConfigurationSnapshot?> ReadAsync(
        GovernedObjectKind objectKind,
        string objectId,
        long version,
        CancellationToken cancellationToken)
    {
        GovernedConfigurationSnapshotRow? row = await _context.Set<GovernedConfigurationSnapshotRow>()
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate => candidate.ObjectKind == objectKind
                    && candidate.ObjectId == objectId
                    && candidate.Version == version,
                cancellationToken);
        return row is null ? null : Project(row);
    }

    public async Task<IReadOnlyList<ConfigurationFieldDifference>> DiffAsync(
        GovernedObjectKind objectKind,
        string objectId,
        long leftVersion,
        long rightVersion,
        CancellationToken cancellationToken)
    {
        GovernedConfigurationSnapshot left =
            await ReadAsync(objectKind, objectId, leftVersion, cancellationToken)
            ?? throw new InvalidOperationException(
                FormattableString.Invariant($"No frozen snapshot for {objectKind} {objectId} v{leftVersion}."));
        GovernedConfigurationSnapshot right =
            await ReadAsync(objectKind, objectId, rightVersion, cancellationToken)
            ?? throw new InvalidOperationException(
                FormattableString.Invariant($"No frozen snapshot for {objectKind} {objectId} v{rightVersion}."));

        return ConfigurationSnapshotDiff.Compute(left.ContentJson, right.ContentJson);
    }

    public async Task<string> WriteBusinessAsync(
        GovernanceAuditEntry entry,
        DateTimeOffset recordedAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);

        BusinessAuditRecordRow row = new()
        {
            AuditRecordId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
            RecordedAt = recordedAt,
            RecordedAtUtcTicks = recordedAt.UtcTicks,
            ActorIdentity = _deploymentIdentity.Value,
            ActorAttribution = AuditActorAttribution.NotAttributableToNaturalPerson,
            Action = entry.Action,
            ObjectKind = entry.ObjectKind,
            ObjectId = entry.ObjectId,
            Version = entry.Version,
            Outcome = entry.Outcome,
            SnapshotId = entry.SnapshotId,
            DetailJson = entry.DetailJson
        };
        _context.Set<BusinessAuditRecordRow>().Add(row);
        await _context.SaveChangesAsync(cancellationToken);
        return row.AuditRecordId;
    }

    public async Task<string> WriteAdministratorAsync(
        GovernanceAuditEntry entry,
        DateTimeOffset recordedAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);

        AdministratorAuditRecordRow row = new()
        {
            AuditRecordId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
            RecordedAt = recordedAt,
            RecordedAtUtcTicks = recordedAt.UtcTicks,
            ActorIdentity = _deploymentIdentity.Value,
            ActorAttribution = AuditActorAttribution.NotAttributableToNaturalPerson,
            ClaimedAdministratorRole = entry.ClaimedAdministratorRole,
            Action = entry.Action,
            ObjectKind = entry.ObjectKind,
            ObjectId = entry.ObjectId,
            Version = entry.Version,
            Outcome = entry.Outcome,
            SnapshotId = entry.SnapshotId,
            DetailJson = entry.DetailJson
        };
        _context.Set<AdministratorAuditRecordRow>().Add(row);
        await _context.SaveChangesAsync(cancellationToken);
        return row.AuditRecordId;
    }

    public async Task<GovernedVersionView?> ReadVersionAsync(
        GovernedObjectKind objectKind,
        string objectId,
        long version,
        CancellationToken cancellationToken)
    {
        GovernedConfigurationSnapshot? snapshot =
            await ReadAsync(objectKind, objectId, version, cancellationToken);
        if (snapshot is null)
        {
            return null;
        }

        GovernanceAuditView[] business = await _context.Set<BusinessAuditRecordRow>()
            .AsNoTracking()
            .Where(row => row.ObjectKind == objectKind && row.ObjectId == objectId && row.Version == version)
            // Ordered on ticks, not on RecordedAt: SQLite cannot ORDER BY a DateTimeOffset.
            .OrderBy(row => row.RecordedAtUtcTicks)
            .ThenBy(row => row.AuditRecordId)
            .Select(row => new GovernanceAuditView(
                row.AuditRecordId,
                row.RecordedAt,
                row.ActorIdentity,
                row.ActorAttribution,
                row.Action,
                row.ObjectKind,
                row.ObjectId,
                row.Version,
                row.Outcome,
                row.SnapshotId,
                row.DetailJson))
            .ToArrayAsync(cancellationToken);
        GovernanceAuditView[] administrator = await _context.Set<AdministratorAuditRecordRow>()
            .AsNoTracking()
            .Where(row => row.ObjectKind == objectKind && row.ObjectId == objectId && row.Version == version)
            // Ordered on ticks, not on RecordedAt: SQLite cannot ORDER BY a DateTimeOffset.
            .OrderBy(row => row.RecordedAtUtcTicks)
            .ThenBy(row => row.AuditRecordId)
            .Select(row => new GovernanceAuditView(
                row.AuditRecordId,
                row.RecordedAt,
                row.ActorIdentity,
                row.ActorAttribution,
                row.Action,
                row.ObjectKind,
                row.ObjectId,
                row.Version,
                row.Outcome,
                row.SnapshotId,
                row.DetailJson))
            .ToArrayAsync(cancellationToken);

        return new GovernedVersionView(snapshot, business, administrator);
    }

    /// <summary>审计保留期配置被采用时写的那条管理员审计的动作名。</summary>
    public const string RetentionPolicyConfiguredAction = "AUDIT_RETENTION_POLICY_CONFIGURED";

    /// <summary>
    /// REQ-0271 后半句：超过 180 天之后的保留由系统管理员配置，「变更本身必须形成管理员操作审计记录」。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 保留期是启动配置（<c>Governance:auditRetentionDays</c>），新值在服务起来的那一刻生效，所以在启动时比一次：
    /// 与最近一条这类审计记下的天数不同、或者从来没记过，就写一条管理员审计，带上前后两个值；相同就什么都不写——
    /// 重启不是变更。返回是否写了。
    /// </para>
    /// <para>
    /// 本期没有人员认证，记下的是部署身份并标注不可归属自然人；<c>ClaimedAdministratorRole</c> 留空，因为没有任何
    /// 一条消息声称过角色。改配置的人是谁，这条记录回答不了，它回答的是「这个值从哪一刻起生效、之前是多少」。
    /// </para>
    /// </remarks>
    public async Task<bool> RecordRetentionPolicyAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        double configuredDays = _context.AuditRetention.RetainFor.TotalDays;
        AdministratorAuditRecordRow? last = await _context.Set<AdministratorAuditRecordRow>()
            .AsNoTracking()
            .Where(row => row.Action == RetentionPolicyConfiguredAction)
            // Ordered on ticks, not on RecordedAt: SQLite cannot ORDER BY a DateTimeOffset.
            .OrderByDescending(row => row.RecordedAtUtcTicks)
            .ThenByDescending(row => row.AuditRecordId)
            .FirstOrDefaultAsync(cancellationToken);
        double? previousDays = null;
        if (last is not null)
        {
            using JsonDocument detail = JsonDocument.Parse(last.DetailJson);
            previousDays = detail.RootElement.GetProperty("retainForDays").GetDouble();
        }
        if (previousDays == configuredDays)
        {
            return false;
        }

        await WriteAdministratorAsync(
            new GovernanceAuditEntry(
                RetentionPolicyConfiguredAction,
                GovernedObjectKind.AuditRetention,
                "audit-retention",
                null,
                GovernanceActionOutcome.Succeeded,
                JsonSerializer.Serialize(new
                {
                    retainForDays = configuredDays,
                    previousRetainForDays = previousDays,
                    floorDays = AuditRetentionPolicy.Default.RetainFor.TotalDays,
                    configurationKey = "Governance:auditRetentionDays"
                })),
            now,
            cancellationToken);
        return true;
    }

    /// <summary>
    /// Removes audit records that are past retention. Records still inside it are refused by
    /// <see cref="AuditImmutabilityGuard"/>, so this cannot become a way to erase live audit.
    /// </summary>
    public async Task<int> PurgeExpiredAuditAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        // Filtered on the ticks column, not on RecordedAt: EF cannot translate a DateTimeOffset
        // comparison against SQLite, and loading the whole table to filter in memory would read 180
        // days of audit in order to delete a handful of rows.
        long cutoffTicks = _context.AuditRetention.CutoffFor(now).UtcTicks;
        List<BusinessAuditRecordRow> business = await _context.Set<BusinessAuditRecordRow>()
            .Where(row => row.RecordedAtUtcTicks <= cutoffTicks)
            .ToListAsync(cancellationToken);
        List<AdministratorAuditRecordRow> administrator = await _context.Set<AdministratorAuditRecordRow>()
            .Where(row => row.RecordedAtUtcTicks <= cutoffTicks)
            .ToListAsync(cancellationToken);
        if (business.Count == 0 && administrator.Count == 0)
        {
            return 0;
        }

        _context.Set<BusinessAuditRecordRow>().RemoveRange(business);
        _context.Set<AdministratorAuditRecordRow>().RemoveRange(administrator);
        await _context.SaveChangesAsync(cancellationToken);
        return business.Count + administrator.Count;
    }

    private static GovernedConfigurationSnapshot Project(GovernedConfigurationSnapshotRow row) =>
        new(row.SnapshotId, row.ObjectKind, row.ObjectId, row.Version, row.ContentJson, row.ContentSha256, row.FrozenAt);

    private static string Sha256(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
}

/// <summary>
/// Field-level differences, computed from two frozen snapshots. Deliberately has no persistence of
/// its own.
/// </summary>
public static class ConfigurationSnapshotDiff
{
    public static IReadOnlyList<ConfigurationFieldDifference> Compute(string leftJson, string rightJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leftJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(rightJson);

        Dictionary<string, string?> left = Flatten(leftJson);
        Dictionary<string, string?> right = Flatten(rightJson);
        List<ConfigurationFieldDifference> differences = [];
        foreach (string path in left.Keys.Union(right.Keys).Order(StringComparer.Ordinal))
        {
            left.TryGetValue(path, out string? leftValue);
            right.TryGetValue(path, out string? rightValue);
            bool leftPresent = left.ContainsKey(path);
            bool rightPresent = right.ContainsKey(path);
            if (leftPresent && rightPresent && string.Equals(leftValue, rightValue, StringComparison.Ordinal))
            {
                continue;
            }
            differences.Add(new ConfigurationFieldDifference(path, leftValue, rightValue));
        }
        return differences;
    }

    private static Dictionary<string, string?> Flatten(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        Dictionary<string, string?> flattened = new(StringComparer.Ordinal);
        Walk(document.RootElement, string.Empty, flattened);
        return flattened;
    }

    private static void Walk(JsonElement element, string path, Dictionary<string, string?> into)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    Walk(property.Value, path.Length == 0 ? property.Name : $"{path}.{property.Name}", into);
                }
                break;
            case JsonValueKind.Array:
                int index = 0;
                foreach (JsonElement item in element.EnumerateArray())
                {
                    Walk(item, FormattableString.Invariant($"{path}[{index}]"), into);
                    index++;
                }
                break;
            case JsonValueKind.Null:
                into[path] = null;
                break;
            default:
                into[path] = element.ToString();
                break;
        }
    }
}
