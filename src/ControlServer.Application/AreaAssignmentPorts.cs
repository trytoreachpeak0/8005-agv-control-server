namespace ControlServer.Application;

/// <summary>
/// 分区归属表作为受治理对象时的固定名字。
/// </summary>
/// <remarks>
/// 整张表一个版本，不按地图分开发布（program#68 决议 2），所以快照、审计与需求冻结都用同一个
/// <see cref="ObjectId"/>，版本号就是表的版本号。
/// </remarks>
public static class DispatchZoneAreaAssignmentGovernance
{
    public const string ObjectId = "dispatch-zone-area-assignment";

    /// <summary>每导入一个版本写的业务审计动作名。</summary>
    public const string VersionImportedAction = "DISPATCH_ZONE_AREA_ASSIGNMENT_VERSION_IMPORTED";

    /// <summary>需求冻结指派版本时，在 <c>ConfigurationConsumerBindings</c> 里的消费者类别。</summary>
    public const string DemandConsumerKind = "TransportDemand";
}

/// <summary>
/// 一个 AREA 归入哪个分区，以及为它指派的唯一 SlotPosition 分组，即开门侧（REQ-0191、REQ-0349）。
/// </summary>
public sealed record AreaAssignment(string Area, string DispatchZone, string SlotPosition);

/// <summary>分区归属表的一个版本：版本号、冻结快照与整张表的内容，按 AREA 索引。</summary>
public sealed record AreaAssignmentTableVersion(
    long Version,
    string ContentSha256,
    string SnapshotId,
    DateTimeOffset ImportedAt,
    IReadOnlyDictionary<string, AreaAssignment> ByArea);

/// <summary>
/// 分区归属表的存取（REQ-0350）。只存取，不校验业务内容：侧取值、区域号格式、分区是否存在由导入票
/// control-server#68 在调用前拦。
/// </summary>
public interface IAreaAssignmentStore
{
    /// <summary>当前生效的版本，即版本号最大的一版；从未导入过时为 <c>null</c>。</summary>
    Task<AreaAssignmentTableVersion?> ReadCurrentAsync(CancellationToken cancellationToken);

    /// <summary>按版本号读；该版本不存在时为 <c>null</c>。</summary>
    Task<AreaAssignmentTableVersion?> ReadVersionAsync(long version, CancellationToken cancellationToken);

    /// <summary>
    /// 把整张表写成一个新版本：先冻结快照、写业务审计，再落版本行与各条指派，全部在一个事务里。
    /// 已写入的版本不可改；回滚是把旧内容再写一次。
    /// </summary>
    Task<AreaAssignmentTableVersion> WriteVersionAsync(
        IReadOnlyList<AreaAssignment> assignments,
        DateTimeOffset importedAt,
        CancellationToken cancellationToken);
}

/// <summary>一条需求在分配目标仓位那一刻冻结的分区归属表版本。</summary>
public sealed record DemandAreaAssignmentFreeze(
    string DemandId,
    long Version,
    string SnapshotId,
    DateTimeOffset FrozenAt);

/// <summary>对已冻结的需求再冻结另一个版本时抛出：冻结之后的版本变化不改变这条需求（REQ-0350）。</summary>
public sealed class DemandAreaAssignmentFreezeConflictException : InvalidOperationException
{
    public DemandAreaAssignmentFreezeConflictException(string message)
        : base(message)
    {
    }

    public DemandAreaAssignmentFreezeConflictException()
        : base("The demand has already frozen a different area assignment version.")
    {
    }

    public DemandAreaAssignmentFreezeConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// 受理时分区归属表的当前版本已不是评估这条需求时用的版本：导入发生在评估与受理之间。受理整份不落，
/// 由受理方按候选事实已变处理，下一轮按新版本重判（control-server#72）。
/// </summary>
/// <remarks>
/// 不能照旧受理再冻结评估时的版本：选仓用的是那一版的开门侧，而现场此刻生效的是新版，装卸时可能按另一侧开门。
/// </remarks>
public sealed class AreaAssignmentVersionChangedException : InvalidOperationException
{
    public AreaAssignmentVersionChangedException(string message)
        : base(message)
    {
    }

    public AreaAssignmentVersionChangedException()
        : base("The area assignment version changed between evaluating the demand and accepting it.")
    {
    }

    public AreaAssignmentVersionChangedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// 需求冻结指派版本（REQ-0350）。写在批次 3 的通用冻结表 <c>ConfigurationConsumerBindings</c> 里，一需求一行。
/// </summary>
public interface IDemandAreaAssignmentFreeze
{
    /// <summary>
    /// 冻结。同一需求再冻结同一版本是幂等的，返回第一次冻结的记录；冻结不同版本抛
    /// <see cref="DemandAreaAssignmentFreezeConflictException"/>。版本不存在时抛 <see cref="InvalidOperationException"/>。
    /// </summary>
    Task<DemandAreaAssignmentFreeze> FreezeAsync(
        string demandId,
        long version,
        DateTimeOffset frozenAt,
        CancellationToken cancellationToken);

    /// <summary>该需求冻结的版本；尚未冻结时为 <c>null</c>。</summary>
    Task<DemandAreaAssignmentFreeze?> ReadAsync(string demandId, CancellationToken cancellationToken);

    /// <summary>
    /// 在途需求的冻结版本，供导入预览列出开门侧会变的需求。「在途」指旅程还没有进入
    /// <c>Completed</c>：没有旅程行的冻结也算在途，宁可多列不可漏列。
    /// </summary>
    Task<IReadOnlyList<DemandAreaAssignmentFreeze>> ListInFlightAsync(CancellationToken cancellationToken);
}

/// <summary>一条结构性派车阻断（REQ-0210）。<see cref="ClearedAt"/> 为空表示仍成立。</summary>
public sealed record StructuralDispatchBlock(
    string DemandId,
    string ReasonCode,
    string TransportDemandKey,
    DateTimeOffset FirstRaisedAt,
    DateTimeOffset LastSeenAt,
    DateTimeOffset? ClearedAt,
    string DetailJson);

/// <summary>
/// 结构性派车阻断的存取，按 <c>(DemandId, ReasonCode)</c> 去重。control-server#74 写、control-server#70 读。
/// </summary>
public interface IStructuralDispatchBlockStore
{
    /// <summary>
    /// 置位或刷新。仍成立的同一原因只更新 <c>LastSeenAt</c>，<c>FirstRaisedAt</c> 与明细不变；已清除的同一原因再次
    /// 成立时重新开始一段阻断：<c>FirstRaisedAt</c> 取这次、清除时间置空、明细取这次。
    /// </summary>
    Task<StructuralDispatchBlock> RaiseOrRefreshAsync(
        string demandId,
        string reasonCode,
        string transportDemandKey,
        string detailJson,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken);

    /// <summary>清除仍成立的阻断；没有这条或已清除时返回 <c>false</c>。</summary>
    Task<bool> ClearAsync(
        string demandId,
        string reasonCode,
        DateTimeOffset clearedAt,
        CancellationToken cancellationToken);

    /// <summary>全部仍成立的阻断，按首次成立时间排序。</summary>
    Task<IReadOnlyList<StructuralDispatchBlock>> ListUnclearedAsync(CancellationToken cancellationToken);
}

/// <summary>车辆绑的是哪个整车模型，是从哪份服务端记录解析出来的。</summary>
public enum VehicleSlotPositionSource
{
    /// <summary>该车的生效配置 <c>ActiveSlotConfigurations</c> 引用的模型。</summary>
    ActiveSlotConfiguration,

    /// <summary>没有生效配置时，该车最新一版已发布 IO 绑定引用的模型。</summary>
    LatestPublishedIoBinding
}

/// <summary>一台车各物理仓位的 SlotPosition 分组，取服务端权威，不取车报（program#70 定案 4）。</summary>
public sealed record VehicleSlotPositions(
    string AgvId,
    string SlotModelVersionId,
    VehicleSlotPositionSource Source,
    IReadOnlyDictionary<int, string> SlotPositionByPhysicalSlot)
{
    /// <summary>各分组的物理仓位数。</summary>
    public IReadOnlyDictionary<string, int> PhysicalSlotCountByGroup { get; } = SlotPositionByPhysicalSlot
        .GroupBy(pair => pair.Value, StringComparer.Ordinal)
        .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
}

/// <summary>
/// 一组车里某分组物理仓位数的最大值（REQ-0352 的结构性判据）。解析不出模型的车不计入，列在
/// <see cref="UnresolvedAgvIds"/> 里；一台都解析不出时最大值为 0。
/// </summary>
public sealed record SlotPositionGroupCapacity(
    string SlotPosition,
    int LargestPhysicalSlotCount,
    IReadOnlyList<string> UnresolvedAgvIds);

/// <summary>
/// 读车辆的仓位分组。解析顺序固定：生效配置引用的模型 → 最新已发布 IO 绑定引用的模型 → 未解析。
/// </summary>
/// <remarks>
/// 不回退到已批准八仓事实的默认模型，也不按仓号区间推断：那会把「这台车是什么模型」变成隐含默认，将来出现不同车型时
/// 悄悄出错。未解析由调用方 fail-closed。
/// </remarks>
public interface IVehicleSlotPositionReader
{
    /// <summary>该车的仓位分组；两份记录都没有时为 <c>null</c>，即未解析。</summary>
    Task<VehicleSlotPositions?> ReadAsync(string agvId, CancellationToken cancellationToken);

    /// <summary>
    /// 给定同图全部车辆，求某分组物理仓位数的最大值。
    /// </summary>
    Task<SlotPositionGroupCapacity> ReadLargestGroupCapacityAsync(
        IReadOnlyCollection<string> agvIds,
        string slotPosition,
        CancellationToken cancellationToken);
}
