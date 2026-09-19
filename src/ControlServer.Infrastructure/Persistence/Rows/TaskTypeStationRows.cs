namespace ControlServer.Infrastructure.Persistence;

/// <summary>
/// 任务类型规则表的一个版本（REQ-0343）：整张表一个版本号。内容冻结在 <see cref="SnapshotId"/> 指向的快照里，
/// 写入即不可改（<see cref="PublishedVersionImmutabilityGuard"/>）。
/// </summary>
public sealed class TaskTypeStationRuleVersionRow
{
    /// <summary>单调递增，从 1 开始，也是快照的版本号。</summary>
    public long Version { get; set; }

    public required string ContentSha256 { get; set; }
    public required string SnapshotId { get; set; }
    public DateTimeOffset LoadedAt { get; set; }

    /// <summary>这一版从哪里来，例如 <c>preset:task-type-stations.settings.json</c>。</summary>
    public required string Source { get; set; }
}

/// <summary>某一版规则表里的一行：一个任务类型的固定端。没有功能列（规格 5.3）。</summary>
public sealed class TaskTypeStationRuleRow
{
    public long Version { get; set; }
    public required string TaskType { get; set; }

    /// <summary><c>ORIGIN</c> 或 <c>DESTINATION</c>。</summary>
    public required string FixedEnd { get; set; }
}

/// <summary>
/// 一张图的绑定集版本（REQ-0337）：每张 Map 一条完整、不可变的版本线。
/// </summary>
public sealed class TaskTypeStationBindingSetVersionRow
{
    public int MapId { get; set; }

    /// <summary>按图各自从 1 编号，也是快照 <c>map-&lt;mapId&gt;</c> 的版本号。</summary>
    public long Version { get; set; }

    /// <summary>这一版依赖的规则版本。</summary>
    public long RuleVersion { get; set; }

    public required string ContentSha256 { get; set; }
    public required string SnapshotId { get; set; }

    /// <summary>写入时据以核验站点的目录修订；没有核验时为空。</summary>
    public long? CatalogRevision { get; set; }

    public DateTimeOffset LoadedAt { get; set; }
    public required string Source { get; set; }
}

/// <summary>某一版绑定集里的一个绑定：一个任务类型在这张图上的固定站（REQ-0334、REQ-0338）。</summary>
/// <remarks>
/// 唯一索引 <c>(MapId, Version, StationRiotId)</c> 在数据库层面挡住同一 Station 被两个任务类型绑定。
/// </remarks>
public sealed class TaskTypeStationBindingRow
{
    public int MapId { get; set; }
    public long Version { get; set; }
    public required string TaskType { get; set; }
    public int StationRiotId { get; set; }
    public required string StationName { get; set; }

    /// <summary>现场用途核对记录的引用。</summary>
    public required string SiteVerificationRef { get; set; }
}

/// <summary>
/// 某一版绑定集的需求集：本图获准启用、需要固定站的任务类型（<c>MapPublicStationRequirementSet</c>，REQ-0335）。
/// </summary>
public sealed class TaskTypeStationRequirementRow
{
    public int MapId { get; set; }
    public long Version { get; set; }
    public required string TaskType { get; set; }
}

/// <summary>一张图当前生效的绑定集版本指针。</summary>
/// <remarks>
/// <see cref="State"/> 与 <see cref="PendingVersion"/> 给批次6-05 的「结果未知时不猜生效版本」（REQ-0347）用；
/// 本票只写 <c>ACTIVE</c>。
/// </remarks>
public sealed class TaskTypeStationActiveBindingSetRow
{
    public int MapId { get; set; }
    public long? ActiveVersion { get; set; }
    public required string State { get; set; }
    public long? PendingVersion { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// 按 <c>Map + TASK_TYPE</c> 的一条暂停（REQ-0340）。同一 <c>(MapId, TaskType)</c> 可同时有多条未解除暂停（人工与目录变化
/// 各自成立），不自动到期，解除只记 <see cref="ReleasedAt"/>，不删行。
/// </summary>
public sealed class TaskTypeStationHoldRow
{
    public required string HoldId { get; set; }
    public int MapId { get; set; }
    public required string TaskType { get; set; }

    /// <summary><c>MANUAL</c> 或 <c>CATALOG_CHANGE</c>。</summary>
    public required string Source { get; set; }

    public required string ReasonCode { get; set; }
    public required string DetailJson { get; set; }
    public DateTimeOffset RaisedAt { get; set; }
    public required string RaisedBy { get; set; }
    public DateTimeOffset? ReleasedAt { get; set; }
    public string? ReleasedBy { get; set; }
}

/// <summary>
/// 一条目录变化记录（REQ-0341、REQ-0342），按 <c>(MapId, StationRiotId, CatalogRevision)</c> 去重。分类由批次6-06 做。
/// </summary>
public sealed class TaskTypeStationCatalogChangeRow
{
    public required string ChangeId { get; set; }
    public int MapId { get; set; }
    public int StationRiotId { get; set; }
    public required string PreviousStationName { get; set; }

    /// <summary>站点被删时为空。</summary>
    public string? CurrentStationName { get; set; }

    public required string ChangeKind { get; set; }
    public required string RiskClass { get; set; }
    public long CatalogRevision { get; set; }
    public DateTimeOffset ObservedAt { get; set; }

    /// <summary>受影响的任务类型，JSON 字符串数组。</summary>
    public required string AffectedTaskTypesJson { get; set; }

    /// <summary>这条变化置的暂停；没有置暂停时为空。</summary>
    public string? HoldId { get; set; }
}
