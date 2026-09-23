namespace ControlServer.Infrastructure.Persistence;

/// <summary>
/// One RIoT order this server found running on one of its own vehicles without having created it (control-server#330,
/// REQ-0164 as revised in requirements baseline v1.5.0): the alarm, the audit record and the state of its handling in one row.
/// </summary>
/// <remarks>
/// <para>
/// <b>Keyed on RIoT's <c>orderId</c>.</b> An order is found, cancelled and ends once; handling the same order twice lands on
/// the same row, which is what makes "only one cancel per order" a fact of the store rather than of the code path
/// (<see cref="CancelCommandAuditId"/>).
/// </para>
/// <para>
/// <b>Written only for an order RIoT shows running on a vehicle this server manages.</b> An order still queueing, not yet
/// queued, or with no executing vehicle to be seen never gets a row: this server does not read, alarm or act on it
/// (REQ-0164: "在队列中、尚未进入队列或看不出执行车辆的订单，8005 不对其发出任何订单命令").
/// </para>
/// <para>
/// <b>While the row is in one of <see cref="ForeignRiotOrderStates.Holding"/>, its vehicle takes no new dispatch and no appended
/// demand</b> -- REQ-0164's 0/1 gate is not relaxed by the cancel: the vehicle is held until a read-back confirms the order
/// has explicitly ended.
/// </para>
/// </remarks>
public sealed class ForeignRiotOrderRow
{
    /// <summary>RIoT's string <c>orderId</c>, what the cancel addresses (BC-ORDER-005).</summary>
    public required string RiotOrderId { get; set; }

    /// <summary>The order's <c>upperId</c> as RIoT listed it; null when it had none.</summary>
    public string? UpperId { get; set; }

    public required string AgvId { get; set; }

    /// <summary>The <c>executeVehicleKey</c> RIoT showed the order running on when it was found.</summary>
    public required string DeviceKey { get; set; }

    /// <summary><see cref="ForeignRiotOrderOwnership"/>: proven foreign, or not provably this server's either way.</summary>
    public required string Ownership { get; set; }

    /// <summary>What the ownership was decided on, for the person reading the alarm.</summary>
    public required string OwnershipBasis { get; set; }

    /// <summary><see cref="ForeignRiotOrderStates"/> -- where the handling stands.</summary>
    public required string State { get; set; }

    public int? OrderStateAtDetection { get; set; }
    public DateTimeOffset DetectedAt { get; set; }

    /// <summary>The last round RIoT listed the order running on this vehicle.</summary>
    public DateTimeOffset LastSeenRunningAt { get; set; }

    /// <summary>When the re-read before the cancel confirmed the order still running on the same vehicle.</summary>
    public DateTimeOffset? CancelDecidedAt { get; set; }

    /// <summary>
    /// The one command audit attempt of the one cancel this server sends to this order. Set means "may have gone out":
    /// nothing sends a second one.
    /// </summary>
    public string? CancelCommandAuditId { get; set; }

    public DateTimeOffset? CancelSentAt { get; set; }

    /// <summary>What the cancel call itself answered (<c>Accepted</c>, <c>Failed</c>, <c>Unknown</c>); null until it answered.</summary>
    public string? CancelCallDisposition { get; set; }

    /// <summary>
    /// The result of the cancel as read back: <see cref="ForeignRiotOrderCancelResults"/>. Null until there is one. Rewritten
    /// when the order ends, so it always says the latest: a cancel handed to a person and read back cancelled later ends as
    /// <see cref="ForeignRiotOrderCancelResults.Cancelled"/> (the hand-over itself is in event 2184).
    /// </summary>
    public string? CancelResult { get; set; }

    public DateTimeOffset? CancelResultAt { get; set; }

    /// <summary>The explicit final state RIoT read back for the order (2, 4, 5 or 6), when it ended.</summary>
    public int? EndedOrderState { get; set; }

    public DateTimeOffset? EndedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Whose order a <see cref="ForeignRiotOrderRow"/> is, as far as this server can prove.</summary>
public static class ForeignRiotOrderOwnership
{
    /// <summary>
    /// Proven not this server's: no <c>OrderIntent</c> and no order command audit of this server's names it, and its
    /// <c>upperId</c> does not look like one of this server's either. Cancelled (REQ-0148, REQ-0164) where the deployment's
    /// cancel gate is open; held and alarmed like an unprovable one where it is not.
    /// </summary>
    public const string Foreign = "FOREIGN";

    /// <summary>
    /// Not provably either way -- for instance an <c>upperId</c> shaped like this server's with no intent behind it. Held and
    /// alarmed, never cancelled ("认不准时不取消，只阻断并告警").
    /// </summary>
    public const string Unproven = "UNPROVEN";
}

/// <summary>Where the handling of a <see cref="ForeignRiotOrderRow"/> stands.</summary>
public static class ForeignRiotOrderStates
{
    /// <summary>Proven foreign and running on this vehicle; the re-read before the cancel has not confirmed it yet.</summary>
    public const string Detected = "DETECTED";

    /// <summary>
    /// The re-read confirmed it still running on the same vehicle and the cancel is decided, not yet armed. A crash here
    /// leaves the cancel to be sent after the restart -- after another re-read.
    /// </summary>
    public const string CancelDecided = "CANCEL_DECIDED";

    /// <summary>The cancel's audit attempt is armed: it may have gone out. Waiting for RIoT to show the order ended.</summary>
    public const string CancelSent = "CANCEL_SENT";

    /// <summary>
    /// The cancel went out and the order is still running past <c>ForeignRunningOrderSupervisor.CancelSettleTime</c>: no second
    /// cancel, held and alarmed for a person.
    /// </summary>
    public const string StillRunningAfterCancel = "STILL_RUNNING_AFTER_CANCEL";

    /// <summary>Ownership not provable: never cancelled, held and alarmed for a person until the order ends.</summary>
    public const string HeldUnproven = "HELD_UNPROVEN";

    /// <summary>
    /// Proven foreign, but this deployment's cancel gate (<c>RiotForeignOrderCancel:Enabled</c>) is closed: not cancelled, held
    /// and alarmed for a person until the order ends. Taken up for the cancel if the gate is opened while it still runs.
    /// </summary>
    public const string HeldCancelNotAuthorized = "HELD_CANCEL_NOT_AUTHORIZED";

    /// <summary>
    /// No longer in RIoT's running listing, and RIoT does not read it back explicitly ended -- SUSPENDED, or not found at all --
    /// for longer than the settle time. It still holds the vehicle (REQ-0164 releases only on a confirmed ending), and a person
    /// has to find out in RIoT what became of it.
    /// </summary>
    public const string Unsettled = "UNSETTLED";

    /// <summary>RIoT read the order back in an explicit final state (2, 4, 5 or 6). The vehicle is no longer held for it.</summary>
    public const string Ended = "ENDED";

    /// <summary>
    /// No longer running on this vehicle and not explicitly ended: nothing is sent to an order that is not on a vehicle of
    /// ours, and the vehicle is no longer held for it. Found running on ours again, it is taken up again -- without a second
    /// cancel if one already went out.
    /// </summary>
    public const string LeftVehicle = "LEFT_VEHICLE";

    /// <summary>The states that hold the vehicle: no new dispatch, no appended demand, not "this server's own order in flight".</summary>
    public static IReadOnlyList<string> Holding { get; } =
        [Detected, CancelDecided, CancelSent, StillRunningAfterCancel, HeldUnproven, HeldCancelNotAuthorized, Unsettled];
}

/// <summary>What reading the order back after its cancel showed.</summary>
public static class ForeignRiotOrderCancelResults
{
    /// <summary>RIoT shows the order CANCELLED (2) -- what an API cancel of an EXECUTING or HELD order does (BC-ORDER-003, BC-ORDER-006).</summary>
    public const string Cancelled = "CANCELLED";

    /// <summary>The order ended in another explicit final state (FAILED 4, SUCCESS 5, DELETED 6) before or instead.</summary>
    public const string EndedOtherwise = "ENDED_OTHERWISE";

    /// <summary>Still not explicitly ended past the settle time: handed to a person, not cancelled again.</summary>
    public const string StillRunning = "STILL_RUNNING";
}
