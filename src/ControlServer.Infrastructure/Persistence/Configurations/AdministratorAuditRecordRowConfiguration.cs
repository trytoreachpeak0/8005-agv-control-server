using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ControlServer.Infrastructure.Persistence.Configurations;

public sealed class AdministratorAuditRecordRowConfiguration
    : IEntityTypeConfiguration<AdministratorAuditRecordRow>
{
    public void Configure(EntityTypeBuilder<AdministratorAuditRecordRow> builder)
    {
        builder.ToTable("AdministratorAuditRecords");
        builder.HasKey(row => row.AuditRecordId);
        builder.Property(row => row.ObjectKind).HasConversion<string>();
        builder.Property(row => row.Outcome).HasConversion<string>();
        // Only the ticks column is indexed: SQLite can neither compare nor order a
        // DateTimeOffset, so an index on RecordedAt could never be used.
        builder.HasIndex(row => row.RecordedAtUtcTicks);
        builder.HasIndex(row => new { row.ObjectKind, row.ObjectId });
    }
}
