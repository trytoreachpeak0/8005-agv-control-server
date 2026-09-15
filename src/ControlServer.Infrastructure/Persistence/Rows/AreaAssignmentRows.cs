namespace ControlServer.Infrastructure.Persistence;

/// <summary>
/// 分区归属表的一个版本（REQ-0191、REQ-0350）。整张表一个版本，不按地图分版本（program#68 决议 2）。
/// </summary>
/// <remarks>
/// 内容冻结在 <see cref="SnapshotId"/> 指向的 <c>GovernedConfigurationSnapshots</c> 行里，这里只记版本号与摘要，
/// 让派车按版本号取指派时不必每次解析快照 JSON。写入即不可改（<see cref="PublishedVersionImmutabilityGuard"/>），
/// 回滚是把旧内容再导入成一个新版本。
/// </remarks>
public sealed class DispatchZoneAreaAssignmentVersionRow
{
    /// <summary>单调递增，从 1 开始。</summary>
    public long Version { get; set; }

    public required string ContentSha256 { get; set; }
    public required string SnapshotId { get; set; }
    public int EntryCount { get; set; }
    public DateTimeOffset ImportedAt { get; set; }
}

/// <summary>
/// 某一版分区归属表里的一条：一个 AREA 归入哪个分区，并指派唯一一个 SlotPosition 分组（REQ-0349）。
/// </summary>
/// <remarks>
/// 开门侧是本表的必填列而不是另一张表（program#68 决议 1）：两张同键的区域号表要保持同步，只会多出「两表对不上」
/// 一类错误。未映射的 AREA 不在表里，按 REQ-0191 静默跳过。
/// </remarks>
public sealed class DispatchZoneAreaAssignmentRow
{
    public long Version { get; set; }
    public required string Area { get; set; }
    public required string DispatchZone { get; set; }
    public required string SlotPosition { get; set; }
}

/// <summary>
/// 一条需求没有任何潜在合法车辆时的结构性派车阻断（REQ-0210），按任务与原因去重。
/// </summary>
/// <remarks>
/// 同一原因持续成立只刷新 <see cref="LastSeenAt"/>，不按每轮调度新建一行；原因消失时记 <see cref="ClearedAt"/>，
/// 行留着作为这段阻断的记录。
/// </remarks>
public sealed class StructuralDispatchBlockRow
{
    public required string DemandId { get; set; }
    public required string ReasonCode { get; set; }
    public required string TransportDemandKey { get; set; }
    public DateTimeOffset FirstRaisedAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public DateTimeOffset? ClearedAt { get; set; }
    public required string DetailJson { get; set; }
}
