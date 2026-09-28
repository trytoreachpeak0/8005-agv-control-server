using Microsoft.Extensions.Options;

namespace ControlServer.Host.Transport;

public sealed class OnboardTransportOptions
{
    public const string SectionName = "OnboardTransport";

    public bool Enabled { get; set; } = true;
    public string ListenAddress { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 58005;
    public int MaxLineBytes { get; set; } = 1_048_576;

    /// <summary>
    /// How many Onboard sessions may be open at once — one per vehicle, plus room for a peer that
    /// is reconnecting before its old socket has been noticed as dead.
    /// </summary>
    public int MaxConcurrentSessions { get; set; } = 8;

    /// <summary>The default for <see cref="WriteTimeout"/>.</summary>
    public static readonly TimeSpan DefaultWriteTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long one write to a vehicle may take -- waiting for the connection's turn, the write and the flush together --
    /// before the connection is treated as lost and closed (control-server#334).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why a write needs a bound of its own.</b> Most writes to a vehicle are made while the runtime holds
    /// <c>JourneyMutationGate</c> for its whole round. A socket whose peer stops reading leaves a write neither done nor
    /// failed, and until this bound existed nothing ended it: the round stopped on that one vehicle, and every other
    /// vehicle's advance, emergency-stop escalation and fault clearance waited behind it.
    /// </para>
    /// <para>
    /// <b>The six-second silence window does not cover it.</b> That window (ADR-cross-0027,
    /// <c>SessionLiveness.Timeout</c>) is enforced on the connection's read. A peer that stops talking is closed by it,
    /// and a stuck write fails with the socket. A peer that goes on sending heartbeats but no longer reads is not: the
    /// server reads the heartbeat, its HeartbeatAck queues behind the stuck write, and the read loop stops on that write,
    /// never reaching the read the window is measured on. The two bounds cover the two halves.
    /// </para>
    /// <para>
    /// <b>Why 5 seconds.</b> A peer that is reading never makes a write of a few kilobytes wait: the kernel buffers
    /// between the two ends take tens of kilobytes before a write blocks at all. A stuck vehicle costs the gate at most
    /// this long, once -- the connection is closed and the next send to it fails at once -- so three vehicles stuck
    /// in one round hold it for 15 seconds, inside the 30 seconds a fault clearance waits for it
    /// (<c>FAULT_RECOVERY_RUNTIME_BUSY</c>). It is also just under the silence window, so a peer that stops reading is
    /// let go no later than one that stops talking. Raising it past 10 seconds lets three stuck vehicles use up a
    /// clearance's whole wait.
    /// </para>
    /// </remarks>
    public TimeSpan WriteTimeout { get; set; } = DefaultWriteTimeout;

    public string CredentialEnvironmentVariable { get; set; } = "CONTROL_SERVER_ONBOARD_CREDENTIAL";
}

/// <summary>
/// Refuses to start when a configuration file still carries a key from the TLS era. Options binding
/// ignores unknown keys silently, which would leave an operator reading a certificate path out of
/// appsettings while the transport is in fact plaintext.
/// </summary>
public sealed class OnboardTransportOptionsValidator(
    IConfiguration configuration) : IValidateOptions<OnboardTransportOptions>
{
    private static readonly (string Section, string Key)[] RemovedKeys =
    [
        (OnboardTransportOptions.SectionName, "serverCertificatePath"),
        (OnboardTransportOptions.SectionName, "serverCertificatePasswordEnvironmentVariable"),
        (OnboardTransportOptions.SectionName, "allowInsecureLoopback"),
        ("OnboardSafetyProjection", "requireHttps")
    ];

    public ValidateOptionsResult Validate(string? name, OnboardTransportOptions options)
    {
        _ = name;
        _ = options;

        List<string> failures = [];
        foreach ((string section, string key) in RemovedKeys)
        {
            if (configuration.GetSection(section).GetChildren()
                .Any(child => string.Equals(child.Key, key, StringComparison.OrdinalIgnoreCase)))
            {
                failures.Add(
                    $"{section}:{key} was removed in this version; the Onboard transport is plaintext. " +
                    "Delete the key from every appsettings file.");
            }
        }
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
