using Microsoft.Extensions.Options;

namespace ControlServer.Host.Runtime;

public sealed class OnboardSafetyProjectionOptions
{
    public const string SectionName = "OnboardSafetyProjection";

    public bool Enabled { get; set; }
    public string CredentialEnvironmentVariable { get; set; } = "CONTROL_SERVER_ONBOARD_CREDENTIAL";
}

public sealed class OnboardSafetyProjectionOptionsValidator : IValidateOptions<OnboardSafetyProjectionOptions>
{
    public ValidateOptionsResult Validate(string? name, OnboardSafetyProjectionOptions options)
    {
        _ = name;
        if (!options.Enabled) return ValidateOptionsResult.Success;

        List<string> failures = [];
        if (string.IsNullOrWhiteSpace(options.CredentialEnvironmentVariable) ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(options.CredentialEnvironmentVariable)))
            failures.Add("OnboardSafetyProjection must name a populated external credential variable.");
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
