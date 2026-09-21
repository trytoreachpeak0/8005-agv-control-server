using Microsoft.Extensions.Options;

namespace ControlServer.Host.Runtime.Dispatch.Criteria;

/// <summary>
/// 在途车的动态事实（批次7-06，control-server#211）：安全、可用、在对的 Map、观测够新、电量够、绑定对得上——
/// 但<b>不要求它停着、不要求它没有订单</b>。
/// </summary>
/// <remarks>
/// <para>
/// <b>这不是把空闲车那条判据放宽，是另一条判据。</b><see cref="VehicleDynamicFactsCriterion"/> 要求 RIoT 报
/// <c>IDLE</c>、速度为零、没有在执行的订单——一辆正在跑自己那趟旅程的车，这三条永远不成立，而那正是它该有的样子。
/// 把那条判据加个「在途就跳过」的分支，会让两种车的准入在同一段代码里纠缠：改一处的人很难看出自己动的是哪一种车，
/// 而这里每一条都是对在途车问的，读的人不必先分辨。
/// </para>
/// <para>
/// <b>两条链各自守什么</b>（PR 里那张对照表的一半）：共用的是安全、连接、启用、绑定、Map、观测新鲜度、电量；
/// 空闲链另有 <c>IDLE</c>、速度为零、无订单占用；在途链另有 <see cref="EnRouteAppendCriterion"/> 的四道追加门。
/// </para>
/// <para>
/// <b>车上没有车载端事实时拒绝，与空闲链一致。</b>仓位判据要在它之上选目标仓位，而一辆读不到车载端事实的车，
/// 它这一侧还剩几个空仓位是不知道的，不是零。
/// </para>
/// </remarks>
public sealed class InTransitVehicleFactsCriterion(IOptions<JourneyRuntimeOptions> options)
    : IDispatchAdmissionCriterion
{
    private readonly JourneyRuntimeOptions _options = options.Value;

    /// <summary>与空闲链上 <see cref="VehicleDynamicFactsCriterion"/> 同一个位置，两条链因此形状一致。</summary>
    public int Order => 80;

    public Task<string> EvaluateAsync(DispatchCandidateEvaluation evaluation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        _ = cancellationToken;
        return Task.FromResult(Evaluate(evaluation.Vehicle, _options));
    }

    /// <summary>同一个裁决，链外也能问——与空闲链那条一样，是为了不让两处判定走岔。</summary>
    public static string Evaluate(DispatchVehicleFacts facts, JourneyRuntimeOptions options)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(options);

        if (facts.Onboard is null)
        {
            return "ONBOARD_FACTS_NOT_READY";
        }

        // 在途车的「安全」只问没上过锁的那几样：目标仓位是否全部上锁，说的是这一趟已经装好的货，
        // 追加一条需求不改变它，也不该被它挡住。未知存在与解锁输出未复位则仍然挡——那是车本身处在一个
        // 说不清的状态里。
        if (!facts.Onboard.DepartureSafe || !facts.Onboard.AllUnlockOutputsReset || facts.Onboard.UnknownPresent)
        {
            return "ONBOARD_DEPARTURE_UNSAFE";
        }

        if (!facts.Vehicle.Connected || !facts.Vehicle.Enabled)
        {
            return "RIOT_VEHICLE_NOT_AVAILABLE";
        }

        if (!string.Equals(facts.Vehicle.VehicleKey, facts.VehicleKey, StringComparison.Ordinal))
        {
            return "RIOT_VEHICLE_BINDING_MISMATCH";
        }

        if (!string.Equals(facts.Vehicle.CurrentMap, options.MapIdentity, StringComparison.Ordinal))
        {
            return "RIOT_VEHICLE_MAP_MISMATCH";
        }

        // 未来的时间戳与太旧的一样不可用：两边的钟对不上，算出来的「新鲜度」就什么都证明不了。
        if (facts.Vehicle.ObservedAt > facts.ObservedAt ||
            facts.ObservedAt - facts.Vehicle.ObservedAt > options.MaximumEvidenceAge)
        {
            return "RIOT_VEHICLE_FACT_STALE";
        }

        if (facts.Vehicle.BatteryPercent is null || string.IsNullOrWhiteSpace(facts.Vehicle.BatteryState))
        {
            return "BATTERY_FACT_UNKNOWN";
        }

        if (string.Equals(facts.Vehicle.BatteryState, "CHARGING", StringComparison.Ordinal) ||
            facts.Vehicle.BatteryPercent < options.MinimumBatteryPercent)
        {
            return "BATTERY_POLICY_NOT_SATISFIED";
        }

        return DispatchAdmissionChain.Eligible;
    }
}
