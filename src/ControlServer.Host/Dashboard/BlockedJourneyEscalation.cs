using Microsoft.Extensions.Configuration;

namespace ControlServer.Host.Dashboard;

/// <summary>
/// 旅程阻断在看板上的升级档：谁该去处理它（program#55，规程
/// <c>8005-agv-program/docs/site-procedures/station-door-not-closed-escalation.md</c>）。
/// </summary>
/// <remarks>
/// 时长只转移岗位，不推送（REQ-0270）：挂上起算，满 10 分钟转班组长，满 30 分钟转维护管理员。
/// </remarks>
internal enum BlockedJourneyEscalationLevel
{
    Operator,
    ShiftLeader,
    MaintenanceAdministrator
}

/// <summary>
/// 两道升级线。服务端配置，放自己的文件 <see cref="FileName"/>，不进 <c>appsettings.json</c>。
/// </summary>
/// <remarks>
/// <para>
/// 自己一个文件，是因为 <c>appsettings.json</c> 与 <c>JourneyRuntimeOptions</c> 同批次还有别的票在改（control-server#84、#89），
/// 看板的两道线不值得和它们抢同一段。环境变量 <c>BlockedJourneyEscalation__shiftLeaderAfter</c> 这类写法照样盖在文件之上，
/// 与服务端其它设置的覆盖顺序一致。
/// </para>
/// <para>
/// 在端点被发现的那一刻读、那一刻校验：<see cref="DashboardQueryEndpointCatalog"/> 在服务启动时实例化端点，所以配错的线让服务
/// 起不来，而不是让看板每 2 秒报一次错。
/// </para>
/// </remarks>
internal sealed record BlockedJourneyEscalationOptions(TimeSpan ShiftLeaderAfter, TimeSpan MaintenanceAdministratorAfter)
{
    internal const string FileName = "blocked-journey-escalation.settings.json";
    internal const string SectionName = "BlockedJourneyEscalation";

    internal static BlockedJourneyEscalationOptions Default { get; } =
        new(TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(30));

    internal static BlockedJourneyEscalationOptions Load(string baseDirectory)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .SetBasePath(baseDirectory)
            .AddJsonFile(FileName, optional: true, reloadOnChange: false)
            .AddEnvironmentVariables()
            .Build();
        return From(configuration.GetSection(SectionName));
    }

    internal static BlockedJourneyEscalationOptions From(IConfiguration section)
    {
        ArgumentNullException.ThrowIfNull(section);

        BlockedJourneyEscalationOptions options = new(
            Read(section, "shiftLeaderAfter", Default.ShiftLeaderAfter),
            Read(section, "maintenanceAdministratorAfter", Default.MaintenanceAdministratorAfter));
        if (options.ShiftLeaderAfter <= TimeSpan.Zero)
        {
            throw new InvalidDataException($"{SectionName}:shiftLeaderAfter must be positive.");
        }
        if (options.MaintenanceAdministratorAfter <= options.ShiftLeaderAfter)
        {
            throw new InvalidDataException(
                $"{SectionName}:maintenanceAdministratorAfter must be longer than shiftLeaderAfter.");
        }
        return options;
    }

    private static TimeSpan Read(IConfiguration section, string key, TimeSpan fallback)
    {
        string? value = section[key];
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }
        return TimeSpan.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out TimeSpan parsed)
            ? parsed
            : throw new InvalidDataException($"{SectionName}:{key} is not a time span: '{value}'.");
    }

    /// <summary>
    /// 一条阻断现在归哪一档。
    /// </summary>
    /// <param name="blockedFor">挂了多久；开始时间没有记录时为 null。</param>
    /// <param name="carriesSessionSafety">这一行是否带会话的安全字段（只有 <c>ONBOARD_SESSION_NOT_READY</c> 带）。</param>
    /// <param name="safetyUnknownPresent">会话的 <c>SafetyUnknownPresent</c>；没有会话行时为 null。</param>
    /// <remarks>
    /// 两条直接进最高档、不等时长的情形，都是「看不出只是操作员走开了」：安全证据不全（<c>SafetyUnknownPresent</c> 不为
    /// <c>false</c>，包括根本没有会话行），以及挂上的时刻没有记录（这一列加上之前就挂着的阻断）——说不出挂了多久，就不能说它
    /// 还不到 10 分钟。边界取「满」：正好 10 分钟已归班组长。
    /// </remarks>
    internal BlockedJourneyEscalationLevel Classify(
        TimeSpan? blockedFor,
        bool carriesSessionSafety,
        bool? safetyUnknownPresent)
    {
        if (carriesSessionSafety && safetyUnknownPresent != false)
        {
            return BlockedJourneyEscalationLevel.MaintenanceAdministrator;
        }
        if (blockedFor is not TimeSpan elapsed)
        {
            return BlockedJourneyEscalationLevel.MaintenanceAdministrator;
        }
        return elapsed >= MaintenanceAdministratorAfter
            ? BlockedJourneyEscalationLevel.MaintenanceAdministrator
            : elapsed >= ShiftLeaderAfter
                ? BlockedJourneyEscalationLevel.ShiftLeader
                : BlockedJourneyEscalationLevel.Operator;
    }
}
