namespace ControlServer.Application;

// 批次 7 建表票 control-server#206 的持久化端口。表在一次迁移里落齐，端口在这一个文件里一次给齐，批次7-02～7-12 只取用：
// 不改 Ports.cs，不再加迁移。每个写方法自己 SaveChanges；要与别的事实同一次提交，调用方在同一个上下文里先暂存那些事实
// （或在外面开事务）再调用——SaveChanges 保存的是上下文里全部暂存的改动。

/// <summary>旅程与停靠的身份规则。</summary>
/// <remarks>
/// 单需求旅程的 <c>JourneyId</c> 是 <c>journey:{DemandId}</c>：迁移回填用的是同一条 SQL 可算的规则，所以回填行与新受理的行不可区分。
/// </remarks>
public static class JourneyIdentity
{
    public const string Prefix = "journey:";

    public static string ForAnchorDemand(string demandId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(demandId);
        return Prefix + demandId;
    }

    public static string PickupStopId(string journeyId) => $"{journeyId}|{JourneyStopRoles.Pickup}";

    public static string UnloadStopId(string journeyId) => $"{journeyId}|{JourneyStopRoles.Unload}";
}

/// <summary>停靠的角色。</summary>
public static class JourneyStopRoles
{
    public const string Pickup = "PICKUP";
    public const string Unload = "UNLOAD";
}

/// <summary>停靠的状态：待到、进行、完成、移除。</summary>
public static class JourneyStopStatuses
{
    public const string Pending = "PENDING";
    public const string Active = "ACTIVE";
    public const string Completed = "COMPLETED";
    public const string Removed = "REMOVED";
}

/// <summary>从属需求的状态：待装、已装、已卸、已终结。</summary>
public static class JourneyDemandStatuses
{
    public const string PendingLoad = "PENDING_LOAD";
    public const string Loaded = "LOADED";
    public const string Unloaded = "UNLOADED";
    public const string Terminated = "TERMINATED";
}

/// <summary>车辆占有的用途。本批只有搬运一种（规格 3.3 第 9 项）；<c>REQ-0290</c> 的其余用途在批次 8。</summary>
public static class VehiclePurposes
{
    public const string Transport = "TRANSPORT";
}

/// <summary>每区派车参数版本从哪来。</summary>
public static class DispatchZoneParameterSources
{
    /// <summary>经 <see cref="IDispatchZoneParameterStore"/> 写入，带治理快照与业务审计。</summary>
    public const string GovernedImport = "GOVERNED_IMPORT";

    /// <summary>L2 编排器直写库预置，不经治理快照与审计（scripts/l2/L2DispatchZoneParameters.psm1）。</summary>
    public const string L2Preset = "L2_PRESET";
}

/// <summary>每区派车参数的治理对象身份与审计动作名。</summary>
public static class DispatchZoneParameterGovernance
{
    public const string ObjectId = "dispatch-zone-parameters";

    public const string VersionImportedAction = "DISPATCH_ZONE_PARAMETERS_VERSION_IMPORTED";
}

/// <summary>旅程里的一个停靠。</summary>
public sealed record JourneyStop(
    string StopId,
    string JourneyId,
    int Sequence,
    string StopRole,
    string StationId,
    int StationRiotId,
    string DispatchZone,
    string OperationSessionId,
    string MovementLegId,
    string UpperId,
    string VehicleBusinessMessageId,
    string WorklistMessageId,
    string PlanMessageId,
    string? SublotRequestMessageId,
    string? DepartureSafetyCheckMessageId,
    string? DepartureSafetyCheckId,
    string Status,
    DateTimeOffset CreatedAt);

/// <summary>一条需求在一趟旅程里的归属。</summary>
public sealed record JourneyDemand(
    string JourneyId,
    string DemandId,
    string PickupStopId,
    string UnloadStopId,
    int ExpectedBasketCount,
    IReadOnlyList<int> TargetSlots,
    IReadOnlyList<int>? LoadedSlots,
    string LoadSlotOperationAttemptId,
    string LoadCommandMessageId,
    string UnloadSlotOperationAttemptId,
    string UnloadCommandMessageId,
    string DispatchZone,
    long DispatchGeneration,
    string Status,
    DateTimeOffset AddedAt,
    DateTimeOffset? RemovedAt,
    string? RemovalReason,
    long? DispatchZoneParameterVersion);

/// <summary>停靠与从属需求的读写（规格 5.2）。</summary>
public interface IJourneyMembershipStore
{
    /// <summary>这条需求当前（未移除）归属的旅程，没有则为空。</summary>
    Task<string?> FindJourneyIdByDemandAsync(string demandId, CancellationToken cancellationToken);

    /// <summary>旅程的全部停靠，按序位排。</summary>
    Task<IReadOnlyList<JourneyStop>> ListStopsAsync(string journeyId, CancellationToken cancellationToken);

    /// <summary>旅程的从属需求；<paramref name="includeRemoved"/> 为假时只列未移除的。</summary>
    Task<IReadOnlyList<JourneyDemand>> ListDemandsAsync(
        string journeyId, bool includeRemoved, CancellationToken cancellationToken);

    Task AddStopAsync(JourneyStop journeyStop, CancellationToken cancellationToken);

    /// <summary>加一条归属；同一需求已有未移除的归属时由唯一索引拒绝。</summary>
    Task AddDemandAsync(JourneyDemand demand, CancellationToken cancellationToken);

    Task SetStopStatusAsync(string stopId, string status, CancellationToken cancellationToken);

    Task SetStopSequenceAsync(string stopId, int sequence, CancellationToken cancellationToken);

    /// <summary>把一条归属标记为移除（不删行）。已移除的再移除不改原记录。</summary>
    Task RemoveDemandAsync(
        string journeyId, string demandId, string removalReason, DateTimeOffset removedAt,
        CancellationToken cancellationToken);
}

/// <summary>车辆被哪种用途、哪趟旅程占着。</summary>
public sealed record VehiclePurposeClaim(string VehicleKey, string Purpose, string JourneyId, DateTimeOffset ClaimedAt);

/// <summary>车辆用途占有（规格 3.3 第 9 项、5.2）。谁占到由主键冲突决定，不先读后写。</summary>
public interface IVehiclePurposeClaimStore
{
    Task<VehiclePurposeClaim?> ReadAsync(string vehicleKey, CancellationToken cancellationToken);

    /// <summary>
    /// 为这趟旅程占住车辆。占到或这趟旅程本来就占着时为真；被别的旅程或用途占着时为假，现有占有不变。
    /// </summary>
    Task<bool> TryClaimAsync(VehiclePurposeClaim claim, CancellationToken cancellationToken);

    /// <summary>释放这趟旅程对车辆的占有；车辆没被它占着时什么也不做。</summary>
    Task ReleaseAsync(string vehicleKey, string journeyId, CancellationToken cancellationToken);
}

/// <summary>一个业务键的终态抑制。</summary>
public sealed record TransportDemandSuppression(
    string TransportDemandKey, string DemandId, string ReasonCode, DateTimeOffset SuppressedAt);

/// <summary>按业务键抑制（REQ-0155、REQ-0156、REQ-0211）。只追加、不改写，先写者胜。</summary>
public interface ITransportDemandSuppressionStore
{
    /// <summary>键没有抑制时写入这一条；已有时不改、不抛。返回键上成立的那一条。</summary>
    Task<TransportDemandSuppression> SuppressIfAbsentAsync(
        TransportDemandSuppression suppression, CancellationToken cancellationToken);

    Task<TransportDemandSuppression?> ReadAsync(string transportDemandKey, CancellationToken cancellationToken);
}

/// <summary>
/// 一个分区的两个派车参数，均可空，空即未配置。<see cref="EnRouteAdditionMaxPathCostIncrease"/> 的单位是计划路径代价（今天是毫米），
/// <c>0</c> 与空都表示本区禁止途中追加；<see cref="StarvationThresholdSeconds"/> 是秒。
/// </summary>
public sealed record DispatchZoneParameters(
    string DispatchZone, long? EnRouteAdditionMaxPathCostIncrease, long? StarvationThresholdSeconds);

/// <summary>每区派车参数的一个版本。表里没有的分区即未配置。</summary>
public sealed record DispatchZoneParameterTableVersion(
    long Version,
    string ContentSha256,
    string? SnapshotId,
    DateTimeOffset LoadedAt,
    string Source,
    IReadOnlyDictionary<string, DispatchZoneParameters> Zones);

/// <summary>每区派车参数（REQ-0198、REQ-0203）。版本行与子行只追加，写入即不可改。</summary>
public interface IDispatchZoneParameterStore
{
    /// <summary>写一个新版本（当前最大 + 1），经治理快照与业务审计。并发写入由主键冲突整体回滚一方。</summary>
    Task<DispatchZoneParameterTableVersion> WriteVersionAsync(
        IReadOnlyList<DispatchZoneParameters> zones, DateTimeOffset loadedAt, CancellationToken cancellationToken);

    /// <summary>当前（版本号最大的）版本；一版都没有即全部未配置，为空。</summary>
    Task<DispatchZoneParameterTableVersion?> ReadCurrentAsync(CancellationToken cancellationToken);

    Task<DispatchZoneParameterTableVersion?> ReadVersionAsync(long version, CancellationToken cancellationToken);
}

/// <summary>一辆车三条快照流的修订号。</summary>
public sealed record VehicleSnapshotRevisions(string AgvId, long VehicleBusiness, long Worklist, long Plan);

/// <summary>按车修订号计数器（ADR-cross-0048）。跨旅程单调。</summary>
public interface IVehicleSnapshotRevisionStore
{
    Task<VehicleSnapshotRevisions?> ReadAsync(string agvId, CancellationToken cancellationToken);

    /// <summary>把计数器推进到 <paramref name="revisions"/>；任何一条流往回走都拒绝，计数器不变。</summary>
    Task AdvanceAsync(VehicleSnapshotRevisions revisions, CancellationToken cancellationToken);
}
