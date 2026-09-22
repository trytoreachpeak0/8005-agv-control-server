using System.Text;
using System.Text.Json;
using ControlServer.Application;

namespace ControlServer.Host.Transport;

/// <summary>
/// The Onboard peers this server is currently talking to, one per vehicle.
/// </summary>
/// <remarks>
/// <para>
/// <b>N sessions, and they do not cross.</b> Each connection is filed under the <c>agvId</c> its
/// session established, so one vehicle's outbound traffic can only reach that vehicle's socket.
/// The single-connection version could not express a fleet at all: the second vehicle to attach
/// was refused, and until it was every message went to whichever peer had connected first.
/// </para>
/// <para>
/// <b>The address is in the message.</b> Every outbound envelope carries the <c>agvId</c> it was
/// built for — the publisher stamps it into the wire line and the peer verifies it — so routing
/// reads the addressee rather than taking it from a second argument that could disagree with the
/// payload. That also keeps <see cref="IOnboardPeer"/> unchanged, which matters because the port
/// is what the recovery and publisher tests are written against.
/// </para>
/// <para>
/// <b>No addressee, no send.</b> A line without a readable <c>agvId</c>, or one naming a vehicle
/// with no attached connection, throws rather than being broadcast or dropped. Both are the same
/// failure the single-connection version reported when nothing was attached, and the caller
/// already treats it as one.
/// </para>
/// <para>
/// <b>Only a connection whose handshake is done is in here</b> (control-server#259). Until the vehicle has
/// read the answer to its recovery report it reads one line per request and takes the next line as the
/// answer, so anything pushed in that window is read in place of an answer and the vehicle drops the
/// connection. This class is the one way server-originated traffic reaches a socket -- every sender holds an
/// <see cref="IOnboardPeer"/>, and this is its only implementation -- so keeping unfinished handshakes out of
/// the routing table gates every sender at once, including the next one somebody writes. To a sender, a
/// vehicle in its handshake is a vehicle that is not connected yet: the send throws, the outbox row stays
/// unacknowledged, and the replay that follows the recovery report or the runtime's next round delivers it,
/// exactly as after a reconnect. <c>OnboardOutboundFunnelArchitectureTests</c> pins the "one way".
/// </para>
/// <para>
/// <b>And only to the session it was built for.</b> Each connection is filed with the session generation its
/// handshake established, and a line stamped with another generation is refused the same way. A sender reads
/// the session, then sends: the runtime reads generation N at the top of a round and pushes some hundreds of
/// milliseconds later, and if the vehicle reconnected in between, routing by <c>agvId</c> alone handed the new
/// session a line for the old one. The onboard rejects such a line in its receive loop
/// (<c>STALE_SESSION_GENERATION</c>) and the loop ends, which drops the connection just as a push into the
/// handshake did (review of control-server#309). Refused here, the line stays unacknowledged, and the replays
/// that deliver held-back lines rewrite its generation to the current one.
/// </para>
/// <para>
/// <b>"Stays unacknowledged and is delivered later" holds for senders outside the connection's own loop</b> --
/// the runtime, the activation endpoint. A send from inside the loop, the deferred flush after a line
/// (<c>OnboardMessageProcessor.FlushDeferredOutboundAsync</c>, which ends in
/// <c>OnboardJourneyPublisher.SendPersistedAsync</c>), is not caught on the way out: a refusal there propagates
/// to <c>OnboardTcpServer.HandleClientAsync</c> and the server ends the connection, and the line is replayed
/// when the vehicle reconnects. Before the generation check such a line reached the vehicle and the vehicle
/// ended the connection instead, so the outcome for the vehicle is the same; what changed is which end closes.
/// </para>
/// </remarks>
public sealed class OnboardPeer : IOnboardPeer
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (OnboardPeerConnection Connection, long SessionGeneration)> _connections =
        new(StringComparer.Ordinal);

    /// <summary>Makes a connection routable. Refused unless its session has finished the handshake.</summary>
    internal void Attach(OnboardConnectionState session, OnboardPeerConnection connection)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!session.HandshakeCompleted ||
            session.SessionGeneration is null ||
            string.IsNullOrWhiteSpace(session.AgvId))
        {
            throw new InvalidOperationException(
                "An Onboard connection is routable only once its handshake is done; " +
                "until then the vehicle would read a push as the answer it is waiting for.");
        }
        string agvId = session.AgvId;
        long generation = session.SessionGeneration.Value;
        lock (_gate)
        {
            // One vehicle, one live connection. Two sockets claiming the same agvId is not a fleet,
            // it is the ambiguity the single-connection version refused, and it is still refused --
            // per vehicle now rather than for the server.
            if (_connections.TryGetValue(agvId, out (OnboardPeerConnection Connection, long) existing) &&
                !ReferenceEquals(existing.Connection, connection))
            {
                throw new InvalidOperationException($"An Onboard peer is already attached for '{agvId}'.");
            }

            _connections[agvId] = (connection, generation);
        }
    }

    internal void Detach(string agvId, OnboardPeerConnection connection)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agvId);
        lock (_gate)
        {
            if (_connections.TryGetValue(agvId, out (OnboardPeerConnection Connection, long) existing) &&
                ReferenceEquals(existing.Connection, connection))
            {
                _connections.Remove(agvId);
            }
        }
    }

    public Task SendAsync(ReadOnlyMemory<byte> ndjsonLine, CancellationToken cancellationToken)
    {
        (string agvId, long generation) = ReadAddressee(ndjsonLine.Span);
        (OnboardPeerConnection Connection, long SessionGeneration) attached;
        lock (_gate)
        {
            attached = _connections.TryGetValue(agvId, out (OnboardPeerConnection, long) found)
                ? found
                : throw new IOException($"No recovered Onboard peer is connected for '{agvId}'.");
        }
        if (attached.SessionGeneration != generation)
        {
            throw new IOException(
                $"The Onboard peer connected for '{agvId}' is in session generation {attached.SessionGeneration}; " +
                $"this line was built for generation {generation}.");
        }

        return attached.Connection.SendAsync(ndjsonLine, cancellationToken);
    }

    /// <summary>
    /// Reads the <c>agvId</c> and session generation the first envelope in this buffer is addressed to.
    /// </summary>
    /// <remarks>
    /// A buffer may hold several newline-terminated envelopes, and they are sent as one write, so
    /// they have to share a destination. Only the first is parsed and the rest ride with it: every
    /// caller builds a buffer for one session, and parsing each line to re-check would cost a JSON
    /// parse per message to detect a bug no caller can currently have.
    /// </remarks>
    private static (string AgvId, long SessionGeneration) ReadAddressee(ReadOnlySpan<byte> ndjsonLine)
    {
        int newline = ndjsonLine.IndexOf((byte)'\n');
        ReadOnlySpan<byte> first = newline < 0 ? ndjsonLine : ndjsonLine[..newline];
        try
        {
            using JsonDocument document = JsonDocument.Parse(Encoding.UTF8.GetString(first));
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("agvId", out JsonElement agvId) ||
                agvId.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(agvId.GetString()))
            {
                throw new InvalidDataException("Onboard outbound envelope must name the agvId it is addressed to.");
            }
            if (!root.TryGetProperty("sessionGeneration", out JsonElement generation) ||
                !generation.TryGetInt64(out long sessionGeneration))
            {
                throw new InvalidDataException(
                    "Onboard outbound envelope must name the session generation it was built for.");
            }
            return (agvId.GetString()!, sessionGeneration);
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("Onboard outbound data must be a JSON envelope.", error);
        }
    }
}

internal sealed class OnboardPeerConnection(Stream stream) : IAsyncDisposable
{
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    public async Task SendAsync(ReadOnlyMemory<byte> ndjsonLine, CancellationToken cancellationToken)
    {
        if (ndjsonLine.IsEmpty || ndjsonLine.Span[^1] != (byte)'\n')
        {
            throw new InvalidDataException("Onboard outbound data must be one or more newline-terminated NDJSON messages.");
        }

        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(ndjsonLine, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        _sendGate.Dispose();
        return ValueTask.CompletedTask;
    }

    public static ReadOnlyMemory<byte> Encode(string message) =>
        Encoding.UTF8.GetBytes(message.TrimEnd('\r', '\n') + "\n");
}
