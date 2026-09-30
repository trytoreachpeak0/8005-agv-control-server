using ControlServer.Application;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Dispatch;
using ControlServer.Host.Runtime.Dispatch.Criteria;

namespace ControlServer.Tests;

/// <summary>
/// 电量资格、强制充电判定与 <c>batteryState</c> 映射（批次9-05，control-server#403；REQ-0208 电量那一半、REQ-0281、REQ-0290）：
/// 派车两条链的公开入口（<see cref="VehicleDynamicFactsCriterion.Evaluate"/>、<see cref="InTransitVehicleFactsCriterion.Evaluate"/>）逐个边界，
/// 以及映射表的四个值。
/// </summary>
/// <remarks>
/// 策略取值刻意让两道边界分开：入口线 30、余量 20、每趟估计 10。于是「电量 = 入口线」与「电量 − 估计 = 余量」落在同一个 30 上，
/// 29 只违反入口线；另一组入口线 20、余量 20、估计 15，35 − 15 = 20 恰好保住，34 只违反余量——每条边界各自一个原因码。
/// </remarks>
public sealed class BatteryThresholdEligibilityTests
{
    private const string VehicleKey = "BROKERX-0001";
    private const string Map = "MAP-26";
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 6, 0, 0, TimeSpan.Zero);

    private static readonly ChargingPolicyContent EntryBoundary = TestChargingPolicies.Content with
    {
        MandatoryChargeEntryThresholdPercent = 30,
        MinimumPostTaskBatteryMarginPercent = 20,
        EstimatedTaskConsumptionPercent = 10,
    };

    private static readonly ChargingPolicyContent MarginBoundary = TestChargingPolicies.Content with
    {
        MandatoryChargeEntryThresholdPercent = 20,
        MinimumPostTaskBatteryMarginPercent = 20,
        EstimatedTaskConsumptionPercent = 15,
    };

    /// <summary>
    /// 边界逐一，每条一个原因码：入口线与入口线 − 1、余量与余量 − 1、电量缺失、电池状态缺失、充电中、没有策略。空闲链与在途链对同一份事实
    /// 答同一个拒绝码——在途链这里读不出计划，按保守的两趟算，所以合格的两条与余量那两条只对空闲链问（在途链的余量见下一条）。
    /// </summary>
    [Theory]
    [InlineData("entry", 30, "NO_CHARGE", DispatchAdmissionChain.Eligible)]
    [InlineData("entry", 29, "NO_CHARGE", DispatchReasonCodes.MandatoryChargeRequired)]
    [InlineData("margin", 35, "NO_CHARGE", DispatchAdmissionChain.Eligible)]
    [InlineData("margin", 34, "NO_CHARGE", VehicleDynamicFactsCriterion.BatteryPolicyNotSatisfiedReason)]
    [InlineData("entry", null, "NO_CHARGE", VehicleDynamicFactsCriterion.BatteryFactUnknownReason)]
    [InlineData("entry", 80, null, VehicleDynamicFactsCriterion.BatteryFactUnknownReason)]
    [InlineData("entry", 80, "CHARGING", VehicleDynamicFactsCriterion.BatteryPolicyNotSatisfiedReason)]
    [InlineData("none", 80, "NO_CHARGE", DispatchReasonCodes.ChargingPolicyNotApproved)]
    public void EachBatteryBoundaryAnswersItsOwnReason(string policy, int? battery, string? batteryState, string expected)
    {
        DispatchVehicleFacts facts = Facts(battery, batteryState, policy switch
        {
            "entry" => new DispatchBatteryPolicy(1, EntryBoundary),
            "margin" => new DispatchBatteryPolicy(1, MarginBoundary),
            _ => null,
        });

        Assert.Equal(expected, VehicleDynamicFactsCriterion.Evaluate(facts, Options()));
        if (policy != "margin" && expected != DispatchAdmissionChain.Eligible)
        {
            Assert.Equal(expected, InTransitVehicleFactsCriterion.Evaluate(facts with { Vehicle = UnderWay(facts.Vehicle) }, Options()));
        }
    }

    /// <summary>
    /// 读数过期不走电量这一段：两条链在它前面都答 <c>RIOT_VEHICLE_FACT_STALE</c>，过期的电量永远到不了阈值比较（码与位置本票不动）。
    /// </summary>
    [Fact]
    public void AStaleBatteryReadingIsRefusedBeforeTheThresholdsAreRead()
    {
        DispatchVehicleFacts facts = Facts(80, "NO_CHARGE", new DispatchBatteryPolicy(1, EntryBoundary));
        facts = facts with { Vehicle = facts.Vehicle with { ObservedAt = Now.AddMinutes(-5) } };

        Assert.Equal("RIOT_VEHICLE_FACT_STALE", VehicleDynamicFactsCriterion.Evaluate(facts, Options()));
        Assert.Equal("RIOT_VEHICLE_FACT_STALE", InTransitVehicleFactsCriterion.Evaluate(facts, Options()));
    }

    /// <summary>
    /// 途中追加（REQ-0281：追加就是新任务）：在途车电量掉到入口线以下就不接追加；追加后整趟估计保不住余量也不接。这一趟还有一条需求没卸，
    /// 追加后按两趟算——45 − 2 × 10 = 25 &lt; 30，而同一辆车若是空闲车接一趟是 45 − 10 = 35，合格：在途的估计只往保守的方向走。
    /// </summary>
    [Theory]
    [InlineData(29, 0, DispatchReasonCodes.MandatoryChargeRequired)]
    [InlineData(45, 10, VehicleDynamicFactsCriterion.BatteryPolicyNotSatisfiedReason)]
    [InlineData(50, 10, DispatchAdmissionChain.Eligible)]
    public void AnAppendIsJudgedForTheWholeJourneyAfterIt(int battery, int estimate, string expected)
    {
        ChargingPolicyContent policy = TestChargingPolicies.ContentAt(30) with { EstimatedTaskConsumptionPercent = estimate };
        DispatchVehicleFacts facts = Facts(battery, "NO_CHARGE", new DispatchBatteryPolicy(1, policy)) with
        {
            Plan = PlanWithOpenDemands(1),
        };

        Assert.Equal(expected, InTransitVehicleFactsCriterion.Evaluate(facts with { Vehicle = UnderWay(facts.Vehicle) }, Options()));
        if (battery == 45)
        {
            Assert.Equal(DispatchAdmissionChain.Eligible, VehicleDynamicFactsCriterion.Evaluate(facts, Options()));
        }
    }

    /// <summary>追加后要覆盖的趟数：当前下一站起各卸货停靠上没卸完的需求数加一；读不出计划时按两趟。</summary>
    [Theory]
    [InlineData(-1, 2)]
    [InlineData(0, 2)]
    [InlineData(1, 2)]
    [InlineData(3, 4)]
    public void TasksToCoverAfterAnAppendCountEveryOpenDemandAsAWholeTask(int openDemands, int expected)
    {
        Assert.Equal(expected, BatteryEligibility.TasksToCoverAfterAppend(openDemands < 0 ? null : PlanWithOpenDemands(openDemands)));
    }

    /// <summary>
    /// 已完成的停靠不算：计划里当前下一站之前的卸货停靠即便清单上还挂着数，也已经卸过了。
    /// </summary>
    [Fact]
    public void StopsBeforeTheCurrentNextStopAreNotCounted()
    {
        EnRouteVehiclePlan plan = new(
            [
                new EnRouteStop("U-DONE", "S-1", 11, "Z", JourneyStopRoles.Unload),
                new EnRouteStop("P-1", "S-2", 12, "Z", JourneyStopRoles.Pickup),
                new EnRouteStop("U-1", "S-3", 13, "Z", JourneyStopRoles.Unload),
            ],
            VehicleStationRiotId: 11,
            CurrentNextStopIndex: 1,
            new Dictionary<string, int> { ["U-DONE"] = 5, ["P-1"] = 1, ["U-1"] = 1 });

        Assert.Equal(2, BatteryEligibility.TasksToCoverAfterAppend(plan));
    }

    /// <summary>
    /// <c>batteryState</c> 的映射表，四个值各一条，外加没有策略、读数过期、电池状态缺失都是 <c>UNKNOWN</c>。
    /// 入口线 30、余量 20、估计 10：29 → <c>MANDATORY_CHARGE</c>；30 保住余量 → <c>SUFFICIENT</c>；换成估计 11 时 30 − 11 = 19 → <c>LOW</c>。
    /// 充电中照样按电量映射：充没充电是 <c>chargingCycleState</c> 的事。
    /// </summary>
    [Theory]
    [InlineData(29, "NO_CHARGE", 10, true, true, BatteryStates.MandatoryCharge)]
    [InlineData(30, "NO_CHARGE", 10, true, true, BatteryStates.Sufficient)]
    [InlineData(30, "NO_CHARGE", 11, true, true, BatteryStates.Low)]
    [InlineData(80, "CHARGING", 10, true, true, BatteryStates.Sufficient)]
    [InlineData(null, "NO_CHARGE", 10, true, true, BatteryStates.Unknown)]
    [InlineData(80, null, 10, true, true, BatteryStates.Unknown)]
    [InlineData(80, "NO_CHARGE", 10, false, true, BatteryStates.Unknown)]
    [InlineData(80, "NO_CHARGE", 10, true, false, BatteryStates.Unknown)]
    public void TheBatteryStateProjectionFollowsTheTable(
        int? battery, string? batteryState, int estimate, bool fresh, bool withPolicy, string expected)
    {
        DispatchBatteryPolicy? policy = withPolicy
            ? new DispatchBatteryPolicy(1, EntryBoundary with { EstimatedTaskConsumptionPercent = estimate })
            : null;

        Assert.Equal(expected, BatteryEligibility.Project(Facts(battery, batteryState, policy).Vehicle, policy, fresh));
    }

    /// <summary>强制充电判定的比较：低于即是，等于不是——与此前 <c>MinimumBatteryPercent</c>「低于即拒、等于放行」同一个。</summary>
    [Theory]
    [InlineData(29, true)]
    [InlineData(30, false)]
    public void TheMandatoryChargeJudgementIsStrictlyBelowTheEntryThreshold(int battery, bool expected)
    {
        Assert.Equal(expected, BatteryEligibility.IsMandatoryCharge(battery, EntryBoundary));
    }

    /// <summary>
    /// 默认测试策略下与此前等价（票面「默认测试策略下与今天等价」）：入口线 30、余量 30、估计 0，0～100 每一个电量的合格与否都与
    /// 「充电中或低于 30 即拒」相同。原因码只在「低于线」那一侧从 <c>BATTERY_POLICY_NOT_SATISFIED</c> 换成了新码——这是票面要的分开。
    /// </summary>
    [Fact]
    public void UnderTheDefaultTestPolicyEveryBatteryIsAdmittedExactlyAsUnderTheOldThirtyPercentThreshold()
    {
        DispatchBatteryPolicy policy = TestChargingPolicies.Battery();
        for (int battery = 0; battery <= 100; battery++)
        {
            foreach (string state in new[] { "NO_CHARGE", "CHARGING" })
            {
                bool before = !(state == "CHARGING" || battery < 30);
                string now = VehicleDynamicFactsCriterion.Evaluate(Facts(battery, state, policy), Options());
                Assert.True(before == (now == DispatchAdmissionChain.Eligible), $"{battery}% {state}: {now}");
                if (!before)
                {
                    Assert.Equal(
                        state == "CHARGING"
                            ? VehicleDynamicFactsCriterion.BatteryPolicyNotSatisfiedReason
                            : DispatchReasonCodes.MandatoryChargeRequired,
                        now);
                }
            }
        }
    }

    private static JourneyRuntimeOptions Options() =>
        new() { MapIdentity = Map, MaximumEvidenceAge = TimeSpan.FromSeconds(30) };

    private static DispatchVehicleFacts Facts(int? battery, string? batteryState, DispatchBatteryPolicy? policy) =>
        new(
            VehicleKey,
            "agv02",
            new OnboardDispatchFacts(7, [3, 4], true, true, true, true, false),
            new RiotVehicleObservation(VehicleKey, true, true, "IDLE", Map, 12, battery, batteryState, 0, Now, 0, null),
            Now,
            BatteryPolicy: policy);

    private static RiotVehicleObservation UnderWay(RiotVehicleObservation vehicle) =>
        vehicle with { ProcState = "RUNNING", Speed = 0.8, LockStatus = 1, OrderTaskId = "RIOT-TASK-1" };

    /// <summary>一趟在途旅程：当前下一站是一个卸货停靠，上面还有 <paramref name="openDemands"/> 条没卸完的需求。</summary>
    private static EnRouteVehiclePlan PlanWithOpenDemands(int openDemands) =>
        new(
            [new EnRouteStop("U-1", "S-1", 11, "Z", JourneyStopRoles.Unload)],
            VehicleStationRiotId: 12,
            CurrentNextStopIndex: 0,
            new Dictionary<string, int> { ["U-1"] = openDemands });
}
