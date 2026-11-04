using System.Collections.Concurrent;

namespace ControlServer.Host.Transport;

/// <summary>
/// Vehicles whose readiness the server changed on its own, with no inbound message to answer, and which have to be told
/// (control-server#837). The server announces <c>SessionReadiness</c> only on its answers to the vehicle's messages; a change
/// made by a background round -- a load cancellation that timed out into recovery -- would otherwise wait for a reconnect or
/// the next safety change, and a vehicle standing still at its stop may have neither for a long time, its recovery entries
/// dark. The round registers the vehicle here; the vehicle's next <c>Heartbeat</c> (about every two seconds) is answered with
/// its readiness decided afresh, through the one place an answer takes a readiness line, so the handshake rule holds and the
/// connection's own readiness stays the one it was told.
/// </summary>
/// <remarks>
/// A singleton, so the per-round engine and the per-connection processor share it. Kept in memory only: a restart ends every
/// connection, and the next handshake's recovery report announces readiness unconditionally.
/// </remarks>
public sealed class SessionReadinessRecheck
{
    private readonly ConcurrentDictionary<string, byte> _due = new(StringComparer.Ordinal);

    /// <summary>The vehicle's next heartbeat is to be answered with its readiness.</summary>
    public void Request(string agvId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agvId);
        _due[agvId] = 0;
    }

    /// <summary>Whether a recheck was due for the vehicle; it is no longer due afterwards.</summary>
    public bool TryTake(string agvId) => _due.TryRemove(agvId, out _);
}
