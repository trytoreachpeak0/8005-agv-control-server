using ControlServer.Domain;

namespace ControlServer.Application;

/// <summary>
/// FieldOps 换绑定集版本、解除暂停与对账的原因码（control-server#161）。与 <see cref="TaskTypeStationReasonCodes"/> 并列：
/// 那边是配置本身与目录的违规，这边是激活流程自己的拒绝理由。
/// </summary>
public static class TaskTypeStationActivationReasonCodes
{
    /// <summary>候选依赖的规则版本不是库里当前的规则版本（REQ-0343：规则与绑定同一门禁校验）。</summary>
    public const string RuleVersionNotCurrent = "TASK_TYPE_BINDING_RULE_VERSION_NOT_CURRENT";

    /// <summary>该图上一次激活结果未知、还没对账；在它有结论之前不开下一次激活或解除。</summary>
    public const string ActivationPendingReconciliation = "TASK_TYPE_ACTIVATION_PENDING_RECONCILIATION";

    /// <summary>校验之后、落库之前生效指针被别人改了：按新的状态重新来一次，不在别人的结果上接着写。</summary>
    public const string ActivationConcurrentChange = "TASK_TYPE_ACTIVATION_CONCURRENT_CHANGE";

    /// <summary>回滚点名的历史版本不存在。</summary>
    public const string VersionNotFound = "TASK_TYPE_BINDING_SET_VERSION_NOT_FOUND";

    /// <summary>解除暂停的任务类型在生效版本里没有绑定，无从重验。</summary>
    public const string TaskTypeNotBound = "TASK_TYPE_NOT_BOUND_IN_ACTIVE_VERSION";

    /// <summary>该 <c>Map + TASK_TYPE</c> 没有未解除的人工或目录变化暂停。</summary>
    public const string NoHoldToRelease = "TASK_TYPE_HOLD_NOTHING_TO_RELEASE";

    /// <summary>激活第二步没有确认落地；该图停在暂停，等对账（REQ-0347）。</summary>
    public const string ActivationResultUnknown = "TASK_TYPE_ACTIVATION_RESULT_UNKNOWN";
}

/// <summary>激活、回滚、解除暂停与对账写审计时用的动作名（REQ-0348）。</summary>
public static class TaskTypeStationActivationAuditActions
{
    public const string Previewed = "TASK_TYPE_STATION_BINDING_SET_ACTIVATION_PREVIEWED";
    public const string Rejected = "TASK_TYPE_STATION_BINDING_SET_ACTIVATION_REJECTED";
    public const string Started = "TASK_TYPE_STATION_BINDING_SET_ACTIVATION_STARTED";
    public const string Activated = "TASK_TYPE_STATION_BINDING_SET_ACTIVATED";
    public const string ResultUnknown = "TASK_TYPE_STATION_BINDING_SET_ACTIVATION_RESULT_UNKNOWN";
    public const string Reconciled = "TASK_TYPE_STATION_BINDING_SET_ACTIVATION_RECONCILED";
    public const string HoldReleased = "TASK_TYPE_STATION_HOLD_RELEASED";
    public const string HoldReleaseRejected = "TASK_TYPE_STATION_HOLD_RELEASE_REJECTED";
}

/// <summary>请求类别，写进审计的 <c>requestCategory</c>。</summary>
public static class TaskTypeStationRequestCategory
{
    public const string Activate = "ACTIVATE";
    public const string Rollback = "ROLLBACK";
    public const string Reconcile = "RECONCILE";
    public const string ReleaseHold = "RELEASE_HOLD";
}

/// <summary>
/// 一份候选：整图的 <c>TASK_TYPE → Station</c> 绑定、需求集，以及它所依赖的规则版本。每个绑定的
/// <see cref="TaskTypeStationBinding.SiteVerificationRef"/> 就是该站点的现场用途核对记录。
/// </summary>
public sealed record TaskTypeStationCandidate(
    int MapId,
    long RuleVersion,
    IReadOnlyList<string> RequiredTaskTypes,
    IReadOnlyList<TaskTypeStationBinding> Bindings);

/// <summary>
/// 一次请求的人给出的东西：变更理由（必填），以及可选的自报角色。自报角色原样记下，<b>不是</b>两级管理员分工
/// （REQ-0336 延后）——本期没有任何人员认证，审计的操作者仍是部署标识。
/// </summary>
public sealed record TaskTypeStationChangeRequest(string Reason, string? SelfReportedRole = null);

/// <summary>激活、回滚的结论。</summary>
public enum TaskTypeStationActivationOutcome
{
    /// <summary><c>--dry-run</c>：只校验与预览，除一条审计外什么都没写。</summary>
    Previewed,

    /// <summary>第二步已提交，并且读回核对过生效版本就是目标版本。</summary>
    Activated,

    /// <summary>校验不通过，什么都没切换。</summary>
    Rejected,

    /// <summary>第一步已提交、第二步没有确认落地：该图停在暂停，等对账（REQ-0347）。</summary>
    ResultUnknown
}

/// <summary>一个任务类型的绑定在当前生效版本与候选之间的变化；某一边没有绑定时为 <c>null</c>。</summary>
public sealed record TaskTypeStationBindingChange(
    string TaskType,
    TaskTypeStationBinding? Before,
    TaskTypeStationBinding? After);

/// <summary>一条在途需求：它冻结的绑定集版本与站点，以及候选下它的任务类型会改到哪个站点。</summary>
public sealed record TaskTypeStationInFlightImpact(
    string DemandId,
    string TaskType,
    long FrozenBindingSetVersion,
    int? FrozenStationRiotId,
    int? CandidateStationRiotId);

/// <summary>影响预览：该图全部在途需求数，以及其中冻结站点与候选不同的那些（它们仍按冻结的走）。</summary>
public sealed record TaskTypeStationImpactPreview(
    int InFlightDemandCount,
    IReadOnlyList<TaskTypeStationInFlightImpact> Affected);

/// <summary>激活、回滚的结果。</summary>
public sealed record TaskTypeStationActivationResult(
    TaskTypeStationActivationOutcome Outcome,
    string RequestCategory,
    int MapId,
    string AttemptId,
    long? PreviousVersion,
    long? TargetVersion,
    long? RuleVersion,
    long? CatalogRevision,
    IReadOnlyList<TaskTypeStationViolation> Violations,
    IReadOnlyList<TaskTypeStationBindingChange> Changes,
    TaskTypeStationImpactPreview Impact,
    IReadOnlyList<string> AuditRecordIds,
    string? Detail);

/// <summary>对账结论（REQ-0347）。</summary>
public enum TaskTypeStationReconciliationConclusion
{
    /// <summary>读回的生效版本就是那次激活的目标版本，内容完整：撤掉「激活结果未知」暂停。</summary>
    TargetActive,

    /// <summary>读回的仍是激活前的版本，内容完整：激活没有发生，撤掉「激活结果未知」暂停。</summary>
    PreviousActive,

    /// <summary>读回的既不是目标也不是原版本，或内容与版本记下的指纹不符：暂停保留，如实输出。</summary>
    Contradictory,

    /// <summary>该图没有未结的激活尝试。</summary>
    NothingToReconcile
}

/// <summary>对账结果。</summary>
public sealed record TaskTypeStationReconciliationResult(
    TaskTypeStationReconciliationConclusion Conclusion,
    int MapId,
    string? AttemptId,
    long? PreviousVersion,
    long? TargetVersion,
    long? ActiveVersion,
    string? ActiveContentSha256,
    IReadOnlyList<string> ReleasedHoldIds,
    string AuditRecordId,
    string Detail);

/// <summary>解除暂停的结论。</summary>
public enum TaskTypeStationHoldReleaseOutcome
{
    Released,
    Rejected
}

/// <summary>解除暂停的结果。</summary>
public sealed record TaskTypeStationHoldReleaseResult(
    TaskTypeStationHoldReleaseOutcome Outcome,
    int MapId,
    string TaskType,
    IReadOnlyList<TaskTypeStationViolation> Violations,
    IReadOnlyList<TaskTypeStationHold> Released,
    string AuditRecordId);

/// <summary>一次激活尝试：第一步提交之后它就在库里，直到第二步或对账给出结论。</summary>
public sealed record TaskTypeStationActivationAttempt(
    string AttemptId,
    int MapId,
    long? PreviousVersion,
    long TargetVersion,
    IReadOnlyList<string> HeldTaskTypes,
    IReadOnlyList<string> HoldIds);

/// <summary>第一步要落的东西。</summary>
public sealed record TaskTypeStationActivationStart(
    string AttemptId,
    TaskTypeStationCandidate Candidate,
    long? ExpectedPreviousVersion,
    long? CatalogRevision,
    string Source,
    IReadOnlyList<string> HeldTaskTypes,
    DateTimeOffset StartedAt);

/// <summary>提交后读回：生效指针、它指向的版本，以及版本内容是否与记下的指纹一致。</summary>
public sealed record TaskTypeStationActiveReadBack(
    TaskTypeStationActivePointer? ActivePointer,
    TaskTypeStationBindingSetVersion? Active,
    bool ContentVerified,
    string Detail);

/// <summary>一条在途需求冻结的绑定集版本（从批次 3 的通用冻结表读出）。</summary>
public sealed record TaskTypeStationInFlightDemand(string DemandId, string TaskType, long FrozenBindingSetVersion);

/// <summary>第一步落库时发现生效指针已不是校验时看到的那一个。</summary>
public sealed class TaskTypeStationActivationConflictException : InvalidOperationException
{
    public TaskTypeStationActivationConflictException()
        : base("The active pointer changed between validation and the first step.")
    {
    }

    public TaskTypeStationActivationConflictException(string message)
        : base(message)
    {
    }

    public TaskTypeStationActivationConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// 激活的两步落库、读回、对账与解除暂停的存取（control-server#161）。激活尝试用 #159 生效指针表的
/// <c>State</c>／<c>PendingVersion</c> 表达，尝试的来历在第一步那条审计里，不另建表。
/// </summary>
public interface ITaskTypeStationActivationStore
{
    /// <summary>
    /// 第一步，单独一个事务：写（或复用）目标版本、把指针标成 <c>ACTIVATION_UNKNOWN</c> 并记下待定版本、给
    /// <see cref="TaskTypeStationActivationStart.HeldTaskTypes"/> 每个任务类型置一条「激活结果未知」暂停，并写一条
    /// <paramref name="startedAudit"/> 给出的审计。指针此刻不是 <see cref="TaskTypeStationActivationStart.ExpectedPreviousVersion"/>
    /// 或已是未知状态时抛 <see cref="TaskTypeStationActivationConflictException"/>，什么都不写。
    /// </summary>
    Task<TaskTypeStationActivationAttempt> BeginAsync(
        TaskTypeStationActivationStart start,
        Func<TaskTypeStationActivationAttempt, GovernanceAuditEntry> startedAudit,
        CancellationToken cancellationToken);

    /// <summary>第二步，一个事务：指针指向目标版本、状态回到 <c>ACTIVE</c>、撤掉本次尝试的暂停。失败时整体回滚。</summary>
    Task CompleteAsync(TaskTypeStationActivationAttempt attempt, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>读回该图的生效指针与生效版本，按版本行重算内容指纹，与版本头和冻结快照核对。</summary>
    Task<TaskTypeStationActiveReadBack> ReadBackAsync(int mapId, CancellationToken cancellationToken);

    /// <summary>
    /// 把该图重新标成结果未知：指针状态 <c>ACTIVATION_UNKNOWN</c>、待定版本为目标版本（生效版本不动），本次尝试缺暂停的
    /// 任务类型补上。第二步失败或读回矛盾时用。
    /// </summary>
    Task<TaskTypeStationActivationAttempt> MarkUnknownAsync(
        TaskTypeStationActivationAttempt attempt,
        DateTimeOffset at,
        CancellationToken cancellationToken);

    /// <summary>该图未结的激活尝试（指针处于 <c>ACTIVATION_UNKNOWN</c>）；没有时为 <c>null</c>。</summary>
    Task<TaskTypeStationActivationAttempt?> ReadOpenAttemptAsync(int mapId, CancellationToken cancellationToken);

    /// <summary>对账有结论后收尾：指针状态回到 <c>ACTIVE</c>、清掉待定版本（生效版本不动），撤掉本次尝试的暂停。</summary>
    Task<IReadOnlyList<string>> ResolveAsync(
        TaskTypeStationActivationAttempt attempt,
        DateTimeOffset at,
        CancellationToken cancellationToken);

    /// <summary>解除该 <c>Map + TASK_TYPE</c> 全部未解除的人工与目录变化暂停，只记解除、不删行。</summary>
    Task<IReadOnlyList<TaskTypeStationHold>> ReleaseManualAndCatalogHoldsAsync(
        int mapId,
        string taskType,
        string releasedBy,
        DateTimeOffset at,
        CancellationToken cancellationToken);

    /// <summary>该图冻结了绑定集版本、旅程还没完成的需求。</summary>
    Task<IReadOnlyList<TaskTypeStationInFlightDemand>> ListInFlightDemandsAsync(
        int mapId,
        CancellationToken cancellationToken);
}
