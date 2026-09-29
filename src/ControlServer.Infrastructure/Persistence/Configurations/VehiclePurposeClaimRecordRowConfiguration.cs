using ControlServer.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ControlServer.Infrastructure.Persistence.Configurations;

public sealed class VehiclePurposeClaimRecordRowConfiguration : IEntityTypeConfiguration<VehiclePurposeClaimRecordRow>
{
    public void Configure(EntityTypeBuilder<VehiclePurposeClaimRecordRow> builder)
    {
        builder.ToTable("VehiclePurposeClaimRecords", table => table.HasCheckConstraint(
            "CK_VehiclePurposeClaimRecords_Purpose", CheckConstraintSql.OneOf("Purpose", VehiclePurposes.All)));
        builder.HasKey(row => row.RecordId);
        // At most one unreleased record per vehicle, decided by the database in the same save as the claim's own key.
        builder.HasIndex(row => row.VehicleKey).IsUnique().HasFilter("ReleasedAt IS NULL");
        builder.HasIndex(row => row.JourneyId);
    }
}
