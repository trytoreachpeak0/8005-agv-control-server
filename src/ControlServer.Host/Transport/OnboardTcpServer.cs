using System.Net;
using System.Net.Sockets;
using System.Text;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Extensions.Options;

namespace ControlServer.Host.Transport;

public sealed partial class OnboardTcpServer : BackgroundService
{
    private readonly OnboardTransportOptions _options;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly OnboardPeer _peer;
    private readonly ILogger<OnboardTcpServer> _logger;
    private readonly TimeProvider _clock;

    /// <summary>The composition root's constructor: the project-wide liveness timeout, on the system clock.</summary>
    public OnboardTcpServer(
        IOptions<OnboardTransportOptions> options,
        IServiceScopeFactory scopeFactory,
        OnboardPeer peer,
        ILogger<OnboardTcpServer> logger)
        : this(options, scopeFactory, peer, logger, TimeProvider.System, SessionLiveness.Timeout)
    {
    }

    /// <summary>
    /// Tests only. The timeout is not configuration and never comes from a settings file: ADR-cross-0027 fixes it
    /// project-wide at <see cref="SessionLiveness.Timeout"/>, which is also what the dashboard and the journey
    /// runtime judge liveness by. A test that had to wait out six real seconds twice over would be paying wall-clock
    /// time to re-measure a number another test already pins.
    /// </summary>
    internal OnboardTcpServer(
        IOptions<OnboardTransportOptions> options,
        IServiceScopeFactory scopeFactory,
        OnboardPeer peer,
        ILogger<OnboardTcpServer> logger,
        TimeProvider clock,
        TimeSpan idleTimeout)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);
        _options = options.Value;
        _scopeFactory = scopeFactory;
        _peer = peer;
        _logger = logger;
        _clock = clock;
        IdleTimeout = idleTimeout;
    }

    /// <summary>How long a connection may go without a legal inbound message before the server closes it.</summary>
    internal TimeSpan IdleTimeout { get; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            LogTransportDisabled(_logger);
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken).ConfigureAwait(false);
            return;
        }

        IPAddress address = IPAddress.Parse(_options.ListenAddress);
        ValidateConfiguration();
        TcpListener listener = new(address, _options.Port);
        listener.Start();
        LogTransportStarted(_logger, address, _options.Port);
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
                    LogConnectionRefused(_logger, _options.MaxConcurrentSessions);
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
            LogConnectionEnded(_logger, error);
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
        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
        OnboardMessageProcessor processor = scope.ServiceProvider.GetRequiredService<OnboardMessageProcessor>();
        OnboardConnectionState state = new() { DeferOutboundUntilResponseWritten = true };
        string? attachedAgvId = null;
        // ADR-cross-0027: the peer heartbeats every two seconds, and six seconds without a legal message means
        // the session is lost even though the socket is still open. Until control-server#234 nothing measured
        // this: a vehicle whose process had hung kept a Ready session row for as long as its TCP connection
        // survived, because the disconnect path writes nothing and RecordConnectionLossAsync has no caller.
        OnboardConnectionLiveness liveness = new(_clock, IdleTimeout);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                string? line;
                long arrivedAt;
                // The deadline is enforced on the read itself rather than by a watchdog beside it, so the
                // moment the window closes is the moment this connection stops being read from. There is no
                // window in which a late line could still be processed and revive the session: past the
                // timeout this method returns, and nothing below runs again.
                using (CancellationTokenSource idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    idle.CancelAfter(liveness.Remaining);
                    try
                    {
                        line = await reader.ReadLineAsync(idle.Token).ConfigureAwait(false);
                        // Stamped the moment the line is off the wire, and used to refresh below once it has
                        // been judged legal. Refreshing from "after processing" instead would charge this
                        // connection for our own work -- the response write, the deferred outbound flush --
                        // and shorten its next window by however long that took, which on a slow write is
                        // exactly when the peer least deserves to be cut off.
                        arrivedAt = _clock.GetTimestamp();
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        // Silent past the window: this server closes the connection, and the peer reconnects
                        // into the five-step handshake (ADR-cross-0029) where both ends reconcile afresh.
                        // Closing is all that happens here -- REQ-0287 forbids a liveness timeout from ending
                        // an order, releasing a lease, reassigning or moving the vehicle.
                        LogOnboardSessionSilent(
                            _logger, attachedAgvId ?? state.AgvId ?? "(no session)", state.SessionGeneration,
                            liveness.Silence, null);
                        return;
                    }
                }
                if (line is null)
                {
                    return;
                }
                if (Encoding.UTF8.GetByteCount(line) > _options.MaxLineBytes)
                {
                    throw new InvalidDataException("Protocol line exceeds OnboardTransport:MaxLineBytes.");
                }
                string response = await processor.ProcessAsync(line, state, cancellationToken).ConfigureAwait(false);
                // A SessionHello on a connection that was routable starts a new handshake, and the gate closes
                // before its answer goes out, for the reason given at the attach below. No onboard does this
                // today -- it opens a new socket per handshake -- but the gate is "this handshake is done", not
                // "a handshake once finished here".
                if (attachedAgvId is not null && !state.HandshakeCompleted)
                {
                    _peer.Detach(attachedAgvId, connection);
                    attachedAgvId = null;
                }
                // Refreshed only once the message has been processed: ADR-cross-0027 counts legal protocol
                // messages, and a line is not known to be one until the envelope and the session generation
                // have been checked. A line that throws does not refresh, and it ends the connection anyway.
                // The window runs from when the line arrived, not from now -- see the stamp above.
                liveness.RefreshTo(arrivedAt);
                // The answer to the line just read. This write and OnboardPeer are the only two ways onto the
                // socket (OnboardOutboundFunnelArchitectureTests), and this one needs no gate: the vehicle is
                // waiting for exactly this line.
                if (!string.IsNullOrWhiteSpace(response))
                {
                    await connection.SendAsync(
                        OnboardPeerConnection.Encode(response),
                        cancellationToken).ConfigureAwait(false);
                }
                // Routable only from here, once the recovery report's answer -- its DurableAck and
                // SessionReadiness -- is on the wire (control-server#259). Before, the vehicle is in its handshake,
                // reading one line per request, and a push would be read as the answer it is waiting for; it
                // dropped the connection in real-rig run 35513390399 and synthetic runs 35553615265 and
                // 35651373959. Attaching as soon as SessionHello was answered, as this did until then, covered only
                // the lower bound: a connection has no session to route to before it. HandshakeCompleted alone is
                // not the moment either: the processor sets it while handling the recovery report, before the
                // answer is written, and a push in between would go out ahead of it.
                //
                // Before the deferred flush, not after: the recovery report's flush replays everything held back
                // during the handshake -- recovery commands, recovery session snapshots, activation commands --
                // through OnboardPeer, so it needs the connection routable. Anything a sender tried to push during
                // the handshake was refused as "not connected" after its outbox row was written, so it is in what
                // that replay or the runtime's next round reads.
                if (attachedAgvId is null && state.HandshakeCompleted)
                {
                    _peer.Attach(state, connection);
                    attachedAgvId = state.AgvId;
                }
                await processor.FlushDeferredOutboundAsync(state, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            if (attachedAgvId is not null)
            {
                _peer.Detach(attachedAgvId, connection);
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
        // Unreachable from the composition root, which always passes SessionLiveness.Timeout: the window is
        // not configuration and no settings file can reach it (the ticket asked for a JourneyRuntime setting
        // and a startup validation for it; both lapsed when the threshold turned out to exist already). What
        // this guards is the internal constructor the tests use, so a nonsensical window fails at startup
        // rather than turning into "never expires".
        if (IdleTimeout <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("The Onboard liveness timeout must be positive.");
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

    [LoggerMessage(EventId = 1005, Level = LogLevel.Warning,
        Message = "Onboard session for {AgvId} (generation {SessionGeneration}) went silent for {Silence} with the " +
                  "connection still open; closing it (ADR-cross-0027). No order is held, ended or reassigned.")]
    private static partial void LogOnboardSessionSilent(
        ILogger logger, string agvId, long? sessionGeneration, TimeSpan silence, Exception? error);
}
