namespace ControlServer.Domain;

/// <summary>How far a vehicle's fault has been established (REQ-0232's two levels).</summary>
public enum VehicleFaultLevel
{
    /// <summary>No fault fact is currently held for this vehicle.</summary>
    None,

    /// <summary>
    /// A symptom was observed — offline, comms loss, navigation failure, a single order FAILED.
    /// Blocks new dispatch, but is not itself proof that the vehicle is isolated.
    /// </summary>
    SuspectedBlocked,

    /// <summary>
    /// Hard evidence from the closed whitelist (REQ-0233). Only facts on that list may reach this
    /// level automatically; anything else stops at <see cref="SuspectedBlocked"/>.
    /// </summary>
    ConfirmedIsolated,
}

/// <summary>Outcome of one RIoT order-command attempt, as reconciled.</summary>
public enum RiotOrderCommandOutcome
{
    /// <summary>Issued, terminal state not yet confirmed. A command is not successful here.</summary>
    Pending,

    /// <summary>Reconciliation confirmed the command's terminal state.</summary>
    Confirmed,

    /// <summary>The call itself failed and no state change can be assumed.</summary>
    Failed,

    /// <summary>The call's outcome is unknown — reconciliation must resolve it before any retry.</summary>
    Unknown,
}

/// <summary>Catalog-level state (REQ-0308) — distinct from a task-level block.</summary>
public enum MapStationCatalogState
{
    /// <summary>Last complete confirmation is fresh; the catalog may be used.</summary>
    Fresh,

    /// <summary>A refresh attempt failed. The catalog does not become unusable on this alone.</summary>
    RefreshFailed,

    /// <summary>The candidate returned by RIoT was invalid and was not adopted.</summary>
    CandidateInvalid,

    /// <summary>Time since the last complete confirmation exceeded the approved maximum.</summary>
    FreshnessExceeded,

    /// <summary>The build or environment does not match what the catalog was confirmed against.</summary>
    BuildIncompatible,
}

/// <summary>Which end of a transport demand a frozen station serves.</summary>
public enum FrozenStationRole
{
    Pickup,
    Dropoff,
}

/// <summary>What the pre-create gate decided.</summary>
public enum CreateGateVerdict
{
    Allowed,

    /// <summary>The catalog was not fresh enough to resolve endpoints from.</summary>
    BlockedCatalogNotFresh,

    /// <summary>The demand's AREA did not resolve to a station.</summary>
    BlockedStationUnresolved,

    /// <summary>RIoT's <c>getRouteCostsBy</c> said this vehicle cannot reach that station.</summary>
    BlockedUnreachable,

    /// <summary>
    /// RIoT could not be asked, or answered nothing about this vehicle. Deliberately not the same
    /// verdict as <see cref="BlockedUnreachable"/>: "we did not get an answer" recorded as "the
    /// station is unreachable" would put a claim about the Map into the audit that nobody made.
    /// </summary>
    BlockedRouteCostUnavailable,

    /// <summary>
    /// A station this demand froze at creation time is no longer in the current fresh catalog.
    /// This is REQ-0308's task level — a real missing station, proven by a snapshot that is
    /// itself fresh — as opposed to the catalog level, where the snapshot is what is in doubt.
    /// </summary>
    BlockedFrozenStationAbsent,

    /// <summary>
    /// The two evidence sources disagreed. Blocked and alarmed rather than silently picking one
    /// — the self-built graph and RIoT's RouteCost answer different questions and neither
    /// overrides the other.
    /// </summary>
    BlockedEvidenceConflict,
}
