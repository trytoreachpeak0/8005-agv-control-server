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
    private static readonly TimeSpan AcceptRetryDelay = TimeSpan.FromMilliseconds(100);

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
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                TcpClient? accepted = await AcceptNextClientAsync(listener, stoppingToken).ConfigureAwait(false);
                if (accepted is null)
                {
                    continue;
                }
                using TcpClient client = accepted;
                try
                {
                    await HandleClientAsync(client, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception error)
                {
                    LogConnectionEnded(logger, error);
                }
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>
    /// Test seam for the accept call. Production always uses the listener's own.
    /// </summary>
    internal Func<TcpListener, CancellationToken, ValueTask<TcpClient>> Accept { get; init; } =
        static (listener, cancellationToken) => listener.AcceptTcpClientAsync(cancellationToken);

    // Connections are served one at a time, so a second vehicle process connecting with the same
    // identity waits in the listen backlog. When both processes die at once -- what agv01 did at
    // 22:39 on 2026-09-18 -- the waiting connection can be reset before it is accepted, and the
    // accept then fails with a SocketException for a connection that no longer exists. Kestrel
    // retries the same case. Left uncaught it ended ExecuteAsync, and with it the whole host.
    private async Task<TcpClient?> AcceptNextClientAsync(TcpListener listener, CancellationToken stoppingToken)
    {
        try
        {
            return await Accept(listener, stoppingToken).ConfigureAwait(false);
        }
        catch (SocketException error) when (!stoppingToken.IsCancellationRequested)
        {
            LogAcceptFailed(logger, error, error.SocketErrorCode);
            // Keeps a listener that fails every time from spinning; a reset backlog entry costs one pause.
            await Task.Delay(AcceptRetryDelay, stoppingToken).ConfigureAwait(false);
            return null;
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
        bool attached = false;
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
                if (state.SessionGeneration is not null && !attached)
                {
                    peer.Attach(connection);
                    attached = true;
                }
            }
        }
        finally
        {
            if (attached)
            {
                peer.Detach(connection);
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
        Message = "Accepting an Onboard connection failed with {SocketError}; the listener keeps accepting.")]
    private static partial void LogAcceptFailed(ILogger logger, Exception error, SocketError socketError);
}
