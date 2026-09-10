using Microsoft.Extensions.Options;

namespace ControlServer.Host.Runtime;

/// <summary>
/// 仓位配置激活的发起入口开关与凭据。
/// </summary>
/// <remarks>
/// <para>
/// <b>默认关闭，而且这个默认是刻意的。</b>这个入口发出去的是协议 v2 消息 7——一条让车换掉自己
/// 仓位 IO 绑定的命令。装好就开着的服务器等于把这件事挂在网上，所以要显式开，并且开之前必须先有
/// 一个被填过的凭据环境变量，否则启动期校验就失败。<c>OnboardSafetyProjection</c> 是同一个形状，
/// 理由也是同一个。
/// </para>
/// <para>
/// <b>这不是认证。</b>Bearer 比的是一个全场共用的环境变量，批次 0 的权限骨架两端零实现。它挡住的
/// 是「谁都能 POST 一下」，不是「这个人是谁」——真正的操作者身份由调用方在
/// <c>administrator</c> 里交上来，服务端原样带上线、不发明也不补默认值。
/// </para>
/// </remarks>
public sealed class SlotConfigurationActivationOptions
{
    public const string SectionName = "SlotConfigurationActivation";

    public bool Enabled { get; set; }

    public string CredentialEnvironmentVariable { get; set; } = "CONTROL_SERVER_GOVERNANCE_CREDENTIAL";
}

public sealed class SlotConfigurationActivationOptionsValidator
    : IValidateOptions<SlotConfigurationActivationOptions>
{
    public ValidateOptionsResult Validate(string? name, SlotConfigurationActivationOptions options)
    {
        _ = name;
        if (!options.Enabled) return ValidateOptionsResult.Success;

        List<string> failures = [];
        if (string.IsNullOrWhiteSpace(options.CredentialEnvironmentVariable) ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(options.CredentialEnvironmentVariable)))
            failures.Add("SlotConfigurationActivation must name a populated external credential variable.");
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
