using ControlServer.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ControlServer.Infrastructure.Persistence.Configurations;

public sealed class VehicleChargingEligibilityHoldRowConfiguration : IEntityTypeConfiguration<VehicleChargingEligibilityHoldRow>
{
    public void Configure(EntityTypeBuilder<VehicleChargingEligibilityHoldRow> builder)
    {
        builder.ToTable("VehicleChargingEligibilityHolds", table => table.HasCheckConstraint(
            "CK_VehicleChargingEligibilityHolds_Reason",
            CheckConstraintSql.OneOf("Reason", VehicleChargingEligibilityHoldReasons.All)));
        builder.HasKey(row => row.HoldId);
        builder.HasIndex(row => row.IdempotencyKey).IsUnique();
        builder.HasIndex(row => row.VehicleKey);
    }
}
