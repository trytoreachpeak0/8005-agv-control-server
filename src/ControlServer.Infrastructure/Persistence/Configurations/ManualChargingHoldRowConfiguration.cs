using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ControlServer.Infrastructure.Persistence.Configurations;

public sealed class ManualChargingHoldRowConfiguration : IEntityTypeConfiguration<ManualChargingHoldRow>
{
    public void Configure(EntityTypeBuilder<ManualChargingHoldRow> builder)
    {
        builder.ToTable("ManualChargingHolds");
        // One row per vehicle: whoever inserts first holds it.
        builder.HasKey(row => row.VehicleKey);
        builder.HasIndex(row => row.HoldId).IsUnique();
        builder.Property(row => row.HoldId).IsConcurrencyToken();
    }
}
