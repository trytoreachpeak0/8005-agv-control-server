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
