using System.Globalization;
using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ControlServer.Infrastructure.Persistence;

/// <summary>
/// AGV 归档与恢复的最小闭环（剖面簇 <c>FP-C5</c>）。
/// </summary>
/// <remarks>
/// 这里没有任何身份核验，也**不应该**有。甲档下没有 <c>SystemAdministrator</c> 实体：服务端
/// <c>src/</c> 里账号、密码哈希、登录会话全部零命中，唯一的「认证」是对一个全场共用环境变量做定时
/// 安全比较——持有密钥者可以自称任何角色。所以 REQ-0311 在本期的落地形态是「恢复入口只在受控运维
/// 流程里」，而不是拿那个密钥冒充身份核验。有架构测试盯着这一点。
/// </remarks>
public sealed class AgvRestorationStore(
    ControlServerDbContext context,
    IGovernanceAuditWriter auditWriter)
{
    private readonly ControlServerDbContext _context =
        context ?? throw new ArgumentNullException(nameof(context));
    private readonly IGovernanceAuditWriter _auditWriter =
        auditWriter ?? throw new ArgumentNullException(nameof(auditWriter));

    /// <summary>归档一台车。一台实体车只有一份档案，键就是 <c>agvId</c>。</summary>
    public async Task<AgvArchiveRow> ArchiveAsync(
        string agvId,
        string archiveReason,
        string? activeSlotConfigurationFingerprint,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agvId);
        ArgumentException.ThrowIfNullOrWhiteSpace(archiveReason);

        // 一台实体车只有一份档案，永远。恢复沿用它，而不是造第二份——这里明说，而不是让它撞在
        // 主键上变成一条读不懂的存储层报错。
        if (await _context.Set<AgvArchiveRow>().AnyAsync(row => row.AgvId == agvId, cancellationToken))
        {
            throw new InvalidOperationException(
                FormattableString.Invariant(
                    $"{agvId} already has an archive record. A physical car has exactly one, ever."));
        }

        AgvLifecycleRow lifecycle = await RequireLifecycleAsync(agvId, occurredAt, cancellationToken);
        lifecycle.Archived = true;
        lifecycle.Commissioned = false;
        lifecycle.UpdatedAt = occurredAt;

        AgvArchiveRow archive = new()
        {
            AgvId = agvId,
            ArchivedAt = occurredAt,
            ArchiveReason = archiveReason,
            ArchivedLifecycleGeneration = lifecycle.LifecycleGeneration,
            ArchivedActiveSlotConfigurationFingerprint = activeSlotConfigurationFingerprint
        };
        _context.Set<AgvArchiveRow>().Add(archive);
        await _context.SaveChangesAsync(cancellationToken);
        return archive;
    }

    /// <summary>
    /// 恢复一台已归档的车。**只能恢复回原来那台**：沿用原 <c>agvId</c>，不为同一实体车建第二份档案。
    /// </summary>
    /// <remarks>
    /// 新生命周期与候选 RIoT 绑定在一个事务里同成同败。车在不在线都不影响——完成恢复只要求原
    /// <c>agvId</c> 可用（REQ-0318）。恢复完成**不**等于可以干活：<see cref="AgvLifecycleRow.Commissioned"/>
    /// 保持 <c>false</c>，投运是另一个动作。
    /// </remarks>
    public async Task<AgvRestorationAttemptRow> RestoreAsync(
        string agvId,
        string restorationAttemptId,
        string candidateRiotBindingJson,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agvId);
        ArgumentException.ThrowIfNullOrWhiteSpace(restorationAttemptId);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateRiotBindingJson);

        AgvArchiveRow archive = await _context.Set<AgvArchiveRow>()
            .FirstOrDefaultAsync(row => row.AgvId == agvId, cancellationToken)
            ?? throw new InvalidOperationException(
                FormattableString.Invariant($"{agvId} has no archive record; there is nothing to restore."));

        await using IDbContextTransaction transaction =
            await _context.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            AgvLifecycleRow lifecycle = await RequireLifecycleAsync(agvId, occurredAt, cancellationToken);
            lifecycle.Archived = false;
            lifecycle.LifecycleGeneration++;
            // 恢复不投运。REQ-0317 的「检查通过也不自动投运」由此自动成立。
            lifecycle.Commissioned = false;
            lifecycle.CandidateRiotBindingJson = candidateRiotBindingJson;
            lifecycle.UpdatedAt = occurredAt;

            AgvRestorationAttemptRow attempt = new()
            {
                RestorationAttemptId = restorationAttemptId,
                AgvId = agvId,
                StartedAt = occurredAt,
                SettledAt = occurredAt,
                Outcome = GovernanceActionOutcome.Succeeded,
                ArchiveReason = archive.ArchiveReason,
                RestoredLifecycleGeneration = lifecycle.LifecycleGeneration
            };
            _context.Set<AgvRestorationAttemptRow>().Add(attempt);
            await _context.SaveChangesAsync(cancellationToken);

            attempt.AuditRecordId = await WriteAttemptAuditAsync(attempt, occurredAt, cancellationToken);
            await _context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return attempt;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    /// <summary>
    /// 记录一次没有成功的恢复尝试：失败、超时、结果未知。三种都留不可改写审计（REQ-0320）。
    /// </summary>
    public async Task<AgvRestorationAttemptRow> RecordUnsuccessfulAttemptAsync(
        string agvId,
        string restorationAttemptId,
        GovernanceActionOutcome outcome,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agvId);
        ArgumentException.ThrowIfNullOrWhiteSpace(restorationAttemptId);
        if (outcome == GovernanceActionOutcome.Succeeded)
        {
            throw new ArgumentException(
                "A successful restoration is recorded by RestoreAsync, which also moves the lifecycle.",
                nameof(outcome));
        }

        AgvArchiveRow? archive = await _context.Set<AgvArchiveRow>()
            .FirstOrDefaultAsync(row => row.AgvId == agvId, cancellationToken);
        AgvLifecycleRow lifecycle = await RequireLifecycleAsync(agvId, occurredAt, cancellationToken);
        AgvRestorationAttemptRow attempt = new()
        {
            RestorationAttemptId = restorationAttemptId,
            AgvId = agvId,
            StartedAt = occurredAt,
            SettledAt = occurredAt,
            Outcome = outcome,
            ArchiveReason = archive?.ArchiveReason ?? "UNKNOWN",
            RestoredLifecycleGeneration = lifecycle.LifecycleGeneration
        };
        _context.Set<AgvRestorationAttemptRow>().Add(attempt);
        await _context.SaveChangesAsync(cancellationToken);

        attempt.AuditRecordId = await WriteAttemptAuditAsync(attempt, occurredAt, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);
        return attempt;
    }

    /// <summary>把一台车的生命周期事实读出来，供谓词链求值。</summary>
    public async Task<AgvLifecycleFacts> ReadLifecycleFactsAsync(
        string agvId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agvId);

        AgvLifecycleRow? row = await _context.Set<AgvLifecycleRow>()
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.AgvId == agvId, cancellationToken);
        return row is null
            ? new AgvLifecycleFacts(agvId, 0, Archived: true, Commissioned: false, HasCandidateRiotBinding: false)
            : new AgvLifecycleFacts(
                row.AgvId,
                row.LifecycleGeneration,
                row.Archived,
                row.Commissioned,
                !string.IsNullOrWhiteSpace(row.CandidateRiotBindingJson));
    }

    private async Task<string> WriteAttemptAuditAsync(
        AgvRestorationAttemptRow attempt,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        // 审计必须带恢复尝试 ID、目标 agvId 与归档原因。四种结局同等保留。
        string detail = JsonSerializer.Serialize(new
        {
            restorationAttemptId = attempt.RestorationAttemptId,
            agvId = attempt.AgvId,
            archiveReason = attempt.ArchiveReason,
            restoredLifecycleGeneration = attempt.RestoredLifecycleGeneration
        });
        return await _auditWriter.WriteBusinessAsync(
            new GovernanceAuditEntry(
                "AGV_RESTORATION_ATTEMPT",
                GovernedObjectKind.AgvLifecycle,
                attempt.AgvId,
                attempt.RestoredLifecycleGeneration,
                attempt.Outcome ?? GovernanceActionOutcome.ResultUnknown,
                detail),
            occurredAt,
            cancellationToken);
    }

    private async Task<AgvLifecycleRow> RequireLifecycleAsync(
        string agvId,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        AgvLifecycleRow? row = await _context.Set<AgvLifecycleRow>()
            .FirstOrDefaultAsync(candidate => candidate.AgvId == agvId, cancellationToken);
        if (row is not null)
        {
            return row;
        }
        row = new AgvLifecycleRow
        {
            AgvId = agvId,
            LifecycleGeneration = 1,
            Archived = false,
            Commissioned = false,
            UpdatedAt = occurredAt
        };
        _context.Set<AgvLifecycleRow>().Add(row);
        return row;
    }

    internal static string NewAttemptId() => Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
}
