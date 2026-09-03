using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ControlServer.TestDoubles;

/// <summary>Why a control-plane command was refused. Stable codes, never prose.</summary>
public static class ReasonCodes
{
    public const string RunIdMismatch = "RUN_ID_MISMATCH";
    public const string CommandIdConflict = "COMMAND_ID_CONFLICT";
    public const string RevisionConflict = "REVISION_CONFLICT";
    public const string InvalidArgument = "INVALID_ARGUMENT";
    public const string NotFound = "NOT_FOUND";
    public const string NotAllowedInState = "NOT_ALLOWED_IN_STATE";
}

public sealed record CommandOutcome(
    bool Changed,
    bool Replayed,
    long AppliedRevision,
    string? ReasonCode = null)
{
    public bool Accepted => ReasonCode is null;
}

/// <summary>Thrown from a mutation to refuse the command with a stable reason code.</summary>
public sealed class CommandRefusedException : Exception
{
    public CommandRefusedException(string reasonCode)
        : base(reasonCode) => ReasonCode = reasonCode;

    public CommandRefusedException()
        : this(ReasonCodes.InvalidArgument)
    {
    }

    public CommandRefusedException(string reasonCode, Exception innerException)
        : base(reasonCode, innerException) => ReasonCode = reasonCode;

    public string ReasonCode { get; }
}

/// <summary>One round's identity plus the domain state it carries.</summary>
public sealed record EngineSnapshot<TState>(string RunId, long Revision, TState State);

/// <summary>
/// The round, command-identity and revision rules every test double in this workspace shares.
/// They are the ones slots-simulator established in its docs/EXTERNAL_AUTOMATION_CONTROL_API.md, so
/// one orchestrator can drive the simulator, the fake RIoT, the fake MesIngest and the fake Onboard
/// without four sets of conventions. Keeping them in one class is the only way that stays true:
/// four copies of an idempotency rule are four chances to drift.
/// </summary>
/// <remarks>
/// All state lives behind one lock and <typeparamref name="TState"/> is expected to be immutable,
/// so a snapshot handed to a request thread can never be torn.
/// </remarks>
public sealed class CommandEngine<TState>
{
    private static readonly JsonSerializerOptions FingerprintOptions = new(JsonSerializerDefaults.Web);
    private readonly object gate = new();
    private readonly Dictionary<string, Receipt> commandsByRunAndId = new(StringComparer.Ordinal);
    private readonly Func<TState> buildInitialState;
    private Receipt? lastResetReceipt;
    private string runId;
    private long revision;
    private TState state;

    public CommandEngine(string instanceId, Func<TState> buildInitialState)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentNullException.ThrowIfNull(buildInitialState);
        InstanceId = instanceId;
        this.buildInitialState = buildInitialState;
        runId = NewRunId();
        // A fresh round starts at 1, the floor slots-simulator uses, so an orchestrator can assert
        // an absolute revision after a reset rather than a relative one.
        revision = 1;
        state = buildInitialState();
    }

    public string InstanceId { get; }

    public EngineSnapshot<TState> Snapshot()
    {
        lock (gate)
        {
            return new EngineSnapshot<TState>(runId, revision, state);
        }
    }

    /// <summary>
    /// Establishes a new round. Reset is the one command matched against an instance-level receipt
    /// before its runId is validated: a reset whose response was lost must be retryable with the
    /// same commandId and return the same new runId, not create a second round.
    /// </summary>
    public CommandOutcome Reset(string? requestedRunId, string commandId, long? expectedRevision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandId);
        lock (gate)
        {
            const string fingerprint = "reset";
            if (lastResetReceipt is { } receipt && receipt.CommandId == commandId)
            {
                return receipt.Fingerprint == fingerprint
                    ? new CommandOutcome(false, true, receipt.AppliedRevision)
                    : new CommandOutcome(false, false, revision, ReasonCodes.CommandIdConflict);
            }
            if (requestedRunId is not null && requestedRunId != runId)
            {
                return new CommandOutcome(false, false, revision, ReasonCodes.RunIdMismatch);
            }
            if (expectedRevision is { } expected && expected != revision)
            {
                return new CommandOutcome(false, false, revision, ReasonCodes.RevisionConflict);
            }

            commandsByRunAndId.Clear();
            runId = NewRunId();
            revision = 1;
            state = buildInitialState();
            lastResetReceipt = new Receipt(commandId, fingerprint, revision);
            return new CommandOutcome(true, false, revision);
        }
    }

    /// <summary>
    /// Applies one command. <paramref name="mutate"/> returns the default value of
    /// <typeparamref name="TState"/> -- null for a reference type -- when the command is legal but
    /// changes nothing observable, which must not move the revision.
    /// </summary>
    public CommandOutcome Apply(
        string operation,
        string? requestedRunId,
        string commandId,
        long? expectedRevision,
        object businessContent,
        Func<TState, TState?> mutate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandId);
        ArgumentNullException.ThrowIfNull(mutate);
        lock (gate)
        {
            if (requestedRunId != runId)
            {
                return new CommandOutcome(false, false, revision, ReasonCodes.RunIdMismatch);
            }
            string key = runId + " " + commandId;
            string fingerprint = Fingerprint(operation, businessContent);
            if (commandsByRunAndId.TryGetValue(key, out Receipt? receipt))
            {
                // A replay is answered from the receipt even when its expectedRevision has since
                // gone stale. That is the point of retrying a command whose response was lost;
                // only a different body under the same commandId is a conflict.
                return receipt.Fingerprint == fingerprint
                    ? new CommandOutcome(false, true, receipt.AppliedRevision)
                    : new CommandOutcome(false, false, revision, ReasonCodes.CommandIdConflict);
            }
            if (expectedRevision is { } expected && expected != revision)
            {
                return new CommandOutcome(false, false, revision, ReasonCodes.RevisionConflict);
            }

            TState? next;
            try
            {
                next = mutate(state);
            }
            catch (CommandRefusedException error)
            {
                return new CommandOutcome(false, false, revision, error.ReasonCode);
            }

            bool changed = next is not null;
            if (next is not null)
            {
                state = next;
                revision++;
            }
            commandsByRunAndId[key] = new Receipt(commandId, fingerprint, revision);
            return new CommandOutcome(changed, false, revision);
        }
    }

    /// <summary>
    /// Changes state from inside the double itself rather than from a command -- an order created
    /// through the RIoT data plane, a message received by the fake peer. There is no command
    /// identity to deduplicate on, so the caller owns idempotency; the revision still moves.
    /// </summary>
    public TResult Mutate<TResult>(Func<TState, (TState? Next, TResult Result)> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        lock (gate)
        {
            (TState? next, TResult result) = mutate(state);
            if (next is not null)
            {
                state = next;
                revision++;
            }
            return result;
        }
    }

    private static string NewRunId() => string.Concat("run-", Guid.NewGuid().ToString("N").AsSpan(0, 8));

    private static string Fingerprint(string operation, object content)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(
            operation + " " + JsonSerializer.Serialize(content, FingerprintOptions));
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private sealed record Receipt(string CommandId, string Fingerprint, long AppliedRevision);
}
