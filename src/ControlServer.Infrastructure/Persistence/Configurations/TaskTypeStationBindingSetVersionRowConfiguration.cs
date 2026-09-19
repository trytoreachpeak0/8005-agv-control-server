using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ControlServer.Infrastructure.Persistence.Configurations;

public sealed class TaskTypeStationBindingSetVersionRowConfiguration
    : IEntityTypeConfiguration<TaskTypeStationBindingSetVersionRow>
{
    public void Configure(EntityTypeBuilder<TaskTypeStationBindingSetVersionRow> builder)
    {
        builder.ToTable("TaskTypeStationBindingSetVersions");
        // One version line per Map (REQ-0337).
        builder.HasKey(row => new { row.MapId, row.Version });
        builder.Property(row => row.Version).ValueGeneratedNever();
        builder.HasIndex(row => row.SnapshotId).IsUnique();
    }
}
