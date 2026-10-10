using ControlServer.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ControlServer.Infrastructure.Persistence.Configurations;

public sealed class StationExclusivityRecordRowConfiguration : IEntityTypeConfiguration<StationExclusivityRecordRow>
{
    public void Configure(EntityTypeBuilder<StationExclusivityRecordRow> builder)
    {
        builder.ToTable("StationExclusivityRecords", table => table.HasCheckConstraint(
            "CK_StationExclusivityRecords_StationKind",
            CheckConstraintSql.OneOf("StationKind", StationExclusivityKinds.All)));
        builder.HasKey(row => row.RecordId);
        // Deliberately not unique, for the reason VehiclePurposeClaimRecords' index is not: the passage is evidence, and
        // who holds a station is decided by StationExclusivities' key alone.
        builder.HasIndex(row => new { row.MapId, row.StationId });
        builder.HasIndex(row => row.VehicleKey);
        builder.HasIndex(row => row.JourneyId);
    }
}
