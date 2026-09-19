using ControlServer.Application;

namespace ControlServer.Host.Runtime;

/// <summary>
/// Where a task type's fixed station is taken from: read once per dispatch round, then asked per
/// candidate by task type (scope specification 5.3).
/// </summary>
/// <remarks>
/// <para>
/// Once per round for the same reason <see cref="Dispatch.DispatchRoundFacts"/> reads everything else
/// once: every candidate of a round must be judged against the same bindings, and a criterion that
/// re-read them could admit two candidates under two versions that never held at the same instant.
/// </para>
/// <para>
/// An implementation may throw <see cref="StationResolutionException"/> from
/// <see cref="ReadForRoundAsync"/>, which means the whole Map is unusable: the engine records a
/// catalog-level failure and ends the round, as it always has. A single task type that cannot be
/// resolved is not that — it is a refusal the view returns for that task type alone (REQ-0335), so
/// that one missing binding never takes every other task type down with it.
/// </para>
/// </remarks>
public interface IFixedTaskStationResolver
{
    Task<IFixedTaskStationView> ReadForRoundAsync(
        RiotMapStationCatalogSnapshot map,
        CancellationToken cancellationToken);
}

/// <summary>One round's fixed stations, answered per task type.</summary>
public interface IFixedTaskStationView
{
    /// <summary>Resolves one task type's fixed station. Never throws for a task type it cannot resolve.</summary>
    FixedTaskStationResolution Resolve(string taskType);
}

/// <summary>Which end of the transport the fixed station is.</summary>
public enum FixedStationEnd
{
    /// <summary>The transport starts at the fixed station (STAGING_TO_WIRE).</summary>
    Origin,

    /// <summary>The transport ends at the fixed station (WIRE_TO_GATE).</summary>
    Destination,
}

/// <summary>
/// One task type's fixed station, or why it has none. Exactly one of <see cref="Station"/> and
/// <see cref="RefusalReasonCode"/> is set.
/// </summary>
/// <remarks>
/// The two versions are what an acceptance freezes (REQ-0344); an implementation that has no
/// versioned bindings leaves them null.
/// </remarks>
public sealed record FixedTaskStationResolution
{
    private FixedTaskStationResolution(
        string taskType,
        FixedStationEnd fixedEnd,
        RiotMapStation? station,
        string? refusalReasonCode,
        long? ruleVersion,
        long? bindingSetVersion)
    {
        TaskType = taskType;
        FixedEnd = fixedEnd;
        Station = station;
        RefusalReasonCode = refusalReasonCode;
        RuleVersion = ruleVersion;
        BindingSetVersion = bindingSetVersion;
    }

    public string TaskType { get; }

    public FixedStationEnd FixedEnd { get; }

    public RiotMapStation? Station { get; }

    public string? RefusalReasonCode { get; }

    public long? RuleVersion { get; }

    public long? BindingSetVersion { get; }

    public static FixedTaskStationResolution Resolved(
        string taskType,
        FixedStationEnd fixedEnd,
        RiotMapStation station,
        long? ruleVersion = null,
        long? bindingSetVersion = null)
    {
        ArgumentNullException.ThrowIfNull(taskType);
        ArgumentNullException.ThrowIfNull(station);
        return new(taskType, fixedEnd, station, null, ruleVersion, bindingSetVersion);
    }

    public static FixedTaskStationResolution Refused(
        string taskType,
        FixedStationEnd fixedEnd,
        string refusalReasonCode,
        long? ruleVersion = null,
        long? bindingSetVersion = null)
    {
        ArgumentNullException.ThrowIfNull(taskType);
        ArgumentException.ThrowIfNullOrWhiteSpace(refusalReasonCode);
        return new(taskType, fixedEnd, null, refusalReasonCode, ruleVersion, bindingSetVersion);
    }
}
