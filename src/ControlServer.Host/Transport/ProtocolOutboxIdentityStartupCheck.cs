using ControlServer.Infrastructure.Persistence;

namespace ControlServer.Host.Transport;

public static class ProtocolOutboxIdentityStartupCheck
{
    public const string ReasonCode = "OUTBOX_PROTOCOL_IDENTITY_MISMATCH";

    public static Task EnsureAsync(
        ControlServerDbContext dbContext, ILogger logger, string? databasePath, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
