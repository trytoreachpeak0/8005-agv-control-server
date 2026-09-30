using System.Text.Json;
using ControlServer.Infrastructure.Persistence;

namespace ControlServer.Host.Transport;

public enum SlotFaultDeclarationResultDisposition
{
    /// <summary>The first result for the declaration: recorded.</summary>
    Recorded,

    /// <summary>The declaration already has its result; this one changes nothing.</summary>
    AlreadyAnswered,

    /// <summary>No declaration of this server has that id; acknowledged and changes nothing.</summary>
    UnknownDeclaration,

    /// <summary>The result names another attempt than the declaration; acknowledged, and the declaration stays pending.</summary>
    AttemptMismatch
}

/// <summary>
/// The inbound half of REQ-0359: <c>SlotFaultDeclarationResult</c>, and which declaration commands a reconnect replays.
/// </summary>
public static class SlotFaultDeclarationResults
{
    public static Task<SlotFaultDeclarationResultDisposition> RecordAsync(
        ControlServerDbContext dbContext,
        string agvId,
        string resultMessageId,
        JsonElement payload,
        DateTimeOffset receivedAt,
        CancellationToken cancellationToken)
    {
        _ = (dbContext, agvId, resultMessageId, payload, receivedAt, cancellationToken);
        throw new NotImplementedException();
    }

    public static Task<string[]> PendingCommandMessageIdsAsync(
        ControlServerDbContext dbContext,
        string agvId,
        CancellationToken cancellationToken)
    {
        _ = (dbContext, agvId, cancellationToken);
        throw new NotImplementedException();
    }
}
