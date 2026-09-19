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

    /// <summary>人工收尾只收「矛盾」：读回能给出结论的，走对账。</summary>
    public const string ActivationNotContradictory = "TASK_TYPE_ACTIVATION_NOT_CONTRADICTORY";

    /// <summary>该图没有未结的激活尝试，也没有未解除的「激活结果未知」暂停，没有什么可收尾。</summary>
    public const string NothingToClose = "TASK_TYPE_ACTIVATION_NOTHING_TO_CLOSE";

    /// <summary>这一步写库没有确认落地（超时、断联、提交抛出）；没有任何东西被当作已生效。</summary>
    public const string NotCommitted = "TASK_TYPE_ACTIVATION_STEP_NOT_COMMITTED";
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
    public const string ClosedManually = "TASK_TYPE_STATION_BINDING_SET_ACTIVATION_CLOSED_MANUALLY";
    public const string CloseRejected = "TASK_TYPE_STATION_BINDING_SET_ACTIVATION_CLOSE_REJECTED";
}

/// <summary>请求类别，写进审计的 <c>requestCategory</c>。</summary>
public static class TaskTypeStationRequestCategory
{
    public const string Activate = "ACTIVATE";
    public const string Rollback = "ROLLBACK";
    public const string Reconcile = "RECONCILE";
    public const string ReleaseHold = "RELEASE_HOLD";
    public const string CloseManually = "CLOSE_MANUALLY";
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

    /// <summary>该图没有未结的激活尝试。仍有孤儿「激活结果未知」暂停时一并撤掉（该图任何时候最多一次未结尝试）。</summary>
    NothingToReconcile,

    /// <summary>对账本身没能落库（超时、断联）：什么都没改，暂停照旧，稍后再对账。</summary>
    NotConcluded
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

/// <summary>人工收尾的结论。</summary>
public enum TaskTypeStationManualCloseOutcome
{
    /// <summary>该图回到「无生效版本」形态，全部「激活结果未知」暂停已撤。</summary>
    Closed,

    Rejected
}

/// <summary>
/// 人工收尾的结果。只在对账读回「矛盾」时才允许：两个版本都读不回完整内容，系统给不出结论，由人决定放弃这次尝试。
/// </summary>
public sealed record TaskTypeStationManualCloseResult(
    TaskTypeStationManualCloseOutcome Outcome,
    int MapId,
    string? AttemptId,
    long? ActiveVersionBefore,
    IReadOnlyList<string> ReleasedHoldIds,
    IReadOnlyList<TaskTypeStationViolation> Violations,
    string? AuditRecordId,
    string Detail);

/// <summary>对账在一个事务里读到的、判下的、撤掉的。</summary>
public sealed record TaskTypeStationReconciliation(
    TaskTypeStationActivationAttempt? Attempt,
    TaskTypeStationActiveReadBack ReadBack,
    TaskTypeStationReconciliationConclusion Conclusion,
    IReadOnlyList<string> ReleasedHoldIds,
    string AuditRecordId);

/// <summary>人工收尾在一个事务里读到的与撤掉的。</summary>
public sealed record TaskTypeStationManualClose(
    TaskTypeStationActivationAttempt? Attempt,
    TaskTypeStationActiveReadBack ReadBack,
    bool Closed,
    IReadOnlyList<string> ReleasedHoldIds,
    string AuditRecordId);

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
/// <c>State</c>／<c>PendingVersion</c> 表达，尝试的来历在它置下的「激活结果未知」暂停的 <c>DetailJson</c> 里，不另建表。
/// 每个会改指针或暂停的方法都是一个事务，判断所依据的读也在同一事务里（SQLite <c>BEGIN IMMEDIATE</c>，写者串行）。
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

    /// <summary>
    /// 第二步，一个事务：指针指向目标版本、状态回到 <c>ACTIVE</c>、撤掉本次尝试的暂停。先核对指针仍是本次尝试留下的样子
    /// （<c>ACTIVATION_UNKNOWN</c>、待定版本是本次目标、生效版本是本次记下的原版本），不是就抛
    /// <see cref="TaskTypeStationActivationConflictException"/>、什么都不写——迟到的第二步不能盖掉对账的结论或更新的一次尝试。
    /// </summary>
    Task CompleteAsync(TaskTypeStationActivationAttempt attempt, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>读回该图的生效指针与生效版本，按版本行重算内容指纹，与版本头和冻结快照核对。</summary>
    Task<TaskTypeStationActiveReadBack> ReadBackAsync(int mapId, CancellationToken cancellationToken);

    /// <summary>
    /// 把该图重新标成结果未知：指针状态 <c>ACTIVATION_UNKNOWN</c>、待定版本为目标版本（生效版本不动），本次尝试缺暂停的
    /// 任务类型补上。第二步失败或读回矛盾时用。该图此刻挂着另一次尝试时抛 <see cref="TaskTypeStationActivationConflictException"/>。
    /// </summary>
    Task<TaskTypeStationActivationAttempt> MarkUnknownAsync(
        TaskTypeStationActivationAttempt attempt,
        DateTimeOffset at,
        CancellationToken cancellationToken);

    /// <summary>
    /// 该图未结的激活尝试：从仍成立的「激活结果未知」暂停的 <c>DetailJson</c> 取尝试号、原版本与目标版本（优先与指针待定版本相符的那次），
    /// 不按审计时间排序；指针处于 <c>ACTIVATION_UNKNOWN</c> 却没有这样的暂停时，按指针给出。都没有时为 <c>null</c>。
    /// <see cref="TaskTypeStationActivationAttempt.HoldIds"/> 是该图全部仍成立的「激活结果未知」暂停。
    /// </summary>
    Task<TaskTypeStationActivationAttempt?> ReadOpenAttemptAsync(int mapId, CancellationToken cancellationToken);

    /// <summary>
    /// 对账，一个事务：读未结尝试与生效版本，交给 <paramref name="decide"/> 判结论；不是「矛盾」时指针回到对应形态（原版本为空时删掉指针行，
    /// 即「无生效版本」），撤该图全部仍成立的「激活结果未知」暂停；再写 <paramref name="audit"/> 给出的审计。
    /// </summary>
    Task<TaskTypeStationReconciliation> ReconcileAsync(
        int mapId,
        Func<TaskTypeStationActivationAttempt?, TaskTypeStationActiveReadBack, TaskTypeStationReconciliationConclusion> decide,
        Func<TaskTypeStationActivationAttempt?, TaskTypeStationActiveReadBack, TaskTypeStationReconciliationConclusion, IReadOnlyList<string>, GovernanceAuditEntry> audit,
        DateTimeOffset at,
        CancellationToken cancellationToken);

    /// <summary>
    /// 人工收尾，一个事务：<paramref name="isContradictory"/> 对此刻读到的状态说「是矛盾」时，删掉指针行（该图回到「无生效版本」）、撤全部
    /// 「激活结果未知」暂停；否则什么都不改。两种情况都写 <paramref name="audit"/> 给出的审计。
    /// </summary>
    Task<TaskTypeStationManualClose> CloseManuallyAsync(
        int mapId,
        Func<TaskTypeStationActivationAttempt?, TaskTypeStationActiveReadBack, bool> isContradictory,
        Func<TaskTypeStationActivationAttempt?, TaskTypeStationActiveReadBack, bool, IReadOnlyList<string>, GovernanceAuditEntry> audit,
        DateTimeOffset at,
        CancellationToken cancellationToken);

    /// <summary>
    /// 解除该 <c>Map + TASK_TYPE</c> 全部未解除的人工与目录变化暂停，只记解除、不删行；<paramref name="audit"/> 给出的审计在同一个事务里写。
    /// </summary>
    Task<(IReadOnlyList<TaskTypeStationHold> Released, string AuditRecordId)> ReleaseManualAndCatalogHoldsAsync(
        int mapId,
        string taskType,
        string releasedBy,
        Func<IReadOnlyList<TaskTypeStationHold>, GovernanceAuditEntry> audit,
        DateTimeOffset at,
        CancellationToken cancellationToken);

    /// <summary>该图冻结了绑定集版本、旅程还没完成的需求。</summary>
    Task<IReadOnlyList<TaskTypeStationInFlightDemand>> ListInFlightDemandsAsync(
        int mapId,
        CancellationToken cancellationToken);
}
