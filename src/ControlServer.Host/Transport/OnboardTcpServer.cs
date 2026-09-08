using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Options;

namespace ControlServer.Host.Transport;

public sealed partial class OnboardTcpServer(
    IOptions<OnboardTransportOptions> options,
    IServiceScopeFactory scopeFactory,
    OnboardPeer peer,
    ILogger<OnboardTcpServer> logger) : BackgroundService
{
    private readonly OnboardTransportOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            LogTransportDisabled(logger);
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken).ConfigureAwait(false);
            return;
        }

        IPAddress address = IPAddress.Parse(_options.ListenAddress);
        ValidateConfiguration();
        TcpListener listener = new(address, _options.Port);
        listener.Start();
        LogTransportStarted(logger, address, _options.Port);
        // Connections are served concurrently, one task each. Serving them one at a time was
        // adequate while there was one vehicle and is a deadlock with a fleet: the accept loop only
        // came back round when the current peer's session ended, so the second vehicle waited in
        // the listen backlog for the whole life of the first vehicle's session.
        List<Task> connections = [];
        using SemaphoreSlim slots = new(_options.MaxConcurrentSessions, _options.MaxConcurrentSessions);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                TcpClient client = await listener.AcceptTcpClientAsync(stoppingToken).ConfigureAwait(false);
                // Bounded rather than unbounded: every accepted connection holds a service scope, a
                // read buffer and a socket, and the fleet size is known. A connection past the
                // bound is closed at once, which a peer retries, rather than queued behind sessions
                // that may last hours.
                if (!await slots.WaitAsync(TimeSpan.Zero, stoppingToken).ConfigureAwait(false))
                {
                    LogConnectionRefused(logger, _options.MaxConcurrentSessions);
                    client.Dispose();
                    continue;
                }

                connections.RemoveAll(task => task.IsCompleted);
                connections.Add(ServeAsync(client, slots, stoppingToken));
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown, not a fault. The connection tasks are watching the same token.
        }
        finally
        {
            listener.Stop();
            await Task.WhenAll(connections).ConfigureAwait(false);
        }
    }

    /// <summary>Serves one connection to its end, whatever ends it.</summary>
    /// <remarks>
    /// Every failure is confined here. One peer's protocol error, disconnect or timeout must not
    /// reach the accept loop, because the accept loop is now shared with every other vehicle.
    /// </remarks>
    private async Task ServeAsync(TcpClient client, SemaphoreSlim slots, CancellationToken cancellationToken)
    {
        try
        {
            using (client)
            {
                await HandleClientAsync(client, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown.
        }
        catch (Exception error)
        {
            LogConnectionEnded(logger, error);
        }
        finally
        {
            slots.Release();
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        await using NetworkStream stream = client.GetStream();
        using StreamReader reader = new(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        await using OnboardPeerConnection connection = new(stream);
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        OnboardMessageProcessor processor = scope.ServiceProvider.GetRequiredService<OnboardMessageProcessor>();
        OnboardConnectionState state = new() { DeferOutboundUntilResponseWritten = true };
        string? attachedAgvId = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                string? line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    return;
                }
                if (Encoding.UTF8.GetByteCount(line) > _options.MaxLineBytes)
                {
                    throw new InvalidDataException("Protocol line exceeds OnboardTransport:MaxLineBytes.");
                }
                string response = await processor.ProcessAsync(line, state, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(response))
                {
                    await connection.SendAsync(
                        OnboardPeerConnection.Encode(response),
                        cancellationToken).ConfigureAwait(false);
                }
                await processor.FlushDeferredOutboundAsync(state, cancellationToken).ConfigureAwait(false);
                // The agvId is what files this connection, so both halves of the session identity
                // have to be established before it can be attached. Until then nothing addressed to
                // this vehicle can be routed to it, which is correct: it has no session yet.
                if (attachedAgvId is null &&
                    state.SessionGeneration is not null &&
                    !string.IsNullOrWhiteSpace(state.AgvId))
                {
                    peer.Attach(state.AgvId, connection);
                    attachedAgvId = state.AgvId;
                }
            }
        }
        finally
        {
            if (attachedAgvId is not null)
            {
                peer.Detach(attachedAgvId, connection);
            }
        }
    }

    private void ValidateConfiguration()
    {
        if (_options.Port is < 1 or > 65535)
        {
            throw new InvalidOperationException("OnboardTransport:Port must be in 1..65535.");
        }
        if (_options.MaxLineBytes < 4096)
        {
            throw new InvalidOperationException("OnboardTransport:MaxLineBytes must be at least 4096.");
        }
        if (_options.MaxConcurrentSessions < 1)
        {
            throw new InvalidOperationException("OnboardTransport:MaxConcurrentSessions must be at least 1.");
        }
    }

    [LoggerMessage(EventId = 1001, Level = LogLevel.Warning,
        Message = "Onboard transport is disabled; vehicle readiness cannot become READY.")]
    private static partial void LogTransportDisabled(ILogger logger);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Information,
        Message = "Onboard NDJSON listener started on {Address}:{Port}; transport=plaintext")]
    private static partial void LogTransportStarted(ILogger logger, IPAddress address, int port);

    [LoggerMessage(EventId = 1003, Level = LogLevel.Warning,
        Message = "Onboard connection ended with a protocol or transport error.")]
    private static partial void LogConnectionEnded(ILogger logger, Exception error);

    [LoggerMessage(EventId = 1004, Level = LogLevel.Warning,
        Message = "Onboard connection refused: {MaxConcurrentSessions} concurrent sessions are already open.")]
    private static partial void LogConnectionRefused(ILogger logger, int maxConcurrentSessions);
}
