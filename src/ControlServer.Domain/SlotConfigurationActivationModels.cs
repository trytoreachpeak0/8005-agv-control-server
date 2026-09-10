namespace ControlServer.Domain;

/// <summary>
/// 仓位配置激活的交付约定（规格 6.2）。
/// </summary>
/// <remarks>
/// **必须走 RELIABLE，不能走 REQUEST/RESPONSE。**REQ-0264 的「不能猜测成功」正是
/// <c>PENDING_RESULT_REPLAY</c> 存在的理由：用 RESPONSE 就没有补报语义，断线即丢，服务端除了猜
/// 没有别的可做。<see cref="RecoveryRole"/> 是新增的 <c>SLOT_CONFIGURATION</c>。
///
/// 这里只有约定，没有传输：消息 7／8 的序列化与收发在
/// <c>ControlServer.Host.Transport.SlotConfigurationActivationWire</c>。
/// </remarks>
public static class SlotConfigurationActivationDelivery
{
    /// <summary>交付类别。RELIABLE——补报语义的前提。</summary>
    public const string DeliveryClass = "RELIABLE";

    /// <summary>恢复角色。重连补报时按它找回未结的激活。</summary>
    public const string RecoveryRole = "SLOT_CONFIGURATION";

    /// <summary>协议 v2 消息 7。</summary>
    public const string CommandMessageType = "SlotConfigurationActivationCommand";

    /// <summary>协议 v2 消息 8。</summary>
    public const string ResultMessageType = "SlotConfigurationActivationResult";

    /// <summary><c>CapabilitySnapshot</c> 上的指纹字段名。</summary>
    public const string CapabilityFingerprintField = "activeSlotConfigurationFingerprint";
}

/// <summary>
/// 一次激活此刻处在哪一格。
/// </summary>
/// <remarks>
/// <see cref="PendingResult"/> 是一个**明确的态**，不是「不知道」的委婉说法：结果没回来的时候服务端
/// 既不认为成功也不认为失败，它认为自己在等。这三个值之间没有第四个「大概成功了」。
/// </remarks>
public static class SlotConfigurationActivationState
{
    public const string PendingResult = "PENDING_RESULT";
    public const string Activated = "ACTIVATED";
    public const string Failed = "FAILED";
}

/// <summary>
/// 一条激活记录记的是哪一种动作。
/// </summary>
/// <remarks>
/// 回滚也是一次激活——它选一个旧的不可变版本内容在当下重新激活，所以它和普通激活共用同一张表、
/// 同一个待补报态、同一个恢复角色，只在这里分开。
/// </remarks>
public static class SlotConfigurationActivationKind
{
    public const string Activation = "ACTIVATION";
    public const string Rollback = "ROLLBACK";
}

/// <summary>车载端补上来的一次激活结果。</summary>
public sealed record ActivationResultReport(
    string ActivationId,
    bool Succeeded,
    string? ReasonCode,
    DateTimeOffset ReportedAt);

/// <summary>
/// 车报上来的生效配置指纹与服务端认定的那一版是否一致。
/// </summary>
/// <remarks>
/// <para>
/// <b>不一致时服务端不采纳那份能力快照。</b>它不是「以谁为准」的问题——服务端认定这台车装着 A，
/// 车说自己装着 B，那么这台车此刻装着什么，双方都不知道。采纳 B 等于服务端放弃自己的权威，采纳 A
/// 等于假装没看见。两条都不做：拒收那一份快照并回一条稳定错误码
/// <c>SLOT_CONFIGURATION_FINGERPRINT_MISMATCH</c>，能力修订号因此没被采纳，这台车在
/// <c>DecideReadinessAsync</c> 里就取不到业务就绪——修正的路是重新走一次激活。
/// </para>
/// <para>
/// 服务端手上还没有任何生效版本时（这台车从没激活过，或者刚被恢复回来）没有可比对的对象，
/// <see cref="Agrees"/> 为真，此时看的是 <see cref="RestorationCandidate"/>：那份指纹能不能让归档前
/// 的配置成为恢复候选（REQ-0316）。
/// </para>
/// </remarks>
public sealed record SlotConfigurationFingerprintVerdict(
    string AgvId,
    bool Agrees,
    string? ExpectedFingerprint,
    string ReportedFingerprint,
    RecoveryCandidateVerdict? RestorationCandidate)
{
    /// <summary>协议冻结的稳定错误码，也是 <c>CV-SLOT-CONFIGURATION-ACTIVATION</c> 指定的那一个。</summary>
    public const string MismatchCode = "SLOT_CONFIGURATION_FINGERPRINT_MISMATCH";

    /// <summary>不一致时指向线上那个字段本身，好让对端知道是哪一项对不上。</summary>
    public const string MismatchFieldPath = "payload.activeSlotConfigurationFingerprint";
}

/// <summary>
/// 归档前配置能不能作为这次重连的恢复候选（REQ-0316）。
/// </summary>
/// <remarks>
/// 指纹一致才认。不一致时按不匹配处置——不是「以服务端的为准」也不是「以车上的为准」，是这份候选
/// 不成立，重新走一次激活。
/// </remarks>
public sealed record RecoveryCandidateVerdict(
    string AgvId,
    bool Accepted,
    string ReasonCode,
    string? CandidateFingerprint,
    string? ReportedFingerprint)
{
    public const string AcceptedCode = "RESTORATION_CANDIDATE_ACCEPTED";
    public const string FingerprintMismatchCode = "RESTORATION_CANDIDATE_FINGERPRINT_MISMATCH";
    public const string NoCandidateCode = "RESTORATION_CANDIDATE_ABSENT";
}

/// <summary>
/// 下发对象不合格：配置未发布，或该车的 IO 绑定不完备（REQ-0264）。
/// </summary>
/// <remarks>
/// 原子激活的前提是「有一份完整的东西可以整份换上去」。缺一个仓位的 IO 绑定，换上去的就是一份半份
/// 的配置，而车上没有「半份生效」这个状态——所以拦在下发之前，不是拦在结果里。
/// </remarks>
public sealed class ActivationTargetIncompleteException : InvalidOperationException
{
    public ActivationTargetIncompleteException(string message)
        : base(message)
    {
    }

    public ActivationTargetIncompleteException()
        : base("The configuration is not publishable to a vehicle: it is unpublished or its IO bindings are incomplete.")
    {
    }

    public ActivationTargetIncompleteException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
