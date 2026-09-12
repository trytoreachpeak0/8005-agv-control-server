using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ControlServer.TestDoubles;

namespace ControlServer.ProtocolFaultProxy;

/// <summary>
/// The data plane: a TCP listener the onboard connects to instead of the server, one upstream
/// connection per onboard connection, NDJSON lines copied both ways.
/// </summary>
public sealed class ProtocolRelay(
    ProtocolFaultProxyHost.RelayEndpoints endpoints,
    CommandEngine<ProtocolFaultProxyState> engine,
    TrafficLog log,
    RelayConnections connections) : BackgroundService
{
    public const string OnboardToServer = "onboard->server";
    public const string ServerToOnboard = "server->onboard";
    public const string DisconnectedOnRequest = "relay disconnected on request";

    // Both ends frame a line with a bare LF (OnboardPeer.Encode, WireToGateProtocolSerializer.SerializeLine)
    // and both hash the bytes of a line, so the relay must hand each one on exactly: read it without its
    // LF, write it back with one. A CR would not survive that, and neither end sends one.
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        TcpListener listener = new(endpoints.Listen);
        listener.Start();
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                TcpClient onboard = await listener.AcceptTcpClientAsync(stoppingToken).ConfigureAwait(false);
                int connection = log.OpenConnection();
                _ = RelayAsync(onboard, connection, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            listener.Stop();
        }
    }

    private async Task RelayAsync(TcpClient onboard, int connection, CancellationToken stoppingToken)
    {
        using TcpClient server = new();
        using (onboard)
        {
            try
            {
                await server.ConnectAsync(endpoints.Target, stoppingToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is SocketException or OperationCanceledException)
            {
                log.CloseConnection(connection, "upstream connect failed: " + error.GetType().Name);
                return;
            }

            using CancellationTokenSource relay = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            connections.Register(connection, relay);
            NetworkStream onboardStream = onboard.GetStream();
            NetworkStream serverStream = server.GetStream();
            Task<string> upstream = PumpAsync(onboardStream, serverStream, connection, OnboardToServer, relay.Token);
            Task<string> downstream = PumpAsync(serverStream, onboardStream, connection, ServerToOnboard, relay.Token);
            string closedBy = await (await Task.WhenAny(upstream, downstream).ConfigureAwait(false)).ConfigureAwait(false);

            // Closing both sockets is what either end sees as the link going down. Cancelling alone
            // would leave the other half waiting on a read its peer never answers.
            await relay.CancelAsync().ConfigureAwait(false);
            onboard.Close();
            server.Close();
            await Task.WhenAll(upstream, downstream).ConfigureAwait(false);
            // A requested disconnect reaches the pumps as a cancelled read; record what it really was.
            log.CloseConnection(connection, connections.Release(connection) ? DisconnectedOnRequest : closedBy);
        }
    }

    /// <summary>Copies lines until one side closes or a drop ends the connection. Never throws.</summary>
    private async Task<string> PumpAsync(
        NetworkStream from,
        NetworkStream to,
        int connection,
        string direction,
        CancellationToken cancellationToken)
    {
        try
        {
            using StreamReader reader = new(
                from, Utf8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
            while (true)
            {
                string? line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    return direction == OnboardToServer ? "onboard closed" : "server closed";
                }

                LineRecord record = Describe(connection, direction, line);
                if (direction == ServerToOnboard && TryDrop(record))
                {
                    // Nothing after the dropped line is forwarded either -- the server wrote the
                    // readiness that follows an ack in the same send, and a link that loses one
                    // loses both.
                    log.RecordLine(record with { Dropped = true });
                    return "relay dropped DurableAck for " + record.AcceptedMessageType;
                }

                log.RecordLine(record);
                await to.WriteAsync(Utf8.GetBytes(line + "\n"), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (
            error is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
            return direction + " ended: " + error.GetType().Name;
        }
    }

    private bool TryDrop(LineRecord line)
    {
        ProtocolFaultProxyState plan = engine.Snapshot().State;
        return plan.PlanId is not null &&
               plan.DropAckForMessageType is not null &&
               string.Equals(line.MessageType, "DurableAck", StringComparison.Ordinal) &&
               string.Equals(line.AcceptedMessageType, plan.DropAckForMessageType, StringComparison.Ordinal) &&
               log.TryClaimDrop(
                   plan.PlanId,
                   plan.DropCount,
                   new DropRecord(plan.PlanId, line.Connection, line.At, line.CorrelationId, plan.DropAckForMessageType));
    }

    private static LineRecord Describe(int connection, string direction, string line)
    {
        string? messageType = null;
        string? messageId = null;
        string? correlationId = null;
        string? acceptedMessageType = null;
        long? sessionGeneration = null;
        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                messageType = Text(root, "messageType");
                messageId = Text(root, "messageId");
                // A DurableAck's correlationId is the messageId it accepts; the server refuses one
                // that is not, so it names the acknowledged message without reading the payload.
                correlationId = Text(root, "correlationId");
                if (root.TryGetProperty("sessionGeneration", out JsonElement generation) &&
                    generation.ValueKind == JsonValueKind.Number)
                {
                    sessionGeneration = generation.GetInt64();
                }
                if (root.TryGetProperty("payload", out JsonElement payload))
                {
                    acceptedMessageType = Text(payload, "acceptedMessageType");
                }
            }
        }
        catch (JsonException)
        {
            // Not this double's line to judge. It is forwarded as it came, and the receiving end's
            // own parser decides what it is.
        }
        return new LineRecord(
            connection, direction, DateTimeOffset.UtcNow, messageType, messageId, correlationId,
            sessionGeneration, acceptedMessageType, Dropped: false);
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out JsonElement value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
