using Microsoft.Extensions.Options;

namespace ControlServer.Host.Runtime;

public sealed class OnboardSafetyProjectionOptions
{
    public const string SectionName = "OnboardSafetyProjection";

    public bool Enabled { get; set; }
    public string CredentialEnvironmentVariable { get; set; } = "CONTROL_SERVER_ONBOARD_CREDENTIAL";

    /// <summary>
    /// How long a complete non-final order listing may stand in for one that did not add up (control-server#573); 0 turns
    /// the carry-over off.
    /// </summary>
    public int OrderCoverageCarryOverMs { get; set; } = 3_000;
}

public sealed class OnboardSafetyProjectionOptionsValidator : IValidateOptions<OnboardSafetyProjectionOptions>
{
    /// <summary>
    /// The onboard trusts a STOPPED observation up to 5000 ms after its <c>observedAt</c>, and its clock may run up to
    /// 1000 ms ahead of this server's (<c>VehicleSafetySettings.MaximumEvidenceAgeMs</c> and the cap on
    /// <c>ClockSkewToleranceMs</c>, onboard-hmi <c>w2g/fp-v2-impl</c>). A carried observation older than the difference
    /// could arrive already expired, which is the flicker control-server#573 removes.
    /// </summary>
    internal const int MaximumOrderCoverageCarryOverMs = 5_000 - 1_000;

    public ValidateOptionsResult Validate(string? name, OnboardSafetyProjectionOptions options)
    {
        _ = name;
        // control-server#573: checked even while the projection is off, so a bad value is found before the day it is turned on.
        if (options.OrderCoverageCarryOverMs is < 0 or > MaximumOrderCoverageCarryOverMs)
            return ValidateOptionsResult.Fail(
                $"OnboardSafetyProjection OrderCoverageCarryOverMs must be between 0 and {MaximumOrderCoverageCarryOverMs}.");
        if (!options.Enabled) return ValidateOptionsResult.Success;

        List<string> failures = [];
        if (string.IsNullOrWhiteSpace(options.CredentialEnvironmentVariable) ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(options.CredentialEnvironmentVariable)))
            failures.Add("OnboardSafetyProjection must name a populated external credential variable.");
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
