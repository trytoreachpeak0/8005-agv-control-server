using ControlServer.Host.Runtime.Dispatch.Criteria;

namespace ControlServer.Host.Runtime.Dispatch;

/// <summary>
/// 派车轮给装货阶段的读口：上一轮里，每辆在途车有哪几侧被「只因本车货物占侧」的候选判满（批次7-07，control-server#212；
/// REQ-0354、ADR-cross-0059）。
/// </summary>
/// <remarks>
/// <para>
/// <b>一侧装满有两种，这里只答其中一种。</b>REQ-0354：「该分组已无空仓，或存在一条除该分组空仓外其余准入全部通过、
/// 只因车上已装或已预留的货物占用该分组仓位而无法整批装入的候选 Demand」。前一种是车自己的事实，推进段读账本就答得出
/// （<see cref="JourneyAwareSlotLedger"/>），不需要派车轮；后一种只有「把候选对这辆车判一遍」才说得出，而那正是派车轮
/// 每一轮都在做的事——所以它从派车轮的裁决里取，不在推进段再判一遍候选。
/// </para>
/// <para>
/// <b>「其余准入全部通过」是由链的次序保证的，不是由这里保证的。</b><see cref="SlotCapacityCriterion"/> 是两条链上的最后
/// 一条（Order 100），链在第一个拒绝处停下，所以一条候选能拿到
/// <see cref="DispatchReasonCodes.SlotGroupOccupiedByOwnCargo"/>，就说明它排在前面的每一条都过了——包括追加的四道门与
/// 装货阶段是否还开着。被别的门禁挡住的候选根本走不到仓位判据，因此不计入；超大需求在仓位判据里先于这个原因码返回
/// <see cref="DispatchReasonCodes.ExpectedBasketCountExceedsSlotGroup"/>；仓位被禁用返回的是
/// <see cref="DispatchReasonCodes.SlotGroupCapacityTemporarilyUnavailable"/>。三种「不计入」都不必在这里排除，
/// <b>前提是仓位判据一直排在最后</b>——
/// <c>DispatchAdmissionChainDerivationTests.TheSlotCapacityCriterionRunsLastOnBothChains</c> 钉着这件事。
/// </para>
/// <para>
/// <b>只记上一轮，整张替换。</b>一辆车这一轮没跑完（预算耗尽、读挂了）或根本没参加，它就不在表里，读出来是
/// 「说不出来」，而不是沿用更早那一轮的结论：候选会被别的车接走、会被取消，早一轮的「有一条候选装不下」到这一轮
/// 可能已经不存在了。说不出来时装货阶段不改判（<see cref="LoadingPhaseMachine"/> 第 6 条）。
/// </para>
/// <para>
/// 进程内的，不落库。重启之后第一轮之前它是空的，那一段装货阶段按落库的判定走（<c>FullSlotPositionsJson</c>），不会翻状态。
/// </para>
/// </remarks>
public sealed class SlotGroupFullnessBoard
{
    private readonly object _gate = new();
    private Dictionary<string, IReadOnlySet<string>> _ownCargoBlockedGroups = new(StringComparer.Ordinal);

    /// <summary>用一轮的裁决替换整张表。</summary>
    public void Record(DispatchRoundOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        Dictionary<string, IReadOnlySet<string>> next = new(StringComparer.Ordinal);
        foreach (DispatchVehicleOutcome vehicle in outcome.CompletedVehicles)
        {
            next[vehicle.AgvId] = OwnCargoBlockedGroups(vehicle.Verdicts);
        }

        lock (_gate)
        {
            _ownCargoBlockedGroups = next;
        }
    }

    /// <summary>这辆车上一轮被「本车货物占侧」判满的那几侧；这辆车上一轮没有结论时为空引用。</summary>
    public IReadOnlySet<string>? OwnCargoBlockedGroupsOf(string agvId)
    {
        lock (_gate)
        {
            return _ownCargoBlockedGroups.TryGetValue(agvId, out IReadOnlySet<string>? groups) ? groups : null;
        }
    }

    /// <summary>一辆车的一轮裁决里，拿到「本车货物占侧」的候选各落在哪一侧。</summary>
    public static IReadOnlySet<string> OwnCargoBlockedGroups(IEnumerable<DispatchCandidateVerdict> verdicts)
    {
        ArgumentNullException.ThrowIfNull(verdicts);
        return verdicts
            .Where(verdict => verdict.ReasonCode == DispatchReasonCodes.SlotGroupOccupiedByOwnCargo)
            .Select(verdict => verdict.Evaluation.RequiredSlotPosition)
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
    }
}
