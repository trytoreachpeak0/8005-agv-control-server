using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ControlServer.Infrastructure.Persistence.Configurations;

public sealed class DispatchZoneParameterRowConfiguration : IEntityTypeConfiguration<DispatchZoneParameterRow>
{
    public void Configure(EntityTypeBuilder<DispatchZoneParameterRow> builder)
    {
        builder.ToTable("DispatchZoneParameters");
        builder.HasKey(row => new { row.Version, row.DispatchZone });
        builder.Property(row => row.Version).ValueGeneratedNever();
    }
}
