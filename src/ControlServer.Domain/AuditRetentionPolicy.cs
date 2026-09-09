namespace ControlServer.Domain;

/// <summary>
/// How long audit records stay online. REQ-0271 sets the floor at 180 days; what happens after that
/// is configuration, and the configuration change is itself an administrator action.
/// </summary>
/// <remarks>
/// The policy is not a licence to delete. It only decides when a record stops being protected from
/// deletion -- an unexpired record cannot be removed by any path, and no record can be updated at
/// any age.
/// </remarks>
public sealed record AuditRetentionPolicy(TimeSpan RetainFor)
{
    /// <summary>The floor REQ-0271 sets, and the value used when nothing is configured.</summary>
    public static AuditRetentionPolicy Default { get; } = new(TimeSpan.FromDays(180));

    /// <summary>The instant before which records are past retention and may be purged.</summary>
    public DateTimeOffset CutoffFor(DateTimeOffset now) => now - RetainFor;

    /// <summary>Whether a record written at <paramref name="recordedAt"/> is still within retention.</summary>
    public bool IsWithinRetention(DateTimeOffset recordedAt, DateTimeOffset now) =>
        recordedAt > CutoffFor(now);
}
