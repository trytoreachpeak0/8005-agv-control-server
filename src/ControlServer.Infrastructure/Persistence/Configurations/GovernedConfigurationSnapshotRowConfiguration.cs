using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ControlServer.Infrastructure.Persistence.Configurations;

public sealed class GovernedConfigurationSnapshotRowConfiguration
    : IEntityTypeConfiguration<GovernedConfigurationSnapshotRow>
{
    public void Configure(EntityTypeBuilder<GovernedConfigurationSnapshotRow> builder)
    {
        builder.ToTable("GovernedConfigurationSnapshots");
        builder.HasKey(row => row.SnapshotId);
        builder.Property(row => row.ObjectKind).HasConversion<string>();
        // Freezing the same version twice must not produce a second snapshot. The index says so
        // rather than leaving it to whoever calls freeze.
        builder
            .HasIndex(row => new { row.ObjectKind, row.ObjectId, row.Version })
            .IsUnique();
        builder.HasIndex(row => row.ContentSha256);
    }
}
