using System.Collections.Concurrent;

namespace ControlServer.Host.Runtime.Faults;

/// <summary>
/// At most one resume in flight per vehicle, in this process (control-server#299, second review item 1).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the gate does not do this.</b> A resume is judged under <see cref="JourneyMutationGate"/>, and its
/// <c>CONTINUE_FROM_HELD</c> goes to RIoT after the gate is released, so that a slow RIoT cannot hold the fleet's runtime
/// round. That leaves a window: while the first request's continue is on its way, a second request takes the gate, still
/// reads the order HELD and the fault in effect, passes every criterion and continues again -- the command service writes
/// one audit row per attempt and does not deduplicate. A flight is taken before anything is read and held until the
/// continue has been confirmed or refused, so the second request is refused instead.
/// </para>
/// <para>
/// <b>In process is enough</b> because one ControlServer drives a vehicle. A singleton in the host. Taking a flight never
/// waits: a second request is told the first is still running, and asked again it finds the fault already resumed.
/// </para>
/// </remarks>
public sealed class VehicleFaultResumeFlights
{
    private readonly ConcurrentDictionary<string, byte> inFlight = new(StringComparer.Ordinal);

    /// <summary>Takes the vehicle's flight, or returns null when a resume for it is already in flight.</summary>
    public IDisposable? TryBegin(string agvId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agvId);
        return inFlight.TryAdd(agvId, 0) ? new Flight(inFlight, agvId) : null;
    }

    private sealed class Flight(ConcurrentDictionary<string, byte> inFlight, string agvId) : IDisposable
    {
        private int ended;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref ended, 1) == 0)
            {
                inFlight.TryRemove(agvId, out _);
            }
        }
    }
}
