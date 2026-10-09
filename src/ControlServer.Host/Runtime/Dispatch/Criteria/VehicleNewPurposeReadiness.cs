using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Host.Runtime.Dispatch.Criteria;

/// <summary>
/// 这辆车此刻能不能承接一个新用途——车辆这一侧的判定，派车与空闲返回共用（control-server#389 审查 M1）。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么共用。</b>派车链把「车能不能接活」拆在几条判据里（故障阻断 15、动态事实 80），空闲返回的资格另写一份时，
/// 故障那一条就被漏掉了：一辆被判疑似阻塞、甚至已确认隔离的车，派车链不给它活，空闲返回却承诺它开往等待点（准入线 1，
/// REQ-0291「受阻的车不改状态」）。两份各写各的会再漂一次，所以车辆侧的这几条只在这里定义，两边都来调。
/// </para>
/// <para>
/// 派车链仍是两条判据、顺序不变（结构性告警按判据顺序分类）：<see cref="VehicleFaultBlockCriterion"/> 调
/// <see cref="FaultVerdictAsync"/>，<see cref="VehicleDynamicFactsCriterion"/> 就是 <see cref="VehicleDynamicFactsCriterion.Evaluate"/>。
/// 空闲返回调 <see cref="JudgeAsync"/>，三者依次判。批次 9 的投运策略、强制充电、人工充电等待、充电资格暂停加在这里。
/// 投运策略（control-server#400）是第三格 <see cref="CommissioningVerdictAsync"/>，派车链那一侧是
/// <see cref="ChargingPolicyCommissioningCriterion"/>（Order 17）——同一个判定，每条链只判一次。
/// </para>
/// </remarks>
public static class VehicleNewPurposeReadiness
{
    /// <summary>故障阻断（REQ-0232、REQ-0234）：任一级别都不接新用途；身份解析不出也不接。能接答 <see cref="DispatchAdmissionChain.Eligible"/>。</summary>
    public static async Task<string> FaultVerdictAsync(
        IVehicleFaultStore faults, string agvId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(faults);
        if (string.IsNullOrWhiteSpace(agvId))
        {
            return VehicleFaultBlockCriterion.IdentityUnresolvedReason;
        }

        VehicleFaultFact? fault = await faults.ReadAsync(agvId, cancellationToken).ConfigureAwait(false);
        return fault?.Level switch
        {
            VehicleFaultLevel.ConfirmedIsolated => VehicleFaultBlockCriterion.IsolatedReason,
            VehicleFaultLevel.SuspectedBlocked => VehicleFaultBlockCriterion.SuspectedReason,
            _ => DispatchAdmissionChain.Eligible
        };
    }

    /// <summary>
    /// 故障阻断，然后门未证明的扣车（REQ-0364，control-server#385）：扣着的车不接任何新用途。派车链的故障阻断判据调它，
    /// <see cref="JudgeAsync"/> 也调它，所以搬运、空闲返回、充电走的是同一处。
    /// </summary>
    /// <remarks>
    /// 扣车也让会话不就绪（<c>WireToGateStore.DecideReadinessAsync</c>），动态事实那一格本来就会挡；这里单独判，是为了不靠
    /// 那条间接的路：原因码直说「被扣」，而且哪天就绪的算法变了、或有一条用途不看会话就绪，扣着的车照样派不出去（准入线 1）。
    /// </remarks>
    public static async Task<string> BlockVerdictAsync(
        IVehicleFaultStore faults, ControlServerDbContext dbContext, string agvId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        string fault = await FaultVerdictAsync(faults, agvId, cancellationToken).ConfigureAwait(false);
        if (fault != DispatchAdmissionChain.Eligible)
        {
            return fault;
        }

        return await dbContext.SlotDoorHolds.AsNoTracking()
            .AnyAsync(hold => hold.AgvId == agvId && hold.ReleasedAt == null, cancellationToken).ConfigureAwait(false)
            ? DispatchReasonCodes.VehicleSlotDoorHold
            : DispatchAdmissionChain.Eligible;
    }

    /// <summary>
    /// 投运（control-server#400；REQ-0282，规格 8.6 逐车硬阻断）：没有已批准、已激活、覆盖这辆车的充电策略版本，或读不到，
    /// 都不接新用途。能接答 <see cref="DispatchAdmissionChain.Eligible"/>，否则 <see cref="DispatchReasonCodes.ChargingPolicyNotApproved"/>；
    /// 判定本身一并交回，调用方要写日志时用它的原因与说明。
    /// </summary>
    /// <remarks>
    /// control-server#403：投运之外再判一条——生效版本的强制充电线不高于 <paramref name="rescueBatteryPercent"/>（服务端的救命告警线）时，
    /// 这一版视为不可用，答 <see cref="DispatchReasonCodes.ChargingPolicyEntryNotAboveRescueLine"/>。激活走 FieldOps、不经服务端，
    /// 所以这是服务端在「用」的时候唯一能拦的一处；读它的是派车两条链、空闲返回与充电分配，一处定义。
    /// </remarks>
    public static async Task<(string Verdict, VehicleChargingPolicyDecision Decision)> CommissioningVerdictAsync(
        IChargingPolicyResolver chargingPolicy, string vehicleKey, int rescueBatteryPercent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chargingPolicy);
        if (string.IsNullOrWhiteSpace(vehicleKey))
        {
            return (DispatchReasonCodes.ChargingPolicyNotApproved,
                new VehicleChargingPolicyDecision(vehicleKey ?? string.Empty, ChargingPolicyCommissioningReasons.NotApproved, null, "no vehicle key"));
        }

        VehicleChargingPolicyDecision decision =
            await chargingPolicy.ResolveForNewDecisionAsync(vehicleKey, cancellationToken).ConfigureAwait(false);
        if (decision.Effective is { } effective &&
            BatteryEligibility.EntryNotAboveRescueLine(effective.Policy.Content, rescueBatteryPercent))
        {
            return (DispatchReasonCodes.ChargingPolicyEntryNotAboveRescueLine, decision with
            {
                Detail = string.Create(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $"charging policy version {effective.Policy.Version} has MandatoryChargeEntryThreshold {effective.Policy.Content.MandatoryChargeEntryThresholdPercent}, not above JourneyRuntime:WaitingJourneyRescueBatteryPercent {rescueBatteryPercent}")
            });
        }

        return (decision.Commissioned ? DispatchAdmissionChain.Eligible : DispatchReasonCodes.ChargingPolicyNotApproved, decision);
    }

    /// <summary>故障阻断与门未证明扣车（<see cref="BlockVerdictAsync"/>），然后投运策略，然后车辆动态事实（安全、在线、绑定、IDLE、地图、新鲜、电量门槛、停止、RIoT 上没有它的单）。</summary>
    public static async Task<string> JudgeAsync(
        IVehicleFaultStore faults,
        ControlServerDbContext dbContext,
        IChargingPolicyResolver chargingPolicy,
        DispatchVehicleFacts facts,
        JourneyRuntimeOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(facts);
        string blocked = await BlockVerdictAsync(faults, dbContext, facts.AgvId, cancellationToken).ConfigureAwait(false);
        if (blocked != DispatchAdmissionChain.Eligible)
        {
            return blocked;
        }

        (string commissioning, _) = await CommissioningVerdictAsync(
                chargingPolicy, facts.VehicleKey, options.WaitingJourneyRescueBatteryPercent, cancellationToken)
            .ConfigureAwait(false);
        return commissioning != DispatchAdmissionChain.Eligible
            ? commissioning
            : VehicleDynamicFactsCriterion.Evaluate(facts, options);
    }

    /// <summary>
    /// 服务端持有的人工充电等待（批次9-06，control-server#404；<c>REQ-0171</c> 的退化路径，规格 8.6）：在等待中的车不接任何新用途——
    /// 搬运、空闲返回、自动充电都不接——出口只有「充电后返回服务」，电量回升本身不恢复资格。能接答
    /// <see cref="DispatchAdmissionChain.Eligible"/>，否则 <see cref="DispatchReasonCodes.VehicleInManualChargingHold"/>。
    /// </summary>
    /// <remarks>派车链那一侧是 <see cref="ChargingStandingCriterion"/>（Order 18），空闲返回在它的资格里调，充电分配在它的开头调：同一个读法。</remarks>
    public static async Task<string> ManualChargingHoldVerdictAsync(
        ControlServerDbContext dbContext, string vehicleKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentException.ThrowIfNullOrWhiteSpace(vehicleKey);
        return await dbContext.Set<ManualChargingHoldRow>().AsNoTracking()
            .AnyAsync(row => row.VehicleKey == vehicleKey, cancellationToken).ConfigureAwait(false)
            ? DispatchReasonCodes.VehicleInManualChargingHold
            : DispatchAdmissionChain.Eligible;
    }

    /// <summary>
    /// 这辆车此刻能不能承接<b>充电</b>这个新用途（批次9-06，control-server#404）：与派车、空闲返回同一份车辆侧判定，只有电量那一段反过来问——
    /// 它必须恰好是「低于强制充电线」，别的每一条照旧都要满足。能接答 <see cref="DispatchAdmissionChain.Eligible"/>，否则答挡住它的那一条的码。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>只认这一轮读定的那一份策略</b>（<see cref="DispatchVehicleFacts.BatteryPolicy"/>），不再问解析器：一轮派车对一辆车只认一个版本，
    /// 判它要不要充电的、搬运判它的、记到充电周期与旅程上的是同一份。没有策略答 <see cref="DispatchReasonCodes.ChargingPolicyNotApproved"/>，
    /// 强制充电线不高于救命线答 <see cref="DispatchReasonCodes.ChargingPolicyEntryNotAboveRescueLine"/>——策略坏了，充电也一样不接
    /// （两个码都出自 <see cref="VehicleDynamicFactsCriterion.Evaluate"/> 的电量一段，即 <see cref="BatteryEligibility.Judge"/>）。
    /// </para>
    /// <para>
    /// <b>怎么问「除电量之外」</b>：<see cref="VehicleDynamicFactsCriterion.Evaluate"/> 把电量判在中间（新鲜度之后，停稳与订单占用之前），
    /// 答了「要充电」就不往后判。所以问两次：照实问一次，必须答 <see cref="DispatchReasonCodes.MandatoryChargeRequired"/>（答别的就是别的挡着，
    /// 或它根本不需要充电）；再把电量换成一个任何余量都保得住的数问一次，那一次必须放行。判据本身一行不动，两条链永远不会各判各的。
    /// </para>
    /// </remarks>
    public static async Task<string> JudgeForChargingAsync(
        IVehicleFaultStore faults,
        ControlServerDbContext dbContext,
        DispatchVehicleFacts facts,
        JourneyRuntimeOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(facts);
        // control-server#456（合并 batch-p3/v3 与批次 9）：门未证明扣车也挡充电，与搬运、空闲返回走同一处（REQ-0364：扣着的车不接任何新用途）。
        string blocked = await BlockVerdictAsync(faults, dbContext, facts.AgvId, cancellationToken).ConfigureAwait(false);
        if (blocked != DispatchAdmissionChain.Eligible)
        {
            return blocked;
        }

        string asObserved = VehicleDynamicFactsCriterion.Evaluate(facts, options);
        return asObserved != DispatchReasonCodes.MandatoryChargeRequired
            ? asObserved
            : VehicleDynamicFactsCriterion.Evaluate(
                facts with { Vehicle = facts.Vehicle with { BatteryPercent = int.MaxValue } }, options);
    }
}
