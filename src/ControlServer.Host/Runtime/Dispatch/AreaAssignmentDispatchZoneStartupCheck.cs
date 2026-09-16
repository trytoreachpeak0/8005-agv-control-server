using ControlServer.Application;
using Microsoft.Extensions.Options;

namespace ControlServer.Host.Runtime.Dispatch;

/// <summary>
/// Refuses to start a dispatching server whose current area assignment version routes an AREA into a dispatch
/// zone <c>JourneyRuntime:allowedDispatchZones</c> does not allow.
/// </summary>
/// <remarks>
/// <para>
/// The half of control-server#49's startup check that survives batch 4. The dispatch zone used to be one
/// configured value checked against the allowed list by <see cref="JourneyRuntimeOptionsValidator"/>; now each
/// AREA's zone comes from the table, so the same check has to read the table, and only the database can
/// answer it. Every offending entry is named, not just the first, so one restart fixes all of them.
/// </para>
/// <para>
/// The other half — an allowed zone that no vehicle serves — is deliberately not checked here. It is a
/// runtime fact (<see cref="Criteria.DispatchZoneVehicleCriterion.ZoneNotConfiguredReason"/>) that
/// control-server#74 classifies as a structural block, and refusing to start on it would take down every other
/// zone with it.
/// </para>
/// <para>
/// Skipped when the journey runtime is off, for the same reason the options validator skips its zone check:
/// that server dispatches nothing, and its allowed list means nothing. A version imported while the server is
/// running is not re-checked; a candidate routed into a disallowed zone is still refused at runtime by
/// <see cref="Criteria.StationResolutionCriterion"/>.
/// </para>
/// </remarks>
public static class AreaAssignmentDispatchZoneStartupCheck
{
    /// <summary>The entries of <paramref name="table"/> whose zone <paramref name="allowedDispatchZones"/> omits, by AREA.</summary>
    public static IReadOnlyList<AreaAssignment> FindDisallowed(
        AreaAssignmentTableVersion? table,
        IReadOnlyCollection<string> allowedDispatchZones)
    {
        ArgumentNullException.ThrowIfNull(allowedDispatchZones);

        return table is null
            ? []
            :
            [
                .. table.ByArea.Values
                    .Where(assignment => !allowedDispatchZones.Contains(assignment.DispatchZone, StringComparer.Ordinal))
                    .OrderBy(assignment => assignment.Area, StringComparer.Ordinal)
            ];
    }

    /// <summary>Throws <see cref="InvalidOperationException"/> naming every offending entry, if there is one.</summary>
    public static async Task EnsureAsync(
        IAreaAssignmentStore areaAssignments,
        JourneyRuntimeOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(areaAssignments);
        ArgumentNullException.ThrowIfNull(options);
        if (!options.Enabled)
        {
            return;
        }

        AreaAssignmentTableVersion? table = await areaAssignments.ReadCurrentAsync(cancellationToken)
            .ConfigureAwait(false);
        IReadOnlyList<AreaAssignment> disallowed = FindDisallowed(table, options.AllowedDispatchZones);
        if (disallowed.Count == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Area assignment version {table!.Version} routes {disallowed.Count} AREA(s) into a dispatch zone " +
            $"JourneyRuntime:allowedDispatchZones does not allow ({string.Join(", ", options.AllowedDispatchZones)}): " +
            string.Join("; ", disallowed.Select(assignment => $"{assignment.Area} -> {assignment.DispatchZone}")) +
            ". Import a corrected table or allow the zone before starting.");
    }

    /// <summary>The host's entry point: resolves the store and the options from a fresh scope.</summary>
    public static async Task EnsureAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);

        await using AsyncServiceScope scope = services.CreateAsyncScope();
        await EnsureAsync(
                scope.ServiceProvider.GetRequiredService<IAreaAssignmentStore>(),
                scope.ServiceProvider.GetRequiredService<IOptions<JourneyRuntimeOptions>>().Value,
                cancellationToken)
            .ConfigureAwait(false);
    }
}
