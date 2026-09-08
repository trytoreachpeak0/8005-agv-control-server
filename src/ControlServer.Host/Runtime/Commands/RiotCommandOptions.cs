using Microsoft.Extensions.Options;

namespace ControlServer.Host.Runtime.Commands;

/// <summary>
/// The controlled backoff REQ-0248 requires while an emergency stop is unconfirmed.
/// </summary>
/// <remarks>
/// <para>
/// <b>There is no attempt limit, and there is not going to be one.</b> REQ-0248 says the retry
/// continues until the state is confirmed as <c>CAN_RECOVER</c> or <c>CAN_NOT_RECOVER</c>; a cap
/// would mean the server stops trying to stop a moving vehicle after some number nobody can
/// justify. What is bounded is the rate, not the persistence.
/// </para>
/// <para>
/// The values are settable because a plant is not a test rig — the delay that keeps a saturated
/// RIoT from being made worse is a site fact — not because they are arbitrary. The defaults are
/// deliberately impatient at the start: the first seconds are when a vehicle is still moving.
/// </para>
/// </remarks>
public sealed class RiotCommandOptions
{
    public const string SectionName = "RiotCommands";

    /// <summary>How long after an unconfirmed <c>triggerEmergency</c> the next one may go out.</summary>
    public TimeSpan EmergencyRetryInitialDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Multiplier applied per further unconfirmed attempt.</summary>
    public double EmergencyRetryBackoffFactor { get; set; } = 2.0;

    /// <summary>
    /// The ceiling the backoff climbs to and stays at. Reached, the server keeps retrying at this
    /// interval indefinitely.
    /// </summary>
    public TimeSpan EmergencyRetryMaxDelay { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Refuses a backoff that would not back off, or that would retry faster than it started.
/// </summary>
public sealed class RiotCommandOptionsValidator : IValidateOptions<RiotCommandOptions>
{
    public ValidateOptionsResult Validate(string? name, RiotCommandOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        List<string> failures = [];
        if (options.EmergencyRetryInitialDelay <= TimeSpan.Zero)
        {
            failures.Add(
                $"{RiotCommandOptions.SectionName}:EmergencyRetryInitialDelay must be positive.");
        }

        if (options.EmergencyRetryBackoffFactor < 1.0)
        {
            failures.Add(
                $"{RiotCommandOptions.SectionName}:EmergencyRetryBackoffFactor must be at least 1.0; " +
                "a factor below one shortens the delay with every failure.");
        }

        if (options.EmergencyRetryMaxDelay < options.EmergencyRetryInitialDelay)
        {
            failures.Add(
                $"{RiotCommandOptions.SectionName}:EmergencyRetryMaxDelay " +
                $"({options.EmergencyRetryMaxDelay}) must not be below EmergencyRetryInitialDelay " +
                $"({options.EmergencyRetryInitialDelay}).");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
