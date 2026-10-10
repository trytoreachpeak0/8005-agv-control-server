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
    public int OrderCoverageCarryOverMs { get; set; } = 1_500;
}

public sealed class OnboardSafetyProjectionOptionsValidator : IValidateOptions<OnboardSafetyProjectionOptions>
{
    /// <summary>
    /// The onboard trusts a STOPPED observation up to 5000 ms after its <c>observedAt</c>, judged each time it uses it
    /// (<c>VehicleSafetySettings.MaximumEvidenceAgeMs</c>, <c>IsStoppedAndFresh(DateTimeOffset.UtcNow, ...)</c>), and its
    /// clock may run up to 1000 ms ahead of this server's (the cap on <c>ClockSkewToleranceMs</c>). A carried observation
    /// has to last until the next poll lands: 1000 ms of poll interval (<c>PollIntervalMs</c>) and up to 1000 ms left for
    /// that request. All onboard-hmi <c>w2g/fp-v2-impl</c>. The first version allowed 4000 and defaulted to 3000; the real
    /// rig (run 38069023213) caught a carried observation expiring before the next poll answered (control-server#573).
    /// </summary>
    internal const int MaximumOrderCoverageCarryOverMs = 5_000 - 1_000 - 1_000 - 1_000;

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
