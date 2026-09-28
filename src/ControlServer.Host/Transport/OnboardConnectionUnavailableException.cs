namespace ControlServer.Host.Transport;

/// <summary>
/// This vehicle's Onboard connection cannot take a line right now: no connection is routable for it, the one that is
/// belongs to another session generation, a write did not finish within <see cref="OnboardTransportOptions.WriteTimeout"/>,
/// or the connection was closed under the send (control-server#334).
/// </summary>
/// <remarks>
/// <para>
/// <b>An <see cref="IOException"/>, on purpose.</b> Every sender already reads an <see cref="IOException"/> from a send as
/// "the vehicle is not connected": the outbox row stays unacknowledged and the replay after the reconnect delivers it. This
/// type narrows that to its one cause without changing any of those catches.
/// </para>
/// <para>
/// <b>Why it needs a name of its own.</b> The journey runtime lets one vehicle yield its turn on exactly this failure and goes
/// on with the others (<c>JourneyRuntimeEngine</c>). "Any <see cref="IOException"/> in the chain" would be too wide for that:
/// a RIoT call that cannot connect carries a <see cref="System.Net.Sockets.SocketException"/> inside its
/// <see cref="HttpRequestException"/>, and RIoT being down is not one vehicle's trouble.
/// </para>
/// <para>
/// <b>A closed connection is this, not an <see cref="ObjectDisposedException"/>.</b> Between the stream being closed and the
/// connection leaving the routing table a send finds a disposed stream; before this type that surfaced as
/// <see cref="ObjectDisposedException"/>, which a sender catching only <see cref="IOException"/> answered with a 500 although
/// its outbox row was already written.
/// </para>
/// </remarks>
public sealed class OnboardConnectionUnavailableException : IOException
{
    public OnboardConnectionUnavailableException()
    {
    }

    public OnboardConnectionUnavailableException(string message)
        : base(message)
    {
    }

    public OnboardConnectionUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Whether <paramref name="error"/> is this failure, or carries it anywhere down its inner exceptions.</summary>
    public static bool IsIn(Exception? error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
        {
            if (current is OnboardConnectionUnavailableException)
            {
                return true;
            }
        }
        return false;
    }
}
