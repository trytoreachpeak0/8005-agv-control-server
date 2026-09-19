using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ControlServer.Infrastructure.Persistence.Configurations;

public sealed class TaskTypeStationRequirementRowConfiguration
    : IEntityTypeConfiguration<TaskTypeStationRequirementRow>
{
    public void Configure(EntityTypeBuilder<TaskTypeStationRequirementRow> builder)
    {
        builder.ToTable("TaskTypeStationRequirements");
        builder.HasKey(row => new { row.MapId, row.Version, row.TaskType });
        builder.Property(row => row.Version).ValueGeneratedNever();
    }
}
