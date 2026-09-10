namespace ControlServer.Infrastructure.Persistence;

/// <summary>
/// The latest onboard alarm snapshot for one vehicle -- the read model the FP-C8 dashboard projects
/// its alarm card from.
/// </summary>
/// <remarks>
/// One row per vehicle, replaced whole. Alarms travel as a snapshot rather than an event stream
/// precisely so there is never a "stale" display state to reason about (REQ-0269): after a
/// reconnect the newest snapshot is the current truth, and what was missed in between does not
/// matter. Alarm codes stay an open set in <see cref="AlarmsJson"/> and are never narrowed into the
/// closed <c>ErrorCode</c> enum.
/// </remarks>
public sealed class OnboardAlarmSnapshotRow
{
    public required string AgvId { get; set; }

    /// <summary>
    /// 这一份快照是在哪一代会话里报上来的。
    /// </summary>
    /// <remarks>
    /// <see cref="SnapshotSequence"/> 的单调域只有一代会话那么宽：车载端的告警板序号活在进程里，
    /// 车一重启就从 1 重新开始。只按序号采纳的话，重启后那台车的快照会被静默忽略，看板停在重启前
    /// 那一批——正是 REQ-0269 要禁的不确定新旧的旧值。所以采纳判据是 <c>(会话代, 序号)</c> 这一对：
    /// 新会话的快照无条件采纳，同一代内序号不前进才忽略。
    ///
    /// 能力快照与安全态快照本来就没有这个问题，它们的修订号记在按 <c>(agvId, sessionGeneration)</c>
    /// 分行的 <c>SessionRecoveryRow</c> 上，新会话就是新的一行；告警快照是一车一行，所以会话代要显式
    /// 记在这里。
    /// </remarks>
    public long SessionGeneration { get; set; }

    public long SnapshotSequence { get; set; }
    public DateTimeOffset CapturedAt { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public required string AlarmsJson { get; set; }
}
