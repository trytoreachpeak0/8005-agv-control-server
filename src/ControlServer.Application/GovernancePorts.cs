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
    /// Freezes one version. Freezing the same version again with the same content returns the
    /// snapshot that already exists rather than writing a second one -- a version has exactly one
    /// frozen content, ever.
    /// </summary>
    /// <remarks>
    /// Freezing a version that is already frozen with <i>different</i> content throws
    /// <see cref="GovernedSnapshotVersionConflictException"/>. That combination means two writers
    /// allocated the same version on one version line, and the caller's content is not what that
    /// version holds; returning the existing snapshot would silently attach the caller's rows and
    /// audit to somebody else's content.
    /// </remarks>
    /// <exception cref="GovernedSnapshotVersionConflictException">
    /// The version is already frozen and holds different content.
    /// </exception>
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

/// <summary>
/// Two writers allocated the same version on one version line, and the second one's content is not
/// what that version already holds.
/// </summary>
/// <remarks>
/// <para>
/// This is a collision, not a rewrite attempt: the snapshot that is already frozen stays exactly as
/// it is. What is refused is the <i>silent</i> outcome -- handing the second writer the first one's
/// snapshot, so that its rows and its audit end up naming content it never published.
/// </para>
/// <para>
/// A caller that allocates versions on a shared line is expected to catch this, allocate again and
/// retry, which is what <c>SlotConfigurationVersionLine</c> does. A caller that cannot retry should
/// let it out: an operator seeing this is strictly better off than a database that quietly says the
/// wrong thing about which wiring a vehicle was given.
/// </para>
/// </remarks>
public sealed class GovernedSnapshotVersionConflictException : InvalidOperationException
{
    private const string DefaultMessage =
        "That version of this governed object is already frozen with different content.";

    public GovernedSnapshotVersionConflictException()
        : base(DefaultMessage)
    {
    }

    public GovernedSnapshotVersionConflictException(string message)
        : base(message)
    {
    }

    public GovernedSnapshotVersionConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public GovernedSnapshotVersionConflictException(
        GovernedObjectKind objectKind,
        string objectId,
        long version,
        string existingSnapshotId,
        string existingContentSha256,
        string attemptedContentSha256,
        Exception? innerException = null)
        : base(
            MessageFor(objectKind, objectId, version, existingSnapshotId, existingContentSha256, attemptedContentSha256),
            innerException)
    {
        ObjectKind = objectKind;
        ObjectId = objectId;
        Version = version;
        ExistingSnapshotId = existingSnapshotId;
        ExistingContentSha256 = existingContentSha256;
        AttemptedContentSha256 = attemptedContentSha256;
    }

    public GovernedObjectKind ObjectKind { get; }

    public string? ObjectId { get; }

    public long Version { get; }

    public string? ExistingSnapshotId { get; }

    public string? ExistingContentSha256 { get; }

    public string? AttemptedContentSha256 { get; }

    private static string MessageFor(
        GovernedObjectKind objectKind,
        string objectId,
        long version,
        string existingSnapshotId,
        string existingContentSha256,
        string attemptedContentSha256) =>
        string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"Version {version} of {objectKind} '{objectId}' is already frozen as snapshot {existingSnapshotId} (sha256 {existingContentSha256}), and this publication's content hashes to {attemptedContentSha256}. A frozen version has exactly one content, ever: allocate the next version and publish again rather than attaching to content this publication did not write.");
}
