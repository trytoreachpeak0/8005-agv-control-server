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
        // Deliberately not unique: the record is evidence, not an arbiter. Who holds a vehicle is decided by
        // VehiclePurposeClaims' key alone; a second arbiter here could disagree with it -- a record left open by a path
        // that released the claim without it -- and then refuse the vehicle for good (review of control-server#394).
        builder.HasIndex(row => row.VehicleKey);
        builder.HasIndex(row => row.JourneyId);
    }
}
