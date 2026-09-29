namespace ControlServer.Infrastructure.Persistence;

// 批次 8 建表票 control-server#386 的行：占有记录、站点独占与它的经过、等待点登记。占有记录只经 VehiclePurposeClaimWrites
// 写（账本端口与引擎的受理、释放都走它，批次8-16 control-server#387）；另两类只经 StationExclusivityStore 与 WaitingPointRegistry。

/// <summary>
/// 一次用途占有的经过（<c>REQ-0297</c>）：取得时插入，释放时写上 <see cref="ReleasedAt"/> 与 <see cref="ReleaseReason"/>，从不删除。
/// </summary>
/// <remarks>
/// <para>
/// 它是占有历史的载体（批次8-16，control-server#387 删了租约表，已释放的租约行在那次迁移里搬进来，原因记
/// <c>DISPATCH_LEASE_RELEASED</c>）。脚本读「占用已释放」读这里的 <see cref="ReleasedAt"/>。
/// </para>
/// <para>
/// 它是证据，不是仲裁者：谁占着车只由 <c>VehiclePurposeClaims</c> 的主键决定，这里的 <c>VehicleKey</c> 索引故意不唯一。
/// 两个仲裁者一旦说法不一——比如一条路径删了占有行却没关记录——第二个会把车永久挡住（control-server#394 审查必修 1）。
/// </para>
/// <para>
/// 批次8-16 的迁移在把引擎改走同一条写入路径时回填：每条当时的占有行补一条开着的「取得」，并关掉占有行已不在的开着的记录。
/// 从那以后占有行与它开着的记录同一次保存生灭，不存在开着却没有占有行的记录。
/// </para>
/// </remarks>
public sealed class VehiclePurposeClaimRecordRow
{
    public required string RecordId { get; set; }
    public required string VehicleKey { get; set; }
    public required string Purpose { get; set; }

    /// <summary>持有者：占着车的那趟旅程。</summary>
    public required string JourneyId { get; set; }

    public DateTimeOffset AcquiredAt { get; set; }
    public DateTimeOffset? ReleasedAt { get; set; }
    public string? ReleaseReason { get; set; }
}

/// <summary>
/// 站点独占（规格 5.4）：一站一行，两种状态（在途预占、在点占用），释放即删行；经过在 <see cref="StationExclusivityRecordRow"/>。
/// </summary>
/// <remarks>
/// 主键 <c>(MapId, StationId)</c>：谁占到由主键冲突决定，不先读后写。等待点与 <c>REQ-0204</c> 固定公共站点共用这一张表，
/// 由 <see cref="StationKind"/> 区分。<see cref="State"/> 与 <see cref="StationKind"/> 由 CHECK 约束只收各自的值。
/// </remarks>
public sealed class StationExclusivityRow
{
    public int MapId { get; set; }

    /// <summary>RIoT 站号。</summary>
    public int StationId { get; set; }

    public required string StationKind { get; set; }
    public required string State { get; set; }
    public required string VehicleKey { get; set; }

    /// <summary>持有者：取得这个独占的那次用途占有的旅程。</summary>
    public required string JourneyId { get; set; }

    /// <summary>进入当前状态的时刻。</summary>
    public DateTimeOffset StateSince { get; set; }

    /// <summary>所依据的等待点登记版本；固定公共站点与充电桩为空。</summary>
    public long? WaitingPointVersion { get; set; }

    /// <summary>这一次独占的经过是哪一条 <see cref="StationExclusivityRecordRow"/>。</summary>
    public required string RecordId { get; set; }

    /// <summary>
    /// 这次预占所依据的充电桩名册版本；只有 <c>CHARGER</c> 有（批次 9 建表票 control-server#399）。放在最后一列：它是那次迁移
    /// 手写重建时追加的，前面各列的位置不变。
    /// </summary>
    public long? ChargerRosterVersion { get; set; }
}

/// <summary>
/// 站点独占的一次经过：何时预占、何时到点、何时为什么放（<c>REQ-0293</c> 的离点证据写在 <see cref="ReleaseReason"/>）。从不删除。
/// </summary>
/// <remarks>同 <see cref="VehiclePurposeClaimRecordRow"/>，是证据不是仲裁者：谁占着站只由 <see cref="StationExclusivityRow"/> 的主键决定。</remarks>
public sealed class StationExclusivityRecordRow
{
    public required string RecordId { get; set; }
    public int MapId { get; set; }
    public int StationId { get; set; }
    public required string StationKind { get; set; }
    public required string VehicleKey { get; set; }
    public required string JourneyId { get; set; }
    public long? WaitingPointVersion { get; set; }

    /// <summary>预占的时刻；已在点的车直接占用时为空。</summary>
    public DateTimeOffset? ReservedAt { get; set; }

    public DateTimeOffset? OccupiedAt { get; set; }
    public DateTimeOffset? ReleasedAt { get; set; }
    public string? ReleaseReason { get; set; }

    /// <summary>同 <see cref="StationExclusivityRow.ChargerRosterVersion"/>。</summary>
    public long? ChargerRosterVersion { get; set; }
}

/// <summary>等待点登记的一个版本（受治理配置，照每区派车参数）。只追加，写入即不可改。</summary>
public sealed class WaitingPointVersionRow
{
    /// <summary>单调递增，从 1 开始。</summary>
    public long Version { get; set; }

    public required string ContentSha256 { get; set; }
    public string? SnapshotId { get; set; }
    public DateTimeOffset LoadedAt { get; set; }
    public required string Source { get; set; }
}

/// <summary>某一版登记里的一个等待点。主键 <c>(Version, MapId, StationId)</c>。</summary>
public sealed class WaitingPointRow
{
    public long Version { get; set; }
    public int MapId { get; set; }

    /// <summary>RIoT 站号。</summary>
    public int StationId { get; set; }

    public required string StationName { get; set; }
    public bool Enabled { get; set; }
}

/// <summary>
/// 某一版登记里某个等待点的 <c>WaitingPointVehicleScope</c> 白名单的一辆车。一个等待点没有任何一行，即同图全部车辆开放（<c>REQ-0289</c>）。
/// </summary>
public sealed class WaitingPointVehicleScopeRow
{
    public long Version { get; set; }
    public int MapId { get; set; }
    public int StationId { get; set; }
    public required string VehicleKey { get; set; }
}
