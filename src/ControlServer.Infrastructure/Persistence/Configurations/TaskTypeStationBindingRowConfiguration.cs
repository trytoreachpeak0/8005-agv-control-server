using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ControlServer.Infrastructure.Persistence.Configurations;

public sealed class TaskTypeStationBindingRowConfiguration : IEntityTypeConfiguration<TaskTypeStationBindingRow>
{
    public void Configure(EntityTypeBuilder<TaskTypeStationBindingRow> builder)
    {
        builder.ToTable("TaskTypeStationBindings");
        // At most one station per task type per map version (REQ-0334).
        builder.HasKey(row => new { row.MapId, row.Version, row.TaskType });
        builder.Property(row => row.Version).ValueGeneratedNever();
        // And one task type per station: a station bound to two task types is refused by the database, not only
        // by the startup validator (REQ-0334, REQ-0338).
        builder.HasIndex(row => new { row.MapId, row.Version, row.StationRiotId }).IsUnique();
    }
}
