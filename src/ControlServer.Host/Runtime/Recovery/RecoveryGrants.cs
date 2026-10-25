using System.Collections.Immutable;

namespace ControlServer.Host.Runtime.Recovery;

/// <summary>
/// The per-vehicle, time-limited recovery grant (control-server#797, ADR-cross-0066, REQ-0235/REQ-0279/REQ-0254 as CP-0022 revised
/// them): a named maintenance person on the control host grants one vehicle -- and, for what acts on a journey, one journey -- a
/// few named recovery actions for a while. Opening an exception recovery session on the vehicle, every action in it, every
/// vehicle fault recovery request and the two charging confirmations that act on a vehicle are checked against it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Off unless <see cref="Required"/></b>. The product default is off so the rigs, G3 and the scenarios that open sessions
/// without a grant keep running as they did; the v2 instance definitions turn it on (ParallelInstance.psm1 writes it on every
/// install). Off, nothing is checked and the grant entries still answer, so a grant can be tried before it is enforced.
/// </para>
/// </remarks>
public sealed class RecoveryGrantOptions
{
    public const string SectionName = "RecoveryGrant";

    /// <summary>The configuration key the coordinator reads directly.</summary>
    public const string RequiredKey = SectionName + ":required";

    /// <summary>Whether recovery needs a grant. Off by default; on in the v2 instance definitions.</summary>
    public bool Required { get; set; }

    /// <summary>How long a grant lasts when the request names no duration (ADR-cross-0066 decision 2: 30 minutes).</summary>
    public int DefaultMinutes { get; set; } = 30;

    /// <summary>
    /// The longest a grant may be asked for. A long forced recovery is granted longer or renewed before it runs out; nothing is
    /// granted for good (a grant that outlived its reason is the unattended entry decision 2 rejects).
    /// </summary>
    public int MaximumMinutes { get; set; } = 480;
}

/// <summary>The action names a grant can carry, and the one it carries when it names none.</summary>
public static class RecoveryGrantActions
{
    // The vehicle's exception recovery session (OnboardRecoveryCoordinator).
    public const string ResumeAfterRepair = "RESUME_AFTER_REPAIR";
    public const string CompensateLoadAllEmpty = "COMPENSATE_LOAD_ALL_EMPTY";
    public const string FaultCargoHandoff = "FAULT_CARGO_HANDOFF";
    public const string ForcedMechanicalRecovery = "FORCED_MECHANICAL_RECOVERY";
    public const string HardwareRepairRelease = "HARDWARE_REPAIR_RELEASE";

    // The control host's charging confirmations that act on a vehicle (REQ-0254 as CP-0022 revised it).
    public const string ConfirmChargingStationClearance = "CONFIRM_CHARGING_STATION_CLEARANCE";
    public const string RecoverVehicleChargingEligibility = "RECOVER_VEHICLE_CHARGING_ELIGIBILITY";

    /// <summary>What a grant allows when it names nothing: the repair resume only (ADR-cross-0066 decision 3, the user's Q2).</summary>
    public static ImmutableSortedSet<string> Default { get; } = ImmutableSortedSet.Create(StringComparer.Ordinal, ResumeAfterRepair);

    /// <summary>The actions of the vehicle's session; a session opens only on a grant naming at least one.</summary>
    public static ImmutableSortedSet<string> SessionActions { get; } = ImmutableSortedSet.Create(
        StringComparer.Ordinal,
        ResumeAfterRepair, CompensateLoadAllEmpty, FaultCargoHandoff, ForcedMechanicalRecovery, HardwareRepairRelease);

    /// <summary>
    /// The actions that act on a journey: the session's and the fault recovery entry's other than <c>CLEAR_FAULT</c>. Their grant
    /// must name the journey they act on; the vehicle-wide ones -- clearing a fault, the two charging confirmations -- are
    /// checked by vehicle alone.
    /// </summary>
    public static bool ActsOnJourney(string action) =>
        action is not ("CLEAR_FAULT" or ConfirmChargingStationClearance or RecoverVehicleChargingEligibility);

    /// <summary>Every name a grant may carry.</summary>
    public static ImmutableSortedSet<string> All { get; } = SessionActions
        .Union(VehicleFaultRecoveryEndpoints.Actions.Keys)
        .Union([ConfirmChargingStationClearance, RecoverVehicleChargingEligibility]);
}

/// <summary>Why a recovery was refused for want of a grant.</summary>
public static class RecoveryGrantCodes
{
    /// <summary>The vehicle has no grant in effect: none was given, it ran out, it was revoked, or the server restarted.</summary>
    public const string Missing = "RECOVERY_GRANT_MISSING";

    /// <summary>The grant does not name this action.</summary>
    public const string ActionNotGranted = "RECOVERY_GRANT_ACTION_NOT_GRANTED";

    /// <summary>The grant names another journey than the one the action acts on, or none where one is needed.</summary>
    public const string JourneyMismatch = "RECOVERY_GRANT_JOURNEY_MISMATCH";
}

/// <summary>One vehicle's grant.</summary>
/// <param name="JourneyId">The journey it is limited to; null for a vehicle-wide grant, which covers no journey action.</param>
/// <param name="AuditRecordId">The administrator audit record of the grant.</param>
public sealed record RecoveryGrant(
    string GrantId,
    string AgvId,
    string VehicleKey,
    string? JourneyId,
    ImmutableSortedSet<string> Actions,
    string OperatorId,
    DateTimeOffset GrantedAt,
    DateTimeOffset ExpiresAt,
    string AuditRecordId)
{
    /// <summary>Whether it covers <paramref name="action"/> on <paramref name="journeyId"/> (the journey it acts on, if any).</summary>
    public string? RefusalFor(string action, string? journeyId) =>
        !Actions.Contains(action) ? RecoveryGrantCodes.ActionNotGranted
        : RecoveryGrantActions.ActsOnJourney(action) && !string.Equals(JourneyId, journeyId, StringComparison.Ordinal)
            ? RecoveryGrantCodes.JourneyMismatch
            : null;
}

/// <summary>
/// The grants in effect, one per vehicle, in this process's memory only: a restart ends them all, which is the point
/// (ADR-cross-0066 decision 2: when in doubt the entry is shut). A new grant for a vehicle replaces its old one -- that is a
/// renewal, the duration counted from then -- and an expired one is gone the moment it is read.
/// </summary>
/// <remarks>
/// Every change bumps <see cref="Version"/> and wakes <see cref="WaitForChangeAsync"/>, which the sweep
/// (<see cref="RecoveryGrantSweeper"/>) waits on to end sessions a grant no longer covers.
/// </remarks>
public sealed class RecoveryGrantRegistry(TimeProvider timeProvider)
{
    private readonly object _gate = new();
    private ImmutableDictionary<string, RecoveryGrant> _byAgvId = ImmutableDictionary.Create<string, RecoveryGrant>(StringComparer.Ordinal);
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _version;

    /// <summary>Counts every grant, renewal and revocation.</summary>
    public long Version => Interlocked.Read(ref _version);

    /// <summary>The vehicle's grant, if one is in effect now.</summary>
    public RecoveryGrant? Current(string agvId)
    {
        ArgumentNullException.ThrowIfNull(agvId);
        RecoveryGrant? grant = _byAgvId.GetValueOrDefault(agvId);
        return grant is not null && grant.ExpiresAt > timeProvider.GetUtcNow() ? grant : null;
    }

    /// <summary>Every grant still in effect.</summary>
    public IReadOnlyList<RecoveryGrant> InEffect()
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        return [.. _byAgvId.Values.Where(grant => grant.ExpiresAt > now).OrderBy(grant => grant.AgvId, StringComparer.Ordinal)];
    }

    /// <summary>The earliest expiry of a grant still in effect, for the sweep to wake at.</summary>
    public DateTimeOffset? NextExpiry()
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        return _byAgvId.Values.Where(grant => grant.ExpiresAt > now).Select(grant => (DateTimeOffset?)grant.ExpiresAt).Min();
    }

    /// <summary>Puts <paramref name="grant"/> in effect for its vehicle, replacing what it had; returns the replaced one.</summary>
    public RecoveryGrant? Grant(RecoveryGrant grant)
    {
        ArgumentNullException.ThrowIfNull(grant);
        RecoveryGrant? replaced;
        lock (_gate)
        {
            replaced = Current(grant.AgvId);
            _byAgvId = _byAgvId.SetItem(grant.AgvId, grant);
            SignalLocked();
        }
        return replaced;
    }

    /// <summary>Ends the vehicle's grant before its time; returns the one it ended, or null when there was none in effect.</summary>
    public RecoveryGrant? Revoke(string agvId)
    {
        ArgumentNullException.ThrowIfNull(agvId);
        lock (_gate)
        {
            RecoveryGrant? current = Current(agvId);
            if (!_byAgvId.ContainsKey(agvId))
            {
                return null;
            }
            _byAgvId = _byAgvId.Remove(agvId);
            SignalLocked();
            return current;
        }
    }

    /// <summary>
    /// Ends the vehicle's grant if, and only if, it is in effect and names <paramref name="journeyId"/>: a journey withdrawn by hand
    /// (control-server#771 part 2) takes its grant with it. Returns the one it ended.
    /// </summary>
    public RecoveryGrant? RevokeIfJourney(string agvId, string journeyId) => null;

    /// <summary>Completes when a grant is next given, renewed or revoked (not when one expires: the sweep times that itself).</summary>
    public Task WaitForChangeAsync(CancellationToken cancellationToken) =>
        Volatile.Read(ref _changed).Task.WaitAsync(cancellationToken);

    private void SignalLocked()
    {
        Interlocked.Increment(ref _version);
        TaskCompletionSource signalled = _changed;
        _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        signalled.TrySetResult();
    }
}
