using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ControlServer.Infrastructure.Persistence.Configurations;

public sealed class DispatchZoneAreaAssignmentVersionRowConfiguration
    : IEntityTypeConfiguration<DispatchZoneAreaAssignmentVersionRow>
{
    public void Configure(EntityTypeBuilder<DispatchZoneAreaAssignmentVersionRow> builder)
    {
        builder.ToTable("DispatchZoneAreaAssignmentVersions");
        builder.HasKey(row => row.Version);
        // The version number is assigned by the store, never by the database: it is also the
        // governed snapshot's version, and the two must be the same number.
        builder.Property(row => row.Version).ValueGeneratedNever();
        builder.HasIndex(row => row.SnapshotId).IsUnique();
    }
}
