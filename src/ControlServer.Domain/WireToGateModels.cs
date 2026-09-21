namespace ControlServer.Domain;

public sealed record SessionIdentity(
    string AgvId,
    long SessionGeneration,
    string ProtocolCommit,
    string ManifestSha256,
    string ProfileId,
    int ProtocolVersion);

public enum SessionReadiness
{
    RecoveryRequired,
    Ready
}

public sealed record SessionReadinessDecision(SessionReadiness Readiness, string ReasonCode);

public sealed record StationOperationPlan(
    string SlotOperationAttemptId,
    string DemandId,
    string SublotId,
    IReadOnlyList<int> TargetSlots,
    SlotOperationType OperationType,
    long ForcedRecoveryGeneration,
    string ContentHash,
    DateTimeOffset CreatedAt,
    string? AdmissionStationId = null,
    string? AdmissionTaskType = null);

public sealed record StationTaskTypeAdmission(string StationId, string TaskType);

public sealed record AdmissionPolicyDefinition(
    long Version,
    string DeploymentId,
    IReadOnlyList<StationTaskTypeAdmission> Relations,
    DateTimeOffset ImportedAt);

public sealed record StationOperationResult(
    string ResultId,
    string SlotOperationAttemptId,
    string DemandId,
    SlotOperationType OperationType,
    string OverallOutcome,
    IReadOnlyList<SlotPhysicalEvidence> SlotEvidence,
    bool AllSlotsCompleted,
    DateTimeOffset ObservedAt,
    string ResultContentSha256,
    string WireContentSha256,
    IReadOnlyList<SlotOutcomeReport>? SlotOutcomes = null);

public enum StationOperationStatus
{
    Prepared,
    Committed,
    Cancelled,
    /// <summary>
    /// A load that missed its target and said so completely, after its station deadline: every slot's
    /// state known, door locked, unlock output reset, and a reason with a terminal state
    /// (<see cref="DeterminateLoadFailure"/>). Nothing is uncertain, so nobody is asked to recover it; the
    /// runtime ends the demand. Unload never reaches it.
    /// </summary>
    Failed,
    RecoveryRequired
}

public enum SlotBusinessState
{
    Empty,
    Occupied,
    Unknown
}

public sealed record SlotPhysicalEvidence(
    int SlotNumber,
    SlotBusinessState State,
    bool DoorLocked,
    bool UnlockOutputReset);

public sealed record SafetyCheckObservation(
    string CheckId,
    long SafetyStateVersion,
    bool DepartureSafe,
    DateTimeOffset ObservedAt,
    DateTimeOffset ValidUntil);

public sealed record VehicleBusinessBlockingFact(
    string ReasonCode,
    string SubjectType,
    string? SubjectId);

/// <summary>
/// Carries no observation timestamp: the snapshot keeps one deterministic messageId per journey
/// stage, so its wire payload has to be reproducible for that revision. The publisher stamps
/// observedAt from the envelope's frozen sentAt instead.
/// </summary>
/// <remarks>
/// <see cref="ChargingCycleState"/> and <see cref="LoadingPhase"/> are the protocol <c>2.0.0</c>
/// candidate's items 5 and 6, and their values follow the semantic tables in
/// <c>8005-agv-program</c> commit <c>db5a1d14</c> (<c>8005-agv-program#94</c>). The publisher refuses
/// a transport projection without a loading phase and a non-transport one with one, because the
/// schema accepts both and the outbound schema gate could not tell.
/// </remarks>
public sealed record VehicleBusinessProjection(
    long Revision,
    string Readiness,
    string? ActivePurpose,
    bool ManualChargingHold,
    string BatteryState,
    string ChargingCycleState,
    LoadingPhaseProjection? LoadingPhase,
    IReadOnlyList<VehicleBusinessBlockingFact> BlockingFacts);

/// <summary>
/// The <c>activePurpose</c> values this server uses by name.
/// </summary>
public static class VehicleActivePurposes
{
    /// <summary>The vehicle is carrying a demand; the only purpose the v2 journey runtime can be in.</summary>
    public const string Transport = "TRANSPORT";
}

/// <summary>
/// <c>VehicleBusinessStateSnapshot.loadingPhase</c>: where the loading phase of a transport journey
/// stands.
/// </summary>
/// <remarks>
/// <para>
/// 批次7-07（control-server#212）起四个状态都会出现，而且<b>从旅程行上的三列读，不再由阶段推</b>
/// （<c>JourneyRuntimeRow.LoadingPhaseState</c>、<c>LoadingClosedReason</c>、<c>CargoHoldingStartedAt</c>）。
/// 取值的语义是 program#94 那张表：<c>LOADING</c> 装货进行中、未等单；<c>CARGO_HOLDING_WAIT</c> 持货等单（REQ-0354）；
/// <c>VEHICLE_FULL</c> 不再等单，但离开最后一个装货停靠之前仍可追加；<c>CLOSED</c> 装货阶段已结束。
/// </para>
/// <para>
/// 分区都禁止途中追加的旅程（参数批准之前的全部旅程）仍然只出现下面两个值，与批次 7 之前逐条相同。
/// <c>WAITING_STATION_YIELD</c> 只有让站才产生，归 control-server#213。
/// </para>
/// </remarks>
public sealed record LoadingPhaseProjection(
    string State,
    DateTimeOffset? CargoHoldingDeadlineAt,
    string? ClosedReason)
{
    /// <summary>The pickup's planned loading has not ended.</summary>
    public static LoadingPhaseProjection Loading { get; } = new("LOADING", null, null);

    /// <summary>
    /// The pickup's load batch has closed safely; the journey no longer takes new demands and carries
    /// on to unload.
    /// </summary>
    public static LoadingPhaseProjection PlannedLoadingComplete { get; } =
        new("CLOSED", null, "PLANNED_LOADING_COMPLETE");
}

/// <summary><c>loadingPhase.state</c> 的四个取值（协议 <c>2.0.0</c>，program#94）。</summary>
public static class LoadingPhaseStates
{
    public const string Loading = "LOADING";
    public const string CargoHoldingWait = "CARGO_HOLDING_WAIT";
    public const string VehicleFull = "VEHICLE_FULL";
    public const string Closed = "CLOSED";
}

/// <summary><c>loadingPhase.closedReason</c> 的四个取值；只在 <see cref="LoadingPhaseStates.Closed"/> 时非空。</summary>
public static class LoadingClosedReasons
{
    public const string VehicleFull = "VEHICLE_FULL";
    public const string CargoHoldingTimeout = "CARGO_HOLDING_TIMEOUT";
    public const string WaitingStationYield = "WAITING_STATION_YIELD";
    public const string PlannedLoadingComplete = "PLANNED_LOADING_COMPLETE";
}

public sealed record CurrentStopWorklistItem(
    string DemandId,
    string TransportDemandKey,
    string Sublot,
    string WorkType,
    string StopRole,
    int ExpectedBasketCount);

/// <remarks>
/// <see cref="StationDepartureDeadlineAt"/> is when the station departure wait (ADR-cross-0055) runs
/// out, taken from <c>JourneyRuntimeEngine.StationDepartureDeadline</c> and nowhere else; null where
/// no such wait applies -- the drop-off stop, or the timeout switched off.
/// </remarks>
public sealed record CurrentStopWorklistProjection(
    string StationId,
    long Revision,
    string? OperationSessionId,
    DateTimeOffset? StationDepartureDeadlineAt,
    IReadOnlyList<CurrentStopWorklistItem> Items);

/// <summary>
/// One leg of the plan the vehicle is shown.
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="LegType"/> is <c>TO_PICKUP</c> or <c>TO_DROPOFF</c>, never <c>TO_GATE</c>.</b>
/// The gate is one destination among the six task types v2 froze, and the protocol renamed the leg
/// after what it is rather than after this profile's only instance of it -- the same rename that
/// took <c>CV-GATE-UNLOAD-ALL-EMPTY</c> to <c>CV-DESTINATION-UNLOAD-ALL-EMPTY</c>. The server's own
/// internal movement purpose is still <c>TO_GATE</c>; that string is a RIoT intent and an arrival
/// purpose, not a wire value, and the two must not be conflated.
/// </para>
/// <para>
/// <see cref="DemandId"/> moved here from the payload's top level in v2: with <c>legs.maxItems</c>
/// at 9 a plan can span several demands, so one demand id for the whole snapshot has no meaning.
/// <see cref="PublicStationFunction"/> is nullable and null today -- binding a station to a public
/// function is <c>FP-C9b</c>, batch 4, and naming one from the leg's own station id would be
/// inventing that capability rather than reporting it.
/// </para>
/// </remarks>
public sealed record UpcomingMovementLeg(
    string MovementLegId,
    string? LegType,
    string StopPurposeCategory,
    string? DemandId,
    string? PublicStationFunction,
    int Sequence,
    string StationId,
    string MapId,
    string State);

public sealed record UpcomingStopPlanProjection(
    long Revision,
    IReadOnlyList<UpcomingMovementLeg> Legs);

/// <remarks>
/// Protocol <c>2.0.0</c> item 2: the vehicle is given the sublots of the dispatch scope rather than
/// one demand's, and names no demand back. With one demand per journey the scope is that demand's
/// sublot. Within one <c>(OperationSessionId, WorklistRevision)</c> the set must not change -- that
/// pair is the message's business deduplication key.
/// </remarks>
public sealed record SublotEntryRequest(
    string OperationSessionId,
    string StationId,
    long WorklistRevision,
    IReadOnlyList<string> ExpectedSublots);

/// <summary>
/// <c>$defs/Problem</c>: why a request was refused. <see cref="ReasonCode"/> is an <c>ErrorCode</c>, so
/// it comes from <c>ServerReasonCodes</c>.
/// </summary>
public sealed record WireProblem(
    string ReasonCode,
    string? FieldPath,
    string? DisplayMessage);

/// <summary>
/// The payload of <c>SublotRejected</c>: the server refusing one <c>SublotSubmitted</c>.
/// </summary>
/// <remarks>
/// Protocol <c>2.0.0</c> item 2. <see cref="DemandId"/> is null when the sublot resolves to no demand
/// in the dispatch scope (<c>SUBLOT_NOT_IN_DISPATCH_SCOPE</c>), and names the resolved demand when the
/// sublot is in scope but fails its re-check. <see cref="RejectedSublot"/> is the value the operator
/// entered. Only the type exists here; sending it, answering the submission with its messageId as
/// <c>correlationId</c>, is <c>8005-agv-control-server#82</c>.
/// </remarks>
public sealed record SublotRejection(
    string? DemandId,
    string OperationSessionId,
    WireProblem Problem,
    long CurrentWorklistRevision,
    string RejectedSublot);

public enum SlotOperationType
{
    Load,
    Unload
}

public sealed record SlotOperationCommand(
    string? CorrelationId,
    string DemandId,
    string SublotId,
    string OperationSessionId,
    string SlotOperationAttemptId,
    SlotOperationType OperationType,
    IReadOnlyList<int> Slots,
    long ForcedRecoveryGeneration,
    string CommandContentSha256);

public sealed record PreDepartureSafetyCheckCommand(
    string PreDepartureSafetyCheckId,
    string DemandId,
    string MovementLegId,
    long ExpectedSafetyStateVersion,
    string TargetStationId);

public enum DemandExecutionStatus
{
    Accepted,
    Succeeded,
    Cancelled,
    RecoveryRequired
}

public enum JourneyRuntimeStage
{
    AwaitingPickupArrival,
    AwaitingSublot,
    AwaitingLoadResult,
    /// <summary>
    /// The load is committed and the vehicle is still at the pickup: the one window in which an
    /// ordinary mis-placement may be corrected (REQ-0237, ADR-cross-0054/0055). Departure safety is
    /// not asked for until the station departure wait has run out with no correction open.
    /// </summary>
    AwaitingStationDeparture,
    AwaitingDepartureSafety,
    AwaitingGateArrival,
    AwaitingUnloadResult,
    Completed,
    Blocked
}

public sealed record JourneyExecutionPlan(
    string AgvId,
    string VehicleKey,
    long AgvLifecycleGeneration,
    int MapId,
    string MapIdentity,
    string DispatchZone,
    string RouteEvidenceId,
    string PickupStationId,
    int PickupStationRiotId,
    string GateStationId,
    int GateStationRiotId,
    int ExpectedBasketCount,
    IReadOnlyList<int> TargetSlots,
    string OperationSessionId,
    string PickupMovementLegId,
    string PickupUpperId,
    string GateMovementLegId,
    string GateUpperId,
    long DispatchGeneration,
    DateTimeOffset CreatedAt,
    long? AreaAssignmentVersion = null,
    string? RequiredSlotPosition = null,
    long? TaskTypeStationRuleVersion = null,
    long? TaskTypeStationBindingSetVersion = null,
    long? StationCatalogRevision = null,
    string? IdentityKey = null)
{
    /// <summary>
    /// 这趟受理派生身份（旅程 id、停靠 id、报文与 attempt id）所用的键：需求第一次受理时就是需求 id，改派之后带上代次
    /// （<c>JourneyIdentity.DerivationKey</c>，批次7-10，control-server#215）。为空即需求 id——那是改派出现之前唯一的形状。
    /// </summary>
    public string DerivationKeyFor(string demandId) => IdentityKey ?? demandId;
}

/// <summary>
/// 把一条需求追加进一辆在途车已有旅程时，要一次写下的全部（票面第 3 条，批次7-06，control-server#211）。
/// </summary>
/// <remarks>
/// <para>
/// 它<b>复用</b> <see cref="JourneyExecutionPlan"/> 来携带这条需求自己的那一份：端点、仓位、花篮数、
/// 派车代次与三样冻结版本，因为追加受理要写的冻结与受理时一字不差——一条需求不会因为它是被追加进来的，
/// 就少冻结一个版本。旅程层面的那几样（会话、订单、租约）在计划里仍然填着，但追加路径不读它们：
/// 那辆车已经被这趟旅程占着。
/// </para>
/// <para>
/// <see cref="Resequenced"/> 是插入之后<b>全部</b>停靠的新序位，包括没动的那些：序位是可变的序位，
/// 身份是 <c>StopId</c>（MVP 拿序号当身份，一插队后面每个停靠的身份都跟着变）。整张表一起给，
/// 是为了「插在哪」这个决定只由一处做出，而不是让写库的一方再推一遍。
/// </para>
/// </remarks>
public sealed record JourneyAppendPlan(
    string JourneyId,
    string DemandId,
    JourneyExecutionPlan Demand,
    string PickupStopId,
    string UnloadStopId,
    string DispatchZone,
    long? DispatchZoneParameterVersion,
    IReadOnlyList<JourneyStopSequenceChange> Resequenced,
    DateTimeOffset AddedAt);

/// <summary>一个停靠插入之后的新序位。</summary>
public sealed record JourneyStopSequenceChange(string StopId, int Sequence);

public enum ConnectionRecoveryStatus
{
    Connected,
    SafelyFinishing,
    AwaitingHandshake,
    RecoveryRequired
}

public enum OperationResultDisposition
{
    Accepted,
    /// <summary>Judged <see cref="StationOperationStatus.Failed"/>: no recovery is asked for.</summary>
    DeterminateFailure,
    RecoveryRequired,
    /// <summary>
    /// Judged <see cref="StationOperationStatus.RecoveryRequired"/> although it looked determinate:
    /// received before the station deadline (<see cref="DeterminateLoadFailure.FailedBeforeStationDeadline"/>).
    /// </summary>
    FailedBeforeStationDeadline,
    /// <summary>
    /// Judged <see cref="StationOperationStatus.RecoveryRequired"/> although it looked determinate: its
    /// reason has no terminal state (<see cref="DeterminateLoadFailure.ReasonWithoutTerminalState"/>).
    /// </summary>
    FailureReasonWithoutTerminalState,
    HistoricalOnly,
    Replay
}

/// <summary>
/// A ManualChargingReturnToServiceRequested payload, decoded. The protocol declares
/// <c>observedBatteryPercent</c> as <c>number | null</c>, so an absent reading is a value the
/// administrator supplied rather than a violation.
/// </summary>
public sealed record ManualChargingReturnToServiceRequest(
    string RequestId,
    string AgvId,
    long SessionGeneration,
    string RequestMessageId,
    string RequestContentHash,
    string AdministratorId,
    string AdministratorRole,
    string Reason,
    double? ObservedBatteryPercent);

/// <summary>
/// What the server decided about one such request, durable so the same <c>requestId</c> arriving
/// again returns the same conclusion instead of deciding twice.
/// </summary>
/// <remarks>
/// <c>ProblemReasonCode</c> is null exactly when <c>Outcome</c> is
/// <c>RETURNED_TO_ELIGIBILITY_EVALUATION</c>; the schema types the field as
/// <c>Problem | null</c> and the rejected outcome is the only one carrying one.
/// </remarks>
public sealed record ManualChargingReturnToServiceDecision(
    string Outcome,
    string? ProblemReasonCode,
    string? ProblemFieldPath,
    string? ProblemDisplayMessage,
    long VehicleBusinessStateRevision)
{
    public const string ReturnedToEligibilityEvaluation = "RETURNED_TO_ELIGIBILITY_EVALUATION";
    public const string Rejected = "REJECTED";
}

public sealed class ProtocolIdentityMismatchException(string message) : InvalidOperationException(message);
public sealed class StaleSessionGenerationException(string message) : InvalidOperationException(message);
public sealed class ProtocolContentConflictException(string message) : InvalidOperationException(message);
public sealed class UnsafePhysicalEvidenceException(string message) : InvalidOperationException(message);
public sealed class UnsafeMovementAuthorizationException(string message) : InvalidOperationException(message);
public sealed class ActiveUnlockSetExpansionException(string message) : InvalidOperationException(message);
public sealed class BusinessIdentityConflictException(string message) : InvalidOperationException(message);

/// <summary>
/// A journey plan carries the task type station rule and binding set versions but not the station catalog revision its
/// endpoints were taken from, so accepting it would freeze the versions and not the endpoints (control-server#198).
/// </summary>
public sealed class JourneyPlanFreezeIncompleteException(string message) : InvalidOperationException(message);
