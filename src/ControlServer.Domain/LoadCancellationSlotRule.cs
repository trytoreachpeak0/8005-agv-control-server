namespace ControlServer.Domain;

/// <summary>
/// 装货开始之后申请取消时的逐仓判定（修订后的 REQ-0211，CP-0021，协议 6.0.0 候选，control-server#837）：只在全部目标仓都空、门都证明锁闭、开锁输出
/// 都复位时放行。只做纯判断：读数由调用方读齐。
/// </summary>
/// <remarks>
/// <para>
/// <b>先后照 wire-notes 第 94 行</b>：<c>SLOT_STATE_UNKNOWN</c> → <c>LOAD_CANCELLATION_BASKET_LOADED</c> → <c>LOCK_NOT_CLOSED</c> →
/// <c>UNLOCK_OUTPUT_NOT_RESET</c>，第一条不过的决定答复。
/// </para>
/// <para>
/// <b>读不到按读数不明。</b>读数过期（调用方给 null）、某个目标仓没有读数、读到 <c>UNKNOWN</c>，都是 <c>SLOT_STATE_UNKNOWN</c>——没有证据不当有证据用，
/// 这条线上「有货」就是从严那一边。「有货」按读数 <c>OCCUPIED</c> 判，不要求它锁闭复位：门开着、篮子已经放进去，同样不能取消。
/// </para>
/// </remarks>
public static class LoadCancellationSlotRule
{
    /// <summary>不能取消的原因，能取消时为 null。</summary>
    public static WireProblem? Refusal(
        IReadOnlyCollection<int> targetSlots,
        IReadOnlyDictionary<int, RemainingBasketsSkipRule.SlotReading>? readings)
    {
        ArgumentNullException.ThrowIfNull(targetSlots);
        RemainingBasketsSkipRule.SlotReading? Reading(int slot) =>
            readings is not null && readings.TryGetValue(slot, out RemainingBasketsSkipRule.SlotReading? reading) ? reading : null;
        if (targetSlots.Count == 0 || targetSlots.Any(slot => Reading(slot) is not { IsUnknown: false }))
        {
            return new WireProblem(ServerReasonCodes.SlotStateUnknown, "payload.slotOperationAttemptId",
                "有仓位的状态还没有确认，暂时不能取消装货。请稍候再按一次。");
        }
        if (targetSlots.Any(slot => Reading(slot)!.Physical != "EMPTY"))
        {
            return new WireProblem(ServerReasonCodes.LoadCancellationBasketLoaded, "payload.slotOperationAttemptId",
                "已经装进花篮，不能取消装货。可以继续装货，或申请剩下的不装。");
        }
        if (targetSlots.Any(slot => Reading(slot)!.Lock != "LOCKED"))
        {
            return new WireProblem(ServerReasonCodes.LockNotClosed, "payload.slotOperationAttemptId",
                "有仓门没有证明锁闭，请先关好仓门再取消装货。");
        }
        if (targetSlots.Any(slot => Reading(slot)!.Output != "RESET"))
        {
            return new WireProblem(ServerReasonCodes.UnlockOutputNotReset, "payload.slotOperationAttemptId",
                "有仓位的开锁输出没有复位，请稍候再取消装货。");
        }
        return null;
    }
}
