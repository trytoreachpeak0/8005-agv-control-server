using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ControlServer.Infrastructure.Persistence.Configurations;

public sealed class VehiclePurposeClaimRowConfiguration : IEntityTypeConfiguration<VehiclePurposeClaimRow>
{
    public void Configure(EntityTypeBuilder<VehiclePurposeClaimRow> builder)
    {
        builder.ToTable("VehiclePurposeClaims");
        // One row per vehicle: whoever inserts first holds it, and the key refuses the second. That is the whole
        // arbitration -- nothing reads before it writes.
        builder.HasKey(row => row.VehicleKey);
        builder.HasIndex(row => row.JourneyId);
    }
}
