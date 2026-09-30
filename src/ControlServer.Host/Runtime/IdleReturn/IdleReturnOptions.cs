using ControlServer.Application;
using ControlServer.Host.Runtime.Dispatch.Criteria;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ControlServer.Host.Runtime.IdleReturn;

/// <summary>
/// 空闲返回的开关（<c>REQ-0291</c>；批次8-18 control-server#389 立，批次8-19 control-server#390 接上执行）。配置节 <c>IdleReturn</c>，
/// 不在 <c>appsettings.json</c> 里：不配即关。
/// </summary>
/// <remarks>
/// <para>
/// <b>默认仍是关。</b>批次8-19 合入之后，承诺会被执行：物化成旅程、过出发前安全门、建单、开往等待点、收敛、离点释放，失败按
/// <c>REQ-0296</c> 分流，孤儿承诺自动释放——打开它不再会把车锁死，所以批次8-18 那道「单独打开即拒绝启动」的过渡护栏与只给合成 L2 的
/// 确认键一起删了。但打开它会让车在没有需求时自己开往等待点，那是会让车动的事：什么时候在哪个环境打开、要不要翻成默认开，
/// 由调度按上车部署窗口定，第一次在现场打开按「Ask first」第 1 类逐次授权。
/// </para>
/// <para>
/// 关掉只阻止新承诺，不取消、不改写既有的承诺（<c>REQ-0291</c>）：已形成的承诺照常物化、执行到收敛或失败收尾。打开、或等待点登记激活新版本，
/// 下一轮派车即对所有当前满足条件的车重评：评估每一轮都做，所以「立即重评」不需要另外的触发。
/// </para>
/// </remarks>
public sealed class IdleReturnOptions
{
    public const string SectionName = "IdleReturn";

    /// <summary>是否形成新的空闲返回承诺。默认关。</summary>
    public bool Enabled { get; set; }
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
