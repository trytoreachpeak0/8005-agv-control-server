using ControlServer.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ControlServer.Infrastructure.Persistence.Configurations;

public sealed class ChargingStationAllocationHoldRowConfiguration : IEntityTypeConfiguration<ChargingStationAllocationHoldRow>
{
    public void Configure(EntityTypeBuilder<ChargingStationAllocationHoldRow> builder)
    {
        builder.ToTable("ChargingStationAllocationHolds", table =>
        {
            table.HasCheckConstraint(
                "CK_ChargingStationAllocationHolds_Trigger",
                CheckConstraintSql.OneOf("Trigger", ChargingStationHoldTriggers.All));
            table.HasCheckConstraint(
                "CK_ChargingStationAllocationHolds_RootCause",
                CheckConstraintSql.OneOf("RootCause", [ChargingHoldRootCauses.Unknown]));
        });
        builder.HasKey(row => row.HoldId);
        // The same trigger makes one row: the key's refusal is the idempotency, not a read first (REQ-0177).
        builder.HasIndex(row => row.IdempotencyKey).IsUnique();
        builder.HasIndex(row => new { row.MapId, row.StationId });
    }
}
