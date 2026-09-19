using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ControlServer.Infrastructure.Persistence.Configurations;

public sealed class TaskTypeStationHoldRowConfiguration : IEntityTypeConfiguration<TaskTypeStationHoldRow>
{
    public void Configure(EntityTypeBuilder<TaskTypeStationHoldRow> builder)
    {
        builder.ToTable("TaskTypeStationHolds");
        // Keyed by the hold, not by (MapId, TaskType): a manual hold and a catalog-change hold on the same task
        // type both stand until each is released (REQ-0340).
        builder.HasKey(row => row.HoldId);
        builder.HasIndex(row => new { row.MapId, row.TaskType, row.ReleasedAt });
    }
}
