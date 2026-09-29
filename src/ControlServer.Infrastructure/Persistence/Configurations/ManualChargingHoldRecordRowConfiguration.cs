using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ControlServer.Infrastructure.Persistence.Configurations;

public sealed class ManualChargingHoldRecordRowConfiguration : IEntityTypeConfiguration<ManualChargingHoldRecordRow>
{
    public void Configure(EntityTypeBuilder<ManualChargingHoldRecordRow> builder)
    {
        builder.ToTable("ManualChargingHoldRecords");
        builder.HasKey(row => row.HoldId);
        // Deliberately not unique: the record is evidence, and whether a vehicle is held is ManualChargingHolds' key alone.
        builder.HasIndex(row => row.VehicleKey);
    }
}
