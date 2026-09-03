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
}

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
}

/// <summary>
/// One message seen in either direction. This is the peer's half of the timeline; the
/// orchestrator merges it with the server's database observations.
/// </summary>
public sealed record WireEvent(string Direction, string MessageType, string MessageId, DateTimeOffset At);
