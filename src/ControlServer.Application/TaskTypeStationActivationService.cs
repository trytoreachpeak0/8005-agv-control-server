using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using ControlServer.Domain;

namespace ControlServer.Application;

/// <summary>
/// 一张图公共站点绑定集的整集版本化（control-server#161，<c>FP-C9b</c>）：候选预览、激活、回滚、结果未知对账、解除暂停。
/// </summary>
/// <remarks>
/// <para>
/// <b>全部是 FieldOps 动词，不上看板</b>（规格 5.7）：没有人员认证，改变生效版本或撤销暂停只走这条要人在机器前刻意敲一次、
/// 每次都写不可改写审计的路。审计的操作者是部署标识并标不可归属自然人；自报角色原样记下，不代表两级管理员分工（REQ-0336 延后）。
/// </para>
/// <para>
/// <b>激活分两步落库</b>（REQ-0347）。第一步单独提交：写目标版本、把生效指针标成结果未知、给该图需求集合里每个任务类型置一条
/// 「激活结果未知」暂停。第二步一个事务：切指针、撤这些暂停；提交后读回生效版本的身份与完整内容指纹核对。第二步抛出或读回矛盾，
/// 这一图就停在暂停里（准入读暂停，fail-safe），只有对账动词读到实际状态后才撤。任何时候都不以「请求成功」写「已生效」。
/// </para>
/// <para>
/// <b>激活不动在途</b>（REQ-0345）：只写绑定集版本、生效指针、暂停与审计；已受理需求按冻结的版本走。
/// </para>
/// </remarks>
public sealed class TaskTypeStationActivationService(
    ITaskTypeStationRuleStore rules,
    ITaskTypeStationBindingStore bindings,
    ITaskTypeStationActivationStore activations,
    ICatalogAvailabilityStore catalogStates,
    IGovernanceAuditWriter audit)
{
    private static readonly JsonSerializerOptions AuditJson = new()
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };

    private readonly ITaskTypeStationRuleStore _rules = rules ?? throw new ArgumentNullException(nameof(rules));
    private readonly ITaskTypeStationBindingStore _bindings = bindings ?? throw new ArgumentNullException(nameof(bindings));
    private readonly ITaskTypeStationActivationStore _activations =
        activations ?? throw new ArgumentNullException(nameof(activations));
    private readonly ICatalogAvailabilityStore _catalogStates =
        catalogStates ?? throw new ArgumentNullException(nameof(catalogStates));
    private readonly IGovernanceAuditWriter _audit = audit ?? throw new ArgumentNullException(nameof(audit));

    /// <summary>
    /// 激活一份候选；<paramref name="dryRun"/> 时只校验并预览，除一条审计外什么都不写。
    /// </summary>
    public Task<TaskTypeStationActivationResult> ActivateAsync(
        TaskTypeStationCandidate candidate,
        RiotMapStationCatalogSnapshot? catalog,
        TaskTypeStationChangeRequest request,
        bool dryRun,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        RunActivationAsync(
            candidate, catalog, request, dryRun, now, TaskTypeStationRequestCategory.Activate, rollbackOf: null,
            cancellationToken);

    /// <summary>
    /// 回滚：把历史版本 <paramref name="version"/> 的内容当作一次新的激活，再走一遍全部校验（照 <c>import-area-assignments</c>
    /// 「回滚就是把旧内容再导入一次」）。不把指针拨回去——生效的是一个新版本号，内容与那一版相同。
    /// </summary>
    public async Task<TaskTypeStationActivationResult> RollbackAsync(
        int mapId,
        long version,
        RiotMapStationCatalogSnapshot? catalog,
        TaskTypeStationChangeRequest request,
        bool dryRun,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Reason);

        TaskTypeStationBindingSetVersion? old = await _bindings.ReadVersionAsync(mapId, version, cancellationToken);
        if (old is not null)
        {
            return await RunActivationAsync(
                new TaskTypeStationCandidate(mapId, old.RuleVersion, old.RequiredTaskTypes, old.Bindings),
                catalog, request, dryRun, now, TaskTypeStationRequestCategory.Rollback, version, cancellationToken);
        }

        TaskTypeStationViolation[] missing =
        [
            new(TaskTypeStationActivationReasonCodes.VersionNotFound, null, null,
                Invariant($"Map {mapId} has no binding set version {version} to roll back to."))
        ];
        TaskTypeStationActivePointer? pointer = await _bindings.ReadActivePointerAsync(mapId, cancellationToken);
        ActivationAuditFacts facts = new(
            Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture), TaskTypeStationRequestCategory.Rollback, dryRun,
            request, new TaskTypeStationCandidate(mapId, 0, [], []), null, pointer, null, [],
            new TaskTypeStationImpactPreview(0, []), version);
        string auditId = await _audit.WriteBusinessAsync(
            facts.Entry(TaskTypeStationActivationAuditActions.Rejected, GovernanceActionOutcome.Failed, null, missing,
                conclusion: "REJECTED"),
            now,
            cancellationToken);
        return facts.Result(TaskTypeStationActivationOutcome.Rejected, null, missing, [auditId], null) with
        {
            RuleVersion = null
        };
    }

    /// <summary>
    /// 对账（REQ-0347）：只读取实际生效的版本、它的身份与完整内容，再写结论。读、判、写与审计在一个事务里（审查 S1），迟到的第二步插不进来。
    /// 生效的是目标版本或仍是原版本，撤该图全部「激活结果未知」暂停（含孤儿，审查 S3）；原版本是「没有」时该图回到无生效版本：
    /// 从人工收尾的墓碑出发、或没有暂停记着从哪里出发的，回到墓碑（第二轮复审 N1、control-server#191），其余删指针行（审查 S4）；
    /// 两者都不是、或内容与记下的指纹不符，暂停保留并如实输出，出口是 <see cref="CloseManuallyAsync"/>。没有未结尝试时也写一条审计。
    /// 对账自己没能落库时结论是 <see cref="TaskTypeStationReconciliationConclusion.NotConcluded"/>，什么都没改（审查 S5）。
    /// </summary>
    public async Task<TaskTypeStationReconciliationResult> ReconcileAsync(
        int mapId,
        TaskTypeStationChangeRequest request,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Reason);

        TaskTypeStationReconciliation reconciled;
        try
        {
            reconciled = await _activations.ReconcileAsync(
                mapId,
                Decide,
                (attempt, readBack, conclusion, released) => ReconcileEntry(
                    mapId, request, attempt, readBack, conclusion, released,
                    Describe(mapId, attempt, readBack, conclusion), OutcomeOf(conclusion)),
                now,
                cancellationToken);
        }
#pragma warning disable CA1031 // Whatever stopped the reconciliation, nothing changed and the holds stand; say so and stop.
        catch (Exception failure)
#pragma warning restore CA1031
        {
            string detail = Invariant($"The reconciliation did not commit, so nothing changed and the holds stand: {failure.GetType().Name}: {failure.Message}");
            string? auditId = await TryWriteAsync(
                ReconcileEntry(
                    mapId, request, null, null, TaskTypeStationReconciliationConclusion.NotConcluded, [], detail,
                    TimedOutOrUnknown(failure)),
                now);
            return new TaskTypeStationReconciliationResult(
                TaskTypeStationReconciliationConclusion.NotConcluded, mapId, null, null, null, null, null, [], auditId ?? string.Empty,
                detail);
        }

        TaskTypeStationActiveReadBack back = reconciled.ReadBack;
        return new TaskTypeStationReconciliationResult(
            reconciled.Conclusion,
            mapId,
            reconciled.Attempt?.AttemptId,
            reconciled.Attempt?.PreviousVersion,
            reconciled.Attempt?.TargetVersion,
            back.ActivePointer?.ActiveVersion,
            back.Active?.ContentSha256,
            reconciled.ReleasedHoldIds,
            reconciled.AuditRecordId,
            Describe(mapId, reconciled.Attempt, back, reconciled.Conclusion));
    }

    /// <summary>
    /// 人工收尾（审查 S3）：对账读回「矛盾」、系统给不出结论时，由人决定放弃这次尝试。该图留下墓碑（指针 <c>CLOSED_MANUALLY</c>、
    /// 没有生效版本，第二轮复审 N1）、撤全部「激活结果未知」暂停；重启不装预置，要再有生效版本，走一次新的激活或回滚。理由必填，自报角色原样记下；判定与写入在同一个事务里重读，读回能下结论时拒绝。
    /// </summary>
    public async Task<TaskTypeStationManualCloseResult> CloseManuallyAsync(
        int mapId,
        TaskTypeStationChangeRequest request,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Reason);

        TaskTypeStationManualClose close;
        try
        {
            close = await _activations.CloseManuallyAsync(
                mapId,
                (attempt, readBack) => attempt is not null
                    && Decide(attempt, readBack) == TaskTypeStationReconciliationConclusion.Contradictory,
                (attempt, readBack, closed, released) => CloseEntry(
                    mapId, request, attempt, readBack, closed, released, CloseViolations(mapId, attempt, readBack, closed)),
                now,
                cancellationToken);
        }
#pragma warning disable CA1031 // Nothing was closed; the holds stand. Record the refusal and stop.
        catch (Exception failure)
#pragma warning restore CA1031
        {
            TaskTypeStationViolation[] notCommitted =
            [
                new(TaskTypeStationActivationReasonCodes.NotCommitted, null, null,
                    Invariant($"The manual close did not commit, so nothing changed: {failure.GetType().Name}: {failure.Message}"))
            ];
            string? auditId = await TryWriteAsync(CloseEntry(mapId, request, null, null, false, [], notCommitted), now);
            return new TaskTypeStationManualCloseResult(
                TaskTypeStationManualCloseOutcome.Rejected, mapId, null, null, [], notCommitted, auditId, notCommitted[0].Detail);
        }

        IReadOnlyList<TaskTypeStationViolation> violations = CloseViolations(mapId, close.Attempt, close.ReadBack, close.Closed);
        return new TaskTypeStationManualCloseResult(
            close.Closed ? TaskTypeStationManualCloseOutcome.Closed : TaskTypeStationManualCloseOutcome.Rejected,
            mapId,
            close.Attempt?.AttemptId,
            close.ReadBack.ActivePointer?.ActiveVersion,
            close.ReleasedHoldIds,
            violations,
            close.AuditRecordId,
            close.Closed
                ? Invariant($"Map {mapId} now has no active version; {close.ReleasedHoldIds.Count} activation hold(s) released. {close.ReadBack.Detail}")
                : violations[0].Detail);
    }

    public static string ConclusionName(TaskTypeStationReconciliationConclusion conclusion) => conclusion switch
    {
        TaskTypeStationReconciliationConclusion.TargetActive => "TARGET_ACTIVE",
        TaskTypeStationReconciliationConclusion.PreviousActive => "PREVIOUS_ACTIVE",
        TaskTypeStationReconciliationConclusion.Contradictory => "CONTRADICTORY",
        TaskTypeStationReconciliationConclusion.NotConcluded => "NOT_CONCLUDED",
        _ => "NOTHING_TO_RECONCILE"
    };

    /// <summary>对账的判定，只看读回的事实：生效版本是谁、内容是否与记下的指纹一致。</summary>
    private static TaskTypeStationReconciliationConclusion Decide(
        TaskTypeStationActivationAttempt? attempt,
        TaskTypeStationActiveReadBack readBack)
    {
        long? active = readBack.ActivePointer?.ActiveVersion;
        if (attempt is null)
        {
            return TaskTypeStationReconciliationConclusion.NothingToReconcile;
        }
        if (active == attempt.TargetVersion && readBack.ContentVerified)
        {
            return TaskTypeStationReconciliationConclusion.TargetActive;
        }
        if (active == attempt.PreviousVersion && (active is null || readBack.ContentVerified))
        {
            return TaskTypeStationReconciliationConclusion.PreviousActive;
        }
        return TaskTypeStationReconciliationConclusion.Contradictory;
    }

    private static string Describe(
        int mapId,
        TaskTypeStationActivationAttempt? attempt,
        TaskTypeStationActiveReadBack readBack,
        TaskTypeStationReconciliationConclusion conclusion)
    {
        switch (conclusion)
        {
            case TaskTypeStationReconciliationConclusion.NothingToReconcile:
                return readBack.ActivePointer?.ActiveVersion is long active
                    ? Invariant($"Map {mapId} has no open activation attempt; version {active} is active. {readBack.Detail}")
                    : Invariant($"Map {mapId} has no open activation attempt and no active version (pointer {readBack.ActivePointer?.State ?? "absent"}); an activation or rollback gives it one.");
            case TaskTypeStationReconciliationConclusion.TargetActive:
                return Invariant($"The target version {attempt!.TargetVersion} is the one in force. {readBack.Detail}");
            case TaskTypeStationReconciliationConclusion.PreviousActive when attempt!.PreviousVersion is null:
                return Invariant($"No version was active before and none is now; the activation of version {attempt.TargetVersion} did not happen, and Map {mapId} is back to having no active version. After a manual close, or when no hold recorded where the attempt started, that is the manual-close tombstone: a restart does not load the preset, and an activation or rollback gives the map a version.");
            case TaskTypeStationReconciliationConclusion.PreviousActive:
                return Invariant($"The previous version {attempt!.PreviousVersion} is still in force; the activation of version {attempt.TargetVersion} did not happen. {readBack.Detail}");
            default:
                return Invariant($"Neither the target version {attempt!.TargetVersion} nor the previous version {attempt.PreviousVersion} reads back whole: {readBack.Detail} The holds stay; close-task-type-station-activation is the way out.");
        }
    }

    private static GovernanceActionOutcome OutcomeOf(TaskTypeStationReconciliationConclusion conclusion) =>
        conclusion == TaskTypeStationReconciliationConclusion.Contradictory
            ? GovernanceActionOutcome.ResultUnknown
            : GovernanceActionOutcome.Succeeded;

    private static GovernanceAuditEntry ReconcileEntry(
        int mapId,
        TaskTypeStationChangeRequest request,
        TaskTypeStationActivationAttempt? attempt,
        TaskTypeStationActiveReadBack? readBack,
        TaskTypeStationReconciliationConclusion conclusion,
        IReadOnlyList<string> released,
        string detail,
        GovernanceActionOutcome outcome) =>
        new(
            TaskTypeStationActivationAuditActions.Reconciled,
            GovernedObjectKind.PublicStationBinding,
            TaskTypeStationGovernance.BindingSetObjectId(mapId),
            attempt?.TargetVersion,
            outcome,
            JsonSerializer.Serialize(
                new
                {
                    mapId,
                    requestCategory = TaskTypeStationRequestCategory.Reconcile,
                    reason = request.Reason,
                    selfReportedRole = request.SelfReportedRole,
                    attemptId = attempt?.AttemptId,
                    bindingSetVersion = new
                    {
                        previous = attempt?.PreviousVersion,
                        target = attempt?.TargetVersion,
                        active = readBack?.ActivePointer?.ActiveVersion
                    },
                    activeContentSha256 = readBack?.Active?.ContentSha256,
                    contentVerified = readBack?.ContentVerified,
                    heldTaskTypes = attempt?.HeldTaskTypes ?? [],
                    releasedHoldIds = released,
                    conclusion = ConclusionName(conclusion),
                    detail
                },
                AuditJson),
            readBack?.Active?.SnapshotId);

    private static IReadOnlyList<TaskTypeStationViolation> CloseViolations(
        int mapId,
        TaskTypeStationActivationAttempt? attempt,
        TaskTypeStationActiveReadBack readBack,
        bool closed) =>
        closed
            ? []
            : attempt is null
                ? [new(TaskTypeStationActivationReasonCodes.NothingToClose, null, null,
                    Invariant($"Map {mapId} has no open activation attempt to close; reconcile-task-type-stations releases any leftover activation hold."))]
                : [new(TaskTypeStationActivationReasonCodes.ActivationNotContradictory, null, null,
                    Invariant($"Map {mapId}'s attempt reads back as {ConclusionName(Decide(attempt, readBack))}, not as a contradiction; reconcile-task-type-stations concludes it. {readBack.Detail}"))];

    private static GovernanceAuditEntry CloseEntry(
        int mapId,
        TaskTypeStationChangeRequest request,
        TaskTypeStationActivationAttempt? attempt,
        TaskTypeStationActiveReadBack? readBack,
        bool closed,
        IReadOnlyList<string> released,
        IReadOnlyList<TaskTypeStationViolation> violations) =>
        new(
            closed ? TaskTypeStationActivationAuditActions.ClosedManually : TaskTypeStationActivationAuditActions.CloseRejected,
            GovernedObjectKind.PublicStationBinding,
            TaskTypeStationGovernance.BindingSetObjectId(mapId),
            attempt?.TargetVersion,
            closed ? GovernanceActionOutcome.Succeeded : GovernanceActionOutcome.Failed,
            JsonSerializer.Serialize(
                new
                {
                    mapId,
                    requestCategory = TaskTypeStationRequestCategory.CloseManually,
                    reason = request.Reason,
                    selfReportedRole = request.SelfReportedRole,
                    selfReportedRoleNote = "Recorded as given; not verified. REQ-0336 two-level administrators are deferred and no personnel authentication exists.",
                    attemptId = attempt?.AttemptId,
                    bindingSetVersion = new
                    {
                        previous = attempt?.PreviousVersion,
                        target = attempt?.TargetVersion,
                        activeBefore = readBack?.ActivePointer?.ActiveVersion,
                        activeAfter = closed ? null : readBack?.ActivePointer?.ActiveVersion
                    },
                    readBack = readBack?.Detail,
                    releasedHoldIds = released,
                    validation = new
                    {
                        passed = violations.Count == 0,
                        violations = violations.Select(violation => new
                        {
                            reasonCode = violation.ReasonCode,
                            detail = violation.Detail
                        })
                    },
                    conclusion = closed ? "CLOSED_MANUALLY" : "REJECTED"
                },
                AuditJson),
            readBack?.Active?.SnapshotId);

    /// <summary>审计尽力而为地写：写不进去时返回 <c>null</c>，由调用方如实说「审计也没写成」。</summary>
    private async Task<string?> TryWriteAsync(GovernanceAuditEntry entry, DateTimeOffset now)
    {
        try
        {
            return await _audit.WriteBusinessAsync(entry, now, CancellationToken.None);
        }
#pragma warning disable CA1031 // The caller already reports a failure; a second one must not replace the first.
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }
    }

    private static GovernanceActionOutcome TimedOutOrUnknown(Exception failure) =>
        failure is TimeoutException || IsLockTimeout(failure)
            ? GovernanceActionOutcome.TimedOut
            : GovernanceActionOutcome.ResultUnknown;

    /// <summary>
    /// 解除一个明确的 <c>Map + TASK_TYPE</c> 上人工或目录变化来源的暂停（REQ-0340 恢复半边）。必须带现场核对记录，并对当前新鲜目录
    /// 把该任务类型在生效版本里的绑定完整重验一遍：站点仍在、名字与生效版本记下的一致、没有被别的任务类型绑定。不通过就拒绝并逐条列原因。
    /// 只记解除，不删暂停行；「激活结果未知」暂停不归这里撤，那是对账的事。
    /// </summary>
    public async Task<TaskTypeStationHoldReleaseResult> ReleaseHoldAsync(
        int mapId,
        string taskType,
        string? siteVerificationRef,
        RiotMapStationCatalogSnapshot? catalog,
        TaskTypeStationChangeRequest request,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskType);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Reason);

        TaskTypeStationActivePointer? pointer = await _bindings.ReadActivePointerAsync(mapId, cancellationToken);
        TaskTypeStationBindingSetVersion? active = await _bindings.ReadActiveAsync(mapId, cancellationToken);
        MapStationCatalogAvailability? catalogState = await _catalogStates.ReadStateAsync(mapId, cancellationToken);
        TaskTypeStationBinding? binding = active?.Bindings.FirstOrDefault(candidate =>
            string.Equals(candidate.TaskType, taskType, StringComparison.Ordinal));

        List<TaskTypeStationViolation> violations = [];
        if (pointer is not null
            && string.Equals(pointer.State, TaskTypeStationActivationState.ActivationUnknown, StringComparison.Ordinal))
        {
            violations.Add(new(
                TaskTypeStationActivationReasonCodes.ActivationPendingReconciliation, taskType, null,
                Invariant($"Map {mapId} has an activation of version {pointer.PendingVersion} whose result is unknown; which binding is in force is not known until it is reconciled.")));
        }
        if (binding is null)
        {
            violations.Add(new(
                TaskTypeStationActivationReasonCodes.TaskTypeNotBound, taskType, null,
                active is null
                    ? Invariant($"Map {mapId} has no active version, so {taskType} has no binding to revalidate.")
                    : Invariant($"{taskType} has no binding in Map {mapId}'s active version {active.Version}, so there is nothing to revalidate.")));
        }
        if (string.IsNullOrWhiteSpace(siteVerificationRef))
        {
            violations.Add(new(
                TaskTypeStationReasonCodes.BindingSiteVerificationMissing, taskType, binding?.StationRiotId,
                Invariant($"Releasing the hold on Map {mapId} {taskType} without a site verification reference.")));
        }
        violations.AddRange(TaskTypeStationCatalogEvidence.JudgeFreshness(mapId, catalog, catalogState, now));
        if (binding is not null && catalog is not null)
        {
            violations.AddRange(TaskTypeStationConfigurationValidator.EvaluateCatalog(
                mapId, [binding], catalog, catalogFresh: true));
        }
        if (binding is not null)
        {
            string[] sharing =
            [
                .. active!.Bindings
                    .Where(other => other.StationRiotId == binding.StationRiotId
                        && !string.Equals(other.TaskType, taskType, StringComparison.Ordinal))
                    .Select(other => other.TaskType)
            ];
            if (sharing.Length > 0)
            {
                violations.Add(new(
                    TaskTypeStationReasonCodes.StationReused, taskType, binding.StationRiotId,
                    Invariant($"Station {binding.StationName}/{binding.StationRiotId} is also bound to {string.Join(", ", sharing)} in version {active.Version}.")));
            }
        }

        GovernanceAuditEntry Entry(IReadOnlyList<TaskTypeStationViolation> refused, IReadOnlyList<TaskTypeStationHold> released)
        {
            bool ok = refused.Count == 0;
            return new GovernanceAuditEntry(
                ok ? TaskTypeStationActivationAuditActions.HoldReleased : TaskTypeStationActivationAuditActions.HoldReleaseRejected,
                GovernedObjectKind.PublicStationBinding,
                TaskTypeStationGovernance.BindingSetObjectId(mapId),
                active?.Version,
                ok ? GovernanceActionOutcome.Succeeded : GovernanceActionOutcome.Failed,
                JsonSerializer.Serialize(
                    new
                    {
                        mapId,
                        taskType,
                        requestCategory = TaskTypeStationRequestCategory.ReleaseHold,
                        reason = request.Reason,
                        selfReportedRole = request.SelfReportedRole,
                        siteVerification = siteVerificationRef,
                        catalogRevision = catalogState?.CatalogRevision,
                        bindingSetVersion = active?.Version,
                        ruleVersion = active?.RuleVersion,
                        station = Station(binding),
                        releasedHolds = released.Select(hold => new
                        {
                            holdId = hold.HoldId,
                            source = hold.Source,
                            reasonCode = hold.ReasonCode,
                            raisedAt = hold.RaisedAt,
                            raisedBy = hold.RaisedBy
                        }),
                        validation = new
                        {
                            passed = ok,
                            violations = refused.Select(violation => new
                            {
                                reasonCode = violation.ReasonCode,
                                taskType = violation.TaskType,
                                stationRiotId = violation.StationRiotId,
                                detail = violation.Detail
                            })
                        },
                        conclusion = ok ? "RELEASED" : "REJECTED"
                    },
                    AuditJson),
                active?.SnapshotId);
        }

        if (violations.Count > 0)
        {
            string rejectedId = await _audit.WriteBusinessAsync(Entry(violations, []), now, cancellationToken);
            return new TaskTypeStationHoldReleaseResult(
                TaskTypeStationHoldReleaseOutcome.Rejected, mapId, taskType, violations, [], rejectedId);
        }

        TaskTypeStationViolation[] nothingHeld =
        [
            new(TaskTypeStationActivationReasonCodes.NoHoldToRelease, taskType, null,
                Invariant($"Map {mapId} {taskType} has no unreleased manual or catalog-change hold."))
        ];
        try
        {
            (IReadOnlyList<TaskTypeStationHold> released, string auditId) = await _activations.ReleaseManualAndCatalogHoldsAsync(
                mapId,
                taskType,
                "fieldops:release-hold:" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
                released => Entry(released.Count == 0 ? nothingHeld : [], released),
                now,
                cancellationToken);
            return released.Count == 0
                ? new TaskTypeStationHoldReleaseResult(
                    TaskTypeStationHoldReleaseOutcome.Rejected, mapId, taskType, nothingHeld, [], auditId)
                : new TaskTypeStationHoldReleaseResult(
                    TaskTypeStationHoldReleaseOutcome.Released, mapId, taskType, [], released, auditId);
        }
#pragma warning disable CA1031 // The release and its audit rolled back together; nothing was released. Record that.
        catch (Exception failure)
#pragma warning restore CA1031
        {
            TaskTypeStationViolation[] notCommitted =
            [
                new(TaskTypeStationActivationReasonCodes.NotCommitted, taskType, binding?.StationRiotId,
                    Invariant($"The release and its audit did not commit, so no hold was released: {failure.GetType().Name}: {failure.Message}"))
            ];
            string? auditId = await TryWriteAsync(Entry(notCommitted, []), now);
            return new TaskTypeStationHoldReleaseResult(
                TaskTypeStationHoldReleaseOutcome.Rejected, mapId, taskType, notCommitted, [], auditId ?? string.Empty);
        }
    }

    private async Task<TaskTypeStationActivationResult> RunActivationAsync(
        TaskTypeStationCandidate candidate,
        RiotMapStationCatalogSnapshot? catalog,
        TaskTypeStationChangeRequest request,
        bool dryRun,
        DateTimeOffset now,
        string category,
        long? rollbackOf,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Reason);

        string attemptId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        int mapId = candidate.MapId;
        TaskTypeStationActivePointer? pointer = await _bindings.ReadActivePointerAsync(mapId, cancellationToken);
        TaskTypeStationBindingSetVersion? active = await _bindings.ReadActiveAsync(mapId, cancellationToken);
        MapStationCatalogAvailability? catalogState = await _catalogStates.ReadStateAsync(mapId, cancellationToken);
        long? catalogRevision = catalogState?.CatalogRevision;

        IReadOnlyList<TaskTypeStationViolation> violations =
            await ValidateCandidateAsync(candidate, catalog, catalogState, pointer, now, cancellationToken);
        IReadOnlyList<TaskTypeStationBindingChange> changes = Diff(active?.Bindings ?? [], candidate.Bindings ?? []);
        TaskTypeStationImpactPreview impact = await PreviewImpactAsync(mapId, candidate.Bindings ?? [], cancellationToken);
        ActivationAuditFacts facts = new(
            attemptId, category, dryRun, request, candidate, active, pointer, catalogRevision, changes, impact, rollbackOf);

        if (violations.Count > 0)
        {
            string rejectedId = await _audit.WriteBusinessAsync(
                facts.Entry(TaskTypeStationActivationAuditActions.Rejected, GovernanceActionOutcome.Failed, null,
                    violations, conclusion: "REJECTED"),
                now,
                cancellationToken);
            return facts.Result(TaskTypeStationActivationOutcome.Rejected, null, violations, [rejectedId], null);
        }

        if (dryRun)
        {
            string previewId = await _audit.WriteBusinessAsync(
                facts.Entry(TaskTypeStationActivationAuditActions.Previewed, GovernanceActionOutcome.Succeeded, null,
                    violations, conclusion: "PREVIEWED"),
                now,
                cancellationToken);
            return facts.Result(TaskTypeStationActivationOutcome.Previewed, null, violations, [previewId], null);
        }

        // Every task type the map required before or will require after: while the result is unknown, neither the old
        // nor the new station may be used for them.
        string[] held =
        [
            .. (active?.RequiredTaskTypes ?? [])
                .Concat(candidate.RequiredTaskTypes ?? [])
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
        ];
        facts = facts with { HeldTaskTypes = held };
        string source = rollbackOf is long rolledBack
            ? Invariant($"fieldops:rollback:v{rolledBack}:{attemptId}")
            : "fieldops:activate:" + attemptId;

        TaskTypeStationActivationAttempt attempt;
        try
        {
            attempt = await _activations.BeginAsync(
                new TaskTypeStationActivationStart(
                    attemptId, candidate, pointer?.ActiveVersion, catalogRevision, source, held, now),
                begun => facts.Entry(
                    TaskTypeStationActivationAuditActions.Started, GovernanceActionOutcome.ResultUnknown,
                    begun.TargetVersion, violations, conclusion: "STARTED"),
                cancellationToken);
        }
        catch (TaskTypeStationActivationConflictException conflict)
        {
            TaskTypeStationViolation[] raced =
                [new(TaskTypeStationActivationReasonCodes.ActivationConcurrentChange, null, null, conflict.Message)];
            string rejectedId = await _audit.WriteBusinessAsync(
                facts.Entry(TaskTypeStationActivationAuditActions.Rejected, GovernanceActionOutcome.Failed, null,
                    raced, conclusion: "REJECTED"),
                now,
                cancellationToken);
            return facts.Result(TaskTypeStationActivationOutcome.Rejected, null, raced, [rejectedId], null);
        }
#pragma warning disable CA1031 // The first step may or may not have committed: unknown, audited, never a crash (review S5).
        catch (Exception failure)
#pragma warning restore CA1031
        {
            string detail = Invariant($"The first step did not confirm, so whether the map is now held is not known here; reconcile-task-type-stations reads what is there: {failure.GetType().Name}: {failure.Message}");
            string? auditId = await TryWriteAsync(
                facts.Entry(TaskTypeStationActivationAuditActions.ResultUnknown, TimedOutOrUnknown(failure), null,
                    violations, conclusion: "RESULT_UNKNOWN", detail: detail),
                now);
            return facts.Result(
                TaskTypeStationActivationOutcome.ResultUnknown, null, violations, auditId is null ? [] : [auditId], detail);
        }

        try
        {
            await _activations.CompleteAsync(attempt, now, cancellationToken);
        }
        catch (TaskTypeStationActivationConflictException overtaken)
        {
            // A reconciliation or a newer attempt got there first and wrote nothing of ours: what it wrote stands, so the map
            // is not re-marked here (review S1).
            return await ConcludeUnknownAsync(
                facts, attempt, violations, GovernanceActionOutcome.ResultUnknown,
                Invariant($"The second step was overtaken and wrote nothing: {overtaken.Message}"),
                now,
                remark: false);
        }
#pragma warning disable CA1031 // Any other failure of the second step means the same thing: the result is not known.
        catch (Exception failure)
#pragma warning restore CA1031
        {
            return await ConcludeUnknownAsync(
                facts, attempt, violations, TimedOutOrUnknown(failure),
                Invariant($"The second step did not confirm: {failure.GetType().Name}: {failure.Message}"),
                now);
        }

        TaskTypeStationActiveReadBack readBack;
        string activatedId;
        try
        {
            readBack = await _activations.ReadBackAsync(mapId, cancellationToken);
            if (readBack.ActivePointer?.ActiveVersion != attempt.TargetVersion
                || !string.Equals(readBack.ActivePointer.State, TaskTypeStationActivationState.Active, StringComparison.Ordinal)
                || !readBack.ContentVerified)
            {
                return await ConcludeUnknownAsync(
                    facts, attempt, violations, GovernanceActionOutcome.ResultUnknown,
                    Invariant($"The second step committed but reads back as version {readBack.ActivePointer?.ActiveVersion} in state {readBack.ActivePointer?.State}, not version {attempt.TargetVersion}: {readBack.Detail}"),
                    now);
            }
            activatedId = await _audit.WriteBusinessAsync(
                facts.Entry(TaskTypeStationActivationAuditActions.Activated, GovernanceActionOutcome.Succeeded,
                    attempt.TargetVersion, violations, conclusion: "TARGET_ACTIVE", readBack.Active?.SnapshotId,
                    readBack.Detail),
                now,
                cancellationToken);
        }
#pragma warning disable CA1031 // The switch committed but was not seen to: unknown, audited, never a crash (review round 2, N3).
        catch (Exception failure)
#pragma warning restore CA1031
        {
            return await ConcludeUnknownAsync(
                facts, attempt, violations, TimedOutOrUnknown(failure),
                Invariant($"The second step committed but its read-back or its audit did not complete: {failure.GetType().Name}: {failure.Message}"),
                now);
        }
        return facts.Result(
            TaskTypeStationActivationOutcome.Activated, attempt.TargetVersion, violations, [activatedId], readBack.Detail);
    }

    /// <summary>
    /// 第二步没有确认落地：把该图重新标成结果未知并确保需求集合里每个任务类型都暂停着，写一条结果未知审计。
    /// 这里的每一步都尽力而为——库此刻可能正是出问题的那一方；做不成的部分由第一步已提交的暂停兜底，结论照样是「未知」，从不是「已生效」。
    /// </summary>
    private async Task<TaskTypeStationActivationResult> ConcludeUnknownAsync(
        ActivationAuditFacts facts,
        TaskTypeStationActivationAttempt attempt,
        IReadOnlyList<TaskTypeStationViolation> violations,
        GovernanceActionOutcome outcome,
        string detail,
        DateTimeOffset now,
        bool remark = true)
    {
        List<string> auditIds = [];
        string reported = detail;
        // Deliberately not the caller's token: a cancelled operator must still leave the map held and say so.
        try
        {
            if (remark)
            {
                await _activations.MarkUnknownAsync(attempt, now, CancellationToken.None);
            }
        }
        catch (TaskTypeStationActivationConflictException overtaken)
        {
            // Someone else's verdict or activation stands; this attempt only records that it did not get to say (N2).
            reported += " " + overtaken.Message;
        }
#pragma warning disable CA1031 // Best effort; the holds committed in the first step already keep the map held.
        catch (Exception failure)
#pragma warning restore CA1031
        {
            reported += Invariant($" Re-marking the map as unknown also failed: {failure.GetType().Name}: {failure.Message}");
        }
        try
        {
            auditIds.Add(await _audit.WriteBusinessAsync(
                facts.Entry(TaskTypeStationActivationAuditActions.ResultUnknown, outcome, attempt.TargetVersion,
                    violations, conclusion: "RESULT_UNKNOWN", detail: reported),
                now,
                CancellationToken.None));
        }
#pragma warning disable CA1031 // The started record from the first step already names this attempt.
        catch (Exception failure)
#pragma warning restore CA1031
        {
            reported += Invariant($" Writing the result-unknown audit also failed: {failure.GetType().Name}: {failure.Message}");
        }
        return facts.Result(TaskTypeStationActivationOutcome.ResultUnknown, attempt.TargetVersion, violations, auditIds, reported);
    }

    /// <summary>SQLite 等锁超限（<c>SQLITE_BUSY</c> 5、<c>SQLITE_LOCKED</c> 6），按类型名认，Application 不引用驱动。</summary>
    private static bool IsLockTimeout(Exception failure)
    {
        for (Exception? current = failure; current is not null; current = current.InnerException)
        {
            if (current.GetType().Name == "SqliteException"
                && current.GetType().GetProperty("SqliteErrorCode")?.GetValue(current) is int code
                && code is 5 or 6)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 激活前重验（REQ-0337、REQ-0338、REQ-0343）：规则版本是当前版本、配置本身的静态校验（与启动装载同一个校验器）、目录新鲜且就是
    /// 服务端刚确认过的那一份、每个绑定站点在这份目录里按 id 与名字都在且同图。全部原因一次列出。
    /// </summary>
    private async Task<IReadOnlyList<TaskTypeStationViolation>> ValidateCandidateAsync(
        TaskTypeStationCandidate candidate,
        RiotMapStationCatalogSnapshot? catalog,
        MapStationCatalogAvailability? catalogState,
        TaskTypeStationActivePointer? pointer,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        List<TaskTypeStationViolation> violations = [];
        if (pointer is not null
            && string.Equals(pointer.State, TaskTypeStationActivationState.ActivationUnknown, StringComparison.Ordinal))
        {
            violations.Add(new(
                TaskTypeStationActivationReasonCodes.ActivationPendingReconciliation,
                null,
                null,
                Invariant($"Map {candidate.MapId} has an activation of version {pointer.PendingVersion} whose result is unknown; reconcile it before activating again.")));
        }

        TaskTypeStationRuleVersion? currentRules = await _rules.ReadCurrentAsync(cancellationToken);
        if (currentRules is null || currentRules.Version != candidate.RuleVersion)
        {
            violations.Add(new(
                TaskTypeStationActivationReasonCodes.RuleVersionNotCurrent,
                null,
                null,
                Invariant($"The candidate depends on rule version {candidate.RuleVersion}, but the current rule version is {(currentRules is null ? "none" : currentRules.Version.ToString(CultureInfo.InvariantCulture))}.")));
        }

        TaskTypeStationMapConfiguration map = new(
            candidate.MapId, candidate.RequiredTaskTypes ?? [], candidate.Bindings ?? []);
        violations.AddRange(TaskTypeStationConfigurationValidator.ValidateStatic(
            new TaskTypeStationConfiguration(currentRules?.Rules ?? [], map),
            candidate.MapId));

        violations.AddRange(TaskTypeStationCatalogEvidence.JudgeFreshness(candidate.MapId, catalog, catalogState, now));
        if (catalog is not null)
        {
            // Identity against the supplied list regardless of freshness, so one run names every reason.
            violations.AddRange(TaskTypeStationConfigurationValidator.EvaluateCatalog(
                candidate.MapId, map.Bindings, catalog, catalogFresh: true));
        }
        return violations;
    }

    private static List<TaskTypeStationBindingChange> Diff(
        IReadOnlyList<TaskTypeStationBinding> before,
        IReadOnlyList<TaskTypeStationBinding> after)
    {
        List<TaskTypeStationBindingChange> changes = [];
        foreach (string taskType in before.Select(binding => binding.TaskType)
                     .Concat(after.Select(binding => binding.TaskType))
                     .Distinct(StringComparer.Ordinal)
                     .Order(StringComparer.Ordinal))
        {
            TaskTypeStationBinding? was = before.FirstOrDefault(binding =>
                string.Equals(binding.TaskType, taskType, StringComparison.Ordinal));
            TaskTypeStationBinding? will = after.FirstOrDefault(binding =>
                string.Equals(binding.TaskType, taskType, StringComparison.Ordinal));
            if (was is not null && will is not null
                && was.StationRiotId == will.StationRiotId
                && string.Equals(was.StationName, will.StationName, StringComparison.Ordinal))
            {
                continue;
            }
            changes.Add(new TaskTypeStationBindingChange(taskType, was, will));
        }
        return changes;
    }

    /// <summary>该图在途需求按它们冻结的版本计：冻结站点与候选不同的那些列出来，它们仍按冻结的走（REQ-0345）。</summary>
    private async Task<TaskTypeStationImpactPreview> PreviewImpactAsync(
        int mapId,
        IReadOnlyList<TaskTypeStationBinding> candidateBindings,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<TaskTypeStationInFlightDemand> inFlight =
            await _activations.ListInFlightDemandsAsync(mapId, cancellationToken);
        Dictionary<long, TaskTypeStationBindingSetVersion?> frozen = [];
        List<TaskTypeStationInFlightImpact> affected = [];
        foreach (TaskTypeStationInFlightDemand demand in inFlight)
        {
            if (!frozen.TryGetValue(demand.FrozenBindingSetVersion, out TaskTypeStationBindingSetVersion? version))
            {
                version = await _bindings.ReadVersionAsync(mapId, demand.FrozenBindingSetVersion, cancellationToken);
                frozen[demand.FrozenBindingSetVersion] = version;
            }
            int? frozenStation = version?.Bindings
                .FirstOrDefault(binding => string.Equals(binding.TaskType, demand.TaskType, StringComparison.Ordinal))
                ?.StationRiotId;
            int? candidateStation = candidateBindings
                .FirstOrDefault(binding => string.Equals(binding.TaskType, demand.TaskType, StringComparison.Ordinal))
                ?.StationRiotId;
            if (frozenStation != candidateStation)
            {
                affected.Add(new TaskTypeStationInFlightImpact(
                    demand.DemandId, demand.TaskType, demand.FrozenBindingSetVersion, frozenStation, candidateStation));
            }
        }
        return new TaskTypeStationImpactPreview(inFlight.Count, affected);
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    private static object Station(TaskTypeStationBinding? binding) =>
        binding is null ? null! : new { stationRiotId = binding.StationRiotId, stationName = binding.StationName };

    /// <summary>
    /// 一次激活、回滚的审计素材（REQ-0348）：Map、各任务类型变更前后的 Station、目录修订、新旧规则与绑定版本、影响预览、
    /// 变更理由、现场核对、活动任务影响数量、校验结果、请求类别与结论，写进 <c>DetailJson</c>。
    /// </summary>
    private sealed record ActivationAuditFacts(
        string AttemptId,
        string Category,
        bool DryRun,
        TaskTypeStationChangeRequest Request,
        TaskTypeStationCandidate Candidate,
        TaskTypeStationBindingSetVersion? Active,
        TaskTypeStationActivePointer? Pointer,
        long? CatalogRevision,
        IReadOnlyList<TaskTypeStationBindingChange> Changes,
        TaskTypeStationImpactPreview Impact,
        long? RollbackOf)
    {
        /// <summary>第一步置暂停的任务类型；对账据此知道这次尝试该撤哪些。</summary>
        public IReadOnlyList<string> HeldTaskTypes { get; init; } = [];

        public GovernanceAuditEntry Entry(
            string action,
            GovernanceActionOutcome outcome,
            long? targetVersion,
            IReadOnlyList<TaskTypeStationViolation> violations,
            string conclusion,
            string? snapshotId = null,
            string? detail = null) =>
            new(
                action,
                GovernedObjectKind.PublicStationBinding,
                TaskTypeStationGovernance.BindingSetObjectId(Candidate.MapId),
                targetVersion,
                outcome,
                JsonSerializer.Serialize(
                    new
                    {
                        mapId = Candidate.MapId,
                        attemptId = AttemptId,
                        requestCategory = Category,
                        dryRun = DryRun,
                        rollbackOfVersion = RollbackOf,
                        reason = Request.Reason,
                        selfReportedRole = Request.SelfReportedRole,
                        selfReportedRoleNote = "Recorded as given; not verified. REQ-0336 two-level administrators are deferred and no personnel authentication exists.",
                        taskTypes = Changes.Select(change => new
                        {
                            taskType = change.TaskType,
                            before = Station(change.Before),
                            after = Station(change.After)
                        }),
                        requiredTaskTypes = Candidate.RequiredTaskTypes,
                        heldTaskTypes = HeldTaskTypes,
                        catalogRevision = CatalogRevision,
                        ruleVersion = new { previous = Active?.RuleVersion, next = Candidate.RuleVersion },
                        bindingSetVersion = new { previous = Pointer?.ActiveVersion, target = targetVersion },
                        siteVerification = (Candidate.Bindings ?? []).Select(binding => new
                        {
                            taskType = binding.TaskType,
                            stationRiotId = binding.StationRiotId,
                            reference = binding.SiteVerificationRef
                        }),
                        impactPreview = new
                        {
                            inFlightDemandCount = Impact.InFlightDemandCount,
                            affected = Impact.Affected.Select(item => new
                            {
                                demandId = item.DemandId,
                                taskType = item.TaskType,
                                frozenBindingSetVersion = item.FrozenBindingSetVersion,
                                frozenStationRiotId = item.FrozenStationRiotId,
                                candidateStationRiotId = item.CandidateStationRiotId
                            })
                        },
                        activeTaskImpactCount = Impact.Affected.Count,
                        validation = new
                        {
                            passed = violations.Count == 0,
                            violations = violations.Select(violation => new
                            {
                                reasonCode = violation.ReasonCode,
                                taskType = violation.TaskType,
                                stationRiotId = violation.StationRiotId,
                                detail = violation.Detail
                            })
                        },
                        conclusion,
                        detail
                    },
                    AuditJson),
                snapshotId,
                ClaimedAdministratorRole: null);

        public TaskTypeStationActivationResult Result(
            TaskTypeStationActivationOutcome outcome,
            long? targetVersion,
            IReadOnlyList<TaskTypeStationViolation> violations,
            IReadOnlyList<string> auditRecordIds,
            string? detail) =>
            new(
                outcome,
                Category,
                Candidate.MapId,
                AttemptId,
                Pointer?.ActiveVersion,
                targetVersion,
                Candidate.RuleVersion,
                CatalogRevision,
                violations,
                Changes,
                Impact,
                auditRecordIds,
                detail);
    }
}
