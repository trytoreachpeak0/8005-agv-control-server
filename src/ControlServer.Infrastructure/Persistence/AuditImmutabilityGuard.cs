using ControlServer.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace ControlServer.Infrastructure.Persistence;

/// <summary>
/// Raised when something tries to rewrite an audit record, or to delete one that is still inside its
/// retention period.
/// </summary>
public sealed class AuditRecordImmutabilityException : InvalidOperationException
{
    public AuditRecordImmutabilityException(string message)
        : base(message)
    {
    }

    public AuditRecordImmutabilityException()
        : base("An audit record cannot be rewritten.")
    {
    }

    public AuditRecordImmutabilityException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The audit records are write-once. This enforces it inside <see cref="ControlServerDbContext"/>
/// itself rather than in the stores that write them, so that "do not rewrite audit" is a property of
/// the database context and not a rule every future caller has to remember.
/// </summary>
internal static class AuditImmutabilityGuard
{
    internal static void Enforce(
        ChangeTracker changeTracker,
        AuditRetentionPolicy retentionPolicy,
        DateTimeOffset now)
    {
        foreach (EntityEntry entry in changeTracker.Entries())
        {
            if (entry.Entity is not (BusinessAuditRecordRow or AdministratorAuditRecordRow))
            {
                continue;
            }

            switch (entry.State)
            {
                case EntityState.Modified:
                    throw new AuditRecordImmutabilityException(
                        $"{entry.Entity.GetType().Name} is write-once and cannot be updated.");
                case EntityState.Deleted when retentionPolicy.IsWithinRetention(RecordedAt(entry.Entity), now):
                    throw new AuditRecordImmutabilityException(
                        $"{entry.Entity.GetType().Name} is inside the {retentionPolicy.RetainFor.TotalDays:0} day "
                        + "retention period and cannot be deleted.");
                default:
                    break;
            }
        }
    }

    private static DateTimeOffset RecordedAt(object entity) => entity switch
    {
        BusinessAuditRecordRow business => business.RecordedAt,
        AdministratorAuditRecordRow administrator => administrator.RecordedAt,
        _ => throw new ArgumentOutOfRangeException(nameof(entity), entity, "Not an audit record."),
    };
}
