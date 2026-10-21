namespace ControlServer.Domain;

/// <summary>
/// 多篮装货「剩下的不装」的判定与结算核对（REQ-0367、修订后的 REQ-0211 与 REQ-0352，CP-0021，协议 6.0.0 候选，control-server#830）。
/// 只做纯判断：事实由调用方读齐，这里按协议规定的先后逐条核，第一条不过的决定答复。
/// </summary>
/// <remarks>
/// <para>
/// <b>先后照 wire-notes 减篮一节</b>：<c>DEMAND_NOT_CURRENT</c> → <c>OPERATION_SESSION_MISMATCH</c> → <c>ACTION_NOT_ALLOWED_IN_STATE</c>（N = 1、
/// 这次装货已有结果、已转人工恢复、纠错或取消在处理、这次装货已授权过一次）→ <c>SLOT_STATE_UNKNOWN</c> → <c>LOCK_NOT_CLOSED</c> →
/// <c>UNLOCK_OUTPUT_NOT_RESET</c> → <c>REMAINING_BASKETS_SKIP_NOTHING_LOADED</c> → <c>SLOT_SET_INVALID</c>。
/// </para>
/// <para>
/// <b>「安全闭环为 OCCUPIED」按服务端手上的读数判</b>：有货、锁闭、开锁输出复位。读不到某个目标仓的读数，与读到 <c>UNKNOWN</c> 同样按
/// <c>SLOT_STATE_UNKNOWN</c> 拒——没有证据不当有证据用。读数是否过期由调用方判：过期就先要一份快照、不来这里。
/// </para>
/// </remarks>
public static class RemainingBasketsSkipRule
{
    public const string Authorized = "AUTHORIZED";
    public const string Rejected = "REJECTED";

    /// <summary>判定读的事实。</summary>
    /// <param name="DemandIsCurrentLoad">需求是车此刻所在取货停靠上的、attempt 是它此刻在装的那一次。</param>
    /// <param name="OperationSessionMatches">请求里的 <c>operationSessionId</c> 是此刻停靠的。</param>
    /// <param name="ExpectedBasketCount">需求的期待篮数 N。</param>
    /// <param name="NotAllowedInState">装货此刻不接受减篮的原因（已有结果、人工恢复、纠错或取消在处理、已授权过），没有为 null。</param>
    /// <param name="TargetSlots">这次装货命令的目标仓位。</param>
    /// <param name="Readings">服务端手上每个目标仓的读数；缺了的仓视为读不到。</param>
    /// <param name="RequestedLoadedSlots">请求里的 <c>loadedSlots</c>。</param>
    public sealed record Facts(
        bool DemandIsCurrentLoad,
        bool OperationSessionMatches,
        int ExpectedBasketCount,
        string? NotAllowedInState,
        IReadOnlyList<int> TargetSlots,
        IReadOnlyDictionary<int, SlotReading> Readings,
        IReadOnlyList<int> RequestedLoadedSlots);

    /// <summary>一个仓的读数：<c>physicalState</c>、<c>lockState</c>、<c>unlockOutputState</c> 的线上取值。</summary>
    public sealed record SlotReading(string Physical, string Lock, string Output)
    {
        public bool IsUnknown => Physical == "UNKNOWN" || Lock == "UNKNOWN" || Output == "UNKNOWN";

        public bool ClosedSafelyOccupied => Physical == "OCCUPIED" && Lock == "LOCKED" && Output == "RESET";
    }

    /// <summary>判定结果：授权时 <see cref="Problem"/> 为 null、<see cref="LoadedSlots"/> 是那 k 个仓；拒绝时带原因、<see cref="LoadedSlots"/> 为空。</summary>
    public sealed record Decision(string Outcome, WireProblem? Problem, IReadOnlyList<int> LoadedSlots);

    public static Decision Decide(Facts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (!facts.DemandIsCurrentLoad)
        {
            return Reject(ServerReasonCodes.DemandNotCurrent, "payload.slotOperationAttemptId",
                "这条需求不是车此刻所在取货站正在装的那一条，不能申请剩下的不装。");
        }
        if (!facts.OperationSessionMatches)
        {
            return Reject(ServerReasonCodes.OperationSessionMismatch, "payload.operationSessionId",
                "这次站点操作已经不是车此刻所在停靠的，不能申请剩下的不装。");
        }
        if (facts.ExpectedBasketCount <= 1)
        {
            return Reject(ServerReasonCodes.ActionNotAllowedInState, "payload.demandId",
                "这条需求只有一篮，没有剩下的可以不装。");
        }
        if (facts.NotAllowedInState is { } notAllowed)
        {
            return Reject(ServerReasonCodes.ActionNotAllowedInState, "payload.slotOperationAttemptId", notAllowed);
        }

        int[] targets = [.. facts.TargetSlots.Distinct().Order()];
        SlotReading? Reading(int slot) => facts.Readings.TryGetValue(slot, out SlotReading? reading) ? reading : null;
        if (targets.Any(slot => Reading(slot) is not { IsUnknown: false }))
        {
            return Reject(ServerReasonCodes.SlotStateUnknown, "payload.loadedSlots",
                "有仓位的状态不明，不能申请剩下的不装。请先确认仓位状态。");
        }
        if (targets.Any(slot => Reading(slot)!.Lock != "LOCKED"))
        {
            return Reject(ServerReasonCodes.LockNotClosed, "payload.loadedSlots",
                "有仓门没有证明锁闭，请先关好仓门再申请剩下的不装。");
        }
        if (targets.Any(slot => Reading(slot)!.Output != "RESET"))
        {
            return Reject(ServerReasonCodes.UnlockOutputNotReset, "payload.loadedSlots",
                "有仓位的开锁输出没有复位，请稍候再申请剩下的不装。");
        }
        int[] occupied = [.. targets.Where(slot => Reading(slot)!.ClosedSafelyOccupied)];
        if (occupied.Length == 0)
        {
            return Reject(ServerReasonCodes.RemainingBasketsSkipNothingLoaded, "payload.loadedSlots",
                "还没有装进任何花篮。一篮都不装请改用取消装货。");
        }
        int[] requested = [.. facts.RequestedLoadedSlots.Distinct().Order()];
        if (!requested.SequenceEqual(occupied) || occupied.Length == targets.Length)
        {
            return Reject(ServerReasonCodes.SlotSetInvalid, "payload.loadedSlots",
                "申请里的已装仓位与服务端读到的不一致，请稍候重试。");
        }
        return new Decision(Authorized, null, occupied);
    }

    /// <summary>
    /// <c>COMPLETED_REMAINING_SKIPPED</c> 的结果对得上这次授权：授权的仓 <c>COMPLETED</c>、有货、锁闭、复位；其余目标仓 <c>NOT_STARTED</c>、空、锁闭、
    /// 复位、带 <c>REMAINING_BASKETS_SKIPPED</c>；两组合起来恰好是命令的目标仓，各一次。
    /// </summary>
    public static bool SettlesAuthorization(
        StationOperationResult result,
        IReadOnlyList<int> targetSlots,
        IReadOnlyList<int> authorizedSlots)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(targetSlots);
        ArgumentNullException.ThrowIfNull(authorizedSlots);
        if (result.SlotOutcomes is not { } outcomes || authorizedSlots.Count == 0)
        {
            return false;
        }
        int[] targets = [.. targetSlots.Order()];
        int[] authorized = [.. authorizedSlots.Distinct().Order()];
        if (authorized.Length >= targets.Length || !authorized.All(targets.Contains) ||
            !result.SlotEvidence.Select(item => item.SlotNumber).Order().SequenceEqual(targets) ||
            !outcomes.Select(item => item.SlotNumber).Order().SequenceEqual(targets))
        {
            return false;
        }
        foreach (int slot in targets)
        {
            SlotPhysicalEvidence evidence = result.SlotEvidence.Single(item => item.SlotNumber == slot);
            SlotOutcomeReport outcome = outcomes.Single(item => item.SlotNumber == slot);
            bool skipReason = outcome.ReasonCodes.Contains(ServerReasonCodes.RemainingBasketsSkipped, StringComparer.Ordinal);
            bool settled = authorized.Contains(slot)
                ? outcome.Outcome == "COMPLETED" && evidence.State == SlotBusinessState.Occupied && !skipReason
                : outcome.Outcome == "NOT_STARTED" && evidence.State == SlotBusinessState.Empty && skipReason;
            if (!settled || !evidence.DoorLocked || !evidence.UnlockOutputReset)
            {
                return false;
            }
        }
        return true;
    }

    private static Decision Reject(string reasonCode, string fieldPath, string message) =>
        new(Rejected, new WireProblem(reasonCode, fieldPath, message), []);
}
