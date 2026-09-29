using ControlServer.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ControlServer.Infrastructure.Persistence.Configurations;

public sealed class ChargingCycleRowConfiguration : IEntityTypeConfiguration<ChargingCycleRow>
{
    public void Configure(EntityTypeBuilder<ChargingCycleRow> builder)
    {
        builder.ToTable("ChargingCycles", table =>
        {
            table.HasCheckConstraint(
                "CK_ChargingCycles_WireState", CheckConstraintSql.OneOf("WireState", ChargingCycleWireStates.All));
            table.HasCheckConstraint(
                "CK_ChargingCycles_Phase", CheckConstraintSql.OneOf("Phase", ChargingCyclePhases.All));
        });
        builder.HasKey(row => row.CycleId);
        // At most one cycle per vehicle that has not ended, decided by the index rather than by a read first.
        builder.HasIndex(row => row.VehicleKey)
            .IsUnique()
            .HasFilter("Phase <> 'ENDED'")
            .HasDatabaseName("IX_ChargingCycles_VehicleKey_Open");
        builder.HasIndex(row => row.JourneyId);
        builder.HasIndex(row => new { row.MapId, row.StationId });
        builder.Property(row => row.Version).IsConcurrencyToken();
    }
}
