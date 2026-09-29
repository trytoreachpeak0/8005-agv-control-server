using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ControlServer.Infrastructure.Persistence.Configurations;

public sealed class ChargingStationRecoveryRowConfiguration : IEntityTypeConfiguration<ChargingStationRecoveryRow>
{
    public void Configure(EntityTypeBuilder<ChargingStationRecoveryRow> builder)
    {
        builder.ToTable("ChargingStationRecoveries");
        builder.HasKey(row => row.RecoveryId);
        builder.HasIndex(row => row.HoldId).IsUnique();
    }
}
