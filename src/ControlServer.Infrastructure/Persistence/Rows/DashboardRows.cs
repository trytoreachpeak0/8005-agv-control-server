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
    public long SnapshotSequence { get; set; }
    public DateTimeOffset CapturedAt { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public required string AlarmsJson { get; set; }
}
