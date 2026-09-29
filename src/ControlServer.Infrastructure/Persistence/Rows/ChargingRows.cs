namespace ControlServer.Infrastructure.Persistence;

// 批次 9 建表票 control-server#399 的行。每一类只经 ChargingStores.cs 里对应的存储写；本票没有运行时读者。

/// <summary>充电桩名册的一个版本（受治理配置，照等待点登记）。只追加，写入即不可改。</summary>
public sealed class ChargerRosterVersionRow
{
    /// <summary>单调递增，从 1 开始。</summary>
    public long Version { get; set; }

    public required string ContentSha256 { get; set; }
    public string? SnapshotId { get; set; }
    public DateTimeOffset LoadedAt { get; set; }
    public required string Source { get; set; }
    public required string ApprovedBy { get; set; }
    public required string ApprovalBasis { get; set; }
    public string? ChangeNote { get; set; }
}

/// <summary>某一版名册上的一个桩。主键 <c>(Version, MapId, StationId)</c>。一版没有任何一行即「名册置空」。</summary>
public sealed class ChargerRosterEntryRow
{
    public long Version { get; set; }
    public int MapId { get; set; }

    /// <summary>RIoT 站号。</summary>
    public int StationId { get; set; }

    public required string StationName { get; set; }

    /// <summary>登记时核对到的进点站号。</summary>
    public int? EntryStationId { get; set; }

    /// <summary>登记时核对到的出点站号。</summary>
    public int? ExitStationId { get; set; }
}

/// <summary>某一版名册上某个桩的候选车辆。一个桩没有任何一行，即对投运名册里的全部车开放。</summary>
public sealed class ChargerRosterVehicleScopeRow
{
    public long Version { get; set; }
    public int MapId { get; set; }
    public int StationId { get; set; }
    public required string VehicleKey { get; set; }
}

/// <summary><c>ChargingPolicyVersion</c> 的一个版本与它的内容。只追加，写入即不可改；批准与激活在另两张表。</summary>
public sealed class ChargingPolicyVersionRow
{
    public long Version { get; set; }
    public required string ContentSha256 { get; set; }
    public string? SnapshotId { get; set; }
    public DateTimeOffset WrittenAt { get; set; }
    public string? ChangeNote { get; set; }
    public int MinimumPostTaskBatteryMarginPercent { get; set; }
    public int MandatoryChargeEntryThresholdPercent { get; set; }
    public int ChargingCompletionThresholdPercent { get; set; }
    public int EstimatedTaskConsumptionPercent { get; set; }
    public int ProgressStabilizationSeconds { get; set; }
    public int ProgressObservationWindowSeconds { get; set; }
    public int ProgressMinimumIncreasePercent { get; set; }
}

/// <summary>某一版策略适用的一辆车。一个版本没有任何一行，即适用全部投运车辆。</summary>
public sealed class ChargingPolicyVehicleScopeRow
{
    public long Version { get; set; }
    public required string VehicleKey { get; set; }
}

/// <summary>一次策略批准。只追加。</summary>
public sealed class ChargingPolicyApprovalRow
{
    public required string ApprovalId { get; set; }
    public long Version { get; set; }
    public required string ApprovedBy { get; set; }
    public required string ApproverRole { get; set; }
    public DateTimeOffset ApprovedAt { get; set; }
    public required string BasisReference { get; set; }
    public required string Source { get; set; }
}

/// <summary>一次策略激活。只追加；<see cref="Sequence"/> 唯一，最大的那一次是当前生效。</summary>
public sealed class ChargingPolicyActivationRow
{
    public required string ActivationId { get; set; }
    public long Sequence { get; set; }
    public long Version { get; set; }
    public DateTimeOffset ActivatedAt { get; set; }
    public required string ActivatedBy { get; set; }
}

/// <summary>
/// 一个充电周期。一车至多一行 <see cref="Phase"/> 不是 <c>ENDED</c>，由过滤唯一索引保证。<see cref="Version"/> 是并发令牌，由存储加一。
/// </summary>
public sealed class ChargingCycleRow
{
    public required string CycleId { get; set; }
    public required string VehicleKey { get; set; }
    public required string JourneyId { get; set; }
    public int MapId { get; set; }
    public int StationId { get; set; }
    public long ChargerRosterVersion { get; set; }
    public long ChargingPolicyVersion { get; set; }
    public required string WireState { get; set; }
    public required string Phase { get; set; }
    public string? UpperId { get; set; }
    public DateTimeOffset AllocatedAt { get; set; }
    public DateTimeOffset? OrderConfirmedAt { get; set; }
    public DateTimeOffset? ArrivedAt { get; set; }
    public DateTimeOffset? FirstChargingSeenAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? DepartedAt { get; set; }
    public DateTimeOffset? ReleasedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public string? EndReason { get; set; }
    public DateTimeOffset? ObservationWindowStartedAt { get; set; }
    public int? ObservationWindowStartPercent { get; set; }
    public DateTimeOffset? LastSampleAt { get; set; }
    public int? LastSamplePercent { get; set; }
    public long Version { get; set; }
}

/// <summary>一次桩分配暂停（<c>REQ-0177</c> 的全部字段）。只追加，从不改写；恢复在 <see cref="ChargingStationRecoveryRow"/>。</summary>
public sealed class ChargingStationAllocationHoldRow
{
    public required string HoldId { get; set; }
    public required string IdempotencyKey { get; set; }
    public required string Trigger { get; set; }
    public required string RootCause { get; set; }
    public int MapId { get; set; }
    public int StationId { get; set; }
    public long? ChargerRosterVersion { get; set; }
    public string? VehicleKey { get; set; }
    public string? ReservationRecordId { get; set; }
    public string? CycleId { get; set; }
    public string? UpperId { get; set; }
    public string? OrderId { get; set; }
    public DateTimeOffset? ArrivedAt { get; set; }
    public DateTimeOffset? ChargingStartedAt { get; set; }
    public DateTimeOffset? FailedAt { get; set; }
    public DateTimeOffset? FinalHangAt { get; set; }
    public DateTimeOffset? ConfirmedAt { get; set; }
    public DateTimeOffset HeldAt { get; set; }
    public string? RawPositionJson { get; set; }
    public string? RawOrderJson { get; set; }
    public string? RawActionResultJson { get; set; }
    public string? RawBatteryJson { get; set; }
    public string? EvidenceReference { get; set; }
    public string? RiotBuild { get; set; }
    public string? RiotContractVersion { get; set; }
    public string? ConfirmedByPersonId { get; set; }
    public string? ConfirmedByRole { get; set; }
    public DateTimeOffset? ConfirmedAuthenticatedAt { get; set; }
    public string? SiteDisposition { get; set; }
}

/// <summary><c>ChargingStationRecoveryConfirmation</c>：恢复一次桩暂停。一次暂停至多一行（<see cref="HoldId"/> 唯一）。</summary>
public sealed class ChargingStationRecoveryRow
{
    public required string RecoveryId { get; set; }
    public required string HoldId { get; set; }
    public required string RecoveredBy { get; set; }
    public required string RecovererRole { get; set; }
    public DateTimeOffset RecoveredAt { get; set; }
    public required string Basis { get; set; }
}

/// <summary>一次车辆充电资格暂停。只追加；恢复在 <see cref="VehicleChargingEligibilityRecoveryRow"/>。</summary>
public sealed class VehicleChargingEligibilityHoldRow
{
    public required string HoldId { get; set; }
    public required string IdempotencyKey { get; set; }
    public required string VehicleKey { get; set; }
    public string? CycleId { get; set; }
    public required string Reason { get; set; }
    public DateTimeOffset HeldAt { get; set; }
    public string? EvidenceReference { get; set; }
}

/// <summary>恢复一次车辆充电资格暂停。一次暂停至多一行。</summary>
public sealed class VehicleChargingEligibilityRecoveryRow
{
    public required string RecoveryId { get; set; }
    public required string HoldId { get; set; }
    public required string RecoveredBy { get; set; }
    public required string RecovererRole { get; set; }
    public DateTimeOffset RecoveredAt { get; set; }
    public required string Basis { get; set; }
}

/// <summary>一次清桩（<c>REQ-0178</c>、<c>REQ-0179</c>）。一个周期至多一行（<see cref="CycleId"/> 唯一）。</summary>
public sealed class StationClearanceRow
{
    public required string ClearanceId { get; set; }
    public required string CycleId { get; set; }
    public required string VehicleKey { get; set; }
    public int MapId { get; set; }
    public int StationId { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? Proof { get; set; }
    public int? WaitingPointMapId { get; set; }
    public int? WaitingPointStationId { get; set; }
    public string? ConfirmedBy { get; set; }
    public string? ConfirmedByRole { get; set; }
    public DateTimeOffset? ConfirmedAt { get; set; }
    public string? VehicleFinalPosition { get; set; }
    public string? OldOrderDisposition { get; set; }

    /// <summary>协助者（JSON 字符串数组），另记于确认人之外；没有协助者为 <c>[]</c>。</summary>
    public required string AssistantsJson { get; set; }

    /// <summary>人工证明时现场确认的腾空情况（协议 <c>clearedCondition</c>）；系统证明时为空。</summary>
    public string? ClearedCondition { get; set; }

    /// <summary>人工证明来自哪一个现场确认请求；Host 人工入口没有线上请求时可以为空。</summary>
    public string? ConfirmationRequestId { get; set; }
}

/// <summary>服务端持有的人工充电等待，每车一行当前状态。谁置上由主键 <see cref="VehicleKey"/> 决定；经过在 <see cref="ManualChargingHoldRecordRow"/>。</summary>
public sealed class ManualChargingHoldRow
{
    public required string VehicleKey { get; set; }
    public required string HoldId { get; set; }
    public required string Reason { get; set; }
    public DateTimeOffset Since { get; set; }
    public DateTimeOffset? WarnedAt { get; set; }
}

/// <summary>人工充电等待的一次经过。证据不是仲裁者：<see cref="VehicleKey"/> 上的索引故意不唯一。</summary>
public sealed class ManualChargingHoldRecordRow
{
    public required string HoldId { get; set; }
    public required string VehicleKey { get; set; }
    public required string Reason { get; set; }
    public DateTimeOffset Since { get; set; }
    public DateTimeOffset? WarnedAt { get; set; }
    public DateTimeOffset? ReleasedAt { get; set; }
    public string? ReleaseRequestId { get; set; }
}

/// <summary><c>UnableToChargeFieldConfirmationRequested</c> 与它唯一的一次判定。</summary>
public sealed class UnableToChargeFieldConfirmationRow
{
    public required string ConfirmationRequestId { get; set; }
    public required string AgvId { get; set; }
    public long SessionGeneration { get; set; }
    public required string RequestMessageId { get; set; }
    public required string RequestContentHash { get; set; }
    public required string OperatorId { get; set; }
    public required string VerificationMethod { get; set; }
    public DateTimeOffset VerifiedAt { get; set; }
    public DateTimeOffset ObservedAt { get; set; }
    public required string ChargerStationId { get; set; }
    public required string ObservedCondition { get; set; }
    public required string Outcome { get; set; }
    public string? ProblemReasonCode { get; set; }
    public string? ProblemFieldPath { get; set; }
    public string? ProblemDisplayMessage { get; set; }
    public string? ChargingPolicyDecision { get; set; }
    public DateTimeOffset DecidedAt { get; set; }
}

/// <summary><c>ManualStationClearanceConfirmationRequested</c> 与它唯一的一次判定。</summary>
public sealed class ManualStationClearanceConfirmationRow
{
    public required string ConfirmationRequestId { get; set; }
    public required string AgvId { get; set; }
    public long SessionGeneration { get; set; }
    public required string RequestMessageId { get; set; }
    public required string RequestContentHash { get; set; }
    public required string OperatorId { get; set; }
    public required string VerificationMethod { get; set; }
    public DateTimeOffset VerifiedAt { get; set; }
    public DateTimeOffset ObservedAt { get; set; }
    public required string StationId { get; set; }
    public string? PublicStationFunction { get; set; }
    public required string ClearedCondition { get; set; }
    public required string Outcome { get; set; }
    public string? ProblemReasonCode { get; set; }
    public string? ProblemFieldPath { get; set; }
    public string? ProblemDisplayMessage { get; set; }
    public bool StationReleased { get; set; }
    public DateTimeOffset DecidedAt { get; set; }
}
