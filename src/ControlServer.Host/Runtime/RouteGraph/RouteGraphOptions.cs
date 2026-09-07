using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace ControlServer.Host.Runtime.RouteGraph;

/// <summary>
/// The engine's two refresh cycles and its staleness budget.
/// </summary>
/// <remarks>
/// Defaults are the specification's (5.6): design state on <c>gmtUpdate</c> with a 10-minute TTL
/// backstop, runtime removals on their own 10-second cycle. They are settable because a plant is
/// not a test rig, not because the values are arbitrary.
/// </remarks>
public sealed class RouteGraphOptions
{
    public const string SectionName = "RouteGraph";

    /// <summary>Whether the engine runs at all. Off leaves dispatch exactly as it was before it existed.</summary>
    public bool Enabled { get; set; }

    /// <summary>The Map the engine holds a graph for.</summary>
    [Range(1, int.MaxValue)]
    public int MapId { get; set; }

    /// <summary>
    /// How long a design state may stand without a refresh, regardless of what <c>gmtUpdate</c>
    /// says. The backstop exists because <c>gmtUpdate</c> is RIoT's account of its own changes:
    /// if it stops advancing for a reason that is not "nothing changed", nothing else would notice.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:10", "24:00:00")]
    public TimeSpan DesignStateTtl { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>How often the runtime removals are re-read.</summary>
    [Range(typeof(TimeSpan), "00:00:01", "00:10:00")]
    public TimeSpan RuntimeRefreshPeriod { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long the runtime state may stand before the snapshot is stale.
    /// </summary>
    /// <remarks>
    /// Separate from the period, and necessarily larger: one missed cycle is a hiccup, and a
    /// budget equal to the period would make every hiccup a dispatch outage. Validated against the
    /// period at startup.
    /// </remarks>
    [Range(typeof(TimeSpan), "00:00:02", "00:30:00")]
    public TimeSpan RuntimeStateMaxAge { get; set; } = TimeSpan.FromSeconds(45);
}

/// <summary>
/// Refuses a configuration whose runtime budget is not larger than its refresh period.
/// </summary>
/// <remarks>
/// The same shape REQ-0302 requires of the catalog's two parameters, and for the same reason: a
/// maximum age at or below the period means the state is stale the instant it is written, and the
/// engine would block every round while looking perfectly configured.
/// </remarks>
public sealed class RouteGraphOptionsValidator : IValidateOptions<RouteGraphOptions>
{
    public ValidateOptionsResult Validate(string? name, RouteGraphOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        List<string> failures = [];
        if (options.MapId <= 0)
        {
            failures.Add("RouteGraph:MapId must be a positive Map id when the engine is enabled.");
        }

        if (options.RuntimeStateMaxAge <= options.RuntimeRefreshPeriod)
        {
            failures.Add(
                $"RouteGraph:RuntimeStateMaxAge ({options.RuntimeStateMaxAge}) must exceed " +
                $"RouteGraph:RuntimeRefreshPeriod ({options.RuntimeRefreshPeriod}).");
        }

        if (options.DesignStateTtl <= options.RuntimeRefreshPeriod)
        {
            failures.Add(
                $"RouteGraph:DesignStateTtl ({options.DesignStateTtl}) must exceed " +
                $"RouteGraph:RuntimeRefreshPeriod ({options.RuntimeRefreshPeriod}).");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
