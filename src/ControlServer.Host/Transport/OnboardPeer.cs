using System.Text;
using ControlServer.Application;

namespace ControlServer.Host.Transport;

public sealed class OnboardPeer : IOnboardPeer
{
    private readonly object _gate = new();
    private OnboardPeerConnection? _connection;

    internal void Attach(OnboardPeerConnection connection)
    {
        lock (_gate)
        {
            if (_connection is not null && !ReferenceEquals(_connection, connection))
            {
                throw new InvalidOperationException("An Onboard peer is already attached.");
            }

            _connection = connection;
        }
    }

    internal void Detach(OnboardPeerConnection connection)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_connection, connection))
            {
                _connection = null;
            }
        }
    }

    public Task SendAsync(ReadOnlyMemory<byte> ndjsonLine, CancellationToken cancellationToken)
    {
        OnboardPeerConnection connection;
        lock (_gate)
        {
            connection = _connection
                ?? throw new IOException("No recovered Onboard peer is connected.");
        }

        return connection.SendAsync(ndjsonLine, cancellationToken);
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
