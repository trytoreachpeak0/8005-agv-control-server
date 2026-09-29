namespace ControlServer.Infrastructure.Persistence;

// 批次 8 建表票 control-server#386 的行：占有记录、站点独占与它的经过、等待点登记。本票没有运行时写入者，写它们的只有
// VehiclePurposeLedgerStore、StationExclusivityStore 与 WaitingPointRegistry。

/// <summary>
/// 一次用途占有的经过（<c>REQ-0297</c>）：取得时插入，释放时写上 <see cref="ReleasedAt"/> 与 <see cref="ReleaseReason"/>，从不删除。
/// </summary>
/// <remarks>
/// <para>
/// 它是占有历史的载体。今天那段历史由租约行的 <c>ReleasedAt</c> 承载，批次8-16（control-server#387）删租约表之后，脚本改读这里。
/// </para>
/// <para>
/// 它是证据，不是仲裁者：谁占着车只由 <c>VehiclePurposeClaims</c> 的主键决定，这里的 <c>VehicleKey</c> 索引故意不唯一。
/// 两个仲裁者一旦说法不一——比如一条路径删了占有行却没关记录——第二个会把车永久挡住（control-server#394 审查必修 1）。
/// </para>
/// <para>
/// 本票迁移建出空表、不回填：本票到批次8-16 之间引擎仍直写占有行、不读不写这里，此时回填的「取得」会在引擎按老路放车后
/// 永远开着。回填归批次8-16，在它把引擎改走 <c>IVehiclePurposeLedger</c> 的同一次迁移里从当时的占有行补。
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

    /// <summary>所依据的等待点登记版本；固定公共站点为空。</summary>
    public long? WaitingPointVersion { get; set; }

    /// <summary>这一次独占的经过是哪一条 <see cref="StationExclusivityRecordRow"/>。</summary>
    public required string RecordId { get; set; }
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
