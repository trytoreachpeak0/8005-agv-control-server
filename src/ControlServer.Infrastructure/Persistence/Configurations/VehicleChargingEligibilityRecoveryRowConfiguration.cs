using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ControlServer.Infrastructure.Persistence.Configurations;

public sealed class VehicleChargingEligibilityRecoveryRowConfiguration : IEntityTypeConfiguration<VehicleChargingEligibilityRecoveryRow>
{
    public void Configure(EntityTypeBuilder<VehicleChargingEligibilityRecoveryRow> builder)
    {
        builder.ToTable("VehicleChargingEligibilityRecoveries");
        builder.HasKey(row => row.RecoveryId);
        builder.HasIndex(row => row.HoldId).IsUnique();
    }
}
