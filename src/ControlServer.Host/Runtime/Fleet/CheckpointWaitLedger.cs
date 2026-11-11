using System.Collections.Concurrent;

namespace ControlServer.Host.Runtime.Fleet;

/// <summary>The RIoT <c>movementState</c> values this server names rather than merely reads.</summary>
/// <remarks>
/// Seven values have been observed in the behaviour lab. Two of them — <c>MT_FINISHED</c> and
/// <c>MT_PAUSED</c> — are the closed list <c>HttpRiotMovementGateway</c> reads as "not moving", and
/// that list is a safety input: adding to it weakens REQ-0247's stop proof. This constant is the
/// other kind of recognition. It does not change what the state means for motion — a vehicle
/// waiting at a checkpoint may still be rolling, so it stays
/// <see cref="ControlServer.Application.VehicleMotionReading.Unknown"/> — it only lets the runtime
/// say why a journey that is not arriving is not arriving.
/// </remarks>
public static class RiotMovementStates
{
    /// <summary>
    /// The vehicle is holding at a traffic checkpoint. Normal with several vehicles on one map, and
    /// essentially absent with one, which is why B2 is where it first has to be handled.
    /// </summary>
    public const string WaitForCheckpoint = "MT_WAIT_FOR_CHECKPOINT";
}

/// <summary>How long each vehicle has been holding at a checkpoint, in this process's memory.</summary>
/// <remarks>
/// <para>
/// <b>Not persisted, for the same reason the motion ledger is not.</b> The quantity is "how long
/// have we been watching this vehicle wait"; a value carried across a restart would be a claim
/// about a stretch of time during which nobody was watching. After a restart the wait is measured
/// again from the first observation, which is the honest answer and errs towards waiting longer
/// rather than raising an alarm this process cannot support.
/// </para>
/// <para>
/// Keyed on the RIoT vehicle key because that is what the motion sample is keyed on. A vehicle that
/// stops waiting is forgotten, so a later wait starts its own clock rather than inheriting one.
/// </para>
/// </remarks>
public sealed class CheckpointWaitLedger
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _waitingSince = new(StringComparer.Ordinal);

    /// <summary>
    /// Records that <paramref name="vehicleKey"/> is holding at a checkpoint and returns how long
    /// it has been holding, counting from the first observation this process made.
    /// </summary>
    public TimeSpan Observe(string vehicleKey, DateTimeOffset observedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vehicleKey);

        DateTimeOffset since = _waitingSince.GetOrAdd(vehicleKey, observedAt);
        // A sample stamped before the one that opened the window says nothing about how long the
        // wait has lasted; reporting a negative duration would read as "no wait at all".
        return observedAt > since ? observedAt - since : TimeSpan.Zero;
    }

    /// <summary>Forgets <paramref name="vehicleKey"/>'s wait, because it is no longer waiting.</summary>
    public void Clear(string vehicleKey) => _waitingSince.TryRemove(vehicleKey, out _);

    /// <summary>
    /// How often a vehicle behind the readiness gate is asked whether it holds at a checkpoint (control-server#866).
    /// </summary>
    /// <remarks>
    /// A real onboard is not ready for the whole of a leg that carries this server's order, so behind the gate is where a
    /// driving vehicle spends every round, and a motion sample is two RIoT reads. Asked every round, that is two more requests
    /// per driving vehicle per round, on the round that also dispatches. The budget a checkpoint wait is judged against is
    /// minutes and the waiting journey warning is ten, so half a minute late changes no outcome.
    /// </remarks>
    public static readonly TimeSpan BehindTheGateSampleInterval = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, DateTimeOffset> _sampledBehindTheGateAt = new(StringComparer.Ordinal);

    /// <summary>
    /// Whether <paramref name="vehicleKey"/> is due a checkpoint sample behind the readiness gate at <paramref name="now"/>;
    /// when it is, <paramref name="now"/> is recorded as its latest one. The first time is always due.
    /// </summary>
    public bool TakeBehindTheGateSample(string vehicleKey, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vehicleKey);
        if (_sampledBehindTheGateAt.TryGetValue(vehicleKey, out DateTimeOffset last) &&
            now - last < BehindTheGateSampleInterval &&
            now >= last)
        {
            return false;
        }
        _sampledBehindTheGateAt[vehicleKey] = now;
        return true;
    }
}
