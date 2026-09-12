namespace ControlServer.FakeOnboard;

/// <summary>How the peer answers one kind of request from ControlServer.</summary>
public enum AnswerMode
{
    /// <summary>Answer as a working vehicle would, as soon as the request arrives.</summary>
    Auto,

    /// <summary>Hold the request open until the scenario answers it through the control plane.</summary>
    Manual,

    /// <summary>Never answer. This is what a station operation running out its own timeout looks like.</summary>
    Silent
}

/// <summary>The abstract safety summary this peer currently stands behind (ADR-cross-0033).</summary>
public sealed record SafetySummary
{
    public bool DepartureSafe { get; init; } = true;
    public bool VehicleStopped { get; init; } = true;
    public bool AllTargetSlotsLocked { get; init; } = true;
    public bool AllUnlockOutputsReset { get; init; } = true;
    public bool UnknownPresent { get; init; }
    public IReadOnlyList<string> ReasonCodes { get; init; } = [];
}

/// <summary>A request from ControlServer this peer has received and not yet answered.</summary>
public sealed record PendingRequest(
    string MessageType,
    string MessageId,
    string Key,
    string PayloadJson,
    DateTimeOffset ReceivedAt);

public sealed record FakeOnboardPolicy
{
    public AnswerMode Sublot { get; init; } = AnswerMode.Auto;
    public AnswerMode LoadResult { get; init; } = AnswerMode.Auto;
    public AnswerMode UnloadResult { get; init; } = AnswerMode.Auto;
    public AnswerMode SafetyCheck { get; init; } = AnswerMode.Auto;

    /// <summary>Protocol v2 message 7. Manual is how a scenario withholds a result across a disconnect.</summary>
    public AnswerMode SlotConfigurationActivation { get; init; } = AnswerMode.Auto;
}

/// <summary>
/// One alarm this peer currently reports, in the shape protocol v2's <c>AlarmEntry</c> carries.
/// </summary>
public sealed record FakeAlarm
{
    public string AlarmId { get; init; } = string.Empty;
    public string Code { get; init; } = string.Empty;
    public string Severity { get; init; } = "WARNING";
    public DateTimeOffset RaisedAt { get; init; }
    public string SubjectType { get; init; } = "VEHICLE";
    public string? SubjectId { get; init; }
    public string? DisplayMessage { get; init; }
}

/// <summary>
/// What this peer concluded about one activation. Kept so that a replayed command gets this answer
/// again rather than a second activation -- the peer-side half of <c>PENDING_RESULT_REPLAY</c>.
/// </summary>
public sealed record FakeActivationOutcome(
    string ActivationId,
    string Outcome,
    string? ReasonCode,
    string ActiveSlotConfigurationVersion,
    string ActiveSlotConfigurationFingerprint,
    DateTimeOffset VerifiedAt);

public sealed record FakeOnboardState
{
    public long SessionGeneration { get; init; }
    public string Readiness { get; init; } = "DISCONNECTED";
    public string? ReadinessReasonCode { get; init; }
    public long SafetyStateVersion { get; init; } = 1;
    public SafetySummary Safety { get; init; } = new();
    public FakeOnboardPolicy Policy { get; init; } = new();

    /// <summary>Open requests keyed by the identity a scenario names them with.</summary>
    public IReadOnlyDictionary<string, PendingRequest> Pending { get; init; } =
        new Dictionary<string, PendingRequest>(StringComparer.Ordinal);

    /// <summary>
    /// The configuration CapabilitySnapshot reports. This peer has no IO to compute a digest from, so
    /// it adopts the target of every activation it accepts; that the two ends compute the same digest
    /// is the G3 runner's claim against the real onboard, not this peer's.
    /// </summary>
    public string ActiveSlotConfigurationVersion { get; init; } = "fake-slot-config-v1";

    public string ActiveSlotConfigurationFingerprint { get; init; } = new('0', 64);

    public IReadOnlyDictionary<string, FakeActivationOutcome> ActivationOutcomes { get; init; } =
        new Dictionary<string, FakeActivationOutcome>(StringComparer.Ordinal);

    /// <summary>
    /// The messageId of every SlotConfigurationActivationCommand received, in arrival order, repeats
    /// included. A scenario reads the replay off this rather than off the bounded wire log.
    /// </summary>
    public IReadOnlyList<string> ActivationCommandMessageIds { get; init; } = [];

    public int ActivationResultsSent { get; init; }

    /// <summary>Protocol v2 message 9. Every change of <see cref="Alarms"/> moves this on by one.</summary>
    public long AlarmSnapshotRevision { get; init; } = 1;

    public IReadOnlyList<FakeAlarm> Alarms { get; init; } = [];
}

/// <summary>
/// One message seen in either direction. This is the peer's half of the timeline; the
/// orchestrator merges it with the server's database observations.
/// </summary>
public sealed record WireEvent(string Direction, string MessageType, string MessageId, DateTimeOffset At);
