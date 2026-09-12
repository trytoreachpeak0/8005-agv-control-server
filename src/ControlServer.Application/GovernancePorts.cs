using ControlServer.Domain;

namespace ControlServer.Application;

/// <summary>
/// One governance action, as it is handed to the audit writer. The person field is not here on
/// purpose: the writer fills it with the deployment identity and the explicit
/// "not attributable to a natural person" marker, and no caller gets to supply a name.
/// </summary>
public sealed record GovernanceAuditEntry(
    string Action,
    GovernedObjectKind ObjectKind,
    string ObjectId,
    long? Version,
    GovernanceActionOutcome Outcome,
    string DetailJson,
    string? SnapshotId = null,
    string? ClaimedAdministratorRole = null);

/// <summary>
/// Freezes and reads back version-level complete snapshots of governed objects.
/// </summary>
public interface IConfigurationSnapshotStore
{
    /// <summary>
    /// Freezes one version. Freezing the same version again returns the snapshot that already
    /// exists rather than writing a second one -- a version has exactly one frozen content, ever.
    /// </summary>
    Task<GovernedConfigurationSnapshot> FreezeAsync(
        GovernedObjectKind objectKind,
        string objectId,
        long version,
        string contentJson,
        DateTimeOffset frozenAt,
        CancellationToken cancellationToken);

    Task<GovernedConfigurationSnapshot?> ReadAsync(
        GovernedObjectKind objectKind,
        string objectId,
        long version,
        CancellationToken cancellationToken);

    /// <summary>
    /// Computes the field-level difference between two frozen versions. Nothing is stored: a stored
    /// difference would be a second truth about the same change alongside the two snapshots.
    /// </summary>
    Task<IReadOnlyList<ConfigurationFieldDifference>> DiffAsync(
        GovernedObjectKind objectKind,
        string objectId,
        long leftVersion,
        long rightVersion,
        CancellationToken cancellationToken);
}

/// <summary>
/// Writes the two immutable audit streams.
/// </summary>
public interface IGovernanceAuditWriter
{
    Task<string> WriteBusinessAsync(
        GovernanceAuditEntry entry,
        DateTimeOffset recordedAt,
        CancellationToken cancellationToken);

    Task<string> WriteAdministratorAsync(
        GovernanceAuditEntry entry,
        DateTimeOffset recordedAt,
        CancellationToken cancellationToken);
}

/// <summary>One audit record as a read-only query returns it.</summary>
public sealed record GovernanceAuditView(
    string AuditRecordId,
    DateTimeOffset RecordedAt,
    string ActorIdentity,
    string ActorAttribution,
    string Action,
    GovernedObjectKind ObjectKind,
    string ObjectId,
    long? Version,
    GovernanceActionOutcome Outcome,
    string? SnapshotId,
    string DetailJson);

/// <summary>One governed version and the audit that accompanied it.</summary>
public sealed record GovernedVersionView(
    GovernedConfigurationSnapshot Snapshot,
    IReadOnlyList<GovernanceAuditView> BusinessAudit,
    IReadOnlyList<GovernanceAuditView> AdministratorAudit);

/// <summary>
/// The read-only side of the governance mechanism: take a version and its audit back out.
/// </summary>
public interface IGovernedConfigurationReader
{
    Task<GovernedVersionView?> ReadVersionAsync(
        GovernedObjectKind objectKind,
        string objectId,
        long version,
        CancellationToken cancellationToken);
}
