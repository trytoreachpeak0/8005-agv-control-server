namespace ControlServer.Infrastructure.Persistence;

/// <summary>
/// 本服务端自己建的一张移动单终结之后，为同一辆车、同一条需求重建它的一次记录（control-server#318）。
/// </summary>
/// <remarks>
/// <para>
/// <b>一行是一次事件，也是它的审计记录。</b>三个来源——单在 RIoT 里被取消或删除、人工清除「FAILED 且车上可能有货」的故障、
/// 人工清除「FAILED 且车上没货」的故障——都在这里落一行，之后的延迟、车况等待、建单门禁、建单、重建成功或被「短时二次出问题」
/// 挡住，都记在同一行上。
/// </para>
/// <para>
/// <b>主键是终结的那张单的去重键派生的稳定 id</b>（<see cref="RebuildId"/> = <c>StableGuid(EndedUpperId, ...)</c>）：一张 RIoT 单只会终结一次，
/// 所以同一次取消、同一次清除被处理两次，落到的是同一行，建不出第二张单。
/// </para>
/// <para>
/// 新单的 <see cref="NewUpperId"/> 与 <see cref="NewMovementLegId"/> 在落这一行时就定下并存着：崩在「已决定、未建单」或「已建单、未记账」
/// 之间，重启后用的是同一对 id，RIoT 按 upperId 幂等（BC-ORDER-004），不会多建也不会漏建。
/// </para>
/// </remarks>
public sealed class OwnOrderRebuildRow
{
    public required string RebuildId { get; set; }
    public required string JourneyId { get; set; }

    /// <summary>旅程的锚需求，也就是新单上的 <c>demandId</c>（「同一 DemandId」）。短时二次出问题按它判。</summary>
    public required string DemandId { get; set; }
    public required string AgvId { get; set; }
    public required string VehicleKey { get; set; }

    /// <summary>车正开往、终结的那张单要去的那个停靠。重建的单去同一个停靠，剩余停靠不动。</summary>
    public required string StopId { get; set; }

    /// <summary><see cref="OwnOrderRebuildSources"/> 之一。</summary>
    public required string Source { get; set; }

    public required string EndedUpperId { get; set; }
    public string? EndedOrderId { get; set; }
    public int? EndedOrderState { get; set; }

    /// <summary>出问题的时刻：取消是服务端读到它的那一刻，FAILED 是故障记下的那一刻。短时二次出问题按它算。</summary>
    public DateTimeOffset IncidentAt { get; set; }

    /// <summary>这一行落下的时刻：取消是读到的那一轮，故障是人工清除成功的那一刻。</summary>
    public DateTimeOffset RecordedAt { get; set; }

    /// <summary>最早可以建新单的时刻（护栏一：延迟）。</summary>
    public DateTimeOffset DueAt { get; set; }

    /// <summary>清除故障的人（来源二、三）；取消来源为空。</summary>
    public string? OperatorId { get; set; }

    public required string NewUpperId { get; set; }
    public required string NewMovementLegId { get; set; }

    /// <summary><see cref="OwnOrderRebuildStates"/> 之一。</summary>
    public required string State { get; set; }

    /// <summary>此刻在等什么（车况、建单门禁、建单结果）；不在等时为空。只在变化时改写。</summary>
    public string? WaitingReason { get; set; }
    public DateTimeOffset? WaitingSince { get; set; }

    /// <summary>新单在 RIoT 上确认建成的时刻（审计用；REQ-0361 的窗口按 <see cref="IncidentAt"/> 算，不按它）。</summary>
    public DateTimeOffset? RebuiltAt { get; set; }

    /// <summary>
    /// 为什么不再自动重建（护栏三，或有货时快照证明不了货在原仓）；在 <see cref="OwnOrderRebuildStates.Stopped"/> 时有值。
    /// <see cref="OwnOrderRebuildStates.Failed"/> 与 <see cref="OwnOrderRebuildStates.Ended"/> 时写的是新单确认前怎么终结的。
    /// </summary>
    public string? StoppedReason { get; set; }
    public DateTimeOffset? StoppedAt { get; set; }

    /// <summary>
    /// 有货来源（REQ-0362）：清除之后车报的快照证明了货还完整留在原仓、门锁闭、开锁输出复位的时刻；为空时不建。其余来源始终为空。
    /// </summary>
    public DateTimeOffset? CargoProvenAt { get; set; }

    /// <summary>证明（或证明不了）货在原仓的那份 <c>SafetyStateSnapshot</c> 的 messageId，审计用。</summary>
    public string? CargoEvidenceMessageId { get; set; }

    /// <summary>
    /// 有货来源：服务端最近一次向车要快照（<c>SafetyStateSnapshotRequested</c>）时的会话代次；为空时还没要过。节流见
    /// <c>OwnOrderRebuilds.ClaimCargoEvidenceRequestAsync</c>。
    /// </summary>
    public long? CargoEvidenceRequestedGeneration { get; set; }

    /// <summary>那一次请求发出时会话是不是就绪。未就绪时要过的，会话在同一代次里变成就绪后可以再要一次。</summary>
    public bool CargoEvidenceRequestedWhileReady { get; set; }

    /// <summary>
    /// 最近一次向车要快照的时刻（服务端时钟）。判不了之后再要一次的节流从它和那份快照的收到时刻里较晚的一个算起：车不回应时，
    /// 旧快照一直够老，只看快照会每轮都再要（审查增量 B1）。
    /// </summary>
    public DateTimeOffset? CargoEvidenceRequestedAt { get; set; }
}

/// <summary>重建由哪一种终结引起（control-server#318 票面「三个触发来源」）。</summary>
public static class OwnOrderRebuildSources
{
    /// <summary>本服务端自己的在途单在 RIoT 里被取消或删除。</summary>
    public const string CancelledInRiot = "ORDER_CANCELLED_IN_RIOT";

    /// <summary>人工清除「FAILED 且车上可能有货」的故障。</summary>
    public const string FaultClearedCargoOnBoard = "FAULT_CLEARED_CARGO_ON_BOARD";

    /// <summary>人工清除「FAILED 且车上没货」的故障。</summary>
    public const string FaultClearedNothingOnBoard = "FAULT_CLEARED_NOTHING_ON_BOARD";
}

/// <summary>一次重建走到了哪一步。</summary>
public static class OwnOrderRebuildStates
{
    /// <summary>已记下，还没建新单：在等延迟、车况或建单门禁。停靠仍指向终结的那张单。</summary>
    public const string Pending = "PENDING";

    /// <summary>新单的意图已写下、停靠已改指向它，RIoT 上的单正在建或等确认。</summary>
    public const string Ordering = "ORDERING";

    /// <summary>新单在 RIoT 上确认建成。</summary>
    public const string Rebuilt = "REBUILT";

    /// <summary>不再自动重建，挡住并报警，等人处理（护栏三）。</summary>
    public const string Stopped = "STOPPED";

    /// <summary>
    /// 新单在确认建成之前就 FAILED 了（审查 S2）：按普通 FAILED 记故障，停靠留在这张新单上，引擎每轮照常把它喂给故障模型。
    /// 人清除故障时另记一条以新单为终结单的记录，这一条随即转 <see cref="Ended"/>。
    /// </summary>
    public const string Failed = "FAILED";

    /// <summary>
    /// 新单在确认建成之前就终结了，而那次终结已经另记了一条记录接手：确认前被取消的，当场另记一条（由 REQ-0361 的窗口判）；
    /// 确认前 FAILED 的，人清除故障时另记。这一条只留作历史，不再有人等它。
    /// </summary>
    public const string Ended = "ENDED";
}
