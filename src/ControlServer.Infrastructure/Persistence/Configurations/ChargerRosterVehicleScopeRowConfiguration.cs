using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ControlServer.Infrastructure.Persistence.Configurations;

public sealed class ChargerRosterVehicleScopeRowConfiguration : IEntityTypeConfiguration<ChargerRosterVehicleScopeRow>
{
    public void Configure(EntityTypeBuilder<ChargerRosterVehicleScopeRow> builder)
    {
        builder.ToTable("ChargerRosterVehicleScopes");
        builder.HasKey(row => new { row.Version, row.MapId, row.StationId, row.VehicleKey });
        builder.Property(row => row.Version).ValueGeneratedNever();
        builder.Property(row => row.MapId).ValueGeneratedNever();
        builder.Property(row => row.StationId).ValueGeneratedNever();
    }
}
