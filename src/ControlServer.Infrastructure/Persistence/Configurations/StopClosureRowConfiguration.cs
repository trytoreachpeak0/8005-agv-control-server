using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ControlServer.Infrastructure.Persistence.Configurations;

public sealed class StopClosureRowConfiguration : IEntityTypeConfiguration<StopClosureRow>
{
    public void Configure(EntityTypeBuilder<StopClosureRow> builder)
    {
        builder.HasKey(row => row.DemandId);
    }
}
