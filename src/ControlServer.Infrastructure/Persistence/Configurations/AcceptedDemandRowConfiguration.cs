using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ControlServer.Infrastructure.Persistence.Configurations;

public sealed class AcceptedDemandRowConfiguration : IEntityTypeConfiguration<AcceptedDemandRow>
{
    public void Configure(EntityTypeBuilder<AcceptedDemandRow> builder)
    {
        builder.HasKey(row => row.DemandId);
        builder.HasIndex(row => row.TransportDemandKey).IsUnique();
        builder.Property(row => row.Status).HasConversion<string>();
    }
}
