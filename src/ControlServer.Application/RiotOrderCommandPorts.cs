namespace ControlServer.Application;

/// <summary>
/// RIoT's <c>orderState</c> codes, by name.
/// </summary>
/// <remarks>
/// <para>
/// The mapping is RIoT's, read off <c>OrderRecordObject.OrderState</c> and recorded in the
/// behaviour lab's endpoint catalogue: 1 QUEUEING, 2 CANCELLED, 3 EXECUTING, 4 FAILED, 5 SUCCESS,
/// 6 DELETED, 7 PAUSED, 8 SUSPENDED, 9 HANG, 10 queue-priority. Rounds 10, 14 and 27 exercised the
/// three that matter here — <c>CMD_ORDER_HELD</c> lands on 7, <c>CMD_ORDER_CONTINUE_FROM_HELD</c>
/// returns to 3, <c>CMD_ORDER_CANCEL</c> lands on 2, and 9 is the HANG threshold.
/// </para>
/// <para>
/// They are named here because reconciliation is the one place that has to read them as meanings
/// rather than pass them through. Everywhere else in this server an <c>orderState</c> is either
/// carried verbatim or collapsed into <see cref="RiotOrderObservationKind"/>, and that is the
/// right default: the codes are RIoT's vocabulary, not the server's.
/// </para>
/// </remarks>
public static class RiotOrderState
{
    public const int Queueing = 1;
    public const int Cancelled = 2;
    public const int Executing = 3;
    public const int Failed = 4;
    public const int Success = 5;
    public const int Deleted = 6;
    public const int Paused = 7;
    public const int Suspended = 8;
    public const int Hang = 9;
    public const int QueuePriority = 10;
}

/// <summary>
/// The four order commands on <c>POST /api/task/v1/order/command/{orderId}</c>.
/// </summary>
/// <remarks>
/// The allowlist approves exactly these four <c>commandType</c> values and no others
/// (<c>docs/riot-call-allowlist.md</c> 1.3). <c>PriorityExec</c> exists in the Facade and is
/// deliberately absent here — being in the Facade was never the authorisation.
/// </remarks>
public enum RiotOrderCommandKind
{
    /// <summary><c>CMD_ORDER_CANCEL</c>.</summary>
    Cancel,

    /// <summary><c>CMD_ORDER_HELD</c>.</summary>
    Hold,

    /// <summary><c>CMD_ORDER_CONTINUE_FROM_HELD</c>. Not for a HANG.</summary>
    ContinueFromHeld,

    /// <summary><c>CMD_ORDER_CONTINUE_FROM_HANG</c>. Not for a HELD.</summary>
    ContinueFromHang,
}

/// <summary>
/// The two device commands on <c>POST /api/device/v1/command/sync/service/{deviceKey}/{serviceId}</c>.
/// </summary>
public enum RiotEmergencyCommandKind
{
    /// <summary><c>triggerEmergency</c>. Software emergency stop.</summary>
    Trigger,

    /// <summary>
    /// <c>cancelEmergency</c>. Forbidden while the latch is <c>CAN_NOT_RECOVER</c>
    /// (<c>docs/riot-call-allowlist.md</c> 1.5).
    /// </summary>
    Cancel,
}

/// <summary>
/// What the call itself did — <b>not</b> what it achieved.
/// </summary>
/// <remarks>
/// The distinction is the whole of <c>RIoTRetryReconciliation</c>. RIoT answers a command with a
/// business code, and the SDK's own documentation says in as many words that HTTP and business
/// success do not prove the order left HANG or that the vehicle stopped. So a command is never
/// successful at this layer; it is at most accepted, and the terminal state has to be read back.
/// </remarks>
public enum RiotCommandCallDisposition
{
    /// <summary>RIoT accepted the call. Says nothing about the resulting state.</summary>
    Accepted,

    /// <summary>The call failed definitively; no state change may be assumed.</summary>
    Failed,

    /// <summary>
    /// Timeout, transport failure, or an unreadable response. The command may or may not have
    /// taken effect, and only a read-back can tell — never a retry on its own.
    /// </summary>
    Unknown,
}

/// <summary>One issued command call and its receipt.</summary>
public sealed record RiotCommandCallResult(
    RiotCommandCallDisposition Disposition,
    RiotOrderCallReceipt Receipt);

/// <summary>
/// A vehicle's software emergency-stop latch as RIoT reports it.
/// </summary>
/// <remarks>
/// <para>
/// Verbatim, because the three values are not interchangeable: <c>CAN_RECOVER</c> is the only one
/// from which <c>cancelEmergency</c> may be called at all, and REQ-0248 stops re-triggering once
/// either latched value is confirmed. <see cref="IRiotVehicleSafetyFacts"/> collapses the same
/// field into one <c>RIOT_EMERGENCY_NOT_OK</c> reason code, which is right for its question and
/// useless for this one.
/// </para>
/// <para>
/// <c>null</c> means the state could not be read. It is not <c>OK</c> and it is not a latch — an
/// unknown latch keeps REQ-0248's retry running.
/// </para>
/// </remarks>
public sealed record RiotVehicleEmergencyObservation(
    string DeviceKey,
    string? EmergencyState,
    DateTimeOffset ObservedAt)
{
    public const string Ok = "OK";
    public const string CanRecover = "CAN_RECOVER";
    public const string CanNotRecover = "CAN_NOT_RECOVER";

    /// <summary>Whether RIoT answered at all.</summary>
    public bool IsKnown => EmergencyState is not null;

    /// <summary>Whether the latch is engaged, in either of its two forms.</summary>
    public bool IsLatched =>
        EmergencyState is CanRecover or CanNotRecover;
}

/// <summary>
/// The <c>commandType</c> strings the audit table stores, and the receipt operation names.
/// </summary>
/// <remarks>
/// They are the names ticket 06 wrote on <c>RiotOrderCommandAuditRow.CommandType</c>, kept exactly
/// — inconsistent casing included. That column carries a unique index together with the attempt
/// number, so the strings are stored contract rather than a display choice, and tidying them would
/// orphan every row already written under the old spelling.
/// </remarks>
public static class RiotCommandTypeNames
{
    public const string CancelOrder = "CANCEL";
    public const string OrderHold = "OrderHold";
    public const string OrderContinue = "OrderContinue";
    public const string HangContinue = "HangContinue";
    public const string TriggerEmergency = "triggerEmergency";
    public const string CancelEmergency = "cancelEmergency";

    public static string For(RiotOrderCommandKind kind) => kind switch
    {
        RiotOrderCommandKind.Cancel => CancelOrder,
        RiotOrderCommandKind.Hold => OrderHold,
        RiotOrderCommandKind.ContinueFromHeld => OrderContinue,
        RiotOrderCommandKind.ContinueFromHang => HangContinue,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown RIoT order command.")
    };

    public static string For(RiotEmergencyCommandKind kind) => kind switch
    {
        RiotEmergencyCommandKind.Trigger => TriggerEmergency,
        RiotEmergencyCommandKind.Cancel => CancelEmergency,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown RIoT emergency command.")
    };

    public static RiotOrderCommandKind ParseOrderCommand(string commandType) => commandType switch
    {
        CancelOrder => RiotOrderCommandKind.Cancel,
        OrderHold => RiotOrderCommandKind.Hold,
        OrderContinue => RiotOrderCommandKind.ContinueFromHeld,
        HangContinue => RiotOrderCommandKind.ContinueFromHang,
        _ => throw new ArgumentOutOfRangeException(
            nameof(commandType), commandType, "Not one of the four approved order commands.")
    };
}

/// <summary>
/// The six approved RIoT commands, issued through named Facades.
/// </summary>
/// <remarks>
/// <para>
/// In its own file rather than in <c>Ports.cs</c>, following the engine's and the create gate's
/// ports: that file is ticket 06's for the batch so the capability lanes can land in parallel, and
/// this port belongs to one lane. Its storage side does live there
/// (<see cref="IRiotOrderCommandAuditStore"/>).
/// </para>
/// <para>
/// Two methods rather than six because RIoT has two routes, not six — the four order commands are
/// one endpoint discriminated by <c>commandType</c> in the body, and the two emergency commands
/// are one endpoint discriminated by a path segment. Splitting on the route also keeps each
/// target non-nullable: an order command addresses an <c>orderId</c>, an emergency command
/// addresses a <c>deviceKey</c>, and the two are never interchangeable.
/// </para>
/// <para>
/// <b>No method here throws for a RIoT failure.</b> The caller has to tell "refused" from "never
/// arrived", and an exception collapses them into one — which is exactly the collapse that would
/// let a command that may have gone out be retried as though it had not.
/// </para>
/// </remarks>
public interface IRiotOrderCommandGateway
{
    Task<RiotCommandCallResult> IssueOrderCommandAsync(
        RiotOrderCommandKind kind,
        string orderId,
        string? reason,
        CancellationToken cancellationToken);

    Task<RiotCommandCallResult> IssueEmergencyCommandAsync(
        RiotEmergencyCommandKind kind,
        string deviceKey,
        CancellationToken cancellationToken);
}

/// <summary>
/// Reads a vehicle's emergency latch verbatim, from
/// <c>GET /api/task/v1/task/getVehicleInfo/{deviceKey}</c>.
/// </summary>
public interface IRiotVehicleEmergencyFacts
{
    Task<RiotVehicleEmergencyObservation> ReadEmergencyStateAsync(
        string deviceKey,
        CancellationToken cancellationToken);
}

/// <summary>
/// Whether RIoT holds any order for a vehicle that has not reached a terminal state.
/// </summary>
/// <remarks>
/// <paramref name="HasUnfinishedOrder"/> is <c>null</c> when RIoT could not be asked, or answered
/// with a page that does not cover every record. That is not "no order": REQ-0356 refuses a release
/// while the vehicle still has an unfinished order, and an unanswered question refuses it the same
/// way.
/// </remarks>
/// <param name="UnfinishedOrderIds">RIoT's <c>orderId</c> of each unfinished order found, for the refusal to name.</param>
public sealed record RiotVehicleOrderObservation(
    string DeviceKey,
    bool? HasUnfinishedOrder,
    IReadOnlyList<string> UnfinishedOrderIds,
    DateTimeOffset ObservedAt)
{
    /// <summary>Whether RIoT gave a complete answer.</summary>
    public bool IsKnown => HasUnfinishedOrder is not null;
}

/// <summary>
/// Reads the orders RIoT holds for one vehicle, from <c>GET /api/order/v1/orderRecord</c> by state.
/// </summary>
public interface IRiotVehicleOrderFacts
{
    Task<RiotVehicleOrderObservation> ReadUnfinishedOrdersAsync(
        string deviceKey,
        CancellationToken cancellationToken);
}
