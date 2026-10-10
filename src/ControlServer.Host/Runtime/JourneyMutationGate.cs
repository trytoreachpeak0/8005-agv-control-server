namespace ControlServer.Host.Runtime;

/// <summary>
/// The one lock that keeps a change made from outside the runtime loop from interleaving with a round of it
/// (control-server#299).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why there is a lock here at all, when the runtime deliberately has none.</b> The demand release service runs
/// inside the runtime loop, after the engine, precisely so that the two cannot interleave without a lock
/// (<see cref="JourneyRuntimeWorker"/>). A person's request arrives on an HTTP thread and cannot join the loop that way.
/// Left unserialised, the dangerous interleaving is concrete: the engine reads a journey still waiting at its arrival
/// stage, the fault recovery closes that journey and clears the vehicle's fault, and the engine then observes the same
/// FAILED order it read a moment ago and records a new fault on a vehicle that no longer has a journey -- with its
/// hold, its stop proof and possibly an emergency stop.
/// </para>
/// <para>
/// The loop takes it for a whole round (engine and release service together). A request reads RIoT before it and takes
/// it to re-read this server's tables, decide and write -- no RIoT call is made under it (see
/// <see cref="Faults.VehicleFaultRecoveryService"/>). A singleton in the host, so there is exactly one.
/// </para>
/// </remarks>
public sealed class JourneyMutationGate : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);

    /// <summary>Waits for the gate, returning null when <paramref name="timeout"/> ran out first.</summary>
    public async Task<IDisposable?> TryEnterAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        return await gate.WaitAsync(timeout, cancellationToken).ConfigureAwait(false) ? new Releaser(gate) : null;
    }

    /// <summary>Waits for the gate for as long as it takes.</summary>
    public async Task<IDisposable> EnterAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Releaser(gate);
    }

    public void Dispose() => gate.Dispose();

    private sealed class Releaser(SemaphoreSlim gate) : IDisposable
    {
        private int released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref released, 1) == 0)
            {
                gate.Release();
            }
        }
    }
}
