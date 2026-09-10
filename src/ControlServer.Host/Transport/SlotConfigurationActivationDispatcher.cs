using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Host.Transport;

/// <summary>
/// 把 #15 的激活业务语义接到协议 v2 消息 7／8 的线上。
/// </summary>
/// <remarks>
/// <para>
/// <b>这一层不判断任何业务问题。</b>「对象是否已发布」「IO 绑定是否齐」「结果怎么收敛」「补报是否
/// 幂等」全在 <see cref="SlotConfigurationActivationCoordinator"/> 里，本类一条都不重做。它做的是
/// 三件传输的事：把落好库的那次激活变成一条命令发出去、记住那条命令的 messageId、重连时按
/// <c>SLOT_CONFIGURATION</c> 这个恢复角色把还没结的命令找出来重发。
/// </para>
/// <para>
/// <b>顺序就是 <c>durableBeforeSend</c> 的意思。</b>激活记录先落库（协调器保存），命令信封再落发件箱，
/// 最后才上线。中间任何一步断电，重连时那次激活仍在待补报态、那条命令仍在发件箱里，补发的是逐字节
/// 相同的一行。反过来先发后存，断电就得到一台可能已经换了配置、而服务端一无所知的车。
/// </para>
/// <para>
/// <b>服务端不发明操作者身份。</b><paramref name="administrator" /> 由调用方交上来，本类原样带上线，
/// 不校验、不补默认值。本期两端权限骨架零实现，唯一的「认证」是一个全场共用的环境变量——在这里编一
/// 个操作者出来，等于让审计里出现一个从未存在过的人。
/// </para>
/// </remarks>
public sealed class SlotConfigurationActivationDispatcher(
    ControlServerDbContext context,
    SlotConfigurationActivationCoordinator coordinator,
    OnboardJourneyPublisher publisher,
    IGovernanceAuditWriter auditWriter)
{
    private readonly ControlServerDbContext _context =
        context ?? throw new ArgumentNullException(nameof(context));
    private readonly SlotConfigurationActivationCoordinator _coordinator =
        coordinator ?? throw new ArgumentNullException(nameof(coordinator));
    private readonly OnboardJourneyPublisher _publisher =
        publisher ?? throw new ArgumentNullException(nameof(publisher));
    private readonly IGovernanceAuditWriter _auditWriter =
        auditWriter ?? throw new ArgumentNullException(nameof(auditWriter));

    /// <summary>下发一次激活：落库、排队、上线。</summary>
    public async Task<SlotConfigurationActivationRow> IssueAsync(
        string agvId,
        string slotModelVersionId,
        long sessionGeneration,
        ProtocolOperatorContext administrator,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(administrator);
        administrator.Validated();

        SlotConfigurationActivationRow activation = await _coordinator
            .IssueActivationAsync(agvId, slotModelVersionId, occurredAt, cancellationToken)
            .ConfigureAwait(false);

        // 车此刻装着哪一版，是服务端自己手上的事实。每份 CapabilitySnapshot 到达时都会拿它与车报上来
        // 的指纹核一次，对不上当场拒绝——所以这里读它，而不是另存一份「车说它装着什么」。
        ActiveSlotConfigurationRow? active = await _context.Set<ActiveSlotConfigurationRow>().AsNoTracking()
            .FirstOrDefaultAsync(row => row.AgvId == agvId, cancellationToken).ConfigureAwait(false);

        string messageId = Guid.NewGuid().ToString("D");
        await _publisher.QueueSlotConfigurationActivationCommandAsync(
            messageId,
            agvId,
            sessionGeneration,
            SlotConfigurationActivationWire.Command(
                activation,
                active is null ? null : SlotConfigurationActivationWire.Version(active.ConfigurationVersion),
                administrator),
            cancellationToken).ConfigureAwait(false);

        SlotConfigurationActivationRow tracked = await _context.Set<SlotConfigurationActivationRow>()
            .SingleAsync(row => row.ActivationId == activation.ActivationId, cancellationToken)
            .ConfigureAwait(false);
        tracked.CommandMessageId = messageId;
        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await _publisher.SendPersistedAsync(messageId, cancellationToken).ConfigureAwait(false);
        return tracked;
    }

    /// <summary>
    /// 这台车重连时要补发的激活命令。
    /// </summary>
    /// <remarks>
    /// 判据是「那次激活还在待补报态」，不是「发件箱那一行还没被 ack」。两者通常一致，不一致的那种情况
    /// 恰恰是要补发的：车 ack 了命令然后在报结果之前掉线——发件箱看它已结，而那次激活并没有结果。
    /// </remarks>
    public async Task<IReadOnlyList<string>> PendingCommandMessageIdsAsync(
        string agvId,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<SlotConfigurationActivationRow> pending = await _coordinator
            .ListPendingResultReplayAsync(agvId, cancellationToken).ConfigureAwait(false);
        return [.. pending.Select(row => row.CommandMessageId).OfType<string>()];
    }

    /// <summary>
    /// 车在 <c>CapabilitySnapshot</c> 里报的 <c>activeSlotConfigurationFingerprint</c> 与服务端认定的
    /// 那一版核对一次（REQ-0316、<c>VERIFY_FINGERPRINT_BEFORE_ACTIVATION</c>）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 每一份能力快照到达时都核，不是发起激活时才核。发起激活时才核，中间那段时间里服务端相信的东西
    /// 就没有任何东西在担保；每份快照都核，则「服务端认定的那一版」这个事实是被持续对照过的，发起激活
    /// 时读它才站得住——这也是服务端不为「车说它装着什么」另存一份副本的原因：那份副本一旦与这里的
    /// 判定分家，就会有人去信副本。
    /// </para>
    /// <para>
    /// 不一致要留痕，而且要留在不可改写的那条流上：一次「车与服务端对不上」是治理事件，不是一行日志。
    /// </para>
    /// </remarks>
    public async Task<SlotConfigurationFingerprintVerdict> ReconcileReportedFingerprintAsync(
        string agvId,
        string reportedFingerprint,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agvId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reportedFingerprint);

        ActiveSlotConfigurationRow? active = await _context.Set<ActiveSlotConfigurationRow>().AsNoTracking()
            .FirstOrDefaultAsync(row => row.AgvId == agvId, cancellationToken).ConfigureAwait(false);

        if (active is null)
        {
            // 服务端手上没有生效版本：这台车要么从没激活过，要么刚被恢复回来。没有可比对的对象，所以
            // 这一份指纹要回答的是另一个问题——归档前那份配置还能不能当恢复候选。
            RecoveryCandidateVerdict candidate = await _coordinator
                .EvaluateRecoveryCandidateAsync(agvId, reportedFingerprint, cancellationToken)
                .ConfigureAwait(false);
            return new SlotConfigurationFingerprintVerdict(
                agvId, Agrees: true, ExpectedFingerprint: null, reportedFingerprint, candidate);
        }

        bool agrees = string.Equals(active.Fingerprint, reportedFingerprint, StringComparison.Ordinal);
        if (!agrees)
        {
            await _auditWriter.WriteBusinessAsync(
                new GovernanceAuditEntry(
                    "SLOT_CONFIGURATION_FINGERPRINT_MISMATCH_OBSERVED",
                    GovernedObjectKind.ActiveSlotConfiguration,
                    agvId,
                    active.ConfigurationVersion,
                    GovernanceActionOutcome.Failed,
                    JsonSerializer.Serialize(new
                    {
                        reasonCode = SlotConfigurationFingerprintVerdict.MismatchCode,
                        expectedFingerprint = active.Fingerprint,
                        reportedFingerprint,
                        activationId = active.ActivationId
                    }),
                    active.SnapshotId),
                observedAt,
                cancellationToken).ConfigureAwait(false);
        }
        return new SlotConfigurationFingerprintVerdict(
            agvId, agrees, active.Fingerprint, reportedFingerprint, RestorationCandidate: null);
    }

    /// <summary>收下一份结果，交给协调器收敛。</summary>
    public Task<SlotConfigurationActivationRow> RecordResultAsync(
        ActivationResultReport report,
        CancellationToken cancellationToken) =>
        _coordinator.RecordResultAsync(report, cancellationToken);
}
