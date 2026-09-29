using ControlServer.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ControlServer.Infrastructure.Persistence.Configurations;

public sealed class VehiclePurposeClaimRowConfiguration : IEntityTypeConfiguration<VehiclePurposeClaimRow>
{
    public void Configure(EntityTypeBuilder<VehiclePurposeClaimRow> builder)
    {
        // Batch 8 (control-server#386): the four purposes, and nothing else, are accepted by the database itself.
        builder.ToTable("VehiclePurposeClaims", table => table.HasCheckConstraint(
            "CK_VehiclePurposeClaims_Purpose", CheckConstraintSql.OneOf("Purpose", VehiclePurposes.All)));
        // One row per vehicle: whoever inserts first holds it, and the key refuses the second. That is the whole
        // arbitration -- nothing reads before it writes.
        builder.HasKey(row => row.VehicleKey);
        builder.HasIndex(row => row.JourneyId);
    }
}
