using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ControlServer.Infrastructure.Persistence.Configurations;

public sealed class WaitingPointVehicleScopeRowConfiguration : IEntityTypeConfiguration<WaitingPointVehicleScopeRow>
{
    public void Configure(EntityTypeBuilder<WaitingPointVehicleScopeRow> builder)
    {
        builder.ToTable("WaitingPointVehicleScopes");
        builder.HasKey(row => new { row.Version, row.MapId, row.StationId, row.VehicleKey });
        builder.Property(row => row.Version).ValueGeneratedNever();
        builder.Property(row => row.MapId).ValueGeneratedNever();
        builder.Property(row => row.StationId).ValueGeneratedNever();
    }
}
