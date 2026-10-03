using System.Globalization;
using System.Text.Json;
using ControlServer.Domain;

namespace ControlServer.Application;

// 批次9-02（control-server#400）：ChargingPolicyVersion 的受治理导入、批准、激活，阈值关系的唯一定义，按周期冻结的两个读法，
// 以及逐车投运判定（REQ-0281、REQ-0282；规格 8.6：缺已批准版本是硬阻断，逐车判定）。
// 持久化是 control-server#399 的 IChargingPolicyStore，本票零 migration。

/// <summary>策略文件、批准、激活被拒时的原因码。</summary>
public static class ChargingPolicyReasonCodes
{
    /// <summary>文件不是受控的 JSON：解析不了、缺字段、多字段、字段类型不对。</summary>
    public const string FileMalformed = "CHARGING_POLICY_FILE_MALFORMED";

    /// <summary>单个字段超出取值范围。</summary>
    public const string ValueOutOfRange = "CHARGING_POLICY_VALUE_OUT_OF_RANGE";

    /// <summary>三个阈值不满足 <c>ChargingCompletionThreshold &gt; MandatoryChargeEntryThreshold &gt;= 最低任务后电量余量</c>（<c>REQ-0281</c>）。</summary>
    public const string ThresholdRelationViolated = "CHARGING_POLICY_THRESHOLD_RELATION_VIOLATED";

    /// <summary>适用车辆里有 <c>--fleet</c>（投运名册）之外的车。</summary>
    public const string VehicleOutsideFleet = "CHARGING_POLICY_VEHICLE_OUTSIDE_FLEET";

    public const string VersionNotFound = "CHARGING_POLICY_VERSION_NOT_FOUND";

    /// <summary>要激活的版本还没有任何批准（<c>REQ-0282</c>：只有已批准的版本能被激活）。</summary>
    public const string VersionNotApproved = "CHARGING_POLICY_VERSION_NOT_APPROVED";

    /// <summary>
    /// 要激活的版本只有测试夹具或 L2 预置的批准，没有现场批准，而激活没有明说接受（<c>--allow-non-field-approval</c>）。
    /// </summary>
    public const string OnlyNonFieldApproval = "CHARGING_POLICY_ONLY_NON_FIELD_APPROVAL";

    /// <summary>批准的来源不是 <see cref="ChargingPolicyApprovalSources"/> 之一。</summary>
    public const string ApprovalSourceUnknown = "CHARGING_POLICY_APPROVAL_SOURCE_UNKNOWN";
}

/// <summary>批准与激活的审计动作名。写版本那一条是 <see cref="ChargingGovernance.PolicyVersionWrittenAction"/>，由治理发布写。</summary>
public static class ChargingPolicyAudit
{
    public const string ApprovedAction = "CHARGING_POLICY_VERSION_APPROVED";

    public const string ActivatedAction = "CHARGING_POLICY_VERSION_ACTIVATED";
}

/// <summary>一处违例。</summary>
public sealed record ChargingPolicyViolation(string ReasonCode, string Field, string Detail);

/// <summary>
/// 一版策略内容的校验规则，<b>全仓只有这一份</b>：导入在这里判，服务端启动时（批次9-05）也调这里，不在别处另写。
/// </summary>
public static class ChargingPolicyRules
{
    /// <summary>稳定期与观察窗口的上限：一天。</summary>
    public const int MaximumSeconds = 86_400;

    /// <summary>
    /// <c>REQ-0281</c> 的硬关系：<c>ChargingCompletionThreshold &gt; MandatoryChargeEntryThreshold &gt;= 最低任务后电量余量</c>。
    /// 不满足的每一条各报一处；满足时为空。
    /// </summary>
    public static IReadOnlyList<ChargingPolicyViolation> ThresholdRelationViolations(
        int chargingCompletionThresholdPercent,
        int mandatoryChargeEntryThresholdPercent,
        int minimumPostTaskBatteryMarginPercent)
    {
        List<ChargingPolicyViolation> violations = [];
        if (chargingCompletionThresholdPercent <= mandatoryChargeEntryThresholdPercent)
        {
            violations.Add(new ChargingPolicyViolation(
                ChargingPolicyReasonCodes.ThresholdRelationViolated,
                "chargingCompletionThresholdPercent",
                Invariant($"ChargingCompletionThreshold {chargingCompletionThresholdPercent} must be greater than MandatoryChargeEntryThreshold {mandatoryChargeEntryThresholdPercent} (REQ-0281).")));
        }
        if (mandatoryChargeEntryThresholdPercent < minimumPostTaskBatteryMarginPercent)
        {
            violations.Add(new ChargingPolicyViolation(
                ChargingPolicyReasonCodes.ThresholdRelationViolated,
                "mandatoryChargeEntryThresholdPercent",
                Invariant($"MandatoryChargeEntryThreshold {mandatoryChargeEntryThresholdPercent} must be at least the minimum post-task battery margin {minimumPostTaskBatteryMarginPercent} (REQ-0281).")));
        }
        return violations;
    }

    /// <summary>单字段取值范围加上阈值关系。满足时为空。</summary>
    public static IReadOnlyList<ChargingPolicyViolation> Validate(ChargingPolicyContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        List<ChargingPolicyViolation> violations = [];
        Range("minimumPostTaskBatteryMarginPercent", content.MinimumPostTaskBatteryMarginPercent, 0, 100);
        Range("mandatoryChargeEntryThresholdPercent", content.MandatoryChargeEntryThresholdPercent, 0, 100);
        Range("chargingCompletionThresholdPercent", content.ChargingCompletionThresholdPercent, 0, 100);
        Range("estimatedTaskConsumptionPercent", content.EstimatedTaskConsumptionPercent, 0, 100);
        Range("progressStabilizationSeconds", content.ProgressStabilizationSeconds, 1, MaximumSeconds);
        Range("progressObservationWindowSeconds", content.ProgressObservationWindowSeconds, 1, MaximumSeconds);
        Range("progressMinimumIncreasePercent", content.ProgressMinimumIncreasePercent, 1, 100);
        violations.AddRange(ThresholdRelationViolations(
            content.ChargingCompletionThresholdPercent,
            content.MandatoryChargeEntryThresholdPercent,
            content.MinimumPostTaskBatteryMarginPercent));
        return violations;

        void Range(string field, int value, int minimum, int maximum)
        {
            if (value < minimum || value > maximum)
            {
                violations.Add(new ChargingPolicyViolation(
                    ChargingPolicyReasonCodes.ValueOutOfRange,
                    field,
                    Invariant($"{field} is {value}; it must be between {minimum} and {maximum}.")));
            }
        }
    }

    /// <summary>适用范围为空即全部投运车辆。</summary>
    public static IReadOnlyList<string> Covered(ChargingPolicyContent? content, IReadOnlyList<string> fleet)
    {
        ArgumentNullException.ThrowIfNull(fleet);
        if (content is null)
        {
            return [];
        }
        return content.VehicleScope.Count == 0
            ? [.. fleet]
            : [.. fleet.Where(vehicleKey => content.VehicleScope.Contains(vehicleKey, StringComparer.Ordinal))];
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}

/// <summary>策略的全局现状：最近一次激活与它的版本；没有激活过为空。</summary>
public sealed record ChargingPolicyActiveState(ChargingPolicyActivation Activation, ChargingPolicyVersion Policy);

/// <summary>策略导入、批准、激活要判的库内事实。</summary>
public interface IChargingPolicyGovernanceFacts
{
    /// <summary>版本号最大的那一版的号；一版都没有为空。</summary>
    Task<long?> ReadLatestVersionNumberAsync(CancellationToken cancellationToken);

    /// <summary>最近一次激活；没有为空。</summary>
    Task<ChargingPolicyActivation?> ReadLatestActivationAsync(CancellationToken cancellationToken);

    /// <summary>全部激活，按序号排。</summary>
    Task<IReadOnlyList<ChargingPolicyActivation>> ListActivationsAsync(CancellationToken cancellationToken);

    /// <summary>全部未结束的充电周期，按分配时刻排。</summary>
    Task<IReadOnlyList<ChargingCycle>> ListOpenCyclesAsync(CancellationToken cancellationToken);
}

/// <summary>操作的结论。</summary>
public enum ChargingPolicyOperationOutcome
{
    /// <summary>做成（<c>--dry-run</c> 时只预览）。</summary>
    Accepted,

    /// <summary>与现状相同，什么也没写：导入的内容等于最新版本，或激活的就是当前生效版本。</summary>
    Unchanged,

    /// <summary>被拒，什么也没写。</summary>
    Rejected
}

/// <summary>一个值从哪个变成哪个。</summary>
public sealed record ChargingPolicyFieldChange(string Field, string? Before, string? After);

/// <summary>
/// 相对当前生效版本，这一版改了什么、影响哪些车，以及正在按旧快照进行、不受影响的充电周期（<c>REQ-0282</c>）。
/// </summary>
/// <param name="ComparedWith">比较的对象：当前生效（最近一次激活）的版本；从没激活过为空。</param>
/// <param name="CoveredAfter">这一版生效后有策略的车。</param>
/// <param name="VehiclesLosingPolicy">
/// 现在有生效策略、这一版生效后就没有的车：它们会立刻不承接新用途（规格 8.6 逐车硬阻断）。策略按「全局一个生效版本」存，
/// 激活一版只覆盖部分车的策略，范围外的车就是这里列的。
/// </param>
/// <param name="VehiclesWithoutPolicyAfter">这一版生效后仍（或变得）没有策略的全部车。</param>
public sealed record ChargingPolicyImpact(
    long? ComparedWith,
    IReadOnlyList<ChargingPolicyFieldChange> Changes,
    IReadOnlyList<string> CoveredAfter,
    IReadOnlyList<string> VehiclesLosingPolicy,
    IReadOnlyList<string> VehiclesWithoutPolicyAfter,
    IReadOnlyList<ChargingCycle> OpenCycles)
{
    public const string Retention =
        "Charging cycles already under way keep the policy version they froze until they end; a new version applies only to "
        + "new dispatch decisions and new charging cycles (REQ-0282).";
}

public sealed record ChargingPolicyImportResult(
    ChargingPolicyOperationOutcome Outcome,
    bool DryRun,
    long? PreviousVersion,
    ChargingPolicyVersion? Version,
    ChargingPolicyContent? Content,
    IReadOnlyList<ChargingPolicyViolation> Errors,
    ChargingPolicyImpact? Impact);

public sealed record ChargingPolicyApprovalResult(
    ChargingPolicyOperationOutcome Outcome,
    ChargingPolicyApproval? Approval,
    IReadOnlyList<ChargingPolicyViolation> Errors);

public sealed record ChargingPolicyActivationResult(
    ChargingPolicyOperationOutcome Outcome,
    bool DryRun,
    long? PreviousActiveVersion,
    ChargingPolicyActivation? Activation,
    IReadOnlyList<ChargingPolicyApproval> Approvals,
    IReadOnlyList<ChargingPolicyViolation> Errors,
    ChargingPolicyImpact? Impact);

/// <summary>
/// FieldOps 的策略版本导入、批准、激活（<c>REQ-0282</c>）。
/// </summary>
/// <remarks>
/// <para>
/// <b>三步各自留痕，一次普通导入顺带不了后两步。</b>导入只写一个版本（治理快照与业务审计）；批准写批准人、角色、依据引用与来源，
/// 记一条审计；激活只接受已批准的版本，记一条审计。每一步各是一个调用方的写事务，行与审计要么一起在、要么一起不在。
/// </para>
/// <para>
/// <b>没有开发默认值。</b>这里不给任何字段缺省值；服务端出厂不带任何策略版本，一版都没激活时每辆车都不投运。
/// </para>
/// <para>
/// <b>测试批准不冒充现场批准。</b>来源为 <c>TEST_FIXTURE</c> 或 <c>L2_PRESET</c> 的批准照样写进库（L2 与测试夹具靠它），但一个版本只有
/// 这两种批准时，激活要显式带 <c>--allow-non-field-approval</c>，审计记下这一点——现场照说明操作不会带它，生产库因此不会不知不觉地
/// 用上一版测试批准的策略。
/// </para>
/// <para>
/// <b>激活不要求先禁用车辆。</b>新版本只作用于新派车判断与新充电周期；已开始的周期按它记下的版本号读回（<see cref="ChargingPolicyResolver"/>）。
/// </para>
/// </remarks>
public sealed class ChargingPolicyGovernanceService(
    IChargingPolicyStore store,
    IChargingPolicyGovernanceFacts facts,
    IGovernanceAuditWriter audit)
{
    private static readonly string[] FileFields =
    [
        "minimumPostTaskBatteryMarginPercent", "mandatoryChargeEntryThresholdPercent", "chargingCompletionThresholdPercent",
        "estimatedTaskConsumptionPercent", "progressStabilizationSeconds", "progressObservationWindowSeconds",
        "progressMinimumIncreasePercent", "vehicleScope", "changeNote"
    ];

    private readonly IChargingPolicyStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly IChargingPolicyGovernanceFacts _facts = facts ?? throw new ArgumentNullException(nameof(facts));
    private readonly IGovernanceAuditWriter _audit = audit ?? throw new ArgumentNullException(nameof(audit));

    /// <summary>现在生效的是哪一版（全局）；从没激活过为空。</summary>
    public async Task<ChargingPolicyActiveState?> ReadActiveAsync(CancellationToken cancellationToken)
    {
        ChargingPolicyActivation? activation = await _facts.ReadLatestActivationAsync(cancellationToken).ConfigureAwait(false);
        if (activation is null)
        {
            return null;
        }
        ChargingPolicyVersion? policy = await _store.ReadVersionAsync(activation.Version, cancellationToken).ConfigureAwait(false);
        return policy is null ? null : new ChargingPolicyActiveState(activation, policy);
    }

    /// <summary>导入一版策略文件：校验、diff、写版本。调用方在一个写事务里调用（非预览时）。</summary>
    public async Task<ChargingPolicyImportResult> ImportAsync(
        string fileText,
        IReadOnlyList<string> fleet,
        bool dryRun,
        DateTimeOffset writtenAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fileText);
        ArgumentNullException.ThrowIfNull(fleet);

        long? latest = await _facts.ReadLatestVersionNumberAsync(cancellationToken).ConfigureAwait(false);
        List<ChargingPolicyViolation> errors = [];
        (ChargingPolicyContent? content, string? changeNote) = ReadFile(fileText, errors);
        if (content is not null)
        {
            errors.AddRange(ChargingPolicyRules.Validate(content));
            HashSet<string> commissioned = new(fleet, StringComparer.Ordinal);
            errors.AddRange(content.VehicleScope.Where(vehicleKey => !commissioned.Contains(vehicleKey)).Select(vehicleKey =>
                new ChargingPolicyViolation(
                    ChargingPolicyReasonCodes.VehicleOutsideFleet,
                    "vehicleScope",
                    $"The policy applies to {vehicleKey}, which is not in the fleet (--fleet).")));
        }
        if (errors.Count > 0 || content is null)
        {
            return new ChargingPolicyImportResult(
                ChargingPolicyOperationOutcome.Rejected, dryRun, latest, null, content, errors, null);
        }

        ChargingPolicyContent normalised = content with
        {
            VehicleScope = [.. content.VehicleScope.Order(StringComparer.Ordinal)]
        };
        ChargingPolicyImpact impact = await ImpactAsync(normalised, fleet, cancellationToken).ConfigureAwait(false);
        ChargingPolicyVersion? latestVersion = latest is long number
            ? await _store.ReadVersionAsync(number, cancellationToken).ConfigureAwait(false)
            : null;
        // Compared with the latest version, as the store does: an identical file writes nothing whether or not it was activated.
        if (latestVersion is not null && SameContent(latestVersion.Content, normalised))
        {
            return new ChargingPolicyImportResult(
                ChargingPolicyOperationOutcome.Unchanged, dryRun, latest, latestVersion, normalised, [], impact);
        }

        ChargingPolicyVersion? written = dryRun
            ? null
            : await _store.WriteVersionAsync(normalised, changeNote, writtenAt, cancellationToken).ConfigureAwait(false);
        return new ChargingPolicyImportResult(
            ChargingPolicyOperationOutcome.Accepted, dryRun, latest, written, normalised, [], impact);
    }

    /// <summary>给一版记一次批准，并写一条审计。调用方在一个写事务里调用。</summary>
    public async Task<ChargingPolicyApprovalResult> ApproveAsync(
        long version,
        string approvedBy,
        string approverRole,
        string basisReference,
        string source,
        DateTimeOffset approvedAt,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(approvedBy);
        ArgumentException.ThrowIfNullOrWhiteSpace(approverRole);
        ArgumentException.ThrowIfNullOrWhiteSpace(basisReference);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        List<ChargingPolicyViolation> errors = [];
        ChargingPolicyVersion? policy = await _store.ReadVersionAsync(version, cancellationToken).ConfigureAwait(false);
        if (policy is null)
        {
            errors.Add(new ChargingPolicyViolation(ChargingPolicyReasonCodes.VersionNotFound, "version",
                string.Create(CultureInfo.InvariantCulture, $"Charging policy version {version} does not exist.")));
        }
        if (!ChargingPolicyApprovalSources.All.Contains(source, StringComparer.Ordinal))
        {
            errors.Add(new ChargingPolicyViolation(ChargingPolicyReasonCodes.ApprovalSourceUnknown, "source",
                $"The approval source is one of {string.Join(", ", ChargingPolicyApprovalSources.All)}, not '{source}'."));
        }
        if (errors.Count > 0)
        {
            return new ChargingPolicyApprovalResult(ChargingPolicyOperationOutcome.Rejected, null, errors);
        }

        ChargingPolicyApproval approval = await _store.ApproveAsync(
            version, approvedBy, approverRole, approvedAt, basisReference, source, cancellationToken).ConfigureAwait(false);
        await _audit.WriteBusinessAsync(
            new GovernanceAuditEntry(
                ChargingPolicyAudit.ApprovedAction,
                GovernedObjectKind.ChargingPolicy,
                ChargingGovernance.PolicyObjectId,
                version,
                GovernanceActionOutcome.Succeeded,
                JsonSerializer.Serialize(new
                {
                    approvalId = approval.ApprovalId,
                    approvedBy,
                    approverRole,
                    basisReference,
                    source
                }),
                policy!.SnapshotId),
            approvedAt,
            cancellationToken).ConfigureAwait(false);
        return new ChargingPolicyApprovalResult(ChargingPolicyOperationOutcome.Accepted, approval, []);
    }

    /// <summary>激活一个已批准的版本，并写一条审计。调用方在一个写事务里调用（非预览时）。</summary>
    public async Task<ChargingPolicyActivationResult> ActivateAsync(
        long version,
        string activatedBy,
        IReadOnlyList<string> fleet,
        bool allowNonFieldApproval,
        bool dryRun,
        DateTimeOffset activatedAt,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(activatedBy);
        ArgumentNullException.ThrowIfNull(fleet);

        ChargingPolicyActiveState? active = await ReadActiveAsync(cancellationToken).ConfigureAwait(false);
        ChargingPolicyVersion? policy = await _store.ReadVersionAsync(version, cancellationToken).ConfigureAwait(false);
        if (policy is null)
        {
            return new ChargingPolicyActivationResult(
                ChargingPolicyOperationOutcome.Rejected, dryRun, active?.Policy.Version, null, [],
                [new ChargingPolicyViolation(ChargingPolicyReasonCodes.VersionNotFound, "version",
                    string.Create(CultureInfo.InvariantCulture, $"Charging policy version {version} does not exist."))],
                null);
        }

        IReadOnlyList<ChargingPolicyApproval> approvals =
            await _store.ListApprovalsAsync(version, cancellationToken).ConfigureAwait(false);
        ChargingPolicyImpact impact = await ImpactAsync(policy.Content, fleet, cancellationToken).ConfigureAwait(false);
        List<ChargingPolicyViolation> errors = [];
        if (approvals.Count == 0)
        {
            errors.Add(new ChargingPolicyViolation(ChargingPolicyReasonCodes.VersionNotApproved, "version",
                string.Create(CultureInfo.InvariantCulture,
                    $"Charging policy version {version} has no approval; only an approved version can be activated (REQ-0282).")));
        }
        else if (!approvals.Any(approval => approval.Source == ChargingPolicyApprovalSources.Field) && !allowNonFieldApproval)
        {
            errors.Add(new ChargingPolicyViolation(ChargingPolicyReasonCodes.OnlyNonFieldApproval, "version",
                string.Create(CultureInfo.InvariantCulture,
                    $"Charging policy version {version} is approved only as {string.Join(", ", approvals.Select(approval => approval.Source).Distinct())}, not by the field. Pass --allow-non-field-approval only on a test or L2 database.")));
        }
        if (errors.Count > 0)
        {
            return new ChargingPolicyActivationResult(
                ChargingPolicyOperationOutcome.Rejected, dryRun, active?.Policy.Version, null, approvals, errors, impact);
        }
        if (active?.Policy.Version == version)
        {
            return new ChargingPolicyActivationResult(
                ChargingPolicyOperationOutcome.Unchanged, dryRun, version, active.Activation, approvals, [], impact);
        }
        if (dryRun)
        {
            return new ChargingPolicyActivationResult(
                ChargingPolicyOperationOutcome.Accepted, dryRun, active?.Policy.Version, null, approvals, [], impact);
        }

        ChargingPolicyActivation activation = await _store.ActivateAsync(version, activatedBy, activatedAt, cancellationToken)
            .ConfigureAwait(false);
        await _audit.WriteBusinessAsync(
            new GovernanceAuditEntry(
                ChargingPolicyAudit.ActivatedAction,
                GovernedObjectKind.ChargingPolicy,
                ChargingGovernance.PolicyObjectId,
                version,
                GovernanceActionOutcome.Succeeded,
                JsonSerializer.Serialize(new
                {
                    activationId = activation.ActivationId,
                    sequence = activation.Sequence,
                    activatedBy,
                    previousActiveVersion = active?.Policy.Version,
                    approvalSources = approvals.Select(approval => approval.Source).Distinct().Order(StringComparer.Ordinal),
                    allowNonFieldApproval,
                    vehiclesLosingPolicy = impact.VehiclesLosingPolicy,
                    vehiclesWithoutPolicyAfter = impact.VehiclesWithoutPolicyAfter
                }),
                policy.SnapshotId),
            activatedAt,
            cancellationToken).ConfigureAwait(false);
        return new ChargingPolicyActivationResult(
            ChargingPolicyOperationOutcome.Accepted, dryRun, active?.Policy.Version, activation, approvals, [], impact);
    }

    /// <summary>相对当前生效版本的 diff 与影响。</summary>
    public async Task<ChargingPolicyImpact> ImpactAsync(
        ChargingPolicyContent proposed,
        IReadOnlyList<string> fleet,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(proposed);
        ArgumentNullException.ThrowIfNull(fleet);
        ChargingPolicyActiveState? active = await ReadActiveAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<ChargingCycle> cycles = await _facts.ListOpenCyclesAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<string> coveredBefore = ChargingPolicyRules.Covered(active?.Policy.Content, fleet);
        IReadOnlyList<string> coveredAfter = ChargingPolicyRules.Covered(proposed, fleet);
        return new ChargingPolicyImpact(
            active?.Policy.Version,
            Changes(active?.Policy.Content, proposed),
            coveredAfter,
            [.. coveredBefore.Except(coveredAfter, StringComparer.Ordinal)],
            [.. fleet.Except(coveredAfter, StringComparer.Ordinal)],
            cycles);
    }

    // Field by field: record equality compares the scope lists by reference.
    private static bool SameContent(ChargingPolicyContent left, ChargingPolicyContent right) =>
        left.MinimumPostTaskBatteryMarginPercent == right.MinimumPostTaskBatteryMarginPercent
        && left.MandatoryChargeEntryThresholdPercent == right.MandatoryChargeEntryThresholdPercent
        && left.ChargingCompletionThresholdPercent == right.ChargingCompletionThresholdPercent
        && left.EstimatedTaskConsumptionPercent == right.EstimatedTaskConsumptionPercent
        && left.ProgressStabilizationSeconds == right.ProgressStabilizationSeconds
        && left.ProgressObservationWindowSeconds == right.ProgressObservationWindowSeconds
        && left.ProgressMinimumIncreasePercent == right.ProgressMinimumIncreasePercent
        && left.VehicleScope.Order(StringComparer.Ordinal).SequenceEqual(right.VehicleScope.Order(StringComparer.Ordinal), StringComparer.Ordinal);

    private static IReadOnlyList<ChargingPolicyFieldChange> Changes(ChargingPolicyContent? before, ChargingPolicyContent after)
    {
        (string Field, Func<ChargingPolicyContent, string> Read)[] fields =
        [
            ("minimumPostTaskBatteryMarginPercent", content => Text(content.MinimumPostTaskBatteryMarginPercent)),
            ("mandatoryChargeEntryThresholdPercent", content => Text(content.MandatoryChargeEntryThresholdPercent)),
            ("chargingCompletionThresholdPercent", content => Text(content.ChargingCompletionThresholdPercent)),
            ("estimatedTaskConsumptionPercent", content => Text(content.EstimatedTaskConsumptionPercent)),
            ("progressStabilizationSeconds", content => Text(content.ProgressStabilizationSeconds)),
            ("progressObservationWindowSeconds", content => Text(content.ProgressObservationWindowSeconds)),
            ("progressMinimumIncreasePercent", content => Text(content.ProgressMinimumIncreasePercent)),
            ("vehicleScope", content => content.VehicleScope.Count == 0
                ? "(every vehicle)"
                : string.Join(";", content.VehicleScope.Order(StringComparer.Ordinal))),
        ];
        return
        [
            .. fields
                .Select(field => new ChargingPolicyFieldChange(field.Field, before is null ? null : field.Read(before), field.Read(after)))
                .Where(change => !string.Equals(change.Before, change.After, StringComparison.Ordinal))
        ];

        static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 受控格式：UTF-8 JSON，一个对象，字段逐字是七个整数值、<c>vehicleScope</c>（字符串数组，空即全部车辆）与 <c>changeNote</c>
    /// （字符串或 <c>null</c>）——不多不少，没有缺省值。
    /// </summary>
    private static (ChargingPolicyContent? Content, string? ChangeNote) ReadFile(string text, List<ChargingPolicyViolation> errors)
    {
        string shape =
            "The policy file is one JSON object with exactly these fields and no others, none defaulted: "
            + string.Join(", ", FileFields)
            + ". The seven values are whole numbers, vehicleScope is an array of distinct VehicleKeys (empty for every vehicle) and "
            + "changeNote is a string or null.";
        try
        {
            using JsonDocument document = JsonDocument.Parse(text.TrimStart('﻿'));
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal)
                    .SequenceEqual(FileFields.Order(StringComparer.Ordinal), StringComparer.Ordinal))
            {
                errors.Add(new ChargingPolicyViolation(ChargingPolicyReasonCodes.FileMalformed, "(file)", shape));
                return (null, null);
            }
            int[] values = new int[7];
            for (int index = 0; index < 7; index++)
            {
                JsonElement value = root.GetProperty(FileFields[index]);
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out values[index]))
                {
                    errors.Add(new ChargingPolicyViolation(ChargingPolicyReasonCodes.FileMalformed, FileFields[index], shape));
                }
            }
            JsonElement scopeElement = root.GetProperty("vehicleScope");
            List<string> scope = [];
            bool scopeValid = scopeElement.ValueKind == JsonValueKind.Array;
            if (scopeValid)
            {
                foreach (JsonElement key in scopeElement.EnumerateArray())
                {
                    string? vehicleKey = key.ValueKind == JsonValueKind.String ? key.GetString() : null;
                    if (string.IsNullOrWhiteSpace(vehicleKey) || !string.Equals(vehicleKey, vehicleKey.Trim(), StringComparison.Ordinal))
                    {
                        scopeValid = false;
                        break;
                    }
                    scope.Add(vehicleKey);
                }
                scopeValid = scopeValid && scope.Distinct(StringComparer.Ordinal).Count() == scope.Count;
            }
            if (!scopeValid)
            {
                errors.Add(new ChargingPolicyViolation(ChargingPolicyReasonCodes.FileMalformed, "vehicleScope", shape));
            }
            JsonElement note = root.GetProperty("changeNote");
            string? changeNote = note.ValueKind == JsonValueKind.String ? note.GetString() : null;
            if (note.ValueKind is not (JsonValueKind.String or JsonValueKind.Null) || (changeNote is not null && changeNote.Trim().Length == 0))
            {
                errors.Add(new ChargingPolicyViolation(ChargingPolicyReasonCodes.FileMalformed, "changeNote", shape));
            }
            if (errors.Count > 0)
            {
                return (null, null);
            }
            return (new ChargingPolicyContent(values[0], values[1], values[2], values[3], values[4], values[5], values[6], [.. scope]),
                changeNote);
        }
        catch (JsonException malformed)
        {
            errors.Add(new ChargingPolicyViolation(ChargingPolicyReasonCodes.FileMalformed, "(file)", $"{shape} ({malformed.Message})"));
            return (null, null);
        }
    }
}

/// <summary>逐车投运判定的原因。</summary>
public static class ChargingPolicyCommissioningReasons
{
    /// <summary>有已批准、已激活、适用范围覆盖这辆车的策略版本。</summary>
    public const string Effective = "CHARGING_POLICY_EFFECTIVE";

    /// <summary>没有：一版都没激活过，或最近激活的那一版不覆盖这辆车，或那一版没有批准。</summary>
    public const string NotApproved = "CHARGING_POLICY_NOT_APPROVED";

    /// <summary>读不到（库异常）。按「没有」处理，fail-closed。</summary>
    public const string Unreadable = "CHARGING_POLICY_UNREADABLE";
}

/// <summary>
/// 一辆车此刻能不能承接新用途（规格 8.6：<c>ChargingPolicyVersion</c> 缺失是硬阻断，逐车判定）。
/// </summary>
/// <param name="Effective">生效的策略；不投运时为空。</param>
/// <param name="Detail">读不到时的异常说明；其余为空。</param>
public sealed record VehicleChargingPolicyDecision(
    string VehicleKey,
    string Reason,
    EffectiveChargingPolicy? Effective,
    string? Detail)
{
    public bool Commissioned => Effective is not null;

    /// <summary>新决定要记下的策略版本号（旅程与充电周期上的 <c>ChargingPolicyVersion</c> 列）。</summary>
    public long? PolicyVersion => Effective?.Policy.Version;
}

/// <summary>
/// 按周期冻结的两个读法（<c>REQ-0282</c>）：此刻为这辆车做新决定用哪一版；按记下的版本号读回快照。
/// </summary>
/// <remarks>
/// 新派车判断、空闲返回资格与新充电周期都取 <see cref="ResolveForNewDecisionAsync"/>，把 <see cref="VehicleChargingPolicyDecision.PolicyVersion"/>
/// 记在自己的行上；既有任务、排队、预占、建单与充电周期一律按记下的号调 <see cref="ReadFrozenAsync"/>，激活新版本不改它们读到的内容。
/// 充电分配（批次9-06）用的也是这一个判定，不另写。
/// </remarks>
public interface IChargingPolicyResolver
{
    /// <summary>
    /// 此刻为这辆车做新决定用哪一版。没有已批准、已激活、覆盖它的版本时不投运；读库出错按不投运（fail-closed），不抛出。
    /// </summary>
    Task<VehicleChargingPolicyDecision> ResolveForNewDecisionAsync(string vehicleKey, CancellationToken cancellationToken);

    /// <summary>按版本号读回冻结的内容。版本不存在时抛 <see cref="InvalidOperationException"/>：记下的号必是写过的号。</summary>
    Task<ChargingPolicyVersion> ReadFrozenAsync(long version, CancellationToken cancellationToken);
}

/// <summary><see cref="IChargingPolicyResolver"/> 经 <see cref="IChargingPolicyStore"/> 的实现。</summary>
public sealed class ChargingPolicyResolver(IChargingPolicyStore store) : IChargingPolicyResolver
{
    private readonly IChargingPolicyStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public async Task<VehicleChargingPolicyDecision> ResolveForNewDecisionAsync(
        string vehicleKey, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vehicleKey);
        try
        {
            EffectiveChargingPolicy? effective =
                await _store.ReadEffectiveForVehicleAsync(vehicleKey, cancellationToken).ConfigureAwait(false);
            return effective is null
                ? new VehicleChargingPolicyDecision(vehicleKey, ChargingPolicyCommissioningReasons.NotApproved, null, null)
                : new VehicleChargingPolicyDecision(vehicleKey, ChargingPolicyCommissioningReasons.Effective, effective, null);
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            // Fail-closed: a vehicle whose policy cannot be read is a vehicle without one (specification 8.6).
            return new VehicleChargingPolicyDecision(
                vehicleKey, ChargingPolicyCommissioningReasons.Unreadable, null, failure.GetBaseException().Message);
        }
    }

    public async Task<ChargingPolicyVersion> ReadFrozenAsync(long version, CancellationToken cancellationToken) =>
        await _store.ReadVersionAsync(version, cancellationToken).ConfigureAwait(false)
        ?? throw new InvalidOperationException(string.Create(
            CultureInfo.InvariantCulture, $"Charging policy version {version} was frozen on a record but does not exist."));
}
