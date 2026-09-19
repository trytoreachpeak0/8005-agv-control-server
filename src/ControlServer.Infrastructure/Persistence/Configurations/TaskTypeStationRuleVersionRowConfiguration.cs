using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ControlServer.Infrastructure.Persistence.Configurations;

public sealed class TaskTypeStationRuleVersionRowConfiguration
    : IEntityTypeConfiguration<TaskTypeStationRuleVersionRow>
{
    public void Configure(EntityTypeBuilder<TaskTypeStationRuleVersionRow> builder)
    {
        builder.ToTable("TaskTypeStationRuleVersions");
        builder.HasKey(row => row.Version);
        // Assigned by the store: it is also the governed snapshot's version, and the two must be the same number.
        builder.Property(row => row.Version).ValueGeneratedNever();
        builder.HasIndex(row => row.SnapshotId).IsUnique();
    }
}
