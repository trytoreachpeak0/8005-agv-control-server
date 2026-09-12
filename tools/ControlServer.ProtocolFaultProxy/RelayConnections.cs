namespace ControlServer.ProtocolFaultProxy;

/// <summary>
/// The relay connections open right now, so a scenario can take the link down on purpose without
/// swallowing a single line. That is how to make the onboard reconnect when what is under test is
/// what each end does with the next session -- the server replaying its own unacknowledged messages
/// into it (8005-agv-control-server#31) -- rather than a replay the relay provoked by losing an ack.
/// </summary>
public sealed class RelayConnections
{
    private readonly object gate = new();
    private readonly Dictionary<int, CancellationTokenSource> open = [];
    private readonly HashSet<int> disconnected = [];
    private readonly Dictionary<string, int[]> answered = new(StringComparer.Ordinal);

    public void Register(int connection, CancellationTokenSource relay)
    {
        lock (gate)
        {
            open[connection] = relay;
        }
    }

    /// <summary>Forgets a connection that has ended; true when it ended because a scenario asked.</summary>
    public bool Release(int connection)
    {
        lock (gate)
        {
            open.Remove(connection);
            return disconnected.Contains(connection);
        }
    }

    /// <summary>
    /// Closes every connection open right now, once per commandId: a retried command reports the
    /// connections the first one closed and closes nothing, so a lost response cannot cost a second
    /// reconnect.
    /// </summary>
    public int[] Disconnect(string commandId)
    {
        CancellationTokenSource[] closing;
        int[] numbers;
        lock (gate)
        {
            if (answered.TryGetValue(commandId, out int[]? earlier))
            {
                return earlier;
            }
            numbers = [.. open.Keys.Order()];
            closing = [.. numbers.Select(number => open[number])];
            disconnected.UnionWith(numbers);
            answered[commandId] = numbers;
        }

        // Outside the lock: cancelling can run the relay's continuations inline, and those come back
        // here to release their connection.
        foreach (CancellationTokenSource relay in closing)
        {
            try
            {
                relay.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The connection ended on its own between taking the lock and getting here.
            }
        }
        return numbers;
    }
}
