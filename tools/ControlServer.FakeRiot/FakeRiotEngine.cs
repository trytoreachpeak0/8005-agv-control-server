using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ControlServer.FakeRiot;

/// <summary>Why a control-plane command was refused. Stable codes, never prose.</summary>
public static class FakeRiotReasonCodes
{
    public const string RunIdMismatch = "RUN_ID_MISMATCH";
    public const string CommandIdConflict = "COMMAND_ID_CONFLICT";
    public const string RevisionConflict = "REVISION_CONFLICT";
    public const string VehicleNotFound = "VEHICLE_NOT_FOUND";
    public const string OrderNotFound = "ORDER_NOT_FOUND";
    public const string InvalidArgument = "INVALID_ARGUMENT";
}

public sealed record CommandOutcome(
    bool Changed,
    bool Replayed,
    long AppliedRevision,
    string? ReasonCode = null)
{
    public bool Accepted => ReasonCode is null;
}

/// <summary>
/// The one place fake RIoT state changes. Both surfaces (the RIoT data plane the control server
/// talks to, and the loopback control plane a scenario drives) go through this object under one
/// lock, so there is never a second copy of the truth. The command identity and revision rules are
/// the ones slots-simulator already established in its
/// docs/EXTERNAL_AUTOMATION_CONTROL_API.md; an orchestrator that can drive one can drive the other.
/// </summary>
public sealed class FakeRiotEngine
{
    private static readonly JsonSerializerOptions FingerprintOptions = new(JsonSerializerDefaults.Web);
    private readonly object gate = new();
    private readonly Dictionary<string, CommandReceipt> commandsByRunAndId = new(StringComparer.Ordinal);
    private readonly FakeRiotSeed seed;
    private CommandReceipt? lastResetReceipt;
    private FakeRiotState state;

    public FakeRiotEngine(FakeRiotSeed seed, string instanceId)
    {
        ArgumentNullException.ThrowIfNull(seed);
        this.seed = seed;
        InstanceId = instanceId;
        state = seed.BuildInitialState(NewRunId());
    }

    public string InstanceId { get; }

    public FakeRiotState Snapshot()
    {
        lock (gate)
        {
            return state;
        }
    }

    /// <summary>
    /// Establishes a new round. Reset is the one command checked against an instance-level receipt
    /// before its runId is validated: a reset whose response was lost must be retryable with the
    /// same commandId and return the same new runId, not create a second round.
    /// </summary>
    public CommandOutcome Reset(string? runId, string commandId, long? expectedRevision, object requestContent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandId);
        lock (gate)
        {
            string fingerprint = Fingerprint("reset", requestContent);
            if (lastResetReceipt is { } receipt && receipt.CommandId == commandId)
            {
                return receipt.Fingerprint == fingerprint
                    ? new CommandOutcome(false, true, receipt.AppliedRevision)
                    : new CommandOutcome(false, false, state.Revision, FakeRiotReasonCodes.CommandIdConflict);
            }
            if (runId is not null && runId != state.RunId)
            {
                return new CommandOutcome(false, false, state.Revision, FakeRiotReasonCodes.RunIdMismatch);
            }
            if (expectedRevision is { } expected && expected != state.Revision)
            {
                return new CommandOutcome(false, false, state.Revision, FakeRiotReasonCodes.RevisionConflict);
            }

            commandsByRunAndId.Clear();
            state = seed.BuildInitialState(NewRunId());
            lastResetReceipt = new CommandReceipt(commandId, fingerprint, state.Revision);
            return new CommandOutcome(true, false, state.Revision);
        }
    }

    /// <summary>
    /// Applies one control-plane command. <paramref name="mutate"/> returns null when the command
    /// is legal but changes nothing observable, which must not move the revision.
    /// </summary>
    public CommandOutcome Apply(
        string operation,
        string runId,
        string commandId,
        long? expectedRevision,
        object requestContent,
        Func<FakeRiotState, FakeRiotState?> mutate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandId);
        ArgumentNullException.ThrowIfNull(mutate);
        lock (gate)
        {
            if (runId != state.RunId)
            {
                return new CommandOutcome(false, false, state.Revision, FakeRiotReasonCodes.RunIdMismatch);
            }
            string key = state.RunId + " " + commandId;
            string fingerprint = Fingerprint(operation, requestContent);
            if (commandsByRunAndId.TryGetValue(key, out CommandReceipt? receipt))
            {
                // A replay is answered from the receipt even when its expectedRevision has since
                // gone stale. That is the point of retrying a command whose response was lost;
                // only a different body under the same commandId is a conflict.
                return receipt.Fingerprint == fingerprint
                    ? new CommandOutcome(false, true, receipt.AppliedRevision)
                    : new CommandOutcome(false, false, state.Revision, FakeRiotReasonCodes.CommandIdConflict);
            }
            if (expectedRevision is { } expected && expected != state.Revision)
            {
                return new CommandOutcome(false, false, state.Revision, FakeRiotReasonCodes.RevisionConflict);
            }

            FakeRiotState? next;
            try
            {
                next = mutate(state);
            }
            catch (FakeRiotCommandException error)
            {
                return new CommandOutcome(false, false, state.Revision, error.ReasonCode);
            }

            bool changed = next is not null;
            if (next is not null)
            {
                state = next with { Revision = state.Revision + 1 };
            }
            commandsByRunAndId[key] = new CommandReceipt(commandId, fingerprint, state.Revision);
            return new CommandOutcome(changed, false, state.Revision);
        }
    }

    /// <summary>
    /// The data-plane order create. RIoT itself deduplicates on upperId rather than on a caller
    /// command id, so this deliberately does not go through the command receipt table: a repeated
    /// create for a live upperId must answer the way RIoT does, with business code 0610008.
    /// Returns null when the upperId is already taken.
    /// </summary>
    public FakeOrder? CreateOrder(string upperId, string? appointVehicleKey, IReadOnlyList<FakeMission> missions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(upperId);
        ArgumentNullException.ThrowIfNull(missions);
        lock (gate)
        {
            if (state.OrdersByUpperId.ContainsKey(upperId))
            {
                return null;
            }
            long sequence = state.NextOrderSequence;
            FakeOrder order = new()
            {
                Id = 488000 + sequence,
                OrderId = "ORDER-" + sequence.ToString("D6", System.Globalization.CultureInfo.InvariantCulture),
                UpperId = upperId,
                OrderState = 1,
                AppointVehicleKey = appointVehicleKey,
                ExecuteVehicleKey = "--",
                EndStationNo = missions.Count > 0 ? missions[missions.Count - 1].Destination : null,
                Missions = missions
            };
            Dictionary<string, FakeOrder> orders = new(state.OrdersByUpperId, StringComparer.Ordinal)
            {
                [upperId] = order
            };
            state = state with
            {
                OrdersByUpperId = orders,
                NextOrderSequence = sequence + 1,
                Revision = state.Revision + 1
            };
            return order;
        }
    }

    private static string NewRunId() => string.Concat("run-", Guid.NewGuid().ToString("N").AsSpan(0, 8));

    private static string Fingerprint(string operation, object content)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(
            operation + " " + JsonSerializer.Serialize(content, FingerprintOptions));
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private sealed record CommandReceipt(string CommandId, string Fingerprint, long AppliedRevision);
}

/// <summary>Thrown from a mutation to refuse the command with a stable reason code.</summary>
public sealed class FakeRiotCommandException : Exception
{
    public FakeRiotCommandException(string reasonCode)
        : base(reasonCode) => ReasonCode = reasonCode;

    public FakeRiotCommandException()
        : this(FakeRiotReasonCodes.InvalidArgument)
    {
    }

    public FakeRiotCommandException(string reasonCode, Exception innerException)
        : base(reasonCode, innerException) => ReasonCode = reasonCode;

    public string ReasonCode { get; }
}
