namespace ControlServer.Application;

// 批次 8 建表票 control-server#386 的端口：用途占有（按用途认领与释放、占有历史）、站点独占、等待点登记。
// 引擎的受理与释放与账本端口共用同一条写入路径（批次8-16，control-server#387）；其余取用者是批次8-17～8-21（control-server#388～#392）。

/// <summary>站点独占一行的两种状态（规格 5.4「一行两状态」）。</summary>
public static class StationExclusivityStates
{
    /// <summary>在途预占：承诺已成、车还没到。</summary>
    public const string Reserved = "RESERVED";

    /// <summary>在点占用：车已到点。</summary>
    public const string Occupied = "OCCUPIED";

    public static IReadOnlyList<string> All { get; } = [Reserved, Occupied];
}

/// <summary>独占的这个站点是哪一种。等待点、<c>REQ-0204</c> 固定公共站点与充电桩共用同一个原语（规格 5.4）。</summary>
public static class StationExclusivityKinds
{
    public const string WaitingPoint = "WAITING_POINT";

    /// <summary><c>REQ-0204</c> 的每图每任务类型一个 <c>FixedTaskStation</c>（批次8-20，control-server#391）。</summary>
    public const string FixedTaskStation = "FIXED_TASK_STATION";

    /// <summary>名册上的充电桩（<c>REQ-0171</c>、<c>REQ-0173</c>；批次 9 建表票 control-server#399）。</summary>
    public const string Charger = "CHARGER";

    public static IReadOnlyList<string> All { get; } = [WaitingPoint, FixedTaskStation, Charger];
}

/// <summary>等待点登记从哪来。</summary>
public static class WaitingPointSources
{
    /// <summary>经 <see cref="IWaitingPointRegistry"/> 写入，带治理快照与业务审计。</summary>
    public const string GovernedImport = "GOVERNED_IMPORT";
}

/// <summary>等待点登记的治理对象身份与审计动作名。</summary>
public static class WaitingPointGovernance
{
    public const string ObjectId = "waiting-points";

    public const string VersionImportedAction = "WAITING_POINTS_VERSION_IMPORTED";
}

/// <summary>
/// 用途占有记录上的释放原因里，服务端自己定的那几个（批次8-16，control-server#387）。取货停靠的终结写它自己的终结码
/// （<c>PickupStopTermination</c> 的 <c>reasonCode</c>），不在这里列。
/// </summary>
public static class VehiclePurposeReleaseReasons
{
    /// <summary>旅程最后一条未结束的需求卸完了（卸货成功或卸货结果进来）。</summary>
    public const string LastDemandUnloaded = "LAST_DEMAND_UNLOADED";

    /// <summary>
    /// 迁移时从已释放的租约行搬进来的历史（批次8-16 的迁移删租约表之前）。取得与释放时刻取自租约行。
    /// </summary>
    public const string DispatchLeaseReleased = "DISPATCH_LEASE_RELEASED";

    /// <summary>
    /// 迁移时发现一条开着的记录，而它的占有行已经不在：只会发生在降级跑过旧引擎（旧引擎放车不关记录）再升级回来之后。
    /// 迁移把它关上，免得它比占有行活得久。
    /// </summary>
    public const string ClaimGoneBeforeMigration = "CLAIM_GONE_BEFORE_MIGRATION";
}

/// <summary>
/// 一次用途占有的记录：谁（车）、为什么（用途）、替谁（持有者，也就是那趟旅程）、何时取得、何时为何释放（<c>REQ-0297</c>）。
/// 未释放时 <see cref="ReleasedAt"/> 为空。
/// </summary>
public sealed record VehiclePurposeClaimRecord(
    string RecordId,
    string VehicleKey,
    string Purpose,
    string JourneyId,
    DateTimeOffset AcquiredAt,
    DateTimeOffset? ReleasedAt,
    string? ReleaseReason);

/// <summary>站点独占的一行：此刻谁以哪种状态占着 <c>(MapId, StationId)</c>。</summary>
/// <param name="StationId">RIoT 站号（规格里「站点 212」的那个数）。</param>
/// <param name="JourneyId">持有者：取得这个独占的那次用途占有的旅程。</param>
/// <param name="StateSince">进入当前状态的时刻。</param>
/// <param name="WaitingPointVersion">所依据的等待点登记版本；固定公共站点与充电桩为空。</param>
/// <param name="ChargerRosterVersion">所依据的充电桩名册版本；只有充电桩有（control-server#399）。</param>
public sealed record StationExclusivity(
    int MapId,
    int StationId,
    string StationKind,
    string State,
    string VehicleKey,
    string JourneyId,
    DateTimeOffset StateSince,
    long? WaitingPointVersion,
    long? ChargerRosterVersion = null);

/// <summary>站点独占的一次完整经过：预占、到点、释放，以及为什么释放。未释放时 <see cref="ReleasedAt"/> 为空。</summary>
public sealed record StationExclusivityRecord(
    string RecordId,
    int MapId,
    int StationId,
    string StationKind,
    string VehicleKey,
    string JourneyId,
    long? WaitingPointVersion,
    DateTimeOffset? ReservedAt,
    DateTimeOffset? OccupiedAt,
    DateTimeOffset? ReleasedAt,
    string? ReleaseReason,
    long? ChargerRosterVersion = null);

/// <summary>要取得的站点独占。<paramref name="State"/> 是取得时的状态：承诺时预占，已在点的车直接占用。</summary>
/// <param name="ChargerRosterVersion">充电桩的预占依据哪一版名册（control-server#399）；别的种类为空。</param>
public sealed record StationExclusivityRequest(
    int MapId,
    int StationId,
    string StationKind,
    string State,
    long? WaitingPointVersion,
    long? ChargerRosterVersion = null);

/// <summary>一次认领的结果。</summary>
public enum VehiclePurposeAcquisitionOutcome
{
    /// <summary>用途占有（以及请求了的站点独占）都已取得，同一次保存。</summary>
    Acquired,

    /// <summary>
    /// 请求的全部本来就是这趟旅程的：它以同一用途占着这辆车，请求了站点时也占着那个站点（崩溃后重试就是这样）。没有写任何东西。
    /// 站点的状态也不变：请求的是 <c>OCCUPIED</c> 而持有的仍是 <c>RESERVED</c> 时，它仍是 <c>RESERVED</c>，调用方还要调
    /// <see cref="IStationExclusivityStore.MarkOccupiedAsync"/>。
    /// </summary>
    AlreadyHeld,

    /// <summary>
    /// 车被别的旅程或别的用途占着，或者被这趟旅程占着、却没有它请求的那个站点。什么也没写。
    /// </summary>
    VehicleHeld,

    /// <summary>车不是别人的，但站点被别的旅程占着；什么也没写，用途占有也没留下。</summary>
    StationHeld,

    /// <summary>
    /// 车是空的，站点却已经是这趟旅程的：它的用途占有已经放了，站点还等着离点证据（<c>REQ-0293</c>）。什么也没写，车仍是空的；
    /// 调用方不能把它当成「站点被别人占着」去换别的站点。
    /// </summary>
    StationAlreadyHeld,
}

/// <summary>单独取得一个站点独占的结果。</summary>
public enum StationExclusivityAcquisitionOutcome
{
    Acquired,

    /// <summary>
    /// 这趟旅程的这辆车本来就占着这个站点（崩溃后重试）。没有写任何东西，状态与时刻不变：请求的是 <c>OCCUPIED</c> 而持有的仍是
    /// <c>RESERVED</c> 时它仍是 <c>RESERVED</c>，调用方还要调 <see cref="IStationExclusivityStore.MarkOccupiedAsync"/>。
    /// 调用方不能把它当成「被占」去换别的站点，
    /// 否则会把自己占着的那个晾着——等待点数等于车辆数时就是规格 5.4 说的互锁。
    /// </summary>
    AlreadyHeld,

    /// <summary>站点被别的旅程或别的车占着；什么也没写。</summary>
    Held,
}

/// <summary>
/// 用途占有：按用途认领与释放、占有历史（规格 5.4；<c>REQ-0290</c>～<c>0292</c>、<c>REQ-0297</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 谁占到由数据库约束决定，不先读后写：<c>VehiclePurposeClaims</c> 的主键一车一行，站点独占的主键 <c>(MapId, StationId)</c>
/// 一站一行。占有记录与站点经过是证据，不参与仲裁。
/// </para>
/// <para>
/// 认领时可以同时要一个站点独占：两件事在同一次保存里，要么都在、要么都不在（批次8-18 的原子承诺靠这一点）。
/// </para>
/// </remarks>
public interface IVehiclePurposeLedger
{
    Task<VehiclePurposeClaim?> ReadClaimAsync(string vehicleKey, CancellationToken cancellationToken);

    /// <summary>
    /// 为这趟旅程以这个用途占住车辆，并在同一次保存里记下「取得」；给了 <paramref name="station"/> 时一并取得那个站点独占。
    /// </summary>
    Task<VehiclePurposeAcquisitionOutcome> TryAcquireAsync(
        VehiclePurposeClaim claim,
        StationExclusivityRequest? station,
        CancellationToken cancellationToken);

    /// <summary>
    /// 释放这趟旅程对车辆的占有，在同一次保存里给它的记录写上释放时刻与原因。车辆没被它占着时什么也不做，返回假。
    /// 这趟旅程还持有的站点独占不在这里放——离点要有证据（<c>REQ-0293</c>），由 <see cref="IStationExclusivityStore.ReleaseAsync"/> 放。
    /// </summary>
    Task<bool> ReleaseAsync(
        string vehicleKey,
        string journeyId,
        DateTimeOffset releasedAt,
        string releaseReason,
        CancellationToken cancellationToken);

    /// <summary>这辆车的全部占有记录，按取得时刻排。</summary>
    Task<IReadOnlyList<VehiclePurposeClaimRecord>> ListClaimHistoryAsync(
        string vehicleKey, CancellationToken cancellationToken);
}

/// <summary>站点独占（规格 5.4；<c>REQ-0293</c>～<c>0296</c>、<c>REQ-0204</c>）。一站一行，谁占到由主键决定。</summary>
public interface IStationExclusivityStore
{
    /// <summary>
    /// 为这趟旅程的这辆车取得站点独占，并记下它的经过。站点已被占着时什么也不写，并分清是自己占着还是别人占着。
    /// 要与用途占有一起取得时用 <see cref="IVehiclePurposeLedger.TryAcquireAsync"/>。
    /// </summary>
    Task<StationExclusivityAcquisitionOutcome> TryAcquireAsync(
        StationExclusivityRequest request,
        string vehicleKey,
        string journeyId,
        DateTimeOffset at,
        CancellationToken cancellationToken);

    /// <summary>预占转占用：车到点了。这个站点不是被这趟旅程预占着时返回假，什么也不改；已是占用时返回真、不改时刻。</summary>
    Task<bool> MarkOccupiedAsync(
        int mapId, int stationId, string journeyId, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>释放这趟旅程持有的站点独占，把时刻与原因（离点证据）写进它的经过。不是它持有的时返回假。</summary>
    Task<bool> ReleaseAsync(
        int mapId,
        int stationId,
        string journeyId,
        DateTimeOffset releasedAt,
        string releaseReason,
        CancellationToken cancellationToken);

    Task<StationExclusivity?> ReadAsync(int mapId, int stationId, CancellationToken cancellationToken);

    Task<IReadOnlyList<StationExclusivity>> ListByVehicleAsync(string vehicleKey, CancellationToken cancellationToken);

    /// <summary>这个站点的全部独占经过，按预占（或直接占用）的时刻排。</summary>
    Task<IReadOnlyList<StationExclusivityRecord>> ListHistoryAsync(
        int mapId, int stationId, CancellationToken cancellationToken);
}

/// <summary>登记里的一个等待点。</summary>
/// <param name="StationId">RIoT 站号。</param>
/// <param name="VehicleScope">
/// <c>WaitingPointVehicleScope</c> 白名单：可以停这个等待点的车（<c>VehicleKey</c>）。空即同图全部车辆开放（<c>REQ-0289</c>）。
/// </param>
public sealed record WaitingPointEntry(
    int MapId,
    int StationId,
    string StationName,
    bool Enabled,
    IReadOnlyList<string> VehicleScope);

/// <summary>等待点登记的一个版本。<see cref="Points"/> 按 <c>(MapId, StationId)</c> 排，白名单按 <c>VehicleKey</c> 排。</summary>
public sealed record WaitingPointRegistrationVersion(
    long Version,
    string ContentSha256,
    string? SnapshotId,
    DateTimeOffset LoadedAt,
    string Source,
    IReadOnlyList<WaitingPointEntry> Points);

/// <summary>
/// 等待点登记（<c>REQ-0289</c>、<c>REQ-0297</c>），受治理配置，形状照每区派车参数：版本行与子行只追加，写入即不可改。
/// </summary>
public interface IWaitingPointRegistry
{
    /// <summary>
    /// 写一个新版本（当前最大 + 1），经治理快照与业务审计。内容与当前版本逐字相同时不写，返回当前版本。
    /// 并发写入由主键冲突整体回滚一方。
    /// </summary>
    Task<WaitingPointRegistrationVersion> WriteVersionAsync(
        IReadOnlyList<WaitingPointEntry> points, DateTimeOffset loadedAt, CancellationToken cancellationToken);

    /// <summary>当前（版本号最大的）版本；一版都没有即没有登记任何等待点，为空。</summary>
    Task<WaitingPointRegistrationVersion?> ReadCurrentAsync(CancellationToken cancellationToken);

    Task<WaitingPointRegistrationVersion?> ReadVersionAsync(long version, CancellationToken cancellationToken);
}
