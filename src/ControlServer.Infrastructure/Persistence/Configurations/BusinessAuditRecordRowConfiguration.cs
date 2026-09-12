using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ControlServer.Infrastructure.Persistence.Configurations;

public sealed class BusinessAuditRecordRowConfiguration : IEntityTypeConfiguration<BusinessAuditRecordRow>
{
    public void Configure(EntityTypeBuilder<BusinessAuditRecordRow> builder)
    {
        builder.ToTable("BusinessAuditRecords");
        builder.HasKey(row => row.AuditRecordId);
        builder.Property(row => row.ObjectKind).HasConversion<string>();
        builder.Property(row => row.Outcome).HasConversion<string>();
        // Only the ticks column is indexed: SQLite can neither compare nor order a
        // DateTimeOffset, so an index on RecordedAt could never be used.
        builder.HasIndex(row => row.RecordedAtUtcTicks);
        builder.HasIndex(row => new { row.ObjectKind, row.ObjectId });
    }
}
