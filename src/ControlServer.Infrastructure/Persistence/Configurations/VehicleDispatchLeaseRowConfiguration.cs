using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ControlServer.Infrastructure.Persistence.Configurations;

public sealed class VehicleDispatchLeaseRowConfiguration : IEntityTypeConfiguration<VehicleDispatchLeaseRow>
{
    public void Configure(EntityTypeBuilder<VehicleDispatchLeaseRow> builder)
    {
        builder.HasKey(row => row.JourneyId);
        builder
            .HasIndex(row => row.VehicleKey)
            .IsUnique()
            .HasFilter("ReleasedAt IS NULL");
    }
}
