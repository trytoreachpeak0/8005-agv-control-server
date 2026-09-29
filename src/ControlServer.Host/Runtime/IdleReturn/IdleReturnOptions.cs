using ControlServer.Application;
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

    public bool IsBelowLine(int batteryPercent) => batteryPercent < _options.MinimumBatteryPercent;

    public string Describe() =>
        $"{_options.MinimumBatteryPercent} (transitional: {JourneyRuntimeOptions.SectionName}:{nameof(JourneyRuntimeOptions.MinimumBatteryPercent)})";
}
