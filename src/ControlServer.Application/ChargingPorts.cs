namespace ControlServer.Application;

// 批次 9 建表票 control-server#399 的端口：充电桩名册、充电策略版本、充电周期、桩分配暂停与车辆充电资格暂停、清桩记录、服务端持有的
// 人工充电等待、两类现场确认请求。本票没有运行时读者；取用者是批次9-02～9-12，它们零迁移。

/// <summary>充电桩名册从哪来。</summary>
public static class ChargerRosterSources
{
    /// <summary>经 <see cref="IChargerRoster"/> 写入，带治理快照与业务审计。</summary>
    public const string GovernedImport = "GOVERNED_IMPORT";
}

/// <summary>充电桩名册与充电策略的治理对象身份与审计动作名。</summary>
public static class ChargingGovernance
{
    public const string RosterObjectId = "charger-roster";

    public const string RosterVersionImportedAction = "CHARGER_ROSTER_VERSION_IMPORTED";

    public const string PolicyObjectId = "charging-policy";

    public const string PolicyVersionWrittenAction = "CHARGING_POLICY_VERSION_WRITTEN";
}

/// <summary>
/// 一次充电策略批准的来源（<c>REQ-0282</c>「受证据批准」）。三者分得开：测试夹具与 L2 预置的批准不能被读成现场批准，
/// 取用方（批次9-02）按来源决定认不认。
/// </summary>
public static class ChargingPolicyApprovalSources
{
    /// <summary>现场依据电池规格、耗电、遥测精度与现场测试作出的批准。</summary>
    public const string Field = "FIELD";

    public const string TestFixture = "TEST_FIXTURE";

    public const string L2Preset = "L2_PRESET";

    public static IReadOnlyList<string> All { get; } = [Field, TestFixture, L2Preset];
}

/// <summary>
/// 充电周期在线上的状态：与协议 <c>VehicleBusinessStateSnapshot.chargingCycleState</c> 的取值逐字对齐
/// （<c>OnboardJourneyPublisher</c> 的发送前校验用的是同一组值）。服务端内部的阶段另在 <see cref="ChargingCyclePhases"/>。
/// </summary>
public static class ChargingCycleWireStates
{
    public const string NotCharging = "NOT_CHARGING";
    public const string Allocated = "ALLOCATED";
    public const string EnRoute = "EN_ROUTE";
    public const string Charging = "CHARGING";
    public const string Complete = "COMPLETE";
    public const string UnableToCharge = "UNABLE_TO_CHARGE";
    public const string Unknown = "UNKNOWN";

    public static IReadOnlyList<string> All { get; } =
        [NotCharging, Allocated, EnRoute, Charging, Complete, UnableToCharge, Unknown];
}

/// <summary>
/// 充电周期在服务端内部的阶段，线上没有对应值：清桩中（<c>REQ-0178</c>）与已结束都不是 <c>chargingCycleState</c> 的取值。
/// 一车至多一个未结束（不是 <see cref="Ended"/>）的周期，由过滤唯一索引保证。
/// </summary>
public static class ChargingCyclePhases
{
    /// <summary>从分配到充满离桩：线上状态在 <c>ALLOCATED</c>～<c>COMPLETE</c> 之间走。</summary>
    public const string Active = "ACTIVE";

    /// <summary>失败后的「清桩中」闭环（<c>REQ-0178</c>）：旧订单确认取消、车离桩、桩腾空之前。</summary>
    public const string Clearing = "CLEARING";

    public const string Ended = "ENDED";

    public static IReadOnlyList<string> All { get; } = [Active, Clearing, Ended];
}

/// <summary>桩分配暂停的触发来源（<c>REQ-0177</c>、<c>REQ-0285</c>、<c>REQ-0288</c>）。触发来源即暂停的理由。</summary>
public static class ChargingStationHoldTriggers
{
    /// <summary>已确认充不上（<c>REQ-0177</c>、<c>REQ-0176</c>）。</summary>
    public const string UnableToChargeConfirmed = "UNABLE_TO_CHARGE_CONFIRMED";

    /// <summary><c>ConfirmedChargingInterruption</c>（<c>REQ-0285</c>）。</summary>
    public const string InterruptionConfirmed = "INTERRUPTION_CONFIRMED";

    /// <summary><c>ConfirmedChargingNoProgress</c>（<c>REQ-0285</c>）。</summary>
    public const string NoProgressConfirmed = "NO_PROGRESS_CONFIRMED";

    /// <summary>维修（<c>REQ-0288</c>）：维护管理员或系统管理员让待修的桩退出候选。</summary>
    public const string Maintenance = "MAINTENANCE";

    public static IReadOnlyList<string> All { get; } =
        [UnableToChargeConfirmed, InterruptionConfirmed, NoProgressConfirmed, Maintenance];
}

/// <summary>暂停的根因：原因未知时不得自动归责（<c>REQ-0177</c>、<c>REQ-0286</c>），所以只有这一个值。</summary>
public static class ChargingHoldRootCauses
{
    public const string Unknown = "UNKNOWN";
}

/// <summary>车辆充电资格暂停的原因（<c>REQ-0285</c>：中断与无进展同时暂停车与桩）。</summary>
public static class VehicleChargingEligibilityHoldReasons
{
    public const string InterruptionConfirmed = ChargingStationHoldTriggers.InterruptionConfirmed;

    public const string NoProgressConfirmed = ChargingStationHoldTriggers.NoProgressConfirmed;

    public static IReadOnlyList<string> All { get; } = [InterruptionConfirmed, NoProgressConfirmed];
}

/// <summary>清桩的两种完成证明（<c>REQ-0179</c>）。</summary>
public static class StationClearanceProofs
{
    /// <summary>系统确认车辆已到地图等待点。</summary>
    public const string ArrivedAtWaitingPoint = "ARRIVED_AT_WAITING_POINT";

    /// <summary>人工清桩确认：车辆已移至安全位置且原桩已腾空。</summary>
    public const string ManualConfirmation = "MANUAL_CONFIRMATION";

    public static IReadOnlyList<string> All { get; } = [ArrivedAtWaitingPoint, ManualConfirmation];
}

/// <summary>服务端置人工充电等待的原因。库里不设 CHECK；批次9-12 可能另写它自己的原因。</summary>
public static class ManualChargingHoldReasons
{
    /// <summary>名册为空（或没有这辆车可用的桩）：退化到人工充电等待并告警，不静默（<c>REQ-0171</c>，规格 8.6）。</summary>
    public const string RosterEmpty = "ROSTER_EMPTY";

    /// <summary>
    /// 这辆车的充电单在短时间内第二次被取消、删除或 FAILED 后由人清除（批次9-06，control-server#404）：反复结束说明有人要它别动，
    /// 不再自动分配充电，改为人工充电等待并告警，由人处理——与搬运自建单的「再次出问题即停」（<c>REQ-0361</c>）对等。
    /// </summary>
    public const string ChargingRepeatedlyFailed = "CHARGING_REPEATEDLY_FAILED";
}

// ---------------------------------------------------------------------------------------------------------------------
// 充电桩名册（REQ-0171）

/// <summary>名册上的一个桩。</summary>
/// <param name="StationId">RIoT 站号（规格里「站点 211」的那个数）。</param>
/// <param name="EntryStationId">登记时核对到的进点站号；没有进点为空。</param>
/// <param name="ExitStationId">登记时核对到的出点站号；没有出点为空。</param>
/// <param name="VehicleScope">可以用这个桩的车（<c>VehicleKey</c>），即每车候选集是名册的子集；空即对投运名册里的全部车开放。</param>
public sealed record ChargerRosterEntry(
    int MapId,
    int StationId,
    string StationName,
    int? EntryStationId,
    int? ExitStationId,
    IReadOnlyList<string> VehicleScope);

/// <summary>一版名册的批准：谁、依据什么、改了什么（<c>REQ-0171</c>「保留批准/变更记录」）。</summary>
public sealed record ChargerRosterApproval(string ApprovedBy, string ApprovalBasis, string? ChangeNote);

/// <summary>名册的一个版本。<see cref="Chargers"/> 为空即「名册置空」，是合法版本，不是「没有版本」。</summary>
public sealed record ChargerRosterVersion(
    long Version,
    string ContentSha256,
    string? SnapshotId,
    DateTimeOffset LoadedAt,
    string Source,
    ChargerRosterApproval Approval,
    IReadOnlyList<ChargerRosterEntry> Chargers);

/// <summary>
/// 8005 独占充电桩名册（<c>REQ-0171</c>），受治理配置，形状照等待点登记：版本行与子行只追加，写入即不可改。
/// </summary>
/// <remarks>
/// 用户 2026-09-29 定的并行期隔离就是在「空版本」与「登记 211 的版本」之间来回导入，所以零条目的版本与任何别的版本一样写、一样读。
/// </remarks>
public interface IChargerRoster
{
    /// <summary>
    /// 写一个新版本（当前最大 + 1），经治理快照与业务审计。桩的内容与当前版本逐字相同时不写，返回当前版本。
    /// 并发写入由主键冲突整体回滚一方。
    /// </summary>
    Task<ChargerRosterVersion> WriteVersionAsync(
        IReadOnlyList<ChargerRosterEntry> chargers,
        ChargerRosterApproval approval,
        DateTimeOffset loadedAt,
        CancellationToken cancellationToken);

    /// <summary>
    /// 当前（版本号最大的）版本。当前版本零条目时返回它，<see cref="ChargerRosterVersion.Chargers"/> 为空集合；
    /// 从未写过任何版本时为空引用。两者都不是异常。
    /// </summary>
    Task<ChargerRosterVersion?> ReadCurrentAsync(CancellationToken cancellationToken);

    Task<ChargerRosterVersion?> ReadVersionAsync(long version, CancellationToken cancellationToken);
}

// ---------------------------------------------------------------------------------------------------------------------
// 充电策略版本（REQ-0281、REQ-0282、REQ-0285）

/// <summary>
/// 一版 <c>ChargingPolicyVersion</c> 的内容。电量都是整数百分比（0～100），时长都是整数秒。
/// </summary>
/// <remarks>
/// 三个阈值之间的关系（<c>ChargingCompletionThreshold &gt; MandatoryChargeEntryThreshold &gt;= 最低任务后电量余量</c>，
/// <c>REQ-0281</c>）不在这里、也不在库里校验：关系规则只有一份定义，导入与启动时由同一个校验函数判（批次9-02 写、批次9-05 接到启动）。
/// 库里只有单个字段的取值范围。
/// </remarks>
/// <param name="MinimumPostTaskBatteryMarginPercent"><c>DispatchBatteryEligibility</c> 的预计任务完成后最低电量余量。</param>
/// <param name="MandatoryChargeEntryThresholdPercent">空闲车停止承接普通新任务、进入待充电的当前电量门槛。</param>
/// <param name="ChargingCompletionThresholdPercent">开始正常结束本次充电的目标电量。</param>
/// <param name="EstimatedTaskConsumptionPercent">每趟任务的耗电估计。</param>
/// <param name="ProgressStabilizationSeconds"><c>ChargingProgressObservationPolicy</c> 的稳定期。</param>
/// <param name="ProgressObservationWindowSeconds"><c>ChargingProgressObservationPolicy</c> 的观察窗口。</param>
/// <param name="ProgressMinimumIncreasePercent"><c>ChargingProgressObservationPolicy</c> 的最小电量增量。</param>
/// <param name="VehicleScope">适用车辆（<c>VehicleKey</c>）；空即全部投运车辆。</param>
public sealed record ChargingPolicyContent(
    int MinimumPostTaskBatteryMarginPercent,
    int MandatoryChargeEntryThresholdPercent,
    int ChargingCompletionThresholdPercent,
    int EstimatedTaskConsumptionPercent,
    int ProgressStabilizationSeconds,
    int ProgressObservationWindowSeconds,
    int ProgressMinimumIncreasePercent,
    IReadOnlyList<string> VehicleScope);

/// <summary>策略的一个版本。写入即不可改；批准与激活另行记录，不改这一行。</summary>
public sealed record ChargingPolicyVersion(
    long Version,
    string ContentSha256,
    string? SnapshotId,
    DateTimeOffset WrittenAt,
    string? ChangeNote,
    ChargingPolicyContent Content);

/// <summary>一次批准（只追加）。</summary>
/// <param name="BasisReference">批准依据的引用（电池规格、现场测试记录之类）。</param>
/// <param name="Source"><see cref="ChargingPolicyApprovalSources"/> 之一。</param>
public sealed record ChargingPolicyApproval(
    string ApprovalId,
    long Version,
    string ApprovedBy,
    string ApproverRole,
    DateTimeOffset ApprovedAt,
    string BasisReference,
    string Source);

/// <summary>一次激活（只追加）。<see cref="Sequence"/> 从 1 起单调递增，决定哪一次是「最近一次」。</summary>
public sealed record ChargingPolicyActivation(
    string ActivationId,
    long Sequence,
    long Version,
    DateTimeOffset ActivatedAt,
    string ActivatedBy);

/// <summary>一辆车此刻生效的策略：最近一次激活的版本、它的全部批准（按时刻排）、那次激活。</summary>
public sealed record EffectiveChargingPolicy(
    ChargingPolicyVersion Policy,
    IReadOnlyList<ChargingPolicyApproval> Approvals,
    ChargingPolicyActivation Activation);

/// <summary>
/// <c>ChargingPolicyVersion</c>（<c>REQ-0282</c>）：版本只追加、写入即不可改；批准与激活各一张只追加表。
/// 「当前生效」＝最近一次激活的版本；只有已批准的版本能被激活。
/// </summary>
public interface IChargingPolicyStore
{
    /// <summary>
    /// 写一个新版本（当前最大 + 1），经治理快照与业务审计。内容与当前最大版本逐字相同时不写，返回那一版。单字段超出范围时由库拒绝。
    /// </summary>
    Task<ChargingPolicyVersion> WriteVersionAsync(
        ChargingPolicyContent content, string? changeNote, DateTimeOffset writtenAt, CancellationToken cancellationToken);

    /// <summary>给一个已写入的版本记一次批准。版本不存在时抛 <see cref="InvalidOperationException"/>；来源不在三者之中时由库拒绝。</summary>
    Task<ChargingPolicyApproval> ApproveAsync(
        long version,
        string approvedBy,
        string approverRole,
        DateTimeOffset approvedAt,
        string basisReference,
        string source,
        CancellationToken cancellationToken);

    /// <summary>
    /// 激活一个版本。版本不存在或还没有任何批准时抛 <see cref="InvalidOperationException"/>，什么也不写。两个并发激活由序号的唯一约束
    /// 整体回滚一方。
    /// </summary>
    Task<ChargingPolicyActivation> ActivateAsync(
        long version, string activatedBy, DateTimeOffset activatedAt, CancellationToken cancellationToken);

    /// <summary>
    /// 这辆车此刻生效的策略：最近一次激活的版本，且它的适用范围为空或含这辆车。没有就是空引用——空不是异常，
    /// 逐车硬阻断由取用方判（批次9-02）。
    /// </summary>
    Task<EffectiveChargingPolicy?> ReadEffectiveForVehicleAsync(string vehicleKey, CancellationToken cancellationToken);

    /// <summary>按版本号读内容：充电周期与旅程上冻结的是版本号，回读用这个。</summary>
    Task<ChargingPolicyVersion?> ReadVersionAsync(long version, CancellationToken cancellationToken);

    Task<IReadOnlyList<ChargingPolicyApproval>> ListApprovalsAsync(long version, CancellationToken cancellationToken);
}

// ---------------------------------------------------------------------------------------------------------------------
// 充电周期

/// <summary>
/// 一个充电周期。<see cref="Version"/> 是并发令牌：推进只对读到时的那一版生效。
/// </summary>
/// <param name="JourneyId">所挂的旅程：充电与空闲返回一样挂在「无需求旅程」上，故障监看与在途单重建因此照样够得着。</param>
/// <param name="ChargerRosterVersion">分配时依据的名册版本。</param>
/// <param name="ChargingPolicyVersion">本周期冻结的策略版本（<c>REQ-0282</c>）。</param>
/// <param name="WireState"><see cref="ChargingCycleWireStates"/> 之一。</param>
/// <param name="Phase"><see cref="ChargingCyclePhases"/> 之一。</param>
/// <param name="ObservationWindowStartedAt">无进展观察窗口的起点时刻（<c>REQ-0285</c>）。</param>
/// <param name="ObservationWindowStartPercent">观察窗口起点的电量。</param>
public sealed record ChargingCycle(
    string CycleId,
    string VehicleKey,
    string JourneyId,
    int MapId,
    int StationId,
    long ChargerRosterVersion,
    long ChargingPolicyVersion,
    string WireState,
    string Phase,
    string? UpperId,
    DateTimeOffset AllocatedAt,
    DateTimeOffset? OrderConfirmedAt,
    DateTimeOffset? ArrivedAt,
    DateTimeOffset? FirstChargingSeenAt,
    DateTimeOffset? CompletedAt,
    DateTimeOffset? DepartedAt,
    DateTimeOffset? ReleasedAt,
    DateTimeOffset? EndedAt,
    string? EndReason,
    DateTimeOffset? ObservationWindowStartedAt,
    int? ObservationWindowStartPercent,
    DateTimeOffset? LastSampleAt,
    int? LastSamplePercent,
    long Version);

/// <summary>开始一个充电周期所需的全部。</summary>
public sealed record ChargingCycleStart(
    string CycleId,
    string VehicleKey,
    string JourneyId,
    int MapId,
    int StationId,
    long ChargerRosterVersion,
    long ChargingPolicyVersion,
    DateTimeOffset AllocatedAt);

/// <summary>开始一个充电周期的结果。除 <see cref="Started"/> 外什么也没写。</summary>
public enum ChargingCycleStartOutcome
{
    /// <summary>周期行、<c>CHARGING</c> 用途占有与它的记录、<c>CHARGER</c> 预占与它的经过，同一次保存。</summary>
    Started,

    /// <summary>这个周期 id 已经开始过了（崩溃后重试）。</summary>
    AlreadyStarted,

    /// <summary>这辆车已有一个未结束的周期。</summary>
    OpenCycleExists,

    /// <summary>车被别的旅程或别的用途占着。</summary>
    VehicleHeld,

    /// <summary>桩被别的旅程占着（<c>REQ-0173</c>：预占成功后不可抢占）。</summary>
    StationHeld,
}

/// <summary>
/// 充电周期。一车至多一个未结束的周期，由过滤唯一索引保证，不先读后写。
/// </summary>
public interface IChargingCycleStore
{
    /// <summary>
    /// 开始一个周期：周期行（<c>ALLOCATED</c>、<c>ACTIVE</c>）、<c>CHARGING</c> 用途占有、<c>CHARGER</c> 预占在同一次保存里，
    /// 要么都在、要么都不在（批次9-06 的原子承诺靠这一点）。
    /// </summary>
    /// <param name="sameSave">
    /// 调用方要与它们同一次保存的其余行（持久化层的实体，例如那趟旅程的行与待建的订单意图）。它们随这次保存一起写入；保存被键拒绝时
    /// 与本方法自己暂存的行一起撤出上下文，什么也不留。<b>调用方不要先把这些行 Add 进上下文再调用</b>：那样被拒时它们会留在上下文里，
    /// 随调用方下一次保存被写出去。
    /// </param>
    Task<ChargingCycleStartOutcome> TryStartAsync(
        ChargingCycleStart start, IReadOnlyCollection<object>? sameSave, CancellationToken cancellationToken);

    /// <summary>
    /// 推进：把 <paramref name="cycle"/> 里会变的字段（线上状态、阶段、<c>upperId</c>、各时刻、结束原因、观察样本）写回，
    /// 只对 <see cref="ChargingCycle.Version"/> 仍是读到时那一版的行生效，并把版本加一。被别人先改过时返回假、什么也不写。
    /// 标识、车、旅程、桩、名册与策略版本、分配时刻不可改。
    /// </summary>
    Task<bool> UpdateAsync(ChargingCycle cycle, CancellationToken cancellationToken);

    Task<ChargingCycle?> ReadAsync(string cycleId, CancellationToken cancellationToken);

    /// <summary>这辆车未结束的那个周期；没有为空。</summary>
    Task<ChargingCycle?> ReadOpenAsync(string vehicleKey, CancellationToken cancellationToken);
}

// ---------------------------------------------------------------------------------------------------------------------
// 桩分配暂停与车辆充电资格暂停（REQ-0177、REQ-0285、REQ-0286、REQ-0288）

/// <summary>
/// 一次桩分配暂停（<c>ChargingStationAllocationHold</c>），字段照 <c>REQ-0177</c> 列的全部。暂停行只追加、从不改写；
/// 根因固定为 <see cref="ChargingHoldRootCauses.Unknown"/>，由存储写，不由调用方给。
/// </summary>
/// <param name="IdempotencyKey">同一触发只成一行：同一个键第二次写，得到第一次那一行。</param>
/// <param name="Trigger"><see cref="ChargingStationHoldTriggers"/> 之一，即暂停的理由。</param>
/// <param name="ChargerRosterVersion">名册记录所在的版本；名册记录即这一版里的 <c>(MapId, StationId)</c>。</param>
/// <param name="ReservationRecordId">预占的身份：那次 <c>CHARGER</c> 站点独占的经过 id。</param>
public sealed record ChargingStationAllocationHold(
    string HoldId,
    string IdempotencyKey,
    string Trigger,
    int MapId,
    int StationId,
    long? ChargerRosterVersion,
    string? VehicleKey,
    string? ReservationRecordId,
    string? CycleId,
    string? UpperId,
    string? OrderId,
    DateTimeOffset? ArrivedAt,
    DateTimeOffset? ChargingStartedAt,
    DateTimeOffset? FailedAt,
    DateTimeOffset? FinalHangAt,
    DateTimeOffset? ConfirmedAt,
    DateTimeOffset HeldAt,
    string? RawPositionJson,
    string? RawOrderJson,
    string? RawActionResultJson,
    string? RawBatteryJson,
    string? EvidenceReference,
    string? RiotBuild,
    string? RiotContractVersion,
    string? ConfirmedByPersonId,
    string? ConfirmedByRole,
    DateTimeOffset? ConfirmedAuthenticatedAt,
    string? SiteDisposition);

/// <summary>恢复一次暂停（<c>ChargingStationRecoveryConfirmation</c> 或车辆一侧的恢复）：另起一行，不改暂停行。一次暂停至多恢复一次。</summary>
public sealed record ChargingHoldRecovery(
    string RecoveryId,
    string HoldId,
    string RecoveredBy,
    string RecovererRole,
    DateTimeOffset RecoveredAt,
    string Basis);

/// <summary>一次车辆充电资格暂停（<c>VehicleChargingEligibilityHold</c>）。只追加。</summary>
/// <param name="Reason"><see cref="VehicleChargingEligibilityHoldReasons"/> 之一。</param>
public sealed record VehicleChargingEligibilityHold(
    string HoldId,
    string IdempotencyKey,
    string VehicleKey,
    string? CycleId,
    string Reason,
    DateTimeOffset HeldAt,
    string? EvidenceReference);

/// <summary>写一次暂停的结果：<see cref="Created"/> 为假即同一幂等键已有一行，<see cref="Hold"/> 是那一行。</summary>
public sealed record HoldWriteResult<THold>(THold Hold, bool Created);

/// <summary>
/// 桩分配暂停与车辆充电资格暂停。两侧形状相同：暂停行只追加，恢复另起一行；「当前暂停着」＝没有恢复行的暂停。
/// </summary>
public interface IChargingHoldStore
{
    Task<HoldWriteResult<ChargingStationAllocationHold>> RecordStationHoldAsync(
        ChargingStationAllocationHold hold, CancellationToken cancellationToken);

    /// <summary>恢复一次桩暂停。同一次暂停已恢复过时返回既有那一行，不写第二行；暂停不存在时抛 <see cref="InvalidOperationException"/>。</summary>
    Task<ChargingHoldRecovery> RecoverStationHoldAsync(ChargingHoldRecovery recovery, CancellationToken cancellationToken);

    /// <summary>这个桩当前暂停着的全部（未恢复的），按暂停时刻排。</summary>
    Task<IReadOnlyList<ChargingStationAllocationHold>> ListActiveStationHoldsAsync(
        int mapId, int stationId, CancellationToken cancellationToken);

    Task<ChargingStationAllocationHold?> ReadStationHoldAsync(string holdId, CancellationToken cancellationToken);

    Task<HoldWriteResult<VehicleChargingEligibilityHold>> RecordVehicleHoldAsync(
        VehicleChargingEligibilityHold hold, CancellationToken cancellationToken);

    /// <summary>同 <see cref="RecoverStationHoldAsync"/>，车辆一侧。</summary>
    Task<ChargingHoldRecovery> RecoverVehicleHoldAsync(ChargingHoldRecovery recovery, CancellationToken cancellationToken);

    Task<IReadOnlyList<VehicleChargingEligibilityHold>> ListActiveVehicleHoldsAsync(
        string vehicleKey, CancellationToken cancellationToken);
}

// ---------------------------------------------------------------------------------------------------------------------
// 清桩记录（REQ-0178、REQ-0179）

/// <summary>一次清桩：开始与完成。未完成时完成那一组字段都为空。</summary>
/// <param name="Proof"><see cref="StationClearanceProofs"/> 之一。</param>
/// <param name="WaitingPointMapId">系统证明时，车到的那个等待点。</param>
/// <param name="ConfirmedBy">人工证明时的确认人（以个人身份）。</param>
/// <param name="Assistants">人工证明时的协助者，另记（<c>REQ-0179</c>）。</param>
/// <param name="ClearedCondition">
/// 人工证明时现场确认的腾空情况，取值同协议 <c>ManualStationClearanceConfirmationRequested.clearedCondition</c>（批次9-08 第 6 条）。
/// </param>
/// <param name="ConfirmationRequestId">
/// 人工证明来自哪一个确认请求：线上消息时是它的 <c>confirmationRequestId</c>；Host 人工入口没有线上请求时为空，或由那个入口自己给一个 id。
/// </param>
public sealed record StationClearance(
    string ClearanceId,
    string CycleId,
    string VehicleKey,
    int MapId,
    int StationId,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    string? Proof,
    int? WaitingPointMapId,
    int? WaitingPointStationId,
    string? ConfirmedBy,
    string? ConfirmedByRole,
    DateTimeOffset? ConfirmedAt,
    string? VehicleFinalPosition,
    string? OldOrderDisposition,
    IReadOnlyList<string> Assistants,
    string? ClearedCondition = null,
    string? ConfirmationRequestId = null);

/// <summary>清桩的完成证明。字段含义同 <see cref="StationClearance"/>。</summary>
public sealed record StationClearanceCompletion(
    DateTimeOffset CompletedAt,
    string Proof,
    int? WaitingPointMapId,
    int? WaitingPointStationId,
    string? ConfirmedBy,
    string? ConfirmedByRole,
    DateTimeOffset? ConfirmedAt,
    string? VehicleFinalPosition,
    string? OldOrderDisposition,
    IReadOnlyList<string> Assistants,
    string? ClearedCondition = null,
    string? ConfirmationRequestId = null);

/// <summary>清桩记录。一个充电周期至多一次清桩，由唯一约束保证。</summary>
public interface IStationClearanceStore
{
    /// <summary>开始清桩。这个周期已有清桩记录时不写，返回既有那一条。</summary>
    Task<StationClearance> StartAsync(
        string clearanceId,
        string cycleId,
        string vehicleKey,
        int mapId,
        int stationId,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken);

    /// <summary>写上完成证明。已完成时返回假、什么也不改（完成只有一次）；记录不存在时抛 <see cref="InvalidOperationException"/>。</summary>
    Task<bool> CompleteAsync(string clearanceId, StationClearanceCompletion completion, CancellationToken cancellationToken);

    Task<StationClearance?> ReadByCycleAsync(string cycleId, CancellationToken cancellationToken);
}

// ---------------------------------------------------------------------------------------------------------------------
// 服务端持有的人工充电等待（ManualChargingHold，REQ-0171 的退化路径）

/// <summary>一次人工充电等待：何时为什么起、何时告警、何时经哪一个「充电后返回服务」请求解除。</summary>
public sealed record ManualChargingHold(
    string HoldId,
    string VehicleKey,
    string Reason,
    DateTimeOffset Since,
    DateTimeOffset? WarnedAt,
    DateTimeOffset? ReleasedAt,
    string? ReleaseRequestId);

/// <summary>置人工充电等待的结果。</summary>
public enum ManualChargingHoldPlacement
{
    Placed,

    /// <summary>这辆车已在人工充电等待中；什么也没写，原来那一次的原因与时刻不变。</summary>
    AlreadyHeld,
}

/// <summary>
/// 服务端持有的人工充电等待：每车一行当前状态（主键一车一行，谁置上由主键决定）＋ 只追加的经过。当前行与它的经过同一次保存生灭。
/// </summary>
/// <remarks>今天服务端不持有 hold（<c>WireToGateStore</c> 里那段注释）；读法由批次9-06 改，本票只落载体。</remarks>
public interface IManualChargingHoldStore
{
    Task<ManualChargingHoldPlacement> PlaceAsync(
        string holdId, string vehicleKey, string reason, DateTimeOffset since, CancellationToken cancellationToken);

    /// <summary>记下告警时刻。车不在等待中时返回假。</summary>
    Task<bool> MarkWarnedAsync(string vehicleKey, DateTimeOffset warnedAt, CancellationToken cancellationToken);

    /// <summary>经「充电后返回服务」解除：删当前行，在经过上写解除时刻与请求 id。车不在等待中时返回假。</summary>
    Task<bool> ReleaseAsync(
        string vehicleKey, string releaseRequestId, DateTimeOffset releasedAt, CancellationToken cancellationToken);

    /// <summary>这辆车当前的人工充电等待；不在等待中为空。</summary>
    Task<ManualChargingHold?> ReadAsync(string vehicleKey, CancellationToken cancellationToken);

    /// <summary>这辆车的全部经过，按起始时刻排。</summary>
    Task<IReadOnlyList<ManualChargingHold>> ListHistoryAsync(string vehicleKey, CancellationToken cancellationToken);
}

// ---------------------------------------------------------------------------------------------------------------------
// 两类现场确认请求（批次9-08、9-12），照 ManualChargingReturnToServiceRequests：每个 confirmationRequestId 至多判一次

/// <summary>现场确认请求里共有的那部分：谁、哪个会话、以什么方式核验了身份。</summary>
/// <param name="RequestContentHash">请求内容的摘要：同一个 id 带着不同内容重发是冲突，不是重投。</param>
public sealed record FieldConfirmationRequestIdentity(
    string ConfirmationRequestId,
    string AgvId,
    long SessionGeneration,
    string RequestMessageId,
    string RequestContentHash,
    string OperatorId,
    string VerificationMethod,
    DateTimeOffset VerifiedAt,
    DateTimeOffset ObservedAt);

/// <summary>一次判定里共有的那部分。<see cref="ProblemReasonCode"/> 为空当且仅当确认成立。</summary>
public sealed record FieldConfirmationDecision(
    string Outcome,
    string? ProblemReasonCode,
    string? ProblemFieldPath,
    string? ProblemDisplayMessage,
    DateTimeOffset DecidedAt)
{
    public const string Confirmed = "CONFIRMED";
    public const string Rejected = "REJECTED";
}

/// <summary><c>UnableToChargeFieldConfirmationRequested</c> 与它的判定（<c>REQ-0176</c>）。</summary>
/// <param name="ChargingPolicyDecision">协议的 <c>chargingPolicyDecision</c>；拒绝时为空。</param>
public sealed record UnableToChargeFieldConfirmation(
    FieldConfirmationRequestIdentity Request,
    string ChargerStationId,
    string ObservedCondition,
    FieldConfirmationDecision Decision,
    string? ChargingPolicyDecision);

/// <summary><c>ManualStationClearanceConfirmationRequested</c> 与它的判定（<c>REQ-0179</c>）。</summary>
public sealed record ManualStationClearanceConfirmation(
    FieldConfirmationRequestIdentity Request,
    string StationId,
    string? PublicStationFunction,
    string ClearedCondition,
    FieldConfirmationDecision Decision,
    bool StationReleased);

/// <summary>同一个 <c>confirmationRequestId</c> 带着不同内容重发。</summary>
public sealed class FieldConfirmationContentConflictException(string message) : InvalidOperationException(message);

/// <summary>
/// 两类现场确认请求的「至多判一次」：第一次的判定写下，之后同一个 id 的请求一律拿回那一次的判定，不再判。
/// </summary>
public interface IFieldConfirmationRequestStore
{
    /// <summary>
    /// 写下这次判定并返回它；同一个 id 已判过时不写，返回那一次的判定（<paramref name="confirmation"/> 里的判定被丢弃）。
    /// 同一个 id 内容不同（摘要或车不同）时抛 <see cref="FieldConfirmationContentConflictException"/>。
    /// </summary>
    Task<UnableToChargeFieldConfirmation> DecideUnableToChargeAsync(
        UnableToChargeFieldConfirmation confirmation, CancellationToken cancellationToken);

    Task<UnableToChargeFieldConfirmation?> ReadUnableToChargeAsync(
        string confirmationRequestId, CancellationToken cancellationToken);

    /// <summary>同 <see cref="DecideUnableToChargeAsync"/>，人工清桩确认。</summary>
    Task<ManualStationClearanceConfirmation> DecideManualStationClearanceAsync(
        ManualStationClearanceConfirmation confirmation, CancellationToken cancellationToken);

    Task<ManualStationClearanceConfirmation?> ReadManualStationClearanceAsync(
        string confirmationRequestId, CancellationToken cancellationToken);
}
