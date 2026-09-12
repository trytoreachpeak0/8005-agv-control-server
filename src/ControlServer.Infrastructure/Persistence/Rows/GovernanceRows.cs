using ControlServer.Domain;

namespace ControlServer.Infrastructure.Persistence;

/// <summary>
/// One frozen version-level complete snapshot. Unique on (kind, object, version): freezing the same
/// version twice returns the first snapshot rather than writing a second one.
/// </summary>
public sealed class GovernedConfigurationSnapshotRow
{
    public required string SnapshotId { get; set; }
    public GovernedObjectKind ObjectKind { get; set; }
    public required string ObjectId { get; set; }
    public long Version { get; set; }
    public required string ContentJson { get; set; }
    public required string ContentSha256 { get; set; }
    public DateTimeOffset FrozenAt { get; set; }
}

/// <summary>
/// A business-level governance action. Write-once: <see cref="ControlServerDbContext"/> rejects any
/// update, and rejects a delete until the row is past the configured retention period.
/// </summary>
public sealed class BusinessAuditRecordRow
{
    public required string AuditRecordId { get; set; }
    public DateTimeOffset RecordedAt { get; set; }

    /// <summary>
    /// <see cref="RecordedAt"/> as UTC ticks, so retention can be filtered in the database.
    /// </summary>
    /// <remarks>
    /// EF Core cannot translate a comparison on <see cref="DateTimeOffset"/> against SQLite, and the
    /// alternative -- loading every audit row to filter it in memory -- would read 180 days of audit
    /// to delete a handful. The writer is the only thing that sets either column, and it sets both
    /// from the same instant.
    /// </remarks>
    public long RecordedAtUtcTicks { get; set; }

    /// <summary>The deployment that acted. Never a natural person -- see <see cref="ActorAttribution"/>.</summary>
    public required string ActorIdentity { get; set; }

    /// <summary>Always <see cref="AuditActorAttribution.NotAttributableToNaturalPerson"/> in this release.</summary>
    public required string ActorAttribution { get; set; }

    public required string Action { get; set; }
    public GovernedObjectKind ObjectKind { get; set; }
    public required string ObjectId { get; set; }
    public long? Version { get; set; }
    public GovernanceActionOutcome Outcome { get; set; }
    public string? SnapshotId { get; set; }
    public required string DetailJson { get; set; }
}

/// <summary>
/// An administrator-level governance action, kept apart from the business record because REQ-0271
/// names the two separately and they are retained and queried as two streams.
/// </summary>
public sealed class AdministratorAuditRecordRow
{
    public required string AuditRecordId { get; set; }
    public DateTimeOffset RecordedAt { get; set; }

    /// <summary>
    /// <see cref="RecordedAt"/> as UTC ticks. Same reason as
    /// <see cref="BusinessAuditRecordRow.RecordedAtUtcTicks"/>.
    /// </summary>
    public long RecordedAtUtcTicks { get; set; }

    public required string ActorIdentity { get; set; }
    public required string ActorAttribution { get; set; }

    /// <summary>
    /// The <c>administratorRole</c> string the message carried, when there was one. It authenticates
    /// nobody -- it is recorded as what was claimed, not as who acted.
    /// </summary>
    public string? ClaimedAdministratorRole { get; set; }

    public required string Action { get; set; }
    public GovernedObjectKind ObjectKind { get; set; }
    public required string ObjectId { get; set; }
    public long? Version { get; set; }
    public GovernanceActionOutcome Outcome { get; set; }
    public string? SnapshotId { get; set; }
    public required string DetailJson { get; set; }
}

/// <summary>
/// Where one consumer froze one governed object. This is the whole of the rollback impact rule: a
/// rollback is a new activation of old content, and it reaches only the objects whose freeze point
/// is later than it. Objects already fixed at an earlier freeze point are untouched.
/// </summary>
public sealed class ConfigurationConsumerBindingRow
{
    public required string ConsumerKind { get; set; }
    public required string ConsumerId { get; set; }
    public GovernedObjectKind ObjectKind { get; set; }
    public required string ObjectId { get; set; }
    public long FrozenVersion { get; set; }
    public DateTimeOffset FrozenAt { get; set; }
    public required string SnapshotId { get; set; }
}
