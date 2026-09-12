using System.Globalization;
using System.Text.Json;
using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;

namespace ControlServer.Host.Transport;

/// <summary>
/// 协议 v2 消息 7／8 的线上形状与 #15 的服务端业务语义之间的映射。
/// </summary>
/// <remarks>
/// <para>
/// <b>7／8 走 RELIABLE，不是 REQUEST/RESPONSE。</b>REQ-0264 的「不能猜测成功」正是
/// <c>PENDING_RESULT_REPLAY</c> 存在的理由：用 RESPONSE 就没有补报语义，断线即丢，服务端除了猜
/// 没有别的可做。manifest 冻结的就是 RELIABLE，命令侧 <c>recoveryRole</c> 是
/// <c>SLOT_CONFIGURATION</c>，结果侧是 <c>PENDING_RESULT_REPLAY</c>，两侧
/// <c>durableBeforeSend</c> 与 <c>durableBeforeAck</c> 都是真。
/// </para>
/// <para>
/// <b><c>UNKNOWN</c> 不算成功，也不留在待补报态。</b>协议把车载端的义务写成
/// <c>REPORT_ACTIVATION_OUTCOME_INCLUDING_UNKNOWN</c>——车说「我不知道」是一个合法答复，不是没有
/// 答复。所以它收敛那次激活，但收敛到失败侧：生效配置不动，理由码记下 <c>UNKNOWN</c> 本身。
/// 让它停在待补报态是另一种错：车会一直补报一个它永远答不上来的东西。真实情况由随后的
/// <c>CapabilitySnapshot</c> 说了算——那正是向量第三步存在的原因。
/// </para>
/// </remarks>
internal static class SlotConfigurationActivationWire
{
    /// <summary>协议冻结的三种结果。</summary>
    internal const string Activated = "ACTIVATED";

    internal const string Rejected = "REJECTED";

    internal const string Unknown = "UNKNOWN";

    /// <summary>消息 7 的 payload。</summary>
    internal static object Command(
        SlotConfigurationActivationRow activation,
        string? expectedActiveSlotConfigurationVersion,
        ProtocolOperatorContext administrator)
    {
        ArgumentNullException.ThrowIfNull(activation);
        ArgumentNullException.ThrowIfNull(administrator);

        return new
        {
            activationId = activation.ActivationId,
            targetSlotConfigurationVersion = Version(activation.ConfigurationVersion),
            targetSlotConfigurationFingerprint = activation.Fingerprint,
            expectedActiveSlotConfigurationVersion,
            administrator = new
            {
                operatorId = administrator.OperatorId,
                verificationMethod = administrator.VerificationMethod,
                verifiedAt = administrator.VerifiedAt
            },
            issuedAt = activation.IssuedAt
        };
    }

    /// <summary>把消息 8 读成 #15 已经定好的那份报告。</summary>
    internal static ActivationResultReport Result(JsonElement payload)
    {
        string outcome = Required(payload, "outcome");
        if (outcome is not (Activated or Rejected or Unknown))
        {
            throw new InvalidDataException(
                $"SlotConfigurationActivationResult outcome '{outcome}' is not one of "
                + $"{Activated}, {Rejected}, {Unknown}.");
        }
        return new ActivationResultReport(
            Required(payload, "activationId"),
            Succeeded: outcome == Activated,
            ReasonCode: ReasonCode(payload, outcome),
            ReportedAt: payload.GetProperty("verifiedAt").GetDateTimeOffset());
    }

    /// <summary>
    /// 版本号在库里是整数，在线上是字符串。
    /// </summary>
    /// <remarks>
    /// 协议把 <c>targetSlotConfigurationVersion</c> 定成非空字符串而不是整数，因为它是两端共用的
    /// 版本标识，不保证永远是十进制计数。服务端这一侧当下就是计数，所以转换在这一个地方发生，
    /// 用不变文化格式化——用当前区域格式化会在某些区域下产出带分组分隔符的版本号。
    /// </remarks>
    internal static string Version(long configurationVersion) =>
        configurationVersion.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// 失败侧留什么理由码。
    /// </summary>
    /// <remarks>
    /// 有 <c>problem</c> 就用它的 <c>reasonCode</c>——那是车给出的那个稳定错误码。没有
    /// <c>problem</c> 的失败（<c>UNKNOWN</c> 通常如此）就记 <c>outcome</c> 本身：把它记成
    /// <see langword="null"/> 会让「车说不知道」和「车说成功」在库里长得一样。
    /// </remarks>
    private static string? ReasonCode(JsonElement payload, string outcome)
    {
        if (outcome == Activated)
        {
            return null;
        }
        JsonElement problem = payload.GetProperty("problem");
        return problem.ValueKind == JsonValueKind.Null ? outcome : Required(problem, "reasonCode");
    }

    private static string Required(JsonElement element, string name) =>
        element.GetProperty(name).GetString()
        ?? throw new InvalidDataException($"SlotConfigurationActivationResult '{name}' must be a string.");
}
