namespace ControlServer.Application;

public interface IOnboardPeer
{
    Task SendAsync(ReadOnlyMemory<byte> ndjsonLine, CancellationToken cancellationToken);
}
