using Microsoft.Extensions.Configuration;

namespace ControlServer.Host.Dashboard;

/// <summary>
/// 期待动作超时的门槛：服务端据此由告警的 <c>raisedAt</c> 推算一个仓已经等了多久（REQ-0358，CP-0005 第 4.1 节）。
/// </summary>
/// <remarks>
/// <para>
/// 门槛本身由车载端配置、由车载端判定（默认 3 个 <c>OperationTimeout</c>，每个 120 秒，共 6 分钟；hmi#109）；告警的 <c>raisedAt</c>
/// 是越过门槛的那一刻，所以「已等待」= 门槛 + 越过之后又过了多久。服务端拿不到车上那个值，只能配一份与车上相同的。两边配得不一致时，
/// 看板上的已等时长差的就是两者之差；它只给人看，不驱动任何判定。投运时按现场实测改车上的门槛，这里跟着改。
/// </para>
/// <para>
/// 自己一个文件 <see cref="FileName"/>，写法照 <see cref="BlockedJourneyEscalationOptions"/>：环境变量
/// <c>ExpectedActionOverdue__threshold</c> 照样盖在文件之上；端点被发现时读、时校验，配错让服务起不来。
/// </para>
/// </remarks>
internal sealed record ExpectedActionOverdueOptions(TimeSpan Threshold)
{
    internal const string FileName = "expected-action-overdue.settings.json";
    internal const string SectionName = "ExpectedActionOverdue";

    internal static ExpectedActionOverdueOptions Default { get; } = new(TimeSpan.FromMinutes(6));

    internal static ExpectedActionOverdueOptions Load(string baseDirectory)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .SetBasePath(baseDirectory)
            .AddJsonFile(FileName, optional: true, reloadOnChange: false)
            .AddEnvironmentVariables()
            .Build();
        return From(configuration.GetSection(SectionName));
    }

    internal static ExpectedActionOverdueOptions From(IConfiguration section)
    {
        ArgumentNullException.ThrowIfNull(section);

        string? value = section["threshold"];
        if (string.IsNullOrWhiteSpace(value))
        {
            return Default;
        }
        if (!TimeSpan.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out TimeSpan threshold))
        {
            throw new InvalidDataException($"{SectionName}:threshold is not a time span: '{value}'.");
        }
        if (threshold <= TimeSpan.Zero)
        {
            throw new InvalidDataException($"{SectionName}:threshold must be positive.");
        }
        return new ExpectedActionOverdueOptions(threshold);
    }
}
