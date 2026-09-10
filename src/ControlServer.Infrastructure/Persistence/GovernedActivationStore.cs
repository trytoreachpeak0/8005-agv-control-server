using System.Globalization;
using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Infrastructure.Persistence;

/// <summary>
/// 敏感生效动作：影响预览，以及作为其中一类的回滚（REQ-0346、REQ-0339 的已实施半边）。
/// </summary>
/// <remarks>
/// <para>
/// **回滚是受控的新激活，不是历史覆盖。**它选一个旧的不可变版本内容，在当下发起一次新的激活：产出
/// 的是一条新的激活记录与一版新的冻结快照，被回滚到的那一版一个字节不动。这个类里没有任何一个方法
/// 能改写既有版本——不可改写性由 <see cref="PublishedVersionImmutabilityGuard"/> 在
/// <see cref="ControlServerDbContext.SaveChanges(bool)"/> 上守着，不是靠这里自觉。
/// </para>
/// <para>
/// **预览与实际影响是同一份计算。**执行路径自己调 <see cref="PreviewAsync"/>，把它算出来的那一份
/// 原样带进结果——不是先算一遍给人看、再算一遍去执行。两份计算迟早会分叉，而分叉的那一天，人看到
/// 的预览就成了一句假话。
/// </para>
/// <para>本类不新增任何表。</para>
/// </remarks>
public sealed class GovernedActivationStore(
    ControlServerDbContext context,
    IConfigurationSnapshotStore snapshots,
    GovernedConfigurationPublisher publisher,
    IGovernanceAuditWriter auditWriter)
{
    private readonly ControlServerDbContext _context =
        context ?? throw new ArgumentNullException(nameof(context));
    private readonly IConfigurationSnapshotStore _snapshots =
        snapshots ?? throw new ArgumentNullException(nameof(snapshots));
    private readonly GovernedConfigurationPublisher _publisher =
        publisher ?? throw new ArgumentNullException(nameof(publisher));
    private readonly IGovernanceAuditWriter _auditWriter =
        auditWriter ?? throw new ArgumentNullException(nameof(auditWriter));

    /// <summary>
    /// 一次敏感生效动作会影响哪些对象，一次说清。四类动作走的都是这个方法。
    /// </summary>
    /// <remarks>
    /// 影响边界：各消费者在自己的冻结点固化版本。冻结点**早于**本次动作生效点的消费者已经固化，
    /// 不受影响；冻结点**晚于**它的才够得着。为空时返回的预览仍然是一份预览——
    /// <see cref="ActivationImpactPreview.Statement"/> 会明确说「无影响对象」。
    /// </remarks>
    public async Task<ActivationImpactPreview> PreviewAsync(
        SensitiveActivationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ObjectId);

        ConfigurationConsumerBindingRow[] bindings = await _context.Set<ConfigurationConsumerBindingRow>()
            .AsNoTracking()
            .Where(row => row.ObjectKind == request.ObjectKind && row.ObjectId == request.ObjectId)
            .ToArrayAsync(cancellationToken);

        List<ImpactedConsumer> impacted = [];
        List<ImpactedConsumer> unaffected = [];
        foreach (ConfigurationConsumerBindingRow row in bindings
            .OrderBy(row => row.ConsumerKind, StringComparer.Ordinal)
            .ThenBy(row => row.ConsumerId, StringComparer.Ordinal))
        {
            ImpactedConsumer consumer = new(row.ConsumerKind, row.ConsumerId, row.FrozenVersion, row.FrozenAt);
            if (row.FrozenAt > request.EffectiveFrom)
            {
                impacted.Add(consumer);
            }
            else
            {
                unaffected.Add(consumer);
            }
        }
        return new ActivationImpactPreview(request, impacted, unaffected);
    }

    /// <summary>记录一个消费者在自己的冻结点固化了哪一版。影响边界整个建立在这张表上。</summary>
    public async Task RecordConsumerFreezeAsync(
        string consumerKind,
        string consumerId,
        GovernedObjectKind objectKind,
        string objectId,
        long frozenVersion,
        DateTimeOffset frozenAt,
        string snapshotId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(consumerKind);
        ArgumentException.ThrowIfNullOrWhiteSpace(consumerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotId);

        _context.Set<ConfigurationConsumerBindingRow>().Add(new ConfigurationConsumerBindingRow
        {
            ConsumerKind = consumerKind,
            ConsumerId = consumerId,
            ObjectKind = objectKind,
            ObjectId = objectId,
            FrozenVersion = frozenVersion,
            FrozenAt = frozenAt,
            SnapshotId = snapshotId
        });
        await _context.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// 执行一次敏感生效动作：先算影响，再动，审计里带着算出来的那一份。
    /// </summary>
    /// <remarks>
    /// 返回的预览就是执行时用的那一份，调用方拿到的与界面上给人看的是同一个对象。
    /// </remarks>
    public async Task<ActivationImpactPreview> ApplyAsync(
        SensitiveActivationRequest request,
        CancellationToken cancellationToken)
    {
        ActivationImpactPreview preview = await PreviewAsync(request, cancellationToken);
        await _auditWriter.WriteBusinessAsync(
            new GovernanceAuditEntry(
                ActionCodeFor(request.Action),
                request.ObjectKind,
                request.ObjectId,
                request.TargetVersion,
                GovernanceActionOutcome.Succeeded,
                JsonSerializer.Serialize(new
                {
                    effectiveFrom = request.EffectiveFrom,
                    impactStatement = preview.Statement,
                    impacted = preview.Impacted.Select(consumer => new
                    {
                        consumer.ConsumerKind,
                        consumer.ConsumerId,
                        consumer.FrozenVersion
                    }),
                    unaffectedCount = preview.Unaffected.Count
                })),
            request.EffectiveFrom,
            cancellationToken);
        return preview;
    }

    /// <summary>
    /// 回滚一台车的生效配置：选 <paramref name="toVersion"/> 那一版的内容，在当下发起一次新的激活。
    /// </summary>
    /// <remarks>
    /// 被回滚到的那一版不动。产出的是一版新的冻结快照（内容与旧版逐字节相同）、一条新的激活记录，
    /// 以及一次影响预览——三者出自同一次调用，所以审计里记的影响与实际发生的影响不可能是两回事。
    /// </remarks>
    public async Task<RollbackOutcome> RollbackAsync(
        string agvId,
        string slotModelVersionId,
        long toVersion,
        DateTimeOffset effectiveFrom,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agvId);
        ArgumentException.ThrowIfNullOrWhiteSpace(slotModelVersionId);

        string objectId = FormattableString.Invariant($"{agvId}:{slotModelVersionId}");
        GovernedConfigurationSnapshot source = await _snapshots.ReadAsync(
                GovernedObjectKind.ActiveSlotConfiguration, objectId, toVersion, cancellationToken)
            ?? throw new InvalidOperationException(
                FormattableString.Invariant(
                    $"There is no frozen version {toVersion} of {objectId} to roll back to."));

        long newVersion = await NextVersionAsync(objectId, cancellationToken);
        SensitiveActivationRequest request = new(
            SensitiveActivationAction.Rollback,
            GovernedObjectKind.ActiveSlotConfiguration,
            objectId,
            newVersion,
            effectiveFrom);
        ActivationImpactPreview preview = await PreviewAsync(request, cancellationToken);

        // 新的一版：内容取自旧版，逐字节相同；旧版本身没有被读之外的任何动作碰过。
        GovernedConfigurationSnapshot activated = await _publisher.PublishVersionAsync(
            GovernedObjectKind.ActiveSlotConfiguration,
            objectId,
            newVersion,
            source.ContentJson,
            "SLOT_CONFIGURATION_ROLLBACK_ACTIVATED",
            effectiveFrom,
            cancellationToken);

        SlotConfigurationActivationRow activation = new()
        {
            ActivationId = NewId(),
            AgvId = agvId,
            SlotModelVersionId = slotModelVersionId,
            ConfigurationVersion = newVersion,
            Fingerprint = activated.ContentSha256,
            Kind = SlotConfigurationActivationKind.Rollback,
            RolledBackToVersion = toVersion,
            State = SlotConfigurationActivationState.PendingResult,
            RecoveryRole = SlotConfigurationActivationDelivery.RecoveryRole,
            IssuedAt = effectiveFrom,
            SnapshotId = activated.SnapshotId
        };
        _context.Set<SlotConfigurationActivationRow>().Add(activation);
        await _context.SaveChangesAsync(cancellationToken);

        await _auditWriter.WriteBusinessAsync(
            new GovernanceAuditEntry(
                ActionCodeFor(SensitiveActivationAction.Rollback),
                GovernedObjectKind.ActiveSlotConfiguration,
                objectId,
                newVersion,
                GovernanceActionOutcome.Succeeded,
                JsonSerializer.Serialize(new
                {
                    activationId = activation.ActivationId,
                    rolledBackToVersion = toVersion,
                    impactStatement = preview.Statement,
                    impactedCount = preview.Impacted.Count,
                    unaffectedCount = preview.Unaffected.Count
                }),
                activated.SnapshotId),
            effectiveFrom,
            cancellationToken);

        return new RollbackOutcome(
            activation.ActivationId, objectId, toVersion, newVersion, activated.SnapshotId, preview);
    }

    private static string ActionCodeFor(SensitiveActivationAction action) => action switch
    {
        SensitiveActivationAction.ActivateVersion => "SENSITIVE_ACTIVATION_VERSION_ACTIVATED",
        SensitiveActivationAction.ReactivateSuspendedBinding => "SENSITIVE_ACTIVATION_BINDING_REACTIVATED",
        SensitiveActivationAction.Rollback => "SENSITIVE_ACTIVATION_ROLLED_BACK",
        SensitiveActivationAction.RemoveActiveBinding => "SENSITIVE_ACTIVATION_BINDING_REMOVED",
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown sensitive activation action.")
    };

    private async Task<long> NextVersionAsync(string objectId, CancellationToken cancellationToken)
    {
        long[] versions = await _context.Set<GovernedConfigurationSnapshotRow>().AsNoTracking()
            .Where(row => row.ObjectKind == GovernedObjectKind.ActiveSlotConfiguration && row.ObjectId == objectId)
            .Select(row => row.Version)
            .ToArrayAsync(cancellationToken);
        return versions.Length == 0 ? 1 : versions.Max() + 1;
    }

    /// <summary>
    /// 激活 id 用协议的 <c>Id</c> 形状（<c>D</c>），不是批次 3 其余 id 的 <c>N</c>。
    /// </summary>
    /// <remarks>
    /// 它是批次 3 唯一一个要上线的 id：manifest 把 <c>activationId</c> 定为消息 7／8 的
    /// <c>businessDedupKey</c>，schema 把它定为 <c>Id</c>（<c>format: uuid</c>）。让库里存的和线上
    /// 走的是同一个字符串，而不是在传输层来回换一次形状——一个身份两种写法，迟早有人只查得到一种。
    /// </remarks>
    private static string NewId() => Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture);
}
