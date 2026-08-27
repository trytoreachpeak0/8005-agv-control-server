using Microsoft.Extensions.Options;

namespace ControlServer.Host.Runtime;

public sealed class OnboardSafetyProjectionOptions
{
    public const string SectionName = "OnboardSafetyProjection";

    public bool Enabled { get; set; }
    public bool RequireHttps { get; set; } = true;
    public string CredentialEnvironmentVariable { get; set; } = "CONTROL_SERVER_ONBOARD_CREDENTIAL";
}

public sealed class OnboardSafetyProjectionOptionsValidator(
    IConfiguration configuration) : IValidateOptions<OnboardSafetyProjectionOptions>
{
    public ValidateOptionsResult Validate(string? name, OnboardSafetyProjectionOptions options)
    {
        _ = name;
        if (!options.Enabled) return ValidateOptionsResult.Success;

        List<string> failures = [];
        if (!options.RequireHttps) failures.Add("OnboardSafetyProjection.RequireHttps must remain true.");
        if (!Uri.TryCreate(configuration["Health:url"], UriKind.Absolute, out Uri? uri) || uri.Scheme != "https")
            failures.Add("Health:url must be HTTPS when OnboardSafetyProjection is enabled.");
        if (string.IsNullOrWhiteSpace(options.CredentialEnvironmentVariable) ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(options.CredentialEnvironmentVariable)))
            failures.Add("OnboardSafetyProjection must name a populated external credential variable.");
        if (string.IsNullOrWhiteSpace(configuration["OnboardTransport:serverCertificatePath"]))
            failures.Add("OnboardTransport:serverCertificatePath is required for the HTTPS projection.");
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
