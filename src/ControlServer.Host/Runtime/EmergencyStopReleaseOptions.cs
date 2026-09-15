using Microsoft.Extensions.Options;

namespace ControlServer.Host.Runtime;

/// <summary>
/// The switch and the credential for REQ-0356's release-on-confirmation entry point.
/// </summary>
/// <remarks>
/// <para>
/// <b>Off by default.</b> The entry point takes an emergency stop off a vehicle, so an installed
/// server does not offer it until a site turns it on deliberately.
/// </para>
/// <para>
/// <b>This is not authentication.</b> The bearer credential is one shared environment variable, the
/// same arrangement as <see cref="SlotConfigurationActivationOptions"/>, because the server has no
/// login or permission model yet. It keeps out whoever can merely reach the port; who the person is
/// comes from the <c>operatorId</c> in the request, recorded verbatim. The user chose this on
/// 2026-09-15 until a login session can take its place.
/// </para>
/// </remarks>
public sealed class EmergencyStopReleaseOptions
{
    public const string SectionName = "EmergencyStopRelease";

    public bool Enabled { get; set; }

    public string CredentialEnvironmentVariable { get; set; } = "CONTROL_SERVER_EMERGENCY_RELEASE_CREDENTIAL";
}

public sealed class EmergencyStopReleaseOptionsValidator : IValidateOptions<EmergencyStopReleaseOptions>
{
    public ValidateOptionsResult Validate(string? name, EmergencyStopReleaseOptions options)
    {
        _ = name;
        ArgumentNullException.ThrowIfNull(options);
        if (!options.Enabled) return ValidateOptionsResult.Success;

        List<string> failures = [];
        if (string.IsNullOrWhiteSpace(options.CredentialEnvironmentVariable) ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(options.CredentialEnvironmentVariable)))
            failures.Add("EmergencyStopRelease must name a populated external credential variable.");
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
