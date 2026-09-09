using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ControlServer.Infrastructure.Persistence.Configurations;

public sealed class JourneyBacklogRowConfiguration : IEntityTypeConfiguration<JourneyBacklogRow>
{
    public void Configure(EntityTypeBuilder<JourneyBacklogRow> builder)
    {
        builder.HasKey(row => row.DemandId);
        builder.HasIndex(row => row.TransportDemandKey);
    }
}
