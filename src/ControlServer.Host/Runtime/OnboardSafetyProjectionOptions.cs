using Microsoft.Extensions.Options;

namespace ControlServer.Host.Runtime;

public sealed class OnboardSafetyProjectionOptions
{
    public const string SectionName = "OnboardSafetyProjection";

    public bool Enabled { get; set; }
    public string CredentialEnvironmentVariable { get; set; } = "CONTROL_SERVER_ONBOARD_CREDENTIAL";

    /// <summary>
    /// How many more times one onboard projection request reads a non-final order listing that did not add up
    /// (control-server#573). Each reread is a whole read of the listing, so on RIoT (shared with the MVP line) one poll costs at
    /// most 1 + pages x (1 + this) requests: 10 at the default and 3 pages, 16 at the cap.
    /// </summary>
    /// <remarks>
    /// Two is what the field measured for (control-server#573: about 0.2 to 0.5% of reads did not add up on 2026-10-10, 1.3% at
    /// the worst five minutes). Raise it before anything else if the field ever shows a projection flickering again; the next
    /// step after that is a background refresh of the listing, which this ticket deliberately did not build.
    /// </remarks>
    public int NonFinalOrderReadRetries { get; set; } = 2;

    /// <summary>
    /// The onboard's own timeout for one projection request: its <c>RequestTimeoutMs</c> for the vehicle-safety projection, 3000
    /// out of the box. Kept here only to check <see cref="ReadBudgetMilliseconds"/> against; change it when the onboard's changes.
    /// </summary>
    public int OnboardRequestTimeoutMilliseconds { get; set; } = 3_000;

    /// <summary>
    /// How long one projection request may spend reading RIoT, rereads included (control-server#573). It must stay below
    /// <see cref="OnboardRequestTimeoutMilliseconds"/> so the answer arrives before the onboard gives up; when it runs out the
    /// answer is unknown, never an error.
    /// </summary>
    /// <remarks>
    /// The field took 478 ms at most for a whole projection request on 2026-10-10 (p99 131 ms); a slow rig took over 3 seconds
    /// once it reread (run 38082575561).
    /// </remarks>
    public int ReadBudgetMilliseconds { get; set; } = 2_000;

    /// <summary>One projection credential per vehicle (control-server#600).</summary>
    /// <remarks>
    /// <para>
    /// <b>Empty means one vehicle and the shared credential</b>: <see cref="CredentialEnvironmentVariable"/> answers for the
    /// vehicle <c>JourneyRuntime:VehicleKey</c> names, as before control-server#600. With a roster of more than one vehicle the
    /// server cannot tell from a shared credential which vehicle is asking, so that combination is refused at startup.
    /// </para>
    /// <para>
    /// When set, the bearer decides the vehicle: each credential reads its own vehicle's projection and no other, the shared
    /// credential reads none, and a <c>vehicleKey</c> the request names must be that vehicle. Every vehicle listed must be in the
    /// roster, and every value must differ from the others and from the shared onboard credential the TCP session uses -- a
    /// vehicle whose projection credential were the shared one could be read by every vehicle.
    /// </para>
    /// </remarks>
    public OnboardSafetyVehicleCredentialOptions[] VehicleCredentials { get; set; } = [];

    /// <summary>
    /// The vehicle keys this server drives: the roster, or the single vehicle <c>JourneyRuntime:VehicleKey</c> names when there is
    /// none (the same rule as <c>VehicleRoster</c>).
    /// </summary>
    internal static IReadOnlySet<string> RosterVehicleKeys(JourneyRuntimeOptions journey) =>
        journey.Fleet.Length == 0
            ? new HashSet<string>([journey.VehicleKey], StringComparer.Ordinal)
            : new HashSet<string>(journey.Fleet.Select(vehicle => vehicle.VehicleKey), StringComparer.Ordinal);
}

/// <summary>The environment variable holding one vehicle's projection credential (control-server#600).</summary>
public sealed class OnboardSafetyVehicleCredentialOptions
{
    public string VehicleKey { get; set; } = string.Empty;
    public string CredentialEnvironmentVariable { get; set; } = string.Empty;
}

public sealed class OnboardSafetyProjectionOptionsValidator : IValidateOptions<OnboardSafetyProjectionOptions>
{
    internal const int MaximumNonFinalOrderReadRetries = 4;

    public ValidateOptionsResult Validate(string? name, OnboardSafetyProjectionOptions options)
    {
        _ = name;
        // control-server#573: checked even while the projection is off, so a bad value is found before the day it is turned on.
        if (options.NonFinalOrderReadRetries is < 0 or > MaximumNonFinalOrderReadRetries)
            return ValidateOptionsResult.Fail(
                $"OnboardSafetyProjection NonFinalOrderReadRetries must be between 0 and {MaximumNonFinalOrderReadRetries}.");
        // A positive budget below the onboard's timeout also makes that timeout positive.
        if (options.ReadBudgetMilliseconds <= 0 || options.ReadBudgetMilliseconds >= options.OnboardRequestTimeoutMilliseconds)
            return ValidateOptionsResult.Fail(
                "OnboardSafetyProjection ReadBudgetMilliseconds must be positive and below OnboardRequestTimeoutMilliseconds " +
                $"({options.OnboardRequestTimeoutMilliseconds}), so the answer arrives before the onboard gives up.");
        if (!options.Enabled) return ValidateOptionsResult.Success;

        List<string> failures = [];
        if (string.IsNullOrWhiteSpace(options.CredentialEnvironmentVariable) ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(options.CredentialEnvironmentVariable)))
            failures.Add("OnboardSafetyProjection must name a populated external credential variable.");
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
