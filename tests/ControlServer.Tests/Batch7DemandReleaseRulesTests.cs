using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Dispatch.Criteria;
using ControlServer.Host.Runtime.Release;

namespace ControlServer.Tests;

/// <summary>
/// REQ-0328 的触发与裁决（批次7-10，control-server#215）：这辆车对这条需求还合不合格，这条需求此刻能不能释放。
/// </summary>
/// <remarks>
/// <para>
/// 触发的每一条用例都从「一辆完全合格的车」出发，只破坏一样事实：一个把某条规则写反或写漏的实现，恰好让那一条红。
/// 反例同样重要——瞬时事实（观测过期、读不到）与分区级事实（本区一辆车都没配）<b>不</b>触发，否则车一抖需求就被抢走。
/// </para>
/// <para>
/// 裁决按票面与调度决策逐条对应：集合 A 不走本条（票面第 4 条）；到站后不释放（决策 8）；当前下一站带着别的需求不释放
/// （本票限度）；锚需求带着别的未结需求不释放（决策 4）；只剩它、车正开往它的取货站时先取消订单再释放。
/// </para>
/// </remarks>
public sealed class Batch7DemandReleaseRulesTests
{
    private const string AgvId = "agv-01";
    private const string OtherAgv = "agv-02";
    private const string TaskType = "WIRE_TO_GATE";
    private const string Zone = "MAP-25-WIRE_TO_GATE";
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 8, 0, 0, TimeSpan.Zero);

    private static readonly JourneyRuntimeOptions Options = new()
    {
        MapId = 25,
        MapIdentity = "MAP-25",
        MaximumEvidenceAge = TimeSpan.FromSeconds(30),
    };

    [Fact]
    public void AFullyEligibleVehicleIsNotATrigger() =>
        Assert.Null(Trigger());

    [Theory]
    [InlineData(VehicleFaultLevel.ConfirmedIsolated, VehicleFaultBlockCriterion.IsolatedReason)]
    [InlineData(VehicleFaultLevel.SuspectedBlocked, VehicleFaultBlockCriterion.SuspectedReason)]
    public void AFaultOnTheVehicleIsATrigger(VehicleFaultLevel level, string expected) =>
        Assert.Equal(expected, Trigger(fault: Fault(level)));

    [Fact]
    public void AClearedFaultIsNotATrigger() =>
        Assert.Null(Trigger(fault: Fault(VehicleFaultLevel.None)));

    [Fact]
    public void ATaskTypeAdmissionWithdrawnFromThisVehicleIsATrigger() =>
        Assert.Equal(
            VehicleTaskTypeAdmissionCriterion.TaskTypeNotAdmittedReason,
            Trigger(policy: Policy(taskTypes: ["STAGING_TO_WIRE"])));

    [Fact]
    public void AVehicleDroppedFromTheDispatchPolicyIsATrigger() =>
        Assert.Equal(
            VehicleTaskTypeAdmissionCriterion.VehicleNotInPolicyReason,
            Trigger(policy: Policy(policyVehicle: OtherAgv)));

    [Fact]
    public void AZoneAdmissionWithdrawnFromThisVehicleIsATrigger() =>
        Assert.Equal(
            DispatchZoneVehicleCriterion.VehicleNotInZoneReason,
            Trigger(policy: Policy(zoneVehicles: [OtherAgv])));

    /// <summary>本区一辆车都没配是分区的事实：换哪辆车都一样不合格，需求本身就派不出去，不是「只有这辆车不合格」。</summary>
    [Fact]
    public void AZoneWithNoVehiclesAtAllIsNotATrigger() =>
        Assert.Null(Trigger(policy: Policy(zoneConfigured: false)));

    [Fact]
    public void AFreshObservationOnAnotherMapIsATrigger() =>
        Assert.Equal(DemandReleaseRules.MapMismatchReason, Trigger(observation: Observation("MAP-26", Now.AddSeconds(-5))));

    /// <summary>过期的观测说的是过去的地图；在边界上（恰好等于最大年龄）仍算新鲜，多一刻就不算——两条一起钉住比较方向。</summary>
    [Fact]
    public void AnObservationOnAnotherMapCountsOnlyWhileItIsFresh()
    {
        Assert.Equal(
            DemandReleaseRules.MapMismatchReason,
            Trigger(observation: Observation("MAP-26", Now - Options.MaximumEvidenceAge)));
        Assert.Null(Trigger(observation: Observation("MAP-26", Now - Options.MaximumEvidenceAge - TimeSpan.FromTicks(1))));
    }

    /// <summary>来自未来的观测与过期的一样不可用：两边的钟对不上，它证明不了任何事。</summary>
    [Fact]
    public void AnObservationFromTheFutureIsNotATrigger() =>
        Assert.Null(Trigger(observation: Observation("MAP-26", Now.AddSeconds(1))));

    [Fact]
    public void NoObservationIsNotATrigger() =>
        Assert.Null(Trigger(observation: null, observe: false));

    /// <summary>
    /// 一次失败的读取不是车辆的事实（审查 S1）。<c>HttpRiotMovementGateway</c> 遇到任何 SDK 失败都不抛，而是返回
    /// <c>UnknownVehicle</c>：离线、地图为空、观测时刻取「此刻」——按新鲜度它是新鲜的，按地图它与本图不同。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 保证放在规则的入口：观测读不到（为空）或车不在线时，<b>任何</b>判据都不判「不再合格」。所以这里让那个形状
    /// 与每一条会触发的事实各配一次，而不是只测地图——只在地图判据里防，下一条新判据会把它忘掉。
    /// </para>
    /// <para>
    /// 每一行的「触发事实」单独配一辆在线的车时确实会触发（下面 <see cref="EachBreakerTriggersWhileTheVehicleIsOnline"/>），
    /// 否则这里的「不触发」可能只是那条事实本来就不触发。
    /// </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(Breakers))]
    public void AFailedReadNeverMakesTheVehicleIneligible(string breaker)
    {
        (VehicleFaultFact? fault, VehicleDispatchPolicy? policy, string? map) = Break(breaker);
        Assert.Null(Trigger(fault: fault, policy: policy, observation: UnknownVehicle(map)));
        Assert.Null(Trigger(fault: fault, policy: policy, observation: null, observe: false));
    }

    [Theory]
    [MemberData(nameof(Breakers))]
    public void EachBreakerTriggersWhileTheVehicleIsOnline(string breaker)
    {
        (VehicleFaultFact? fault, VehicleDispatchPolicy? policy, string? map) = Break(breaker);
        Assert.NotNull(Trigger(
            fault: fault, policy: policy, observation: Observation(map ?? Options.MapIdentity, Now.AddSeconds(-5))));
    }

    /// <summary>车在线但报了一个空地图：空不是「另一张图」，说不清就不是释放的理由。</summary>
    [Fact]
    public void AnEmptyMapIsNotAnotherMap() =>
        Assert.Null(Trigger(observation: Observation(string.Empty, Now.AddSeconds(-5))));

    public static TheoryData<string> Breakers => new()
    {
        "fault-isolated", "fault-suspected", "task-type-withdrawn", "not-in-policy", "zone-withdrawn", "another-map",
    };

    private static (VehicleFaultFact? Fault, VehicleDispatchPolicy? Policy, string? Map) Break(string breaker) => breaker switch
    {
        "fault-isolated" => (Fault(VehicleFaultLevel.ConfirmedIsolated), null, null),
        "fault-suspected" => (Fault(VehicleFaultLevel.SuspectedBlocked), null, null),
        "task-type-withdrawn" => (null, Policy(taskTypes: ["STAGING_TO_WIRE"]), null),
        "not-in-policy" => (null, Policy(policyVehicle: OtherAgv), null),
        "zone-withdrawn" => (null, Policy(zoneVehicles: [OtherAgv]), null),
        "another-map" => (null, null, "MAP-26"),
        _ => throw new ArgumentOutOfRangeException(nameof(breaker), breaker, null),
    };

    /// <summary>
    /// 与 <c>HttpRiotMovementGateway.UnknownVehicle</c> 逐字段相同的形状（离线、未启用、UNKNOWN、地图为空、观测时刻为此刻），
    /// 只有 <paramref name="map"/> 让「另一张图」那一行也带着一个非空的地图进来——离线的车报什么图都不算数。
    /// </summary>
    private static RiotVehicleObservation UnknownVehicle(string? map) =>
        new("BROKERX-0001", false, false, "UNKNOWN", map ?? string.Empty, null, null, null, null, Now);

    // ---- 裁决 ------------------------------------------------------------------------------------

    [Theory]
    [InlineData(JourneyDemandStatuses.Loading)]
    [InlineData(JourneyDemandStatuses.Loaded)]
    [InlineData(JourneyDemandStatuses.Unloaded)]
    [InlineData(JourneyDemandStatuses.Terminated)]
    public void ADemandThatIsNotWaitingToBeLoadedIsNotReleased(string status) =>
        Assert.Equal(
            DemandReleaseDecision.NotApplicable,
            DemandReleaseRules.Decide(status, isAnchor: false, otherOpenDemands: 0, pickupIsCurrentNextStop: false,
                arrivedAtCurrentNextStop: false));

    [Fact]
    public void ADemandWhoseStopIsFurtherAlongIsReleasedWithoutAnOrder() =>
        Assert.Equal(
            DemandReleaseDecision.ReleaseWithoutOrder,
            Decide(isAnchor: false, otherOpenDemands: 1, pickupIsCurrentNextStop: false, arrived: false));

    [Fact]
    public void TheLastDemandOnItsWayToItsPickupHasItsOrderCancelledFirst() =>
        Assert.Equal(
            DemandReleaseDecision.CancelPickupOrderThenRelease,
            Decide(isAnchor: true, otherOpenDemands: 0, pickupIsCurrentNextStop: true, arrived: false));

    [Fact]
    public void OnceTheVehicleHasArrivedNothingIsReleased() =>
        Assert.Equal(
            DemandReleaseDecision.Refuse(DemandReleaseReasons.AfterArrival),
            Decide(isAnchor: true, otherOpenDemands: 0, pickupIsCurrentNextStop: true, arrived: true));

    [Fact]
    public void TheCurrentStopIsNotReleasedWhileOtherDemandsRide() =>
        Assert.Equal(
            DemandReleaseDecision.Refuse(DemandReleaseReasons.CurrentStopWithOtherDemands),
            Decide(isAnchor: false, otherOpenDemands: 1, pickupIsCurrentNextStop: true, arrived: false));

    [Fact]
    public void AnAnchorIsNotReleasedWhileOtherDemandsAreOpen() =>
        Assert.Equal(
            DemandReleaseDecision.Refuse(DemandReleaseReasons.AnchorWithOtherDemands),
            Decide(isAnchor: true, otherOpenDemands: 1, pickupIsCurrentNextStop: false, arrived: false));

    /// <summary>「到站」只对当前下一站有意义：车到了别的站，更后面那条需求照样可以释放。</summary>
    [Fact]
    public void ArrivalElsewhereDoesNotHoldADemandFurtherAlong() =>
        Assert.Equal(
            DemandReleaseDecision.ReleaseWithoutOrder,
            Decide(isAnchor: false, otherOpenDemands: 1, pickupIsCurrentNextStop: false, arrived: true));

    private static DemandReleaseDecision Decide(bool isAnchor, int otherOpenDemands, bool pickupIsCurrentNextStop, bool arrived) =>
        DemandReleaseRules.Decide(
            JourneyDemandStatuses.PendingLoad, isAnchor, otherOpenDemands, pickupIsCurrentNextStop, arrived);

    private static string? Trigger(
        VehicleFaultFact? fault = null,
        VehicleDispatchPolicy? policy = null,
        RiotVehicleObservation? observation = null,
        bool observe = true) =>
        DemandReleaseRules.VehicleNoLongerEligible(
            fault,
            policy ?? Policy(),
            AgvId,
            TaskType,
            Zone,
            observe ? observation ?? Observation(Options.MapIdentity, Now.AddSeconds(-5)) : null,
            Now,
            Options);

    private static VehicleFaultFact Fault(VehicleFaultLevel level) =>
        new(AgvId, level, 1, "ORDER_FAILED", false, Now.AddMinutes(-1), Now, false, null, null, null, null);

    private static VehicleDispatchPolicy Policy(
        string policyVehicle = AgvId,
        string[]? taskTypes = null,
        string[]? zoneVehicles = null,
        bool zoneConfigured = true) =>
        new(
            [new VehicleDispatchProfile(policyVehicle, new HashSet<string>(taskTypes ?? [TaskType], StringComparer.Ordinal), 5000)],
            zoneConfigured
                ? new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
                {
                    [Zone] = new HashSet<string>(zoneVehicles ?? [AgvId], StringComparer.Ordinal)
                }
                : new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal),
            "policy-1");

    private static RiotVehicleObservation Observation(string map, DateTimeOffset observedAt) =>
        new("BROKERX-0001", true, true, "EXECUTING", map, 12, 80, "DISCHARGING", 0.5, observedAt);
}
