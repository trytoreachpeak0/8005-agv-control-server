using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.Options;

namespace ControlServer.Host.Transport;

public sealed partial class OnboardTcpServer(
    IOptions<OnboardTransportOptions> options,
    IServiceScopeFactory scopeFactory,
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
        ValidateConfiguration(address);
        TcpListener listener = new(address, _options.Port);
        listener.Start();
        LogTransportStarted(
            logger, address, _options.Port, !string.IsNullOrWhiteSpace(_options.ServerCertificatePath));
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                using TcpClient client = await listener.AcceptTcpClientAsync(stoppingToken).ConfigureAwait(false);
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

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        await using Stream stream = await CreateTransportStreamAsync(client, cancellationToken).ConfigureAwait(false);
        using StreamReader reader = new(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        await using StreamWriter writer = new(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n"
        };
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        OnboardMessageProcessor processor = scope.ServiceProvider.GetRequiredService<OnboardMessageProcessor>();
        OnboardConnectionState state = new();
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
            await writer.WriteLineAsync(response.AsMemory(), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<Stream> CreateTransportStreamAsync(TcpClient client, CancellationToken cancellationToken)
    {
        NetworkStream networkStream = client.GetStream();
        if (string.IsNullOrWhiteSpace(_options.ServerCertificatePath))
        {
            return networkStream;
        }

        string? password = string.IsNullOrWhiteSpace(_options.ServerCertificatePasswordEnvironmentVariable)
            ? null
            : Environment.GetEnvironmentVariable(_options.ServerCertificatePasswordEnvironmentVariable);
        X509Certificate2 certificate = new(
            _options.ServerCertificatePath,
            password,
            X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.EphemeralKeySet);
        SslStream sslStream = new(networkStream, leaveInnerStreamOpen: false);
        await sslStream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
        {
            ServerCertificate = certificate,
            ClientCertificateRequired = false,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            CertificateRevocationCheckMode = X509RevocationMode.Online
        }, cancellationToken).ConfigureAwait(false);
        return sslStream;
    }

    private void ValidateConfiguration(IPAddress address)
    {
        if (_options.Port is < 1 or > 65535)
        {
            throw new InvalidOperationException("OnboardTransport:Port must be in 1..65535.");
        }
        if (_options.MaxLineBytes < 4096)
        {
            throw new InvalidOperationException("OnboardTransport:MaxLineBytes must be at least 4096.");
        }
        bool tlsEnabled = !string.IsNullOrWhiteSpace(_options.ServerCertificatePath);
        if (!tlsEnabled && (!IPAddress.IsLoopback(address) || !_options.AllowInsecureLoopback))
        {
            throw new InvalidOperationException(
                "A non-loopback Onboard listener requires a configured TLS server certificate.");
        }
    }

    [LoggerMessage(EventId = 1001, Level = LogLevel.Warning,
        Message = "Onboard transport is disabled; vehicle readiness cannot become READY.")]
    private static partial void LogTransportDisabled(ILogger logger);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Information,
        Message = "Onboard NDJSON listener started on {Address}:{Port}; TLS={TlsEnabled}")]
    private static partial void LogTransportStarted(
        ILogger logger, IPAddress address, int port, bool tlsEnabled);

    [LoggerMessage(EventId = 1003, Level = LogLevel.Warning,
        Message = "Onboard connection ended with a protocol or transport error.")]
    private static partial void LogConnectionEnded(ILogger logger, Exception error);
}
