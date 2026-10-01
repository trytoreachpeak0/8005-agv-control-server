using Microsoft.Extensions.Options;

namespace ControlServer.Host.Runtime.Charging;

/// <summary>
/// 人工清桩这个出口此刻是否真的有人走得通（control-server#406 独立审查必修 M1）。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么要问</b>：「已确认充不上」是引擎自动形成的。形成之后车进清桩中，唯一的出口是有 R-11／R-13 的人确认清桩；清桩中的分支对
/// 「人在 RIoT 里取消旧单」不作反应（不重建、不收尾）。出口走不通时形成它，车与桩就永远挂着——而原来的 <c>ORDER_HANG</c> 至少还有 RIoT 那一侧的出口。
/// 所以出口不可用时不形成确认，照旧写 <c>ORDER_HANG</c>，并告警一次说为什么（事件 2271，在引擎里）；启动时也看一次（事件 2272，<see cref="LogAtStartup"/>）。
/// </para>
/// <para>
/// <b>可用的意思</b>，两样都要：
/// <list type="number">
/// <item>名单可读、且至少有一个具名的人持有 R-11 或 R-13（<see cref="FieldOperatorRoleRoster.AnyoneHolds"/>）——否则任何确认都被拒；</item>
/// <item>至少有一个走得通的入口：Host 的清桩入口已映射（<c>VehicleFaultRecovery:enabled</c>，与 <see cref="ChargingStationEndpoints.MapChargingStationEntries"/>
/// 同一个判断）<b>并且</b>它的凭据变量有值（没有值时入口每次都答 503，审查 N2），或部署方声明了车载端开着入口
/// （<see cref="FieldOperatorRoleOptions.OnboardClearanceEntryDeclared"/>；协议里没有能读到它的字段）。</item>
/// </list>
/// Host 入口那一项<b>在构造时取一次</b>（宿主里是单例，启动时自检就构造了它）：路由只在启动时按当时的配置映射，之后热重载改了开关，路由也不会跟着挂上或撤下，
/// 判定就不能现读（审查 N2）。名单文件每次现读，改了名单不用重启。
/// </para>
/// </remarks>
public sealed class StationClearanceExit(
    FieldOperatorRoleRoster roster,
    IOptions<FieldOperatorRoleOptions> options,
    IConfiguration configuration,
    IOptions<VehicleFaultRecoveryOptions> recoveryOptions)
{
    // Taken once, as the route was mapped once (review N2).
    private readonly bool _hostEntryOffered =
        configuration.GetValue<bool>(VehicleFaultRecoveryEndpoints.EnabledKey) &&
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(recoveryOptions.Value.CredentialEnvironmentVariable));

    /// <summary>名单没配、读不到、为空，或没有人持有 R-11／R-13。</summary>
    public const string RosterEmpty = "FIELD_OPERATOR_ROSTER_EMPTY";

    /// <summary>Host 入口没映射或没有凭据，也没声明车载端入口。</summary>
    public const string NoEntry = "STATION_CLEARANCE_ENTRY_NOT_OFFERED";

    private static readonly Action<ILogger, string, Exception?> LogUnavailableAtStartup =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(2272, nameof(LogUnavailableAtStartup)),
            "The manual station clearance exit is not available ({Reasons}). Until it is, a vehicle that cannot charge at its " +
            "charger is left on ORDER_HANG instead of being paused for a manual clearance (control-server#406). Configure " +
            "FieldOperatorRoles:Path with at least one R-11 or R-13, and VehicleFaultRecovery:enabled or " +
            "FieldOperatorRoles:OnboardClearanceEntryDeclared.");

    /// <summary>Host 的放宽类入口没映射或没有凭据：桩的恢复确认与车的充电资格恢复都走不通（control-server#407）。</summary>
    public const string NoRecoveryEntry = "CHARGING_RECOVERY_ENTRY_NOT_OFFERED";

    /// <summary>
    /// 中断与无进展的隔离此刻能不能写（control-server#407）：人工清桩出口可用（<see cref="Unavailable"/>），<b>并且</b> Host 的恢复入口可用——
    /// 隔离之后桩与车都只能经它恢复。不能写答原因，能写答空。
    /// </summary>
    public string? IsolationUnavailable()
    {
        string? exit = Unavailable();
        return _hostEntryOffered ? exit : exit is null ? NoRecoveryEntry : exit + "," + NoRecoveryEntry;
    }

    /// <summary>为什么不可用（一个或两个原因码，逗号分隔）；可用答空。</summary>
    public string? Unavailable()
    {
        List<string> reasons = [];
        if (!roster.AnyoneHolds(FieldOperatorRoleRoster.StationClearanceRoles))
        {
            reasons.Add(RosterEmpty);
        }
        if (!_hostEntryOffered && !options.Value.OnboardClearanceEntryDeclared)
        {
            reasons.Add(NoEntry);
        }
        return reasons.Count == 0 ? null : string.Join(',', reasons);
    }

    /// <summary>启动时看一次：跑旅程的服务端出口不可用就告警（不拒绝启动——不可用时的退路是照旧的 <c>ORDER_HANG</c>）。</summary>
    public static void LogAtStartup(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (!services.GetRequiredService<IOptions<JourneyRuntimeOptions>>().Value.Enabled)
        {
            return;
        }
        if (services.GetRequiredService<StationClearanceExit>().Unavailable() is { } reasons)
        {
            LogUnavailableAtStartup(services.GetRequiredService<ILogger<StationClearanceExit>>(), reasons, null);
        }
    }
}
