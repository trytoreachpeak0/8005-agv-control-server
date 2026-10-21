using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ControlServer.Host.Runtime.Recovery;

/// <summary>
/// The grant check of the control host's per-request entries (control-server#797, ADR-cross-0066 decision 4): every vehicle fault
/// recovery request, every manual charger clearance and every vehicle charging eligibility recovery. The charger recovery
/// confirmation acts on a charger alone and is not checked (REQ-0254 as CP-0022 revised it).
/// </summary>
/// <remarks>
/// An action that acts on a journey (<see cref="RecoveryGrantActions.ActsOnJourney"/>) acts on the vehicle's one unfinished
/// journey -- the one <c>VehicleFaultRecoveryService</c> reads -- so the grant must name that journey; a vehicle-wide one needs the
/// vehicle and the action only.
/// </remarks>
public sealed class RecoveryGrantGate(
    RecoveryGrantRegistry registry,
    IOptions<RecoveryGrantOptions> options,
    ControlServerDbContext dbContext)
{
    public bool Required => options.Value.Required;

    /// <summary>Why <paramref name="action"/> on the vehicle is refused for want of a grant, or null when it may go on.</summary>
    public async Task<string?> RefusalAsync(string agvId, string action, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agvId);
        ArgumentNullException.ThrowIfNull(action);
        if (!Required)
        {
            return null;
        }
        if (registry.Current(agvId) is not RecoveryGrant grant)
        {
            return RecoveryGrantCodes.Missing;
        }
        string? journeyId = null;
        if (RecoveryGrantActions.ActsOnJourney(action))
        {
            string[] unfinished = await dbContext.JourneyRuntimes.AsNoTracking()
                .Where(row => row.AgvId == agvId && row.Stage != JourneyRuntimeStage.Completed)
                .Select(row => row.JourneyId)
                .ToArrayAsync(cancellationToken).ConfigureAwait(false);
            journeyId = unfinished.Length == 1 ? unfinished[0] : null;
        }
        return grant.RefusalFor(action, journeyId);
    }
}
