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
/// </remarks>
public sealed class OnboardPeer : IOnboardPeer
{
    private readonly object _gate = new();
    private readonly Dictionary<string, OnboardPeerConnection> _connections = new(StringComparer.Ordinal);

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
        lock (_gate)
        {
            // One vehicle, one live connection. Two sockets claiming the same agvId is not a fleet,
            // it is the ambiguity the single-connection version refused, and it is still refused --
            // per vehicle now rather than for the server.
            if (_connections.TryGetValue(agvId, out OnboardPeerConnection? existing) &&
                !ReferenceEquals(existing, connection))
            {
                throw new InvalidOperationException($"An Onboard peer is already attached for '{agvId}'.");
            }

            _connections[agvId] = connection;
        }
    }

    internal void Detach(string agvId, OnboardPeerConnection connection)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agvId);
        lock (_gate)
        {
            if (_connections.TryGetValue(agvId, out OnboardPeerConnection? existing) &&
                ReferenceEquals(existing, connection))
            {
                _connections.Remove(agvId);
            }
        }
    }

    public Task SendAsync(ReadOnlyMemory<byte> ndjsonLine, CancellationToken cancellationToken)
    {
        string agvId = ReadAddressee(ndjsonLine.Span);
        OnboardPeerConnection connection;
        lock (_gate)
        {
            connection = _connections.TryGetValue(agvId, out OnboardPeerConnection? attached)
                ? attached
                : throw new IOException($"No recovered Onboard peer is connected for '{agvId}'.");
        }

        return connection.SendAsync(ndjsonLine, cancellationToken);
    }

    /// <summary>
    /// Reads the <c>agvId</c> the first envelope in this buffer is addressed to.
    /// </summary>
    /// <remarks>
    /// A buffer may hold several newline-terminated envelopes, and they are sent as one write, so
    /// they have to share a destination. Only the first is parsed and the rest ride with it: every
    /// caller builds a buffer for one session, and parsing each line to re-check would cost a JSON
    /// parse per message to detect a bug no caller can currently have.
    /// </remarks>
    private static string ReadAddressee(ReadOnlySpan<byte> ndjsonLine)
    {
        int newline = ndjsonLine.IndexOf((byte)'\n');
        ReadOnlySpan<byte> first = newline < 0 ? ndjsonLine : ndjsonLine[..newline];
        try
        {
            using JsonDocument document = JsonDocument.Parse(Encoding.UTF8.GetString(first));
            if (document.RootElement.TryGetProperty("agvId", out JsonElement agvId) &&
                agvId.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(agvId.GetString()))
            {
                return agvId.GetString()!;
            }
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("Onboard outbound data must be a JSON envelope.", error);
        }

        throw new InvalidDataException("Onboard outbound envelope must name the agvId it is addressed to.");
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
