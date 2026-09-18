namespace ControlServer.ProtocolFaultProxy;

/// <summary>
/// What crossed the relay: connections, one record per line, and the drops. Envelope identity only,
/// never a payload -- SessionHello carries the onboard credential. Outside the command engine for the
/// reason ClockSkewProxy's ForwardLog gives: traffic that moved the revision would make
/// expectedRevision useless for the one command that carries a real change.
/// </summary>
public sealed class TrafficLog
{
    /// <summary>Several minutes of a real-onboard run; a run that outgrows it says so in the snapshot.</summary>
    private const int MaximumLines = 20_000;

    private readonly object gate = new();
    private readonly List<ConnectionRecord> connections = [];
    private readonly List<LineRecord> lines = [];
    private readonly List<DropRecord> drops = [];
    private long overflowedLines;

    /// <summary>Numbers connections from 1 in accept order, which is the order the onboard made them.</summary>
    public int OpenConnection()
    {
        lock (gate)
        {
            int number = connections.Count + 1;
            connections.Add(new ConnectionRecord(number, DateTimeOffset.UtcNow));
            return number;
        }
    }

    public void CloseConnection(int number, string closedBy)
    {
        lock (gate)
        {
            ConnectionRecord record = connections[number - 1];
            if (record.ClosedAt is null)
            {
                connections[number - 1] = record with { ClosedAt = DateTimeOffset.UtcNow, ClosedBy = closedBy };
            }
        }
    }

    public void RecordLine(LineRecord line)
    {
        lock (gate)
        {
            if (lines.Count >= MaximumLines)
            {
                overflowedLines++;
                return;
            }
            lines.Add(line);
        }
    }

    /// <summary>
    /// Takes one drop from the plan, atomically: two acks arriving together must not both take the last one.
    /// </summary>
    public bool TryClaimDrop(string planId, int dropCount, DropRecord drop)
    {
        lock (gate)
        {
            if (drops.Count(item => string.Equals(item.PlanId, planId, StringComparison.Ordinal)) >= dropCount)
            {
                return false;
            }
            drops.Add(drop);
            return true;
        }
    }

    public object Snapshot()
    {
        lock (gate)
        {
            return new
            {
                connections = connections.ToArray(),
                drops = drops.ToArray(),
                lines = lines.ToArray(),
                overflowedLines
            };
        }
    }
}

public sealed record ConnectionRecord(int Connection, DateTimeOffset OpenedAt)
{
    public DateTimeOffset? ClosedAt { get; init; }

    /// <summary>Which side ended it, or that the relay did because it dropped an ack.</summary>
    public string? ClosedBy { get; init; }
}

public sealed record LineRecord(
    int Connection,
    string Direction,
    DateTimeOffset At,
    string? MessageType,
    string? MessageId,
    string? CorrelationId,
    long? SessionGeneration,
    string? AcceptedMessageType,
    bool Dropped);

/// <summary>
/// One dropped line. <c>AcceptedMessageId</c> is its correlationId, the message it answers, for an ack and an
/// answer alike. <c>AcceptedMessageType</c> is what the plan named: the acknowledged type for a DurableAck,
/// the dropped line's own type for an answer.
/// </summary>
public sealed record DropRecord(
    string PlanId,
    int Connection,
    DateTimeOffset At,
    string? AcceptedMessageId,
    string AcceptedMessageType);
