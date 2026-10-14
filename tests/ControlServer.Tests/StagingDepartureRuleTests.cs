using ControlServer.Application;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Dispatch;
using ControlServer.Host.Runtime.Dispatch.Criteria;

namespace ControlServer.Tests;

/// <summary>
/// <see cref="StagingDepartureRule"/> 的纯函数用例（control-server#710）：票面验收 1～8 每条在这里都有一行，引擎那一头在
/// <see cref="StagingDepartureFullOrNoFitTests"/>。
/// </summary>
public sealed class StagingDepartureRuleTests
{
    private const string OwnCargo = DispatchReasonCodes.SlotGroupOccupiedByOwnCargo;
    private const string ShortOfFreeSlots = DispatchReasonCodes.SlotGroupCapacityTemporarilyUnavailable;
    private const string PlanLimit = DispatchReasonCodes.EnRouteAppendPlanLimitReached;
    private const string Eligible = DispatchAdmissionChain.Eligible;
    private const string MandatoryCharge = DispatchReasonCodes.MandatoryChargeRequired;
    private const string NotAdmitted = VehicleTaskTypeAdmissionCriterion.TaskTypeNotAdmittedReason;

    public static TheoryData<string, bool, string[], bool> Rows => new()
    {
        // 验收 1：前仓满（本车货占着它），等着的全要前仓。
        { "every waiting demand needs the full side", false, [OwnCargo, OwnCargo], true },
        // 验收 2：有一条要后仓且装得下。
        { "one waiting demand still fits", false, [OwnCargo, Eligible], false },
        { "only fitting demands", false, [Eligible], false },
        // 验收 3：两侧都满，没有等着的也走；等着的里有合格的也走（每一侧都没空仓，它其实装不进来）。
        { "every side full, nothing waiting", true, [], true },
        { "every side full, something eligible", true, [Eligible], true },
        // 验收 4：那一侧空仓不够（后仓剩 1 个、要 2 篮）。
        { "short of free slots on its side", false, [ShortOfFreeSlots], true },
        // 验收 5：全部因非容量原因被拒，或者没有等着的。
        { "nothing waiting", false, [], false },
        { "all refused for battery", false, [MandatoryCharge, MandatoryCharge], false },
        { "all refused for admission", false, [NotAdmitted], false },
        { "oversized is not capacity", false, [DispatchReasonCodes.ExpectedBasketCountExceedsSlotGroup], false },
        // 验收 6：容量与准入、电量混在一起，没有一条装得下。
        { "capacity and admission", false, [OwnCargo, NotAdmitted, MandatoryCharge], true },
        // 验收 6 的反面：混着的里有一条合格。
        { "capacity, admission and one that fits", false, [OwnCargo, NotAdmitted, Eligible], false },
        // 验收 7：只因 9 段、8 条上限。
        { "plan limit only", false, [PlanLimit], true },
        { "plan limit and own cargo", false, [PlanLimit, OwnCargo, ShortOfFreeSlots], true },
    };

    [Theory]
    [MemberData(nameof(Rows))]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public void TheVehicleLeavesOnlyWhenEverySideIsFullOrNothingWaitingFits(
        string row, bool everySideFull, string[] waiting, bool expected)
    {
        Assert.True(
            StagingDepartureRule.Decide(new StagingDepartureRule.Facts(false, everySideFull, waiting)) == expected,
            row);
    }

    /// <summary>验收 8：派车轮这一轮没有这辆车的结论、或车的仓位读不到时说不出来；判过该走的照旧该走（锁住，不翻回）。</summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public void WithoutARoundOrSlotFactsItCannotSayAndOnceDueItStaysDue()
    {
        Assert.Null(StagingDepartureRule.Decide(new StagingDepartureRule.Facts(false, false, null)));
        Assert.Null(StagingDepartureRule.Decide(new StagingDepartureRule.Facts(false, true, null)));
        Assert.Null(StagingDepartureRule.Decide(new StagingDepartureRule.Facts(false, null, [OwnCargo])));
        Assert.True(StagingDepartureRule.Decide(new StagingDepartureRule.Facts(true, null, null)));
        Assert.True(StagingDepartureRule.Decide(new StagingDepartureRule.Facts(true, false, [Eligible])));
    }

    /// <summary>容量只有三个码；票面列的四样里 9 段与 8 条共用一个。</summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public void CapacityIsOwnCargoShortOfFreeSlotsAndThePlanLimit()
    {
        Assert.True(StagingDepartureRule.IsCapacityRefusal(OwnCargo));
        Assert.True(StagingDepartureRule.IsCapacityRefusal(ShortOfFreeSlots));
        Assert.True(StagingDepartureRule.IsCapacityRefusal(PlanLimit));
        Assert.False(StagingDepartureRule.IsCapacityRefusal(Eligible));
        Assert.False(StagingDepartureRule.IsCapacityRefusal(DispatchReasonCodes.ExpectedBasketCountExceedsSlotGroup));
        Assert.False(StagingDepartureRule.IsCapacityRefusal(DispatchReasonCodes.EnRouteAppendDelayGateExceeded));
        Assert.False(StagingDepartureRule.IsCapacityRefusal(DispatchReasonCodes.EnRouteAppendBreaksZoneContiguity));
        Assert.False(StagingDepartureRule.IsCapacityRefusal(DispatchReasonCodes.LoadingPhaseClosed));
    }

    /// <summary>
    /// 「本站的待送候选」：取货站是本站、任务类型是 STAGING_TO_WIRE。没走到站点解析的候选（没有路线）认不出取货站，不算——它们反正是被
    /// 非容量的判据拒的。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public void OnlyStagingCandidatesPickedUpAtThisStationAreWaitingHere()
    {
        DispatchCandidateVerdict[] verdicts =
        [
            StagingDepartureFullOrNoFitTests.Verdict("A", TransportTaskTypes.StagingToWire, 305, "FRONT", OwnCargo),
            StagingDepartureFullOrNoFitTests.Verdict("B", TransportTaskTypes.StagingToWire, 306, "FRONT", Eligible),
            StagingDepartureFullOrNoFitTests.Verdict("C", TransportTaskTypes.WireToGate, 305, "REAR", Eligible),
            StagingDepartureFullOrNoFitTests.Verdict("D", TransportTaskTypes.StagingToWire, null, null, NotAdmitted),
            StagingDepartureFullOrNoFitTests.Verdict("E", TransportTaskTypes.StagingToWire, 305, null, PlanLimit),
        ];

        Assert.Equal([OwnCargo, PlanLimit], StagingDepartureRule.WaitingAt(verdicts, 305));
        Assert.Equal([Eligible], StagingDepartureRule.WaitingAt(verdicts, 306));
    }
}
