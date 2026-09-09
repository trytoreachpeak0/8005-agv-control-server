namespace ControlServer.Domain;

/// <summary>
/// 四类敏感生效动作（REQ-0339 已实施的那一半）。
/// </summary>
/// <remarks>
/// 这四类在执行前都要给出明确确认影响预览。REQ-0339 的另一半——「<c>SystemAdministrator</c> 使用
/// 当前密码新鲜二次认证」——**本期不做**：本期不建人员认证，随 <c>FP-C10</c> 延后。宁可这一半空
/// 着，也不拿那个全场共用的环境变量密钥冒充二次认证：持有密钥者可以自称任何角色，用它当「新鲜二
/// 次认证」只是把一句不成立的话写进日志。有架构测试守着这一条。
/// </remarks>
public enum SensitiveActivationAction
{
    /// <summary>激活一个新版本。</summary>
    ActivateVersion,

    /// <summary>重新激活一条被暂停的绑定。</summary>
    ReactivateSuspendedBinding,

    /// <summary>回滚：选一个旧的不可变版本内容，在当下发起一次新的激活。</summary>
    Rollback,

    /// <summary>移除当前生效的绑定。</summary>
    RemoveActiveBinding
}

/// <summary>一次敏感生效动作的完整描述。预览与执行吃的是同一个它。</summary>
public sealed record SensitiveActivationRequest(
    SensitiveActivationAction Action,
    GovernedObjectKind ObjectKind,
    string ObjectId,
    long? TargetVersion,
    DateTimeOffset EffectiveFrom);

/// <summary>预览里的一个受影响对象：一个消费者，和它固化在哪一版上。</summary>
public sealed record ImpactedConsumer(
    string ConsumerKind,
    string ConsumerId,
    long FrozenVersion,
    DateTimeOffset FrozenAt);

/// <summary>
/// 一次敏感生效动作的影响预览。
/// </summary>
/// <remarks>
/// 影响边界按规格 5.3 的统一口径：**各消费者在自己的冻结点固化版本**。一次生效动作只够到冻结点
/// 晚于它的对象；已经固化在早先冻结点上的对象一个都不动。<see cref="Unaffected"/> 存在的理由正是
/// 这一句——「谁不受影响」和「谁受影响」是同一次判断的两半，只报一半会让人以为另一半没算过。
///
/// 预览为空不是「没有预览」：<see cref="HasNoImpactedObject"/> 为真时界面仍然要明确显示「无影响
/// 对象」，静默跳过会让人分不清「算过、结果是零」与「根本没算」。
/// </remarks>
public sealed record ActivationImpactPreview(
    SensitiveActivationRequest Request,
    IReadOnlyList<ImpactedConsumer> Impacted,
    IReadOnlyList<ImpactedConsumer> Unaffected)
{
    public bool HasNoImpactedObject => Impacted.Count == 0;

    /// <summary>给界面直述用的一句话。为空时它明确说「无影响对象」，而不是什么都不说。</summary>
    public string Statement => HasNoImpactedObject
        ? "无影响对象"
        : string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"影响 {Impacted.Count} 个对象");

    /// <summary>
    /// 按内容相等，不按引用。
    /// </summary>
    /// <remarks>
    /// 「给人看的预览与实际执行用的是同一份计算」这句话要能被断言，两份内容相同的预览就必须相等。
    /// <c>record</c> 默认对 <see cref="IReadOnlyList{T}"/> 比引用，那会让这条断言恒假——它测不出
    /// 两份计算有没有分叉，只测出它们不是同一个对象。
    /// </remarks>
    public bool Equals(ActivationImpactPreview? other) =>
        other is not null
        && Request == other.Request
        && Impacted.SequenceEqual(other.Impacted)
        && Unaffected.SequenceEqual(other.Unaffected);

    public override int GetHashCode() => HashCode.Combine(Request, Impacted.Count, Unaffected.Count);
}

/// <summary>
/// 一次回滚的结果。
/// </summary>
/// <remarks>
/// **回滚不是把历史改回去。**它选一个旧的不可变版本内容，在当下发起一次新的激活——所以这里带的是
/// 一个**新的** <see cref="ActivationId"/> 与一个**新的** <see cref="NewVersion"/>，而
/// <see cref="RolledBackToVersion"/> 那一版一个字节都没动。没有任何一条路能改写它，也没有「恢复
/// 历史」这个动作存在。
/// </remarks>
public sealed record RollbackOutcome(
    string ActivationId,
    string ObjectId,
    long RolledBackToVersion,
    long NewVersion,
    string SnapshotId,
    ActivationImpactPreview Impact);
