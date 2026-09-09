using System.Globalization;
using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Infrastructure.Persistence;

/// <summary>
/// 整车仓位配置的原子激活下发与结果补报，服务端半边（REQ-0264、REQ-0265、REQ-0316）。
/// </summary>
/// <remarks>
/// <para>
/// **本类不实现协议 v2 的传输与序列化。**消息 7／8 与 <c>CapabilitySnapshot</c> 的指纹字段属批次 2
/// 轨 A，尚未落地。这里固化的是业务语义：原子性、待补报态、补报收敛、指纹比对规则。轨 A 到位后，
/// 传输层调用的就是这几个方法，语义不需要再议一遍。
/// </para>
/// <para>
/// **结果没回来时服务端在等，不在猜。**<see cref="SlotConfigurationActivationState.PendingResult"/>
/// 是一个明确的态：既不记成功也不记失败。补报到达时
/// <see cref="RecordResultAsync"/> 收敛它，而且是幂等的——同一次激活补报两次不会产生第二次激活。
/// </para>
/// <para>
/// **一次激活动作已经包含重新投运意图（REQ-0265）。**这个类里没有第二道人工审批关卡，也没有一个
/// 「已确认」字段等着谁去填：发起激活就是那次决定本身。有架构测试守着这一条。
/// </para>
/// <para>本类不新增任何表。</para>
/// </remarks>
public sealed class SlotConfigurationActivationCoordinator(
    ControlServerDbContext context,
    GovernedConfigurationPublisher publisher,
    IGovernanceAuditWriter auditWriter)
{
    private const string PublishedStatus = "PUBLISHED";

    private readonly ControlServerDbContext _context =
        context ?? throw new ArgumentNullException(nameof(context));
    private readonly GovernedConfigurationPublisher _publisher =
        publisher ?? throw new ArgumentNullException(nameof(publisher));
    private readonly IGovernanceAuditWriter _auditWriter =
        auditWriter ?? throw new ArgumentNullException(nameof(auditWriter));

    /// <summary>
    /// 下发一次整车配置激活。对象必须是已发布、且这台车每个仓位的 IO 绑定都齐的配置。
    /// </summary>
    /// <remarks>
    /// 这里查的是「有没有一份完整的东西可以整份换上去」，不是那台车的仓位配置就绪门禁——后者还要
    /// 逐仓核验，是另一件事。产出的激活记录一落地就处在待补报态。
    /// </remarks>
    public async Task<SlotConfigurationActivationRow> IssueActivationAsync(
        string agvId,
        string slotModelVersionId,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agvId);
        ArgumentException.ThrowIfNullOrWhiteSpace(slotModelVersionId);

        await RequireCompletePublishedTargetAsync(agvId, slotModelVersionId, cancellationToken);

        string objectId = FormattableString.Invariant($"{agvId}:{slotModelVersionId}");
        long version = await NextVersionAsync(objectId, cancellationToken);
        SlotIoBindingRow[] bindings = await LatestPublishedBindingsAsync(agvId, slotModelVersionId, cancellationToken);
        GovernedConfigurationSnapshot snapshot = await _publisher.PublishVersionAsync(
            GovernedObjectKind.ActiveSlotConfiguration,
            objectId,
            version,
            JsonSerializer.Serialize(bindings
                .OrderBy(binding => binding.PhysicalSlotNumber)
                .Select(binding => new
                {
                    binding.PhysicalSlotNumber,
                    binding.UnlockOutputPoint,
                    binding.LockFeedbackInputPoint,
                    binding.LightCurtainInputPoint,
                    binding.SignalPolarity,
                    binding.PulseResetMilliseconds
                })),
            "SLOT_CONFIGURATION_ACTIVATION_ISSUED",
            occurredAt,
            cancellationToken);

        SlotConfigurationActivationRow activation = new()
        {
            ActivationId = NewId(),
            AgvId = agvId,
            SlotModelVersionId = slotModelVersionId,
            ConfigurationVersion = version,
            Fingerprint = snapshot.ContentSha256,
            Kind = "ACTIVATION",
            State = SlotConfigurationActivationState.PendingResult,
            RecoveryRole = SlotConfigurationActivationDelivery.RecoveryRole,
            IssuedAt = occurredAt,
            SnapshotId = snapshot.SnapshotId
        };
        _context.Set<SlotConfigurationActivationRow>().Add(activation);
        await _context.SaveChangesAsync(cancellationToken);
        return activation;
    }

    /// <summary>
    /// 这台车此刻有哪些激活还没拿到结果——重连时按 <c>SLOT_CONFIGURATION</c> 这个恢复角色补报的就是
    /// 它们。
    /// </summary>
    public async Task<IReadOnlyList<SlotConfigurationActivationRow>> ListPendingResultReplayAsync(
        string agvId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agvId);

        SlotConfigurationActivationRow[] pending = await _context.Set<SlotConfigurationActivationRow>()
            .AsNoTracking()
            .Where(row => row.AgvId == agvId
                && row.State == SlotConfigurationActivationState.PendingResult
                && row.RecoveryRole == SlotConfigurationActivationDelivery.RecoveryRole)
            .ToArrayAsync(cancellationToken);

        // SQLite 不能对 DateTimeOffset 做 ORDER BY，所以顺序在内存里排。待补报的激活数量以「这台车
        // 手上还没结的那几次」为上限，不是一张会长大的表。
        return [.. pending.OrderBy(row => row.IssuedAt)];
    }

    /// <summary>
    /// 收下一次补报，把那次激活收敛掉。
    /// </summary>
    /// <remarks>
    /// **幂等。**同一个 <c>activationId</c> 补报两次，第二次原样返回已经收敛的那一行，不产生第二次
    /// 激活、不改写第一次的结论。断线重连会让同一份结果到达不止一次，那是补报机制正常工作的样子，
    /// 不是异常。
    /// </remarks>
    public async Task<SlotConfigurationActivationRow> RecordResultAsync(
        ActivationResultReport report,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentException.ThrowIfNullOrWhiteSpace(report.ActivationId);

        SlotConfigurationActivationRow activation = await _context.Set<SlotConfigurationActivationRow>()
            .FirstOrDefaultAsync(row => row.ActivationId == report.ActivationId, cancellationToken)
            ?? throw new InvalidOperationException(
                FormattableString.Invariant($"There is no activation {report.ActivationId} to report against."));

        if (!string.Equals(
            activation.State, SlotConfigurationActivationState.PendingResult, StringComparison.Ordinal))
        {
            return activation;
        }

        activation.State = report.Succeeded
            ? SlotConfigurationActivationState.Activated
            : SlotConfigurationActivationState.Failed;
        activation.ResultReceivedAt = report.ReportedAt;
        activation.ResultJson = JsonSerializer.Serialize(new { report.Succeeded, report.ReasonCode });

        if (report.Succeeded)
        {
            ActiveSlotConfigurationRow? active = await _context.Set<ActiveSlotConfigurationRow>()
                .FirstOrDefaultAsync(row => row.AgvId == activation.AgvId, cancellationToken);
            if (active is null)
            {
                _context.Set<ActiveSlotConfigurationRow>().Add(new ActiveSlotConfigurationRow
                {
                    AgvId = activation.AgvId,
                    SlotModelVersionId = activation.SlotModelVersionId,
                    ConfigurationVersion = activation.ConfigurationVersion,
                    Fingerprint = activation.Fingerprint,
                    ActivatedAt = report.ReportedAt,
                    ActivationId = activation.ActivationId,
                    SnapshotId = activation.SnapshotId!
                });
            }
            else
            {
                active.SlotModelVersionId = activation.SlotModelVersionId;
                active.ConfigurationVersion = activation.ConfigurationVersion;
                active.Fingerprint = activation.Fingerprint;
                active.ActivatedAt = report.ReportedAt;
                active.ActivationId = activation.ActivationId;
                active.SnapshotId = activation.SnapshotId!;
            }
        }
        await _context.SaveChangesAsync(cancellationToken);

        await _auditWriter.WriteBusinessAsync(
            new GovernanceAuditEntry(
                "SLOT_CONFIGURATION_ACTIVATION_RESULT_RECORDED",
                GovernedObjectKind.ActiveSlotConfiguration,
                activation.AgvId,
                activation.ConfigurationVersion,
                report.Succeeded ? GovernanceActionOutcome.Succeeded : GovernanceActionOutcome.Failed,
                JsonSerializer.Serialize(new
                {
                    activationId = activation.ActivationId,
                    deliveryClass = SlotConfigurationActivationDelivery.DeliveryClass,
                    recoveryRole = activation.RecoveryRole,
                    reasonCode = report.ReasonCode
                }),
                activation.SnapshotId),
            report.ReportedAt,
            cancellationToken);
        return activation;
    }

    /// <summary>
    /// 车重连回来，报了一个 <c>activeSlotConfigurationFingerprint</c>：归档前的那份配置还能不能当恢复
    /// 候选（REQ-0316）。
    /// </summary>
    /// <remarks>
    /// 指纹一致才认。不一致时这份候选不成立——不是谁覆盖谁，是重新走一次激活。
    /// </remarks>
    public async Task<RecoveryCandidateVerdict> EvaluateRecoveryCandidateAsync(
        string agvId,
        string? reportedFingerprint,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agvId);

        AgvArchiveRow? archive = await _context.Set<AgvArchiveRow>().AsNoTracking()
            .FirstOrDefaultAsync(row => row.AgvId == agvId, cancellationToken);
        string? candidate = archive?.ArchivedActiveSlotConfigurationFingerprint;
        if (candidate is null)
        {
            return new RecoveryCandidateVerdict(
                agvId, false, RecoveryCandidateVerdict.NoCandidateCode, null, reportedFingerprint);
        }
        bool matches = string.Equals(candidate, reportedFingerprint, StringComparison.Ordinal);
        return new RecoveryCandidateVerdict(
            agvId,
            matches,
            matches ? RecoveryCandidateVerdict.AcceptedCode : RecoveryCandidateVerdict.FingerprintMismatchCode,
            candidate,
            reportedFingerprint);
    }

    private async Task RequireCompletePublishedTargetAsync(
        string agvId,
        string slotModelVersionId,
        CancellationToken cancellationToken)
    {
        SlotModelVersionRow? model = await _context.Set<SlotModelVersionRow>().AsNoTracking()
            .FirstOrDefaultAsync(row => row.SlotModelVersionId == slotModelVersionId, cancellationToken);
        if (model is null || !string.Equals(model.Status, PublishedStatus, StringComparison.Ordinal))
        {
            throw new ActivationTargetIncompleteException(
                FormattableString.Invariant(
                    $"Vehicle model version {slotModelVersionId} is not published; it cannot be activated on a vehicle."));
        }

        int[] modelSlots = await _context.Set<SlotModelSlotRow>().AsNoTracking()
            .Where(row => row.SlotModelVersionId == slotModelVersionId)
            .Select(row => row.PhysicalSlotNumber)
            .ToArrayAsync(cancellationToken);
        SlotIoBindingRow[] bindings =
            await LatestPublishedBindingsAsync(agvId, slotModelVersionId, cancellationToken);
        int[] missing = [.. modelSlots
            .Where(slot => !bindings.Any(binding => binding.PhysicalSlotNumber == slot))
            .Order()];
        if (missing.Length > 0)
        {
            throw new ActivationTargetIncompleteException(
                FormattableString.Invariant(
                    $"REQ-0264: {agvId} has no published IO binding for slot(s) {string.Join(", ", missing)}. ")
                + "A vehicle configuration is activated whole or not at all, so an incomplete one is refused "
                + "before it goes out, not after.");
        }
    }

    private async Task<SlotIoBindingRow[]> LatestPublishedBindingsAsync(
        string agvId,
        string slotModelVersionId,
        CancellationToken cancellationToken)
    {
        SlotIoBindingRow[] bindings = await _context.Set<SlotIoBindingRow>().AsNoTracking()
            .Where(row => row.AgvId == agvId
                && row.SlotModelVersionId == slotModelVersionId
                && row.Status == PublishedStatus)
            .ToArrayAsync(cancellationToken);
        if (bindings.Length == 0)
        {
            return bindings;
        }
        long latest = bindings.Max(row => row.Version);
        return [.. bindings.Where(row => row.Version == latest)];
    }

    private async Task<long> NextVersionAsync(string objectId, CancellationToken cancellationToken)
    {
        long[] versions = await _context.Set<GovernedConfigurationSnapshotRow>().AsNoTracking()
            .Where(row => row.ObjectKind == GovernedObjectKind.ActiveSlotConfiguration && row.ObjectId == objectId)
            .Select(row => row.Version)
            .ToArrayAsync(cancellationToken);
        return versions.Length == 0 ? 1 : versions.Max() + 1;
    }

    private static string NewId() => Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
}
