using Microsoft.Extensions.Options;

namespace ControlServer.Host.Runtime;

/// <summary>
/// The switch and the credential for the vehicle fault recovery entry point (control-server#299).
/// </summary>
/// <remarks>
/// <para>
/// <b>Off by default</b>, like REQ-0356's release: the entry point clears a vehicle's fault and hands its demands back for
/// redispatch, so an installed server does not offer it until a site turns it on deliberately.
/// </para>
/// <para>
/// <b>This is not authentication</b>, the same arrangement as <see cref="EmergencyStopReleaseOptions"/>: one shared
/// credential in an environment variable keeps out whoever can merely reach the port, and who the person is comes from
/// the <c>operatorId</c> in the request, recorded verbatim. REQ-0253 asks for a personal administrator account; the user
/// accepted this in its place for REQ-0356 on 2026-09-15 and for the fault recovery on 2026-09-22, until the server has
/// logins. A credential of its own, not the release's, so a site can offer one entry point without the other.
/// </para>
/// </remarks>
public sealed class VehicleFaultRecoveryOptions
{
    public const string SectionName = "VehicleFaultRecovery";

    public bool Enabled { get; set; }

    public string CredentialEnvironmentVariable { get; set; } = "CONTROL_SERVER_FAULT_RECOVERY_CREDENTIAL";
}

public sealed class VehicleFaultRecoveryOptionsValidator : IValidateOptions<VehicleFaultRecoveryOptions>
{
    public ValidateOptionsResult Validate(string? name, VehicleFaultRecoveryOptions options)
    {
        _ = name;
        ArgumentNullException.ThrowIfNull(options);
        if (!options.Enabled) return ValidateOptionsResult.Success;

        List<string> failures = [];
        if (string.IsNullOrWhiteSpace(options.CredentialEnvironmentVariable) ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(options.CredentialEnvironmentVariable)))
            failures.Add("VehicleFaultRecovery must name a populated external credential variable.");
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
