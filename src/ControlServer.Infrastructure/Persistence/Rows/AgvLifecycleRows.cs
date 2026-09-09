using ControlServer.Domain;

namespace ControlServer.Infrastructure.Persistence;

/// <summary>
/// A vehicle's lifecycle, as two orthogonal dimensions plus one flag.
/// </summary>
/// <remarks>
/// <see cref="Archived"/> and <see cref="LifecycleGeneration"/> are the two dimensions.
/// <see cref="Commissioned"/> is a flag a fail-closed predicate chain reads -- it is deliberately
/// not "business ready". Business availability is derived from that chain, so REQ-0317's "passing
/// the checks does not commission the vehicle" holds without a guard being written for it, and there
/// is no path that can set business availability true directly.
/// </remarks>
public sealed class AgvLifecycleRow
{
    public required string AgvId { get; set; }
    public long LifecycleGeneration { get; set; }
    public bool Archived { get; set; }
    public bool Commissioned { get; set; }

    /// <summary>The candidate RIoT binding established atomically with a restoration.</summary>
    public string? CandidateRiotBindingJson { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// An archived vehicle record. Restoration reuses this <c>agvId</c> and never creates a second
/// record for the same physical car (REQ-0310).
/// </summary>
public sealed class AgvArchiveRow
{
    public required string AgvId { get; set; }
    public DateTimeOffset ArchivedAt { get; set; }
    public required string ArchiveReason { get; set; }
    public long ArchivedLifecycleGeneration { get; set; }

    /// <summary>
    /// The fingerprint of the configuration that was active when the vehicle was archived, kept as a
    /// restoration candidate (REQ-0316). It is accepted only when the reconnecting vehicle's
    /// CapabilitySnapshot reports the same fingerprint.
    /// </summary>
    public string? ArchivedActiveSlotConfigurationFingerprint { get; set; }
}

/// <summary>
/// One restoration attempt and how it ended. All four outcomes are recorded, and each writes an
/// immutable audit record (REQ-0320).
/// </summary>
public sealed class AgvRestorationAttemptRow
{
    public required string RestorationAttemptId { get; set; }
    public required string AgvId { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? SettledAt { get; set; }
    public GovernanceActionOutcome? Outcome { get; set; }
    public required string ArchiveReason { get; set; }
    public long RestoredLifecycleGeneration { get; set; }
    public string? AuditRecordId { get; set; }
}
