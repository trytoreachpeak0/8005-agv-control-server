using Microsoft.Extensions.Options;

namespace ControlServer.Host.Runtime.Faults;

/// <summary>
/// The consecutive count and the time window REQ-0247 leaves to be set alongside the rest of the
/// monitoring freshness rules.
/// </summary>
/// <remarks>
/// <para>
/// REQ-0247 fixes the shape of the proof — a fresh, unambiguous non-moving <c>movementState</c>,
/// several consecutive readings with an unchanged position, and no sign of movement in between —
/// and says in as many words that the count and the window are settled together with the other
/// monitoring rules. These are those two numbers, and nothing else lives here: what counts as
/// evidence is not configurable, only how much of it and how recent.
/// </para>
/// <para>
/// <b>Every default errs towards refusing the proof.</b> Three samples rather than two, a floor on
/// the spacing so that three reads in the same instant cannot pass for three moments, and a
/// ceiling on it so that a gap nobody watched cannot be counted as a period with no sign of
/// movement. A rejected proof escalates to an emergency stop, which is the safe direction to be
/// wrong in.
/// </para>
/// </remarks>
public sealed class VehicleFaultOptions
{
    public const string SectionName = "VehicleFault";

    /// <summary>How many consecutive samples must agree. REQ-0247's "连续多次".</summary>
    public int StopProofSampleCount { get; set; } = 3;

    /// <summary>
    /// The shortest gap between two samples that still counts as two moments.
    /// </summary>
    /// <remarks>
    /// Without a floor, a caller that evaluated three times in a loop would satisfy "several
    /// consecutive readings" inside a millisecond, which proves nothing about a vehicle that takes
    /// seconds to coast to a halt.
    /// </remarks>
    public TimeSpan MinimumSampleInterval { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// The longest gap between two samples that still counts as continuous observation.
    /// </summary>
    /// <remarks>
    /// This is the half of REQ-0247 that is easiest to lose: "确认期间没有新的移动迹象" is a claim
    /// about the whole interval, and a server that looked twice an hour apart cannot make it. A
    /// vehicle can leave and return to the same station in that hour and every sample would agree.
    /// </remarks>
    public TimeSpan MaximumSampleInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How old the newest sample may be and still be called fresh.</summary>
    public TimeSpan MaximumEvidenceAge { get; set; } = TimeSpan.FromSeconds(3);
}

/// <summary>
/// Refuses a configuration that would let a weaker proof through than REQ-0247 describes.
/// </summary>
public sealed class VehicleFaultOptionsValidator : IValidateOptions<VehicleFaultOptions>
{
    public ValidateOptionsResult Validate(string? name, VehicleFaultOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        List<string> failures = [];
        if (options.StopProofSampleCount < 2)
        {
            failures.Add(
                $"{VehicleFaultOptions.SectionName}:StopProofSampleCount must be at least 2; " +
                "REQ-0247 requires consecutive readings, and one reading is a single query, which " +
                "it names as insufficient on its own.");
        }

        if (options.MinimumSampleInterval <= TimeSpan.Zero)
        {
            failures.Add(
                $"{VehicleFaultOptions.SectionName}:MinimumSampleInterval must be positive; " +
                "samples taken in the same instant are one observation, not several.");
        }

        if (options.MaximumSampleInterval < options.MinimumSampleInterval)
        {
            failures.Add(
                $"{VehicleFaultOptions.SectionName}:MaximumSampleInterval " +
                $"({options.MaximumSampleInterval}) must not be below MinimumSampleInterval " +
                $"({options.MinimumSampleInterval}).");
        }

        if (options.MaximumEvidenceAge <= TimeSpan.Zero)
        {
            failures.Add(
                $"{VehicleFaultOptions.SectionName}:MaximumEvidenceAge must be positive; " +
                "REQ-0247 requires the facts to be fresh, and any non-positive age is unsatisfiable.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
