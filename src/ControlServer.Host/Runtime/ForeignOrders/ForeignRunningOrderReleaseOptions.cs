namespace ControlServer.Host.Runtime.ForeignOrders;

/// <summary>
/// The switch the foreign running order release entry hangs on (control-server#617), separate from
/// <c>VehicleFaultRecovery:enabled</c>. Off unless the deployed configuration turns it on, so a missing section maps no route.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a switch of its own</b> (independent review M1). The entry's whole point is that a vehicle held by a foreign order that
/// left RIoT's running listing unread as ended has a way out without editing the database. Hung on the fault recovery switch it
/// was missing exactly where it is needed: the production definitions keep fault recovery off, and turning the whole recovery
/// surface on just to get this exit is a larger loosening than this exit is.
/// </para>
/// <para>
/// <b>What it shares with the fault recovery entries</b>: the credential (the variable named by
/// <c>VehicleFaultRecovery:credentialEnvironmentVariable</c>, which the deployment writes whether recovery is on or not), the
/// R-11/R-13 roster, the administrator audit. Only the switch is its own.
/// </para>
/// </remarks>
public sealed class ForeignRunningOrderReleaseOptions
{
    public const string SectionName = "ForeignRunningOrderRelease";

    /// <summary>The configuration key the route is mapped on.</summary>
    public const string EnabledKey = SectionName + ":enabled";

    public bool Enabled { get; set; }
}

/// <summary>What the release entry is at start: on or off, and whether its credential variable holds a value.</summary>
public sealed record ForeignRunningOrderReleaseState(bool Enabled, bool CredentialPresent);

/// <summary>
/// The start-up check of the release entry (control-server#617 incremental review S-1): on, with its credential variable empty,
/// it answers every request 503. That is said at start, as a warning (event 2235), and in <c>EFFECTIVE_CONFIGURATION</c>; the
/// server still starts.
/// </summary>
public static class ForeignRunningOrderReleaseStartup
{
    private static readonly Action<ILogger, string, Exception?> LogCredentialMissing =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(2235, nameof(LogCredentialMissing)),
            "The foreign running order release entry is on (ForeignRunningOrderRelease:enabled), but its credential variable " +
            "{CredentialVariable} holds no value: every request to it is answered 503, and a vehicle held by a foreign order that " +
            "left RIoT's running listing unread as ended has no way out but the database until it is set (control-server#617).");

    /// <summary>Reads the state, warns when the entry is on without a credential, and answers the state.</summary>
    public static ForeignRunningOrderReleaseState Check(
        ILogger logger,
        ForeignRunningOrderReleaseOptions release,
        VehicleFaultRecoveryOptions recovery,
        Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(recovery);
        ArgumentNullException.ThrowIfNull(environment);
        string variable = recovery.CredentialEnvironmentVariable;
        bool present = !string.IsNullOrWhiteSpace(variable) && !string.IsNullOrWhiteSpace(environment(variable));
        if (release.Enabled && !present)
        {
            LogCredentialMissing(logger, variable ?? string.Empty, null);
        }
        return new ForeignRunningOrderReleaseState(release.Enabled, present);
    }
}
