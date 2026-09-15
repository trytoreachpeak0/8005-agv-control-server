namespace ControlServer.EmergencyDrill;

/// <summary>
/// drill-state.json: what this run has already done. It is written <em>before</em> every call that
/// moves the vehicle, stops it, releases it or cancels its order, so a crash between the write and
/// the call still reads as "attempted" and the next invocation refuses to repeat it.
/// </summary>
internal sealed class DrillState
{
    public string Schema { get; set; } = "w1-emergency-drill-state/1";
    public string RunId { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public string CreatedOnHost { get; set; } = string.Empty;
    public string RiotBaseUrl { get; set; } = string.Empty;
    public string DeviceKey { get; set; } = string.Empty;
    public string VehicleAlias { get; set; } = string.Empty;
    public int MapId { get; set; }
    public string MapIdentity { get; set; } = string.Empty;
    public bool FakeRiot { get; set; }
    public PreflightRecord? Preflight { get; set; }
    public OrderRecord? Order { get; set; }
    public WatchRecord? LastWatch { get; set; }
    public TriggerRecord? Trigger { get; set; }
    public ReleaseRecord? Release { get; set; }
    public CancelOrderRecord? CancelOrder { get; set; }
    public MotionRecord? LatestMotion { get; set; }
    public EmergencyRecord? LatestEmergency { get; set; }
    public bool CanNotRecoverObserved { get; set; }
    public DateTimeOffset? SummarizedAt { get; set; }

    public string UpperId => "W1-DRILL-" + RunId;
}

internal sealed class PreflightRecord
{
    public DateTimeOffset At { get; set; }
    public string Host { get; set; } = string.Empty;
    public bool Passed { get; set; }
    public string RouteVerdict { get; set; } = string.Empty;
    public string? AcceptedRouteReason { get; set; }
    public double? LatencyMedianMs { get; set; }
    public double? LatencyMaxMs { get; set; }
    public List<string> Failures { get; set; } = [];
}

internal sealed class OrderRecord
{
    public string UpperId { get; set; } = string.Empty;
    public int StartStationId { get; set; }
    public int DestinationStationId { get; set; }
    public DateTimeOffset AttemptedAt { get; set; }
    public string Disposition { get; set; } = "IN_FLIGHT";
    public string? CreateReceipt { get; set; }
    public string? OrderId { get; set; }
    public int? OrderState { get; set; }
    public string? ObservationKind { get; set; }
}

internal sealed class WatchRecord
{
    public DateTimeOffset At { get; set; }
    public string Outcome { get; set; } = string.Empty;
    public int Samples { get; set; }
}

internal sealed class CallReceiptRecord
{
    public string Operation { get; set; } = string.Empty;
    public string Disposition { get; set; } = string.Empty;
    public string Classification { get; set; } = string.Empty;
    public int? HttpStatusCode { get; set; }
    public string? BusinessCode { get; set; }
    public string? FailureCategory { get; set; }
    public double ElapsedMs { get; set; }
}

internal sealed class TriggerRecord
{
    public DateTimeOffset AttemptedAt { get; set; }
    public bool AllowStationary { get; set; }
    public MotionRecord? PreSample { get; set; }
    public string Disposition { get; set; } = "IN_FLIGHT";
    public CallReceiptRecord? Receipt { get; set; }
    public int ObserveSeconds { get; set; }
    public int Samples { get; set; }
    public int ReadFailures { get; set; }
    public bool Latched { get; set; }
    public string? LatchState { get; set; }
    public double? MsToLatch { get; set; }
    public bool Stopped { get; set; }
    public double? MsToStop { get; set; }
    public int StopStreak { get; set; }
    public bool? StopStreakProductReadingNotMoving { get; set; }
    /// <summary>Set when the stop was first observed: whether it rested on speed 0 + MT_RUNNING under an engaged latch.</summary>
    public bool? StillWhileLatchedRunning { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

internal sealed class ReleaseRecord
{
    public DateTimeOffset AttemptedAt { get; set; }
    public string FieldConfirmedBy { get; set; } = string.Empty;
    public string FieldConfirmation { get; set; } = string.Empty;
    public string? PreLatch { get; set; }
    public int? OrderStateAtRelease { get; set; }
    /// <summary>Whether the stillness guard passed on speed 0 + MT_RUNNING under the engaged latch.</summary>
    public bool StillWhileLatchedRunning { get; set; }
    public string Disposition { get; set; } = "IN_FLIGHT";
    public CallReceiptRecord? Receipt { get; set; }
    public bool OkObserved { get; set; }
    public double? MsToOk { get; set; }
    public string? LastLatch { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

internal sealed class CancelOrderRecord
{
    public DateTimeOffset AttemptedAt { get; set; }
    public string OrderId { get; set; } = string.Empty;
    public string? LatchAtCancel { get; set; }
    /// <summary>Whether the stillness guard passed on speed 0 + MT_RUNNING under the engaged latch.</summary>
    public bool StillWhileLatchedRunning { get; set; }
    public bool AlreadyTerminal { get; set; }
    public string Disposition { get; set; } = "IN_FLIGHT";
    public CallReceiptRecord? Receipt { get; set; }
    public int? OrderStateAfter { get; set; }
    public bool TerminalObserved { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

internal sealed class MotionRecord
{
    public DateTimeOffset At { get; set; }
    public string Reading { get; set; } = string.Empty;
    public string? MovementState { get; set; }
    public double? Speed { get; set; }
    public string? CurrentMap { get; set; }
    public int? CurrentStationId { get; set; }
    public bool MovingBetweenStations { get; set; }
}

internal sealed class EmergencyRecord
{
    public DateTimeOffset At { get; set; }
    public string? State { get; set; }
}
