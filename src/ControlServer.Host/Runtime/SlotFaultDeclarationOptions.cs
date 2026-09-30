using Microsoft.Extensions.Options;

namespace ControlServer.Host.Runtime;

/// <summary>
/// The switch and the credential for the manual slot-fault declaration entry point (REQ-0359, control-server#383).
/// </summary>
/// <remarks>
/// <para>
/// <b>Off by default</b>, like REQ-0356's release and the fault recovery: a declaration stops a slot operation on a vehicle,
/// so an installed server does not offer it until a site turns it on deliberately. While it is off the route is not mapped
/// and no <c>SlotFaultDeclarationCommand</c> can be written, which is also what keeps the command away from an onboard that
/// does not know it yet (onboard-hmi#215; an unknown RELIABLE command ends that vehicle's session, and every reconnect would
/// resend it).
/// </para>
/// <para>
/// <b>This is not authentication</b>, the same arrangement as <see cref="EmergencyStopReleaseOptions"/>: one shared credential
/// in an environment variable keeps out whoever can merely reach the port, and who the administrator is comes from the
/// <c>operatorId</c> in the request, recorded verbatim. REQ-0359 asks for the administrator's personal identity to be
/// verified; section 8 of CP-0005, approved with it, settles that until accounts exist (<c>FP-C6</c>) the entry point takes
/// the release's shared credential plus a personal id. A credential of its own, so a site can offer one entry point without
/// the others.
/// </para>
/// </remarks>
public sealed class SlotFaultDeclarationOptions
{
    public const string SectionName = "SlotFaultDeclaration";

    public bool Enabled { get; set; }

    /// <summary>
    /// The switch as every reader of it reads it: the route mapping, the reconnect replay and the startup warning all go
    /// through here, so "off" means the same thing to each (review of control-server#383, S1/S2).
    /// </summary>
    public static bool IsEnabled(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return configuration.GetValue<bool>(SectionName + ":enabled");
    }

    public string CredentialEnvironmentVariable { get; set; } = "CONTROL_SERVER_SLOT_FAULT_DECLARATION_CREDENTIAL";
}

public sealed class SlotFaultDeclarationOptionsValidator : IValidateOptions<SlotFaultDeclarationOptions>
{
    public ValidateOptionsResult Validate(string? name, SlotFaultDeclarationOptions options)
    {
        _ = name;
        ArgumentNullException.ThrowIfNull(options);
        if (!options.Enabled) return ValidateOptionsResult.Success;

        List<string> failures = [];
        if (string.IsNullOrWhiteSpace(options.CredentialEnvironmentVariable) ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(options.CredentialEnvironmentVariable)))
            failures.Add("SlotFaultDeclaration must name a populated external credential variable.");
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
