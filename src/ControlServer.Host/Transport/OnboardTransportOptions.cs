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
