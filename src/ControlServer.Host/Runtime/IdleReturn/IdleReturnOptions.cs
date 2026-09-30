using ControlServer.Application;
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
/// 强制充电入口线的过渡实现：读 <see cref="JourneyRuntimeOptions.MinimumBatteryPercent"/>，与搬运的电量门槛是同一个值。
/// </summary>
/// <remarks>
/// 选它的理由：这个值今天已经挡住低电量的车接搬运（<c>VehicleDynamicFactsCriterion</c> 的 <c>BATTERY_POLICY_NOT_SATISFIED</c>，
/// 判据是「低于它即拒」），空闲返回用同一条线、同一个比较，低电量的车就既不接搬运也不开往等待点，停在原地——不会有一辆低电量的车
/// 被派去干活。另立一个值会让两条线可能错开，错开的那一段就是「搬运挡住了、空闲返回却放它走」。批次 9 替换这个实现。
/// </remarks>
public sealed class TransitionalMandatoryChargeLine(IOptions<JourneyRuntimeOptions> options) : IMandatoryChargeLine
{
    private readonly JourneyRuntimeOptions _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;

    public ValueTask<bool> IsBelowLineAsync(string vehicleKey, int batteryPercent, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vehicleKey);
        return ValueTask.FromResult(batteryPercent < _options.MinimumBatteryPercent);
    }

    public string Describe(string vehicleKey) =>
        $"{_options.MinimumBatteryPercent} (transitional: {JourneyRuntimeOptions.SectionName}:{nameof(JourneyRuntimeOptions.MinimumBatteryPercent)})";
}
