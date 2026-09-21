using ControlServer.Application;
using ControlServer.Host.Runtime.Dispatch;
using ControlServer.Host.Runtime.Dispatch.Criteria;

namespace ControlServer.Tests;

/// <summary>
/// 装货阶段在派车那一侧的两件东西（批次7-07，control-server#212）：装货阶段结束的车不再接追加的那一道判据，以及派车轮
/// 交给装货阶段的「哪几侧被本车货物占满」读口。
/// </summary>
public sealed class Batch7LoadingPhaseDispatchTests
{
    [Fact]
    public async Task AVehicleWhoseLoadingPhaseClosedTakesNoAppendedDemand()
    {
        Assert.Equal(
            DispatchReasonCodes.LoadingPhaseClosed,
            await new LoadingPhaseOpenCriterion().EvaluateAsync(Evaluation(Plan(closed: true)), TestContext.Current.CancellationToken));
        Assert.Equal(
            DispatchAdmissionChain.Eligible,
            await new LoadingPhaseOpenCriterion().EvaluateAsync(Evaluation(Plan(closed: false)), TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// 没有计划的车（空闲车，或者链装配错了）放行，理由与 <see cref="EnRouteAppendCriterion"/> 那一处相同：它不是这道判据要守的东西，
    /// 而抛会把一轮派车整个带下去。
    /// </summary>
    [Fact]
    public async Task AVehicleWithNoPlanIsNotThisCriterionsBusiness() =>
        Assert.Equal(
            DispatchAdmissionChain.Eligible,
            await new LoadingPhaseOpenCriterion().EvaluateAsync(Evaluation(plan: null), TestContext.Current.CancellationToken));

    /// <summary>
    /// 读口只认「本车货物占侧」一种原因码，按候选的那一侧归类；同一侧几条只算一次。
    /// </summary>
    [Fact]
    public void OnlyOwnCargoVerdictsMarkASideAndEachSideOnce()
    {
        IReadOnlySet<string> groups = SlotGroupFullnessBoard.OwnCargoBlockedGroups(
        [
            Verdict(DispatchReasonCodes.SlotGroupOccupiedByOwnCargo, "FRONT"),
            Verdict(DispatchReasonCodes.SlotGroupOccupiedByOwnCargo, "FRONT"),
            Verdict(DispatchReasonCodes.ExpectedBasketCountExceedsSlotGroup, "REAR"),
            Verdict(DispatchReasonCodes.SlotGroupCapacityTemporarilyUnavailable, "REAR"),
            Verdict(DispatchAdmissionChain.Eligible, "REAR"),
            // 仓位判据之前就被挡住的候选，走不到选侧那一步，侧是空的：不算任何一侧。
            Verdict(DispatchReasonCodes.SlotGroupOccupiedByOwnCargo, side: null),
        ]);

        Assert.Equal(["FRONT"], groups.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// 一轮整张替换：上一轮有结论、这一轮没跑完的车读出来是「说不出来」，不沿用更早那一轮。
    /// </summary>
    /// <remarks>
    /// 沿用的话，一条早被别的车接走的候选会让这辆车一直判满——候选会消失，而消失只有下一轮的裁决说得出来。
    /// </remarks>
    [Fact]
    public void EachRoundReplacesTheWholeBoard()
    {
        SlotGroupFullnessBoard board = new();
        board.Record(Round(("agv02", [Verdict(DispatchReasonCodes.SlotGroupOccupiedByOwnCargo, "REAR")]), ("agv03", [])));
        Assert.Equal(["REAR"], board.OwnCargoBlockedGroupsOf("agv02")!.Order(StringComparer.Ordinal));
        Assert.Empty(board.OwnCargoBlockedGroupsOf("agv03")!);

        board.Record(Round(("agv03", [])));

        Assert.Null(board.OwnCargoBlockedGroupsOf("agv02"));
        Assert.NotNull(board.OwnCargoBlockedGroupsOf("agv03"));
        Assert.Null(new SlotGroupFullnessBoard().OwnCargoBlockedGroupsOf("agv02"));
    }

    private static EnRouteVehiclePlan Plan(bool closed) =>
        new([], 210, 0, new Dictionary<string, int>(StringComparer.Ordinal), LoadingPhaseClosed: closed);

    private static DispatchCandidateEvaluation Evaluation(EnRouteVehiclePlan? plan) =>
        new(candidate: null!, round: null!, new DispatchVehicleFacts(
            "BROKERX-0001",
            "agv02",
            new OnboardDispatchFacts(1, [3, 4], true, true, true, true, false),
            new RiotVehicleObservation(
                "BROKERX-0001", true, true, "IDLE", "MAP-26", 12, 90, "NO_CHARGE", 0,
                new DateTimeOffset(2026, 9, 21, 6, 0, 0, TimeSpan.Zero)),
            new DateTimeOffset(2026, 9, 21, 6, 0, 0, TimeSpan.Zero),
            Plan: plan));

    private static DispatchCandidateVerdict Verdict(string reason, string? side) =>
        new(new DispatchCandidateEvaluation(candidate: null!, round: null!, vehicle: null!)
        {
            AreaAssignment = side is null ? null : new AreaAssignment("N1-1", "MAP-26-WIRE_TO_GATE", side)
        },
        reason);

    private static DispatchRoundOutcome Round(params (string AgvId, DispatchCandidateVerdict[] Verdicts)[] vehicles) =>
        new(Round: null!, [.. vehicles.Select(vehicle => new DispatchVehicleOutcome(vehicle.AgvId, $"KEY-{vehicle.AgvId}", vehicle.Verdicts))]);
}
