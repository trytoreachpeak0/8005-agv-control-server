using ControlServer.Application;
using ControlServer.Host.Runtime.Dispatch.Criteria;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ControlServer.Host.Runtime.IdleReturn;

/// <summary>
/// 空闲返回的开关（<c>REQ-0291</c>；批次8-18，control-server#389）。配置节 <c>IdleReturn</c>，不在 <c>appsettings.json</c> 里：
/// 不配即关。
/// </summary>
/// <remarks>
/// <para>
/// <b>默认关，而且在批次8-19（control-server#390）合入之前任何环境都不得打开。</b>本票只形成承诺（用途占有与等待点预占），
/// 不建单也不执行：打开它，车会一直占着 <c>IDLE_RETURN</c>、不再接搬运，却哪儿也不去。翻不翻默认由批次8-19 决定。
/// </para>
/// <para>
/// 关掉只阻止新承诺，不取消、不改写既有的承诺（<c>REQ-0291</c>）。打开、或等待点登记激活新版本，下一轮派车即对所有当前满足条件的车重评：
/// 评估每一轮都做，所以「立即重评」不需要另外的触发。
/// </para>
/// </remarks>
public sealed class IdleReturnOptions
{
    public const string SectionName = "IdleReturn";

    /// <summary>是否形成新的空闲返回承诺。默认关。</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// 只给合成 L2 用、不得上现场：确认「打开了也没有执行」——承诺形成之后没人建单、没人释放，车会一直占着 <c>IDLE_RETURN</c>。
    /// 只有 <c>scripts/l2/Invoke-L2Scenario.ps1</c> 设它（审查 S2）；现场配置与安装脚本里出现它由
    /// <c>IdleReturnCommitmentTests.TheL2OnlyKeyAppearsInNoSiteConfigurationOrInstallScript</c> 拦。由批次8-19（control-server#390）
    /// 连同 <see cref="IdleReturnOptionsValidator"/> 一起删掉。
    /// </summary>
    public bool AllowWithoutExecutionForL2Only { get; set; }
}

/// <summary>
/// 打开了、而且是合成 L2 那种打开时，启动打一条 Warning（事件 2201）：承诺在批次8-19 合入前不会被执行、也不会被释放。
/// </summary>
public sealed class IdleReturnStartupWarning(IOptions<IdleReturnOptions> options, ILogger<IdleReturnStartupWarning> logger)
    : Microsoft.Extensions.Hosting.IHostedService
{
    private static readonly Action<ILogger, Exception?> LogEnabledWithoutExecution = LoggerMessage.Define(
        LogLevel.Warning,
        new EventId(2201, nameof(LogEnabledWithoutExecution)),
        "Idle return is enabled with IdleReturn:AllowWithoutExecutionForL2Only: commitments made before control-server#390 " +
        "are never executed nor released, and a committed vehicle takes no transport until the database is edited. " +
        "This is for the synthetic L2 rig only.");

    public Task StartAsync(CancellationToken cancellationToken)
    {
        IdleReturnOptions current = options.Value;
        if (current.Enabled && current.AllowWithoutExecutionForL2Only)
        {
            LogEnabledWithoutExecution(logger, null);
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// 过渡期的启动护栏（审查 S2）：批次8-19 合入之前，<c>IdleReturn:Enabled=true</c> 而没有 <c>AllowWithoutExecutionForL2Only</c> 就拒绝启动。
/// </summary>
/// <remarks>
/// <para>
/// 打开之后承诺永不释放（用途占有的释放在生产代码里没有调用方，那是批次8-19 的离点与失败分流），车被永久挡住、只能改库——
/// 准入线第 3 条。一个会在现场这样坏掉的配置，要在启动时被拒，而不是写一句「不得打开」等人记得。
/// </para>
/// <para>
/// 配置键不分大小写（.NET 配置），所以文本扫描用例按不分大小写扫。它覆盖不到仓库之外：机器级环境变量
/// <c>IdleReturn__AllowWithoutExecutionForL2Only</c> 设在现场机器上时，这道护栏就被绕过——那只能靠现场配置不写它。
/// </para>
/// </remarks>
public sealed class IdleReturnOptionsValidator : IValidateOptions<IdleReturnOptions>
{
    public const string RefusalMessage =
        "IdleReturn:Enabled must stay off until control-server#390 (idle return execution) is merged: a commitment made " +
        "now is never executed nor released, and the vehicle stays held by IDLE_RETURN until the database is edited. " +
        "Only the synthetic L2 rig sets IdleReturn:AllowWithoutExecutionForL2Only.";

    public ValidateOptionsResult Validate(string? name, IdleReturnOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.Enabled && !options.AllowWithoutExecutionForL2Only
            ? ValidateOptionsResult.Fail(RefusalMessage)
            : ValidateOptionsResult.Success;
    }
}

/// <summary>
/// 强制充电入口线（<see cref="IMandatoryChargeLine"/>）按车读充电策略版本的实现（批次9-05，control-server#403；替换 cs#389 的过渡实现）。
/// </summary>
/// <remarks>
/// <para>
/// 线是这辆车此刻做新决定用的那一版策略的 <c>MandatoryChargeEntryThreshold</c>（<see cref="IChargingPolicyResolver.ResolveForNewDecisionAsync"/>），
/// 比较是 <see cref="BatteryEligibility.IsMandatoryCharge"/>——与派车链判 <c>MANDATORY_CHARGE_REQUIRED</c> 的是同一个函数，所以一辆车不会
/// 「搬运挡住了、空闲返回却放它走」。
/// </para>
/// <para>
/// <b>读不到策略按低于线答。</b>没有已批准版本的车在前面的投运一格已被拒（<c>CHARGING_POLICY_NOT_APPROVED</c>），走不到这里；
/// 若走到了（轮中策略被撤），宁可让它原地不动，也不承诺它开往等待点。
/// </para>
/// <para>
/// 作用域：每一轮派车一个实例（解析器读库）。<see cref="Describe"/> 给出本实例最近一次为这辆车读到的线与版本号。
/// </para>
/// </remarks>
public sealed class PolicyMandatoryChargeLine(IChargingPolicyResolver chargingPolicy) : IMandatoryChargeLine
{
    private readonly IChargingPolicyResolver _chargingPolicy =
        chargingPolicy ?? throw new ArgumentNullException(nameof(chargingPolicy));

    private readonly Dictionary<string, string> _described = new(StringComparer.Ordinal);

    public async ValueTask<bool> IsBelowLineAsync(string vehicleKey, int batteryPercent, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vehicleKey);
        VehicleChargingPolicyDecision decision =
            await _chargingPolicy.ResolveForNewDecisionAsync(vehicleKey, cancellationToken).ConfigureAwait(false);
        if (decision.Effective is not { } effective)
        {
            _described[vehicleKey] = $"unknown ({decision.Reason}: no approved charging policy covers this vehicle)";
            return true;
        }

        ChargingPolicyContent policy = effective.Policy.Content;
        _described[vehicleKey] = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{policy.MandatoryChargeEntryThresholdPercent} (charging policy version {effective.Policy.Version}: MandatoryChargeEntryThreshold)");
        return BatteryEligibility.IsMandatoryCharge(batteryPercent, policy);
    }

    public string Describe(string vehicleKey) =>
        _described.TryGetValue(vehicleKey, out string? described) ? described : "not read yet";
}
