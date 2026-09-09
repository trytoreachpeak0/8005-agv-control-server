using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ControlServer.Infrastructure.Persistence.Configurations;

public sealed class StationTaskTypeAdmissionRowConfiguration
    : IEntityTypeConfiguration<StationTaskTypeAdmissionRow>
{
    public void Configure(EntityTypeBuilder<StationTaskTypeAdmissionRow> builder)
    {
        builder.HasKey(row => new { row.StationId, row.TaskType });
    }
}
