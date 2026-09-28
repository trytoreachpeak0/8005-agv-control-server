using System.Text.Json;

namespace ControlServer.Host.Runtime;

/// <summary>
/// How the runtime reads MesIngest: with a bound on every request, and with a timeout judged the same failure as any other
/// unreadable answer (control-server#334).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why both halves.</b> The demand catalog and the sublot box counts are read while the runtime holds
/// <c>JourneyMutationGate</c>. Until this ticket their <see cref="HttpClient"/>s had no timeout of their own, so .NET's
/// 100 seconds applied, and a timeout surfaces as <see cref="TaskCanceledException"/>, which none of the three places that
/// read MesIngest caught. A MesIngest that accepted the connection and then said nothing therefore held the gate 100 seconds
/// and then failed the whole round -- every vehicle's advance, emergency-stop escalation and dispatch -- round after round.
/// </para>
/// <para>
/// <b>Why 10 seconds by default, and not RIoT's 30.</b> MesIngest answers on the same host; a healthy answer takes well under
/// a second. Thirty seconds under the gate is a fault clearance's whole wait for it (<c>FAULT_RECOVERY_RUNTIME_BUSY</c>), so one
/// unanswered read would use it up.
/// </para>
/// </remarks>
public static class MesIngestReads
{
    public const string TimeoutSecondsKey = "MesIngest:timeoutSeconds";

    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The configured request timeout, refused at startup unless it is positive.</summary>
    public static TimeSpan Timeout(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        double seconds = configuration.GetValue<double?>(TimeoutSecondsKey) ?? DefaultTimeout.TotalSeconds;
        if (!(seconds > 0))
        {
            throw new InvalidDataException($"{TimeoutSecondsKey} must be positive.");
        }
        return TimeSpan.FromSeconds(seconds);
    }

    /// <summary>
    /// Whether <paramref name="error"/> is a MesIngest read that failed -- unreachable, unreadable, or timed out -- rather than
    /// this server shutting down.
    /// </summary>
    /// <remarks>
    /// A timeout and a shutdown are both <see cref="OperationCanceledException"/>s; only the caller's token tells them apart.
    /// A shutdown must still end the round as a shutdown, so it is not a failed read.
    /// </remarks>
    public static bool IsFailedRead(Exception error, CancellationToken cancellationToken) =>
        error is HttpRequestException or InvalidDataException or JsonException ||
        (error is OperationCanceledException && !cancellationToken.IsCancellationRequested);
}
