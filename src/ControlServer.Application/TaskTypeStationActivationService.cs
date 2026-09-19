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
    /// 对账（REQ-0347）：只读取实际生效的版本、它的身份与完整内容，再写结论。生效的是目标版本或仍是原版本，撤掉这次尝试的
    /// 「激活结果未知」暂停；两者都不是、或内容与记下的指纹不符，暂停保留并如实输出。没有未结尝试时也写一条审计。
    /// </summary>
    public async Task<TaskTypeStationReconciliationResult> ReconcileAsync(
        int mapId,
        TaskTypeStationChangeRequest request,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Reason);

        TaskTypeStationActivationAttempt? attempt = await _activations.ReadOpenAttemptAsync(mapId, cancellationToken);
        TaskTypeStationActiveReadBack readBack = await _activations.ReadBackAsync(mapId, cancellationToken);
        long? activeVersion = readBack.ActivePointer?.ActiveVersion;
        string? activeSha = readBack.Active?.ContentSha256;

        TaskTypeStationReconciliationConclusion conclusion;
        string detail;
        if (attempt is null)
        {
            conclusion = TaskTypeStationReconciliationConclusion.NothingToReconcile;
            detail = Invariant($"Map {mapId} has no open activation attempt; version {activeVersion} is active. {readBack.Detail}");
        }
        else if (activeVersion == attempt.TargetVersion && readBack.ContentVerified)
        {
            conclusion = TaskTypeStationReconciliationConclusion.TargetActive;
            detail = Invariant($"The target version {attempt.TargetVersion} is the one in force. {readBack.Detail}");
        }
        else if (activeVersion == attempt.PreviousVersion && (activeVersion is null || readBack.ContentVerified))
        {
            conclusion = TaskTypeStationReconciliationConclusion.PreviousActive;
            detail = Invariant($"The previous version {attempt.PreviousVersion} is still in force; the activation of version {attempt.TargetVersion} did not happen. {readBack.Detail}");
        }
        else
        {
            conclusion = TaskTypeStationReconciliationConclusion.Contradictory;
            detail = Invariant($"Neither the target version {attempt.TargetVersion} nor the previous version {attempt.PreviousVersion} reads back whole: {readBack.Detail} The holds stay.");
        }

        IReadOnlyList<string> released = [];
        if (attempt is not null && conclusion != TaskTypeStationReconciliationConclusion.Contradictory)
        {
            released = await _activations.ResolveAsync(attempt, now, cancellationToken);
        }

        string auditId = await _audit.WriteBusinessAsync(
            new GovernanceAuditEntry(
                TaskTypeStationActivationAuditActions.Reconciled,
                GovernedObjectKind.PublicStationBinding,
                TaskTypeStationGovernance.BindingSetObjectId(mapId),
                attempt?.TargetVersion,
                conclusion == TaskTypeStationReconciliationConclusion.Contradictory
                    ? GovernanceActionOutcome.ResultUnknown
                    : GovernanceActionOutcome.Succeeded,
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
                            active = activeVersion
                        },
                        activeContentSha256 = activeSha,
                        contentVerified = readBack.ContentVerified,
                        heldTaskTypes = attempt?.HeldTaskTypes ?? [],
                        releasedHoldIds = released,
                        conclusion = ConclusionName(conclusion),
                        detail
                    },
                    AuditJson),
                readBack.Active?.SnapshotId),
            now,
            cancellationToken);
        return new TaskTypeStationReconciliationResult(
            conclusion, mapId, attempt?.AttemptId, attempt?.PreviousVersion, attempt?.TargetVersion, activeVersion, activeSha,
            released, auditId, detail);
    }

    public static string ConclusionName(TaskTypeStationReconciliationConclusion conclusion) => conclusion switch
    {
        TaskTypeStationReconciliationConclusion.TargetActive => "TARGET_ACTIVE",
        TaskTypeStationReconciliationConclusion.PreviousActive => "PREVIOUS_ACTIVE",
        TaskTypeStationReconciliationConclusion.Contradictory => "CONTRADICTORY",
        _ => "NOTHING_TO_RECONCILE"
    };

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
                Invariant($"{taskType} has no binding in Map {mapId}'s active version {active?.Version}, so there is nothing to revalidate.")));
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

        IReadOnlyList<TaskTypeStationHold> released = [];
        if (violations.Count == 0)
        {
            released = await _activations.ReleaseManualAndCatalogHoldsAsync(
                mapId, taskType, "fieldops:release-hold:" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
                now, cancellationToken);
            if (released.Count == 0)
            {
                violations.Add(new(
                    TaskTypeStationActivationReasonCodes.NoHoldToRelease, taskType, null,
                    Invariant($"Map {mapId} {taskType} has no unreleased manual or catalog-change hold.")));
            }
        }

        bool ok = violations.Count == 0;
        string auditId = await _audit.WriteBusinessAsync(
            new GovernanceAuditEntry(
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
                            violations = violations.Select(violation => new
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
                active?.SnapshotId),
            now,
            cancellationToken);
        return new TaskTypeStationHoldReleaseResult(
            ok ? TaskTypeStationHoldReleaseOutcome.Released : TaskTypeStationHoldReleaseOutcome.Rejected,
            mapId, taskType, violations, released, auditId);
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

        try
        {
            await _activations.CompleteAsync(attempt, now, cancellationToken);
        }
#pragma warning disable CA1031 // Any failure of the second step means the same thing: the result is not known.
        catch (Exception failure)
#pragma warning restore CA1031
        {
            GovernanceActionOutcome outcome = failure is TimeoutException || IsLockTimeout(failure)
                ? GovernanceActionOutcome.TimedOut
                : GovernanceActionOutcome.ResultUnknown;
            return await ConcludeUnknownAsync(
                facts, attempt, violations, outcome,
                Invariant($"The second step did not confirm: {failure.GetType().Name}: {failure.Message}"),
                now);
        }

        TaskTypeStationActiveReadBack readBack = await _activations.ReadBackAsync(mapId, cancellationToken);
        if (readBack.ActivePointer?.ActiveVersion != attempt.TargetVersion
            || !string.Equals(readBack.ActivePointer.State, TaskTypeStationActivationState.Active, StringComparison.Ordinal)
            || !readBack.ContentVerified)
        {
            return await ConcludeUnknownAsync(
                facts, attempt, violations, GovernanceActionOutcome.ResultUnknown,
                Invariant($"The second step committed but reads back as version {readBack.ActivePointer?.ActiveVersion} in state {readBack.ActivePointer?.State}, not version {attempt.TargetVersion}: {readBack.Detail}"),
                now);
        }
        string activatedId = await _audit.WriteBusinessAsync(
            facts.Entry(TaskTypeStationActivationAuditActions.Activated, GovernanceActionOutcome.Succeeded,
                attempt.TargetVersion, violations, conclusion: "TARGET_ACTIVE", readBack.Active?.SnapshotId,
                readBack.Detail),
            now,
            cancellationToken);
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
        DateTimeOffset now)
    {
        List<string> auditIds = [];
        string reported = detail;
        // Deliberately not the caller's token: a cancelled operator must still leave the map held and say so.
        try
        {
            await _activations.MarkUnknownAsync(attempt, now, CancellationToken.None);
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
            candidate.MapId,
            gateScalar: null));

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
