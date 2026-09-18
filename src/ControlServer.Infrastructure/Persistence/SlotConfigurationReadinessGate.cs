using System.Globalization;
using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Infrastructure.Persistence;

/// <summary>
/// 每车一份的仓位配置就绪门禁（REQ-0259、REQ-0262、REQ-0263）。
/// </summary>
/// <remarks>
/// <para>
/// **门禁是每车判定。**基线里两处「整车」说的是**一台车之内**不得有缺口：这台车的每一个仓位都要有
/// 完整并经实际核对的 <see cref="SlotIoBindingRow"/>，缺一个就不就绪。它对另外两台车什么都没说，
/// 所以三台现有车逐台推进，门禁在第一台核对通过之后再上线，零停产。
/// </para>
/// <para>
/// 判定是**现算的**：<see cref="EvaluateAsync"/> 只读事实，不写任何东西。
/// <see cref="SlotConfigurationReadinessRow"/> 存的是这份判定的最近一次结论，不是判定本身——所以
/// 门禁上线不会改变任何一台已核对通过车辆的状态，它只是把本来就成立的事实写了下来。
/// </para>
/// <para>本类不新增任何表：用的全是 #9 建好的那几张。</para>
/// </remarks>
public sealed class SlotConfigurationReadinessGate(
    ControlServerDbContext context,
    IGovernanceAuditWriter auditWriter)
{
    private readonly ControlServerDbContext _context =
        context ?? throw new ArgumentNullException(nameof(context));
    private readonly IGovernanceAuditWriter _auditWriter =
        auditWriter ?? throw new ArgumentNullException(nameof(auditWriter));

    /// <summary>
    /// 进入整车配置维护态。**只有服务端能开这道门**，而且要求服务端此刻确实握着这台车的会话
    /// （REQ-0262）。
    /// </summary>
    /// <remarks>
    /// 断线时服务端没有这台车的活跃会话，于是这个方法拒绝——车载端因此无法在断线时自行进入维护态：
    /// 它根本没有别的入口。进入维护态会让该车此前的就绪立即失效，因为维护本身就是要动硬件。
    /// </remarks>
    public async Task EnterWholeVehicleMaintenanceAsync(
        string agvId,
        string slotModelVersionId,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agvId);
        ArgumentException.ThrowIfNullOrWhiteSpace(slotModelVersionId);

        if (!await HasOnlineServerLinkAsync(agvId, cancellationToken))
        {
            throw new VehicleMaintenanceRequiresServerLinkException(
                FormattableString.Invariant($"REQ-0262: {agvId} has no ready session with this server, ")
                + "so whole-vehicle configuration maintenance cannot be entered. "
                + "The onboard side has no entry of its own.");
        }

        await InvalidateAsync(
            agvId,
            slotModelVersionId,
            SlotReadinessReasonCode.WholeVehicleMaintenance,
            "WHOLE_VEHICLE_MAINTENANCE_ENTERED",
            occurredAt,
            cancellationToken);
    }

    /// <summary>
    /// 记录一台车的逐仓核验（REQ-0263）。**抽样被拒**：交上来的仓位集合必须与该模型的仓位集合逐一
    /// 对上，少一个就整批拒绝。
    /// </summary>
    public async Task RecordVerificationAsync(
        string agvId,
        string slotModelVersionId,
        IReadOnlyList<SlotVerificationConfirmation> confirmations,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agvId);
        ArgumentException.ThrowIfNullOrWhiteSpace(slotModelVersionId);
        ArgumentNullException.ThrowIfNull(confirmations);

        int[] modelSlots = await ModelSlotNumbersAsync(slotModelVersionId, cancellationToken);
        int[] confirmed =
            [.. confirmations.Select(confirmation => confirmation.PhysicalSlotNumber).Distinct().Order()];
        if (!modelSlots.SequenceEqual(confirmed))
        {
            throw new SampledVerificationRejectedException(
                FormattableString.Invariant(
                    $"REQ-0263: {agvId} has {modelSlots.Length} slots on model version {slotModelVersionId}; ")
                + FormattableString.Invariant($"{confirmed.Length} were confirmed. ")
                + "Every slot on the vehicle is confirmed one by one, or none of it counts.");
        }

        foreach (SlotVerificationConfirmation confirmation in confirmations)
        {
            SlotConfigurationVerificationRow? existing = await _context.Set<SlotConfigurationVerificationRow>()
                .FirstOrDefaultAsync(
                    row => row.AgvId == agvId
                        && row.SlotModelVersionId == slotModelVersionId
                        && row.PhysicalSlotNumber == confirmation.PhysicalSlotNumber,
                    cancellationToken);
            if (existing is null)
            {
                _context.Set<SlotConfigurationVerificationRow>().Add(new SlotConfigurationVerificationRow
                {
                    VerificationId = NewId(),
                    AgvId = agvId,
                    SlotModelVersionId = slotModelVersionId,
                    PhysicalSlotNumber = confirmation.PhysicalSlotNumber,
                    OpenSignalConfirmed = confirmation.OpenSignalConfirmed,
                    CloseSignalConfirmed = confirmation.CloseSignalConfirmed,
                    InPlaceSignalConfirmed = confirmation.InPlaceSignalConfirmed,
                    VerifiedAt = occurredAt,
                    FieldRecordReference = confirmation.FieldRecordReference
                });
            }
            else
            {
                existing.OpenSignalConfirmed = confirmation.OpenSignalConfirmed;
                existing.CloseSignalConfirmed = confirmation.CloseSignalConfirmed;
                existing.InPlaceSignalConfirmed = confirmation.InPlaceSignalConfirmed;
                existing.VerifiedAt = occurredAt;
                existing.FieldRecordReference = confirmation.FieldRecordReference;
            }
        }
        await _context.SaveChangesAsync(cancellationToken);

        await WriteAuditAsync(
            "SLOT_CONFIGURATION_VERIFICATION_RECORDED",
            agvId,
            new
            {
                slotModelVersionId,
                slots = modelSlots,
                negative = confirmations.Where(c => !c.IsComplete).Select(c => c.PhysicalSlotNumber).Order()
            },
            occurredAt,
            cancellationToken);
        await RefreshReadinessAsync(agvId, slotModelVersionId, occurredAt, cancellationToken);
    }

    /// <summary>
    /// 任何硬件相关变化都让这台车重新核验（REQ-0263）：此前的核验记录作废，就绪随之失效。
    /// </summary>
    public Task InvalidateForHardwareChangeAsync(
        string agvId,
        string slotModelVersionId,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken) =>
        InvalidateAsync(
            agvId,
            slotModelVersionId,
            SlotReadinessReasonCode.HardwareChanged,
            "SLOT_CONFIGURATION_REVERIFICATION_REQUIRED",
            occurredAt,
            cancellationToken);

    /// <summary>
    /// 现算一台车的就绪判定。只读——它不改变任何一台车的任何状态。
    /// </summary>
    public async Task<SlotConfigurationReadinessVerdict> EvaluateAsync(
        string agvId,
        string slotModelVersionId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agvId);
        ArgumentException.ThrowIfNullOrWhiteSpace(slotModelVersionId);

        int[] modelSlots = await ModelSlotNumbersAsync(slotModelVersionId, cancellationToken);

        SlotIoBindingRow[] bindings = await VehicleSlotModelResolver.ReadLatestPublishedBindingsAsync(
            _context, agvId, slotModelVersionId, cancellationToken);
        HashSet<int> boundSlots = [.. bindings.Select(row => row.PhysicalSlotNumber)];

        SlotConfigurationVerificationRow[] verifications = await _context.Set<SlotConfigurationVerificationRow>()
            .AsNoTracking()
            .Where(row => row.AgvId == agvId && row.SlotModelVersionId == slotModelVersionId)
            .ToArrayAsync(cancellationToken);
        Dictionary<int, SlotConfigurationVerificationRow> verifiedBySlot =
            verifications.ToDictionary(row => row.PhysicalSlotNumber);

        int[] missingBinding = [.. modelSlots.Where(slot => !boundSlots.Contains(slot))];
        int[] missingVerification = [.. modelSlots.Where(slot => !verifiedBySlot.ContainsKey(slot))];
        int[] negative = [.. modelSlots
            .Where(slot => verifiedBySlot.TryGetValue(slot, out SlotConfigurationVerificationRow? row)
                && !(row.OpenSignalConfirmed && row.CloseSignalConfirmed && row.InPlaceSignalConfirmed))];

        SlotConfigurationReadinessRow? persisted = await _context.Set<SlotConfigurationReadinessRow>()
            .AsNoTracking()
            .FirstOrDefaultAsync(row => row.AgvId == agvId, cancellationToken);
        bool invalidated = persisted is not null
            && string.Equals(persisted.SlotModelVersionId, slotModelVersionId, StringComparison.Ordinal)
            && persisted.InvalidatedAt is not null;

        string reasonCode = true switch
        {
            // 进过维护态或发生过硬件变化的车，在**重新逐仓核验完成之前**一直挂着那个原因码。核验补齐
            // 之后这一分支自然失效——「重新核验」是它唯一的出口，没有别的办法把它抹掉。
            _ when invalidated && missingVerification.Length > 0 => persisted!.ReasonCode,
            _ when missingBinding.Length > 0 => SlotReadinessReasonCode.IoBindingIncomplete,
            _ when missingVerification.Length == modelSlots.Length => SlotReadinessReasonCode.NeverVerified,
            _ when missingVerification.Length > 0 => SlotReadinessReasonCode.VerificationIncomplete,
            _ when negative.Length > 0 => SlotReadinessReasonCode.VerificationNegative,
            _ => SlotReadinessReasonCode.Ready
        };
        return new SlotConfigurationReadinessVerdict(
            agvId,
            slotModelVersionId,
            string.Equals(reasonCode, SlotReadinessReasonCode.Ready, StringComparison.Ordinal),
            reasonCode,
            missingBinding,
            missingVerification,
            negative);
    }

    /// <summary>把现算的判定写进读模型。判定本身不因为写不写而改变。</summary>
    public async Task<SlotConfigurationReadinessVerdict> RefreshReadinessAsync(
        string agvId,
        string slotModelVersionId,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        SlotConfigurationReadinessVerdict verdict =
            await EvaluateAsync(agvId, slotModelVersionId, cancellationToken);
        SlotConfigurationReadinessRow? row = await _context.Set<SlotConfigurationReadinessRow>()
            .FirstOrDefaultAsync(existing => existing.AgvId == agvId, cancellationToken);
        if (row is null)
        {
            _context.Set<SlotConfigurationReadinessRow>().Add(new SlotConfigurationReadinessRow
            {
                AgvId = agvId,
                SlotModelVersionId = slotModelVersionId,
                Ready = verdict.Ready,
                ReasonCode = verdict.ReasonCode,
                UpdatedAt = occurredAt
            });
        }
        else
        {
            row.SlotModelVersionId = slotModelVersionId;
            row.Ready = verdict.Ready;
            row.ReasonCode = verdict.ReasonCode;
            row.UpdatedAt = occurredAt;
            if (verdict.Ready)
            {
                row.InvalidatedAt = null;
            }
        }
        await _context.SaveChangesAsync(cancellationToken);
        return verdict;
    }

    /// <summary>
    /// 这台车的仓位配置就绪能不能作为 <see cref="AgvGenerationEvidence"/> 的一项正面证据。
    /// </summary>
    /// <remarks>
    /// 返回值直接喂给 <see cref="AgvGenerationEvidence.SlotConfigurationReadinessConfirmed"/>：不就绪
    /// 时它是 <c>false</c>，于是 <see cref="BusinessAvailability"/> 的 fail-closed 谓词链把这台车挡在
    /// 业务就绪之外——挡的只有这一台。
    /// </remarks>
    public async Task<bool> IsSlotConfigurationConfirmedAsync(
        string agvId,
        string slotModelVersionId,
        CancellationToken cancellationToken) =>
        (await EvaluateAsync(agvId, slotModelVersionId, cancellationToken)).Ready;

    private async Task InvalidateAsync(
        string agvId,
        string slotModelVersionId,
        string reasonCode,
        string action,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        SlotConfigurationVerificationRow[] stale = await _context.Set<SlotConfigurationVerificationRow>()
            .Where(row => row.AgvId == agvId && row.SlotModelVersionId == slotModelVersionId)
            .ToArrayAsync(cancellationToken);
        _context.Set<SlotConfigurationVerificationRow>().RemoveRange(stale);

        SlotConfigurationReadinessRow? row = await _context.Set<SlotConfigurationReadinessRow>()
            .FirstOrDefaultAsync(existing => existing.AgvId == agvId, cancellationToken);
        if (row is null)
        {
            _context.Set<SlotConfigurationReadinessRow>().Add(new SlotConfigurationReadinessRow
            {
                AgvId = agvId,
                SlotModelVersionId = slotModelVersionId,
                Ready = false,
                ReasonCode = reasonCode,
                UpdatedAt = occurredAt,
                InvalidatedAt = occurredAt
            });
        }
        else
        {
            row.SlotModelVersionId = slotModelVersionId;
            row.Ready = false;
            row.ReasonCode = reasonCode;
            row.UpdatedAt = occurredAt;
            row.InvalidatedAt = occurredAt;
        }
        await _context.SaveChangesAsync(cancellationToken);

        await WriteAuditAsync(
            action,
            agvId,
            new { slotModelVersionId, reasonCode, discardedVerifications = stale.Length },
            occurredAt,
            cancellationToken);
    }

    private Task<string> WriteAuditAsync(
        string action,
        string agvId,
        object detail,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken) =>
        _auditWriter.WriteBusinessAsync(
            new GovernanceAuditEntry(
                action,
                GovernedObjectKind.ActiveSlotConfiguration,
                agvId,
                null,
                GovernanceActionOutcome.Succeeded,
                JsonSerializer.Serialize(detail)),
            occurredAt,
            cancellationToken);

    private async Task<bool> HasOnlineServerLinkAsync(string agvId, CancellationToken cancellationToken) =>
        await _context.SessionRecoveries.AsNoTracking()
            .AnyAsync(row => row.AgvId == agvId && row.Readiness == SessionReadiness.Ready, cancellationToken);

    private async Task<int[]> ModelSlotNumbersAsync(string slotModelVersionId, CancellationToken cancellationToken)
    {
        int[] slots = await _context.Set<SlotModelSlotRow>().AsNoTracking()
            .Where(row => row.SlotModelVersionId == slotModelVersionId)
            .Select(row => row.PhysicalSlotNumber)
            .ToArrayAsync(cancellationToken);
        if (slots.Length == 0)
        {
            throw new InvalidOperationException(
                FormattableString.Invariant(
                    $"Vehicle model version {slotModelVersionId} has no slots; there is nothing to verify against."));
        }
        Array.Sort(slots);
        return slots;
    }

    private static string NewId() => Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
}
