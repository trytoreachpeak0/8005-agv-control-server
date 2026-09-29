using System.Globalization;
using ControlServer.Application;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Storage;

namespace ControlServer.FieldOps;

/// <summary>
/// 充电策略版本的 FieldOps 动词（control-server#400，批次9-02；REQ-0281、REQ-0282）：导入、批准、激活、只读查看。
/// </summary>
/// <remarks>
/// <para>
/// <b>三个写动词，各留各的痕。</b>导入只写一个版本；批准是另一个动词，写批准人、角色、依据与来源；激活又是一个动词，只收已批准的版本。
/// 一次普通导入顺带不了批准与激活。
/// </para>
/// <para>
/// <b>策略按「全局一个生效版本」存</b>（最近一次激活）。激活一版只覆盖部分车的策略，范围外的车立刻不承接新用途——导入与激活的输出都列出
/// <c>vehiclesLosingPolicy</c>，照着 <c>--fleet</c> 算。
/// </para>
/// <para>
/// <b>测试批准不冒充现场批准。</b>来源为 <c>TEST_FIXTURE</c>、<c>L2_PRESET</c> 的批准照写，但只有这两种批准的版本，激活要显式带
/// <c>--allow-non-field-approval</c>；现场说明里没有这个开关。
/// </para>
/// </remarks>
internal static partial class Program
{
    private const string ImportChargingPolicyCommand = "import-charging-policy";

    private const string ApproveChargingPolicyCommand = "approve-charging-policy";

    private const string ActivateChargingPolicyCommand = "activate-charging-policy";

    private const string ReadChargingPolicyCommand = "charging-policy";

    private static async Task<int> ImportChargingPolicyAsync(
        ControlServerDbContext context,
        GovernanceStore governance,
        Dictionary<string, string> options,
        DateTimeOffset now)
    {
        const string usage = " needs --input <charging-policy.json> --fleet <VehicleKey;VehicleKey...>";
        if (!options.TryGetValue("input", out string? inputPath) || !TryReadFleet(options, out string[] fleet, out string? problem))
        {
            return Usage(ImportChargingPolicyCommand + usage);
        }
        if (problem is not null)
        {
            return Usage(problem);
        }
        if (!File.Exists(inputPath))
        {
            return Usage($"input file not found: {inputPath}");
        }
        bool dryRun = options.ContainsKey("dry-run");

        ChargingPolicyGovernanceService service = PolicyService(context, governance);
        string text = await File.ReadAllTextAsync(inputPath);
        ChargingPolicyImportResult result;
        try
        {
            if (dryRun)
            {
                result = await service.ImportAsync(text, fleet, dryRun: true, now, CancellationToken.None);
            }
            else
            {
                await using IDbContextTransaction transaction = await context.Database.BeginTransactionAsync(CancellationToken.None);
                result = await service.ImportAsync(text, fleet, dryRun: false, now, CancellationToken.None);
                if (result.Outcome == ChargingPolicyOperationOutcome.Accepted)
                {
                    await transaction.CommitAsync(CancellationToken.None);
                }
            }
        }
        catch (Exception conflict) when (IsVersionNumberAlreadyTaken(conflict))
        {
            return Conflict(ImportChargingPolicyCommand, conflict);
        }

        return Emit(
            new
            {
                command = ImportChargingPolicyCommand,
                outcome = Outcome(result.Outcome),
                dryRun,
                input = Path.GetFullPath(inputPath),
                fleet,
                previousVersion = result.PreviousVersion,
                version = result.Version?.Version,
                contentSha256 = result.Version?.ContentSha256,
                snapshotId = result.Version?.SnapshotId,
                content = PolicyContent(result.Content),
                errorCount = result.Errors.Count,
                errors = Violations(result.Errors),
                impact = Impact(result.Impact),
                next = result.Outcome == ChargingPolicyOperationOutcome.Rejected || dryRun
                    ? null
                    : "The version is written but neither approved nor active: run approve-charging-policy, then activate-charging-policy."
            },
            result.Outcome == ChargingPolicyOperationOutcome.Rejected ? 1 : 0);
    }

    private static async Task<int> ApproveChargingPolicyAsync(
        ControlServerDbContext context,
        GovernanceStore governance,
        Dictionary<string, string> options,
        DateTimeOffset now)
    {
        const string usage =
            " needs --version <n> --approved-by <person> --role <role> --basis <evidence reference> --source <FIELD|TEST_FIXTURE|L2_PRESET>";
        if (!TryReadVersion(options, out long version)
            || !TryReadNonBlank(options, "approved-by", out string? approvedBy)
            || !TryReadNonBlank(options, "role", out string? role)
            || !TryReadNonBlank(options, "basis", out string? basis)
            || !TryReadNonBlank(options, "source", out string? source))
        {
            return Usage(ApproveChargingPolicyCommand + usage);
        }

        ChargingPolicyGovernanceService service = PolicyService(context, governance);
        await using IDbContextTransaction transaction = await context.Database.BeginTransactionAsync(CancellationToken.None);
        ChargingPolicyApprovalResult result = await service.ApproveAsync(
            version, approvedBy!, role!, basis!, source!, now, CancellationToken.None);
        if (result.Outcome == ChargingPolicyOperationOutcome.Accepted)
        {
            await transaction.CommitAsync(CancellationToken.None);
        }
        return Emit(
            new
            {
                command = ApproveChargingPolicyCommand,
                outcome = Outcome(result.Outcome),
                version,
                approvalId = result.Approval?.ApprovalId,
                approvedBy = result.Approval?.ApprovedBy,
                approverRole = result.Approval?.ApproverRole,
                approvedAt = result.Approval?.ApprovedAt,
                basisReference = result.Approval?.BasisReference,
                source = result.Approval?.Source,
                errorCount = result.Errors.Count,
                errors = Violations(result.Errors),
                note = "--role is recorded as given and is not verified: there is no personnel authentication."
            },
            result.Outcome == ChargingPolicyOperationOutcome.Rejected ? 1 : 0);
    }

    private static async Task<int> ActivateChargingPolicyAsync(
        ControlServerDbContext context,
        GovernanceStore governance,
        Dictionary<string, string> options,
        DateTimeOffset now)
    {
        const string usage =
            " needs --version <n> --activated-by <person> --fleet <VehicleKey;VehicleKey...> [--allow-non-field-approval] [--dry-run]";
        if (!TryReadVersion(options, out long version)
            || !TryReadNonBlank(options, "activated-by", out string? activatedBy)
            || !TryReadFleet(options, out string[] fleet, out string? problem))
        {
            return Usage(ActivateChargingPolicyCommand + usage);
        }
        if (problem is not null)
        {
            return Usage(problem);
        }
        bool dryRun = options.ContainsKey("dry-run");
        bool allowNonField = options.ContainsKey("allow-non-field-approval");

        ChargingPolicyGovernanceService service = PolicyService(context, governance);
        ChargingPolicyActivationResult result;
        try
        {
            if (dryRun)
            {
                result = await service.ActivateAsync(version, activatedBy!, fleet, allowNonField, dryRun: true, now, CancellationToken.None);
            }
            else
            {
                await using IDbContextTransaction transaction = await context.Database.BeginTransactionAsync(CancellationToken.None);
                result = await service.ActivateAsync(version, activatedBy!, fleet, allowNonField, dryRun: false, now, CancellationToken.None);
                if (result.Outcome == ChargingPolicyOperationOutcome.Accepted)
                {
                    await transaction.CommitAsync(CancellationToken.None);
                }
            }
        }
        catch (Exception conflict) when (IsVersionNumberAlreadyTaken(conflict))
        {
            return Conflict(ActivateChargingPolicyCommand, conflict);
        }

        return Emit(
            new
            {
                command = ActivateChargingPolicyCommand,
                outcome = Outcome(result.Outcome),
                dryRun,
                version,
                previousActiveVersion = result.PreviousActiveVersion,
                activationId = result.Activation?.ActivationId,
                sequence = result.Activation?.Sequence,
                activatedAt = result.Activation?.ActivatedAt,
                allowNonFieldApproval = allowNonField,
                approvals = result.Approvals.Select(Approval),
                errorCount = result.Errors.Count,
                errors = Violations(result.Errors),
                impact = Impact(result.Impact)
            },
            result.Outcome == ChargingPolicyOperationOutcome.Rejected ? 1 : 0);
    }

    /// <summary>
    /// 当前生效的版本、全部版本的批准与激活历史；给了 <c>--version</c> 时附上那一版的内容；给了 <c>--fleet</c> 时附上每辆车投不投运。
    /// <b>只读</b>，库以只读模式开。一版都没激活也是 <c>OK</c>：那时每辆车都不投运。
    /// </summary>
    private static async Task<int> ReadChargingPolicyAsync(
        ControlServerDbContext context,
        GovernanceStore governance,
        Dictionary<string, string> options)
    {
        long? requested = null;
        if (options.TryGetValue("version", out string? versionText))
        {
            if (!TryReadVersion(options, out long parsed))
            {
                return Usage($"--version must be a whole number, not '{versionText}'");
            }
            requested = parsed;
        }
        string[] fleet = [];
        if (options.ContainsKey("fleet") && (!TryReadFleet(options, out fleet, out string? problem) || problem is not null))
        {
            return Usage(problem ?? "--fleet lists VehicleKeys separated by ';'");
        }

        ChargingPolicyStore store = new(context, new GovernedConfigurationPublisher(governance, governance));
        ChargingGovernanceFacts facts = Facts(context, governance);
        ChargingPolicyGovernanceService service = new(store, facts, governance);
        ChargingPolicyActiveState? active = await service.ReadActiveAsync(CancellationToken.None);
        ChargingPolicyVersion? version = requested is long number ? await store.ReadVersionAsync(number, CancellationToken.None) : null;
        if (requested is not null && version is null)
        {
            return Emit(
                new { command = ReadChargingPolicyCommand, outcome = "NOT_FOUND", requestedVersion = requested, detail = "That version does not exist." },
                1);
        }
        long latest = await facts.ReadLatestVersionNumberAsync(CancellationToken.None) ?? 0;
        List<object> history = [];
        for (long each = 1; each <= latest; each++)
        {
            ChargingPolicyVersion? written = await store.ReadVersionAsync(each, CancellationToken.None);
            if (written is null)
            {
                continue;
            }
            history.Add(new
            {
                version = written.Version,
                writtenAt = written.WrittenAt,
                changeNote = written.ChangeNote,
                contentSha256 = written.ContentSha256,
                approvals = (await store.ListApprovalsAsync(each, CancellationToken.None)).Select(Approval)
            });
        }
        ChargingPolicyResolver resolver = new(store);
        List<object> vehicles = [];
        foreach (string vehicleKey in fleet)
        {
            VehicleChargingPolicyDecision decision = await resolver.ResolveForNewDecisionAsync(vehicleKey, CancellationToken.None);
            vehicles.Add(new { vehicleKey, commissioned = decision.Commissioned, reason = decision.Reason, policyVersion = decision.PolicyVersion });
        }
        return Emit(
            new
            {
                command = ReadChargingPolicyCommand,
                outcome = "OK",
                activeVersion = active?.Policy.Version,
                activation = active is null
                    ? null
                    : new
                    {
                        activationId = active.Activation.ActivationId,
                        sequence = active.Activation.Sequence,
                        activatedAt = active.Activation.ActivatedAt,
                        activatedBy = active.Activation.ActivatedBy
                    },
                active = PolicyContent(active?.Policy.Content),
                requestedVersion = requested,
                requested = PolicyContent(version?.Content),
                history,
                activations = (await facts.ListActivationsAsync(CancellationToken.None)).Select(activation => new
                {
                    activationId = activation.ActivationId,
                    sequence = activation.Sequence,
                    version = activation.Version,
                    activatedAt = activation.ActivatedAt,
                    activatedBy = activation.ActivatedBy
                }),
                vehicles,
                openCycles = (await facts.ListOpenCyclesAsync(CancellationToken.None)).Select(Cycle)
            },
            0);
    }

    private static ChargingPolicyGovernanceService PolicyService(ControlServerDbContext context, GovernanceStore governance) =>
        new(new ChargingPolicyStore(context, new GovernedConfigurationPublisher(governance, governance)), Facts(context, governance), governance);

    /// <summary>缺 <c>--fleet</c> 时返回假；写法不对时返回真并给出 <paramref name="problem"/>。</summary>
    private static bool TryReadFleet(Dictionary<string, string> options, out string[] fleet, out string? problem)
    {
        fleet = [];
        problem = null;
        if (!options.TryGetValue("fleet", out string? fleetText))
        {
            return false;
        }
        fleet = fleetText.Split(';');
        if (fleet.Any(key => key.Length == 0 || !string.Equals(key, key.Trim(), StringComparison.Ordinal))
            || fleet.Distinct(StringComparer.Ordinal).Count() != fleet.Length)
        {
            problem = $"--fleet lists each VehicleKey once, separated by ';', not '{fleetText}'";
        }
        return true;
    }

    private static bool TryReadVersion(Dictionary<string, string> options, out long version)
    {
        version = 0;
        return options.TryGetValue("version", out string? text)
            && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out version)
            && version > 0;
    }

    private static bool TryReadNonBlank(Dictionary<string, string> options, string name, out string? value) =>
        options.TryGetValue(name, out value) && !string.IsNullOrWhiteSpace(value);

    private static string Outcome(ChargingPolicyOperationOutcome outcome) => outcome switch
    {
        ChargingPolicyOperationOutcome.Accepted => "OK",
        ChargingPolicyOperationOutcome.Unchanged => "UNCHANGED",
        _ => "REJECTED"
    };

    private static int Conflict(string command, Exception conflict) =>
        Emit(
            new
            {
                command,
                outcome = "CONFLICT",
                detail = "Another write committed first and this one was rolled back whole. Read the current state and run it again if it is still wanted.",
                cause = conflict.GetBaseException().Message
            },
            1);

    private static IEnumerable<object> Violations(IReadOnlyList<ChargingPolicyViolation> violations) =>
        violations.Select(violation => new { reasonCode = violation.ReasonCode, field = violation.Field, detail = violation.Detail });

    private static object Approval(ChargingPolicyApproval approval) =>
        new
        {
            approvalId = approval.ApprovalId,
            approvedBy = approval.ApprovedBy,
            approverRole = approval.ApproverRole,
            approvedAt = approval.ApprovedAt,
            basisReference = approval.BasisReference,
            source = approval.Source
        };

    private static object? PolicyContent(ChargingPolicyContent? content) =>
        content is null
            ? null
            : new
            {
                minimumPostTaskBatteryMarginPercent = content.MinimumPostTaskBatteryMarginPercent,
                mandatoryChargeEntryThresholdPercent = content.MandatoryChargeEntryThresholdPercent,
                chargingCompletionThresholdPercent = content.ChargingCompletionThresholdPercent,
                estimatedTaskConsumptionPercent = content.EstimatedTaskConsumptionPercent,
                progressStabilizationSeconds = content.ProgressStabilizationSeconds,
                progressObservationWindowSeconds = content.ProgressObservationWindowSeconds,
                progressMinimumIncreasePercent = content.ProgressMinimumIncreasePercent,
                vehicleScope = content.VehicleScope
            };

    private static object? Impact(ChargingPolicyImpact? impact) =>
        impact is null
            ? null
            : new
            {
                comparedWithActiveVersion = impact.ComparedWith,
                changes = impact.Changes.Select(change => new { field = change.Field, before = change.Before, after = change.After }),
                coveredAfter = impact.CoveredAfter,
                vehiclesLosingPolicy = impact.VehiclesLosingPolicy,
                vehiclesWithoutPolicyAfter = impact.VehiclesWithoutPolicyAfter,
                warning = impact.VehiclesWithoutPolicyAfter.Count == 0
                    ? null
                    : "Once this version is active, the vehicles under vehiclesWithoutPolicyAfter take no new work (REQ-0282).",
                openCycles = impact.OpenCycles.Select(Cycle),
                retention = ChargingPolicyImpact.Retention
            };
}
