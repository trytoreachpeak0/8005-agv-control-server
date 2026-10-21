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

    /// <summary>
    /// How long a repair resumption keeps reading the order back after RIoT accepted the continue, before it answers that
    /// the continue is still awaiting confirmation (control-server#791).
    /// </summary>
    /// <remarks>
    /// RIoT accepts CONTINUE_FROM_HELD before it moves the order: on agv01 on 2026-10-19 the read 50 ms after the call
    /// still said HELD (7), and the order read SUCCESS (5) from 16:38:24 on. Zero reads once, as before this ticket.
    /// It is spent as a count, <see cref="ResumeConfirmationRereads"/> reads one interval apart, not as a deadline: each read
    /// can itself take up to the RIoT client's timeout, so with RIoT not answering the entry answers minutes later.
    /// </remarks>
    public TimeSpan ResumeConfirmationBudget { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>The gap between two of those reads.</summary>
    public TimeSpan ResumeConfirmationInterval { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>How many reads after the first the budget allows.</summary>
    internal int ResumeConfirmationRereads =>
        (int)Math.Ceiling(ResumeConfirmationBudget / ResumeConfirmationInterval);

    /// <summary>
    /// How long a driving vehicle's slot may read unknown (<c>SLOT_STATE_UNKNOWN</c>), from the moment this server received
    /// it, before its doors count as not proven locked (REQ-0246 as revised by CP-0017; control-server#789). Zero gives no
    /// grace, as before CP-0017. A door read not locked is never graced.
    /// </summary>
    public TimeSpan DoorUnknownGrace { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>How many graces one vehicle may start within <see cref="DoorUnknownGraceWindow"/>; the next unknown gets none.</summary>
    public int DoorUnknownGraceLimit { get; set; } = 2;

    /// <summary>The sliding window <see cref="DoorUnknownGraceLimit"/> counts in.</summary>
    public TimeSpan DoorUnknownGraceWindow { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How long the doors must read locked without a break before a stop raised only for doors that could not be read is
    /// released on its own (REQ-0167 as revised by CP-0017).
    /// </summary>
    public TimeSpan DoorReadingRestoredFor { get; set; } = TimeSpan.FromSeconds(10);
}

/// <summary>
/// Refuses a configuration that would let a weaker proof through than REQ-0247 describes.
/// </summary>
public sealed class VehicleFaultOptionsValidator : IValidateOptions<VehicleFaultOptions>
{
    internal static readonly TimeSpan MaximumDoorUnknownGrace = TimeSpan.FromSeconds(10);
    internal const int MaximumDoorUnknownGraceLimit = 10;
    internal static readonly TimeSpan MaximumDoorUnknownGraceWindow = TimeSpan.FromHours(24);
    internal static readonly TimeSpan MinimumDoorReadingRestoredFor = TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan MaximumDoorReadingRestoredFor = TimeSpan.FromMinutes(5);

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

        if (options.ResumeConfirmationInterval <= TimeSpan.Zero)
        {
            failures.Add(
                $"{VehicleFaultOptions.SectionName}:ResumeConfirmationInterval must be positive; " +
                "reads with no gap between them are one read.");
        }

        if (options.ResumeConfirmationBudget < TimeSpan.Zero)
        {
            failures.Add($"{VehicleFaultOptions.SectionName}:ResumeConfirmationBudget must not be negative.");
        }

        // CP-0017's values are the defaults. The ceilings keep a typo from turning the grace into a drive with the doors
        // unread: at the 0.55 m/s the vehicles drive, 10 s is about 5.5 m (control-server#789).
        if (options.DoorUnknownGrace < TimeSpan.Zero || options.DoorUnknownGrace > MaximumDoorUnknownGrace)
        {
            failures.Add(
                $"{VehicleFaultOptions.SectionName}:DoorUnknownGrace must be between 0 and {MaximumDoorUnknownGrace}; " +
                "zero gives no grace.");
        }

        if (options.DoorUnknownGraceLimit < 0 || options.DoorUnknownGraceLimit > MaximumDoorUnknownGraceLimit)
        {
            failures.Add(
                $"{VehicleFaultOptions.SectionName}:DoorUnknownGraceLimit must be between 0 and {MaximumDoorUnknownGraceLimit}; " +
                "zero gives no grace.");
        }

        if (options.DoorUnknownGraceWindow <= TimeSpan.Zero || options.DoorUnknownGraceWindow > MaximumDoorUnknownGraceWindow)
        {
            failures.Add(
                $"{VehicleFaultOptions.SectionName}:DoorUnknownGraceWindow must be positive and at most " +
                $"{MaximumDoorUnknownGraceWindow}.");
        }

        if (options.DoorReadingRestoredFor < MinimumDoorReadingRestoredFor ||
            options.DoorReadingRestoredFor > MaximumDoorReadingRestoredFor)
        {
            failures.Add(
                $"{VehicleFaultOptions.SectionName}:DoorReadingRestoredFor must be between {MinimumDoorReadingRestoredFor} and " +
                $"{MaximumDoorReadingRestoredFor}; one locked reading by chance must not release a stop.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
