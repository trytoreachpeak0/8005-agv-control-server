using ControlServer.Application;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Persistence;

namespace ControlServer.Host.Runtime.Withdrawal;

/// <summary>
/// 一次人工撤下旅程的请求（control-server#771）。文本字段可空：缺了是一条拒绝理由，不是格式错误。
/// </summary>
/// <param name="JourneyId">要撤的那一趟。点名，不推断「这辆车当前的旅程」。</param>
/// <param name="VehicleKey">那一趟的车（RIoT <c>VehicleKey</c>），必须与旅程行对得上。</param>
/// <param name="SiteVerification">现场核实的记录引用（照片、会话记录的位置）。</param>
/// <param name="RiotVerification">现场看过 RIoT、本车没有未结束订单的记录引用。</param>
/// <param name="SlotFacts">
/// 现场看过的仓位事实，<c>&lt;仓号&gt;=&lt;事实&gt;</c>，逗号分隔，例如 <c>5=EMPTY_LOCKED</c>。只在旅程挂着一次没结的装货时要。
/// </param>
/// <param name="CargoLocation">那一篮此刻在哪里（例如「在机台上」）。只在旅程挂着一次没结的装货时要。</param>
/// <param name="ClaimedRole">操作员自述的角色，照录，不认证任何东西。</param>
/// <param name="ConfirmationCode">演练给出的确认码。撤下时必填；演练时不看。</param>
public sealed record JourneyWithdrawalRequest(
    string? JourneyId,
    string? VehicleKey,
    string? OperatorId,
    string? Reason,
    string? SiteVerification,
    string? RiotVerification,
    string? SlotFacts = null,
    string? CargoLocation = null,
    string? ClaimedRole = null,
    string? ConfirmationCode = null);

/// <summary>撤下会动到的一条需求：撤下之前的样子，和它的去向。</summary>
public sealed record JourneyWithdrawalDemand(
    string DemandId,
    string TransportDemandKey,
    string MembershipStatus,
    string DemandStatus,
    string PickupStopId,
    string Disposition);

/// <summary>旅程挂着的那一次没结的装货：撤下时结成 <c>Cancelled</c>。</summary>
/// <param name="ReportedSlots">车报来的那次结果里，每个目标仓位的读数（<c>5:EMPTY:LOCKED:RESET</c>）。</param>
public sealed record JourneyWithdrawalOperation(
    string SlotOperationAttemptId,
    string DemandId,
    string Status,
    IReadOnlyList<int> TargetSlots,
    IReadOnlyList<string> ReportedSlots);

/// <summary>这一趟名下的一条订单意图。撤下不改它，只列出来、绑进确认码。</summary>
public sealed record JourneyWithdrawalIntent(string UpperId, string Status, string? OrderId);

/// <summary>
/// 撤下会做的每一件事。演练与撤下各算一次，两次算出的确认码相同才执行：中间任何一项变了，执行就拒。
/// </summary>
public sealed record JourneyWithdrawalPlan(
    string JourneyId,
    string AgvId,
    string VehicleKey,
    string Stage,
    string? BlockReasonCode,
    DateTimeOffset JourneyUpdatedAt,
    IReadOnlyList<JourneyWithdrawalDemand> Demands,
    JourneyWithdrawalOperation? UnresolvedLoad,
    IReadOnlyList<JourneyWithdrawalIntent> OrderIntents,
    string? SessionReadiness,
    string? SessionReasonCode);

/// <summary>演练或撤下的结果。</summary>
/// <param name="Outcome"><c>PLANNED</c>、<c>WITHDRAWN</c>、<c>ALREADY_DONE</c> 或 <c>REJECTED</c>。</param>
/// <param name="AuditRecordId">撤下（含被拒）写的管理员审计；演练不写，为空。</param>
public sealed record JourneyWithdrawalResult(
    string Outcome,
    IReadOnlyList<string> Codes,
    JourneyWithdrawalPlan? Plan,
    string? ConfirmationCode,
    string? AuditRecordId);

/// <summary>
/// 人工撤下一趟旅程（control-server#771）：车上没货时，旅程收尾，需求退回待派、不抑制；挂着一次结果未知的装货时，那一次结成取消。
/// </summary>
public static class JourneyWithdrawal
{
    /// <summary>旅程收尾码。</summary>
    public const string WithdrawnReason = "WITHDRAWN_BY_OPERATOR";

    /// <summary>每一次撤下请求（含被拒）写的管理员审计动作。</summary>
    public const string AuditAction = "JOURNEY_MANUAL_WITHDRAWAL";

    /// <summary>需求的去向：退回待派。</summary>
    public const string ReleaseToBacklog = "RELEASE_TO_BACKLOG";

    /// <summary>现场看过、仓是空的、门锁着。今天唯一接受的仓位事实（D2：真有货就拒绝撤下）。</summary>
    public const string SlotEmptyLocked = "EMPTY_LOCKED";

    /// <summary>现场看见仓里有货。</summary>
    public const string SlotOccupied = "OCCUPIED";

    public const string OperatorRequired = "OPERATOR_REQUIRED";
    public const string ReasonRequired = "REASON_REQUIRED";
    public const string SiteVerificationRequired = "SITE_VERIFICATION_REQUIRED";
    public const string RiotVerificationRequired = "RIOT_VERIFICATION_REQUIRED";
    public const string JourneyRequired = "JOURNEY_REQUIRED";
    public const string VehicleRequired = "VEHICLE_REQUIRED";
    public const string FieldTooLong = "FIELD_TOO_LONG";
    public const string JourneyNotFound = "JOURNEY_NOT_FOUND";
    public const string JourneyAlreadyCompleted = "JOURNEY_ALREADY_COMPLETED";
    public const string VehicleMismatch = "VEHICLE_MISMATCH";
    public const string JourneyKindNotWithdrawable = "JOURNEY_KIND_NOT_WITHDRAWABLE";
    public const string CargoOnBoard = "CARGO_ON_BOARD";
    public const string OtherOperationUnresolved = "OTHER_OPERATION_UNRESOLVED";
    public const string SlotFactsRequired = "SLOT_FACTS_REQUIRED";
    public const string SlotFactsMalformed = "SLOT_FACTS_MALFORMED";
    public const string SlotFactsMismatch = "SLOT_FACTS_DO_NOT_COVER_THE_TARGET_SLOTS";
    public const string SiteReportsCargo = "SITE_REPORTS_CARGO";
    public const string CargoLocationRequired = "CARGO_LOCATION_REQUIRED";
    public const string SlotEvidenceMissing = "SLOT_EVIDENCE_MISSING";
    public const string SlotEvidenceNotEmpty = "SLOT_EVIDENCE_NOT_EMPTY";
    public const string DoorNotProvenClosed = "DOOR_NOT_PROVEN_CLOSED";
    public const string RecoverySessionOpen = "RECOVERY_SESSION_OPEN";
    public const string SlotFaultDeclarationOpen = "SLOT_FAULT_DECLARATION_OPEN";
    public const string RebuildRecordOpen = "REBUILD_RECORD_OPEN";
    public const string OrderOutcomeUnknown = "ORDER_OUTCOME_UNKNOWN";
    public const string FaultInEffect = "FAULT_IN_EFFECT";
    public const string EmergencyStopOpen = "EMERGENCY_STOP_OPEN";
    public const string FaultedCargoBound = "FAULTED_CARGO_BOUND";
    public const string ConfirmationRequired = "CONFIRMATION_REQUIRED";
    public const string ConfirmationStale = "CONFIRMATION_STALE";

    /// <summary>理由、两条核实记录与那一篮去向的最长长度（去掉首尾空白后的 UTF-16 码元）。</summary>
    public const int MaxTextLength = 500;

    /// <summary>车号、旅程号、操作员与自述角色的最长长度。</summary>
    public const int MaxIdentifierLength = 128;

    /// <summary>演练：只读，一行不写，不写审计。给出计划与确认码，或者拒绝的理由。</summary>
    public static Task<JourneyWithdrawalResult> PlanAsync(
        ControlServerDbContext dbContext,
        JourneyWithdrawalRequest request,
        CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    /// <summary>
    /// 撤下：重算计划，确认码对得上才写；每一次请求（含被拒）写一条管理员审计。<paramref name="publisher"/> 为空表示服务端停着
    /// （FieldOps 直接写库），收尾快照留在发件箱里，车重连后补发。
    /// </summary>
    public static Task<JourneyWithdrawalResult> WithdrawAsync(
        ControlServerDbContext dbContext,
        IGovernanceAuditWriter audit,
        JourneyWithdrawalRequest request,
        DateTimeOffset now,
        OnboardJourneyPublisher? publisher,
        CancellationToken cancellationToken) =>
        throw new NotImplementedException();
}
