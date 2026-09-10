namespace ControlServer.Domain;

/// <summary>
/// 协议 v2 <c>OperatorContext</c>：一次动作是谁在什么时候、以哪种方式被核对过身份的。
/// </summary>
/// <remarks>
/// <para>
/// <b>这不是认证。</b>服务端不校验 <see cref="OperatorId"/>，也不校验
/// <see cref="VerificationMethod"/> 所声称的那次核对真的发生过——本期两端的权限骨架零实现，唯一
/// 的「认证」是一个全场共用的环境变量。这个类型做的全部事情是：把调用方交上来的那份上下文按协议
/// 的形状原样带到线上，并且拒绝一个协议不认的 <c>verificationMethod</c>，免得一条注定被对端拒收
/// 的命令被发出去。
/// </para>
/// <para>
/// 之所以要有这么个类型而不是直接拼一个匿名对象：<c>SlotConfigurationActivationCommand</c> 的
/// <c>administrator</c> 是 <c>required</c> 且 <c>additionalProperties: false</c>，三个字段少一个
/// 或多一个都是对端拒收。让它在编译期就只能是对的形状。
/// </para>
/// </remarks>
public sealed record ProtocolOperatorContext(
    string OperatorId,
    string VerificationMethod,
    DateTimeOffset VerifiedAt)
{
    /// <summary>刷卡核对。</summary>
    public const string Badge = "BADGE";

    /// <summary>凭已建立的会话核对。</summary>
    public const string Session = "SESSION";

    /// <summary>协议冻结的两种取值，没有第三种。</summary>
    public static IReadOnlyList<string> VerificationMethods { get; } = [Badge, Session];

    /// <summary>按协议的形状检查这份上下文，不合格就在发出去之前抛。</summary>
    public ProtocolOperatorContext Validated()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(OperatorId);
        if (!VerificationMethods.Contains(VerificationMethod, StringComparer.Ordinal))
        {
            throw new InvalidDataException(
                $"OperatorContext verificationMethod '{VerificationMethod}' is not one of "
                + string.Join(", ", VerificationMethods) + ".");
        }
        return this;
    }
}
