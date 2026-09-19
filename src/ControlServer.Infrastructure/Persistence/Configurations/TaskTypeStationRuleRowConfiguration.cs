using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ControlServer.Infrastructure.Persistence.Configurations;

public sealed class TaskTypeStationRuleRowConfiguration : IEntityTypeConfiguration<TaskTypeStationRuleRow>
{
    public void Configure(EntityTypeBuilder<TaskTypeStationRuleRow> builder)
    {
        builder.ToTable("TaskTypeStationRules");
        // One rule per task type per version (REQ-0343): the key is the rule, not a convention of the loader.
        builder.HasKey(row => new { row.Version, row.TaskType });
        builder.Property(row => row.Version).ValueGeneratedNever();
    }
}
