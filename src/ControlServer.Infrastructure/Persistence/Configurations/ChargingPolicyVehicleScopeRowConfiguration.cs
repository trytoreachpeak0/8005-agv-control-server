using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ControlServer.Infrastructure.Persistence.Configurations;

public sealed class ChargingPolicyVehicleScopeRowConfiguration : IEntityTypeConfiguration<ChargingPolicyVehicleScopeRow>
{
    public void Configure(EntityTypeBuilder<ChargingPolicyVehicleScopeRow> builder)
    {
        builder.ToTable("ChargingPolicyVehicleScopes");
        builder.HasKey(row => new { row.Version, row.VehicleKey });
        builder.Property(row => row.Version).ValueGeneratedNever();
    }
}
