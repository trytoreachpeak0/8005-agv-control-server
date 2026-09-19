using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ControlServer.Infrastructure.Persistence.Configurations;

public sealed class TaskTypeStationActiveBindingSetRowConfiguration
    : IEntityTypeConfiguration<TaskTypeStationActiveBindingSetRow>
{
    public void Configure(EntityTypeBuilder<TaskTypeStationActiveBindingSetRow> builder)
    {
        builder.ToTable("TaskTypeStationActiveBindingSets");
        builder.HasKey(row => row.MapId);
        builder.Property(row => row.MapId).ValueGeneratedNever();
    }
}
