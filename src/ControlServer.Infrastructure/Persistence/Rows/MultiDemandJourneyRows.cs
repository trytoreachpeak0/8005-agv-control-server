using ControlServer.Application;

namespace ControlServer.Infrastructure.Persistence;

/// <summary>
/// 一趟旅程里的一个停靠（规格 5.2，REQ-0189）。批次 7 建表票 control-server#206 建表，读者由批次7-03（control-server#208）起接入。
/// </summary>
/// <remarks>
/// <para>
/// 主键是稳定身份 <see cref="StopId"/>，<b>序号不当主键</b>：MVP 拿序号当主键，途中追加一插队，后面每个停靠的身份都跟着变。
/// <see cref="Sequence"/> 是可变的序位。
/// </para>
/// <para>
/// 今天的单需求旅程恰好两个停靠：取货停靠（<see cref="JourneyStopRoles.Pickup"/>）与卸货停靠（<see cref="JourneyStopRoles.Unload"/>，
/// 即今天的关卡）。旅程行上今天的 13 个派生 id 里，属于停靠的那 9 个原样搬到这里（取货停靠：车辆业务状态、清单、计划、
/// 录入请求、离站核验消息与核验 id；卸货停靠：车辆业务状态、清单、计划），另 4 个属于需求，在 <see cref="JourneyDemandRow"/>。
/// 两个停靠的 <see cref="OperationSessionId"/> 都取旅程行上的原值——今天取货与关卡共用一个作业会话。
/// </para>
/// <para>
/// <see cref="Status"/> 在本票只在受理与迁移回填时写（受理时两个停靠都是 <see cref="JourneyStopStatuses.Pending"/>），引擎本票不推进它；
/// 在批次7-03 接管之前，旅程走到哪一步仍以 <c>JourneyRuntimes.Stage</c> 为准。
/// </para>
/// </remarks>
public sealed class JourneyStopRow
{
    public required string StopId { get; set; }
    public required string JourneyId { get; set; }
    public int Sequence { get; set; }
    public required string StopRole { get; set; }
    public required string StationId { get; set; }
    public int StationRiotId { get; set; }
    public required string DispatchZone { get; set; }
    public required string OperationSessionId { get; set; }
    public required string MovementLegId { get; set; }
    public required string UpperId { get; set; }
    public required string VehicleBusinessMessageId { get; set; }
    public required string WorklistMessageId { get; set; }
    public required string PlanMessageId { get; set; }

    /// <summary>取货停靠的批次录入请求；卸货停靠为空。</summary>
    public string? SublotRequestMessageId { get; set; }

    /// <summary>离开本停靠之前的离站核验请求消息；今天只有取货停靠有。</summary>
    public string? DepartureSafetyCheckMessageId { get; set; }

    /// <summary>离开本停靠之前的离站核验 id；今天只有取货停靠有。</summary>
    public string? DepartureSafetyCheckId { get; set; }

    public required string Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// 一条需求归属于一趟旅程（规格 5.2，REQ-0189、REQ-0195～0197）。主键 <c>(JourneyId, DemandId)</c>；同一需求至多一条未移除的归属，
/// 由 <c>DemandId</c> 上以 <c>RemovedAt IS NULL</c> 过滤的唯一索引保证。
/// </summary>
/// <remarks>
/// 旅程行上属于需求的 4 个派生 id（装与卸的 attempt id 与命令消息 id）原样搬到这里。<see cref="DispatchGeneration"/> 是按需求的派车代次，
/// 回填取旅程行上的值；批次7-10（control-server#215）改派时靠它换新代次。<see cref="Status"/>、<see cref="LoadedSlotsJson"/>、
/// <see cref="RemovedAt"/>、<see cref="RemovalReason"/>、<see cref="DispatchZoneParameterVersion"/> 在本票只由受理与回填写或留空。
/// </remarks>
public sealed class JourneyDemandRow
{
    public required string JourneyId { get; set; }
    public required string DemandId { get; set; }
    public required string PickupStopId { get; set; }
    public required string UnloadStopId { get; set; }
    public int ExpectedBasketCount { get; set; }
    public required string TargetSlotsJson { get; set; }

    /// <summary>实际装进的仓位（JSON 数组），装货闭环之前为空。本票不写。</summary>
    public string? LoadedSlotsJson { get; set; }

    public required string LoadSlotOperationAttemptId { get; set; }
    public required string LoadCommandMessageId { get; set; }
    public required string UnloadSlotOperationAttemptId { get; set; }
    public required string UnloadCommandMessageId { get; set; }
    public required string DispatchZone { get; set; }
    public long DispatchGeneration { get; set; }
    public required string Status { get; set; }
    public DateTimeOffset AddedAt { get; set; }
    public DateTimeOffset? RemovedAt { get; set; }
    public string? RemovalReason { get; set; }

    /// <summary>途中追加这条需求时所用的每区派车参数版本；随初始派车进入旅程的需求为空。</summary>
    public long? DispatchZoneParameterVersion { get; set; }
}

/// <summary>
/// 车辆被哪种用途、哪趟旅程占着（规格 3.3 第 9 项、5.2，REQ-0290 的搬运一半）。批次 7 起它是车辆占用的权威。
/// </summary>
/// <remarks>
/// 主键 <c>VehicleKey</c>：一车一行，谁占到由主键冲突决定，不先读后写。释放即删除行；占用的历史由租约行的 <c>ReleasedAt</c> 承载。
/// 另两套占用（<c>VehicleDispatchLeases</c> 与 <c>OrderIntents.VehicleOccupancy*</c>）本批行为不变，批次 8 退役。
/// </remarks>
public sealed class VehiclePurposeClaimRow
{
    public required string VehicleKey { get; set; }
    public required string Purpose { get; set; }
    public required string JourneyId { get; set; }
    public DateTimeOffset ClaimedAt { get; set; }
}

/// <summary>
/// 一个业务键被终态抑制（REQ-0155、REQ-0156、REQ-0211）。主键 <c>TransportDemandKey</c>，只追加、不改写；写入在批次7-05（control-server#210）。
/// </summary>
public sealed class TransportDemandSuppressionRow
{
    public required string TransportDemandKey { get; set; }
    public required string DemandId { get; set; }
    public required string ReasonCode { get; set; }
    public DateTimeOffset SuppressedAt { get; set; }
}

/// <summary>
/// 每区派车参数的一个版本（REQ-0198、REQ-0203）。整张表一个版本，写入即不可改（<see cref="PublishedVersionImmutabilityGuard"/>）。
/// </summary>
/// <remarks>
/// <see cref="Source"/> 说这一版从哪来：经 <see cref="GovernedConfigurationPublisher"/> 正式导入的有 <see cref="SnapshotId"/>；
/// L2 编排器直写库预置的（<see cref="DispatchZoneParameterSources.L2Preset"/>）不经治理快照与审计，<see cref="SnapshotId"/> 为空。
/// </remarks>
public sealed class DispatchZoneParameterVersionRow
{
    /// <summary>单调递增，从 1 开始。</summary>
    public long Version { get; set; }

    public required string ContentSha256 { get; set; }
    public string? SnapshotId { get; set; }
    public DateTimeOffset LoadedAt { get; set; }
    public required string Source { get; set; }
}

/// <summary>
/// 某一版每区派车参数里一个分区的两个值。两列都可空，空即未配置；表里没有的分区同样是未配置。
/// </summary>
/// <remarks>
/// <see cref="EnRouteAdditionMaxPathCostIncrease"/> 的单位与批次7-06（control-server#211）的计划路径代价一致，今天是毫米——列名故意不写成时间。
/// <c>0</c> 与空都表示本区禁止途中追加，两者都存得下、读回可区分。<see cref="StarvationThresholdSeconds"/> 是秒。
/// </remarks>
public sealed class DispatchZoneParameterRow
{
    public long Version { get; set; }
    public required string DispatchZone { get; set; }
    public long? EnRouteAdditionMaxPathCostIncrease { get; set; }
    public long? StarvationThresholdSeconds { get; set; }
}

/// <summary>
/// 按车的快照修订号计数器（ADR-cross-0048）。三列对应车辆业务状态、清单、计划三条流。
/// </summary>
/// <remarks>
/// 本票里它的值恒等于 <c>JourneyRuntimes</c> 上该车三个修订列的最大值——也就是今天受理时推算下一趟基准所依据的那个值；
/// 受理事务在写旅程行的同一次保存里把它推进到新旅程行的三个值。读者由批次7-03（control-server#208）切换过来。
/// </remarks>
public sealed class VehicleSnapshotRevisionRow
{
    public required string AgvId { get; set; }
    public long VehicleBusinessRevision { get; set; }
    public long WorklistRevision { get; set; }
    public long PlanRevision { get; set; }
}
